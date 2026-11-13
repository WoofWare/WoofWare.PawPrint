namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsCheck
open FsCheck.FSharp
open WoofWare.PawPrint

/// Generated emit scopes, for property tests of `EmitScope`, its writer and what holds one: each a list
/// of definitions whose references are choices resolved against the rows the scope has so far, so that
/// every list makes a valid scope, and a prefix of the list makes a scope the whole list extends.
module EmitScopeGen =

    /// A body as ECMA-335 II.25.4 lays it out: `nop`s then `ret`.
    type BodyShape =
        | Tiny of nops : int
        /// `locals` picks a `StandAloneSig` to name as the local signature, if the scope has any.
        | Fat of nops : int * maxStack : uint16 * locals : int option
        /// No body at all, for a method that is not abstract: an IL method whose RVA is 0, as a
        /// dynamic module's method is until `SetMethodIL`.
        | NoBody

    /// One definition, its references given as choices that `apply` resolves against the rows the
    /// scope has so far, so that every generated list of operations makes a valid scope.
    type Op =
        | AssemblyRef of name : string * version : uint16 * withToken : bool
        | TypeRef of scope : int * name : string * ns : string
        | TypeDef of name : string * ns : string * flags : int * extends : int option
        | Field of owner : int * name : string * flags : int * fieldType : int
        | Method of
            owner : int *
            name : string *
            flags : int *
            parameters : int list *
            returnType : int *
            body : BodyShape
        | Param of method : int * sequence : int * name : string * flags : int
        | InterfaceImpl of cls : int * iface : int
        | MemberRef of parent : int * name : string * parameters : int list option
        /// A local signature; never empty.
        | StandAloneSig of locals : int list
        | MethodImpl of cls : int * body : int * declaration : int
        /// Overwrite the implementation flags of a method already in the scope, if it has any.
        | SetMethodImplFlags of method : int * flags : int
        /// Overwrite the parent of a type already in the scope other than `<Module>`, if it has any.
        | SetTypeDefExtends of typeDef : int * extends : int
        /// Give a method already in the scope its body, over any it has, if it is not abstract and `body`
        /// is one: `SetMethodIL` sets no body for an abstract method's empty code.
        | SetMethodBody of method : int * body : BodyShape
        /// A TypeSpec: a single-dimensional array of the element type this chooses.
        | TypeSpec of element : int
        /// An instantiation of a method or method reference already in the scope, if it has any; never
        /// of no type arguments.
        | MethodSpec of method : int * arguments : int list
        /// A generic parameter of a type or method already in the scope, if it has any, numbered after its
        /// owner's others, with a constraint for each of `constraints` naming a TypeDef, TypeRef or
        /// TypeSpec of the scope. The rows are in creation order, which need not be owner order.
        | GenericParam of owner : int * name : string * flags : int * constraints : int list
        /// A property of a type already in the scope, of the element type this chooses; its owner need not
        /// own the scope's last property, as CoreCLR's tokens are in definition order across the module.
        | Property of owner : int * name : string * flags : int * propertyType : int
        /// An accessor of a property already in the scope, which may be any method of the scope, if it has
        /// both; in the order added, with no search, as `_DefineMethodSemantics` adds them.
        | MethodSemantics of property : int * semantics : int * method : int

    type private Applied =
        {
            Scope : EmitScope
            /// The parameter count of each method's signature, by RID - 1.
            ParamCounts : int list
            /// Which `(method, sequence)` pairs already have a `Param` row.
            ParamSequences : Set<int * int>
            /// The `MemberRef`s with a method signature.
            MethodRefs : MemberRefRid list
        }

    // Unpaired surrogates too, built at run time because the F# compiler replaces one in a literal.
    let genName : Gen<string> =
        Gen.elements
            [
                "a"
                "Z"
                "0"
                "_"
                " "
                "<"
                "é"
                "日"
                "\U0001F600"
                string (char 0xD800)
                string (char 0xDFFF)
            ]
        |> Gen.listOf
        |> Gen.map String.Concat

    let genNamespace : Gen<string> = Gen.elements [ "" ; "A" ; "A.B" ; "日本" ]

    let typeFlagChoices : TypeAttributes list =
        [
            TypeAttributes.Public ||| TypeAttributes.Class
            TypeAttributes.NotPublic ||| TypeAttributes.Class
            TypeAttributes.Public ||| TypeAttributes.Interface ||| TypeAttributes.Abstract
            TypeAttributes.Public
            ||| TypeAttributes.Sealed
            ||| TypeAttributes.BeforeFieldInit
        ]

    let fieldFlagChoices : FieldAttributes list =
        [
            FieldAttributes.Public
            FieldAttributes.Private ||| FieldAttributes.Static
            FieldAttributes.Assembly ||| FieldAttributes.InitOnly
        ]

    let methodFlagChoices : MethodAttributes list =
        [
            MethodAttributes.Public ||| MethodAttributes.Static
            MethodAttributes.Public ||| MethodAttributes.HideBySig
            MethodAttributes.Public
            ||| MethodAttributes.Virtual
            ||| MethodAttributes.Final
            ||| MethodAttributes.HideBySig
            ||| MethodAttributes.NewSlot
            ||| MethodAttributes.SpecialName
            MethodAttributes.Public
            ||| MethodAttributes.Virtual
            ||| MethodAttributes.Abstract
            ||| MethodAttributes.NewSlot
        ]

    let methodImplFlagChoices : MethodImplAttributes list =
        [
            MethodImplAttributes.IL
            MethodImplAttributes.NoInlining
            MethodImplAttributes.AggressiveInlining ||| MethodImplAttributes.NoOptimization
            MethodImplAttributes.Synchronized
        ]

    let paramFlagChoices : ParameterAttributes list =
        [
            ParameterAttributes.None
            ParameterAttributes.In
            ParameterAttributes.Out
            ParameterAttributes.Optional
        ]

    let genericParamFlagChoices : GenericParameterAttributes list =
        [
            GenericParameterAttributes.None
            GenericParameterAttributes.Covariant
            GenericParameterAttributes.Contravariant
            GenericParameterAttributes.Covariant
            ||| GenericParameterAttributes.Contravariant
            GenericParameterAttributes.ReferenceTypeConstraint
            ||| GenericParameterAttributes.DefaultConstructorConstraint
            GenericParameterAttributes.NotNullableValueTypeConstraint
        ]

    let propertyFlagChoices : PropertyAttributes list =
        [
            PropertyAttributes.None
            PropertyAttributes.SpecialName
            // Every bit outside `prReservedMask`.
            enum<PropertyAttributes> 0x0BFF
        ]

    let semanticsChoices : MethodSemanticsAttributes list =
        [
            MethodSemanticsAttributes.Getter
            MethodSemanticsAttributes.Setter
            MethodSemanticsAttributes.Other
        ]

    let genBody : Gen<BodyShape> =
        Gen.oneof
            [
                Gen.choose (0, 60) |> Gen.map BodyShape.Tiny
                Gen.constant BodyShape.NoBody
                gen {
                    let! nops = Gen.choose (0, 100)
                    let! maxStack = Gen.choose (0, 20) |> Gen.map uint16
                    let! locals = Gen.optionOf (Gen.choose (0, 1000))
                    return BodyShape.Fat (nops, maxStack, locals)
                }
            ]

    let choice : Gen<int> = Gen.choose (0, 1000)

    let genOp : Gen<Op> =
        Gen.frequency
            [
                1,
                gen {
                    let! name = genName
                    let! version = Gen.choose (0, 65535) |> Gen.map uint16
                    let! withToken = Gen.elements [ true ; false ]
                    return Op.AssemblyRef ($"Ref%s{name}", version, withToken)
                }
                3,
                gen {
                    let! scope = choice
                    let! name = genName
                    let! ns = genNamespace
                    return Op.TypeRef (scope, name, ns)
                }
                3,
                gen {
                    let! name = genName
                    let! ns = genNamespace
                    let! flags = choice
                    let! extends = Gen.optionOf choice
                    return Op.TypeDef (name, ns, flags, extends)
                }
                3,
                gen {
                    let! owner = choice
                    let! name = genName
                    let! flags = choice
                    let! fieldType = choice
                    return Op.Field (owner, name, flags, fieldType)
                }
                5,
                gen {
                    let! owner = choice
                    let! name = genName
                    let! flags = choice
                    let! parameters = Gen.listOfLength 3 choice |> Gen.bind Gen.subListOf
                    let! returnType = choice
                    let! body = genBody
                    return Op.Method (owner, name, flags, List.ofSeq parameters, returnType, body)
                }
                4,
                gen {
                    let! methodChoice = choice
                    let! sequence = Gen.choose (0, 3)
                    let! name = genName
                    let! flags = choice
                    return Op.Param (methodChoice, sequence, name, flags)
                }
                2, Gen.map2 (fun c i -> Op.InterfaceImpl (c, i)) choice choice
                2,
                gen {
                    let! parent = choice
                    let! name = genName
                    let! parameters = Gen.optionOf (Gen.listOfLength 2 choice |> Gen.bind Gen.subListOf)
                    return Op.MemberRef (parent, $"M%s{name}", parameters |> Option.map List.ofSeq)
                }
                // A local signature holds at least one local (ECMA-335 II.23.2.6).
                1,
                Gen.listOfLength 3 choice
                |> Gen.bind Gen.subListOf
                |> Gen.map2 (fun first rest -> Op.StandAloneSig (first :: List.ofSeq rest)) choice
                2, Gen.map3 (fun c b d -> Op.MethodImpl (c, b, d)) choice choice choice
                2, Gen.map2 (fun m f -> Op.SetMethodImplFlags (m, f)) choice choice
                2, Gen.map2 (fun t e -> Op.SetTypeDefExtends (t, e)) choice choice
                2, Gen.map2 (fun m b -> Op.SetMethodBody (m, b)) choice genBody
                2, Gen.map Op.TypeSpec choice
                2,
                Gen.listOfLength 2 choice
                |> Gen.bind Gen.subListOf
                |> Gen.map3 (fun m first rest -> Op.MethodSpec (m, first :: List.ofSeq rest)) choice choice
                3,
                gen {
                    let! owner = choice
                    let! name = genName
                    let! flags = choice
                    let! constraints = Gen.listOfLength 4 choice |> Gen.bind Gen.subListOf
                    return Op.GenericParam (owner, name, flags, List.ofSeq constraints)
                }
                2,
                gen {
                    let! owner = choice
                    let! name = genName
                    let! flags = choice
                    let! propertyType = choice
                    return Op.Property (owner, name, flags, propertyType)
                }
                3, Gen.map3 (fun p s m -> Op.MethodSemantics (p, s, m)) choice choice choice
            ]

    let pick (choice : int) (items : 'a list) : 'a option =
        match items with
        | [] -> None
        | _ -> Some (List.item (choice % List.length items) items)

    /// The types an `ELEMENT_TYPE_CLASS` can name: a TypeDef or a TypeRef, never a TypeSpec.
    let private classTargets (scope : EmitScope) : TypeDefOrRef list =
        [
            for i in 1 .. (EmitScope.typeDefs scope).Count do
                yield TypeDefOrRef.TypeDef (TypeDefRid i)
            for i in 1 .. (EmitScope.typeRefs scope).Count do
                yield TypeDefOrRef.TypeRef (TypeRefRid i)
        ]

    /// The targets a `TypeDefOrRef` column can name: a TypeDef, a TypeRef or a TypeSpec.
    let typeDefOrRefs (scope : EmitScope) : TypeDefOrRef list =
        [
            yield! classTargets scope
            for i in 1 .. (EmitScope.typeSpecs scope).Count do
                yield TypeDefOrRef.TypeSpec (EmitScope.tryTypeSpecRid i scope).Value
        ]

    let compressedUnsigned (value : int) : byte list =
        if value < 0x80 then
            [ byte value ]
        elif value < 0x4000 then
            [ byte (0x80 ||| (value >>> 8)) ; byte value ]
        else
            [
                byte (0xC0 ||| (value >>> 24))
                byte (value >>> 16)
                byte (value >>> 8)
                byte value
            ]

    /// The element type a choice names, as ECMA-335 II.23.2.12 encodes it: a primitive, or a class
    /// already in the scope.
    let elementType (scope : EmitScope) (choice : int) : byte list =
        match choice % 6 with
        | 0 -> [ 0x08uy ] // int32
        | 1 -> [ 0x0Euy ] // string
        | 2 -> [ 0x1Cuy ] // object
        | 3 -> [ 0x02uy ] // bool
        | _ ->
            match pick choice (classTargets scope) with
            | None -> [ 0x08uy ]
            | Some (TypeDefOrRef.TypeDef rid) -> 0x12uy :: compressedUnsigned ((rid.Rid <<< 2) ||| 0)
            | Some (TypeDefOrRef.TypeRef rid) -> 0x12uy :: compressedUnsigned ((rid.Rid <<< 2) ||| 1)
            | Some (TypeDefOrRef.TypeSpec _) -> failwith "unreachable: classTargets holds no TypeSpec"

    let methodSignature (scope : EmitScope) (hasThis : bool) (parameters : int list) (returnType : int) =
        [
            yield (if hasThis then 0x20uy else 0x00uy)
            yield! compressedUnsigned parameters.Length
            yield!
                if returnType % 7 = 6 then
                    [ 0x01uy ]
                else
                    elementType scope returnType
            for p in parameters do
                yield! elementType scope p
        ]
        |> ImmutableArray.CreateRange

    let body (scope : EmitScope) (shape : BodyShape) : ImmutableArray<byte> option =
        match shape with
        | BodyShape.NoBody -> None
        | BodyShape.Tiny nops ->
            [
                yield byte (((nops + 1) <<< 2) ||| 0x2)
                yield! List.replicate nops 0x00uy
                yield 0x2Auy
            ]
            |> ImmutableArray.CreateRange
            |> Some
        | BodyShape.Fat (nops, maxStack, locals) ->
            let localsToken =
                match locals with
                | Some c when (EmitScope.standAloneSigs scope).Count > 0 ->
                    0x11000000 ||| (c % (EmitScope.standAloneSigs scope).Count + 1)
                | _ -> 0

            let codeSize = nops + 1

            [
                // Fat format, three dwords of header; `CorILMethod_InitLocals` when there are locals.
                yield! BitConverter.GetBytes (uint16 (0x3003 ||| (if localsToken = 0 then 0 else 0x10)))
                yield! BitConverter.GetBytes maxStack
                yield! BitConverter.GetBytes codeSize
                yield! BitConverter.GetBytes localsToken
                yield! List.replicate nops 0x00uy
                yield 0x2Auy
            ]
            |> ImmutableArray.CreateRange
            |> Some

    /// The owners a new `GenericParam` row may have: every type and method of the scope, whatever owns
    /// the scope's earlier rows.
    let genericParamOwners (scope : EmitScope) : TypeOrMethodDef list =
        [
            for i in 1 .. (EmitScope.typeDefs scope).Count do
                yield TypeOrMethodDef.TypeDef (TypeDefRid i)
            for i in 1 .. (EmitScope.methodDefs scope).Count do
                yield TypeOrMethodDef.MethodDef (MethodDefRid i)
        ]

    /// How many `GenericParam` rows the scope gives this owner.
    let genericParamCount (owner : TypeOrMethodDef) (scope : EmitScope) : int =
        EmitScope.genericParams scope
        |> Seq.filter (fun row -> row.Owner = owner)
        |> Seq.length

    let private intern (value : string) (applied : Applied) : StringOffset * Applied =
        let offset, scope = EmitScope.internString value applied.Scope

        offset,
        { applied with
            Scope = scope
        }

    let private internBlob (value : ImmutableArray<byte>) (applied : Applied) : BlobOffset * Applied =
        let offset, scope = EmitScope.internBlob value applied.Scope

        offset,
        { applied with
            Scope = scope
        }

    let private withScope (scope : EmitScope) (applied : Applied) : Applied =
        { applied with
            Scope = scope
        }

    let private applyOp (applied : Applied) (op : Op) : Applied =
        let scope = applied.Scope
        let typeDefCount = (EmitScope.typeDefs scope).Count
        let methodCount = (EmitScope.methodDefs scope).Count

        match op with
        | Op.AssemblyRef (name, version, withToken) ->
            let name, applied = intern name applied

            let token, applied =
                internBlob
                    (if withToken then
                         ImmutableArray.Create<byte>
                             [| 0xb7uy ; 0x7auy ; 0x5cuy ; 0x56uy ; 0x19uy ; 0x34uy ; 0xe0uy ; 0x89uy |]
                     else
                         ImmutableArray.Empty)
                    applied

            let row : AssemblyRefRow =
                {
                    Version =
                        {
                            Major = version
                            Minor = 0us
                            Build = 1us
                            Revision = 2us
                        }
                    Flags = enum<AssemblyFlags> 0
                    PublicKeyOrToken = token
                    Name = name
                    Culture = StringOffset 0
                    HashValue = BlobOffset 0
                }

            withScope (EmitScope.addAssemblyRef row applied.Scope |> snd) applied
        | Op.TypeRef (scopeChoice, name, ns) ->
            let scopes =
                [
                    yield ResolutionScope.Module
                    for i in 1 .. (EmitScope.assemblyRefs scope).Count do
                        yield ResolutionScope.AssemblyRef (AssemblyRefRid i)
                    for i in 1 .. (EmitScope.typeRefs scope).Count do
                        yield ResolutionScope.TypeRef (TypeRefRid i)
                ]

            let name, applied = intern name applied
            let ns, applied = intern ns applied

            let row : TypeRefRow =
                {
                    ResolutionScope = (pick scopeChoice scopes).Value
                    Name = name
                    Namespace = ns
                }

            withScope (EmitScope.addTypeRef row applied.Scope |> snd) applied
        | Op.TypeDef (name, ns, flags, extends) ->
            // Unique, so that no two top-level types share a name, which an image may not have.
            let name, applied = intern $"%s{name}`T%d{typeDefCount + 1}" applied
            let ns, applied = intern ns applied

            let row : TypeDefRow =
                {
                    Flags = (pick flags typeFlagChoices).Value
                    Name = name
                    Namespace = ns
                    Extends = extends |> Option.bind (fun c -> pick c (typeDefOrRefs scope))
                }

            withScope (EmitScope.addTypeDef row applied.Scope |> snd) applied
        | Op.Field (owner, name, flags, fieldType) ->
            let name, applied = intern name applied

            let signature, applied =
                internBlob (ImmutableArray.CreateRange (0x06uy :: elementType scope fieldType)) applied

            let row : FieldRow =
                {
                    Owner = TypeDefRid (owner % typeDefCount + 1)
                    Flags = (pick flags fieldFlagChoices).Value
                    Name = name
                    Signature = signature
                }

            withScope (EmitScope.addField row applied.Scope |> snd) applied
        | Op.Method (owner, name, flags, parameters, returnType, shape) ->
            let flags = (pick flags methodFlagChoices).Value
            let name, applied = intern name applied

            let signature, applied =
                internBlob
                    (methodSignature scope (not (flags.HasFlag MethodAttributes.Static)) parameters returnType)
                    applied

            let row : MethodDefRow =
                {
                    Owner = TypeDefRid (owner % typeDefCount + 1)
                    Body =
                        if flags.HasFlag MethodAttributes.Abstract then
                            None
                        else
                            body scope shape
                    ImplFlags = MethodImplAttributes.IL
                    Flags = flags
                    Name = name
                    Signature = signature
                }

            { withScope (EmitScope.addMethodDef row applied.Scope |> snd) applied with
                ParamCounts = applied.ParamCounts @ [ parameters.Length ]
            }
        | Op.Param (methodChoice, sequence, name, flags) ->
            if methodCount = 0 then
                applied
            else
                let methodIndex = methodChoice % methodCount
                let sequence = sequence % (List.item methodIndex applied.ParamCounts + 1)

                if applied.ParamSequences.Contains (methodIndex, sequence) then
                    applied
                else
                    let name, applied = intern name applied

                    let row : ParamRow =
                        {
                            Owner = MethodDefRid (methodIndex + 1)
                            Flags = (pick flags paramFlagChoices).Value
                            Sequence = uint16 sequence
                            Name = name
                        }

                    { withScope (EmitScope.addParam row applied.Scope |> snd) applied with
                        ParamSequences = applied.ParamSequences.Add (methodIndex, sequence)
                    }
        | Op.InterfaceImpl (cls, iface) ->
            let row : InterfaceImplRow =
                {
                    Class = TypeDefRid (cls % typeDefCount + 1)
                    Interface = (pick iface (typeDefOrRefs scope)).Value
                }

            withScope (EmitScope.addInterfaceImpl row applied.Scope) applied
        | Op.MemberRef (parentChoice, name, parameters) ->
            let parents =
                [
                    for p in typeDefOrRefs scope do
                        match p with
                        | TypeDefOrRef.TypeDef rid -> yield MemberRefParent.TypeDef rid
                        | TypeDefOrRef.TypeRef rid -> yield MemberRefParent.TypeRef rid
                        | TypeDefOrRef.TypeSpec rid -> yield MemberRefParent.TypeSpec rid
                ]

            let name, applied = intern name applied

            let signature, applied =
                match parameters with
                | Some parameters -> internBlob (methodSignature scope true parameters 6) applied
                | None -> internBlob (ImmutableArray.Create<byte> [| 0x06uy ; 0x08uy |]) applied

            let row : MemberRefRow =
                {
                    Parent = (pick parentChoice parents).Value
                    Name = name
                    Signature = signature
                }

            let rid, scope = EmitScope.addMemberRef row applied.Scope

            { withScope scope applied with
                MethodRefs =
                    if parameters.IsSome then
                        applied.MethodRefs @ [ rid ]
                    else
                        applied.MethodRefs
            }
        | Op.StandAloneSig locals ->
            let signature, applied =
                [
                    yield 0x07uy
                    yield! compressedUnsigned locals.Length
                    for l in locals do
                        yield! elementType scope l
                ]
                |> ImmutableArray.CreateRange
                |> fun blob -> internBlob blob applied

            let row : StandAloneSigRow =
                {
                    Signature = signature
                }

            withScope (EmitScope.addStandAloneSig row applied.Scope |> snd) applied
        | Op.TypeSpec element ->
            let signature, applied =
                0x1Duy :: elementType scope element
                |> ImmutableArray.CreateRange
                |> fun blob -> internBlob blob applied

            let row : TypeSpecRow =
                {
                    Signature = signature
                }

            withScope (EmitScope.addTypeSpec row applied.Scope |> snd) applied
        | Op.MethodSpec (methodChoice, arguments) ->
            let methods =
                [
                    for i in 1..methodCount do
                        yield MethodDefOrRef.MethodDef (MethodDefRid i)
                    for r in applied.MethodRefs do
                        yield MethodDefOrRef.MemberRef r
                ]

            match pick methodChoice methods with
            | None -> applied
            | Some method ->
                let instantiation, applied =
                    [
                        yield 0x0Auy
                        yield! compressedUnsigned arguments.Length
                        for a in arguments do
                            yield! elementType scope a
                    ]
                    |> ImmutableArray.CreateRange
                    |> fun blob -> internBlob blob applied

                let row : MethodSpecRow =
                    {
                        Method = method
                        Instantiation = instantiation
                    }

                withScope (EmitScope.addMethodSpec row applied.Scope |> snd) applied
        | Op.MethodImpl (cls, bodyChoice, declaration) ->
            let methods =
                [
                    for i in 1..methodCount do
                        yield MethodDefOrRef.MethodDef (MethodDefRid i)
                    for r in applied.MethodRefs do
                        yield MethodDefOrRef.MemberRef r
                ]

            match pick bodyChoice methods, pick declaration methods with
            | Some bodyTarget, Some declarationTarget ->
                let row : MethodImplRow =
                    {
                        Class = TypeDefRid (cls % typeDefCount + 1)
                        Body = bodyTarget
                        Declaration = declarationTarget
                    }

                withScope (EmitScope.addMethodImpl row applied.Scope) applied
            | _ -> applied
        | Op.SetMethodImplFlags (methodChoice, flags) ->
            if methodCount = 0 then
                applied
            else
                let method = MethodDefRid (methodChoice % methodCount + 1)
                let flags = (pick flags methodImplFlagChoices).Value
                withScope (EmitScope.setMethodImplFlags method flags applied.Scope) applied
        | Op.SetTypeDefExtends (typeChoice, extendsChoice) ->
            // `TypeBuilder_SetParentType` is never called for the global type.
            if typeDefCount < 2 then
                applied
            else
                let typeDef = TypeDefRid (typeChoice % (typeDefCount - 1) + 2)
                let extends = (pick extendsChoice (typeDefOrRefs scope)).Value
                withScope (EmitScope.setTypeDefExtends typeDef extends applied.Scope) applied
        | Op.SetMethodBody (methodChoice, shape) ->
            if methodCount = 0 then
                applied
            else
                let method = MethodDefRid (methodChoice % methodCount + 1)

                let row = (EmitScope.methodDefs scope).[method.Rid - 1]

                match body scope shape with
                | Some filled when not (row.Flags.HasFlag MethodAttributes.Abstract) ->
                    withScope (EmitScope.setMethodBody method filled applied.Scope) applied
                | Some _
                | None -> applied
        | Op.GenericParam (ownerChoice, name, flags, constraintChoices) ->
            match pick ownerChoice (genericParamOwners scope) with
            | None -> applied
            | Some owner ->
                let name, applied = intern name applied

                let row : GenericParamRow =
                    {
                        Number = uint16 (genericParamCount owner scope)
                        Flags = (pick flags genericParamFlagChoices).Value
                        Owner = owner
                        Name = name
                    }

                // Choices may repeat a target, as CoreCLR's emitter adds a row for each token given.
                let constraints =
                    constraintChoices |> List.choose (fun c -> pick c (typeDefOrRefs applied.Scope))

                withScope (EmitScope.addGenericParam row constraints applied.Scope |> snd) applied
        | Op.Property (owner, name, flags, propertyType) ->
            let name, applied = intern name applied

            // The header `GetPropertySigHelper` writes, with `HASTHIS` for some.
            let signature, applied =
                internBlob
                    (ImmutableArray.CreateRange (
                        (if flags % 2 = 0 then 0x08uy else 0x28uy)
                        :: 0x00uy
                        :: elementType scope propertyType
                    ))
                    applied

            let row : PropertyRow =
                {
                    Owner = TypeDefRid (owner % typeDefCount + 1)
                    Flags = (pick flags propertyFlagChoices).Value
                    Name = name
                    Signature = signature
                }

            withScope (EmitScope.addProperty row applied.Scope |> snd) applied
        | Op.MethodSemantics (propertyChoice, semantics, methodChoice) ->
            let propertyCount = (EmitScope.properties scope).Count

            if propertyCount = 0 || methodCount = 0 then
                applied
            else
                let row : MethodSemanticsRow =
                    {
                        Semantics = (pick semantics semanticsChoices).Value
                        Method = MethodDefRid (methodChoice % methodCount + 1)
                        Association =
                            HasSemantics.Property
                                (EmitScope.tryPropertyRid (propertyChoice % propertyCount + 1) scope).Value
                    }

                withScope (EmitScope.addMethodSemantics row applied.Scope) applied

    let emptyScope (mvid : Guid) : EmitScope =
        let scope = EmitScope.create "RefEmit_InMemoryManifestModule" mvid
        let name, scope = EmitScope.internString "Generated" scope

        EmitScope.defineAssembly
            {
                HashAlgorithm = AssemblyHashAlgorithm.Sha1
                Version =
                    {
                        Major = 1us
                        Minor = 2us
                        Build = 3us
                        Revision = 4us
                    }
                Flags = enum<AssemblyFlags> 0
                PublicKey = BlobOffset 0
                Name = name
                Culture = StringOffset 0
            }
            scope

    let apply (mvid : Guid) (ops : Op list) : EmitScope =
        let start : Applied =
            {
                Scope = emptyScope mvid
                ParamCounts = []
                ParamSequences = Set.empty
                MethodRefs = []
            }

        (List.fold applyOp start ops).Scope

    let genGuid : Gen<Guid> =
        Gen.arrayOfLength 16 (Gen.choose (0, 255) |> Gen.map byte) |> Gen.map Guid

    let genOps : Gen<Op list> =
        Gen.sized (fun size -> Gen.choose (0, 4 * size) |> Gen.bind (fun n -> Gen.listOfLength n genOp))
        |> Gen.map List.ofSeq

    let genScope : Gen<EmitScope> = Gen.map2 apply genGuid genOps

    // `MetadataReader` over the image's metadata, kept alive by the `PEReader` that owns it.
    let readImage (image : byte[]) : PEReader * MetadataReader =
        let pe = new PEReader (ImmutableArray.Create<byte> image)
        pe, pe.GetMetadataReader ()

    let token (handle : EntityHandle) : int =
        if handle.IsNil then 0 else MetadataTokens.GetToken handle

    /// One row of a table that does not need sorting, as the image holds it: heap offsets and tokens,
    /// without the derived list columns.
    let describeRows (reader : MetadataReader) : Map<TableIndex * int, obj> =
        let heap (h : StringHandle) = MetadataTokens.GetHeapOffset h
        let blob (h : BlobHandle) = MetadataTokens.GetHeapOffset h

        [
            for i in 1 .. reader.GetTableRowCount TableIndex.AssemblyRef do
                let r = reader.GetAssemblyReference (MetadataTokens.AssemblyReferenceHandle i)

                yield
                    (TableIndex.AssemblyRef, i),
                    box (r.Version, r.Flags, blob r.PublicKeyOrToken, heap r.Name, heap r.Culture)
            for i in 1 .. reader.GetTableRowCount TableIndex.TypeRef do
                let r = reader.GetTypeReference (MetadataTokens.TypeReferenceHandle i)
                yield (TableIndex.TypeRef, i), box (token r.ResolutionScope, heap r.Name, heap r.Namespace)
            for i in 1 .. reader.GetTableRowCount TableIndex.TypeDef do
                let r = reader.GetTypeDefinition (MetadataTokens.TypeDefinitionHandle i)
                yield (TableIndex.TypeDef, i), box (r.Attributes, heap r.Name, heap r.Namespace, token r.BaseType)
            for i in 1 .. reader.GetTableRowCount TableIndex.Field do
                let r = reader.GetFieldDefinition (MetadataTokens.FieldDefinitionHandle i)

                yield
                    (TableIndex.Field, i),
                    box (
                        r.Attributes,
                        heap r.Name,
                        blob r.Signature,
                        token (TypeDefinitionHandle.op_Implicit (r.GetDeclaringType ()))
                    )
            for i in 1 .. reader.GetTableRowCount TableIndex.MethodDef do
                let r = reader.GetMethodDefinition (MetadataTokens.MethodDefinitionHandle i)

                yield
                    (TableIndex.MethodDef, i),
                    box (
                        r.Attributes,
                        r.ImplAttributes,
                        heap r.Name,
                        blob r.Signature,
                        token (TypeDefinitionHandle.op_Implicit (r.GetDeclaringType ()))
                    )
            for i in 1 .. reader.GetTableRowCount TableIndex.Param do
                let r = reader.GetParameter (MetadataTokens.ParameterHandle i)
                yield (TableIndex.Param, i), box (r.Attributes, r.SequenceNumber, heap r.Name)
            for i in 1 .. reader.GetTableRowCount TableIndex.MemberRef do
                let r = reader.GetMemberReference (MetadataTokens.MemberReferenceHandle i)
                yield (TableIndex.MemberRef, i), box (token r.Parent, heap r.Name, blob r.Signature)
            for i in 1 .. reader.GetTableRowCount TableIndex.StandAloneSig do
                let r = reader.GetStandaloneSignature (MetadataTokens.StandaloneSignatureHandle i)
                yield (TableIndex.StandAloneSig, i), box (blob r.Signature)
            for i in 1 .. reader.GetTableRowCount TableIndex.TypeSpec do
                let r = reader.GetTypeSpecification (MetadataTokens.TypeSpecificationHandle i)
                yield (TableIndex.TypeSpec, i), box (blob r.Signature)
            for i in 1 .. reader.GetTableRowCount TableIndex.MethodSpec do
                let r = reader.GetMethodSpecification (MetadataTokens.MethodSpecificationHandle i)
                yield (TableIndex.MethodSpec, i), box (token r.Method, blob r.Signature)
            for i in 1 .. reader.GetTableRowCount TableIndex.GenericParam do
                let r = reader.GetGenericParameter (MetadataTokens.GenericParameterHandle i)
                yield (TableIndex.GenericParam, i), box (token r.Parent, r.Index, r.Attributes, heap r.Name)
            for i in 1 .. reader.GetTableRowCount TableIndex.GenericParamConstraint do
                let r =
                    reader.GetGenericParameterConstraint (MetadataTokens.GenericParameterConstraintHandle i)

                yield
                    (TableIndex.GenericParamConstraint, i),
                    box (
                        MetadataTokens.GetRowNumber (GenericParameterHandle.op_Implicit r.Parameter : EntityHandle),
                        token r.Type
                    )
        ]
        |> Map.ofList

    /// `describeRows` without the columns the emitter fills in after adding a row (a method's
    /// implementation flags, a type's parent), which an extension of a scope may change while the
    /// row's type is not created.
    let withoutFilledColumns (rows : Map<TableIndex * int, obj>) : Map<TableIndex * int, obj> =
        rows
        |> Map.map (fun (table, _) row ->
            if table = TableIndex.MethodDef then
                let attributes, _, name, signature, owner =
                    unbox<MethodAttributes * MethodImplAttributes * int * int * int> row

                box (attributes, name, signature, owner)
            elif table = TableIndex.TypeDef then
                let attributes, name, ns, _ = unbox<TypeAttributes * int * int * int> row
                box (attributes, name, ns)
            else
                row
        )

    /// <summary>
    /// The `PropertyMap` rows (each parent's TypeDef RID and its list's start) and the `PropertyPtr`
    /// contents CoreCLR's read-write metadata holds after adding properties of these owners (TypeDef RIDs, in
    /// definition order), replayed one property at a time as <c>RegMeta::DefineProperty</c> and
    /// <c>CMiniMdRW::AddPropertyToPropertyMap</c> add them: a map row at an owner's first property; the
    /// property appended directly to its map's run while that map is the last and no pointer table has
    /// started (<c>AddChildRowDirectForParent</c>), and otherwise through a <c>PropertyPtr</c> at the end of
    /// its map's run (<c>AddChildRowIndirectForParent</c>). <c>None</c> when no pointer table started.
    /// </summary>
    let propertyMapReplay (owners : int list) : (int * int) list * int list option =
        // Each map's parent and its run of property RIDs, in map order.
        let mutable maps : (int * int list) list = []
        let mutable indirect = false

        owners
        |> List.iteri (fun index owner ->
            let rid = index + 1

            match maps |> List.tryFindIndex (fun (parent, _) -> parent = owner) with
            | None -> maps <- maps @ [ owner, [ rid ] ]
            | Some position ->
                if position <> maps.Length - 1 then
                    indirect <- true

                maps <-
                    maps
                    |> List.mapi (fun i (parent, run) -> if i = position then parent, run @ [ rid ] else parent, run)
        )

        let starts =
            maps
            |> List.mapFold (fun next (parent, run) -> (parent, next), next + run.Length) 1
            |> fst

        starts, (if indirect then Some (maps |> List.collect snd) else None)

    /// The raw rows of one of the image's small tables, each as its columns' values: `widths` gives each
    /// column's size in bytes, which must add up to the table's row size.
    let rawRows (pe : PEReader) (reader : MetadataReader) (table : TableIndex) (widths : int list) : int list list =
        let count = reader.GetTableRowCount table
        let rowSize = reader.GetTableRowSize table

        if count > 0 && rowSize <> List.sum widths then
            failwith $"%O{table}'s rows are %d{rowSize} bytes, not the %d{List.sum widths} the columns %A{widths} make"

        let offset = reader.GetTableMetadataOffset table
        let block = pe.GetMetadata().GetContent (offset, count * rowSize)

        [
            for row in 0 .. count - 1 do
                let mutable at = row * rowSize

                yield
                    [
                        for width in widths do
                            let value =
                                if width = 2 then
                                    int block.[at] ||| (int block.[at + 1] <<< 8)
                                else
                                    BitConverter.ToInt32 (block.AsSpan (at, 4))

                            at <- at + width
                            yield value
                    ]
        ]

    /// The image's `PropertyMap` rows, each its parent's TypeDef RID and its list's start, for an image
    /// whose tables are small enough for two-byte columns.
    let propertyMapRows (pe : PEReader) (reader : MetadataReader) : (int * int) list =
        rawRows pe reader TableIndex.PropertyMap [ 2 ; 2 ]
        |> List.map (fun row -> row.[0], row.[1])

    /// The image's `PropertyPtr` contents, or `None` when it has none.
    let propertyPtrRows (pe : PEReader) (reader : MetadataReader) : int list option =
        match rawRows pe reader TableIndex.PropertyPtr [ 2 ] with
        | [] -> None
        | rows -> Some (rows |> List.map List.head)

    /// The image's `MethodSemantics` rows in image order: each its semantics, its method's RID and its
    /// association's HasSemantics coded value.
    let methodSemanticsRows (pe : PEReader) (reader : MetadataReader) : (int * int * int) list =
        rawRows pe reader TableIndex.MethodSemantics [ 2 ; 2 ; 2 ]
        |> List.map (fun row -> row.[0], row.[1], row.[2])
