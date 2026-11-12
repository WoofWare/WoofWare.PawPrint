namespace WoofWare.PawPrint

/// Which method a stack-shape analysis describes. A body read from a PE image is analysed per
/// instantiation and per `MethodDesc` of its row, as CoreCLR compiles it: an argument of type `T`
/// is a float32 in `M<float>` and something else in `M<int>`, and an async variant's signature is
/// not its ordinary variant's, and the analysis must see the difference. A minted dynamic method is
/// its own.
[<RequireQualifiedAccess>]
type StackShapeKey =
    | Metadata of
        assemblyFullName : string *
        methodRow : int *
        variant : MethodVariant *
        typeGenerics : ConcreteTypeHandle list *
        methodGenerics : ConcreteTypeHandle list
    | Dynamic of DynamicMethodHandle
