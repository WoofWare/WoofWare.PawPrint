namespace WoofWare.PawPrint.Test

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Reflection.Metadata.Ecma335
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// `TypeVisibility`, the TypeDef half of `Type.IsVisible`: the rule over a nesting chain, checked
/// exhaustively against the sentence that states it, and the gathering of a chain from an image,
/// including the malformed images it refuses.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTypeVisibility =

    let private allVisibilities : TypeAttributes list =
        [
            TypeAttributes.NotPublic
            TypeAttributes.Public
            TypeAttributes.NestedPublic
            TypeAttributes.NestedPrivate
            TypeAttributes.NestedFamily
            TypeAttributes.NestedAssembly
            TypeAttributes.NestedFamANDAssem
            TypeAttributes.NestedFamORAssem
        ]

    /// Every chain of exactly `length` levels.
    let rec private chainsOfLength (length : int) : TypeAttributes list list =
        if length = 0 then
            [ [] ]
        else
            [
                for rest in chainsOfLength (length - 1) do
                    for level in allVisibilities do
                        yield level :: rest
            ]

    /// The rule as the decisions state it: every level but the last is `NestedPublic`, and the last is
    /// `Public`.
    let private model (chain : TypeAttributes list) : bool =
        match List.rev chain with
        | [] -> false
        | last :: innerReversed ->
            last = TypeAttributes.Public
            && innerReversed |> List.forall (fun level -> level = TypeAttributes.NestedPublic)

    [<Test>]
    let ``The chain rule agrees with its model on every chain of up to five levels`` () : unit =
        let chains = [ 1..5 ] |> List.collect chainsOfLength
        chains.Length |> shouldEqual (8 + 64 + 512 + 4096 + 32768)

        let disagreements =
            chains
            |> List.filter (fun chain -> TypeVisibility.isChainExternallyVisible chain <> model chain)

        disagreements |> shouldEqual []

        chains
        |> List.filter TypeVisibility.isChainExternallyVisible
        |> List.length
        |> shouldEqual 5

    [<Test>]
    let ``The chain rule masks each level's visibility`` () : unit =
        let decorate (level : TypeAttributes) : TypeAttributes =
            level
            ||| TypeAttributes.Sealed
            ||| TypeAttributes.Abstract
            ||| TypeAttributes.BeforeFieldInit

        for chain in [ 1..3 ] |> List.collect chainsOfLength do
            TypeVisibility.isChainExternallyVisible (List.map decorate chain)
            |> shouldEqual (model chain)

    [<Test>]
    let ``An empty chain is invisible`` () : unit =
        TypeVisibility.isChainExternallyVisible [] |> shouldEqual false

    let private source : string =
        """
public class Outer
{
    public class Middle
    {
        protected class Inner { }
    }
}

internal class Lone { }
"""

    let private loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory =
        let _, loggerFactory = LoggerFactory.makeTest ()
        loggerFactory

    let private image : DumpedAssembly =
        let bytes =
            Roslyn.compileAssembly "TypeVisibilityChains" OutputKind.DynamicallyLinkedLibrary [] [ source ]

        use stream = new MemoryStream (bytes)
        AssemblyApi.read loggerFactory None stream

    let private typeNamed (assembly : DumpedAssembly) (name : string) : TypeInfo<GenericParamFromMetadata, TypeDefn> =
        assembly.TypeDefs.Values
        |> Seq.filter (fun ty -> ty.Name = name)
        |> Seq.exactlyOne

    [<Test>]
    let ``The chain of a nested type is its own visibility then each encloser's, innermost first`` () : unit =
        let visibility (attrs : TypeAttributes) = attrs &&& TypeAttributes.VisibilityMask

        TypeVisibility.nestingChain image (typeNamed image "Inner")
        |> List.map visibility
        |> shouldEqual
            [
                TypeAttributes.NestedFamily
                TypeAttributes.NestedPublic
                TypeAttributes.Public
            ]

        TypeVisibility.nestingChain image (typeNamed image "Lone")
        |> List.map visibility
        |> shouldEqual [ TypeAttributes.NotPublic ]

        TypeVisibility.isTypeDefExternallyVisible image (typeNamed image "Middle")
        |> shouldEqual true

        TypeVisibility.isTypeDefExternallyVisible image (typeNamed image "Inner")
        |> shouldEqual false

    let private withTypeDefs
        (assembly : DumpedAssembly)
        (replace : TypeInfo<GenericParamFromMetadata, TypeDefn> -> TypeInfo<GenericParamFromMetadata, TypeDefn>)
        : DumpedAssembly
        =
        let replaced = Dictionary<_, _> ()

        for KeyValue (handle, ty) in assembly.TypeDefs do
            replaced.[handle] <- replace ty

        { assembly with
            TypeDefs = replaced
        }

    [<Test>]
    let ``An encloser absent from the image is a failure naming the type`` () : unit =
        let absent = MetadataTokens.TypeDefinitionHandle 9999

        let broken =
            withTypeDefs
                image
                (fun ty ->
                    if ty.Name = "Middle" then
                        { ty with
                            DeclaringType = absent
                        }
                    else
                        ty
                )

        let e =
            Assert.Throws<Exception> (fun () -> TypeVisibility.nestingChain broken (typeNamed broken "Inner") |> ignore)

        e.Message |> shouldContainText "Middle"

    [<Test>]
    let ``A cycle among NestedClass rows is a failure naming the type`` () : unit =
        let outer = typeNamed image "Outer"
        let middle = typeNamed image "Middle"

        let broken =
            withTypeDefs
                image
                (fun ty ->
                    if ty.Name = "Outer" then
                        { ty with
                            DeclaringType = middle.TypeDefHandle
                            TypeAttributes =
                                (ty.TypeAttributes &&& ~~~TypeAttributes.VisibilityMask)
                                ||| TypeAttributes.NestedPublic
                        }
                    else
                        ty
                )

        outer.Name |> shouldEqual "Outer"

        let e =
            Assert.Throws<Exception> (fun () -> TypeVisibility.nestingChain broken (typeNamed broken "Inner") |> ignore)

        e.Message |> shouldContainText "Inner"
