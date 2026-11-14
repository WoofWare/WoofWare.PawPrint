namespace WoofWare.PawPrint.Test

open System
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata.Ecma335
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `NewobjTarget.classify` against the test host's own JIT, over every method of the test host's
/// CoreLib that a `newobj` can be compiled over: each instance constructor, static method and
/// abstract method of every type that is not generic, that is not itself generic and is not
/// varargs. For each, a `DynamicMethod` holding `ldarg.0; brfalse skip; <a default per parameter>;
/// newobj m; pop; skip: ret` is compiled, by calling it with `false` so that the `newobj` never
/// runs, and the rule's answer for the method as PawPrint reads it from the image must be the
/// exception the compilation raised, or none.
///
/// This compiles some twenty-four thousand methods in the test host's own process. It leaves out
/// every instance method that is neither a constructor nor abstract, because CoreCLR crashed the
/// process (SIGSEGV) compiling a `newobj` of some of them: a delegate's `Invoke`, a struct's
/// method returning a `ReadOnlySpan<char>`, and a class's returning a `ValueTask<int>`. If the
/// test host crashes after CoreLib is bumped, look here first: a new shape among the methods
/// compiled may crash the JIT too.
///
/// The test host's runtime is the oldest supported, so this checks that runtime's JIT and
/// CoreLib.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestNewobjTargetOnHost =

    /// Owns the `DynamicMethod`s, which skip visibility, so that a private constructor compiles.
    type private Owner = class end

    /// What compiling a `newobj` of a method raised: the exception's type and message, or `None`.
    let private compile (method : MethodBase) : (Type * string) option =
        let pushDefault (il : ILGenerator) (t : Type) : unit =
            if t.IsByRef || t.IsPointer || t.IsFunctionPointer then
                il.Emit OpCodes.Ldc_I4_0
                il.Emit OpCodes.Conv_U
            elif not t.IsValueType then
                il.Emit OpCodes.Ldnull
            else
                let local = il.DeclareLocal t
                il.Emit (OpCodes.Ldloca, local)
                il.Emit (OpCodes.Initobj, t)
                il.Emit (OpCodes.Ldloc, local)

        let dm =
            DynamicMethod ("probe", typeof<Void>, [| typeof<bool> |], typeof<Owner>.Module, true)

        let il = dm.GetILGenerator ()
        let skip = il.DefineLabel ()
        il.Emit OpCodes.Ldarg_0
        il.Emit (OpCodes.Brfalse, skip)

        for parameter in method.GetParameters () do
            pushDefault il parameter.ParameterType

        match method with
        | :? ConstructorInfo as ctor -> il.Emit (OpCodes.Newobj, ctor)
        | :? Reflection.MethodInfo as m -> il.Emit (OpCodes.Newobj, m)
        | other -> failwith $"%O{other} is neither a constructor nor a method"

        il.Emit OpCodes.Pop
        il.MarkLabel skip
        il.Emit OpCodes.Ret

        try
            (dm.CreateDelegate typeof<Action<bool>> :?> Action<bool>).Invoke false
            None
        with e ->
            Some (e.GetType (), e.Message)

    /// The exception the rule says the compilation raises, or `None` where it constructs. Fails
    /// for `NotInstanceConstructor`, which is not compiled.
    let private raises (target : NewobjTarget) : (Type * string) option =
        match target with
        | NewobjTarget.AbstractMethod -> Some (typeof<BadImageFormatException>, "Bad IL format.")
        | NewobjTarget.StaticMethod ->
            Some (typeof<InvalidProgramException>, "Common Language Runtime detected an invalid program.")
        | NewobjTarget.AbstractClass ->
            Some (typeof<InvalidOperationException>, "Instances of abstract classes cannot be created.")
        | NewobjTarget.Constructs -> None
        | NewobjTarget.NotInstanceConstructor ->
            failwith "BUG: a method the rule calls not an instance constructor was compiled"

    let private methodBase (m : MemberInfo) : MethodBase option =
        match m with
        | :? MethodBase as m -> Some m
        | _ -> None

    [<Test>]
    let ``the rule gives what the JIT raises for a newobj of every method of CoreLib`` () : unit =
        let _, loggerFactory = LoggerFactory.makeTest ()
        let corelib = Assembly.readFile loggerFactory HostImage.coreLibPath
        let baseClassTypes = BaseClassTypes.ofCorelib corelib
        let loaded = LoadedAssemblies.ofAssemblies [ corelib ]

        let tokenBinding =
            Set.ofList (ResolutionFault.typeNames ResolutionFault.TokenBinding)

        let flags =
            BindingFlags.Public
            ||| BindingFlags.NonPublic
            ||| BindingFlags.Instance
            ||| BindingFlags.Static
            ||| BindingFlags.DeclaredOnly

        let mutable enumerated = 0
        let mutable compiled = 0
        let mutable skipped = 0
        let mutable outcomes : Map<NewobjTarget, int> = Map.empty
        let mismatches = ResizeArray<string> ()
        let covered = Collections.Generic.HashSet<string> ()

        for t in typeof<obj>.Assembly.GetTypes () do
            if not t.ContainsGenericParameters then
                let imageType =
                    corelib.TypeDefs.[MetadataTokens.TypeDefinitionHandle t.MetadataToken]

                let isValueType = LoadedTypeInfo.isValueType baseClassTypes loaded imageType

                if isValueType <> t.IsValueType then
                    mismatches.Add
                        $"%s{t.FullName}: PawPrint reads IsValueType %b{isValueType}, reflection %b{t.IsValueType}"

                for m in t.GetMembers flags |> Seq.choose methodBase do
                    if
                        not m.ContainsGenericParameters
                        && not (m.CallingConvention.HasFlag CallingConventions.VarArgs)
                    then
                        enumerated <- enumerated + 1
                        let describe = $"%s{t.FullName}::%O{m}"

                        let imageMethod =
                            corelib.Methods.[MetadataTokens.MethodDefinitionHandle m.MetadataToken]

                        if imageMethod.RequiredDeclaringType.Definition.Get <> imageType.TypeDefHandle then
                            mismatches.Add $"%s{describe}: the image's method is declared on another type"

                        let target =
                            NewobjTarget.classify
                                (MethodInfo.requireMetadata "newobj sweep" imageMethod).MethodAttributes
                                imageMethod.Name
                                imageType.TypeAttributes
                                isValueType

                        outcomes <- outcomes |> Map.change target (fun n -> Some (1 + defaultArg n 0))

                        if not m.IsConstructor && not m.IsStatic && not m.IsAbstract then
                            skipped <- skipped + 1

                            if target <> NewobjTarget.NotInstanceConstructor then
                                mismatches.Add $"%s{describe}: not compiled, and the rule says %O{target}"
                        else
                            compiled <- compiled + 1

                            if target = NewobjTarget.NotInstanceConstructor then
                                mismatches.Add $"%s{describe}: the rule says it is not an instance constructor"
                            else

                            let actual = compile m

                            match actual with
                            | Some (exnType, _) when not (tokenBinding.Contains exnType.FullName) ->
                                mismatches.Add
                                    $"%s{describe}: the JIT raised %s{exnType.FullName}, which ResolutionFault.TokenBinding does not name"
                            | _ -> ()

                            if actual <> raises target then
                                mismatches.Add $"%s{describe}: the JIT raised %A{actual}, and the rule says %O{target}"

                            let note (shape : string) (holds : bool) =
                                if holds then
                                    covered.Add $"%s{shape} %O{target}" |> ignore<bool>

                            note "static abstract interface member" (m.IsStatic && m.IsAbstract && t.IsInterface)
                            note "static method of an interface" (m.IsStatic && not m.IsAbstract && t.IsInterface)
                            // `IsConstructor` is false of a class constructor.
                            note "class constructor" (m.IsStatic && (m :? ConstructorInfo))

                            note
                                "value type's constructor"
                                (m.IsConstructor && not m.IsStatic && t.IsValueType && not t.IsByRefLike)

                            note "byref-like struct's constructor" (m.IsConstructor && not m.IsStatic && t.IsByRefLike)
                            note "String's constructor" (m.IsConstructor && t = typeof<string>)
                            note "Delegate's constructor" (m.IsConstructor && not m.IsStatic && t = typeof<Delegate>)

        TestContext.Out.WriteLine $"enumerated %d{enumerated}, compiled %d{compiled}, skipped %d{skipped}: %A{outcomes}"

        mismatches |> Seq.truncate 50 |> List.ofSeq |> shouldEqual []
        compiled + skipped |> shouldEqual enumerated
        outcomes |> Map.values |> Seq.sum |> shouldEqual enumerated
        outcomes.[NewobjTarget.NotInstanceConstructor] |> shouldEqual skipped

        // Every outcome a compilation gives, among a good many methods.
        for outcome in
            [
                NewobjTarget.AbstractMethod
                NewobjTarget.StaticMethod
                NewobjTarget.AbstractClass
                NewobjTarget.Constructs
            ] do
            outcomes
            |> Map.tryFind outcome
            |> Option.defaultValue 0
            |> shouldBeGreaterThan 50

        covered
        |> Seq.sort
        |> List.ofSeq
        |> shouldEqual
            [
                "Delegate's constructor AbstractClass"
                "String's constructor Constructs"
                "byref-like struct's constructor Constructs"
                "class constructor StaticMethod"
                "static abstract interface member AbstractMethod"
                "static method of an interface StaticMethod"
                "value type's constructor Constructs"
            ]
