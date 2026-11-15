namespace WoofWare.PawPrint.Test

#nowarn "9"

open System
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Runtime.InteropServices
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.EmitScopeGen

/// `ConstantValue`, `EmitScopeDefinitions.defineConstant`, `EmitScope.setConstant` and the image's `Constant` table: the
/// element types and sizes against CoreCLR's emitter, the encoding against SRM's, `defineConstant` against a model of
/// `RegMeta::_DefineSetConstant`, the image read back through SRM, and every owner's row and flags against the host's own
/// emitter, read raw through CoreLib's internal `MetadataImport`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeConstants =

    // ---- An independent model of the encoding: SRM's writer, and a transcription of the element types. ----

    /// The element type each case names, transcribed from `RuntimeTypeBuilder.SetConstantValue`
    /// (System/Reflection/Emit/RuntimeTypeBuilder.cs:254-298): the value's own `CorElementType` for the twelve
    /// primitives, `ELEMENT_TYPE_STRING` for a string, and `ELEMENT_TYPE_CLASS` for null.
    let private modelTypeCode (value : ConstantValue) : int =
        match value with
        | ConstantValue.Boolean _ -> 0x02
        | ConstantValue.Char _ -> 0x03
        | ConstantValue.SByte _ -> 0x04
        | ConstantValue.Byte _ -> 0x05
        | ConstantValue.Int16 _ -> 0x06
        | ConstantValue.UInt16 _ -> 0x07
        | ConstantValue.Int32 _ -> 0x08
        | ConstantValue.UInt32 _ -> 0x09
        | ConstantValue.Int64 _ -> 0x0A
        | ConstantValue.UInt64 _ -> 0x0B
        | ConstantValue.Single _ -> 0x0C
        | ConstantValue.Double _ -> 0x0D
        | ConstantValue.String _ -> 0x0E
        | ConstantValue.NullReference -> 0x12

    /// The value as the managed caller holds it, boxed, for SRM's writer and the host's emitter; `None` for a
    /// `Boolean` byte other than 0 or 1, which no `bool` the caller boxes holds.
    let private boxed (value : ConstantValue) : obj option =
        match value with
        | ConstantValue.Boolean 0uy -> Some (box false)
        | ConstantValue.Boolean 1uy -> Some (box true)
        | ConstantValue.Boolean _ -> None
        | ConstantValue.Char c -> Some (box c)
        | ConstantValue.SByte v -> Some (box v)
        | ConstantValue.Byte v -> Some (box v)
        | ConstantValue.Int16 v -> Some (box v)
        | ConstantValue.UInt16 v -> Some (box v)
        | ConstantValue.Int32 v -> Some (box v)
        | ConstantValue.UInt32 v -> Some (box v)
        | ConstantValue.Int64 v -> Some (box v)
        | ConstantValue.UInt64 v -> Some (box v)
        | ConstantValue.Single bits -> Some (box (BitConverter.Int32BitsToSingle bits))
        | ConstantValue.Double bits -> Some (box (BitConverter.Int64BitsToDouble bits))
        | ConstantValue.String s -> Some (box s)
        | ConstantValue.NullReference -> Some null

    /// The blob SRM's `BlobBuilder.WriteConstant` writes for the value; a `Boolean` byte that no `bool` holds is
    /// that byte, as the managed caller copies the box's byte.
    let private modelBlob (value : ConstantValue) : byte list =
        match boxed value with
        | None ->
            match value with
            | ConstantValue.Boolean b -> [ b ]
            | other -> failwith $"BUG: only a Boolean is unboxable, not %A{other}"
        | Some o ->
            let builder = BlobBuilder ()
            builder.WriteConstant o
            List.ofArray (builder.ToArray ())

    /// SRM's `BlobReader.ReadConstant` of `bytes` under the element type `code`.
    let private readConstant (code : ConstantTypeCode) (bytes : byte[]) : obj =
        // `BlobReader` reads only from pinned memory.
        let pinned = GCHandle.Alloc (Array.append bytes [| 0uy |], GCHandleType.Pinned)

        try
            let mutable reader =
                BlobReader (NativeInterop.NativePtr.ofNativeInt<byte> (pinned.AddrOfPinnedObject ()), bytes.Length)

            reader.ReadConstant code
        finally
            pinned.Free ()

    // ---- Generators. ----

    let private genBytes (count : int) : Gen<byte[]> =
        Gen.arrayOfLength count (Gen.choose (0, 255) |> Gen.map byte)

    /// Strings of UTF-16 code units, lone surrogates and NULs among them.
    let private genCodeUnits : Gen<string> =
        Gen.frequency
            [
                3, Gen.elements [ 'a' ; 'Z' ; '\000' ; char 0xD800 ; char 0xDFFF ; '日' ; char 0xFFFF ]
                1, Gen.choose (0, 0xFFFF) |> Gen.map char
            ]
        |> Gen.listOf
        |> Gen.map (fun chars -> System.String (Array.ofSeq chars))

    /// A value of any case, its payload drawn from every bit pattern.
    let private genConstantValue : Gen<ConstantValue> =
        Gen.oneof
            [
                genBytes 1 |> Gen.map (fun b -> ConstantValue.Boolean b.[0])
                genBytes 2 |> Gen.map (fun b -> ConstantValue.Char (BitConverter.ToChar (b, 0)))
                genBytes 1 |> Gen.map (fun b -> ConstantValue.SByte (sbyte b.[0]))
                genBytes 1 |> Gen.map (fun b -> ConstantValue.Byte b.[0])
                genBytes 2
                |> Gen.map (fun b -> ConstantValue.Int16 (BitConverter.ToInt16 (b, 0)))
                genBytes 2
                |> Gen.map (fun b -> ConstantValue.UInt16 (BitConverter.ToUInt16 (b, 0)))
                genBytes 4
                |> Gen.map (fun b -> ConstantValue.Int32 (BitConverter.ToInt32 (b, 0)))
                genBytes 4
                |> Gen.map (fun b -> ConstantValue.UInt32 (BitConverter.ToUInt32 (b, 0)))
                genBytes 8
                |> Gen.map (fun b -> ConstantValue.Int64 (BitConverter.ToInt64 (b, 0)))
                genBytes 8
                |> Gen.map (fun b -> ConstantValue.UInt64 (BitConverter.ToUInt64 (b, 0)))
                genBytes 4
                |> Gen.map (fun b -> ConstantValue.Single (BitConverter.ToInt32 (b, 0)))
                genBytes 8
                |> Gen.map (fun b -> ConstantValue.Double (BitConverter.ToInt64 (b, 0)))
                genCodeUnits |> Gen.map ConstantValue.String
                Gen.constant ConstantValue.NullReference
            ]

    /// A case's name, for coverage.
    let private caseName (value : ConstantValue) : string =
        match value with
        | ConstantValue.Boolean _ -> "Boolean"
        | ConstantValue.Char _ -> "Char"
        | ConstantValue.SByte _ -> "SByte"
        | ConstantValue.Byte _ -> "Byte"
        | ConstantValue.Int16 _ -> "Int16"
        | ConstantValue.UInt16 _ -> "UInt16"
        | ConstantValue.Int32 _ -> "Int32"
        | ConstantValue.UInt32 _ -> "UInt32"
        | ConstantValue.Int64 _ -> "Int64"
        | ConstantValue.UInt64 _ -> "UInt64"
        | ConstantValue.Single _ -> "Single"
        | ConstantValue.Double _ -> "Double"
        | ConstantValue.String _ -> "String"
        | ConstantValue.NullReference -> "NullReference"

    let private allCaseNames : string list =
        [
            "Boolean"
            "Char"
            "SByte"
            "Byte"
            "Int16"
            "UInt16"
            "Int32"
            "UInt32"
            "Int64"
            "UInt64"
            "Single"
            "Double"
            "String"
            "NullReference"
        ]

    // ---- The fixed tables. ----

    [<Test>]
    let ``each case's element type and blob size are CoreCLR's`` () : unit =
        // `_GetSizeOfConstantBlob` (md/compiler/mdutil.cpp:281): BOOLEAN, I1 and U1 are 1 byte (:290-296); CHAR, I2 and
        // U2 are 2 (:297-301); I4, U4 and R4 are 4 (:302-307); I8, U8 and R8 are 8 (:309-313); a STRING is two bytes per
        // character up to its first NUL (:315-322); CLASS, a null reference, is 4 (:324-330). The element types are
        // the managed caller's (RuntimeTypeBuilder.cs:254-298).
        let table : (ConstantValue * int * int) list =
            [
                ConstantValue.Boolean 1uy, 0x02, 1
                ConstantValue.Char 'x', 0x03, 2
                ConstantValue.SByte -5y, 0x04, 1
                ConstantValue.Byte 200uy, 0x05, 1
                ConstantValue.Int16 -300s, 0x06, 2
                ConstantValue.UInt16 60000us, 0x07, 2
                ConstantValue.Int32 5, 0x08, 4
                ConstantValue.UInt32 0xFFFFFFFEu, 0x09, 4
                ConstantValue.Int64 0x0123456789ABCDEFL, 0x0A, 8
                ConstantValue.UInt64 UInt64.MaxValue, 0x0B, 8
                ConstantValue.Single 0x3FC00000, 0x0C, 4
                ConstantValue.Double Int64.MinValue, 0x0D, 8
                ConstantValue.String "abc", 0x0E, 6
                ConstantValue.NullReference, 0x12, 4
            ]

        table |> List.map (fun (v, _, _) -> caseName v) |> shouldEqual allCaseNames

        for value, code, size in table do
            int (ConstantValue.typeCode value) |> shouldEqual code
            (ConstantValue.blob value).Length |> shouldEqual size

            let expectedWidth =
                match value with
                | ConstantValue.String _
                | ConstantValue.NullReference -> None
                | _ -> Some size

            ConstantValue.fixedWidthSize (ConstantValue.typeCode value)
            |> shouldEqual expectedWidth

        // No other element type has a width.
        for code in 0..0xFF do
            if not (List.exists (fun (_, c, _) -> c = code) table) then
                ConstantValue.fixedWidthSize (LanguagePrimitives.EnumOfValue<byte, ConstantTypeCode> (byte code))
                |> shouldEqual None

    [<Test>]
    let ``each owner's HasDefault bit and HasConstant tag are ECMA-335's`` () : unit =
        // `fdHasDefault` 0x8000, `pdHasDefault` 0x1000, `prHasDefault` 0x1000 (inc/corhdr.h:469, :504, :527).
        FieldAttributes.HasDefault |> int |> shouldEqual 0x8000
        ParameterAttributes.HasDefault |> int |> shouldEqual 0x1000
        PropertyAttributes.HasDefault |> int |> shouldEqual 0x1000

        // ECMA-335 II.24.2.6: HasConstant tags Field 0, Param 1, Property 2, in two bits.
        (HasConstant.Field (FieldRid 3)).CodedIndex |> shouldEqual ((3 <<< 2) ||| 0)
        (HasConstant.Param (ParamRid 3)).CodedIndex |> shouldEqual ((3 <<< 2) ||| 1)

        (HasConstant.Property (PropertyRid 3)).CodedIndex
        |> shouldEqual ((3 <<< 2) ||| 2)

        (HasConstant.Field (FieldRid 3)).Token |> shouldEqual 0x04000003
        (HasConstant.Param (ParamRid 3)).Token |> shouldEqual 0x08000003
        (HasConstant.Property (PropertyRid 3)).Token |> shouldEqual 0x17000003

    /// A scope with one type `T` (TypeDef 2) holding a static literal field `F` (Field 1), a method `M` with a
    /// parameter `p` (Param 1), and a property `P` with no accessors (Property 1), as the planner's probe defines them.
    let private oneOfEach () : EmitScope * HasConstant list =
        let scope = emptyScope Guid.Empty

        let t, scope =
            match EmitScopeDefinitions.defineTopLevelTypeDef "T" TypeAttributes.Public None [] scope with
            | TypeDefDefinition.Defined (rid, scope) -> rid, scope
            | TypeDefDefinition.Duplicate _ -> failwith "T is defined once"

        let field, scope =
            EmitScopeDefinitions.defineField
                t
                "F"
                (ImmutableArray.Create<byte> [| 0x06uy ; 0x1Cuy |])
                (FieldAttributes.Public ||| FieldAttributes.Static ||| FieldAttributes.Literal)
                scope

        let method, scope =
            EmitScopeDefinitions.defineMethod
                t
                "M"
                (ImmutableArray.Create<byte> [| 0x00uy ; 0x01uy ; 0x01uy ; 0x1Cuy |])
                (MethodAttributes.Public ||| MethodAttributes.Static)
                scope

        let param, scope =
            match EmitScopeDefinitions.defineParam method 1 "p" 0 scope with
            | ParamDefinition.Defined (rid, scope) -> rid, scope
            | ParamDefinition.StrandsLaterMethods _ -> failwith "one parameter strands nothing"

        let property, scope =
            EmitScopeDefinitions.defineProperty
                t
                "P"
                (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x1Cuy |])
                0
                scope

        scope,
        [
            HasConstant.Field field
            HasConstant.Param param
            HasConstant.Property property
        ]

    let private defined (owner : HasConstant) (value : ConstantValue) (scope : EmitScope) : EmitScope =
        match EmitScopeDefinitions.defineConstant owner value scope with
        | ConstantDefinition.Defined scope -> scope
        | ConstantDefinition.DetachesAccessors -> failwith $"%A{owner} has no accessor, but defineConstant says it has"

    /// The owner's flags column.
    let private flagsOf (owner : HasConstant) (scope : EmitScope) : int =
        match owner with
        | HasConstant.Field rid -> int (EmitScope.fields scope).[rid.Rid - 1].Flags
        | HasConstant.Param rid -> int (EmitScope.parameters scope).[rid.Rid - 1].Flags
        | HasConstant.Property rid -> int (EmitScope.properties scope).[rid.Rid - 1].Flags

    /// The owner's rows of the scope's Constant table, each its element type and its blob.
    let private rowsOf (owner : HasConstant) (scope : EmitScope) : (int * byte list) list =
        EmitScope.constants scope
        |> Seq.filter (fun row -> row.Parent = owner)
        |> Seq.map (fun row -> int row.Type, List.ofSeq (EmitScope.blobAt row.Value scope))
        |> List.ofSeq

    let private hex (text : string) : byte list =
        text.Split (' ', StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (fun b -> Convert.ToByte (b, 16))
        |> List.ofSeq

    [<Test>]
    let ``each measured value is stored as real dotnet stores it, on each owner`` () : unit =
        // The planner's probe (`setconstant/probe`, scenario `values`), measured on .NET 10.0.14 and 11.0.0-rc.3: each
        // row's element type and blob, the same on a parameter, a static literal field and a property. The flags
        // gain exactly the owner's `HasDefault`: parameter 0x0000 to 0x1000, field 0x0056 to 0x8056, property 0x0000
        // to 0x1000. `""` stores the empty blob; `"a\0b"` reaches the emitter cut at its NUL, which is the QCall's
        // handler's to do, and is checked by the guest.
        let measured : (ConstantValue * int * string) list =
            [
                ConstantValue.Boolean 1uy, 0x02, "01"
                ConstantValue.Char 'x', 0x03, "78 00"
                ConstantValue.SByte -5y, 0x04, "FB"
                ConstantValue.Byte 200uy, 0x05, "C8"
                ConstantValue.Int16 -300s, 0x06, "D4 FE"
                ConstantValue.UInt16 60000us, 0x07, "60 EA"
                ConstantValue.Int32 5, 0x08, "05 00 00 00"
                ConstantValue.UInt32 0xFFFFFFFEu, 0x09, "FE FF FF FF"
                ConstantValue.Int64 0x0123456789ABCDEFL, 0x0A, "EF CD AB 89 67 45 23 01"
                ConstantValue.UInt64 UInt64.MaxValue, 0x0B, "FF FF FF FF FF FF FF FF"
                ConstantValue.Single (BitConverter.SingleToInt32Bits 1.5f), 0x0C, "00 00 C0 3F"
                ConstantValue.Single 0x7fc00001, 0x0C, "01 00 C0 7F"
                ConstantValue.Double (BitConverter.DoubleToInt64Bits -0.0), 0x0D, "00 00 00 00 00 00 00 80"
                ConstantValue.Double 0x7ff4000000000001L, 0x0D, "01 00 00 00 00 00 F4 7F"
                ConstantValue.Char (char 0xFFFF), 0x03, "FF FF"
                ConstantValue.Boolean 0uy, 0x02, "00"
                ConstantValue.String "abc", 0x0E, "61 00 62 00 63 00"
                ConstantValue.String "", 0x0E, ""
                ConstantValue.String "a", 0x0E, "61 00"
                ConstantValue.String (string (char 0xD800) + "x"), 0x0E, "00 D8 78 00"
                ConstantValue.NullReference, 0x12, "00 00 00 00"
            ]

        for value, code, blob in measured do
            let scope, owners = oneOfEach ()

            for owner in owners do
                let before = flagsOf owner scope
                let after = defined owner value scope
                rowsOf owner after |> shouldEqual [ code, hex blob ]

                let bit =
                    match owner with
                    | HasConstant.Field _ -> 0x8000
                    | HasConstant.Param _
                    | HasConstant.Property _ -> 0x1000

                flagsOf owner after |> shouldEqual (before ||| bit)

    [<Test>]
    let ``a second constant replaces the first, but an empty string keeps the old blob`` () : unit =
        // The planner's `second` scenario and the decisions' `odd` scenario (`setconstant/probe`,
        // `setconstant-decide/probe2`), measured on .NET 10.0.14 and 11.0.0-rc.3, the same on a parameter, a static
        // literal field and a property: the second call's element type, and its blob unless that is empty. The string
        // reflection reads back from a `STRING` row is its blob's whole code units.
        let sequences : (ConstantValue * ConstantValue * int * string * string option) list =
            [
                ConstantValue.Int32 5, ConstantValue.Int32 7, 0x08, "07 00 00 00", None
                ConstantValue.Int32 5, ConstantValue.String "x", 0x0E, "78 00", Some "x"
                ConstantValue.String "abc", ConstantValue.String "", 0x0E, "61 00 62 00 63 00", Some "abc"
                ConstantValue.Int32 5, ConstantValue.String "", 0x0E, "05 00 00 00", Some "\u0005\u0000"
                ConstantValue.NullReference, ConstantValue.Int32 5, 0x08, "05 00 00 00", None
                ConstantValue.String "abc", ConstantValue.NullReference, 0x12, "00 00 00 00", None
                ConstantValue.String "", ConstantValue.String "abc", 0x0E, "61 00 62 00 63 00", Some "abc"
                ConstantValue.Int64 0x1122334455667788L, ConstantValue.Byte 2uy, 0x05, "02", None
                ConstantValue.String "abcdef", ConstantValue.String "xy", 0x0E, "78 00 79 00", Some "xy"
                // The `odd` rows: the old blob kept whatever the old type.
                ConstantValue.Byte 2uy, ConstantValue.String "", 0x0E, "02", Some ""
                ConstantValue.Boolean 1uy, ConstantValue.String "", 0x0E, "01", Some ""
                ConstantValue.NullReference, ConstantValue.String "", 0x0E, "00 00 00 00", Some "\u0000\u0000"
                ConstantValue.Char 'x', ConstantValue.String "", 0x0E, "78 00", Some "x"
                ConstantValue.Int16 0x4142s, ConstantValue.String "", 0x0E, "42 41", Some "䅂"
                ConstantValue.Int64 0x0044004300420041L,
                ConstantValue.String "",
                0x0E,
                "41 00 42 00 43 00 44 00",
                Some "ABCD"
            ]

        for first, second, code, blob, read in sequences do
            let scope, owners = oneOfEach ()

            for owner in owners do
                let after = scope |> defined owner first |> defined owner second
                rowsOf owner after |> shouldEqual [ code, hex blob ]
                (EmitScope.constants after).Count |> shouldEqual 1

                match read with
                | None -> ()
                | Some expected ->
                    let bytes = hex blob

                    System.String (
                        Array.init
                            (bytes.Length / 2)
                            (fun i -> char (int bytes.[2 * i] ||| (int bytes.[2 * i + 1] <<< 8)))
                    )
                    |> shouldEqual expected

        // Two owners set alternately each keep their own row, in the order of their first constants.
        let scope, owners = oneOfEach ()
        let field = owners.[0]
        let param = owners.[1]

        let after =
            scope
            |> defined param (ConstantValue.Int32 1)
            |> defined field (ConstantValue.Int32 2)
            |> defined param (ConstantValue.Int32 3)
            |> defined field (ConstantValue.Int32 4)

        EmitScope.constants after
        |> Seq.map (fun row -> row.Parent, List.ofSeq (EmitScope.blobAt row.Value after))
        |> List.ofSeq
        |> shouldEqual [ param, hex "03 00 00 00" ; field, hex "04 00 00 00" ]

    [<Test>]
    let ``a definition never gives an owner HasDefault without a constant`` () : unit =
        // The decisions' `flagonly` (`setconstant-decide/probe2`), measured on .NET 10.0.14 and 11.0.0-rc.3: a field
        // defined with `HasDefault` has flags 0x0056 and a property 0x0000, and no Constant row; every managed caller
        // clears a parameter's reserved bits, which `defineParam` refuses.
        let scope, _ = oneOfEach ()
        let t = TypeDefRid 2

        let field, scope =
            EmitScopeDefinitions.defineField
                t
                "H"
                (ImmutableArray.Create<byte> [| 0x06uy ; 0x08uy |])
                (FieldAttributes.Public
                 ||| FieldAttributes.Static
                 ||| FieldAttributes.Literal
                 ||| FieldAttributes.HasDefault)
                scope

        flagsOf (HasConstant.Field field) scope |> shouldEqual 0x0056

        let property, scope =
            EmitScopeDefinitions.defineProperty
                t
                "Q"
                (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                (int PropertyAttributes.HasDefault)
                scope

        flagsOf (HasConstant.Property property) scope |> shouldEqual 0x0000
        EmitScope.constants scope |> shouldBeEmpty

        Assert.Throws<ArgumentException> (fun () ->
            EmitScopeDefinitions.defineParam (MethodDefRid 1) 2 "q" 0x1010 scope
            |> ignore<ParamDefinition>
        )
        |> ignore

    // ---- The encoding, against SRM. ----

    [<Test>]
    let ``each value's blob is SRM's encoding of it, and SRM reads it back`` () : unit =
        let property (cover : string -> unit) (value : ConstantValue) : unit =
            cover (caseName value)
            let blob = ConstantValue.blob value
            List.ofSeq blob |> shouldEqual (modelBlob value)
            int (ConstantValue.typeCode value) |> shouldEqual (modelTypeCode value)

            let read = readConstant (ConstantValue.typeCode value) (Array.ofSeq blob)

            match value, read with
            | ConstantValue.Boolean b, (:? bool as r) -> r |> shouldEqual (b <> 0uy)
            | ConstantValue.Single bits, (:? single as r) -> BitConverter.SingleToInt32Bits r |> shouldEqual bits
            | ConstantValue.Double bits, (:? double as r) -> BitConverter.DoubleToInt64Bits r |> shouldEqual bits
            | ConstantValue.String s, (:? string as r) ->
                // SRM decodes through `Encoding.Unicode`, which replaces a lone surrogate; compare where it cannot.
                if s |> Seq.forall (fun c -> not (Char.IsSurrogate c)) then
                    r |> shouldEqual s
                else
                    cover "lone or paired surrogate"
            | ConstantValue.NullReference, r -> r |> shouldEqual null
            | _, r -> r |> shouldEqual (boxed value).Value

            // The fixed-width parse is the blob's inverse, at the size it gives the handler.
            match ConstantValue.fixedWidthSize (ConstantValue.typeCode value) with
            | Some size ->
                size |> shouldEqual blob.Length

                ConstantValue.ofFixedWidth (ConstantValue.typeCode value) blob
                |> shouldEqual value

                for wrong in [ size - 1 ; size + 1 ] do
                    Assert.Throws<ArgumentException> (fun () ->
                        ConstantValue.ofFixedWidth
                            (ConstantValue.typeCode value)
                            (ImmutableArray.CreateRange (Array.zeroCreate<byte> wrong))
                        |> ignore<ConstantValue>
                    )
                    |> ignore
            | None ->
                match value with
                | ConstantValue.String _
                | ConstantValue.NullReference ->
                    Assert.Throws<ArgumentException> (fun () ->
                        ConstantValue.ofFixedWidth (ConstantValue.typeCode value) blob
                        |> ignore<ConstantValue>
                    )
                    |> ignore
                | other -> failwith $"%A{other} has no fixed width"

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 2000) (Arb.fromGen genConstantValue) property

        for name in allCaseNames do
            coverage.Count name |> shouldBeGreaterThan 50

        coverage.Count "lone or paired surrogate" |> shouldBeGreaterThan 20

    // ---- `defineConstant` against a model. ----

    /// What accessors a property of the layout has.
    [<RequireQualifiedAccess>]
    type private PropertyKind =
        | NoAccessors
        | OtherOnly
        | Getter
        | Setter

    /// An owner of the layout, with what decides how `defineConstant` treats it.
    [<RequireQualifiedAccess>]
    type private OwnerKind =
        | Field
        | Param
        | ReturnParam
        | Property of PropertyKind

    /// Two types, each with fields `F` and `G`, a method with a return parameter and two parameters, and properties
    /// with no accessor, an Other method only, a getter and a setter, each owner defined with the flags `flags` chooses.
    /// Field 2 (`A.G`) and Param 1 (`A.M`'s return parameter) are both owners, and the parameter sorts first by
    /// coded index although the Field table comes first.
    let private layout (flags : int list) : EmitScope * (HasConstant * OwnerKind) list =
        let mutable scope = emptyScope Guid.Empty
        let mutable owners = []
        let mutable flagChoices = flags

        let nextFlag (choices : 'a list) : 'a =
            match flagChoices with
            | [] -> choices.Head
            | c :: rest ->
                flagChoices <- rest
                choices.[c % choices.Length]

        let fieldFlags =
            [
                FieldAttributes.Public
                FieldAttributes.Public ||| FieldAttributes.Static ||| FieldAttributes.Literal
                FieldAttributes.Private ||| FieldAttributes.InitOnly
                FieldAttributes.Assembly
                ||| FieldAttributes.Static
                ||| FieldAttributes.SpecialName
            ]

        let paramFlags = [ 0 ; 0x0001 ; 0x0002 ; 0x0010 ; 0x0013 ]
        let propertyFlags = [ 0 ; 0x0200 ]

        let signature =
            ImmutableArray.Create<byte> [| 0x00uy ; 0x02uy ; 0x1Cuy ; 0x1Cuy ; 0x1Cuy |]

        let accessorSignature = ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x1Cuy |]

        for name in [ "A" ; "B" ] do
            let t, s =
                match EmitScopeDefinitions.defineTopLevelTypeDef name TypeAttributes.Public None [] scope with
                | TypeDefDefinition.Defined (rid, s) -> rid, s
                | TypeDefDefinition.Duplicate _ -> failwith $"%s{name} is defined once"

            scope <- s

            for fieldName in [ "F" ; "G" ] do
                let f, s =
                    EmitScopeDefinitions.defineField
                        t
                        fieldName
                        (ImmutableArray.Create<byte> [| 0x06uy ; 0x1Cuy |])
                        (nextFlag fieldFlags)
                        scope

                scope <- s
                owners <- owners @ [ HasConstant.Field f, OwnerKind.Field ]

            let m, s =
                EmitScopeDefinitions.defineMethod t "M" signature MethodAttributes.Public scope

            scope <- s

            for sequence in 0..2 do
                match EmitScopeDefinitions.defineParam m sequence $"p%d{sequence}" (nextFlag paramFlags) scope with
                | ParamDefinition.Defined (p, s) ->
                    scope <- s

                    owners <-
                        owners
                        @ [
                            HasConstant.Param p,
                            (if sequence = 0 then
                                 OwnerKind.ReturnParam
                             else
                                 OwnerKind.Param)
                        ]
                | ParamDefinition.StrandsLaterMethods _ -> failwith "parameters in order strand nothing"

            for propertyName, kind in
                [
                    "P", PropertyKind.NoAccessors
                    "Q", PropertyKind.OtherOnly
                    "R", PropertyKind.Getter
                    "S", PropertyKind.Setter
                ] do
                let p, s =
                    EmitScopeDefinitions.defineProperty
                        t
                        propertyName
                        (ImmutableArray.Create<byte> [| 0x28uy ; 0x00uy ; 0x1Cuy |])
                        (nextFlag propertyFlags)
                        scope

                scope <- s

                let semantics =
                    match kind with
                    | PropertyKind.NoAccessors -> None
                    | PropertyKind.OtherOnly -> Some MethodSemanticsAttributes.Other
                    | PropertyKind.Getter -> Some MethodSemanticsAttributes.Getter
                    | PropertyKind.Setter -> Some MethodSemanticsAttributes.Setter

                match semantics with
                | None -> ()
                | Some semantics ->
                    let accessor, s =
                        EmitScopeDefinitions.defineMethod
                            t
                            $"acc_%s{propertyName}"
                            accessorSignature
                            MethodAttributes.Public
                            scope

                    scope <- EmitScopeDefinitions.defineMethodSemantics (HasSemantics.Property p) semantics accessor s

                owners <- owners @ [ HasConstant.Property p, OwnerKind.Property kind ]

        scope, owners

    /// One generated call: an owner of the layout and a value, each as a choice.
    type private Step =
        {
            Owner : int
            Value : ConstantValue
        }

    /// Values for the schedules: any value, and often the generator's few, among which are empty strings and one- and
    /// two-byte values, so that an empty string often follows another value of its owner.
    let private genScheduleValue : Gen<ConstantValue> =
        Gen.frequency
            [
                2, genConstantValue
                2, Gen.elements constantValues
                1, Gen.constant (ConstantValue.String "")
            ]

    let private genSchedule : Gen<int list * Step list> =
        gen {
            let! flags = Gen.listOfLength 30 (Gen.choose (0, 1000))
            let! count = Gen.choose (1, 40)
            // Owners from a few, sometimes, so that an owner often has a second constant.
            let! narrow = Gen.elements [ 3 ; 6 ; 1000 ]

            let! steps =
                Gen.listOfLength
                    count
                    (Gen.map2
                        (fun o v ->
                            {
                                Owner = o % narrow
                                Value = v
                            }
                        )
                        (Gen.choose (0, 1000))
                        genScheduleValue)

            return List.ofSeq flags, List.ofSeq steps
        }

    [<RequireQualifiedAccess>]
    type private ModelReach =
        | Case of string * OwnerKind
        | SecondSameType
        | SecondOtherType
        | EmptyOverNonEmpty
        | NullOverValue
        | EmptyFirst
        | OneByteThenEmpty
        | TwoBytesThenEmpty
        | NullThenEmpty
        | Detaches of PropertyKind
        | FieldTwoAndParamOne

    /// The model's rows: each owner's element type and blob, in the order of their first constants.
    type private Model = (HasConstant * (int * byte list)) list

    /// The model after a call that sets `owner`'s constant to `value`: the row replaced in place or added, with the
    /// value's element type, and its blob unless that is empty.
    let private modelStep (owner : HasConstant) (value : ConstantValue) (model : Model) : Model =
        let blob = modelBlob value

        match List.tryFindIndex (fun (o, _) -> o = owner) model with
        | Some index ->
            let _, (_, oldBlob) = model.[index]
            let blob = if blob.IsEmpty then oldBlob else blob
            List.updateAt index (owner, (modelTypeCode value, blob)) model
        | None -> model @ [ owner, (modelTypeCode value, blob) ]

    let private hasDefaultBit (owner : HasConstant) : int =
        match owner with
        | HasConstant.Field _ -> 0x8000
        | HasConstant.Param _
        | HasConstant.Property _ -> 0x1000

    /// Every row and heap entry of `before` that a constant must leave alone is in `after`: every table but the
    /// owners' flags columns and `Constant` is unchanged, and the blob heap only grows.
    let private othersUnchanged (before : EmitScope) (after : EmitScope) : unit =
        List.ofSeq (EmitScope.typeDefs after)
        |> shouldEqual (List.ofSeq (EmitScope.typeDefs before))

        List.ofSeq (EmitScope.methodDefs after)
        |> shouldEqual (List.ofSeq (EmitScope.methodDefs before))

        List.ofSeq (EmitScope.methodSemantics after)
        |> shouldEqual (List.ofSeq (EmitScope.methodSemantics before))

        List.ofSeq (EmitScope.customAttributes after)
        |> shouldEqual (List.ofSeq (EmitScope.customAttributes before))

        List.ofSeq (EmitScope.strings after)
        |> shouldEqual (List.ofSeq (EmitScope.strings before))

        List.ofSeq (EmitScope.userStrings after)
        |> shouldEqual (List.ofSeq (EmitScope.userStrings before))

        let clearFlags (rows : seq<'row>) (clear : 'row -> 'row) = rows |> Seq.map clear |> List.ofSeq

        clearFlags
            (EmitScope.fields after)
            (fun r ->
                { r with
                    Flags = enum 0
                }
            )
        |> shouldEqual (
            clearFlags
                (EmitScope.fields before)
                (fun r ->
                    { r with
                        Flags = enum 0
                    }
                )
        )

        clearFlags
            (EmitScope.parameters after)
            (fun r ->
                { r with
                    Flags = enum 0
                }
            )
        |> shouldEqual (
            clearFlags
                (EmitScope.parameters before)
                (fun r ->
                    { r with
                        Flags = enum 0
                    }
                )
        )

        clearFlags
            (EmitScope.properties after)
            (fun r ->
                { r with
                    Flags = enum 0
                }
            )
        |> shouldEqual (
            clearFlags
                (EmitScope.properties before)
                (fun r ->
                    { r with
                        Flags = enum 0
                    }
                )
        )

        let beforeBlobs = List.ofSeq (EmitScope.blobs before) |> List.map List.ofSeq
        let afterBlobs = List.ofSeq (EmitScope.blobs after) |> List.map List.ofSeq
        List.truncate beforeBlobs.Length afterBlobs |> shouldEqual beforeBlobs

    /// The scope's rows as the model holds them.
    let private scopeRows (scope : EmitScope) : Model =
        EmitScope.constants scope
        |> Seq.map (fun row -> row.Parent, (int row.Type, List.ofSeq (EmitScope.blobAt row.Value scope)))
        |> List.ofSeq

    /// Runs a schedule against `defineConstant` and the model, checking each step; the scope, the model and the
    /// initial flags at the end.
    let private runSchedule
        (cover : ModelReach -> unit)
        (flags : int list)
        (steps : Step list)
        : EmitScope * Model * (HasConstant * OwnerKind) list * Map<HasConstant, int>
        =
        let scope, owners = layout flags
        let initial = owners |> List.map (fun (o, _) -> o, flagsOf o scope) |> Map.ofList

        let typeOf (owner : HasConstant) =
            EmitScope.typeOfConstantOwner owner scope

        let finalScope, finalModel, _ =
            ((scope, [], None), steps)
            ||> List.fold (fun (scope, model : Model, last : HasConstant option) step ->
                let owner, kind = owners.[step.Owner % owners.Length]

                match EmitScopeDefinitions.defineConstant owner step.Value scope with
                | ConstantDefinition.DetachesAccessors ->
                    match kind with
                    | OwnerKind.Property (PropertyKind.Getter as k)
                    | OwnerKind.Property (PropertyKind.Setter as k) ->
                        cover (ModelReach.Detaches k)
                        scope, model, last
                    | other -> failwith $"%A{owner}, a %A{other}, has no accessor row, but defineConstant says it has"
                | ConstantDefinition.Defined after ->
                    match kind with
                    | OwnerKind.Property PropertyKind.Getter
                    | OwnerKind.Property PropertyKind.Setter ->
                        failwith $"%A{owner} has an accessor row, but defineConstant set its constant"
                    | _ -> ()

                    cover (ModelReach.Case (caseName step.Value, kind))

                    match List.tryFind (fun (o, _) -> o = owner) model with
                    | Some (_, (oldType, oldBlob)) ->
                        if oldType = modelTypeCode step.Value then
                            cover ModelReach.SecondSameType
                        else
                            cover ModelReach.SecondOtherType

                        let isEmptyString =
                            match step.Value with
                            | ConstantValue.String "" -> true
                            | _ -> false

                        if isEmptyString && not oldBlob.IsEmpty then
                            cover ModelReach.EmptyOverNonEmpty

                        if isEmptyString && oldType = 0x12 then
                            cover ModelReach.NullThenEmpty

                        if isEmptyString && oldBlob.Length = 1 then
                            cover ModelReach.OneByteThenEmpty

                        if isEmptyString && oldBlob.Length = 2 && oldType <> 0x0E then
                            cover ModelReach.TwoBytesThenEmpty

                        if step.Value = ConstantValue.NullReference && oldType <> 0x12 then
                            cover ModelReach.NullOverValue
                    | None ->
                        if step.Value = ConstantValue.String "" then
                            cover ModelReach.EmptyFirst

                    let model = modelStep owner step.Value model
                    scopeRows after |> shouldEqual model

                    for o, _ in owners do
                        let expected =
                            if List.exists (fun (m, _) -> m = o) model then
                                initial.[o] ||| hasDefaultBit o
                            else
                                initial.[o]

                        flagsOf o after |> shouldEqual expected

                    othersUnchanged scope after
                    typeOf owner |> ignore<TypeDefRid>
                    after, model, Some owner
            )

        finalScope, finalModel, owners, initial

    [<Test>]
    let ``defineConstant is the model of _DefineSetConstant`` () : unit =
        let property (cover : ModelReach -> unit) (flags : int list, steps : Step list) : unit =
            runSchedule cover flags steps |> ignore

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 300) (Arb.fromGen genSchedule) property

        for name in allCaseNames do
            for kind in
                [
                    OwnerKind.Field
                    OwnerKind.Param
                    OwnerKind.ReturnParam
                    OwnerKind.Property PropertyKind.NoAccessors
                    OwnerKind.Property PropertyKind.OtherOnly
                ] do
                coverage.Count (ModelReach.Case (name, kind)) |> shouldBeGreaterThan 3

        for label in
            [
                ModelReach.SecondSameType
                ModelReach.SecondOtherType
                ModelReach.EmptyOverNonEmpty
                ModelReach.NullOverValue
                ModelReach.EmptyFirst
                ModelReach.OneByteThenEmpty
                ModelReach.TwoBytesThenEmpty
                ModelReach.NullThenEmpty
                ModelReach.Detaches PropertyKind.Getter
                ModelReach.Detaches PropertyKind.Setter
            ] do
            coverage.Count label |> shouldBeGreaterThan 10

    [<Test>]
    let ``the image holds each owner's constant where SRM finds it`` () : unit =
        let property (cover : ModelReach -> unit) (flags : int list, steps : Step list) : unit =
            let scope, model, owners, initial = runSchedule ignore flags steps
            let pe, reader = readImage (EmitScopeImage.write scope)
            use _ = pe

            for owner, _ in owners do
                let expected =
                    model
                    |> List.tryFind (fun (o, _) -> o = owner)
                    |> Option.map (fun (_, (code, blob)) ->
                        LanguagePrimitives.EnumOfValue<byte, ConstantTypeCode> (byte code), blob
                    )

                imageConstant reader owner |> shouldEqual expected

            reader.GetTableRowCount TableIndex.Constant |> shouldEqual model.Length

            let field2 = HasConstant.Field (FieldRid 2)
            let param1 = HasConstant.Param (ParamRid 1)

            if
                List.exists (fun (o, _) -> o = field2) model
                && List.exists (fun (o, _) -> o = param1) model
            then
                cover ModelReach.FieldTwoAndParamOne

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 300) (Arb.fromGen genSchedule) property

        coverage.Count ModelReach.FieldTwoAndParamOne |> shouldBeGreaterThan 10

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

    let private importOf (moduleBuilder : ModuleBuilder) : Module * obj =
        let runtimeModule = requireType "System.Reflection.RuntimeModule"
        let runtimeModuleBuilder = requireType "System.Reflection.Emit.RuntimeModuleBuilder"

        let internalModule =
            (requireMember (runtimeModuleBuilder.GetProperty ("InternalModule", instance)) "InternalModule").GetValue
                moduleBuilder

        internalModule :?> Module,
        (requireMember (runtimeModule.GetProperty ("MetadataImport", instance)) "RuntimeModule.MetadataImport").GetValue
            internalModule

    /// A row as `MetadataImport.GetDefaultValue` reports it: `None` for no row; for a `STRING` row its code units, the
    /// string `None` when the FCall's pointer is null; for any other its element type, length and the length's low
    /// bytes of the value.
    [<RequireQualifiedAccess>]
    type private RawConstant =
        | NoRow
        | Text of chars : int * text : string option
        | Fixed of code : int * length : int * value : int64

    /// The host's row for `token`.
    let private hostConstant (moduleBuilder : ModuleBuilder) (token : int) : RawConstant =
        let _, import = importOf moduleBuilder

        let getDefaultValue =
            requireMember (import.GetType().GetMethod ("GetDefaultValue", instance)) "MetadataImport.GetDefaultValue"

        let args = [| box token ; null ; null ; null |]
        let text = getDefaultValue.Invoke (import, args) :?> string
        let code = Convert.ToInt32 args.[3]
        let length = unbox<int> args.[2]
        let value = unbox<int64> args.[1]

        match code with
        // `ELEMENT_TYPE_VOID`: no row.
        | 0x01 -> RawConstant.NoRow
        | 0x0E -> RawConstant.Text (length, Option.ofObj text)
        | _ ->
            // Only the low `length` bytes of the union are written.
            let masked =
                if length >= 8 then
                    value
                else
                    value &&& ((1L <<< (8 * length)) - 1L)

            RawConstant.Fixed (code, length, masked)

    /// The mirror's row for `owner`, read by the rule `GetDefaultValue` reads a row by.
    let private mirrorConstant (scope : EmitScope) (owner : HasConstant) : RawConstant =
        match EmitScope.constants scope |> Seq.tryFind (fun row -> row.Parent = owner) with
        | None -> RawConstant.NoRow
        | Some row ->
            let blob = EmitScope.blobAt row.Value scope |> Array.ofSeq

            if row.Type = ConstantTypeCode.String then
                let chars = blob.Length / 2

                let text =
                    if blob.Length = 0 then
                        None
                    else
                        Some (
                            System.String (
                                Array.init chars (fun i -> char (int blob.[2 * i] ||| (int blob.[2 * i + 1] <<< 8)))
                            )
                        )

                RawConstant.Text (chars, text)
            else
                let value =
                    Array.foldBack (fun (b : byte) (acc : int64) -> (acc <<< 8) ||| int64 b) blob 0L

                RawConstant.Fixed (int row.Type, blob.Length, value)

    /// Calls CoreLib's internal `MetadataImport.GetPropertyProps`, whose `out void*` reflection cannot pass, through a
    /// skip-visibility `DynamicMethod`, and returns the flags.
    let private propertyProps : Lazy<Func<Module, int, int>> =
        lazy
            let runtimeModule = requireType "System.Reflection.RuntimeModule"
            let metadataImport = requireType "System.Reflection.MetadataImport"
            let constArray = requireType "System.Reflection.ConstArray"

            let getImport =
                (requireMember (runtimeModule.GetProperty ("MetadataImport", instance)) "RuntimeModule.MetadataImport")
                    .GetMethod

            let props =
                requireMember (metadataImport.GetMethod ("GetPropertyProps", instance)) "GetPropertyProps"

            let dm =
                DynamicMethod (
                    "PropertyProps",
                    typeof<int>,
                    [| typeof<Module> ; typeof<int> |],
                    typeof<obj>.Module,
                    true
                )

            let il = dm.GetILGenerator ()
            let import = il.DeclareLocal metadataImport
            let name = il.DeclareLocal typeof<nativeint>
            let attrs = il.DeclareLocal typeof<PropertyAttributes>
            let signature = il.DeclareLocal constArray
            il.Emit (OpCodes.Ldarg_0)
            il.Emit (OpCodes.Castclass, runtimeModule)
            il.Emit (OpCodes.Call, getImport)
            il.Emit (OpCodes.Stloc, import)
            il.Emit (OpCodes.Ldloca, import)
            il.Emit (OpCodes.Ldarg_1)
            il.Emit (OpCodes.Ldloca, name)
            il.Emit (OpCodes.Ldloca, attrs)
            il.Emit (OpCodes.Ldloca, signature)
            il.Emit (OpCodes.Call, props)
            il.Emit (OpCodes.Ldloc, attrs)
            il.Emit OpCodes.Ret
            dm.CreateDelegate typeof<Func<Module, int, int>> :?> Func<Module, int, int>

    /// The host's flags of the owner whose token is `token`.
    let private hostFlags (moduleBuilder : ModuleBuilder) (token : int) : int =
        let inner, import = importOf moduleBuilder

        match token >>> 24 with
        | 0x04 ->
            let args = [| box token ; null |]

            import.GetType().GetMethod("GetFieldDefProps", instance).Invoke (import, args)
            |> ignore<obj>

            Convert.ToInt32 args.[1]
        | 0x08 ->
            let args = [| box token ; null ; null |]

            import.GetType().GetMethod("GetParamDefProps", instance).Invoke (import, args)
            |> ignore<obj>

            Convert.ToInt32 args.[2]
        | 0x17 -> propertyProps.Value.Invoke (inner, token)
        | _ -> failwith $"0x%08x{token} is no constant's owner"

    let private assemblyCount : int ref = ref 0

    [<RequireQualifiedAccess>]
    type private DifferentialReach =
        | Case of string * string
        | SecondSameType
        | SecondOtherType
        | EmptyOverNonEmpty
        | NullOverValue
        | EmptyFirst
        | OneByteThenEmpty
        | TwoBytesThenEmpty
        | NullThenEmpty
        | Detaches of string

    /// A value the host's emitter can be given: a `Boolean` of 0 or 1. The mirror is given a string cut at its first
    /// NUL, as the QCall's handler cuts it.
    let private genHostValue : Gen<ConstantValue> =
        genScheduleValue
        |> Gen.map (fun value ->
            match value with
            | ConstantValue.Boolean b -> ConstantValue.Boolean (b &&& 1uy)
            | other -> other
        )

    let private cutAtNul (value : ConstantValue) : ConstantValue =
        match value with
        | ConstantValue.String s when s.Contains '\000' -> ConstantValue.String (s.Substring (0, s.IndexOf '\000'))
        | other -> other

    [<Test>]
    let ``each owner's row and flags after SetConstant calls are what the host's emitter writes`` () : unit =
        let property (cover : DifferentialReach -> unit) (steps : (int * ConstantValue) list) : unit =
            let n = Threading.Interlocked.Increment assemblyCount

            let assembly =
                AssemblyBuilder.DefineDynamicAssembly (
                    AssemblyName $"ConstantOracle%d{n}",
                    AssemblyBuilderAccess.RunAndCollect
                )

            let host = assembly.DefineDynamicModule "ConstantOracle"
            let mutable scope = emptyScope Guid.Empty

            let t = host.DefineType ("T", TypeAttributes.Public ||| TypeAttributes.Abstract)

            let mirrorType, s =
                match EmitScopeDefinitions.defineTopLevelTypeDef "T" t.Attributes None [] scope with
                | TypeDefDefinition.Defined (rid, s) -> rid, s
                | TypeDefDefinition.Duplicate _ -> failwith "T is defined once"

            scope <- s

            let objectSignature = ImmutableArray.Create<byte> [| 0x06uy ; 0x1Cuy |]

            let mirrorMethod (name : string) (attributes : MethodAttributes) =
                let m, s =
                    EmitScopeDefinitions.defineMethod
                        mirrorType
                        name
                        (ImmutableArray.Create<byte> [| 0x00uy ; 0x02uy ; 0x1Cuy ; 0x1Cuy ; 0x1Cuy |])
                        attributes
                        scope

                scope <- s
                m

            let token (builder : obj) (field : string) : int =
                builder.GetType().GetField(field, instance).GetValue builder |> unbox<int>

            // The owners: each kind, its host token, how the host sets its constant, and its mirror.
            let m =
                t.DefineMethod (
                    "M",
                    MethodAttributes.Public ||| MethodAttributes.Static,
                    typeof<obj>,
                    [| typeof<obj> ; typeof<obj> |]
                )

            let mm = mirrorMethod "M" m.Attributes

            let param (sequence : int) (name : string) =
                let builder = m.DefineParameter (sequence, ParameterAttributes.None, name)

                match
                    EmitScopeDefinitions.defineParam mm sequence (Option.ofObj name |> Option.defaultValue "") 0 scope
                with
                | ParamDefinition.Defined (rid, s) ->
                    scope <- s
                    token builder "_token", (fun (v : obj) -> builder.SetConstant v), HasConstant.Param rid
                | ParamDefinition.StrandsLaterMethods _ -> failwith "parameters in order strand nothing"

            let returnParam = param 0 null
            let p1 = param 1 "a"
            let p2 = param 2 "b"

            let field (name : string) (attributes : FieldAttributes) =
                let builder = t.DefineField (name, typeof<obj>, attributes)

                let rid, s =
                    EmitScopeDefinitions.defineField mirrorType name objectSignature attributes scope

                scope <- s
                builder.MetadataToken, (fun (v : obj) -> builder.SetConstant v), HasConstant.Field rid

            let literal =
                field "F" (FieldAttributes.Public ||| FieldAttributes.Static ||| FieldAttributes.Literal)

            let instanceField = field "G" FieldAttributes.Public

            let propertyOf (name : string) (accessor : MethodSemanticsAttributes option) =
                let builder =
                    t.DefineProperty (name, PropertyAttributes.None, typeof<obj>, Type.EmptyTypes)

                let rid, s =
                    EmitScopeDefinitions.defineProperty
                        mirrorType
                        name
                        (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x1Cuy |])
                        0
                        scope

                scope <- s

                match accessor with
                | None -> ()
                | Some semantics ->
                    let methodName = $"acc_%s{name}"

                    let a =
                        t.DefineMethod (methodName, MethodAttributes.Public, typeof<obj>, Type.EmptyTypes)

                    let ma = mirrorMethod methodName a.Attributes

                    if semantics = MethodSemanticsAttributes.Other then
                        builder.AddOtherMethod a
                    elif semantics = MethodSemanticsAttributes.Getter then
                        builder.SetGetMethod a
                    else
                        builder.SetSetMethod a

                    scope <- EmitScopeDefinitions.defineMethodSemantics (HasSemantics.Property rid) semantics ma scope

                token builder "m_tkProperty", (fun (v : obj) -> builder.SetConstant v), HasConstant.Property rid

            let noAccessors = propertyOf "P" None
            let otherOnly = propertyOf "Q" (Some MethodSemanticsAttributes.Other)
            let getter = propertyOf "R" (Some MethodSemanticsAttributes.Getter)
            let setter = propertyOf "S" (Some MethodSemanticsAttributes.Setter)

            let owners =
                [
                    "return parameter", returnParam
                    "parameter", p1
                    "parameter", p2
                    "literal field", literal
                    "instance field", instanceField
                    "property", noAccessors
                    "Other-only property", otherOnly
                    "getter property", getter
                    "setter property", setter
                ]

            // The mirror's RIDs are the host's tokens.
            for _, (hostToken, _, mirror) in owners do
                mirror.Token |> shouldEqual hostToken

            let compare () =
                for kind, (hostToken, _, mirror) in owners do
                    if kind <> "getter property" && kind <> "setter property" then
                        mirrorConstant scope mirror |> shouldEqual (hostConstant host hostToken)
                        flagsOf mirror scope |> shouldEqual (hostFlags host hostToken)

            compare ()

            for ownerChoice, value in steps do
                let kind, (_, set, mirror) = owners.[ownerChoice % owners.Length]
                let cut = cutAtNul value

                match EmitScopeDefinitions.defineConstant mirror cut scope with
                | ConstantDefinition.DetachesAccessors ->
                    if kind <> "getter property" && kind <> "setter property" then
                        failwith $"the %s{kind} has no accessor row, but defineConstant says it has"

                    cover (DifferentialReach.Detaches kind)
                | ConstantDefinition.Defined after ->
                    if kind = "getter property" || kind = "setter property" then
                        failwith $"the %s{kind} has an accessor row, but defineConstant set its constant"

                    cover (DifferentialReach.Case (caseName value, kind))

                    match EmitScope.constants scope |> Seq.tryFind (fun row -> row.Parent = mirror) with
                    | Some row ->
                        let oldBlob = EmitScope.blobAt row.Value scope

                        if int row.Type = modelTypeCode cut then
                            cover DifferentialReach.SecondSameType
                        else
                            cover DifferentialReach.SecondOtherType

                        if cut = ConstantValue.String "" then
                            if oldBlob.Length > 0 then
                                cover DifferentialReach.EmptyOverNonEmpty

                            if row.Type = ConstantTypeCode.NullReference then
                                cover DifferentialReach.NullThenEmpty

                            if oldBlob.Length = 1 then
                                cover DifferentialReach.OneByteThenEmpty

                            if oldBlob.Length = 2 && row.Type <> ConstantTypeCode.String then
                                cover DifferentialReach.TwoBytesThenEmpty

                        if cut = ConstantValue.NullReference && row.Type <> ConstantTypeCode.NullReference then
                            cover DifferentialReach.NullOverValue
                    | None ->
                        if cut = ConstantValue.String "" then
                            cover DifferentialReach.EmptyFirst

                    set ((boxed value).Value)
                    scope <- after
                    compare ()

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 150)
                (Arb.fromGen (
                    Gen.choose (1, 30)
                    |> Gen.bind (fun n -> Gen.listOfLength n (Gen.zip (Gen.choose (0, 1000)) genHostValue))
                    |> Gen.map List.ofSeq
                ))
                property

        for name in allCaseNames do
            for kind in
                [
                    "return parameter"
                    "parameter"
                    "literal field"
                    "instance field"
                    "property"
                    "Other-only property"
                ] do
                coverage.Count (DifferentialReach.Case (name, kind)) |> shouldBeGreaterThan 0

        for label in
            [
                DifferentialReach.SecondSameType
                DifferentialReach.SecondOtherType
                DifferentialReach.EmptyOverNonEmpty
                DifferentialReach.NullOverValue
                DifferentialReach.EmptyFirst
                DifferentialReach.OneByteThenEmpty
                DifferentialReach.TwoBytesThenEmpty
                DifferentialReach.NullThenEmpty
                DifferentialReach.Detaches "getter property"
                DifferentialReach.Detaches "setter property"
            ] do
            coverage.Count label |> shouldBeGreaterThan 5
