namespace WoofWare.PawPrint.Test

open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The two `MethodDesc`s CoreCLR makes for a runtime-async method, as PawPrint names them: the body
/// Domain reads for a MethodDef flagged `MethodImplAttributes.Async`, the one constructor of an async
/// variant (`RuntimeAsyncMethod.asyncVariantOf`), and which methods take the async calling convention
/// (`RuntimeAsyncMethod.hasAsyncCallingConvention`).
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestRuntimeAsyncVariant =

    /// Both are compiled with runtime async on .NET 11, so each declares methods that carry
    /// `MethodImplAttributes.Async` and return a promise type; only CoreLib may declare one that
    /// returns anything else.
    let private assemblyNames = [ "System.Private.CoreLib" ; "System.Net.Http" ]

    /// The custom modifiers on the outside of `ty`, as a function putting them back around another
    /// type, and the type under them.
    let rec private peelModifiers (ty : TypeDefn) : (TypeDefn -> TypeDefn) * TypeDefn =
        match ty with
        | TypeDefn.Modified modified ->
            let rewrap, inner = peelModifiers modified.Unmodified

            (fun replacement ->
                TypeDefn.Modified
                    { modified with
                        Unmodified = rewrap replacement
                    }
            ),
            inner
        | ty -> id, ty

    /// What CoreCLR's class loader makes of a promise-returning signature's return column for the async
    /// variant (`methodtablebuilder.cpp`): the bytes of `Task<T>` or `ValueTask<T>` are replaced by those
    /// of `T`, and those of `Task` or `ValueTask` by `void`, leaving every custom modifier before them in
    /// place. A bare `void` decodes as `MethodReturnType.Void`, and a modified one as `Returns`.
    let private expectedUnwrappedReturn (returnType : MethodReturnType<TypeDefn>) : MethodReturnType<TypeDefn> =
        match returnType with
        | MethodReturnType.Void -> failwith "logic error: a promise-returning method returns void"
        | MethodReturnType.Returns ty ->
            match peelModifiers ty, ty with
            | (rewrap, TypeDefn.GenericInstantiation (_, args)), _ when args.Length = 1 ->
                MethodReturnType.Returns (rewrap args.[0])
            | (_, TypeDefn.GenericInstantiation (_, args)), _ ->
                failwith $"logic error: a promise type with %d{args.Length} type arguments"
            | (rewrap, _), TypeDefn.Modified _ -> MethodReturnType.Returns (rewrap TypeDefn.Void)
            | _, _ -> MethodReturnType.Void

    let private readImages (frameworkDirectory : string) =
        let _, loggerFactory = LoggerFactory.makeTest ()

        let images =
            assemblyNames
            |> List.map (fun name ->
                name, Assembly.readFile loggerFactory (Path.Combine (frameworkDirectory, name + ".dll"))
            )

        loggerFactory, images

    /// Whether a MethodDef row is flagged `Async` and names an IL body, read straight from the image
    /// with System.Reflection.Metadata.
    let private asyncWithIlRows (path : string) : System.Collections.Generic.HashSet<MethodDefinitionHandle> =
        use stream = File.OpenRead path
        use pe = new PEReader (stream)
        let reader = pe.GetMetadataReader ()

        reader.MethodDefinitions
        |> Seq.filter (fun handle ->
            let row = reader.GetMethodDefinition handle

            int row.ImplAttributes &&& 0x2000 <> 0 && row.RelativeVirtualAddress <> 0
        )
        |> System.Collections.Generic.HashSet

    let runtimes : TestCaseData list = FrameworkUnderTest.everyRuntime

    [<TestCaseSource(nameof runtimes)>]
    let ``Domain reads exactly the IL of a MethodDef flagged Async as AsyncIl`` (runtime : EmulatedRuntime) : unit =
        let frameworkDirectory =
            FrameworkUnderTest.requireSharedFrameworkDirectoryOf runtime

        let _, images = readImages frameworkDirectory

        let mutable asyncBodies = 0

        for name, image in images do
            let expected = asyncWithIlRows (Path.Combine (frameworkDirectory, name + ".dll"))

            for KeyValue (handle, method : MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>) in
                image.Methods do
                match method.Body with
                | MethodBody.AsyncIl _ ->
                    asyncBodies <- asyncBodies + 1

                    if not (expected.Contains handle) then
                        failwith $"%s{name}: %O{method} reads as AsyncIl, but its row is not an Async method with IL"
                | MethodBody.Il _ ->
                    if expected.Contains handle then
                        failwith $"%s{name}: %O{method} is an Async method with IL, but reads as Il"
                | MethodBody.InternalCall
                | MethodBody.PInvoke
                | MethodBody.RuntimeProvided _
                | MethodBody.Abstract
                | MethodBody.MissingIl ->
                    if expected.Contains handle then
                        failwith $"%s{name}: %O{method} is an Async method with IL, but reads as %O{method.Body}"

        match runtime with
        | EmulatedRuntime.Net10 -> asyncBodies |> shouldEqual 0
        | EmulatedRuntime.Net11 -> asyncBodies |> shouldBeGreaterThan 100

    /// What the corpus check below found, for asserting it is not vacuous.
    [<RequireQualifiedAccess>]
    type private CorpusOutcome =
        | Variant of MethodReturnKind * isValueTask : bool
        | NoPromise
        | NotAsync

    [<TestCaseSource(nameof runtimes)>]
    let ``the async variant of every Async promise-returning method has the unwrapped signature, and no other method has one``
        (runtime : EmulatedRuntime)
        : unit
        =
        let frameworkDirectory =
            FrameworkUnderTest.requireSharedFrameworkDirectoryOf runtime

        let loggerFactory, images = readImages frameworkDirectory
        let coreLib = snd images.Head
        let runtimeDirs = [ frameworkDirectory ]

        let assemblies = ref (LoadedAssemblies.ofAssemblies (List.map snd images))
        let outcomes = System.Collections.Generic.HashSet<CorpusOutcome> ()

        let ok (description : string) (result : LoadedAssemblies * Result<'a, TypeBindingFailure>) : 'a =
            match result with
            | updated, Ok value ->
                assemblies.Value <- updated
                value
            | _, Error failure -> failwith $"%s{description}: %s{TypeBindingFailure.describe failure}"

        for name, image in images do
            for method : MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> in
                image.Methods.Values do
                let facts = MethodInfo.requireMetadata "TestRuntimeAsyncVariant" method

                let declaresAsync =
                    facts.ImplAttributes.HasFlag RuntimeAsyncMethod.asyncImplAttribute

                let kind =
                    RuntimeAsyncMethod.classifyReturn
                        loggerFactory
                        runtimeDirs
                        coreLib
                        image
                        facts.RawSignature.ReturnType
                        assemblies.Value
                    |> ok $"%s{name}: classifying %O{method}"

                let ordinaryConvention =
                    RuntimeAsyncMethod.hasAsyncCallingConvention
                        loggerFactory
                        runtimeDirs
                        coreLib
                        image
                        method
                        assemblies.Value
                    |> ok $"%s{name}: the calling convention of %O{method}"

                method.TryVariant |> shouldEqual (Some MethodVariant.Ordinary)

                let updated, variant =
                    RuntimeAsyncMethod.asyncVariantOf loggerFactory runtimeDirs coreLib image method assemblies.Value

                assemblies.Value <- updated

                match declaresAsync, kind, variant with
                | false, _, Error AsyncVariantRefusal.NotDeclaredAsync ->
                    ordinaryConvention |> shouldEqual false
                    outcomes.Add CorpusOutcome.NotAsync |> ignore
                | true, MethodReturnKind.NormalMethod, Error AsyncVariantRefusal.ReturnsNoPromise ->
                    // `Await`, `Suspend` and the like: one MethodDesc, with the async calling convention.
                    ordinaryConvention |> shouldEqual true
                    outcomes.Add CorpusOutcome.NoPromise |> ignore
                | true,
                  (MethodReturnKind.GenericTaskReturningMethod | MethodReturnKind.NonGenericTaskReturningMethod),
                  Ok asyncVariant ->
                    ordinaryConvention |> shouldEqual false

                    asyncVariant.TryVariant |> shouldEqual (Some MethodVariant.AsyncVariant)

                    asyncVariant.Signature
                    |> shouldEqual
                        { method.Signature with
                            ReturnType = expectedUnwrappedReturn facts.RawSignature.ReturnType
                        }

                    // Everything but the signature and the variant is the row's.
                    asyncVariant.Body |> shouldEqual method.Body
                    asyncVariant.Name |> shouldEqual method.Name
                    asyncVariant.Owner |> shouldEqual method.Owner
                    asyncVariant.Generics |> shouldEqual method.Generics
                    asyncVariant.IsStatic |> shouldEqual method.IsStatic
                    asyncVariant.TryMetadata |> shouldEqual method.TryMetadata

                    // A different MethodDesc of the same MethodDef.
                    asyncVariant.IdentityKey |> shouldNotEqual method.IdentityKey
                    MethodInfo.NominallyEqual asyncVariant method |> shouldEqual false
                    MethodInfo.NominallyEqual asyncVariant asyncVariant |> shouldEqual true
                    MethodInfo.sameDeclaredMethod asyncVariant method |> shouldEqual true

                    RuntimeAsyncMethod.hasAsyncCallingConvention
                        loggerFactory
                        runtimeDirs
                        coreLib
                        image
                        asyncVariant
                        assemblies.Value
                    |> ok $"%s{name}: the calling convention of the async variant of %O{method}"
                    |> shouldEqual true

                    match
                        RuntimeAsyncMethod.asyncVariantOf
                            loggerFactory
                            runtimeDirs
                            coreLib
                            image
                            asyncVariant
                            assemblies.Value
                    with
                    | _, Error AsyncVariantRefusal.AlreadyAsyncVariant -> ()
                    | _, other ->
                        failwith $"%s{name}: the async variant of %O{method} gave a variant of its own: %A{other}"

                    let isValueTask =
                        match facts.RawSignature.ReturnType with
                        | MethodReturnType.Returns ty ->
                            match snd (peelModifiers ty) with
                            | TypeDefn.GenericInstantiation (generic, _) ->
                                match snd (peelModifiers generic) with
                                | TypeDefn.FromDefinition (_, kind)
                                | TypeDefn.FromReference (_, kind) -> kind = SignatureTypeKind.ValueType
                                | other -> failwith $"unexpected promise type %O{other}"
                            | TypeDefn.FromDefinition (_, kind)
                            | TypeDefn.FromReference (_, kind) -> kind = SignatureTypeKind.ValueType
                            | other -> failwith $"unexpected promise type %O{other}"
                        | MethodReturnType.Void -> failwith "logic error: a promise-returning method returns void"

                    outcomes.Add (CorpusOutcome.Variant (kind, isValueTask)) |> ignore
                | _, _, result ->
                    failwith
                        $"%s{name}: %O{method} (declares Async: %b{declaresAsync}, returns %O{kind}) gave %A{result}"

        outcomes.Contains CorpusOutcome.NotAsync |> shouldEqual true

        match runtime with
        | EmulatedRuntime.Net10 -> outcomes.Count |> shouldEqual 1
        | EmulatedRuntime.Net11 ->
            outcomes
            |> Set.ofSeq
            |> shouldEqual (
                Set.ofList
                    [
                        CorpusOutcome.NotAsync
                        CorpusOutcome.NoPromise
                        CorpusOutcome.Variant (MethodReturnKind.NonGenericTaskReturningMethod, false)
                        CorpusOutcome.Variant (MethodReturnKind.NonGenericTaskReturningMethod, true)
                        CorpusOutcome.Variant (MethodReturnKind.GenericTaskReturningMethod, false)
                        CorpusOutcome.Variant (MethodReturnKind.GenericTaskReturningMethod, true)
                    ]
            )

    /// A method the runtime synthesises has no MethodDef, so no variants.
    [<Test>]
    let ``a synthesised method has no async variant and no async calling convention`` () : unit =
        let frameworkDirectory = FrameworkUnderTest.sharedFrameworkDirectory ()
        let loggerFactory, images = readImages frameworkDirectory
        let coreLib = snd images.Head
        let assemblies = LoadedAssemblies.ofAssemblies (List.map snd images)

        let metadata =
            coreLib.Methods.Values
            |> Seq.find (fun m -> m.Name = "ToString" && m.RequiredDeclaringType.Name = "Object")

        let synthesised =
            MethodInfo.Synthesised (metadata.Core, SynthesisedMethod.EntryPointPlaceholder)

        synthesised.TryVariant |> shouldEqual None

        match
            RuntimeAsyncMethod.asyncVariantOf
                loggerFactory
                [ frameworkDirectory ]
                coreLib
                coreLib
                synthesised
                assemblies
        with
        | _, Error AsyncVariantRefusal.Synthesised -> ()
        | _, other -> failwith $"expected a refusal for a synthesised method, got %A{other}"

        match
            RuntimeAsyncMethod.hasAsyncCallingConvention
                loggerFactory
                [ frameworkDirectory ]
                coreLib
                coreLib
                synthesised
                assemblies
        with
        | _, Ok false -> ()
        | _, other -> failwith $"expected no async calling convention for a synthesised method, got %A{other}"

    /// The return types a generated method is given, built from CoreLib's own promise types: each of
    /// the four, each spelled with the other's `class`/`valuetype`, other types, and any of these
    /// under custom modifiers.
    type private Spellings =
        {
            Task : TypeDefn
            TaskOfT : TypeDefn
            ValueTask : TypeDefn
            ValueTaskOfT : TypeDefn
            List : TypeDefn
        }

    let private respell (kind : SignatureTypeKind) (ty : TypeDefn) : TypeDefn =
        match ty with
        | TypeDefn.FromDefinition (identity, _) -> TypeDefn.FromDefinition (identity, kind)
        | TypeDefn.FromReference (reference, _) -> TypeDefn.FromReference (reference, kind)
        | other -> failwith $"logic error: %O{other} is not a TypeDef or TypeRef token"

    let private genReturnType (spellings : Spellings) : Gen<MethodReturnType<TypeDefn>> =
        let genArgument : Gen<TypeDefn> =
            Gen.elements
                [
                    TypeDefn.PrimitiveType PrimitiveType.Int32
                    TypeDefn.PrimitiveType PrimitiveType.String
                    TypeDefn.PrimitiveType PrimitiveType.Single
                    TypeDefn.GenericMethodParameter 0
                    spellings.Task
                    TypeDefn.Modified
                        {
                            Unmodified = TypeDefn.PrimitiveType PrimitiveType.Int64
                            Modifier = spellings.List
                            IsRequired = true
                        }
                ]

        let genCore : Gen<TypeDefn> =
            gen {
                let! argument = genArgument

                return!
                    Gen.elements
                        [
                            spellings.Task
                            spellings.ValueTask
                            TypeDefn.GenericInstantiation (spellings.TaskOfT, ImmutableArray.Create argument)
                            TypeDefn.GenericInstantiation (spellings.ValueTaskOfT, ImmutableArray.Create argument)
                            respell SignatureTypeKind.ValueType spellings.Task
                            respell SignatureTypeKind.Class spellings.ValueTask
                            TypeDefn.GenericInstantiation (
                                respell SignatureTypeKind.ValueType spellings.TaskOfT,
                                ImmutableArray.Create argument
                            )
                            TypeDefn.GenericInstantiation (spellings.List, ImmutableArray.Create argument)
                            argument
                            TypeDefn.Void
                        ]
            }

        let genModifier : Gen<TypeDefn -> TypeDefn> =
            gen {
                let! modifier =
                    Gen.elements [ spellings.List ; TypeDefn.PrimitiveType PrimitiveType.Int32 ; spellings.Task ]

                let! isRequired = Gen.elements [ true ; false ]

                return
                    fun ty ->
                        TypeDefn.Modified
                            {
                                Unmodified = ty
                                Modifier = modifier
                                IsRequired = isRequired
                            }
            }

        gen {
            let! core = genCore
            let! modifiers = Gen.choose (0, 2) |> Gen.bind (fun n -> Gen.listOfLength n genModifier)
            let! bareVoid = Gen.elements [ true ; false ]

            match modifiers, core with
            | [], TypeDefn.Void when bareVoid -> return MethodReturnType.Void
            | modifiers, core -> return MethodReturnType.Returns (List.foldBack (fun wrap ty -> wrap ty) modifiers core)
        }

    /// Whether `returnType` is one of CoreLib's four promise types as the signature spells it, said
    /// apart from `RuntimeAsyncMethod.classifyReturn`: the spellings are exactly the ones
    /// `genReturnType` builds from CoreLib's own.
    let private isPromise (spellings : Spellings) (returnType : MethodReturnType<TypeDefn>) : bool =
        match returnType with
        | MethodReturnType.Void -> false
        | MethodReturnType.Returns ty ->
            match snd (peelModifiers ty) with
            | TypeDefn.GenericInstantiation (generic, args) ->
                args.Length = 1
                && (generic = spellings.TaskOfT || generic = spellings.ValueTaskOfT)
            | other -> other = spellings.Task || other = spellings.ValueTask

    [<Test>]
    let ``asyncVariantOf unwraps exactly the promise types, keeping their custom modifiers`` () : unit =
        let frameworkDirectory = FrameworkUnderTest.sharedFrameworkDirectory ()
        let loggerFactory, images = readImages frameworkDirectory
        let coreLib = snd images.Head
        let assemblies = LoadedAssemblies.ofAssemblies (List.map snd images)

        let methodOf (ns : string) (typeName : string) (methodName : string) =
            coreLib.Methods.Values
            |> Seq.find (fun m ->
                m.Name = methodName
                && m.RequiredDeclaringType.Namespace = ns
                && m.RequiredDeclaringType.Name = typeName
            )

        let returned (m : MethodInfo<_, _, TypeDefn>) : TypeDefn =
            match m.Signature.ReturnType with
            | MethodReturnType.Returns ty -> ty
            | MethodReturnType.Void -> failwith $"%O{m} returns void"

        let genericOf (ty : TypeDefn) : TypeDefn =
            match ty with
            | TypeDefn.GenericInstantiation (generic, _) -> generic
            | other -> failwith $"%O{other} is not a generic instantiation"

        let tasks = "System.Threading.Tasks"

        let spellings =
            {
                Task = returned (methodOf tasks "Task" "get_CompletedTask")
                TaskOfT = genericOf (returned (methodOf tasks "Task" "FromResult"))
                ValueTask = returned (methodOf tasks "ValueTask" "get_CompletedTask")
                ValueTaskOfT = genericOf (returned (methodOf tasks "ValueTask" "FromResult"))
                List = genericOf (returned (methodOf "System.Collections.Generic" "List`1" "GetRange"))
            }

        // A static CoreLib method with IL, which the property flags `Async` and re-signs.
        let template = methodOf tasks "Task" "get_CompletedTask"

        let withReturn (returnType : MethodReturnType<TypeDefn>) =
            match template with
            | MethodInfo.Metadata (core, facts, variant) ->
                let instructions =
                    match core.Body with
                    | MethodBody.Il instructions -> instructions
                    | other -> failwith $"expected Task.get_CompletedTask to have IL, got %O{other}"

                let signature =
                    { core.Signature with
                        ReturnType = returnType
                    }

                MethodInfo.Metadata (
                    { core with
                        Signature = signature
                        Body = MethodBody.AsyncIl instructions
                    },
                    { facts with
                        RawSignature = signature
                        ImplAttributes = facts.ImplAttributes ||| RuntimeAsyncMethod.asyncImplAttribute
                    },
                    variant
                )
            | MethodInfo.Synthesised _ -> failwith "logic error: a CoreLib method read as synthesised"

        let property (returnType : MethodReturnType<TypeDefn>) : unit =
            let ordinary = withReturn returnType

            let convention method =
                match
                    RuntimeAsyncMethod.hasAsyncCallingConvention
                        loggerFactory
                        [ frameworkDirectory ]
                        coreLib
                        coreLib
                        method
                        assemblies
                with
                | _, Ok convention -> convention
                | _, Error failure -> failwith (TypeBindingFailure.describe failure)

            match
                isPromise spellings returnType,
                RuntimeAsyncMethod.asyncVariantOf
                    loggerFactory
                    [ frameworkDirectory ]
                    coreLib
                    coreLib
                    ordinary
                    assemblies
            with
            | true, (_, Ok variant) ->
                variant.Signature.ReturnType |> shouldEqual (expectedUnwrappedReturn returnType)

                variant.Signature.ParameterTypes
                |> shouldEqual ordinary.Signature.ParameterTypes

                variant.IdentityKey |> shouldNotEqual ordinary.IdentityKey
                convention ordinary |> shouldEqual false
                convention variant |> shouldEqual true
            | false, (_, Error AsyncVariantRefusal.ReturnsNoPromise) -> convention ordinary |> shouldEqual true
            | promise, (_, result) -> failwith $"%O{returnType} (a promise: %b{promise}) gave %A{result}"

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 500,
            Prop.forAll (Arb.fromGen (genReturnType spellings)) property
        )

    /// On .NET 11, `StreamReader.ReadToEndAsync` returns the `Task<string>` of
    /// `ReadToEndAsyncInternal`, a runtime-async method, whose IL is written for its async variant.
    let private streamReaderGuest : string =
        """
using System.IO;
using System.Text;

public class Program
{
    public static int Main(string[] args)
    {
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        return reader.ReadToEndAsync().Result == "hello" ? 0 : 1;
    }
}
"""

    [<Test>]
    let ``calling the ordinary variant of a runtime-async method is refused as the task-returning thunk`` () : unit =
        match FrameworkUnderTest.runtime () with
        | EmulatedRuntime.Net10 -> Assert.Ignore ".NET 10's CoreLib declares no runtime-async method"
        | EmulatedRuntime.Net11 -> ()

        let name = "StreamReaderReadToEndAsync.cs"
        let image = Roslyn.compile [ streamReaderGuest ]

        let _messages, loggerFactory =
            LoggerFactory.makeTestWithProperties [ "source_file", name ]

        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let exc =
            Assert.Throws<GuestFailureException> (fun () ->
                BoundedRun.runWith
                    loggerFactory
                    BoundedRun.defaultMaxSteps
                    name
                    (Some name)
                    peImage
                    (HostConfig.Default (FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform))
                |> ExpectRun.ended
                |> ignore<RunOutcome>
            )

        exc.Message |> shouldContainText "task-returning thunk"
        exc.Message |> shouldContainText "ReadToEndAsyncInternal"
