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
/// interfaces. Three corpora go through it: CoreLib's exported closed types, interfaces included;
/// generated hierarchies; and shapes C# refuses to compile, built from a library in two versions,
/// which are the only ones where a declared interface's parents are missing from the type's own
/// rows.
///
/// A generic definition whose base class has interfaces is refused (#2309), and its tests assert
/// what the real runtime answers.
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
    let private compareReceiver
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

    /// The closed receivers asked about: each declaration at all-`int`, all-`string`, and with the
    /// two mixed.
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
                [
                    List.replicate n typeof<int>
                    List.replicate n typeof<string>
                    List.init n (fun i -> if i % 2 = 0 then typeof<int> else typeof<string>)
                    List.init n (fun i -> if i % 2 = 0 then typeof<string> else typeof<int>)
                ]
                |> List.distinct
                |> List.map (fun arguments -> definition.MakeGenericType (Array.ofList arguments))
        )

    /// What a generated receiver reached, read from the host's side alone.
    type private Label =
        | Duplicate
        | DeclaresOverInterfacefulBase
        | ValueTypeReceiver
        | InterfaceReceiver
        | NestedReceiver

    let private labels (images : HostTypeTarget.Images) (receiver : Type) : Label list =
        if receiver.GetInterfaces().Length = 0 then
            []
        else
            [
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

        // About half of what the fixed sample reaches.
        for label, floor in
            [
                Label.Duplicate, 68
                Label.DeclaresOverInterfacefulBase, 190
                Label.ValueTypeReceiver, 65
                Label.InterfaceReceiver, 210
                Label.NestedReceiver, 100
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

    /// Each closed receiver of the classes, and one generic definition, from the classes' host
    /// assembly.
    let private expansionReceivers : (string * Type list) list =
        [
            "C`1", [ typeof<int> ]
            "C`1", [ typeof<string> ]
            "CE`1", [ typeof<int> ]
            "S`1", [ typeof<int> ]
            "IF`1", [ typeof<int> ]
            "Der`1", [ typeof<int> ]
            "CClosed", []
            // A generic definition whose rows are all closed, answered by the definition arm.
            "CClosedG`1", []
            "C2`1", [ typeof<int> ]
            "Der2`1", [ typeof<int> ]
            "D2`2", [ typeof<int> ; typeof<string> ]
            "S2`1", [ typeof<int> ]
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

    // ----- A re-declared interface whose row does not load -----

    /// An image with `I<T>`, `B : I<int>` and `C : B, I<int>`, where `C`'s row spells its `I<int>`
    /// as `GENERICINST VALUETYPE` (`asValueType`) or, correctly, as `GENERICINST CLASS`. C# never
    /// writes the first.
    let private respelledInterfaceImage (asValueType : bool) : byte[] =
        let metadata = Reflection.Metadata.Ecma335.MetadataBuilder ()
        let nil = Unchecked.defaultof<Reflection.Metadata.EntityHandle>

        let definition (handle : Reflection.Metadata.TypeDefinitionHandle) : Reflection.Metadata.EntityHandle =
            Reflection.Metadata.TypeDefinitionHandle.op_Implicit handle

        metadata.AddModule (
            0,
            metadata.GetOrAddString "RespelledRow.dll",
            metadata.GetOrAddGuid (Guid ("0f5b8c2a-3d61-4e7b-9a40-5c7e2d18b963")),
            Unchecked.defaultof<_>,
            Unchecked.defaultof<_>
        )
        |> ignore<Reflection.Metadata.ModuleDefinitionHandle>

        metadata.AddAssembly (
            metadata.GetOrAddString "RespelledRow",
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

        let baseClass = addType "B" Reflection.TypeAttributes.Public objectRef
        let receiver = addType "C" Reflection.TypeAttributes.Public (definition baseClass)

        // `GENERICINST <CLASS or VALUETYPE> I 1 int32`
        let instantiation (kind : byte) =
            let blob = Reflection.Metadata.BlobBuilder ()
            blob.WriteByte 0x15uy
            blob.WriteByte kind
            blob.WriteCompressedInteger (Reflection.Metadata.Ecma335.CodedIndex.TypeDefOrRef (definition iface))
            blob.WriteCompressedInteger 1
            blob.WriteByte 0x08uy

            metadata.AddTypeSpecification (metadata.GetOrAddBlob blob)
            |> fun handle ->
                (Reflection.Metadata.TypeSpecificationHandle.op_Implicit handle : Reflection.Metadata.EntityHandle)

        metadata.AddInterfaceImplementation (baseClass, instantiation 0x12uy)
        |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        metadata.AddInterfaceImplementation (receiver, instantiation (if asValueType then 0x11uy else 0x12uy))
        |> ignore<Reflection.Metadata.InterfaceImplementationHandle>

        metadata.AddGenericParameter (
            definition iface,
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

    /// `C`'s row names the instantiation `B` already put in the map, so it adds no entry; but the
    /// runtime loads every row's interface as the row spells it, so a row that does not load fails
    /// the type all the same.
    [<TestCase true>]
    [<TestCase false>]
    let ``A re-declared interface's own row is loaded`` (asValueType : bool) : unit =
        let image = respelledInterfaceImage asValueType

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context = AssemblyLoadContext ("respelledRow", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let hostAnswer =
                try
                    host.GetType("C", true).GetInterfaces () |> Array.map _.ToString() |> Ok
                with :? TypeLoadException as e ->
                    Error e.Message

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

            if asValueType then
                match hostAnswer with
                | Error _ -> ()
                | Ok interfaces -> failwith $"the host answered %A{interfaces}"

                match pawPrintAnswer with
                | Error (TypeBindingFailure.Refused (TypeLoadRefusal.KindMismatch _)) -> ()
                | other -> failwith $"PawPrint answered %A{other}"
            else
                hostAnswer |> shouldEqual (Ok [| "I`1[System.Int32]" |])

                match pawPrintAnswer with
                | Ok [ entry ] -> entry.Definition.DeclaredOnType |> shouldEqual true
                | other -> failwith $"PawPrint answered %A{other}"
        finally
            context.Unload ()

    // ----- A generic definition -----

    /// A guest whose one assembly is `program`, which returns 0 where the real runtime answers as it
    /// expects; PawPrint must refuse at the definition arm's `TODO`.
    let private refusesDefinitionOverInterfacefulBase (name : string) (program : string) : unit =
        {
            Assemblies = [ CrossAssemblySpec.entryPoint name [] [ program ] ]
            EntryAssemblyName = name
            ExpectedReturnCode = 0
        }
        |> CrossAssemblyHarness.runTestExpectingRefusal [ "TODO" ; "RuntimeTypeHandle.GetInterfaces" ; "#2309" ] [] []

    /// Every generic delegate's base class, `MulticastDelegate`, has interfaces.
    [<Test>]
    let ``A generic delegate definition is refused`` () : unit =
        """
using System;
using System.Runtime.Serialization;

public static class Program
{
    public static int Main()
    {
        Type[] interfaces = typeof(Action<>).GetInterfaces();
        if (interfaces.Length != 2) return 1;
        if (interfaces[0] != typeof(ICloneable)) return 2;
        if (interfaces[1] != typeof(ISerializable)) return 3;
        return 0;
    }
}
"""
        |> refusesDefinitionOverInterfacefulBase "DefinitionOverDelegate"

    [<Test>]
    let ``A generic definition over a closed base with interfaces is refused`` () : unit =
        """
using System;

public interface IB { }
public class ClosedBase : IB { }
public class OverClosed<T> : ClosedBase, IDisposable { public void Dispose() { } }

public static class Program
{
    public static int Main()
    {
        Type[] interfaces = typeof(OverClosed<>).GetInterfaces();
        if (interfaces.Length != 2) return 1;
        if (interfaces[0] != typeof(IB)) return 2;
        if (interfaces[1] != typeof(IDisposable)) return 3;
        return 0;
    }
}
"""
        |> refusesDefinitionOverInterfacefulBase "DefinitionOverClosedBase"

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
