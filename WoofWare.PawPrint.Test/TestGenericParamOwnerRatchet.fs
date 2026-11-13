namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Text.RegularExpressions
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// System.Reflection.Metadata's `TypeDefinition.GetGenericParameters` and
/// `MethodDefinition.GetGenericParameters` binary-search the GenericParam table, so they answer
/// rightly only when it is sorted by owner, and wrongly and quietly (empty, or short) when it is not.
/// PawPrint finds an owner's parameters through `GenericParamOwnerIndex` instead, which reads the
/// table by RID. So:
///
/// * in the built assemblies of PawPrint that are not tests, only the index's own module may call
///   either method, which a scan of each assembly's IL for the MemberRefs naming them checks;
/// * in the test project, only `TestGenericParamOwnerOracle.fs`, which checks the index against them
///   over sorted images, may name them, which a scan of the text of every file the test project
///   compiles checks, comments included. It sees names, not values: it cannot see a member reached
///   through reflection by a string.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestGenericParamOwnerRatchet =

    [<Literal>]
    let private MethodName = "GetGenericParameters"

    /// The assembly and the full name of the type of the only code allowed to call either method.
    let private allowedCaller : string * string =
        "WoofWare.PawPrint.Domain", "WoofWare.PawPrint.GenericParamOwnerIndexModule"

    let private fullName (reader : MetadataReader) (handle : TypeDefinitionHandle) : string =
        let definition = reader.GetTypeDefinition handle
        let name = reader.GetString definition.Name

        match reader.GetString definition.Namespace with
        | "" -> name
        | ns -> ns + "." + name

    /// The full name of the type that `handle` is, or is nested in, at any depth.
    let rec private outermost (reader : MetadataReader) (handle : TypeDefinitionHandle) : string =
        let declaring = (reader.GetTypeDefinition handle).GetDeclaringType ()

        if declaring.IsNil then
            fullName reader handle
        else
            outermost reader declaring

    /// The MemberRefs of `reader` naming either method.
    let private forbiddenReferences (reader : MetadataReader) : Set<int> =
        reader.MemberReferences
        |> Seq.filter (fun handle ->
            let reference = reader.GetMemberReference handle

            reader.GetString reference.Name = MethodName
            && reference.Parent.Kind = HandleKind.TypeReference
            && (let parent =
                    reader.GetTypeReference (TypeReferenceHandle.op_Explicit reference.Parent)

                reader.GetString parent.Namespace = "System.Reflection.Metadata"
                && (reader.GetString parent.Name = "TypeDefinition"
                    || reader.GetString parent.Name = "MethodDefinition"))
        )
        |> Seq.map (fun handle -> MetadataTokens.GetToken (MemberReferenceHandle.op_Implicit handle : EntityHandle))
        |> Set.ofSeq

    /// Every instruction of `image` naming either method, as the outermost type and the name of the method
    /// holding it.
    let private callsIn (image : PEReader) : (string * string) list =
        let reader = image.GetMetadataReader ()
        let forbidden = forbiddenReferences reader

        // An instruction can name only a MemberRef the image has.
        if forbidden.IsEmpty then
            []
        else
            let assembly = reader.GetAssemblyDefinition().GetAssemblyName ()

            [
                for handle in reader.MethodDefinitions do
                    let method = reader.GetMethodDefinition handle

                    if method.RelativeVirtualAddress <> 0 then
                        let body = image.GetMethodBody method.RelativeVirtualAddress

                        for op, _ in
                            IlDecoding.decodeInstructions (IlTokenUniverse.Metadata assembly) (body.GetILBytes ()) do
                            match op with
                            | IlOp.UnaryMetadataToken (_, MetadataOperand.FromMetadata operand) when
                                forbidden.Contains (MetadataToken.toInt operand.Token)
                                ->
                                yield outermost reader (method.GetDeclaringType ()), reader.GetString method.Name
                            | _ -> ()
            ]

    /// Every call in `calls` (assembly name, outermost type, method) from code other than `allowed`.
    let private violations (allowed : string * string) (calls : (string * (string * string) list) list) : string list =
        [
            for assembly, inAssembly in calls do
                for outer, method in inAssembly do
                    if (assembly, outer) <> allowed then
                        yield
                            $"%s{assembly}: %s{outer}, in %s{method}, calls SRM's %s{MethodName}; find an owner's generic parameters through GenericParamOwnerIndex.parametersOf"
        ]

    /// The assemblies of PawPrint that this test assembly references, directly or not, other than tests.
    let private nonTestAssemblies () : System.Reflection.AssemblyName list =
        let rec walk
            (seen : Map<string, System.Reflection.AssemblyName>)
            (pending : System.Reflection.AssemblyName list)
            : Map<string, System.Reflection.AssemblyName>
            =
            match pending with
            | [] -> seen
            | name :: rest when seen.ContainsKey name.Name -> walk seen rest
            | name :: rest ->
                let references =
                    (System.Reflection.Assembly.Load name).GetReferencedAssemblies ()
                    |> Seq.filter (fun r -> r.Name.StartsWith ("WoofWare.PawPrint", StringComparison.Ordinal))
                    |> List.ofSeq

                walk (Map.add name.Name name seen) (references @ rest)

        walk Map.empty [ typeof<Coverage<int>>.Assembly.GetName () ]
        |> Map.values
        |> Seq.filter (fun name -> not (name.Name.Contains (".Test", StringComparison.Ordinal)))
        |> List.ofSeq

    let private callsInFile (path : string) : (string * string) list =
        use stream = File.OpenRead path
        use image = new PEReader (stream)
        callsIn image

    [<Test>]
    let ``only GenericParamOwnerIndex calls SRM's lookup of an owner's generic parameters`` () : unit =
        let assemblies = nonTestAssemblies ()

        // Guards against a walk that finds too little, which would pass vacuously.
        let names = assemblies |> List.map _.Name |> Set.ofList

        for expected in
            [
                "WoofWare.PawPrint"
                "WoofWare.PawPrint.Domain"
                "WoofWare.PawPrint.Loader"
                "WoofWare.PawPrint.TypeSystem"
                "WoofWare.PawPrint.Semantics"
                "WoofWare.PawPrint.Analysis"
                "WoofWare.PawPrint.App"
            ] do
            names |> shouldContain expected

        let calls =
            [
                for name in assemblies do
                    yield name.Name, callsInFile (System.Reflection.Assembly.Load name).Location
            ]

        // The allowance names a type that exists.
        let domain = System.Reflection.Assembly.Load (fst allowedCaller)
        domain.GetType (snd allowedCaller) |> isNull |> shouldEqual false

        violations allowedCaller calls |> shouldBeEmpty

    /// A library in which `Allowed` and `Elsewhere` each call both methods, `Elsewhere` once from a lambda.
    let private planted : Lazy<byte[]> =
        lazy
            (Roslyn.compileAssembly
                "PlantedGenericParameterLookups"
                OutputKind.DynamicallyLinkedLibrary
                []
                [
                    """
using System;
using System.Reflection.Metadata;

namespace Planted
{
    public static class Allowed
    {
        public static int OfType(MetadataReader r, TypeDefinitionHandle h) => r.GetTypeDefinition(h).GetGenericParameters().Count;
        public static int OfMethod(MetadataReader r, MethodDefinitionHandle h) => r.GetMethodDefinition(h).GetGenericParameters().Count;
    }

    public static class Elsewhere
    {
        public static int OfType(MetadataReader r, TypeDefinitionHandle h) => r.GetTypeDefinition(h).GetGenericParameters().Count;
        public static Func<MethodDefinition, int> OfMethod() => m => m.GetGenericParameters().Count;
        public static int Unrelated(Type t) => t.GetGenericArguments().Length;
    }
}
"""
                ])

    [<Test>]
    let ``the IL scan finds each call, inside a lambda too`` () : unit =
        use image = new PEReader (ImmutableArray.Create<byte> (planted.Force ()))

        let calls = callsIn image

        calls
        |> List.map fst
        |> List.sort
        |> shouldEqual
            [
                "Planted.Allowed"
                "Planted.Allowed"
                "Planted.Elsewhere"
                "Planted.Elsewhere"
            ]

        violations ("PlantedGenericParameterLookups", "Planted.Allowed") [ "PlantedGenericParameterLookups", calls ]
        |> List.length
        |> shouldEqual 2

        // The allowance is for one assembly's type, not for a type of that name anywhere.
        violations ("WoofWare.PawPrint.Domain", "Planted.Allowed") [ "PlantedGenericParameterLookups", calls ]
        |> List.length
        |> shouldEqual 4

    type private Allowance =
        {
            File : string
            /// Exactly how many times `File` names either method, so a second use there is not waved
            /// through by the first one's reason.
            Count : int
            Reason : string
        }

    let private allowances : Allowance list =
        [
            {
                File = "TestGenericParamOwnerOracle.fs"
                Count = 4
                Reason = "the oracle, which checks the index against both methods over sorted images"
            }
        ]

    let private pattern = Regex (Regex.Escape MethodName + @"\b")

    /// Every way `sources` (file name, text) departs from the rule.
    let private textViolations (sources : (string * string) list) : string list =
        let files = sources |> List.map fst |> Set.ofList

        [
            for allowance in allowances do
                if not (files.Contains allowance.File) then
                    yield $"an allowance names %s{allowance.File}, which the test project does not compile"

            for file, text in sources do
                if file <> "TestGenericParamOwnerRatchet.fs" then
                    let found = pattern.Matches(text).Count

                    let expected =
                        allowances
                        |> List.tryFind (fun allowance -> allowance.File = file)
                        |> Option.map _.Count
                        |> Option.defaultValue 0

                    if found <> expected then
                        yield
                            $"%s{file} names %s{MethodName} %d{found} time(s), where %d{expected} are allowed: find an owner's generic parameters through GenericParamOwnerIndex.parametersOf"
        ]

    /// The prefix the project file gives each embedded source's resource name.
    [<Literal>]
    let private ResourcePrefix = "PawPrintTestSource/"

    let private compiledSources () : (string * string) list =
        let assembly = typeof<Allowance>.Assembly

        [
            for resource in assembly.GetManifestResourceNames () do
                if resource.StartsWith (ResourcePrefix, StringComparison.Ordinal) then
                    use stream = assembly.GetManifestResourceStream resource
                    use reader = new StreamReader (stream)
                    yield resource.Substring ResourcePrefix.Length, reader.ReadToEnd ()
        ]

    [<Test>]
    let ``only the oracle's file names SRM's lookup of an owner's generic parameters`` () : unit =
        let sources = compiledSources ()

        // Guards against the embedding matching nothing, which would pass vacuously.
        sources.Length |> shouldBeGreaterThan 200

        textViolations sources |> shouldBeEmpty

    /// A copy of the real sources with `planted` appended to `file`.
    let private plantedIn (file : string) (planted : string) : (string * string) list =
        let sources = compiledSources ()
        sources |> List.map fst |> shouldContain file

        sources
        |> List.map (fun (name, text) ->
            if name = file then
                name, text + "\n" + planted
            else
                name, text
        )

    [<TestCase("let ps = typeDef.GetGenericParameters ()")>]
    [<TestCase("(reader.GetMethodDefinition m).GetGenericParameters() |> Seq.length")>]
    [<TestCase("// SRM's MethodDefinition.GetGenericParameters")>]
    let ``a planted call is caught`` (planted : string) : unit =
        textViolations (plantedIn "TestEmitScopeImage.fs" planted)
        |> List.exactlyOne
        |> shouldContainText "TestEmitScopeImage.fs names GetGenericParameters 1 time(s), where 0 are allowed"

        textViolations (plantedIn "TestGenericParamOwnerOracle.fs" planted)
        |> List.exactlyOne
        |> shouldContainText "TestGenericParamOwnerOracle.fs names GetGenericParameters 5 time(s), where 4 are allowed"

    [<Test>]
    let ``a name that only contains the method's is not caught`` () : unit =
        let planted =
            "let a = t.GetGenericParameterConstraints ()\nlet b = m.GetGenericArguments ()\nlet c = GenericParamOwnerIndex.parametersOf"

        textViolations (plantedIn "TestEmitScopeImage.fs" planted) |> shouldBeEmpty
