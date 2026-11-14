namespace WoofWare.PawPrint.Test

open System.Reflection
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `NewobjTarget`, over every combination of the facts it reads, against its order written out as
/// a list of conditions: the first that holds is the answer. `TestNewobjTargetOnHost` holds the
/// same function to the real JIT over every method of CoreLib.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestNewobjTarget =

    /// The facts the rule is stated over, read from attributes as CoreCLR reads them.
    type private Facts =
        {
            Abstract : bool
            Static : bool
            InstanceConstructor : bool
            AbstractType : bool
            ValueType : bool
        }

    let private factsOf
        (method : MethodAttributes)
        (name : string)
        (declaringType : TypeAttributes)
        (valueType : bool)
        : Facts
        =
        {
            Abstract = method &&& MethodAttributes.Abstract <> enum 0
            Static = method &&& MethodAttributes.Static <> enum 0
            InstanceConstructor = method &&& MethodAttributes.RTSpecialName <> enum 0 && name = ".ctor"
            AbstractType = declaringType &&& TypeAttributes.Abstract <> enum 0
            ValueType = valueType
        }

    /// The order: an abstract method; a static method; a method that is not an instance
    /// constructor; an abstract declaring type that is not a value type; otherwise it constructs.
    let private order : ((Facts -> bool) * NewobjTarget) list =
        [
            (fun f -> f.Abstract), NewobjTarget.AbstractMethod
            (fun f -> f.Static), NewobjTarget.StaticMethod
            (fun f -> not f.InstanceConstructor), NewobjTarget.NotInstanceConstructor
            (fun f -> f.AbstractType && not f.ValueType), NewobjTarget.AbstractClass
        ]

    let private expected (facts : Facts) : NewobjTarget =
        order
        |> List.tryPick (fun (holds, outcome) -> if holds facts then Some outcome else None)
        |> Option.defaultValue NewobjTarget.Constructs

    let private names : string list =
        [ ".ctor" ; ".cctor" ; "M" ; ".CTOR" ; "ctor" ; ".ctor " ]

    [<Test>]
    let ``every combination of the facts gives the outcome its place in the order gives`` () : unit =
        let mutable seen : Map<NewobjTarget, int> = Map.empty

        for isAbstract in [ false ; true ] do
            for isStatic in [ false ; true ] do
                for rtSpecialName in [ false ; true ] do
                    for name in names do
                        for abstractType in [ false ; true ] do
                            for valueType in [ false ; true ] do
                                let method =
                                    MethodAttributes.Public
                                    ||| (if isAbstract then
                                             MethodAttributes.Abstract ||| MethodAttributes.Virtual
                                         else
                                             enum 0)
                                    ||| (if isStatic then MethodAttributes.Static else enum 0)
                                    ||| (if rtSpecialName then
                                             MethodAttributes.RTSpecialName ||| MethodAttributes.SpecialName
                                         else
                                             enum 0)

                                let declaringType =
                                    TypeAttributes.Public
                                    ||| (if abstractType then TypeAttributes.Abstract else enum 0)

                                let actual = NewobjTarget.classify method name declaringType valueType

                                actual |> shouldEqual (expected (factsOf method name declaringType valueType))

                                seen <- seen |> Map.change actual (fun n -> Some (1 + defaultArg n 0))

        // Every outcome is reached, so that no row of the order is vacuous.
        seen
        |> Map.toList
        |> shouldEqual
            [
                // 2 static x 2 rtSpecialName x 6 names x 2 x 2
                NewobjTarget.AbstractMethod, 96
                // not abstract: 2 rtSpecialName x 6 names x 2 x 2
                NewobjTarget.StaticMethod, 48
                // neither: every name but `.ctor` with the flag, and every name without it
                NewobjTarget.NotInstanceConstructor, 44
                // an instance constructor of an abstract type that is not a value type
                NewobjTarget.AbstractClass, 1
                NewobjTarget.Constructs, 3
            ]

    [<Test>]
    let ``an instance constructor is RTSpecialName and the name .ctor, whatever else the method is`` () : unit =
        let cases =
            [
                MethodAttributes.RTSpecialName ||| MethodAttributes.SpecialName, ".ctor", true
                MethodAttributes.RTSpecialName, ".ctor", true
                MethodAttributes.RTSpecialName ||| MethodAttributes.Static, ".ctor", true
                MethodAttributes.SpecialName, ".ctor", false
                enum 0, ".ctor", false
                MethodAttributes.RTSpecialName ||| MethodAttributes.SpecialName, ".cctor", false
                MethodAttributes.RTSpecialName ||| MethodAttributes.SpecialName, ".CTOR", false
                MethodAttributes.RTSpecialName ||| MethodAttributes.SpecialName, "M", false
            ]

        for attributes, name, answer in cases do
            NewobjTarget.isInstanceConstructor attributes name |> shouldEqual answer

    /// Bits other than the ones the order names change nothing: attribute words drawn uniformly
    /// from the widths the metadata tables give them, so that each bit is set half the time, with
    /// the name drawn from `names`.
    [<Test>]
    let ``no attribute bit outside the order changes the outcome`` () : unit =
        let mutable seen : Map<NewobjTarget, int> = Map.empty

        let gen : Gen<MethodAttributes * TypeAttributes * string * bool> =
            gen {
                let! methodBits = Gen.choose (0, 0xFFFF)
                let! typeLow = Gen.choose (0, 0xFFFF)
                let! typeHigh = Gen.choose (0, 0xFFFF)
                let! name = Gen.elements names
                let! valueType = Gen.elements [ false ; true ]

                let method : MethodAttributes = enum methodBits
                let declaringType : TypeAttributes = enum ((typeHigh <<< 16) ||| typeLow)
                return method, declaringType, name, valueType
            }

        let property (method : MethodAttributes, declaringType : TypeAttributes, name : string, valueType : bool) =
            let actual = NewobjTarget.classify method name declaringType valueType

            actual |> shouldEqual (expected (factsOf method name declaringType valueType))

            NewobjTarget.isInstanceConstructor method name
            |> shouldEqual (factsOf method name declaringType valueType).InstanceConstructor

            lock names (fun () -> seen <- seen |> Map.change actual (fun n -> Some (1 + defaultArg n 0)))

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 10000, Prop.forAll (Arb.fromGen gen) property)

        let count (outcome : NewobjTarget) : int =
            seen |> Map.tryFind outcome |> Option.defaultValue 0

        // Expected counts of 10000: `AbstractMethod` 1/2, 5000; `StaticMethod` 1/4, 2500;
        // `NotInstanceConstructor` 1/4 * 11/12, 2292; `Constructs` 1/4 * 1/12 * 3/4, 156;
        // `AbstractClass` 1/4 * 1/12 * 1/4, 52. Each bound is at least seven standard deviations
        // below its expectation, so a false failure has probability below 1e-11.
        count NewobjTarget.AbstractMethod |> shouldBeGreaterThan 4600
        count NewobjTarget.StaticMethod |> shouldBeGreaterThan 2200
        count NewobjTarget.NotInstanceConstructor |> shouldBeGreaterThan 2000
        count NewobjTarget.Constructs |> shouldBeGreaterThan 70
        count NewobjTarget.AbstractClass |> shouldBeGreaterThan 9
