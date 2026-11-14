namespace WoofWare.PawPrint

/// Why CoreCLR refuses to load an explicit-layout type, given its own instance fields' FieldLayout
/// rows (ECMA-335 II.22.16). Each case names the field, and where it has one, the row's raw offset.
[<RequireQualifiedAccess>]
type ExplicitFieldOffsetRefusal<'field> =
    /// The offset plus the parent's instance size, or that plus the field's size, does not fit in 32
    /// bits. `ReadOffsetsForExplicitLayout` checks both with `ClrSafeInt` and throws
    /// `OutOfMemoryException` (`COMPlusThrowOM`) on overflow.
    | OffsetOverflows of field : 'field * offset : uint32
    /// The instance field has no FieldLayout row: `TypeLoadException`,
    /// `IDS_CLASSLOAD_NSTRUCT_EXPLICIT_OFFSET`.
    | NoOffset of field : 'field
    /// The offset plus the parent's instance size is above `Int32.MaxValue`: `TypeLoadException`,
    /// `IDS_CLASSLOAD_NSTRUCT_NEGATIVE_OFFSET`.
    | NegativeOffset of field : 'field * offset : uint32
    /// The offset plus the parent's instance size is above `FIELD_OFFSET_LAST_REAL_OFFSET`, the
    /// largest a `FieldDesc` can hold: a `TypeLoadException` carrying only `COR_E_TYPELOAD`
    /// (`FieldDesc::SetOffset`, from `SetFieldOffsets`).
    | BeyondFieldDescOffsetLimit of field : 'field * offset : uint32

[<RequireQualifiedAccess>]
module ExplicitFieldOffsetRefusal =

    /// What CoreCLR does, for a refusal message naming the type as `typeName`.
    let describe
        (fieldName : 'field -> string)
        (typeName : string)
        (refusal : ExplicitFieldOffsetRefusal<'field>)
        : string
        =
        match refusal with
        | ExplicitFieldOffsetRefusal.OffsetOverflows (field, offset) ->
            $"field %s{fieldName field} of explicit-layout type %s{typeName} has FieldLayout offset 0x%08X{offset}, which overflows 32 bits once the parent's size and the field's own are added; CoreCLR refuses to load the type with OutOfMemoryException (ReadOffsetsForExplicitLayout, classlayoutinfo.cpp)"
        | ExplicitFieldOffsetRefusal.NoOffset field ->
            $"instance field %s{fieldName field} of explicit-layout type %s{typeName} has no FieldLayout row; CoreCLR refuses to load the type with TypeLoadException (IDS_CLASSLOAD_NSTRUCT_EXPLICIT_OFFSET)"
        | ExplicitFieldOffsetRefusal.NegativeOffset (field, offset) ->
            $"field %s{fieldName field} of explicit-layout type %s{typeName} has FieldLayout offset 0x%08X{offset}, above Int32.MaxValue once the parent's size is added; CoreCLR refuses to load the type with TypeLoadException (IDS_CLASSLOAD_NSTRUCT_NEGATIVE_OFFSET)"
        | ExplicitFieldOffsetRefusal.BeyondFieldDescOffsetLimit (field, offset) ->
            $"field %s{fieldName field} of explicit-layout type %s{typeName} has FieldLayout offset 0x%08X{offset}, above FIELD_OFFSET_LAST_REAL_OFFSET (0x07FFFFF8) once the parent's size is added; CoreCLR refuses to load the type with TypeLoadException (COR_E_TYPELOAD, FieldDesc::SetOffset)"

/// Where CoreCLR places an explicit-layout type's own instance fields, from their FieldLayout rows:
/// `ReadOffsetsForExplicitLayout` and then `SetFieldOffsets` (classlayoutinfo.cpp).
[<RequireQualifiedAccess>]
module ExplicitFieldOffsets =

    /// `FIELD_OFFSET_LAST_REAL_OFFSET` (vm/field.h): the largest offset a `FieldDesc` holds.
    let fieldDescOffsetLimit : uint32 = (1u <<< 27) - 1u - 7u

    /// Each of `fields` (the field, its size, and its row's raw offset if it has one, in declaration
    /// order) at its row's offset plus `parentSize`; or the first refusal CoreCLR reaches. CoreCLR
    /// checks every row for overflow before any field for a missing or negative offset, and those
    /// before any offset against `fieldDescOffsetLimit`.
    let place
        (parentSize : uint32)
        (fields : ('field * uint32 * uint32 option) list)
        : Result<('field * int) list, ExplicitFieldOffsetRefusal<'field>>
        =
        // The row's offset plus the parent's size, and that plus the field's size, in 32 bits.
        let placedEnd (offset : uint32) (size : uint32) : (uint32 * uint32) option =
            let placed = offset + parentSize

            if placed < offset then
                None
            else
                let fieldEnd = placed + size
                if fieldEnd < placed then None else Some (placed, fieldEnd)

        let overflow =
            fields
            |> List.tryPick (fun (field, size, row) ->
                match row with
                | Some offset when (placedEnd offset size).IsNone ->
                    Some (ExplicitFieldOffsetRefusal.OffsetOverflows (field, offset))
                | _ -> None
            )

        match overflow with
        | Some refusal -> Error refusal
        | None ->

        let missingOrNegative =
            fields
            |> List.tryPick (fun (field, _, row) ->
                match row with
                | None -> Some (ExplicitFieldOffsetRefusal.NoOffset field)
                | Some offset when offset + parentSize > uint32 System.Int32.MaxValue ->
                    Some (ExplicitFieldOffsetRefusal.NegativeOffset (field, offset))
                | Some _ -> None
            )

        match missingOrNegative with
        | Some refusal -> Error refusal
        | None ->

        let placed =
            fields
            |> List.map (fun (field, _, row) ->
                // Every row is present and fits, by the two checks above.
                let offset = Option.get row
                field, offset, offset + parentSize
            )

        match
            placed
            |> List.tryPick (fun (field, offset, placed) ->
                if placed > fieldDescOffsetLimit then
                    Some (ExplicitFieldOffsetRefusal.BeyondFieldDescOffsetLimit (field, offset))
                else
                    None
            )
        with
        | Some refusal -> Error refusal
        | None -> placed |> List.map (fun (field, _, placed) -> field, int placed) |> Ok
