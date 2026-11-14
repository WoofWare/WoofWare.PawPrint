namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsUnitTyped
open NUnit.Framework

/// What CoreCLR does with a FieldLayout row (ECMA-335 II.22.16), the offsets above `Int32.MaxValue`
/// included, on each layout kind. Every case is `T { int A; int B; }`.
///
/// * On an auto- or sequential-layout type the rows are ignored by layout, managed and native, and
///   reflected as a `FieldOffsetAttribute` whose `Value` is the raw column as an `int`.
/// * On an explicit-layout type CoreCLR places each field at its row, and refuses to load the type,
///   in this order, when an offset plus the field's size overflows 32 bits (`OutOfMemoryException`),
///   when an instance field has no row, when an offset is above `Int32.MaxValue`, and when it is above
///   `FIELD_OFFSET_LAST_REAL_OFFSET`. PawPrint refuses each of those by name rather than raising the
///   exception in the guest.
///
/// No C# source can spell these rows, and `PersistedAssemblyBuilder` writes rows only for an
/// explicit-layout type and only with non-negative offsets, so each type is built explicit with a
/// placeholder offset per row, and the saved image is patched: the placeholders to the wanted
/// offsets, and the TypeDef's layout bits to the wanted kind.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedFieldLayoutOffset =

    [<RequireQualifiedAccess>]
    type private Kind =
        | Auto
        | Sequential
        | Explicit

        member this.Attributes : TypeAttributes =
            match this with
            | Kind.Auto -> TypeAttributes.AutoLayout
            | Kind.Sequential -> TypeAttributes.SequentialLayout
            | Kind.Explicit -> TypeAttributes.ExplicitLayout

    type private Spec =
        {
            Name : string
            Kind : Kind
            IsClass : bool
            RowA : uint32 option
            /// `None` both for no row and, in an inline array, for no field `B`.
            RowB : uint32 option
            /// `[InlineArray(n)]`, whose type declares field `A` alone.
            InlineArray : int option
            /// The spec, earlier in the same image, this type derives from; otherwise `object` or
            /// `ValueType`.
            Parent : string option
        }

    /// The placeholder offset `PersistedAssemblyBuilder` writes for row `index`, later patched.
    let private placeholder (index : int) : int = 0x30000000 + index

    let private fabricate (assemblyName : string) (specs : Spec list) : byte[] =
        let builder =
            PersistedAssemblyBuilder (AssemblyName assemblyName, typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule assemblyName

        let mutable wanted : Map<uint32, uint32> = Map.empty
        let mutable nextPlaceholder = 0

        let setRow (field : FieldBuilder) (row : uint32 option) : unit =
            match row with
            | None -> ()
            | Some offset ->
                let p = placeholder nextPlaceholder
                nextPlaceholder <- nextPlaceholder + 1
                field.SetOffset p
                wanted <- wanted.Add (uint32 p, offset)

        let mutable defined : Map<string, TypeBuilder> = Map.empty

        specs
        |> List.iter (fun spec ->
            let isParent = specs |> List.exists (fun other -> other.Parent = Some spec.Name)

            let attrs =
                TypeAttributes.Public
                ||| TypeAttributes.ExplicitLayout
                ||| (if isParent then
                         TypeAttributes.Class
                     else
                         TypeAttributes.Sealed)

            let parent : Type =
                match spec.Parent with
                | Some name -> defined.[name]
                | None -> if spec.IsClass then typeof<obj> else typeof<ValueType>

            let t = modul.DefineType (spec.Name, attrs, parent)
            defined <- defined.Add (spec.Name, t)
            setRow (t.DefineField ("A", typeof<int>, FieldAttributes.Public)) spec.RowA

            match spec.InlineArray with
            | None -> setRow (t.DefineField ("B", typeof<int>, FieldAttributes.Public)) spec.RowB
            | Some length ->
                t.SetCustomAttribute (
                    CustomAttributeBuilder (
                        typeof<System.Runtime.CompilerServices.InlineArrayAttribute>.GetConstructor [| typeof<int> |],
                        [| box length |]
                    )
                )

            if spec.IsClass then
                t.DefineDefaultConstructor MethodAttributes.Public |> ignore<ConstructorBuilder>

            // Created at once, because a derived type's default constructor looks up its
            // parent's, which a `TypeBuilder` answers only once created.
            t.CreateType () |> ignore<Type>
        )

        let bytes =
            use image = new MemoryStream ()
            builder.Save image
            image.ToArray ()

        use peReader = new PEReader (ImmutableArray.CreateRange bytes)
        let mr = peReader.GetMetadataReader ()
        let metadataStart = peReader.PEHeaders.MetadataStartOffset

        let layoutStart = metadataStart + mr.GetTableMetadataOffset TableIndex.FieldLayout

        let layoutRowSize = mr.GetTableRowSize TableIndex.FieldLayout
        let mutable patched = Set.empty

        for row = 0 to mr.GetTableRowCount TableIndex.FieldLayout - 1 do
            let at = layoutStart + row * layoutRowSize
            let current = BitConverter.ToUInt32 (bytes, at)

            match wanted.TryFind current with
            | Some offset ->
                BitConverter.GetBytes(offset).CopyTo (bytes, at)
                patched <- patched.Add current
            | None -> failwith $"FieldLayout row %d{row} holds %x{current}, which is no placeholder"

        patched |> shouldEqual (wanted |> Map.keys |> Set.ofSeq)

        let typeDefStart = metadataStart + mr.GetTableMetadataOffset TableIndex.TypeDef
        let typeDefRowSize = mr.GetTableRowSize TableIndex.TypeDef

        for handle in mr.TypeDefinitions do
            let def = mr.GetTypeDefinition handle

            match specs |> List.tryFind (fun spec -> spec.Name = mr.GetString def.Name) with
            | None -> ()
            | Some spec ->
                // The Flags column is the row's first, four bytes wide.
                let at =
                    typeDefStart
                    + (MetadataTokens.GetRowNumber (TypeDefinitionHandle.op_Implicit handle : EntityHandle)
                       - 1)
                      * typeDefRowSize

                let flags = BitConverter.ToUInt32 (bytes, at)
                flags |> shouldEqual (uint32 def.Attributes)

                let flags =
                    (flags &&& ~~~(uint32 TypeAttributes.LayoutMask))
                    ||| uint32 spec.Kind.Attributes

                BitConverter.GetBytes(flags).CopyTo (bytes, at)

        bytes

    let private kindName (kind : Kind) : string =
        match kind with
        | Kind.Auto -> "Auto"
        | Kind.Sequential -> "Sequential"
        | Kind.Explicit -> "Explicit"

    let private shapeName (isClass : bool) : string = if isClass then "Class" else "Struct"

    /// Every type that loads on real .NET: auto and sequential types with a row on `B` alone, at
    /// offsets either side of 2^31; and explicit types with `A` at 0 and `B` at 16.
    let private loadable : Spec list =
        [
            for kind in [ Kind.Auto ; Kind.Sequential ] do
                for isClass in [ false ; true ] do
                    for offset in [ 0x10u ; 0x7FFFFFFFu ; 0x80000000u ; 0xFFFFFFFFu ] do
                        {
                            Name = $"%s{kindName kind}%s{shapeName isClass}_%08X{offset}"
                            Kind = kind
                            IsClass = isClass
                            RowA = None
                            RowB = Some offset
                            InlineArray = None
                            Parent = None
                        }
            for isClass in [ false ; true ] do
                {
                    Name = $"Explicit%s{shapeName isClass}_00000010"
                    Kind = Kind.Explicit
                    IsClass = isClass
                    RowA = Some 0u
                    RowB = Some 0x10u
                    InlineArray = None
                    Parent = None
                }
        ]

    /// The checks the driver makes of one loadable type, numbered from `code`.
    let private checksOf (code : int) (spec : Spec) : string =
        let rowB = spec.RowB |> Option.get
        let reflected = int rowB

        let placedAt =
            match spec.Kind with
            | Kind.Explicit -> int rowB
            | Kind.Auto
            | Kind.Sequential -> 4

        let instance =
            if spec.IsClass then
                $"var v = new %s{spec.Name} ();"
            else
                $"%s{spec.Name} v = default;"

        let size =
            if spec.IsClass then
                ""
            else
                $"Check (%d{code + 3}, Unsafe.SizeOf<%s{spec.Name}> () == %d{placedAt + 4});"

        let marshal =
            match spec.Kind with
            | Kind.Auto ->
                $"try {{ Marshal.OffsetOf (typeof (%s{spec.Name}), \"B\"); Check (%d{code + 4}, false); }} catch (ArgumentException) {{ }}"
            | Kind.Sequential
            | Kind.Explicit ->
                $"Check (%d{code + 4}, Marshal.OffsetOf (typeof (%s{spec.Name}), \"B\") == (nint) %d{placedAt});"

        $"""
        {{
            Check (%d{code + 1}, Reflected (typeof (%s{spec.Name})) == %d{reflected});
            %s{instance}
            Check (%d{code + 2}, Unsafe.ByteOffset (ref v.A, ref v.B) == (nint) %d{placedAt});
            %s{size}
            %s{marshal}
        }}
"""

    [<Test>]
    let ``rows on auto and sequential types are reflected raw and ignored by layout`` () : unit =
        let checks =
            loadable
            |> List.mapi (fun i spec -> checksOf (10 * (i + 1)) spec)
            |> String.concat ""

        FabricatedGuest.run
            "LayoutRows"
            (fabricate "LayoutRows" loadable)
            "LayoutRowsDriver"
            $"""
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static class Driver
{{
    static int failed;

    static void Check (int code, bool ok)
    {{
        if (failed == 0 && !ok)
        {{
            failed = code;
        }}
    }}

    static int? Reflected (Type t) => t.GetField ("B").GetCustomAttribute<FieldOffsetAttribute> ()?.Value;

    public static int Main (string[] args)
    {{
%s{checks}
        return failed;
    }}
}}
"""
            0

    /// The driver's exit code for each way real .NET refuses the type.
    let private typeLoadGeneral = 10
    let private typeLoadNegativeOffset = 11
    let private outOfMemory = 12
    let private typeLoadNoExplicitOffset = 13

    let private refusalDriver (spec : Spec) : string =
        let instance =
            if spec.IsClass then
                $"return new %s{spec.Name} ().A;"
            else
                $"%s{spec.Name} v = default; return v.A;"

        $"""
using System;
using System.Runtime.CompilerServices;

public static class Driver
{{
    public static int Main (string[] args)
    {{
        try
        {{
            return Probe ();
        }}
        catch (TypeLoadException e) when (e.Message.Contains ("negative offset"))
        {{
            return %d{typeLoadNegativeOffset};
        }}
        catch (TypeLoadException e) when (e.Message.Contains ("not given an explicit offset"))
        {{
            return %d{typeLoadNoExplicitOffset};
        }}
        catch (TypeLoadException)
        {{
            return %d{typeLoadGeneral};
        }}
        catch (OutOfMemoryException)
        {{
            return %d{outOfMemory};
        }}
    }}

    [MethodImpl (MethodImplOptions.NoInlining)]
    static int Probe ()
    {{
        if (typeof (%s{spec.Name}).GetField ("A") == null)
        {{
            return 1;
        }}

        %s{instance}
    }}
}}
"""

    let private messages (e : exn) : string list =
        let rec go (e : exn) : string list =
            match e with
            | null -> []
            | e -> e.Message :: go e.InnerException

        go e

    /// Each explicit layout CoreCLR refuses: `A` at 0 unless the case says otherwise, `B` at the given
    /// offset; the driver's exit code on real .NET; and the text PawPrint's refusal must name.
    let private refusals : (string * uint32 option * uint32 * int * string) list =
        [
            "OverLastRealOffset", Some 0u, 0x07FFFFF9u, typeLoadGeneral, "FIELD_OFFSET_LAST_REAL_OFFSET"
            "Int32Max", Some 0u, 0x7FFFFFFFu, typeLoadGeneral, "FIELD_OFFSET_LAST_REAL_OFFSET"
            "TwoToThe31", Some 0u, 0x80000000u, typeLoadNegativeOffset, "IDS_CLASSLOAD_NSTRUCT_NEGATIVE_OFFSET"
            "LastWithoutOverflow", Some 0u, 0xFFFFFFFBu, typeLoadNegativeOffset, "IDS_CLASSLOAD_NSTRUCT_NEGATIVE_OFFSET"
            "EndOverflows", Some 0u, 0xFFFFFFFCu, outOfMemory, "OutOfMemoryException"
            "AllOnes", Some 0u, 0xFFFFFFFFu, outOfMemory, "OutOfMemoryException"
            "NoRowOnA", None, 0x10u, typeLoadNoExplicitOffset, "IDS_CLASSLOAD_NSTRUCT_EXPLICIT_OFFSET"
        ]

    let refusalCases : TestCaseData seq =
        seq {
            for name, rowA, rowB, exitCode, refusal in refusals do
                for isClass in [ false ; true ] do
                    TestCaseData(
                        {
                            Name = $"Explicit%s{shapeName isClass}"
                            Kind = Kind.Explicit
                            IsClass = isClass
                            RowA = rowA
                            RowB = Some rowB
                            InlineArray = None
                            Parent = None
                        },
                        exitCode,
                        refusal
                    )
                        .SetName
                        $"explicit %s{(shapeName isClass).ToLowerInvariant ()} refused: %s{name}"
        }

    [<TestCaseSource(nameof refusalCases)>]
    let ``explicit layouts CoreCLR refuses to load are refused by name``
        (spec : obj, exitCode : int, refusal : string)
        : unit
        =
        let spec = spec :?> Spec

        let onHost, onPawPrint =
            FabricatedGuest.runOnBoth "Refused" (fabricate "Refused" [ spec ]) "RefusedDriver" (refusalDriver spec)

        onHost |> shouldEqual (RealRuntimeResult.NormalExit exitCode)

        match onPawPrint with
        | FabricatedOutcome.Exited code ->
            failwith $"PawPrint ran the guest to exit code %d{code}, but CoreCLR refuses its type"
        | FabricatedOutcome.Failed e ->
            let messages = messages e

            if not (messages |> List.exists (fun m -> m.Contains refusal)) then
                failwith
                    $"expected PawPrint's refusal to name %s{refusal}; got:\n%s{String.concat Environment.NewLine messages}"

    /// CoreCLR refuses `[InlineArray]` on a type that declares explicit layout, and asks nothing of
    /// the field's FieldLayout row: on a sequential type the row is reflected and ignored.
    [<Test>]
    let ``a row on a sequential inline array is ignored`` () : unit =
        let spec =
            {
                Name = "SequentialInline"
                Kind = Kind.Sequential
                IsClass = false
                RowA = Some 0x80000000u
                RowB = None
                InlineArray = Some 3
                Parent = None
            }

        FabricatedGuest.run
            "InlineRows"
            (fabricate "InlineRows" [ spec ])
            "InlineRowsDriver"
            """
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static class Driver
{
    public static int Main (string[] args)
    {
        if (typeof (SequentialInline).GetField ("A").GetCustomAttribute<FieldOffsetAttribute> ()?.Value != int.MinValue)
        {
            return 1;
        }

        if (Unsafe.SizeOf<SequentialInline> () != 12)
        {
            return 2;
        }

        SequentialInline v = default;
        Unsafe.Add (ref v.A, 2) = 7;
        return Unsafe.Add (ref v.A, 2) == 7 ? 0 : 3;
    }
}
"""
            0

    [<Test>]
    let ``an explicit inline array is refused by name`` () : unit =
        let spec =
            {
                Name = "ExplicitInline"
                Kind = Kind.Explicit
                IsClass = false
                RowA = Some 0u
                RowB = None
                InlineArray = Some 3
                Parent = None
            }

        let onHost, onPawPrint =
            FabricatedGuest.runOnBoth "Refused" (fabricate "Refused" [ spec ]) "RefusedDriver" (refusalDriver spec)

        onHost |> shouldEqual (RealRuntimeResult.NormalExit typeLoadGeneral)

        match onPawPrint with
        | FabricatedOutcome.Exited code ->
            failwith $"PawPrint ran the guest to exit code %d{code}, but CoreCLR refuses its type"
        | FabricatedOutcome.Failed e ->
            let messages = messages e

            if
                not (
                    messages
                    |> List.exists (fun m -> m.Contains "IDS_CLASSLOAD_INLINE_ARRAY_EXPLICIT")
                )
            then
                failwith
                    $"expected PawPrint's refusal to name IDS_CLASSLOAD_INLINE_ARRAY_EXPLICIT; got:\n%s{String.concat Environment.NewLine messages}"

    /// CoreCLR checks an explicit offset after adding the parent's instance size: `B` at 0x7FFFFFFE
    /// alone is only beyond `FIELD_OFFSET_LAST_REAL_OFFSET`, but over an 8-byte parent it is above
    /// `Int32.MaxValue`.
    [<Test>]
    let ``an explicit offset is checked with the parent's size added`` () : unit =
        let parent =
            {
                Name = "SequentialBase"
                Kind = Kind.Sequential
                IsClass = true
                RowA = None
                RowB = None
                InlineArray = None
                Parent = None
            }

        let derived =
            {
                Name = "ExplicitDerived"
                Kind = Kind.Explicit
                IsClass = true
                RowA = Some 0u
                RowB = Some 0x7FFFFFFEu
                InlineArray = None
                Parent = Some parent.Name
            }

        let onHost, onPawPrint =
            FabricatedGuest.runOnBoth
                "Refused"
                (fabricate "Refused" [ parent ; derived ])
                "RefusedDriver"
                (refusalDriver derived)

        onHost |> shouldEqual (RealRuntimeResult.NormalExit typeLoadNegativeOffset)

        match onPawPrint with
        | FabricatedOutcome.Exited code ->
            failwith $"PawPrint ran the guest to exit code %d{code}, but CoreCLR refuses its type"
        | FabricatedOutcome.Failed e ->
            let messages = messages e

            if
                not (
                    messages
                    |> List.exists (fun m -> m.Contains "IDS_CLASSLOAD_NSTRUCT_NEGATIVE_OFFSET")
                )
            then
                failwith
                    $"expected PawPrint's refusal to name IDS_CLASSLOAD_NSTRUCT_NEGATIVE_OFFSET; got:\n%s{String.concat Environment.NewLine messages}"
