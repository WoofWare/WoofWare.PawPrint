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
    let isExternallyVisible
        (assemblies : LoadedAssemblies)
        (concreteTypes : AllConcreteTypes)
        (handle : ConcreteTypeHandle)
        : bool
        =
        failwith "TODO 5u: not yet implemented"
