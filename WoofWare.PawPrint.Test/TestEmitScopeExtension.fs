namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.EmitScopeGen

/// `EmitScopeExtension.violations`, judged by what the two scopes' images read back: the check must
/// pass exactly the replacements under which nothing the runtime has read from the earlier image
/// changes.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeExtension =

    let private created (scope : EmitScope) (choices : int list) : ImmutableHashSet<TypeDefRid> =
        let count = (EmitScope.typeDefs scope).Count

        choices
        |> Seq.map (fun c -> TypeDefRid (c % count + 1))
        |> ImmutableHashSet.CreateRange

    let private rowNumber (handle : EntityHandle) : int = MetadataTokens.GetRowNumber handle

    /// A type's or method's generic parameters as the image holds them: each row's RID, which is its
    /// token, its columns, and its constraints, each constraint row's RID and the type it names.
    let private genericParamsView
        (reader : MetadataReader)
        (index : GenericParamOwnerIndex)
        (owner : GenericParamOwner)
        =
        let parameters = GenericParamOwnerIndex.parametersOf reader owner index

        [
            for p in parameters do
                let row = reader.GetGenericParameter p

                let constraints =
                    [
                        for c in row.GetConstraints () do
                            yield
                                rowNumber (GenericParameterConstraintHandle.op_Implicit c),
                                token (reader.GetGenericParameterConstraint c).Type
                    ]

                yield rowNumber (GenericParameterHandle.op_Implicit p), row.Index, row.Attributes, row.Name, constraints
        ]

    /// Everything a created type's `MethodTable` is built from, and what its methods run: its parent, its
    /// generic parameters, its methods with their implementation flags, parameters, generic parameters
    /// and bodies, and its fields, as handles into the image, and its interface
    /// implementations and method impls in table order; and its properties, each with its accessors in
    /// table order. The interface, method impl and accessor tables are sorted, so their RIDs move as rows
    /// of lower classes or properties are added; the runtime keeps no handle into them, only what each
    /// row names.
    let private typeView (pe : PEReader) (reader : MetadataReader) (typeDef : TypeDefRid) =
        let handle = MetadataTokens.TypeDefinitionHandle typeDef.Rid
        let definition = reader.GetTypeDefinition handle

        let genericParamOwners =
            match GenericParamOwnerIndex.ofReader reader with
            | Ok index -> index
            | Error error -> failwith $"the image's generic parameters are misnumbered: %A{error}"

        let methods =
            [
                for m in definition.GetMethods () do
                    let parameters =
                        [
                            for p in (reader.GetMethodDefinition m).GetParameters () do
                                yield rowNumber (ParameterHandle.op_Implicit p)
                        ]

                    // A body moves as bodies before it are filled in, so it is compared by content.
                    let body =
                        match (reader.GetMethodDefinition m).RelativeVirtualAddress with
                        | 0 -> None
                        | rva ->
                            let block = System.Reflection.Metadata.PEReaderExtensions.GetMethodBody (pe, rva)

                            Some (
                                List.ofArray (block.GetILBytes ()),
                                block.MaxStack,
                                block.LocalVariablesInitialized,
                                token (StandaloneSignatureHandle.op_Implicit block.LocalSignature)
                            )

                    yield
                        rowNumber (MethodDefinitionHandle.op_Implicit m),
                        (reader.GetMethodDefinition m).ImplAttributes,
                        parameters,
                        genericParamsView reader genericParamOwners (GenericParamOwner.MethodDef m),
                        body
            ]

        let fields =
            [
                for f in definition.GetFields () do
                    yield rowNumber (FieldDefinitionHandle.op_Implicit f)
            ]

        let interfaces =
            [
                for i in definition.GetInterfaceImplementations () do
                    yield token (reader.GetInterfaceImplementation i).Interface
            ]

        let impls =
            [
                for i in definition.GetMethodImplementations () do
                    let row = reader.GetMethodImplementation i
                    yield token row.MethodBody, token row.MethodDeclaration
            ]

        let semantics = methodSemanticsRows pe reader

        let properties =
            [
                for p in definition.GetProperties () do
                    let rid = rowNumber (PropertyDefinitionHandle.op_Implicit p)
                    let association = (rid <<< 1) ||| 1

                    yield
                        rid,
                        semantics
                        |> List.filter (fun (_, _, a) -> a = association)
                        |> List.map (fun (s, m, _) -> s, m)
            ]

        token definition.BaseType,
        genericParamsView reader genericParamOwners (GenericParamOwner.TypeDef handle),
        methods,
        fields,
        interfaces,
        impls,
        properties

    let private createdTypeNamed (violation : EmitScopeExtensionViolation) : TypeDefRid option =
        match violation with
        | EmitScopeExtensionViolation.AddedToCreatedType (_, _, createdType)
        | EmitScopeExtensionViolation.FilledInCreatedType (_, _, createdType) -> Some createdType
        | EmitScopeExtensionViolation.RowChanged _
        | EmitScopeExtensionViolation.HeapEntryChanged _ -> None

    [<Test>]
    let ``a scope extends itself`` () : unit =
        let property (scope : EmitScope) (choices : int list) : unit =
            EmitScopeExtension.violations (created scope choices) scope scope
            |> shouldEqual []

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 100,
            Prop.forAll
                (Arb.fromGen (Gen.zip genScope (Gen.listOf choice |> Gen.map List.ofSeq)))
                (fun (scope, choices) -> property scope choices)
        )

    /// What one extension of a scope did to its created types.
    [<RequireQualifiedAccess>]
    type private ExtensionOutcome =
        /// The extension changed no created type.
        | Unchanged
        /// The extension changed some created type.
        | Changed
        /// A violation: a row added to a created type.
        | Added
        /// A violation: a column of a created type's row filled in.
        | Filled

    [<Test>]
    let ``appending to a scope is refused exactly for the created types it changes`` () : unit =
        let property
            (cover : ExtensionOutcome -> unit)
            (mvid : Guid)
            (ops : Op list)
            (extra : Op list)
            (choices : int list)
            : unit
            =
            let before = apply mvid ops
            let after = apply mvid (ops @ extra)
            let created = created before choices
            let violations = EmitScopeExtension.violations created before after

            // `after` appends to `before`, or fills in a column of one of its rows, so every row and
            // heap entry of `before` is still there.
            violations |> List.filter (createdTypeNamed >> Option.isNone) |> shouldEqual []

            let beforePe, beforeReader = readImage (EmitScopeImage.write before)
            use _ = beforePe
            let afterPe, afterReader = readImage (EmitScopeImage.write after)
            use _ = afterPe

            let changedTypes =
                created
                |> Seq.filter (fun t -> typeView beforePe beforeReader t <> typeView afterPe afterReader t)
                |> Seq.map (fun t -> t.Rid)
                |> Set.ofSeq

            violations
            |> List.choose createdTypeNamed
            |> List.map (fun t -> t.Rid)
            |> Set.ofList
            |> shouldEqual changedTypes

            if changedTypes.IsEmpty then
                cover ExtensionOutcome.Unchanged
            else
                cover ExtensionOutcome.Changed

            for violation in violations do
                match violation with
                | EmitScopeExtensionViolation.AddedToCreatedType _ -> cover ExtensionOutcome.Added
                | EmitScopeExtensionViolation.FilledInCreatedType _ -> cover ExtensionOutcome.Filled
                | EmitScopeExtensionViolation.RowChanged _
                | EmitScopeExtensionViolation.HeapEntryChanged _ -> ()

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 300)
                (Arb.fromGen (
                    gen {
                        let! mvid = genGuid
                        let! ops = genOps
                        let! extra = genOps
                        // A few created types, so that many extensions change none of them.
                        let! choices = Gen.choose (0, 3) |> Gen.bind (fun n -> Gen.listOfLength n choice)
                        return mvid, ops, extra, List.ofSeq choices
                    }
                ))
                (fun cover (mvid, ops, extra, choices) -> property cover mvid ops extra choices)

        // Both answers, and every reason for refusing, must be common enough to be tested.
        coverage.Count ExtensionOutcome.Unchanged |> shouldBeGreaterThan 30
        coverage.Count ExtensionOutcome.Changed |> shouldBeGreaterThan 30
        coverage.Count ExtensionOutcome.Added |> shouldBeGreaterThan 30
        coverage.Count ExtensionOutcome.Filled |> shouldBeGreaterThan 10

    /// Rows appended for `<Module>`, the lowest TypeDef, sort before every other type's rows in
    /// `InterfaceImpl` and `MethodImpl`, so they move every created type's rows of those tables in the
    /// image while changing nothing about the type.
    [<Test>]
    let ``a created type's rows of a sorted table may move`` () : unit =
        // `cover` is called for a sample that moves a created type's rows.
        let property (cover : unit -> unit) (mvid : Guid) (ops : Op list) (choices : int list) : unit =
            let before = apply mvid ops
            let created = (created before choices).Remove (TypeDefRid 1)

            let after =
                (before, created)
                ||> Seq.fold (fun scope createdType ->
                    let scope =
                        EmitScope.addInterfaceImpl
                            {
                                Class = TypeDefRid 1
                                Interface = TypeDefOrRef.TypeDef createdType
                            }
                            scope

                    if (EmitScope.methodDefs scope).Count = 0 then
                        scope
                    else
                        EmitScope.addMethodImpl
                            {
                                Class = TypeDefRid 1
                                Body = MethodDefOrRef.MethodDef (MethodDefRid 1)
                                Declaration = MethodDefOrRef.MethodDef (MethodDefRid 1)
                            }
                            scope
                )

            EmitScopeExtension.violations created before after |> shouldEqual []

            let beforePe, beforeReader = readImage (EmitScopeImage.write before)
            use _ = beforePe
            let afterPe, afterReader = readImage (EmitScopeImage.write after)
            use _ = afterPe

            for t in created do
                typeView afterPe afterReader t |> shouldEqual (typeView beforePe beforeReader t)

            let movedIn
                (classes : TypeDefRid seq)
                (beforeRids : ImmutableArray<int>)
                (afterRids : ImmutableArray<int>)
                =
                Seq.zip3 classes beforeRids afterRids
                |> Seq.exists (fun (cls, b, a) -> created.Contains cls && b <> a)

            if
                movedIn
                    (EmitScope.interfaceImpls before |> Seq.map (fun row -> row.Class))
                    (EmitScopeImage.interfaceImplRids before)
                    (EmitScopeImage.interfaceImplRids after)
                || movedIn
                    (EmitScope.methodImpls before |> Seq.map (fun row -> row.Class))
                    (EmitScopeImage.methodImplRids before)
                    (EmitScopeImage.methodImplRids after)
            then
                cover ()

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 300)
                (Arb.fromGen (
                    gen {
                        let! mvid = genGuid
                        let! ops = genOps
                        let! choices = Gen.choose (1, 3) |> Gen.bind (fun n -> Gen.listOfLength n choice)
                        return mvid, ops, List.ofSeq choices
                    }
                ))
                (fun cover (mvid, ops, choices) -> property cover mvid ops choices)

        // Most samples must move a created type's rows, or the property barely tests the admission.
        coverage.Count () |> shouldBeGreaterThan 100

    /// What reading `after`'s image where `before`'s was read shows to have changed, each as a
    /// requirement on the violations reported: a category, for counting, and whether the violations
    /// meet it.
    let private differencesSeen
        (before : EmitScope)
        (after : EmitScope)
        : (string * (EmitScopeExtensionViolation list -> bool)) list
        =
        let beforePe, beforeReader = readImage (EmitScopeImage.write before)
        use _ = beforePe
        let afterPe, afterReader = readImage (EmitScopeImage.write after)
        use _ = afterPe

        let has (violation : EmitScopeExtensionViolation) (violations : EmitScopeExtensionViolation list) =
            List.contains violation violations

        // A heap entry reads back differently only if it, or an entry interned before it, changed.
        let heapEntryAtOrBefore (heap : HeapIndex) (entry : int) (violations : EmitScopeExtensionViolation list) =
            violations
            |> List.exists (fun v ->
                match v with
                | EmitScopeExtensionViolation.HeapEntryChanged (h, e) -> h = heap && e <= entry
                | _ -> false
            )

        let reads (f : unit -> 'a) : 'a option =
            try
                Some (f ())
            with _ ->
                None

        [
            // A row reads back differently only if the scope's row at that RID changed. Nothing is
            // created, so a column the emitter fills in may differ.
            let afterRows = withoutFilledColumns (describeRows afterReader)

            for KeyValue ((table, rid), row) in withoutFilledColumns (describeRows beforeReader) do
                if afterRows.TryFind (table, rid) <> Some row then
                    yield "row", has (EmitScopeExtensionViolation.RowChanged (table, rid))

            for entry, value in Seq.indexed (EmitScope.strings before) do
                let offset = EmitScope.internString value before |> fst

                if
                    reads (fun () -> afterReader.GetString (MetadataTokens.StringHandle offset.Offset))
                    <> Some value
                then
                    yield "string", heapEntryAtOrBefore HeapIndex.String (entry + 1)

            for entry, value in Seq.indexed (EmitScope.blobs before) do
                let offset = EmitScope.internBlob value before |> fst

                let read =
                    reads (fun () ->
                        afterReader.GetBlobBytes (MetadataTokens.BlobHandle offset.Offset)
                        |> List.ofArray
                    )

                if read <> Some (List.ofSeq value) then
                    yield "blob", heapEntryAtOrBefore HeapIndex.Blob (entry + 1)

            for entry, value in Seq.indexed (EmitScope.guids before) do
                if
                    reads (fun () -> afterReader.GetGuid (MetadataTokens.GuidHandle (entry + 1)))
                    <> Some value
                then
                    yield "guid", has (EmitScopeExtensionViolation.HeapEntryChanged (HeapIndex.Guid, entry + 1))

            let assemblyRow (reader : MetadataReader) =
                let a = reader.GetAssemblyDefinition ()

                a.Version,
                a.Flags,
                a.HashAlgorithm,
                MetadataTokens.GetHeapOffset a.PublicKey,
                MetadataTokens.GetHeapOffset a.Name,
                MetadataTokens.GetHeapOffset a.Culture

            if assemblyRow beforeReader <> assemblyRow afterReader then
                yield "assembly", has (EmitScopeExtensionViolation.RowChanged (TableIndex.Assembly, 1))

            // A class's rows in a sorted table, in image order, gain rows only at the end, unless the
            // scope's rows of that table changed.
            let rowOfTable (table : TableIndex) (violations : EmitScopeExtensionViolation list) =
                violations
                |> List.exists (fun v ->
                    match v with
                    | EmitScopeExtensionViolation.RowChanged (t, _) -> t = table
                    | _ -> false
                )

            let perClass (reader : MetadataReader) (t : int) =
                let definition = reader.GetTypeDefinition (MetadataTokens.TypeDefinitionHandle t)

                let interfaces =
                    [
                        for i in definition.GetInterfaceImplementations () do
                            yield token (reader.GetInterfaceImplementation i).Interface
                    ]

                let impls =
                    [
                        for i in definition.GetMethodImplementations () do
                            let row = reader.GetMethodImplementation i
                            yield token row.MethodBody, token row.MethodDeclaration
                    ]

                interfaces, impls

            for t in 1 .. min (EmitScope.typeDefs before).Count (EmitScope.typeDefs after).Count do
                let beforeInterfaces, beforeImpls = perClass beforeReader t
                let afterInterfaces, afterImpls = perClass afterReader t

                if List.truncate beforeInterfaces.Length afterInterfaces <> beforeInterfaces then
                    yield "sorted", rowOfTable TableIndex.InterfaceImpl

                if List.truncate beforeImpls.Length afterImpls <> beforeImpls then
                    yield "sorted", rowOfTable TableIndex.MethodImpl
        ]

    [<Test>]
    let ``rewriting a scope's history is refused for everything its image would read back differently`` () : unit =
        let seen = Collections.Generic.Dictionary<string, int> ()

        let property
            (mvid : Guid)
            (otherMvid : Guid option)
            (ops : Op list)
            (index : int)
            (replacement : Op)
            (suffix : int)
            : unit
            =
            if not ops.IsEmpty then
                let i = index % ops.Length
                let before = apply mvid ops
                // The rewritten history, perhaps cut short, and perhaps of another module.
                let rewritten =
                    List.updateAt i replacement ops |> List.truncate (i + 1 + suffix % ops.Length)

                let after = apply (Option.defaultValue mvid otherMvid) rewritten

                let violations = EmitScopeExtension.violations ImmutableHashSet.Empty before after

                for category, met in differencesSeen before after do
                    seen.[category] <-
                        (match seen.TryGetValue category with
                         | true, n -> n
                         | false, _ -> 0)
                        + 1

                    if not (met violations) then
                        failwith $"a %s{category} difference is not among the violations %A{violations}"

                // Nothing is created, so nothing can be added to or filled in a created type.
                violations |> List.choose createdTypeNamed |> shouldEqual []

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            Prop.forAll
                (Arb.fromGen (
                    gen {
                        let! mvid = genGuid
                        let! otherMvid = Gen.frequency [ 3, Gen.constant None ; 1, Gen.map Some genGuid ]
                        let! ops = genOps
                        let! index = choice
                        let! replacement = genOp
                        let! suffix = choice
                        return mvid, otherMvid, ops, index, replacement, suffix
                    }
                ))
                (fun (mvid, otherMvid, ops, index, replacement, suffix) ->
                    property mvid otherMvid ops index replacement suffix
                )
        )

        for category in [ "row" ; "string" ; "blob" ; "guid" ; "sorted" ] do
            let count =
                match seen.TryGetValue category with
                | true, n -> n
                | false, _ -> 0

            if count < 20 then
                failwith $"only %d{count} %s{category} differences were generated; the property barely tests them"

    /// The one way two modules' scopes differ in the `Module` row alone: the empty name is at offset 0,
    /// and `<Module>`, which every scope interns next, is then at the offset the other name has.
    [<Test>]
    let ``a scope of another module is refused`` () : unit =
        let before = EmitScope.create "" Guid.Empty
        let after = EmitScope.create "<Module>" Guid.Empty

        EmitScope.strings after
        |> List.ofSeq
        |> shouldEqual (List.ofSeq (EmitScope.strings before))

        EmitScopeExtension.violations ImmutableHashSet.Empty before after
        |> shouldEqual [ EmitScopeExtensionViolation.RowChanged (TableIndex.Module, 1) ]

    [<Test>]
    let ``a changed row is reported at its RID`` () : unit =
        let scope = apply Guid.Empty []
        let first, scope = EmitScope.internString "First" scope
        let second, scope = EmitScope.internString "Second" scope

        let typeDef (name : StringOffset) : TypeDefRow =
            {
                Flags = Reflection.TypeAttributes.Public
                Name = name
                Namespace = StringOffset 0
                Extends = None
            }

        let before = EmitScope.addTypeDef (typeDef first) scope |> snd
        let after = EmitScope.addTypeDef (typeDef second) scope |> snd

        EmitScopeExtension.violations ImmutableHashSet.Empty before after
        |> shouldEqual [ EmitScopeExtensionViolation.RowChanged (TableIndex.TypeDef, 2) ]

        EmitScopeExtension.violations ImmutableHashSet.Empty before scope
        |> shouldEqual [ EmitScopeExtensionViolation.RowChanged (TableIndex.TypeDef, 2) ]

    [<Test>]
    let ``a changed method body is reported only for a created type`` () : unit =
        let scope = apply Guid.Empty []
        let name, scope = EmitScope.internString "M" scope

        let signature, scope =
            EmitScope.internBlob (ImmutableArray.Create<byte> [| 0uy ; 0uy ; 1uy |]) scope

        let method (code : byte) : MethodDefRow =
            {
                Owner = TypeDefRid 1
                Body = Some (ImmutableArray.Create<byte> [| 0x06uy ; code |])
                ImplFlags = Reflection.MethodImplAttributes.IL
                Flags = Reflection.MethodAttributes.Public ||| Reflection.MethodAttributes.Static
                Name = name
                Signature = signature
            }

        let before = EmitScope.addMethodDef (method 0x2Auy) scope |> snd
        let same = EmitScope.addMethodDef (method 0x2Auy) scope |> snd
        let after = EmitScope.addMethodDef (method 0x00uy) scope |> snd

        EmitScopeExtension.violations ImmutableHashSet.Empty before same
        |> shouldEqual []

        // `SetMethodIL` gives a method its body again when `CreateType` is retried, so a different body
        // is a fill, which only a created type refuses.
        EmitScopeExtension.violations ImmutableHashSet.Empty before after
        |> shouldEqual []

        EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 1)) before after
        |> shouldEqual
            [
                EmitScopeExtensionViolation.FilledInCreatedType (TableIndex.MethodDef, 1, TypeDefRid 1)
            ]

    [<Test>]
    let ``only a method's implementation flags may be filled in, and only while its type is not created`` () : unit =
        let scope = apply Guid.Empty []
        let name, scope = EmitScope.internString "M" scope

        let signature, scope =
            EmitScope.internBlob (ImmutableArray.Create<byte> [| 0uy ; 0uy ; 1uy |]) scope

        let method (flags : Reflection.MethodAttributes) : MethodDefRow =
            {
                Owner = TypeDefRid 1
                Body = None
                ImplFlags = Reflection.MethodImplAttributes.IL
                Flags = flags
                Name = name
                Signature = signature
            }

        let publicStatic =
            Reflection.MethodAttributes.Public ||| Reflection.MethodAttributes.Static

        let privateStatic =
            Reflection.MethodAttributes.Private ||| Reflection.MethodAttributes.Static

        let before = EmitScope.addMethodDef (method publicStatic) scope |> snd

        let fill (scope : EmitScope) =
            EmitScope.setMethodImplFlags (MethodDefRid 1) Reflection.MethodImplAttributes.NoInlining scope

        let filled = fill before
        let reflagged = EmitScope.addMethodDef (method privateStatic) scope |> snd
        let both = fill reflagged
        let created = ImmutableHashSet.Create (TypeDefRid 1)

        EmitScopeExtension.violations ImmutableHashSet.Empty before filled
        |> shouldEqual []

        EmitScopeExtension.violations created before filled
        |> shouldEqual
            [
                EmitScopeExtensionViolation.FilledInCreatedType (TableIndex.MethodDef, 1, TypeDefRid 1)
            ]

        // Any other column is fixed once the row is added, filled in alongside or not.
        for after in [ reflagged ; both ] do
            for createdTypes in [ ImmutableHashSet.Empty ; created ] do
                EmitScopeExtension.violations createdTypes before after
                |> shouldEqual [ EmitScopeExtensionViolation.RowChanged (TableIndex.MethodDef, 1) ]

    [<Test>]
    let ``only a type's parent may be filled in, and only while it is not created`` () : unit =
        let scope = apply Guid.Empty []
        let first, scope = EmitScope.internString "First" scope
        let second, scope = EmitScope.internString "Second" scope

        let typeDef (name : StringOffset) (flags : Reflection.TypeAttributes) : TypeDefRow =
            {
                Flags = flags
                Name = name
                Namespace = StringOffset 0
                Extends = None
            }

        let firstType, scope =
            EmitScope.addTypeDef (typeDef first Reflection.TypeAttributes.Public) scope

        let before, secondType =
            let secondType, scope =
                EmitScope.addTypeDef (typeDef second Reflection.TypeAttributes.Public) scope

            scope, secondType

        let filled =
            EmitScope.setTypeDefExtends secondType (TypeDefOrRef.TypeDef firstType) before

        let reflagged =
            EmitScope.addTypeDef (typeDef second Reflection.TypeAttributes.Sealed) scope
            |> snd

        let both =
            EmitScope.setTypeDefExtends secondType (TypeDefOrRef.TypeDef firstType) reflagged

        let created = ImmutableHashSet.Create secondType

        EmitScopeExtension.violations ImmutableHashSet.Empty before filled
        |> shouldEqual []

        // Another type's being created does not fix this one's parent.
        EmitScopeExtension.violations (ImmutableHashSet.Create firstType) before filled
        |> shouldEqual []

        EmitScopeExtension.violations created before filled
        |> shouldEqual
            [
                EmitScopeExtensionViolation.FilledInCreatedType (TableIndex.TypeDef, secondType.Rid, secondType)
            ]

        // Any other column is fixed once the row is added, filled in alongside or not.
        for after in [ reflagged ; both ] do
            for createdTypes in [ ImmutableHashSet.Empty ; created ] do
                EmitScopeExtension.violations createdTypes before after
                |> shouldEqual [ EmitScopeExtensionViolation.RowChanged (TableIndex.TypeDef, secondType.Rid) ]

    [<Test>]
    let ``a method's body may be filled in, and only while its type is not created`` () : unit =
        let scope = apply Guid.Empty []
        let name, scope = EmitScope.internString "M" scope

        let signature, scope =
            EmitScope.internBlob (ImmutableArray.Create<byte> [| 0uy ; 0uy ; 1uy |]) scope

        let before =
            EmitScope.addMethodDef
                {
                    Owner = TypeDefRid 1
                    Body = None
                    ImplFlags = Reflection.MethodImplAttributes.IL
                    Flags = Reflection.MethodAttributes.Public ||| Reflection.MethodAttributes.Static
                    Name = name
                    Signature = signature
                }
                scope
            |> snd

        let body (code : byte) =
            ImmutableArray.Create<byte> [| 0x06uy ; code |]

        let filled = EmitScope.setMethodBody (MethodDefRid 1) (body 0x2Auy) before
        let created = ImmutableHashSet.Create (TypeDefRid 1)

        EmitScopeExtension.violations ImmutableHashSet.Empty before filled
        |> shouldEqual []

        EmitScopeExtension.violations created before filled
        |> shouldEqual
            [
                EmitScopeExtensionViolation.FilledInCreatedType (TableIndex.MethodDef, 1, TypeDefRid 1)
            ]

        // A `CreateType` retried after failing partway gives a method its body again.
        let refilled = EmitScope.setMethodBody (MethodDefRid 1) (body 0x00uy) filled

        EmitScopeExtension.violations ImmutableHashSet.Empty filled refilled
        |> shouldEqual []

        EmitScopeExtension.violations created filled refilled
        |> shouldEqual
            [
                EmitScopeExtensionViolation.FilledInCreatedType (TableIndex.MethodDef, 1, TypeDefRid 1)
            ]

    /// A scope with a type, `Owner` (TypeDef 2, coded index 4), two methods of it (MethodDef 1 and 2,
    /// coded indexes 3 and 5), and one generic parameter of the type with these flags; and a second
    /// parameter of the type, and a first of MethodDef 2, either of which the scope admits next.
    let private withGenericParam
        (flags : Reflection.GenericParameterAttributes)
        (constraints : TypeDefOrRef list)
        : EmitScope * GenericParamRow * GenericParamRow
        =
        let scope = apply Guid.Empty []
        let name, scope = EmitScope.internString "Owner" scope

        let owner, scope =
            EmitScope.addTypeDef
                {
                    Flags = Reflection.TypeAttributes.Public
                    Name = name
                    Namespace = StringOffset 0
                    Extends = None
                }
                scope

        let signature, scope =
            EmitScope.internBlob (ImmutableArray.Create<byte> [| 0uy ; 0uy ; 1uy |]) scope

        let method : MethodDefRow =
            {
                Owner = owner
                Body = None
                ImplFlags = Reflection.MethodImplAttributes.IL
                Flags = Reflection.MethodAttributes.Public ||| Reflection.MethodAttributes.Static
                Name = name
                Signature = signature
            }

        let _, scope = EmitScope.addMethodDef method scope
        let second, scope = EmitScope.addMethodDef method scope

        let parameter (owner : TypeOrMethodDef) (number : uint16) : GenericParamRow =
            {
                Number = number
                Flags = Reflection.GenericParameterAttributes.None
                Owner = owner
                Name = name
            }

        let scope =
            EmitScope.addGenericParam
                { parameter (TypeOrMethodDef.TypeDef owner) 0us with
                    Flags = flags
                }
                constraints
                scope
            |> snd

        scope, parameter (TypeOrMethodDef.MethodDef second) 0us, parameter (TypeOrMethodDef.TypeDef owner) 1us

    [<Test>]
    let ``a generic parameter added to a created type or one of its methods is refused`` () : unit =
        let before, ofMethod, ofType =
            withGenericParam Reflection.GenericParameterAttributes.None []

        let owner = TypeDefRid 2
        let created = ImmutableHashSet.Create owner
        let toMethod = EmitScope.addGenericParam ofMethod [] before |> snd
        let toType = EmitScope.addGenericParam ofType [] before |> snd

        EmitScopeExtension.violations ImmutableHashSet.Empty before toType
        |> shouldEqual []

        EmitScopeExtension.violations ImmutableHashSet.Empty before toMethod
        |> shouldEqual []

        // Another type's being created does not fix this one's parameters.
        EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 1)) before toType
        |> shouldEqual []

        EmitScopeExtension.violations created before toType
        |> shouldEqual
            [
                EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.GenericParam, 2, owner)
            ]

        // A method's parameter belongs to the method's declaring type.
        EmitScopeExtension.violations created before toMethod
        |> shouldEqual
            [
                EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.GenericParam, 2, owner)
            ]

    [<Test>]
    let ``a generic parameter added out of owner order is an extension unless its type is created`` () : unit =
        // MethodDef 1 (coded index 3) sorts before `Owner` (TypeDef 2, coded index 4), which owns the
        // scope's only row, so the new row is out of owner order, at RID 2 as CoreCLR numbers it.
        let before, ofMethod, _ =
            withGenericParam Reflection.GenericParameterAttributes.None []

        let owner = TypeDefRid 2

        let outOfOrder =
            { ofMethod with
                Owner = TypeOrMethodDef.MethodDef (MethodDefRid 1)
            }

        outOfOrder.Owner.CodedIndex
        |> shouldBeSmallerThan (EmitScope.genericParams before).[0].Owner.CodedIndex

        let rid, after =
            EmitScope.addGenericParam outOfOrder [ TypeDefOrRef.TypeDef (TypeDefRid 1) ] before

        rid |> shouldEqual (GenericParamRid 2)

        EmitScopeExtension.violations ImmutableHashSet.Empty before after
        |> shouldEqual []

        EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 1)) before after
        |> shouldEqual []

        EmitScopeExtension.violations (ImmutableHashSet.Create owner) before after
        |> shouldEqual
            [
                EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.GenericParam, 2, owner)
                EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.GenericParamConstraint, 1, owner)
            ]

    [<Test>]
    let ``a changed generic parameter is reported at its RID`` () : unit =
        let before, _, _ = withGenericParam Reflection.GenericParameterAttributes.None []

        let after, _, _ =
            withGenericParam Reflection.GenericParameterAttributes.Covariant []

        for createdTypes in [ ImmutableHashSet.Empty ; ImmutableHashSet.Create (TypeDefRid 2) ] do
            EmitScopeExtension.violations createdTypes before after
            |> shouldEqual [ EmitScopeExtensionViolation.RowChanged (TableIndex.GenericParam, 1) ]

    [<Test>]
    let ``a constraint added with a parameter of a created type or one of its methods is refused`` () : unit =
        // One constraint row before, so that the new rows are at positions 2 and 3.
        let before, ofMethod, ofType =
            withGenericParam Reflection.GenericParameterAttributes.None [ TypeDefOrRef.TypeDef (TypeDefRid 1) ]

        let owner = TypeDefRid 2
        let created = ImmutableHashSet.Create owner

        let constraints =
            [ TypeDefOrRef.TypeDef owner ; TypeDefOrRef.TypeDef (TypeDefRid 1) ]

        let toMethod = EmitScope.addGenericParam ofMethod constraints before |> snd
        let toType = EmitScope.addGenericParam ofType constraints before |> snd

        for after in [ toType ; toMethod ] do
            EmitScopeExtension.violations ImmutableHashSet.Empty before after
            |> shouldEqual []

            // Another type's being created does not fix this one's parameters' constraints.
            EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 1)) before after
            |> shouldEqual []

            // A method's parameter's constraint belongs to the method's declaring type.
            EmitScopeExtension.violations created before after
            |> shouldEqual
                [
                    EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.GenericParam, 2, owner)
                    EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.GenericParamConstraint, 2, owner)
                    EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.GenericParamConstraint, 3, owner)
                ]

    [<Test>]
    let ``a changed constraint is reported at its RID`` () : unit =
        let constrained (targets : TypeDefOrRef list) =
            let scope, _, _ =
                withGenericParam Reflection.GenericParameterAttributes.None targets

            scope

        let before =
            constrained [ TypeDefOrRef.TypeDef (TypeDefRid 1) ; TypeDefOrRef.TypeDef (TypeDefRid 2) ]

        let changed =
            constrained [ TypeDefOrRef.TypeDef (TypeDefRid 1) ; TypeDefOrRef.TypeDef (TypeDefRid 1) ]

        let dropped = constrained [ TypeDefOrRef.TypeDef (TypeDefRid 1) ]

        for createdTypes in [ ImmutableHashSet.Empty ; ImmutableHashSet.Create (TypeDefRid 2) ] do
            EmitScopeExtension.violations createdTypes before changed
            |> shouldEqual
                [
                    EmitScopeExtensionViolation.RowChanged (TableIndex.GenericParamConstraint, 2)
                ]

            EmitScopeExtension.violations createdTypes before dropped
            |> shouldEqual
                [
                    EmitScopeExtensionViolation.RowChanged (TableIndex.GenericParamConstraint, 2)
                ]

    [<Test>]
    let ``a user string added is an extension, and one rewritten is refused`` () : unit =
        let scope = EmitScope.create "M" Guid.Empty
        let _, before = EmitScope.internUserString "first" scope
        let _, appended = EmitScope.internUserString "second" before
        let _, rewritten = EmitScope.internUserString "other" scope

        EmitScopeExtension.violations ImmutableHashSet.Empty before appended
        |> shouldEqual []

        // Entries count from the first the scope interned, after the space every heap starts with.
        EmitScopeExtension.violations ImmutableHashSet.Empty before rewritten
        |> shouldEqual [ EmitScopeExtensionViolation.HeapEntryChanged (HeapIndex.UserString, 1) ]

    /// `<Module>`, then `First` (TypeDef 2) and `Second` (TypeDef 3), each with one method (MethodDefs 1 and
    /// 2) and one property (Properties 1 and 2).
    let private withProperties () : EmitScope * PropertyRid * PropertyRid =
        let define (name : string) (scope : EmitScope) : TypeDefRid * EmitScope =
            match EmitScopeDefinitions.defineTopLevelTypeDef name Reflection.TypeAttributes.Public None [] scope with
            | TypeDefDefinition.Defined (rid, scope) -> rid, scope
            | TypeDefDefinition.Duplicate _ -> failwith $"%s{name} is defined twice"

        let signature = ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x08uy |]
        let propertySignature = ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |]
        let scope = EmitScope.create "Props" Guid.Empty
        let first, scope = define "First" scope
        let second, scope = define "Second" scope

        let _, scope =
            EmitScopeDefinitions.defineMethod first "get_A" signature Reflection.MethodAttributes.Public scope

        let _, scope =
            EmitScopeDefinitions.defineMethod second "get_B" signature Reflection.MethodAttributes.Public scope

        let a, scope = EmitScopeDefinitions.defineProperty first "A" propertySignature 0 scope
        let b, scope = EmitScopeDefinitions.defineProperty second "B" propertySignature 0 scope
        scope, a, b

    [<Test>]
    let ``a property added to a created type is refused`` () : unit =
        let before, _, _ = withProperties ()

        let after =
            EmitScopeDefinitions.defineProperty
                (TypeDefRid 2)
                "Late"
                (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                0
                before
            |> snd

        EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 3)) before after
        |> shouldEqual []

        EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 2)) before after
        |> shouldEqual
            [
                EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.Property, 3, TypeDefRid 2)
            ]

    [<Test>]
    let ``an accessor is refused exactly when its property's type is created`` () : unit =
        let before, a, _ = withProperties ()

        // `Second`'s method as an accessor of `First`'s property: CoreCLR checks only the property's type.
        let after =
            EmitScopeDefinitions.defineMethodSemantics
                (HasSemantics.Property a)
                Reflection.MethodSemanticsAttributes.Getter
                (MethodDefRid 2)
                before

        EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 3)) before after
        |> shouldEqual []

        EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 2)) before after
        |> shouldEqual
            [
                EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.MethodSemantics, 1, TypeDefRid 2)
            ]

    [<Test>]
    let ``a created type's accessor rows may move`` () : unit =
        let scope, a, b = withProperties ()

        // `Second`'s property, the later one, has its accessor first; `Second` is created.
        let before =
            EmitScopeDefinitions.defineMethodSemantics
                (HasSemantics.Property b)
                Reflection.MethodSemanticsAttributes.Getter
                (MethodDefRid 2)
                scope

        // `First`'s accessor sorts before it, so `Second`'s row moves from image RID 1 to 2.
        let after =
            EmitScopeDefinitions.defineMethodSemantics
                (HasSemantics.Property a)
                Reflection.MethodSemanticsAttributes.Getter
                (MethodDefRid 1)
                before

        let afterPe, afterReader = readImage (EmitScopeImage.write after)
        use _ = afterPe

        methodSemanticsRows afterPe afterReader
        |> shouldEqual [ 2, 1, (1 <<< 1) ||| 1 ; 2, 2, (2 <<< 1) ||| 1 ]

        EmitScopeExtension.violations (ImmutableHashSet.Create (TypeDefRid 3)) before after
        |> shouldEqual []

    [<Test>]
    let ``a changed property or accessor is reported at its position`` () : unit =
        let scope, a, _ = withProperties ()

        let before =
            EmitScopeDefinitions.defineMethodSemantics
                (HasSemantics.Property a)
                Reflection.MethodSemanticsAttributes.Getter
                (MethodDefRid 1)
                scope

        let rewritten =
            EmitScopeDefinitions.defineMethodSemantics
                (HasSemantics.Property a)
                Reflection.MethodSemanticsAttributes.Setter
                (MethodDefRid 1)
                scope

        EmitScopeExtension.violations ImmutableHashSet.Empty before rewritten
        |> shouldEqual
            [
                EmitScopeExtensionViolation.RowChanged (TableIndex.MethodSemantics, 1)
            ]

        let oneProperty =
            let define (name : string) (scope : EmitScope) : TypeDefRid * EmitScope =
                match EmitScopeDefinitions.defineTopLevelTypeDef name Reflection.TypeAttributes.Public None [] scope with
                | TypeDefDefinition.Defined (rid, scope) -> rid, scope
                | TypeDefDefinition.Duplicate _ -> failwith $"%s{name} is defined twice"

            let s = EmitScope.create "Props" Guid.Empty
            let first, s = define "First" s
            let second, s = define "Second" s
            let signature = ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x08uy |]

            let _, s =
                EmitScopeDefinitions.defineMethod first "get_A" signature Reflection.MethodAttributes.Public s

            let _, s =
                EmitScopeDefinitions.defineMethod second "get_B" signature Reflection.MethodAttributes.Public s

            EmitScopeDefinitions.defineProperty
                second
                "A"
                (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                0
                s
            |> snd

        // The same name and signature, but another owner.
        EmitScopeExtension.violations ImmutableHashSet.Empty scope oneProperty
        |> List.contains (EmitScopeExtensionViolation.RowChanged (TableIndex.Property, 1))
        |> shouldEqual true
