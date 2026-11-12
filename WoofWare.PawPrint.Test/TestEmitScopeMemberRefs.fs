namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The member references CoreCLR's emitter adds to a dynamic module for
/// `ModuleBuilder_GetMemberRefOfMethodInfo`: each `MemberRef` row is found among the rows already
/// there before one is added, and its signature is the method's own, with each type it names by token
/// translated into a TypeRef of the dynamic module.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeMemberRefs =

    /// Names a request may give besides those already in the scope.
    let private memberNames : string list = [ "M" ; "N" ; ".ctor" ; "日本" ; "m" ]

    /// Signatures a request may give besides those already in the scope, some of them alike but for
    /// their length or one byte.
    let private signatures : ImmutableArray<byte> list =
        [
            ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x01uy |]
            ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x08uy |]
            ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x08uy ; 0x08uy |]
            ImmutableArray.Create<byte> [| 0x00uy ; 0x00uy ; 0x08uy |]
        ]

    /// Every parent a `MemberRef` of `scope` could name.
    let private parents (scope : EmitScope) : MemberRefParent list =
        [
            for i in 1 .. (EmitScope.typeDefs scope).Count do
                yield MemberRefParent.TypeDef (TypeDefRid i)
            for i in 1 .. (EmitScope.typeRefs scope).Count do
                yield MemberRefParent.TypeRef (TypeRefRid i)
            for i in 1 .. (EmitScope.methodDefs scope).Count do
                yield MemberRefParent.MethodDef (MethodDefRid i)
        ]

    /// A row as the model holds it.
    let private held (scope : EmitScope) (row : MemberRefRow) : MemberRefParent * string * byte list =
        row.Parent, EmitScope.stringAt row.Name scope, List.ofSeq (EmitScope.blobAt row.Signature scope)

    /// A request: either one taken from a row the scope already has, or one made of choices.
    type private Request =
        | Existing of row : int
        | Made of parent : int * name : int * signature : int

    let private genRequest : Gen<Request> =
        Gen.frequency
            [
                1, Gen.map Request.Existing EmitScopeGen.choice
                3,
                Gen.map3
                    (fun p n s -> Request.Made (p, n, s))
                    EmitScopeGen.choice
                    (Gen.choose (0, memberNames.Length - 1))
                    (Gen.choose (0, signatures.Length - 1))
            ]

    [<Test>]
    let ``a member reference is the first row with its parent, name and signature, or else a new row`` () : unit =
        let mutable found = 0
        let mutable foundOfTwo = 0
        let mutable added = 0

        let property (generated : EmitScope) (duplicate : int option) (requests : Request list) : unit =
            // The emitter never adds a row it would find, but a scope may hold two alike all the same:
            // the first of them is the one found.
            let start =
                match duplicate with
                | Some row when (EmitScope.memberRefs generated).Count > 0 ->
                    let rows = EmitScope.memberRefs generated
                    EmitScope.addMemberRef rows.[row % rows.Count] generated |> snd
                | _ -> generated

            let candidates = parents start
            let mutable model = EmitScope.memberRefs start |> Seq.map (held start) |> List.ofSeq
            let mutable scope = start

            for request in requests do
                let parent, name, signature =
                    match request with
                    | Request.Existing row when not model.IsEmpty ->
                        let parent, name, signature = List.item (row % model.Length) model
                        parent, name, ImmutableArray.CreateRange signature
                    | Request.Existing row ->
                        List.item (row % candidates.Length) candidates, memberNames.Head, signatures.Head
                    | Request.Made (p, n, s) ->
                        List.item (p % candidates.Length) candidates, memberNames.[n], signatures.[s]

                let key = parent, name, List.ofSeq signature
                let rid, next = EmitScopeReferences.defineMemberRef parent name signature scope

                match List.tryFindIndex ((=) key) model with
                | Some index ->
                    found <- found + 1

                    if List.filter ((=) key) model |> List.length > 1 then
                        foundOfTwo <- foundOfTwo + 1

                    rid |> shouldEqual (MemberRefRid (index + 1))
                    Object.ReferenceEquals (next, scope) |> shouldEqual true

                    EmitScopeReferences.tryFindMemberRef parent name signature scope
                    |> shouldEqual (Some rid)
                | None ->
                    added <- added + 1

                    EmitScopeReferences.tryFindMemberRef parent name signature scope
                    |> shouldEqual None

                    rid |> shouldEqual (MemberRefRid (model.Length + 1))
                    model <- model @ [ key ]

                    EmitScope.memberRefs next
                    |> Seq.map (held next)
                    |> List.ofSeq
                    |> shouldEqual model

                    // Only the new row and its heap entries are added.
                    EmitScope.typeRefs next |> shouldEqual (EmitScope.typeRefs scope)
                    EmitScope.typeDefs next |> shouldEqual (EmitScope.typeDefs scope)
                    EmitScope.methodDefs next |> shouldEqual (EmitScope.methodDefs scope)

                scope <- next

        let arb =
            Gen.zip3 EmitScopeGen.genScope (Gen.optionOf EmitScopeGen.choice) (Gen.listOf genRequest)
            |> Arb.fromGen

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            Prop.forAll arb (fun (start, duplicate, requests) -> property start duplicate requests)
        )

        found |> shouldBeGreaterThan 100
        foundOfTwo |> shouldBeGreaterThan 20
        added |> shouldBeGreaterThan 100

    /// A compressed unsigned integer (ECMA-335 II.23.2) and how many bytes it is encoded in: 1 for its
    /// shortest form, or an overlong 2 or 4, which CoreCLR's reader accepts as well.
    type private Compressed =
        {
            Value : int
            Width : int
        }

    let private overlong (value : int) (width : int) : byte list =
        match width with
        | 2 when value < 0x4000 -> [ byte (0x80 ||| (value >>> 8)) ; byte value ]
        | 4 ->
            [
                byte (0xC0 ||| (value >>> 24))
                byte (value >>> 16)
                byte (value >>> 8)
                byte value
            ]
        | _ -> EmitScopeGen.compressedUnsigned value

    let private encoded (integer : Compressed) : byte list = overlong integer.Value integer.Width

    /// A type in a signature (ECMA-335 II.23.2.12), each token given as its `TypeDefOrRefOrSpecEncoded`
    /// value.
    type private Shape =
        | Primitive of code : byte
        | Named of valueType : bool * token : int
        | SzArray of Shape
        | Pointer of Shape
        | VoidPointer
        | ByRef of Shape
        | TypeParameter of index : Compressed
        | MethodParameter of index : Compressed
        | Instance of valueType : bool * token : int * countWidth : int * arguments : Shape list
        | Array of ArrayShape
        | FunctionPointer of MethodShape
        | Modified of required : bool * token : int * Shape

    /// An `ARRAY`'s element type and shape (ECMA-335 II.23.2.13). Each lower bound is a compressed
    /// signed integer, whose length is read as an unsigned one's.
    and private ArrayShape =
        {
            Element : Shape
            Rank : Compressed
            SizeCountWidth : int
            Sizes : Compressed list
            BoundCountWidth : int
            LowerBounds : Compressed list
        }

    /// A method signature (ECMA-335 II.23.2.1). A return type of `None` is `void`.
    and private MethodShape =
        {
            Header : byte
            GenericArity : Compressed
            ParameterCountWidth : int
            Return : Shape option
            Parameters : Shape list
        }

    /// The tokens a shape names, in the order its bytes hold them, each as its
    /// `TypeDefOrRefOrSpecEncoded` value.
    let rec private tokensOf (shape : Shape) : int list =
        match shape with
        | Shape.Primitive _
        | Shape.VoidPointer
        | Shape.TypeParameter _
        | Shape.MethodParameter _ -> []
        | Shape.Named (_, token) -> [ token ]
        | Shape.Instance (_, token, _, arguments) -> token :: List.collect tokensOf arguments
        | Shape.Modified (_, token, inner) -> token :: tokensOf inner
        | Shape.SzArray inner
        | Shape.Pointer inner
        | Shape.ByRef inner -> tokensOf inner
        | Shape.Array array -> tokensOf array.Element
        | Shape.FunctionPointer signature -> methodTokensOf signature

    and private methodTokensOf (signature : MethodShape) : int list =
        (Option.toList signature.Return |> List.collect tokensOf)
        @ List.collect tokensOf signature.Parameters

    /// The shape with each token replaced, in the order `tokensOf` lists them, by the next of
    /// `replacements`; and the replacements left over.
    let rec private replaceTokens (replacements : int list) (shape : Shape) : Shape * int list =
        let take (replacements : int list) : int * int list =
            match replacements with
            | head :: rest -> head, rest
            | [] -> failwith "fewer replacements than tokens"

        let many (replacements : int list) (shapes : Shape list) : Shape list * int list =
            List.mapFold (fun replacements shape -> replaceTokens replacements shape) replacements shapes

        match shape with
        | Shape.Primitive _
        | Shape.VoidPointer
        | Shape.TypeParameter _
        | Shape.MethodParameter _ -> shape, replacements
        | Shape.Named (valueType, _) ->
            let token, replacements = take replacements
            Shape.Named (valueType, token), replacements
        | Shape.Instance (valueType, _, countWidth, arguments) ->
            let token, replacements = take replacements
            let arguments, replacements = many replacements arguments
            Shape.Instance (valueType, token, countWidth, arguments), replacements
        | Shape.Modified (required, _, inner) ->
            let token, replacements = take replacements
            let inner, replacements = replaceTokens replacements inner
            Shape.Modified (required, token, inner), replacements
        | Shape.SzArray inner ->
            let inner, replacements = replaceTokens replacements inner
            Shape.SzArray inner, replacements
        | Shape.Pointer inner ->
            let inner, replacements = replaceTokens replacements inner
            Shape.Pointer inner, replacements
        | Shape.ByRef inner ->
            let inner, replacements = replaceTokens replacements inner
            Shape.ByRef inner, replacements
        | Shape.Array array ->
            let element, replacements = replaceTokens replacements array.Element

            Shape.Array
                { array with
                    Element = element
                },
            replacements
        | Shape.FunctionPointer signature ->
            let signature, replacements = replaceMethodTokens replacements signature
            Shape.FunctionPointer signature, replacements

    and private replaceMethodTokens (replacements : int list) (signature : MethodShape) : MethodShape * int list =
        let returns, replacements =
            match signature.Return with
            | None -> None, replacements
            | Some shape ->
                let shape, replacements = replaceTokens replacements shape
                Some shape, replacements

        let parameters, replacements =
            List.mapFold (fun replacements shape -> replaceTokens replacements shape) replacements signature.Parameters

        { signature with
            Return = returns
            Parameters = parameters
        },
        replacements

    /// A shape's bytes. With `shortestIndices`, a type or method parameter's index and an instantiation's
    /// argument count are in their shortest form, as CoreCLR's emitter writes them; every other integer
    /// is encoded as the shape says either way.
    let rec private encode (shortestIndices : bool) (shape : Shape) : byte list =
        let index (integer : Compressed) : byte list =
            if shortestIndices then
                EmitScopeGen.compressedUnsigned integer.Value
            else
                encoded integer

        match shape with
        | Shape.Primitive code -> [ code ]
        | Shape.Named (valueType, token) ->
            (if valueType then 0x11uy else 0x12uy) :: EmitScopeGen.compressedUnsigned token
        | Shape.SzArray inner -> 0x1Duy :: encode shortestIndices inner
        | Shape.Pointer inner -> 0x0Fuy :: encode shortestIndices inner
        | Shape.VoidPointer -> [ 0x0Fuy ; 0x01uy ]
        | Shape.ByRef inner -> 0x10uy :: encode shortestIndices inner
        | Shape.TypeParameter integer -> 0x13uy :: index integer
        | Shape.MethodParameter integer -> 0x1Euy :: index integer
        | Shape.Instance (valueType, token, countWidth, arguments) ->
            [
                yield 0x15uy
                yield (if valueType then 0x11uy else 0x12uy)
                yield! EmitScopeGen.compressedUnsigned token
                yield!
                    index
                        {
                            Value = arguments.Length
                            Width = countWidth
                        }
                for argument in arguments do
                    yield! encode shortestIndices argument
            ]
        | Shape.Array array ->
            [
                yield 0x14uy
                yield! encode shortestIndices array.Element
                yield! encoded array.Rank
                yield! overlong array.Sizes.Length array.SizeCountWidth
                for size in array.Sizes do
                    yield! encoded size
                yield! overlong array.LowerBounds.Length array.BoundCountWidth
                for bound in array.LowerBounds do
                    yield! encoded bound
            ]
        | Shape.FunctionPointer signature -> 0x1Buy :: encodeMethod shortestIndices signature
        | Shape.Modified (required, token, inner) ->
            [
                yield (if required then 0x1Fuy else 0x20uy)
                yield! EmitScopeGen.compressedUnsigned token
                yield! encode shortestIndices inner
            ]

    and private encodeMethod (shortestIndices : bool) (signature : MethodShape) : byte list =
        [
            yield signature.Header
            if signature.Header &&& 0x10uy <> 0uy then
                yield! encoded signature.GenericArity
            yield! overlong signature.Parameters.Length signature.ParameterCountWidth
            match signature.Return with
            | None -> yield 0x01uy
            | Some shape -> yield! encode shortestIndices shape
            for parameter in signature.Parameters do
                yield! encode shortestIndices parameter
        ]

    let private genBool : Gen<bool> = Gen.elements [ false ; true ]

    /// Mostly the shortest form, so that most signatures are not all overlong.
    let private genWidth : Gen<int> = Gen.elements [ 1 ; 1 ; 2 ; 4 ]

    let private genCompressed (low : int) (high : int) : Gen<Compressed> =
        Gen.map2
            (fun value width ->
                {
                    Value = value
                    Width = width
                }
            )
            (Gen.choose (low, high))
            genWidth

    /// The primitive element types, `object`, `string` and `TypedReference` among them.
    let private primitives : byte list =
        [ 0x02uy .. 0x0Euy ] @ [ 0x16uy ; 0x18uy ; 0x19uy ; 0x1Cuy ]

    /// Where a generated signature's tokens come from: one for a named type or an instantiation, and
    /// one for a custom modifier, where the grammar allows a TypeSpec too.
    type private TokenGens =
        {
            Named : Gen<int>
            Modifier : Gen<int>
        }

    /// A type; one that names no type by token unless given `tokens`.
    let rec private genShape (tokens : TokenGens option) (size : int) : Gen<Shape> =
        let leaf =
            Gen.frequency
                [
                    yield 3, Gen.map Shape.Primitive (Gen.elements primitives)
                    yield 1, Gen.constant Shape.VoidPointer
                    yield 3, Gen.map Shape.TypeParameter (genCompressed 0 0x5000)
                    yield 3, Gen.map Shape.MethodParameter (genCompressed 0 0x5000)
                    match tokens with
                    | Some tokens -> yield 3, Gen.map2 (fun v t -> Shape.Named (v, t)) genBool tokens.Named
                    | None -> ()
                ]

        if size <= 0 then
            leaf
        else
            let inner = genShape tokens (size / 2)

            Gen.frequency
                [
                    yield 3, leaf
                    yield 1, Gen.map Shape.SzArray inner
                    yield 1, Gen.map Shape.Pointer inner
                    yield 1, Gen.map Shape.ByRef inner

                    yield
                        1,
                        gen {
                            let! element = inner
                            let! rank = genCompressed 1 4
                            let! sizeCountWidth = genWidth
                            let! sizes = Gen.listOf (genCompressed 0 0x5000) |> Gen.map (List.truncate rank.Value)
                            let! boundCountWidth = genWidth
                            let! bounds = Gen.listOf (genCompressed 0 0x1000) |> Gen.map (List.truncate rank.Value)

                            return
                                Shape.Array
                                    {
                                        Element = element
                                        Rank = rank
                                        SizeCountWidth = sizeCountWidth
                                        Sizes = sizes
                                        BoundCountWidth = boundCountWidth
                                        LowerBounds = bounds
                                    }
                        }

                    yield 1, Gen.map Shape.FunctionPointer (genMethod tokens (size / 2) false)

                    match tokens with
                    | None -> ()
                    | Some tokens ->
                        yield
                            1,
                            gen {
                                let! valueType = genBool
                                let! token = tokens.Named
                                let! countWidth = genWidth
                                let! count = Gen.choose (1, 3)
                                let! arguments = Gen.listOfLength count inner
                                return Shape.Instance (valueType, token, countWidth, arguments)
                            }

                        yield 1, Gen.map3 (fun r t s -> Shape.Modified (r, t, s)) genBool tokens.Modifier inner
                ]

    /// A method signature: a definition's when `allowGeneric`, and so possibly generic; a function
    /// pointer's otherwise. It names no type by token unless given `tokens`.
    and private genMethod (tokens : TokenGens option) (size : int) (allowGeneric : bool) : Gen<MethodShape> =
        gen {
            let! hasThis = genBool
            let! convention = Gen.elements [ 0x00uy ; 0x05uy ]
            let! generic = if allowGeneric then genBool else Gen.constant false
            let! arity = genCompressed 1 3
            let! parameterCountWidth = genWidth
            let! returns = Gen.optionOf (genShape tokens size)
            let! parameters = Gen.listOf (genShape tokens size) |> Gen.map (List.truncate 4)

            return
                {
                    Header =
                        convention
                        ||| (if hasThis then 0x20uy else 0uy)
                        ||| (if generic then 0x10uy else 0uy)
                    GenericArity = arity
                    ParameterCountWidth = parameterCountWidth
                    Return = returns
                    Parameters = parameters
                }
        }

    /// A TypeDef of a fabricated source module: its namespace and name, and the index of the earlier type
    /// enclosing it, if it is nested.
    type private SourceType =
        {
            Namespace : string
            Name : string
            Enclosing : int option
        }

    /// An AssemblyRef of a fabricated source module.
    type private SourceAssemblyRef =
        {
            Name : string
            Version : Version
            Culture : string
            Key : PublicKeyOrToken
            /// Without `PublicKey`, which the image sets exactly for a full key.
            Flags : AssemblyFlags
            Hash : byte list
        }

    /// What scopes a fabricated source TypeRef: one of the source's AssemblyRefs, an earlier TypeRef
    /// enclosing it, nothing, the source module, or a ModuleRef.
    [<RequireQualifiedAccess>]
    type private SourceTypeRefScope =
        | AssemblyRef of int
        | Enclosing of int
        | Nil
        | Module
        | ModuleRef

    type private SourceTypeRef =
        {
            Scope : SourceTypeRefScope
            Namespace : string
            Name : string
        }

    /// A fabricated source assembly, from whose module a signature is translated. Its TypeDef `i + 2` is
    /// `Types.[i]`, its TypeRef `i + 1` is `TypeRefs.[i]`, and it has one TypeSpec, which the
    /// translation refuses.
    type private SourceAssembly =
        {
            Name : string
            Version : Version
            Culture : string
            Flags : AssemblyFlags
            /// `None` for an unsigned assembly.
            PublicKey : byte list option
            Types : SourceType list
            AssemblyRefs : SourceAssemblyRef list
            TypeRefs : SourceTypeRef list
        }

    let private sourceImage (source : SourceAssembly) : byte[] =
        let metadata = MetadataBuilder ()

        let optionalString (value : string) : StringHandle =
            if value = "" then
                StringHandle ()
            else
                metadata.GetOrAddString value

        metadata.AddModule (
            0,
            metadata.GetOrAddString "Source.dll",
            metadata.GetOrAddGuid (Guid "6a4b3d0e-1f52-4c8b-9b1e-5f0c2d7e8a91"),
            GuidHandle (),
            GuidHandle ()
        )
        |> ignore<ModuleDefinitionHandle>

        metadata.AddAssembly (
            metadata.GetOrAddString source.Name,
            source.Version,
            optionalString source.Culture,
            (match source.PublicKey with
             | Some key -> metadata.GetOrAddBlob (List.toArray key)
             | None -> BlobHandle ()),
            (match source.PublicKey with
             | Some _ -> source.Flags ||| AssemblyFlags.PublicKey
             | None -> source.Flags),
            AssemblyHashAlgorithm.None
        )
        |> ignore<AssemblyDefinitionHandle>

        let assemblyRefs =
            [
                for reference in source.AssemblyRefs do
                    let bytes, flags =
                        match reference.Key with
                        | PublicKeyOrToken.Absent -> BlobHandle (), reference.Flags
                        | PublicKeyOrToken.Token token -> metadata.GetOrAddBlob token, reference.Flags
                        | PublicKeyOrToken.Key key ->
                            metadata.GetOrAddBlob key, reference.Flags ||| AssemblyFlags.PublicKey

                    metadata.AddAssemblyReference (
                        metadata.GetOrAddString reference.Name,
                        reference.Version,
                        optionalString reference.Culture,
                        bytes,
                        flags,
                        (if reference.Hash.IsEmpty then
                             BlobHandle ()
                         else
                             metadata.GetOrAddBlob (List.toArray reference.Hash))
                    )
            ]

        let moduleRef = metadata.AddModuleReference (metadata.GetOrAddString "Other.dll")

        source.TypeRefs
        |> List.iter (fun reference ->
            let scope : EntityHandle =
                match reference.Scope with
                | SourceTypeRefScope.AssemblyRef index -> AssemblyReferenceHandle.op_Implicit assemblyRefs.[index]
                | SourceTypeRefScope.Enclosing index ->
                    TypeReferenceHandle.op_Implicit (MetadataTokens.TypeReferenceHandle (index + 1))
                | SourceTypeRefScope.Nil -> EntityHandle ()
                | SourceTypeRefScope.Module -> ModuleDefinitionHandle.op_Implicit EntityHandle.ModuleDefinition
                | SourceTypeRefScope.ModuleRef -> ModuleReferenceHandle.op_Implicit moduleRef

            metadata.AddTypeReference (
                scope,
                optionalString reference.Namespace,
                metadata.GetOrAddString reference.Name
            )
            |> ignore<TypeReferenceHandle>
        )

        metadata.AddTypeSpecification (metadata.GetOrAddBlob [| 0x1Duy ; 0x08uy |])
        |> ignore<TypeSpecificationHandle>

        metadata.AddTypeDefinition (
            enum<TypeAttributes> 0,
            StringHandle (),
            metadata.GetOrAddString "<Module>",
            EntityHandle (),
            MetadataTokens.FieldDefinitionHandle 1,
            MetadataTokens.MethodDefinitionHandle 1
        )
        |> ignore<TypeDefinitionHandle>

        for t in source.Types do
            metadata.AddTypeDefinition (
                (if t.Enclosing.IsSome then
                     TypeAttributes.NestedPublic
                 else
                     TypeAttributes.Public),
                optionalString t.Namespace,
                metadata.GetOrAddString t.Name,
                EntityHandle (),
                MetadataTokens.FieldDefinitionHandle 1,
                MetadataTokens.MethodDefinitionHandle 1
            )
            |> ignore<TypeDefinitionHandle>

        // The NestedClass table is sorted by the nested type.
        source.Types
        |> List.iteri (fun i t ->
            match t.Enclosing with
            | Some enclosing ->
                metadata.AddNestedType (
                    MetadataTokens.TypeDefinitionHandle (i + 2),
                    MetadataTokens.TypeDefinitionHandle (enclosing + 2)
                )
            | None -> ()
        )

        let image = BlobBuilder ()

        ManagedPEBuilder(
            PEHeaderBuilder (imageCharacteristics = Characteristics.Dll),
            MetadataRootBuilder metadata,
            BlobBuilder ()
        )
            .Serialize
            image
        |> ignore<BlobContentId>

        image.ToArray ()

    /// A 160-byte `PublicKeyBlob` that `StrongNameIsValidPublicKey` accepts: `CALG_RSA_SIGN`, `CALG_SHA1`,
    /// the key's length, and a `PUBLICKEYBLOB` whose bytes `seed` varies.
    let private validKey (seed : int) : byte list =
        [
            0x00uy
            0x24uy
            0x00uy
            0x00uy
            0x04uy
            0x80uy
            0x00uy
            0x00uy
            0x94uy
            0x00uy
            0x00uy
            0x00uy
        ]
        @ (0x06uy :: List.init 147 (fun i -> byte (i * seed + 1)))

    /// Two public keys, and a blob of a key's length that is not one.
    let private keyOne : byte list = validKey 7
    let private keyTwo : byte list = validKey 13
    let private notAKey : byte list = List.init 160 (fun i -> byte (i * 3 + 2))

    let private tokenOf (key : byte list) : byte list =
        List.ofSeq (AssemblyRefRequest.publicKeyToken (ImmutableArray.CreateRange key))

    /// The emitting assembly's name and version, which a source AssemblyRef may name.
    let private emittingName : string = "Dyn"

    /// AssemblyRefs a source may hold: another assembly by nothing, by token or by full key, in another
    /// culture or with a hash; a third by a blob that is not a key; and the emitting assembly by nothing,
    /// by the token of `keyTwo`, or by `keyTwo` itself.
    let private sourceAssemblyRefs : SourceAssemblyRef list =
        let reference (name : string) (version : Version) (key : PublicKeyOrToken) : SourceAssemblyRef =
            {
                Name = name
                Version = version
                Culture = ""
                Key = key
                Flags = enum<AssemblyFlags> 0
                Hash = []
            }

        let other = reference "Other" (Version (1, 0, 0, 0))
        let emitting = reference emittingName (Version (0, 0, 0, 0))

        [
            other PublicKeyOrToken.Absent
            other (PublicKeyOrToken.Token (ImmutableArray.CreateRange (tokenOf keyOne)))
            other (PublicKeyOrToken.Key (ImmutableArray.CreateRange keyOne))
            { other PublicKeyOrToken.Absent with
                Culture = "fr"
            }
            { other (PublicKeyOrToken.Token (ImmutableArray.CreateRange (tokenOf keyOne))) with
                Hash = [ 1uy ; 2uy ; 3uy ]
                Flags = enum<AssemblyFlags> 0x0100
            }
            { other (PublicKeyOrToken.Key (ImmutableArray.CreateRange notAKey)) with
                Name = "Unkeyed"
            }
            emitting PublicKeyOrToken.Absent
            emitting (PublicKeyOrToken.Token (ImmutableArray.CreateRange (tokenOf keyTwo)))
            emitting (PublicKeyOrToken.Key (ImmutableArray.CreateRange keyTwo))
        ]

    let private genSource : Gen<SourceAssembly> =
        let genName : Gen<string * string> =
            Gen.zip (Gen.elements [ "" ; "N" ; "N.M" ]) (Gen.elements [ "A" ; "B" ; "C" ])

        let genType (earlier : int) : Gen<SourceType> =
            gen {
                let! ns, name = genName

                let! enclosing =
                    if earlier = 0 then
                        Gen.constant None
                    else
                        Gen.optionOf (Gen.choose (0, earlier - 1))

                return
                    {
                        // Roslyn gives a nested type no namespace, but the emitter copies whatever it has.
                        Namespace = (if enclosing.IsSome && ns = "N.M" then "" else ns)
                        Name = name
                        Enclosing = enclosing
                    }
            }

        let genTypeRef (assemblyRefs : int) (earlier : int) : Gen<SourceTypeRef> =
            gen {
                let! ns, name = genName

                let! scope =
                    Gen.frequency
                        [
                            yield 8, Gen.choose (0, assemblyRefs - 1) |> Gen.map SourceTypeRefScope.AssemblyRef
                            if earlier > 0 then
                                yield 4, Gen.choose (0, earlier - 1) |> Gen.map SourceTypeRefScope.Enclosing
                            yield 1, Gen.constant SourceTypeRefScope.Nil
                            yield 1, Gen.constant SourceTypeRefScope.Module
                            yield 1, Gen.constant SourceTypeRefScope.ModuleRef
                        ]

                return
                    {
                        Scope = scope
                        // Roslyn gives a nested type no namespace.
                        Namespace =
                            match scope with
                            | SourceTypeRefScope.Enclosing _ -> ""
                            | _ -> ns
                        Name = name
                    }
            }

        let rec genList (generate : int -> Gen<'a>) (remaining : int) (earlier : 'a list) : Gen<'a list> =
            if remaining = 0 then
                Gen.constant (List.rev earlier)
            else
                generate earlier.Length
                |> Gen.bind (fun item -> genList generate (remaining - 1) (item :: earlier))

        gen {
            let! types = Gen.choose (1, 6) |> Gen.bind (fun count -> genList genType count [])

            let! assemblyRefs =
                Gen.shuffle sourceAssemblyRefs
                |> Gen.bind (fun shuffled ->
                    Gen.choose (1, 4) |> Gen.map (fun n -> List.truncate n (List.ofArray shuffled))
                )

            let! typeRefs =
                Gen.choose (1, 6)
                |> Gen.bind (fun count -> genList (genTypeRef assemblyRefs.Length) count [])

            let! signed = genBool
            // A component of 65535, which `GetTypeRef`'s AssemblyRef stores as 0, so that the translation
            // does not find it.
            let! version = Gen.elements [ Version (1, 2, 3, 4) ; Version (1, 2, 65535, 0) ; Version (0, 0, 0, 0) ]
            let! culture = Gen.elements [ "" ; "fr" ]
            // None; `afPA_MSIL`; `afPA_AMD64` marked specified; `afRetargetable`.
            let! flags = Gen.elements [ 0 ; 0x0010 ; 0x00B0 ; 0x0100 ]

            return
                {
                    Name = "Source"
                    Version = version
                    Culture = culture
                    Flags = enum<AssemblyFlags> flags
                    PublicKey = (if signed then Some keyOne else None)
                    Types = types
                    AssemblyRefs = assemblyRefs
                    TypeRefs = typeRefs
                }
        }

    /// Tokens for a signature over `source`: mostly its TypeDefs and TypeRefs, sometimes its TypeSpec or
    /// a token tagged as no table.
    let private sourceTokens (source : SourceAssembly) : TokenGens =
        let token =
            Gen.frequency
                [
                    10, Gen.choose (2, source.Types.Length + 1) |> Gen.map (fun rid -> rid <<< 2)
                    10, Gen.choose (1, source.TypeRefs.Length) |> Gen.map (fun rid -> (rid <<< 2) ||| 1)
                    1, Gen.constant ((1 <<< 2) ||| 2)
                    1, Gen.constant ((1 <<< 2) ||| 3)
                ]

        {
            Named = token
            Modifier = token
        }

    /// What the dynamic module's scope holds before the signature is translated: its `Assembly` row,
    /// with no key or with `keyTwo`; perhaps another assembly's AssemblyRef, by nothing or by token, as
    /// `GetTypeRef` makes it; perhaps the source assembly's, which the managed caller always has made by
    /// then; and the TypeRefs `GetTypeRef` makes for some of the source's types.
    type private Seed =
        {
            EmittingKey : bool
            OtherAssemblyRef : PublicKeyOrToken option
            SourceAssemblyRef : bool
            Types : int list
        }

    let private genSeed : Gen<Seed> =
        gen {
            let! emittingKey = genBool

            let! other =
                Gen.elements
                    [
                        None
                        Some PublicKeyOrToken.Absent
                        Some (PublicKeyOrToken.Token (ImmutableArray.CreateRange (tokenOf keyOne)))
                    ]

            let! sourceRef = Gen.frequency [ 6, Gen.constant true ; 1, Gen.constant false ]
            let! types = Gen.listOf (Gen.choose (0, 5)) |> Gen.map (List.truncate 4)

            return
                {
                    EmittingKey = emittingKey
                    OtherAssemblyRef = other
                    SourceAssemblyRef = sourceRef
                    Types = types
                }
        }

    /// The TypeRef `GetTypeRef` makes for the source type `index`: a nested type's by its name alone, in
    /// the scope of its enclosing type's; any other's by its full name, in the scope of `assemblyRef`.
    let rec private getTypeRef
        (source : SourceAssembly)
        (assemblyRef : AssemblyRefRid)
        (index : int)
        (scope : EmitScope)
        : TypeRefRid * EmitScope
        =
        let t = source.Types.[index]

        match t.Enclosing with
        | Some enclosing ->
            let enclosing, scope = getTypeRef source assemblyRef enclosing scope
            EmitScopeReferences.defineTypeRefByName (ResolutionScope.TypeRef enclosing) t.Name scope
        | None ->
            let fullName =
                if t.Namespace = "" then
                    t.Name
                else
                    $"%s{t.Namespace}.%s{t.Name}"

            EmitScopeReferences.defineTypeRefByName (ResolutionScope.AssemblyRef assemblyRef) fullName scope

    /// An AssemblyRef row as the model holds it.
    type private ModelAssemblyRef =
        {
            Name : string
            Culture : string
            Version : MetadataVersion
            KeyOrToken : byte list
            Flags : AssemblyFlags
            Hash : byte list
        }

    /// A TypeRef row as the model holds it.
    type private ModelTypeRef =
        {
            Scope : ResolutionScope
            Namespace : string
            Name : string
        }

    let private modelAssemblyRefs (scope : EmitScope) : ModelAssemblyRef list =
        EmitScope.assemblyRefs scope
        |> Seq.map (fun row ->
            {
                Name = EmitScope.stringAt row.Name scope
                Culture = EmitScope.stringAt row.Culture scope
                Version = row.Version
                KeyOrToken = List.ofSeq (EmitScope.blobAt row.PublicKeyOrToken scope)
                Flags = row.Flags
                Hash = List.ofSeq (EmitScope.blobAt row.HashValue scope)
            }
        )
        |> List.ofSeq

    let private modelTypeRefs (scope : EmitScope) : ModelTypeRef list =
        EmitScope.typeRefs scope
        |> Seq.map (fun row ->
            {
                Scope = row.ResolutionScope
                Namespace = EmitScope.stringAt row.Namespace scope
                Name = EmitScope.stringAt row.Name scope
            }
        )
        |> List.ofSeq

    let private metadataVersion (version : Version) : MetadataVersion =
        {
            Major = uint16 version.Major
            Minor = uint16 version.Minor
            Build = uint16 version.Build
            Revision = uint16 version.Revision
        }

    /// `FindAssemblyRef`'s match of a row with a request's key: nothing matches only nothing, and a full
    /// key matches a token by reducing it, whichever of the two holds the key.
    let private keyMatches (row : ModelAssemblyRef) (key : PublicKeyOrToken) : bool =
        let rowIsKey = row.Flags.HasFlag AssemblyFlags.PublicKey

        match key with
        | PublicKeyOrToken.Absent -> row.KeyOrToken.IsEmpty
        | PublicKeyOrToken.Token token ->
            not row.KeyOrToken.IsEmpty
            && (if rowIsKey then tokenOf row.KeyOrToken else row.KeyOrToken) = List.ofSeq token
        | PublicKeyOrToken.Key key ->
            not row.KeyOrToken.IsEmpty
            && row.KeyOrToken = (if rowIsKey then
                                     List.ofSeq key
                                 else
                                     tokenOf (List.ofSeq key))

    /// What the model says translating `tokens`, in order, does to a scope holding `assemblyRefs` and
    /// `typeRefs`, for an emitting assembly holding `emittingKey`: the refusal for the first token it
    /// refuses, or each token's replacement and the rows afterwards, and whether some chain was found in
    /// part. A TypeDef names a chain of TypeRefs, outermost first, scoped by the AssemblyRef made from the
    /// source's `Assembly` row; a TypeRef scoped at its outermost by an AssemblyRef not naming the
    /// emitting assembly names its chain scoped by a copy of that AssemblyRef. Each AssemblyRef is found
    /// first; each chain is found from the outermost, and from the first miss the rest added.
    let private modelTranslation
        (source : SourceAssembly)
        (emittingKey : byte list option)
        (tokens : int list)
        (assemblyRefs : ModelAssemblyRef list)
        (typeRefs : ModelTypeRef list)
        : Result<int list * ModelAssemblyRef list * ModelTypeRef list * bool, SignatureTokenRefusal>
        =
        let findOrAdd
            (wanted : ModelAssemblyRef)
            (key : PublicKeyOrToken)
            (assemblyRefs : ModelAssemblyRef list)
            : int * ModelAssemblyRef list
            =
            match
                assemblyRefs
                |> List.tryFindIndex (fun row ->
                    row.Name = wanted.Name
                    && row.Culture = wanted.Culture
                    && row.Version = wanted.Version
                    && keyMatches row key
                )
            with
            | Some index -> index, assemblyRefs
            | None -> assemblyRefs.Length, assemblyRefs @ [ wanted ]

        let rec typeDefChain (index : int) : (string * string) list =
            let t = source.Types.[index]

            match t.Enclosing with
            | Some enclosing -> typeDefChain enclosing @ [ t.Namespace, t.Name ]
            | None -> [ t.Namespace, t.Name ]

        let rec typeRefChain (index : int) : SourceTypeRefScope * (string * string) list =
            let reference = source.TypeRefs.[index]

            match reference.Scope with
            | SourceTypeRefScope.Enclosing enclosing ->
                let outermost, chain = typeRefChain enclosing
                outermost, chain @ [ reference.Namespace, reference.Name ]
            | outermost -> outermost, [ reference.Namespace, reference.Name ]

        let namesEmitting (reference : SourceAssemblyRef) : bool =
            reference.Name = emittingName
            && reference.Version = Version (0, 0, 0, 0)
            && reference.Culture = ""
            && (
                match reference.Key, emittingKey with
                | PublicKeyOrToken.Absent, None -> true
                | PublicKeyOrToken.Token token, Some key -> List.ofSeq token = tokenOf key
                | _ -> false
            )

        let chainIn
            (outermost : ResolutionScope)
            (chain : (string * string) list)
            (typeRefs : ModelTypeRef list)
            : int * ModelTypeRef list * bool
            =
            let innermost, typeRefs, missed, foundAny =
                chain
                |> List.fold
                    (fun (enclosing, typeRefs : ModelTypeRef list, missed, foundAny) (ns, name) ->
                        let row =
                            {
                                Scope = enclosing
                                Namespace = ns
                                Name = name
                            }

                        match
                            (if missed then
                                 None
                             else
                                 List.tryFindIndex ((=) row) typeRefs)
                        with
                        | Some index -> ResolutionScope.TypeRef (TypeRefRid (index + 1)), typeRefs, false, true
                        | None ->
                            ResolutionScope.TypeRef (TypeRefRid (typeRefs.Length + 1)),
                            typeRefs @ [ row ],
                            true,
                            foundAny
                    )
                    (outermost, typeRefs, false, false)

            match innermost with
            | ResolutionScope.TypeRef rid -> rid.Rid, typeRefs, missed && foundAny
            | other -> failwith $"a chain ends in a TypeRef, not %O{other}"

        let rec go
            (tokens : int list)
            (replacements : int list)
            (assemblyRefs : ModelAssemblyRef list)
            (typeRefs : ModelTypeRef list)
            (partlyFound : bool)
            =
            let next (rid : int) (assemblyRefs) (typeRefs) (partly : bool) (rest : int list) =
                go rest (((rid <<< 2) ||| 1) :: replacements) assemblyRefs typeRefs (partlyFound || partly)

            match tokens with
            | [] -> Ok (List.rev replacements, assemblyRefs, typeRefs, partlyFound)
            | token :: rest ->
                match token &&& 3 with
                | 1 ->
                    let handle = MetadataTokens.TypeReferenceHandle (token >>> 2)

                    match typeRefChain ((token >>> 2) - 1) with
                    | SourceTypeRefScope.Nil, _ -> Error (SignatureTokenRefusal.TypeRefWithoutScope handle)
                    | SourceTypeRefScope.Module, _ -> Error (SignatureTokenRefusal.TypeRefOfModule handle)
                    | SourceTypeRefScope.ModuleRef, _ -> Error (SignatureTokenRefusal.TypeRefOfModuleRef handle)
                    | SourceTypeRefScope.Enclosing _, _ ->
                        failwith "unreachable: a chain's outermost has no enclosing TypeRef"
                    | SourceTypeRefScope.AssemblyRef index, chain ->
                        let reference = source.AssemblyRefs.[index]

                        if namesEmitting reference then
                            Error (SignatureTokenRefusal.TypeRefOfEmittingAssembly handle)
                        elif
                            (match reference.Key with
                             | PublicKeyOrToken.Key key -> List.ofSeq key = notAKey
                             | _ -> false)
                        then
                            Error (SignatureTokenRefusal.TypeRefOfInvalidPublicKey handle)
                        else
                            let copied : ModelAssemblyRef =
                                {
                                    Name = reference.Name
                                    Culture = reference.Culture
                                    Version = metadataVersion reference.Version
                                    KeyOrToken =
                                        match reference.Key with
                                        | PublicKeyOrToken.Absent -> []
                                        | PublicKeyOrToken.Token bytes
                                        | PublicKeyOrToken.Key bytes -> List.ofSeq bytes
                                    Flags =
                                        match reference.Key with
                                        | PublicKeyOrToken.Key _ -> reference.Flags ||| AssemblyFlags.PublicKey
                                        | _ -> reference.Flags
                                    Hash = reference.Hash
                                }

                            let assemblyRef, assemblyRefs = findOrAdd copied reference.Key assemblyRefs

                            let rid, typeRefs, partly =
                                chainIn (ResolutionScope.AssemblyRef (AssemblyRefRid (assemblyRef + 1))) chain typeRefs

                            next rid assemblyRefs typeRefs partly rest
                | 2 -> Error (SignatureTokenRefusal.TypeSpec (MetadataTokens.TypeSpecificationHandle (token >>> 2)))
                | 3 -> Error (SignatureTokenRefusal.NoTable token)
                | _ ->
                    let key =
                        match source.PublicKey with
                        | Some key -> PublicKeyOrToken.Token (ImmutableArray.CreateRange (tokenOf key))
                        | None -> PublicKeyOrToken.Absent

                    let wanted : ModelAssemblyRef =
                        {
                            Name = source.Name
                            Culture = source.Culture
                            Version = metadataVersion source.Version
                            KeyOrToken = source.PublicKey |> Option.map tokenOf |> Option.defaultValue []
                            Flags = source.Flags
                            Hash = []
                        }

                    let found =
                        assemblyRefs
                        |> List.exists (fun row ->
                            row.Name = wanted.Name
                            && row.Culture = wanted.Culture
                            && row.Version = wanted.Version
                            && keyMatches row key
                        )

                    if not found && source.PublicKey.IsNone then
                        Error (SignatureTokenRefusal.UnreferencedUnsignedAssembly source.Name)
                    else
                        let assemblyRef, assemblyRefs = findOrAdd wanted key assemblyRefs

                        let rid, typeRefs, partly =
                            chainIn
                                (ResolutionScope.AssemblyRef (AssemblyRefRid (assemblyRef + 1)))
                                (typeDefChain ((token >>> 2) - 2))
                                typeRefs

                        next rid assemblyRefs typeRefs partly rest

        go tokens [] assemblyRefs typeRefs false

    /// The dynamic module's scope `seed` describes, before a signature of `source`, which `reader` reads,
    /// is translated into it.
    let private seededScope (source : SourceAssembly) (seed : Seed) (reader : MetadataReader) : EmitScope =
        let scope = EmitScope.create emittingName Guid.Empty
        let name, scope = EmitScope.internString emittingName scope

        let key, scope =
            EmitScope.internBlob
                (if seed.EmittingKey then
                     ImmutableArray.CreateRange keyTwo
                 else
                     ImmutableArray.Empty)
                scope

        let scope =
            EmitScope.defineAssembly
                {
                    HashAlgorithm = AssemblyHashAlgorithm.Sha1
                    Version =
                        {
                            Major = 0us
                            Minor = 0us
                            Build = 0us
                            Revision = 0us
                        }
                    Flags =
                        (if seed.EmittingKey then
                             AssemblyFlags.PublicKey
                         else
                             enum<AssemblyFlags> 0)
                    PublicKey = key
                    Name = name
                    Culture = StringOffset 0
                }
                scope

        let scope =
            match seed.OtherAssemblyRef with
            | None -> scope
            | Some key ->
                EmitScopeReferences.defineAssemblyRef
                    {
                        Name = "Other"
                        Version = metadataVersion (Version (1, 0, 0, 0))
                        Culture = ""
                        PublicKeyToken =
                            match key with
                            | PublicKeyOrToken.Token token -> token
                            | _ -> ImmutableArray.Empty
                        Flags = enum<AssemblyFlags> 0
                    }
                    scope
                |> snd

        if seed.SourceAssemblyRef then
            let assemblyRef, scope =
                EmitScopeReferences.defineAssemblyRef (AssemblyRefRequest.ofDefinition reader) scope

            seed.Types
            |> List.fold
                (fun scope index -> getTypeRef source assemblyRef (index % source.Types.Length) scope |> snd)
                scope
        else
            scope

    [<Test>]
    let ``a signature is translated with each TypeDef or TypeRef token rewritten to the TypeRef chain the model finds or adds, and refused at its first other token``
        ()
        : unit
        =
        let mutable unchanged = 0
        let mutable reencoded = 0
        let mutable added = 0
        let mutable allFound = 0
        let mutable partlyFound = 0
        let mutable assemblyRefAdded = 0
        let mutable keyCopied = 0
        let mutable foundAcrossForms = 0
        let refusals = Collections.Generic.Dictionary<string, int> ()

        let property (source : SourceAssembly) (seed : Seed) (signature : MethodShape) (trailing : byte list) : unit =
            use pe = new PEReader (ImmutableArray.Create<byte> (sourceImage source))
            let reader = pe.GetMetadataReader ()

            let start = seededScope source seed reader

            let input = encodeMethod false signature
            let tokens = methodTokensOf signature

            let expected =
                modelTranslation
                    source
                    (if seed.EmittingKey then Some keyTwo else None)
                    tokens
                    (modelAssemblyRefs start)
                    (modelTypeRefs start)

            let actual =
                EmitScopeReferences.importMethodSignature reader (ImmutableArray.CreateRange (input @ trailing)) start

            match expected, actual with
            | Error refusal, SignatureImport.Refused actual ->
                let kind =
                    match refusal with
                    | SignatureTokenRefusal.TypeRefWithoutScope _ -> "TypeRefWithoutScope"
                    | SignatureTokenRefusal.TypeRefOfModule _ -> "TypeRefOfModule"
                    | SignatureTokenRefusal.TypeRefOfModuleRef _ -> "TypeRefOfModuleRef"
                    | SignatureTokenRefusal.TypeRefOfEmittingAssembly _ -> "TypeRefOfEmittingAssembly"
                    | SignatureTokenRefusal.TypeRefOfInvalidPublicKey _ -> "TypeRefOfInvalidPublicKey"
                    | SignatureTokenRefusal.TypeSpec _ -> "TypeSpec"
                    | SignatureTokenRefusal.NoTable _ -> "NoTable"
                    | SignatureTokenRefusal.UnreferencedUnsignedAssembly _ -> "UnreferencedUnsignedAssembly"

                refusals.[kind] <-
                    match refusals.TryGetValue kind with
                    | true, n -> n + 1
                    | false, _ -> 1

                actual |> shouldEqual refusal
            | Ok (replacements, assemblyRefs, typeRefs, partly), SignatureImport.Imported (written, next) ->
                let replaced, left = replaceMethodTokens replacements signature
                left |> shouldEqual []
                List.ofSeq written |> shouldEqual (encodeMethod true replaced)

                modelAssemblyRefs next |> shouldEqual assemblyRefs
                modelTypeRefs next |> shouldEqual typeRefs

                // Only TypeRefs and AssemblyRefs are added.
                Object.ReferenceEquals (EmitScope.memberRefs next, EmitScope.memberRefs start)
                |> shouldEqual true

                Object.ReferenceEquals (EmitScope.typeDefs next, EmitScope.typeDefs start)
                |> shouldEqual true

                let startAssemblyRefs = (EmitScope.assemblyRefs start).Count

                let addsNothing =
                    assemblyRefs.Length = startAssemblyRefs
                    && typeRefs.Length = (EmitScope.typeRefs start).Count

                // A scope the translation finds everything in is the scope it was given.
                Object.ReferenceEquals (next, start) |> shouldEqual addsNothing

                if tokens.IsEmpty then
                    if encodeMethod true signature = input then
                        unchanged <- unchanged + 1
                    else
                        reencoded <- reencoded + 1
                elif addsNothing then
                    allFound <- allFound + 1
                else
                    added <- added + 1

                if partly then
                    partlyFound <- partlyFound + 1

                if assemblyRefs.Length > startAssemblyRefs then
                    assemblyRefAdded <- assemblyRefAdded + 1

                if
                    assemblyRefs
                    |> List.skip startAssemblyRefs
                    |> List.exists (fun row -> row.Flags.HasFlag AssemblyFlags.PublicKey)
                then
                    keyCopied <- keyCopied + 1

                // A source reference by key and a row by token, or the reverse, that the translation used.
                let usedAcrossForms =
                    tokens
                    |> List.exists (fun token ->
                        token &&& 3 = 1
                        && (
                            match (List.item ((token >>> 2) - 1) source.TypeRefs).Scope with
                            | SourceTypeRefScope.AssemblyRef index ->
                                let reference = source.AssemblyRefs.[index]

                                assemblyRefs
                                |> List.exists (fun row ->
                                    row.Name = reference.Name
                                    && row.Culture = reference.Culture
                                    && keyMatches row reference.Key
                                    && (row.Flags.HasFlag AssemblyFlags.PublicKey
                                        <> (
                                            match reference.Key with
                                            | PublicKeyOrToken.Key _ -> true
                                            | _ -> false
                                        ))
                                    && not row.KeyOrToken.IsEmpty
                                )
                            | _ -> false
                        )
                    )

                if usedAcrossForms then
                    foundAcrossForms <- foundAcrossForms + 1
            | expected, actual -> failwith $"expected %A{expected}, but the translation gave %A{actual}"

        let arb =
            gen {
                let! source = genSource
                let! seed = genSeed
                let! withTokens = Gen.frequency [ 3, Gen.constant true ; 1, Gen.constant false ]

                let! signature =
                    Gen.sized (fun size ->
                        genMethod (if withTokens then Some (sourceTokens source) else None) (min size 12) true
                    )

                let! trailing =
                    Gen.listOf (Gen.elements [ 0x00uy ; 0x08uy ; 0x80uy ])
                    |> Gen.map (List.truncate 2)

                return source, seed, signature, trailing
            }
            |> Arb.fromGen

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 3000,
            Prop.forAll arb (fun (source, seed, signature, trailing) -> property source seed signature trailing)
        )

        unchanged |> shouldBeGreaterThan 50
        reencoded |> shouldBeGreaterThan 50
        added |> shouldBeGreaterThan 200
        allFound |> shouldBeGreaterThan 30
        partlyFound |> shouldBeGreaterThan 20
        assemblyRefAdded |> shouldBeGreaterThan 100
        keyCopied |> shouldBeGreaterThan 20
        foundAcrossForms |> shouldBeGreaterThan 20

        for kind in
            [
                "TypeRefWithoutScope"
                "TypeRefOfModule"
                "TypeRefOfModuleRef"
                "TypeRefOfEmittingAssembly"
                "TypeRefOfInvalidPublicKey"
                "TypeSpec"
                "NoTable"
                "UnreferencedUnsignedAssembly"
            ] do
            (match refusals.TryGetValue kind with
             | true, n -> n
             | false, _ -> 0)
            |> shouldBeGreaterThan 10

    [<Test>]
    let ``a field signature translates as a method signature taking its type does, with the field's calling convention shortest``
        ()
        : unit
        =
        let mutable imported = 0
        let mutable refused = 0
        let mutable overlongHeaders = 0

        let property (source : SourceAssembly) (seed : Seed) (shape : Shape) (headerWidth : int) : unit =
            use pe = new PEReader (ImmutableArray.Create<byte> (sourceImage source))
            let reader = pe.GetMetadataReader ()
            let start = seededScope source seed reader
            let fieldType = encode false shape

            // `void M(T)`, and a field of type `T` whose calling convention may be overlong.
            let asMethod =
                EmitScopeReferences.importMethodSignature
                    reader
                    (ImmutableArray.CreateRange ([ 0x00uy ; 0x01uy ; 0x01uy ] @ fieldType))
                    start

            let asField =
                EmitScopeReferences.importFieldSignature
                    reader
                    (ImmutableArray.CreateRange (overlong 0x06 headerWidth @ fieldType))
                    start

            if headerWidth > 1 then
                overlongHeaders <- overlongHeaders + 1

            match asMethod, asField with
            | SignatureImport.Refused fromMethod, SignatureImport.Refused fromField ->
                refused <- refused + 1
                fromField |> shouldEqual fromMethod
            | SignatureImport.Imported (methodBytes, methodScope), SignatureImport.Imported (fieldBytes, fieldScope) ->
                imported <- imported + 1

                List.ofSeq fieldBytes
                |> shouldEqual (0x06uy :: List.skip 3 (List.ofSeq methodBytes))

                modelAssemblyRefs fieldScope |> shouldEqual (modelAssemblyRefs methodScope)
                modelTypeRefs fieldScope |> shouldEqual (modelTypeRefs methodScope)

                Object.ReferenceEquals (fieldScope, start)
                |> shouldEqual (Object.ReferenceEquals (methodScope, start))
            | asMethod, asField -> failwith $"as a method %A{asMethod}, but as a field %A{asField}"

        let arb =
            gen {
                let! source = genSource
                let! seed = genSeed
                let! shape = Gen.sized (fun size -> genShape (Some (sourceTokens source)) (min size 12))
                let! headerWidth = Gen.elements [ 1 ; 1 ; 2 ; 4 ]
                return source, seed, shape, headerWidth
            }
            |> Arb.fromGen

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 1000,
            Prop.forAll arb (fun (source, seed, shape, headerWidth) -> property source seed shape headerWidth)
        )

        imported |> shouldBeGreaterThan 200
        refused |> shouldBeGreaterThan 100
        overlongHeaders |> shouldBeGreaterThan 200

    /// Every method and constructor `assembly` declares on a type that is neither generic nor nested in
    /// a generic type, so that the parent a dynamic module's reference names is a `TypeRef`.
    let private methodsOf (assembly : Assembly) : MethodBase list =
        [
            let flags =
                BindingFlags.DeclaredOnly
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
                ||| BindingFlags.Instance
                ||| BindingFlags.Static

            for t in assembly.GetTypes () |> Array.sortBy (fun t -> t.MetadataToken) do
                if not t.ContainsGenericParameters && not t.IsGenericType then
                    for m in t.GetMethods flags |> Array.sortBy (fun m -> m.MetadataToken) do
                        yield m :> MethodBase

                    for c in t.GetConstructors flags |> Array.sortBy (fun c -> c.MetadataToken) do
                        yield c :> MethodBase
        ]

    /// Every field `assembly` declares on a type that is neither generic nor nested in a generic type,
    /// but a literal one: reflection gives a literal field as an `MdFieldInfo`, for which the managed
    /// caller builds the signature itself rather than asking `GetMemberRefOfFieldInfo`.
    let private fieldsOf (assembly : Assembly) : FieldInfo list =
        [
            let flags =
                BindingFlags.DeclaredOnly
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
                ||| BindingFlags.Instance
                ||| BindingFlags.Static

            for t in assembly.GetTypes () |> Array.sortBy (fun t -> t.MetadataToken) do
                if not t.ContainsGenericParameters && not t.IsGenericType then
                    for f in t.GetFields flags |> Array.sortBy (fun f -> f.MetadataToken) do
                        if not f.IsLiteral then
                            yield f
        ]

    /// A nested type's name as the managed caller of `ModuleBuilder_GetTypeRef` passes it: its full
    /// name after the last `+` that is not escaped (`UnmangleTypeName`).
    let private unmangled (fullName : string) : string =
        let rec search (from : int) : int =
            let plus = fullName.LastIndexOf ('+', from)

            if plus < 0 then
                plus
            else
                let mutable slash = plus - 1

                while slash >= 0 && fullName.[slash] = '\\' do
                    slash <- slash - 1

                // An even run of backslashes before the `+` escapes nothing.
                if (plus - 1 - slash) % 2 = 0 then plus else search (slash)

        fullName.Substring (search (fullName.Length - 1) + 1)

    /// The types a method's return and parameter types name that `GetTypeMetadataToken` gives a TypeRef
    /// for: each element type, generic definition and type argument, but no type parameter and no
    /// instantiation (which would be a TypeSpec).
    let rec private namedTypes (t : Type) : Type list =
        if t.IsGenericParameter || t.IsFunctionPointer then
            []
        elif t.HasElementType then
            namedTypes (t.GetElementType ())
        elif t.IsConstructedGenericType then
            namedTypes (t.GetGenericTypeDefinition ())
            @ (t.GetGenericArguments () |> List.ofArray |> List.collect namedTypes)
        else
            [ t ]

    /// Real .NET's emitter against the Domain functions, over every `every`th method of `assemblies`,
    /// in order, then the first thousand of those again, so that some requests are found: for each, the
    /// same token and signature bytes, the declaring type's TypeRef made first as `GetTypeRef` makes it;
    /// then, for each type the method names, the same TypeRef from `GetTypeMetadataToken`; and at the
    /// end, the same TypeRefs, each naming the same type, and the same AssemblyRefs. Then the same for
    /// every `every`th field. Returns how many methods and fields there were, and how many signatures the
    /// translation changed.
    let private translationOracle (assemblies : Assembly list) (every : int) : int * int * int =
        let readers = Collections.Generic.Dictionary<string, PEReader> ()

        try
            // The image of an assembly the test host has loaded, from its framework directory.
            let readerOf (assembly : Assembly) : MetadataReader =
                let name = assembly.GetName().Name

                match readers.TryGetValue name with
                | true, reader -> reader.GetMetadataReader ()
                | false, _ ->
                    let reader =
                        new PEReader (IO.File.OpenRead (IO.Path.Combine (HostImage.frameworkDirectory, name + ".dll")))

                    readers.[name] <- reader
                    reader.GetMetadataReader ()

            let methods =
                assemblies
                |> List.collect methodsOf
                |> List.indexed
                |> List.filter (fun (i, _) -> i % every = 0)
                |> List.map snd

            let requests = methods @ List.truncate 1000 methods

            let assembly =
                AssemblyBuilder.DefineDynamicAssembly (AssemblyName "TranslationOracle", AssemblyBuilderAccess.Run)

            let dynamicModule = assembly.DefineDynamicModule "TranslationOracle"
            let mutable scope = EmitScope.create "TranslationOracle" Guid.Empty

            // What `ModuleBuilder_GetTypeRef` adds for a type, the enclosing type's first.
            let rec typeRef (t : Type) (scope : EmitScope) : TypeRefRid * EmitScope =
                if t.IsNested then
                    let enclosing, scope = typeRef t.DeclaringType scope

                    EmitScopeReferences.defineTypeRefByName
                        (ResolutionScope.TypeRef enclosing)
                        (EmitScopeReferences.unescapeTypeName (unmangled t.FullName))
                        scope
                else
                    let assemblyRef, scope =
                        EmitScopeReferences.defineAssemblyRef
                            (AssemblyRefRequest.ofDefinition (readerOf t.Assembly))
                            scope

                    EmitScopeReferences.defineTypeRefByName
                        (ResolutionScope.AssemblyRef assemblyRef)
                        (EmitScopeReferences.unescapeTypeName t.FullName)
                        scope

            let mutable named = 0

            for m in requests do
                let source = readerOf m.Module.Assembly

                let definitionSignature =
                    ImmutableArray.Create<byte> (m.Module.ResolveSignature m.MetadataToken)

                let token =
                    match m with
                    | :? ConstructorInfo as c -> dynamicModule.GetMethodMetadataToken c
                    | :? MethodInfo as mi -> dynamicModule.GetMethodMetadataToken mi
                    | _ -> failwith $"unexpected method kind %O{m}"

                token >>> 24 |> shouldEqual 0x0A

                // The managed caller makes the declaring type's TypeRef first.
                let parent, withParent = typeRef m.DeclaringType scope

                match EmitScopeReferences.importMethodSignature source definitionSignature withParent with
                | SignatureImport.Refused refusal ->
                    failwith $"%O{m.DeclaringType}::%O{m}'s signature was refused: %A{refusal}"
                | SignatureImport.Imported (signature, translated) ->
                    if signature <> definitionSignature then
                        named <- named + 1

                    dynamicModule.ResolveSignature token |> shouldEqual (Seq.toArray signature)

                    let rid, next =
                        EmitScopeReferences.defineMemberRef (MemberRefParent.TypeRef parent) m.Name signature translated

                    rid.Rid |> shouldEqual (token &&& 0xFFFFFF)
                    scope <- next

                // Each type the method names is the TypeRef `GetTypeRef` would make or find: the
                // translation's own when it scoped the type as `GetTypeRef` does, and otherwise another.
                let types =
                    match m with
                    | :? MethodInfo as mi -> mi.ReturnType :: [ for p in mi.GetParameters () -> p.ParameterType ]
                    | _ -> [ for p in m.GetParameters () -> p.ParameterType ]

                for t in List.collect namedTypes types do
                    let real = dynamicModule.GetTypeMetadataToken t
                    let rid, next = typeRef t scope
                    real |> shouldEqual (0x01000000 ||| rid.Rid)
                    scope <- next

            let fields =
                assemblies
                |> List.collect fieldsOf
                |> List.indexed
                |> List.filter (fun (i, _) -> i % every = 0)
                |> List.map snd

            for f in fields @ List.truncate 1000 fields do
                let source = readerOf f.Module.Assembly

                let definitionSignature =
                    ImmutableArray.Create<byte> (f.Module.ResolveSignature f.MetadataToken)

                let token = dynamicModule.GetFieldMetadataToken f
                token >>> 24 |> shouldEqual 0x0A

                // The managed caller makes the declaring type's TypeRef first.
                let parent, withParent = typeRef f.DeclaringType scope

                match EmitScopeReferences.importFieldSignature source definitionSignature withParent with
                | SignatureImport.Refused refusal ->
                    failwith $"%O{f.DeclaringType}::%s{f.Name}'s signature was refused: %A{refusal}"
                | SignatureImport.Imported (signature, translated) ->
                    if signature <> definitionSignature then
                        named <- named + 1

                    dynamicModule.ResolveSignature token |> shouldEqual (Seq.toArray signature)

                    let rid, next =
                        EmitScopeReferences.defineMemberRef (MemberRefParent.TypeRef parent) f.Name signature translated

                    rid.Rid |> shouldEqual (token &&& 0xFFFFFF)
                    scope <- next

                for t in namedTypes f.FieldType do
                    let real = dynamicModule.GetTypeMetadataToken t
                    let rid, next = typeRef t scope
                    real |> shouldEqual (0x01000000 ||| rid.Rid)
                    scope <- next

            let typeRefs = EmitScope.typeRefs scope

            for rid in 1 .. typeRefs.Count do
                let row = typeRefs.[rid - 1]
                let real = dynamicModule.ResolveType (0x01000000 ||| rid)
                EmitScope.stringAt row.Name scope |> shouldEqual real.Name

                EmitScope.stringAt row.Namespace scope
                |> shouldEqual (
                    if real.IsNested then
                        ""
                    else
                        // A type in no namespace has a null one.
                        Option.ofObj real.Namespace |> Option.defaultValue ""
                )

            Assert.Throws<ArgumentOutOfRangeException> (fun () ->
                dynamicModule.ResolveType (0x01000000 ||| (typeRefs.Count + 1)) |> ignore
            )
            |> ignore

            let tokenOfRow (row : AssemblyRefRow) : byte list =
                let held = EmitScope.blobAt row.PublicKeyOrToken scope

                if not held.IsEmpty && row.Flags.HasFlag AssemblyFlags.PublicKey then
                    List.ofSeq (AssemblyRefRequest.publicKeyToken held)
                else
                    List.ofSeq held

            assembly.GetReferencedAssemblies ()
            |> Array.map (fun a ->
                a.Name,
                a.Version,
                (a.GetPublicKeyToken ()
                 |> Option.ofObj
                 |> Option.map List.ofArray
                 |> Option.defaultValue [])
            )
            |> List.ofArray
            |> shouldEqual (
                EmitScope.assemblyRefs scope
                |> Seq.map (fun row ->
                    EmitScope.stringAt row.Name scope,
                    Version (
                        int row.Version.Major,
                        int row.Version.Minor,
                        int row.Version.Build,
                        int row.Version.Revision
                    ),
                    tokenOfRow row
                )
                |> List.ofSeq
            )

            methods.Length, fields.Length, named
        finally
            for reader in readers.Values do
                reader.Dispose ()

    [<Test>]
    let ``real .NET translates CoreLib's method and field signatures as Domain does, into the TypeRefs GetTypeRef gives``
        ()
        : unit
        =
        // Every fourth method, to bound the run. CoreLib names its own types by TypeDef.
        let methods, fields, named = translationOracle [ typeof<obj>.Assembly ] 4
        methods |> shouldBeGreaterThan 5000
        fields |> shouldBeGreaterThan 1000
        named |> shouldBeGreaterThan 2000

    [<Test>]
    let ``real .NET translates other framework assemblies' method and field signatures as Domain does, copying their AssemblyRefs``
        ()
        : unit
        =
        // These name CoreLib's types, and each other's, by TypeRefs through facades such as System.Runtime.
        let methods, fields, named =
            translationOracle
                [
                    typeof<Console>.Assembly
                    typeof<System.Linq.Enumerable>.Assembly
                    typeof<Collections.Generic.LinkedList<int>>.Assembly
                    typeof<Text.RegularExpressions.Regex>.Assembly
                    typeof<Uri>.Assembly
                    typeof<Net.IPAddress>.Assembly
                    typeof<Numerics.BigInteger>.Assembly
                ]
                1

        methods |> shouldBeGreaterThan 3000
        fields |> shouldBeGreaterThan 500
        named |> shouldBeGreaterThan 2000

    /// An image of an unsigned assembly `name` with an abstract class `C`, whose one method `M` takes a
    /// `N.T` of the assembly `Other`, which it names by `key`.
    let private otherKeyImage (name : string) (key : PublicKeyOrToken) : byte[] =
        let metadata = MetadataBuilder ()

        metadata.AddModule (
            0,
            metadata.GetOrAddString (name + ".dll"),
            metadata.GetOrAddGuid (Guid.NewGuid ()),
            GuidHandle (),
            GuidHandle ()
        )
        |> ignore<ModuleDefinitionHandle>

        metadata.AddAssembly (
            metadata.GetOrAddString name,
            Version (1, 0, 0, 0),
            StringHandle (),
            BlobHandle (),
            enum<AssemblyFlags> 0,
            AssemblyHashAlgorithm.None
        )
        |> ignore<AssemblyDefinitionHandle>

        let coreLib = typeof<obj>.Assembly.GetName ()

        let coreLibRef =
            metadata.AddAssemblyReference (
                metadata.GetOrAddString coreLib.Name,
                coreLib.Version,
                StringHandle (),
                metadata.GetOrAddBlob (coreLib.GetPublicKeyToken ()),
                enum<AssemblyFlags> 0,
                BlobHandle ()
            )

        let objectRef =
            metadata.AddTypeReference (
                AssemblyReferenceHandle.op_Implicit coreLibRef,
                metadata.GetOrAddString "System",
                metadata.GetOrAddString "Object"
            )

        let bytes, flags =
            match key with
            | PublicKeyOrToken.Absent -> BlobHandle (), enum<AssemblyFlags> 0
            | PublicKeyOrToken.Token token -> metadata.GetOrAddBlob token, enum<AssemblyFlags> 0
            | PublicKeyOrToken.Key key -> metadata.GetOrAddBlob key, AssemblyFlags.PublicKey

        let otherRef =
            metadata.AddAssemblyReference (
                metadata.GetOrAddString "Other",
                Version (1, 0, 0, 0),
                StringHandle (),
                bytes,
                flags,
                BlobHandle ()
            )

        let tRef =
            metadata.AddTypeReference (
                AssemblyReferenceHandle.op_Implicit otherRef,
                metadata.GetOrAddString "N",
                metadata.GetOrAddString "T"
            )

        metadata.AddTypeDefinition (
            enum<TypeAttributes> 0,
            StringHandle (),
            metadata.GetOrAddString "<Module>",
            EntityHandle (),
            MetadataTokens.FieldDefinitionHandle 1,
            MetadataTokens.MethodDefinitionHandle 1
        )
        |> ignore<TypeDefinitionHandle>

        metadata.AddTypeDefinition (
            TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Class,
            StringHandle (),
            metadata.GetOrAddString "C",
            TypeReferenceHandle.op_Implicit objectRef,
            MetadataTokens.FieldDefinitionHandle 1,
            MetadataTokens.MethodDefinitionHandle 1
        )
        |> ignore<TypeDefinitionHandle>

        // `instance void M(class [Other]N.T)`
        let signature =
            [|
                0x20uy
                0x01uy
                0x01uy
                0x12uy
                byte (
                    (MetadataTokens.GetRowNumber (TypeReferenceHandle.op_Implicit tRef : EntityHandle)
                     <<< 2)
                    ||| 1
                )
            |]

        metadata.AddMethodDefinition (
            MethodAttributes.Public
            ||| MethodAttributes.Virtual
            ||| MethodAttributes.Abstract
            ||| MethodAttributes.HideBySig
            ||| MethodAttributes.NewSlot,
            MethodImplAttributes.IL,
            metadata.GetOrAddString "M",
            metadata.GetOrAddBlob signature,
            -1,
            MetadataTokens.ParameterHandle 1
        )
        |> ignore<MethodDefinitionHandle>

        let image = BlobBuilder ()

        ManagedPEBuilder(
            PEHeaderBuilder (imageCharacteristics = Characteristics.Dll),
            MetadataRootBuilder metadata,
            BlobBuilder ()
        )
            .Serialize
            image
        |> ignore<BlobContentId>

        image.ToArray ()

    [<Test>]
    let ``real .NET copies an AssemblyRef holding a full key, and a later reference by its token finds it`` () : unit =
        let key = ImmutableArray.CreateRange keyOne

        let images =
            [
                "KeyByKey", otherKeyImage "KeyByKey" (PublicKeyOrToken.Key key)
                "KeyByToken",
                otherKeyImage "KeyByToken" (PublicKeyOrToken.Token (AssemblyRefRequest.publicKeyToken key))
                "KeyAbsent", otherKeyImage "KeyAbsent" PublicKeyOrToken.Absent
            ]

        let loaded = Runtime.Loader.AssemblyLoadContext ("OtherKeys", true)

        try
            let dynamicModule =
                AssemblyBuilder
                    .DefineDynamicAssembly(AssemblyName "OtherKeysOracle", AssemblyBuilderAccess.RunAndCollect)
                    .DefineDynamicModule
                    "OtherKeysOracle"

            let mutable scope = EmitScope.create "OtherKeysOracle" Guid.Empty

            for name, image in images do
                use pe = new PEReader (ImmutableArray.Create<byte> image)
                let reader = pe.GetMetadataReader ()

                let m =
                    loaded.LoadFromStream(new IO.MemoryStream (image)).GetType("C", true).GetMethod "M"

                let token = dynamicModule.GetMethodMetadataToken m

                // The managed caller makes the declaring type's TypeRef first.
                let assemblyRef, withParent =
                    EmitScopeReferences.defineAssemblyRef (AssemblyRefRequest.ofDefinition reader) scope

                let parent, withParent =
                    EmitScopeReferences.defineTypeRefByName (ResolutionScope.AssemblyRef assemblyRef) "C" withParent

                match
                    EmitScopeReferences.importMethodSignature
                        reader
                        (ImmutableArray.Create<byte> (m.Module.ResolveSignature m.MetadataToken))
                        withParent
                with
                | SignatureImport.Refused refusal -> failwith $"%s{name}'s M was refused: %A{refusal}"
                | SignatureImport.Imported (signature, translated) ->
                    dynamicModule.ResolveSignature token |> shouldEqual (Seq.toArray signature)

                    let rid, next =
                        EmitScopeReferences.defineMemberRef (MemberRefParent.TypeRef parent) "M" signature translated

                    rid.Rid |> shouldEqual (token &&& 0xFFFFFF)
                    scope <- next

            let expected =
                [
                    "KeyByKey", []
                    "Other", keyOne
                    "KeyByToken", []
                    "KeyAbsent", []
                    "Other", []
                ]

            dynamicModule.Assembly.GetReferencedAssemblies ()
            |> Array.map (fun a ->
                a.Name,
                (a.GetPublicKey ()
                 |> Option.ofObj
                 |> Option.map List.ofArray
                 |> Option.defaultValue [])
            )
            |> List.ofArray
            |> shouldEqual expected

            EmitScope.assemblyRefs scope
            |> Seq.map (fun row ->
                EmitScope.stringAt row.Name scope,
                (if row.Flags.HasFlag AssemblyFlags.PublicKey then
                     List.ofSeq (EmitScope.blobAt row.PublicKeyOrToken scope)
                 else
                     [])
            )
            |> List.ofSeq
            |> shouldEqual expected
        finally
            loaded.Unload ()

    [<Test>]
    let ``an empty name or signature, a signature that ends early, and one of the wrong kind are refused`` () : unit =
        let scope = EmitScope.create "Dyn" Guid.Empty
        let parent = MemberRefParent.TypeDef (TypeDefRid 1)
        let signature = ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x01uy |]

        Assert.Throws<ArgumentException> (fun () ->
            EmitScopeReferences.defineMemberRef parent "" signature scope |> ignore
        )
        |> ignore

        Assert.Throws<ArgumentException> (fun () ->
            EmitScopeReferences.defineMemberRef parent "M" ImmutableArray.Empty scope
            |> ignore
        )
        |> ignore

        Assert.Throws<ArgumentException> (fun () ->
            EmitScopeReferences.tryFindMemberRef parent "" signature scope |> ignore
        )
        |> ignore

        use source =
            new PEReader (
                ImmutableArray.Create<byte> (
                    sourceImage
                        {
                            Name = "Source"
                            Version = Version (1, 0, 0, 0)
                            Culture = ""
                            Flags = enum<AssemblyFlags> 0
                            PublicKey = None
                            Types =
                                [
                                    {
                                        Namespace = "N"
                                        Name = "A"
                                        Enclosing = None
                                    }
                                ]
                            AssemblyRefs = []
                            TypeRefs = []
                        }
                )
            )

        let refused (bytes : byte list) : unit =
            Assert.Throws<ArgumentException> (fun () ->
                EmitScopeReferences.importMethodSignature
                    (source.GetMetadataReader ())
                    (ImmutableArray.CreateRange bytes)
                    scope
                |> ignore
            )
            |> ignore

        refused []
        // One parameter, but no type for it.
        refused [ 0x20uy ; 0x01uy ; 0x01uy ]
        // A compressed integer cut short.
        refused [ 0x20uy ; 0x00uy ; 0x13uy ; 0x80uy ]
        // A field's, a local, a property's and an instantiation's signature, each of which would
        // otherwise be read whole as a method's.
        refused [ 0x06uy ; 0x00uy ; 0x08uy ]
        refused [ 0x07uy ; 0x00uy ; 0x08uy ]
        refused [ 0x08uy ; 0x00uy ; 0x08uy ]
        refused [ 0x0Auy ; 0x00uy ; 0x08uy ]
        // `VALUEARRAY`, and an element type past `ELEMENT_TYPE_MAX`.
        refused [ 0x00uy ; 0x00uy ; 0x17uy ]
        refused [ 0x00uy ; 0x00uy ; 0x22uy ]

        let refusedAsField (bytes : byte list) : unit =
            Assert.Throws<ArgumentException> (fun () ->
                EmitScopeReferences.importFieldSignature
                    (source.GetMetadataReader ())
                    (ImmutableArray.CreateRange bytes)
                    scope
                |> ignore
            )
            |> ignore

        refusedAsField []
        // A field's calling convention, but no type.
        refusedAsField [ 0x06uy ]
        // A method's, a local's, a property's and an instantiation's signature, each of which would
        // otherwise be read as a field's.
        refusedAsField [ 0x00uy ; 0x00uy ; 0x08uy ]
        refusedAsField [ 0x20uy ; 0x00uy ; 0x08uy ]
        refusedAsField [ 0x07uy ; 0x01uy ; 0x08uy ]
        refusedAsField [ 0x08uy ; 0x00uy ; 0x08uy ]
        refusedAsField [ 0x0Auy ; 0x01uy ; 0x08uy ]
        refusedAsField [ 0x06uy ; 0x17uy ]

    /// Method signatures that CoreCLR reads but did not write, each with the bytes .NET 10's emitter
    /// writes into a dynamic module for it, measured through `Module.ResolveSignature`.
    let private noncanonicalSignatures : (string * byte list * byte list) list =
        [
            "MethodParameterShortest", [ 0x30 ; 0x01 ; 0x00 ; 0x1E ; 0x00 ], [ 0x30 ; 0x01 ; 0x00 ; 0x1E ; 0x00 ]
            "MethodParameterTwoBytes", [ 0x30 ; 0x01 ; 0x00 ; 0x1E ; 0x80 ; 0x00 ], [ 0x30 ; 0x01 ; 0x00 ; 0x1E ; 0x00 ]
            "MethodParameterFourBytes",
            [ 0x30 ; 0x01 ; 0x00 ; 0x1E ; 0xC0 ; 0x00 ; 0x00 ; 0x00 ],
            [ 0x30 ; 0x01 ; 0x00 ; 0x1E ; 0x00 ]
            "TypeParameterTwoBytes", [ 0x20 ; 0x00 ; 0x13 ; 0x80 ; 0x00 ], [ 0x20 ; 0x00 ; 0x13 ; 0x00 ]
            "ParameterCountTwoBytes", [ 0x20 ; 0x80 ; 0x01 ; 0x01 ; 0x08 ], [ 0x20 ; 0x80 ; 0x01 ; 0x01 ; 0x08 ]
            "ArityTwoBytes", [ 0x30 ; 0x80 ; 0x01 ; 0x00 ; 0x01 ], [ 0x30 ; 0x80 ; 0x01 ; 0x00 ; 0x01 ]
            "ArraySizeTwoBytes",
            [ 0x20 ; 0x00 ; 0x14 ; 0x08 ; 0x01 ; 0x01 ; 0x80 ; 0x05 ; 0x00 ],
            [ 0x20 ; 0x00 ; 0x14 ; 0x08 ; 0x01 ; 0x01 ; 0x80 ; 0x05 ; 0x00 ]
            "VectorOfTypeParameterFourBytes",
            [ 0x20 ; 0x00 ; 0x1D ; 0x13 ; 0xC0 ; 0x00 ; 0x00 ; 0x00 ],
            [ 0x20 ; 0x00 ; 0x1D ; 0x13 ; 0x00 ]
            "FunctionPointerCountTwoBytes",
            [ 0x20 ; 0x01 ; 0x01 ; 0x1B ; 0x00 ; 0x80 ; 0x00 ; 0x08 ],
            [ 0x20 ; 0x01 ; 0x01 ; 0x1B ; 0x00 ; 0x80 ; 0x00 ; 0x08 ]
            "TrailingByte", [ 0x20 ; 0x00 ; 0x08 ; 0x08 ], [ 0x20 ; 0x00 ; 0x08 ]
            "PointerToTypeParameterTwoBytes",
            [ 0x20 ; 0x00 ; 0x0F ; 0x13 ; 0x80 ; 0x00 ],
            [ 0x20 ; 0x00 ; 0x0F ; 0x13 ; 0x00 ]
        ]
        |> List.map (fun (name, definition, written) -> name, List.map byte definition, List.map byte written)

    /// An image with an abstract class `C<T>` declaring one abstract method per signature, each named
    /// as the signature is.
    let private noncanonicalImage () : byte[] =
        let metadata = MetadataBuilder ()

        metadata.AddModule (
            0,
            metadata.GetOrAddString "Noncanonical.dll",
            metadata.GetOrAddGuid Guid.Empty,
            GuidHandle (),
            GuidHandle ()
        )
        |> ignore<ModuleDefinitionHandle>

        metadata.AddAssembly (
            metadata.GetOrAddString "Noncanonical",
            Version (1, 0, 0, 0),
            StringHandle (),
            BlobHandle (),
            enum<AssemblyFlags> 0,
            AssemblyHashAlgorithm.None
        )
        |> ignore<AssemblyDefinitionHandle>

        let coreLib = typeof<obj>.Assembly.GetName ()

        let coreLibRef =
            metadata.AddAssemblyReference (
                metadata.GetOrAddString coreLib.Name,
                coreLib.Version,
                StringHandle (),
                metadata.GetOrAddBlob (coreLib.GetPublicKeyToken ()),
                enum<AssemblyFlags> 0,
                BlobHandle ()
            )

        let objectRef =
            metadata.AddTypeReference (
                AssemblyReferenceHandle.op_Implicit coreLibRef,
                metadata.GetOrAddString "System",
                metadata.GetOrAddString "Object"
            )

        metadata.AddTypeDefinition (
            enum<TypeAttributes> 0,
            StringHandle (),
            metadata.GetOrAddString "<Module>",
            EntityHandle (),
            MetadataTokens.FieldDefinitionHandle 1,
            MetadataTokens.MethodDefinitionHandle 1
        )
        |> ignore<TypeDefinitionHandle>

        let c =
            metadata.AddTypeDefinition (
                TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Class,
                StringHandle (),
                metadata.GetOrAddString "C`1",
                TypeReferenceHandle.op_Implicit objectRef,
                MetadataTokens.FieldDefinitionHandle 1,
                MetadataTokens.MethodDefinitionHandle 1
            )

        let methodGenerics =
            [
                for name, definition, _ in noncanonicalSignatures do
                    let m =
                        metadata.AddMethodDefinition (
                            MethodAttributes.Public
                            ||| MethodAttributes.Virtual
                            ||| MethodAttributes.Abstract
                            ||| MethodAttributes.HideBySig
                            ||| MethodAttributes.NewSlot,
                            MethodImplAttributes.IL,
                            metadata.GetOrAddString name,
                            metadata.GetOrAddBlob (List.toArray definition),
                            -1,
                            MetadataTokens.ParameterHandle 1
                        )

                    if definition.Head &&& 0x10uy <> 0uy then
                        yield (MethodDefinitionHandle.op_Implicit m : EntityHandle)
            ]

        // The GenericParam table is sorted by its owner's TypeOrMethodDef coded index.
        let coded (owner : EntityHandle) : int =
            (MetadataTokens.GetRowNumber owner <<< 1)
            ||| (if owner.Kind = HandleKind.MethodDefinition then 1 else 0)

        for owner in
            ((TypeDefinitionHandle.op_Implicit c : EntityHandle) :: methodGenerics)
            |> List.sortBy coded do
            metadata.AddGenericParameter (owner, GenericParameterAttributes.None, metadata.GetOrAddString "T", 0)
            |> ignore<GenericParameterHandle>

        let image = BlobBuilder ()

        ManagedPEBuilder(
            PEHeaderBuilder (imageCharacteristics = Characteristics.Dll),
            MetadataRootBuilder metadata,
            BlobBuilder ()
        )
            .Serialize
            image
        |> ignore<BlobContentId>

        image.ToArray ()

    [<Test>]
    let ``real .NET imports a signature it did not write as importMethodSignature says`` () : unit =
        let image = noncanonicalImage ()
        let loaded = Runtime.Loader.AssemblyLoadContext ("Noncanonical", true)

        try
            let c = loaded.LoadFromStream(new IO.MemoryStream (image)).GetType ("C`1", true)

            let dynamicModule =
                AssemblyBuilder
                    .DefineDynamicAssembly(AssemblyName "NoncanonicalOracle", AssemblyBuilderAccess.RunAndCollect)
                    .DefineDynamicModule
                    "NoncanonicalOracle"

            use source = new PEReader (ImmutableArray.Create<byte> image)
            let scope = EmitScope.create "NoncanonicalOracle" Guid.Empty

            for name, definition, written in noncanonicalSignatures do
                let m = c.GetMethod (name, BindingFlags.Public ||| BindingFlags.Instance)

                m.Module.ResolveSignature m.MetadataToken
                |> List.ofArray
                |> shouldEqual definition

                let token = dynamicModule.GetMethodMetadataToken m
                dynamicModule.ResolveSignature token |> List.ofArray |> shouldEqual written

                match
                    EmitScopeReferences.importMethodSignature
                        (source.GetMetadataReader ())
                        (ImmutableArray.CreateRange definition)
                        scope
                with
                | SignatureImport.Imported (imported, next) ->
                    List.ofSeq imported |> shouldEqual written
                    Object.ReferenceEquals (next, scope) |> shouldEqual true
                | SignatureImport.Refused refusal -> failwith $"%s{name} was refused: %A{refusal}"
        finally
            loaded.Unload ()
