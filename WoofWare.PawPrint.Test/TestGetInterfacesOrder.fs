namespace WoofWare.PawPrint.Test

open System
open System.Diagnostics
open System.IO
open System.Runtime.Loader
open System.Text
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// `RuntimeTypeInterfaces.ofReceiver` is what the `RuntimeTypeHandle_GetInterfaces` QCall answers:
/// the receiver's interface map, entry for entry, in CoreCLR's order (the base class's entries
/// first, then each declared interface followed by its own parents, depth-first), with an entry
/// kept apart from another of the same closed type when the two are different instantiations of the
/// generic definition's interfaces.
///
/// The host's `Type.GetInterfaces()` over the same image is the oracle, except for an array, where
/// the QCall answers `System.Array`'s map and managed code appends an SZ array's generic
/// interfaces. Four corpora go through it: CoreLib's exported closed types, interfaces included;
/// CoreLib's generic definitions and instantiations of them, closed and open; generated
/// hierarchies, with their definitions and open constructions; and shapes C# refuses to compile,
/// built from a library in two versions, which are the only ones where a declared interface's
/// parents are missing from the type's own rows.
///
/// An open receiver's answer is its definition's map converted to targets over the receiver's
/// arguments (`RuntimeTypeInterfaces.ofInstantiation`), and a closed receiver's is
/// `interfaceMapOf`'s; every closed receiver of every corpus also checks that the first, given the
/// receiver's closed arguments, gives the second's handles.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestGetInterfacesOrder =

    // The factory is intentionally undisposed: the DumpedAssembly loggers close over its sinks and
    // outlive this scope.
    let private loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory =
        let _, loggerFactory = LoggerFactory.makeTest ()
        loggerFactory

    let private corelib : DumpedAssembly =
        Assembly.readFile loggerFactory HostImage.coreLibPath

    let private hostCoreLib : Reflection.Assembly = typeof<obj>.Assembly

    let private bct : BaseClassTypes<DumpedAssembly> = BaseClassTypes.ofCorelib corelib

    /// The host's CoreLib alone, concretised once. Each corpus is added to it.
    let private coreState : IlMachineState =
        let initial = IlMachineState.initial loggerFactory HostImage.runtimeDirs corelib

        { initial with
            TypeSystem =
                { initial.TypeSystem with
                    ConcreteTypes =
                        Corelib.concretizeAll initial.TypeSystem._LoadedAssemblies bct AllConcreteTypes.Empty
                }
        }

    /// What the QCall answers on the host for `receiver`: its `GetInterfaces()`, or for an array
    /// `System.Array`'s, since `RuntimeTypeCache.PopulateInterfaces` appends an SZ array's generic
    /// interfaces in managed code.
    let private hostAnswer (receiver : Type) : Type list =
        if receiver.IsArray then
            typeof<Array>.GetInterfaces () |> List.ofArray
        else
            receiver.GetInterfaces () |> List.ofArray

    let private firstLine (message : string) : string =
        match message.IndexOf '\n' with
        | -1 -> message
        | i -> message.Substring (0, i)

    /// Compare PawPrint's answer for `receiver` with the host's, giving why they differ, if they do.
    let private compareAnswer
        (images : HostTypeTarget.Images)
        (state : IlMachineState)
        (receiver : Type)
        : IlMachineState * string option
        =
        let host = hostAnswer receiver

        let state, expected =
            ((state, []), host)
            ||> List.fold (fun (state, acc) iface ->
                let state, target = HostTypeTarget.target loggerFactory bct images state iface
                state, target :: acc
            )

        let expected = List.rev expected

        let state, receiverTarget =
            HostTypeTarget.target loggerFactory bct images state receiver

        let before = state

        let state, actual =
            try
                let state, actual =
                    RuntimeTypeInterfaces.ofReceiver loggerFactory bct "test" state receiverTarget

                state, Ok actual
            with e ->
                before, Error (firstLine e.Message)

        let render (types : Type list) : string =
            types |> List.map (fun t -> t.ToString ()) |> String.concat ", "

        match actual with
        | Error message -> state, Some $"%O{receiver}: threw %s{message}\n    host     [%s{render host}]"
        | Ok actual when actual = expected -> state, None
        | Ok actual ->
            // Each of PawPrint's entries by the host type it matches, so that the two lines read alike.
            let named =
                actual
                |> List.map (fun target ->
                    match List.tryFindIndex ((=) target) expected with
                    | Some i -> host.[i].ToString ()
                    | None -> $"%O{target}"
                )
                |> String.concat ", "

            state, Some $"%O{receiver}:\n    PawPrint [%s{named}]\n    host     [%s{render host}]"

    /// For a closed receiver with a TypeDef row, its definition's map converted to targets with the
    /// receiver's closed arguments (`ofInstantiation`) against `interfaceMapOf`'s handles, giving why
    /// they differ, if they do; nothing for any other receiver.
    let private crossCheck
        (state : IlMachineState)
        (receiver : Type)
        (receiverTarget : RuntimeTypeHandleTarget)
        : IlMachineState * string option
        =
        match receiverTarget with
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Concrete _ as handle) ->
            let before = state

            try
                let ty =
                    AllConcreteTypes.lookup handle state.TypeSystem.ConcreteTypes
                    |> Option.defaultWith (fun () -> failwith $"%O{handle} is not registered")

                let state, closed =
                    InterfaceDispatch.interfaceMapOf loggerFactory bct "test" state handle

                let expected =
                    closed |> List.map (fun entry -> RuntimeTypeHandleTarget.Closed entry.Interface)

                let arguments =
                    ty.Generics
                    |> Seq.map RuntimeTypeHandleTarget.Closed
                    |> Collections.Immutable.ImmutableArray.CreateRange

                match RuntimeTypeInterfaces.ofInstantiation loggerFactory bct "test" state ty.Identity arguments with
                | state, Ok actual when actual = expected -> state, None
                | state, Ok actual ->
                    state, Some $"%O{receiver}: by targets %A{actual}\n    interfaceMapOf %A{expected}"
                | state, Error failure ->
                    state, Some $"%O{receiver}: by targets refused %s{TypeBindingFailure.describe failure}"
            with e ->
                before, Some $"%O{receiver}: the cross-check threw %s{firstLine e.Message}"
        | _ -> state, None

    /// Compare PawPrint's answer for `receiver` with the host's, giving why they differ, if they do;
    /// for a closed receiver, `crossCheck` too.
    let private compareReceiver
        (images : HostTypeTarget.Images)
        (state : IlMachineState)
        (receiver : Type)
        : IlMachineState * string option
        =
        let state, answer = compareAnswer images state receiver

        match answer with
        | Some _ -> state, answer
        | None ->
            let state, receiverTarget =
                HostTypeTarget.target loggerFactory bct images state receiver

            crossCheck state receiver receiverTarget

    let private report (label : string) (failures : string list) : unit =
        if not failures.IsEmpty then
            let shown = failures |> List.truncate 40

            failwith (
                $"%s{label}: %i{failures.Length} disagreements with the host CLR (first %i{shown.Length}):\n"
                + String.Join ("\n", shown)
            )

    /// The receiver's base class has interfaces, and the receiver has InterfaceImpl rows of its own:
    /// the shape where the base's entries must come first.
    let private declaresOverInterfacefulBase (images : HostTypeTarget.Images) (receiver : Type) : bool =
        not receiver.IsArray
        && not (isNull receiver.BaseType)
        && receiver.BaseType.GetInterfaces().Length > 0
        && not (HostTypeTarget.typeInfoOf images receiver).ImplementedInterfaces.IsEmpty

    let private hasDuplicate (receiver : Type) : bool =
        let interfaces = receiver.GetInterfaces ()
        (Array.distinct interfaces).Length < interfaces.Length

    // ----- CoreLib -----

    /// Every exported closed CoreLib type with an interface, interfaces included, a spread of
    /// generic instantiations, and arrays.
    [<Test>]
    let ``CoreLib: GetInterfaces agrees with the host`` () : unit =
        let stopwatch = Stopwatch.StartNew ()

        let exported =
            hostCoreLib.GetExportedTypes ()
            |> Array.filter (fun t -> not t.ContainsGenericParameters && t.GetInterfaces().Length > 0)
            |> List.ofArray

        let instantiations : Type list =
            [
                typeof<Collections.Generic.List<int>>
                typeof<Collections.Generic.List<string>>
                typeof<Collections.Generic.Dictionary<string, obj>>
                typeof<Collections.Generic.HashSet<int>>
                typeof<Collections.Generic.Queue<Exception>>
                typeof<Collections.ObjectModel.ReadOnlyCollection<string>>
                typeof<Collections.ObjectModel.Collection<int>>
                typeof<Collections.Generic.EqualityComparer<string>>
                typeof<Collections.Generic.Comparer<int>>
                typeof<Nullable<int>>
                typeof<ArraySegment<byte>>
                typeof<Memory<char>>
                typeof<ValueTuple<int, string>>
                typeof<Tuple<int, string>>
                typeof<Lazy<int>>
                typeof<Collections.Generic.KeyValuePair<int, string>>
                typeof<Action<int>>
                typeof<Func<string, int>>
                typeof<Predicate<obj>>
                typeof<Collections.Generic.IList<int>>
                typeof<Collections.Generic.ICollection<string>>
                typeof<Collections.Generic.IEnumerable<int>>
                typeof<Collections.Generic.IDictionary<string, obj>>
                typeof<Collections.Generic.IReadOnlyDictionary<int, string>>
                typeof<Collections.Generic.IReadOnlyList<Exception>>
                typeof<Collections.Generic.ISet<int>>
                typeof<Collections.Generic.IAsyncEnumerable<int>>
                typeof<obj>.Assembly.GetType("System.Numerics.INumber`1").MakeGenericType typeof<int>
                typeof<IEquatable<string>>
                typeof<int[]>
                typeof<string[]>
                typeof<int[,]>
                typeof<DayOfWeek[]>
                typeof<Collections.Generic.List<int>[]>
            ]

        let receivers = exported @ instantiations
        let images = [ hostCoreLib, corelib ]
        let mutable state = coreState
        let mutable failures = []

        for receiver in receivers do
            let after, failure = compareReceiver images state receiver
            state <- after

            match failure with
            | Some failure -> failures <- failure :: failures
            | None -> ()

        let interfaceReceivers =
            receivers
            |> List.filter (fun t -> t.IsInterface && t.GetInterfaces().Length > 0)
            |> List.length

        let baseFirst =
            receivers |> List.filter (declaresOverInterfacefulBase images) |> List.length

        TestContext.Progress.WriteLine
            $"CoreLib: %d{receivers.Length} receivers, %d{interfaceReceivers} interfaces, %d{baseFirst} declaring rows over a base with interfaces, %d{stopwatch.ElapsedMilliseconds} ms"

        report "CoreLib" (List.rev failures)

        // About half of what a .NET 10 host reaches, so that a corpus gone quietly empty is caught.
        Assert.That (receivers.Length, Is.GreaterThanOrEqualTo 250)
        Assert.That (interfaceReceivers, Is.GreaterThanOrEqualTo 9)
        Assert.That (baseFirst, Is.GreaterThanOrEqualTo 3)
        // `MulticastDelegate` re-declares `ISerializable` after `Delegate`'s `ICloneable`, which every
        // delegate inherits.
        Assert.That (
            List.contains typeof<MulticastDelegate> receivers
            && declaresOverInterfacefulBase images typeof<MulticastDelegate>,
            Is.True
        )

    /// While this holds, CoreCLR's rule that a CoreLib value type or interface does not expand its
    /// rows' parents (`ExpandApproxInterface`, methodtablebuilder.cpp) cannot be observed: every entry
    /// of such a type's map is one of its InterfaceImpl rows or one of its base's entries, so
    /// expanding the rows adds nothing.
    [<Test>]
    let ``CoreLib: every value type and interface lists all its interfaces itself`` () : unit =
        let mutable checkedTypes = 0
        let mutable failures = []

        for t in hostCoreLib.GetTypes () do
            if t.IsValueType || t.IsInterface then
                let rows =
                    (HostTypeTarget.typeInfoOf [ hostCoreLib, corelib ] t).ImplementedInterfaces.Length

                let inherited =
                    if isNull t.BaseType then
                        0
                    else
                        t.BaseType.GetInterfaces().Length

                let total = t.GetInterfaces().Length
                checkedTypes <- checkedTypes + 1

                if rows + inherited <> total then
                    failures <-
                        $"%O{t}: %d{rows} rows and %d{inherited} from the base, but %d{total} interfaces"
                        :: failures

        failures |> shouldEqual []
        Assert.That (checkedTypes, Is.GreaterThanOrEqualTo 500)

    /// The receivers of `receivers`, compared with the host one by one, the state threaded through.
    let private compareAll
        (images : HostTypeTarget.Images)
        (state : IlMachineState)
        (receivers : Type list)
        : IlMachineState * string list
        =
        ((state, []), receivers)
        ||> List.fold (fun (state, failures) receiver ->
            match compareReceiver images state receiver with
            | state, Some failure -> state, failure :: failures
            | state, None -> state, failures
        )
        |> fun (state, failures) -> state, List.rev failures

    let private dictionaryVariables : Type[] =
        typedefof<Collections.Generic.Dictionary<obj, obj>>.GetGenericArguments ()

    /// Some interface of `receiver` mentions a variable.
    let private hasOpenInterface (receiver : Type) : bool =
        receiver.GetInterfaces () |> Array.exists (fun t -> t.ContainsGenericParameters)

    /// Every generic type definition CoreLib declares; three arrays over a variable, which answer
    /// `System.Array`'s map as a closed array does; and two open constructions over an interface's own
    /// variables, whose entry over that interface is the interface applied to its own variables, which
    /// is the interface's definition.
    [<Test>]
    let ``CoreLib: every generic definition agrees with the host`` () : unit =
        let stopwatch = Stopwatch.StartNew ()

        let definitions =
            hostCoreLib.GetTypes ()
            |> Array.filter (fun t -> t.IsGenericTypeDefinition)
            |> List.ofArray

        let listT = typedefof<Collections.Generic.List<obj>>.GetGenericArguments().[0]

        let arrays =
            [
                listT.MakeArrayType ()
                typedefof<Collections.Generic.List<obj>>.MakeGenericType(dictionaryVariables.[0]).MakeArrayType ()
                listT.MakeArrayType 2
            ]

        let overInterfaceVariables =
            [
                typedefof<Collections.Generic.List<obj>>
                    .MakeGenericType (typedefof<Collections.Generic.IList<obj>>.GetGenericArguments ())
                typedefof<Collections.Generic.Dictionary<obj, obj>>
                    .MakeGenericType (typedefof<Collections.Generic.IDictionary<obj, obj>>.GetGenericArguments ())
            ]

        let _, failures =
            compareAll [ hostCoreLib, corelib ] coreState (definitions @ arrays @ overInterfaceVariables)

        let withOpenInterface = definitions |> List.filter hasOpenInterface |> List.length

        TestContext.Progress.WriteLine
            $"CoreLib definitions: %d{definitions.Length}, %d{withOpenInterface} with an interface over a variable, %d{stopwatch.ElapsedMilliseconds} ms"

        report "CoreLib definitions" failures
        // A .NET 10 host has 421 definitions, 124 of them with an interface over a variable.
        Assert.That (definitions.Length, Is.GreaterThanOrEqualTo 300)
        Assert.That (withOpenInterface, Is.GreaterThanOrEqualTo 100)

    /// The instantiations of `definition` the substitution law is checked at: all `object`, all
    /// `int`, alternating `string` and `long`, and, for a definition with no constraints, all
    /// `Dictionary<,>`'s `K`, alternating its `V` and `K`, and the definition's own variables reversed.
    /// An instantiation the host refuses (a constraint it violates) is left out.
    let private lawInstantiations (definition : Type) : Type list =
        let variables = definition.GetGenericArguments ()
        let n = variables.Length

        let unconstrained =
            variables
            |> Array.forall (fun v ->
                v.GenericParameterAttributes = Reflection.GenericParameterAttributes.None
                && v.GetGenericParameterConstraints().Length = 0
            )

        let alternate (a : Type) (b : Type) : Type list =
            List.init n (fun i -> if i % 2 = 0 then a else b)

        [
            yield List.replicate n typeof<obj>
            yield List.replicate n typeof<int>
            yield alternate typeof<string> typeof<int64>
            if unconstrained then
                yield List.replicate n dictionaryVariables.[0]
                yield alternate dictionaryVariables.[1] dictionaryVariables.[0]
                yield List.ofArray (Array.rev variables)
        ]
        |> List.choose (fun arguments ->
            try
                Some (definition.MakeGenericType (Array.ofList arguments))
            with :? ArgumentException ->
                None
        )
        |> List.distinct

    /// The substitution law, at PawPrint's level: every instantiation of a CoreLib definition, closed
    /// or open, answers as the host does.
    [<Test>]
    let ``CoreLib: instantiations of every generic definition agree with the host`` () : unit =
        let stopwatch = Stopwatch.StartNew ()

        let receivers =
            hostCoreLib.GetTypes ()
            |> Array.filter (fun t -> t.IsGenericTypeDefinition)
            |> List.ofArray
            |> List.collect lawInstantiations

        let _, failures = compareAll [ hostCoreLib, corelib ] coreState receivers

        let openReceivers =
            receivers
            |> List.filter (fun t -> t.ContainsGenericParameters && not t.IsGenericTypeDefinition)

        let openWithOpenInterface =
            openReceivers |> List.filter hasOpenInterface |> List.length

        TestContext.Progress.WriteLine
            $"CoreLib instantiations: %d{receivers.Length}, %d{openReceivers.Length} open, %d{openWithOpenInterface} of them with an interface over a variable, %d{stopwatch.ElapsedMilliseconds} ms"

        report "CoreLib instantiations" failures
        // About half of what a .NET 10 host reaches.
        Assert.That (receivers.Length, Is.GreaterThanOrEqualTo 800)
        Assert.That (openWithOpenInterface, Is.GreaterThanOrEqualTo 85)

    /// The canonical forms of an open receiver's entries, named outright: an entry over the
    /// receiver's variables is an open construction over those variables, a non-generic interface is
    /// closed, a definition applied to its own variables is the definition, and a closed receiver
    /// whose arguments make two entries one type lists that type twice.
    [<Test>]
    let ``An open receiver's entries are in canonical form`` () : unit =
        let source =
            """
using System;
public interface IA<T> { }
public interface IB { }
public interface IC<T> : IA<T>, IB { }
public interface IX<T> : IC<T>, IEquatable<IX<T>> { }
public class BaseA<T> : IA<T> { }
public class Dup<T, U> : BaseA<T>, IA<U> { }
"""

        let image =
            Roslyn.compileAssembly "CanonicalForms" OutputKind.DynamicallyLinkedLibrary [] [ source ]

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let identityOf (assembly : DumpedAssembly) (ns : string) (name : string) : ResolvedTypeIdentity =
            (assembly.TypeDefs.Values
             |> Seq.find (fun typeInfo -> typeInfo.Namespace = ns && typeInfo.Name = name))
                .Identity

        let state = coreState.WithLoadedAssembly dumped

        let listIdentity = identityOf corelib "System.Collections.Generic" "List`1"

        let state, list =
            RuntimeTypeInterfaces.ofReceiver
                loggerFactory
                bct
                "test"
                state
                (RuntimeTypeHandleTarget.OpenGenericTypeDefinition listIdentity)

        list.[0]
        |> shouldEqual (
            RuntimeTypeHandleTarget.OpenConstructed (
                identityOf corelib "System.Collections.Generic" "IList`1",
                [ RuntimeTypeHandleTarget.GenericParameter (listIdentity, 0) ]
            )
        )

        match list.[3] with
        | RuntimeTypeHandleTarget.Closed handle ->
            let ty =
                AllConcreteTypes.lookup handle state.TypeSystem.ConcreteTypes
                |> Option.defaultWith (fun () -> failwith $"%O{handle} is not registered")

            ty.Identity
            |> shouldEqual (identityOf corelib "System.Collections" "IEnumerable")
        | other -> failwith $"List<>'s fourth interface, IEnumerable, came back as %O{other}"

        let ixIdentity = identityOf dumped "" "IX`1"

        let state, ix =
            RuntimeTypeInterfaces.ofReceiver
                loggerFactory
                bct
                "test"
                state
                (RuntimeTypeHandleTarget.OpenGenericTypeDefinition ixIdentity)

        ix.[3]
        |> shouldEqual (
            RuntimeTypeHandleTarget.OpenConstructed (
                identityOf corelib "System" "IEquatable`1",
                [ RuntimeTypeHandleTarget.OpenGenericTypeDefinition ixIdentity ]
            )
        )

        let state, dupIntInt =
            HostTypeTarget.target loggerFactory bct [] state typeof<int>
            |> fun (state, intTarget) ->
                match intTarget with
                | RuntimeTypeHandleTarget.Closed intHandle ->
                    let dupIdentity = identityOf dumped "" "Dup`2"

                    RuntimeTypeInterfaces.ofInstantiation
                        loggerFactory
                        bct
                        "test"
                        state
                        dupIdentity
                        (Collections.Immutable.ImmutableArray.Create (
                            RuntimeTypeHandleTarget.Closed intHandle,
                            RuntimeTypeHandleTarget.Closed intHandle
                        ))
                | other -> failwith $"int came back as %O{other}"

        ignore<IlMachineState> state

        match dupIntInt with
        | Ok [ RuntimeTypeHandleTarget.Closed first ; RuntimeTypeHandleTarget.Closed second ] ->
            first |> shouldEqual second
        | other -> failwith $"Dup<int, int> answered %A{other}"

    // ----- Generated hierarchies -----

    [<RequireQualifiedAccess>]
    type private DeclKind =
        | Interface
        | Class
        | Struct

    /// A generic argument, read in the vocabulary of the declaration it appears on.
    [<RequireQualifiedAccess>]
    type private Arg =
        | Int
        | Str
        /// The declaration's own variable: a nested declaration's holder's variable is its first.
        | Param of int
        | Array of Arg
        /// An instantiation of an earlier top-level declaration.
        | Of of int * Arg list
        /// The declaration itself, over its own variables.
        | Self

    type private Decl =
        {
            Kind : DeclKind
            /// The variables the declaration declares itself.
            Arity : int
            /// Declared inside a holder class `H<TH>`, whose variable comes first among the
            /// declaration's own. No other declaration names a nested one.
            Nested : bool
            /// A class's base class, an earlier top-level class.
            Base : (int * Arg list) option
            /// The InterfaceImpl rows: an interface's parents, a class's or struct's interfaces.
            Rows : (int * Arg list) list
        }

        member this.TotalArity : int = (if this.Nested then 1 else 0) + this.Arity

    let rec private substitute (arguments : Arg list) (self : Arg) (arg : Arg) : Arg =
        match arg with
        | Arg.Int
        | Arg.Str -> arg
        | Arg.Param i -> arguments.[i]
        | Arg.Array element -> Arg.Array (substitute arguments self element)
        | Arg.Of (j, inner) -> Arg.Of (j, inner |> List.map (substitute arguments self))
        | Arg.Self -> self

    /// `used` and every interface it inherits, in the user's vocabulary.
    let rec private closure (decls : Decl list) (j : int, arguments : Arg list) : (int * Arg list) list =
        (j, arguments)
        :: (decls.[j].Rows
            |> List.collect (fun (k, inner) ->
                closure decls (k, inner |> List.map (substitute arguments (Arg.Of (j, arguments))))
            ))

    type private Term =
        | Var of int
        | Con of string * Term list

    let rec private toTerm (selfIndex : int) (selfArity : int) (arg : Arg) : Term =
        match arg with
        | Arg.Int -> Con ("int", [])
        | Arg.Str -> Con ("string", [])
        | Arg.Param i -> Var i
        | Arg.Array element -> Con ("[]", [ toTerm selfIndex selfArity element ])
        | Arg.Of (j, inner) -> Con (string<int> j, inner |> List.map (toTerm selfIndex selfArity))
        | Arg.Self -> Con (string<int> selfIndex, List.init selfArity Var)

    /// Whether some assignment of the declaring type's variables makes `a` and `b` one type, which is
    /// C#'s test for two interfaces that "may unify" (CS0695).
    let private mayUnify (a : Term) (b : Term) : bool =
        let rec walk (s : Map<int, Term>) (t : Term) : Term =
            match t with
            | Var v ->
                match Map.tryFind v s with
                | Some bound -> walk s bound
                | None -> t
            | Con _ -> t

        let rec occurs (s : Map<int, Term>) (v : int) (t : Term) : bool =
            match walk s t with
            | Var w -> w = v
            | Con (_, ts) -> ts |> List.exists (occurs s v)

        let rec unify (s : Map<int, Term>) (a : Term) (b : Term) : Map<int, Term> option =
            match walk s a, walk s b with
            | Var x, Var y when x = y -> Some s
            | Var x, t
            | t, Var x -> if occurs s x t then None else Some (Map.add x t s)
            | Con (f, xs), Con (g, ys) ->
                if f <> g || xs.Length <> ys.Length then
                    None
                else
                    (Some s, List.zip xs ys)
                    ||> List.fold (fun s (x, y) -> s |> Option.bind (fun s -> unify s x y))

        (unify Map.empty a b).IsSome

    /// The rows of declaration `index` that C# accepts, in order: a row is dropped if it repeats
    /// one kept, or if its closure holds an instantiation that may unify with, without being, another
    /// one in its closure or in a kept row's.
    let private keepCompilable (decls : Decl list) (index : int) (selfArity : int) (rows : (int * Arg list) list) =
        let term (j : int, arguments : Arg list) : Term =
            toTerm index selfArity (Arg.Of (j, arguments))

        let conflicts (a : int * Arg list) (b : int * Arg list) : bool =
            fst a = fst b && a <> b && mayUnify (term a) (term b)

        let kept, _ =
            (([], []), rows)
            ||> List.fold (fun (kept, seen) row ->
                let mine = closure decls row

                if List.contains row kept then
                    kept, seen
                elif mine |> List.exists (fun m -> seen |> List.exists (conflicts m)) then
                    kept, seen
                elif mine |> List.exists (fun m -> mine |> List.exists (conflicts m)) then
                    kept, seen
                else
                    row :: kept, mine @ seen
            )

        List.rev kept

    let rec private genArg (decls : Decl list) (total : int) (depth : int) : Gen<Arg> =
        let leaves =
            [
                yield 2, Gen.constant Arg.Int
                yield 2, Gen.constant Arg.Str
                yield 1, Gen.constant Arg.Self
                if total > 0 then
                    yield 5, Gen.choose (0, total - 1) |> Gen.map Arg.Param
            ]
            |> Gen.frequency

        if depth = 0 then
            leaves
        else
            let generic =
                decls |> List.indexed |> List.filter (fun (_, d) -> not d.Nested && d.Arity > 0)

            let composite =
                [
                    yield genArg decls total (depth - 1) |> Gen.map Arg.Array
                    if not generic.IsEmpty then
                        yield
                            gen {
                                let! j, d = Gen.elements generic
                                let! inner = Gen.listOfLength d.Arity (genArg decls total (depth - 1))
                                return Arg.Of (j, inner)
                            }
                ]
                |> Gen.oneof

            Gen.frequency [ 3, leaves ; 1, composite ]

    let private genUse (decls : Decl list) (candidates : int list) (total : int) : Gen<int * Arg list> =
        gen {
            let! j = Gen.elements candidates
            let! depth = Gen.choose (0, 1)
            let! arguments = Gen.listOfLength decls.[j].Arity (genArg decls total depth)
            return j, arguments
        }

    /// Append one declaration to `decls`.
    let private genDecl (isInterface : bool) (decls : Decl list) : Gen<Decl list> =
        gen {
            let index = decls.Length

            let interfaces =
                decls
                |> List.indexed
                |> List.filter (fun (_, d) -> d.Kind = DeclKind.Interface)
                |> List.map fst

            let classes =
                decls
                |> List.indexed
                |> List.filter (fun (_, d) -> d.Kind = DeclKind.Class && not d.Nested)
                |> List.map fst

            let! kind, nested =
                if isInterface then
                    Gen.constant (DeclKind.Interface, false)
                else
                    Gen.frequency
                        [
                            4, Gen.constant (DeclKind.Class, false)
                            1, Gen.constant (DeclKind.Struct, false)
                            1, Gen.constant (DeclKind.Class, true)
                        ]

            let! arity = Gen.choose (0, 2)
            let total = (if nested then 1 else 0) + arity

            let! baseClass =
                if kind <> DeclKind.Class || classes.IsEmpty then
                    Gen.constant None
                else
                    Gen.frequency [ 2, Gen.constant None ; 3, genUse decls classes total |> Gen.map Some ]

            let! rowCount =
                if interfaces.IsEmpty then
                    Gen.constant 0
                else
                    Gen.choose (0, 3)

            let! rows = Gen.listOfLength rowCount (genUse decls interfaces total)

            let decl =
                {
                    Kind = kind
                    Arity = arity
                    Nested = nested
                    Base = baseClass
                    Rows = []
                }

            // The closure of a row naming this declaration itself needs its own rows, which an interface
            // never names: only an earlier interface is a row.
            let decls = decls @ [ decl ]

            return
                decls
                |> List.mapi (fun i d ->
                    if i = index then
                        { d with
                            Rows = keepCompilable decls index total rows
                        }
                    else
                        d
                )
        }

    /// A fixed opening that reaches the shapes a random corpus reaches rarely: a base whose entry
    /// a derived row duplicates once closed, and a re-declared row.
    let private opening : Decl list =
        let iface arity rows =
            {
                Kind = DeclKind.Interface
                Arity = arity
                Nested = false
                Base = None
                Rows = rows
            }

        let cls arity baseClass rows =
            {
                Kind = DeclKind.Class
                Arity = arity
                Nested = false
                Base = baseClass
                Rows = rows
            }

        [
            // I0<T0>, I1, I2<T0> : I0<T0>, I1
            iface 1 []
            iface 0 []
            iface 1 [ 0, [ Arg.Param 0 ] ; 1, [] ]
            // C3<T0> : I0<T0>
            cls 1 None [ 0, [ Arg.Param 0 ] ]
            // C4<T0, T1> : C3<T0>, I0<T1>
            cls 2 (Some (3, [ Arg.Param 0 ])) [ 0, [ Arg.Param 1 ] ]
            // C5<T0> : C3<int>, I2<T0>
            cls 1 (Some (3, [ Arg.Int ])) [ 2, [ Arg.Param 0 ] ]
        ]

    let private genCorpus : Gen<Decl list> =
        gen {
            let! opened = Gen.frequency [ 1, Gen.constant opening ; 2, Gen.constant [] ]
            let! interfaceCount = Gen.choose (2, 4)
            let! typeCount = Gen.choose (2, 6)

            let! withInterfaces =
                (Gen.constant opened, [ 1..interfaceCount ])
                ||> List.fold (fun acc _ -> acc |> Gen.bind (genDecl true))

            return!
                (Gen.constant withInterfaces, [ 1..typeCount ])
                ||> List.fold (fun acc _ -> acc |> Gen.bind (genDecl false))
        }

    let private declName (index : int) (decl : Decl) : string =
        match decl.Kind, decl.Nested with
        | DeclKind.Interface, _ -> $"I%d{index}"
        | DeclKind.Struct, _ -> $"S%d{index}"
        | DeclKind.Class, false -> $"C%d{index}"
        | DeclKind.Class, true -> $"N%d{index}"

    let private parameters (decl : Decl) : string list =
        (if decl.Nested then [ "TH" ] else [])
        @ List.init decl.Arity (fun i -> $"T%d{i}")

    let private ownParameters (decl : Decl) : string list =
        List.init decl.Arity (fun i -> $"T%d{i}")

    let private applied (name : string) (arguments : string list) : string =
        match arguments with
        | [] -> name
        | arguments -> name + "<" + String.Join (", ", arguments) + ">"

    let rec private renderArg (decls : Decl list) (index : int) (arg : Arg) : string =
        let decl = decls.[index]

        match arg with
        | Arg.Int -> "int"
        | Arg.Str -> "string"
        | Arg.Param i -> (parameters decl).[i]
        | Arg.Array element -> renderArg decls index element + "[]"
        | Arg.Of (j, inner) -> applied (declName j decls.[j]) (inner |> List.map (renderArg decls index))
        // Inside its holder, a nested type's own name binds to the holder's instantiation.
        | Arg.Self -> applied (declName index decl) (ownParameters decl)

    let private render (decls : Decl list) : string =
        let source = StringBuilder ()

        decls
        |> List.iteri (fun index decl ->
            let uses =
                [
                    match decl.Base with
                    | Some (j, arguments) -> yield (j, arguments)
                    | None -> ()
                    yield! decl.Rows
                ]
                |> List.map (fun (j, arguments) ->
                    applied (declName j decls.[j]) (arguments |> List.map (renderArg decls index))
                )

            let header =
                let keyword =
                    match decl.Kind with
                    | DeclKind.Interface -> "interface"
                    | DeclKind.Class -> "class"
                    | DeclKind.Struct -> "struct"

                let name = applied (declName index decl) (ownParameters decl)

                match uses with
                | [] -> $"public %s{keyword} %s{name} {{ }}"
                | uses ->
                    let joined = String.Join (", ", uses)
                    $"public %s{keyword} %s{name} : %s{joined} {{ }}"

            if decl.Nested then
                source.AppendLine $"public class H%d{index}<TH> {{ %s{header} }}"
                |> ignore<StringBuilder>
            else
                source.AppendLine header |> ignore<StringBuilder>
        )

        source.ToString ()

    /// The receivers asked about: each declaration at all-`int`, all-`string`, and with the two mixed;
    /// and each generic declaration as its definition, as an open construction over `Dictionary<,>`'s
    /// `K` alone and alternating with `int`, and its base class where that is open.
    let private pool (decls : Decl list) (host : Reflection.Assembly) : Type list =
        decls
        |> List.indexed
        |> List.collect (fun (index, decl) ->
            let suffix arity = if arity = 0 then "" else $"`%d{arity}"

            let name =
                if decl.Nested then
                    $"H%d{index}`1+%s{declName index decl}%s{suffix decl.Arity}"
                else
                    declName index decl + suffix decl.Arity

            let definition =
                match host.GetType name with
                | null -> failwith $"the corpus has no type %s{name}"
                | t -> t

            let n = decl.TotalArity

            if n = 0 then
                [ definition ]
            else
                let k = dictionaryVariables.[0]

                let instantiations =
                    [
                        List.replicate n typeof<int>
                        List.replicate n typeof<string>
                        List.init n (fun i -> if i % 2 = 0 then typeof<int> else typeof<string>)
                        List.init n (fun i -> if i % 2 = 0 then typeof<string> else typeof<int>)
                        List.replicate n k
                        List.init n (fun i -> if i % 2 = 0 then k else typeof<int>)
                    ]
                    |> List.distinct
                    |> List.map (fun arguments -> definition.MakeGenericType (Array.ofList arguments))

                let openBase =
                    match definition.BaseType with
                    | null -> []
                    | baseType when baseType.ContainsGenericParameters -> [ baseType ]
                    | _ -> []

                definition :: instantiations @ openBase
        )

    /// What a generated receiver reached, read from the host's side alone.
    type private Label =
        | Duplicate
        | DeclaresOverInterfacefulBase
        | ValueTypeReceiver
        | InterfaceReceiver
        | NestedReceiver
        | DefinitionReceiver
        | OpenConstructionReceiver
        /// An interface whose argument is a generic definition, which is how CoreCLR represents the
        /// definition applied to its own variables.
        | DefinitionArgument

    let private labels (images : HostTypeTarget.Images) (receiver : Type) : Label list =
        if receiver.GetInterfaces().Length = 0 then
            []
        else
            [
                if receiver.IsGenericTypeDefinition then
                    yield Label.DefinitionReceiver
                elif receiver.ContainsGenericParameters then
                    yield Label.OpenConstructionReceiver
                if
                    receiver.GetInterfaces ()
                    |> Array.exists (fun i ->
                        i.GetGenericArguments () |> Array.exists (fun a -> a.IsGenericTypeDefinition)
                    )
                then
                    yield Label.DefinitionArgument
                if hasDuplicate receiver then
                    yield Label.Duplicate
                if declaresOverInterfacefulBase images receiver then
                    yield Label.DeclaresOverInterfacefulBase
                if receiver.IsValueType then
                    yield Label.ValueTypeReceiver
                if receiver.IsInterface then
                    yield Label.InterfaceReceiver
                if receiver.IsNested then
                    yield Label.NestedReceiver
            ]

    let private checkGenerated (cover : Label -> unit) (decls : Decl list) : unit =
        let source = render decls

        let image =
            try
                Roslyn.compileAssembly "GetInterfacesOrder" OutputKind.DynamicallyLinkedLibrary [] [ source ]
            with e ->
                failwith $"%s{e.Message}\nin the corpus:\n%s{source}"

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("getInterfacesOrder", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let images = [ hostCoreLib, corelib ; host, dumped ]
            let mutable state = coreState.WithLoadedAssembly dumped
            let mutable failures = []

            for receiver in pool decls host do
                for label in labels images receiver do
                    cover label

                let after, failure = compareReceiver images state receiver
                state <- after

                match failure with
                | Some failure -> failures <- failure :: failures
                | None -> ()

            if not failures.IsEmpty then
                failwith (
                    $"%d{failures.Length} disagreements with the host:\n"
                    + String.concat "\n" (List.rev failures)
                    + $"\nin the corpus:\n%s{source}"
                )
        finally
            context.Unload ()

    [<Test>]
    let ``Generated hierarchies: GetInterfaces agrees with the host`` () : unit =
        let stopwatch = Stopwatch.StartNew ()

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 100)
                (Arb.fromGen genCorpus)
                (fun cover corpus -> checkGenerated cover corpus)

        TestContext.Progress.WriteLine $"generated hierarchies: %d{stopwatch.ElapsedMilliseconds} ms"

        // About half of what the fixed sample reached over closed receivers alone, and for the labels
        // only an open receiver reaches, about half of what it reaches now.
        for label, floor in
            [
                Label.Duplicate, 68
                Label.DeclaresOverInterfacefulBase, 190
                Label.ValueTypeReceiver, 65
                Label.InterfaceReceiver, 210
                Label.NestedReceiver, 100
                Label.DefinitionReceiver, 210
                Label.OpenConstructionReceiver, 340
                Label.DefinitionArgument, 29
            ] do
            Assert.That (coverage.Count label, Is.GreaterThanOrEqualTo floor, $"%A{label}")

    // ----- Shapes C# refuses -----

    /// A library whose second version gives `IC<T>` and `IE<T>` parents, which the classes, compiled
    /// against the first, do not list.
    let private expansionLibrary : string =
        """
public interface IA<T> { }
public interface IB { }
public interface ID { }
#if V2
public interface IC<T> : IA<T>, IB { }
public interface IE<T> : IA<T[]> { }
#else
public interface IC<T> { }
public interface IE<T> { }
#endif
public class LBase<T> : IC<T> { }
"""

    let private expansionClasses : string =
        """
public class BaseA<T> : IA<T> { }
public class C<T> : IC<T>, ID { }
public class CE<T> : IE<T>, IC<T> { }
public struct S<T> : IC<T>, ID { }
public interface IF<T> : IC<T>, ID { }
public class Der<T> : LBase<T>, ID { }
public class CClosed : IC<int>, ID { }
public class CClosedG<T> : IC<int>, ID { }
public class C2<T> : IC<T>, IA<T> { }
public class Der2<T> : BaseA<T>, IC<T> { }
public class D2<T, U> : IC<T>, IA<U> { }
public struct S2<T> : IC<T[]> { }
"""

    let private expansionLibraryBefore : byte[] Lazy =
        lazy (Roslyn.compileAssembly "ExpansionLib" OutputKind.DynamicallyLinkedLibrary [] [ expansionLibrary ])

    let private expansionLibraryAfter : byte[] Lazy =
        lazy
            (Roslyn.compileAssembly
                "ExpansionLib"
                OutputKind.DynamicallyLinkedLibrary
                []
                [ "#define V2\n" + expansionLibrary ])

    let private expansionClassesImage : byte[] Lazy =
        lazy
            (Roslyn.compileAssembly
                "ExpansionCls"
                OutputKind.DynamicallyLinkedLibrary
                [ MetadataReference.CreateFromImage expansionLibraryBefore.Value ]
                [ expansionClasses ])

    /// Each closed receiver of the classes, and each generic definition, from the classes' host
    /// assembly: a type with no arguments listed is the definition itself.
    let private expansionReceivers : (string * Type list) list =
        [
            "C`1", [ typeof<int> ]
            "C`1", [ typeof<string> ]
            "CE`1", [ typeof<int> ]
            "S`1", [ typeof<int> ]
            "IF`1", [ typeof<int> ]
            "Der`1", [ typeof<int> ]
            "CClosed", []
            "CClosedG`1", []
            "C2`1", [ typeof<int> ]
            "Der2`1", [ typeof<int> ]
            "D2`2", [ typeof<int> ; typeof<string> ]
            "S2`1", [ typeof<int> ]
            "C`1", []
            "CE`1", []
            "S`1", []
            "IF`1", []
            "Der`1", []
            "C2`1", []
            "Der2`1", []
            "D2`2", []
            "S2`1", []
            "C`1", [ dictionaryVariables.[0] ]
        ]

    [<Test>]
    let ``Shapes C# refuses: GetInterfaces agrees with the host`` () : unit =
        let library =
            use stream = new MemoryStream (expansionLibraryAfter.Value)
            AssemblyApi.read loggerFactory None stream

        let classes =
            use stream = new MemoryStream (expansionClassesImage.Value)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("getInterfacesExpansion", isCollectible = true)

        try
            let hostLibrary =
                use stream = new MemoryStream (expansionLibraryAfter.Value)
                context.LoadFromStream stream

            let hostClasses =
                use stream = new MemoryStream (expansionClassesImage.Value)
                context.LoadFromStream stream

            let images = [ hostCoreLib, corelib ; hostLibrary, library ; hostClasses, classes ]

            let mutable state =
                (coreState.WithLoadedAssembly library).WithLoadedAssembly classes

            let mutable failures = []

            let receivers =
                expansionReceivers
                |> List.map (fun (name, arguments) ->
                    let definition =
                        match hostClasses.GetType name with
                        | null -> failwith $"the classes have no type %s{name}"
                        | t -> t

                    if arguments.IsEmpty then
                        definition
                    else
                        definition.MakeGenericType (Array.ofList arguments)
                )

            // `Der<>`'s open base, `LBase<T>` over `Der`'s `T`, whose map comes from the library.
            let receivers = receivers @ [ hostClasses.GetType("Der`1").BaseType ]

            for receiver in receivers do
                let after, failure = compareReceiver images state receiver
                state <- after

                match failure with
                | Some failure -> failures <- failure :: failures
                | None -> ()

            report "shapes C# refuses" (List.rev failures)

            // The second version is what the host loaded: `C<int>` reaches `IA<int>` only through it.
            Assert.That (
                receivers.[0].GetInterfaces ()
                |> Array.exists (fun t -> t.IsGenericType && t.GetGenericTypeDefinition().Name = "IA`1"),
                Is.True
            )

            // CoreCLR refuses to load `D2<int, int>` ("has a contracting interface set for some
            // instantiations"): its `IA<T>`, found by expanding `IC<T>`, and its declared `IA<U>` would
            // become one entry. So it is not a receiver here.
            Assert.Throws<TypeLoadException> (fun () ->
                hostClasses.GetType("D2`2").MakeGenericType (typeof<int>, typeof<int>)
                |> ignore<Type>
            )
            |> ignore<TypeLoadException>
        finally
            context.Unload ()

    // ----- A TypeSpec with a leading custom modifier -----

    /// An image with `I<T>`, `B<T> : I<T>` and a class `C`, a type `Modifier` naming nothing, and
    /// either `C : modopt(Modifier) I<int>` (`onInterface`) or `C : modopt(Modifier) B<int>`. C# never
    /// spells either, but a TypeSpec blob may begin with custom modifiers, and CoreCLR loads both.
    let private modifiedTypeSpecImage (onInterface : bool) : byte[] =
        let metadata = Reflection.Metadata.Ecma335.MetadataBuilder ()
        let nil = Unchecked.defaultof<Reflection.Metadata.EntityHandle>

        let definition (handle : Reflection.Metadata.TypeDefinitionHandle) : Reflection.Metadata.EntityHandle =
            Reflection.Metadata.TypeDefinitionHandle.op_Implicit handle

        metadata.AddModule (
            0,
            metadata.GetOrAddString "ModifiedSpec.dll",
            metadata.GetOrAddGuid (Guid ("6e1c1f0e-8f5a-4c4e-9d55-2b8c6f1d0a71")),
            Unchecked.defaultof<_>,
            Unchecked.defaultof<_>
        )
        |> ignore<Reflection.Metadata.ModuleDefinitionHandle>

        metadata.AddAssembly (
            metadata.GetOrAddString "ModifiedSpec",
            Version (1, 0, 0, 0),
            Unchecked.defaultof<_>,
            Unchecked.defaultof<_>,
            enum<Reflection.AssemblyFlags> 0,
            Reflection.AssemblyHashAlgorithm.None
        )
        |> ignore<Reflection.Metadata.AssemblyDefinitionHandle>

        let coreLibName = hostCoreLib.GetName ()

        let coreLibRef =
            metadata.AddAssemblyReference (
                metadata.GetOrAddString coreLibName.Name,
                coreLibName.Version,
                Unchecked.defaultof<_>,
                metadata.GetOrAddBlob (coreLibName.GetPublicKeyToken ()),
                enum<Reflection.AssemblyFlags> 0,
                Unchecked.defaultof<_>
            )

        let objectRef =
            metadata.AddTypeReference (
                Reflection.Metadata.AssemblyReferenceHandle.op_Implicit coreLibRef,
                metadata.GetOrAddString "System",
                metadata.GetOrAddString "Object"
            )
            |> fun handle ->
                (Reflection.Metadata.TypeReferenceHandle.op_Implicit handle : Reflection.Metadata.EntityHandle)

        let addType
            (name : string)
            (attributes : Reflection.TypeAttributes)
            (baseType : Reflection.Metadata.EntityHandle)
            =
            metadata.AddTypeDefinition (
                attributes,
                Unchecked.defaultof<_>,
                metadata.GetOrAddString name,
                baseType,
                Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle 1,
                Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle 1
            )

        addType "<Module>" Reflection.TypeAttributes.NotPublic nil
        |> ignore<Reflection.Metadata.TypeDefinitionHandle>

        let iface =
            addType
                "I`1"
                (Reflection.TypeAttributes.Public
                 ||| Reflection.TypeAttributes.Interface
                 ||| Reflection.TypeAttributes.Abstract)
                nil

        let baseClass = addType "B`1" Reflection.TypeAttributes.Public objectRef
        let modifier = addType "Modifier" Reflection.TypeAttributes.Public objectRef

        // `modopt(Modifier) GENERICINST CLASS <generic> 1 int32`
        let modifiedInstantiation (generic : Reflection.Metadata.TypeDefinitionHandle) =
            let blob = Reflection.Metadata.BlobBuilder ()
            blob.WriteByte 0x20uy
            blob.WriteCompressedInteger (Reflection.Metadata.Ecma335.CodedIndex.TypeDefOrRef (definition modifier))
            blob.WriteByte 0x15uy
            blob.WriteByte 0x12uy
            blob.WriteCompressedInteger (Reflection.Metadata.Ecma335.CodedIndex.TypeDefOrRef (definition generic))
            blob.WriteCompressedInteger 1
            blob.WriteByte 0x08uy

            metadata.AddTypeSpecification (metadata.GetOrAddBlob blob)
            |> fun handle ->
                (Reflection.Metadata.TypeSpecificationHandle.op_Implicit handle : Reflection.Metadata.EntityHandle)

        let receiver =
            if onInterface then
                addType "C" Reflection.TypeAttributes.Public objectRef
            else
                addType "C" Reflection.TypeAttributes.Public (modifiedInstantiation baseClass)

        // B<T> : I<T>, spelled `GENERICINST CLASS I 1 VAR 0`.
        let overOwnVariable =
            let blob = Reflection.Metadata.BlobBuilder ()
            blob.WriteByte 0x15uy
            blob.WriteByte 0x12uy
            blob.WriteCompressedInteger (Reflection.Metadata.Ecma335.CodedIndex.TypeDefOrRef (definition iface))
            blob.WriteCompressedInteger 1
            blob.WriteByte 0x13uy
            blob.WriteCompressedInteger 0

            metadata.AddTypeSpecification (metadata.GetOrAddBlob blob)
            |> fun handle ->
                (Reflection.Metadata.TypeSpecificationHandle.op_Implicit handle : Reflection.Metadata.EntityHandle)

        metadata.AddInterfaceImplementation (baseClass, overOwnVariable)
        |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        if onInterface then
            metadata.AddInterfaceImplementation (receiver, modifiedInstantiation iface)
            |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        metadata.AddGenericParameter (
            definition iface,
            Reflection.GenericParameterAttributes.None,
            metadata.GetOrAddString "T",
            0
        )
        |> ignore<Reflection.Metadata.GenericParameterHandle>

        metadata.AddGenericParameter (
            definition baseClass,
            Reflection.GenericParameterAttributes.None,
            metadata.GetOrAddString "T",
            0
        )
        |> ignore<Reflection.Metadata.GenericParameterHandle>

        let builder =
            Reflection.PortableExecutable.ManagedPEBuilder (
                Reflection.PortableExecutable.PEHeaderBuilder (
                    imageCharacteristics =
                        (Reflection.PortableExecutable.Characteristics.ExecutableImage
                         ||| Reflection.PortableExecutable.Characteristics.Dll)
                ),
                Reflection.Metadata.Ecma335.MetadataRootBuilder metadata,
                Reflection.Metadata.BlobBuilder ()
            )

        let output = Reflection.Metadata.BlobBuilder ()
        builder.Serialize output |> ignore<Reflection.Metadata.BlobContentId>
        output.ToArray ()

    [<TestCase true>]
    [<TestCase false>]
    let ``A TypeSpec with a leading custom modifier names the instantiation it modifies`` (onInterface : bool) : unit =
        let image = modifiedTypeSpecImage onInterface

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("modifiedTypeSpec", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let receiver = host.GetType "C"
            // The host reads the modified spelling as `I<int>` itself, or through `B<int>`.
            receiver.GetInterfaces ()
            |> Array.map _.ToString()
            |> shouldEqual [| "I`1[System.Int32]" |]

            let _, failure =
                compareReceiver [ hostCoreLib, corelib ; host, dumped ] (coreState.WithLoadedAssembly dumped) receiver

            failure |> shouldEqual None
        finally
            context.Unload ()

    // ----- A deep chain of generic interfaces -----

    /// `I16<A, B, C> : I15<A, B, C>`, and so on down to `I0`, each passing its three arguments on; a
    /// class implementing the top, and a chain of generic classes over it. Each interface's arguments
    /// are read in the context of the one above, so the contexts nest sixteen deep, and closing or
    /// re-reading each occurrence of a context afresh would take time exponential in the depth.
    [<Test>]
    let ``A deep chain of generic interfaces agrees with the host`` () : unit =
        let depth = 16

        let source =
            let interfaces =
                [
                    yield "public interface I0<A, B, C> { }"
                    for i in 1..depth do
                        yield $"public interface I%d{i}<A, B, C> : I%d{i - 1}<A, B, C> {{ }}"
                ]

            String.concat
                "\n"
                (interfaces
                 @ [
                     $"public class Top : I%d{depth}<int, string, long> {{ }}"
                     $"public class B0<A, B, C> : I%d{depth}<A, B, C> {{ }}"
                     "public class B1<A, B, C> : B0<A, B, C> { }"
                     "public class B2<A, B, C> : B1<A, B, C> { }"
                 ])

        let image =
            Roslyn.compileAssembly "DeepChain" OutputKind.DynamicallyLinkedLibrary [] [ source ]

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("deepChain", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let images = [ hostCoreLib, corelib ; host, dumped ]
            let mutable state = coreState.WithLoadedAssembly dumped
            let mutable failures = []
            let stopwatch = Stopwatch.StartNew ()

            for receiver in
                [
                    host.GetType "Top"
                    host.GetType("B2`3").MakeGenericType (typeof<int>, typeof<string>, typeof<int64>)
                ] do
                let after, failure = compareReceiver images state receiver
                state <- after

                match failure with
                | Some failure -> failures <- failure :: failures
                | None -> ()

            report "deep chain" (List.rev failures)
            (host.GetType "Top").GetInterfaces().Length |> shouldEqual (depth + 1)
            // Linear work finishes in well under a second; the exponential would not finish at all.
            Assert.That (stopwatch.Elapsed, Is.LessThan (TimeSpan.FromSeconds 60.0))
        finally
            context.Unload ()

    // ----- InterfaceImpl rows C# never writes -----

    /// An argument of a hand-spelled `I<_>` row.
    [<RequireQualifiedAccess>]
    type private RowArgument =
        /// `int32`.
        | Int32
        /// `valuetype [CoreLib]System.Int32`.
        | Int32AsValueType
        /// `class [CoreLib]System.Int32`, the wrong kind.
        | Int32AsClass
        /// `!0`, in a type with no variables.
        | Var0
        /// `class [CoreLib]System.Missing`, which CoreLib does not declare.
        | Missing

    /// One InterfaceImpl row of a hand-built image.
    [<RequireQualifiedAccess>]
    type private Row =
        /// `GENERICINST <CLASS or VALUETYPE> I 1 <argument>`.
        | Instantiation of asValueType : bool * argument : RowArgument
        /// `CLASS I`: the generic `I<T>` with no instantiation.
        | BareGeneric
        /// `[CoreLib]System.IAbsent`, which CoreLib does not declare.
        | Absent
        /// `!!0`, the whole spelling, in a row where no method variable is in scope.
        | MethodVariable
        /// `CLASS Fake`, a class of the image whose base type CoreLib does not declare.
        | ClassOverAbsentBase
        /// `GENERICINST CLASS I 2 <first> <second>`: two arguments to the one-parameter `I`.
        | TwoArguments of first : RowArgument * second : RowArgument
        /// `[CoreLib]System.Object`, a class, by its TypeRef.
        | Object
        /// `I<T>` by its TypeDef token, which carries no instantiation.
        | GenericByTypeDef
        /// `GENERICINST CLASS J 1 !0`, where `J<T>` extends the absent `IAbsent`, in a type with no
        /// variables.
        | OverAbsentParentWithVar0
        /// `CLASS K`, the non-generic interface `K` as a TypeSpec.
        | NonGenericAsTypeSpec
        /// `K` by its TypeDef token.
        | NonGenericByTypeDef
        /// The class `F2`, which implements the absent `IAbsent`, by its TypeDef token.
        | ClassOverAbsentInterface
        /// `L` by its TypeDef token, where `L : M<int, int>` gives the one-parameter `M<T> : L` two
        /// arguments: a cycle through a row whose arguments cannot be read.
        | CycleThroughWrongArity

    /// An image with `I<T>`, `B : <baseRows>` and `C : B, <receiverRows>`.
    let private rowsImage (baseRows : Row list) (receiverRows : Row list) : byte[] =
        let metadata = Reflection.Metadata.Ecma335.MetadataBuilder ()
        let nil = Unchecked.defaultof<Reflection.Metadata.EntityHandle>

        let definition (handle : Reflection.Metadata.TypeDefinitionHandle) : Reflection.Metadata.EntityHandle =
            Reflection.Metadata.TypeDefinitionHandle.op_Implicit handle

        metadata.AddModule (
            0,
            metadata.GetOrAddString "InterfaceRows.dll",
            metadata.GetOrAddGuid (Guid ("0f5b8c2a-3d61-4e7b-9a40-5c7e2d18b963")),
            Unchecked.defaultof<_>,
            Unchecked.defaultof<_>
        )
        |> ignore<Reflection.Metadata.ModuleDefinitionHandle>

        metadata.AddAssembly (
            metadata.GetOrAddString "InterfaceRows",
            Version (1, 0, 0, 0),
            Unchecked.defaultof<_>,
            Unchecked.defaultof<_>,
            enum<Reflection.AssemblyFlags> 0,
            Reflection.AssemblyHashAlgorithm.None
        )
        |> ignore<Reflection.Metadata.AssemblyDefinitionHandle>

        let coreLibName = hostCoreLib.GetName ()

        let coreLibRef =
            metadata.AddAssemblyReference (
                metadata.GetOrAddString coreLibName.Name,
                coreLibName.Version,
                Unchecked.defaultof<_>,
                metadata.GetOrAddBlob (coreLibName.GetPublicKeyToken ()),
                enum<Reflection.AssemblyFlags> 0,
                Unchecked.defaultof<_>
            )

        let coreLibType (name : string) : Reflection.Metadata.EntityHandle =
            metadata.AddTypeReference (
                Reflection.Metadata.AssemblyReferenceHandle.op_Implicit coreLibRef,
                metadata.GetOrAddString "System",
                metadata.GetOrAddString name
            )
            |> Reflection.Metadata.TypeReferenceHandle.op_Implicit

        let objectRef = coreLibType "Object"
        let int32Ref = coreLibType "Int32"
        let missingRef = coreLibType "Missing"
        let absentRef = coreLibType "IAbsent"
        let absentBaseRef = coreLibType "AbsentBase"

        let addType
            (name : string)
            (attributes : Reflection.TypeAttributes)
            (baseType : Reflection.Metadata.EntityHandle)
            =
            metadata.AddTypeDefinition (
                attributes,
                Unchecked.defaultof<_>,
                metadata.GetOrAddString name,
                baseType,
                Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle 1,
                Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle 1
            )

        addType "<Module>" Reflection.TypeAttributes.NotPublic nil
        |> ignore<Reflection.Metadata.TypeDefinitionHandle>

        let iface =
            addType
                "I`1"
                (Reflection.TypeAttributes.Public
                 ||| Reflection.TypeAttributes.Interface
                 ||| Reflection.TypeAttributes.Abstract)
                nil

        let baseClass = addType "B" Reflection.TypeAttributes.Public objectRef
        let receiver = addType "C" Reflection.TypeAttributes.Public (definition baseClass)
        let fake = addType "Fake" Reflection.TypeAttributes.Public absentBaseRef

        let interfaceAttributes =
            Reflection.TypeAttributes.Public
            ||| Reflection.TypeAttributes.Interface
            ||| Reflection.TypeAttributes.Abstract

        let overAbsent = addType "J`1" interfaceAttributes nil
        let nonGeneric = addType "K" interfaceAttributes nil
        let classOverAbsent = addType "F2" Reflection.TypeAttributes.Public objectRef
        let cycleL = addType "L" interfaceAttributes nil
        let cycleM = addType "M`1" interfaceAttributes nil

        let typeSpec (write : Reflection.Metadata.BlobBuilder -> unit) : Reflection.Metadata.EntityHandle =
            let blob = Reflection.Metadata.BlobBuilder ()
            write blob

            metadata.AddTypeSpecification (metadata.GetOrAddBlob blob)
            |> Reflection.Metadata.TypeSpecificationHandle.op_Implicit

        let codedIndex (handle : Reflection.Metadata.EntityHandle) : int =
            Reflection.Metadata.Ecma335.CodedIndex.TypeDefOrRef handle

        let writeArgument (blob : Reflection.Metadata.BlobBuilder) (argument : RowArgument) : unit =
            match argument with
            | RowArgument.Int32 -> blob.WriteByte 0x08uy
            | RowArgument.Int32AsValueType ->
                blob.WriteByte 0x11uy
                blob.WriteCompressedInteger (codedIndex int32Ref)
            | RowArgument.Int32AsClass ->
                blob.WriteByte 0x12uy
                blob.WriteCompressedInteger (codedIndex int32Ref)
            | RowArgument.Var0 ->
                blob.WriteByte 0x13uy
                blob.WriteCompressedInteger 0
            | RowArgument.Missing ->
                blob.WriteByte 0x12uy
                blob.WriteCompressedInteger (codedIndex missingRef)

        let token (row : Row) : Reflection.Metadata.EntityHandle =
            match row with
            | Row.Absent -> absentRef
            | Row.Object -> objectRef
            | Row.NonGenericAsTypeSpec ->
                typeSpec (fun blob ->
                    blob.WriteByte 0x12uy
                    blob.WriteCompressedInteger (codedIndex (definition nonGeneric))
                )
            | Row.NonGenericByTypeDef -> definition nonGeneric
            | Row.ClassOverAbsentInterface -> definition classOverAbsent
            | Row.CycleThroughWrongArity -> definition cycleL
            | Row.GenericByTypeDef -> definition iface
            | Row.OverAbsentParentWithVar0 ->
                typeSpec (fun blob ->
                    blob.WriteByte 0x15uy
                    blob.WriteByte 0x12uy
                    blob.WriteCompressedInteger (codedIndex (definition overAbsent))
                    blob.WriteCompressedInteger 1
                    writeArgument blob RowArgument.Var0
                )
            | Row.MethodVariable ->
                typeSpec (fun blob ->
                    blob.WriteByte 0x1Euy
                    blob.WriteCompressedInteger 0
                )
            | Row.ClassOverAbsentBase ->
                typeSpec (fun blob ->
                    blob.WriteByte 0x12uy
                    blob.WriteCompressedInteger (codedIndex (definition fake))
                )
            | Row.TwoArguments (first, second) ->
                typeSpec (fun blob ->
                    blob.WriteByte 0x15uy
                    blob.WriteByte 0x12uy
                    blob.WriteCompressedInteger (codedIndex (definition iface))
                    blob.WriteCompressedInteger 2
                    writeArgument blob first
                    writeArgument blob second
                )
            | Row.BareGeneric ->
                typeSpec (fun blob ->
                    blob.WriteByte 0x12uy
                    blob.WriteCompressedInteger (codedIndex (definition iface))
                )
            | Row.Instantiation (asValueType, argument) ->
                typeSpec (fun blob ->
                    blob.WriteByte 0x15uy
                    blob.WriteByte (if asValueType then 0x11uy else 0x12uy)
                    blob.WriteCompressedInteger (codedIndex (definition iface))
                    blob.WriteCompressedInteger 1
                    writeArgument blob argument
                )

        for row in baseRows do
            metadata.AddInterfaceImplementation (baseClass, token row)
            |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        for row in receiverRows do
            metadata.AddInterfaceImplementation (receiver, token row)
            |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        metadata.AddInterfaceImplementation (overAbsent, absentRef)
        |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        metadata.AddInterfaceImplementation (classOverAbsent, absentRef)
        |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        let mOfIntInt =
            typeSpec (fun blob ->
                blob.WriteByte 0x15uy
                blob.WriteByte 0x12uy
                blob.WriteCompressedInteger (codedIndex (definition cycleM))
                blob.WriteCompressedInteger 2
                blob.WriteByte 0x08uy
                blob.WriteByte 0x08uy
            )

        metadata.AddInterfaceImplementation (cycleL, mOfIntInt)
        |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        metadata.AddInterfaceImplementation (cycleM, definition cycleL)
        |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        metadata.AddGenericParameter (
            definition iface,
            Reflection.GenericParameterAttributes.None,
            metadata.GetOrAddString "T",
            0
        )
        |> ignore<Reflection.Metadata.GenericParameterHandle>

        metadata.AddGenericParameter (
            definition overAbsent,
            Reflection.GenericParameterAttributes.None,
            metadata.GetOrAddString "T",
            0
        )
        |> ignore<Reflection.Metadata.GenericParameterHandle>

        metadata.AddGenericParameter (
            definition cycleM,
            Reflection.GenericParameterAttributes.None,
            metadata.GetOrAddString "T",
            0
        )
        |> ignore<Reflection.Metadata.GenericParameterHandle>

        let builder =
            Reflection.PortableExecutable.ManagedPEBuilder (
                Reflection.PortableExecutable.PEHeaderBuilder (
                    imageCharacteristics =
                        (Reflection.PortableExecutable.Characteristics.ExecutableImage
                         ||| Reflection.PortableExecutable.Characteristics.Dll)
                ),
                Reflection.Metadata.Ecma335.MetadataRootBuilder metadata,
                Reflection.Metadata.BlobBuilder ()
            )

        let output = Reflection.Metadata.BlobBuilder ()
        builder.Serialize output |> ignore<Reflection.Metadata.BlobContentId>
        output.ToArray ()

    /// The host's `C.GetInterfaces()`, or the exception that loading `C` raised; and PawPrint's
    /// interface map of `C`.
    let private rowsOutcome
        (baseRows : Row list)
        (receiverRows : Row list)
        : Result<string[], exn> * Result<ConcreteInterfaceDispatch.InterfaceMapEntry list, TypeBindingFailure>
        =
        let image = rowsImage baseRows receiverRows

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("interfaceRows", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let hostAnswer =
                try
                    host.GetType("C", true).GetInterfaces () |> Array.map _.ToString() |> Ok
                with e ->
                    Error e

            let state = coreState.WithLoadedAssembly dumped

            let state, receiver =
                LoadedTypeInfo.typeInfoToTypeDefn'
                    bct
                    state.TypeSystem._LoadedAssemblies
                    (dumped.TypeDefs.Values |> Seq.find (fun typeInfo -> typeInfo.Name = "C"))
                |> IlMachineState.concretizeType
                    loggerFactory
                    bct
                    state
                    dumped.DefinitionFullName
                    Collections.Immutable.ImmutableArray.Empty
                    Collections.Immutable.ImmutableArray.Empty

            let _, pawPrintAnswer =
                ConcreteInterfaceDispatch.interfaceMapOf
                    loggerFactory
                    state.DotnetRuntimeDirs
                    bct
                    "test"
                    state.TypeSystem
                    receiver

            hostAnswer, pawPrintAnswer
        finally
            context.Unload ()

    /// `C`'s row names the instantiation `B` already put in the map, so it adds no entry; but the
    /// runtime loads every row's interface as the row spells it, so a row that does not load fails
    /// the type all the same.
    [<TestCase true>]
    [<TestCase false>]
    let ``A re-declared interface's own row is loaded`` (asValueType : bool) : unit =
        let host, pawPrint =
            rowsOutcome
                [ Row.Instantiation (false, RowArgument.Int32) ]
                [ Row.Instantiation (asValueType, RowArgument.Int32) ]

        if asValueType then
            match host with
            | Error (:? TypeLoadException) -> ()
            | other -> failwith $"the host answered %A{other}"

            match pawPrint with
            | Error (TypeBindingFailure.Refused (TypeLoadRefusal.KindMismatch _)) -> ()
            | other -> failwith $"PawPrint answered %A{other}"
        else
            host |> Result.toOption |> shouldEqual (Some [| "I`1[System.Int32]" |])

            match pawPrint with
            | Ok [ entry ] -> entry.Definition.DeclaredOnType |> shouldEqual true
            | other -> failwith $"PawPrint answered %A{other}"

    /// `C`'s `I<class Int32>` names `B`'s `I<valuetype Int32>` again and adds no entry, and nothing but
    /// loading its argument finds the wrong kind there.
    [<Test>]
    let ``A row that adds no entry is still loaded`` () : unit =
        let host, pawPrint =
            rowsOutcome
                [ Row.Instantiation (false, RowArgument.Int32AsValueType) ]
                [ Row.Instantiation (false, RowArgument.Int32AsClass) ]

        match host with
        | Error (:? TypeLoadException) -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.KindMismatch _)) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// Rows are loaded in the order read, the parent's first, whether or not they add an entry:
    /// `C`'s `I<Missing>` comes before its `I<class Int32>`, which names `B`'s `I<valuetype Int32>`
    /// again with the wrong kind.
    [<Test>]
    let ``Rows that do not load fail in the order they are read`` () : unit =
        let host, pawPrint =
            rowsOutcome
                [ Row.Instantiation (false, RowArgument.Int32AsValueType) ]
                [
                    Row.Instantiation (false, RowArgument.Missing)
                    Row.Instantiation (false, RowArgument.Int32AsClass)
                ]

        match host with
        | Error (:? TypeLoadException as e) when e.Message.Contains "Missing" -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.TypeAbsent _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A row's interface is resolved, and its spelling's kind checked, before any later row is read:
    /// `GENERICINST VALUETYPE I<int>` fails before the absent interface after it.
    [<Test>]
    let ``A row's interface is spelled with the wrong kind before a later row is absent`` () : unit =
        let host, pawPrint =
            rowsOutcome [] [ Row.Instantiation (true, RowArgument.Int32) ; Row.Absent ]

        match host with
        | Error (:? TypeLoadException as e) when not (e.Message.Contains "IAbsent") -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.KindMismatch _)) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A TypeSpec naming the generic `I<T>` with no instantiation, which the host does not load.
    [<Test>]
    let ``A row naming a generic interface without an instantiation is refused`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.BareGeneric ]

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Unmodelled _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// `C`, which has no type variables, names `I<!0>`, as `B` names `I<int>`, so the duplicate check
    /// would compare a variable its context does not supply. The host does not load `C`.
    [<Test>]
    let ``A row naming a variable its type does not have is refused`` () : unit =
        let host, pawPrint =
            rowsOutcome [ Row.Instantiation (false, RowArgument.Int32) ] [ Row.Instantiation (false, RowArgument.Var0) ]

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.TypeParameterOutOfScope (0, 0))) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A row whose arguments the definition walk cannot read is loaded in its turn, so an earlier
    /// row's absent argument is reported first: `I<Missing>` before `I<!0>` in a type with no variables,
    /// and before `I<Missing, int>` given to a one-parameter `I`, as on the host.
    [<TestCase false>]
    [<TestCase true>]
    let ``An earlier row's absent argument comes before a later row's unreadable arguments`` (arity : bool) : unit =
        let later =
            if arity then
                Row.TwoArguments (RowArgument.Int32, RowArgument.Int32)
            else
                Row.Instantiation (false, RowArgument.Var0)

        let host, pawPrint =
            rowsOutcome [] [ Row.Instantiation (false, RowArgument.Missing) ; later ]

        match host with
        | Error (:? TypeLoadException as e) when e.Message.Contains "Missing" -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.TypeAbsent _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// The same arity row alone: its own absent argument is reported before its arity, as the host
    /// reports it.
    [<Test>]
    let ``A row's absent argument comes before its wrong number of arguments`` () : unit =
        let host, pawPrint =
            rowsOutcome [] [ Row.TwoArguments (RowArgument.Missing, RowArgument.Int32) ]

        match host with
        | Error (:? TypeLoadException as e) when e.Message.Contains "Missing" -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.TypeAbsent _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A row spelled `!!0`, a TypeSpec that is not a generic instantiation.
    [<Test>]
    let ``A row spelled as a method variable is refused`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.MethodVariable ]

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Unmodelled _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A row spelled `CLASS Fake`, a class of the image whose base type does not exist. The host
    /// refuses the row's shape (a TypeSpec here must be a generic instantiation) before reading
    /// `Fake`, and so does PawPrint.
    [<Test>]
    let ``A TypeSpec row naming a class is refused for its shape`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.ClassOverAbsentBase ]

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Unmodelled _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A row spelled `!!0` fails before a later absent interface is read, as the host fails on it.
    [<Test>]
    let ``A row spelled as a method variable fails before a later absent row`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.MethodVariable ; Row.Absent ]

        match host with
        | Error e when not (e.Message.Contains "IAbsent") -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.Unmodelled _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A TypeDef row naming the generic `I<T>` itself, with no instantiation.
    [<Test>]
    let ``A TypeDef row naming a generic interface is refused`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.GenericByTypeDef ]

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.UninstantiatedDefinition _)) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A row naming `System.Object`, which is not an interface.
    [<Test>]
    let ``A row naming a class is refused`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.Object ]

        match host with
        | Error (:? TypeLoadException) -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.Unmodelled _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// `J<!0>` in a type with no variables cannot be read, but `J`'s own parent `IAbsent` is absent,
    /// and the host reports that first.
    [<Test>]
    let ``An unreadable row's interface's absent parent is reported first`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.OverAbsentParentWithVar0 ]

        match host with
        | Error (:? TypeLoadException as e) when e.Message.Contains "IAbsent" -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.TypeAbsent _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// `CLASS K`, a non-generic interface spelled as a TypeSpec, is refused for its shape; `K` by its
    /// TypeDef token is the control, answered on both.
    [<TestCase true>]
    [<TestCase false>]
    let ``A non-generic interface is named by a token, not a TypeSpec`` (asTypeSpec : bool) : unit =
        let row =
            if asTypeSpec then
                Row.NonGenericAsTypeSpec
            else
                Row.NonGenericByTypeDef

        let host, pawPrint = rowsOutcome [] [ row ]

        if asTypeSpec then
            match host with
            | Error (:? TypeLoadException) -> ()
            | other -> failwith $"the host answered %A{other}"

            match pawPrint with
            | Error (TypeBindingFailure.Unmodelled _) -> ()
            | other -> failwith $"PawPrint answered %A{other}"
        else
            host |> Result.toOption |> shouldEqual (Some [| "K" |])

            match pawPrint with
            | Ok [ _ ] -> ()
            | other -> failwith $"PawPrint answered %A{other}"

    /// A row naming the class `F2`, which implements the absent `IAbsent`: the host loads `F2`,
    /// and fails on its interface, before it finds that `F2` is not an interface.
    [<Test>]
    let ``A row naming a class fails first on that class's own interfaces`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.ClassOverAbsentInterface ]

        match host with
        | Error (:? TypeLoadException as e) when e.Message.Contains "IAbsent" -> ()
        | other -> failwith $"the host answered %A{other}"

        match pawPrint with
        | Error (TypeBindingFailure.TypeAbsent _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// `L : M<int, int>` and `M<T> : L`: a cycle through a row with the wrong number of arguments.
    /// CoreCLR finds the cycle, a recursive load, before it reads any arity.
    [<Test>]
    let ``A cycle through an unreadable row is refused`` () : unit =
        let host, pawPrint = rowsOutcome [] [ Row.CycleThroughWrongArity ]

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Unmodelled unmodelled) when unmodelled.Diagnostic.Contains "IDS_CLASSLOAD_GENERAL" ->
            ()
        | other -> failwith $"PawPrint answered %A{other}"

    // ----- Hand-built type graphs -----

    /// A type as a hand-built image spells it, in a row, an extends clause or an argument.
    [<RequireQualifiedAccess>]
    type private Spelling =
        /// A type of the image, by name (with its arity suffix), with arguments if it is generic: by its
        /// TypeDef token as a row or base when it has none, by `GENERICINST` otherwise.
        | Named of name : string * arguments : Spelling list
        /// `!i`.
        | Var of index : int
        /// `<element>*`.
        | Pointer of element : Spelling
        /// `<element>[]`.
        | SzArray of element : Spelling
        | Int32
        | String
        /// `[CoreLib]System.Collections.Generic.List<_>`.
        | CoreLibList of element : Spelling
        /// `VALUETYPE [CoreLib]System.Missing`, which CoreLib does not declare.
        | MissingValueType
        /// `[CoreLib]System.IAbsent`, which CoreLib does not declare, by its TypeRef.
        | AbsentInterface
        /// `GENERICINST int32 1 int32`: an instantiation whose head is not a type token.
        | Int32HeadedInstantiation
        /// `[CoreLib]System.Int32` by its TypeRef, spelled `VALUETYPE` (`asValueType`) or `CLASS`.
        | CoreLibInt32 of asValueType : bool

    [<RequireQualifiedAccess>]
    type private SketchKind =
        | Interface
        | Class
        | Struct

    /// One type of a hand-built image. A class or struct with no `Base` extends `object` or
    /// `ValueType`.
    type private Sketch =
        {
            Name : string
            Kind : SketchKind
            Arity : int
            Base : Spelling option
            Rows : Spelling list
        }

    let private sketch (name : string) (kind : SketchKind) (arity : int) (rows : Spelling list) : Sketch =
        {
            Name = name
            Kind = kind
            Arity = arity
            Base = None
            Rows = rows
        }

    /// An image holding `sketches`, in that order.
    let private sketchImage (sketches : Sketch list) : byte[] =
        let metadata = Reflection.Metadata.Ecma335.MetadataBuilder ()
        let nil = Unchecked.defaultof<Reflection.Metadata.EntityHandle>

        metadata.AddModule (
            0,
            metadata.GetOrAddString "Sketch.dll",
            metadata.GetOrAddGuid (Guid ("6d0e2b7a-91c4-4f3e-8a55-0d2c7b1e4f86")),
            Unchecked.defaultof<_>,
            Unchecked.defaultof<_>
        )
        |> ignore<Reflection.Metadata.ModuleDefinitionHandle>

        metadata.AddAssembly (
            metadata.GetOrAddString "Sketch",
            Version (1, 0, 0, 0),
            Unchecked.defaultof<_>,
            Unchecked.defaultof<_>,
            enum<Reflection.AssemblyFlags> 0,
            Reflection.AssemblyHashAlgorithm.None
        )
        |> ignore<Reflection.Metadata.AssemblyDefinitionHandle>

        let coreLibName = hostCoreLib.GetName ()

        let coreLibRef =
            metadata.AddAssemblyReference (
                metadata.GetOrAddString coreLibName.Name,
                coreLibName.Version,
                Unchecked.defaultof<_>,
                metadata.GetOrAddBlob (coreLibName.GetPublicKeyToken ()),
                enum<Reflection.AssemblyFlags> 0,
                Unchecked.defaultof<_>
            )

        let coreLibType (ns : string) (name : string) : Reflection.Metadata.EntityHandle =
            metadata.AddTypeReference (
                Reflection.Metadata.AssemblyReferenceHandle.op_Implicit coreLibRef,
                metadata.GetOrAddString ns,
                metadata.GetOrAddString name
            )
            |> Reflection.Metadata.TypeReferenceHandle.op_Implicit

        let objectRef = coreLibType "System" "Object"
        let valueTypeRef = coreLibType "System" "ValueType"
        let listRef = coreLibType "System.Collections.Generic" "List`1"
        let missingRef = coreLibType "System" "Missing"
        let absentRef = coreLibType "System" "IAbsent"
        let int32Ref = coreLibType "System" "Int32"

        // `<Module>` is row 1, so the sketches are rows 2 onwards, in order.
        let handles =
            sketches
            |> List.mapi (fun i s ->
                s.Name, (Reflection.Metadata.Ecma335.MetadataTokens.TypeDefinitionHandle (i + 2), s)
            )
            |> Map.ofList

        let token (name : string) : Reflection.Metadata.EntityHandle * Sketch =
            match Map.tryFind name handles with
            | Some (handle, s) -> Reflection.Metadata.TypeDefinitionHandle.op_Implicit handle, s
            | None -> failwith $"the sketch has no type %s{name}"

        let codedIndex (handle : Reflection.Metadata.EntityHandle) : int =
            Reflection.Metadata.Ecma335.CodedIndex.TypeDefOrRef handle

        let kindByte (s : Sketch) : byte =
            match s.Kind with
            | SketchKind.Struct -> 0x11uy
            | SketchKind.Interface
            | SketchKind.Class -> 0x12uy

        let rec write (blob : Reflection.Metadata.BlobBuilder) (spelling : Spelling) : unit =
            match spelling with
            | Spelling.Named (name, []) ->
                let handle, s = token name
                blob.WriteByte (kindByte s)
                blob.WriteCompressedInteger (codedIndex handle)
            | Spelling.Named (name, arguments) ->
                let handle, s = token name
                blob.WriteByte 0x15uy
                blob.WriteByte (kindByte s)
                blob.WriteCompressedInteger (codedIndex handle)
                blob.WriteCompressedInteger arguments.Length

                for argument in arguments do
                    write blob argument
            | Spelling.Var index ->
                blob.WriteByte 0x13uy
                blob.WriteCompressedInteger index
            | Spelling.Pointer element ->
                blob.WriteByte 0x0Fuy
                write blob element
            | Spelling.SzArray element ->
                blob.WriteByte 0x1Duy
                write blob element
            | Spelling.Int32 -> blob.WriteByte 0x08uy
            | Spelling.String -> blob.WriteByte 0x0Euy
            | Spelling.CoreLibList element ->
                blob.WriteByte 0x15uy
                blob.WriteByte 0x12uy
                blob.WriteCompressedInteger (codedIndex listRef)
                blob.WriteCompressedInteger 1
                write blob element
            | Spelling.MissingValueType ->
                blob.WriteByte 0x11uy
                blob.WriteCompressedInteger (codedIndex missingRef)
            | Spelling.AbsentInterface ->
                blob.WriteByte 0x12uy
                blob.WriteCompressedInteger (codedIndex absentRef)
            | Spelling.Int32HeadedInstantiation ->
                blob.WriteByte 0x15uy
                blob.WriteByte 0x08uy
                blob.WriteCompressedInteger 1
                blob.WriteByte 0x08uy
            | Spelling.CoreLibInt32 asValueType ->
                blob.WriteByte (if asValueType then 0x11uy else 0x12uy)
                blob.WriteCompressedInteger (codedIndex int32Ref)

        // A row or base: a TypeDef or TypeRef token when the spelling has one, a TypeSpec otherwise.
        let typeDefOrRefOrSpec (spelling : Spelling) : Reflection.Metadata.EntityHandle =
            match spelling with
            | Spelling.Named (name, []) -> fst (token name)
            | Spelling.AbsentInterface -> absentRef
            | other ->
                let blob = Reflection.Metadata.BlobBuilder ()
                write blob other

                metadata.AddTypeSpecification (metadata.GetOrAddBlob blob)
                |> Reflection.Metadata.TypeSpecificationHandle.op_Implicit

        metadata.AddTypeDefinition (
            Reflection.TypeAttributes.NotPublic,
            Unchecked.defaultof<_>,
            metadata.GetOrAddString "<Module>",
            nil,
            Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle 1,
            Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle 1
        )
        |> ignore<Reflection.Metadata.TypeDefinitionHandle>

        for s in sketches do
            let attributes, baseType =
                match s.Kind with
                | SketchKind.Interface ->
                    Reflection.TypeAttributes.Public
                    ||| Reflection.TypeAttributes.Interface
                    ||| Reflection.TypeAttributes.Abstract,
                    nil
                | SketchKind.Class ->
                    Reflection.TypeAttributes.Public,
                    s.Base |> Option.map typeDefOrRefOrSpec |> Option.defaultValue objectRef
                | SketchKind.Struct ->
                    Reflection.TypeAttributes.Public
                    ||| Reflection.TypeAttributes.Sealed
                    ||| Reflection.TypeAttributes.SequentialLayout,
                    s.Base |> Option.map typeDefOrRefOrSpec |> Option.defaultValue valueTypeRef

            metadata.AddTypeDefinition (
                attributes,
                Unchecked.defaultof<_>,
                metadata.GetOrAddString s.Name,
                baseType,
                Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle 1,
                Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle 1
            )
            |> ignore<Reflection.Metadata.TypeDefinitionHandle>

        for s in sketches do
            let owner = fst (token s.Name)

            for row in s.Rows do
                metadata.AddInterfaceImplementation (
                    Reflection.Metadata.TypeDefinitionHandle.op_Explicit owner,
                    typeDefOrRefOrSpec row
                )
                |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        for s in sketches do
            for i in 0 .. s.Arity - 1 do
                metadata.AddGenericParameter (
                    fst (token s.Name),
                    Reflection.GenericParameterAttributes.None,
                    metadata.GetOrAddString $"T%d{i}",
                    i
                )
                |> ignore<Reflection.Metadata.GenericParameterHandle>

        let builder =
            Reflection.PortableExecutable.ManagedPEBuilder (
                Reflection.PortableExecutable.PEHeaderBuilder (
                    imageCharacteristics =
                        (Reflection.PortableExecutable.Characteristics.ExecutableImage
                         ||| Reflection.PortableExecutable.Characteristics.Dll)
                ),
                Reflection.Metadata.Ecma335.MetadataRootBuilder metadata,
                Reflection.Metadata.BlobBuilder ()
            )

        let output = Reflection.Metadata.BlobBuilder ()
        builder.Serialize output |> ignore<Reflection.Metadata.BlobContentId>
        output.ToArray ()

    /// What the host and PawPrint answer for the non-generic type `receiver` of `sketches`: the host's
    /// `GetInterfaces()`, or what loading the type raised; PawPrint's interface map, each entry as the
    /// host type it names over the same image; the bytes PawPrint's call allocated on this thread; and
    /// the bytes naming its entries allocated, each host type built once per handle.
    let private sketchOutcome
        (sketches : Sketch list)
        (receiver : string)
        : Result<Type[], exn> * Result<Type list, TypeBindingFailure> * int64 * int64
        =
        let image = sketchImage sketches

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("sketch", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let hostAnswer =
                try
                    host.GetType(receiver, true).GetInterfaces () |> Ok
                with e ->
                    Error e

            let state = coreState.WithLoadedAssembly dumped

            let state, handle =
                LoadedTypeInfo.typeInfoToTypeDefn'
                    bct
                    state.TypeSystem._LoadedAssemblies
                    (dumped.TypeDefs.Values |> Seq.find (fun typeInfo -> typeInfo.Name = receiver))
                |> IlMachineState.concretizeType
                    loggerFactory
                    bct
                    state
                    dumped.DefinitionFullName
                    Collections.Immutable.ImmutableArray.Empty
                    Collections.Immutable.ImmutableArray.Empty

            let before = GC.GetAllocatedBytesForCurrentThread ()

            let typeSystem, pawPrintAnswer =
                ConcreteInterfaceDispatch.interfaceMapOf
                    loggerFactory
                    state.DotnetRuntimeDirs
                    bct
                    "test"
                    state.TypeSystem
                    handle

            let allocated = GC.GetAllocatedBytesForCurrentThread () - before
            let namingBefore = GC.GetAllocatedBytesForCurrentThread ()

            // A shared type such as `Pair<A, A>` names one handle many times; each is built once.
            let built = Collections.Generic.Dictionary<ConcreteTypeHandle, Type> ()

            let rec hostTypeOf (handle : ConcreteTypeHandle) : Type =
                match built.TryGetValue handle with
                | true, ty -> ty
                | false, _ ->
                    let ty = buildHostType handle
                    built.[handle] <- ty
                    ty

            and buildHostType (handle : ConcreteTypeHandle) : Type =
                match handle with
                | ConcreteTypeHandle.Concrete _ ->
                    let ty =
                        AllConcreteTypes.lookup handle typeSystem.ConcreteTypes
                        |> Option.defaultWith (fun () -> failwith $"%O{handle} is not registered")

                    let assembly =
                        if ty.Identity.AssemblyFullName = dumped.DefinitionFullName then
                            host
                        else
                            hostCoreLib

                    let definition =
                        assembly.ManifestModule.ResolveType (
                            Reflection.Metadata.Ecma335.MetadataTokens.GetToken (
                                Reflection.Metadata.TypeDefinitionHandle.op_Implicit ty.Identity.TypeDefinition.Get
                                : Reflection.Metadata.EntityHandle
                            )
                        )

                    if ty.Generics.IsEmpty then
                        definition
                    else
                        definition.MakeGenericType (ty.Generics |> Seq.map hostTypeOf |> Array.ofSeq)
                | ConcreteTypeHandle.OneDimArrayZero element -> (hostTypeOf element).MakeArrayType ()
                | ConcreteTypeHandle.Pointer element -> (hostTypeOf element).MakePointerType ()
                | other -> failwith $"no host type is built for %O{other}"

            let named =
                pawPrintAnswer
                |> Result.map (List.map (fun entry -> hostTypeOf entry.Interface))

            let namingAllocated = GC.GetAllocatedBytesForCurrentThread () - namingBefore
            hostAnswer, named, allocated, namingAllocated
        finally
            context.Unload ()

    /// The host's and PawPrint's answers agree, interface for interface, in order. Types are compared
    /// as values, and named only to report a disagreement.
    let private agreeWithHost (host : Result<Type[], exn>) (pawPrint : Result<Type list, TypeBindingFailure>) : unit =
        match host, pawPrint with
        | Ok hostInterfaces, Ok interfaces when interfaces = List.ofArray hostInterfaces -> ()
        | Ok hostInterfaces, Ok interfaces ->
            let render (types : Type seq) =
                types |> Seq.map string |> String.concat ", "

            failwith $"the host answered [%s{render hostInterfaces}] and PawPrint [%s{render interfaces}]"
        | other -> failwith $"the host and PawPrint answered %A{other}"

    /// The refusal of an interface hierarchy that reaches a definition whose load is already in
    /// progress.
    let private isCycleRefusal (failure : TypeBindingFailure) : bool =
        match failure with
        | TypeBindingFailure.Unmodelled unmodelled -> unmodelled.Diagnostic.Contains "IDS_CLASSLOAD_GENERAL"
        | _ -> false

    /// `N0<A, B> : I<A>, I<B>`, and `Nk<A, B> : N(k-1)<Pair<A, A>, Pair<A, B>>` up to `depth`, under
    /// `C : N<depth><int, string>`. Every `I<_>` the bottom compares is an instantiation over contexts
    /// shared `depth` deep.
    let private pairChain (depth : int) : Sketch list =
        [
            yield sketch "I`1" SketchKind.Interface 1 []
            yield sketch "Pair`2" SketchKind.Class 2 []
            yield
                sketch
                    "N0`2"
                    SketchKind.Interface
                    2
                    [
                        Spelling.Named ("I`1", [ Spelling.Var 0 ])
                        Spelling.Named ("I`1", [ Spelling.Var 1 ])
                    ]
            for k in 1..depth do
                yield
                    sketch
                        $"N%d{k}`2"
                        SketchKind.Interface
                        2
                        [
                            Spelling.Named (
                                $"N%d{k - 1}`2",
                                [
                                    Spelling.Named ("Pair`2", [ Spelling.Var 0 ; Spelling.Var 0 ])
                                    Spelling.Named ("Pair`2", [ Spelling.Var 0 ; Spelling.Var 1 ])
                                ]
                            )
                        ]
            yield
                sketch "C" SketchKind.Class 0 [ Spelling.Named ($"N%d{depth}`2", [ Spelling.Int32 ; Spelling.String ]) ]
        ]

    /// The interface map of a sixteen-deep chain of shared contexts agrees with the host, and costs
    /// at most 32 times what an eight-deep one does: linear work gives about 2, exponential about 256.
    [<Test>]
    let ``A chain passing Pair<A, A> and Pair<A, B> on agrees with the host, in work linear in its depth`` () : unit =
        let warm, _, _, _ = sketchOutcome (pairChain 2) "C"
        warm |> Result.isOk |> shouldEqual true

        let host8, pawPrint8, allocated8, naming8 = sketchOutcome (pairChain 8) "C"
        agreeWithHost host8 pawPrint8

        let host16, pawPrint16, allocated16, naming16 = sketchOutcome (pairChain 16) "C"
        agreeWithHost host16 pawPrint16
        host16 |> Result.map Array.length |> shouldEqual (Ok 19)

        TestContext.Progress.WriteLine $"pair chain: %d{allocated8} bytes at depth 8, %d{allocated16} at depth 16"
        Assert.That (allocated16, Is.LessThanOrEqualTo (32L * allocated8))
        // The oracle naming the entries must not do the exponential work the test rules out.
        TestContext.Progress.WriteLine $"pair chain naming: %d{naming8} bytes at depth 8, %d{naming16} at depth 16"
        Assert.That (naming16, Is.LessThanOrEqualTo (8L * naming8))

    /// `L0<A> : I<A>`, `Lk<A> : L(k-1)<Pair<A, A>>` up to `depth`, the same for `R`, under
    /// `C : L<depth><int>, R<depth><int>`. Each interface's row names its one direct parent, so the
    /// image holds spellings of constant size, linear in the depth; the two chains' `I<_>` rows close
    /// to one handle, and their substitutions, equal and shared `depth` deep, are compared.
    let private pairDiamond (depth : int) : Sketch list =
        let chain (side : string) =
            [
                yield sketch $"%s{side}0`1" SketchKind.Interface 1 [ Spelling.Named ("I`1", [ Spelling.Var 0 ]) ]
                for k in 1..depth do
                    yield
                        sketch
                            $"%s{side}%d{k}`1"
                            SketchKind.Interface
                            1
                            [
                                Spelling.Named (
                                    $"%s{side}%d{k - 1}`1",
                                    [ Spelling.Named ("Pair`2", [ Spelling.Var 0 ; Spelling.Var 0 ]) ]
                                )
                            ]
            ]

        [
            yield sketch "I`1" SketchKind.Interface 1 []
            yield sketch "Pair`2" SketchKind.Class 2 []
            yield! chain "L"
            yield! chain "R"
            yield
                sketch
                    "C"
                    SketchKind.Class
                    0
                    [
                        Spelling.Named ($"L%d{depth}`1", [ Spelling.Int32 ])
                        Spelling.Named ($"R%d{depth}`1", [ Spelling.Int32 ])
                    ]
        ]

    /// The diamond of two chains of shared contexts agrees with the host, and costs at most 32 times
    /// at sixteen levels what it does at eight: the image is linear in its depth, so linear work gives
    /// about 2, and comparing the two equal substitutions unshared about 256.
    [<Test>]
    let ``A diamond of two chains passing Pair<A, A> on agrees with the host, in work linear in its depth`` () : unit =
        let warm, _, _, _ = sketchOutcome (pairDiamond 2) "C"
        warm |> Result.isOk |> shouldEqual true

        let host8, pawPrint8, allocated8, naming8 = sketchOutcome (pairDiamond 8) "C"
        agreeWithHost host8 pawPrint8

        let host16, pawPrint16, allocated16, naming16 = sketchOutcome (pairDiamond 16) "C"
        agreeWithHost host16 pawPrint16
        host16 |> Result.map Array.length |> shouldEqual (Ok 35)

        TestContext.Progress.WriteLine $"pair diamond: %d{allocated8} bytes at depth 8, %d{allocated16} at depth 16"
        Assert.That (allocated16, Is.LessThanOrEqualTo (32L * allocated8))
        TestContext.Progress.WriteLine $"pair diamond naming: %d{naming8} bytes at depth 8, %d{naming16} at depth 16"
        Assert.That (naming16, Is.LessThanOrEqualTo (8L * naming8))

    /// `C : I<A0>, ..., I<A(count - 1)>`, each `Ai` a class of the image.
    let private manyInstantiations (count : int) : Sketch list =
        [
            yield sketch "I`1" SketchKind.Interface 1 []
            for i in 0 .. count - 1 do
                yield sketch $"A%d{i}" SketchKind.Class 0 []
            yield
                sketch
                    "C"
                    SketchKind.Class
                    0
                    [
                        for i in 0 .. count - 1 do
                            Spelling.Named ("I`1", [ Spelling.Named ($"A%d{i}", []) ])
                    ]
        ]

    /// `n` instantiations of one interface make `n(n-1)/2` duplicate comparisons, as CoreCLR's
    /// `ExpandApproxInterface` makes them, so the work is quadratic: 128 cost at most 8 times what 64
    /// do, where quadratic work gives 4. What one comparison costs is #2335.
    [<Test>]
    let ``Many instantiations of one interface cost work quadratic in their number`` () : unit =
        let warm, _, _, _ = sketchOutcome (manyInstantiations 8) "C"
        warm |> Result.isOk |> shouldEqual true

        let host64, pawPrint64, allocated64, _ = sketchOutcome (manyInstantiations 64) "C"
        agreeWithHost host64 pawPrint64

        let host128, pawPrint128, allocated128, _ =
            sketchOutcome (manyInstantiations 128) "C"

        agreeWithHost host128 pawPrint128
        host128 |> Result.map Array.length |> shouldEqual (Ok 128)

        TestContext.Progress.WriteLine $"many instantiations: %d{allocated64} bytes at 64, %d{allocated128} at 128"

        Assert.That (allocated128, Is.LessThanOrEqualTo (8L * allocated64))

    /// `J0<T>`, and `Jk<T>` with two rows each giving its predecessor `J(k-1)` two arguments, under
    /// `C : J<depth><int>`. The image holds two rows a level; each row's arguments cannot be read, and
    /// its interface's own map is walked before it is deferred. Walking the same interface's map again
    /// for the second row of each level would double the work at every level.
    let private wrongArityLadder (depth : int) : Sketch list =
        [
            yield sketch "J0`1" SketchKind.Interface 1 []
            for k in 1..depth do
                let previous = Spelling.Named ($"J%d{k - 1}`1", [ Spelling.Var 0 ; Spelling.Var 0 ])
                yield sketch $"J%d{k}`1" SketchKind.Interface 1 [ previous ; previous ]
            yield sketch "C" SketchKind.Class 0 [ Spelling.Named ($"J%d{depth}`1", [ Spelling.Int32 ]) ]
        ]

    /// Both refuse the ladder; PawPrint's work at twelve levels is at most 8 times its work at six,
    /// where walking each interface once gives about 2 and walking it once a row about 64.
    [<Test>]
    let ``Rows that cannot be read walk each interface once`` () : unit =
        let _, _, _, _ = sketchOutcome (wrongArityLadder 2) "C"
        let host6, pawPrint6, allocated6, _ = sketchOutcome (wrongArityLadder 6) "C"
        let host12, pawPrint12, allocated12, _ = sketchOutcome (wrongArityLadder 12) "C"

        for host, pawPrint in [ host6, pawPrint6 ; host12, pawPrint12 ] do
            match host with
            | Error _ -> ()
            | Ok interfaces -> failwith $"the host answered %A{interfaces}"

            match pawPrint with
            | Error (TypeBindingFailure.Refused (TypeLoadRefusal.WrongArity _)) -> ()
            | other -> failwith $"PawPrint answered %A{other}"

        TestContext.Progress.WriteLine
            $"wrong-arity ladder: %d{allocated6} bytes at depth 6, %d{allocated12} at depth 12"

        Assert.That (allocated12, Is.LessThanOrEqualTo (8L * allocated6))

    /// A class `D : int32[]`, an extends clause naming no class.
    let private overArray : Sketch list =
        [
            { sketch "D" SketchKind.Class 0 [] with
                Base = Some (Spelling.SzArray Spelling.Int32)
            }
        ]

    /// A row naming `D`, whose extends clause is `int32[]`: loading the row's head refuses the clause,
    /// as the host refuses `C`.
    [<Test>]
    let ``A row naming a class whose extends clause names no class is refused`` () : unit =
        let host, pawPrint, _, _ =
            sketchOutcome (overArray @ [ sketch "C" SketchKind.Class 0 [ Spelling.Named ("D", []) ] ]) "C"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Unmodelled _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// The same clause through `interfaceMapOfDefinition` on `D`. Other extends shapes CoreCLR refuses, a
    /// non-generic class spelled as a bare TypeSpec or a value type as a base, are #2336.
    [<Test>]
    let ``A definition's map refuses an extends clause naming no class`` () : unit =
        let image = sketchImage overArray

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let state = coreState.WithLoadedAssembly dumped

        let identity =
            let handle, _ =
                dumped.TypeDefs
                |> Seq.map (fun (KeyValue (handle, typeInfo)) -> handle, typeInfo)
                |> Seq.find (fun (_, typeInfo) -> typeInfo.Name = "D")

            ResolvedTypeIdentity.ofDefinitionInAssembly dumped.DefinitionFullName handle

        let _, result =
            ConcreteInterfaceDispatch.interfaceMapOfDefinition
                loggerFactory
                state.DotnetRuntimeDirs
                bct
                "test"
                state.TypeSystem
                identity

        match result with
        | Error (TypeBindingFailure.Unmodelled _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// Shapes whose interface hierarchy reaches a definition already being loaded, which CoreCLR
    /// refuses as a recursive load (`IDS_CLASSLOAD_GENERAL`). A row naming a class whose base chain
    /// is a cycle (`K : K2`, `K2 : K`) is not here: the Loader's base-chain loading, which reading the
    /// row's token runs before the walk sees the row, follows that chain without end (#2330).
    let private cycles : (string * Sketch list) list =
        [
            "I<T> : I<T*>",
            [
                sketch "I`1" SketchKind.Interface 1 [ Spelling.Named ("I`1", [ Spelling.Pointer (Spelling.Var 0) ]) ]
                sketch "C" SketchKind.Class 0 [ Spelling.Named ("I`1", [ Spelling.Int32 ]) ]
            ]
            "I<T> : I<List<T>>",
            [
                sketch
                    "I`1"
                    SketchKind.Interface
                    1
                    [ Spelling.Named ("I`1", [ Spelling.CoreLibList (Spelling.Var 0) ]) ]
                sketch "C" SketchKind.Class 0 [ Spelling.Named ("I`1", [ Spelling.Int32 ]) ]
            ]
            "I<T> : J<T[]>, J<T> : I<T>",
            [
                sketch "I`1" SketchKind.Interface 1 [ Spelling.Named ("J`1", [ Spelling.SzArray (Spelling.Var 0) ]) ]
                sketch "J`1" SketchKind.Interface 1 [ Spelling.Named ("I`1", [ Spelling.Var 0 ]) ]
                sketch "C" SketchKind.Class 0 [ Spelling.Named ("I`1", [ Spelling.Int32 ]) ]
            ]
            "I<T> : J<T>, J<T> : I<T>",
            [
                sketch "I`1" SketchKind.Interface 1 [ Spelling.Named ("J`1", [ Spelling.Var 0 ]) ]
                sketch "J`1" SketchKind.Interface 1 [ Spelling.Named ("I`1", [ Spelling.Var 0 ]) ]
                sketch "C" SketchKind.Class 0 [ Spelling.Named ("I`1", [ Spelling.Int32 ]) ]
            ]
        ]

    /// Each must end, and in a refusal, where the host refuses too. Run with a time limit outside the
    /// test host when the walk is suspect: NUnit cannot stop a loop, and a recursion overflows the host's
    /// stack.
    let private cycleNames : string list = cycles |> List.map fst

    [<TestCaseSource(nameof cycleNames)>]
    let ``An interface hierarchy that reaches itself is refused`` (name : string) : unit =
        let sketches = cycles |> List.find (fun (n, _) -> n = name) |> snd
        let host, pawPrint, _, _ = sketchOutcome sketches "C"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error failure when isCycleRefusal failure -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// Shapes that reach a definition more than once without reaching it from itself, each answered
    /// as the host answers it.
    let private notCycles : (string * Sketch list * string) list =
        [
            "a diamond, C : L, R over I",
            [
                sketch "I" SketchKind.Interface 0 []
                sketch "L" SketchKind.Interface 0 [ Spelling.Named ("I", []) ]
                sketch "R" SketchKind.Interface 0 [ Spelling.Named ("I", []) ]
                sketch "C" SketchKind.Class 0 [ Spelling.Named ("L", []) ; Spelling.Named ("R", []) ]
            ],
            "C"
            "struct V : I<V>",
            [
                sketch "I`1" SketchKind.Interface 1 []
                sketch "V" SketchKind.Struct 0 [ Spelling.Named ("I`1", [ Spelling.Named ("V", []) ]) ]
            ],
            "V"
            "class C : I<C>",
            [
                sketch "I`1" SketchKind.Interface 1 []
                sketch "C" SketchKind.Class 0 [ Spelling.Named ("I`1", [ Spelling.Named ("C", []) ]) ]
            ],
            "C"
            "I<T> : J<I<T>>",
            [
                sketch "J`1" SketchKind.Interface 1 []
                sketch
                    "I`1"
                    SketchKind.Interface
                    1
                    [ Spelling.Named ("J`1", [ Spelling.Named ("I`1", [ Spelling.Var 0 ]) ]) ]
                sketch "C" SketchKind.Class 0 [ Spelling.Named ("I`1", [ Spelling.Int32 ]) ]
            ],
            "C"
        ]

    let private notCycleNames : string list = notCycles |> List.map (fun (n, _, _) -> n)

    [<TestCaseSource(nameof notCycleNames)>]
    let ``A hierarchy that reaches a definition twice without a cycle is answered`` (name : string) : unit =
        let _, sketches, receiver = notCycles |> List.find (fun (n, _, _) -> n = name)
        let host, pawPrint, _, _ = sketchOutcome sketches receiver
        agreeWithHost host pawPrint

    /// `GENERICINST int32 1 int32`, a TypeSpec whose head is not a type token, alone and before a
    /// later absent row: refused for its shape, before any later row is read.
    [<TestCase false>]
    [<TestCase true>]
    let ``A TypeSpec row whose head is not a type token is refused`` (laterAbsent : bool) : unit =
        let rows =
            [
                yield Spelling.Int32HeadedInstantiation
                if laterAbsent then
                    yield Spelling.AbsentInterface
            ]

        let host, pawPrint, _, _ = sketchOutcome [ sketch "C" SketchKind.Class 0 rows ] "C"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Unmodelled _) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A one-parameter class `G`, and `BadHead : G<int, int>`.
    let private badHead : Sketch list =
        [
            sketch "G`1" SketchKind.Class 1 []
            { sketch "BadHead" SketchKind.Class 0 [] with
                Base = Some (Spelling.Named ("G`1", [ Spelling.Int32 ; Spelling.Int32 ]))
            }
        ]

    /// A row naming `BadHead`, whose extends clause gives `G` two arguments: loading the row's head
    /// fails on that clause, as the host fails.
    [<Test>]
    let ``A row naming a class whose extends clause has the wrong arity is refused`` () : unit =
        let host, pawPrint, _, _ =
            sketchOutcome (badHead @ [ sketch "C" SketchKind.Class 0 [ Spelling.Named ("BadHead", []) ] ]) "C"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.WrongArity _)) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// The same extends clause through `interfaceMapOfDefinition` on `BadHead` itself.
    [<Test>]
    let ``A definition's map refuses an extends clause with the wrong arity`` () : unit =
        let image = sketchImage badHead

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let state = coreState.WithLoadedAssembly dumped

        let identity =
            let handle, _ =
                dumped.TypeDefs
                |> Seq.map (fun (KeyValue (handle, typeInfo)) -> handle, typeInfo)
                |> Seq.find (fun (_, typeInfo) -> typeInfo.Name = "BadHead")

            ResolvedTypeIdentity.ofDefinitionInAssembly dumped.DefinitionFullName handle

        let _, result =
            ConcreteInterfaceDispatch.interfaceMapOfDefinition
                loggerFactory
                state.DotnetRuntimeDirs
                bct
                "test"
                state.TypeSystem
                identity

        match result with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.WrongArity _)) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// `K : K2` and `K2 : K`, through `interfaceMapOfDefinition` on `K`, which reads each extends
    /// clause without loading the chain it names: the walk reaches `K` again while loading it.
    [<Test>]
    let ``A definition's map refuses a base chain that reaches itself`` () : unit =
        let image =
            sketchImage
                [
                    { sketch "K" SketchKind.Class 0 [] with
                        Base = Some (Spelling.Named ("K2", []))
                    }
                    { sketch "K2" SketchKind.Class 0 [] with
                        Base = Some (Spelling.Named ("K", []))
                    }
                ]

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let state = coreState.WithLoadedAssembly dumped

        let identity =
            let handle, _ =
                dumped.TypeDefs
                |> Seq.map (fun (KeyValue (handle, typeInfo)) -> handle, typeInfo)
                |> Seq.find (fun (_, typeInfo) -> typeInfo.Name = "K")

            ResolvedTypeIdentity.ofDefinitionInAssembly dumped.DefinitionFullName handle

        let _, result =
            ConcreteInterfaceDispatch.interfaceMapOfDefinition
                loggerFactory
                state.DotnetRuntimeDirs
                bct
                "test"
                state.TypeSystem
                identity

        match result with
        | Error failure when isCycleRefusal failure -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// A row naming a class `G` at an instantiation CoreCLR's approximate load refuses before it
    /// finds `G` is not an interface: the host names that earlier failure, PawPrint refuses the row as
    /// not an interface. Which refusal is named is #2329; these pin that both refuse.
    [<TestCase false>]
    [<TestCase true>]
    let ``A row naming a class at a bad instantiation is refused`` (missingArgument : bool) : unit =
        let row =
            if missingArgument then
                Spelling.Named ("G`1", [ Spelling.MissingValueType ])
            else
                Spelling.Named ("G`1", [ Spelling.Int32 ; Spelling.Int32 ])

        let host, pawPrint, _, _ =
            sketchOutcome [ sketch "G`1" SketchKind.Class 1 [] ; sketch "C" SketchKind.Class 0 [ row ] ] "C"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error _ -> ()
        | Ok entries -> failwith $"PawPrint answered %A{entries}"

    // ----- A generic definition -----

    /// What the host's `GetInterfaces()` answers for the generic definition `receiver` of `sketches`,
    /// and what `ask` answers given the image loaded, the definition, and its own variables, which is
    /// how the QCall asks about it.
    let private withSketchDefinition<'a>
        (sketches : Sketch list)
        (receiver : string)
        (ask :
            IlMachineState
                -> ResolvedTypeIdentity
                -> Collections.Immutable.ImmutableArray<RuntimeTypeHandleTarget>
                -> 'a)
        : Result<Type[], exn> * 'a
        =
        let image = sketchImage sketches

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("sketchDefinition", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let hostAnswer =
                try
                    host.GetType(receiver, true).GetInterfaces () |> Ok
                with e ->
                    Error e

            let typeInfo =
                dumped.TypeDefs.Values |> Seq.find (fun typeInfo -> typeInfo.Name = receiver)

            let arguments =
                Seq.init
                    typeInfo.Generics.Length
                    (fun i -> RuntimeTypeHandleTarget.GenericParameter (typeInfo.Identity, i))
                |> Collections.Immutable.ImmutableArray.CreateRange

            hostAnswer, ask (coreState.WithLoadedAssembly dumped) typeInfo.Identity arguments
        finally
            context.Unload ()

    /// What the host's `GetInterfaces()` and `ofInstantiation` answer for the generic definition
    /// `receiver` of `sketches`, the latter at the definition's own variables.
    let private sketchDefinitionOutcome
        (sketches : Sketch list)
        (receiver : string)
        : Result<Type[], exn> * Result<RuntimeTypeHandleTarget list, TypeBindingFailure>
        =
        withSketchDefinition
            sketches
            receiver
            (fun state identity arguments ->
                snd (RuntimeTypeInterfaces.ofInstantiation loggerFactory bct "test" state identity arguments)
            )

    /// What the host's `GetInterfaces()` and the QCall's `ofReceiver` answer for the generic definition
    /// `receiver` of `sketches`: PawPrint's interfaces, or why it stopped.
    let private sketchDefinitionReceiver
        (sketches : Sketch list)
        (receiver : string)
        : Result<Type[], exn> * Result<RuntimeTypeHandleTarget list, string>
        =
        withSketchDefinition
            sketches
            receiver
            (fun state identity _ ->
                try
                    RuntimeTypeInterfaces.ofReceiver
                        loggerFactory
                        bct
                        "test"
                        state
                        (RuntimeTypeHandleTarget.OpenGenericTypeDefinition identity)
                    |> snd
                    |> Ok
                with e ->
                    Error e.Message
            )

    /// An open receiver's base chain is loaded, as a closed one's is, though its base adds no entry:
    /// `C<T> : B<IAbsent>`, where `B<U>` has no interfaces and CoreLib declares no `IAbsent`. The host
    /// refuses to load `C<>`.
    [<Test>]
    let ``An open receiver's base whose argument is absent is refused`` () : unit =
        let host, pawPrint =
            sketchDefinitionReceiver
                [
                    sketch "B`1" SketchKind.Class 1 []
                    { sketch "C`1" SketchKind.Class 1 [] with
                        Base = Some (Spelling.Named ("B`1", [ Spelling.AbsentInterface ]))
                    }
                ]
                "C`1"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error message -> message |> shouldContainText "IAbsent"
        | Ok interfaces -> failwith $"PawPrint answered %A{interfaces}"

    /// Every row of an open receiver's map is loaded as it is spelled, as a closed receiver's is,
    /// though it adds no entry: `C<T>`'s `I<class Int32>` names `B`'s `I<valuetype Int32>` again, and
    /// only loading it finds the wrong kind. The host refuses to load `C<>`.
    [<Test>]
    let ``An open receiver's row that adds no entry is still loaded`` () : unit =
        let host, pawPrint =
            sketchDefinitionReceiver
                [
                    sketch "I`1" SketchKind.Interface 1 []
                    sketch "B" SketchKind.Class 0 [ Spelling.Named ("I`1", [ Spelling.CoreLibInt32 true ]) ]
                    { sketch "C`1" SketchKind.Class 1 [ Spelling.Named ("I`1", [ Spelling.CoreLibInt32 false ]) ] with
                        Base = Some (Spelling.Named ("B", []))
                    }
                ]
                "C`1"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error message -> message |> shouldContainText "Int32"
        | Ok interfaces -> failwith $"PawPrint answered %A{interfaces}"

    /// An open receiver's answer is its definition's map, which cannot compare a row whose arguments
    /// it cannot read with the others: a variable the definition does not have, or the wrong number of
    /// arguments. The host refuses to load the definition.
    [<TestCase false>]
    [<TestCase true>]
    let ``An open receiver's row whose arguments cannot be read is refused`` (wrongArity : bool) : unit =
        let row =
            if wrongArity then
                Spelling.Named ("I`1", [ Spelling.Var 0 ; Spelling.Var 0 ])
            else
                Spelling.Named ("I`1", [ Spelling.Var 1 ])

        let host, pawPrint =
            sketchDefinitionOutcome
                [
                    sketch "I`1" SketchKind.Interface 1 []
                    sketch "G`1" SketchKind.Class 1 [ row ]
                ]
                "G`1"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint, wrongArity with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.WrongArity _)), true
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.TypeParameterOutOfScope _)), false -> ()
        | other, _ -> failwith $"PawPrint answered %A{other}"

    /// An open receiver whose definition's extends clause names a variable the definition does not
    /// have. This is one malformed extends clause the definition's map refuses; it does not refuse
    /// every one CoreCLR refuses (#2336).
    [<Test>]
    let ``An open receiver whose extends clause names a variable it lacks is refused`` () : unit =
        let host, pawPrint =
            sketchDefinitionOutcome
                [
                    sketch "B`1" SketchKind.Class 1 []
                    { sketch "G`1" SketchKind.Class 1 [] with
                        Base = Some (Spelling.Named ("B`1", [ Spelling.Var 1 ]))
                    }
                ]
                "G`1"

        match host with
        | Error _ -> ()
        | Ok interfaces -> failwith $"the host answered %A{interfaces}"

        match pawPrint with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.TypeParameterOutOfScope _)) -> ()
        | other -> failwith $"PawPrint answered %A{other}"

    /// `ofInstantiation` of `receiver` in `pairChain depth`, at its own variables if it is generic:
    /// the host's count of its interfaces, how many PawPrint answered, and the bytes the call allocated
    /// on this thread, measured after a first call has registered every type it closes.
    let private pairChainConversion (depth : int) (receiver : string) : int * int * int64 =
        let image = sketchImage (pairChain depth)

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("pairChainConversion", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let hostCount = host.GetType(receiver, true).GetInterfaces().Length

            let typeInfo =
                dumped.TypeDefs.Values |> Seq.find (fun typeInfo -> typeInfo.Name = receiver)

            let arguments =
                Seq.init
                    typeInfo.Generics.Length
                    (fun i -> RuntimeTypeHandleTarget.GenericParameter (typeInfo.Identity, i))
                |> Collections.Immutable.ImmutableArray.CreateRange

            let convert (state : IlMachineState) =
                match
                    RuntimeTypeInterfaces.ofInstantiation loggerFactory bct "test" state typeInfo.Identity arguments
                with
                | state, Ok targets -> state, targets
                | _, Error failure -> failwith $"PawPrint refused: %s{TypeBindingFailure.describe failure}"

            let state, _ = convert (coreState.WithLoadedAssembly dumped)
            let before = GC.GetAllocatedBytesForCurrentThread ()
            let _, targets = convert state
            let allocated = GC.GetAllocatedBytesForCurrentThread () - before
            hostCount, targets.Length, allocated
        finally
            context.Unload ()

    /// Converting a definition's map to targets converts each context the map shares once. The chain's
    /// image holds one row per interface, each of constant size, so what it holds is linear in its
    /// depth, and so is the work: sixteen levels cost at most 32 times what eight do, where converting
    /// each occurrence of a shared context afresh gives about 256. The closed `C` takes the path where
    /// every argument is closed; the definition `N<depth>` the path over its own variables.
    [<TestCase "C">]
    [<TestCase "N">]
    let ``Converting a chain passing Pair<A, A> and Pair<A, B> on costs work linear in its depth``
        (receiver : string)
        : unit
        =
        let name (depth : int) =
            if receiver = "C" then "C" else $"N%d{depth}`2"

        let host8, pawPrint8, allocated8 = pairChainConversion 8 (name 8)
        pawPrint8 |> shouldEqual host8
        let host16, pawPrint16, allocated16 = pairChainConversion 16 (name 16)
        pawPrint16 |> shouldEqual host16

        TestContext.Progress.WriteLine $"%s{receiver}: %d{allocated8} bytes at depth 8, %d{allocated16} at depth 16"

        Assert.That (allocated16, Is.LessThanOrEqualTo (32L * allocated8))

    /// A guest whose one assembly is `program`, which returns 0 where the real runtime answers as it
    /// expects; PawPrint must stop where managed code asks `GetUtf8NameInternal` for the name of an
    /// open construction (#2315).
    let private refusesOpenConstructionName (name : string) (program : string) : unit =
        {
            Assemblies = [ CrossAssemblySpec.entryPoint name [] [ program ] ]
            EntryAssemblyName = name
            ExpectedReturnCode = 0
        }
        |> CrossAssemblyHarness.runTestExpectingRefusal
            [
                "TODO"
                "open constructed types are not handled"
                "NativeRuntimeTypeFCall"
                "GetUtf8NameInternal"
            ]
            []
            []

    /// The QCall answers an array over a variable with `System.Array`'s map; managed code then names
    /// each SZ-array interface it appends, `IList<T>` first (#2315).
    [<Test>]
    let ``An array over a variable stops at the name of an open construction`` () : unit =
        """
using System;
using System.Collections;
using System.Collections.Generic;

public static class Program
{
    public static int Main()
    {
        Type t = typeof(List<>).GetGenericArguments()[0];
        Type[] interfaces = t.MakeArrayType().GetInterfaces();
        Type[] expected =
        {
            typeof(ICloneable), typeof(IList), typeof(ICollection), typeof(IEnumerable),
            typeof(IStructuralComparable), typeof(IStructuralEquatable),
            typeof(IList<>).MakeGenericType(t), typeof(ICollection<>).MakeGenericType(t),
            typeof(IEnumerable<>).MakeGenericType(t), typeof(IReadOnlyList<>).MakeGenericType(t),
            typeof(IReadOnlyCollection<>).MakeGenericType(t),
        };
        if (interfaces.Length != expected.Length) return 1;
        for (int i = 0; i < expected.Length; i++)
        {
            if (!ReferenceEquals(interfaces[i], expected[i])) return 2 + i;
        }
        return 0;
    }
}
"""
        |> refusesOpenConstructionName "ArrayOverVariable"

    /// `GetInterface(name)` names every interface the receiver has, so on an open receiver it names
    /// an open construction (#2315).
    [<Test>]
    let ``GetInterface by name on a definition stops at the name of an open construction`` () : unit =
        """
using System;
using System.Collections.Generic;

public static class Program
{
    public static int Main()
    {
        Type t = typeof(List<>).GetGenericArguments()[0];
        Type found = typeof(List<>).GetInterface("IList`1");
        if (!ReferenceEquals(found, typeof(IList<>).MakeGenericType(t))) return 1;
        return 0;
    }
}
"""
        |> refusesOpenConstructionName "GetInterfaceByNameOnDefinition"

    /// A definition whose base classes have no interfaces answers its own closed rows, each followed
    /// by its parents, whether its base is `object`, a closed class, or an open construction over its
    /// own variable.
    [<Test>]
    let ``A generic definition whose base classes have no interfaces is answered`` () : unit =
        {
            Assemblies =
                [
                    CrossAssemblySpec.entryPoint
                        "DefinitionOverInterfacelessBase"
                        []
                        [
                            """
using System;

public interface IB { }
public interface IC : IB { }
public class EmptyClosed { }
public class EmptyOpen<T> { }
public class OverObject<T> : IDisposable, IB { public void Dispose() { } }
public class OverEmptyClosed<T> : EmptyClosed, IDisposable { public void Dispose() { } }
public class OverEmptyOpen<T> : EmptyOpen<T>, IB, IDisposable { public void Dispose() { } }
public class OverObjectWithParents<T> : IC, IDisposable { public void Dispose() { } }

public static class Program
{
    public static int Main()
    {
        Type[] a = typeof(OverObject<>).GetInterfaces();
        if (a.Length != 2 || a[0] != typeof(IDisposable) || a[1] != typeof(IB)) return 1;
        Type[] b = typeof(OverEmptyClosed<>).GetInterfaces();
        if (b.Length != 1 || b[0] != typeof(IDisposable)) return 2;
        Type[] c = typeof(OverEmptyOpen<>).GetInterfaces();
        if (c.Length != 2 || c[0] != typeof(IB) || c[1] != typeof(IDisposable)) return 3;
        Type[] d = typeof(OverObjectWithParents<>).GetInterfaces();
        if (d.Length != 3 || d[0] != typeof(IC) || d[1] != typeof(IB) || d[2] != typeof(IDisposable)) return 4;
        return 0;
    }
}
"""
                        ]
                ]
            EntryAssemblyName = "DefinitionOverInterfacelessBase"
            ExpectedReturnCode = 0
        }
        |> CrossAssemblyHarness.runTest
