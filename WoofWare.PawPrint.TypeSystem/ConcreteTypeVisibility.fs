namespace WoofWare.PawPrint

/// Whether a closed type is visible outside its assembly: CoreCLR's `TypeHandle::IsExternallyVisible`
/// (vm/typehandle.cpp:991) over a `ConcreteTypeHandle`, which `Type.IsVisible` of a `RuntimeType` asks.
[<RequireQualifiedAccess>]
module ConcreteTypeVisibility =

    /// <summary>
    /// A byref, pointer or array asks its element; a function pointer asks every parameter type and its
    /// return type (a <c>void</c> return is visible, <c>System.Void</c> being public); a nominal type asks
    /// <c>TypeVisibility.isTypeDefExternallyVisible</c> of its definition and then each generic argument.
    /// </summary>
    /// <remarks>
    /// Fails on a nominal handle with no row in <paramref name="concreteTypes"/>, which is a PawPrint
    /// fault, and on a runtime-async continuation, which no <c>RuntimeType</c> names.
    /// </remarks>
    let rec isExternallyVisible
        (assemblies : LoadedAssemblies)
        (concreteTypes : AllConcreteTypes)
        (handle : ConcreteTypeHandle)
        : bool
        =
        let recurse = isExternallyVisible assemblies concreteTypes

        match handle with
        | ConcreteTypeHandle.Byref element
        | ConcreteTypeHandle.Pointer element
        | ConcreteTypeHandle.OneDimArrayZero element
        | ConcreteTypeHandle.Array (element, _) -> recurse element
        | ConcreteTypeHandle.FunctionPointer signature ->
            // `FnPtrTypeDesc::IsExternallyVisible` (typedesc.cpp:1775) walks the return type and every
            // parameter type.
            List.forall recurse signature.ParameterTypes
            && (
                match signature.ReturnType with
                | MethodReturnType.Void -> true
                | MethodReturnType.Returns returnType -> recurse returnType
            )
        | ConcreteTypeHandle.Concrete _ ->
            match AllConcreteTypes.tryTypeInfo assemblies concreteTypes handle with
            | None ->
                failwith
                    $"BUG: ConcreteTypeVisibility.isExternallyVisible: the nominal handle %O{handle} has no row among the concrete types"
            | Some (concreteType, typeInfo) ->
                // `MethodTable::IsExternallyVisible` (methodtable.cpp:1500): the definition, then each
                // argument. A closed handle is never a generic type definition, whose arguments CoreCLR
                // skips; PawPrint spells a definition `RuntimeTypeHandleTarget.OpenGenericTypeDefinition`.
                TypeVisibility.isTypeDefExternallyVisible
                    (assemblies.ByDefinitionName concreteType.AssemblyFullName)
                    typeInfo
                && Seq.forall recurse concreteType.Generics
        | ConcreteTypeHandle.Continuation layout ->
            // CoreCLR's source gives false: the continuation's class comes from
            // `EEClass::CreateMinimalClass` with no attributes set (asynccontinuations.cpp:54), so it is
            // `tdNotPublic`. Unmeasured, because no `RuntimeType` names one.
            ContinuationType.unasked "ConcreteTypeVisibility.isExternallyVisible" layout
