namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Runtime.InteropServices
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.EmitScopeGen

/// `EmitScopeDefinitions.defineCustomAttribute` and `classifyCustomAttribute`, `HasCustomAttribute.tryOfToken`,
/// `EmitScope.addCustomAttribute`, `EmitScope.typeOfCustomAttributeOwner` and `KnownCustomAttribute.effect`: each
/// against a model, and the classifier, the rows and the flags pseudo-attributes set against the host's own emitter,
/// the rows read raw through CoreLib's internal `MetadataImport`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeCustomAttributes =

    /// The two names `RegMeta::DefineCustomAttribute` treats specially after its known attributes.
    let private suppressName : string * string =
        "System.Security", "SuppressUnmanagedCodeSecurityAttribute"

    let private dynamicSecurityName : string * string =
        "System.Security", "DynamicSecurityMethodAttribute"

    let private ctorSignature : ImmutableArray<byte> =
        ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x01uy |]

    /// A scope with `Holder` (TypeDef 2), which has a method `M` (MethodDef 1) with a parameter and a generic parameter,
    /// a field, a property, an event and a generic parameter of its own; and an owner of each of the nine kinds.
    let private withOwners () : EmitScope * HasCustomAttribute list =
        let scope = emptyScope Guid.Empty

        let holder, scope =
            match EmitScopeDefinitions.defineTopLevelTypeDef "Holder" TypeAttributes.Public None [] scope with
            | TypeDefDefinition.Defined (rid, scope) -> rid, scope
            | TypeDefDefinition.Duplicate _ -> failwith "Holder is not yet defined"

        let method, scope =
            EmitScopeDefinitions.defineMethod
                holder
                "M"
                (ImmutableArray.Create<byte> [| 0x10uy ; 0x01uy ; 0x01uy ; 0x01uy ; 0x08uy |])
                (MethodAttributes.Public ||| MethodAttributes.Static)
                scope

        let param, scope =
            match EmitScopeDefinitions.defineParam method 1 "x" 0 scope with
            | ParamDefinition.Defined (rid, scope) -> rid, scope
            | ParamDefinition.StrandsLaterMethods _ -> failwith "one parameter strands nothing"

        let field, scope =
            EmitScopeDefinitions.defineField
                holder
                "F"
                (ImmutableArray.Create<byte> [| 0x06uy ; 0x08uy |])
                FieldAttributes.Public
                scope

        let property, scope =
            EmitScopeDefinitions.defineProperty
                holder
                "P"
                (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                0
                scope

        let event, scope =
            EmitScopeDefinitions.defineEvent holder "E" (TypeDefOrRef.TypeDef holder) 0 scope

        let typeParameter, scope =
            EmitScopeDefinitions.defineGenericParam (TypeOrMethodDef.TypeDef holder) 0us 0 "T" [] scope

        let methodParameter, scope =
            EmitScopeDefinitions.defineGenericParam (TypeOrMethodDef.MethodDef method) 0us 0 "U" [] scope

        scope,
        [
            HasCustomAttribute.Assembly
            HasCustomAttribute.Module
            HasCustomAttribute.TypeDef holder
            HasCustomAttribute.MethodDef method
            HasCustomAttribute.Field field
            HasCustomAttribute.Param param
            HasCustomAttribute.Property property
            HasCustomAttribute.Event event
            HasCustomAttribute.GenericParam typeParameter
            HasCustomAttribute.GenericParam methodParameter
        ]

    /// Where a constructor's declaring type is named, as the emitter reads it.
    [<RequireQualifiedAccess>]
    type private Parent =
        /// A MemberRef on a TypeRef whose resolution scope is an AssemblyRef.
        | ReferenceInAssembly of ns : string * name : string
        /// A MemberRef on a TypeRef whose resolution scope is the module.
        | ReferenceInModule of ns : string * name : string
        /// A MemberRef on a TypeRef nested in another TypeRef.
        | NestedReference of ns : string * name : string
        /// A MemberRef on a top-level TypeDef.
        | MemberOfDefinition of ns : string * name : string
        /// A MethodDef of a top-level TypeDef.
        | MethodOfDefinition of ns : string * name : string
        /// A MethodDef of a TypeDef whose visibility is nested.
        | MethodOfNestedDefinition of ns : string * name : string
        /// A MemberRef on a TypeSpec instantiating a TypeRef with this name: the TypeSpec has no name of its own.
        | MemberOfSpecification of ns : string * name : string
        /// A MemberRef on a MethodDef, a vararg call site's, which has no name.
        | MemberOfMethod

    let private typeDef (ns : string) (name : string) (flags : TypeAttributes) (scope : EmitScope) =
        let name, scope = EmitScope.internString name scope
        let ns, scope = EmitScope.internString ns scope

        EmitScope.addTypeDef
            {
                Flags = flags
                Name = name
                Namespace = ns
                Extends = None
            }
            scope

    let private typeRef (resolution : ResolutionScope) (ns : string) (name : string) (scope : EmitScope) =
        let name, scope = EmitScope.internString name scope
        let ns, scope = EmitScope.internString ns scope

        EmitScope.addTypeRef
            {
                ResolutionScope = resolution
                Name = name
                Namespace = ns
            }
            scope

    let private memberRef (parent : MemberRefParent) (scope : EmitScope) : MethodDefOrRef * EmitScope =
        let name, scope = EmitScope.internString ".ctor" scope
        let signature, scope = EmitScope.internBlob ctorSignature scope

        let rid, scope =
            EmitScope.addMemberRef
                {
                    Parent = parent
                    Name = name
                    Signature = signature
                }
                scope

        MethodDefOrRef.MemberRef rid, scope

    let private assemblyRef (scope : EmitScope) : AssemblyRefRid * EmitScope =
        let name, scope = EmitScope.internString "Other" scope

        EmitScope.addAssemblyRef
            {
                Version =
                    {
                        Major = 1us
                        Minor = 0us
                        Build = 0us
                        Revision = 0us
                    }
                Flags = enum<AssemblyFlags> 0
                PublicKeyOrToken = BlobOffset 0
                Name = name
                Culture = StringOffset 0
                HashValue = BlobOffset 0
            }
            scope

    /// `scope` with a constructor whose declaring type is named as `parent` says.
    let private constructorOf (parent : Parent) (scope : EmitScope) : MethodDefOrRef * EmitScope =
        match parent with
        | Parent.ReferenceInAssembly (ns, name) ->
            let other, scope = assemblyRef scope
            let target, scope = typeRef (ResolutionScope.AssemblyRef other) ns name scope
            memberRef (MemberRefParent.TypeRef target) scope
        | Parent.ReferenceInModule (ns, name) ->
            let target, scope = typeRef ResolutionScope.Module ns name scope
            memberRef (MemberRefParent.TypeRef target) scope
        | Parent.NestedReference (ns, name) ->
            let other, scope = assemblyRef scope
            let outer, scope = typeRef (ResolutionScope.AssemblyRef other) "" "Outer" scope
            let target, scope = typeRef (ResolutionScope.TypeRef outer) ns name scope
            memberRef (MemberRefParent.TypeRef target) scope
        | Parent.MemberOfDefinition (ns, name) ->
            let target, scope = typeDef ns name TypeAttributes.Public scope
            memberRef (MemberRefParent.TypeDef target) scope
        | Parent.MethodOfDefinition (ns, name) ->
            let target, scope = typeDef ns name TypeAttributes.Public scope

            let method, scope =
                EmitScopeDefinitions.defineMethod target ".ctor" ctorSignature MethodAttributes.Public scope

            MethodDefOrRef.MethodDef method, scope
        | Parent.MethodOfNestedDefinition (ns, name) ->
            let target, scope = typeDef ns name TypeAttributes.NestedPublic scope

            let method, scope =
                EmitScopeDefinitions.defineMethod target ".ctor" ctorSignature MethodAttributes.Public scope

            MethodDefOrRef.MethodDef method, scope
        | Parent.MemberOfSpecification (ns, name) ->
            let other, scope = assemblyRef scope
            let generic, scope = typeRef (ResolutionScope.AssemblyRef other) ns name scope

            // `GENERICINST CLASS <TypeRef> 1 int32`: an instantiation of the type with this name, which the emitter does
            // not read through a TypeSpec.
            let signature, scope =
                EmitScope.internBlob
                    (ImmutableArray.CreateRange (
                        [ 0x15uy ; 0x12uy ]
                        @ compressedUnsigned ((generic.Rid <<< 2) ||| 1)
                        @ [ 0x01uy ; 0x08uy ]
                    ))
                    scope

            let spec, scope =
                EmitScope.addTypeSpec
                    {
                        Signature = signature
                    }
                    scope

            memberRef (MemberRefParent.TypeSpec spec) scope
        | Parent.MemberOfMethod -> memberRef (MemberRefParent.MethodDef (EmitScope.tryMethodDefRid 1 scope).Value) scope

    /// What the emitter does with an attribute named `(ns, name)` on `owner`, whatever parent names it.
    let private expectedKind (ns : string, name : string) (owner : HasCustomAttribute) : CustomAttributeEmitKind =
        match
            KnownCustomAttribute.all
            |> List.tryFind (fun k -> KnownCustomAttribute.name k = (ns, name))
        with
        | Some known -> CustomAttributeEmitKind.Known known
        | None ->
            match owner with
            | HasCustomAttribute.TypeDef _
            | HasCustomAttribute.MethodDef _ when (ns, name) = suppressName ->
                CustomAttributeEmitKind.SuppressUnmanagedCodeSecurity
            | HasCustomAttribute.MethodDef _ when (ns, name) = dynamicSecurityName ->
                CustomAttributeEmitKind.DynamicSecurityMethod
            | _ -> CustomAttributeEmitKind.Ordinary

    [<Test>]
    let ``KnownCaList has twenty names, each once`` () : unit =
        KnownCustomAttribute.all |> List.length |> shouldEqual 20
        KnownCustomAttribute.all |> List.distinct |> List.length |> shouldEqual 20

        KnownCustomAttribute.all
        |> List.map KnownCustomAttribute.name
        |> List.distinct
        |> List.length
        |> shouldEqual 20

        KnownCustomAttribute.name KnownCustomAttribute.Serializable
        |> shouldEqual ("System", "SerializableAttribute")

        KnownCustomAttribute.name KnownCustomAttribute.WindowsRuntimeImport
        |> shouldEqual ("System.Runtime.InteropServices.WindowsRuntime", "WindowsRuntimeImportAttribute")

    [<Test>]
    let ``the classifier names each known attribute and security name, however its type is reached`` () : unit =
        let names =
            (KnownCustomAttribute.all |> List.map KnownCustomAttribute.name)
            @ [ suppressName ; dynamicSecurityName ]

        let shapes : ((string * string) -> Parent) list =
            [
                Parent.ReferenceInAssembly
                Parent.ReferenceInModule
                Parent.NestedReference
                Parent.MemberOfDefinition
                Parent.MethodOfDefinition
                Parent.MethodOfNestedDefinition
            ]

        for name in names do
            for shape in shapes do
                let scope, owners = withOwners ()
                let constructor, scope = constructorOf (shape name) scope

                for owner in owners do
                    EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                    |> shouldEqual (expectedKind name owner)

    [<Test>]
    let ``the classifier finds nothing in a near miss or a parent with no name`` () : unit =
        let names =
            (KnownCustomAttribute.all |> List.map KnownCustomAttribute.name)
            @ [ suppressName ; dynamicSecurityName ]

        let nearMisses (ns : string, name : string) : (string * string) list =
            [
                ns + ".Other", name
                ns, name.ToLowerInvariant ()
                ns, name.ToUpperInvariant ()
                ns, name + "2"
                "", ns + "." + name
                "", name
            ]

        for name in names do
            for miss in nearMisses name do
                for shape in [ Parent.ReferenceInAssembly ; Parent.MethodOfDefinition ] do
                    let scope, owners = withOwners ()
                    let constructor, scope = constructorOf (shape miss) scope

                    for owner in owners do
                        EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                        |> shouldEqual CustomAttributeEmitKind.Ordinary

        let unnamed =
            Parent.MemberOfMethod :: (names |> List.map Parent.MemberOfSpecification)

        for shape in unnamed do
            let scope, owners = withOwners ()
            let constructor, scope = constructorOf shape scope

            for owner in owners do
                EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                |> shouldEqual CustomAttributeEmitKind.Ordinary

    /// `scope` with constructors whose declaring types have every known name, a security name, and an ordinary name.
    let private withNamedConstructors (scope : EmitScope) : EmitScope =
        [
            yield! KnownCustomAttribute.all |> List.map KnownCustomAttribute.name
            suppressName
            dynamicSecurityName
            "System", "ObsoleteAttribute"
        ]
        |> List.fold (fun scope name -> constructorOf (Parent.ReferenceInModule name) scope |> snd) scope

    /// <summary>
    /// The pseudo-attributes that set a flag in their owner's row, each with the owners it accepts and the bit it sets
    /// in each, transcribed from md/compiler/custattr_emit.cpp at the pinned .NET 10 source: the target list
    /// (<c>rTypes</c>) and the <c>AddFlags</c> of <c>RegMeta::_HandleKnownCustomAttribute</c>. An owner is named as
    /// <c>ownerKind</c> names it; "method implementation" is a method's <c>ImplFlags</c>. Every other known name is
    /// unmodelled.
    /// </summary>
    let private flagTable : (KnownCustomAttribute * (string * int) list) list =
        [
            // SerializableTargets :106 (mdtTypeDef); SerializableAttributeProps :236; AddFlags(tdSerializable) :1278.
            KnownCustomAttribute.Serializable, [ "type", 0x2000 ]
            // NonSerializedTargets :108 (mdtFieldDef); :240; AddFlags(fdNotSerialized) :1282.
            KnownCustomAttribute.NonSerialized, [ "field", 0x0080 ]
            // SpecialNameTargets :117 (mdtTypeDef, mdtMethodDef, mdtFieldDef, mdtProperty, mdtEvent); :244;
            // tdSpecialName :1252, mdSpecialName :1256, fdSpecialName :1260, prSpecialName :1264, evSpecialName :1268.
            KnownCustomAttribute.SpecialName,
            [
                "type", 0x0400
                "method", 0x0800
                "field", 0x0200
                "property", 0x0200
                "event", 0x0200
            ]
            // InOutTargets :112 (mdtParamDef); InAttributeProps :341; AddFlags(pdIn) :1286.
            KnownCustomAttribute.In, [ "parameter", 0x0001 ]
            // InOutTargets :112; OutAttributeProps :345; AddFlags(pdOut) :1290.
            KnownCustomAttribute.Out, [ "parameter", 0x0002 ]
            // InOutTargets :112; OptionalAttributeProps :349; AddFlags(pdOptional) :1294.
            KnownCustomAttribute.Optional, [ "parameter", 0x0010 ]
            // PreserveSigTargets :111 (mdtMethodDef); :337; AddImplFlags(miPreserveSig) :1332.
            KnownCustomAttribute.PreserveSig, [ "method implementation", 0x0080 ]
        ]

    /// <summary>
    /// The pseudo-attributes that CoreCLR's emitter validates and then keeps as a row, each with the owners it accepts
    /// (named as <c>ownerKind</c> names them), the arguments it reads and the rule they must keep, transcribed from
    /// md/compiler/custattr_emit.cpp at the pinned .NET 10 source: the target list (<c>rTypes</c>, without
    /// <c>mdtTypeRef</c>), the declared arguments (<c>DEFINE_CA_CTOR_ARGS</c>) and the checks of
    /// <c>RegMeta::_HandleKnownCustomAttribute</c>, with the two bounds from inc/metadata.h.
    /// </summary>
    let private keptTable
        : (KnownCustomAttribute * (string list * KnownCustomAttributeArgument list * KnownCustomAttributeValueRule)) list =
        [
            // GuidTargets :102 (mdtTypeDef, mdtTypeRef, mdtModule, mdtAssembly); GuidAttribute args :205 (STRING);
            // 36 characters that LPCSTRToGuid reads inside braces :1203.
            KnownCustomAttribute.Guid,
            ([ "type" ; "module" ; "assembly" ],
             [ KnownCustomAttributeArgument.String ],
             KnownCustomAttributeValueRule.Guid)
            // InterfaceTypeTargets :104 (mdtTypeDef); args :218 (U2); `u2 >= ifLast` refused :1231, ifLast = 4
            // (metadata.h:45).
            KnownCustomAttribute.InterfaceType,
            ([ "type" ], [ KnownCustomAttributeArgument.U2 ], KnownCustomAttributeValueRule.Below 4us)
            // ClassInterfaceTargets :105 (mdtTypeDef, mdtAssembly, mdtTypeRef); args :227 (U2); `u2 >= clsIfLast` refused
            // :1239, clsIfLast = 3 (metadata.h:58).
            KnownCustomAttribute.ClassInterface,
            ([ "type" ; "assembly" ], [ KnownCustomAttributeArgument.U2 ], KnownCustomAttributeValueRule.Below 3us)
            // TypeLibVersionTargets :115 (mdtAssembly, mdtTypeRef); args :394 (I4, I4); either negative refused :1416.
            KnownCustomAttribute.TypeLibVersion,
            ([ "assembly" ],
             [ KnownCustomAttributeArgument.I4 ; KnownCustomAttributeArgument.I4 ],
             KnownCustomAttributeValueRule.NonNegative)
            // ComCompatibleVersionTargets :116 (mdtAssembly, mdtTypeRef); args :403 (four I4); any negative refused
            // :1421.
            KnownCustomAttribute.ComCompatibleVersion,
            ([ "assembly" ], List.replicate 4 KnownCustomAttributeArgument.I4, KnownCustomAttributeValueRule.NonNegative)
            // AllowPartiallyTrustedCallersTargets :118 (mdtAssembly, mdtTypeRef); no arguments :416, so the blob is never
            // parsed (:1044); no check :1426.
            KnownCustomAttribute.AllowPartiallyTrustedCallers,
            ([ "assembly" ], [], KnownCustomAttributeValueRule.Unchecked)
        ]

    /// An argument as the model of the emitter's blob parser reads it.
    [<RequireQualifiedAccess>]
    type private ModelValue =
        | U2 of int
        | I4 of int
        | String of byte list option

    let private ofValue (value : KnownCustomAttributeValue) : ModelValue =
        match value with
        | KnownCustomAttributeValue.U2 v -> ModelValue.U2 (int v)
        | KnownCustomAttributeValue.I4 v -> ModelValue.I4 v
        | KnownCustomAttributeValue.String s -> ModelValue.String (s |> Option.map List.ofSeq)

    /// <summary>
    /// A model of CoreCLR's <c>CustomAttributeParser</c> as <c>ParseKnownCaArgs</c> and <c>ParseKnownCaNamedArgs</c> use
    /// it for an attribute without named arguments (md/compiler/custattr_emit.cpp, inc/caparser.h, and
    /// <c>CPackedLen::SafeGetLength</c> in utilcode/stgpooli.cpp), over a list of bytes.
    /// </summary>
    let private modelParse
        (arguments : KnownCustomAttributeArgument list)
        (blob : byte list)
        : Result<ModelValue list, KnownCustomAttributeRefusal>
        =
        // A packed length's three forms, each taken whatever value it holds; any other first byte fails.
        let packedLength (bytes : byte list) : (int * byte list) option =
            match bytes with
            | b :: rest when b &&& 0x80uy = 0uy -> Some (int b, rest)
            | b :: c :: rest when b &&& 0xC0uy = 0x80uy -> Some (int (b &&& 0x3Fuy) * 0x100 + int c, rest)
            | b :: c :: d :: e :: rest when b &&& 0xE0uy = 0xC0uy ->
                Some (int (b &&& 0x1Fuy) * 0x1000000 + int c * 0x10000 + int d * 0x100 + int e, rest)
            | _ -> None

        let readOne (argument : KnownCustomAttributeArgument) (bytes : byte list) : (ModelValue * byte list) option =
            match argument, bytes with
            | KnownCustomAttributeArgument.U2, lo :: hi :: rest -> Some (ModelValue.U2 (int lo + 0x100 * int hi), rest)
            | KnownCustomAttributeArgument.I4, a :: b :: c :: d :: rest ->
                Some (ModelValue.I4 (BitConverter.ToInt32 ([| a ; b ; c ; d |], 0)), rest)
            | KnownCustomAttributeArgument.String, 0xFFuy :: rest -> Some (ModelValue.String None, rest)
            | KnownCustomAttributeArgument.String, _ ->
                match packedLength bytes with
                | Some (count, rest) when List.length rest >= count ->
                    Some (ModelValue.String (Some (List.take count rest)), List.skip count rest)
                | _ -> None
            | _ -> None

        let rec readAll
            (arguments : KnownCustomAttributeArgument list)
            (bytes : byte list)
            (read : ModelValue list)
            : (ModelValue list * byte list) option
            =
            match arguments with
            | [] -> Some (List.rev read, bytes)
            | argument :: arguments ->
                readOne argument bytes
                |> Option.bind (fun (value, bytes) -> readAll arguments bytes (value :: read))

        match blob with
        | 0x01uy :: 0x00uy :: rest ->
            match readAll arguments rest [] with
            | None -> Error KnownCustomAttributeRefusal.MalformedBlob
            | Some (values, tail) ->
                match tail with
                | lo :: hi :: _ when BitConverter.ToInt16 ([| lo ; hi |], 0) > 0s ->
                    Error KnownCustomAttributeRefusal.NamedArgument
                | _ -> Ok values
        | _ -> Error KnownCustomAttributeRefusal.MalformedBlob

    /// Whether `values` keep `rule`, as a model of the checks of `RegMeta::_HandleKnownCustomAttribute`.
    let private modelKeeps (rule : KnownCustomAttributeValueRule) (values : ModelValue list) : bool =
        match rule, values with
        | KnownCustomAttributeValueRule.Below bound, [ ModelValue.U2 v ] -> v < int bound
        | KnownCustomAttributeValueRule.Guid, [ ModelValue.String None ] -> false
        | KnownCustomAttributeValueRule.Guid, [ ModelValue.String (Some bytes) ] ->
            Text.RegularExpressions.Regex.IsMatch (
                System.String (bytes |> List.map char |> Array.ofList),
                "^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\\z"
            )
        | KnownCustomAttributeValueRule.NonNegative, values ->
            values
            |> List.forall (fun value ->
                match value with
                | ModelValue.I4 v -> v >= 0
                | other -> failwith $"NonNegative applies to I4 arguments only, not %A{other}"
            )
        | KnownCustomAttributeValueRule.Unchecked, _ -> true
        | rule, values -> failwith $"the table pairs %A{rule} with arguments that read as %A{values}"

    /// What the emitter does with a validated pseudo-attribute of `keptTable` on an owner of kind `owner`: the target
    /// checked first, then the blob parsed only if the attribute declares arguments, then the rule.
    let private modelKept
        (targets : string list, arguments : KnownCustomAttributeArgument list, rule : KnownCustomAttributeValueRule)
        (owner : string)
        (blob : byte list)
        : Result<unit, KnownCustomAttributeRefusal>
        =
        if not (List.contains owner targets) then
            Error KnownCustomAttributeRefusal.WrongTarget
        elif arguments.IsEmpty then
            Ok ()
        else
            modelParse arguments blob
            |> Result.bind (fun values ->
                if modelKeeps rule values then
                    Ok ()
                else
                    Error KnownCustomAttributeRefusal.InvalidValue
            )

    /// A GUID's 36 characters, as the emitter accepts them.
    let private guidText : string = "01234567-89ab-cdef-0123-456789abcdef"

    let private bytesOf (text : string) : byte list =
        text |> Seq.map (fun c -> byte c) |> List.ofSeq

    let private int16Bytes (value : int) : byte list =
        [ byte (value &&& 0xFF) ; byte ((value >>> 8) &&& 0xFF) ]

    let private int32Bytes (value : int) : byte list =
        List.ofArray (BitConverter.GetBytes value)

    /// A well-formed named argument, a property or field `int Foo = 1` (`SERIALIZATION_TYPE_PROPERTY` or `_FIELD`,
    /// `ELEMENT_TYPE_I4`).
    let private namedArgument (property : bool) : byte list =
        [ (if property then 0x54uy else 0x53uy) ; 0x08uy ; 3uy ]
        @ bytesOf "Foo"
        @ int32Bytes 1

    /// <summary>
    /// A generator of blobs around the grammar of the emitter's parser, for an attribute declaring
    /// <paramref name="arguments"/>, each with the features it was built with: a prolog right or wrong, each argument
    /// whole, truncated or, for a <c>U2</c>, written as a four-byte enum; a string null, as a GUID with its variants or
    /// otherwise, with its length in each packed form, past the end or with a first byte that no form allows; then
    /// nothing, one byte, or a named-argument count followed by nothing, junk or a well-formed named argument. One in
    /// ten is random bytes.
    /// </summary>
    let private genGrammarBlob (arguments : KnownCustomAttributeArgument list) : Gen<byte list * Set<string>> =
        let tagged (feature : string) (g : Gen<byte list>) : Gen<byte list * Set<string>> =
            g |> Gen.map (fun bytes -> bytes, Set.singleton feature)

        let anyByte : Gen<byte> = Gen.choose (0, 255) |> Gen.map byte

        let prolog : Gen<byte list * Set<string>> =
            Gen.frequency
                [
                    17, Gen.constant ([ 1uy ; 0uy ], Set.empty)
                    3,
                    Gen.elements
                        [
                            []
                            [ 1uy ]
                            [ 1uy ; 1uy ]
                            [ 2uy ; 0uy ]
                            [ 0xFFuy ; 0xFFuy ]
                            [ 0uy ; 1uy ]
                        ]
                    |> tagged "prolog wrong"
                ]

        let u2 : Gen<byte list * Set<string>> =
            Gen.frequency
                [
                    6,
                    Gen.oneof
                        [
                            Gen.elements [ 0 ; 1 ; 2 ; 3 ; 4 ; 5 ; 0x7FFF ; 0x8000 ; 0xFFFF ]
                            Gen.choose (0, 0xFFFF)
                        ]
                    |> Gen.map (fun v -> int16Bytes v, Set.empty)
                    3,
                    gen {
                        let! low = Gen.choose (0, 4)
                        let! high = Gen.frequency [ 2, Gen.constant 0 ; 1, Gen.constant 1 ; 1, Gen.choose (2, 0xFFFF) ]

                        let feature =
                            match high with
                            | 0 -> "enum high half 0"
                            | 1 -> "enum high half 1"
                            | _ -> "enum high half other"

                        return int16Bytes low @ int16Bytes high, Set.singleton feature
                    }
                    1, Gen.elements [ [] ; [ 1uy ] ] |> tagged "argument truncated"
                ]

        let i4 : Gen<byte list * Set<string>> =
            Gen.frequency
                [
                    5,
                    Gen.oneof [ Gen.elements [ 0 ; 1 ; 2 ; Int32.MaxValue ] ; Gen.choose (0, Int32.MaxValue) ]
                    |> Gen.map (fun v -> int32Bytes v, Set.empty)
                    2,
                    Gen.oneof [ Gen.elements [ -1 ; Int32.MinValue ] ; Gen.choose (Int32.MinValue, -1) ]
                    |> Gen.map (fun v -> int32Bytes v, Set.singleton "I4 negative")
                    1,
                    gen {
                        let! n = Gen.choose (0, 3)
                        let! bytes = Gen.listOfLength n anyByte
                        return List.ofSeq bytes, Set.singleton "argument truncated"
                    }
                ]

        let payload : Gen<byte list * Set<string>> =
            let guid = bytesOf guidText

            let replaceAt (i : int) (b : byte) (bytes : byte list) =
                bytes |> List.mapi (fun j x -> if i = j then b else x)

            Gen.frequency
                [
                    3, Gen.constant guid |> tagged "guid lower"
                    2, Gen.constant (bytesOf (guidText.ToUpperInvariant ())) |> tagged "guid upper"
                    2,
                    Gen.constant (bytesOf "01234567-89AB-cdef-0123-456789ABcdef")
                    |> tagged "guid mixed"
                    1,
                    Gen.choose (0, 35)
                    |> Gen.map (fun i -> replaceAt i 0uy guid)
                    |> tagged "guid NUL"
                    1,
                    Gen.choose (0, 35)
                    |> Gen.map (fun i -> replaceAt i 0xC3uy guid)
                    |> tagged "guid non-ASCII"
                    1,
                    Gen.elements [ 8 ; 13 ; 18 ; 23 ]
                    |> Gen.map (fun d -> guid |> replaceAt d (byte 'a') |> replaceAt (d + 1) (byte '-'))
                    |> tagged "guid dash moved"
                    1,
                    Gen.elements
                        [
                            List.take 35 guid
                            guid @ [ byte 'a' ]
                            bytesOf ("{" + guidText + "}")
                            []
                        ]
                    |> tagged "guid wrong length"
                    1,
                    gen {
                        let! n = Gen.choose (0, 300)
                        let! bytes = Gen.listOfLength n (Gen.choose (0x20, 0x7E) |> Gen.map byte)
                        return List.ofSeq bytes, Set.empty
                    }
                ]

        let str : Gen<byte list * Set<string>> =
            Gen.frequency
                [
                    1, Gen.constant ([ 0xFFuy ], Set.singleton "null string")
                    1,
                    gen {
                        let! first = Gen.choose (0xE0, 0xFE)
                        let! rest = Gen.listOfLength 3 anyByte
                        let! text, features = payload
                        return (byte first :: List.ofSeq rest) @ text, Set.add "length E0-FE" features
                    }
                    8,
                    gen {
                        let! text, features = payload
                        let n = text.Length
                        let! over = Gen.frequency [ 7, Gen.constant 0 ; 1, Gen.choose (1, 3) ]

                        let! form =
                            Gen.elements (
                                [ "length two-byte" ; "length four-byte" ]
                                @ (if n + over < 0x80 then [ "length one-byte" ] else [])
                            )

                        let length = n + over

                        let encoded =
                            match form with
                            | "length one-byte" -> [ byte length ]
                            | "length two-byte" -> [ byte (0x80 ||| (length >>> 8)) ; byte (length &&& 0xFF) ]
                            | _ -> [ 0xC0uy ; 0uy ; byte (length >>> 8) ; byte (length &&& 0xFF) ]

                        let features = Set.add form features

                        return
                            encoded @ text,
                            (if over > 0 then
                                 Set.add "length past end" features
                             else
                                 features)
                    }
                ]

        let argument (argument : KnownCustomAttributeArgument) : Gen<byte list * Set<string>> =
            match argument with
            | KnownCustomAttributeArgument.U2 -> u2
            | KnownCustomAttributeArgument.I4 -> i4
            | KnownCustomAttributeArgument.String -> str

        let tail : Gen<byte list * Set<string>> =
            Gen.frequency
                [
                    4, Gen.constant ([], Set.empty)
                    1, anyByte |> Gen.map (fun b -> [ b ], Set.singleton "one trailing byte")
                    6,
                    gen {
                        let! count =
                            Gen.oneof
                                [
                                    Gen.elements [ -1 ; -32768 ; 0 ; 1 ; 0x7FFF ]
                                    Gen.choose (int Int16.MinValue, int Int16.MaxValue)
                                ]

                        let feature =
                            match count with
                            | -1
                            | -32768
                            | 0
                            | 1
                            | 0x7FFF -> $"count %d{count}"
                            | c when c < 0 -> "count other negative"
                            | _ -> "count other positive"

                        let! after =
                            Gen.frequency
                                [
                                    3, Gen.constant ([], Set.empty)
                                    2,
                                    gen {
                                        let! n = Gen.choose (1, 4)
                                        let! bytes = Gen.listOfLength n anyByte
                                        return List.ofSeq bytes, Set.singleton "junk after count"
                                    }
                                    2,
                                    Gen.elements [ true ; false ]
                                    |> Gen.map (fun p -> namedArgument p, Set.singleton "named argument")
                                ]

                        return int16Bytes count @ fst after, Set.add feature (snd after)
                    }
                ]

        let rec all (arguments : KnownCustomAttributeArgument list) : Gen<byte list * Set<string>> =
            match arguments with
            | [] -> Gen.constant ([], Set.empty)
            | a :: rest ->
                gen {
                    let! b, f = argument a
                    let! bs, fs = all rest
                    return b @ bs, Set.union f fs
                }

        let structured : Gen<byte list * Set<string>> =
            gen {
                let! p, pf = prolog
                let! a, af = all arguments
                let! t, tf = tail
                let bytes = p @ a @ t
                let features = Set.unionMany [ pf ; af ; tf ]

                return bytes, (if bytes.IsEmpty then Set.add "empty" features else features)
            }

        Gen.frequency
            [
                9, structured
                1,
                gen {
                    let! n = Gen.frequency [ 1, Gen.constant 0 ; 4, Gen.choose (1, 12) ]
                    let! bytes = Gen.listOfLength n anyByte

                    return
                        List.ofSeq bytes,
                        (if n = 0 then
                             Set.ofList [ "random" ; "empty" ]
                         else
                             Set.singleton "random")
                }
            ]

    /// What one generated call to `defineCustomAttribute` reached.
    [<RequireQualifiedAccess>]
    type private DefineReach =
        | Defined
        | NotStored
        | EqualPair
        | EmptyBlob
        | Owner of string
        | FlagSet of KnownCustomAttribute
        | KeptAny
        | Refused of KnownCustomAttribute
        | BitAlreadySet
        | BitNotSet

    let private ownerKind (owner : HasCustomAttribute) : string =
        match owner with
        | HasCustomAttribute.Assembly -> "assembly"
        | HasCustomAttribute.Module -> "module"
        | HasCustomAttribute.TypeDef _ -> "type"
        | HasCustomAttribute.MethodDef _ -> "method"
        | HasCustomAttribute.Field _ -> "field"
        | HasCustomAttribute.Param _ -> "parameter"
        | HasCustomAttribute.Property _ -> "property"
        | HasCustomAttribute.Event _ -> "event"
        | HasCustomAttribute.GenericParam _ -> "generic parameter"

    let private ownerKinds : string list =
        [
            "assembly"
            "module"
            "type"
            "method"
            "field"
            "parameter"
            "property"
            "event"
            "generic parameter"
        ]

    /// Every table and heap of `scope`, by name, as F# values that compare by content.
    let private tables (scope : EmitScope) : Map<string, obj> =
        Map.ofList
            [
                "strings", box (List.ofSeq (EmitScope.strings scope))
                "blobs", box (EmitScope.blobs scope |> Seq.map List.ofSeq |> List.ofSeq)
                "userStrings", box (EmitScope.userStrings scope |> Seq.map List.ofSeq |> List.ofSeq)
                "guids", box (List.ofSeq (EmitScope.guids scope))
                "module", box (EmitScope.moduleRow scope)
                "assembly", box (EmitScope.assembly scope)
                "assemblyRefs", box (List.ofSeq (EmitScope.assemblyRefs scope))
                "typeRefs", box (List.ofSeq (EmitScope.typeRefs scope))
                "typeDefs", box (List.ofSeq (EmitScope.typeDefs scope))
                "fields", box (List.ofSeq (EmitScope.fields scope))
                "methodDefs",
                box (
                    EmitScope.methodDefs scope
                    |> Seq.map (fun row ->
                        { row with
                            Body = None
                        },
                        row.Body |> Option.map List.ofSeq
                    )
                    |> List.ofSeq
                )
                "params", box (List.ofSeq (EmitScope.parameters scope))
                "memberRefs", box (List.ofSeq (EmitScope.memberRefs scope))
                "standAloneSigs", box (List.ofSeq (EmitScope.standAloneSigs scope))
                "typeSpecs", box (List.ofSeq (EmitScope.typeSpecs scope))
                "methodSpecs", box (List.ofSeq (EmitScope.methodSpecs scope))
                "genericParams", box (List.ofSeq (EmitScope.genericParams scope))
                "genericParamConstraints", box (List.ofSeq (EmitScope.genericParamConstraints scope))
                "interfaceImpls", box (List.ofSeq (EmitScope.interfaceImpls scope))
                "methodImpls", box (List.ofSeq (EmitScope.methodImpls scope))
                "properties", box (List.ofSeq (EmitScope.properties scope))
                "methodSemantics", box (List.ofSeq (EmitScope.methodSemantics scope))
                "events", box (List.ofSeq (EmitScope.events scope))
                "customAttributes", box (List.ofSeq (EmitScope.customAttributes scope))
            ]

    /// `before`'s tables with `bit` ORed into `owner`'s row, in the column `target` names (`flagTable`'s names).
    let private withBit (owner : HasCustomAttribute) (target : string) (bit : int) (before : EmitScope) =
        let update (rows : ImmutableList<'row>) (rid : int) (f : 'row -> 'row) : 'row list =
            rows |> Seq.mapi (fun i row -> if i = rid - 1 then f row else row) |> List.ofSeq

        let expected =
            match owner, target with
            | HasCustomAttribute.TypeDef rid, "type" ->
                "typeDefs",
                box (
                    update
                        (EmitScope.typeDefs before)
                        rid.Rid
                        (fun row ->
                            { row with
                                Flags = row.Flags ||| enum bit
                            }
                        )
                )
            | HasCustomAttribute.MethodDef rid, ("method" | "method implementation") ->
                "methodDefs",
                box (
                    update
                        (EmitScope.methodDefs before)
                        rid.Rid
                        (fun row ->
                            if target = "method" then
                                { row with
                                    Flags = row.Flags ||| enum bit
                                }
                            else
                                { row with
                                    ImplFlags = row.ImplFlags ||| enum bit
                                }
                        )
                    |> List.map (fun row ->
                        { row with
                            Body = None
                        },
                        row.Body |> Option.map List.ofSeq
                    )
                )
            | HasCustomAttribute.Field rid, "field" ->
                "fields",
                box (
                    update
                        (EmitScope.fields before)
                        rid.Rid
                        (fun row ->
                            { row with
                                Flags = row.Flags ||| enum bit
                            }
                        )
                )
            | HasCustomAttribute.Param rid, "parameter" ->
                "params",
                box (
                    update
                        (EmitScope.parameters before)
                        rid.Rid
                        (fun row ->
                            { row with
                                Flags = row.Flags ||| enum bit
                            }
                        )
                )
            | HasCustomAttribute.Property rid, "property" ->
                "properties",
                box (
                    update
                        (EmitScope.properties before)
                        rid.Rid
                        (fun row ->
                            { row with
                                Flags = row.Flags ||| enum bit
                            }
                        )
                )
            | HasCustomAttribute.Event rid, "event" ->
                "events",
                box (
                    update
                        (EmitScope.events before)
                        rid.Rid
                        (fun row ->
                            { row with
                                Flags = row.Flags ||| enum bit
                            }
                        )
                )
            | _ -> failwith $"%s{target} names no column of %A{owner}"

        tables before |> Map.add (fst expected) (snd expected)

    /// Whether `owner`'s row already has `bit` in the column `target` names.
    let private hasBit (owner : HasCustomAttribute) (target : string) (bit : int) (scope : EmitScope) : bool =
        let flags =
            match owner, target with
            | HasCustomAttribute.TypeDef rid, _ -> int (EmitScope.typeDefs scope).[rid.Rid - 1].Flags
            | HasCustomAttribute.MethodDef rid, "method" -> int (EmitScope.methodDefs scope).[rid.Rid - 1].Flags
            | HasCustomAttribute.MethodDef rid, _ -> int (EmitScope.methodDefs scope).[rid.Rid - 1].ImplFlags
            | HasCustomAttribute.Field rid, _ -> int (EmitScope.fields scope).[rid.Rid - 1].Flags
            | HasCustomAttribute.Param rid, _ -> int (EmitScope.parameters scope).[rid.Rid - 1].Flags
            | HasCustomAttribute.Property rid, _ -> int (EmitScope.properties scope).[rid.Rid - 1].Flags
            | HasCustomAttribute.Event rid, _ -> int (EmitScope.events scope).[rid.Rid - 1].Flags
            | _ -> failwith $"%A{owner} has no flags"

        flags &&& bit <> 0

    /// Blobs that a validated pseudo-attribute keeps on a right target, beside `customAttributeBlobs`, and some it
    /// refuses: a `U2` of 1 with a count of 0, a `U2` above both bounds, two `I4`s, four `I4`s with a negative one and
    /// without, a GUID, and a GUID followed by a named-argument count of 1.
    let private keptBlobs : ImmutableArray<byte> list =
        [
            [ 1uy ; 0uy ] @ int16Bytes 1 @ int16Bytes 0
            [ 1uy ; 0uy ] @ int16Bytes 5 @ int16Bytes 0
            [ 1uy ; 0uy ] @ int32Bytes 1 @ int32Bytes 2
            [ 1uy ; 0uy ]
            @ int32Bytes 1
            @ int32Bytes 2
            @ int32Bytes 3
            @ int32Bytes -4
            @ int16Bytes 0
            [ 1uy ; 0uy ]
            @ int32Bytes 1
            @ int32Bytes 2
            @ int32Bytes 3
            @ int32Bytes 4
            @ int16Bytes 0
            [ 1uy ; 0uy ; 36uy ] @ bytesOf guidText @ int16Bytes 0
            [ 1uy ; 0uy ; 36uy ] @ bytesOf guidText @ int16Bytes 1
        ]
        |> List.map ImmutableArray.CreateRange

    [<Test>]
    let ``an attribute stored as given is a new row, a flag pseudo-attribute sets its bit, and any other adds nothing``
        ()
        : unit
        =
        let blobs = customAttributeBlobs @ keptBlobs

        let property (cover : DefineReach -> unit) (scope : EmitScope) (calls : (int * int * int * bool) list) : unit =
            let scope = withNamedConstructors scope
            let owners = customAttributeOwners scope
            let constructors = customAttributeConstructors scope
            let mutable scope = scope

            let mutable previous : (HasCustomAttribute * MethodDefOrRef * ImmutableArray<byte>) option =
                None

            for ownerChoice, constructorChoice, blobChoice, repeat in calls do
                let owner, constructor, value =
                    match previous with
                    | Some call when repeat -> call
                    | _ ->
                        (pick ownerChoice owners).Value,
                        (pick constructorChoice constructors).Value,
                        (pick blobChoice blobs).Value

                let kind = EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                let before = EmitScope.customAttributes scope
                cover (DefineReach.Owner (ownerKind owner))

                // The model: a name of `flagTable` sets its bit on an owner it lists and is refused on any other.
                let flag =
                    match kind with
                    | CustomAttributeEmitKind.Known known ->
                        flagTable
                        |> List.tryFind (fun (k, _) -> k = known)
                        |> Option.map (fun (_, targets) ->
                            known,
                            targets
                            |> List.tryFind (fun (target, _) ->
                                target = ownerKind owner
                                || (target = "method implementation" && ownerKind owner = "method")
                            )
                        )
                    | _ -> None

                // The model: a name of `keptTable` is kept or refused as `modelKept` says.
                let kept =
                    match kind with
                    | CustomAttributeEmitKind.Known known ->
                        keptTable
                        |> List.tryFind (fun (k, _) -> k = known)
                        |> Option.map (fun (_, rule) -> known, modelKept rule (ownerKind owner) (List.ofSeq value))
                    | _ -> None

                match EmitScopeDefinitions.defineCustomAttribute owner constructor value scope with
                | CustomAttributeDefinition.Defined (rid, after) ->
                    match kept with
                    | Some (_, Ok ()) -> cover DefineReach.KeptAny
                    | Some (_, Error refusal) ->
                        failwith $"%A{kind} on %A{owner} was kept, but the model refuses it: %A{refusal}"
                    | None -> kind |> shouldEqual CustomAttributeEmitKind.Ordinary

                    rid.Rid |> shouldEqual (before.Count + 1)
                    let rows = EmitScope.customAttributes after
                    List.ofSeq (rows.RemoveAt before.Count) |> shouldEqual (List.ofSeq before)
                    let row = rows.[before.Count]
                    row.Parent |> shouldEqual owner
                    row.Constructor |> shouldEqual constructor
                    List.ofSeq (EmitScope.blobAt row.Value after) |> shouldEqual (List.ofSeq value)
                    // Nothing else changes but the blob heap, which may gain the blob.
                    EmitScopeExtension.violations ImmutableHashSet.Empty scope after
                    |> shouldEqual []

                    (EmitScope.typeDefs after).Count |> shouldEqual (EmitScope.typeDefs scope).Count
                    cover DefineReach.Defined

                    if value.IsEmpty then
                        cover DefineReach.EmptyBlob

                    if previous = Some (owner, constructor, value) && repeat then
                        cover DefineReach.EqualPair

                    scope <- after
                | CustomAttributeDefinition.FlagSet after ->
                    match flag with
                    | Some (known, Some (target, bit)) ->
                        tables after |> shouldEqual (withBit owner target bit scope)
                        cover (DefineReach.FlagSet known)

                        cover (
                            if hasBit owner target bit scope then
                                DefineReach.BitAlreadySet
                            else
                                DefineReach.BitNotSet
                        )

                        // The extension rule admits the bit, while the owner's type is not created.
                        EmitScopeExtension.violations ImmutableHashSet.Empty scope after
                        |> shouldEqual []

                        scope <- after
                    | _ -> failwith $"%A{kind} on %A{owner} set a flag, but the model expects %A{flag}"
                | CustomAttributeDefinition.Refused refusal ->
                    match flag, kept with
                    | Some (known, None), None ->
                        refusal |> shouldEqual KnownCustomAttributeRefusal.WrongTarget
                        cover (DefineReach.Refused known)
                    | None, Some (known, Error expected) ->
                        refusal |> shouldEqual expected
                        cover (DefineReach.Refused known)
                    | _ -> failwith $"%A{kind} on %A{owner} was refused, but the model expects %A{flag} or %A{kept}"
                | CustomAttributeDefinition.NotStoredAsGiven reported ->
                    reported |> shouldEqual kind
                    kind |> shouldNotEqual CustomAttributeEmitKind.Ordinary
                    flag |> shouldEqual None
                    kept |> shouldEqual None
                    cover DefineReach.NotStored

                previous <- Some (owner, constructor, value)

        let genCalls =
            Gen.listOf (
                Gen.zip (Gen.zip3 choice choice choice) (Gen.frequency [ 4, Gen.constant false ; 1, Gen.constant true ])
            )
            |> Gen.map (List.ofSeq >> List.map (fun ((o, c, b), r) -> o, c, b, r))

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 300)
                (Arb.fromGen (Gen.zip genScope genCalls))
                (fun cover (scope, calls) -> property cover scope calls)

        coverage.Count DefineReach.Defined |> shouldBeGreaterThan 500
        coverage.Count DefineReach.NotStored |> shouldBeGreaterThan 50
        coverage.Count DefineReach.EqualPair |> shouldBeGreaterThan 30
        coverage.Count DefineReach.EmptyBlob |> shouldBeGreaterThan 30
        coverage.Count DefineReach.BitAlreadySet |> shouldBeGreaterThan 20
        coverage.Count DefineReach.BitNotSet |> shouldBeGreaterThan 20

        for known, _ in flagTable do
            coverage.Count (DefineReach.FlagSet known) |> shouldBeGreaterThan 5
            coverage.Count (DefineReach.Refused known) |> shouldBeGreaterThan 5

        // `the validated pseudo-attributes are kept or refused as the model says` covers each of the six names; here
        // they are drawn among every other call.
        coverage.Count DefineReach.KeptAny |> shouldBeGreaterThan 5

        for kind in ownerKinds do
            coverage.Count (DefineReach.Owner kind) |> shouldBeGreaterThan 20

    [<RequireQualifiedAccess>]
    type private KeptReach =
        | Kept of KnownCustomAttribute
        | Refused of KnownCustomAttribute * KnownCustomAttributeRefusal
        | Impostor

    [<Test>]
    let ``the validated pseudo-attributes are kept or refused as the model says`` () : unit =
        let ownerKindsInOrder = withOwners () |> snd |> List.map ownerKind

        let genCase =
            gen {
                let! name, rule = Gen.elements keptTable
                let targets, arguments, _ = rule
                let! impostor = Gen.frequency [ 3, Gen.constant false ; 1, Gen.constant true ]
                let! count = Gen.choose (1, 12)

                // Half the owners are among the name's targets.
                let onTarget =
                    ownerKindsInOrder
                    |> List.indexed
                    |> List.filter (fun (_, kind) -> List.contains kind targets)
                    |> List.map fst

                let owner = Gen.frequency [ 1, Gen.elements onTarget ; 1, Gen.choose (0, 9) ]

                let! calls = Gen.listOfLength count (Gen.zip owner (genGrammarBlob arguments |> Gen.map fst))

                return name, rule, impostor, List.ofSeq calls
            }

        let property
            (cover : KeptReach -> unit)
            (name : KnownCustomAttribute, rule, impostor : bool, calls : (int * byte list) list)
            : unit
            =
            let scope, owners = withOwners ()

            let parent =
                if impostor then
                    Parent.MethodOfDefinition (KnownCustomAttribute.name name)
                else
                    Parent.ReferenceInAssembly (KnownCustomAttribute.name name)

            let constructor, scope = constructorOf parent scope
            let mutable scope = scope

            for ownerIndex, blob in calls do
                let owner = owners.[ownerIndex]
                let value = ImmutableArray.CreateRange blob
                let before = EmitScope.customAttributes scope

                match
                    EmitScopeDefinitions.defineCustomAttribute owner constructor value scope,
                    modelKept rule (ownerKind owner) blob
                with
                | CustomAttributeDefinition.Defined (rid, after), Ok () ->
                    rid.Rid |> shouldEqual (before.Count + 1)
                    let rows = EmitScope.customAttributes after
                    List.ofSeq (rows.RemoveAt before.Count) |> shouldEqual (List.ofSeq before)
                    let row = rows.[before.Count]
                    row.Parent |> shouldEqual owner
                    row.Constructor |> shouldEqual constructor
                    List.ofSeq (EmitScope.blobAt row.Value after) |> shouldEqual blob

                    EmitScopeExtension.violations ImmutableHashSet.Empty scope after
                    |> shouldEqual []

                    cover (KeptReach.Kept name)

                    if impostor then
                        cover KeptReach.Impostor

                    scope <- after
                | CustomAttributeDefinition.Refused refusal, Error expected ->
                    refusal |> shouldEqual expected
                    cover (KeptReach.Refused (name, refusal))
                | outcome, expected ->
                    failwith $"%A{name} on %A{owner} with %A{blob}: %A{outcome}, but the model expects %A{expected}"

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 1500)
                (Arb.fromGen genCase)
                (fun cover case -> property cover case)

        coverage.Count KeptReach.Impostor |> shouldBeGreaterThan 20

        for name, (_, arguments, rule) in keptTable do
            coverage.Count (KeptReach.Kept name) |> shouldBeGreaterThan 20

            coverage.Count (KeptReach.Refused (name, KnownCustomAttributeRefusal.WrongTarget))
            |> shouldBeGreaterThan 20

            if not arguments.IsEmpty then
                coverage.Count (KeptReach.Refused (name, KnownCustomAttributeRefusal.MalformedBlob))
                |> shouldBeGreaterThan 20

                coverage.Count (KeptReach.Refused (name, KnownCustomAttributeRefusal.NamedArgument))
                |> shouldBeGreaterThan 20

            if rule <> KnownCustomAttributeValueRule.Unchecked then
                coverage.Count (KeptReach.Refused (name, KnownCustomAttributeRefusal.InvalidValue))
                |> shouldBeGreaterThan 20

    [<Test>]
    let ``each pseudo-attribute that is validated and kept has CoreCLR's targets, arguments and rule, and only those six are``
        ()
        : unit
        =
        let tableOf (table : System.Reflection.Metadata.Ecma335.TableIndex) : string =
            match table with
            | System.Reflection.Metadata.Ecma335.TableIndex.Assembly -> "assembly"
            | System.Reflection.Metadata.Ecma335.TableIndex.Module -> "module"
            | System.Reflection.Metadata.Ecma335.TableIndex.TypeDef -> "type"
            | System.Reflection.Metadata.Ecma335.TableIndex.MethodDef -> "method"
            | System.Reflection.Metadata.Ecma335.TableIndex.Field -> "field"
            | System.Reflection.Metadata.Ecma335.TableIndex.Param -> "parameter"
            | System.Reflection.Metadata.Ecma335.TableIndex.Property -> "property"
            | System.Reflection.Metadata.Ecma335.TableIndex.Event -> "event"
            | System.Reflection.Metadata.Ecma335.TableIndex.GenericParam -> "generic parameter"
            | other -> $"%O{other}"

        KnownCustomAttribute.all
        |> List.choose (fun known ->
            match KnownCustomAttribute.effect known with
            | KnownCustomAttributeEffect.ValidatesAndKeeps validated ->
                Some (known, (validated.Targets |> Seq.map tableOf |> Set.ofSeq, validated.Arguments, validated.Rule))
            | KnownCustomAttributeEffect.SetsFlag _
            | KnownCustomAttributeEffect.Unmodelled -> None
        )
        |> Map.ofList
        |> shouldEqual (
            keptTable
            |> List.map (fun (known, (targets, arguments, rule)) -> known, (Set.ofList targets, arguments, rule))
            |> Map.ofList
        )

    [<Test>]
    let ``each pseudo-attribute that sets a flag accepts exactly CoreCLR's targets, and the rest are kept or unmodelled``
        ()
        : unit
        =
        let observed (known : KnownCustomAttribute) : (string * int) list option =
            match KnownCustomAttribute.effect known with
            | KnownCustomAttributeEffect.Unmodelled
            | KnownCustomAttributeEffect.ValidatesAndKeeps _ -> None
            | KnownCustomAttributeEffect.SetsFlag flags ->
                Some
                    [
                        match flags.TypeDef with
                        | Some bit -> yield "type", int bit
                        | None -> ()
                        match flags.MethodDef with
                        | Some (MethodDefFlag.Flags bit) -> yield "method", int bit
                        | Some (MethodDefFlag.ImplFlags bit) -> yield "method implementation", int bit
                        | None -> ()
                        match flags.Field with
                        | Some bit -> yield "field", int bit
                        | None -> ()
                        match flags.Param with
                        | Some bit -> yield "parameter", int bit
                        | None -> ()
                        match flags.Property with
                        | Some bit -> yield "property", int bit
                        | None -> ()
                        match flags.Event with
                        | Some bit -> yield "event", int bit
                        | None -> ()
                    ]

        KnownCustomAttribute.all
        |> List.choose (fun known -> observed known |> Option.map (fun targets -> known, targets))
        |> Map.ofList
        |> shouldEqual (Map.ofList flagTable)

    [<Test>]
    let ``the extension rule's masks are the bits the pseudo-attributes set`` () : unit =
        let mask = KnownCustomAttribute.flagMask

        [
            int mask.TypeDef
            int mask.MethodDef
            int mask.Field
            int mask.Param
            int mask.Property
            int mask.Event
        ]
        |> shouldEqual [ 0x2400 ; 0x0800 ; 0x0280 ; 0x0013 ; 0x0200 ; 0x0200 ]

    [<RequireQualifiedAccess>]
    type private ParseReach =
        | Feature of string
        | Parsed
        | Refused of KnownCustomAttributeRefusal

    [<Test>]
    let ``the blob parser reads what the model of CoreCLR's parser reads`` () : unit =
        let genArguments : Gen<KnownCustomAttributeArgument list> =
            Gen.frequency
                [
                    3, Gen.elements (keptTable |> List.map (fun (_, (_, arguments, _)) -> arguments))
                    1,
                    Gen.choose (0, 4)
                    |> Gen.bind (fun n ->
                        Gen.listOfLength
                            n
                            (Gen.elements
                                [
                                    KnownCustomAttributeArgument.U2
                                    KnownCustomAttributeArgument.I4
                                    KnownCustomAttributeArgument.String
                                ])
                    )
                    |> Gen.map List.ofSeq
                ]

        // The blob is built for one list of arguments and, one time in four, parsed by another.
        let genCase =
            gen {
                let! arguments = genArguments
                let! other = genArguments
                let! mismatched = Gen.frequency [ 3, Gen.constant false ; 1, Gen.constant true ]
                let! blob, features = genGrammarBlob (if mismatched then other else arguments)
                return arguments, blob, features
            }

        let property
            (cover : ParseReach -> unit)
            (arguments : KnownCustomAttributeArgument list, blob : byte list, features : Set<string>)
            : unit
            =
            for feature in features do
                cover (ParseReach.Feature feature)

            let actual =
                KnownCustomAttributeBlob.parse arguments (ImmutableArray.CreateRange blob)
                |> Result.map (List.map ofValue)

            actual |> shouldEqual (modelParse arguments blob)

            match actual with
            | Ok _ -> cover ParseReach.Parsed
            | Error refusal -> cover (ParseReach.Refused refusal)

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 3000)
                (Arb.fromGen genCase)
                (fun cover case -> property cover case)

        coverage.Count ParseReach.Parsed |> shouldBeGreaterThan 500

        coverage.Count (ParseReach.Refused KnownCustomAttributeRefusal.MalformedBlob)
        |> shouldBeGreaterThan 300

        coverage.Count (ParseReach.Refused KnownCustomAttributeRefusal.NamedArgument)
        |> shouldBeGreaterThan 300

        for feature in
            [
                "prolog wrong"
                "empty"
                "random"
                "enum high half 0"
                "enum high half 1"
                "enum high half other"
                "argument truncated"
                "I4 negative"
                "null string"
                "length one-byte"
                "length two-byte"
                "length four-byte"
                "length E0-FE"
                "length past end"
                "guid lower"
                "guid upper"
                "guid mixed"
                "guid NUL"
                "guid non-ASCII"
                "guid dash moved"
                "guid wrong length"
                "one trailing byte"
                "count -1"
                "count -32768"
                "count 0"
                "count 1"
                "count 32767"
                "count other negative"
                "count other positive"
                "junk after count"
                "named argument"
            ] do
            coverage.Count (ParseReach.Feature feature) |> shouldBeGreaterThan 20

    [<Test>]
    let ``a token names an owner exactly when it is a row of one of the nine tables`` () : unit =
        let property (scope : EmitScope) : unit =
            let tables : (int * int * (int -> HasCustomAttribute)) list =
                [
                    0x02, (EmitScope.typeDefs scope).Count, (fun i -> HasCustomAttribute.TypeDef (TypeDefRid i))
                    0x06, (EmitScope.methodDefs scope).Count, (fun i -> HasCustomAttribute.MethodDef (MethodDefRid i))
                    0x04, (EmitScope.fields scope).Count, (fun i -> HasCustomAttribute.Field (FieldRid i))
                    0x08, (EmitScope.parameters scope).Count, (fun i -> HasCustomAttribute.Param (ParamRid i))
                    0x17, (EmitScope.properties scope).Count, (fun i -> HasCustomAttribute.Property (PropertyRid i))
                    0x14, (EmitScope.events scope).Count, (fun i -> HasCustomAttribute.Event (EventRid i))
                    0x2A,
                    (EmitScope.genericParams scope).Count,
                    (fun i -> HasCustomAttribute.GenericParam (GenericParamRid i))
                ]

            for table, count, owner in tables do
                for i in 1..count do
                    HasCustomAttribute.tryOfToken ((table <<< 24) ||| i) scope
                    |> shouldEqual (Some (owner i))

                HasCustomAttribute.tryOfToken ((table <<< 24) ||| (count + 1)) scope
                |> shouldEqual None

                HasCustomAttribute.tryOfToken (table <<< 24) scope |> shouldEqual None

            HasCustomAttribute.tryOfToken 0x00000001 scope
            |> shouldEqual (Some HasCustomAttribute.Module)

            HasCustomAttribute.tryOfToken 0x00000002 scope |> shouldEqual None
            HasCustomAttribute.tryOfToken 0x00000000 scope |> shouldEqual None

            HasCustomAttribute.tryOfToken 0x20000001 scope
            |> shouldEqual (Some HasCustomAttribute.Assembly)

            HasCustomAttribute.tryOfToken 0x20000002 scope |> shouldEqual None

            // Every other table, at a RID its own table may have.
            for table in
                [
                    0x01
                    0x09
                    0x0A
                    0x0C
                    0x0E
                    0x11
                    0x1A
                    0x1B
                    0x23
                    0x26
                    0x27
                    0x28
                    0x2B
                    0x2C
                    0x70
                ] do
                for i in 1..3 do
                    HasCustomAttribute.tryOfToken ((table <<< 24) ||| i) scope |> shouldEqual None

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 100, Prop.forAll (Arb.fromGen genScope) property)

        // A scope with no Assembly row has no assembly to own an attribute.
        HasCustomAttribute.tryOfToken 0x20000001 (EmitScope.create "M" Guid.Empty)
        |> shouldEqual None

    [<Test>]
    let ``a row naming an owner, a constructor or a blob the scope lacks is refused`` () : unit =
        let scope, owners = withOwners ()
        let constructor = MethodDefOrRef.MethodDef (EmitScope.tryMethodDefRid 1 scope).Value

        let row (owner : HasCustomAttribute) (constructor : MethodDefOrRef) (value : BlobOffset) : CustomAttributeRow =
            {
                Parent = owner
                Constructor = constructor
                Value = value
            }

        for owner in owners do
            EmitScope.addCustomAttribute (row owner constructor (BlobOffset 0)) scope
            |> fst
            |> _.Rid
            |> shouldEqual 1

        let absent =
            [
                HasCustomAttribute.TypeDef (TypeDefRid 3)
                HasCustomAttribute.MethodDef (MethodDefRid 2)
                HasCustomAttribute.Field (FieldRid 2)
                HasCustomAttribute.Param (ParamRid 2)
                HasCustomAttribute.Property (PropertyRid 2)
                HasCustomAttribute.Event (EventRid 2)
                HasCustomAttribute.GenericParam (GenericParamRid 3)
            ]

        for owner in absent do
            Assert.Throws<ArgumentException> (fun () ->
                EmitScope.addCustomAttribute (row owner constructor (BlobOffset 0)) scope
                |> ignore<CustomAttributeRid * EmitScope>
            )
            |> ignore<ArgumentException>

        Assert.Throws<ArgumentException> (fun () ->
            EmitScope.addCustomAttribute
                (row HasCustomAttribute.Assembly constructor (BlobOffset 0))
                (EmitScope.create "M" Guid.Empty)
            |> ignore<CustomAttributeRid * EmitScope>
        )
        |> ignore<ArgumentException>

        for constructor in
            [
                MethodDefOrRef.MethodDef (MethodDefRid 2)
                MethodDefOrRef.MemberRef (MemberRefRid 1)
            ] do
            Assert.Throws<ArgumentException> (fun () ->
                EmitScope.addCustomAttribute (row HasCustomAttribute.Module constructor (BlobOffset 0)) scope
                |> ignore<CustomAttributeRid * EmitScope>
            )
            |> ignore<ArgumentException>

        Assert.Throws<ArgumentException> (fun () ->
            EmitScope.addCustomAttribute (row HasCustomAttribute.Module constructor (BlobOffset 99999)) scope
            |> ignore<CustomAttributeRid * EmitScope>
        )
        |> ignore<ArgumentException>

    [<Test>]
    let ``an owner's type is found by walking the rows it belongs to`` () : unit =
        let model (scope : EmitScope) (owner : HasCustomAttribute) : TypeDefRid option =
            let methodOwner (method : MethodDefRid) =
                (EmitScope.methodDefs scope).[method.Rid - 1].Owner

            match owner with
            | HasCustomAttribute.Assembly
            | HasCustomAttribute.Module -> None
            | HasCustomAttribute.TypeDef t -> Some t
            | HasCustomAttribute.MethodDef m -> Some (methodOwner m)
            | HasCustomAttribute.Field f -> Some (EmitScope.fields scope).[f.Rid - 1].Owner
            | HasCustomAttribute.Param p -> Some (methodOwner (EmitScope.parameters scope).[p.Rid - 1].Owner)
            | HasCustomAttribute.Property p -> Some (EmitScope.properties scope).[p.Rid - 1].Owner
            | HasCustomAttribute.Event e -> Some (EmitScope.events scope).[e.Rid - 1].Owner
            | HasCustomAttribute.GenericParam g ->
                match (EmitScope.genericParams scope).[g.Rid - 1].Owner with
                | TypeOrMethodDef.TypeDef t -> Some t
                | TypeOrMethodDef.MethodDef m -> Some (methodOwner m)

        let property (cover : string -> unit) (scope : EmitScope) : unit =
            for owner in customAttributeOwners scope do
                EmitScope.typeOfCustomAttributeOwner owner scope
                |> shouldEqual (model scope owner)

                match owner with
                | HasCustomAttribute.Param p ->
                    let method = (EmitScope.parameters scope).[p.Rid - 1].Owner

                    // A parameter whose method's RID is a type's RID too, so that a walk that stops at the method
                    // gives a type, and a wrong one.
                    if method.Rid <> (EmitScope.methodDefs scope).[method.Rid - 1].Owner.Rid then
                        cover "parameter"
                | HasCustomAttribute.GenericParam g ->
                    match (EmitScope.genericParams scope).[g.Rid - 1].Owner with
                    | TypeOrMethodDef.MethodDef m when m.Rid <> (EmitScope.methodDefs scope).[m.Rid - 1].Owner.Rid ->
                        cover "method's generic parameter"
                    | _ -> ()
                | _ -> ()

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 200) (Arb.fromGen genScope) property

        coverage.Count "parameter" |> shouldBeGreaterThan 100
        coverage.Count "method's generic parameter" |> shouldBeGreaterThan 50

    // ---- The host's emitter, read raw through CoreLib's internal `MetadataImport`. ----

    let private coreLib : Assembly = typeof<obj>.Assembly

    let private requireType (name : string) : Type =
        match coreLib.GetType name with
        | null -> failwith $"CoreLib has no internal %s{name}, through which this test reads the host's rows"
        | t -> t

    let private instance : BindingFlags =
        BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

    let private requireMember (found : 'a) (what : string) : 'a =
        if isNull (box found) then
            failwith $"CoreLib has no internal %s{what}, through which this test reads the host's rows"

        found

    /// Calls CoreLib's internal `MetadataImport.EnumCustomAttributes` on a `RuntimeModule` and returns the tokens.
    let private enumerateCustomAttributes : Lazy<Func<Module, int, int[]>> =
        lazy
            let runtimeModule = requireType "System.Reflection.RuntimeModule"
            let metadataImport = requireType "System.Reflection.MetadataImport"
            let enumResult = requireType "System.Reflection.MetadataEnumResult"

            let getImport =
                (requireMember (runtimeModule.GetProperty ("MetadataImport", instance)) "RuntimeModule.MetadataImport")
                    .GetMethod

            let enumerate =
                requireMember (metadataImport.GetMethod ("EnumCustomAttributes", instance)) "EnumCustomAttributes"

            let getLength =
                (requireMember (enumResult.GetProperty ("Length", instance)) "MetadataEnumResult.Length").GetMethod

            let getItem =
                (requireMember (enumResult.GetProperty ("Item", instance)) "MetadataEnumResult.Item").GetMethod

            let dm =
                DynamicMethod (
                    "EnumCustomAttributes",
                    typeof<int[]>,
                    [| typeof<Module> ; typeof<int> |],
                    typeof<obj>.Module,
                    true
                )

            let il = dm.GetILGenerator ()
            let import = il.DeclareLocal metadataImport
            let result = il.DeclareLocal enumResult
            let array = il.DeclareLocal typeof<int[]>
            let index = il.DeclareLocal typeof<int>
            il.Emit (OpCodes.Ldarg_0)
            il.Emit (OpCodes.Castclass, runtimeModule)
            il.Emit (OpCodes.Call, getImport)
            il.Emit (OpCodes.Stloc, import)
            il.Emit (OpCodes.Ldloca, import)
            il.Emit (OpCodes.Ldarg_1)
            il.Emit (OpCodes.Ldloca, result)
            il.Emit (OpCodes.Call, enumerate)
            il.Emit (OpCodes.Ldloca, result)
            il.Emit (OpCodes.Call, getLength)
            il.Emit (OpCodes.Newarr, typeof<int>)
            il.Emit (OpCodes.Stloc, array)
            il.Emit (OpCodes.Ldc_I4_0)
            il.Emit (OpCodes.Stloc, index)
            let top = il.DefineLabel ()
            let test = il.DefineLabel ()
            il.Emit (OpCodes.Br, test)
            il.MarkLabel top
            il.Emit (OpCodes.Ldloc, array)
            il.Emit (OpCodes.Ldloc, index)
            il.Emit (OpCodes.Ldloca, result)
            il.Emit (OpCodes.Ldloc, index)
            il.Emit (OpCodes.Call, getItem)
            il.Emit (OpCodes.Stelem_I4)
            il.Emit (OpCodes.Ldloc, index)
            il.Emit (OpCodes.Ldc_I4_1)
            il.Emit (OpCodes.Add)
            il.Emit (OpCodes.Stloc, index)
            il.MarkLabel test
            il.Emit (OpCodes.Ldloc, index)
            il.Emit (OpCodes.Ldloc, array)
            il.Emit (OpCodes.Ldlen)
            il.Emit (OpCodes.Conv_I4)
            il.Emit (OpCodes.Blt, top)
            il.Emit (OpCodes.Ldloc, array)
            il.Emit (OpCodes.Ret)
            dm.CreateDelegate typeof<Func<Module, int, int[]>> :?> Func<Module, int, int[]>

    /// The rows the host's emitter holds for `owner` in `moduleBuilder`, in its enumeration order: each row's token,
    /// its constructor's token and its blob.
    let private hostRows (moduleBuilder : ModuleBuilder) (owner : int) : (int * int * byte list) list =
        let runtimeModule = requireType "System.Reflection.RuntimeModule"
        let metadataImport = requireType "System.Reflection.MetadataImport"
        let constArray = requireType "System.Reflection.ConstArray"
        let runtimeModuleBuilder = requireType "System.Reflection.Emit.RuntimeModuleBuilder"

        let internalModule =
            (requireMember (runtimeModuleBuilder.GetProperty ("InternalModule", instance)) "InternalModule").GetValue
                moduleBuilder

        let import =
            (requireMember (runtimeModule.GetProperty ("MetadataImport", instance)) "RuntimeModule.MetadataImport")
                .GetValue
                internalModule

        // `MetadataEnumResult` is a ref struct, which reflection cannot box, so a skip-visibility `DynamicMethod`
        // calls `EnumCustomAttributes` and copies the result into an array.
        let rows : int[] =
            enumerateCustomAttributes.Value.Invoke (internalModule :?> Module, owner)

        let props =
            requireMember (metadataImport.GetMethod ("GetCustomAttributeProps", instance)) "GetCustomAttributeProps"

        [
            for row in rows do
                let propsArgs = [| box row ; null ; null |]
                props.Invoke (import, propsArgs) |> ignore<obj>
                let blob = propsArgs.[2]

                let blobLength =
                    constArray.GetProperty("Length", instance).GetValue blob |> unbox<int>

                let pointer =
                    constArray.GetProperty("Signature", instance).GetValue blob |> unbox<nativeint>

                let bytes = Array.zeroCreate<byte> blobLength

                if blobLength > 0 then
                    Marshal.Copy (pointer, bytes, 0, blobLength)

                yield row, unbox<int> propsArgs.[1], List.ofArray bytes
        ]

    let private noArguments : byte[] = [| 1uy ; 0uy ; 0uy ; 0uy |]

    let private assemblyCount : int ref = ref 0

    let private newModule (name : string) : AssemblyBuilder * ModuleBuilder =
        let n = Threading.Interlocked.Increment assemblyCount

        let assembly =
            AssemblyBuilder.DefineDynamicAssembly (AssemblyName $"%s{name}%d{n}", AssemblyBuilderAccess.RunAndCollect)

        assembly, assembly.DefineDynamicModule name

    /// One constructor of the classifier's differential pool, and where the host's emitter finds its type's name.
    type private PoolEntry =
        {
            Label : string
            Constructor : ConstructorInfo
            Parent : Parent
        }

    /// The pool: CoreLib's known attributes and some ordinary ones, through MemberRefs; types `module` defines with
    /// each known and security name and some near misses, through their `ConstructorBuilder`s and their created
    /// constructors; the same names nested in a type of the module, and in a second dynamic assembly, top level and
    /// nested; and a generic type of a second dynamic assembly with a known name and no arity suffix, instantiated.
    let private classifierPool (moduleBuilder : ModuleBuilder) : PoolEntry list =
        let names =
            (KnownCustomAttribute.all |> List.map KnownCustomAttribute.name)
            @ [
                suppressName
                dynamicSecurityName
                "System", "Serializableattribute"
                "", "SerializableAttribute"
                "System.Security", "DynamicSecurityMethodAttribute2"
            ]

        let full (ns : string, name : string) =
            if ns = "" then name else ns + "." + name

        let coreLibTypes =
            [
                typeof<SerializableAttribute>
                typeof<NonSerializedAttribute>
                typeof<ComImportAttribute>
                typeof<PreserveSigAttribute>
                typeof<InAttribute>
                typeof<OutAttribute>
                typeof<OptionalAttribute>
                typeof<System.Runtime.CompilerServices.SpecialNameAttribute>
                typeof<System.Security.AllowPartiallyTrustedCallersAttribute>
                typeof<GuidAttribute>
                typeof<InterfaceTypeAttribute>
                typeof<ClassInterfaceAttribute>
                typeof<System.Runtime.CompilerServices.MethodImplAttribute>
                typeof<MarshalAsAttribute>
                typeof<StructLayoutAttribute>
                typeof<FieldOffsetAttribute>
                typeof<TypeLibVersionAttribute>
                typeof<ComCompatibleVersionAttribute>
                typeof<DllImportAttribute>
                typeof<System.Security.SuppressUnmanagedCodeSecurityAttribute>
                typeof<ObsoleteAttribute>
                typeof<FlagsAttribute>
                typeof<ThreadStaticAttribute>
                typeof<CLSCompliantAttribute>
            ]

        let attributeType (owner : ModuleBuilder) (fullName : string) : TypeBuilder * ConstructorBuilder =
            let t = owner.DefineType (fullName, TypeAttributes.Public, typeof<Attribute>)
            t, t.DefineDefaultConstructor MethodAttributes.Public

        let ownDefinitions =
            names |> List.map (fun name -> name, attributeType moduleBuilder (full name))

        // Each nested type has an encloser of its own: reflection keys a nested type by its encloser and the part of
        // its name after the last dot, which two of the names share.
        let ownNested =
            names
            |> List.mapi (fun i name ->
                let outer = moduleBuilder.DefineType ($"Outer%d{i}", TypeAttributes.Public)

                let t =
                    outer.DefineNestedType (full name, TypeAttributes.NestedPublic, typeof<Attribute>)

                name, outer, t, t.DefineDefaultConstructor MethodAttributes.Public
            )

        let _, other = newModule "ClassifierOther"

        let otherTop =
            names
            |> List.map (fun name ->
                let t, _ = attributeType other (full name)
                name, t.CreateType().GetConstructor Type.EmptyTypes
            )

        let otherNested =
            names
            |> List.mapi (fun i name ->
                let outer = other.DefineType ($"Outer%d{i}", TypeAttributes.Public)

                let t =
                    outer.DefineNestedType (full name, TypeAttributes.NestedPublic, typeof<Attribute>)

                t.DefineDefaultConstructor MethodAttributes.Public |> ignore<ConstructorBuilder>
                outer.CreateType () |> ignore<Type>
                name, t.CreateType().GetConstructor Type.EmptyTypes
            )

        let generic =
            let _, third = newModule "ClassifierGeneric"

            let t =
                third.DefineType ("System.SerializableAttribute", TypeAttributes.Public, typeof<Attribute>)

            t.DefineGenericParameters "T" |> ignore<GenericTypeParameterBuilder[]>
            t.DefineDefaultConstructor MethodAttributes.Public |> ignore<ConstructorBuilder>
            t.CreateType().MakeGenericType(typeof<int>).GetConstructor Type.EmptyTypes

        let ownCreated =
            ownDefinitions
            |> List.map (fun (name, (t, _)) -> name, t.CreateType().GetConstructor Type.EmptyTypes)

        let ownNestedCreated =
            ownNested
            |> List.map (fun (name, outer, t, c) ->
                outer.CreateType () |> ignore<Type>
                t.CreateType () |> ignore<Type>
                name, (), c
            )

        [
            for t in coreLibTypes do
                yield
                    {
                        Label = $"CoreLib %s{t.FullName}"
                        Constructor = t.GetConstructors().[0]
                        Parent = Parent.ReferenceInAssembly (t.Namespace, t.Name)
                    }
            for (ns, name), (_, builder) in ownDefinitions do
                yield
                    {
                        Label = $"module's builder %s{ns}.%s{name}"
                        Constructor = builder
                        Parent = Parent.MethodOfDefinition (ns, name)
                    }
            for (ns, name), created in ownCreated do
                yield
                    {
                        Label = $"module's created %s{ns}.%s{name}"
                        Constructor = created
                        Parent = Parent.MethodOfDefinition (ns, name)
                    }
            for (ns, name), (), builder in ownNestedCreated do
                yield
                    {
                        Label = $"module's nested %s{ns}.%s{name}"
                        Constructor = builder
                        Parent = Parent.MethodOfNestedDefinition (ns, name)
                    }
            for (ns, name), created in otherTop do
                yield
                    {
                        Label = $"other assembly's %s{ns}.%s{name}"
                        Constructor = created
                        Parent = Parent.ReferenceInAssembly (ns, name)
                    }
            for (ns, name), created in otherNested do
                yield
                    {
                        Label = $"other assembly's nested %s{ns}.%s{name}"
                        Constructor = created
                        Parent = Parent.NestedReference (ns, name)
                    }
            yield
                {
                    Label = "other assembly's generic, instantiated"
                    Constructor = generic
                    Parent = Parent.MemberOfSpecification ("System", "SerializableAttribute")
                }
        ]

    /// What the host's emitter did with one attribute on a fresh owner.
    type private HostOutcome =
        {
            Threw : bool
            Rows : int
            HasSecurity : bool
            RequireSecObject : bool
        }

    [<Test>]
    let ``the classifier's answer is what the host's emitter does`` () : unit =
        let _, moduleBuilder = newModule "Classifier"
        let pool = classifierPool moduleBuilder
        let plain = typeof<ObsoleteAttribute>.GetConstructor Type.EmptyTypes
        let mutable count = 0
        let mutable reached = Set.empty

        for entry in pool do
            for ownerKind in [ "type" ; "method" ; "field" ] do
                count <- count + 1
                // `RegMeta::DefineCustomAttribute` compares a name it fills only for a TypeRef or TypeDef parent, so
                // an attribute named through a TypeSpec straight after a security name meets that name; an ordinary
                // attribute first leaves an ordinary name in its buffer.
                let buffer = moduleBuilder.DefineType ($"Buffer%d{count}", TypeAttributes.Public)
                buffer.SetCustomAttribute (plain, noArguments)

                let holder = moduleBuilder.DefineType ($"H%d{count}", TypeAttributes.Public)

                let method =
                    holder.DefineMethod ("M", MethodAttributes.Public ||| MethodAttributes.Static, typeof<Void>, [||])

                method.GetILGenerator().Emit OpCodes.Ret
                let field = holder.DefineField ("F", typeof<int>, FieldAttributes.Public)

                let ownerToken, apply =
                    match ownerKind with
                    | "type" ->
                        holder.MetadataToken, (fun () -> holder.SetCustomAttribute (entry.Constructor, noArguments))
                    | "method" ->
                        method.MetadataToken, (fun () -> method.SetCustomAttribute (entry.Constructor, noArguments))
                    | _ -> field.MetadataToken, (fun () -> field.SetCustomAttribute (entry.Constructor, noArguments))

                let threw =
                    try
                        apply ()
                        false
                    with :? ArgumentException ->
                        true

                let rows = hostRows moduleBuilder ownerToken |> List.length

                // The mirror: the same owner kind, and a constructor named as the host's emitter names it.
                let scope, owners = withOwners ()
                let constructor, scope = constructorOf entry.Parent scope

                let owner =
                    match ownerKind with
                    | "type" -> owners.[2]
                    | "method" -> owners.[3]
                    | _ -> owners.[4]

                let kind = EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                reached <- reached.Add (sprintf "%A" kind |> fun s -> s.Split(' ').[0])

                let flags () : HostOutcome =
                    let created = holder.CreateType ()

                    {
                        Threw = threw
                        Rows = rows
                        HasSecurity =
                            match ownerKind with
                            | "type" -> created.Attributes.HasFlag TypeAttributes.HasSecurity
                            | "method" -> (created.GetMethod "M").Attributes.HasFlag MethodAttributes.HasSecurity
                            | _ -> false
                        RequireSecObject =
                            ownerKind = "method"
                            && (created.GetMethod "M").Attributes.HasFlag MethodAttributes.RequireSecObject
                    }

                let describe = $"%s{entry.Label} on a %s{ownerKind}: classified %A{kind}"

                match kind with
                | CustomAttributeEmitKind.Known known ->
                    // The six attributes `KnownCaList` marks `bKEEPCA` are validated and then stored as rows, which a
                    // well-formed blob on a right target makes indistinguishable from an ordinary row here; the
                    // fixed table pins their names. Every other known attribute writes no row.
                    let kept =
                        [
                            KnownCustomAttribute.Guid
                            KnownCustomAttribute.InterfaceType
                            KnownCustomAttribute.ClassInterface
                            KnownCustomAttribute.TypeLibVersion
                            KnownCustomAttribute.ComCompatibleVersion
                            KnownCustomAttribute.AllowPartiallyTrustedCallers
                        ]

                    if threw then
                        reached <- reached.Add "Known, refused"
                    elif rows = 0 then
                        reached <- reached.Add "Known, no row"
                    elif not (List.contains known kept) || rows <> 1 then
                        failwith $"%s{describe}, but the host wrote %d{rows} row(s) and threw nothing"
                | CustomAttributeEmitKind.Ordinary ->
                    let outcome = flags ()

                    if
                        outcome
                        <> {
                               Threw = false
                               Rows = 1
                               HasSecurity = false
                               RequireSecObject = false
                           }
                    then
                        failwith $"%s{describe}, but the host did %A{outcome}"
                | CustomAttributeEmitKind.SuppressUnmanagedCodeSecurity ->
                    let outcome = flags ()

                    if
                        outcome
                        <> {
                               Threw = false
                               Rows = 1
                               HasSecurity = true
                               RequireSecObject = false
                           }
                    then
                        failwith $"%s{describe}, but the host did %A{outcome}"
                | CustomAttributeEmitKind.DynamicSecurityMethod ->
                    let outcome = flags ()

                    if
                        outcome
                        <> {
                               Threw = false
                               Rows = 0
                               HasSecurity = false
                               RequireSecObject = true
                           }
                    then
                        failwith $"%s{describe}, but the host did %A{outcome}"

        reached
        |> shouldEqual (
            Set.ofList
                [
                    "Ordinary"
                    "Known"
                    "Known, refused"
                    "Known, no row"
                    "SuppressUnmanagedCodeSecurity"
                    "DynamicSecurityMethod"
                ]
        )

    /// One generated `SetCustomAttribute` call of the rows' differential property.
    type private Call =
        {
            Owner : int
            Constructor : int
            Blob : int
        }

    type private RowsSchedule =
        {
            Types : int
            Calls : Call list
            /// The step at which every type is created; from then on only the assembly and the module get attributes.
            CreateAt : int
        }

    [<RequireQualifiedAccess>]
    type private RowsReach =
        | OwnerWithSeventeen
        | ModuleWithTwentyFive
        | BakedAmongLater
        | EmptyBlob
        | Owner of string

    [<Test>]
    let ``every owner's rows are what the host's emitter writes`` () : unit =
        let property (cover : RowsReach -> unit) (schedule : RowsSchedule) : unit =
            let assembly, host = newModule "RowsOracle"
            // An attribute type of the module, whose constructor is a MethodDef: TypeDef 2, MethodDef 1.
            let own = host.DefineType ("Own", TypeAttributes.Public, typeof<Attribute>)
            let ownCtor = own.DefineDefaultConstructor MethodAttributes.Public

            let types =
                [|
                    for i in 0 .. schedule.Types - 1 do
                        yield host.DefineType ($"T%d{i}", TypeAttributes.Public)
                |]

            let typeParameters =
                types |> Array.map (fun t -> (t.DefineGenericParameters [| "G" |]).[0])

            // Owners' tokens, forced in this order: each type's method, then its parameter, field, property and
            // event, as the scope below defines them.
            let methods =
                types
                |> Array.map (fun t ->
                    let m =
                        t.DefineMethod (
                            "M",
                            MethodAttributes.Public ||| MethodAttributes.Static,
                            typeof<Void>,
                            [| typeof<int> |]
                        )

                    m.GetILGenerator().Emit OpCodes.Ret
                    m
                )

            for m in methods do
                m.MetadataToken |> ignore<int>

            let parameters =
                methods
                |> Array.map (fun m -> m.DefineParameter (1, ParameterAttributes.None, "x"))

            let fields =
                types
                |> Array.map (fun t -> t.DefineField ("F", typeof<int>, FieldAttributes.Public))

            let properties =
                types
                |> Array.map (fun t -> t.DefineProperty ("P", PropertyAttributes.None, typeof<int>, [||]))

            let events =
                types
                |> Array.map (fun t -> t.DefineEvent ("E", EventAttributes.None, typeof<Action>))

            let pool : ConstructorInfo list =
                [
                    ownCtor
                    typeof<ObsoleteAttribute>.GetConstructor [| typeof<string> |]
                    typeof<CLSCompliantAttribute>.GetConstructor [| typeof<bool> |]
                    typeof<FlagsAttribute>.GetConstructor Type.EmptyTypes
                ]

            let poolTokens = pool |> List.map host.GetMethodMetadataToken

            // The scope: the same owners at the same RIDs, and as many MethodDef and MemberRef rows as the
            // constructors' tokens need.
            let mutable scope = emptyScope Guid.Empty

            let addType (name : string) : TypeDefRid =
                let name, s = EmitScope.internString name scope

                let rid, s =
                    EmitScope.addTypeDef
                        {
                            Flags = TypeAttributes.Public
                            Name = name
                            Namespace = StringOffset 0
                            Extends = None
                        }
                        s

                scope <- s
                rid

            let ownRid = addType "Own"

            scope <-
                EmitScopeDefinitions.defineMethod ownRid ".ctor" ctorSignature MethodAttributes.Public scope
                |> snd

            let typeRids = [| for i in 0 .. schedule.Types - 1 -> addType $"T%d{i}" |]

            let methodSignature =
                ImmutableArray.Create<byte> [| 0x00uy ; 0x01uy ; 0x01uy ; 0x08uy |]

            let methodRids =
                typeRids
                |> Array.map (fun t ->
                    let rid, s =
                        EmitScopeDefinitions.defineMethod
                            t
                            "M"
                            methodSignature
                            (MethodAttributes.Public ||| MethodAttributes.Static)
                            scope

                    scope <- s
                    rid
                )

            let paramRids =
                methodRids
                |> Array.map (fun m ->
                    match EmitScopeDefinitions.defineParam m 1 "x" 0 scope with
                    | ParamDefinition.Defined (rid, s) ->
                        scope <- s
                        rid
                    | ParamDefinition.StrandsLaterMethods _ -> failwith "one parameter each strands nothing"
                )

            let fieldRids =
                typeRids
                |> Array.map (fun t ->
                    let rid, s =
                        EmitScopeDefinitions.defineField
                            t
                            "F"
                            (ImmutableArray.Create<byte> [| 0x06uy ; 0x08uy |])
                            FieldAttributes.Public
                            scope

                    scope <- s
                    rid
                )

            let propertyRids =
                typeRids
                |> Array.map (fun t ->
                    let rid, s =
                        EmitScopeDefinitions.defineProperty
                            t
                            "P"
                            (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                            0
                            scope

                    scope <- s
                    rid
                )

            let eventRids =
                typeRids
                |> Array.map (fun t ->
                    let rid, s = EmitScopeDefinitions.defineEvent t "E" (TypeDefOrRef.TypeDef t) 0 scope

                    scope <- s
                    rid
                )

            // Each owner's token on the host is its RID in the scope.
            for i in 0 .. schedule.Types - 1 do
                types.[i].MetadataToken |> shouldEqual (0x02000000 ||| typeRids.[i].Rid)
                methods.[i].MetadataToken |> shouldEqual (0x06000000 ||| methodRids.[i].Rid)

                parameters.[i].GetType().GetField("_token", instance).GetValue parameters.[i]
                |> unbox<int>
                |> shouldEqual (0x08000000 ||| paramRids.[i].Rid)

                fields.[i].MetadataToken |> shouldEqual (0x04000000 ||| fieldRids.[i].Rid)

            let maxMemberRef =
                poolTokens
                |> List.filter (fun t -> t >>> 24 = 0x0A)
                |> List.map (fun t -> t &&& 0xFFFFFF)
                |> List.fold max 0

            scope <-
                (scope, [ 1..maxMemberRef ])
                ||> List.fold (fun scope _ -> memberRef (MemberRefParent.TypeDef ownRid) scope |> snd)

            let mirror (token : int) : MethodDefOrRef =
                match token >>> 24 with
                | 0x06 -> MethodDefOrRef.MethodDef (EmitScope.tryMethodDefRid (token &&& 0xFFFFFF) scope).Value
                | 0x0A -> MethodDefOrRef.MemberRef (EmitScope.tryMemberRefRid (token &&& 0xFFFFFF) scope).Value
                | _ -> failwith $"GetMethodMetadataToken gave 0x%08x{token}"

            let define (owner : HasCustomAttribute) (constructor : int) (value : byte[]) : int =
                match
                    EmitScopeDefinitions.defineCustomAttribute
                        owner
                        (mirror constructor)
                        (ImmutableArray.Create<byte> value)
                        scope
                with
                | CustomAttributeDefinition.Defined (rid, s) ->
                    scope <- s
                    rid.Rid
                | CustomAttributeDefinition.NotStoredAsGiven kind ->
                    failwith $"the pool holds no pseudo-attribute, but the emitter would %A{kind}"
                | CustomAttributeDefinition.FlagSet _
                | CustomAttributeDefinition.Refused _ ->
                    failwith "the pool holds no pseudo-attribute, but the emitter treated one as a flag"

            // Attributes of each type's generic parameter, queued until the type is created, as the host queues them.
            let queued = Array.init schedule.Types (fun _ -> ResizeArray<int * byte[]> ())
            let mutable created = false
            let mutable lastGenericRow = 0
            let mutable rowsAfterQueue = false

            let createAll () =
                for i in 0 .. schedule.Types - 1 do
                    types.[i].CreateType () |> ignore<Type>

                    let parameter, s =
                        EmitScopeDefinitions.defineGenericParam
                            (TypeOrMethodDef.TypeDef typeRids.[i])
                            0us
                            0
                            "G"
                            []
                            scope

                    scope <- s

                    for constructor, value in queued.[i] do
                        lastGenericRow <- define (HasCustomAttribute.GenericParam parameter) constructor value

                    if queued.[i].Count > 0 && rowsAfterQueue then
                        cover RowsReach.BakedAmongLater

                created <- true

            for step, call in List.indexed schedule.Calls do
                if step = schedule.CreateAt && not created then
                    createAll ()

                let kinds = if created then 2 else 9
                let index = call.Owner % schedule.Types
                let constructorIndex = call.Constructor % pool.Length
                let constructor = pool.[constructorIndex]
                let constructorToken = poolTokens.[constructorIndex]
                let value = (pick call.Blob customAttributeBlobs).Value |> Seq.toArray

                if value.Length = 0 then
                    cover RowsReach.EmptyBlob

                let kind = (call.Owner / 7) % kinds

                let owner, apply =
                    match kind with
                    | 0 -> HasCustomAttribute.Assembly, (fun () -> assembly.SetCustomAttribute (constructor, value))
                    | 1 -> HasCustomAttribute.Module, (fun () -> host.SetCustomAttribute (constructor, value))
                    | 2 ->
                        HasCustomAttribute.TypeDef typeRids.[index],
                        (fun () -> types.[index].SetCustomAttribute (constructor, value))
                    | 3 ->
                        HasCustomAttribute.MethodDef methodRids.[index],
                        (fun () -> methods.[index].SetCustomAttribute (constructor, value))
                    | 4 ->
                        HasCustomAttribute.Field fieldRids.[index],
                        (fun () -> fields.[index].SetCustomAttribute (constructor, value))
                    | 5 ->
                        HasCustomAttribute.Param paramRids.[index],
                        (fun () -> parameters.[index].SetCustomAttribute (constructor, value))
                    | 6 ->
                        HasCustomAttribute.Property propertyRids.[index],
                        (fun () -> properties.[index].SetCustomAttribute (constructor, value))
                    | 7 ->
                        HasCustomAttribute.Event eventRids.[index],
                        (fun () -> events.[index].SetCustomAttribute (constructor, value))
                    | _ ->
                        // The parameter's row does not exist yet: `owner` is a placeholder the queue replaces.
                        HasCustomAttribute.Module,
                        (fun () -> typeParameters.[index].SetCustomAttribute (constructor, value))

                apply ()

                if kind = 8 then
                    queued.[index].Add (constructorToken, value)
                    cover (RowsReach.Owner "generic parameter")
                else
                    define owner constructorToken value |> ignore<int>
                    cover (RowsReach.Owner (ownerKind owner))

                    if queued |> Array.exists (fun q -> q.Count > 0) then
                        rowsAfterQueue <- true

            if not created then
                createAll ()

            // Every owner, compared with the image of the scope through the index `Assembly.read` builds.
            let image = EmitScopeImage.write scope

            use dumped =
                Assembly.read NullLoggerFactory.Instance (Some "RowsOracle.dll") (new MemoryStream (image))

            let ownerTokens =
                [
                    yield 0x20000001
                    yield 0x00000001
                    for i in 0 .. schedule.Types - 1 do
                        yield 0x02000000 ||| typeRids.[i].Rid
                        yield 0x06000000 ||| methodRids.[i].Rid
                        yield 0x04000000 ||| fieldRids.[i].Rid
                        yield 0x08000000 ||| paramRids.[i].Rid
                        yield 0x17000000 ||| propertyRids.[i].Rid
                        yield 0x14000000 ||| eventRids.[i].Rid
                    for i in 1 .. (EmitScope.genericParams scope).Count do
                        yield 0x2A000000 ||| i
                ]

            for token in ownerTokens do
                let expected = hostRows host token

                let actual =
                    match dumped.CustomAttributesByParentToken.TryGetValue token with
                    | true, rows ->
                        [
                            for row in rows do
                                let attribute =
                                    dumped.Attributes.[System
                                        .Reflection
                                        .Metadata
                                        .Ecma335
                                        .MetadataTokens
                                        .CustomAttributeHandle (row &&& 0xFFFFFF)]

                                yield row, MetadataToken.toInt attribute.Constructor, List.ofSeq attribute.Value
                        ]
                    | false, _ -> []

                actual |> shouldEqual expected

                if expected.Length >= 17 then
                    cover RowsReach.OwnerWithSeventeen

            if hostRows host 0x00000001 |> List.length >= 25 then
                cover RowsReach.ModuleWithTwentyFive

        let genCall (favourite : int) : Gen<Call> =
            gen {
                let! owner = Gen.frequency [ 1, Gen.constant favourite ; 2, choice ]
                let! constructor = choice
                let! blob = choice

                return
                    {
                        Owner = owner
                        Constructor = constructor
                        Blob = blob
                    }
            }

        let genSchedule : Gen<RowsSchedule> =
            gen {
                let! types = Gen.choose (1, 3)
                let! favourite = choice
                let! count = Gen.choose (0, 80)
                let! calls = Gen.listOfLength count (genCall favourite)
                let! createAt = Gen.choose (0, count + 5)

                return
                    {
                        Types = types
                        Calls = List.ofSeq calls
                        CreateAt = createAt
                    }
            }

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 150) (Arb.fromGen genSchedule) property

        coverage.Count RowsReach.OwnerWithSeventeen |> shouldBeGreaterThan 10
        coverage.Count RowsReach.ModuleWithTwentyFive |> shouldBeGreaterThan 3
        coverage.Count RowsReach.BakedAmongLater |> shouldBeGreaterThan 10
        coverage.Count RowsReach.EmptyBlob |> shouldBeGreaterThan 50

        for kind in ownerKinds do
            coverage.Count (RowsReach.Owner kind) |> shouldBeGreaterThan 20

    /// One generated `SetCustomAttribute` call of the flags' differential property: an owner, a constructor of the pool
    /// and a blob, each as a choice.
    type private FlagCall =
        {
            Owner : int
            Constructor : int
            Blob : int
        }

    /// The owners of one type of the flags' differential property, with the flags each is defined with.
    type private FlagOwners =
        {
            Type : TypeAttributes
            Method : MethodAttributes
            Constructor : MethodAttributes
            Parameter : ParameterAttributes
            Return : ParameterAttributes
            Field : FieldAttributes
            Property : PropertyAttributes
            Event : EventAttributes
        }

    type private FlagsSchedule =
        {
            Types : FlagOwners list
            Calls : FlagCall list
        }

    [<RequireQualifiedAccess>]
    type private FlagsReach =
        | Accepted of KnownCustomAttribute * string
        | Refused of KnownCustomAttribute
        | BitAlreadySet
        | BitNotSet
        | Impostor

    /// The ten owner kinds of the flags' differential property, in the order its owner choices name them.
    let private flagOwnerKinds : string list =
        [
            "assembly"
            "module"
            "type"
            "method"
            "constructor"
            "parameter"
            "return parameter"
            "field"
            "property"
            "event"
        ]

    [<Test>]
    let ``each owner's flags after flag pseudo-attributes are what the host's emitter writes`` () : unit =
        let all =
            BindingFlags.Public
            ||| BindingFlags.NonPublic
            ||| BindingFlags.Instance
            ||| BindingFlags.Static
            ||| BindingFlags.DeclaredOnly

        let property (cover : FlagsReach -> unit) (schedule : FlagsSchedule) : unit =
            let assembly, host = newModule "FlagsOracle"
            let mutable scope = emptyScope Guid.Empty

            // The pool: CoreLib's seven attributes, and an impostor of each name defined in the module.
            let pool : (KnownCustomAttribute * bool * ConstructorInfo * MethodDefOrRef) list =
                [
                    for known, _ in flagTable do
                        let ns, name = KnownCustomAttribute.name known
                        let coreLib = typeof<obj>.Assembly.GetType $"%s{ns}.%s{name}"
                        let mirror, s = constructorOf (Parent.ReferenceInAssembly (ns, name)) scope
                        scope <- s
                        yield known, false, coreLib.GetConstructor Type.EmptyTypes, mirror

                    for known, _ in flagTable do
                        let ns, name = KnownCustomAttribute.name known

                        let impostor =
                            host.DefineType ($"%s{ns}.%s{name}", TypeAttributes.Public, typeof<Attribute>)

                        let ctor = impostor.DefineDefaultConstructor MethodAttributes.Public
                        let mirror, s = constructorOf (Parent.MethodOfDefinition (ns, name)) scope
                        scope <- s
                        yield known, true, (ctor :> ConstructorInfo), mirror
                ]

            let methodSignature =
                ImmutableArray.Create<byte> [| 0x00uy ; 0x01uy ; 0x08uy ; 0x08uy |]

            let ctorSignature = ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x01uy |]

            // Each type's owners on the host, each with its owner on the mirror and its host token.
            let owners =
                [
                    for i, flags in List.indexed schedule.Types do
                        let t = host.DefineType ($"T%d{i}", flags.Type)
                        let m = t.DefineMethod ("M", flags.Method, typeof<int>, [| typeof<int> |])
                        let ret = m.DefineParameter (0, flags.Return, "r")
                        let x = m.DefineParameter (1, flags.Parameter, "x")
                        let il = m.GetILGenerator ()
                        il.Emit OpCodes.Ldarg_0
                        il.Emit OpCodes.Ret

                        let c =
                            t.DefineConstructor (flags.Constructor, CallingConventions.Standard, Type.EmptyTypes)

                        let il = c.GetILGenerator ()
                        il.Emit OpCodes.Ldarg_0
                        il.Emit (OpCodes.Call, typeof<obj>.GetConstructor Type.EmptyTypes)
                        il.Emit OpCodes.Ret
                        let f = t.DefineField ("F", typeof<int>, flags.Field)
                        let p = t.DefineProperty ("P", flags.Property, typeof<int>, Type.EmptyTypes)
                        let e = t.DefineEvent ("E", flags.Event, typeof<Action>)

                        let typeRid, s =
                            match EmitScopeDefinitions.defineTopLevelTypeDef $"T%d{i}" t.Attributes None [] scope with
                            | TypeDefDefinition.Defined (rid, s) -> rid, s
                            | TypeDefDefinition.Duplicate _ -> failwith "each type's name is its own"

                        let method, s =
                            EmitScopeDefinitions.defineMethod typeRid "M" methodSignature m.Attributes s

                        let param (sequence : int) (flags : ParameterAttributes) (s : EmitScope) =
                            match EmitScopeDefinitions.defineParam method sequence "p" (int flags) s with
                            | ParamDefinition.Defined (rid, s) -> rid, s
                            | ParamDefinition.StrandsLaterMethods _ ->
                                failwith "a method's parameters in order strand nothing"

                        let retRid, s = param 0 (enum<ParameterAttributes> ret.Attributes) s
                        let xRid, s = param 1 (enum<ParameterAttributes> x.Attributes) s
                        // `DefineConstructor` adds `SpecialName` and `RTSpecialName` to the flags it is given.
                        let ctorRid, s =
                            EmitScopeDefinitions.defineMethod typeRid ".ctor" ctorSignature c.Attributes s

                        let fieldRid, s =
                            EmitScopeDefinitions.defineField
                                typeRid
                                "F"
                                (ImmutableArray.Create<byte> [| 0x06uy ; 0x08uy |])
                                f.Attributes
                                s

                        let propertyRid, s =
                            EmitScopeDefinitions.defineProperty
                                typeRid
                                "P"
                                (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                                (int p.Attributes)
                                s

                        let eventRid, s =
                            EmitScopeDefinitions.defineEvent
                                typeRid
                                "E"
                                (TypeDefOrRef.TypeDef typeRid)
                                (int flags.Event)
                                s

                        scope <- s

                        let token (builder : obj) (field : string) : int =
                            builder.GetType().GetField(field, instance).GetValue builder |> unbox<int>

                        yield
                            t,
                            [
                                "type",
                                (fun (cab : ConstructorInfo, blob) -> t.SetCustomAttribute (cab, blob)),
                                HasCustomAttribute.TypeDef typeRid,
                                t.MetadataToken
                                "method",
                                (fun (cab, blob) -> m.SetCustomAttribute (cab, blob)),
                                HasCustomAttribute.MethodDef method,
                                m.MetadataToken
                                "constructor",
                                (fun (cab, blob) -> c.SetCustomAttribute (cab, blob)),
                                HasCustomAttribute.MethodDef ctorRid,
                                c.MetadataToken
                                "parameter",
                                (fun (cab, blob) -> x.SetCustomAttribute (cab, blob)),
                                HasCustomAttribute.Param xRid,
                                token x "_token"
                                "return parameter",
                                (fun (cab, blob) -> ret.SetCustomAttribute (cab, blob)),
                                HasCustomAttribute.Param retRid,
                                token ret "_token"
                                "field",
                                (fun (cab, blob) -> f.SetCustomAttribute (cab, blob)),
                                HasCustomAttribute.Field fieldRid,
                                f.MetadataToken
                                "property",
                                (fun (cab, blob) -> p.SetCustomAttribute (cab, blob)),
                                HasCustomAttribute.Property propertyRid,
                                token p "m_tkProperty"
                                "event",
                                (fun (cab, blob) -> e.SetCustomAttribute (cab, blob)),
                                HasCustomAttribute.Event eventRid,
                                token e "m_evToken"
                            ]
                ]

            let blobs : byte[] list =
                [
                    [||]
                    [| 1uy ; 0uy |]
                    [| 0xFFuy ; 0xFFuy |]
                    [| 1uy |]
                    [| 1uy ; 0uy ; 0xDEuy ; 0xADuy ; 0xBEuy ; 0xEFuy |]
                    [| 1uy ; 0uy ; 1uy ; 0uy ; 0x99uy |]
                    [| 1uy ; 0uy ; 0uy ; 0uy |]
                ]

            for call in schedule.Calls do
                let known, isImpostor, constructor, mirror = pool.[call.Constructor % pool.Length]
                let kind = flagOwnerKinds.[(call.Owner / 7) % flagOwnerKinds.Length]
                let blob = blobs.[call.Blob % blobs.Length]

                let apply, owner =
                    match kind with
                    | "assembly" ->
                        (fun (cab, blob) -> assembly.SetCustomAttribute (cab, blob)), HasCustomAttribute.Assembly
                    | "module" -> (fun (cab, blob) -> host.SetCustomAttribute (cab, blob)), HasCustomAttribute.Module
                    | _ ->
                        let _, typeOwners = owners.[call.Owner % owners.Length]
                        let _, apply, owner, _ = typeOwners |> List.find (fun (k, _, _, _) -> k = kind)
                        apply, owner

                let threw =
                    try
                        apply (constructor, blob)
                        false
                    with :? ArgumentException as e ->
                        e.Message |> shouldEqual "Invalid custom attribute provided."
                        true

                let target =
                    flagTable
                    |> List.find (fun (k, _) -> k = known)
                    |> snd
                    |> List.tryFind (fun (t, _) ->
                        t = kind
                        || (t = "method" && kind = "constructor")
                        || (t = "method implementation" && (kind = "method" || kind = "constructor"))
                        || (t = "parameter" && kind = "return parameter")
                    )

                match
                    EmitScopeDefinitions.defineCustomAttribute owner mirror (ImmutableArray.Create<byte> blob) scope
                with
                | CustomAttributeDefinition.FlagSet after ->
                    threw |> shouldEqual false

                    match target with
                    | Some (column, bit) ->
                        cover (
                            if hasBit owner column bit scope then
                                FlagsReach.BitAlreadySet
                            else
                                FlagsReach.BitNotSet
                        )
                    | None -> failwith $"%A{known} on a %s{kind} set a flag, but CoreCLR's targets do not list it"

                    cover (FlagsReach.Accepted (known, kind))

                    if isImpostor then
                        cover FlagsReach.Impostor

                    scope <- after
                | CustomAttributeDefinition.Refused _ ->
                    threw |> shouldEqual true
                    cover (FlagsReach.Refused known)
                | outcome -> failwith $"%A{known} on a %s{kind}: %A{outcome}"

            // Each owner's flags, on the created type and in the image of the scope; and no row on either.
            let image = EmitScopeImage.write scope
            let pe, reader = readImage image
            use _ = pe

            for t, typeOwners in owners do
                let created = t.CreateType ()
                let m = created.GetMethod ("M", all)
                let c = (created.GetConstructors all).[0]

                let hostFlags =
                    [
                        int created.Attributes
                        int m.Attributes
                        int m.MethodImplementationFlags
                        int c.Attributes
                        int c.MethodImplementationFlags
                        int (m.GetParameters ()).[0].Attributes
                        int m.ReturnParameter.Attributes
                        int (created.GetField ("F", all)).Attributes
                        int (created.GetProperty ("P", all)).Attributes
                        int (created.GetEvent ("E", all)).Attributes
                    ]

                let rid (kind : string) : int =
                    let _, _, owner, _ = typeOwners |> List.find (fun (k, _, _, _) -> k = kind)
                    owner.Token &&& 0xFFFFFF

                let methodRow (kind : string) =
                    reader.GetMethodDefinition (
                        System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle (rid kind)
                    )

                let imageFlags =
                    [
                        int
                            (reader.GetTypeDefinition (
                                System.Reflection.Metadata.Ecma335.MetadataTokens.TypeDefinitionHandle (rid "type")
                            ))
                                .Attributes
                        int (methodRow "method").Attributes
                        int (methodRow "method").ImplAttributes
                        int (methodRow "constructor").Attributes
                        int (methodRow "constructor").ImplAttributes
                        int
                            (reader.GetParameter (
                                System.Reflection.Metadata.Ecma335.MetadataTokens.ParameterHandle (rid "parameter")
                            ))
                                .Attributes
                        int
                            (reader.GetParameter (
                                System.Reflection.Metadata.Ecma335.MetadataTokens.ParameterHandle (
                                    rid "return parameter"
                                )
                            ))
                                .Attributes
                        int
                            (reader.GetFieldDefinition (
                                System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle (rid "field")
                            ))
                                .Attributes
                        int
                            (reader.GetPropertyDefinition (
                                System.Reflection.Metadata.Ecma335.MetadataTokens.PropertyDefinitionHandle (
                                    rid "property"
                                )
                            ))
                                .Attributes
                        int
                            (reader.GetEventDefinition (
                                System.Reflection.Metadata.Ecma335.MetadataTokens.EventDefinitionHandle (rid "event")
                            ))
                                .Attributes
                    ]

                imageFlags |> shouldEqual hostFlags

                for _, _, _, token in typeOwners do
                    hostRows host token |> shouldEqual []

            for token in [ 0x20000001 ; 0x00000001 ] do
                hostRows host token |> shouldEqual []

            EmitScope.customAttributes scope |> List.ofSeq |> shouldEqual []

        let genOwners : Gen<FlagOwners> =
            gen {
                let! typeFlags =
                    Gen.elements
                        [
                            TypeAttributes.Public
                            TypeAttributes.NotPublic
                            TypeAttributes.Public ||| TypeAttributes.Sealed
                            TypeAttributes.Public ||| TypeAttributes.Abstract
                            // `tdSerializable`, and `tdSpecialName`.
                            TypeAttributes.Public ||| enum 0x2000
                            TypeAttributes.Public ||| TypeAttributes.SpecialName
                            TypeAttributes.Public
                            ||| TypeAttributes.Sealed
                            ||| TypeAttributes.BeforeFieldInit
                        ]

                let! methodFlags =
                    Gen.elements
                        [
                            MethodAttributes.Public ||| MethodAttributes.Static
                            MethodAttributes.Private ||| MethodAttributes.Static
                            MethodAttributes.Public
                            ||| MethodAttributes.Static
                            ||| MethodAttributes.HideBySig
                            MethodAttributes.Public
                            ||| MethodAttributes.Static
                            ||| MethodAttributes.SpecialName
                        ]

                let! ctorFlags =
                    Gen.elements
                        [
                            MethodAttributes.Public
                            MethodAttributes.Private
                            MethodAttributes.Public ||| MethodAttributes.HideBySig
                        ]

                let parameterFlags =
                    Gen.elements [ 0 ; 0x1 ; 0x2 ; 0x3 ; 0x10 ; 0x11 ; 0x12 ; 0x13 ]
                    |> Gen.map enum<ParameterAttributes>

                let! parameter = parameterFlags
                let! ret = parameterFlags

                let! fieldFlags =
                    Gen.elements
                        [
                            FieldAttributes.Public
                            FieldAttributes.Private ||| FieldAttributes.Static
                            // `fdNotSerialized`.
                            FieldAttributes.Public ||| enum 0x80
                            FieldAttributes.Public ||| FieldAttributes.SpecialName
                            FieldAttributes.Assembly ||| FieldAttributes.InitOnly
                        ]

                let! propertyFlags = Gen.elements [ PropertyAttributes.None ; PropertyAttributes.SpecialName ]
                let! eventFlags = Gen.elements [ EventAttributes.None ; EventAttributes.SpecialName ]

                return
                    {
                        Type = typeFlags
                        Method = methodFlags
                        Constructor = ctorFlags
                        Parameter = parameter
                        Return = ret
                        Field = fieldFlags
                        Property = propertyFlags
                        Event = eventFlags
                    }
            }

        let genSchedule : Gen<FlagsSchedule> =
            gen {
                let! typeCount = Gen.choose (1, 3)
                let! types = Gen.listOfLength typeCount genOwners
                let! count = Gen.choose (0, 40)

                let! calls =
                    Gen.listOfLength
                        count
                        (Gen.map3
                            (fun o c b ->
                                {
                                    Owner = o
                                    Constructor = c
                                    Blob = b
                                }
                            )
                            choice
                            choice
                            choice)

                return
                    {
                        Types = List.ofSeq types
                        Calls = List.ofSeq calls
                    }
            }

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 150) (Arb.fromGen genSchedule) property

        for known, targets in flagTable do
            coverage.Count (FlagsReach.Refused known) |> shouldBeGreaterThan 3

            for target, _ in targets do
                let kinds =
                    match target with
                    | "method"
                    | "method implementation" -> [ "method" ; "constructor" ]
                    | "parameter" -> [ "parameter" ; "return parameter" ]
                    | kind -> [ kind ]

                for kind in kinds do
                    coverage.Count (FlagsReach.Accepted (known, kind)) |> shouldBeGreaterThan 3

        coverage.Count FlagsReach.BitAlreadySet |> shouldBeGreaterThan 20
        coverage.Count FlagsReach.BitNotSet |> shouldBeGreaterThan 20
        coverage.Count FlagsReach.Impostor |> shouldBeGreaterThan 20

    /// One constructor of a validated pseudo-attribute, as the host's emitter and the mirror each see it.
    type private KeptConstructor =
        {
            Label : string
            Known : KnownCustomAttribute
            Constructor : ConstructorInfo
        }

    /// CoreLib's constructors of the six validated pseudo-attributes, both of `ClassInterface`'s and of
    /// `InterfaceType`'s.
    let private keptConstructors : KeptConstructor list =
        let ctor (label : string) (known : KnownCustomAttribute) (t : Type) (parameters : Type[]) =
            {
                Label = label
                Known = known
                Constructor = requireMember (t.GetConstructor parameters) $"%s{t.Name}(%A{parameters})"
            }

        [
            ctor
                "ClassInterface(short)"
                KnownCustomAttribute.ClassInterface
                typeof<ClassInterfaceAttribute>
                [| typeof<int16> |]
            ctor
                "ClassInterface(ClassInterfaceType)"
                KnownCustomAttribute.ClassInterface
                typeof<ClassInterfaceAttribute>
                [| typeof<ClassInterfaceType> |]
            ctor
                "InterfaceType(short)"
                KnownCustomAttribute.InterfaceType
                typeof<InterfaceTypeAttribute>
                [| typeof<int16> |]
            ctor
                "InterfaceType(ComInterfaceType)"
                KnownCustomAttribute.InterfaceType
                typeof<InterfaceTypeAttribute>
                [| typeof<ComInterfaceType> |]
            ctor "Guid" KnownCustomAttribute.Guid typeof<GuidAttribute> [| typeof<string> |]
            ctor
                "TypeLibVersion"
                KnownCustomAttribute.TypeLibVersion
                typeof<TypeLibVersionAttribute>
                [| typeof<int> ; typeof<int> |]
            ctor
                "ComCompatibleVersion"
                KnownCustomAttribute.ComCompatibleVersion
                typeof<ComCompatibleVersionAttribute>
                (Array.replicate 4 typeof<int>)
            ctor
                "AllowPartiallyTrustedCallers"
                KnownCustomAttribute.AllowPartiallyTrustedCallers
                typeof<System.Security.AllowPartiallyTrustedCallersAttribute>
                [||]
        ]

    let private keptConstructor (label : string) : KeptConstructor =
        keptConstructors |> List.find (fun c -> c.Label = label)

    /// The eight owner kinds of the validated pseudo-attributes' differential tests, in the order of `withOwners`.
    let private keptOwnerKinds : string list =
        [
            "assembly"
            "module"
            "type"
            "method"
            "field"
            "parameter"
            "property"
            "event"
        ]

    /// A fresh dynamic module's owners of each of `keptOwnerKinds`, each as how to apply an attribute to it and its
    /// token.
    let private hostKeptOwners
        (name : string)
        : ModuleBuilder * (string * (ConstructorInfo -> byte[] -> unit) * int) list
        =
        let assembly, host = newModule name
        let t = host.DefineType ("T", TypeAttributes.Public)

        let m =
            t.DefineMethod ("M", MethodAttributes.Public ||| MethodAttributes.Static, typeof<Void>, [| typeof<int> |])

        m.GetILGenerator().Emit OpCodes.Ret
        let x = m.DefineParameter (1, ParameterAttributes.None, "x")
        let f = t.DefineField ("F", typeof<int>, FieldAttributes.Public)

        let p =
            t.DefineProperty ("P", PropertyAttributes.None, typeof<int>, Type.EmptyTypes)

        let e = t.DefineEvent ("E", EventAttributes.None, typeof<Action>)

        let token (builder : obj) (field : string) : int =
            builder.GetType().GetField(field, instance).GetValue builder |> unbox<int>

        host,
        [
            "assembly", (fun c b -> assembly.SetCustomAttribute (c, b)), 0x20000001
            "module", (fun c b -> host.SetCustomAttribute (c, b)), 0x00000001
            "type", (fun c b -> t.SetCustomAttribute (c, b)), t.MetadataToken
            "method", (fun c b -> m.SetCustomAttribute (c, b)), m.MetadataToken
            "field", (fun c b -> f.SetCustomAttribute (c, b)), f.MetadataToken
            "parameter", (fun c b -> x.SetCustomAttribute (c, b)), token x "_token"
            "property", (fun c b -> p.SetCustomAttribute (c, b)), token p "m_tkProperty"
            "event", (fun c b -> e.SetCustomAttribute (c, b)), token e "m_evToken"
        ]

    let private hex (text : string) : byte list =
        text.Split (' ', StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (fun b -> Convert.ToByte (b, 16))
        |> List.ofSeq

    /// <summary>
    /// The blobs of the validated pseudo-attributes measured on .NET 10.0.14 and 11.0.0-rc.3: the planner's probe
    /// (<c>5z/probe</c>, scenario <c>kept</c>) and the decisions' (<c>5z-decide/probe3</c>, scenario <c>kept2</c>),
    /// each with its constructor, its owner, and whether real .NET wrote the row. A refusal is given with the reason
    /// CoreCLR's source gives for it, which real .NET does not report.
    /// </summary>
    let private measuredKeptBlobs : (string * string * byte list * Result<unit, KnownCustomAttributeRefusal>) list =
        let ok = Ok ()
        let malformed = Error KnownCustomAttributeRefusal.MalformedBlob
        let named = Error KnownCustomAttributeRefusal.NamedArgument
        let invalid = Error KnownCustomAttributeRefusal.InvalidValue
        let wrong = Error KnownCustomAttributeRefusal.WrongTarget
        let ciEnum = "ClassInterface(ClassInterfaceType)"
        let ciShort = "ClassInterface(short)"
        let itEnum = "InterfaceType(ComInterfaceType)"
        let itShort = "InterfaceType(short)"
        let guid = bytesOf guidText
        // A GUID string blob: the prolog, the length as given, the characters, then the tail as given.
        let str (length : byte list) (text : byte list) (tail : byte list) = [ 1uy ; 0uy ] @ length @ text @ tail

        let at (i : int) (b : byte) =
            guid |> List.mapi (fun j x -> if i = j then b else x)

        let ciEnumBlobs =
            [
                "01 00 00 00 00 00 00 00", ok
                "01 00 01 00 00 00 00 00", ok
                "01 00 02 00 00 00 00 00", ok
                "01 00 03 00 00 00 00 00", invalid
                "01 00 01 00 01 00 00 00", named
                "01 00 FF FF FF FF 00 00", invalid
                "", malformed
                "FF FF 01 00 00 00 00 00", malformed
                "01 00", malformed
                "01 00 01", malformed
                "01 00 01 00 00 00", ok
                "01 00 01 00 00 00 01 00 54 08 03 46 6F 6F 01 00 00 00", ok
            ]

        let guidBlobs =
            [
                str [ 36uy ] guid [ 0uy ; 0uy ], ok
                str [ 38uy ] (bytesOf ("{" + guidText + "}")) [ 0uy ; 0uy ], invalid
                str [ 4uy ] (bytesOf "0123") [ 0uy ; 0uy ], invalid
                str [ 36uy ] (bytesOf "0123456z-89ab-cdef-0123-456789abcdef") [ 0uy ; 0uy ], invalid
                str [ 36uy ] (bytesOf "0123456789abcdef0123456789abcdef0000") [ 0uy ; 0uy ], invalid
                hex "01 00 FF 00 00", invalid
                [], malformed
            ]

        let tlv = "01 00 01 00 00 00 02 00 00 00 00 00"
        let tlvNegative = "01 00 FF FF FF FF 00 00 00 00 00 00"
        let ccv = "01 00 01 00 00 00 02 00 00 00 03 00 00 00 04 00 00 00 00 00"
        let ccvNegative = "01 00 00 00 00 00 00 00 00 00 00 00 00 00 FF FF FF FF 00 00"

        [
            // The planner's `kept`.
            for owner in [ "type" ; "assembly" ] do
                for blob, outcome in ciEnumBlobs do
                    yield ciEnum, owner, hex blob, outcome
            for owner in [ "method" ; "parameter" ; "field" ; "module" ] do
                for blob, _ in ciEnumBlobs do
                    yield ciEnum, owner, hex blob, wrong
            yield ciShort, "type", hex "01 00 01 00 00 00", ok
            yield ciShort, "type", hex "01 00 03 00 00 00", invalid
            yield ciShort, "type", hex "01 00 FF 7F 00 00", invalid
            yield itEnum, "type", hex "01 00 00 00 00 00 00 00", ok
            yield itEnum, "type", hex "01 00 03 00 00 00 00 00", ok
            yield itEnum, "type", hex "01 00 04 00 00 00 00 00", invalid
            yield itEnum, "type", hex "01 00 02 00 01 00 00 00", named
            for blob in
                [
                    "01 00 00 00 00 00 00 00"
                    "01 00 03 00 00 00 00 00"
                    "01 00 04 00 00 00 00 00"
                ] do
                yield itEnum, "assembly", hex blob, wrong
            yield itShort, "type", hex "01 00 01 00 00 00", ok
            yield itShort, "type", hex "01 00 04 00 00 00", invalid
            for owner in [ "type" ; "assembly" ; "module" ] do
                for blob, outcome in guidBlobs do
                    yield "Guid", owner, blob, outcome
            for owner in [ "method" ; "parameter" ] do
                for blob, _ in guidBlobs do
                    yield "Guid", owner, blob, wrong
            yield "TypeLibVersion", "assembly", hex tlv, ok
            yield "TypeLibVersion", "assembly", hex tlvNegative, invalid
            yield "TypeLibVersion", "assembly", hex "01 00 01 00 00 00", malformed
            for blob in [ tlv ; tlvNegative ; "01 00 01 00 00 00" ] do
                yield "TypeLibVersion", "type", hex blob, wrong
            yield "ComCompatibleVersion", "assembly", hex ccv, ok
            yield "ComCompatibleVersion", "assembly", hex ccvNegative, invalid
            yield "ComCompatibleVersion", "type", hex ccv, wrong
            yield "ComCompatibleVersion", "type", hex ccvNegative, wrong
            for blob in [ "01 00 00 00" ; "FF FF" ; "" ] do
                yield "AllowPartiallyTrustedCallers", "assembly", hex blob, ok
                yield "AllowPartiallyTrustedCallers", "type", hex blob, wrong
                yield "AllowPartiallyTrustedCallers", "module", hex blob, wrong

            // The decisions' `kept2`.
            yield ciEnum, "type", hex "01 00 01 00 FF FF", ok
            yield ciEnum, "type", hex "01 00 01 00 00 80", ok
            yield ciEnum, "type", hex "01 00 01 00 FF FF DE AD", ok
            yield ciEnum, "type", hex "01 00 01 00 FF 7F", named
            yield ciEnum, "type", hex "01 00 01 00 00", ok
            yield ciEnum, "type", hex "01 00 01 00 FF", ok
            yield ciEnum, "type", hex "01 00 01 00 01 00 54 08 03 46 6F 6F 01 00 00 00", named
            yield ciEnum, "type", hex "01 00 01 00 01 00 53 08 03 46 6F 6F 01 00 00 00", named
            yield ciEnum, "type", hex "01 00 01 00 01 00", named
            yield ciEnum, "type", hex "01 00 01 00 00 00 DE AD BE EF", ok
            yield ciEnum, "type", hex "01 01 01 00 00 00", malformed
            yield ciEnum, "type", hex "02 00 01 00 00 00", malformed
            yield ciShort, "type", hex "01 00 01 00", ok
            yield ciShort, "type", hex "01 00 00 80 00 00", invalid
            yield ciShort, "type", hex "01 00 FF FF 00 00", invalid
            yield "Guid", "type", str [ 36uy ] guid [], ok
            yield "Guid", "type", str [ 36uy ] (bytesOf (guidText.ToUpperInvariant ())) [ 0uy ; 0uy ], ok
            yield "Guid", "type", str [ 36uy ] (bytesOf "01234567-89AB-cdef-0123-456789ABcdef") [ 0uy ; 0uy ], ok
            yield "Guid", "type", str [ 0x80uy ; 36uy ] guid [ 0uy ; 0uy ], ok
            yield "Guid", "type", str [ 0xC0uy ; 0uy ; 0uy ; 36uy ] guid [ 0uy ; 0uy ], ok
            yield "Guid", "type", str [ 36uy ] guid [ 0xFFuy ; 0xFFuy ], ok
            yield "Guid", "type", str [ 36uy ] guid [ 1uy ; 0uy ], named
            yield "Guid", "type", str [ 36uy ] guid [ 7uy ], ok
            yield "Guid", "type", str [ 36uy ] (at 5 0uy) [ 0uy ; 0uy ], invalid
            yield "Guid", "type", str [ 36uy ] (at 5 0xC3uy) [ 0uy ; 0uy ], invalid
            yield "Guid", "type", str [ 37uy ] guid [], malformed
            yield "Guid", "type", str [ 35uy ] (List.take 35 guid) [ 0uy ; 0uy ], invalid
            yield "Guid", "type", hex "01 00 00 00 00", invalid
            yield "Guid", "type", str [ 36uy ] (bytesOf "012345678-9ab-cdef-0123-456789abcdef") [ 0uy ; 0uy ], invalid
            yield "Guid", "type", str [ 36uy ] (bytesOf "+1234567-89ab-cdef-0123-456789abcdef") [ 0uy ; 0uy ], invalid
            yield "Guid", "type", str [ 36uy ] (bytesOf " 1234567-89ab-cdef-0123-456789abcdef") [ 0uy ; 0uy ], invalid
            yield "TypeLibVersion", "assembly", hex "01 00 01 00 00 00 02 00 00 00", ok
            yield "TypeLibVersion", "assembly", hex "01 00 01 00 00 00 02 00 00 00 FF FF", ok
            yield "TypeLibVersion", "assembly", hex "01 00 01 00 00 00 02 00 00 00 01 00", named
            yield "TypeLibVersion", "assembly", hex "01 00 00 00 00 00 00 00 00 00", ok
            yield "TypeLibVersion", "assembly", hex "01 00 00 00 00 00 00 00 00 80", invalid
            yield "TypeLibVersion", "assembly", hex "01 00 01 00 00 00 02 00 00", malformed
        ]

    [<Test>]
    let ``the measured blobs of the validated pseudo-attributes are kept or refused as on real .NET`` () : unit =
        for label, owner, blob, expected in measuredKeptBlobs do
            let entry = keptConstructor label
            let describe = $"%s{label} on a %s{owner} with %A{blob}"

            // The host's emitter, whose answer the measurement recorded.
            let host, hostOwners = hostKeptOwners "Measured"
            let _, apply, token = hostOwners |> List.find (fun (k, _, _) -> k = owner)

            let threw =
                try
                    apply entry.Constructor (Array.ofList blob)
                    false
                with :? ArgumentException ->
                    true

            if threw <> Result.isError expected then
                failwith $"%s{describe}: the host threw %b{threw}, but the measurement says %A{expected}"

            hostRows host token
            |> List.map (fun (_, _, b) -> b)
            |> shouldEqual (if threw then [] else [ blob ])

            // The emitter's rule in Domain, which also gives the reason.
            let scope, owners = withOwners ()

            let constructor, scope =
                constructorOf (Parent.ReferenceInAssembly (KnownCustomAttribute.name entry.Known)) scope

            let mirror = owners.[List.findIndex ((=) owner) keptOwnerKinds]

            match
                EmitScopeDefinitions.defineCustomAttribute mirror constructor (ImmutableArray.CreateRange blob) scope,
                expected
            with
            | CustomAttributeDefinition.Defined (_, after), Ok () ->
                EmitScope.customAttributes after
                |> Seq.map (fun row -> row.Parent, List.ofSeq (EmitScope.blobAt row.Value after))
                |> List.ofSeq
                |> shouldEqual [ mirror, blob ]
            | CustomAttributeDefinition.Refused refusal, Error reason when refusal = reason -> ()
            | outcome, _ -> failwith $"%s{describe}: Domain answered %A{outcome}, but the measurement says %A{expected}"

    /// One generated `SetCustomAttribute` call of the validation differential: a constructor, an owner kind and a
    /// blob, with the features the blob was generated with.
    type private KeptCall =
        {
            Constructor : int
            Owner : int
            Blob : byte list
            Features : Set<string>
        }

    [<RequireQualifiedAccess>]
    type private ValidationReach =
        | Kept of KnownCustomAttribute
        | Refused of KnownCustomAttribute
        | Feature of string * kept : bool

    [<Test>]
    let ``the rows the validated pseudo-attributes leave are what the host's emitter writes`` () : unit =
        let property (cover : ValidationReach -> unit) (calls : KeptCall list) : unit =
            let host, hostOwners = hostKeptOwners "ValidationOracle"

            let hostTokens =
                keptConstructors
                |> List.map (fun c -> host.GetMethodMetadataToken c.Constructor)

            let scope, owners = withOwners ()
            let mutable scope = scope

            let mirrors =
                [
                    for entry in keptConstructors do
                        let mirror, s =
                            constructorOf (Parent.ReferenceInAssembly (KnownCustomAttribute.name entry.Known)) scope

                        scope <- s
                        yield mirror
                ]

            for call in calls do
                let entry = keptConstructors.[call.Constructor]
                let ownerKind = keptOwnerKinds.[call.Owner]
                let _, apply, _ = hostOwners.[call.Owner]

                let threw =
                    try
                        apply entry.Constructor (Array.ofList call.Blob)
                        false
                    with :? ArgumentException as e ->
                        e.Message |> shouldEqual "Invalid custom attribute provided."
                        true

                let kept =
                    match
                        EmitScopeDefinitions.defineCustomAttribute
                            owners.[call.Owner]
                            mirrors.[call.Constructor]
                            (ImmutableArray.CreateRange call.Blob)
                            scope
                    with
                    | CustomAttributeDefinition.Defined (_, after) ->
                        scope <- after
                        true
                    | CustomAttributeDefinition.Refused _ -> false
                    | outcome -> failwith $"%s{entry.Label} on a %s{ownerKind}: %A{outcome}"

                if kept = threw then
                    failwith
                        $"%s{entry.Label} on a %s{ownerKind} with %A{call.Blob}: the host threw %b{threw}, and Domain kept it %b{kept}"

                cover (
                    if kept then
                        ValidationReach.Kept entry.Known
                    else
                        ValidationReach.Refused entry.Known
                )

                for feature in call.Features do
                    cover (ValidationReach.Feature (feature, kept))

            // Each owner's rows, in order: the row's RID, its constructor and its blob.
            let mirrorRows (owner : HasCustomAttribute) =
                EmitScope.customAttributes scope
                |> Seq.indexed
                |> Seq.filter (fun (_, row) -> row.Parent = owner)
                |> Seq.map (fun (i, row) ->
                    i + 1, List.findIndex ((=) row.Constructor) mirrors, List.ofSeq (EmitScope.blobAt row.Value scope)
                )
                |> List.ofSeq

            for (_, _, token), owner in List.zip hostOwners (List.take keptOwnerKinds.Length owners) do
                hostRows host token
                |> List.map (fun (row, constructor, blob) ->
                    row &&& 0xFFFFFF, List.findIndex ((=) constructor) hostTokens, blob
                )
                |> shouldEqual (mirrorRows owner)

        let genCall : Gen<KeptCall> =
            gen {
                let! constructor = Gen.choose (0, keptConstructors.Length - 1)
                let known = keptConstructors.[constructor].Known
                let _, (targets, own, _) = keptTable |> List.find (fun (k, _) -> k = known)

                let onTarget =
                    keptOwnerKinds
                    |> List.indexed
                    |> List.filter (fun (_, kind) -> List.contains kind targets)
                    |> List.map fst

                let! owner = Gen.frequency [ 1, Gen.elements onTarget ; 1, Gen.choose (0, keptOwnerKinds.Length - 1) ]
                let! _, (_, other, _) = Gen.elements keptTable
                let! arguments = Gen.frequency [ 3, Gen.constant own ; 1, Gen.constant other ]
                let! blob, features = genGrammarBlob arguments

                return
                    {
                        Constructor = constructor
                        Owner = owner
                        Blob = blob
                        Features = features
                    }
            }

        let genCalls : Gen<KeptCall list> =
            gen {
                let! count = Gen.choose (0, 40)
                let! calls = Gen.listOfLength count genCall
                return List.ofSeq calls
            }

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 150) (Arb.fromGen genCalls) property

        for known, _ in keptTable do
            coverage.Count (ValidationReach.Kept known) |> shouldBeGreaterThan 10
            coverage.Count (ValidationReach.Refused known) |> shouldBeGreaterThan 10

        // The four-byte enum's high half read as a named-argument count: 0 keeps the row, 1 refuses it.
        coverage.Count (ValidationReach.Feature ("enum high half 0", true))
        |> shouldBeGreaterThan 10

        coverage.Count (ValidationReach.Feature ("enum high half 1", false))
        |> shouldBeGreaterThan 10

        for feature in
            [
                "prolog wrong"
                "empty"
                "count -1"
                "count -32768"
                "count 0"
                "count 1"
                "count 32767"
                "named argument"
                "one trailing byte"
                "length one-byte"
                "length two-byte"
                "length four-byte"
                "length E0-FE"
                "length past end"
                "null string"
                "guid upper"
                "guid mixed"
                "guid NUL"
                "guid non-ASCII"
                "guid dash moved"
            ] do
            coverage.Count (ValidationReach.Feature (feature, false))
            |> shouldBeGreaterThan 3

        for feature in
            [
                "count -1"
                "count -32768"
                "count 0"
                "one trailing byte"
                "length one-byte"
                "length two-byte"
                "length four-byte"
                "guid upper"
                "guid mixed"
            ] do
            coverage.Count (ValidationReach.Feature (feature, true))
            |> shouldBeGreaterThan 3
