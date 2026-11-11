namespace WoofWare.PawPrint

open System.Collections.Immutable
open System.Reflection.Metadata

/// <summary>
/// What an operand of a stub the runtime emits names: an entry of what CoreCLR's stub linker keeps
/// in the stub's <c>TokenLookupMap</c> (<c>ILCodeStream::GetToken</c>), which the runtime resolved
/// itself rather than read from a token of any image.
/// </summary>
/// <remarks>
/// Each case is relative to the method the stub runs for, whose frame resolves it.
/// </remarks>
[<RequireQualifiedAccess>]
type StubOperand =
    /// A field CoreLib declares on a type that is not generic.
    | CoreLibField of FieldDefinitionHandle
    /// A method CoreLib declares, neither it nor its type generic.
    | CoreLibMethod of MethodDefinitionHandle
    /// A method CoreLib declares on a type that is not generic, with one generic parameter,
    /// instantiated over the result type of the promise the stub's method returns: the `T` of its
    /// `Task<T>` or `ValueTask<T>` (`GetTokenForGenericMethodCallWithAsyncReturnType`,
    /// asyncthunks.cpp).
    | CoreLibMethodAtResultType of MethodDefinitionHandle
    /// A type CoreLib declares that is not generic.
    | CoreLibType of TypeDefinitionHandle
    /// The async variant of the method the stub runs for, at that method's own instantiation
    /// (`GetTokenForThunkTarget`, asyncthunks.cpp).
    | AsyncVariantOfStubMethod

/// <summary>
/// A stub the runtime emits and runs in place of a method's own body.
/// </summary>
/// <remarks>
/// Every token operand in <c>Body</c> is a <c>MetadataOperand.FromStubScope</c>, and every
/// <c>catch</c> clause's type an <c>ExceptionCatchType.FromStubScope</c>, indexing
/// <c>Operands</c>. <c>Body</c>'s locals are types in the context of the stub's method, whose
/// generic parameters they may name.
/// </remarks>
type VmStub =
    {
        Body : MethodInstructions
        Operands : ImmutableArray<StubOperand>
    }

/// The promise type a task-returning thunk's method returns, which decides the thunk's IL.
[<RequireQualifiedAccess>]
type ThunkPromise =
    /// `System.Threading.Tasks.Task`.
    | Task
    /// `System.Threading.Tasks.ValueTask`.
    | ValueTask
    /// `System.Threading.Tasks.Task<T>`, at `T` as the method's signature spells it.
    | TaskOf of result : TypeDefn
    /// `System.Threading.Tasks.ValueTask<T>`, at `T` as the method's signature spells it.
    | ValueTaskOf of result : TypeDefn

[<RequireQualifiedAccess>]
module VmStub =

    /// The stack effect of every instruction of `stub` that has an operand, for
    /// `StackShapeInputs.Tokens`: a CoreLib method's from its signature, and the call of the stub's
    /// method's async variant as `asyncVariant` says, since only the stub's caller can read that
    /// method's signature under its instantiation.
    let tokenShapes (corelib : DumpedAssembly) (asyncVariant : TokenShape) (stub : VmStub) : Map<int, TokenShape> =
        stub.Body.Instructions
        |> List.choose (fun (instruction, offset) ->
            match instruction with
            | IlOp.UnaryMetadataToken ((UnaryMetadataTokenIlOp.Call | UnaryMetadataTokenIlOp.Callvirt),
                                       MetadataOperand.FromStubScope index) ->
                match stub.Operands.[index] with
                | StubOperand.CoreLibMethod handle
                | StubOperand.CoreLibMethodAtResultType handle ->
                    // The instantiation's argument is never a float the callee's own signature
                    // passes or returns by itself: a `Task<T>`, `ValueTask<T>` or exception is.
                    StackShapeTokens.calleeShape
                        corelib
                        GenericBinding.AtDefinition
                        GenericSubstitution.None
                        corelib.Methods.[handle].Signature
                    |> Some
                | StubOperand.AsyncVariantOfStubMethod -> Some asyncVariant
                | StubOperand.CoreLibField _
                | StubOperand.CoreLibType _ as operand ->
                    failwith $"logic error: a stub calls its operand %d{index}, %O{operand}, which is not a method"
                |> Option.map (fun shape -> offset, shape)
            | IlOp.UnaryMetadataToken (op, MetadataOperand.FromStubScope index) ->
                // `ldsflda`, the only other operand a stub has, pushes an address whatever it names.
                match op with
                | UnaryMetadataTokenIlOp.Ldsflda -> None
                | op ->
                    failwith
                        $"logic error: a stub's %O{op} names operand %d{index}, and only call and ldsflda name one"
            | IlOp.UnaryMetadataToken (op, operand) ->
                failwith $"logic error: a stub's %O{op} names %O{operand}, which is not one of its operands"
            | IlOp.Nullary _
            | IlOp.UnaryConst _
            | IlOp.UnaryStringToken _
            | IlOp.Switch _ -> None
        )
        |> Map.ofList

/// <summary>
/// The task-returning thunk CoreCLR's VM emits as the ordinary variant of a runtime-async method
/// returning a promise type (<c>MethodDesc::EmitTaskReturningThunk</c>, asyncthunks.cpp; crossgen's
/// copy is <c>AsyncThunks.cs</c>): it calls the method's async variant, and wraps what that returns,
/// or throws, in the promise.
/// </summary>
/// <remarks>
/// In C#, roughly:
/// <code>
/// RuntimeAsyncStackState stackState;
/// ref RuntimeAsyncAwaitState awaitState = ref AsyncHelpers.t_runtimeAsyncAwaitState;
/// awaitState.Push(&amp;stackState);
/// try
/// {
///     try
///     {
///         T result = AsyncVariant(args);
///         if (AsyncHelpers.AsyncCallContinuation() == null)
///             return Task.FromResult(result);
///         return AsyncHelpers.CreateRuntimeAsyncTask(ref awaitState);
///     }
///     catch (Exception ex)
///     {
///         return AsyncHelpers.TaskFromException(ex);
///     }
/// }
/// finally
/// {
///     awaitState.Pop();
/// }
/// </code>
/// </remarks>
[<RequireQualifiedAccess>]
module TaskReturningThunk =

    let private find (description : string) (candidates : 'a list) : 'a =
        match candidates with
        | [ single ] -> single
        | [] -> failwith $"CoreLib has no %s{description}, which CoreCLR's VM names in the task-returning thunk"
        | several ->
            failwith
                $"CoreLib has %d{several.Length} candidates for %s{description}, which CoreCLR's VM binds by name in the task-returning thunk"

    let private topLevelType
        (corelib : DumpedAssembly)
        (ns : string)
        (name : string)
        : TypeInfo<GenericParamFromMetadata, TypeDefn>
        =
        corelib.TryGetTopLevelTypeDef ns name
        |> Option.toList
        |> find $"type %s{ns}.%s{name}"

    let private nestedType
        (corelib : DumpedAssembly)
        (enclosing : TypeInfo<GenericParamFromMetadata, TypeDefn>)
        (name : string)
        : TypeInfo<GenericParamFromMetadata, TypeDefn>
        =
        corelib.TryGetNestedTypeDef enclosing.Identity.TypeDefinition.Get "" name
        |> Option.toList
        |> find $"type %s{enclosing.Namespace}.%s{enclosing.Name}+%s{name}"

    /// The method of `declaringType` named `name` with `genericArity` generic parameters and
    /// `parameters` parameters, as corelib.h's binder picks it: by name, with the signature
    /// distinguishing overloads.
    let private method
        (declaringType : TypeInfo<GenericParamFromMetadata, TypeDefn>)
        (name : string)
        (genericArity : int)
        (parameters : int)
        : MethodDefinitionHandle
        =
        declaringType.Methods
        |> List.filter (fun m ->
            m.Name = name
            && m.Generics.Length = genericArity
            && m.Signature.ParameterTypes.Length = parameters
        )
        |> find $"method %s{declaringType.Namespace}.%s{declaringType.Name}::%s{name}`%d{genericArity}"
        |> MethodInfo.requireMetadata "the task-returning thunk"
        |> _.Handle

    let private field (declaringType : TypeInfo<GenericParamFromMetadata, TypeDefn>) (name : string) =
        declaringType.Fields
        |> List.filter (fun f -> f.Name = name)
        |> find $"field %s{declaringType.Namespace}.%s{declaringType.Name}::%s{name}"
        |> _.Handle

    let rec private unmodified (ty : TypeDefn) : TypeDefn =
        match ty with
        | TypeDefn.Modified modified -> unmodified modified.Unmodified
        | ty -> ty

    let private isValueType (token : TypeDefn) : bool =
        match unmodified token with
        | TypeDefn.FromDefinition (_, kind)
        | TypeDefn.FromReference (_, kind) -> kind = SignatureTypeKind.ValueType
        | other -> failwith $"logic error: %O{other} names a promise type, but is not a TypeDef or TypeRef"

    /// The promise `returnType` names, and the type the thunk's `ret` local has, which is the
    /// promise type without the custom modifiers the signature puts round it. The signature must be
    /// one `RuntimeAsyncMethod.classifyReturn` says returns a promise type, which it spells
    /// `valuetype` exactly when it is a `ValueTask`.
    let promiseOf (returnType : MethodReturnType<TypeDefn>) : ThunkPromise * TypeDefn =
        match returnType with
        | MethodReturnType.Void -> failwith "logic error: a method returning void has no task-returning thunk"
        | MethodReturnType.Returns ty ->
            let promiseType = unmodified ty

            let promise =
                match promiseType with
                | TypeDefn.GenericInstantiation (generic, args) when args.Length = 1 ->
                    if isValueType generic then
                        ThunkPromise.ValueTaskOf args.[0]
                    else
                        ThunkPromise.TaskOf args.[0]
                | TypeDefn.GenericInstantiation (_, args) ->
                    failwith $"logic error: a promise type with %d{args.Length} type arguments"
                | token ->
                    if isValueType token then
                        ThunkPromise.ValueTask
                    else
                        ThunkPromise.Task

            promise, promiseType

    let private loadArgument (index : int) : IlOp =
        match index with
        | 0 -> IlOp.Nullary NullaryIlOp.LdArg0
        | 1 -> IlOp.Nullary NullaryIlOp.LdArg1
        | 2 -> IlOp.Nullary NullaryIlOp.LdArg2
        | 3 -> IlOp.Nullary NullaryIlOp.LdArg3
        // The stub linker shortens to a form whose operand fits a *signed* byte (`FitsInI1`,
        // stubgen.cpp), though the short form's operand is unsigned.
        | index when index <= 127 -> IlOp.UnaryConst (UnaryConstIlOp.Ldarg_s (uint8 index))
        | index -> IlOp.UnaryConst (UnaryConstIlOp.Ldarg (uint16 index))

    let private loadLocal (index : int) : IlOp =
        match index with
        | 0 -> IlOp.Nullary NullaryIlOp.Ldloc_0
        | 1 -> IlOp.Nullary NullaryIlOp.Ldloc_1
        | 2 -> IlOp.Nullary NullaryIlOp.Ldloc_2
        | 3 -> IlOp.Nullary NullaryIlOp.Ldloc_3
        | index -> failwith $"logic error: the task-returning thunk has no local %d{index}"

    let private storeLocal (index : int) : IlOp =
        match index with
        | 0 -> IlOp.Nullary NullaryIlOp.Stloc_0
        | 1 -> IlOp.Nullary NullaryIlOp.Stloc_1
        | 2 -> IlOp.Nullary NullaryIlOp.Stloc_2
        | 3 -> IlOp.Nullary NullaryIlOp.Stloc_3
        | index -> failwith $"logic error: the task-returning thunk has no local %d{index}"

    /// The thunk for a method returning `promise`, whose `ret` local has type `promiseType`, and
    /// which takes `parameters` parameters after `this` if `hasThis`. The thunk's locals and
    /// operands name `promise`'s result type, which is a type in the method's own context.
    let build
        (corelib : DumpedAssembly)
        (promise : ThunkPromise)
        (promiseType : TypeDefn)
        (hasThis : bool)
        (parameters : int)
        : VmStub
        =
        let asyncHelpers =
            topLevelType corelib "System.Runtime.CompilerServices" "AsyncHelpers"

        let awaitState = nestedType corelib asyncHelpers "RuntimeAsyncAwaitState"
        let stackState = nestedType corelib asyncHelpers "RuntimeAsyncStackState"

        let task, isValueTask, result =
            match promise with
            | ThunkPromise.Task -> topLevelType corelib "System.Threading.Tasks" "Task", false, None
            | ThunkPromise.ValueTask -> topLevelType corelib "System.Threading.Tasks" "ValueTask", true, None
            | ThunkPromise.TaskOf result -> topLevelType corelib "System.Threading.Tasks" "Task", false, Some result
            | ThunkPromise.ValueTaskOf result ->
                topLevelType corelib "System.Threading.Tasks" "ValueTask", true, Some result

        // `NewLocal` in the order `EmitTaskReturningThunk` calls it.
        let resultLocal, returnLocal, stackStateLocal, awaitStateLocal =
            match result with
            | Some _ -> Some 0, 1, 2, 3
            | None -> None, 0, 1, 2

        let locals =
            [
                yield! Option.toList result
                yield promiseType
                yield TypeDefn.FromDefinition (stackState.Identity, SignatureTypeKind.ValueType)
                yield TypeDefn.Byref (TypeDefn.FromDefinition (awaitState.Identity, SignatureTypeKind.ValueType))
            ]

        // `GetToken`, which hands out one entry per call.
        let operands = ResizeArray<StubOperand> ()

        let withOperand (op : UnaryMetadataTokenIlOp) (operand : StubOperand) : IlStubCode =
            operands.Add operand
            IlStubCode.Op (IlOp.UnaryMetadataToken (op, MetadataOperand.FromStubScope (operands.Count - 1)))

        let call (operand : StubOperand) : IlStubCode =
            withOperand UnaryMetadataTokenIlOp.Call operand

        let op (instruction : IlOp) : IlStubCode = IlStubCode.Op instruction

        let nullary (instruction : NullaryIlOp) : IlStubCode =
            IlStubCode.Op (IlOp.Nullary instruction)

        let genericOrNot (name : string) : StubOperand =
            match result with
            | Some _ -> StubOperand.CoreLibMethodAtResultType (method asyncHelpers name 1 1)
            | None -> StubOperand.CoreLibMethod (method asyncHelpers name 0 1)

        let prologue =
            [
                withOperand
                    UnaryMetadataTokenIlOp.Ldsflda
                    (StubOperand.CoreLibField (field asyncHelpers "t_runtimeAsyncAwaitState"))
                op (storeLocal awaitStateLocal)
                op (loadLocal awaitStateLocal)
                op (IlOp.UnaryConst (UnaryConstIlOp.Ldloca_s (uint8 stackStateLocal)))
                call (StubOperand.CoreLibMethod (method awaitState "Push" 0 1))
            ]

        let arguments = parameters + (if hasThis then 1 else 0)

        let innerTry =
            [
                IlStubCode.Mark "innerTry"
                yield! List.init arguments (loadArgument >> op)
                call StubOperand.AsyncVariantOfStubMethod
                yield! resultLocal |> Option.map (storeLocal >> op) |> Option.toList
                call (StubOperand.CoreLibMethod (method asyncHelpers "AsyncCallContinuation" 0 0))
                IlStubCode.Branch (UnaryConstIlOp.Brfalse, "finished")
                IlStubCode.Branch (UnaryConstIlOp.Leave, "suspended")
                IlStubCode.Mark "finished"
                match resultLocal with
                | Some local ->
                    op (loadLocal local)
                    call (StubOperand.CoreLibMethodAtResultType (method task "FromResult" 1 1))
                | None -> call (StubOperand.CoreLibMethod (method task "get_CompletedTask" 0 0))
                op (storeLocal returnLocal)
                IlStubCode.Branch (UnaryConstIlOp.Leave, "return")
            ]

        let catchHandler =
            [
                IlStubCode.Mark "catch"
                call (
                    genericOrNot (
                        if isValueTask then
                            "ValueTaskFromException"
                        else
                            "TaskFromException"
                    )
                )
                op (storeLocal returnLocal)
                IlStubCode.Branch (UnaryConstIlOp.Leave, "return")
                IlStubCode.Mark "catchEnd"
            ]

        let suspended =
            [
                IlStubCode.Mark "suspended"
                op (loadLocal awaitStateLocal)
                call (
                    genericOrNot (
                        if isValueTask then
                            "CreateRuntimeAsyncValueTask"
                        else
                            "CreateRuntimeAsyncTask"
                    )
                )
                op (storeLocal returnLocal)
                IlStubCode.Branch (UnaryConstIlOp.Leave, "return")
            ]

        let finallyHandler =
            [
                IlStubCode.Mark "finally"
                op (loadLocal awaitStateLocal)
                call (StubOperand.CoreLibMethod (method awaitState "Pop" 0 0))
                nullary NullaryIlOp.Endfinally
                IlStubCode.Mark "finallyEnd"
            ]

        let epilogue =
            [
                IlStubCode.Mark "return"
                op (loadLocal returnLocal)
                nullary NullaryIlOp.Ret
            ]

        let code =
            [
                yield! prologue
                IlStubCode.Mark "outerTry"
                // "Separate try blocks": the two `try`s would otherwise begin at one offset.
                nullary NullaryIlOp.Nop
                yield! innerTry
                yield! catchHandler
                yield! suspended
                yield! finallyHandler
                yield! epilogue
            ]

        // The type the `catch` clause names.
        let exceptionType = topLevelType corelib "System" "Exception"

        let catchOperand =
            operands.Add (StubOperand.CoreLibType exceptionType.Identity.TypeDefinition.Get)
            operands.Count - 1

        let clauses =
            [
                {
                    TryStart = "innerTry"
                    TryEnd = "catch"
                    HandlerStart = "catch"
                    HandlerEnd = "catchEnd"
                    Handler = IlStubHandler.Catch (ExceptionCatchType.FromStubScope catchOperand)
                }
                {
                    TryStart = "outerTry"
                    TryEnd = "finally"
                    HandlerStart = "finally"
                    HandlerEnd = "finallyEnd"
                    Handler = IlStubHandler.Finally
                }
            ]

        {
            Body = IlStub.assemble locals code clauses
            Operands = ImmutableArray.CreateRange operands
        }

    /// The thunk of `method`, the ordinary variant of a runtime-async method returning a promise
    /// type, which `RuntimeAsyncMethod.asyncVariantOf` would give an async variant.
    let ofMethod (corelib : DumpedAssembly) (method : MethodInfo<'typeGenerics, 'methodGenerics, TypeDefn>) : VmStub =
        let promise, promiseType = promiseOf method.Signature.ReturnType
        build corelib promise promiseType (not method.IsStatic) method.Signature.ParameterTypes.Length
