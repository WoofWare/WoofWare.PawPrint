namespace WoofWare.PawPrint

open System.Collections.Immutable
open Microsoft.Extensions.Logging

/// <summary>
/// How one custom-attribute argument is both read out of the blob and turned into a runtime value:
/// the ECMA-335 II.23.3 grammar its bytes follow, plus — for an <c>SZARRAY</c> — the concretised
/// element type, which the blob does not record.
/// </summary>
/// <remarks>
/// This mirrors <see cref="T:WoofWare.PawPrint.CustomAttribArgShape"/>, which is what the decoder
/// consumes and which <c>CustomAttribArgPlan.shape</c> projects back out. The extra information is
/// needed because a fixed argument's element type comes from the constructor signature rather than
/// from the blob, and because the shape's <c>Enum</c> case carries only the underlying width —
/// enough to read the bytes, but not enough to say that a <c>MyEnum[]</c> is a <c>MyEnum[]</c>
/// rather than an <c>int16[]</c>. Driving the read and the lowering from one value is what makes a
/// value/plan mismatch impossible for anything the decoder produced.
/// </remarks>
[<RequireQualifiedAccess>]
type CustomAttribArgPlan =
    | Primitive of PrimitiveType
    | Enum of underlying : EnumUnderlyingType
    /// A <c>System.Type</c> parameter. The blob carries a type *name*, which the lowering cannot
    /// resolve itself: the caller resolves every name first (see
    /// <c>CustomAttribute.typeNamesToResolve</c>) and hands <c>toCliType</c> the resulting
    /// <c>RuntimeType</c> objects.
    | Type
    /// <c>elementType</c> is the element type the constructor *declared*, concretised: CoreCLR's
    /// fixed-arg loop likewise takes it from the signature (<c>customattribute.cpp:961</c>) rather
    /// than from the blob, so the allocated array's element type does not depend on what the bytes
    /// happen to look like.
    | SzArray of elementType : ConcreteTypeHandle * elements : CustomAttribArgPlan
    /// An <c>object</c> parameter. Its bytes name their own type, so the plan for the value — and,
    /// for an array, its element type — comes from the decoded tag rather than from here.
    | TaggedObject

[<RequireQualifiedAccess>]
module CustomAttribArgPlan =
    /// The decoder's view of a plan: what the bytes look like, with the runtime types dropped.
    let rec shape (plan : CustomAttribArgPlan) : CustomAttribArgShape =
        match plan with
        | CustomAttribArgPlan.Primitive pt -> CustomAttribArgShape.Primitive pt
        | CustomAttribArgPlan.Enum underlying -> CustomAttribArgShape.Enum underlying
        | CustomAttribArgPlan.Type -> CustomAttribArgShape.Type
        | CustomAttribArgPlan.SzArray (_, elements) -> CustomAttribArgShape.SzArray (shape elements)
        | CustomAttribArgPlan.TaggedObject -> CustomAttribArgShape.TaggedObject

/// <summary>
/// One type name from a <c>CustomAttrib</c> blob, as <c>TypeNameResolver</c> resolved it: the
/// <c>RuntimeType</c> it answered with, and, when that type is an enum, the enum's handle and
/// underlying type.
/// </summary>
/// <remarks>
/// A <c>System.Type</c> argument needs only the object; an enum named inside a
/// <c>TAGGED_OBJECT</c> needs the handle, to box its value as that enum, and the underlying type,
/// which is how wide its value is in the blob.
/// </remarks>
type CustomAttribResolvedType =
    {
        RuntimeType : ManagedHeapAddress
        Enum : (ConcreteTypeHandle * EnumUnderlyingType) option
    }

[<RequireQualifiedAccess>]
module CustomAttribResolvedType =
    /// What the decoder needs to know of a resolution.
    let name (resolved : CustomAttribResolvedType) : CustomAttribResolvedName =
        match resolved.Enum with
        | Some (_, underlying) -> CustomAttribResolvedName.Enum underlying
        | None -> CustomAttribResolvedName.NotEnum

/// <summary>
/// Lower a decoded <c>CustomAttribFixedArg</c> to the <c>CliType</c> value the
/// attribute ctor expects on its parameter slot.
/// </summary>
/// <remarks>
/// Mirrors the per-arg cases that CoreCLR's <c>CustomAttribute_CreateCustomAttributeInstance</c>
/// (<c>customattribute.cpp:900</c>) produces via <c>GetDataFromBlob</c> + <c>Box</c> just
/// before invoking the ctor. The set is the one the blob reader emits: primitives,
/// <c>SerString</c>, <c>ENUM</c>, <c>TYPE</c>, <c>TAGGED_OBJECT</c>, and <c>SZARRAY</c> of those.
/// </remarks>
[<RequireQualifiedAccess>]
module CustomAttribValueLowering =

    /// <summary>
    /// Convert a fixed arg whose lowering needs nothing but the decoded value itself. Returns
    /// <c>Error</c> for the variants that need more — a heap allocation (<c>String (Some _)</c>,
    /// <c>Tagged</c>), a resolved type (<c>Type (Some _)</c>), or the element type an <c>Array</c>
    /// does not carry; callers should use <c>toCliType</c> in that case.
    /// </summary>
    /// <remarks>
    /// CLI eval-stack rules treat <c>uint32</c>/<c>uint64</c> identically to their
    /// signed counterparts (the spec stores them with two's-complement wraparound;
    /// see the <c>UInt32</c>/<c>UInt64</c> branches of <c>CliType.zeroOfPrimitive</c>),
    /// so the unsigned variants here route through <c>Int32</c>/<c>Int64</c>.
    /// </remarks>
    let rec tryToPureCliType (arg : CustomAttribFixedArg) : Result<CliType, string> =
        match arg with
        | CustomAttribFixedArg.Bool b -> CliType.Bool (if b then 1uy else 0uy) |> Ok
        | CustomAttribFixedArg.Char c ->
            let v = uint16 c
            CliType.Char (byte (v >>> 8), byte (v &&& 0xFFus)) |> Ok
        | CustomAttribFixedArg.I1 v -> CliType.Numeric (CliNumericType.Int8 v) |> Ok
        | CustomAttribFixedArg.U1 v -> CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim v)) |> Ok
        | CustomAttribFixedArg.I2 v -> CliType.Numeric (CliNumericType.Int16 v) |> Ok
        | CustomAttribFixedArg.U2 v -> CliType.Numeric (CliNumericType.UInt16 v) |> Ok
        | CustomAttribFixedArg.I4 v -> CliType.Numeric (CliNumericType.Int32 v) |> Ok
        | CustomAttribFixedArg.U4 v -> CliType.Numeric (CliNumericType.Int32 (int32 v)) |> Ok
        | CustomAttribFixedArg.I8 v -> CliType.Numeric (CliNumericType.Int64 (Int64Source.Verbatim v)) |> Ok
        | CustomAttribFixedArg.U8 v -> CliType.Numeric (CliNumericType.Int64 (Int64Source.Verbatim (int64 v))) |> Ok
        | CustomAttribFixedArg.R4 v -> CliType.Numeric (CliNumericType.Float32 v) |> Ok
        | CustomAttribFixedArg.R8 v -> CliType.Numeric (CliNumericType.Float64 v) |> Ok
        | CustomAttribFixedArg.String None -> CliType.ObjectRef None |> Ok
        | CustomAttribFixedArg.String (Some _) ->
            Error "CustomAttribFixedArg.String (Some _) requires allocation; use CustomAttribValueLowering.toCliType"
        // An enum argument lowers to the bare underlying integer, with no enum wrapper built here.
        // `IlMachineStateExecution.callMethod` derives each parameter's
        // zero value from the ctor's *declared* parameter type — for an enum parameter, the
        // `value__` struct — and `EvalStackValue.toCliTypeCoerced` rewraps the popped integer into
        // that slot. Enums flatten to their underlying integer on the eval stack
        // (`PrimitiveLikeKind.EnumLike`), so pushing the wrapper here would be wrong.
        | CustomAttribFixedArg.Enum underlying -> tryToPureCliType underlying
        // The null sentinel is a null `Type`, and CoreCLR likewise passes a null reference without
        // resolving anything (customattribute.cpp:438).
        | CustomAttribFixedArg.Type None -> CliType.ObjectRef None |> Ok
        | CustomAttribFixedArg.Type (Some _) ->
            Error
                "CustomAttribFixedArg.Type (Some _) requires the resolved System.Type object; use CustomAttribValueLowering.toCliType"
        // Even the null-array sentinel is refused here rather than answered with `ObjectRef None`:
        // ECMA-335 II.23.3 gives an SZARRAY argument no element type, so without a
        // `CustomAttribArgPlan` there is nothing to confirm this value came from an array
        // parameter at all.
        | CustomAttribFixedArg.Array _ ->
            Error
                "CustomAttribFixedArg.Array requires a CustomAttribArgPlan naming the element type; use CustomAttribValueLowering.toCliType"
        // Even a null string, which needs no box, is refused, for the same reason as the null array:
        // without a plan nothing confirms that an `object` parameter is what receives it.
        | CustomAttribFixedArg.Tagged _ ->
            Error
                "CustomAttribFixedArg.Tagged boxes its value as the type its tag names; use CustomAttribValueLowering.toCliType"

    /// The handle of the corelib type a primitive serialization type names, as CoreCLR's
    /// `CoreLibBinder::GetElementType` answers it.
    let private primitiveHandle
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (pt : PrimitiveType)
        (state : IlMachineState)
        : IlMachineState * ConcreteTypeHandle
        =
        IlMachineTypeResolution.concretizeType
            loggerFactory
            baseClassTypes
            state
            baseClassTypes.Corelib.DefinitionFullName
            ImmutableArray.Empty
            ImmutableArray.Empty
            (TypeDefn.PrimitiveType pt)

    /// `System.Type`, the element type CoreCLR allocates for a `TYPE` array (`CLASS__TYPE` in
    /// `GetTypeHandleFromBlob`).
    let private systemTypeHandle
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (state : IlMachineState)
        : IlMachineState * ConcreteTypeHandle
        =
        let typeInfo =
            baseClassTypes.Corelib.TryGetTopLevelTypeDef "System" "Type"
            |> Option.defaultWith (fun () -> failwith "CustomAttribValueLowering: CoreLib defines no System.Type")

        LoadedTypeInfo.typeInfoToTypeDefn' baseClassTypes state.TypeSystem._LoadedAssemblies typeInfo
        |> IlMachineTypeResolution.concretizeType
            loggerFactory
            baseClassTypes
            state
            baseClassTypes.Corelib.DefinitionFullName
            ImmutableArray.Empty
            ImmutableArray.Empty

    /// The enum that the head of `resolvedTypes` names, for an enum named inside a TAGGED_OBJECT.
    let private takeEnum
        (name : string)
        (resolvedTypes : CustomAttribResolvedType list)
        : ConcreteTypeHandle * EnumUnderlyingType * CustomAttribResolvedType list
        =
        match resolvedTypes with
        | {
              Enum = Some (handle, underlying)
          } :: rest -> handle, underlying, rest
        | {
              Enum = None
          } :: _ ->
            failwithf
                "CustomAttribValueLowering.toCliType: bug — the resolved type for the enum \"%s\" is not an enum; CustomAttribute.readFixedArgsResolving refuses that blob, so it must have been decoded with other resolutions"
                name
        | [] ->
            failwithf
                "CustomAttribValueLowering.toCliType: bug — no resolved type left for the enum \"%s\"; the caller must resolve every name CustomAttribute.typeNamesToResolve lists, in order"
                name

    /// <summary>
    /// Lower a fixed arg to a <c>CliType</c>, allocating on the heap when the arg is a non-null
    /// <c>SerString</c> or a non-null <c>SZARRAY</c>. For every other variant the state is returned
    /// unchanged. <paramref name="plan"/> must be the plan the arg was decoded with.
    /// </summary>
    /// <param name="loggerFactory">LoggerFactory into which to emit logs.</param>
    /// <param name="baseClassTypes">
    /// CoreLib's well-known types, which allocating a string or an array's elements needs.
    /// </param>
    /// <param name="plan">
    /// The plan <paramref name="arg"/> was decoded with. Pairing a value with any other plan is a
    /// caller bug and fails loudly.
    /// </param>
    /// <param name="arg">The decoded fixed argument to lower.</param>
    /// <param name="resolvedTypes">
    /// The resolutions of the names <c>CustomAttribute.typeNamesToResolve</c> listed for this arg,
    /// in that order. Each non-null <c>Type</c>, and each enum a <c>Tagged</c> value names, consumes
    /// the head, and the unconsumed tail is returned, so a caller lowering several args threads it
    /// through and checks that nothing is left at the end. Running out is a caller bug and fails
    /// loudly.
    /// </param>
    /// <param name="state">The state to allocate in; the returned state holds whatever was allocated.</param>
    /// <remarks>
    /// A <c>Tagged</c> value is boxed as the type its tag names, as CoreCLR's <c>typeObject</c> arm
    /// does: a primitive as that corelib primitive (so <c>U4</c> boxes as <c>System.UInt32</c>,
    /// whatever its eval-stack form), an enum as the resolved enum. A string, a <c>Type</c> and an
    /// array are references already.
    ///
    /// The empty <c>SerString</c> routes through the canonical interned empty
    /// string, mirroring CoreCLR's <c>GetDataFromBlob</c> for
    /// <c>SERIALIZATION_TYPE_STRING</c>: <c>StringObject::NewString(0)</c>
    /// (and the <c>(LPCUTF8, cBytes)</c> overload's <c>cBytes == 0</c> branch)
    /// both return <c>GetEmptyString()</c>. Non-empty <c>SerString</c> values
    /// allocate a fresh heap object on every call.
    /// </remarks>
    let rec toCliType
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (plan : CustomAttribArgPlan)
        (arg : CustomAttribFixedArg)
        (resolvedTypes : CustomAttribResolvedType list)
        (state : IlMachineState)
        : CliType * CustomAttribResolvedType list * IlMachineState
        =
        let box
            (handle : ConcreteTypeHandle)
            (payload : CliType)
            (resolvedTypes : CustomAttribResolvedType list)
            (state : IlMachineState)
            =
            let addr, state =
                Boxing.boxValueType loggerFactory baseClassTypes handle (EvalStackValue.ofCliType payload) state

            CliType.ObjectRef (Some addr), resolvedTypes, state

        match plan, arg with
        // ECMA-335 II.23.3's `NumElem = 0xFFFFFFFF` is the null-array sentinel, which is a
        // different result from the zero-length array below; CoreCLR likewise leaves the argument
        // slot null rather than allocating (`customattribute.cpp:845`).
        | CustomAttribArgPlan.SzArray _, CustomAttribFixedArg.Array None -> CliType.ObjectRef None, resolvedTypes, state
        | CustomAttribArgPlan.SzArray (elementType, elementPlan), CustomAttribFixedArg.Array (Some elements) ->
            let elementZero, state =
                IlMachineState.cliTypeZeroOfHandle state baseClassTypes elementType

            let addr, state =
                IlMachineState.allocateArray
                    (ConcreteTypeHandle.OneDimArrayZero elementType)
                    (fun () -> elementZero)
                    (List.length elements)
                    state

            let resolvedTypes, state =
                ((resolvedTypes, state), List.indexed elements)
                ||> List.fold (fun (resolvedTypes, state) (index, element) ->
                    let cell, resolvedTypes, state =
                        toCliType loggerFactory baseClassTypes elementPlan element resolvedTypes state

                    // The coercion `stelem` performs on every guest array store. It is what rewraps
                    // an enum element — which lowers to its bare underlying integer — into the
                    // `EnumLike` value-type cell the array's stride was measured from. For the
                    // other element types in scope it is the identity, bar `Float32`, which widens
                    // to double on the evaluation stack and narrows back; that quietens a
                    // signalling-NaN bit pattern, which no C#-emitted blob carries and which the
                    // scalar argument path already does when it pushes.
                    let cell =
                        EvalStackValue.toCliTypeCoerced elementZero (EvalStackValue.ofCliType cell)

                    resolvedTypes, IlMachineState.setArrayValue addr cell index state
                )

            CliType.ObjectRef (Some addr), resolvedTypes, state
        | CustomAttribArgPlan.Type, CustomAttribFixedArg.Type (Some name) ->
            // The head is this occurrence's type: `typeNamesToResolve` lists names in the order
            // this walk meets them, and each is resolved to exactly one object.
            match resolvedTypes with
            | resolved :: rest -> CliType.ObjectRef (Some resolved.RuntimeType), rest, state
            | [] ->
                failwithf
                    "CustomAttribValueLowering.toCliType: bug — no resolved type left for the System.Type argument named \"%s\"; the caller must resolve every name CustomAttribute.typeNamesToResolve lists, in order"
                    name
        | CustomAttribArgPlan.Type, (CustomAttribFixedArg.Type None as arg) ->
            match tryToPureCliType arg with
            | Ok t -> t, resolvedTypes, state
            | Error msg -> failwithf "CustomAttribValueLowering.toCliType: bug — a null Type produced Error %s" msg
        | CustomAttribArgPlan.Type, other ->
            failwithf
                "CustomAttribValueLowering.toCliType: bug — Type plan paired with non-Type value %A; a value must be lowered with the plan it was decoded with"
                other
        | CustomAttribArgPlan.TaggedObject, CustomAttribFixedArg.Tagged (tag, value) ->
            match tag with
            | CustomAttribTaggedType.Primitive PrimitiveType.String ->
                toCliType
                    loggerFactory
                    baseClassTypes
                    (CustomAttribArgPlan.Primitive PrimitiveType.String)
                    value
                    resolvedTypes
                    state
            | CustomAttribTaggedType.Primitive pt ->
                let payload, resolvedTypes, state =
                    toCliType loggerFactory baseClassTypes (CustomAttribArgPlan.Primitive pt) value resolvedTypes state

                let state, handle = primitiveHandle loggerFactory baseClassTypes pt state
                box handle payload resolvedTypes state
            | CustomAttribTaggedType.Type ->
                toCliType loggerFactory baseClassTypes CustomAttribArgPlan.Type value resolvedTypes state
            | CustomAttribTaggedType.Enum name ->
                let handle, underlying, resolvedTypes = takeEnum name resolvedTypes

                let payload, resolvedTypes, state =
                    toCliType
                        loggerFactory
                        baseClassTypes
                        (CustomAttribArgPlan.Enum underlying)
                        value
                        resolvedTypes
                        state

                box handle payload resolvedTypes state
            | CustomAttribTaggedType.SzArray element ->
                // The element type comes from the tag, as CoreCLR's `GetTypeHandleFromBlob` takes it,
                // so an empty or null array still has it; an enum's is resolved even for a null array.
                let state, elementType, elementPlan, resolvedTypes =
                    match element with
                    | CustomAttribTaggedElementType.Primitive pt ->
                        let state, handle = primitiveHandle loggerFactory baseClassTypes pt state
                        state, handle, CustomAttribArgPlan.Primitive pt, resolvedTypes
                    | CustomAttribTaggedElementType.Type ->
                        let state, handle = systemTypeHandle loggerFactory baseClassTypes state
                        state, handle, CustomAttribArgPlan.Type, resolvedTypes
                    | CustomAttribTaggedElementType.Object ->
                        let state, handle =
                            primitiveHandle loggerFactory baseClassTypes PrimitiveType.Object state

                        state, handle, CustomAttribArgPlan.TaggedObject, resolvedTypes
                    | CustomAttribTaggedElementType.Enum name ->
                        let handle, underlying, resolvedTypes = takeEnum name resolvedTypes
                        state, handle, CustomAttribArgPlan.Enum underlying, resolvedTypes

                toCliType
                    loggerFactory
                    baseClassTypes
                    (CustomAttribArgPlan.SzArray (elementType, elementPlan))
                    value
                    resolvedTypes
                    state
        | CustomAttribArgPlan.TaggedObject, other ->
            failwithf
                "CustomAttribValueLowering.toCliType: bug — TaggedObject plan paired with untagged value %A; a value must be lowered with the plan it was decoded with"
                other
        | (CustomAttribArgPlan.Primitive _ | CustomAttribArgPlan.Enum _), CustomAttribFixedArg.Tagged _ ->
            failwithf
                "CustomAttribValueLowering.toCliType: bug — plan %A paired with tagged value %A; a value must be lowered with the plan it was decoded with"
                plan
                arg
        | (CustomAttribArgPlan.Primitive _ | CustomAttribArgPlan.Enum _ | CustomAttribArgPlan.SzArray _),
          CustomAttribFixedArg.Type _ ->
            failwithf
                "CustomAttribValueLowering.toCliType: bug — plan %A paired with Type value %A; a value must be lowered with the plan it was decoded with"
                plan
                arg
        | CustomAttribArgPlan.SzArray _, other ->
            failwithf
                "CustomAttribValueLowering.toCliType: bug — SZARRAY plan %A paired with non-array value %A; a value must be lowered with the plan it was decoded with"
                plan
                other
        | (CustomAttribArgPlan.Primitive _ | CustomAttribArgPlan.Enum _), CustomAttribFixedArg.Array _ ->
            failwithf
                "CustomAttribValueLowering.toCliType: bug — scalar plan %A paired with array value %A; a value must be lowered with the plan it was decoded with"
                plan
                arg
        | _, CustomAttribFixedArg.String (Some "") ->
            let addr, state =
                IlMachineState.internCanonicalEmptyString loggerFactory baseClassTypes state

            CliType.ObjectRef (Some addr), resolvedTypes, state
        | _, CustomAttribFixedArg.String (Some s) ->
            let addr, state =
                IlMachineState.allocateManagedString loggerFactory baseClassTypes s state

            CliType.ObjectRef (Some addr), resolvedTypes, state
        | _, _ ->
            match tryToPureCliType arg with
            | Ok t -> t, resolvedTypes, state
            | Error msg ->
                failwithf "CustomAttribValueLowering.toCliType: bug — non-allocating arg %A produced Error %s" arg msg
