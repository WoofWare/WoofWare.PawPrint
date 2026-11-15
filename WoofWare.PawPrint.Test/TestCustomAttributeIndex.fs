namespace WoofWare.PawPrint.Test

open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// The index of custom attributes that `Assembly.read` builds, over images whose `CustomAttribute` table is sorted by
/// owner, as every compiler writes it: each owner's attribute tokens are the grouping by owner of SRM's enumeration of
/// the table, in that enumeration's order, and every row is in `Attributes`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestCustomAttributeIndex =

    /// A compiled assembly with attributes on every kind of owner a compiler writes, several on one owner.
    let private source : string =
        """
using System;
[assembly: System.Reflection.AssemblyTitle("title")]
[assembly: System.Reflection.AssemblyDescription("description")]
[module: Tag("module")]

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class TagAttribute : Attribute
{
    public TagAttribute(string s) { S = s; }
    public string S;
}

[Tag("a"), Tag("b"), Tag("c")]
public class Owner<[Tag("gp")] T>
{
    [Tag("field")] public int Field;
    [Tag("property")] public int Property { get; set; }
    [Tag("event")] public event Action Event;
    [Tag("method"), Tag("method2")]
    [return: Tag("return")]
    public void Method([Tag("param")] int x) { }
}
"""

    let private assertIndexIsSrmGrouping (dumped : DumpedAssembly) (reader : MetadataReader) : unit =
        let expected = Dictionary<int, ResizeArray<int>> ()

        for handle in reader.CustomAttributes do
            let parent = MetadataTokens.GetToken (reader.GetCustomAttribute handle).Parent

            let tokens =
                match expected.TryGetValue parent with
                | true, tokens -> tokens
                | false, _ ->
                    let tokens = ResizeArray ()
                    expected.[parent] <- tokens
                    tokens

            tokens.Add (MetadataTokens.GetToken (CustomAttributeHandle.op_Implicit handle : EntityHandle))

        let expected =
            expected |> Seq.map (fun kvp -> kvp.Key, List.ofSeq kvp.Value) |> Map.ofSeq

        let actual =
            dumped.CustomAttributesByParentToken
            |> Seq.map (fun kvp -> kvp.Key, List.ofSeq kvp.Value)
            |> Map.ofSeq

        actual |> shouldEqual expected

        dumped.Attributes.Keys
        |> Seq.map (fun (handle : CustomAttributeHandle) ->
            MetadataTokens.GetRowNumber (CustomAttributeHandle.op_Implicit handle : EntityHandle)
        )
        |> Seq.sort
        |> List.ofSeq
        |> shouldEqual [ 1 .. reader.GetTableRowCount TableIndex.CustomAttribute ]

    [<Test>]
    let ``the index of CoreLib is SRM's enumeration grouped by owner`` () : unit =
        let _, loggerFactory = LoggerFactory.makeTest ()
        let bytes = File.ReadAllBytes HostImage.coreLibPath

        let dumped =
            Assembly.read loggerFactory (Some "System.Private.CoreLib.dll") (new MemoryStream (bytes))

        use pe =
            new System.Reflection.PortableExecutable.PEReader (ImmutableArray.Create<byte> bytes)

        assertIndexIsSrmGrouping dumped (pe.GetMetadataReader ())

    [<Test>]
    let ``the index of a compiled assembly is SRM's enumeration grouped by owner`` () : unit =
        let _, loggerFactory = LoggerFactory.makeTest ()

        let image =
            Roslyn.compileAssembly "Indexed" OutputKind.DynamicallyLinkedLibrary [] [ source ]

        let dumped =
            Assembly.read loggerFactory (Some "Indexed.dll") (new MemoryStream (image))

        use pe =
            new System.Reflection.PortableExecutable.PEReader (ImmutableArray.Create<byte> image)

        let reader = pe.GetMetadataReader ()
        // The fixture holds an owner with several rows, so that the order within one owner is compared.
        reader.GetTableRowCount TableIndex.CustomAttribute |> shouldBeGreaterThan 10
        assertIndexIsSrmGrouping dumped reader
