namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
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

/// `Type.IsVisible` of a `RuntimeType`, PawPrint's against the host's over the same images: the
/// host's CoreLib (`HostImage`) and a Roslyn fixture loaded into both. PawPrint's answer is
/// `RuntimeTypeHandleTarget.isExternallyVisible`, the pure function the `RuntimeTypeHandle_IsVisible`
/// handler calls; the guests (`TypeIsVisible.cs` and its siblings) cover the handler.
///
/// The fixture's nesting chains are enumerated: every chain of a top-level class, public or not, with
/// up to three nested levels of each of the six nested visibilities. Constructions over the fixture's
/// types and CoreLib's (instantiations, arrays, pointers, byrefs, generic parameters, function
/// pointers) are drawn by FsCheck, each built on both sides from one tree.
[<TestFixture>]
module TestIsVisibleSameImage =

    /// The six nested visibilities, as a C# keyword and a short code for the type's name.
    let private nestedVisibilities : (string * string) list =
        [
            "public", "pu"
            "private", "pr"
            "protected", "fa"
            "internal", "as"
            "private protected", "fx"
            "protected internal", "fo"
        ]

    let private nestingSource : string =
        let builder = Text.StringBuilder ()

        let rec emit (indent : string) (keyword : string) (name : string) (depth : int) : unit =
            builder.Append($"%s{indent}%s{keyword} class %s{name}\n%s{indent}{{\n") |> ignore

            if depth < 3 then
                for nestedKeyword, code in nestedVisibilities do
                    emit (indent + "    ") nestedKeyword $"%s{name}_%s{code}" (depth + 1)

            builder.Append($"%s{indent}}}\n") |> ignore

        emit "" "public" "C_P" 0
        emit "" "internal" "C_N" 0
        builder.ToString ()

    /// Every signature of at most two parameters over the types the holder can name, each a field.
    let private fnParameterTypes : string list = [ "Pub" ; "Internal" ; "PubG<Internal>" ; "T" ]

    let private fnReturnTypes : string list = "void" :: fnParameterTypes

    let private fnSignatures : (string list * string) list =
        [
            for count in 0..2 do
                let rec parameterLists (n : int) : string list list =
                    if n = 0 then
                        [ [] ]
                    else
                        [
                            for rest in parameterLists (n - 1) do
                                for p in fnParameterTypes do
                                    yield p :: rest
                        ]

                for parameters in parameterLists count do
                    for ret in fnReturnTypes do
                        yield parameters, ret
        ]

    let private fnFieldName (index : int) : string = $"f%d{index}"

    let private constructionSource : string =
        let fields =
            fnSignatures
            |> List.mapi (fun i (parameters, ret) ->
                let types = parameters @ [ ret ] |> String.concat ", "
                $"    internal delegate*<%s{types}> %s{fnFieldName i};"
            )
            |> String.concat "\n"

        $"""
public class Pub {{ }}
internal class Internal {{ }}
public class PubG<T> {{ }}
internal class InternalG<T> {{ }}
public class PubG2<T, U> {{ }}

public class GP<T>
{{
    public class NPub {{ }}
    internal class NAsm {{ }}
    protected class NFam {{ }}
    public class NG<U> {{ public class Deep {{ }} }}
}}

internal class GI<T>
{{
    public class NPub {{ }}
}}

public class Gm {{ public static void M<U>() {{ }} }}
internal class Gmi {{ internal static void M<U>() {{ }} }}

public unsafe class FnFields<T>
{{
%s{fields}
}}
"""

    let private image : byte[] =
        Roslyn.compileAssembly
            "IsVisibleSameImage"
            OutputKind.DynamicallyLinkedLibrary
            []
            [ nestingSource ; constructionSource ]

    /// The same image the PawPrint side reads, loaded by the real CLR. Nothing here is a guest.
    let private hostAssembly : Assembly = Assembly.Load image

    let private hostCoreLib : Assembly = typeof<obj>.Assembly

    // The factory is intentionally undisposed: the DumpedAssembly loggers close over its sinks.
    let private loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory =
        let _, loggerFactory = LoggerFactory.makeTest ()
        loggerFactory

    let private corelib : DumpedAssembly =
        Assembly.readFile loggerFactory HostImage.coreLibPath

    let private guest : DumpedAssembly =
        use stream = new MemoryStream (image)
        AssemblyApi.read loggerFactory None stream

    let private bct : BaseClassTypes<DumpedAssembly> = BaseClassTypes.ofCorelib corelib

    let private initialState : IlMachineState =
        let withAssemblies =
            (IlMachineState.initial loggerFactory ImmutableArray.Empty guest).WithLoadedAssembly corelib

        { withAssemblies with
            TypeSystem =
                { withAssemblies.TypeSystem with
                    ConcreteTypes =
                        Corelib.concretizeAll withAssemblies.TypeSystem._LoadedAssemblies bct AllConcreteTypes.Empty
                }
        }

    let private typeDefToken (handle : TypeDefinitionHandle) : int =
        MetadataTokens.GetToken (TypeDefinitionHandle.op_Implicit handle : EntityHandle)

    /// The `<Module>` type, TypeDef row 1, which the host's `Module.ResolveType` refuses to name, so
    /// that it has no host answer to compare with.
    let private isModuleType (ty : TypeInfo<GenericParamFromMetadata, TypeDefn>) : bool =
        MetadataTokens.GetRowNumber (TypeDefinitionHandle.op_Implicit ty.TypeDefHandle : EntityHandle) = 1

    /// PawPrint's reading of the TypeDef the host's `t` is, by metadata token.
    let private typeInfoOf (t : Type) : DumpedAssembly * TypeInfo<GenericParamFromMetadata, TypeDefn> =
        let assembly =
            if Object.ReferenceEquals (t.Assembly, hostAssembly) then guest
            elif Object.ReferenceEquals (t.Assembly, hostCoreLib) then corelib
            else failwith $"%O{t} is in neither the fixture nor CoreLib"

        assembly, assembly.TypeDefs.[MetadataTokens.TypeDefinitionHandle (t.MetadataToken &&& 0x00FFFFFF)]

    let private definitionDefn (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>) : TypeDefn =
        TypeDefn.FromDefinition (
            typeInfo.Identity,
            LoadedTypeInfo.signatureTypeKind bct initialState.TypeSystem._LoadedAssemblies typeInfo
        )

    let private concretize (state : IlMachineState) (defn : TypeDefn) : IlMachineState * ConcreteTypeHandle =
        IlMachineState.concretizeType
            loggerFactory
            bct
            state
            guest.DefinitionFullName
            ImmutableArray.Empty
            ImmutableArray.Empty
            defn

    let private pawPrintAnswer (state : IlMachineState) (target : RuntimeTypeHandleTarget) : bool =
        RuntimeTypeHandleTarget.isExternallyVisible state.TypeSystem._LoadedAssemblies state.TypeSystem.ConcreteTypes target

    /// A TypeDef as the handle a `RuntimeType` of it would carry: the definition for a generic type,
    /// else the closed type, concretised.
    let private definitionTarget
        (state : IlMachineState)
        (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>)
        : IlMachineState * RuntimeTypeHandleTarget
        =
        if typeInfo.Generics.IsEmpty then
            let state, handle = concretize state (definitionDefn typeInfo)
            state, RuntimeTypeHandleTarget.Closed handle
        else
            state, RuntimeTypeHandleTarget.OpenGenericTypeDefinition typeInfo.Identity

    // ----- The CoreLib sweep -----

    [<Test>]
    let ``The TypeDef rule agrees with the host for every TypeDef of the host's CoreLib`` () : unit =
        let imageCount =
            use stream = File.OpenRead HostImage.coreLibPath
            use pe = new PEReader (stream)
            pe.GetMetadataReader().TypeDefinitions.Count

        let compared =
            corelib.TypeDefs.Values |> Seq.filter (isModuleType >> not) |> List.ofSeq

        let disagreements =
            compared
            |> List.filter (fun ty ->
                let host = hostCoreLib.ManifestModule.ResolveType (typeDefToken ty.TypeDefHandle)
                TypeVisibility.isTypeDefExternallyVisible corelib ty <> host.IsVisible
            )
            |> List.map (fun ty -> $"%s{ty.Namespace}.%s{ty.Name}")

        corelib.TypeDefs.Count |> shouldEqual imageCount
        compared.Length |> shouldEqual (imageCount - 1)
        disagreements |> shouldEqual []

    /// The CoreLib types whose definition PawPrint's concretisation refuses, each with the start of its
    /// reason: the walk is not asked about these.
    let private expectedCoreLibRefusals : (string * string) list = []

    [<Test>]
    let ``The walk agrees with the host for every TypeDef of the host's CoreLib`` () : unit =
        let mutable state = initialState
        let refusals = ResizeArray<string * string> ()
        let disagreements = ResizeArray<string> ()
        let mutable compared = 0

        for ty in corelib.TypeDefs.Values |> Seq.filter (isModuleType >> not) do
            let name = $"%s{ty.Namespace}.%s{ty.Name}"

            match
                (try
                    Ok (definitionTarget state ty)
                 with e ->
                     Error e.Message)
            with
            | Error message -> refusals.Add (name, message.Substring (0, min 80 message.Length))
            | Ok (newState, target) ->
                state <- newState
                compared <- compared + 1
                let host = hostCoreLib.ManifestModule.ResolveType (typeDefToken ty.TypeDefHandle)

                if pawPrintAnswer state target <> host.IsVisible then
                    disagreements.Add name

        List.ofSeq disagreements |> shouldEqual []
        List.ofSeq refusals |> shouldEqual expectedCoreLibRefusals
        compared |> shouldEqual (corelib.TypeDefs.Count - 1 - expectedCoreLibRefusals.Length)

    // ----- The enumerated nesting fixture -----

    /// Every TypeDef of the fixture but `<Module>`: the chains, the generic forest, and the construction
    /// pool.
    let private fixtureTypes : TypeInfo<GenericParamFromMetadata, TypeDefn> list =
        guest.TypeDefs.Values |> Seq.filter (isModuleType >> not) |> List.ofSeq

    [<Test>]
    let ``The fixture holds every chain of up to three nested levels`` () : unit =
        let chainTypes =
            guest.TypeDefs.Values |> Seq.filter (fun ty -> ty.Name.StartsWith "C_") |> List.ofSeq

        chainTypes.Length |> shouldEqual 518

        chainTypes
        |> List.filter (fun ty -> ty.Name.Split('_').Length = 5)
        |> List.length
        |> shouldEqual 432

    [<Test>]
    let ``The TypeDef rule and the walk agree with the host on every type of the fixture`` () : unit =
        let mutable state = initialState
        let disagreements = ResizeArray<string> ()
        let mutable visible = 0

        for ty in fixtureTypes do
            let host = hostAssembly.ManifestModule.ResolveType (typeDefToken ty.TypeDefHandle)

            if host.IsVisible then
                visible <- visible + 1

            if TypeVisibility.isTypeDefExternallyVisible guest ty <> host.IsVisible then
                disagreements.Add $"rule: %s{ty.Name}"

            let newState, target = definitionTarget state ty
            state <- newState

            if pawPrintAnswer state target <> host.IsVisible then
                disagreements.Add $"walk: %s{ty.Name}"

        List.ofSeq disagreements |> shouldEqual []
        // `C_P` and its all-public chains at each depth, the public members of the generic forest and
        // the construction pool's public types.
        visible |> shouldBeGreaterThan 4
        visible |> shouldBeSmallerThan fixtureTypes.Length

    // ----- Constructions -----

    type private Leaf =
        /// A non-generic type, closed.
        | Nominal of Type
        /// A generic type definition, bare.
        | Definition of Type
        /// The type parameter at a position of a generic type definition.
        | TypeParam of Type * int
        /// The method parameter at a position of a generic method, by declaring type and name.
        | MethodParam of Type * string * int

    type private Tree =
        | Leaf of Leaf
        | Instantiate of Type * Tree list
        | SzArray of Tree
        | MdArray of Tree * int
        | Pointer of Tree
        | Byref of Tree
        /// The type of the fixture's `FnFields<T>` field at this index.
        | FnPtr of int

    type private Reach =
        | VisibleDefinitionInvisibleArgument
        | VisibleDefinitionInvisibleDeeperArgument
        | InvisibleUnderSzArray
        | InvisibleUnderMdArray
        | InvisibleUnderPointer
        | InvisibleUnderByref
        | OpenWithInvisibleClosedArgument
        | FunctionPointerInvisibleByReturnAlone
        | BareDefinitionAsArgument

    let private fixtureType (name : string) : Type =
        match hostAssembly.GetType name with
        | null -> failwith $"fixture type %s{name} not found"
        | t -> t

    let private coreLibType (name : string) : Type =
        match hostCoreLib.GetType name with
        | null -> failwith $"CoreLib type %s{name} not found"
        | t -> t

    let private allBindings : BindingFlags =
        BindingFlags.Public
        ||| BindingFlags.NonPublic
        ||| BindingFlags.Static
        ||| BindingFlags.Instance

    let private nominalPool : Type list =
        let chainTypes =
            guest.TypeDefs.Values
            |> Seq.filter (fun ty -> ty.Name.StartsWith "C_")
            |> Seq.map (fun ty -> hostAssembly.ManifestModule.ResolveType (typeDefToken ty.TypeDefHandle))
            |> List.ofSeq

        chainTypes
        @ [
            fixtureType "Pub"
            fixtureType "Internal"
            typeof<obj>
            typeof<int>
            typeof<string>
            coreLibType "System.RuntimeType"
        ]

    /// Definitions with no constraints, so that any non-pointer argument instantiates them.
    let private definitionPool : Type list =
        [
            fixtureType "PubG`1"
            fixtureType "InternalG`1"
            fixtureType "PubG2`2"
            fixtureType "GP`1"
            fixtureType "GP`1+NPub"
            fixtureType "GP`1+NAsm"
            fixtureType "GP`1+NG`1"
            fixtureType "GI`1"
            coreLibType "System.Collections.Generic.List`1"
            coreLibType "System.Collections.Generic.Dictionary`2"
            coreLibType "System.SZGenericArrayEnumerator`1"
        ]

    [<Test>]
    let ``Every definition the generator instantiates is unconstrained`` () : unit =
        for definition in definitionPool do
            for parameter in definition.GetGenericArguments () do
                parameter.GetGenericParameterConstraints () |> shouldEqual [||]

                parameter.GenericParameterAttributes &&& GenericParameterAttributes.SpecialConstraintMask
                |> shouldEqual GenericParameterAttributes.None

    let private parameterPool : Leaf list =
        [
            TypeParam (fixtureType "PubG`1", 0)
            TypeParam (fixtureType "InternalG`1", 0)
            TypeParam (fixtureType "GP`1+NG`1", 1)
            MethodParam (fixtureType "Gm", "M", 0)
            MethodParam (fixtureType "Gmi", "M", 0)
        ]

    let private fnFields : Type = fixtureType "FnFields`1"

    /// The function-pointer fields invisible by their return type alone: every parameter visible.
    let private invisibleByReturnAlone (parameters : string list, ret : string) : bool =
        (parameters |> List.forall (fun p -> p = "Pub" || p = "T"))
        && (ret = "Internal" || ret = "PubG<Internal>")

    let private returnAloneIndices : int list =
        fnSignatures
        |> List.indexed
        |> List.filter (snd >> invisibleByReturnAlone)
        |> List.map fst

    let private genLeaf (argument : bool) : Gen<Tree> =
        Gen.frequency
            [
                6, Gen.elements nominalPool |> Gen.map (Leaf.Nominal >> Tree.Leaf)
                2, Gen.elements definitionPool |> Gen.map (Leaf.Definition >> Tree.Leaf)
                2, Gen.elements parameterPool |> Gen.map Tree.Leaf
                (if argument then 0 else 2), Gen.choose (0, fnSignatures.Length - 1) |> Gen.map Tree.FnPtr
                // A rare shape among the 105 signatures, drawn on purpose so that the fixed sample
                // reaches it often.
                (if argument then 0 else 1), Gen.elements returnAloneIndices |> Gen.map Tree.FnPtr
            ]

    /// A tree the host can build: no pointer or function pointer beneath a generic argument, byref only
    /// outermost (`genTop`), instantiation only of the unconstrained `definitionPool`.
    let rec private genInner (argument : bool) (size : int) : Gen<Tree> =
        if size <= 0 then
            genLeaf argument
        else
            let smaller = genInner argument (size / 2)

            Gen.frequency
                [
                    3, genLeaf argument
                    4,
                    gen {
                        let! definition = Gen.elements definitionPool
                        let arity = definition.GetGenericArguments().Length
                        let! arguments = Gen.listOfLength arity (genInner true (size / 2))
                        return Tree.Instantiate (definition, arguments)
                    }
                    2, smaller |> Gen.map Tree.SzArray
                    1, Gen.map2 (fun e rank -> Tree.MdArray (e, rank)) smaller (Gen.choose (2, 3))
                    (if argument then 0 else 2), smaller |> Gen.map Tree.Pointer
                ]

    let private genTop : Gen<Tree> =
        Gen.sized (fun size ->
            let inner = genInner false (min size 8)
            // A fixed opening for the rarest label: a function pointer invisible by its return alone.
            let returnAlone = Gen.elements returnAloneIndices |> Gen.map Tree.FnPtr
            Gen.frequency [ 8, inner ; 2, inner |> Gen.map Tree.Byref ; 1, returnAlone ]
        )

    let rec private hostType (tree : Tree) : Type =
        match tree with
        | Tree.Leaf (Leaf.Nominal t)
        | Tree.Leaf (Leaf.Definition t) -> t
        | Tree.Leaf (Leaf.TypeParam (definition, position)) -> definition.GetGenericArguments().[position]
        | Tree.Leaf (Leaf.MethodParam (declaring, name, position)) ->
            declaring.GetMethod(name, allBindings).GetGenericArguments().[position]
        | Tree.Instantiate (definition, arguments) ->
            definition.MakeGenericType (arguments |> List.map hostType |> Array.ofList)
        | Tree.SzArray element -> (hostType element).MakeArrayType ()
        | Tree.MdArray (element, rank) -> (hostType element).MakeArrayType rank
        | Tree.Pointer element -> (hostType element).MakePointerType ()
        | Tree.Byref element -> (hostType element).MakeByRefType ()
        | Tree.FnPtr index -> fnFields.GetField(fnFieldName index, allBindings).FieldType

    /// A part of PawPrint's side: closed, as the `TypeDefn` to concretise, or open, as its target.
    type private Built =
        | ClosedDefn of TypeDefn
        | OpenTarget of RuntimeTypeHandleTarget

    let private targetOf (state : IlMachineState) (built : Built) : IlMachineState * RuntimeTypeHandleTarget =
        match built with
        | Built.OpenTarget target -> state, target
        | Built.ClosedDefn defn ->
            let state, handle = concretize state defn
            state, RuntimeTypeHandleTarget.Closed handle

    let private nominalDefn (t : Type) : TypeDefn =
        if t = typeof<int> then TypeDefn.PrimitiveType PrimitiveType.Int32
        elif t = typeof<string> then TypeDefn.PrimitiveType PrimitiveType.String
        elif t = typeof<obj> then TypeDefn.PrimitiveType PrimitiveType.Object
        else definitionDefn (snd (typeInfoOf t))

    let private applyShape (shape : CompositeShape) (built : Built) : Built =
        match built with
        | Built.ClosedDefn defn ->
            match shape with
            | CompositeShape.Byref -> TypeDefn.Byref defn
            | CompositeShape.Pointer -> TypeDefn.Pointer defn
            | CompositeShape.OneDimArrayZero -> TypeDefn.OneDimensionalArrayLowerBoundZero defn
            | CompositeShape.Array rank -> TypeDefn.Array (defn, rank)
            |> Built.ClosedDefn
        | Built.OpenTarget target -> Built.OpenTarget (RuntimeTypeHandleTarget.composite shape target)

    let private signatureHeader : ComparableSignatureHeader =
        ComparableSignatureHeader.Make (
            SignatureHeader (SignatureKind.Method, SignatureCallingConvention.Default, SignatureAttributes.None)
        )

    /// The type a function-pointer field's signature names, as PawPrint builds it.
    let private fnPart (name : string) : Built =
        match name with
        | "Pub" -> Built.ClosedDefn (nominalDefn (fixtureType "Pub"))
        | "Internal" -> Built.ClosedDefn (nominalDefn (fixtureType "Internal"))
        | "PubG<Internal>" ->
            Built.ClosedDefn (
                TypeDefn.GenericInstantiation (
                    definitionDefn (snd (typeInfoOf (fixtureType "PubG`1"))),
                    ImmutableArray.Create (nominalDefn (fixtureType "Internal"))
                )
            )
        | "T" ->
            Built.OpenTarget (RuntimeTypeHandleTarget.GenericParameter ((snd (typeInfoOf fnFields)).Identity, 0))
        | other -> failwith $"no function-pointer part %s{other}"

    let rec private build (state : IlMachineState) (tree : Tree) : IlMachineState * Built =
        match tree with
        | Tree.Leaf (Leaf.Nominal t) -> state, Built.ClosedDefn (nominalDefn t)
        | Tree.Leaf (Leaf.Definition t) ->
            state, Built.OpenTarget (RuntimeTypeHandleTarget.OpenGenericTypeDefinition (snd (typeInfoOf t)).Identity)
        | Tree.Leaf (Leaf.TypeParam (definition, position)) ->
            state,
            Built.OpenTarget (RuntimeTypeHandleTarget.GenericParameter ((snd (typeInfoOf definition)).Identity, position))
        | Tree.Leaf (Leaf.MethodParam (declaring, name, position)) ->
            let method = declaring.GetMethod (name, allBindings)

            let handle =
                ComparableMethodDefinitionHandle.Make (
                    MetadataTokens.MethodDefinitionHandle (method.MetadataToken &&& 0x00FFFFFF)
                )

            state,
            Built.OpenTarget (
                RuntimeTypeHandleTarget.MethodGenericParameter ((snd (typeInfoOf declaring)).Identity, handle, position)
            )
        | Tree.Instantiate (definition, arguments) ->
            let state, builtArguments =
                ((state, []), arguments)
                ||> List.fold (fun (state, acc) argument ->
                    let state, built = build state argument
                    state, built :: acc
                )

            let builtArguments = List.rev builtArguments
            let definitionInfo = snd (typeInfoOf definition)

            let closedArguments =
                builtArguments
                |> List.choose (fun built ->
                    match built with
                    | Built.ClosedDefn defn -> Some defn
                    | Built.OpenTarget _ -> None
                )

            if closedArguments.Length = builtArguments.Length then
                state,
                Built.ClosedDefn (
                    TypeDefn.GenericInstantiation (definitionDefn definitionInfo, ImmutableArray.CreateRange closedArguments)
                )
            else
                let state, targets =
                    ((state, []), builtArguments)
                    ||> List.fold (fun (state, acc) built ->
                        let state, target = targetOf state built
                        state, target :: acc
                    )

                state,
                Built.OpenTarget (RuntimeTypeHandleTarget.openConstructed definitionInfo.Identity (List.rev targets))
        | Tree.SzArray element ->
            let state, built = build state element
            state, applyShape CompositeShape.OneDimArrayZero built
        | Tree.MdArray (element, rank) ->
            let state, built = build state element
            state, applyShape (CompositeShape.Array rank) built
        | Tree.Pointer element ->
            let state, built = build state element
            state, applyShape CompositeShape.Pointer built
        | Tree.Byref element ->
            let state, built = build state element
            state, applyShape CompositeShape.Byref built
        | Tree.FnPtr index ->
            let parameters, ret = fnSignatures.[index]
            let parts = parameters |> List.map fnPart
            let retPart = if ret = "void" then None else Some (fnPart ret)

            let allClosed =
                parts @ Option.toList retPart
                |> List.forall (fun part ->
                    match part with
                    | Built.ClosedDefn _ -> true
                    | Built.OpenTarget _ -> false
                )

            if allClosed then
                let defnOf (part : Built) : TypeDefn =
                    match part with
                    | Built.ClosedDefn defn -> defn
                    | Built.OpenTarget _ -> failwith "unreachable: every part was checked closed"

                let signature : TypeMethodSignature<TypeDefn> =
                    {
                        Header = signatureHeader
                        ParameterTypes = parts |> List.map defnOf
                        GenericParameterCount = 0
                        RequiredParameterCount = parts.Length
                        ReturnType =
                            match retPart with
                            | None -> MethodReturnType.Void
                            | Some part -> MethodReturnType.Returns (defnOf part)
                    }

                state, Built.ClosedDefn (TypeDefn.FunctionPointer signature)
            else
                let state, parameterTargets =
                    ((state, []), parts)
                    ||> List.fold (fun (state, acc) part ->
                        let state, target = targetOf state part
                        state, target :: acc
                    )

                let state, retTarget =
                    match retPart with
                    | None -> state, MethodReturnType.Void
                    | Some part ->
                        let state, target = targetOf state part
                        state, MethodReturnType.Returns target

                let signature : TypeMethodSignature<RuntimeTypeHandleTarget> =
                    {
                        Header = signatureHeader
                        ParameterTypes = List.rev parameterTargets
                        GenericParameterCount = 0
                        RequiredParameterCount = parts.Length
                        ReturnType = retTarget
                    }

                state, Built.OpenTarget (RuntimeTypeHandleTarget.functionPointer signature)

    /// Records every label a tree's parts reach, judged by the host's answers.
    let rec private labels (cover : Reach -> unit) (tree : Tree) : unit =
        let invisible (t : Tree) : bool = not (hostType t).IsVisible

        let isLeaf (t : Tree) : bool =
            match t with
            | Tree.Leaf _ -> true
            | _ -> false

        match tree with
        | Tree.Leaf _ -> ()
        | Tree.Instantiate (definition, arguments) ->
            if definition.IsVisible then
                if arguments |> List.exists invisible then
                    cover Reach.VisibleDefinitionInvisibleArgument

                if arguments |> List.exists (fun a -> not (isLeaf a) && invisible a) then
                    cover Reach.VisibleDefinitionInvisibleDeeperArgument

            if (hostType tree).ContainsGenericParameters
               && arguments
                  |> List.exists (fun a -> not (hostType a).ContainsGenericParameters && invisible a) then
                cover Reach.OpenWithInvisibleClosedArgument

            if
                arguments
                |> List.exists (fun a ->
                    match a with
                    | Tree.Leaf (Leaf.Definition _) -> true
                    | _ -> false
                )
            then
                cover Reach.BareDefinitionAsArgument

            arguments |> List.iter (labels cover)
        | Tree.SzArray element ->
            if invisible element then
                cover Reach.InvisibleUnderSzArray

            labels cover element
        | Tree.MdArray (element, _) ->
            if invisible element then
                cover Reach.InvisibleUnderMdArray

            labels cover element
        | Tree.Pointer element ->
            if invisible element then
                cover Reach.InvisibleUnderPointer

            labels cover element
        | Tree.Byref element ->
            if invisible element then
                cover Reach.InvisibleUnderByref

            labels cover element
        | Tree.FnPtr index ->
            if invisibleByReturnAlone fnSignatures.[index] then
                cover Reach.FunctionPointerInvisibleByReturnAlone

    [<Test>]
    let ``PawPrint's answer for a drawn construction is the host's`` () : unit =
        let property (cover : Reach -> unit) (tree : Tree) : unit =
            let host = hostType tree
            let state, built = build initialState tree
            let state, target = targetOf state built
            labels cover tree

            if pawPrintAnswer state target <> host.IsVisible then
                failwith $"%O{host} (%A{tree}): PawPrint %b{not host.IsVisible}, the host %b{host.IsVisible}"

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 300) (Arb.fromGen genTop) property

        for label in
            [
                Reach.VisibleDefinitionInvisibleArgument
                Reach.VisibleDefinitionInvisibleDeeperArgument
                Reach.InvisibleUnderSzArray
                Reach.InvisibleUnderMdArray
                Reach.InvisibleUnderPointer
                Reach.InvisibleUnderByref
                Reach.OpenWithInvisibleClosedArgument
                Reach.FunctionPointerInvisibleByReturnAlone
                Reach.BareDefinitionAsArgument
            ] do
            if coverage.Count label < 15 then
                failwith $"the fixed sample reached %A{label} only %d{coverage.Count label} times"

    // ----- Targets no image declares -----

    [<Test>]
    let ``The dynamic-methods class and an array of it are visible`` () : unit =
        let dynamicClass = RuntimeTypeHandleTarget.DynamicMethodsClass guest.DefinitionFullName

        pawPrintAnswer initialState dynamicClass |> shouldEqual true

        let array = RuntimeTypeHandleTarget.composite CompositeShape.OneDimArrayZero dynamicClass

        match array with
        | RuntimeTypeHandleTarget.Composite _ -> ()
        | other -> failwith $"expected a Composite over the dynamic-methods class, got %O{other}"

        pawPrintAnswer initialState array |> shouldEqual true
