namespace WoofWare.PawPrint

open System
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Metadata.Ecma335

/// <summary>
/// The outcome of asking CoreCLR's emitter to define a type in a dynamic module's scope
/// (<c>RegMeta::_DefineTypeDef</c>).
/// </summary>
[<RequireQualifiedAccess>]
type TypeDefDefinition =
    /// A new <c>TypeDef</c> row, and the scope that holds it.
    | Defined of TypeDefRid * EmitScope
    /// <summary>
    /// The scope already has this row, with the same namespace and name, so nothing is added: the
    /// emitter returns <c>META_S_DUPLICATE</c>, for which <c>TypeBuilder_DefineType</c> raises
    /// <c>ArgumentException</c>.
    /// </summary>
    | Duplicate of TypeDefRid

/// <summary>
/// The outcome of asking CoreCLR's emitter to define a parameter in a dynamic module's scope
/// (<c>RegMeta::DefineParam</c>).
/// </summary>
[<RequireQualifiedAccess>]
type ParamDefinition =
    /// A new <c>Param</c> row, and the scope that holds it.
    | Defined of ParamRid * EmitScope
    /// <summary>
    /// CoreCLR would add the row and, sliding it ahead of an earlier row of <c>slid</c>, start
    /// the <c>ParamPtr</c> table while every MethodDef row after it up to <c>last</c> still
    /// holds <c>END_OF_TABLE</c> as its <c>ParamList</c>. Real .NET then refuses or misreports those
    /// methods' parameters, which a scope does not model, so nothing is added (issue #2253).
    /// </summary>
    | StrandsLaterMethods of slid : MethodDefRid * last : MethodDefRid

/// The bit a pseudo-attribute sets in a method's row: in its <c>Flags</c> column, or in its <c>ImplFlags</c>.
[<RequireQualifiedAccess>]
type MethodDefFlag =
    | Flags of MethodAttributes
    | ImplFlags of MethodImplAttributes

/// <summary>
/// The bit a pseudo-attribute sets in its owner's row, for each table of owner the emitter accepts it on, and
/// <c>None</c> for each table the emitter refuses it on (<c>KnownCaProp.rTypes</c>, and the <c>AddFlags</c> calls of
/// <c>RegMeta::_HandleKnownCustomAttribute</c>, md/compiler/custattr_emit.cpp). The assembly, the module and a generic
/// parameter have no flags column, and no such attribute accepts them.
/// </summary>
type PseudoAttributeFlags =
    {
        TypeDef : TypeAttributes option
        MethodDef : MethodDefFlag option
        Field : FieldAttributes option
        /// A parameter's, the return parameter's included.
        Param : ParameterAttributes option
        Property : PropertyAttributes option
        Event : EventAttributes option
    }

/// <summary>
/// The constructor arguments that a pseudo-attribute the emitter validates and keeps declares (<c>KnownCaProp.pArgs</c>,
/// md/compiler/custattr_emit.cpp), each case with the rule <c>RegMeta::_HandleKnownCustomAttribute</c> checks of them
/// once the blob has parsed. The emitter reads each argument by its declared type, whatever the constructor's signature
/// says: an enum-typed constructor whose entry declares a <c>U2</c> has two bytes of its four read as the argument.
/// </summary>
[<RequireQualifiedAccess>]
type ValidatedPseudoAttributeArguments =
    /// One <c>U2</c> (two bytes, little-endian), below the bound.
    | U2Below of bound : uint16
    /// <summary>
    /// One string (<c>FF</c> for the null string, or a packed length and that many bytes) that is a GUID as
    /// <c>LPCSTRToGuid</c> reads it inside braces: exactly 36 bytes, <c>-</c> at offsets 8, 13, 18 and 23, and a
    /// hexadecimal digit of either case at every other.
    /// </summary>
    | GuidString
    /// Two <c>I4</c>s (four bytes each, little-endian), each 0 or more.
    | TwoNonNegativeI4s
    /// Four <c>I4</c>s, each 0 or more.
    | FourNonNegativeI4s
    /// No arguments, so the emitter never parses the blob, and keeps it whatever it holds.
    | NoArguments

/// <summary>
/// The arguments the emitter read from a blob it keeps, one case for each case of
/// <see cref="ValidatedPseudoAttributeArguments"/>.
/// </summary>
[<RequireQualifiedAccess>]
type ValidatedPseudoAttributeValues =
    | U2 of uint16
    /// The GUID's 36 bytes, as the blob holds them.
    | GuidString of ImmutableArray<byte>
    | TwoI4s of int * int
    | FourI4s of int * int * int * int
    | NoArguments

/// <summary>
/// What CoreCLR's emitter checks of a pseudo-attribute that it validates and then keeps as an ordinary row
/// (<c>bKEEPCA</c> in <c>KnownCaList</c>).
/// </summary>
type ValidatedPseudoAttribute =
    {
        /// <summary>
        /// The tables of owner the emitter accepts the attribute on (<c>KnownCaProp.rTypes</c>), but for
        /// <c>mdtTypeRef</c>, which no builder passes.
        /// </summary>
        Targets : Set<TableIndex>
        /// The constructor arguments the emitter reads, and the rule they must keep.
        Arguments : ValidatedPseudoAttributeArguments
    }

/// <summary>
/// Why CoreCLR's emitter refuses a pseudo-attribute (<c>RegMeta::_HandleKnownCustomAttribute</c>). For each,
/// <c>TypeBuilder_DefineCustomAttribute</c> throws <c>ArgumentException</c> "Invalid custom attribute provided.", and
/// the scope is unchanged.
/// </summary>
[<RequireQualifiedAccess>]
type KnownCustomAttributeRefusal =
    /// <summary>The owner's table is not among the attribute's targets (<c>META_E_CA_INVALID_TARGET</c>).</summary>
    | WrongTarget
    /// <summary>
    /// The blob does not parse by the attribute's declared arguments: its first two bytes are not the prolog
    /// <c>0x0001</c>, or an argument cannot be read from the bytes left.
    /// </summary>
    | MalformedBlob
    /// <summary>
    /// The blob declares a positive count of named arguments, which none of these attributes accepts
    /// (<c>ParseKnownCaNamedArgs</c>).
    /// </summary>
    | NamedArgument
    /// <summary>
    /// The arguments break the rule their <see cref="ValidatedPseudoAttributeArguments"/> case gives
    /// (<c>META_E_CA_INVALID_VALUE</c>, <c>META_E_CA_INVALID_UUID</c>).
    /// </summary>
    | InvalidValue

/// What CoreCLR's emitter does with a pseudo-attribute, as far as PawPrint models it.
[<RequireQualifiedAccess>]
type KnownCustomAttributeEffect =
    /// <summary>
    /// The attribute takes no arguments, so its blob is never read. On an owner whose table has a bit, the emitter ORs
    /// the bit into the owner's row and stores no row; on any other owner, it refuses the attribute.
    /// </summary>
    | SetsFlag of PseudoAttributeFlags
    /// <summary>
    /// On an owner among the targets, the emitter parses the blob by the declared arguments (when there are any) and
    /// checks the rule, then stores the row as it would an ordinary attribute's, its blob as given; it refuses the
    /// attribute on any other owner, or for a blob that fails either step.
    /// </summary>
    | ValidatesAndKeeps of ValidatedPseudoAttribute
    /// PawPrint does not model what the emitter does with the attribute, on any owner.
    | Unmodelled

/// <summary>
/// The bits of each flags column that some pseudo-attribute sets: for each table, the union of the bits that every
/// <c>SetsFlag</c> effect gives it. A method's <c>ImplFlags</c> are not among them: the emitter overwrites that column
/// whatever it holds (<c>RegMeta::SetMethodImplFlags</c>).
/// </summary>
type PseudoAttributeFlagMask =
    {
        TypeDef : TypeAttributes
        MethodDef : MethodAttributes
        Field : FieldAttributes
        Param : ParameterAttributes
        Property : PropertyAttributes
        Event : EventAttributes
    }

/// How CoreCLR's emitter reads and checks the blob of a pseudo-attribute that it validates and keeps.
[<RequireQualifiedAccess>]
module KnownCustomAttributeBlob =
    /// <summary>
    /// What <c>RegMeta::_HandleKnownCustomAttribute</c> does with <paramref name="blob"/>, for an attribute declaring
    /// <paramref name="arguments"/> on one of its targets: for an attribute with arguments, it reads them as
    /// <c>ParseKnownCaArgs</c> and <c>ParseKnownCaNamedArgs</c> do (md/compiler/custattr_emit.cpp), the prolog
    /// <c>0x0001</c>, each argument in turn, then, if any bytes are left, a signed two-byte count of named arguments, of
    /// which these attributes declare none: a count that one lone byte cannot hold is 0, a count above 0 is refused, and
    /// nothing after the count is read. Then it checks the arguments' rule. An attribute without arguments is never
    /// parsed. The arguments read, or why the emitter refuses the blob (never <c>WrongTarget</c>).
    /// </summary>
    let validate
        (arguments : ValidatedPseudoAttributeArguments)
        (blob : ImmutableArray<byte>)
        : Result<ValidatedPseudoAttributeValues, KnownCustomAttributeRefusal>
        =
        let length = blob.Length

        // `CPackedLen::SafeGetLength` (utilcode/stgpooli.cpp): each of the three forms is taken whatever value it
        // holds, shortest or not, if its bytes are there; any other first byte fails. The length, and the offset after
        // it.
        let packedLength (offset : int) : (int * int) option =
            let available = length - offset

            if available < 1 then
                None
            else
                let first = int blob.[offset]

                if first &&& 0x80 = 0 then
                    Some (first, offset + 1)
                elif available < 2 then
                    None
                elif first &&& 0xC0 = 0x80 then
                    Some (((first &&& 0x3F) <<< 8) ||| int blob.[offset + 1], offset + 2)
                elif available < 4 then
                    None
                elif first &&& 0xE0 = 0xC0 then
                    Some (
                        ((first &&& 0x1F) <<< 24)
                        ||| (int blob.[offset + 1] <<< 16)
                        ||| (int blob.[offset + 2] <<< 8)
                        ||| int blob.[offset + 3],
                        offset + 4
                    )
                else
                    None

        let u2At (offset : int) : uint16 =
            uint16 blob.[offset] ||| (uint16 blob.[offset + 1] <<< 8)

        // `ParseKnownCaValue` for each declared type: the argument at `offset`, and the offset after it.
        let readU2 (offset : int) : (uint16 * int) option =
            if length - offset < 2 then
                None
            else
                Some (u2At offset, offset + 2)

        let readI4 (offset : int) : (int * int) option =
            if length - offset < 4 then
                None
            else
                Some (int (u2At offset) ||| (int (u2At (offset + 2)) <<< 16), offset + 4)

        // `CustomAttributeParser::GetString`: one byte is needed for the null string's sentinel. `None` inside for the
        // null string.
        let readString (offset : int) : (ImmutableArray<byte> option * int) option =
            if offset >= length then
                None
            elif blob.[offset] = 0xFFuy then
                Some (None, offset + 1)
            else
                match packedLength offset with
                | Some (count, start) when count <= length - start ->
                    Some (Some (ImmutableArray.Create (blob, start, count)), start + count)
                | Some _
                | None -> None

        // `ParseKnownCaNamedArgs`, reached only while bytes are left after the arguments at `offset`:
        // `INT16 cActualArgs`, 0 when it cannot be read, and each named argument fails to match, there being none to
        // match.
        let namedArgumentsAt (offset : int) : Result<unit, KnownCustomAttributeRefusal> =
            let count = if length - offset < 2 then 0s else int16 (u2At offset)

            if count > 0s then
                Error KnownCustomAttributeRefusal.NamedArgument
            else
                Ok ()

        let isHexDigit (b : byte) : bool =
            (b >= byte '0' && b <= byte '9')
            || (b >= byte 'A' && b <= byte 'F')
            || (b >= byte 'a' && b <= byte 'f')

        // The arguments, read from the end of the prolog, then the named-argument count after them, then the rule.
        let read
            (readArguments : int -> ('values * int) option)
            (keeps : 'values -> ValidatedPseudoAttributeValues option)
            : Result<ValidatedPseudoAttributeValues, KnownCustomAttributeRefusal>
            =
            // `CustomAttributeParser::ValidateProlog`.
            if length < 2 || u2At 0 <> 1us then
                Error KnownCustomAttributeRefusal.MalformedBlob
            else
                match readArguments 2 with
                | None -> Error KnownCustomAttributeRefusal.MalformedBlob
                | Some (values, offset) ->
                    namedArgumentsAt offset
                    |> Result.bind (fun () ->
                        match keeps values with
                        | Some kept -> Ok kept
                        | None -> Error KnownCustomAttributeRefusal.InvalidValue
                    )

        let i4s (count : int) (offset : int) : (int list * int) option =
            (Some ([], offset), [ 1..count ])
            ||> List.fold (fun read _ ->
                read
                |> Option.bind (fun (values, offset) ->
                    readI4 offset |> Option.map (fun (value, offset) -> values @ [ value ], offset)
                )
            )

        match arguments with
        | ValidatedPseudoAttributeArguments.U2Below bound ->
            read
                readU2
                (fun value ->
                    if value < bound then
                        Some (ValidatedPseudoAttributeValues.U2 value)
                    else
                        None
                )
        | ValidatedPseudoAttributeArguments.GuidString ->
            read
                readString
                (fun text ->
                    match text with
                    // `cbStr == 36`, which the null string's 0 is not.
                    | None -> None
                    | Some text ->
                        let isGuid =
                            text.Length = 36
                            && text
                               |> Seq.indexed
                               |> Seq.forall (fun (i, b) ->
                                   match i with
                                   | 8
                                   | 13
                                   | 18
                                   | 23 -> b = byte '-'
                                   | _ -> isHexDigit b
                               )

                        if isGuid then
                            Some (ValidatedPseudoAttributeValues.GuidString text)
                        else
                            None
                )
        | ValidatedPseudoAttributeArguments.TwoNonNegativeI4s ->
            read
                (i4s 2)
                (fun values ->
                    match values with
                    | [ a ; b ] when a >= 0 && b >= 0 -> Some (ValidatedPseudoAttributeValues.TwoI4s (a, b))
                    | _ -> None
                )
        | ValidatedPseudoAttributeArguments.FourNonNegativeI4s ->
            read
                (i4s 4)
                (fun values ->
                    match values with
                    | [ a ; b ; c ; d ] when a >= 0 && b >= 0 && c >= 0 && d >= 0 ->
                        Some (ValidatedPseudoAttributeValues.FourI4s (a, b, c, d))
                    | _ -> None
                )
        // `if (props->cArgs || props->cNamedArgs)`: not parsed.
        | ValidatedPseudoAttributeArguments.NoArguments -> Ok ValidatedPseudoAttributeValues.NoArguments

/// <summary>
/// The pseudo-attributes of CoreCLR's emitter (<c>KnownCaList</c>, md/compiler/custattr_emit.cpp): each name the
/// emitter recognises, by namespace and name alone, as an attribute it turns into flags or rows of other tables, or
/// validates, rather than storing a row as given.
/// </summary>
[<RequireQualifiedAccess>]
type KnownCustomAttribute =
    | DllImport
    | Guid
    | ComImport
    | InterfaceType
    | ClassInterface
    | Serializable
    | NonSerialized
    | MethodImpl
    | MarshalAs
    | PreserveSig
    | In
    | Out
    | Optional
    | StructLayout
    | FieldOffset
    | TypeLibVersion
    | ComCompatibleVersion
    | SpecialName
    | AllowPartiallyTrustedCallers
    | WindowsRuntimeImport

/// The names of <see cref="KnownCustomAttribute"/>'s cases.
[<RequireQualifiedAccess>]
module KnownCustomAttribute =
    /// Every case, once each.
    let all : KnownCustomAttribute list =
        [
            KnownCustomAttribute.DllImport
            KnownCustomAttribute.Guid
            KnownCustomAttribute.ComImport
            KnownCustomAttribute.InterfaceType
            KnownCustomAttribute.ClassInterface
            KnownCustomAttribute.Serializable
            KnownCustomAttribute.NonSerialized
            KnownCustomAttribute.MethodImpl
            KnownCustomAttribute.MarshalAs
            KnownCustomAttribute.PreserveSig
            KnownCustomAttribute.In
            KnownCustomAttribute.Out
            KnownCustomAttribute.Optional
            KnownCustomAttribute.StructLayout
            KnownCustomAttribute.FieldOffset
            KnownCustomAttribute.TypeLibVersion
            KnownCustomAttribute.ComCompatibleVersion
            KnownCustomAttribute.SpecialName
            KnownCustomAttribute.AllowPartiallyTrustedCallers
            KnownCustomAttribute.WindowsRuntimeImport
        ]

    /// The namespace and the name of the attribute type, as <c>KnownCaList</c>'s entries spell them.
    let name (known : KnownCustomAttribute) : string * string =
        let interop = "System.Runtime.InteropServices"
        let compilerServices = "System.Runtime.CompilerServices"

        match known with
        | KnownCustomAttribute.DllImport -> interop, "DllImportAttribute"
        | KnownCustomAttribute.Guid -> interop, "GuidAttribute"
        | KnownCustomAttribute.ComImport -> interop, "ComImportAttribute"
        | KnownCustomAttribute.InterfaceType -> interop, "InterfaceTypeAttribute"
        | KnownCustomAttribute.ClassInterface -> interop, "ClassInterfaceAttribute"
        | KnownCustomAttribute.Serializable -> "System", "SerializableAttribute"
        | KnownCustomAttribute.NonSerialized -> "System", "NonSerializedAttribute"
        | KnownCustomAttribute.MethodImpl -> compilerServices, "MethodImplAttribute"
        | KnownCustomAttribute.MarshalAs -> interop, "MarshalAsAttribute"
        | KnownCustomAttribute.PreserveSig -> interop, "PreserveSigAttribute"
        | KnownCustomAttribute.In -> interop, "InAttribute"
        | KnownCustomAttribute.Out -> interop, "OutAttribute"
        | KnownCustomAttribute.Optional -> interop, "OptionalAttribute"
        | KnownCustomAttribute.StructLayout -> interop, "StructLayoutAttribute"
        | KnownCustomAttribute.FieldOffset -> interop, "FieldOffsetAttribute"
        | KnownCustomAttribute.TypeLibVersion -> interop, "TypeLibVersionAttribute"
        | KnownCustomAttribute.ComCompatibleVersion -> interop, "ComCompatibleVersionAttribute"
        | KnownCustomAttribute.SpecialName -> compilerServices, "SpecialNameAttribute"
        | KnownCustomAttribute.AllowPartiallyTrustedCallers ->
            "System.Security", "AllowPartiallyTrustedCallersAttribute"
        | KnownCustomAttribute.WindowsRuntimeImport ->
            "System.Runtime.InteropServices.WindowsRuntime", "WindowsRuntimeImportAttribute"

    /// <summary>
    /// What the emitter does with the attribute, as far as PawPrint models it: for each name, its target tables and its
    /// effect in <c>RegMeta::_HandleKnownCustomAttribute</c> (md/compiler/custattr_emit.cpp).
    /// </summary>
    let effect (known : KnownCustomAttribute) : KnownCustomAttributeEffect =
        let none : PseudoAttributeFlags =
            {
                TypeDef = None
                MethodDef = None
                Field = None
                Param = None
                Property = None
                Event = None
            }

        match known with
        | KnownCustomAttribute.Serializable ->
            KnownCustomAttributeEffect.SetsFlag
                { none with
                    // `tdSerializable`; `TypeAttributes.Serializable` is obsolete.
                    TypeDef = Some (enum<TypeAttributes> 0x2000)
                }
        | KnownCustomAttribute.NonSerialized ->
            KnownCustomAttributeEffect.SetsFlag
                { none with
                    // `fdNotSerialized`; `FieldAttributes.NotSerialized` is obsolete.
                    Field = Some (enum<FieldAttributes> 0x80)
                }
        | KnownCustomAttribute.SpecialName ->
            KnownCustomAttributeEffect.SetsFlag
                { none with
                    TypeDef = Some TypeAttributes.SpecialName
                    MethodDef = Some (MethodDefFlag.Flags MethodAttributes.SpecialName)
                    Field = Some FieldAttributes.SpecialName
                    Property = Some PropertyAttributes.SpecialName
                    Event = Some EventAttributes.SpecialName
                }
        | KnownCustomAttribute.In ->
            KnownCustomAttributeEffect.SetsFlag
                { none with
                    Param = Some ParameterAttributes.In
                }
        | KnownCustomAttribute.Out ->
            KnownCustomAttributeEffect.SetsFlag
                { none with
                    Param = Some ParameterAttributes.Out
                }
        | KnownCustomAttribute.Optional ->
            KnownCustomAttributeEffect.SetsFlag
                { none with
                    Param = Some ParameterAttributes.Optional
                }
        | KnownCustomAttribute.PreserveSig ->
            KnownCustomAttributeEffect.SetsFlag
                { none with
                    MethodDef = Some (MethodDefFlag.ImplFlags MethodImplAttributes.PreserveSig)
                }
        | KnownCustomAttribute.Guid ->
            KnownCustomAttributeEffect.ValidatesAndKeeps
                {
                    Targets = Set.ofList [ TableIndex.TypeDef ; TableIndex.Module ; TableIndex.Assembly ]
                    Arguments = ValidatedPseudoAttributeArguments.GuidString
                }
        | KnownCustomAttribute.InterfaceType ->
            KnownCustomAttributeEffect.ValidatesAndKeeps
                {
                    Targets = Set.singleton TableIndex.TypeDef
                    // `ifLast` (inc/metadata.h).
                    Arguments = ValidatedPseudoAttributeArguments.U2Below 4us
                }
        | KnownCustomAttribute.ClassInterface ->
            KnownCustomAttributeEffect.ValidatesAndKeeps
                {
                    Targets = Set.ofList [ TableIndex.TypeDef ; TableIndex.Assembly ]
                    // `clsIfLast` (inc/metadata.h).
                    Arguments = ValidatedPseudoAttributeArguments.U2Below 3us
                }
        | KnownCustomAttribute.TypeLibVersion ->
            KnownCustomAttributeEffect.ValidatesAndKeeps
                {
                    Targets = Set.singleton TableIndex.Assembly
                    Arguments = ValidatedPseudoAttributeArguments.TwoNonNegativeI4s
                }
        | KnownCustomAttribute.ComCompatibleVersion ->
            KnownCustomAttributeEffect.ValidatesAndKeeps
                {
                    Targets = Set.singleton TableIndex.Assembly
                    Arguments = ValidatedPseudoAttributeArguments.FourNonNegativeI4s
                }
        | KnownCustomAttribute.AllowPartiallyTrustedCallers ->
            KnownCustomAttributeEffect.ValidatesAndKeeps
                {
                    Targets = Set.singleton TableIndex.Assembly
                    Arguments = ValidatedPseudoAttributeArguments.NoArguments
                }
        | KnownCustomAttribute.DllImport
        | KnownCustomAttribute.ComImport
        | KnownCustomAttribute.MethodImpl
        | KnownCustomAttribute.MarshalAs
        | KnownCustomAttribute.StructLayout
        | KnownCustomAttribute.FieldOffset
        | KnownCustomAttribute.WindowsRuntimeImport -> KnownCustomAttributeEffect.Unmodelled

    /// The bits of each flags column that <c>effect</c> gives some attribute.
    let flagMask : PseudoAttributeFlagMask =
        let orSome (bit : 'a option) (mask : 'a) (combine : 'a -> 'a -> 'a) : 'a =
            match bit with
            | Some bit -> combine mask bit
            | None -> mask

        let empty : PseudoAttributeFlagMask =
            {
                TypeDef = enum<TypeAttributes> 0
                MethodDef = enum<MethodAttributes> 0
                Field = enum<FieldAttributes> 0
                Param = enum<ParameterAttributes> 0
                Property = enum<PropertyAttributes> 0
                Event = enum<EventAttributes> 0
            }

        (empty, all)
        ||> List.fold (fun mask known ->
            match effect known with
            | KnownCustomAttributeEffect.ValidatesAndKeeps _
            | KnownCustomAttributeEffect.Unmodelled -> mask
            | KnownCustomAttributeEffect.SetsFlag flags ->
                let methodFlags =
                    match flags.MethodDef with
                    | Some (MethodDefFlag.Flags bit) -> Some bit
                    | Some (MethodDefFlag.ImplFlags _)
                    | None -> None

                {
                    TypeDef = orSome flags.TypeDef mask.TypeDef (|||)
                    MethodDef = orSome methodFlags mask.MethodDef (|||)
                    Field = orSome flags.Field mask.Field (|||)
                    Param = orSome flags.Param mask.Param (|||)
                    Property = orSome flags.Property mask.Property (|||)
                    Event = orSome flags.Event mask.Event (|||)
                }
        )

/// <summary>
/// What CoreCLR's emitter does with an attribute, by its constructor's declaring type and the owner
/// (<c>RegMeta::DefineCustomAttribute</c>).
/// </summary>
[<RequireQualifiedAccess>]
type CustomAttributeEmitKind =
    /// Stored as a row, its blob as given.
    | Ordinary
    /// A pseudo-attribute, on any owner.
    | Known of KnownCustomAttribute
    /// <summary>
    /// <c>System.Security.SuppressUnmanagedCodeSecurityAttribute</c> on a type or a method, which sets the owner's
    /// <c>HasSecurity</c> flag and is stored as a row.
    /// </summary>
    | SuppressUnmanagedCodeSecurity
    /// <summary>
    /// <c>System.Security.DynamicSecurityMethodAttribute</c> on a method, which sets the method's
    /// <c>RequireSecObject</c> flag and stores no row.
    /// </summary>
    | DynamicSecurityMethod

/// <summary>
/// The outcome of asking CoreCLR's emitter to define a custom attribute in a dynamic module's scope
/// (<c>RegMeta::DefineCustomAttribute</c>).
/// </summary>
[<RequireQualifiedAccess>]
type CustomAttributeDefinition =
    /// A new <c>CustomAttribute</c> row, and the scope that holds it.
    | Defined of CustomAttributeRid * EmitScope
    /// A pseudo-attribute's flag ORed into its owner's row, and the scope that holds it. No row is stored.
    | FlagSet of EmitScope
    /// A pseudo-attribute the emitter refuses. Nothing is added.
    | Refused of KnownCustomAttributeRefusal
    /// <summary>
    /// CoreCLR would not store the row as given, but would do what this kind says instead, which a scope does not
    /// model, so nothing is added.
    /// </summary>
    | NotStoredAsGiven of CustomAttributeEmitKind

/// <summary>
/// A generic parameter whose variance PawPrint does not model when its type is created.
/// </summary>
[<RequireQualifiedAccess>]
type UnmodelledVariance =
    /// Both variance bits are set, which names no variance.
    | BothVarianceBits of GenericParamRid
    /// <summary>
    /// The parameter is covariant or contravariant, and its type is not an interface: CoreCLR raises
    /// <c>TypeLoadException</c> from <c>CreateType</c> for a class, and loads a delegate.
    /// </summary>
    | VariantOfNonInterface of GenericParamRid
    /// <summary>
    /// The parameter is covariant or contravariant, and belongs to a method of the type: CoreCLR raises
    /// <c>TypeLoadException</c> from <c>CreateType</c> for it, of an interface's method too.
    /// </summary>
    | VariantOfMethod of GenericParamRid * MethodDefRid

/// What an exception clause's handler is (ECMA-335 II.19), as <c>TypeBuilder_SetMethodIL</c> writes the
/// clause's flags and its class token or filter offset.
[<RequireQualifiedAccess>]
type ExceptionClauseHandler =
    /// Catches exceptions assignable to the type this token names.
    | Catch of classToken : int
    /// Runs the filter that starts at this offset to decide whether to handle the exception.
    | Filter of filterOffset : int
    /// Runs however the try block is left. CoreCLR writes its class token as <c>mdTypeRefNil</c>.
    | Finally
    /// Runs when the try block is left by an exception. CoreCLR writes the class token the managed
    /// caller passes, which is 0.
    | Fault of classToken : int

/// An exception clause of a method body: its handler, and the try block and handler it covers, each
/// as an offset into the code and a length.
type ExceptionClause =
    {
        Handler : ExceptionClauseHandler
        TryOffset : int
        TryLength : int
        HandlerOffset : int
        HandlerLength : int
    }

/// <summary>
/// A value <c>TypeBuilder_SetConstantValue</c> is given: one case for each of the fourteen element types its managed
/// caller (<c>RuntimeTypeBuilder.SetConstantValue</c>) passes. A fixed-width case holds the value's raw bits.
/// </summary>
[<RequireQualifiedAccess>]
type ConstantValue =
    /// The boxed <c>bool</c>'s byte, as the managed caller copies it.
    | Boolean of byte
    | Char of char
    | SByte of sbyte
    | Byte of byte
    | Int16 of int16
    | UInt16 of uint16
    | Int32 of int32
    | UInt32 of uint32
    | Int64 of int64
    | UInt64 of uint64
    /// The <c>float</c>'s bits, so that every NaN payload and <c>-0.0</c> are kept.
    | Single of bits : int32
    /// The <c>double</c>'s bits, so that every NaN payload and <c>-0.0</c> are kept.
    | Double of bits : int64
    /// UTF-16 code units, lone surrogates and NULs included.
    | String of string
    /// A null reference, of any type: <c>ELEMENT_TYPE_CLASS</c>.
    | NullReference

/// <summary>
/// The element type and the value blob of a <c>ConstantValue</c>, as CoreCLR's emitter stores them
/// (<c>_GetSizeOfConstantBlob</c>, md/compiler/mdutil.cpp).
/// </summary>
[<RequireQualifiedAccess>]
module ConstantValue =
    /// The element type the row holds for the value.
    let typeCode (value : ConstantValue) : System.Reflection.Metadata.ConstantTypeCode =
        match value with
        | ConstantValue.Boolean _ -> System.Reflection.Metadata.ConstantTypeCode.Boolean
        | ConstantValue.Char _ -> System.Reflection.Metadata.ConstantTypeCode.Char
        | ConstantValue.SByte _ -> System.Reflection.Metadata.ConstantTypeCode.SByte
        | ConstantValue.Byte _ -> System.Reflection.Metadata.ConstantTypeCode.Byte
        | ConstantValue.Int16 _ -> System.Reflection.Metadata.ConstantTypeCode.Int16
        | ConstantValue.UInt16 _ -> System.Reflection.Metadata.ConstantTypeCode.UInt16
        | ConstantValue.Int32 _ -> System.Reflection.Metadata.ConstantTypeCode.Int32
        | ConstantValue.UInt32 _ -> System.Reflection.Metadata.ConstantTypeCode.UInt32
        | ConstantValue.Int64 _ -> System.Reflection.Metadata.ConstantTypeCode.Int64
        | ConstantValue.UInt64 _ -> System.Reflection.Metadata.ConstantTypeCode.UInt64
        | ConstantValue.Single _ -> System.Reflection.Metadata.ConstantTypeCode.Single
        | ConstantValue.Double _ -> System.Reflection.Metadata.ConstantTypeCode.Double
        | ConstantValue.String _ -> System.Reflection.Metadata.ConstantTypeCode.String
        | ConstantValue.NullReference -> System.Reflection.Metadata.ConstantTypeCode.NullReference

    /// <summary>
    /// The value blob: a fixed-width value's little-endian bytes, two bytes per code unit for a string, and four zero
    /// bytes for <c>NullReference</c>.
    /// </summary>
    let blob (value : ConstantValue) : ImmutableArray<byte> =
        // Little-endian whatever the host, as the metadata's blobs are.
        let littleEndian (bits : uint64) (size : int) : ImmutableArray<byte> =
            ImmutableArray.CreateRange (Seq.init size (fun i -> byte (bits >>> (8 * i))))

        match value with
        | ConstantValue.Boolean b -> ImmutableArray.Create<byte> b
        | ConstantValue.Char c -> littleEndian (uint64 c) 2
        | ConstantValue.SByte v -> littleEndian (uint64 (byte v)) 1
        | ConstantValue.Byte v -> littleEndian (uint64 v) 1
        | ConstantValue.Int16 v -> littleEndian (uint64 (uint16 v)) 2
        | ConstantValue.UInt16 v -> littleEndian (uint64 v) 2
        | ConstantValue.Int32 v -> littleEndian (uint64 (uint32 v)) 4
        | ConstantValue.UInt32 v -> littleEndian (uint64 v) 4
        | ConstantValue.Int64 v -> littleEndian (uint64 v) 8
        | ConstantValue.UInt64 v -> littleEndian v 8
        | ConstantValue.Single bits -> littleEndian (uint64 (uint32 bits)) 4
        | ConstantValue.Double bits -> littleEndian (uint64 bits) 8
        | ConstantValue.String s ->
            // Code unit by code unit, so that a lone surrogate is kept, as `Encoding.Unicode` would not keep it.
            ImmutableArray.CreateRange (s |> Seq.collect (fun c -> [ byte (int c &&& 0xFF) ; byte (int c >>> 8) ]))
        | ConstantValue.NullReference -> littleEndian 0UL 4

    /// <summary>
    /// The size of a fixed-width element type's value: 1, 2, 4 or 8 for each of the twelve primitive element types, and
    /// <c>None</c> for <c>String</c>, <c>NullReference</c> and any other.
    /// </summary>
    let fixedWidthSize (code : System.Reflection.Metadata.ConstantTypeCode) : int option =
        match code with
        | System.Reflection.Metadata.ConstantTypeCode.Boolean
        | System.Reflection.Metadata.ConstantTypeCode.SByte
        | System.Reflection.Metadata.ConstantTypeCode.Byte -> Some 1
        | System.Reflection.Metadata.ConstantTypeCode.Char
        | System.Reflection.Metadata.ConstantTypeCode.Int16
        | System.Reflection.Metadata.ConstantTypeCode.UInt16 -> Some 2
        | System.Reflection.Metadata.ConstantTypeCode.Int32
        | System.Reflection.Metadata.ConstantTypeCode.UInt32
        | System.Reflection.Metadata.ConstantTypeCode.Single -> Some 4
        | System.Reflection.Metadata.ConstantTypeCode.Int64
        | System.Reflection.Metadata.ConstantTypeCode.UInt64
        | System.Reflection.Metadata.ConstantTypeCode.Double -> Some 8
        | _ -> None

    /// <summary>
    /// The value of fixed-width element type <paramref name="code"/> whose little-endian bytes are
    /// <paramref name="bytes"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is not fixed-width, or <paramref name="bytes"/> is not its <c>fixedWidthSize</c>.
    /// </exception>
    let ofFixedWidth
        (code : System.Reflection.Metadata.ConstantTypeCode)
        (bytes : ImmutableArray<byte>)
        : ConstantValue
        =
        match fixedWidthSize code with
        | None -> invalidArg (nameof code) $"ConstantValue.ofFixedWidth: %O{code} is not a fixed-width element type"
        | Some size when size <> bytes.Length ->
            invalidArg
                (nameof bytes)
                $"ConstantValue.ofFixedWidth: %O{code} is %d{size} bytes wide, but %d{bytes.Length} bytes were given"
        | Some _ ->

        let bits =
            Seq.foldBack (fun (b : byte) (acc : uint64) -> (acc <<< 8) ||| uint64 b) bytes 0UL

        match code with
        | System.Reflection.Metadata.ConstantTypeCode.Boolean -> ConstantValue.Boolean (byte bits)
        | System.Reflection.Metadata.ConstantTypeCode.SByte -> ConstantValue.SByte (sbyte bits)
        | System.Reflection.Metadata.ConstantTypeCode.Byte -> ConstantValue.Byte (byte bits)
        | System.Reflection.Metadata.ConstantTypeCode.Char -> ConstantValue.Char (char (uint16 bits))
        | System.Reflection.Metadata.ConstantTypeCode.Int16 -> ConstantValue.Int16 (int16 bits)
        | System.Reflection.Metadata.ConstantTypeCode.UInt16 -> ConstantValue.UInt16 (uint16 bits)
        | System.Reflection.Metadata.ConstantTypeCode.Int32 -> ConstantValue.Int32 (int32 bits)
        | System.Reflection.Metadata.ConstantTypeCode.UInt32 -> ConstantValue.UInt32 (uint32 bits)
        | System.Reflection.Metadata.ConstantTypeCode.Single -> ConstantValue.Single (int32 bits)
        | System.Reflection.Metadata.ConstantTypeCode.Int64 -> ConstantValue.Int64 (int64 bits)
        | System.Reflection.Metadata.ConstantTypeCode.UInt64 -> ConstantValue.UInt64 bits
        | System.Reflection.Metadata.ConstantTypeCode.Double -> ConstantValue.Double (int64 bits)
        | other -> failwith $"BUG: ConstantValue.ofFixedWidth: fixedWidthSize gave %O{other} a width"

/// <summary>
/// The outcome of asking CoreCLR's emitter to set an owner's constant in a dynamic module's scope
/// (<c>RegMeta::SetFieldProps</c>, <c>SetParamProps</c> or <c>SetPropertyProps</c>).
/// </summary>
[<RequireQualifiedAccess>]
type ConstantDefinition =
    /// The owner's <c>Constant</c> row set and its <c>HasDefault</c> flag ORed in, and the scope that holds them.
    | Defined of EmitScope
    /// <summary>
    /// The owner is a property with a <c>MethodSemantics</c> row whose semantics is exactly <c>Getter</c> or exactly
    /// <c>Setter</c>. CoreCLR's <c>_SetPropertyProps</c> detaches each such row from the property, which a scope does
    /// not model, so nothing is changed.
    /// </summary>
    | DetachesAccessors

/// <summary>
/// The definitions CoreCLR's emitter adds to a dynamic module's scope: types, each checked for a
/// duplicate first (<c>MDDupTypeDef</c>), and methods, which are not (<c>MDDupMethodDef</c> is not set).
/// </summary>
[<RequireQualifiedAccess>]
module EmitScopeDefinitions =
    /// `tdReservedMask`: `tdRTSpecialName` and `tdHasSecurity`, which only the emitter sets.
    let private reservedFlags : TypeAttributes = enum<TypeAttributes> 0x00040800

    /// `mdReservedMask`: `mdRTSpecialName`, `mdHasSecurity` and `mdRequireSecObject`, which only the
    /// emitter sets.
    let private reservedMethodFlags : MethodAttributes = enum<MethodAttributes> 0xd000

    /// <summary>
    /// The top-level <c>TypeDef</c> row with this namespace and name, compared ordinally, as CoreCLR's
    /// <c>ImportHelper::FindTypeDefByName</c> finds it with no enclosing type: the first row whose
    /// visibility is not nested.
    /// </summary>
    let tryFindTopLevelTypeDef (ns : string) (name : string) (scope : EmitScope) : TypeDefRid option =
        // Equal strings are one heap entry, so a row matches exactly when it holds these offsets.
        match EmitScope.tryStringOffset ns scope, EmitScope.tryStringOffset name scope with
        | Some ns, Some name ->
            EmitScope.typeDefs scope
            |> Seq.tryFindIndex (fun row ->
                // `IsTdNested`: a row of any nested visibility is not a top-level type.
                (row.Flags &&& TypeAttributes.VisibilityMask) < TypeAttributes.NestedPublic
                && row.Name = name
                && row.Namespace = ns
            )
            |> Option.map (fun index -> TypeDefRid (index + 1))
        | _ -> None

    /// <summary>
    /// Define a type that no other type encloses, as <c>RegMeta::DefineTypeDef</c> does: the full name
    /// is divided by <c>EmitScopeReferences.splitTypeName</c>, and if <c>tryFindTopLevelTypeDef</c>
    /// finds a row with that namespace and name, nothing is added. Otherwise a new row holds the
    /// name, then the namespace, interned in that order; the flags without the reserved bits
    /// (<c>tdReservedMask</c>), which the emitter keeps for itself; and the parent, if there is one.
    /// One <c>InterfaceImpl</c> row follows for each of <paramref name="interfaces"/>, in order, a
    /// repeated interface included.
    /// </summary>
    let defineTopLevelTypeDef
        (fullName : string)
        (flags : TypeAttributes)
        (extends : TypeDefOrRef option)
        (interfaces : TypeDefOrRef list)
        (scope : EmitScope)
        : TypeDefDefinition
        =
        let ns, name = EmitScopeReferences.splitTypeName fullName

        match tryFindTopLevelTypeDef ns name scope with
        | Some existing -> TypeDefDefinition.Duplicate existing
        | None ->
            let name, scope = EmitScope.internString name scope
            let ns, scope = EmitScope.internString ns scope

            let typeDef, scope =
                EmitScope.addTypeDef
                    {
                        Flags = flags &&& ~~~reservedFlags
                        Name = name
                        Namespace = ns
                        Extends = extends
                    }
                    scope

            let scope =
                (scope, interfaces)
                ||> List.fold (fun scope target ->
                    EmitScope.addInterfaceImpl
                        {
                            Class = typeDef
                            Interface = target
                        }
                        scope
                )

            TypeDefDefinition.Defined (typeDef, scope)

    /// <summary>
    /// Define a method of <paramref name="owner"/>, as <c>RegMeta::DefineMethod</c> does in a dynamic
    /// module, whose emitter does not check for a duplicate <c>MethodDef</c> (<c>MDDupMethodDef</c>
    /// is not set): a new row, at the next RID, however many of its type's methods share its name and
    /// signature.
    /// </summary>
    /// <remarks>
    /// The row keeps the low 16 bits of <paramref name="flags"/>, the column's width, without the
    /// reserved bits (<c>mdReservedMask</c>), which the emitter keeps for itself. A method named
    /// <c>.ctor</c> or <c>.cctor</c>, or whose name starts with <c>_VtblGap</c>, compared ordinally,
    /// gains <c>RTSpecialName</c> and <c>SpecialName</c>. The row has no body, and its implementation
    /// flags are <c>IL</c>, until later calls fill them in.
    /// </remarks>
    let defineMethod
        (owner : TypeDefRid)
        (name : string)
        (signature : ImmutableArray<byte>)
        (flags : MethodAttributes)
        (scope : EmitScope)
        : MethodDefRid * EmitScope
        =
        let special =
            name = ".ctor"
            || name = ".cctor"
            || name.StartsWith ("_VtblGap", StringComparison.Ordinal)

        let flags =
            (flags &&& ~~~reservedMethodFlags &&& enum<MethodAttributes> 0xFFFF)
            ||| (if special then
                     MethodAttributes.RTSpecialName ||| MethodAttributes.SpecialName
                 else
                     enum<MethodAttributes> 0)

        let name, scope = EmitScope.internString name scope
        let signature, scope = EmitScope.internBlob signature scope

        EmitScope.addMethodDef
            {
                Owner = owner
                Body = None
                ImplFlags = MethodImplAttributes.IL
                Flags = flags
                Name = name
                Signature = signature
            }
            scope

    /// <summary>
    /// Record that <paramref name="cls"/> implements <paramref name="target"/>, as
    /// <c>RegMeta::AddInterfaceImpl</c> does: unless the table already has a row with this class and
    /// interface (<c>ImportHelper::FindInterfaceImpl</c>), which it looks for whether or not the emitter
    /// checks for duplicates elsewhere, a new row. <c>DefineType</c>'s interfaces are added without
    /// that search.
    /// </summary>
    let addInterfaceImpl (cls : TypeDefRid) (target : TypeDefOrRef) (scope : EmitScope) : EmitScope =
        let existing =
            EmitScope.interfaceImpls scope
            |> Seq.exists (fun row -> row.Class = cls && row.Interface = target)

        if existing then
            scope
        else
            EmitScope.addInterfaceImpl
                {
                    Class = cls
                    Interface = target
                }
                scope

    /// `fdRTSpecialName`, `fdHasFieldMarshal` and `fdHasDefault`: the bits of `fdReservedMask` that
    /// `RegMeta::DefineField` clears from the flags it is given, keeping `fdHasFieldRVA`.
    let private reservedFieldFlags : FieldAttributes = enum<FieldAttributes> 0x9400

    /// <summary>
    /// Define a field of <paramref name="owner"/>, as <c>RegMeta::DefineField</c> does in a dynamic
    /// module, whose emitter does not check for a duplicate <c>Field</c> (<c>MDDupFieldDef</c> is not
    /// set): a new row, at the next RID.
    /// </summary>
    /// <remarks>
    /// The row keeps the low 16 bits of <paramref name="flags"/>, the column's width, without
    /// <c>RTSpecialName</c>, <c>HasFieldMarshal</c> and <c>HasDefault</c>, which the emitter keeps for
    /// itself. A field named exactly <c>value__</c> gains <c>RTSpecialName</c> and <c>SpecialName</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="flags"/> has <c>HasFieldRVA</c>, for which the emitter adds a <c>FieldRVA</c>
    /// row, which a scope does not hold.
    /// </exception>
    let defineField
        (owner : TypeDefRid)
        (name : string)
        (signature : ImmutableArray<byte>)
        (flags : FieldAttributes)
        (scope : EmitScope)
        : FieldRid * EmitScope
        =
        if flags.HasFlag FieldAttributes.HasFieldRVA then
            invalidArg
                (nameof flags)
                $"EmitScopeDefinitions.defineField: %s{name} has HasFieldRVA, for which the emitter adds a FieldRVA row, which a scope does not hold"

        let flags =
            (flags &&& ~~~reservedFieldFlags &&& enum<FieldAttributes> 0xFFFF)
            ||| (if name = "value__" then
                     FieldAttributes.RTSpecialName ||| FieldAttributes.SpecialName
                 else
                     enum<FieldAttributes> 0)

        let name, scope = EmitScope.internString name scope
        let signature, scope = EmitScope.internBlob signature scope

        EmitScope.addField
            {
                Owner = owner
                Flags = flags
                Name = name
                Signature = signature
            }
            scope

    /// <summary>
    /// The body <c>TypeBuilder_SetMethodIL</c> writes: the header <c>COR_ILMETHOD::Size</c> picks, the
    /// code, and a section of the exception clauses if there are any. A tiny header (ECMA-335 II.25.4.2)
    /// when the method zeroes no locals, has no local signature, a max stack of at most 8, fewer than 64
    /// bytes of code and no clauses; otherwise a fat one (II.25.4.3) holding all four. The section
    /// (II.25.4.5) follows the code at the next 4-byte boundary, and is small, as
    /// <c>ExceptionHandlingSize</c> picks, when it holds at most 20 clauses whose offsets fit 16 bits and
    /// whose lengths fit 8; otherwise fat.
    /// </summary>
    let methodBody
        (initLocals : bool)
        (maxStack : uint16)
        (code : ImmutableArray<byte>)
        (localSignature : StandAloneSigRid option)
        (clauses : ExceptionClause list)
        : ImmutableArray<byte>
        =
        if code.IsDefault then
            invalidArg (nameof code) "a method body's code must be an initialised array"

        let builder = ImmutableArray.CreateBuilder<byte> ()

        let little (value : int) (width : int) =
            for i in 0 .. width - 1 do
                builder.Add (byte (value >>> (8 * i)))

        let moreSections = not clauses.IsEmpty

        if
            not initLocals
            && localSignature.IsNone
            && maxStack <= 8us
            && code.Length < 64
            && not moreSections
        then
            // `CorILMethod_TinyFormat`, with the code size in the top six bits.
            builder.Add (byte (0x02 ||| (code.Length <<< 2)))
        else
            // `CorILMethod_FatFormat`, `CorILMethod_InitLocals` if asked for, `CorILMethod_MoreSects`
            // if there are clauses, and the header's size of three 4-byte words in the top nibble.
            let flags =
                0x03
                ||| (if initLocals then 0x10 else 0)
                ||| (if moreSections then 0x08 else 0)
                ||| (3 <<< 12)

            let localSignatureToken =
                match localSignature with
                | Some rid -> 0x11000000 ||| rid.Rid
                | None -> 0

            little flags 2
            little (int maxStack) 2
            little code.Length 4
            little localSignatureToken 4

        builder.AddRange code

        if moreSections then
            // The header is 12 bytes, so aligning the code's end aligns the section.
            while builder.Count % 4 <> 0 do
                builder.Add 0uy

            let flagsAndClassToken (clause : ExceptionClause) : int * int =
                match clause.Handler with
                | ExceptionClauseHandler.Catch token -> 0, token
                | ExceptionClauseHandler.Filter offset -> 1, offset
                // `mdTypeRefNil`.
                | ExceptionClauseHandler.Finally -> 2, 0x01000000
                | ExceptionClauseHandler.Fault token -> 4, token

            let small =
                clauses.Length <= 20
                && clauses
                   |> List.forall (fun c ->
                       c.TryOffset <= 0xFFFF
                       && c.TryLength <= 0xFF
                       && c.HandlerOffset <= 0xFFFF
                       && c.HandlerLength <= 0xFF
                   )

            if small then
                // `CorILMethod_Sect_EHTable`, the data size in one byte, and two reserved bytes.
                builder.Add 0x01uy
                builder.Add (byte (4 + 12 * clauses.Length))
                little 0 2

                for clause in clauses do
                    let flags, classToken = flagsAndClassToken clause
                    little flags 2
                    little clause.TryOffset 2
                    little clause.TryLength 1
                    little clause.HandlerOffset 2
                    little clause.HandlerLength 1
                    little classToken 4
            else
                // `CorILMethod_Sect_EHTable` with `CorILMethod_Sect_FatFormat`, and the data size in
                // three bytes.
                builder.Add 0x41uy
                little (4 + 24 * clauses.Length) 3

                for clause in clauses do
                    let flags, classToken = flagsAndClassToken clause
                    little flags 4
                    little clause.TryOffset 4
                    little clause.TryLength 4
                    little clause.HandlerOffset 4
                    little clause.HandlerLength 4
                    little classToken 4

        builder.ToImmutable ()

    /// <summary>
    /// Define a generic parameter of <paramref name="owner"/>, as <c>RegMeta::DefineGenericParam</c> does
    /// in a dynamic module, whose emitter does not check for a duplicate (<c>MDDupGenericParam</c> is not
    /// set): a new row, at the next RID, holding the name, and the flags as
    /// <c>_SetGenericParamProps</c> stores them, 0 for <c>-1</c> and otherwise their low 16 bits; then one
    /// <c>GenericParamConstraint</c> row for each of <paramref name="constraints"/>, in that order,
    /// repeats included. The new row's RID is the token CoreCLR gives the parameter, in the order of
    /// creation, whatever owns the rows before it.
    /// </summary>
    let defineGenericParam
        (owner : TypeOrMethodDef)
        (number : uint16)
        (flags : int)
        (name : string)
        (constraints : TypeDefOrRef list)
        (scope : EmitScope)
        : GenericParamRid * EmitScope
        =
        // `_SetGenericParamProps` leaves an empty name unset, which reads back as interning it would.
        let name, scope = EmitScope.internString name scope

        let flags =
            if flags = -1 then
                enum<GenericParameterAttributes> 0
            else
                enum<GenericParameterAttributes> (flags &&& 0xFFFF)

        EmitScope.addGenericParam
            {
                Number = number
                Flags = flags
                Owner = owner
                Name = name
            }
            constraints
            scope

    /// <summary>
    /// The first generic parameter of <paramref name="typeDef"/> or of one of its methods, in RID order,
    /// whose variance PawPrint does not model when the type is created, if it has one.
    /// </summary>
    let unmodelledVariance (typeDef : TypeDefRid) (scope : EmitScope) : UnmodelledVariance option =
        let isInterface =
            match EmitScope.tryTypeDefRid typeDef.Rid scope with
            | Some _ -> (EmitScope.typeDefs scope).[typeDef.Rid - 1].Flags.HasFlag TypeAttributes.Interface
            | None ->
                invalidArg
                    (nameof typeDef)
                    $"EmitScopeDefinitions.unmodelledVariance: TypeDef %d{typeDef.Rid} is not in the scope"

        // The parameter's method, if its owner is a method of the type.
        let methodOfType (row : GenericParamRow) : MethodDefRid option =
            match row.Owner with
            | TypeOrMethodDef.MethodDef method when (EmitScope.methodDefs scope).[method.Rid - 1].Owner = typeDef ->
                Some method
            | TypeOrMethodDef.MethodDef _
            | TypeOrMethodDef.TypeDef _ -> None

        EmitScope.genericParams scope
        |> Seq.indexed
        |> Seq.tryPick (fun (index, row) ->
            let rid = GenericParamRid (index + 1)
            let method = methodOfType row

            if row.Owner <> TypeOrMethodDef.TypeDef typeDef && method.IsNone then
                None
            else
                match row.Flags &&& GenericParameterAttributes.VarianceMask, method with
                | GenericParameterAttributes.VarianceMask, _ -> Some (UnmodelledVariance.BothVarianceBits rid)
                | GenericParameterAttributes.None, _ -> None
                | _, Some method -> Some (UnmodelledVariance.VariantOfMethod (rid, method))
                | _, None when isInterface -> None
                | _, None -> Some (UnmodelledVariance.VariantOfNonInterface rid)
        )

    /// <summary>
    /// Set a method's implementation flags, as <c>RegMeta::SetMethodImplFlags</c> does: the low 16
    /// bits of <paramref name="flags"/>, the column's width, replacing whatever the row held.
    /// </summary>
    let setMethodImplFlags (method : MethodDefRid) (flags : MethodImplAttributes) (scope : EmitScope) : EmitScope =
        EmitScope.setMethodImplFlags method (flags &&& enum<MethodImplAttributes> 0xFFFF) scope

    /// `prReservedMask`: `prRTSpecialName`, `prHasDefault` and the unused bits, which only the emitter sets.
    let private reservedPropertyFlags : int = 0xF400

    /// <summary>
    /// Define a property of <paramref name="owner"/>, as <c>RegMeta::DefineProperty</c> does in a dynamic
    /// module, whose emitter does not check for a duplicate (<c>MDDupProperty</c> is not set): a new row, at
    /// the next RID, however many of the type's properties share its name and signature, holding the name
    /// and the signature as given.
    /// </summary>
    /// <remarks>
    /// The flags are as <c>_SetPropertyProps</c> stores them: 0 for <c>-1</c>, which leaves them unset;
    /// otherwise without <c>prReservedMask</c>, which the emitter keeps for itself, in the column's two
    /// bytes. An empty name is stored, as the QCall stores the part of a name before its first NUL.
    /// </remarks>
    let defineProperty
        (owner : TypeDefRid)
        (name : string)
        (signature : ImmutableArray<byte>)
        (flags : int)
        (scope : EmitScope)
        : PropertyRid * EmitScope
        =
        let flags =
            if flags = -1 then
                0
            else
                flags &&& ~~~reservedPropertyFlags &&& 0xFFFF

        let name, scope = EmitScope.internString name scope
        let signature, scope = EmitScope.internBlob signature scope

        EmitScope.addProperty
            {
                Owner = owner
                Flags = enum<PropertyAttributes> flags
                Name = name
                Signature = signature
            }
            scope

    /// <summary>
    /// Make <paramref name="method"/> an accessor of <paramref name="association"/>, as
    /// <c>RegMeta::_DefineMethodSemantics</c> does for <c>TypeBuilder_DefineMethodSemantics</c>: a new row
    /// every time, with no search, whatever type the method belongs to.
    /// </summary>
    let defineMethodSemantics
        (association : HasSemantics)
        (semantics : MethodSemanticsAttributes)
        (method : MethodDefRid)
        (scope : EmitScope)
        : EmitScope
        =
        EmitScope.addMethodSemantics
            {
                Semantics = semantics
                Method = method
                Association = association
            }
            scope

    /// `evReservedMask`: `evRTSpecialName`, which only the emitter sets.
    let private reservedEventFlags : int = 0x0400

    /// <summary>
    /// Define an event of <paramref name="owner"/>, as <c>RegMeta::_DefineEvent</c> does in a dynamic
    /// module, whose emitter does not check for a duplicate (<c>MDDupEvent</c> is not set): a new row, at
    /// the next RID, however many of the type's events share its name, holding the name and the event type
    /// as given.
    /// </summary>
    /// <remarks>
    /// The flags are as <c>_SetEventProps1</c> stores them: 0 for <c>-1</c>, which leaves them unset;
    /// otherwise without <c>evReservedMask</c> (<c>RTSpecialName</c>, which the emitter keeps for itself), in
    /// the column's two bytes. An empty name is stored, as <c>RegMeta</c> stores any name.
    /// </remarks>
    let defineEvent
        (owner : TypeDefRid)
        (name : string)
        (eventType : TypeDefOrRef)
        (flags : int)
        (scope : EmitScope)
        : EventRid * EmitScope
        =
        let flags =
            if flags = -1 then
                0
            else
                flags &&& ~~~reservedEventFlags &&& 0xFFFF

        let name, scope = EmitScope.internString name scope

        EmitScope.addEvent
            {
                Owner = owner
                Flags = enum<EventAttributes> flags
                Name = name
                EventType = eventType
            }
            scope

    /// <summary>
    /// Define a parameter of <paramref name="method"/>, as <c>RegMeta::DefineParam</c> does for
    /// <c>TypeBuilder_SetParamInfo</c>, whose emitter does not check for a duplicate (<c>MDDupParamDef</c> is
    /// not set): a new row at the next RID, whatever rows the method already has, unless CoreCLR would strand
    /// later MethodDef rows (<see cref="ParamDefinition.StrandsLaterMethods"/>).
    /// </summary>
    /// <remarks>
    /// The row holds the low 16 bits of <paramref name="sequence"/> and of <paramref name="flags"/>, the
    /// columns' widths. An empty name leaves the name column 0, as a null name does in CoreCLR.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="flags"/> has a bit of <c>ParameterAttributes.ReservedMask</c> (0xF000), which every
    /// managed caller clears; <paramref name="sequence"/> is negative, which every managed caller refuses;
    /// the scope lacks <paramref name="method"/>; or <paramref name="name"/> holds a NUL.
    /// </exception>
    let defineParam
        (method : MethodDefRid)
        (sequence : int)
        (name : string)
        (flags : int)
        (scope : EmitScope)
        : ParamDefinition
        =
        if flags &&& 0xF000 <> 0 then
            invalidArg
                (nameof flags)
                $"EmitScopeDefinitions.defineParam: the flags 0x%08x{flags} have a reserved bit (0xF000), which every managed caller clears"

        if sequence < 0 then
            invalidArg
                (nameof sequence)
                $"EmitScopeDefinitions.defineParam: the sequence %d{sequence} is negative, which every managed caller refuses"

        if (EmitScope.tryMethodDefRid method.Rid scope).IsNone then
            invalidArg
                (nameof method)
                $"EmitScopeDefinitions.defineParam: the scope has no MethodDef row %d{method.Rid}"

        let stored = uint16 (sequence &&& 0xFFFF)
        let nameOffset, named = EmitScope.internString name scope
        let parameters = EmitScope.parameters scope
        let methodCount = (EmitScope.methodDefs scope).Count

        // `CMiniMdRW::FixParamSequence` starts `ParamPtr` without converting the `END_OF_TABLE` markers when
        // the new row slides and no table exists. With no table, the rows with a real `ParamList` end at the
        // method of the last row, so the row slides past one of its own method only if that method owns the
        // last row; and the methods after it hold the marker.
        let noTable =
            parameters
            |> Seq.pairwise
            |> Seq.forall (fun (a, b) -> (a.Owner.Rid, a.Sequence) <= (b.Owner.Rid, b.Sequence))

        let slidesPastLast =
            parameters.Count > 0
            && parameters.[parameters.Count - 1].Owner = method
            && parameters.[parameters.Count - 1].Sequence > stored

        if noTable && slidesPastLast && methodCount > method.Rid then
            ParamDefinition.StrandsLaterMethods (method, MethodDefRid methodCount)
        else
            let rid, scope =
                EmitScope.addParam
                    {
                        Owner = method
                        Flags = enum<ParameterAttributes> (flags &&& 0xFFFF)
                        Sequence = stored
                        Name = nameOffset
                    }
                    named

            ParamDefinition.Defined (rid, scope)

    /// <summary>
    /// What CoreCLR's emitter does with an attribute of <paramref name="owner"/> whose constructor is
    /// <paramref name="constructor"/>, as <c>RegMeta::DefineCustomAttribute</c> and <c>_IsKnownCustomAttribute</c>
    /// decide it: by the namespace and name of the constructor's declaring type, compared ordinally, read from the
    /// TypeRef or TypeDef that a MemberRef's parent is, or from the TypeDef that owns a MethodDef. Any other parent has
    /// no name, and is <c>Ordinary</c>. The signature is never consulted.
    /// </summary>
    let classifyCustomAttribute
        (owner : HasCustomAttribute)
        (constructor : MethodDefOrRef)
        (scope : EmitScope)
        : CustomAttributeEmitKind
        =
        let operation = "EmitScopeDefinitions.classifyCustomAttribute"
        // Refuses an owner the scope lacks.
        EmitScope.typeOfCustomAttributeOwner owner scope |> ignore<TypeDefRid option>

        let typeDefName (typeDef : TypeDefRid) : string * string =
            let row = (EmitScope.typeDefs scope).[typeDef.Rid - 1]
            EmitScope.stringAt row.Namespace scope, EmitScope.stringAt row.Name scope

        let name : (string * string) option =
            match constructor with
            | MethodDefOrRef.MemberRef rid ->
                if (EmitScope.tryMemberRefRid rid.Rid scope).IsNone then
                    invalidArg (nameof constructor) $"%s{operation}: the scope has no MemberRef row %d{rid.Rid}"

                match (EmitScope.memberRefs scope).[rid.Rid - 1].Parent with
                | MemberRefParent.TypeRef typeRef ->
                    let row = (EmitScope.typeRefs scope).[typeRef.Rid - 1]
                    Some (EmitScope.stringAt row.Namespace scope, EmitScope.stringAt row.Name scope)
                | MemberRefParent.TypeDef typeDef -> Some (typeDefName typeDef)
                | MemberRefParent.TypeSpec _
                | MemberRefParent.MethodDef _ -> None
            | MethodDefOrRef.MethodDef rid ->
                if (EmitScope.tryMethodDefRid rid.Rid scope).IsNone then
                    invalidArg (nameof constructor) $"%s{operation}: the scope has no MethodDef row %d{rid.Rid}"

                Some (typeDefName (EmitScope.methodDefs scope).[rid.Rid - 1].Owner)

        match name with
        | None -> CustomAttributeEmitKind.Ordinary
        | Some name ->

        match
            KnownCustomAttribute.all
            |> List.tryFind (fun known -> KnownCustomAttribute.name known = name)
        with
        | Some known -> CustomAttributeEmitKind.Known known
        | None ->
            // `RegMeta::DefineCustomAttribute` compares the joined path; every name a managed caller can write is a
            // full name divided at its last dot, so comparing the two parts agrees with it.
            match owner, name with
            | HasCustomAttribute.MethodDef _, ("System.Security", "DynamicSecurityMethodAttribute") ->
                CustomAttributeEmitKind.DynamicSecurityMethod
            | (HasCustomAttribute.TypeDef _ | HasCustomAttribute.MethodDef _),
              ("System.Security", "SuppressUnmanagedCodeSecurityAttribute") ->
                CustomAttributeEmitKind.SuppressUnmanagedCodeSecurity
            | _ -> CustomAttributeEmitKind.Ordinary

    /// <summary>
    /// The scope with the bit that <paramref name="flags"/> gives <paramref name="owner"/>'s table ORed into the owner's
    /// row, as <c>RegMeta::_HandleKnownCustomAttribute</c> does; <c>None</c> when that table has no bit, which is a
    /// wrong target.
    /// </summary>
    /// <exception cref="ArgumentException">The scope lacks the owner.</exception>
    let addPseudoAttributeFlag
        (flags : PseudoAttributeFlags)
        (owner : HasCustomAttribute)
        (scope : EmitScope)
        : EmitScope option
        =
        // Refuses an owner the scope lacks.
        EmitScope.typeOfCustomAttributeOwner owner scope |> ignore<TypeDefRid option>

        match owner with
        | HasCustomAttribute.TypeDef rid ->
            flags.TypeDef |> Option.map (fun bit -> EmitScope.addTypeDefFlags rid bit scope)
        | HasCustomAttribute.MethodDef rid ->
            match flags.MethodDef with
            | Some (MethodDefFlag.Flags bit) -> Some (EmitScope.addMethodDefFlags rid bit scope)
            | Some (MethodDefFlag.ImplFlags bit) ->
                let row = (EmitScope.methodDefs scope).[rid.Rid - 1]
                Some (EmitScope.setMethodImplFlags rid (row.ImplFlags ||| bit) scope)
            | None -> None
        | HasCustomAttribute.Field rid -> flags.Field |> Option.map (fun bit -> EmitScope.addFieldFlags rid bit scope)
        | HasCustomAttribute.Param rid -> flags.Param |> Option.map (fun bit -> EmitScope.addParamFlags rid bit scope)
        | HasCustomAttribute.Property rid ->
            flags.Property
            |> Option.map (fun bit -> EmitScope.addPropertyFlags rid bit scope)
        | HasCustomAttribute.Event rid -> flags.Event |> Option.map (fun bit -> EmitScope.addEventFlags rid bit scope)
        | HasCustomAttribute.Assembly
        | HasCustomAttribute.Module
        | HasCustomAttribute.GenericParam _ -> None

    // A new row at the next RID, its blob interned as given, with no search for an equal row.
    let private storeAsGiven
        (owner : HasCustomAttribute)
        (constructor : MethodDefOrRef)
        (value : ImmutableArray<byte>)
        (scope : EmitScope)
        : CustomAttributeDefinition
        =
        let blob, scope = EmitScope.internBlob value scope

        let rid, scope =
            EmitScope.addCustomAttribute
                {
                    Parent = owner
                    Constructor = constructor
                    Value = blob
                }
                scope

        CustomAttributeDefinition.Defined (rid, scope)

    // `RegMeta::_HandleKnownCustomAttribute` for an attribute it keeps: the owner's table among the targets, then the
    // blob. Why it refuses, if it does.
    let private validatePseudoAttribute
        (validated : ValidatedPseudoAttribute)
        (owner : HasCustomAttribute)
        (value : ImmutableArray<byte>)
        : KnownCustomAttributeRefusal option
        =
        let table =
            match owner with
            | HasCustomAttribute.Assembly -> TableIndex.Assembly
            | HasCustomAttribute.Module -> TableIndex.Module
            | HasCustomAttribute.TypeDef _ -> TableIndex.TypeDef
            | HasCustomAttribute.MethodDef _ -> TableIndex.MethodDef
            | HasCustomAttribute.Field _ -> TableIndex.Field
            | HasCustomAttribute.Param _ -> TableIndex.Param
            | HasCustomAttribute.Property _ -> TableIndex.Property
            | HasCustomAttribute.Event _ -> TableIndex.Event
            | HasCustomAttribute.GenericParam _ -> TableIndex.GenericParam

        if not (validated.Targets.Contains table) then
            Some KnownCustomAttributeRefusal.WrongTarget
        else
            match KnownCustomAttributeBlob.validate validated.Arguments value with
            | Error refusal -> Some refusal
            | Ok _ -> None

    /// <summary>
    /// Define a custom attribute of <paramref name="owner"/>, as <c>RegMeta::DefineCustomAttribute</c> does for
    /// <c>TypeBuilder_DefineCustomAttribute</c>: unless <c>classifyCustomAttribute</c> says that the emitter does
    /// something else with it, a new row at the next RID, its blob interned as given, with no search for an equal row.
    /// A pseudo-attribute whose <c>KnownCustomAttribute.effect</c> sets a flag sets it or is refused, and its blob is not
    /// read; one the emitter validates and keeps is stored as an ordinary attribute is, or refused; any other is
    /// <c>NotStoredAsGiven</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The scope lacks the owner or the constructor.</exception>
    let defineCustomAttribute
        (owner : HasCustomAttribute)
        (constructor : MethodDefOrRef)
        (value : ImmutableArray<byte>)
        (scope : EmitScope)
        : CustomAttributeDefinition
        =
        match classifyCustomAttribute owner constructor scope with
        | CustomAttributeEmitKind.Ordinary -> storeAsGiven owner constructor value scope
        | CustomAttributeEmitKind.Known known as kind ->
            match KnownCustomAttribute.effect known with
            | KnownCustomAttributeEffect.SetsFlag flags ->
                match addPseudoAttributeFlag flags owner scope with
                | Some scope -> CustomAttributeDefinition.FlagSet scope
                | None -> CustomAttributeDefinition.Refused KnownCustomAttributeRefusal.WrongTarget
            | KnownCustomAttributeEffect.ValidatesAndKeeps validated ->
                match validatePseudoAttribute validated owner value with
                | Some refusal -> CustomAttributeDefinition.Refused refusal
                | None -> storeAsGiven owner constructor value scope
            | KnownCustomAttributeEffect.Unmodelled -> CustomAttributeDefinition.NotStoredAsGiven kind
        | kind -> CustomAttributeDefinition.NotStoredAsGiven kind

    /// <summary>
    /// Set <paramref name="owner"/>'s constant, as <c>TypeBuilder_SetConstantValue</c> does through
    /// <c>RegMeta::_DefineSetConstant</c>: the owner's <c>Constant</c> row, replaced in place if it has one and added
    /// otherwise, takes the value's element type, and takes its blob unless the blob is empty (an empty string), in
    /// which case the row keeps its old blob, whatever its old type, or a new row the empty blob; and the owner's
    /// <c>HasDefault</c> flag is ORed in. A string is stored whole: the managed caller's string is cut at its first NUL
    /// before it reaches here. A property with an accessor row is <c>DetachesAccessors</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The scope lacks the owner.</exception>
    let defineConstant (owner : HasConstant) (value : ConstantValue) (scope : EmitScope) : ConstantDefinition =
        // Refuses an owner the scope lacks.
        EmitScope.typeOfConstantOwner owner scope |> ignore<TypeDefRid>

        // `_SetPropertyProps` detaches each of the property's rows whose semantics is exactly `msGetter` or exactly
        // `msSetter` (`_DefineMethodSemantics`, md/compiler/regmeta_emit.cpp:975); an Other method's row stays.
        let detaches =
            match owner with
            | HasConstant.Property property ->
                EmitScope.methodSemantics scope
                |> Seq.exists (fun row ->
                    row.Association = HasSemantics.Property property
                    && (row.Semantics = MethodSemanticsAttributes.Getter
                        || row.Semantics = MethodSemanticsAttributes.Setter)
                )
            | HasConstant.Field _
            | HasConstant.Param _ -> false

        if detaches then
            ConstantDefinition.DetachesAccessors
        else

        let newBlob = ConstantValue.blob value

        // `_DefineSetConstant` writes the blob only when its size is above 0 (md/compiler/regmeta_emit.cpp:1503).
        let blob, scope =
            if newBlob.Length > 0 then
                EmitScope.internBlob newBlob scope
            else
                match EmitScope.constants scope |> Seq.tryFind (fun row -> row.Parent = owner) with
                | Some existing -> existing.Value, scope
                | None -> EmitScope.internBlob ImmutableArray.Empty scope

        let scope =
            EmitScope.setConstant
                {
                    Parent = owner
                    Type = ConstantValue.typeCode value
                    Value = blob
                }
                scope

        let scope =
            match owner with
            | HasConstant.Field rid -> EmitScope.addFieldFlags rid FieldAttributes.HasDefault scope
            | HasConstant.Param rid -> EmitScope.addParamFlags rid ParameterAttributes.HasDefault scope
            | HasConstant.Property rid -> EmitScope.addPropertyFlags rid PropertyAttributes.HasDefault scope

        ConstantDefinition.Defined scope
