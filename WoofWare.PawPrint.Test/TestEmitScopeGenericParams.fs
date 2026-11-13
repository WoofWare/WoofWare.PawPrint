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

    /// What adding one parameter with constraints did, for coverage.
    [<RequireQualifiedAccess>]
    type private ConstraintAddition =
        | Added
        | AddedWithRepeat
        | RefusedMissingTarget

    /// The targets a constraint may name that the scope lacks: the next row of each table.
    let private missingTargets (scope : EmitScope) : TypeDefOrRef list =
        [
            TypeDefOrRef.TypeDef (TypeDefRid ((EmitScope.typeDefs scope).Count + 1))
            TypeDefOrRef.TypeRef (TypeRefRid ((EmitScope.typeRefs scope).Count + 1))
            TypeDefOrRef.TypeSpec (TypeSpecRid ((EmitScope.typeSpecs scope).Count + 1))
        ]

    [<Test>]
    let ``a parameter's constraints are added after it, in the order given`` () : unit =
        let property
            (cover : ConstraintAddition -> unit)
            (scope : EmitScope)
            (ownerChoice : int)
            (choices : int list)
            (missing : int option)
            : unit
            =
            match pick ownerChoice (genericParamOwners scope) with
            | None -> ()
            | Some owner ->
                let present = choices |> List.choose (fun c -> pick c (typeDefOrRefs scope))

                // Perhaps one target the scope lacks, somewhere among them.
                let constraints =
                    match missing with
                    | None -> present
                    | Some m ->
                        let at = m % (present.Length + 1)

                        List.take at present
                        @ [ (pick m (missingTargets scope)).Value ]
                        @ List.skip at present

                let row : GenericParamRow =
                    {
                        Number = uint16 (genericParamCount owner scope)
                        Flags = GenericParameterAttributes.None
                        Owner = owner
                        Name = StringOffset 0
                    }

                let added =
                    try
                        Some (EmitScope.addGenericParam row constraints scope)
                    with :? ArgumentException ->
                        None

                let defined =
                    try
                        Some (EmitScopeDefinitions.defineGenericParam owner row.Number 0 "" constraints scope)
                    with :? ArgumentException ->
                        None

                match missing, added with
                | Some _, Some _ -> failwith "a constraint naming no row of the scope was admitted"
                | Some _, None ->
                    // The definition refuses loudly too.
                    defined.IsNone |> shouldEqual true
                    cover ConstraintAddition.RefusedMissingTarget
                | None, None -> failwith $"constraints naming rows of the scope were refused: %A{constraints}"
                | None, Some (rid, next) ->
                    let before = EmitScope.genericParamConstraints scope
                    let after = EmitScope.genericParamConstraints next

                    // Every earlier row is kept, and one row is appended for each constraint, in order.
                    List.ofSeq after
                    |> shouldEqual (
                        List.ofSeq before
                        @ (constraints
                           |> List.map (fun target ->
                               {
                                   Owner = rid
                                   Constraint = target
                               }
                           ))
                    )

                    EmitScope.genericParams next |> Seq.last |> shouldEqual row

                    match defined with
                    | None -> failwith $"defineGenericParam refused what addGenericParam admitted: %A{constraints}"
                    | Some (GenericParamDefinition.Defined (definedRid, definedScope)) ->
                        definedRid |> shouldEqual rid

                        EmitScope.genericParamConstraints definedScope
                        |> List.ofSeq
                        |> shouldEqual (List.ofSeq after)
                    | Some (GenericParamDefinition.OutOfOrder (o, last)) ->
                        failwith $"an owner genericParamOwners offers was refused: %O{o} after %O{last}"

                    if List.length (List.distinct constraints) < constraints.Length then
                        cover ConstraintAddition.AddedWithRepeat
                    else
                        cover ConstraintAddition.Added

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 300)
                (Arb.fromGen (
                    gen {
                        let! scope = genScope
                        let! owner = choice
                        let! choices = Gen.listOfLength 4 choice |> Gen.bind Gen.subListOf
                        let! missing = Gen.frequency [ 3, Gen.constant None ; 1, Gen.map Some choice ]
                        return scope, owner, List.ofSeq choices, missing
                    }
                ))
                (fun cover (scope, owner, choices, missing) -> property cover scope owner choices missing)

        coverage.Count ConstraintAddition.Added |> shouldBeGreaterThan 50
        coverage.Count ConstraintAddition.AddedWithRepeat |> shouldBeGreaterThan 20
        coverage.Count ConstraintAddition.RefusedMissingTarget |> shouldBeGreaterThan 30

    /// ECMA-335 II.22.21 sorts `GenericParamConstraint` by owner, which SRM checks; the scope adds a
    /// parameter's constraints only with it, so every scope's rows are in that order, each naming a
    /// parameter the scope has.
    [<Test>]
    let ``every scope's constraint rows are in owner order`` () : unit =
        let property (cover : bool -> unit) (scope : EmitScope) : unit =
            let constraints = EmitScope.genericParamConstraints scope
            let parameters = (EmitScope.genericParams scope).Count

            for row in constraints do
                row.Owner.Rid |> shouldBeGreaterThan 0
                row.Owner.Rid |> shouldBeSmallerThan (parameters + 1)

            constraints
            |> Seq.pairwise
            |> Seq.iter (fun (a, b) -> a.Owner.Rid |> shouldBeSmallerThan (b.Owner.Rid + 1))

            // `cover true` for a scope with constraints on more than one parameter.
            cover ((constraints |> Seq.map _.Owner |> Seq.distinct |> Seq.length) > 1)

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 300) (Arb.fromGen genScope) property

        coverage.Count true |> shouldBeGreaterThan 50

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

    /// The `GenericParamConstraint` RIDs real .NET's emitter gave one parameter of a dynamic module, in
    /// RID order, read through CoreLib's internal `MetadataImport.Enum`, which reflection does not expose:
    /// a method compiled against the test host's CoreLib, since `MetadataEnumResult` is a ref struct.
    let private realConstraintRows : Lazy<Func<Module, int, int[]>> =
        lazy
            let corelib = typeof<obj>.Assembly
            let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

            let find (name : string) : Type =
                match corelib.GetType name with
                | null -> failwith $"the test host's CoreLib has no %s{name}"
                | t -> t

            let getter (t : Type) (name : string) : MethodInfo =
                match t.GetProperty (name, flags) with
                | null -> failwith $"%s{t.FullName} has no property %s{name}"
                | p -> p.GetGetMethod true

            let importType = find "System.Reflection.MetadataImport"
            let resultType = find "System.Reflection.MetadataEnumResult"
            let runtimeModule = find "System.Reflection.RuntimeModule"

            let enumMethod =
                match importType.GetMethod ("Enum", flags) with
                | null -> failwith "MetadataImport has no instance method Enum"
                | m -> m

            let dm =
                System.Reflection.Emit.DynamicMethod (
                    "ConstraintRows",
                    typeof<int[]>,
                    [| typeof<Module> ; typeof<int> |],
                    typeof<obj>.Module,
                    true
                )

            let il = dm.GetILGenerator ()
            let import = il.DeclareLocal importType
            let result = il.DeclareLocal resultType
            let rows = il.DeclareLocal typeof<int[]>
            let i = il.DeclareLocal typeof<int>
            let loop = il.DefineLabel ()
            let test = il.DefineLabel ()
            il.Emit System.Reflection.Emit.OpCodes.Ldarg_0
            il.Emit (System.Reflection.Emit.OpCodes.Castclass, runtimeModule)
            il.Emit (System.Reflection.Emit.OpCodes.Call, getter runtimeModule "MetadataImport")
            il.Emit (System.Reflection.Emit.OpCodes.Stloc, import)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloca, import)
            // `MetadataTokenType.GenericParamConstraint`.
            il.Emit (System.Reflection.Emit.OpCodes.Ldc_I4, 0x2C000000)
            il.Emit (System.Reflection.Emit.OpCodes.Ldarg_1)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloca, result)
            il.Emit (System.Reflection.Emit.OpCodes.Call, enumMethod)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloca, result)
            il.Emit (System.Reflection.Emit.OpCodes.Call, getter resultType "Length")
            il.Emit (System.Reflection.Emit.OpCodes.Newarr, typeof<int>)
            il.Emit (System.Reflection.Emit.OpCodes.Stloc, rows)
            il.Emit (System.Reflection.Emit.OpCodes.Ldc_I4_0)
            il.Emit (System.Reflection.Emit.OpCodes.Stloc, i)
            il.Emit (System.Reflection.Emit.OpCodes.Br, test)
            il.MarkLabel loop
            il.Emit (System.Reflection.Emit.OpCodes.Ldloc, rows)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloc, i)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloca, result)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloc, i)
            il.Emit (System.Reflection.Emit.OpCodes.Call, getter resultType "Item")
            il.Emit (System.Reflection.Emit.OpCodes.Stelem_I4)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloc, i)
            il.Emit (System.Reflection.Emit.OpCodes.Ldc_I4_1)
            il.Emit (System.Reflection.Emit.OpCodes.Add)
            il.Emit (System.Reflection.Emit.OpCodes.Stloc, i)
            il.MarkLabel test
            il.Emit (System.Reflection.Emit.OpCodes.Ldloc, i)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloc, rows)
            il.Emit (System.Reflection.Emit.OpCodes.Ldlen)
            il.Emit (System.Reflection.Emit.OpCodes.Conv_I4)
            il.Emit (System.Reflection.Emit.OpCodes.Blt, loop)
            il.Emit (System.Reflection.Emit.OpCodes.Ldloc, rows)
            il.Emit (System.Reflection.Emit.OpCodes.Ret)
            dm.CreateDelegate<Func<Module, int, int[]>> ()

    /// One generated parameter: its name, its attributes, its interface constraints (each choosing one of
    /// the module's interfaces, repeats allowed) and perhaps a base type constraint (choosing a class).
    type private ConstrainedParameter =
        {
            Name : string
            Flags : int
            Interfaces : int list
            BaseType : int option
        }

    /// Real .NET's emitter, over generated generic interfaces whose parameters are constrained to
    /// non-generic interfaces and classes the module defined first: the `GenericParamConstraint` RIDs it
    /// gives each parameter, and the types reflection reports for the created type's parameter, are the
    /// rows `defineGenericParam` adds to a scope holding the same types.
    [<Test>]
    let ``real .NET gives each parameter's constraints the rows the scope defines`` () : unit =
        let interfaceNames = [ "J0" ; "J1" ; "J2" ]
        let classNames = [ "C0" ; "C1" ]

        let genParameter : Gen<ConstrainedParameter> =
            gen {
                let! name = genName
                let! flags = Gen.elements [ 0 ; 1 ; 2 ; 3 ; 4 ; 8 ; 16 ; 0x1C ]
                let! interfaces = Gen.listOf (Gen.choose (0, interfaceNames.Length - 1))
                let! baseType = Gen.optionOf (Gen.choose (0, classNames.Length - 1))

                return
                    {
                        Name = name
                        Flags = flags
                        Interfaces = List.truncate 4 (List.ofSeq interfaces)
                        BaseType = baseType
                    }
            }

        let genInterface : Gen<ConstrainedParameter list> =
            Gen.listOf genParameter
            |> Gen.map (List.ofSeq >> List.truncate 3)
            |> Gen.filter (fun ps -> not ps.IsEmpty)

        // `cover` names whether real .NET created each interface, and the shapes of constraint list seen.
        let property (cover : string -> unit) (interfaces : ConstrainedParameter list list) : unit =
            let assembly =
                System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly (
                    AssemblyName "GenericParamOracle",
                    System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect
                )

            let module' = assembly.DefineDynamicModule "GenericParamOracle"
            let mutable scope = DynamicAssemblyImage.scope dynamicParts Guid.Empty

            // Each type in both, at the same TypeDef RID. The scope's classes extend nothing, where real
            // .NET's extend `object`; no constraint row depends on a type's parent.
            let define (name : string) (flags : TypeAttributes) : Type * TypeDefRid =
                let builder = module'.DefineType (name, flags)

                match EmitScopeDefinitions.defineTopLevelTypeDef name flags None [] scope with
                | TypeDefDefinition.Duplicate existing ->
                    failwith $"the scope already has %s{name} as TypeDef %d{existing.Rid}"
                | TypeDefDefinition.Defined (rid, next) ->
                    builder.MetadataToken |> shouldEqual (0x02000000 ||| rid.Rid)
                    scope <- next
                    builder.CreateType (), rid

            let interfaceFlags =
                TypeAttributes.Public ||| TypeAttributes.Interface ||| TypeAttributes.Abstract

            let targets =
                interfaceNames
                |> List.map (fun name -> define name interfaceFlags)
                |> Array.ofList

            let classes =
                classNames
                |> List.map (fun name -> define name (TypeAttributes.Public ||| TypeAttributes.Class))
                |> Array.ofList

            let runtimeModule = (fst targets.[0]).Module

            for index, parameters in List.indexed interfaces do
                let name = $"I%d{index}"
                let builder = module'.DefineType (name, interfaceFlags)

                let builders =
                    builder.DefineGenericParameters (parameters |> List.map _.Name |> Array.ofList)

                for b, p in Array.zip builders (Array.ofList parameters) do
                    b.SetGenericParameterAttributes (enum<GenericParameterAttributes> p.Flags)

                    if not p.Interfaces.IsEmpty then
                        b.SetInterfaceConstraints (p.Interfaces |> List.map (fun j -> fst targets.[j]) |> Array.ofList)

                    match p.BaseType with
                    | Some c -> b.SetBaseTypeConstraint (fst classes.[c])
                    | None -> ()

                let typeDef =
                    match EmitScopeDefinitions.defineTopLevelTypeDef name interfaceFlags None [] scope with
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
                for number, p in List.indexed parameters do
                    let constraints =
                        [
                            for j in p.Interfaces do
                                yield TypeDefOrRef.TypeDef (snd targets.[j])
                            match p.BaseType with
                            | Some c -> yield TypeDefOrRef.TypeDef (snd classes.[c])
                            | None -> ()
                        ]

                    match
                        EmitScopeDefinitions.defineGenericParam
                            (TypeOrMethodDef.TypeDef typeDef)
                            (uint16 number)
                            p.Flags
                            p.Name
                            constraints
                            scope
                    with
                    | GenericParamDefinition.OutOfOrder (owner, last) ->
                        failwith $"real .NET's creation order was refused: %O{owner} after %O{last}"
                    | GenericParamDefinition.Defined (rid, next) ->
                        builders.[number].MetadataToken |> shouldEqual (0x2A000000 ||| rid.Rid)
                        scope <- next

                        let scopeRows =
                            EmitScope.genericParamConstraints scope
                            |> Seq.indexed
                            |> Seq.filter (fun (_, row) -> row.Owner = rid)
                            |> List.ofSeq

                        realConstraintRows.Force().Invoke (runtimeModule, 0x2A000000 ||| rid.Rid)
                        |> Seq.map (fun token -> token &&& 0x00FFFFFF)
                        |> List.ofSeq
                        |> shouldEqual (scopeRows |> List.map (fun (i, _) -> i + 1))

                        match created with
                        | None -> ()
                        | Some created ->
                            (created.GetGenericArguments ()).[number].GetGenericParameterConstraints ()
                            |> Seq.map _.MetadataToken
                            |> List.ofSeq
                            |> shouldEqual (
                                scopeRows
                                |> List.map (fun (_, row) ->
                                    match row.Constraint with
                                    | TypeDefOrRef.TypeDef t -> 0x02000000 ||| t.Rid
                                    | other ->
                                        failwith $"the oracle's scope holds only TypeDef constraints, not %O{other}"
                                )
                            )

                cover (if created.IsSome then "created" else "refused")

                for p in parameters do
                    if p.BaseType.IsSome && not p.Interfaces.IsEmpty then
                        cover "base and interfaces"

                    if List.length (List.distinct p.Interfaces) < p.Interfaces.Length then
                        cover "repeated interface"

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 100)
                (Arb.fromGen (Gen.listOf genInterface |> Gen.map (List.ofSeq >> List.truncate 3)))
                property

        coverage.Count "created" |> shouldBeGreaterThan 50
        coverage.Count "base and interfaces" |> shouldBeGreaterThan 30
        coverage.Count "repeated interface" |> shouldBeGreaterThan 20
