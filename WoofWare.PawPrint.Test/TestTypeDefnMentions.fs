namespace WoofWare.PawPrint.Test

open System.Collections.Immutable
open System.Reflection.Metadata
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `TypeDefn.mentionsGenericTypeParameter` and `mentionsGenericMethodParameter` against a naive
/// reference: whether the union's structural printing names the case anywhere.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTypeDefnMentions =

    let private genTypeDefn : Gen<TypeDefn> =
        let leaf =
            Gen.oneof
                [
                    Gen.elements [ PrimitiveType.Int32 ; PrimitiveType.String ; PrimitiveType.Object ]
                    |> Gen.map TypeDefn.PrimitiveType
                    Gen.constant TypeDefn.Void
                    Gen.choose (0, 2) |> Gen.map TypeDefn.GenericTypeParameter
                    Gen.choose (0, 2) |> Gen.map TypeDefn.GenericMethodParameter
                ]

        let rec go (depth : int) : Gen<TypeDefn> =
            if depth <= 0 then
                leaf
            else
                let sub = go (depth - 1)

                Gen.frequency
                    [
                        3, leaf
                        1, Gen.map2 (fun e r -> TypeDefn.Array (e, r)) sub (Gen.choose (1, 3))
                        1, sub |> Gen.map TypeDefn.Pinned
                        1, sub |> Gen.map TypeDefn.Pointer
                        1, sub |> Gen.map TypeDefn.Byref
                        1, sub |> Gen.map TypeDefn.OneDimensionalArrayLowerBoundZero
                        2,
                        gen {
                            let! unmodified = sub
                            let! modifier = sub
                            let! isRequired = Gen.elements [ true ; false ]

                            return
                                TypeDefn.Modified
                                    {
                                        Unmodified = unmodified
                                        Modifier = modifier
                                        IsRequired = isRequired
                                    }
                        }
                        2,
                        gen {
                            let! generic = sub
                            let! n = Gen.choose (0, 3)
                            let! arguments = Gen.listOfLength n sub
                            return TypeDefn.GenericInstantiation (generic, ImmutableArray.CreateRange arguments)
                        }
                        2,
                        gen {
                            let! returns = Gen.optionOf sub
                            let! n = Gen.choose (0, 3)
                            let! parameters = Gen.listOfLength n sub

                            return
                                TypeDefn.FunctionPointer
                                    {
                                        Header =
                                            ComparableSignatureHeader.Make (
                                                SignatureHeader (
                                                    SignatureKind.Method,
                                                    SignatureCallingConvention.Default,
                                                    SignatureAttributes.None
                                                )
                                            )
                                        ParameterTypes = List.ofSeq parameters
                                        GenericParameterCount = 0
                                        RequiredParameterCount = n
                                        ReturnType =
                                            match returns with
                                            | Some r -> MethodReturnType.Returns r
                                            | None -> MethodReturnType.Void
                                    }
                        }
                    ]

        Gen.choose (0, 4) |> Gen.bind go

    [<Test>]
    let ``Each mention check agrees with the type's structural printing`` () : unit =
        let property (cover : string * bool -> unit) (t : TypeDefn) : unit =
            let printed = sprintf "%A" t
            let typeParameter = printed.Contains "GenericTypeParameter"
            let methodParameter = printed.Contains "GenericMethodParameter"
            cover ("type parameter", typeParameter)
            cover ("method parameter", methodParameter)
            TypeDefn.mentionsGenericTypeParameter t |> shouldEqual typeParameter
            TypeDefn.mentionsGenericMethodParameter t |> shouldEqual methodParameter

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 1000) (Arb.fromGen genTypeDefn) property

        for label in
            [
                "type parameter", true
                "type parameter", false
                "method parameter", true
                "method parameter", false
            ] do
            coverage.Count label |> shouldBeGreaterThan 100
