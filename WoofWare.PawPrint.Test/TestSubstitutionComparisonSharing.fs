namespace WoofWare.PawPrint.Test

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection.Metadata
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// <summary>
/// A definition-rooted substitution is held as a chain of shared arrays: each ancestor's context is
/// one array whose arguments are spellings read against the array below. A spelling that mentions a
/// variable twice, such as <c>KeyValuePair&lt;!0, !0&gt;</c>, makes the type it denotes a tree twice
/// as large as the level below while the chain grows by one array. Comparing two such chains must
/// cost what the chains hold, not what they denote.
/// </summary>
/// <remarks>
/// <para>
/// No image is needed: the contexts are built with <c>SubstitutionContext.forDefinition</c> and
/// <c>forBase</c> over CoreLib's <c>KeyValuePair`2</c>, and every spelling is read in CoreLib's
/// token space, so the comparison resolves nothing outside CoreLib.
/// </para>
/// <para>
/// Cost is measured as bytes allocated on the test's thread, which has no clock in it. The answers
/// are checked against a reference written here: within the fragment generated (type variables,
/// <c>int32</c>, <c>KeyValuePair`2</c> instantiations, vectors and pointers, all in one assembly,
/// with no TypeRefs and no modifiers), two substitutions are equivalent exactly when expanding every
/// variable gives structurally equal trees.
/// </para>
/// </remarks>
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestSubstitutionComparisonSharing =

    let private corelib : DumpedAssembly =
        let _, loggerFactory = LoggerFactory.makeTest ()
        Assembly.readFile loggerFactory HostImage.coreLibPath

    let private corelibName : string = corelib.DefinitionFullName

    /// Immutable, so one serves every comparison; a comparison's own loads go into the context it
    /// returns, which these tests discard.
    let private concretizationContext : TypeConcretization.ConcretizationContext<DumpedAssembly> =
        {
            TypeConcretization.ConcretizationContext.ConcreteTypes = AllConcreteTypes.Empty
            TypeConcretization.ConcretizationContext.LoadedAssemblies = LoadedAssemblies.ofAssemblies [ corelib ]
            TypeConcretization.ConcretizationContext.BaseTypes = BaseClassTypes.ofCorelib corelib
        }

    /// CoreLib's two-parameter `KeyValuePair`2`. It also serves as the owner of the variables at the
    /// bottom of every chain: the comparison checks only that both sides' owners agree.
    let private keyValuePair : ResolvedTypeIdentity =
        corelib.TryGetTopLevelTypeDef "System.Collections.Generic" "KeyValuePair`2"
        |> Option.defaultWith (fun () -> failwith "expected System.Collections.Generic.KeyValuePair`2 in corelib")
        |> fun ty -> ty.Identity

    let private keyValuePairHead : TypeDefn =
        TypeDefn.FromDefinition (keyValuePair, SignatureTypeKind.ValueType)

    let private pairOf (key : TypeDefn) (value : TypeDefn) : TypeDefn =
        TypeDefn.GenericInstantiation (keyValuePairHead, ImmutableArray.Create (key, value))

    /// The answer of one `substitutionsEquivalent` call, and the bytes it allocated on this thread.
    let private measure
        (left : TypeConcretization.SubstitutionContext)
        (right : TypeConcretization.SubstitutionContext)
        : bool * int64
        =
        let ctx = concretizationContext
        let before = GC.GetAllocatedBytesForCurrentThread ()

        let result, _ =
            TypeConcretization.substitutionsEquivalent ctx IAssemblyLoad.alreadyLoadedOnly left right

        let allocated = GC.GetAllocatedBytesForCurrentThread () - before

        match result with
        | Ok answer -> answer, allocated
        | Error failure -> failwith $"the comparison failed to bind: %A{failure}"

    /// `levels` contexts stacked on `bottom`, each `[KeyValuePair<!0, !0>; KeyValuePair<!0, !1>]` over
    /// the one below, with fresh spelling nodes at every level. Its first argument at depth `n` denotes
    /// a complete binary tree of depth `n`, held as `n` arrays.
    let private doublingChain
        (bottom : TypeConcretization.SubstitutionContext)
        (levels : int)
        : TypeConcretization.SubstitutionContext
        =
        (bottom, [ 1..levels ])
        ||> List.fold (fun below _ ->
            let spellings =
                ImmutableArray.Create (
                    pairOf (TypeDefn.GenericTypeParameter 0) (TypeDefn.GenericTypeParameter 0),
                    pairOf (TypeDefn.GenericTypeParameter 0) (TypeDefn.GenericTypeParameter 1)
                )

            TypeConcretization.SubstitutionContext.forBase corelibName spellings below
        )

    /// A bottom context over a fresh `forDefinition` array, supplying `!first` and `!second` of the
    /// definition.
    let private bottomOf (first : int) (second : int) : TypeConcretization.SubstitutionContext =
        TypeConcretization.SubstitutionContext.forDefinition keyValuePair 2
        |> TypeConcretization.SubstitutionContext.forBase
            corelibName
            (ImmutableArray.Create (TypeDefn.GenericTypeParameter first, TypeDefn.GenericTypeParameter second))

    /// The two shapes T1 measures, each built twice so that no array or spelling is shared between
    /// the sides. In `differsAtBottom`, the first arguments are equal (the expensive answer) and the
    /// second differ only in the variable at the very bottom.
    let private equalSides
        (depth : int)
        : TypeConcretization.SubstitutionContext * TypeConcretization.SubstitutionContext
        =
        doublingChain (bottomOf 0 1) depth, doublingChain (bottomOf 0 1) depth

    let private differsAtBottom
        (depth : int)
        : TypeConcretization.SubstitutionContext * TypeConcretization.SubstitutionContext
        =
        doublingChain (bottomOf 0 1) depth, doublingChain (bottomOf 0 0) depth

    [<TestCase true>]
    [<TestCase false>]
    let ``comparing chains that double at each level costs what the chains hold`` (equal : bool) : unit =
        let shape = if equal then equalSides else differsAtBottom

        // One untimed call first, so that one-off work is not charged to the measurement.
        shape 8 ||> measure |> ignore<bool * int64>

        let answer8, allocated8 = shape 8 ||> measure
        let answer16, allocated16 = shape 16 ||> measure

        TestContext.Out.WriteLine
            $"equal=%b{equal}: depth 8 allocated %d{allocated8} bytes, depth 16 allocated %d{allocated16} bytes, ratio %.2f{float allocated16 / float allocated8}"

        answer8 |> shouldEqual equal
        answer16 |> shouldEqual equal

        // Work exponential in depth gives a ratio near 2^8 = 256, and work linear in it a ratio near 2.
        allocated16 |> shouldBeSmallerThan (8L * allocated8 + 1L)

    /// A spelling in the generated fragment, before it is built as a `TypeDefn`.
    type private Spelling =
        | Variable of int
        | Integer
        | Pair of Spelling * Spelling
        | Vector of Spelling
        | Pointer of Spelling

    let rec private spellingSize (spelling : Spelling) : int =
        match spelling with
        | Spelling.Variable _
        | Spelling.Integer -> 1
        | Spelling.Pair (key, value) -> 1 + spellingSize key + spellingSize value
        | Spelling.Vector element
        | Spelling.Pointer element -> 1 + spellingSize element

    /// Builds each spelling with fresh nodes, so that no two are the same object.
    let private freshBuilder () : Spelling -> TypeDefn =
        let rec build (spelling : Spelling) : TypeDefn =
            match spelling with
            | Spelling.Variable index -> TypeDefn.GenericTypeParameter index
            | Spelling.Integer -> TypeDefn.PrimitiveType PrimitiveType.Int32
            | Spelling.Pair (key, value) -> pairOf (build key) (build value)
            | Spelling.Vector element -> TypeDefn.OneDimensionalArrayLowerBoundZero (build element)
            | Spelling.Pointer element -> TypeDefn.Pointer (build element)

        build

    /// Builds each distinct spelling once, so that equal spellings are one object wherever they are
    /// used: at several levels, in several arguments, and on both sides if both use this builder.
    let private internedBuilder () : Spelling -> TypeDefn =
        let built = Dictionary<Spelling, TypeDefn> HashIdentity.Structural

        let rec build (spelling : Spelling) : TypeDefn =
            match built.TryGetValue spelling with
            | true, ty -> ty
            | false, _ ->
                let ty =
                    match spelling with
                    | Spelling.Variable index -> TypeDefn.GenericTypeParameter index
                    | Spelling.Integer -> TypeDefn.PrimitiveType PrimitiveType.Int32
                    | Spelling.Pair (key, value) -> pairOf (build key) (build value)
                    | Spelling.Vector element -> TypeDefn.OneDimensionalArrayLowerBoundZero (build element)
                    | Spelling.Pointer element -> TypeDefn.Pointer (build element)

                built.[spelling] <- ty
                ty

        build

    /// A substitution chain: `BottomArity` variables of the definition, then each level's spellings
    /// read against the level below, bottom first.
    type private Chain =
        {
            BottomArity : int
            Levels : Spelling list list
        }

        member this.TopArity : int =
            match List.tryLast this.Levels with
            | None -> this.BottomArity
            | Some level -> level.Length

    let private buildChain (build : Spelling -> TypeDefn) (chain : Chain) : TypeConcretization.SubstitutionContext =
        (TypeConcretization.SubstitutionContext.forDefinition keyValuePair chain.BottomArity, chain.Levels)
        ||> List.fold (fun below level ->
            TypeConcretization.SubstitutionContext.forBase
                corelibName
                (level |> List.map build |> ImmutableArray.CreateRange)
                below
        )

    /// The reference: a type with every variable replaced by what it denotes.
    type private Expansion =
        | Variable of int
        | Integer
        | Pair of Expansion * Expansion
        | Vector of Expansion
        | Pointer of Expansion

    let rec private expandArgument (argument : TypeConcretization.SubstitutionArgument) : Expansion =
        match argument with
        | TypeConcretization.SubstitutionArgument.Formal (owner, index) ->
            if owner <> keyValuePair then
                failwith $"the generator produced a variable of %O{owner}"

            Expansion.Variable index
        | TypeConcretization.SubstitutionArgument.Spelled (assembly, spelling, context) ->
            if assembly <> corelibName then
                failwith $"the generator produced a spelling in %s{assembly}"

            expandSpelling context spelling
        | TypeConcretization.SubstitutionArgument.Closed handle ->
            failwith $"the generator produced the closed argument %O{handle}"

    and private expandSpelling
        (context : ImmutableArray<TypeConcretization.SubstitutionArgument>)
        (spelling : TypeDefn)
        : Expansion
        =
        match spelling with
        | TypeDefn.GenericTypeParameter index -> expandArgument context.[index]
        | TypeDefn.PrimitiveType PrimitiveType.Int32 -> Expansion.Integer
        | TypeDefn.GenericInstantiation (head, arguments) when head = keyValuePairHead && arguments.Length = 2 ->
            Expansion.Pair (expandSpelling context arguments.[0], expandSpelling context arguments.[1])
        | TypeDefn.OneDimensionalArrayLowerBoundZero element -> Expansion.Vector (expandSpelling context element)
        | TypeDefn.Pointer element -> Expansion.Pointer (expandSpelling context element)
        | other -> failwith $"the generator produced %O{other}, outside the fragment the reference covers"

    let private expectedEquivalent
        (left : TypeConcretization.SubstitutionContext)
        (right : TypeConcretization.SubstitutionContext)
        : bool
        =
        left.Arguments.Length = right.Arguments.Length
        && Seq.forall2 (fun l r -> expandArgument l = expandArgument r) left.Arguments right.Arguments

    let private genVariable (arity : int) : Gen<Spelling> =
        Gen.choose (0, arity - 1) |> Gen.map Spelling.Variable

    /// One level of the alphabet: a variable, or one constructor over variables.
    let private genFlatSpelling (arity : int) : Gen<Spelling> =
        Gen.frequency
            [
                4, genVariable arity
                4, Gen.map2 (fun key value -> Spelling.Pair (key, value)) (genVariable arity) (genVariable arity)
                1, Gen.map Spelling.Vector (genVariable arity)
                1, Gen.map Spelling.Pointer (genVariable arity)
                1, Gen.constant Spelling.Integer
            ]

    /// The alphabet with one level of nesting.
    let private genSpelling (arity : int) : Gen<Spelling> =
        let flat = genFlatSpelling arity

        Gen.frequency
            [
                6, flat
                2, Gen.map2 (fun key value -> Spelling.Pair (key, value)) flat flat
                1, Gen.map Spelling.Vector flat
                1, Gen.map Spelling.Pointer flat
            ]

    let private genChain (genSpelling : int -> Gen<Spelling>) (bottomArity : int) (depth : int) : Gen<Chain> =
        let rec levels (below : int) (remaining : int) : Gen<Spelling list list> =
            if remaining = 0 then
                Gen.constant []
            else
                gen {
                    let! arity = Gen.choose (1, 3)
                    let! level = Gen.listOfLength arity (genSpelling below)
                    let! rest = levels arity (remaining - 1)
                    return level :: rest
                }

        levels bottomArity depth
        |> Gen.map (fun levels ->
            {
                BottomArity = bottomArity
                Levels = levels
            }
        )

    /// `chain` with one spelling replaced by another drawn for the same level, which may or may not
    /// change what the chain denotes.
    let private genRespelled (genSpelling : int -> Gen<Spelling>) (chain : Chain) : Gen<Chain> =
        match chain.Levels with
        | [] -> Gen.constant chain
        | _ ->

        gen {
            let! levelIndex = Gen.choose (0, chain.Levels.Length - 1)

            let below =
                if levelIndex = 0 then
                    chain.BottomArity
                else
                    chain.Levels.[levelIndex - 1].Length

            let! position = Gen.choose (0, chain.Levels.[levelIndex].Length - 1)
            let! replacement = genSpelling below

            return
                { chain with
                    Levels =
                        chain.Levels
                        |> List.mapi (fun i level ->
                            if i = levelIndex then
                                level
                                |> List.mapi (fun j spelling -> if j = position then replacement else spelling)
                            else
                                level
                        )
                }
        }

    let rec private genRespelledTimes (genSpelling : int -> Gen<Spelling>) (times : int) (chain : Chain) : Gen<Chain> =
        if times = 0 then
            Gen.constant chain
        else
            genRespelled genSpelling chain
            |> Gen.bind (genRespelledTimes genSpelling (times - 1))

    /// How the two sides' spellings are built.
    [<RequireQualifiedAccess>]
    type private Sharing =
        /// Every spelling a fresh object.
        | Fresh
        /// Each side interns its own spellings, so a node recurs within a side but never across.
        | InternedPerSide
        /// One interning across both sides, so the same node appears in many contexts on each.
        | InternedAcross

    let private builders (sharing : Sharing) : (Spelling -> TypeDefn) * (Spelling -> TypeDefn) =
        match sharing with
        | Sharing.Fresh -> freshBuilder (), freshBuilder ()
        | Sharing.InternedPerSide -> internedBuilder (), internedBuilder ()
        | Sharing.InternedAcross ->
            let build = internedBuilder ()
            build, build

    type private SubstitutionCase =
        {
            Left : Chain
            Right : Chain
            Sharing : Sharing
        }

    /// Two chains over one bottom arity, the right respelled zero to two times from the left, so that
    /// equal answers are common.
    let private genSubstitutionCase : Gen<SubstitutionCase> =
        gen {
            let! depth = Gen.choose (0, 6)
            let! bottomArity = Gen.choose (1, 3)
            let! left = genChain genSpelling bottomArity depth
            let! respellings = Gen.frequency [ 2, Gen.constant 0 ; 2, Gen.constant 1 ; 1, Gen.constant 2 ]
            let! right = genRespelledTimes genSpelling respellings left

            let! sharing = Gen.elements [ Sharing.Fresh ; Sharing.InternedPerSide ; Sharing.InternedAcross ]

            return
                {
                    Left = left
                    Right = right
                    Sharing = sharing
                }
        }

    [<RequireQualifiedAccess>]
    type private Answer =
        | Equivalent
        | Inequivalent

    let private answerOf (equivalent : bool) : Answer =
        if equivalent then
            Answer.Equivalent
        else
            Answer.Inequivalent

    [<Test>]
    let ``substitutionsEquivalent agrees with comparing the expanded trees`` () : unit =
        let property (cover : Answer -> unit) (case : SubstitutionCase) : unit =
            let buildLeft, buildRight = builders case.Sharing
            let left = buildChain buildLeft case.Left
            let right = buildChain buildRight case.Right
            let expected = expectedEquivalent left right
            cover (answerOf expected)
            measure left right |> fst |> shouldEqual expected

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 1000)
                (Arb.fromGen genSubstitutionCase)
                property

        // Counted over a fixed sample of 1000, so this holds on every run or on none.
        coverage.Count Answer.Equivalent |> shouldBeGreaterThan 200
        coverage.Count Answer.Inequivalent |> shouldBeGreaterThan 200

    type private ConstraintCase =
        {
            Substitution : SubstitutionCase
            Implemented : Spelling list
            Declared : Spelling list
        }

    /// A substitution case, and constraints over each side's top context. The declared side draws
    /// from the implementing side's constraints and two more, with repeats, so that one declared
    /// constraint is often compared against the same implementing one more than once.
    let private genConstraintCase : Gen<ConstraintCase> =
        gen {
            let! substitution = genSubstitutionCase
            let arity = substitution.Left.TopArity
            let! implementedCount = Gen.choose (1, 2)
            let! implemented = Gen.listOfLength implementedCount (genSpelling arity)
            let! others = Gen.listOfLength 2 (genSpelling arity)
            let! declaredCount = Gen.choose (1, 3)
            let! declared = Gen.listOfLength declaredCount (Gen.elements (implemented @ others))

            return
                {
                    Substitution = substitution
                    Implemented = implemented
                    Declared = declared
                }
        }

    let private parameterConstrainedTo (constraints : TypeDefn list) : GenericParamMetadata =
        {
            Variance = None
            RequiresReferenceType = false
            RequiresNonNullableValueType = false
            RequiresParameterlessConstructor = false
            AllowsByRefLike = false
            Constraints = ImmutableArray.CreateRange constraints
        }

    /// `methodConstraintsMatch` is the one comparison that tries alternatives: a `false` against one
    /// declared constraint moves on to the next rather than ending the whole answer. So it is the one
    /// in which recording an unequal pair as proved, or recording a pair before it is proved, changes
    /// an answer.
    [<Test>]
    let ``methodConstraintsMatch agrees with comparing the expanded trees`` () : unit =
        let property (cover : Answer -> unit) (case : ConstraintCase) : unit =
            let buildLeft, buildRight = builders case.Substitution.Sharing
            let left = buildChain buildLeft case.Substitution.Left
            let right = buildChain buildRight case.Substitution.Right
            let implemented = case.Implemented |> List.map buildLeft
            let declared = case.Declared |> List.map buildRight

            let expected =
                implemented
                |> List.forall (fun implementedConstraint ->
                    declared
                    |> List.exists (fun declaredConstraint ->
                        expandSpelling left.Arguments implementedConstraint = expandSpelling
                            right.Arguments
                            declaredConstraint
                    )
                )

            cover (answerOf expected)

            let comparand
                (context : TypeConcretization.SubstitutionContext)
                (constraints : TypeDefn list)
                : TypeConcretization.ConstraintComparand
                =
                {
                    Parameters = [ parameterConstrainedTo constraints ]
                    AssemblyFullName = corelibName
                    DeclaringTypeGenerics = context
                }

            match
                TypeConcretization.methodConstraintsMatch
                    concretizationContext
                    IAssemblyLoad.alreadyLoadedOnly
                    (comparand left implemented)
                    (comparand right declared)
            with
            | Ok actual, _ -> actual |> shouldEqual expected
            | Error failure, _ -> failwith $"the comparison failed to bind: %A{failure}"

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 1000) (Arb.fromGen genConstraintCase) property

        coverage.Count Answer.Equivalent |> shouldBeGreaterThan 200
        coverage.Count Answer.Inequivalent |> shouldBeGreaterThan 200

    /// The alphabet with at most two variable occurrences in a spelling, so that a chain of depth `d`
    /// denotes trees of at most `2^d` leaves per argument and a run that walks them as trees still
    /// ends. `doubling` weights the spellings with two occurrences against each of the others: the
    /// nearer every spelling is to two, the nearer `2^d` the trees are, which is what makes walking
    /// them as trees fail the bound by orders of magnitude rather than by a little.
    let private genBoundedSpelling (doubling : int) (arity : int) : Gen<Spelling> =
        let variable = genVariable arity

        Gen.frequency
            [
                1, variable
                doubling, Gen.map2 (fun key value -> Spelling.Pair (key, value)) variable variable
                1, Gen.map Spelling.Vector variable
                1, Gen.map Spelling.Pointer variable
                doubling,
                Gen.map2
                    (fun key value -> Spelling.Pair (Spelling.Vector key, Spelling.Pointer value))
                    variable
                    variable
                doubling, Gen.map2 (fun key value -> Spelling.Vector (Spelling.Pair (key, value))) variable variable
                1, Gen.map (fun key -> Spelling.Pair (key, Spelling.Integer)) variable
                1, Gen.constant Spelling.Integer
            ]

    /// `spelling` with its first leaf changed: a variable becomes `int32` and `int32` becomes `!0`.
    let rec private changeFirstLeaf (spelling : Spelling) : Spelling =
        match spelling with
        | Spelling.Variable _ -> Spelling.Integer
        | Spelling.Integer -> Spelling.Variable 0
        | Spelling.Pair (key, value) -> Spelling.Pair (changeFirstLeaf key, value)
        | Spelling.Vector element -> Spelling.Vector (changeFirstLeaf element)
        | Spelling.Pointer element -> Spelling.Pointer (changeFirstLeaf element)

    type private BoundCase =
        {
            Left : Chain
            Right : Chain
            InternLeft : bool
            InternRight : bool
        }

    /// Two chains of depth 1 to 20, built separately: either from one recipe, or differing in one
    /// leaf of the last argument of the bottom level.
    let private genBoundCase : Gen<BoundCase> =
        gen {
            let! depth = Gen.choose (1, 20)
            let! bottomArity = Gen.choose (1, 2)
            // Fuzzed over, from as likely as any other spelling to nine in ten of them.
            let! doubling = Gen.elements [ 1 ; 4 ; 16 ]
            let! left = genChain (genBoundedSpelling doubling) bottomArity depth
            let! differ = Gen.elements [ true ; false ]

            let right =
                if differ then
                    { left with
                        Levels =
                            match left.Levels with
                            | bottom :: above ->
                                let last = List.length bottom - 1

                                (bottom
                                 |> List.mapi (fun i spelling ->
                                     if i = last then changeFirstLeaf spelling else spelling
                                 ))
                                :: above
                            | [] -> failwith "a bound case has at least one level"
                    }
                else
                    left

            let! internLeft = Gen.elements [ true ; false ]
            let! internRight = Gen.elements [ true ; false ]

            return
                {
                    Left = left
                    Right = right
                    InternLeft = internLeft
                    InternRight = internRight
                }
        }

    /// The most nodes in any one level's spellings, over both sides.
    let private largestLevel (case : BoundCase) : int =
        case.Left.Levels @ case.Right.Levels
        |> List.map (List.sumBy spellingSize)
        |> List.max

    /// `K` in the bound: twenty times the largest `bytes / (depth * (1 + s)^2)` measured with sharing
    /// in place, which was 195.8 over 20,000 cases of this generator. Walking the chains as trees
    /// instead allocates about 2.6 kB per element compared, so a depth-20 case exceeds the bound by
    /// orders of magnitude.
    let private bytesPerUnit : int64 = 4000L

    [<Test>]
    let ``a comparison allocates at most quadratically in what the chains hold`` () : unit =
        // One untimed call first, so that one-off work is not charged to a measured case.
        equalSides 4 ||> measure |> ignore<bool * int64>

        // Reported rather than asserted, as the measurement `bytesPerUnit` was chosen from.
        let largestRatio = ref 0.0

        let property (case : BoundCase) : unit =
            let builder (intern : bool) : Spelling -> TypeDefn =
                if intern then internedBuilder () else freshBuilder ()

            let left = buildChain (builder case.InternLeft) case.Left
            let right = buildChain (builder case.InternRight) case.Right
            let _, allocated = measure left right
            let depth = int64 case.Left.Levels.Length
            let largest = 1L + int64 (largestLevel case)
            let units = depth * largest * largest
            largestRatio.Value <- max largestRatio.Value (float allocated / float units)
            allocated |> shouldBeSmallerThan (bytesPerUnit * units + 1L)

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 200, Prop.forAll (Arb.fromGen genBoundCase) property)
        TestContext.Out.WriteLine $"largest bytes per unit: %.1f{largestRatio.Value}"

    /// A function pointer spelling the GENERIC calling convention, which a comparison against
    /// another with the same generic-parameter count refuses rather than answers.
    let private genericFunctionPointer : TypeDefn =
        TypeDefn.FunctionPointer
            {
                Header =
                    ComparableSignatureHeader.Make (
                        SignatureHeader (
                            SignatureKind.Method,
                            SignatureCallingConvention.Default,
                            SignatureAttributes.Generic
                        )
                    )
                ParameterTypes = [ TypeDefn.PrimitiveType PrimitiveType.Int32 ]
                GenericParameterCount = 1
                RequiredParameterCount = 1
                ReturnType = MethodReturnType.Void
            }

    /// The answer of one `substitutionsEquivalent` call, as the result says it.
    let private compareIn
        (ctx : TypeConcretization.ConcretizationContext<DumpedAssembly>)
        (left : TypeConcretization.SubstitutionContext)
        (right : TypeConcretization.SubstitutionContext)
        : Result<bool, TypeBindingFailure>
        =
        TypeConcretization.substitutionsEquivalent ctx IAssemblyLoad.alreadyLoadedOnly left right
        |> fst

    /// Being the same object is not being equal: comparing an element with itself can refuse, so a
    /// context compared with itself must refuse wherever comparing it with an equal copy does. Both
    /// at the root, and through a type variable, where the comparison keys pairs by identity.
    [<Test>]
    let ``a substitution compared with itself refuses as a copy of it does`` () : unit =
        let atRoot () : TypeConcretization.SubstitutionContext =
            TypeConcretization.SubstitutionContext.forDefinition keyValuePair 1
            |> TypeConcretization.SubstitutionContext.forBase corelibName (ImmutableArray.Create genericFunctionPointer)

        let throughVariable () : TypeConcretization.SubstitutionContext =
            atRoot ()
            |> TypeConcretization.SubstitutionContext.forBase
                corelibName
                (ImmutableArray.Create (TypeDefn.GenericTypeParameter 0))

        for build in [ atRoot ; throughVariable ] do
            let subject = build ()

            for right in [ subject ; build () ] do
                match compareIn concretizationContext subject right with
                | Error (TypeBindingFailure.Unmodelled unmodelled) ->
                    unmodelled.Diagnostic |> shouldContainText "GENERIC calling convention"
                | other -> failwith $"expected the comparison to be refused, got %A{other}"

    /// What a comparison answers depends on its arguments alone, not on comparisons made before it:
    /// the pairs one call proves are not carried into the next. Here the first call is made where the
    /// assembly a TypeRef needs is loaded, and proves the pair; the second, of the very same objects,
    /// is made where it is not, and must fail to load it just as it does when made first.
    [<Test>]
    let ``a comparison's answer does not depend on comparisons made before it`` () : unit =
        let image =
            Roslyn.compileAssembly
                "SubstitutionComparisonSharingTypeRef"
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary
                []
                [
                    "public class Holder { public System.Collections.Generic.LinkedList<int> Field; }"
                ]

        let assembly =
            use stream = new IO.MemoryStream (image)
            let _, loggerFactory = LoggerFactory.makeTest ()
            AssemblyApi.read loggerFactory None stream

        // `LinkedList`1` is declared outside CoreLib, so naming it by TypeRef needs this assembly.
        let systemCollections : DumpedAssembly =
            let _, loggerFactory = LoggerFactory.makeTest ()
            Assembly.readFile loggerFactory (IO.Path.Combine (HostImage.frameworkDirectory, "System.Collections.dll"))

        let linkedListReference : TypeDefn =
            assembly.TypeRefs.Values
            |> Seq.find (fun typeRef ->
                typeRef.Namespace = "System.Collections.Generic"
                && typeRef.Name = "LinkedList`1"
            )
            |> fun typeRef -> TypeDefn.FromReference (typeRef, SignatureTypeKind.Class)

        let linkedListDefinition : TypeDefn =
            systemCollections.TryGetTopLevelTypeDef "System.Collections.Generic" "LinkedList`1"
            |> Option.defaultWith (fun () ->
                failwith "expected System.Collections.Generic.LinkedList`1 in System.Collections"
            )
            |> fun ty -> TypeDefn.FromDefinition (ty.Identity, SignatureTypeKind.Class)

        // `!0` over a context supplying `spelling`, so that the pair is reached through a variable.
        let chain (spellingAssembly : string) (spelling : TypeDefn) : TypeConcretization.SubstitutionContext =
            TypeConcretization.SubstitutionContext.forDefinition keyValuePair 1
            |> TypeConcretization.SubstitutionContext.forBase spellingAssembly (ImmutableArray.Create spelling)
            |> TypeConcretization.SubstitutionContext.forBase
                spellingAssembly
                (ImmutableArray.Create (TypeDefn.GenericTypeParameter 0))

        let left = chain assembly.DefinitionFullName linkedListReference
        let right = chain systemCollections.DefinitionFullName linkedListDefinition

        let withSystemCollections =
            { concretizationContext with
                LoadedAssemblies = LoadedAssemblies.ofAssemblies [ corelib ; assembly ; systemCollections ]
            }

        let withoutSystemCollections =
            { concretizationContext with
                LoadedAssemblies = LoadedAssemblies.ofAssemblies [ corelib ; assembly ]
            }

        let failsToLoad () : unit =
            let thrown =
                Assert.Throws<exn> (fun () ->
                    compareIn withoutSystemCollections left right
                    |> ignore<Result<bool, TypeBindingFailure>>
                )

            thrown.Message |> shouldContainText "System.Collections"

        failsToLoad ()
        compareIn withSystemCollections left right |> shouldEqual (Ok true)
        failsToLoad ()

    /// A comparison whose contexts supply no spelled argument cannot meet a pair twice, so it keeps no
    /// record of pairs: a closed signature comparison costs what the same comparison with nothing
    /// substituted costs, give or take its two contexts.
    [<Test>]
    let ``a closed comparison keeps no record of pairs`` () : unit =
        let signature : TypeMethodSignature<TypeDefn> =
            let parameters =
                List.init
                    64
                    (fun _ -> TypeDefn.OneDimensionalArrayLowerBoundZero (TypeDefn.PrimitiveType PrimitiveType.Int32))

            {
                Header =
                    ComparableSignatureHeader.Make (
                        SignatureHeader (
                            SignatureKind.Method,
                            SignatureCallingConvention.Default,
                            SignatureAttributes.None
                        )
                    )
                ParameterTypes = parameters
                GenericParameterCount = 0
                RequiredParameterCount = parameters.Length
                ReturnType = MethodReturnType.Void
            }

        let closed () : bool * int64 =
            let comparand : TypeConcretization.SignatureComparand =
                {
                    Signature = signature
                    AssemblyFullName = corelibName
                    DeclaringTypeGenerics = TypeConcretization.SubstitutionContext.ofClosed ImmutableArray.Empty
                }

            let before = GC.GetAllocatedBytesForCurrentThread ()

            let result, _ =
                TypeConcretization.signaturesEquivalent
                    concretizationContext
                    IAssemblyLoad.alreadyLoadedOnly
                    false
                    comparand
                    comparand

            let allocated = GC.GetAllocatedBytesForCurrentThread () - before
            BindingResult.value result, allocated

        let unsubstituted () : bool * int64 =
            let comparand : TypeConcretization.UnsubstitutedComparand =
                {
                    Signature = signature
                    AssemblyFullName = corelibName
                }

            let before = GC.GetAllocatedBytesForCurrentThread ()

            let result, _ =
                TypeConcretization.signaturesEquivalentWithoutSubstitution
                    concretizationContext
                    IAssemblyLoad.alreadyLoadedOnly
                    false
                    comparand
                    comparand

            let allocated = GC.GetAllocatedBytesForCurrentThread () - before
            BindingResult.value result, allocated

        // Untimed first calls, so that one-off work is charged to neither.
        closed () |> ignore<bool * int64>
        unsubstituted () |> ignore<bool * int64>

        let closedAnswer, closedBytes = closed ()
        let unsubstitutedAnswer, unsubstitutedBytes = unsubstituted ()

        TestContext.Out.WriteLine $"closed: %d{closedBytes} bytes; unsubstituted: %d{unsubstitutedBytes} bytes"

        closedAnswer |> shouldEqual true
        unsubstitutedAnswer |> shouldEqual true
        // A record of 64 pairs costs kilobytes; the two contexts' own representations, a few dozen bytes.
        closedBytes |> shouldBeSmallerThan (unsubstitutedBytes + 256L)
