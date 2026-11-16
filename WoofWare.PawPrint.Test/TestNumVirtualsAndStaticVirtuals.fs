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

    /// What a generated member returns: `int`, or the declaring interface's parameter.
    type private Returns =
        | RInt
        | RParam

    type private MemberKind =
        | InstanceAbstract
        | DefaultBody
        | SealedBody
        | PrivateInstance
        | StaticAbstract
        | StaticVirtual
        | PlainStatic
        | InstanceProperty
        | StaticProperty
        | InstanceEvent
        | StaticEvent
        | InstanceGeneric
        | StaticGeneric

    type private Member =
        {
            Kind : MemberKind
            Returns : Returns
        }

    /// How a derived interface overrides a method of an interface it inherits.
    type private OverrideKind =
        /// An explicit default body, `R IBase.M() => default;`.
        | ExplicitBody
        /// An instance reabstraction, `abstract R IBase.M();`.
        | Reabstract
        /// A static reabstraction, `static abstract R IBase.M();`.
        | StaticReabstract

    type private Override =
        {
            Interface : int
            Member : int
            Kind : OverrideKind
        }

    /// A generic argument in a generated declaration.
    type private Argument =
        | AInt
        | AString
        /// The declaring entity's own parameter.
        | AParam

    type private InterfaceUse =
        {
            Interface : int
            /// `None` exactly when the interface is not generic.
            Argument : Argument option
        }

    type private InterfaceDecl =
        {
            Generic : bool
            Members : Member list
            Bases : InterfaceUse list
            Overrides : Override list
        }

    type private TypeDecl =
        {
            IsStruct : bool
            Generic : bool
            Implements : InterfaceUse list
        }

    type private Corpus =
        {
            Interfaces : InterfaceDecl list
            Types : TypeDecl list
        }

    let private substitute (outer : Argument option) (argument : Argument option) : Argument option =
        match argument with
        | Some Argument.AParam -> outer
        | other -> other

    /// `used` and every interface it inherits, with arguments in the user's context.
    let rec private closure (interfaces : InterfaceDecl list) (used : InterfaceUse) : InterfaceUse list =
        used
        :: (interfaces.[used.Interface].Bases
            |> List.collect (fun b ->
                closure
                    interfaces
                    { b with
                        Argument = substitute used.Argument b.Argument
                    }
            ))

    /// Keep each declared interface whose closure names no interface with a different argument from
    /// one already kept: two instantiations of one interface could unify, which C# refuses.
    let private dropConflicts (interfaces : InterfaceDecl list) (declared : InterfaceUse list) : InterfaceUse list =
        let conflicts (a : InterfaceUse) (b : InterfaceUse) : bool =
            a.Interface = b.Interface && a.Argument <> b.Argument

        let kept, _ =
            (([], []), declared)
            ||> List.fold (fun (kept, seen) used ->
                let mine = closure interfaces used

                if List.contains used kept then
                    kept, seen
                elif mine |> List.exists (fun m -> seen |> List.exists (conflicts m)) then
                    kept, seen
                elif mine |> List.exists (fun m -> mine |> List.exists (conflicts m)) then
                    kept, seen
                else
                    used :: kept, mine @ seen
            )

        List.rev kept

    let private canOverride (kind : OverrideKind) (target : MemberKind) : bool =
        match kind, target with
        | OverrideKind.ExplicitBody, (MemberKind.InstanceAbstract | MemberKind.DefaultBody)
        | OverrideKind.Reabstract, (MemberKind.InstanceAbstract | MemberKind.DefaultBody)
        | OverrideKind.StaticReabstract, (MemberKind.StaticAbstract | MemberKind.StaticVirtual) -> true
        | _ -> false

    /// Make a generated corpus valid C#: no argument names a parameter its declarer does not have, no
    /// two instantiations of one interface in a closure, a member returns the parameter only of a
    /// generic interface, and each override names a method of an inherited interface which it can
    /// override and which nothing else in the corpus overrides.
    let private repair (corpus : Corpus) : Corpus =
        let fixUse (generic : bool) (interfaces : InterfaceDecl list) (used : InterfaceUse) : InterfaceUse =
            let argument =
                if not interfaces.[used.Interface].Generic then
                    None
                else
                    match used.Argument with
                    | Some Argument.AParam when not generic -> Some Argument.AInt
                    | None -> Some Argument.AInt
                    | other -> other

            { used with
                Argument = argument
            }

        let interfaces, _ =
            ((([] : InterfaceDecl list), (Set.empty : Set<int * int>)), corpus.Interfaces)
            ||> List.fold (fun (earlier, overridden) decl ->
                let members =
                    decl.Members
                    |> List.map (fun m ->
                        if decl.Generic then
                            m
                        else
                            { m with
                                Returns = Returns.RInt
                            }
                    )

                let bases =
                    decl.Bases
                    |> List.filter (fun b -> b.Interface < earlier.Length)
                    |> List.map (fixUse decl.Generic earlier)
                    |> dropConflicts earlier

                let inherited =
                    bases |> List.collect (closure earlier) |> List.map _.Interface |> Set.ofList

                let overrides, overridden =
                    ((([] : Override list), overridden), decl.Overrides)
                    ||> List.fold (fun (kept, overridden) o ->
                        if
                            Set.contains o.Interface inherited
                            && o.Member < earlier.[o.Interface].Members.Length
                            && canOverride o.Kind earlier.[o.Interface].Members.[o.Member].Kind
                            && not (Set.contains (o.Interface, o.Member) overridden)
                        then
                            o :: kept, Set.add (o.Interface, o.Member) overridden
                        else
                            kept, overridden
                    )

                earlier
                @ [
                    {
                        Generic = decl.Generic
                        Members = members
                        Bases = bases
                        Overrides = List.rev overrides
                    }
                ],
                overridden
            )

        let types =
            corpus.Types
            |> List.map (fun decl ->
                { decl with
                    Implements =
                        decl.Implements
                        |> List.map (fixUse decl.Generic interfaces)
                        |> dropConflicts interfaces
                }
            )

        {
            Interfaces = interfaces
            Types = types
        }

    let private interfaceName (index : int) : string = $"I%d{index}"
    let private typeName (index : int) : string = $"D%d{index}"

    let private memberName (kind : MemberKind) (i : int) (k : int) : string =
        match kind with
        | MemberKind.InstanceProperty
        | MemberKind.StaticProperty -> $"P%d{i}_%d{k}"
        | MemberKind.InstanceEvent
        | MemberKind.StaticEvent -> $"E%d{i}_%d{k}"
        | MemberKind.InstanceGeneric
        | MemberKind.StaticGeneric -> $"G%d{i}_%d{k}"
        | _ -> $"M%d{i}_%d{k}"

    let private render (corpus : Corpus) : string =
        let builder = Text.StringBuilder ()

        let argumentText (parameter : string) (argument : Argument) : string =
            match argument with
            | Argument.AInt -> "int"
            | Argument.AString -> "string"
            | Argument.AParam -> parameter

        let useText (parameter : string) (used : InterfaceUse) : string =
            match used.Argument with
            | None -> interfaceName used.Interface
            | Some argument -> $"%s{interfaceName used.Interface}<%s{argumentText parameter argument}>"

        // What a member of `used`'s interface returns, in the context whose parameter is `parameter`.
        let returnText (parameter : string) (used : InterfaceUse) (returns : Returns) : string =
            match returns, used.Argument with
            | Returns.RInt, _ -> "int"
            | Returns.RParam, Some argument -> argumentText parameter argument
            | Returns.RParam, None -> failwith "a non-generic interface's member returns a parameter"

        corpus.Interfaces
        |> List.iteri (fun i decl ->
            let self =
                {
                    Interface = i
                    Argument = if decl.Generic then Some Argument.AParam else None
                }

            let bases =
                match decl.Bases with
                | [] -> ""
                | bs -> " : " + (bs |> List.map (useText "P0") |> String.concat ", ")

            let selfText = useText "P0" self
            builder.Append ($"public interface %s{selfText}%s{bases}\n{{\n") |> ignore

            decl.Members
            |> List.iteri (fun k m ->
                let r = returnText "P0" self m.Returns
                let name = memberName m.Kind i k

                let text =
                    match m.Kind with
                    | MemberKind.InstanceAbstract -> $"%s{r} %s{name}();"
                    | MemberKind.DefaultBody -> $"%s{r} %s{name}() => default;"
                    | MemberKind.SealedBody -> $"sealed %s{r} %s{name}() => default;"
                    | MemberKind.PrivateInstance -> $"private %s{r} %s{name}() => default;"
                    | MemberKind.StaticAbstract -> $"static abstract %s{r} %s{name}();"
                    | MemberKind.StaticVirtual -> $"static virtual %s{r} %s{name}() => default;"
                    | MemberKind.PlainStatic -> $"static %s{r} %s{name}() => default;"
                    | MemberKind.InstanceProperty -> $"%s{r} %s{name} {{ get; }}"
                    | MemberKind.StaticProperty -> $"static abstract %s{r} %s{name} {{ get; }}"
                    | MemberKind.InstanceEvent -> $"event System.Action %s{name};"
                    | MemberKind.StaticEvent -> $"static abstract event System.Action %s{name};"
                    | MemberKind.InstanceGeneric -> $"void %s{name}<U>();"
                    | MemberKind.StaticGeneric -> $"static abstract void %s{name}<U>();"

                builder.Append ($"    %s{text}\n") |> ignore
            )

            let inherited = decl.Bases |> List.collect (closure corpus.Interfaces)

            for o in decl.Overrides do
                let used = inherited |> List.find (fun u -> u.Interface = o.Interface)

                let target = corpus.Interfaces.[o.Interface].Members.[o.Member]
                let r = returnText "P0" used target.Returns
                let usedText = useText "P0" used
                let name = $"%s{usedText}.%s{memberName target.Kind o.Interface o.Member}"

                let text =
                    match o.Kind with
                    | OverrideKind.ExplicitBody -> $"%s{r} %s{name}() => default;"
                    | OverrideKind.Reabstract -> $"abstract %s{r} %s{name}();"
                    | OverrideKind.StaticReabstract -> $"static abstract %s{r} %s{name}();"

                builder.Append ($"    %s{text}\n") |> ignore

            builder.Append "}\n" |> ignore
        )

        corpus.Types
        |> List.iteri (fun index decl ->
            let self =
                if decl.Generic then
                    $"%s{typeName index}<T>"
                else
                    typeName index

            let parents =
                match decl.Implements with
                | [] -> ""
                | uses -> " : " + (uses |> List.map (useText "T") |> String.concat ", ")

            let kind = if decl.IsStruct then "struct" else "class"
            builder.Append ($"public %s{kind} %s{self}%s{parents}\n{{\n") |> ignore

            // Every virtual member of every interface it inherits, explicitly, so that no member's most
            // specific implementation is ambiguous.
            for used in decl.Implements |> List.collect (closure corpus.Interfaces) |> List.distinct do
                let owner = useText "T" used

                corpus.Interfaces.[used.Interface].Members
                |> List.iteri (fun k m ->
                    let r = returnText "T" used m.Returns
                    let name = $"%s{owner}.%s{memberName m.Kind used.Interface k}"

                    let text =
                        match m.Kind with
                        | MemberKind.InstanceAbstract
                        | MemberKind.DefaultBody -> Some $"%s{r} %s{name}() => default;"
                        | MemberKind.StaticAbstract
                        | MemberKind.StaticVirtual -> Some $"static %s{r} %s{name}() => default;"
                        | MemberKind.InstanceProperty -> Some $"%s{r} %s{name} => default;"
                        | MemberKind.StaticProperty -> Some $"static %s{r} %s{name} => default;"
                        | MemberKind.InstanceEvent ->
                            Some $"event System.Action %s{name} {{ add {{ }} remove {{ }} }}"
                        | MemberKind.StaticEvent ->
                            Some $"static event System.Action %s{name} {{ add {{ }} remove {{ }} }}"
                        | MemberKind.InstanceGeneric -> Some $"void %s{name}<U>() {{ }}"
                        | MemberKind.StaticGeneric -> Some $"static void %s{name}<U>() {{ }}"
                        | MemberKind.SealedBody
                        | MemberKind.PrivateInstance
                        | MemberKind.PlainStatic -> None

                    match text with
                    | Some text -> builder.Append ($"    %s{text}\n") |> ignore
                    | None -> ()
                )

            builder.Append "}\n" |> ignore
        )

        builder.ToString ()

    let private genMember : Gen<Member> =
        Gen.map2
            (fun kind returns ->
                {
                    Kind = kind
                    Returns = returns
                }
            )
            (Gen.elements
                [
                    MemberKind.InstanceAbstract
                    MemberKind.DefaultBody
                    MemberKind.SealedBody
                    MemberKind.PrivateInstance
                    MemberKind.StaticAbstract
                    MemberKind.StaticVirtual
                    MemberKind.PlainStatic
                    MemberKind.InstanceProperty
                    MemberKind.StaticProperty
                    MemberKind.InstanceEvent
                    MemberKind.StaticEvent
                    MemberKind.InstanceGeneric
                    MemberKind.StaticGeneric
                ])
            (Gen.elements [ Returns.RInt ; Returns.RParam ])

    let private genUse (count : int) : Gen<InterfaceUse> =
        Gen.map2
            (fun index argument ->
                {
                    Interface = index
                    Argument = Some argument
                }
            )
            (Gen.choose (0, count - 1))
            (Gen.elements [ Argument.AInt ; Argument.AString ; Argument.AParam ])

    let private genInterfaceDecl (index : int) : Gen<InterfaceDecl> =
        gen {
            let! generic = Gen.elements [ false ; true ]
            let! memberCount = Gen.choose (0, 4)
            let! members = Gen.listOfLength memberCount genMember

            let! bases =
                if index = 0 then
                    Gen.constant []
                else
                    Gen.choose (0, 2) |> Gen.bind (fun n -> Gen.listOfLength n (genUse index))

            let! overrides =
                if index = 0 then
                    Gen.constant []
                else
                    Gen.choose (0, 3)
                    |> Gen.bind (fun n ->
                        Gen.listOfLength
                            n
                            (Gen.map3
                                (fun i k kind ->
                                    {
                                        Interface = i
                                        Member = k
                                        Kind = kind
                                    }
                                )
                                (Gen.choose (0, index - 1))
                                (Gen.choose (0, 3))
                                (Gen.elements
                                    [
                                        OverrideKind.ExplicitBody
                                        OverrideKind.Reabstract
                                        OverrideKind.StaticReabstract
                                    ]))
                    )

            return
                {
                    Generic = generic
                    Members = members
                    Bases = bases
                    Overrides = overrides
                }
        }

    let private genCorpus : Gen<Corpus> =
        gen {
            let! interfaceCount = Gen.choose (1, 4)

            let! interfaces = [ 0 .. interfaceCount - 1 ] |> List.map genInterfaceDecl |> Gen.sequenceToList

            let! typeCount = Gen.choose (1, 3)

            let! types =
                Gen.listOfLength
                    typeCount
                    (gen {
                        let! isStruct = Gen.elements [ false ; true ]
                        let! generic = Gen.elements [ false ; true ]
                        let! implementCount = Gen.choose (0, 3)
                        let! implements = Gen.listOfLength implementCount (genUse interfaceCount)

                        return
                            {
                                IsStruct = isStruct
                                Generic = generic
                                Implements = implements
                            }
                    })

            return
                repair
                    {
                        Interfaces = interfaces
                        Types = types
                    }
        }

    let private listT : Type =
        typedefof<System.Collections.Generic.List<obj>>.GetGenericArguments().[0]

    /// Each interface's definition, its instantiations over `int` and `string`, and an open
    /// construction over a class's own parameter; each class and struct, and an instantiation of each
    /// generic one; and an array of each closed type among them.
    let private pool (corpus : Corpus) (host : Reflection.Assembly) : Type list =
        let hostType (name : string) : Type =
            match host.GetType name with
            | null -> failwith $"generated type %s{name} not found"
            | t -> t

        let variable =
            corpus.Types
            |> List.indexed
            |> List.tryPick (fun (index, decl) ->
                if decl.Generic then
                    Some ((hostType $"%s{typeName index}`1").GetGenericArguments().[0])
                else
                    None
            )
            |> Option.defaultValue listT

        let interfaces =
            corpus.Interfaces
            |> List.mapi (fun index decl ->
                if decl.Generic then
                    let definition = hostType $"%s{interfaceName index}`1"

                    [
                        definition
                        definition.MakeGenericType typeof<int>
                        definition.MakeGenericType typeof<string>
                        definition.MakeGenericType variable
                    ]
                else
                    [ hostType (interfaceName index) ]
            )
            |> List.concat

        let types =
            corpus.Types
            |> List.mapi (fun index decl ->
                if decl.Generic then
                    let definition = hostType $"%s{typeName index}`1"
                    [ definition ; definition.MakeGenericType typeof<int> ]
                else
                    [ hostType (typeName index) ]
            )
            |> List.concat

        let all = interfaces @ types

        all
        @ (all
           |> List.filter (fun t -> not t.ContainsGenericParameters)
           |> List.map _.MakeArrayType())

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
