namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Reflection
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.EmitScopeGen

/// The generic parameters CoreCLR's emitter defines in a dynamic module for
/// `TypeBuilder_DefineGenericParam`: each a new row, numbered in the order of creation, refused when the
/// image's owner order could not hold it at that RID; and which parameters' variance PawPrint refuses
/// when their type is created.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeGenericParams =

    /// A scope with `types` types after `<Module>` and, for each choice in `methods`, a method of the type
    /// that choice picks, as generated scopes are built.
    let private withOwners (types : int) (methods : int list) : EmitScope =
        let ops =
            [
                for i in 1..types do
                    yield Op.TypeDef ($"T%d{i}", "", i, None)
                for m in methods do
                    yield Op.Method (m, "M", 0, [], 0, BodyShape.NoBody)
            ]

        apply Guid.Empty ops

    let private allOwners (scope : EmitScope) : TypeOrMethodDef list =
        [
            for i in 1 .. (EmitScope.typeDefs scope).Count do
                yield TypeOrMethodDef.TypeDef (TypeDefRid i)
            for i in 1 .. (EmitScope.methodDefs scope).Count do
                yield TypeOrMethodDef.MethodDef (MethodDefRid i)
        ]

    let private genOwnerScope : Gen<EmitScope> =
        gen {
            let! types = Gen.choose (0, 4)
            let! methods = Gen.choose (0, 5) |> Gen.bind (fun n -> Gen.listOfLength n choice)
            return withOwners types (List.ofSeq methods)
        }

    /// What `defineGenericParam` answered for one request, compared with the model.
    [<RequireQualifiedAccess>]
    type private Admission =
        | Admitted
        | Refused

    /// The model of the order rule: a row is admitted when the scope has none, or when its owner's coded
    /// index is at least that of the last row admitted.
    [<Test>]
    let ``the order rule admits exactly the rows a model admits`` () : unit =
        let property (cover : Admission -> unit) (scope : EmitScope) (requests : int list) : unit =
            let owners = allOwners scope
            let mutable scope = scope
            let mutable last : TypeOrMethodDef option = None
            let mutable admitted = 0

            for request in requests do
                let owner = (pick request owners).Value
                let number = uint16 (genericParamCount owner scope)

                let modelAdmits =
                    match last with
                    | None -> true
                    | Some last -> owner.CodedIndex >= last.CodedIndex

                EmitScope.genericParamOrderConflict owner scope
                |> shouldEqual (if modelAdmits then None else last)

                let row : GenericParamRow =
                    {
                        Number = number
                        Flags = GenericParameterAttributes.None
                        Owner = owner
                        Name = StringOffset 0
                    }

                // The primitive refuses what the definition refuses, loudly.
                let added =
                    try
                        Some (EmitScope.addGenericParam row [] scope)
                    with :? ArgumentException ->
                        None

                added.IsSome |> shouldEqual modelAdmits

                match EmitScopeDefinitions.defineGenericParam owner number 0 "" [] scope with
                | GenericParamDefinition.Defined (rid, next) ->
                    modelAdmits |> shouldEqual true
                    rid.Rid |> shouldEqual (admitted + 1)
                    added.Value |> fst |> shouldEqual rid
                    (EmitScope.genericParams next).[admitted] |> shouldEqual row
                    scope <- next
                    last <- Some owner
                    admitted <- admitted + 1
                    cover Admission.Admitted
                | GenericParamDefinition.OutOfOrder (refusedOwner, lastOwner) ->
                    modelAdmits |> shouldEqual false
                    refusedOwner |> shouldEqual owner
                    Some lastOwner |> shouldEqual last
                    cover Admission.Refused

            (EmitScope.genericParams scope).Count |> shouldEqual admitted

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 300)
                (Arb.fromGen (
                    gen {
                        let! scope = genOwnerScope
                        let! requests = Gen.listOf choice
                        return scope, List.ofSeq requests
                    }
                ))
                (fun cover (scope, requests) -> property cover scope requests)

        coverage.Count Admission.Admitted |> shouldBeGreaterThan 100
        coverage.Count Admission.Refused |> shouldBeGreaterThan 100

    [<Test>]
    let ``a parameter's number must be the count of its owner's parameters`` () : unit =
        // `cover true` for a number the scope admits, `cover false` for one it refuses.
        let property (cover : bool -> unit) (scope : EmitScope) (ownerChoice : int) (number : int) : unit =
            match pick ownerChoice (genericParamOwners scope) with
            | None -> ()
            | Some owner ->
                let expected = genericParamCount owner scope

                let row : GenericParamRow =
                    {
                        Number = uint16 number
                        Flags = GenericParameterAttributes.None
                        Owner = owner
                        Name = StringOffset 0
                    }

                let added =
                    try
                        Some (EmitScope.addGenericParam row [] scope)
                    with :? ArgumentException ->
                        None

                added.IsSome |> shouldEqual (number = expected)
                cover (number = expected)

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 300)
                (Arb.fromGen (Gen.zip3 genScope choice (Gen.choose (0, 3))))
                (fun cover (scope, ownerChoice, number) -> property cover scope ownerChoice number)

        coverage.Count true |> shouldBeGreaterThan 30
        coverage.Count false |> shouldBeGreaterThan 30

    [<Test>]
    let ``a parameter's flags and name are stored as CoreCLR stores them`` () : unit =
        let genFlags : Gen<int> =
            Gen.oneof
                [
                    Gen.elements
                        [
                            -1
                            0
                            1
                            2
                            3
                            4
                            8
                            16
                            0x1C
                            0x10004
                            0xFFFF
                            0x10000
                            Int32.MinValue
                        ]
                    Gen.choose (Int32.MinValue, Int32.MaxValue)
                ]

        let property (flags : int) (name : string) : unit =
            let scope = withOwners 1 []

            match
                EmitScopeDefinitions.defineGenericParam (TypeOrMethodDef.TypeDef (TypeDefRid 2)) 0us flags name [] scope
            with
            | GenericParamDefinition.OutOfOrder (owner, last) ->
                failwith $"the first parameter of a scope was refused as out of order: %O{owner} after %O{last}"
            | GenericParamDefinition.Defined (rid, next) ->
                rid.Rid |> shouldEqual 1
                let row = (EmitScope.genericParams next).[0]

                int row.Flags |> shouldEqual (if flags = -1 then 0 else flags &&& 0xFFFF)

                // What interning the name stores, which for a lone surrogate is U+FFFD.
                let stored, interned = EmitScope.internString name scope

                EmitScope.stringAt row.Name next
                |> shouldEqual (EmitScope.stringAt stored interned)

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            Prop.forAll (Arb.fromGen (Gen.zip genFlags genName)) (fun (flags, name) -> property flags name)
        )

    /// The model of `unmodelledVariance`: the first parameter of the type, in RID order, with both
    /// variance bits, or with either if the type is not an interface.
    [<Test>]
    let ``the variance PawPrint does not model is found for exactly the parameters a model finds`` () : unit =
        // `cover` names which answer the sample gave.
        let property (cover : string -> unit) (scope : EmitScope) : unit =
            for t in 1 .. (EmitScope.typeDefs scope).Count do
                let typeDef = TypeDefRid t

                let isInterface =
                    (EmitScope.typeDefs scope).[t - 1].Flags.HasFlag TypeAttributes.Interface

                let expected =
                    EmitScope.genericParams scope
                    |> Seq.indexed
                    |> Seq.filter (fun (_, row) -> row.Owner = TypeOrMethodDef.TypeDef typeDef)
                    |> Seq.tryPick (fun (i, row) ->
                        let variance = int row.Flags &&& 3

                        if variance = 3 then
                            Some (UnmodelledVariance.BothVarianceBits (GenericParamRid (i + 1)))
                        elif variance <> 0 && not isInterface then
                            Some (UnmodelledVariance.VariantOfNonInterface (GenericParamRid (i + 1)))
                        else
                            None
                    )

                let actual = EmitScopeDefinitions.unmodelledVariance typeDef scope
                actual |> shouldEqual expected

                match actual with
                | None -> cover "none"
                | Some (UnmodelledVariance.BothVarianceBits _) -> cover "both"
                | Some (UnmodelledVariance.VariantOfNonInterface _) -> cover "non-interface"

                // A variant parameter of an interface is modelled.
                if
                    isInterface
                    && actual.IsNone
                    && EmitScope.genericParams scope
                       |> Seq.exists (fun row ->
                           row.Owner = TypeOrMethodDef.TypeDef typeDef && int row.Flags &&& 3 <> 0
                       )
                then
                    cover "variant interface"

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 300) (Arb.fromGen genScope) property

        for label in [ "none" ; "both" ; "non-interface" ; "variant interface" ] do
            coverage.Count label |> shouldBeGreaterThan 20

    let private dynamicParts : DynamicAssemblyName =
        {
            SimpleName = "GenericParamOracle"
            Version = Version (0, 0, 0, 0)
            Culture = ""
            PublicKey = ImmutableArray<byte>.Empty
            Flags = enum<AssemblyFlags> 0
            HashAlgorithm = AssemblyHashAlgorithm.Sha1
        }

    /// Real .NET's emitter, over generated generic interfaces: each parameter's token, name and attributes,
    /// as reflection reports them for the created type, are the row `defineGenericParam` adds to a scope
    /// holding the same interface.
    [<Test>]
    let ``real .NET gives each parameter of a created interface the row the scope defines`` () : unit =
        let genFlags : Gen<int> =
            Gen.oneof
                [
                    Gen.elements [ -1 ; 0 ; 1 ; 2 ; 4 ; 8 ; 16 ; 0x1C ; 0x10004 ; 0x10001 ]
                    Gen.choose (0, 0x3F)
                ]

        let genParameters : Gen<(string * int) list> =
            Gen.zip genName genFlags
            |> Gen.listOf
            |> Gen.map (List.ofSeq >> List.truncate 4)
            |> Gen.filter (fun ps -> not ps.IsEmpty)

        // `cover true` for an interface real .NET created, `cover false` for one it refused to load.
        let property (cover : bool -> unit) (interfaces : (string * int) list list) : unit =
            let assembly =
                System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly (
                    AssemblyName "GenericParamOracle",
                    System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect
                )

            let module' = assembly.DefineDynamicModule "GenericParamOracle"
            let mutable scope = DynamicAssemblyImage.scope dynamicParts Guid.Empty

            let flags =
                TypeAttributes.Public ||| TypeAttributes.Interface ||| TypeAttributes.Abstract

            for index, parameters in List.indexed interfaces do
                let name = $"I%d{index}"
                let builder = module'.DefineType (name, flags)

                let builders =
                    builder.DefineGenericParameters (parameters |> List.map fst |> Array.ofList)

                for b, (_, f) in Array.zip builders (Array.ofList parameters) do
                    b.SetGenericParameterAttributes (enum<GenericParameterAttributes> f)

                let typeDef =
                    match EmitScopeDefinitions.defineTopLevelTypeDef name flags None [] scope with
                    | TypeDefDefinition.Duplicate existing ->
                        failwith $"the scope already has %s{name} as TypeDef %d{existing.Rid}"
                    | TypeDefDefinition.Defined (rid, next) ->
                        builder.MetadataToken |> shouldEqual (0x02000000 ||| rid.Rid)
                        scope <- next
                        rid

                let created =
                    try
                        Some (builder.CreateType ())
                    with :? TypeLoadException ->
                        None

                // The rows are made before the type is loaded, so a type that fails to load has them too.
                for number, (parameterName, parameterFlags) in List.indexed parameters do
                    match
                        EmitScopeDefinitions.defineGenericParam
                            (TypeOrMethodDef.TypeDef typeDef)
                            (uint16 number)
                            parameterFlags
                            parameterName
                            []
                            scope
                    with
                    | GenericParamDefinition.OutOfOrder (owner, last) ->
                        failwith $"real .NET's creation order was refused: %O{owner} after %O{last}"
                    | GenericParamDefinition.Defined (rid, next) ->
                        builders.[number].MetadataToken |> shouldEqual (0x2A000000 ||| rid.Rid)
                        scope <- next

                match created with
                | None -> cover false
                | Some created ->
                    cover true

                    for argument in created.GetGenericArguments () do
                        let rid = argument.MetadataToken &&& 0x00FFFFFF
                        let row = (EmitScope.genericParams scope).[rid - 1]
                        row.Owner |> shouldEqual (TypeOrMethodDef.TypeDef typeDef)
                        int row.Number |> shouldEqual argument.GenericParameterPosition
                        row.Flags |> shouldEqual argument.GenericParameterAttributes
                        EmitScope.stringAt row.Name scope |> shouldEqual argument.Name

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 100)
                (Arb.fromGen (Gen.listOf genParameters |> Gen.map (List.ofSeq >> List.truncate 4)))
                property

        coverage.Count true |> shouldBeGreaterThan 50
