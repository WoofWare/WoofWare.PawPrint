namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Reflection
open System.Reflection.Metadata.Ecma335
open System.Runtime.Loader
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.InterfaceCorpusGenerator

/// `Type.GetInterfaceMap` names, for each method of an interface a type implements, the method
/// CoreCLR's `RuntimeTypeHandle_GetInterfaceMethodImplementation` answers: the walk a call takes
/// (`MethodTable::FindDispatchSlotForInterfaceMD`, or `MethodTable::ResolveVirtualStaticMethod` for a
/// static virtual), differing from a call's only in what it does at a conflict. The host's
/// `GetInterfaceMap` is the oracle here, over images both sides read.
///
/// The characterisation property drives PawPrint's call path, `ConcreteVirtualDispatch.tryResolveVirtualImplementation`
/// and `StaticVirtualDispatch.resolve`, and states where it agrees with the host's map: a target that is
/// not an interface's abstract method is the method the call finds; an interface's abstract target is
/// the reabstraction the call finds; a null target is no method found; and a map that throws is a
/// conflict. Three corpora: CoreLib's exported types; generated libraries
/// (`InterfaceCorpusGenerator.genImplementingCorpus`); and shapes C# refuses to compile, built as a
/// library in two versions, the classes compiled against the first and loaded beside the second, each
/// with the one answer the call path gives.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestInterfaceMethodImplementation =

    // The factory is intentionally undisposed: the DumpedAssembly loggers close over its sinks and
    // outlive this scope.
    let private loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory =
        let _, loggerFactory = LoggerFactory.makeTest ()
        loggerFactory

    let internal corelib : DumpedAssembly =
        Assembly.readFile loggerFactory HostImage.coreLibPath

    let internal hostCoreLib : Reflection.Assembly = typeof<obj>.Assembly

    let internal bct : BaseClassTypes<DumpedAssembly> = BaseClassTypes.ofCorelib corelib

    let internal dirs : string seq = HostImage.runtimeDirs :> string seq

    /// The host's CoreLib alone, concretised once. Each corpus is added to it.
    let internal coreState : IlMachineState =
        let initial = IlMachineState.initial loggerFactory HostImage.runtimeDirs corelib

        { initial with
            TypeSystem =
                { initial.TypeSystem with
                    ConcreteTypes =
                        Corelib.concretizeAll initial.TypeSystem._LoadedAssemblies bct AllConcreteTypes.Empty
                }
        }

    /// How many concrete types `types` has registered. Handles are allocated in turn from zero and
    /// never released.
    let internal concreteTypeCount (types : AllConcreteTypes) : int =
        let rec go (n : int) : int =
            match AllConcreteTypes.lookup (ConcreteTypeHandle.Concrete n) types with
            | Some _ -> go (n + 1)
            | None -> n

        go 0

    // ----- The host's answers -----

    /// One method of an interface, asked about on a receiver.
    type internal Question =
        {
            Receiver : Type
            Interface : Type
            Method : MethodInfo
        }

        override this.ToString () =
            $"%O{this.Receiver} / %O{this.Interface}::%s{this.Method.Name} (0x%08x{this.Method.MetadataToken})"

    /// What the host's `GetInterfaceMap` says about a question.
    [<RequireQualifiedAccess>]
    type internal HostAnswer =
        | Target of MethodInfo
        | NoTarget
        /// The whole map throws `AmbiguousImplementationException`, so the host gives no rows.
        | Throws

    /// The host's map of `iface` on `receiver`, a row at a time. Where it throws
    /// `AmbiguousImplementationException`, each static virtual `iface` declares, as `Throws`: only a
    /// static method's conflict throws.
    let internal hostAnswers (receiver : Type) (iface : Type) : (Question * HostAnswer) list =
        let question (m : MethodInfo) : Question =
            {
                Receiver = receiver
                Interface = iface
                Method = m
            }

        let map =
            try
                Some (receiver.GetInterfaceMap iface)
            with :? System.Runtime.AmbiguousImplementationException ->
                None

        match map with
        | Some map ->
            Array.zip map.InterfaceMethods map.TargetMethods
            |> Array.map (fun (m, target) ->
                match target with
                | null -> question m, HostAnswer.NoTarget
                | target -> question m, HostAnswer.Target target
            )
            |> List.ofArray
        | None ->
            iface.GetMethods (
                BindingFlags.DeclaredOnly
                ||| BindingFlags.Static
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
            )
            |> Array.filter _.IsVirtual
            |> Array.map (fun m -> question m, HostAnswer.Throws)
            |> List.ofArray

    // ----- PawPrint's call path -----

    /// Why a question is not compared.
    [<RequireQualifiedAccess>]
    type internal Skip =
        /// The receiver is a generic definition or an open construction.
        | OpenReceiver
        | OpenInterface
        /// A generic method with a constraint, which the call path needs method arguments for and
        /// `System.Object` may not satisfy.
        | ConstrainedGenericMethod

    /// What the call path answers.
    [<RequireQualifiedAccess>]
    type internal CallAnswer =
        | Answered of VirtualImplementation
        /// The binding failure the type system returned, or the first line of what it threw.
        | Failed of string
        | Skipped of Skip

    let internal dumpedOf (images : HostTypeTarget.Images) (assembly : Reflection.Assembly) : DumpedAssembly =
        images
        |> List.tryFind (fun (host, _) -> Object.ReferenceEquals (host, assembly))
        |> Option.map snd
        |> Option.defaultWith (fun () -> failwith $"%O{assembly} is in none of the images under test")

    /// PawPrint's reading of the MethodDef row `m` names.
    let internal definitionOf
        (images : HostTypeTarget.Images)
        (m : MethodInfo)
        : WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>
        =
        (dumpedOf images m.Module.Assembly)
            .Methods.[MetadataTokens.MethodDefinitionHandle (m.MetadataToken &&& 0x00FFFFFF)]

    let private isConstrained (m : MethodInfo) : bool =
        m.IsGenericMethodDefinition
        && m.GetGenericArguments ()
           |> Array.exists (fun argument ->
               argument.GetGenericParameterConstraints().Length > 0
               || argument.GenericParameterAttributes
                  &&& GenericParameterAttributes.SpecialConstraintMask
                  <> GenericParameterAttributes.None
           )

    /// The receiver's and the interface's handles, or why the question is not compared.
    let internal closedHandles
        (images : HostTypeTarget.Images)
        (state : IlMachineState)
        (question : Question)
        : IlMachineState * Result<ConcreteTypeHandle * ConcreteTypeHandle, Skip>
        =
        if question.Receiver.ContainsGenericParameters then
            state, Error Skip.OpenReceiver
        else

        let state, receiver =
            HostTypeTarget.target loggerFactory bct images state question.Receiver

        let state, iface =
            HostTypeTarget.target loggerFactory bct images state question.Interface

        match receiver, iface with
        | RuntimeTypeHandleTarget.Closed receiver, RuntimeTypeHandleTarget.Closed iface -> state, Ok (receiver, iface)
        | RuntimeTypeHandleTarget.Closed _, _ -> state, Error Skip.OpenInterface
        | _, _ -> state, Error Skip.OpenReceiver

    let private firstLine (message : string) : string =
        match message.Split '\n' with
        | [||] -> message
        | lines -> lines.[0]

    /// What the call path answers for `question`: the interface method instantiated over the
    /// interface's arguments, and over `System.Object` for each of its own parameters, dispatched on
    /// the receiver.
    let internal callAnswer
        (images : HostTypeTarget.Images)
        (state : IlMachineState)
        (question : Question)
        : IlMachineState * CallAnswer
        =
        match closedHandles images state question with
        | state, Error skip -> state, CallAnswer.Skipped skip
        | state, Ok _ when isConstrained question.Method -> state, CallAnswer.Skipped Skip.ConstrainedGenericMethod
        | state, Ok (receiver, iface) ->

        let definition = definitionOf images question.Method
        let before = state.TypeSystem

        let typeGenerics =
            match AllConcreteTypes.lookup iface before.ConcreteTypes with
            | Some iface -> iface.Generics
            | None -> failwith $"%O{question.Interface} was concretised but is not registered"

        let objectHandle =
            AllConcreteTypes.getRequiredNonGenericHandle before.ConcreteTypes bct.Object

        let methodGenerics =
            ImmutableArray.CreateRange (Seq.replicate definition.Signature.GenericParameterCount objectHandle)

        let after, answer =
            try
                match
                    MethodConcretisation.concretizeMethodWithAllGenerics
                        loggerFactory
                        dirs
                        bct
                        typeGenerics
                        definition
                        methodGenerics
                        before
                with
                | after, Error failure -> after, CallAnswer.Failed $"%O{failure}"
                | after, Ok (concretised, _) ->
                    let after, result =
                        if concretised.IsStatic then
                            StaticVirtualDispatch.resolve loggerFactory dirs bct receiver concretised after
                        else
                            ConcreteVirtualDispatch.tryResolveVirtualImplementation
                                loggerFactory
                                dirs
                                bct
                                concretised.Generics
                                concretised
                                receiver
                                true
                                after

                    match result with
                    | Ok answer -> after, CallAnswer.Answered answer
                    | Error failure -> after, CallAnswer.Failed $"%O{failure}"
            with e ->
                before, CallAnswer.Failed (firstLine e.Message)

        { state with
            TypeSystem = after
        },
        answer

    /// Whether `found` is the host's `expected`: the same MethodDef of the same image, declared by the
    /// same type at the same instantiation.
    let internal sameMethod
        (images : HostTypeTarget.Images)
        (state : IlMachineState)
        (found : DispatchedMethod)
        (expected : MethodInfo)
        : IlMachineState * bool
        =
        let definition = definitionOf images expected

        let sameRow =
            found.Definition.DeclaringAssemblyFullName = definition.DeclaringAssemblyFullName
            && found.Definition.IdentityKey = definition.IdentityKey

        let state, declaring =
            HostTypeTarget.target loggerFactory bct images state expected.DeclaringType

        let foundDeclaring =
            AllConcreteTypes.findExistingConcreteType
                state.TypeSystem.ConcreteTypes
                found.Definition.RequiredDeclaringType.Identity
                found.TypeGenerics

        let sameDeclaring =
            match declaring with
            | RuntimeTypeHandleTarget.Closed declaring -> foundDeclaring = Some declaring
            | _ -> false

        state, sameRow && sameDeclaring

    /// Whether `found`, a method definition, is the host's `expected`.
    let internal sameDefinition
        (images : HostTypeTarget.Images)
        (found : WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>)
        (expected : MethodInfo)
        : bool
        =
        let definition = definitionOf images expected

        found.DeclaringAssemblyFullName = definition.DeclaringAssemblyFullName
        && found.IdentityKey = definition.IdentityKey

    let private describeHost (host : HostAnswer) : string =
        match host with
        | HostAnswer.Target m ->
            let isAbstract = if m.IsAbstract then " (abstract)" else ""
            $"%O{m.DeclaringType}::%s{m.Name}%s{isAbstract}"
        | HostAnswer.NoTarget -> "no target"
        | HostAnswer.Throws -> "AmbiguousImplementationException"

    let internal describeCall (answer : CallAnswer) : string =
        let name (m : WoofWare.PawPrint.MethodInfo<_, _, _>) : string =
            $"%s{MethodOwner.describe m.Owner}::%s{m.Name}"

        match answer with
        | CallAnswer.Answered (VirtualImplementation.Found found) ->
            let typeGenerics = found.TypeGenerics |> Seq.map string |> String.concat ", "
            let methodGenerics = found.MethodGenerics |> Seq.map string |> String.concat ", "
            $"Found %s{name found.Definition} [%s{typeGenerics}] [%s{methodGenerics}]"
        | CallAnswer.Answered VirtualImplementation.NotOverridden -> "NotOverridden"
        | CallAnswer.Answered VirtualImplementation.NotImplemented -> "NotImplemented"
        | CallAnswer.Answered (VirtualImplementation.Ambiguous candidates) ->
            let candidates = candidates |> List.map name |> String.concat ", "
            $"Ambiguous %s{candidates}"
        | CallAnswer.Answered (VirtualImplementation.Reabstracted reabstraction) ->
            $"Reabstracted %s{name reabstraction}"
        | CallAnswer.Answered (VirtualImplementation.Unmodelled reason) -> $"Unmodelled %s{firstLine reason}"
        | CallAnswer.Failed message -> $"Failed %s{message}"
        | CallAnswer.Skipped skip -> $"Skipped %A{skip}"

    /// Where the call path's answer breaks the characterisation's rules for a C#-compilable shape,
    /// why. A call-only answer (`Unmodelled`, or a refusal) is never right here.
    let internal judgeCall
        (images : HostTypeTarget.Images)
        (state : IlMachineState)
        (question : Question)
        (host : HostAnswer)
        (answer : CallAnswer)
        : IlMachineState * string option
        =
        let disagree () =
            Some $"%O{question}: the host says %s{describeHost host}, the call path %s{describeCall answer}"

        match host, answer with
        | _, CallAnswer.Skipped _ -> state, None
        | HostAnswer.Target m, CallAnswer.Answered (VirtualImplementation.Reabstracted reabstraction) when
            m.IsAbstract && m.DeclaringType.IsInterface
            ->
            state,
            (if sameDefinition images reabstraction m then
                 None
             else
                 disagree ())
        | HostAnswer.Target m, CallAnswer.Answered (VirtualImplementation.Found found) when
            not (m.IsAbstract && m.DeclaringType.IsInterface)
            ->
            match sameMethod images state found m with
            | state, true -> state, None
            | state, false -> state, disagree ()
        | HostAnswer.NoTarget,
          CallAnswer.Answered (VirtualImplementation.NotImplemented | VirtualImplementation.Reabstracted _ | VirtualImplementation.Ambiguous _) ->
            state, None
        | HostAnswer.Throws, CallAnswer.Answered (VirtualImplementation.Ambiguous _) -> state, None
        | _, _ -> state, disagree ()

    // ----- CoreLib -----

    /// Every exported CoreLib type that is not an interface, generic definitions included, and a spread
    /// of instantiations and arrays, each with each interface it implements except an SZ array's generic
    /// ones, which `GetInterfaceMap` refuses (`Argument_ArrayGetInterfaceMap`).
    let internal coreLibPairs : (Type * Type) list =
        let instantiations : Type list =
            [
                typeof<System.Collections.Generic.List<int>>
                typeof<System.Collections.Generic.List<string>>
                typeof<System.Collections.Generic.Dictionary<string, obj>>
                typeof<System.Collections.Generic.HashSet<int>>
                typeof<System.Collections.Generic.Queue<Exception>>
                typeof<System.Collections.ObjectModel.ReadOnlyCollection<string>>
                typeof<System.Collections.ObjectModel.Collection<int>>
                typeof<System.Collections.Generic.EqualityComparer<string>>
                typeof<System.Collections.Generic.Comparer<int>>
                typeof<Nullable<int>>
                typeof<ArraySegment<byte>>
                typeof<Memory<char>>
                typeof<ValueTuple<int, string>>
                typeof<Tuple<int, string>>
                typeof<Lazy<int>>
                typeof<System.Collections.Generic.KeyValuePair<int, string>>
                typeof<int[]>
                typeof<string[]>
                typeof<int[,]>
            ]

        [
            for receiver in Array.append (hostCoreLib.GetExportedTypes ()) (Array.ofList instantiations) do
                if not receiver.IsInterface then
                    for iface in receiver.GetInterfaces () do
                        if not (receiver.IsSZArray && iface.IsGenericType) then
                            yield receiver, iface
        ]

    [<Test>]
    let ``CoreLib: the call path agrees with GetInterfaceMap`` () : unit =
        let stopwatch = Stopwatch.StartNew ()
        let images = [ hostCoreLib, corelib ]
        let failures = ResizeArray<string> ()
        let mutable state = coreState
        let mutable compared = 0
        let mutable byDefaultBody = 0
        let mutable statics = 0
        let mutable genericOverObject = 0
        let mutable constrained = 0
        let mutable openReceivers = 0

        for receiver, iface in coreLibPairs do
            if receiver.ContainsGenericParameters then
                openReceivers <- openReceivers + 1
            else

            for question, host in hostAnswers receiver iface do
                let afterCall, answer = callAnswer images state question
                let afterJudging, failure = judgeCall images afterCall question host answer
                state <- afterJudging

                match answer with
                | CallAnswer.Skipped Skip.ConstrainedGenericMethod -> constrained <- constrained + 1
                | CallAnswer.Skipped _ -> ()
                | CallAnswer.Answered _
                | CallAnswer.Failed _ ->
                    compared <- compared + 1

                    if question.Method.IsStatic then
                        statics <- statics + 1

                    if question.Method.IsGenericMethodDefinition then
                        genericOverObject <- genericOverObject + 1

                    match host with
                    | HostAnswer.Target m when m.DeclaringType.IsInterface -> byDefaultBody <- byDefaultBody + 1
                    | _ -> ()

                match failure with
                | Some failure -> failures.Add failure
                | None -> ()

        TestContext.Progress.WriteLine
            $"CoreLib: %d{compared} rows compared, %d{byDefaultBody} by a default body, %d{statics} static, %d{genericOverObject} generic over System.Object, %d{constrained} constrained generic skipped, %d{openReceivers} open receivers skipped, %d{stopwatch.ElapsedMilliseconds} ms"

        failures |> Seq.truncate 40 |> List.ofSeq |> shouldEqual []

        // About half of what a .NET 10 host measured: 6,965 rows compared, 332 by a default body, 2,170
        // static, 172 constrained generic methods and 423 open receivers. No exported CoreLib type
        // implements an interface with an unconstrained generic method, so the generated corpus counts
        // those instead.
        Assert.That (compared, Is.GreaterThanOrEqualTo 3400)
        Assert.That (byDefaultBody, Is.GreaterThanOrEqualTo 160)
        Assert.That (statics, Is.GreaterThanOrEqualTo 1000)
        Assert.That (constrained, Is.GreaterThanOrEqualTo 80)
        Assert.That (openReceivers, Is.GreaterThanOrEqualTo 200)

    // ----- Generated corpora -----

    /// Compile `corpus`, load it into the host in a collectible context and into a state over CoreLib,
    /// and give `f` both with the pairs `implementingPairs` asks about. The context is unloaded after.
    let internal withImplementingCorpus
        (corpus : ImplementingCorpus)
        (f : string -> HostTypeTarget.Images -> IlMachineState -> (Type * Type) list -> 'a)
        : 'a
        =
        let source = renderImplementing corpus

        let image =
            try
                Roslyn.compileAssembly "InterfaceMethodImplementation" OutputKind.DynamicallyLinkedLibrary [] [ source ]
            with e ->
                failwith $"%s{e.Message}\nin the corpus:\n%s{source}"

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context =
            AssemblyLoadContext ("interfaceMethodImplementation", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            f
                source
                [ hostCoreLib, corelib ; host, dumped ]
                (coreState.WithLoadedAssembly dumped)
                (implementingPairs corpus host)
        finally
            context.Unload ()

    /// What a generated row reached, read from the host's side alone.
    type private Label =
        /// A target declared by the receiver.
        | OwnMethod
        /// A target declared by a class the receiver derives from.
        | Inherited
        /// A default body of the interface asked about, or of an instantiation of it.
        | DefaultOnItself
        /// A default body of a more specific interface.
        | DefaultOnMoreSpecific
        | NullRow
        /// A static row answered by a method of a class or struct.
        | StaticByMethodImpl
        /// A static row answered by an interface's method.
        | StaticByDefault
        /// An interface the receiver does not implement but casts to by variance.
        | ThroughVariance
        /// A target that is a generic method definition.
        | GenericMethodDefinition

    let private labels (question : Question) (host : HostAnswer) : Label list =
        let definitionOf (t : Type) : Type =
            if t.IsGenericType then t.GetGenericTypeDefinition () else t

        [
            if not (Array.contains question.Interface (question.Receiver.GetInterfaces ())) then
                yield Label.ThroughVariance

            match host with
            | HostAnswer.NoTarget -> yield Label.NullRow
            | HostAnswer.Throws -> ()
            | HostAnswer.Target m ->
                if m.DeclaringType = question.Receiver then
                    yield Label.OwnMethod
                elif not m.DeclaringType.IsInterface then
                    yield Label.Inherited
                elif definitionOf m.DeclaringType = definitionOf question.Interface then
                    yield Label.DefaultOnItself
                else
                    yield Label.DefaultOnMoreSpecific

                if question.Method.IsStatic then
                    if m.DeclaringType.IsInterface then
                        yield Label.StaticByDefault
                    else
                        yield Label.StaticByMethodImpl

                if m.IsGenericMethodDefinition then
                    yield Label.GenericMethodDefinition
        ]

    let private checkGenerated (cover : Label -> unit) (corpus : ImplementingCorpus) : unit =
        withImplementingCorpus
            corpus
            (fun source images state pairs ->
                let mutable state = state
                let failures = ResizeArray<string> ()

                for receiver, iface in pairs do
                    if not receiver.ContainsGenericParameters then
                        for question, host in hostAnswers receiver iface do
                            for label in labels question host do
                                cover label

                            let afterCall, answer = callAnswer images state question
                            let afterJudging, failure = judgeCall images afterCall question host answer
                            state <- afterJudging

                            match failure with
                            | Some failure -> failures.Add failure
                            | None -> ()

                if failures.Count > 0 then
                    failwith (
                        $"%d{failures.Count} disagreements with the host:\n"
                        + String.concat "\n" failures
                        + $"\nin the corpus:\n%s{source}"
                    )
            )

    [<Test>]
    let ``Generated corpora: the call path agrees with GetInterfaceMap`` () : unit =
        let stopwatch = Stopwatch.StartNew ()

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 100)
                (Arb.fromGen genImplementingCorpus)
                (fun cover corpus -> checkGenerated cover corpus)

        TestContext.Progress.WriteLine $"generated corpora: %d{stopwatch.ElapsedMilliseconds} ms"

        // About half of what the fixed sample reaches.
        for label, floor in
            [
                Label.OwnMethod, 430
                Label.Inherited, 60
                Label.DefaultOnItself, 60
                Label.DefaultOnMoreSpecific, 11
                Label.NullRow, 44
                Label.StaticByMethodImpl, 215
                Label.StaticByDefault, 9
                Label.ThroughVariance, 44
                Label.GenericMethodDefinition, 89
            ] do
            Assert.That (coverage.Count label, Is.GreaterThanOrEqualTo floor, $"%A{label}")

    // ----- Shapes C# refuses -----

    /// A library whose second version adds the bodies and reabstractions C# would refuse the classes
    /// over; `swapClasses` is compiled against the first.
    let private swapLibrary : string =
        """
// Static diamond: two equally specific bodies of a static virtual.
public interface IAs { static virtual int S() => 1; }
public interface IBs : IAs
{
#if V2
    static int IAs.S() => 2;
#endif
}
public interface ICs : IAs
{
#if V2
    static int IAs.S() => 3;
#endif
}

// Static reabstraction as the most specific default.
public interface IAr { static virtual int S() => 1; }
public interface IRr : IAr
{
#if V2
    static abstract int IAr.S();
#endif
}

// Instance diamond through a variant interface, at its exact instantiation.
public interface IAv<out T> { int M() => 1; }
public interface IBv : IAv<string>
{
#if V2
    int IAv<string>.M() => 2;
#endif
}
public interface ICv : IAv<string>
{
#if V2
    int IAv<string>.M() => 3;
#endif
}

// Static diamond through a variant interface.
public interface IAsv<out T> { static virtual int S() => 1; }
public interface IBsv : IAsv<string>
{
#if V2
    static int IAsv<string>.S() => 2;
#endif
}
public interface ICsv : IAsv<string>
{
#if V2
    static int IAsv<string>.S() => 3;
#endif
}

// Instance reabstraction as the most specific default.
public interface IAi { void M() { } }
public interface IRi : IAi
{
#if V2
    abstract void IAi.M();
#endif
}

// Static reabstraction of a generic interface as the most specific default.
public interface IArG<T> { static virtual int S() => 1; }
public interface IRrG<T> : IArG<T>
{
#if V2
    static abstract int IArG<T>.S();
#endif
}

// Instance diamond through a variant interface, one of whose bodies is a reabstraction.
public interface IAw<out T> { int M() => 1; }
public interface IBw : IAw<string>
{
#if V2
    abstract int IAw<string>.M();
#endif
}
public interface ICw : IAw<string>
{
#if V2
    int IAw<string>.M() => 3;
#endif
}

// A static default body of a generic interface, which C# compiles.
public interface IStatG<T> { static virtual int SV() => 1; }

// Instance diamond, for an IDynamicInterfaceCastable receiver.
public interface IAd { int M() => 1; }
public interface IBd : IAd
{
#if V2
    int IAd.M() => 2;
#endif
}
public interface ICd : IAd
{
#if V2
    int IAd.M() => 3;
#endif
}
"""

    let private swapClasses : string =
        """
public class CDiamondS : IBs, ICs { }
public class CReabsS : IRr { }
public class CDiamondV : IBv, ICv { }
public class CDiamondSV : IBsv, ICsv { }
public class CReabsI : IRi { }
public class CReabsSG : IRrG<string> { }
public class CReabsSGen<T> : IRrG<T> { }
public class CDiamondW : IBw, ICw { }
public class CDiamondW2 : ICw, IBw { }
public class CStatG : IStatG<string> { }
public class CGBaseM<T> : IAi { public void M() { } }
public class CDicReabs : System.Runtime.InteropServices.IDynamicInterfaceCastable, IRi
{
    public bool IsInterfaceImplemented(System.RuntimeTypeHandle interfaceType, bool throwIfNotImplemented) => false;
    public System.RuntimeTypeHandle GetInterfaceImplementation(System.RuntimeTypeHandle interfaceType) => default;
}
public class CDicDiamond : System.Runtime.InteropServices.IDynamicInterfaceCastable, IBd, ICd
{
    public bool IsInterfaceImplemented(System.RuntimeTypeHandle interfaceType, bool throwIfNotImplemented) => false;
    public System.RuntimeTypeHandle GetInterfaceImplementation(System.RuntimeTypeHandle interfaceType) => default;
}
"""

    let private swapLibraryBefore : byte[] Lazy =
        lazy (Roslyn.compileAssembly "SwapLib" OutputKind.DynamicallyLinkedLibrary [] [ swapLibrary ])

    let private swapLibraryAfter : byte[] Lazy =
        lazy (Roslyn.compileAssembly "SwapLib" OutputKind.DynamicallyLinkedLibrary [] [ "#define V2\n" + swapLibrary ])

    let private swapClassesImage : byte[] Lazy =
        lazy
            (Roslyn.compileAssembly
                "SwapCls"
                OutputKind.DynamicallyLinkedLibrary
                [ MetadataReference.CreateFromImage swapLibraryBefore.Value ]
                [ swapClasses ])

    let private swapLibraryDumped : DumpedAssembly Lazy =
        lazy
            (use stream = new MemoryStream (swapLibraryAfter.Value)
             AssemblyApi.read loggerFactory None stream)

    let private swapClassesDumped : DumpedAssembly Lazy =
        lazy
            (use stream = new MemoryStream (swapClassesImage.Value)
             AssemblyApi.read loggerFactory None stream)

    /// What the host's map gives for a shape's one row, as the plan measured it on .NET 10 and 11.
    [<RequireQualifiedAccess>]
    type internal ExpectedHost =
        | Target of declaringType : string * name : string * isAbstract : bool
        | NoTarget
        | Throws

    /// The one answer the call path gives for a shape's one row.
    [<RequireQualifiedAccess>]
    type internal ExpectedCall =
        /// `Found`, the host's target.
        | FoundTarget
        /// `Found`, a method of the named interface: the first survivor of a conflict in the variant
        /// pass, which a call runs and the map throws for.
        | FoundOn of string
        /// `Reabstracted`, declared by the named interface.
        | Reabstracted of string
        /// `Ambiguous`, between methods of the named interfaces.
        | Ambiguous of string list
        | NotImplemented
        /// The call-only refusal of a conflict at a variant interface's exact instantiation.
        | Unmodelled

    /// A receiver and an interface of the two-version library, with one method to ask about.
    type internal Shape =
        {
            Name : string
            /// From the library's host assembly and the classes' host assembly.
            Receiver : Reflection.Assembly -> Reflection.Assembly -> Type
            Interface : Reflection.Assembly -> Reflection.Assembly -> Type
            Host : ExpectedHost
            Call : ExpectedCall
        }

    let private named (assembly : Reflection.Assembly) (name : string) : Type =
        match assembly.GetType name with
        | null -> failwith $"%O{assembly} has no type %s{name}"
        | t -> t

    let private classNamed (name : string) : Reflection.Assembly -> Reflection.Assembly -> Type =
        fun _ classes -> named classes name

    let private interfaceNamed (name : string) : Reflection.Assembly -> Reflection.Assembly -> Type =
        fun library _ -> named library name

    let private interfaceAt (name : string) (argument : Type) : Reflection.Assembly -> Reflection.Assembly -> Type =
        fun library _ -> (named library name).MakeGenericType argument

    let internal shapes : Shape list =
        [
            {
                Name = "static diamond"
                Receiver = classNamed "CDiamondS"
                Interface = interfaceNamed "IAs"
                Host = ExpectedHost.Throws
                Call = ExpectedCall.Ambiguous [ "IBs" ; "ICs" ]
            }
            {
                Name = "static reabstraction"
                Receiver = classNamed "CReabsS"
                Interface = interfaceNamed "IAr"
                Host = ExpectedHost.Target ("IRr", "IAr.S", true)
                Call = ExpectedCall.Reabstracted "IRr"
            }
            {
                Name = "static reabstraction, asked at the reabstracting interface"
                Receiver = classNamed "CReabsS"
                Interface = interfaceNamed "IRr"
                Host = ExpectedHost.NoTarget
                Call = ExpectedCall.NotImplemented
            }
            {
                Name = "variant diamond at its exact instantiation"
                Receiver = classNamed "CDiamondV"
                Interface = interfaceAt "IAv`1" typeof<string>
                Host = ExpectedHost.Target ("IBv", "IAv<System.String>.M", false)
                Call = ExpectedCall.Unmodelled
            }
            {
                Name = "variant diamond by variance"
                Receiver = classNamed "CDiamondV"
                Interface = interfaceAt "IAv`1" typeof<obj>
                Host = ExpectedHost.Target ("IBv", "IAv<System.String>.M", false)
                Call = ExpectedCall.FoundTarget
            }
            {
                Name = "static variant diamond at its exact instantiation"
                Receiver = classNamed "CDiamondSV"
                Interface = interfaceAt "IAsv`1" typeof<string>
                Host = ExpectedHost.Throws
                Call = ExpectedCall.Ambiguous [ "IBsv" ; "ICsv" ]
            }
            {
                Name = "static variant diamond by variance"
                Receiver = classNamed "CDiamondSV"
                Interface = interfaceAt "IAsv`1" typeof<obj>
                Host = ExpectedHost.Throws
                Call = ExpectedCall.FoundOn "IBsv"
            }
            {
                Name = "instance reabstraction"
                Receiver = classNamed "CReabsI"
                Interface = interfaceNamed "IAi"
                Host = ExpectedHost.NoTarget
                Call = ExpectedCall.Reabstracted "IRi"
            }
            {
                Name = "generic static reabstraction"
                Receiver = classNamed "CReabsSG"
                Interface = interfaceAt "IArG`1" typeof<string>
                Host = ExpectedHost.Target ("IRrG`1", "IArG<T>.S", true)
                Call = ExpectedCall.Reabstracted "IRrG`1"
            }
            {
                Name = "generic static reabstraction, asked at the reabstracting interface"
                Receiver = classNamed "CReabsSG"
                Interface = interfaceAt "IRrG`1" typeof<string>
                Host = ExpectedHost.NoTarget
                Call = ExpectedCall.NotImplemented
            }
            {
                Name = "generic static reabstraction on a generic class"
                Receiver = fun _ classes -> (named classes "CReabsSGen`1").MakeGenericType typeof<int>
                Interface = interfaceAt "IArG`1" typeof<int>
                Host = ExpectedHost.Target ("IRrG`1", "IArG<T>.S", true)
                Call = ExpectedCall.Reabstracted "IRrG`1"
            }
            {
                Name = "variant diamond with the reabstraction first, at its exact instantiation"
                Receiver = classNamed "CDiamondW"
                Interface = interfaceAt "IAw`1" typeof<string>
                Host = ExpectedHost.NoTarget
                Call = ExpectedCall.Unmodelled
            }
            {
                Name = "variant diamond with the reabstraction first, by variance"
                Receiver = classNamed "CDiamondW"
                Interface = interfaceAt "IAw`1" typeof<obj>
                Host = ExpectedHost.NoTarget
                Call = ExpectedCall.Reabstracted "IBw"
            }
            {
                Name = "variant diamond with the reabstraction second, at its exact instantiation"
                Receiver = classNamed "CDiamondW2"
                Interface = interfaceAt "IAw`1" typeof<string>
                Host = ExpectedHost.Target ("ICw", "IAw<System.String>.M", false)
                Call = ExpectedCall.Unmodelled
            }
            {
                Name = "variant diamond with the reabstraction second, by variance"
                Receiver = classNamed "CDiamondW2"
                Interface = interfaceAt "IAw`1" typeof<obj>
                Host = ExpectedHost.Target ("ICw", "IAw<System.String>.M", false)
                Call = ExpectedCall.FoundTarget
            }
            {
                Name = "static default body of a generic interface"
                Receiver = classNamed "CStatG"
                Interface = interfaceAt "IStatG`1" typeof<string>
                Host = ExpectedHost.Target ("IStatG`1", "SV", false)
                Call = ExpectedCall.FoundTarget
            }
            {
                Name = "a closed generic class's own method"
                Receiver = fun _ classes -> (named classes "CGBaseM`1").MakeGenericType typeof<string>
                Interface = interfaceNamed "IAi"
                Host = ExpectedHost.Target ("CGBaseM`1", "M", false)
                Call = ExpectedCall.FoundTarget
            }
            {
                Name = "IDynamicInterfaceCastable receiver, instance reabstraction"
                Receiver = classNamed "CDicReabs"
                Interface = interfaceNamed "IAi"
                Host = ExpectedHost.NoTarget
                Call = ExpectedCall.Reabstracted "IRi"
            }
            {
                Name = "IDynamicInterfaceCastable receiver, instance diamond"
                Receiver = classNamed "CDicDiamond"
                Interface = interfaceNamed "IAd"
                Host = ExpectedHost.NoTarget
                Call = ExpectedCall.Ambiguous [ "IBd" ; "ICd" ]
            }
        ]

    /// Load the library's second version and then the classes into a collectible context, so that the
    /// classes' reference binds to the second version, and into a state over CoreLib; give `f` both with
    /// `shape`'s question and the host's answer to it. The context is unloaded after.
    let internal withShape
        (shape : Shape)
        (f : HostTypeTarget.Images -> IlMachineState -> Question -> HostAnswer -> 'a)
        : 'a
        =
        let context = AssemblyLoadContext ($"swap %s{shape.Name}", isCollectible = true)

        try
            let library =
                use stream = new MemoryStream (swapLibraryAfter.Value)
                context.LoadFromStream stream

            let classes =
                use stream = new MemoryStream (swapClassesImage.Value)
                context.LoadFromStream stream

            let images =
                [
                    hostCoreLib, corelib
                    library, swapLibraryDumped.Value
                    classes, swapClassesDumped.Value
                ]

            let state =
                (coreState.WithLoadedAssembly swapLibraryDumped.Value).WithLoadedAssembly swapClassesDumped.Value

            let receiver = shape.Receiver library classes
            let iface = shape.Interface library classes

            match hostAnswers receiver iface with
            | [ question, host ] -> f images state question host
            | rows -> failwith $"%s{shape.Name}: expected one row, the host's map has %d{rows.Length}"
        finally
            context.Unload ()

    /// Where `host` is not the answer `expected` records, why.
    let private checkHost (expected : ExpectedHost) (host : HostAnswer) : string option =
        match expected, host with
        | ExpectedHost.Target (declaringType, name, isAbstract), HostAnswer.Target m when
            m.DeclaringType.Name = declaringType
            && m.Name = name
            && m.IsAbstract = isAbstract
            ->
            None
        | ExpectedHost.NoTarget, HostAnswer.NoTarget
        | ExpectedHost.Throws, HostAnswer.Throws -> None
        | _, _ -> Some $"the host's map says %s{describeHost host}, where %A{expected} was measured"

    [<Test>]
    let ``Shapes C# refuses: each gives the one call path answer expected`` () : unit =
        let failures =
            shapes
            |> List.choose (fun shape ->
                withShape
                    shape
                    (fun images state question host ->
                        match checkHost shape.Host host with
                        | Some failure -> Some $"%s{shape.Name}: %s{failure}"
                        | None ->

                        let state, answer = callAnswer images state question

                        let agrees =
                            match shape.Call, answer, host with
                            | ExpectedCall.FoundTarget,
                              CallAnswer.Answered (VirtualImplementation.Found found),
                              HostAnswer.Target m -> snd (sameMethod images state found m)
                            | ExpectedCall.FoundOn name, CallAnswer.Answered (VirtualImplementation.Found found), _ ->
                                found.Definition.RequiredDeclaringType.Name = name
                            | ExpectedCall.Reabstracted name,
                              CallAnswer.Answered (VirtualImplementation.Reabstracted reabstraction),
                              _ -> reabstraction.RequiredDeclaringType.Name = name
                            | ExpectedCall.Ambiguous names,
                              CallAnswer.Answered (VirtualImplementation.Ambiguous candidates),
                              _ -> (candidates |> List.map _.RequiredDeclaringType.Name |> List.sort) = names
                            | ExpectedCall.NotImplemented, CallAnswer.Answered VirtualImplementation.NotImplemented, _ ->
                                true
                            | ExpectedCall.Unmodelled, CallAnswer.Answered (VirtualImplementation.Unmodelled _), _ ->
                                true
                            | _, _, _ -> false

                        if agrees then
                            None
                        else
                            Some $"%s{shape.Name}: expected %A{shape.Call}, the call path %s{describeCall answer}"
                    )
            )

        failures |> shouldEqual []
