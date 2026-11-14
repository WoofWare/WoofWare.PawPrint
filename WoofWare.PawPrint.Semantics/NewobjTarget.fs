namespace WoofWare.PawPrint

open System.Reflection

/// What CoreCLR's JIT makes of a `newobj` whose token binds a method: whether it compiles the
/// method holding the instruction, and what it raises if it refuses to.
///
/// The JIT refuses as it compiles, so a refusal arrives where the method holding the `newobj` is
/// called, before that method has run anything, and none of its own handlers can catch it. Nothing
/// here depends on how the token was spelled: a token of an image and an entry of a
/// `DynamicMethod`'s scope bind their method first, and the JIT asks the same of what they bind.
[<RequireQualifiedAccess>]
type NewobjTarget =
    /// The method is abstract: `BadImageFormatException`, "Bad IL format." (`BFA_BAD_IL`), from
    /// `CEEInfo::getCallInfo`, as for a `call` of it.
    | AbstractMethod
    /// The method is static, and not abstract: `InvalidProgramException`, with its default
    /// message (`BADCODE("newobj on static or abstract method")`). A class constructor and a
    /// `DynamicMethod` are both static.
    | StaticMethod
    /// The method is an instance method but not an instance constructor. Real .NET has no single
    /// behaviour here: it constructs and calls some such methods and crashes the process compiling
    /// others, so nothing raised can stand for it.
    | NotInstanceConstructor
    /// The method is an instance constructor of an abstract class: `InvalidOperationException`,
    /// "Instances of abstract classes cannot be created."
    /// (`InvalidOperation_CantInstantiateAbstractClass`, from `CEEInfo::getNewHelper`).
    | AbstractClass
    /// The method is an instance constructor the JIT compiles a `newobj` of.
    | Constructs

[<RequireQualifiedAccess>]
module NewobjTarget =

    /// Whether a method with these attributes and this name is an instance constructor, as CoreCLR's
    /// `IsMdInstanceInitializer` says (`inc/corhdr.h`): `RTSpecialName` set and the name `.ctor`.
    /// A method named `.ctor` without `RTSpecialName` is not one.
    let isInstanceConstructor (attributes : MethodAttributes) (name : string) : bool =
        attributes.HasFlag MethodAttributes.RTSpecialName && name = ".ctor"

    /// Whether the JIT refuses to allocate, for a `newobj` of one of its instance methods, a type
    /// with attributes `declaringType`, which is a value type if `isValueType`: whether it is an
    /// abstract class, which `CEEInfo::getNewHelper` refuses with `InvalidOperationException`.
    /// The JIT allocates a value type without asking `getNewHelper`, so a value type flagged
    /// abstract is allocated (measured on .NET 10 and 11).
    ///
    /// This holds whatever the instance method is, constructor or not: measured on .NET 10 and 11,
    /// a `newobj` of a `void` instance method named `.ctor` without `RTSpecialName` refuses an
    /// abstract class and allocates anything else, as for a constructor.
    let refusesAsAbstractClass (declaringType : TypeAttributes) (isValueType : bool) : bool =
        declaringType.HasFlag TypeAttributes.Abstract && not isValueType

    /// What the JIT makes of a `newobj` naming a method with attributes `method` and name
    /// `methodName`, declared on a type with attributes `declaringType`, which is a value type if
    /// `declaringTypeIsValueType`. The outcomes are tried in the order `NewobjTarget` lists them,
    /// and the first that holds is the answer.
    ///
    /// The declaring type is the type the token names wherever the answer depends on it: that is
    /// only for an instance constructor, which a token never finds through a derived type. Whether
    /// an instance constructor's type is refused is `refusesAsAbstractClass`.
    ///
    /// An array type's constructor, which the runtime synthesises, is not a method this answers
    /// for: the JIT allocates the array before it reaches this question.
    let classify
        (method : MethodAttributes)
        (methodName : string)
        (declaringType : TypeAttributes)
        (declaringTypeIsValueType : bool)
        : NewobjTarget
        =
        if method.HasFlag MethodAttributes.Abstract then
            NewobjTarget.AbstractMethod
        elif method.HasFlag MethodAttributes.Static then
            NewobjTarget.StaticMethod
        elif not (isInstanceConstructor method methodName) then
            NewobjTarget.NotInstanceConstructor
        elif refusesAsAbstractClass declaringType declaringTypeIsValueType then
            NewobjTarget.AbstractClass
        else
            NewobjTarget.Constructs
