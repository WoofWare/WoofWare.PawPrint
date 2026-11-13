namespace WoofWare.PawPrint.Test

open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `GenericParamOwnerIndex` against System.Reflection.Metadata's `TypeDefinition.GetGenericParameters`
/// and `MethodDefinition.GetGenericParameters`, for every TypeDef and MethodDef of every image of the
/// framework under test and every image beside the test assembly.
///
/// SRM finds an owner's parameters by a binary search that is right only when the GenericParam table
/// is sorted by owner, so this is an oracle only over sorted images, and it fails on any other rather
/// than comparing. This is the one file that may call those two methods; `TestGenericParamOwnerRatchet`
/// holds everything else to the index.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestGenericParamOwnerOracle =

    let private codedIndex (owner : EntityHandle) : int =
        match owner.Kind with
        | HandleKind.TypeDefinition -> (MetadataTokens.GetRowNumber owner <<< 1) ||| 0
        | HandleKind.MethodDefinition -> (MetadataTokens.GetRowNumber owner <<< 1) ||| 1
        | kind -> failwith $"a GenericParam row's owner is a %O{kind}"

    /// What one image gave: how many owners had parameters, by kind.
    type private Compared =
        {
            TypeOwners : int
            MethodOwners : int
        }

    let private rids (handles : GenericParameterHandle seq) : int list =
        handles |> Seq.map MetadataTokens.GetRowNumber |> List.ofSeq

    /// Compares the index with SRM over every owner of the image at `path`, or `None` if it is not a
    /// managed image.
    let private compare (path : string) : Compared option =
        use stream = File.OpenRead path
        use pe = new PEReader (stream)

        // Framework directories hold native libraries too.
        if not pe.HasMetadata then
            None
        else
            let reader = pe.GetMetadataReader ()
            let rows = reader.GetTableRowCount TableIndex.GenericParam

            for rid in 2..rows do
                let previous =
                    reader.GetGenericParameter (MetadataTokens.GenericParameterHandle (rid - 1))

                let this = reader.GetGenericParameter (MetadataTokens.GenericParameterHandle rid)

                if codedIndex this.Parent < codedIndex previous.Parent then
                    failwith
                        $"%s{path}: GenericParam row %d{rid} sorts before row %d{rid - 1}, so SRM's lookup is no oracle for this image"

            let index =
                match GenericParamOwnerIndex.ofReader reader with
                | Ok index -> index
                | Error error -> failwith $"%s{path}: the index refuses the image: %A{error}"

            let mutable typeOwners = 0
            let mutable methodOwners = 0
            let mutable found = 0

            for handle in reader.TypeDefinitions do
                let expected = rids ((reader.GetTypeDefinition handle).GetGenericParameters ())

                let actual =
                    rids (GenericParamOwnerIndex.parametersOf reader (GenericParamOwner.TypeDef handle) index)

                if actual <> expected then
                    failwith
                        $"%s{path}: TypeDef 0x%08x{MetadataTokens.GetToken (TypeDefinitionHandle.op_Implicit handle : EntityHandle)}: the index has rows %A{actual}, SRM %A{expected}"

                if not expected.IsEmpty then
                    typeOwners <- typeOwners + 1

                found <- found + expected.Length

            for handle in reader.MethodDefinitions do
                let expected = rids ((reader.GetMethodDefinition handle).GetGenericParameters ())

                let actual =
                    rids (GenericParamOwnerIndex.parametersOf reader (GenericParamOwner.MethodDef handle) index)

                if actual <> expected then
                    failwith
                        $"%s{path}: MethodDef 0x%08x{MetadataTokens.GetToken (MethodDefinitionHandle.op_Implicit handle : EntityHandle)}: the index has rows %A{actual}, SRM %A{expected}"

                if not expected.IsEmpty then
                    methodOwners <- methodOwners + 1

                found <- found + expected.Length

            // Every row has an owner that exists, so the owners between them account for every row.
            found |> shouldEqual rows

            Some
                {
                    TypeOwners = typeOwners
                    MethodOwners = methodOwners
                }

    /// Every directory the framework under test is read from, by default and for a guest on the default
    /// kernel.
    let private frameworkDirectories () : string list =
        Seq.append
            (FrameworkUnderTest.runtimeDirs ())
            (FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform)
        |> Seq.distinct
        |> List.ofSeq

    let private compareAll (directory : string) : Compared list =
        Directory.GetFiles (directory, "*.dll")
        |> Array.sort
        |> Array.choose compare
        |> List.ofArray

    [<Test>]
    let ``the index agrees with SRM over the framework under test`` () : unit =
        let directories = frameworkDirectories ()

        directories
        |> List.exists (fun dir -> File.Exists (Path.Combine (dir, "System.Private.CoreLib.dll")))
        |> shouldEqual true

        let compared = directories |> List.collect compareAll

        compared.Length |> shouldBeGreaterThan 150
        compared |> List.sumBy _.TypeOwners |> shouldBeGreaterThan 1000
        compared |> List.sumBy _.MethodOwners |> shouldBeGreaterThan 1000

    [<Test>]
    let ``the index agrees with SRM over the test assemblies`` () : unit =
        let directory = Path.GetDirectoryName typeof<Compared>.Assembly.Location

        let compared = compareAll directory

        compared.Length |> shouldBeGreaterThan 20
        compared |> List.sumBy _.TypeOwners |> shouldBeGreaterThan 1000
        compared |> List.sumBy _.MethodOwners |> shouldBeGreaterThan 1000
