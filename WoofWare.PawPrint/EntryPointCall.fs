namespace WoofWare.PawPrint

open System.Collections.Immutable
open System.Reflection.Metadata

/// What calls a program's `Main` on the VM's behalf.
[<RequireQualifiedAccess>]
type internal MainCaller =
    /// The VM itself, from native code, as .NET 10's `RunMainInternal` does: `Main` is the entry
    /// thread's bottom frame.
    | Vm
    /// CoreLib's `[UnmanagedCallersOnly] System.Environment.CallEntryPoint`, which .NET 11's
    /// `RunMainInternal` calls, and which calls `Main` through a function pointer.
    | CallEntryPoint

[<RequireQualifiedAccess>]
module internal MainCaller =

    /// What calls `Main` on `runtime`.
    let ofRuntime (runtime : EmulatedRuntime) : MainCaller =
        match runtime with
        | EmulatedRuntime.Net10 -> MainCaller.Vm
        | EmulatedRuntime.Net11 -> MainCaller.CallEntryPoint

/// Finding CoreLib's `Environment.CallEntryPoint`, and building the entry thread's bottom frame
/// on it.
[<RequireQualifiedAccess>]
module internal EntryPointCall =

    let private purpose : string = "call the program's Main"

    let private staticHeader : SignatureHeader =
        SignatureHeader (SignatureKind.Method, SignatureCallingConvention.Default, SignatureAttributes.None)

    /// The signature `RunMainInternal` calls: `static void CallEntryPoint(IntPtr entryPoint,
    /// string[]* pArgument, int* pReturnValue, bool captureException, Exception* pException)`.
    let private describeExpected : string =
        "static void CallEntryPoint(System.IntPtr, string[]*, int*, bool, System.Exception*)"

    let private hasExpectedSignature
        (corelibException : ResolvedTypeIdentity)
        (signature : TypeMethodSignature<TypeDefn>)
        : bool
        =
        signature.Header.Get = staticHeader
        && signature.ReturnType = MethodReturnType.Void
        && signature.ParameterTypes = [
            TypeDefn.PrimitiveType PrimitiveType.IntPtr
            TypeDefn.Pointer (TypeDefn.OneDimensionalArrayLowerBoundZero (TypeDefn.PrimitiveType PrimitiveType.String))
            TypeDefn.Pointer (TypeDefn.PrimitiveType PrimitiveType.Int32)
            TypeDefn.PrimitiveType PrimitiveType.Boolean
            TypeDefn.Pointer (TypeDefn.FromDefinition (corelibException, SignatureTypeKind.Class))
        ]

    /// CoreLib's `System.Environment::CallEntryPoint`. Refuses when there is none, when there are
    /// several, or when the one there is has a signature other than the one `RunMainInternal`
    /// calls.
    let locate
        (corelib : DumpedAssembly)
        : WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>
        =
        let environment =
            CorelibEntryPoint.findCorelibType corelib "System" "Environment" purpose

        let exceptionType =
            CorelibEntryPoint.findCorelibType corelib "System" "Exception" purpose

        match environment.Methods |> List.filter (fun m -> m.Name = "CallEntryPoint") with
        | [ callEntryPoint ] ->
            if hasExpectedSignature exceptionType.Identity callEntryPoint.Signature then
                callEntryPoint
            else
                failwith
                    $"CoreLib's System.Environment::CallEntryPoint has the signature %O{callEntryPoint.Signature}, which is not the `%s{describeExpected}` PawPrint knows how to call; PawPrint calls it to %s{purpose}."
        | [] ->
            failwith $"Could not find System.Environment::CallEntryPoint in CoreLib; PawPrint calls it to %s{purpose}."
        | _ :: _ :: _ ->
            failwith
                $"Found several System.Environment::CallEntryPoint methods in CoreLib; expected exactly one. PawPrint calls it to %s{purpose}."

    /// The entry thread's bottom frame calling CoreLib's `CallEntryPoint` as `RunMainInternal`
    /// does, to call `main` with `mainArgs` (its `string[]`, or nothing for a `Main` that takes
    /// none), where `returns` is what `main` returns; the handle of `CallEntryPoint`'s declaring
    /// type; and how the call reports `main`'s return.
    let buildFrame
        (loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (main : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        (mainArgs : ImmutableArray<CliType>)
        (returns : MainReturn)
        (state : IlMachineState)
        : IlMachineState * MethodState * ConcreteTypeHandle * MainCall
        =
        let callEntryPoint = locate baseClassTypes.Corelib

        let pointerArg (pointer : ManagedPointerSource) : CliType =
            CliType.RuntimePointer (CliRuntimePointer.Managed pointer)

        let nullPointerArg : CliType =
            CliType.RuntimePointer (CliRuntimePointer.Verbatim 0L)

        let entryPointArg : CliType =
            let zero =
                CliType.zeroOfPrimitive state.TypeSystem.ConcreteTypes baseClassTypes PrimitiveType.IntPtr

            EvalStackValue.toCliTypeCoerced
                zero
                (EvalStackValue.NativeInt (NativeIntSource.FunctionPointer (FunctionPointerTarget.Managed main)))

        // `pArgument` is the address of the VM's local holding the arguments when `Main` takes
        // them, and null when it does not.
        let argumentArg, state =
            match List.ofSeq mainArgs with
            | [] -> nullPointerArg, state
            | [ CliType.ObjectRef (Some arguments) ] ->
                let pointer, state = HostStartupCall.allocateReferenceLocal arguments state
                pointerArg pointer, state
            | other ->
                failwith $"logic error: Main is given either nothing or its string[], but startup prepared %O{other}"

        // `pReturnValue` is the address of `RunMain`'s `*piRetVal`, which it sets to -1 before the
        // call, when `Main` returns an `int`, and null when it returns nothing.
        let returnValue, returnValueArg, state =
            match returns with
            | MainReturn.Void -> None, nullPointerArg, state
            | MainReturn.Int32 ->
                let cell, pool =
                    NativeInt32Cell.allocate -1 (IlMachineState.getNativeMemoryPool state)

                Some cell, pointerArg cell.Pointer, IlMachineState.setNativeMemoryPool pool state

        let exceptionSlot, state = HostStartupCall.allocateExceptionSlot state

        let args =
            ImmutableArray.Create (
                entryPointArg,
                argumentArg,
                returnValueArg,
                // `captureException`: false, as `CorHost2::ExecuteAssembly` passes it unless
                // configured to swallow unhandled exceptions, so what `Main` raises escapes.
                CliType.Bool 0uy,
                pointerArg exceptionSlot.Pointer
            )

        let state, frame, declaringType =
            HostStartupCall.buildFrame loggerFactory baseClassTypes callEntryPoint args purpose state

        state, frame, declaringType, MainCall.ThroughCallEntryPoint (returnValue, exceptionSlot)
