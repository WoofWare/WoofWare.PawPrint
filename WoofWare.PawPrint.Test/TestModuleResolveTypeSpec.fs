namespace WoofWare.PawPrint.Test

open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// <summary>
/// <c>Module.ResolveType</c> on a TypeSpec token, against the real runtime. CoreCLR
/// (runtimehandles.cpp, <c>ModuleHandle_ResolveType</c>) reads the TypeSpec's signature in a
/// <c>SigTypeContext</c> built from the caller's type and method instantiations, and substitutes
/// each generic parameter the signature names from them.
/// </summary>
/// <remarks>
/// A signature naming a generic parameter the caller's context does not supply raises
/// <c>BadImageFormatException</c> (siginfo.cpp, <c>SigPointer::GetTypeVariableThrowing</c>), which
/// <c>RuntimeModule.ResolveType</c> wraps in <c>ArgumentException</c>. This holds for a TypeSpec that
/// instantiates a generic type definition over its own parameters, too: it is not the definition.
/// Custom attribute reads resolve an attribute's type this way, with no context, so an emitted
/// attribute whose constructor is one of an open generic type fails every read of its owner.
/// </remarks>
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestModuleResolveTypeSpec =

    /// Each method of `Holder` and of `Program` other than `Classify` and `Main` loads exactly one
    /// TypeSpec with `ldtoken`. `Main` resolves each token it is given and classifies the answer: in
    /// `classify` mode it returns the one token's classification, and in `check` mode it returns 0 if
    /// every token's is the one given beside it, or one more than the index of the first that is not.
    let private source =
        """
using System;
using System.Reflection;

public class Holder<T>
{
    public static Type OfT() => typeof(T);
    public static Type ArrayOfT() => typeof(T[]);
    public static Type Self() => typeof(Holder<T>);
}

public static class Program
{
    static Type OfV<V>() => typeof(V);
    static Type HolderOfV<V>() => typeof(Holder<V>);
    static Type HolderOfInt() => typeof(Holder<int>);

    static Type[] TypeContext(string context) =>
        context switch
        {
            "none" => null,
            "m=int" => null,
            "t=long" => new[] { typeof(long) },
            "t=long,m=int" => new[] { typeof(long) },
            _ => throw new ArgumentException(context),
        };

    static Type[] MethodContext(string context) =>
        context switch
        {
            "none" => null,
            "m=int" => new[] { typeof(int) },
            "t=long" => null,
            "t=long,m=int" => new[] { typeof(int) },
            _ => throw new ArgumentException(context),
        };

    static readonly Type[] Candidates =
    {
        typeof(Holder<long>), typeof(Holder<int>), typeof(Holder<>), typeof(long), typeof(int), typeof(long[]),
    };

    static int Classify(int token, string context)
    {
        Type t;
        try
        {
            t = typeof(Program).Module.ResolveType(token, TypeContext(context), MethodContext(context));
        }
        catch (ArgumentException e) when (e.InnerException is BadImageFormatException)
        {
            Exception inner = e.InnerException;
            bool faithful =
                e.GetType() == typeof(ArgumentException)
                && inner.GetType() == typeof(BadImageFormatException)
                && inner.HResult == unchecked((int)0x8007000B)
                && inner.Message == "An attempt was made to load a program with an incorrect format.\n (0x8007000B)"
                && inner.InnerException == null;
            return faithful ? 128 : 129;
        }

        return Array.IndexOf(Candidates, t) + 1;
    }

    public static int Main(string[] args)
    {
        if (args[0] == "classify")
            return Classify(int.Parse(args[1]), args[2]);

        for (int i = 0; 1 + 3 * i < args.Length; i++)
        {
            int token = int.Parse(args[1 + 3 * i]);
            if (Classify(token, args[2 + 3 * i]) != int.Parse(args[3 + 3 * i]))
                return i + 1;
        }

        return 0;
    }
}
"""

    let private image : Lazy<byte[]> = lazy (Roslyn.compile [ source ])

    /// The token of the one TypeSpec that the method `methodName` loads with `ldtoken`, on the type
    /// whose metadata name, arity suffix included, is `typeName`.
    let private typeSpecToken (image : byte[]) (typeName : string) (methodName : string) : int =
        use peReader = new PEReader (new MemoryStream (image))
        let reader = peReader.GetMetadataReader ()

        let tokens =
            [
                for typeHandle in reader.TypeDefinitions do
                    let typeDef = reader.GetTypeDefinition typeHandle

                    if reader.GetString typeDef.Name = typeName then
                        for methodHandle in typeDef.GetMethods () do
                            let methodDef = reader.GetMethodDefinition methodHandle

                            if reader.GetString methodDef.Name = methodName then
                                let il = (peReader.GetMethodBody methodDef.RelativeVirtualAddress).GetILBytes ()

                                // An `ldtoken` whose operand is a TypeSpec token (table 0x1B).
                                for offset in 0 .. il.Length - 5 do
                                    if il.[offset] = 0xD0uy && il.[offset + 4] = 0x1Buy then
                                        yield System.BitConverter.ToInt32 (il, offset + 1)
            ]
            |> List.distinct

        match tokens with
        | [ token ] -> token
        | tokens -> failwith $"test bug: %s{typeName}.%s{methodName} loads %d{tokens.Length} TypeSpecs"

    let private realRuntime (args : string list) : int =
        match RealRuntime.executeWithRealRuntime (Array.ofList args) (image.Force ()) with
        | RealRuntimeResult.NormalExit exitCode -> exitCode
        | other -> failwith $"expected the real runtime to exit normally, got %A{other}"

    let private underPawPrint (name : string) (args : string list) : RunOutcome =
        let _messages, loggerFactory =
            LoggerFactory.makeTestWithProperties [ "source_file", name ]

        use _loggerFactoryResource = loggerFactory

        let config =
            let config =
                HostConfig.Default (
                    FrameworkUnderTest.assembliesFor
                        (GuestFramework.PinnedPackOf KernelConfig.Default.UnixPlatform)
                        GuestApplication.InMemory
                )

            { config with
                Guest =
                    { config.Guest with
                        Argv = args
                    }
            }

        use peImage = new MemoryStream (image.Force ())
        BoundedRun.run loggerFactory name (Some name) peImage config |> ExpectRun.ended

    // Classifications, as `Classify` computes them: the answer's index in `Candidates` plus one
    // (`Holder<long>` 1, `Holder<int>` 2, `Holder<>` 3, `long` 4, `int` 5, `long[]` 6), or 0 for a type
    // not among them; or 128 for an `ArgumentException` wrapping the `BadImageFormatException` CoreCLR
    // raises, 129 for one wrapping another.
    let private missingContext = 128

    /// Each case: the type and method whose `ldtoken` names the TypeSpec, the context it is resolved
    /// in, and what the real runtime was measured to answer.
    let private cases : (string * string * string * int) list =
        [
            // `Holder<!0>` is an instantiation over the type's own parameter, not the definition.
            "Holder`1", "Self", "none", missingContext
            // A method context does not give a type context.
            "Holder`1", "Self", "m=int", missingContext
            "Holder`1", "Self", "t=long", 1
            // `Holder<!!0>`.
            "Program", "HolderOfV", "none", missingContext
            "Program", "HolderOfV", "t=long", missingContext
            "Program", "HolderOfV", "m=int", 2
            "Program", "HolderOfV", "t=long,m=int", 2
            // `Holder<int>` reads no context.
            "Program", "HolderOfInt", "none", 2
            // `!0`, `!0[]` and `!!0`.
            "Holder`1", "OfT", "none", missingContext
            "Holder`1", "OfT", "t=long", 4
            "Holder`1", "ArrayOfT", "none", missingContext
            "Holder`1", "ArrayOfT", "m=int", missingContext
            "Holder`1", "ArrayOfT", "t=long", 6
            "Program", "OfV", "none", missingContext
            "Program", "OfV", "t=long", missingContext
            "Program", "OfV", "m=int", 5
        ]

    [<Test>]
    let ``The real runtime answers as measured`` () : unit =
        for typeName, methodName, context, measured in cases do
            let token = typeSpecToken (image.Force ()) typeName methodName
            let actual = realRuntime [ "classify" ; string token ; context ]

            if actual <> measured then
                failwith
                    $"%s{typeName}.%s{methodName} in context %s{context}: measured %d{measured}, but the real runtime now gives %d{actual}"

    [<Test>]
    let ``PawPrint resolves a TypeSpec as CoreCLR does`` () : unit =
        let args =
            [
                yield "check"
                for typeName, methodName, context, expected in cases do
                    yield string (typeSpecToken (image.Force ()) typeName methodName)
                    yield context
                    yield string expected
            ]

        realRuntime args |> shouldEqual 0

        match underPawPrint "ModuleResolveTypeSpec" args with
        | RunOutcome.NormalExit (terminalState, _, _)
        | RunOutcome.ProcessExit (terminalState, _, _) ->
            match terminalState.LatchedExitCode with
            | 0 -> ()
            | failing ->
                let typeName, methodName, context, expected = cases.[failing - 1]

                failwith
                    $"%s{typeName}.%s{methodName} in context %s{context}: the real runtime classifies it %d{expected}; PawPrint does not"
        | other -> failwith $"expected the guest to exit, got %A{other}"
