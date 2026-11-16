namespace WoofWare.PawPrint

open NativeRuntimeTypeHelpers

/// <summary>
/// QCalls declared on <c>System.Reflection.RuntimeModule</c>, which ask about the module's
/// own contents rather than resolving a token the caller supplies. The
/// <c>ModuleHandle_*</c> entry points — including the token resolvers, which live in
/// <c>NativeRuntimeTypeQCall</c> next to the type-resolution machinery they share — are
/// elsewhere.
/// </summary>
[<RequireQualifiedAccess>]
module NativeRuntimeModule =
    /// <summary>
    /// <c>RuntimeModule_GetTypes</c> (coreclr/vm/commodule.cpp), the QCall behind
    /// <c>Module.GetTypes()</c> and <c>Assembly.DefinedTypes</c>, once its caller has checked the
    /// call's shape: the first native argument is the <c>QCallModule</c> and the second the
    /// <c>ObjectHandleOnStack</c> through which the types are returned.
    /// </summary>
    let private getTypes (ctx : NativeCallContext) : NativeHandlerResult =
        let state = ctx.State
        let instruction = ctx.Instruction
        let operation = "RuntimeModule_GetTypes"

        // CoreCLR enumerates the module's TypeDef table, loads each row with
        // `ClassLoader::LoadTypeDefOrRefThrowing` under `PermitUninstDefOrRef`, and writes back an
        // array of each loaded type's `GetManagedClassObject()`.
        //
        // CoreCLR wraps each row's load in an `EX_TRY` and collects the throwables. .NET 10's
        // native then throws a `ReflectionTypeLoadException` carrying the partially-populated
        // array; .NET 11's hands the throwables back through a third argument, and
        // `RuntimeModule.GetDefinedTypes` throws that exception itself. Neither is reproduced: a
        // row PawPrint cannot concretize is a gap in PawPrint, not a malformed guest assembly, and
        // reporting it as the latter would let the guest swallow it in a `catch` and carry on with
        // a silently short array. So such a row aborts the run instead, with whatever diagnostic
        // concretization gives for it.
        let assemblyFullName =
            NativeCall.qCallModuleToAssemblyFullName
                operation
                state
                (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

        let retTypes =
            NativeCall.objectHandleOnStackTarget operation state "retTypes" instruction.Arguments.[1]

        let assembly =
            state.LoadedAssembly assemblyFullName
            |> Option.defaultWith (fun () ->
                failwith $"%s{operation}: module's assembly %s{assemblyFullName} is not loaded"
            )

        // `AllocateObjectArray(dwNumTypeDefs, CoreLibBinder::GetClass(CLASS__CLASS))`, and
        // `CLASS__CLASS` is `System.RuntimeType` (vm/corelib.h:151) — matching the
        // `RuntimeType[]` the managed caller declares.
        //
        // Written unconditionally, including for an empty array. Unlike
        // `RuntimeTypeHandle_GetInterfaces`, whose managed wrapper pre-initialises its local
        // to `[]`, `RuntimeModule.GetDefinedTypes` starts from `null` and returns `types!` on both
        // runtimes, so a skipped write is a NullReferenceException in the
        // guest.
        let state, typeAddresses =
            loadTypeDefRuntimeTypes
                ctx.LoggerFactory
                ctx.BaseClassTypes
                assembly
                (ExportedTypeSelection.enumeratedTypeDefs assembly)
                state

        let state =
            writeTypeArray
                ctx.LoggerFactory
                ctx.BaseClassTypes
                TypeArrayElement.RuntimeType
                typeAddresses
                retTypes
                state

        NativeHandlerResult.completed state

    let tryExecuteQCall (entryPoint : string) (ctx : NativeCallContext) : NativeHandlerResult option =
        let state = ctx.State
        let instruction = ctx.Instruction

        match
            entryPoint,
            ctx.TargetAssembly.Name.Name,
            ctx.TargetType.Namespace,
            ctx.TargetType.Name,
            instruction.ExecutingMethod.Signature.ParameterTypes,
            instruction.ExecutingMethod.Signature.ReturnType
        with
        | "RuntimeModule_GetTypes",
          "System.Private.CoreLib",
          "System.Reflection",
          "RuntimeModule",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "QCallModule",
                                                        qCallModuleGenerics)
            CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "ObjectHandleOnStack",
                                                        objectHandleGenerics) ],
          MethodReturnType.Void when qCallModuleGenerics.IsEmpty && objectHandleGenerics.IsEmpty ->
            // .NET 10's declaration: `GetTypes(QCallModule module, ObjectHandleOnStack retTypes)`.
            if instruction.Arguments.Length <> 2 then
                failwith $"RuntimeModule_GetTypes: expected two native arguments, got %d{instruction.Arguments.Length}"

            getTypes ctx |> Some
        | "RuntimeModule_GetTypes",
          "System.Private.CoreLib",
          "System.Reflection",
          "RuntimeModule",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "QCallModule",
                                                        qCallModuleGenerics)
            CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "ObjectHandleOnStack",
                                                        retTypesGenerics)
            CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "ObjectHandleOnStack",
                                                        retExceptionsGenerics) ],
          MethodReturnType.Void when
            qCallModuleGenerics.IsEmpty
            && retTypesGenerics.IsEmpty
            && retExceptionsGenerics.IsEmpty
            ->
            // .NET 11's declaration: `GetTypes(QCallModule module, ObjectHandleOnStack retTypes,
            // ObjectHandleOnStack retExceptions)`. CoreCLR sets `retExceptions` only when some row
            // failed to load, and `getTypes` aborts the run on any such row, so it is never
            // written: the caller's `exceptions` local stays null, as it does on real .NET when
            // every row loads.
            if instruction.Arguments.Length <> 3 then
                failwith
                    $"RuntimeModule_GetTypes: expected three native arguments, got %d{instruction.Arguments.Length}"

            NativeCall.objectHandleOnStackTarget
                "RuntimeModule_GetTypes"
                state
                "retExceptions"
                instruction.Arguments.[2]
            |> ignore

            getTypes ctx |> Some
        | "RuntimeModule_GetScopeName",
          "System.Private.CoreLib",
          "System.Reflection",
          "RuntimeModule",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "QCallModule",
                                                        qCallModuleGenerics)
            CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "StringHandleOnStack",
                                                        stringHandleGenerics) ],
          MethodReturnType.Void when qCallModuleGenerics.IsEmpty && stringHandleGenerics.IsEmpty ->
            // `RuntimeModule_GetScopeName` (coreclr/vm/commodule.cpp:604), behind
            // `Module.ScopeName` and so `Module.ToString()`. CoreCLR answers
            // `GetMDImport()->GetScopeProps(&szName, 0)`, which is the `Name` column of the
            // module's own `Module` metadata row.
            let operation = "RuntimeModule_GetScopeName"

            if instruction.Arguments.Length <> 2 then
                failwith $"%s{operation}: expected two native arguments, got %d{instruction.Arguments.Length}"

            let assemblyFullName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            let retString =
                NativeCall.stringHandleOnStackTarget operation state "retString" instruction.Arguments.[1]

            let assembly =
                state.LoadedAssembly assemblyFullName
                |> Option.defaultWith (fun () ->
                    failwith $"%s{operation}: module's assembly %s{assemblyFullName} is not loaded"
                )

            // CoreCLR's leading `IsValidToken(GetModuleFromScope())` guard, which reports
            // `COR_E_BADIMAGEFORMAT` for an image whose `Module` table is empty, is absent here:
            // that table is mandatory and has exactly one row, and an image lacking it would have
            // failed when its metadata was first read rather than reaching a QCall.
            // Reported verbatim, including an empty string. CoreCLR performs no emptiness check
            // here -- it hands `GetScopeProps`'s name straight to `retString.Set` -- so an image
            // whose Module row names the empty string is one whose `ScopeName` really is "".
            // Unlike `AssemblyNative_GetSimpleName`, which does refuse: that reads the *Assembly*
            // row, where CoreCLR's own `_ASSERTE` treats an empty name as a corrupted image.
            let scopeName = assembly.ScopeName

            let nameAddr, state =
                if System.String.IsNullOrEmpty scopeName then
                    // `StringObject::NewString` returns the shared empty-string instance for a
                    // zero-length string (object.cpp:651), so an empty scope name is
                    // reference-equal to `string.Empty` and to itself across reads.
                    IlMachineState.internCanonicalEmptyString ctx.LoggerFactory ctx.BaseClassTypes state
                else
                    // Allocated afresh per call otherwise: CoreCLR does not intern a QCall's
                    // string result, so two reads of a non-empty `ScopeName` are
                    // reference-distinct.
                    IlMachineState.allocateManagedString ctx.LoggerFactory ctx.BaseClassTypes scopeName state

            let state =
                IlMachineState.writeManagedByrefWithBase
                    ctx.BaseClassTypes
                    state
                    (ManagedPointerSource.requireAddressed retString)
                    (CliType.ObjectRef (Some nameAddr))

            NativeHandlerResult.completed state |> Some
        | "RuntimeModule_GetFullyQualifiedName",
          "System.Private.CoreLib",
          "System.Reflection",
          "RuntimeModule",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "QCallModule",
                                                        qCallModuleGenerics)
            CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "StringHandleOnStack",
                                                        stringHandleGenerics) ],
          MethodReturnType.Void when qCallModuleGenerics.IsEmpty && stringHandleGenerics.IsEmpty ->
            // `RuntimeModule_GetFullyQualifiedName` (coreclr/vm/commodule.cpp:628), behind
            // `Module.FullyQualifiedName` and `Module.Name`. CoreCLR answers
            // `<In Memory Module>` for a Reflection.Emit module, and otherwise the module's
            // path, or `<Unknown>` when that path is empty.
            let operation = "RuntimeModule_GetFullyQualifiedName"

            if instruction.Arguments.Length <> 2 then
                failwith $"%s{operation}: expected two native arguments, got %d{instruction.Arguments.Length}"

            let assemblyFullName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            if (state.LoadedAssembly assemblyFullName).IsNone then
                failwith $"%s{operation}: module's assembly %s{assemblyFullName} is not loaded"

            let retString =
                NativeCall.stringHandleOnStackTarget operation state "retString" instruction.Arguments.[1]

            // Every image PawPrint loads has an empty path, as `AssemblyNative_GetLocation`
            // reports: there is no file the guest could open. CoreCLR's `Module::GetPath` is
            // `PEAssembly::GetPath`, the same string `GetLocation` returns, so a non-emitted
            // module takes CoreCLR's empty-path branch.
            let fullyQualifiedName =
                if state.TypeSystem._LoadedAssemblies.IsDynamic assemblyFullName then
                    "<In Memory Module>"
                else
                    "<Unknown>"

            // Allocated afresh per call: `retString.Set` builds a new string each time, and
            // CoreCLR does not intern it.
            let nameAddr, state =
                IlMachineState.allocateManagedString ctx.LoggerFactory ctx.BaseClassTypes fullyQualifiedName state

            let state =
                IlMachineState.writeManagedByrefWithBase
                    ctx.BaseClassTypes
                    state
                    (ManagedPointerSource.requireAddressed retString)
                    (CliType.ObjectRef (Some nameAddr))

            NativeHandlerResult.completed state |> Some
        | _ -> None
