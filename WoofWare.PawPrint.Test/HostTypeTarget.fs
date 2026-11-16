namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Reflection.Metadata.Ecma335
open Microsoft.Extensions.Logging
open WoofWare.PawPrint

/// The `RuntimeTypeHandleTarget` a guest's `typeof` gives for a host `Type`, built from PawPrint's
/// reading of the same image, for a same-image oracle: a test that asks the host's CoreLib a question
/// about a `Type` and asks PawPrint the same question about the target built here.
///
/// A closed type is concretised into the state passed in; an open one (a generic definition, a type
/// variable, an open construction, and any shape over one) is answered as its target. A function
/// pointer is not built.
[<RequireQualifiedAccess>]
module HostTypeTarget =

    /// The host assemblies a type may come from, each with PawPrint's reading of the same image.
    type Images = (System.Reflection.Assembly * DumpedAssembly) list

    /// PawPrint's TypeDef for the definition of `t`, found in the image of `t`'s assembly.
    let typeInfoOf (images : Images) (t : Type) : TypeInfo<GenericParamFromMetadata, TypeDefn> =
        let assembly =
            images
            |> List.tryFind (fun (host, _) -> Object.ReferenceEquals (host, t.Assembly))
            |> Option.map snd
            |> Option.defaultWith (fun () -> failwith $"%O{t} is in none of the images under test")

        assembly.TypeDefs.[MetadataTokens.TypeDefinitionHandle (t.MetadataToken &&& 0x00FFFFFF)]

    let private primitives : (Type * PrimitiveType) list =
        [
            typeof<bool>, PrimitiveType.Boolean
            typeof<char>, PrimitiveType.Char
            typeof<sbyte>, PrimitiveType.SByte
            typeof<byte>, PrimitiveType.Byte
            typeof<int16>, PrimitiveType.Int16
            typeof<uint16>, PrimitiveType.UInt16
            typeof<int>, PrimitiveType.Int32
            typeof<uint32>, PrimitiveType.UInt32
            typeof<int64>, PrimitiveType.Int64
            typeof<uint64>, PrimitiveType.UInt64
            typeof<single>, PrimitiveType.Single
            typeof<double>, PrimitiveType.Double
            typeof<string>, PrimitiveType.String
            typeof<obj>.Assembly.GetType "System.TypedReference", PrimitiveType.TypedReference
            typeof<nativeint>, PrimitiveType.IntPtr
            typeof<unativeint>, PrimitiveType.UIntPtr
            typeof<obj>, PrimitiveType.Object
        ]

    /// A part of PawPrint's side: closed, as the `TypeDefn` to concretise, or open, as its target.
    type private Built =
        | ClosedDefn of TypeDefn
        | OpenTarget of RuntimeTypeHandleTarget

    let private definitionDefn
        (bct : BaseClassTypes<DumpedAssembly>)
        (state : IlMachineState)
        (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>)
        : TypeDefn
        =
        TypeDefn.FromDefinition (
            typeInfo.Identity,
            LoadedTypeInfo.signatureTypeKind bct state.TypeSystem._LoadedAssemblies typeInfo
        )

    let private targetOfBuilt
        (loggerFactory : ILoggerFactory)
        (bct : BaseClassTypes<DumpedAssembly>)
        (state : IlMachineState)
        (built : Built)
        : IlMachineState * RuntimeTypeHandleTarget
        =
        match built with
        | Built.OpenTarget target -> state, target
        | Built.ClosedDefn defn ->
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

    let rec private build
        (loggerFactory : ILoggerFactory)
        (bct : BaseClassTypes<DumpedAssembly>)
        (images : Images)
        (state : IlMachineState)
        (t : Type)
        : IlMachineState * Built
        =
        let build = build loggerFactory bct images

        if t.IsGenericParameter then
            match t.DeclaringMethod with
            | null ->
                state,
                Built.OpenTarget (
                    RuntimeTypeHandleTarget.GenericParameter (
                        (typeInfoOf images t.DeclaringType).Identity,
                        t.GenericParameterPosition
                    )
                )
            | method ->
                let handle =
                    ComparableMethodDefinitionHandle.Make (
                        MetadataTokens.MethodDefinitionHandle (method.MetadataToken &&& 0x00FFFFFF)
                    )

                state,
                Built.OpenTarget (
                    RuntimeTypeHandleTarget.MethodGenericParameter (
                        (typeInfoOf images method.DeclaringType).Identity,
                        handle,
                        t.GenericParameterPosition
                    )
                )
        elif t.IsByRef then
            let state, element = build state (t.GetElementType ())
            state, applyShape CompositeShape.Byref element
        elif t.IsPointer then
            let state, element = build state (t.GetElementType ())
            state, applyShape CompositeShape.Pointer element
        elif t.IsSZArray then
            let state, element = build state (t.GetElementType ())
            state, applyShape CompositeShape.OneDimArrayZero element
        elif t.IsArray then
            let state, element = build state (t.GetElementType ())
            state, applyShape (CompositeShape.Array (t.GetArrayRank ())) element
        elif t.IsFunctionPointer then
            failwith $"no PawPrint target is built here for the function pointer %O{t}"
        elif t.IsGenericTypeDefinition then
            state, Built.OpenTarget (RuntimeTypeHandleTarget.OpenGenericTypeDefinition (typeInfoOf images t).Identity)
        elif t.IsConstructedGenericType then
            let definition = typeInfoOf images (t.GetGenericTypeDefinition ())

            let state, arguments =
                ((state, []), t.GetGenericArguments ())
                ||> Array.fold (fun (state, acc) argument ->
                    let state, built = build state argument
                    state, built :: acc
                )

            let arguments = List.rev arguments

            let closed =
                arguments
                |> List.choose (fun built ->
                    match built with
                    | Built.ClosedDefn defn -> Some defn
                    | Built.OpenTarget _ -> None
                )

            if closed.Length = arguments.Length then
                state,
                Built.ClosedDefn (
                    TypeDefn.GenericInstantiation (
                        definitionDefn bct state definition,
                        ImmutableArray.CreateRange closed
                    )
                )
            else
                let state, targets =
                    ((state, []), arguments)
                    ||> List.fold (fun (state, acc) built ->
                        let state, target = targetOfBuilt loggerFactory bct state built
                        state, target :: acc
                    )

                state, Built.OpenTarget (RuntimeTypeHandleTarget.openConstructed definition.Identity (List.rev targets))
        else
            match primitives |> List.tryFind (fun (p, _) -> p = t) with
            | Some (_, primitive) -> state, Built.ClosedDefn (TypeDefn.PrimitiveType primitive)
            | None -> state, Built.ClosedDefn (definitionDefn bct state (typeInfoOf images t))

    /// The target a guest's `typeof` of `t` gives: the definition for a generic type definition, the
    /// variable for a generic parameter, and otherwise the type built from its parts.
    let target
        (loggerFactory : ILoggerFactory)
        (bct : BaseClassTypes<DumpedAssembly>)
        (images : Images)
        (state : IlMachineState)
        (t : Type)
        : IlMachineState * RuntimeTypeHandleTarget
        =
        let state, built = build loggerFactory bct images state t
        targetOfBuilt loggerFactory bct state built
