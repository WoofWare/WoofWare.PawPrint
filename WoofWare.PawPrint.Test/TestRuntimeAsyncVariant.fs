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

        // .NET 10's CoreLib already declares runtime async's infrastructure (`AsyncHelpers.Await` and
        // the like); .NET 11 compiles every async method of both assemblies as runtime-async.
        match runtime with
        | EmulatedRuntime.Net10 -> asyncBodies |> shouldBeGreaterThan 0
        | EmulatedRuntime.Net11 -> asyncBodies |> shouldBeGreaterThan 100

    /// What the corpus check below found, for asserting it is not vacuous.
    [<RequireQualifiedAccess>]
    type private CorpusOutcome =
        | Variant of MethodReturnKind * isValueTask : bool * body : string
        | AsyncCallNoPromise
        | NoPromise
        | Delegate

    /// Whether a TypeDef row's immediate base type is named `System.MulticastDelegate`, and whether
    /// it is flagged `TypeAttributes.Import`, read straight from the image with
    /// System.Reflection.Metadata.
    let private delegateAndComImportRows
        (path : string)
        : System.Collections.Generic.HashSet<TypeDefinitionHandle> *
          System.Collections.Generic.HashSet<TypeDefinitionHandle>
        =
        use stream = File.OpenRead path
        use pe = new PEReader (stream)
        let reader = pe.GetMetadataReader ()

        let delegates = System.Collections.Generic.HashSet ()
        let comImports = System.Collections.Generic.HashSet ()

        for handle in reader.TypeDefinitions do
            let row = reader.GetTypeDefinition handle

            if int row.Attributes &&& int TypeAttributes.Import <> 0 then
                comImports.Add handle |> ignore

            if not row.BaseType.IsNil then
                let ns, name =
                    match row.BaseType.Kind with
                    | HandleKind.TypeReference ->
                        let reference =
                            reader.GetTypeReference (TypeReferenceHandle.op_Explicit row.BaseType)

                        reader.GetString reference.Namespace, reader.GetString reference.Name
                    | HandleKind.TypeDefinition ->
                        let definition =
                            reader.GetTypeDefinition (TypeDefinitionHandle.op_Explicit row.BaseType)

                        reader.GetString definition.Namespace, reader.GetString definition.Name
                    | _ -> "", ""

                if ns = "System" && name = "MulticastDelegate" then
                    delegates.Add handle |> ignore

        delegates, comImports

    let private bodyKind (body : MethodBody) : string =
        match body with
        | MethodBody.Il _ -> "Il"
        | MethodBody.AsyncIl _ -> "AsyncIl"
        | MethodBody.Abstract -> "Abstract"
        | MethodBody.InternalCall -> "InternalCall"
        | MethodBody.PInvoke -> "PInvoke"
        | MethodBody.RuntimeProvided _ -> "RuntimeProvided"
        | MethodBody.MissingIl -> "MissingIl"

    [<TestCaseSource(nameof runtimes)>]
    let ``every promise-returning method of a type neither a delegate nor ComImport has an async variant with the unwrapped signature, and no other method has one``
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
            let delegates, comImports =
                delegateAndComImportRows (Path.Combine (frameworkDirectory, name + ".dll"))

            for method : MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> in
                image.Methods.Values do
                let facts = MethodInfo.requireMetadata "TestRuntimeAsyncVariant" method
                let declaringType = method.RequiredDeclaringType.Identity.TypeDefinition.Get

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

                method.TryVariant |> shouldEqual (Some MethodVariant.ordinary)

                let updated, variant =
                    RuntimeAsyncMethod.asyncVariantOf loggerFactory runtimeDirs coreLib image method assemblies.Value

                assemblies.Value <- updated

                // Neither assembly declares a promise-returning method of a ComImport type.
                if comImports.Contains declaringType then
                    kind |> shouldEqual MethodReturnKind.NormalMethod

                match delegates.Contains declaringType, declaresAsync, kind, variant with
                | true, _, _, Error AsyncVariantRefusal.DelegateMethod ->
                    ordinaryConvention |> shouldEqual false
                    outcomes.Add CorpusOutcome.Delegate |> ignore
                | false, false, MethodReturnKind.NormalMethod, Error AsyncVariantRefusal.ReturnsNoPromise ->
                    ordinaryConvention |> shouldEqual false
                    outcomes.Add CorpusOutcome.NoPromise |> ignore
                | false, true, MethodReturnKind.NormalMethod, Error AsyncVariantRefusal.ReturnsNoPromise ->
                    // `Await`, `Suspend` and the like: one MethodDesc, with the async calling convention.
                    ordinaryConvention |> shouldEqual true
                    outcomes.Add CorpusOutcome.AsyncCallNoPromise |> ignore
                | false,
                  _,
                  (MethodReturnKind.GenericTaskReturningMethod | MethodReturnKind.NonGenericTaskReturningMethod),
                  Ok asyncVariant ->
                    ordinaryConvention |> shouldEqual false

                    match asyncVariant.TryVariant with
                    | Some MethodVariant.AsyncVariant -> ()
                    | other -> failwith $"%s{name}: the async variant of %O{method} is %O{other}"

                    asyncVariant.Signature
                    |> shouldEqual
                        { method.Signature with
                            ReturnType = expectedUnwrappedReturn facts.RawSignature.ReturnType
                        }

                    // Everything but the signature and the variant is the row's.
                    obj.ReferenceEquals (asyncVariant.Body, method.Body) |> shouldEqual true
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

                    // The IL of a method flagged `Async` is runtime-async IL, and of no other.
                    match method.Body with
                    | MethodBody.AsyncIl _ -> declaresAsync |> shouldEqual true
                    | _ -> declaresAsync |> shouldEqual false

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

                    outcomes.Add (CorpusOutcome.Variant (kind, isValueTask, bodyKind method.Body))
                    |> ignore
                | isDelegate, _, _, result ->
                    failwith
                        $"%s{name}: %O{method} (a delegate's: %b{isDelegate}; declares Async: %b{declaresAsync}; returns %O{kind}) gave %A{result}"

        // Every promise type comes back, from a body of each kind there is.
        let variants (body : string) : CorpusOutcome list =
            [
                CorpusOutcome.Variant (MethodReturnKind.NonGenericTaskReturningMethod, false, body)
                CorpusOutcome.Variant (MethodReturnKind.NonGenericTaskReturningMethod, true, body)
                CorpusOutcome.Variant (MethodReturnKind.GenericTaskReturningMethod, false, body)
                CorpusOutcome.Variant (MethodReturnKind.GenericTaskReturningMethod, true, body)
            ]

        let observed = outcomes |> Set.ofSeq

        for outcome in
            [
                CorpusOutcome.Delegate
                CorpusOutcome.NoPromise
                CorpusOutcome.AsyncCallNoPromise
                yield! variants "Il"
                CorpusOutcome.Variant (MethodReturnKind.GenericTaskReturningMethod, false, "Abstract")
                CorpusOutcome.Variant (MethodReturnKind.GenericTaskReturningMethod, true, "Abstract")
            ] do
            if not (observed.Contains outcome) then
                failwith $"the corpus gave no %A{outcome}"

        match runtime with
        | EmulatedRuntime.Net10 ->
            observed
            |> Set.exists (fun outcome ->
                match outcome with
                | CorpusOutcome.Variant (_, _, "AsyncIl") -> true
                | _ -> false
            )
            |> shouldEqual false
        | EmulatedRuntime.Net11 ->
            for outcome in variants "AsyncIl" do
                if not (observed.Contains outcome) then
                    failwith $"the corpus gave no %A{outcome}"

    /// The async variant of `method`, or why there is none, against the framework of `runtime`.
    let private variantOf
        (runtime : EmulatedRuntime)
        (assemblyName : string)
        (ns : string)
        (typeName : string)
        (methodName : string)
        : (MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> *
          Result<MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>, AsyncVariantRefusal>) list
        =
        let frameworkDirectory =
            FrameworkUnderTest.requireSharedFrameworkDirectoryOf runtime

        let loggerFactory, images = readImages frameworkDirectory
        let coreLib = snd images.Head

        let image =
            match images |> List.tryFind (fun (name, _) -> name = assemblyName) with
            | Some (_, image) -> image
            | None -> Assembly.readFile loggerFactory (Path.Combine (frameworkDirectory, assemblyName + ".dll"))

        let methods =
            image.Methods.Values
            |> Seq.filter (fun m ->
                m.Name = methodName
                && m.RequiredDeclaringType.Namespace = ns
                && m.RequiredDeclaringType.Name = typeName
            )
            |> List.ofSeq

        if methods.IsEmpty then
            failwith $"%s{assemblyName} declares no %s{ns}.%s{typeName}::%s{methodName}"

        let assemblies = LoadedAssemblies.ofAssemblies (image :: List.map snd images)

        methods
        |> List.map (fun method ->
            method,
            snd (
                RuntimeAsyncMethod.asyncVariantOf loggerFactory [ frameworkDirectory ] coreLib image method assemblies
            )
        )

    /// Each of `methods` has an async variant, whose body is the method's own, of kind `body`.
    let private shouldHaveVariants
        (body : string)
        (methods :
            (MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> *
            Result<MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>, AsyncVariantRefusal>) list)
        : unit
        =
        for method, variant in methods do
            match variant with
            | Ok variant ->
                match variant.TryVariant with
                | Some MethodVariant.AsyncVariant -> ()
                | other -> failwith $"the async variant of %O{method} is %O{other}"

                obj.ReferenceEquals (variant.Body, method.Body) |> shouldEqual true
                bodyKind variant.Body |> shouldEqual body
            | Error refusal -> failwith $"%O{method} has no async variant: %A{refusal}"

    let private shouldBeRefused
        (refusal : AsyncVariantRefusal)
        (methods :
            (MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> *
            Result<MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>, AsyncVariantRefusal>) list)
        : unit
        =
        for method, variant in methods do
            match variant with
            | Error actual when actual = refusal -> ()
            | other -> failwith $"%O{method}: expected %A{refusal}, got %A{other}"

    [<Test>]
    let ``the variant of a user's runtime-async method runs its runtime-async IL`` () : unit =
        variantOf EmulatedRuntime.Net11 "System.Private.CoreLib" "System.IO" "StreamReader" "ReadBufferAsync"
        |> shouldHaveVariants "AsyncIl"

    [<Test>]
    let ``the variant of a promise-returning method with IL and no Async flag is an async version`` () : unit =
        variantOf EmulatedRuntime.Net11 "System.Private.CoreLib" "System.IO" "StreamWriter" "FlushAsyncInternal"
        |> shouldHaveVariants "Il"

        variantOf
            EmulatedRuntime.Net11
            "System.Private.CoreLib"
            "System.Threading.Tasks"
            "TaskCompletionSource`1"
            "get_Task"
        |> shouldHaveVariants "Il"

    [<Test>]
    let ``a virtual promise-returning method has a variant`` () : unit =
        let readAsync =
            variantOf EmulatedRuntime.Net11 "System.Private.CoreLib" "System.IO" "Stream" "ReadAsync"
            |> List.filter (fun (method, _) -> method.IsVirtual)

        // `ReadAsync(byte[], int, int, CancellationToken)` and `ReadAsync(Memory<byte>, CancellationToken)`.
        readAsync.Length |> shouldEqual 2
        readAsync |> shouldHaveVariants "Il"

    [<Test>]
    let ``an abstract promise-returning method has a variant, a name to dispatch on`` () : unit =
        variantOf
            EmulatedRuntime.Net11
            "System.Private.CoreLib"
            "System.Collections.Generic"
            "IAsyncEnumerator`1"
            "MoveNextAsync"
        |> shouldHaveVariants "Abstract"

    [<Test>]
    let ``a delegate's method has no variant, whatever it returns`` () : unit =
        // `Func<TResult>.Invoke` returns its type parameter; `ServerOptionsSelectionCallback.Invoke`
        // returns a `ValueTask<SslServerAuthenticationOptions>`.
        variantOf EmulatedRuntime.Net11 "System.Private.CoreLib" "System" "Func`1" "Invoke"
        |> shouldBeRefused AsyncVariantRefusal.DelegateMethod

        variantOf
            EmulatedRuntime.Net11
            "System.Net.Security"
            "System.Net.Security"
            "ServerOptionsSelectionCallback"
            "Invoke"
        |> shouldBeRefused AsyncVariantRefusal.DelegateMethod

    [<Test>]
    let ``ConfigureAwait returns no promise type, so has no variant`` () : unit =
        variantOf EmulatedRuntime.Net11 "System.Private.CoreLib" "System.Threading.Tasks" "Task`1" "ConfigureAwait"
        |> shouldBeRefused AsyncVariantRefusal.ReturnsNoPromise

    /// Shapes the framework does not declare, compiled for the purpose.
    let private fabricatedSource =
        """
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

[ComImport, Guid("3B6A9F4C-6D57-4E8A-9C1D-2F0E4B7A1C55"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IComThing
{
    Task Run();
}

public delegate Task<int> Producer();

public abstract class Holder
{
    [DllImport("nowhere")]
    public static extern Task<int> Imported();

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern ValueTask Internal();

    public static Task<int> WithIl() => Task.FromResult(3);

    public abstract ValueTask<string> Declared();

    public static T Identity<T>(T value) => value;
}
"""

    let private fabricatedVariants
        ()
        : Map<
              string,
              Result<MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>, AsyncVariantRefusal>
           >
        =
        let frameworkDirectory = FrameworkUnderTest.sharedFrameworkDirectory ()
        let _, loggerFactory = LoggerFactory.makeTest ()

        let coreLib =
            Assembly.readFile loggerFactory (Path.Combine (frameworkDirectory, "System.Private.CoreLib.dll"))

        let image =
            Roslyn.compileAssembly
                "AsyncVariantShapes"
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary
                []
                [ fabricatedSource ]

        let fixture =
            Assembly.read loggerFactory (Some "AsyncVariantShapes.dll") (new MemoryStream (image))

        let assemblies = LoadedAssemblies.ofAssemblies [ coreLib ; fixture ]

        fixture.Methods.Values
        |> Seq.filter (fun m -> m.Name <> ".ctor" && m.Name <> "BeginInvoke")
        |> Seq.map (fun method ->
            $"%s{method.RequiredDeclaringType.Name}::%s{method.Name}",
            snd (
                RuntimeAsyncMethod.asyncVariantOf
                    loggerFactory
                    [ frameworkDirectory ]
                    coreLib
                    fixture
                    method
                    assemblies
            )
        )
        |> Map.ofSeq

    [<Test>]
    let ``asyncVariantOf refuses by name the methods CoreCLR binds no await to, or PawPrint cannot run`` () : unit =
        let variants = fabricatedVariants ()

        let outcome (name : string) : string =
            match variants.[name] with
            | Ok variant -> $"variant (%s{bodyKind variant.Body})"
            | Error refusal -> $"%A{refusal}"

        [
            "IComThing::Run"
            "Producer::Invoke"
            "Producer::EndInvoke"
            "Holder::Imported"
            "Holder::Internal"
            "Holder::WithIl"
            "Holder::Declared"
            "Holder::Identity"
        ]
        |> List.map (fun name -> name, outcome name)
        |> shouldEqual
            [
                "IComThing::Run", "ComImportType"
                "Producer::Invoke", "DelegateMethod"
                "Producer::EndInvoke", "DelegateMethod"
                "Holder::Imported", "UnmodelledBody PInvoke"
                "Holder::Internal", "UnmodelledBody InternalCall"
                "Holder::WithIl", "variant (Il)"
                "Holder::Declared", "variant (Abstract)"
                "Holder::Identity", "ReturnsNoPromise"
            ]

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

    /// A type is a delegate only when its base type is CoreLib's `MulticastDelegate`, not another
    /// type of that name: one the same assembly defines, or one another assembly does.
    [<Test>]
    let ``a type deriving from a MulticastDelegate that is not CoreLib's is no delegate`` () : unit =
        let frameworkDirectory = FrameworkUnderTest.sharedFrameworkDirectory ()
        let _, loggerFactory = LoggerFactory.makeTest ()

        let coreLib =
            Assembly.readFile loggerFactory (Path.Combine (frameworkDirectory, "System.Private.CoreLib.dll"))

        let impostorSource =
            """
using System.Threading.Tasks;

namespace System
{
    public abstract class MulticastDelegate
    {
    }
}

public class Local : System.MulticastDelegate
{
    public Task<int> Run() => Task.FromResult(1);
}
"""

        let impostorImage =
            Roslyn.compileAssembly
                "MulticastImpostor"
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary
                []
                [ impostorSource ]

        let foreignSource =
            """
extern alias impostor;
using System.Threading.Tasks;

public class Foreign : impostor::System.MulticastDelegate
{
    public Task<int> Run() => Task.FromResult(2);
}
"""

        let foreignImage =
            Roslyn.compileAssembly
                "MulticastForeign"
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary
                [
                    Microsoft.CodeAnalysis.MetadataReference
                        .CreateFromImage(ImmutableArray.CreateRange impostorImage)
                        .WithAliases
                        [ "impostor" ]
                ]
                [ foreignSource ]

        let impostor =
            Assembly.read loggerFactory (Some "MulticastImpostor.dll") (new MemoryStream (impostorImage))

        let foreign =
            Assembly.read loggerFactory (Some "MulticastForeign.dll") (new MemoryStream (foreignImage))

        let assemblies = LoadedAssemblies.ofAssemblies [ coreLib ; impostor ; foreign ]

        let outcome (image : DumpedAssembly) (typeName : string) : string =
            let method =
                image.Methods.Values
                |> Seq.find (fun m -> m.Name = "Run" && m.RequiredDeclaringType.Name = typeName)

            match
                RuntimeAsyncMethod.asyncVariantOf loggerFactory [ frameworkDirectory ] coreLib image method assemblies
            with
            | _, Ok variant -> $"variant (%s{bodyKind variant.Body})"
            | _, Error refusal -> $"%A{refusal}"

        [ outcome impostor "Local" ; outcome foreign "Foreign" ]
        |> shouldEqual [ "variant (Il)" ; "variant (Il)" ]

    /// No call constructs an async version for a frame to run yet, so `frameBody` refuses one by
    /// name; it gives the ordinary variant of the same method its own IL.
    [<Test>]
    let ``frameBody refuses by name to run an async version`` () : unit =
        let _, loggerFactory = LoggerFactory.makeTest ()
        let coreLib = Assembly.readFile loggerFactory HostImage.coreLibPath
        let baseClassTypes = BaseClassTypes.ofCorelib coreLib
        let loaded = LoadedAssemblies.ofAssemblies [ coreLib ]

        let initialState = IlMachineState.initial loggerFactory ImmutableArray.Empty coreLib

        let state =
            { initialState with
                TypeSystem =
                    { initialState.TypeSystem with
                        ConcreteTypes = Corelib.concretizeAll loaded baseClassTypes AllConcreteTypes.Empty
                    }
            }

        // `TaskCompletionSource.Task`, a getter with IL returning a `Task`.
        let ordinary =
            coreLib.Methods.Values
            |> Seq.find (fun m ->
                m.Name = "get_Task"
                && m.RequiredDeclaringType.Namespace = "System.Threading.Tasks"
                && m.RequiredDeclaringType.Name = "TaskCompletionSource"
            )

        let variant =
            match
                RuntimeAsyncMethod.asyncVariantOf
                    loggerFactory
                    [ Path.GetDirectoryName HostImage.coreLibPath ]
                    coreLib
                    coreLib
                    ordinary
                    loaded
            with
            | _, Ok variant -> variant
            | _, Error refusal -> failwith $"%O{ordinary} has no async variant: %A{refusal}"

        let concretised (method : MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>) =
            match
                ExecutionConcretization.concretizeMethodWithAllGenerics
                    loggerFactory
                    baseClassTypes
                    ImmutableArray.Empty
                    method
                    ImmutableArray.Empty
                    state
            with
            | state, Ok (method, _) -> state, method
            | _, Error failure -> failwith $"concretising %O{method}: %s{TypeBindingFailure.describe failure}"

        let state, ordinary = concretised ordinary

        let _, frame =
            IlMachineStateExecution.frameBody loggerFactory baseClassTypes coreLib ordinary state

        obj.ReferenceEquals (frame.Method, ordinary) |> shouldEqual true

        let state, variant = concretised variant

        let refusal =
            Assert.Throws<System.Exception> (fun () ->
                IlMachineStateExecution.frameBody loggerFactory baseClassTypes coreLib variant state
                |> ignore
            )

        refusal.Message |> shouldContainText "an async version"
