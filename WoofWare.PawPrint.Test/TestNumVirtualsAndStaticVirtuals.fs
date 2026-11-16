namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.InterfaceCorpusGenerator

/// `VirtualSlotLayout.numVirtualsAndStaticVirtuals` is CoreCLR's `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals`:
/// `GetNumVirtuals()`, plus an interface's own static virtuals. `Type.GetInterfaceMap` calls
/// `GetMethodAt` at every slot below it, so the count and `VirtualSlotLayout.methodAt` must agree:
/// a count past `methodAt`'s last slot makes `GetInterfaceMap` throw, and one short of it drops
/// methods from the map.
///
/// The host is the oracle, through the QCall's managed wrapper and `GetMethodAt` by reflection, over
/// one image read by both sides. On .NET 11 CoreCLR counts an async variant slot after each
/// `Task`-returning virtual, at which `GetMethodAt` answers null, and PawPrint lays out no such slot
/// (`docs/divergences.md`, "A runtime-async method's async variant has no virtual slot of its own"); so
/// the law is that PawPrint's count is the host's less its null slots, which on a .NET 10 host is
/// equality. The per-runtime tests check .NET 11's CoreLib without a .NET 11 host.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestNumVirtualsAndStaticVirtuals =

    // The factory is intentionally undisposed: the DumpedAssembly loggers close over its sinks and
    // outlive this scope.
    let private loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory =
        let _, loggerFactory = LoggerFactory.makeTest ()
        loggerFactory

    let private corelib : DumpedAssembly =
        Assembly.readFile loggerFactory HostImage.coreLibPath

    let private hostCoreLib : Reflection.Assembly = typeof<obj>.Assembly

    let private bct : BaseClassTypes<DumpedAssembly> = BaseClassTypes.ofCorelib corelib

    /// An initial state over `corelib`, read from the directory `corelibPath` names, concretised once.
    let private stateOver
        (corelibPath : string)
        (corelib : DumpedAssembly)
        (bct : BaseClassTypes<DumpedAssembly>)
        : IlMachineState
        =
        let dirs = ImmutableArray.Create (Path.GetDirectoryName corelibPath)
        let initial = IlMachineState.initial loggerFactory dirs corelib

        { initial with
            TypeSystem =
                { initial.TypeSystem with
                    ConcreteTypes =
                        Corelib.concretizeAll initial.TypeSystem._LoadedAssemblies bct AllConcreteTypes.Empty
                }
        }

    /// The host's CoreLib alone. Each corpus is added to it.
    let private coreState : IlMachineState = stateOver HostImage.coreLibPath corelib bct

    let private count
        (bct : BaseClassTypes<DumpedAssembly>)
        (state : IlMachineState)
        (target : RuntimeTypeHandleTarget)
        : IlMachineState * int
        =
        VirtualSlotLayout.numVirtualsAndStaticVirtuals loggerFactory bct "test" state target

    let private methodAt
        (bct : BaseClassTypes<DumpedAssembly>)
        (state : IlMachineState)
        (target : RuntimeTypeHandleTarget)
        (slot : int)
        : IlMachineState * VirtualSlotLayout.MethodAtSlot
        =
        VirtualSlotLayout.methodAt loggerFactory bct "test" state target slot

    // ----- The host's answers -----

    let private runtimeType : Type = hostCoreLib.GetType "System.RuntimeType"

    let private internalHandle : Type =
        hostCoreLib.GetType "System.RuntimeMethodHandleInternal"

    let private nonNullMethod (description : string) (m : MethodInfo) : MethodInfo =
        match m with
        | null -> failwith $"the host's CoreLib has no %s{description}"
        | m -> m

    let private hostWrapper : MethodInfo =
        typeof<RuntimeTypeHandle>
            .GetMethod (
                "GetNumVirtualsAndStaticVirtuals",
                BindingFlags.NonPublic ||| BindingFlags.Static,
                null,
                [| runtimeType |],
                null
            )
        |> nonNullMethod "RuntimeTypeHandle.GetNumVirtualsAndStaticVirtuals(RuntimeType)"

    let private hostGetNumVirtuals : MethodInfo =
        typeof<RuntimeTypeHandle>
            .GetMethod ("GetNumVirtuals", BindingFlags.NonPublic ||| BindingFlags.Static, null, [| runtimeType |], null)
        |> nonNullMethod "RuntimeTypeHandle.GetNumVirtuals(RuntimeType)"

    let private hostGetMethodAt : MethodInfo =
        typeof<RuntimeTypeHandle>
            .GetMethod (
                "GetMethodAt",
                BindingFlags.NonPublic ||| BindingFlags.Static,
                null,
                [| runtimeType ; typeof<int> |],
                null
            )
        |> nonNullMethod "RuntimeTypeHandle.GetMethodAt(RuntimeType, int)"

    let private hostGetMethodBase : MethodInfo =
        runtimeType.GetMethod (
            "GetMethodBase",
            BindingFlags.NonPublic ||| BindingFlags.Static,
            null,
            [| runtimeType ; internalHandle |],
            null
        )
        |> nonNullMethod "RuntimeType.GetMethodBase(RuntimeType, RuntimeMethodHandleInternal)"

    let private hostIsNullHandle : MethodInfo =
        internalHandle.GetMethod ("IsNullHandle", BindingFlags.NonPublic ||| BindingFlags.Instance)
        |> nonNullMethod "RuntimeMethodHandleInternal.IsNullHandle()"

    let private hostCount (t : Type) : int =
        hostWrapper.Invoke ((null : obj), [| box t |]) :?> int

    let private hostNumVirtuals (t : Type) : int =
        hostGetNumVirtuals.Invoke ((null : obj), [| box t |]) :?> int

    /// What the host's `GetMethodAt` names at each slot below its count: `None` for a null handle,
    /// which `GetInterfaceMap` skips.
    let private hostSlots (t : Type) : MethodBase option list =
        [
            for slot in 0 .. hostCount t - 1 do
                let handle = hostGetMethodAt.Invoke ((null : obj), [| box t ; box slot |])

                if hostIsNullHandle.Invoke (handle, [||]) :?> bool then
                    yield None
                else
                    yield Some (hostGetMethodBase.Invoke ((null : obj), [| box t ; handle |]) :?> MethodBase)
        ]

    // ----- Comparing a slot -----

    let private methodToken (slot : VtableSlot) : int =
        match fst slot.Method.IdentityKey with
        | Some (_, MethodVariant.AsyncVariant) ->
            failwith "a slot holds an async variant, which PawPrint's tables never place"
        | Some (handle, MethodVariant.Ordinary) ->
            MetadataTokens.GetToken (MethodDefinitionHandle.op_Implicit handle : EntityHandle)
        | None -> -1

    let private tokenOfTypeDefinition (identity : ResolvedTypeIdentity) : int =
        MetadataTokens.GetToken (TypeDefinitionHandle.op_Implicit identity.TypeDefinition.Get : EntityHandle)

    /// Whether `occupant` is `expected`: the same MethodDef of the same definition of the same image.
    let private sameMethod (images : HostTypeTarget.Images) (occupant : VtableSlot) (expected : MethodBase) : bool =
        let declaring =
            if expected.DeclaringType.IsGenericType then
                expected.DeclaringType.GetGenericTypeDefinition ()
            else
                expected.DeclaringType

        let assembly =
            images
            |> List.tryFind (fun (host, _) -> Object.ReferenceEquals (host, declaring.Assembly))
            |> Option.map (fun (_, dumped) -> dumped.DefinitionFullName)

        methodToken occupant = expected.MetadataToken
        && tokenOfTypeDefinition occupant.DeclaredBy.Identity = declaring.MetadataToken
        && assembly = Some occupant.DeclaredBy.AssemblyFullName

    /// What a type reached, read from the host's side alone.
    type private Label =
        /// An interface whose count is above its `GetNumVirtuals`: some static virtual counted.
        | StaticTail
        /// An interface counting a reabstraction of a base interface's method, instance or static.
        | ReabstractionCounted
        /// An interface declaring a method which is not virtual, which is not counted.
        | NonVirtualNotCounted
        /// A class or struct implementing an interface which declares a static virtual.
        | ImplementsStaticVirtualInterface
        /// A generic type over a type variable which is not its own definition.
        | OpenConstruction
        /// A type with a slot below its count at which the host's `GetMethodAt` answers null.
        | NullSlot

    let private hostLabels (t : Type) (slots : MethodBase option list) : Label list =
        let declaresStaticVirtual (i : Type) : bool =
            i.GetMethods (
                BindingFlags.DeclaredOnly
                ||| BindingFlags.Static
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
            )
            |> Array.exists _.IsVirtual

        [
            if t.IsInterface && slots.Length > hostNumVirtuals t then
                yield Label.StaticTail
            if
                t.IsInterface
                && slots
                   |> List.exists (fun slot ->
                       match slot with
                       | Some m -> m.IsAbstract && m.Name.Contains '.'
                       | None -> false
                   )
            then
                yield Label.ReabstractionCounted
            if
                t.IsInterface
                && t.GetMethods (
                    BindingFlags.DeclaredOnly
                    ||| BindingFlags.Instance
                    ||| BindingFlags.Static
                    ||| BindingFlags.Public
                    ||| BindingFlags.NonPublic
                   )
                   |> Array.exists (fun m -> not m.IsVirtual)
            then
                yield Label.NonVirtualNotCounted
            if not t.IsInterface && t.GetInterfaces () |> Array.exists declaresStaticVirtual then
                yield Label.ImplementsStaticVirtualInterface
            if t.IsConstructedGenericType && t.ContainsGenericParameters then
                yield Label.OpenConstruction
            if slots |> List.exists Option.isNone then
                yield Label.NullSlot
        ]

    /// Compare PawPrint's count for `t`, and `methodAt` around it, with the host's. Returns the state
    /// to carry on with and every disagreement found.
    let private compareType
        (cover : Label -> unit)
        (images : HostTypeTarget.Images)
        (state : IlMachineState)
        (t : Type)
        : IlMachineState * string list
        =
        let slots = hostSlots t

        for label in hostLabels t slots do
            cover label

        let expected = slots |> List.choose id
        let state, target = HostTypeTarget.target loggerFactory bct images state t
        let state, actual = count bct state target

        if actual <> expected.Length then
            state,
            [
                $"%O{t}: PawPrint counts %d{actual}, the host %d{slots.Length} with %d{slots.Length - expected.Length} null slots"
            ]
        else

        // For an interface, `methodAt` names the host's non-null methods in order: the pairing
        // `GetInterfaceMap` relies on.
        let state, slotDisagreements =
            if t.IsInterface then
                ((state, []), List.indexed expected)
                ||> List.fold (fun (state, acc) (slot, expectedMethod) ->
                    match methodAt bct state target slot with
                    | state, VirtualSlotLayout.MethodAtSlot.Method occupant when
                        sameMethod images occupant expectedMethod
                        ->
                        state, acc
                    | state, answer ->
                        state, $"%O{t} slot %d{slot}: PawPrint %A{answer}, the host %O{expectedMethod}" :: acc
                )
            else
                state, []

        // For every type, the boundary: the count's first half is the placement vtable's length, and
        // `methodAt` takes its boundary from the content vtable, so check they meet.
        let state, boundary =
            match methodAt bct state target actual with
            | state, VirtualSlotLayout.MethodAtSlot.OutOfRange -> state, []
            | state, answer -> state, [ $"%O{t}: methodAt at the count %d{actual} is %A{answer}" ]

        let state, below =
            if actual > 0 then
                match methodAt bct state target (actual - 1) with
                | state, VirtualSlotLayout.MethodAtSlot.Method _ -> state, []
                | state, answer -> state, [ $"%O{t}: methodAt below the count %d{actual} is %A{answer}" ]
            else
                state, []

        state, List.rev slotDisagreements @ boundary @ below

    // ----- CoreLib -----

    [<Test>]
    let ``CoreLib: every exported type's count is the host's less its null slots`` () : unit =
        let stopwatch = Stopwatch.StartNew ()
        let images = [ hostCoreLib, corelib ]
        let exported = hostCoreLib.GetExportedTypes ()
        let disagreements = ResizeArray<string> ()
        let mutable staticTails = 0
        let mutable nullSlots = 0

        let cover (label : Label) : unit =
            match label with
            | Label.StaticTail -> staticTails <- staticTails + 1
            | Label.NullSlot -> nullSlots <- nullSlots + 1
            | _ -> ()

        let mutable state = coreState

        for t in exported do
            let state', found = compareType cover images state t
            state <- state'
            disagreements.AddRange found

        let interfaces = exported |> Array.filter _.IsInterface |> Array.length

        TestContext.Progress.WriteLine
            $"CoreLib: %d{exported.Length} types, %d{interfaces} interfaces, %d{staticTails} with a static tail, %d{nullSlots} with a null slot, %d{stopwatch.ElapsedMilliseconds} ms"

        List.ofSeq disagreements |> shouldEqual []
        // The comparison of interfaces' tails says nothing if no interface has one: 33 of CoreLib's
        // exported interfaces have one, measured on a .NET 10 host.
        Assert.That (staticTails, Is.GreaterThanOrEqualTo 30)

    // ----- Generated corpora -----

    let private checkCorpus (cover : Label -> unit) (corpus : Corpus) : unit =
        let source = render corpus

        let image =
            Roslyn.compileAssembly "NumVirtualsGenerated" OutputKind.DynamicallyLinkedLibrary [] [ source ]

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context =
            Runtime.Loader.AssemblyLoadContext ("numVirtualsAndStaticVirtuals", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let images = [ hostCoreLib, corelib ; host, dumped ]
            let mutable state = coreState.WithLoadedAssembly dumped
            let disagreements = ResizeArray<string> ()

            for t in pool corpus host do
                let state', found = compareType cover images state t
                state <- state'
                disagreements.AddRange found

            if disagreements.Count > 0 then
                failwith (
                    $"%d{disagreements.Count} disagreements with the host:\n"
                    + String.concat "\n" disagreements
                    + $"\nin the corpus:\n%s{source}"
                )
        finally
            context.Unload ()

    [<Test>]
    let ``Generated corpora: every type's count is the host's less its null slots`` () : unit =
        let stopwatch = Stopwatch.StartNew ()

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 100)
                (Arb.fromGen genCorpus)
                (fun cover corpus -> checkCorpus cover corpus)

        TestContext.Progress.WriteLine $"generated corpora: %d{stopwatch.ElapsedMilliseconds} ms"

        // About half of what the fixed sample reaches.
        for label, floor in
            [
                Label.StaticTail, 164
                Label.ReabstractionCounted, 12
                Label.NonVirtualNotCounted, 96
                Label.ImplementsStaticVirtualInterface, 75
                Label.OpenConstruction, 64
            ] do
            Assert.That (coverage.Count label, Is.GreaterThanOrEqualTo floor, $"%A{label}")

    // ----- Each supported runtime's CoreLib, without a host of that runtime -----

    let runtimes : TestCaseData list = FrameworkUnderTest.everyRuntime

    /// `runtime`'s CoreLib, read by PawPrint, with a state over it.
    let private runtimeCoreLib
        (runtime : EmulatedRuntime)
        : string * DumpedAssembly * BaseClassTypes<DumpedAssembly> * IlMachineState
        =
        let path = FrameworkUnderTest.requireCoreLibPathOf runtime
        let corelib = AssemblyApi.readFile loggerFactory path
        let bct = BaseClassTypes.ofCorelib corelib
        path, corelib, bct, stateOver path corelib bct

    /// The target a guest's `typeof` of a definition gives: the definition itself if it is generic,
    /// and otherwise the closed type.
    let private definitionTarget
        (bct : BaseClassTypes<DumpedAssembly>)
        (state : IlMachineState)
        (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>)
        : IlMachineState * RuntimeTypeHandleTarget
        =
        if not typeInfo.Generics.IsEmpty then
            state, RuntimeTypeHandleTarget.OpenGenericTypeDefinition typeInfo.Identity
        else
            let defn =
                TypeDefn.FromDefinition (
                    typeInfo.Identity,
                    LoadedTypeInfo.signatureTypeKind bct state.TypeSystem._LoadedAssemblies typeInfo
                )

            let state, handle =
                IlMachineState.concretizeType
                    loggerFactory
                    bct
                    state
                    bct.Corelib.DefinitionFullName
                    ImmutableArray.Empty
                    ImmutableArray.Empty
                    defn

            state, RuntimeTypeHandleTarget.Closed handle

    /// An interface has no parent, so its vtable is its own instance virtuals and its tail its own
    /// static virtuals: its count is the number of its MethodDef rows marked `Virtual`. The rows are
    /// read with `System.Reflection.Metadata` directly, not through PawPrint's reader. On .NET 11's
    /// CoreLib this is the check that PawPrint counts no async variant slot.
    [<TestCaseSource(nameof runtimes)>]
    let ``Each runtime's CoreLib: an interface's count is its virtual MethodDef rows`` (runtime : EmulatedRuntime) =
        let path, corelib, bct, state = runtimeCoreLib runtime

        use stream = File.OpenRead path
        use pe = new PEReader (stream)
        let metadata = pe.GetMetadataReader ()
        let disagreements = ResizeArray<string> ()
        let mutable interfaces = 0
        let mutable withStaticVirtual = 0
        let mutable state = state

        for handle in metadata.TypeDefinitions do
            let definition = metadata.GetTypeDefinition handle

            if definition.Attributes.HasFlag TypeAttributes.Interface then
                interfaces <- interfaces + 1

                let rows =
                    definition.GetMethods ()
                    |> Seq.map (fun m -> (metadata.GetMethodDefinition m).Attributes)
                    |> Seq.filter (fun a -> a.HasFlag MethodAttributes.Virtual)
                    |> List.ofSeq

                if rows |> List.exists (fun a -> a.HasFlag MethodAttributes.Static) then
                    withStaticVirtual <- withStaticVirtual + 1

                let typeInfo = corelib.TypeDefs.[handle]
                let state', target = definitionTarget bct state typeInfo
                let state', actual = count bct state' target
                state <- state'

                if actual <> rows.Length then
                    disagreements.Add
                        $"%s{TypeInfo.fullName (fun h -> corelib.TypeDefs.[h]) typeInfo}: PawPrint %d{actual}, %d{rows.Length} virtual rows"

        TestContext.Progress.WriteLine
            $"%O{runtime}: %d{interfaces} interfaces, %d{withStaticVirtual} with a static virtual"

        List.ofSeq disagreements |> shouldEqual []
        // 50 on .NET 10 and 57 on .NET 11 measured, counting the interfaces CoreLib does not export.
        Assert.That (withStaticVirtual, Is.GreaterThanOrEqualTo 40)

    /// Real .NET 11 counts 2 and 3, an async variant slot after `DisposeAsync` and after
    /// `MoveNextAsync`, at which `GetMethodAt` answers null; PawPrint lays out no such slot
    /// (`docs/divergences.md`, "A runtime-async method's async variant has no virtual slot of its own").
    [<TestCaseSource(nameof runtimes)>]
    let ``Each runtime's CoreLib: the async interfaces count no variant slot`` (runtime : EmulatedRuntime) =
        let _, corelib, bct, state = runtimeCoreLib runtime

        for ns, name, expected in
            [
                "System", "IAsyncDisposable", 1
                "System.Collections.Generic", "IAsyncEnumerator`1", 2
            ] do
            let typeInfo =
                corelib.TryGetTopLevelTypeDef ns name
                |> Option.defaultWith (fun () -> failwith $"%O{runtime}'s CoreLib has no %s{ns}.%s{name}")

            let state, target = definitionTarget bct state typeInfo
            let state, actual = count bct state target
            actual |> shouldEqual expected

            match methodAt bct state target actual with
            | _, VirtualSlotLayout.MethodAtSlot.OutOfRange -> ()
            | _, answer -> failwith $"%s{name}: methodAt at the count %d{actual} is %A{answer}"

    // ----- Examples -----

    let private fixedSource : string =
        """
public interface IStat { static abstract int S(); void I(); }
public interface IStatVirt { static virtual int SV() => 1; static abstract int SA(); int P { get; } static int Plain() => 2; }
public interface ISealedDim { sealed void S() { } private void P() { } void A(); }
public interface IBase { void M(); static abstract int S(); }
public interface IReabs : IBase { abstract void IBase.M(); }
public interface IStaticReabs : IBase { static abstract int IBase.S(); }
public interface IStatOrder { void I1(); static abstract int S1(); void I2(); static abstract int S2(); }
public interface IG<T> { T Get(); static abstract T Make(); }
public interface IStatInit { static int F = 3; static abstract int S(); }
public struct SStat : IStat { public static int S() => 1; public void I() { } }
public class HasT<T> { }
"""

    let private fixedImage : byte[] =
        Roslyn.compileAssembly "NumVirtualsFixed" OutputKind.DynamicallyLinkedLibrary [] [ fixedSource ]

    let private fixedHost : Reflection.Assembly = Reflection.Assembly.Load fixedImage

    let private fixedDumped : DumpedAssembly =
        use stream = new MemoryStream (fixedImage)
        AssemblyApi.read loggerFactory None stream

    let private fixedImages : HostTypeTarget.Images =
        [ hostCoreLib, corelib ; fixedHost, fixedDumped ]

    let private fixedState : IlMachineState = coreState.WithLoadedAssembly fixedDumped

    let private fixedType (name : string) : Type =
        match fixedHost.GetType name with
        | null -> failwith $"fixed corpus type %s{name} not found"
        | t -> t

    let private fixedCount (t : Type) : int =
        let state, target = HostTypeTarget.target loggerFactory bct fixedImages fixedState t
        snd (count bct state target)

    /// Shapes a generated corpus can miss.
    [<Test>]
    let ``Each fixed shape has its measured count`` () : unit =
        let hasT = (fixedType "HasT`1").GetGenericArguments().[0]

        let cases : (Type * int) list =
            [
                fixedType "IStat", 2
                fixedType "IStatVirt", 3
                fixedType "IStaticReabs", 1
                fixedType "IReabs", 1
                fixedType "ISealedDim", 1
                fixedType "IStatOrder", 4
                (fixedType "IG`1").MakeGenericType hasT, 2
                fixedType "SStat", 5
                // The field initialiser gives it a class constructor, which is static and not virtual.
                fixedType "IStatInit", 1
                // An array's count is `System.Array`'s, not its element's.
                (fixedType "IStat").MakeArrayType (), 24
            ]

        for t, expected in cases do
            hostCount t |> shouldEqual expected

            let actual = fixedCount t

            if actual <> expected then
                failwith $"%O{t}: expected %d{expected}, PawPrint %d{actual}"

    /// A byref, pointer, function pointer or type variable is a TypeDesc. CoreCLR counts the method
    /// table `TypeHandle::GetMethodTable` substitutes for it (`UIntPtr`'s for a pointer and a
    /// function pointer, none for a byref and a type variable), which PawPrint does not model
    /// (#2299), so each is refused. Whoever answers #2299 sees this test change.
    [<Test>]
    let ``A TypeDesc is refused, naming #2299`` () : unit =
        let header : ComparableSignatureHeader =
            ComparableSignatureHeader.Make (
                SignatureHeader (SignatureKind.Method, SignatureCallingConvention.Default, SignatureAttributes.None)
            )

        let functionPointer : RuntimeTypeHandleTarget =
            RuntimeTypeHandleTarget.functionPointer
                {
                    Header = header
                    ParameterTypes = []
                    GenericParameterCount = 0
                    RequiredParameterCount = 0
                    ReturnType = MethodReturnType.Void
                }

        let hostTarget (t : Type) : RuntimeTypeHandleTarget =
            snd (HostTypeTarget.target loggerFactory bct fixedImages fixedState t)

        let cases : (string * RuntimeTypeHandleTarget) list =
            [
                // The closed pointer fails first if the TypeDesc test is missing or late: the
                // vtable of a closed pointer is empty, so a count would answer 0.
                "int*", hostTarget (typeof<int>.MakePointerType ())
                "delegate*<void>", functionPointer
                "int&", hostTarget (typeof<int>.MakeByRefType ())
                "List<>'s T", hostTarget listT
                "T*", hostTarget (listT.MakePointerType ())
                "T&", hostTarget (listT.MakeByRefType ())
            ]

        match cases with
        | (_, RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Pointer _)) :: _ -> ()
        | (name, other) :: _ -> failwith $"%s{name} was built as %O{other}, not as a closed pointer"
        | [] -> failwith "no cases"

        for name, target in cases do
            let e = Assert.Throws<Exception> (fun () -> count bct fixedState target |> ignore)

            Assert.That (e.Message, Does.StartWith "TODO:", name)
            Assert.That (e.Message, Does.Contain "#2299", name)
