namespace WoofWare.PawPrint

open System
open System.Collections.Immutable
open System.Reflection.Metadata.Ecma335

/// <summary>
/// One way in which a later emit scope fails to extend an earlier one, so that an image of the later
/// scope could not replace the earlier image without changing something the runtime has already read.
/// </summary>
/// <remarks>
/// A row's <c>position</c> is its RID in the scope, which is its RID in the image, except in
/// <c>InterfaceImpl</c>, <c>MethodImpl</c>, <c>MethodSemantics</c> and <c>Constant</c>: there it is the 1-based
/// position at which the scope holds the row, because the image sorts the first two by class, the third by
/// association and the fourth by parent. A row of those tables therefore moves when a later row sorts before
/// it, and that is not a violation: the runtime keeps no handle into them, only what each row names, and a
/// class's or an association's rows keep their order.
/// </remarks>
[<RequireQualifiedAccess>]
type EmitScopeExtensionViolation =
    /// A row of the earlier scope differs in the later one, or the later scope does not have it.
    | RowChanged of table : TableIndex * position : int

    /// An entry of one of the earlier scope's heaps differs in the later one, or the later scope does
    /// not have it. <c>entry</c> counts from 1 in the order the entries were interned, the empty
    /// string or blob, and the space every user string heap starts with, not counted.
    | HeapEntryChanged of heap : HeapIndex * entry : int

    /// A row the later scope adds is a member of a type already created: a field, method, property or
    /// event it declares, a parameter of one of its methods, one of its interface implementations or method
    /// impls, a generic parameter of it or of one of its methods, or a constraint of one, an accessor
    /// of one of its properties or events, whatever type the accessor's method belongs to, a custom attribute of
    /// it or of any of those members (<c>EmitScope.typeOfCustomAttributeOwner</c>), or a constant of one of its
    /// fields, properties or parameters (<c>EmitScope.typeOfConstantOwner</c>). A custom attribute of the
    /// assembly or the module is no type's.
    | AddedToCreatedType of table : TableIndex * position : int * createdType : TypeDefRid

    /// A row of the earlier scope differs in the later one only in what the emitter fills in after adding
    /// the row (a method's implementation flags or body, a type's parent, a bit of a flags column that a
    /// pseudo-attribute sets, <c>KnownCustomAttribute.flagMask</c>, the <c>HasDefault</c> bit of an owner that has
    /// a constant, or a constant's element type and value), and the row is a type already created or a member of
    /// one.
    | FilledInCreatedType of table : TableIndex * position : int * createdType : TypeDefRid

/// Whether one emit scope extends another, given which of its types the runtime has already created.
[<RequireQualifiedAccess>]
module EmitScopeExtension =

    /// <summary>
    /// Every way in which <paramref name="after"/> fails to extend <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// Empty when <paramref name="after"/> was built from <paramref name="before"/> by appending rows,
    /// interning entries, filling in the implementation flags and bodies of methods and the parents of
    /// types, setting the bits of flags columns that pseudo-attributes set, and setting constants (the owner's
    /// <c>Constant</c> row, replaced in place or added, and its <c>HasDefault</c> bit), none of them for a created
    /// type. A flags column that loses a bit, or gains one that no pseudo-attribute sets and that is not the
    /// <c>HasDefault</c> of an owner with a <c>Constant</c> row in <paramref name="after"/>, is a changed row; so is
    /// a <c>Constant</c> row whose owner differs at its position. Then an image of <paramref name="after"/> reads back every heap
    /// entry and created type of <paramref name="before"/>'s as <paramref name="before"/>'s image does,
    /// and every row but for those columns, at the same RID except in the tables the image sorts. A history that was
    /// otherwise rewritten, rather than appended to, is refused even where the image happens to read
    /// back the same.
    /// </remarks>
    /// <param name="created">
    /// The types of <paramref name="before"/> the runtime has created, whose members are fixed from
    /// then on.
    /// </param>
    /// <param name="before">The scope whose image the runtime has read.</param>
    /// <param name="after">The scope whose image is to replace it.</param>
    let violations
        (created : ImmutableHashSet<TypeDefRid>)
        (before : EmitScope)
        (after : EmitScope)
        : EmitScopeExtensionViolation list
        =
        // Each row of `before` at the same position of `after`, compared whole.
        let rewritten (table : TableIndex) (beforeRows : ImmutableList<'row>) (afterRows : ImmutableList<'row>) =
            [
                for i in 0 .. beforeRows.Count - 1 do
                    if i >= afterRows.Count || beforeRows.[i] <> afterRows.[i] then
                        yield EmitScopeExtensionViolation.RowChanged (table, i + 1)
            ]

        let heapRewritten
            (heap : HeapIndex)
            (same : 'entry -> 'entry -> bool)
            (beforeEntries : ImmutableList<'entry>)
            (afterEntries : ImmutableList<'entry>)
            =
            [
                for i in 0 .. beforeEntries.Count - 1 do
                    if i >= afterEntries.Count || not (same beforeEntries.[i] afterEntries.[i]) then
                        yield EmitScopeExtensionViolation.HeapEntryChanged (heap, i + 1)
            ]

        // Each row `after` appends whose type, as `owner` names it, is created.
        let addedToCreated (table : TableIndex) (beforeCount : int) (afterRows : ImmutableList<'row>) owner =
            [
                for i in beforeCount .. afterRows.Count - 1 do
                    let owner : TypeDefRid = owner afterRows.[i]

                    if created.Contains owner then
                        yield EmitScopeExtensionViolation.AddedToCreatedType (table, i + 1, owner)
            ]

        let mask = KnownCustomAttribute.flagMask

        // Whether a flags column changed only by gaining bits of `mask`, which pseudo-attributes set.
        let gainedPseudoAttributeBits (mask : int) (was : int) (now : int) : bool =
            was &&& ~~~now = 0 && (now &&& ~~~was) &&& ~~~mask = 0

        // Each row of `before` at the same position of `after`, where `filled` says whether `after`'s row at a
        // RID differs from `before`'s only in what the emitter fills in. A fill is refused only for a created type,
        // which `owner` names.
        let filledOrRewritten
            (table : TableIndex)
            (beforeRows : ImmutableList<'row>)
            (afterRows : ImmutableList<'row>)
            (filled : int -> 'row -> 'row -> bool)
            (owner : 'row -> TypeDefRid)
            =
            [
                for i in 0 .. beforeRows.Count - 1 do
                    if i >= afterRows.Count then
                        yield EmitScopeExtensionViolation.RowChanged (table, i + 1)
                    else
                        let was = beforeRows.[i]
                        let row = afterRows.[i]

                        if was = row then
                            ()
                        elif filled (i + 1) was row then
                            let owner = owner was

                            if created.Contains owner then
                                yield EmitScopeExtensionViolation.FilledInCreatedType (table, i + 1, owner)
                        else
                            yield EmitScopeExtensionViolation.RowChanged (table, i + 1)
            ]

        // Each method of `before` at the same position of `after`. The emitter fills in a method's
        // implementation flags and body after adding its row, the body again when `CreateType` is retried,
        // and the bits of its flags that pseudo-attributes set, so a change to those alone is a fill,
        // refused only for a created type.
        let methodsRewritten (beforeRows : ImmutableList<MethodDefRow>) (afterRows : ImmutableList<MethodDefRow>) =
            [
                for i in 0 .. beforeRows.Count - 1 do
                    if i >= afterRows.Count then
                        yield EmitScopeExtensionViolation.RowChanged (TableIndex.MethodDef, i + 1)
                    else
                        let was = beforeRows.[i]
                        let row = afterRows.[i]

                        if was = row then
                            ()
                        elif
                            { was with
                                ImplFlags = row.ImplFlags
                                Body = row.Body
                                Flags = row.Flags
                            } = row
                            && gainedPseudoAttributeBits (int mask.MethodDef) (int was.Flags) (int row.Flags)
                        then
                            if created.Contains was.Owner then
                                yield
                                    EmitScopeExtensionViolation.FilledInCreatedType (
                                        TableIndex.MethodDef,
                                        i + 1,
                                        was.Owner
                                    )
                        else
                            yield EmitScopeExtensionViolation.RowChanged (TableIndex.MethodDef, i + 1)
            ]

        // Each type of `before` at the same position of `after`. The emitter fills in a type's parent
        // after adding its row, and the bits of its flags that pseudo-attributes set, so a change to those
        // alone is a fill, refused only for a created type.
        let typeDefsRewritten (beforeRows : ImmutableList<TypeDefRow>) (afterRows : ImmutableList<TypeDefRow>) =
            [
                for i in 0 .. beforeRows.Count - 1 do
                    if i >= afterRows.Count then
                        yield EmitScopeExtensionViolation.RowChanged (TableIndex.TypeDef, i + 1)
                    else
                        let was = beforeRows.[i]
                        let row = afterRows.[i]

                        if was = row then
                            ()
                        elif
                            { was with
                                Extends = row.Extends
                                Flags = row.Flags
                            } = row
                            && gainedPseudoAttributeBits (int mask.TypeDef) (int was.Flags) (int row.Flags)
                        then
                            match EmitScope.tryTypeDefRid (i + 1) after with
                            | Some typeDef when created.Contains typeDef ->
                                yield
                                    EmitScopeExtensionViolation.FilledInCreatedType (TableIndex.TypeDef, i + 1, typeDef)
                            | Some _ -> ()
                            | None ->
                                failwith $"BUG: TypeDef %d{i + 1} is in the scope, but tryTypeDefRid has no row for it"
                        else
                            yield EmitScopeExtensionViolation.RowChanged (TableIndex.TypeDef, i + 1)
            ]

        let constantsAfter = EmitScope.constants after

        // The `HasDefault` bit of an owner that has a Constant row in `after`, which the emitter sets with the row,
        // and 0 for any other owner.
        let hasDefaultOf (owner : HasConstant) (bit : int) : int =
            if constantsAfter |> Seq.exists (fun row -> row.Parent = owner) then
                bit
            else
                0

        let methodOwner (method : MethodDefRid) : TypeDefRid =
            (EmitScope.methodDefs after).[method.Rid - 1].Owner

        // The type an accessor row belongs to: its property's or its event's. CoreCLR checks only that
        // type before adding one, and nothing reads the table by method.
        let associationOwner (row : MethodSemanticsRow) : TypeDefRid =
            match row.Association with
            | HasSemantics.Event ev -> (EmitScope.events after).[ev.Rid - 1].Owner
            | HasSemantics.Property property -> (EmitScope.properties after).[property.Rid - 1].Owner

        // The type a generic parameter belongs to: its owner, or its owner's declaring type.
        let genericParamOwner (parameter : GenericParamRow) : TypeDefRid =
            match parameter.Owner with
            | TypeOrMethodDef.TypeDef typeDef -> typeDef
            | TypeOrMethodDef.MethodDef method -> methodOwner method

        [
            if EmitScope.moduleRow before <> EmitScope.moduleRow after then
                yield EmitScopeExtensionViolation.RowChanged (TableIndex.Module, 1)

            yield! rewritten TableIndex.TypeRef (EmitScope.typeRefs before) (EmitScope.typeRefs after)
            yield! typeDefsRewritten (EmitScope.typeDefs before) (EmitScope.typeDefs after)

            yield!
                filledOrRewritten
                    TableIndex.Field
                    (EmitScope.fields before)
                    (EmitScope.fields after)
                    (fun rid was row ->
                        let hasDefault =
                            hasDefaultOf
                                (HasConstant.Field (FieldRid rid))
                                (int System.Reflection.FieldAttributes.HasDefault)

                        { was with
                            Flags = row.Flags
                        } = row
                        && gainedPseudoAttributeBits (int mask.Field ||| hasDefault) (int was.Flags) (int row.Flags)
                    )
                    (fun row -> row.Owner)

            yield!
                addedToCreated
                    TableIndex.Field
                    (EmitScope.fields before).Count
                    (EmitScope.fields after)
                    (fun row -> row.Owner)

            yield! methodsRewritten (EmitScope.methodDefs before) (EmitScope.methodDefs after)

            yield!
                addedToCreated
                    TableIndex.MethodDef
                    (EmitScope.methodDefs before).Count
                    (EmitScope.methodDefs after)
                    (fun row -> row.Owner)

            yield!
                filledOrRewritten
                    TableIndex.Param
                    (EmitScope.parameters before)
                    (EmitScope.parameters after)
                    (fun rid was row ->
                        let hasDefault =
                            hasDefaultOf
                                (HasConstant.Param (ParamRid rid))
                                (int System.Reflection.ParameterAttributes.HasDefault)

                        { was with
                            Flags = row.Flags
                        } = row
                        && gainedPseudoAttributeBits (int mask.Param ||| hasDefault) (int was.Flags) (int row.Flags)
                    )
                    (fun row -> methodOwner row.Owner)

            yield!
                addedToCreated
                    TableIndex.Param
                    (EmitScope.parameters before).Count
                    (EmitScope.parameters after)
                    (fun row -> methodOwner row.Owner)

            yield! rewritten TableIndex.InterfaceImpl (EmitScope.interfaceImpls before) (EmitScope.interfaceImpls after)

            yield!
                addedToCreated
                    TableIndex.InterfaceImpl
                    (EmitScope.interfaceImpls before).Count
                    (EmitScope.interfaceImpls after)
                    (fun row -> row.Class)


            yield!
                rewritten
                    TableIndex.CustomAttribute
                    (EmitScope.customAttributes before)
                    (EmitScope.customAttributes after)

            let attributesBefore = (EmitScope.customAttributes before).Count
            let attributesAfter = EmitScope.customAttributes after

            for i in attributesBefore .. attributesAfter.Count - 1 do
                match EmitScope.typeOfCustomAttributeOwner attributesAfter.[i].Parent after with
                | Some owner when created.Contains owner ->
                    yield EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.CustomAttribute, i + 1, owner)
                | Some _
                | None -> ()

            // A Constant row stays with its owner; its element type and value are filled in, refused for a created
            // type. The image sorts the table by parent, so a row's position is the scope's.
            let constantsBefore = EmitScope.constants before

            for i in 0 .. constantsBefore.Count - 1 do
                let was = constantsBefore.[i]

                if i >= constantsAfter.Count || constantsAfter.[i].Parent <> was.Parent then
                    yield EmitScopeExtensionViolation.RowChanged (TableIndex.Constant, i + 1)
                elif constantsAfter.[i] <> was then
                    let owner = EmitScope.typeOfConstantOwner was.Parent after

                    if created.Contains owner then
                        yield EmitScopeExtensionViolation.FilledInCreatedType (TableIndex.Constant, i + 1, owner)

            for i in constantsBefore.Count .. constantsAfter.Count - 1 do
                let owner = EmitScope.typeOfConstantOwner constantsAfter.[i].Parent after

                if created.Contains owner then
                    yield EmitScopeExtensionViolation.AddedToCreatedType (TableIndex.Constant, i + 1, owner)

            yield! rewritten TableIndex.MemberRef (EmitScope.memberRefs before) (EmitScope.memberRefs after)
            yield! rewritten TableIndex.StandAloneSig (EmitScope.standAloneSigs before) (EmitScope.standAloneSigs after)

            yield!
                filledOrRewritten
                    TableIndex.Event
                    (EmitScope.events before)
                    (EmitScope.events after)
                    (fun _ was row ->
                        { was with
                            Flags = row.Flags
                        } = row
                        && gainedPseudoAttributeBits (int mask.Event) (int was.Flags) (int row.Flags)
                    )
                    (fun row -> row.Owner)

            yield!
                addedToCreated
                    TableIndex.Event
                    (EmitScope.events before).Count
                    (EmitScope.events after)
                    (fun row -> row.Owner)

            yield!
                filledOrRewritten
                    TableIndex.Property
                    (EmitScope.properties before)
                    (EmitScope.properties after)
                    (fun rid was row ->
                        let hasDefault =
                            hasDefaultOf
                                (HasConstant.Property (PropertyRid rid))
                                (int System.Reflection.PropertyAttributes.HasDefault)

                        { was with
                            Flags = row.Flags
                        } = row
                        && gainedPseudoAttributeBits (int mask.Property ||| hasDefault) (int was.Flags) (int row.Flags)
                    )
                    (fun row -> row.Owner)

            yield!
                addedToCreated
                    TableIndex.Property
                    (EmitScope.properties before).Count
                    (EmitScope.properties after)
                    (fun row -> row.Owner)

            yield!
                rewritten
                    TableIndex.MethodSemantics
                    (EmitScope.methodSemantics before)
                    (EmitScope.methodSemantics after)

            yield!
                addedToCreated
                    TableIndex.MethodSemantics
                    (EmitScope.methodSemantics before).Count
                    (EmitScope.methodSemantics after)
                    associationOwner

            yield! rewritten TableIndex.TypeSpec (EmitScope.typeSpecs before) (EmitScope.typeSpecs after)

            yield! rewritten TableIndex.MethodImpl (EmitScope.methodImpls before) (EmitScope.methodImpls after)

            yield!
                addedToCreated
                    TableIndex.MethodImpl
                    (EmitScope.methodImpls before).Count
                    (EmitScope.methodImpls after)
                    (fun row -> row.Class)


            // A scope gains its `Assembly` row once; from then on the row is fixed.
            match EmitScope.assembly before with
            | Some row when EmitScope.assembly after <> Some row ->
                yield EmitScopeExtensionViolation.RowChanged (TableIndex.Assembly, 1)
            | Some _
            | None -> ()

            yield! rewritten TableIndex.AssemblyRef (EmitScope.assemblyRefs before) (EmitScope.assemblyRefs after)
            yield! rewritten TableIndex.GenericParam (EmitScope.genericParams before) (EmitScope.genericParams after)

            yield!
                addedToCreated
                    TableIndex.GenericParam
                    (EmitScope.genericParams before).Count
                    (EmitScope.genericParams after)
                    genericParamOwner

            yield! rewritten TableIndex.MethodSpec (EmitScope.methodSpecs before) (EmitScope.methodSpecs after)

            yield!
                rewritten
                    TableIndex.GenericParamConstraint
                    (EmitScope.genericParamConstraints before)
                    (EmitScope.genericParamConstraints after)

            yield!
                addedToCreated
                    TableIndex.GenericParamConstraint
                    (EmitScope.genericParamConstraints before).Count
                    (EmitScope.genericParamConstraints after)
                    (fun row -> genericParamOwner (EmitScope.genericParams after).[row.Owner.Rid - 1])

            yield! heapRewritten HeapIndex.String (=) (EmitScope.strings before) (EmitScope.strings after)

            yield!
                heapRewritten
                    HeapIndex.Blob
                    (fun (x : ImmutableArray<byte>) y -> x.AsSpan().SequenceEqual (y.AsSpan ()))
                    (EmitScope.blobs before)
                    (EmitScope.blobs after)

            yield! heapRewritten HeapIndex.Guid (=) (EmitScope.guids before) (EmitScope.guids after)

            // The space every heap starts with is entry 0, so entries count from the first interned.
            let userStringEntries (scope : EmitScope) =
                (EmitScope.userStrings scope).RemoveAt 0

            yield!
                heapRewritten
                    HeapIndex.UserString
                    (fun (x : ImmutableArray<byte>) y -> x.AsSpan().SequenceEqual (y.AsSpan ()))
                    (userStringEntries before)
                    (userStringEntries after)
        ]
