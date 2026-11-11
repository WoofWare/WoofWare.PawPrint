namespace WoofWare.PawPrint.Test

open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Text.RegularExpressions
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The task-returning thunk as data (`TaskReturningThunk`): its IL is the IL CoreCLR's VM emits
/// (`EmitTaskReturningThunk`, asyncthunks.cpp), it passes PawPrint's own stack-shape analysis for
/// every shape of method, and its operands name exactly the CoreLib members the VM binds.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTaskReturningThunk =

    /// .NET 11's CoreLib, the first to have the thunk's helpers.
    let private corelib : Lazy<DumpedAssembly> =
        lazy
            (let _, loggerFactory = LoggerFactory.makeTest ()

             Assembly.readFile loggerFactory (FrameworkUnderTest.requireCoreLibPathOf EmulatedRuntime.Net11))

    let private corelibType (ns : string) (name : string) : TypeInfo<GenericParamFromMetadata, TypeDefn> =
        match corelib.Value.TryGetTopLevelTypeDef ns name with
        | Some ty -> ty
        | None -> failwith $"CoreLib has no %s{ns}.%s{name}"

    let private promiseTypeOf (promise : ThunkPromise) : TypeDefn =
        let named (name : string) (kind : SignatureTypeKind) =
            TypeDefn.FromDefinition ((corelibType "System.Threading.Tasks" name).Identity, kind)

        match promise with
        | ThunkPromise.Task -> named "Task" SignatureTypeKind.Class
        | ThunkPromise.ValueTask -> named "ValueTask" SignatureTypeKind.ValueType
        | ThunkPromise.TaskOf result ->
            TypeDefn.GenericInstantiation (named "Task`1" SignatureTypeKind.Class, ImmutableArray.Create result)
        | ThunkPromise.ValueTaskOf result ->
            TypeDefn.GenericInstantiation (
                named "ValueTask`1" SignatureTypeKind.ValueType,
                ImmutableArray.Create result
            )

    let private ldarg (index : int) : IlOp =
        match index with
        | 0 -> IlOp.Nullary NullaryIlOp.LdArg0
        | 1 -> IlOp.Nullary NullaryIlOp.LdArg1
        | 2 -> IlOp.Nullary NullaryIlOp.LdArg2
        | 3 -> IlOp.Nullary NullaryIlOp.LdArg3
        | index when index < 128 -> IlOp.UnaryConst (UnaryConstIlOp.Ldarg_s (uint8 index))
        | index -> IlOp.UnaryConst (UnaryConstIlOp.Ldarg (uint16 index))

    /// What each instruction of a stub is, with its operand replaced by what the operand names.
    [<RequireQualifiedAccess>]
    type private Step =
        /// An instruction with no operand of the stub's, as `IlOp.Format` renders it.
        | Op of string
        | Named of UnaryMetadataTokenIlOp * StubOperand

    let private opStep (op : IlOp) : Step = Step.Op (IlOp.Format op 0)

    let private steps (stub : VmStub) : Step list =
        stub.Body.Instructions
        |> List.map (fun (op, _) ->
            match op with
            | IlOp.UnaryMetadataToken (token, MetadataOperand.FromStubScope index) ->
                Step.Named (token, stub.Operands.[index])
            | op -> opStep op
        )

    let private methodNamed (ns : string) (typeName : string) (name : string) (genericArity : int) (parameters : int) =
        let ty =
            if typeName.Contains '+' then
                let outer, inner = typeName.Split('+').[0], typeName.Split('+').[1]

                corelib.Value.TryGetNestedTypeDef (corelibType ns outer).Identity.TypeDefinition.Get "" inner
                |> Option.get
            else
                corelibType ns typeName

        ty.Methods
        |> List.filter (fun m ->
            m.Name = name
            && m.Generics.Length = genericArity
            && m.Signature.ParameterTypes.Length = parameters
        )
        |> List.exactlyOne
        |> MethodInfo.requireMetadata "TestTaskReturningThunk"
        |> _.Handle

    /// `EmitTaskReturningThunk` for an instance method of two parameters returning `Task<int>`,
    /// written out instruction by instruction, with its labels resolved.
    [<Test>]
    let ``the thunk of an instance method returning Task<T> is the IL the VM emits`` () : unit =
        let result = TypeDefn.PrimitiveType PrimitiveType.Int32
        let promise = ThunkPromise.TaskOf result

        let stub =
            TaskReturningThunk.build corelib.Value promise (promiseTypeOf promise) true 2

        let ch = "System.Runtime.CompilerServices"
        let tasks = "System.Threading.Tasks"
        let asyncHelpers = corelibType ch "AsyncHelpers"

        let awaitStateField =
            asyncHelpers.Fields
            |> List.find (fun f -> f.Name = "t_runtimeAsyncAwaitState")
            |> _.Handle

        let at = stub.Body.Instructions |> List.map snd |> Array.ofList

        let expected =
            [
                Step.Named (UnaryMetadataTokenIlOp.Ldsflda, StubOperand.CoreLibField awaitStateField)
                opStep (IlOp.Nullary NullaryIlOp.Stloc_3)
                opStep (IlOp.Nullary NullaryIlOp.Ldloc_3)
                opStep (IlOp.UnaryConst (UnaryConstIlOp.Ldloca_s 2uy))
                Step.Named (
                    UnaryMetadataTokenIlOp.Call,
                    StubOperand.CoreLibMethod (methodNamed ch "AsyncHelpers+RuntimeAsyncAwaitState" "Push" 0 1)
                )
                opStep (IlOp.Nullary NullaryIlOp.Nop)
                opStep (IlOp.Nullary NullaryIlOp.LdArg0)
                opStep (IlOp.Nullary NullaryIlOp.LdArg1)
                opStep (IlOp.Nullary NullaryIlOp.LdArg2)
                Step.Named (UnaryMetadataTokenIlOp.Call, StubOperand.AsyncVariantOfStubMethod)
                opStep (IlOp.Nullary NullaryIlOp.Stloc_0)
                Step.Named (
                    UnaryMetadataTokenIlOp.Call,
                    StubOperand.CoreLibMethod (methodNamed ch "AsyncHelpers" "AsyncCallContinuation" 0 0)
                )
                // brfalse FINISHED, over the `leave`
                opStep (IlOp.UnaryConst (UnaryConstIlOp.Brfalse 5))
                // leave SUSPENDED, over the finished block and the catch handler
                opStep (IlOp.UnaryConst (UnaryConstIlOp.Leave (at.[21] - at.[14])))
                opStep (IlOp.Nullary NullaryIlOp.Ldloc_0)
                Step.Named (
                    UnaryMetadataTokenIlOp.Call,
                    StubOperand.CoreLibMethodAtResultType (methodNamed tasks "Task" "FromResult" 1 1)
                )
                opStep (IlOp.Nullary NullaryIlOp.Stloc_1)
                opStep (IlOp.UnaryConst (UnaryConstIlOp.Leave (at.[28] - at.[18])))
                Step.Named (
                    UnaryMetadataTokenIlOp.Call,
                    StubOperand.CoreLibMethodAtResultType (methodNamed ch "AsyncHelpers" "TaskFromException" 1 1)
                )
                opStep (IlOp.Nullary NullaryIlOp.Stloc_1)
                opStep (IlOp.UnaryConst (UnaryConstIlOp.Leave (at.[28] - at.[21])))
                opStep (IlOp.Nullary NullaryIlOp.Ldloc_3)
                Step.Named (
                    UnaryMetadataTokenIlOp.Call,
                    StubOperand.CoreLibMethodAtResultType (methodNamed ch "AsyncHelpers" "CreateRuntimeAsyncTask" 1 1)
                )
                opStep (IlOp.Nullary NullaryIlOp.Stloc_1)
                opStep (IlOp.UnaryConst (UnaryConstIlOp.Leave (at.[28] - at.[25])))
                opStep (IlOp.Nullary NullaryIlOp.Ldloc_3)
                Step.Named (
                    UnaryMetadataTokenIlOp.Call,
                    StubOperand.CoreLibMethod (methodNamed ch "AsyncHelpers+RuntimeAsyncAwaitState" "Pop" 0 0)
                )
                opStep (IlOp.Nullary NullaryIlOp.Endfinally)
                opStep (IlOp.Nullary NullaryIlOp.Ldloc_1)
                opStep (IlOp.Nullary NullaryIlOp.Ret)
            ]

        steps stub |> shouldEqual expected

        stub.Body.LocalVars
        |> shouldEqual (
            Some (
                ImmutableArray.Create (
                    result,
                    promiseTypeOf promise,
                    TypeDefn.FromDefinition (
                        (corelib.Value.TryGetNestedTypeDef
                            asyncHelpers.Identity.TypeDefinition.Get
                            ""
                            "RuntimeAsyncStackState"
                         |> Option.get)
                            .Identity,
                        SignatureTypeKind.ValueType
                    ),
                    TypeDefn.Byref (
                        TypeDefn.FromDefinition (
                            (corelib.Value.TryGetNestedTypeDef
                                asyncHelpers.Identity.TypeDefinition.Get
                                ""
                                "RuntimeAsyncAwaitState"
                             |> Option.get)
                                .Identity,
                            SignatureTypeKind.ValueType
                        )
                    )
                )
            )
        )

        // The `catch` covers from the first `ldarg` to the handler, which runs to `SUSPENDED`; the
        // `finally`'s `try` runs from the `nop` to the handler, which runs to `RETURN`.
        let catchType = corelibType "System" "Exception"

        match List.ofSeq stub.Body.ExceptionRegions with
        | [ ExceptionRegion.Catch (ExceptionCatchType.FromStubScope catchOperand, inner) ; ExceptionRegion.Finally outer ] ->
            stub.Operands.[catchOperand]
            |> shouldEqual (StubOperand.CoreLibType catchType.Identity.TypeDefinition.Get)

            inner
            |> shouldEqual
                {
                    TryOffset = at.[6]
                    TryLength = at.[18] - at.[6]
                    HandlerOffset = at.[18]
                    HandlerLength = at.[21] - at.[18]
                }

            outer
            |> shouldEqual
                {
                    TryOffset = at.[5]
                    TryLength = at.[25] - at.[5]
                    HandlerOffset = at.[25]
                    HandlerLength = at.[28] - at.[25]
                }
        | regions -> failwith $"unexpected exception regions %A{regions}"

    /// The result types the thunk is built over: a float of each width, which the stack-shape
    /// analysis tells apart, an object, and a generic parameter of the method.
    let private results : TypeDefn list =
        [
            TypeDefn.PrimitiveType PrimitiveType.Int32
            TypeDefn.PrimitiveType PrimitiveType.Single
            TypeDefn.PrimitiveType PrimitiveType.Double
            TypeDefn.PrimitiveType PrimitiveType.Object
            TypeDefn.GenericMethodParameter 0
        ]

    let private genPromise : Gen<ThunkPromise> =
        Gen.oneof
            [
                Gen.constant ThunkPromise.Task
                Gen.constant ThunkPromise.ValueTask
                Gen.elements results |> Gen.map ThunkPromise.TaskOf
                Gen.elements results |> Gen.map ThunkPromise.ValueTaskOf
            ]

    [<Test>]
    let ``every thunk passes the stack-shape analysis, loading every argument in order, and ends returning its promise``
        ()
        : unit
        =
        let property (promise : ThunkPromise) (hasThis : bool) (parameters : int) : unit =
            let stub =
                TaskReturningThunk.build corelib.Value promise (promiseTypeOf promise) hasThis parameters

            let arguments = parameters + (if hasThis then 1 else 0)
            let binding = GenericBinding.AtDefinition

            let shapeOf (ty : TypeDefn) =
                StackShapeTokens.shapeOfTypeDefn corelib.Value binding ty

            let returns =
                match promise with
                | ThunkPromise.TaskOf result
                | ThunkPromise.ValueTaskOf result -> Some (shapeOf result)
                | ThunkPromise.Task
                | ThunkPromise.ValueTask -> None

            let inputs : StackShapeInputs =
                {
                    Arguments = ImmutableArray.CreateRange (List.replicate arguments SlotShape.Other)
                    Locals =
                        stub.Body.LocalVars
                        |> Option.defaultValue ImmutableArray.Empty
                        |> Seq.map shapeOf
                        |> ImmutableArray.CreateRange
                    ReturnsValue = true
                    Tokens = VmStub.tokenShapes corelib.Value (TokenShape.Callee (arguments, returns)) stub
                }

            let shape = StackShape.analyse inputs stub.Body

            if not shape.Invalid.IsEmpty then
                failwith $"%A{promise} (this: %b{hasThis}, %d{parameters}) is invalid at %A{shape.Invalid}"

            for _, offset in stub.Body.Instructions do
                if not (shape.Entry.ContainsKey offset) then
                    failwith $"offset %d{offset} of %A{promise} (this: %b{hasThis}, %d{parameters}) is untyped"

            let ops = stub.Body.Instructions |> List.map fst

            // The `ret` local follows the result's, where the promise has a result.
            match promise, List.item (ops.Length - 2) ops, List.last ops with
            | (ThunkPromise.Task | ThunkPromise.ValueTask),
              IlOp.Nullary NullaryIlOp.Ldloc_0,
              IlOp.Nullary NullaryIlOp.Ret
            | (ThunkPromise.TaskOf _ | ThunkPromise.ValueTaskOf _),
              IlOp.Nullary NullaryIlOp.Ldloc_1,
              IlOp.Nullary NullaryIlOp.Ret -> ()
            | _, penultimate, last -> failwith $"the thunk of %A{promise} ends %A{penultimate}; %A{last}"

            // The arguments are loaded in order just before the async variant is called.
            let call =
                steps stub
                |> List.findIndex (fun step ->
                    step = Step.Named (UnaryMetadataTokenIlOp.Call, StubOperand.AsyncVariantOfStubMethod)
                )

            ops
            |> List.skip (call - arguments)
            |> List.take arguments
            |> List.map (fun op -> IlOp.Format op 0)
            |> shouldEqual (List.init arguments (fun i -> IlOp.Format (ldarg i) 0))

            match shape.Entry.[snd (List.item call stub.Body.Instructions)] with
            | entry -> entry.Length |> shouldEqual arguments

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 200,
            Prop.forAll
                (Arb.fromGen (
                    gen {
                        let! promise = genPromise
                        let! hasThis = Gen.elements [ true ; false ]
                        let! parameters = Gen.choose (0, 300)
                        return promise, hasThis, parameters
                    }
                ))
                (fun (promise, hasThis, parameters) -> property promise hasThis parameters)
        )

    /// What `EmitTaskReturningThunk` names through corelib.h's binder (`CoreLibBinder::GetMethod`
    /// and the like), as each name's type and member: every `METHOD__`, `FIELD__` and `CLASS__` it
    /// mentions, looked up in corelib.h.
    let private namedByTheVm () : Set<string * string> =
        let source = FrameworkUnderTest.requireRuntimeSourceOf EmulatedRuntime.Net11

        let thunks =
            File.ReadAllText (Path.Combine (source, "src", "coreclr", "vm", "asyncthunks.cpp"))

        let start = thunks.IndexOf "void MethodDesc::EmitTaskReturningThunk"
        let finish = thunks.IndexOf ("\n}\n", start)
        let body = thunks.Substring (start, finish - start)

        let corelibH =
            File.ReadAllLines (Path.Combine (source, "src", "coreclr", "vm", "corelib.h"))

        let classes =
            corelibH
            |> Seq.choose (fun line ->
                let m = Regex.Match (line, @"^DEFINE_CLASS\(\s*(\w+),\s*[\w.]+,\s*([\w+`]+)\s*\)")

                if m.Success then
                    // The simple name of the innermost type.
                    Some (m.Groups.[1].Value, m.Groups.[2].Value.Split ('+') |> Array.last)
                else
                    None
            )
            |> Map.ofSeq

        let members =
            corelibH
            |> Seq.choose (fun line ->
                let m = Regex.Match (line, @"^DEFINE_(?:METHOD|FIELD)\(\s*(\w+),\s*(\w+),\s*(\w+)")

                if m.Success then
                    classes
                    |> Map.tryFind m.Groups.[1].Value
                    |> Option.map (fun cls ->
                        $"%s{m.Groups.[1].Value}__%s{m.Groups.[2].Value}", (cls, m.Groups.[3].Value)
                    )
                else
                    None
            )
            |> Map.ofSeq

        Regex.Matches (body, @"\b(METHOD|FIELD|CLASS)__(\w+)")
        |> Seq.map (fun m ->
            match m.Groups.[1].Value with
            | "CLASS" -> classes.[m.Groups.[2].Value], ""
            | _ -> members.[m.Groups.[2].Value]
        )
        |> Set.ofSeq

    [<Test>]
    let ``the thunks name exactly the CoreLib members the VM's thunk names`` () : unit =
        let ch = "System.Runtime.CompilerServices"
        let asyncHelpers = corelibType ch "AsyncHelpers"

        let typeName (handle : TypeDefinitionHandle) : string = corelib.Value.TypeDefs.[handle].Name

        let named =
            [
                ThunkPromise.Task
                ThunkPromise.ValueTask
                ThunkPromise.TaskOf (TypeDefn.PrimitiveType PrimitiveType.Int32)
                ThunkPromise.ValueTaskOf (TypeDefn.PrimitiveType PrimitiveType.Int32)
            ]
            |> List.collect (fun promise ->
                let stub =
                    TaskReturningThunk.build corelib.Value promise (promiseTypeOf promise) false 0

                let operands =
                    stub.Operands
                    |> Seq.choose (fun operand ->
                        match operand with
                        | StubOperand.CoreLibField handle ->
                            let field = corelib.Value.Fields.[handle]
                            Some (field.DeclaringType.Name, field.Name)
                        | StubOperand.CoreLibMethod handle
                        | StubOperand.CoreLibMethodAtResultType handle ->
                            let m = corelib.Value.Methods.[handle]
                            Some (m.RequiredDeclaringType.Name, m.Name)
                        | StubOperand.CoreLibType handle -> Some (typeName handle, "")
                        | StubOperand.AsyncVariantOfStubMethod -> None
                    )
                    |> List.ofSeq

                // The VM names its locals' types through the binder too.
                let locals =
                    stub.Body.LocalVars
                    |> Option.defaultValue ImmutableArray.Empty
                    |> Seq.choose (fun local ->
                        match local with
                        | TypeDefn.FromDefinition (identity, _)
                        | TypeDefn.Byref (TypeDefn.FromDefinition (identity, _)) when
                            identity.AssemblyFullName = corelib.Value.DefinitionFullName
                            && corelib.Value.TypeDefs.[identity.TypeDefinition.Get].Namespace = ""
                            ->
                            Some (typeName identity.TypeDefinition.Get, "")
                        | _ -> None
                    )
                    |> List.ofSeq

                operands @ locals
            )
            |> Set.ofList

        // A truncated slice of asyncthunks.cpp would pass vacuously.
        named
        |> Set.contains ("AsyncHelpers", "t_runtimeAsyncAwaitState")
        |> shouldEqual true

        asyncHelpers.Name |> shouldEqual "AsyncHelpers"

        named |> shouldEqual (namedByTheVm ())
