namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// `IlMachineState.verifyInterfaceIsImplemented` is CoreCLR's `RuntimeTypeHandle_VerifyInterfaceIsImplemented`
/// without the raise: which of its five arms a pair of handles takes, or that it passes. The host is the
/// oracle: its verdict for a pair is the QCall itself, reached through its managed wrapper
/// `RuntimeTypeHandle.VerifyInterfaceIsImplemented(RuntimeTypeHandle)` by reflection, and named by the
/// message of the `ArgumentException` it raises. Each pair is built twice from one host `Type`, as the
/// host's handle and as the target PawPrint reflects for the same type of the same image.
///
/// Two of the six verdicts raise one message, so no comparison with the host tells them apart; an
/// example test pins them. The pairs whose receiver is an open value type and whose target is a variant
/// interface definition are checked by a weaker property: PawPrint may refuse them, as
/// `isRuntimeTypeHandleTargetAssignableTo` does when the answer depends on CoreCLR's
/// `MayHaveOpenInterfacesInInterfaceMap` flag (#2295).
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestInterfaceVerification =

    // The factory is intentionally undisposed: the DumpedAssembly loggers close over its sinks and
    // outlive this scope.
    let private loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory =
        let _, loggerFactory = LoggerFactory.makeTest ()
        loggerFactory

    let private corelib : DumpedAssembly =
        Assembly.readFile loggerFactory HostImage.coreLibPath

    let private hostCoreLib : Reflection.Assembly = typeof<obj>.Assembly

    let private bct : BaseClassTypes<DumpedAssembly> = BaseClassTypes.ofCorelib corelib

    /// CoreLib alone, concretised once. Each corpus is added to it.
    let private coreState : IlMachineState =
        let initial = IlMachineState.initial loggerFactory ImmutableArray.Empty corelib

        { initial with
            TypeSystem =
                { initial.TypeSystem with
                    ConcreteTypes =
                        Corelib.concretizeAll initial.TypeSystem._LoadedAssemblies bct AllConcreteTypes.Empty
                }
        }

    // ----- The host's verdict -----

    let private invalidHandle : string = "Invalid handle."
    let private notFound : string = "Interface not found."
    let private thisIsInterface : string = "'this' type cannot be an interface itself."
    let private mustBeInterface : string = "Type passed must be an interface."

    let private wrapper : MethodInfo =
        match
            typeof<RuntimeTypeHandle>
                .GetMethod (
                    "VerifyInterfaceIsImplemented",
                    BindingFlags.NonPublic ||| BindingFlags.Instance,
                    null,
                    [| typeof<RuntimeTypeHandle> |],
                    null
                )
        with
        | null -> failwith "the host's CoreLib has no RuntimeTypeHandle.VerifyInterfaceIsImplemented(RuntimeTypeHandle)"
        | m -> m

    /// The message the host's QCall raises for the pair, or `None` if it passes. Every raise must be
    /// exactly an `ArgumentException` with `HResult` 0x80070057 and a null `ParamName`.
    let private hostVerdict (receiver : Type) (interfaceType : Type) : string option =
        try
            wrapper.Invoke (box receiver.TypeHandle, [| box interfaceType.TypeHandle |])
            |> ignore

            None
        with :? TargetInvocationException as e ->
            match e.InnerException with
            | :? ArgumentException as inner when inner.GetType () = typeof<ArgumentException> ->
                if inner.HResult <> 0x80070057 then
                    failwith $"%O{receiver} / %O{interfaceType}: the host raised HResult 0x%08X{inner.HResult}"

                if not (isNull inner.ParamName) then
                    failwith $"%O{receiver} / %O{interfaceType}: the host raised with ParamName %s{inner.ParamName}"

                Some inner.Message
            | inner -> failwith $"%O{receiver} / %O{interfaceType}: the host raised %O{inner}"

    /// The message the handler raises for `verdict`, or `None` if it completes.
    let private messageOf (verdict : IlMachineRuntimeMetadata.InterfaceVerification) : string option =
        match verdict with
        | IlMachineRuntimeMetadata.InterfaceVerification.Implemented -> None
        | IlMachineRuntimeMetadata.InterfaceVerification.ReceiverIsTypeVariable -> Some invalidHandle
        | IlMachineRuntimeMetadata.InterfaceVerification.ReceiverIsTypeDesc -> Some notFound
        | IlMachineRuntimeMetadata.InterfaceVerification.ReceiverIsInterface -> Some thisIsInterface
        | IlMachineRuntimeMetadata.InterfaceVerification.TargetIsNotInterface -> Some mustBeInterface
        | IlMachineRuntimeMetadata.InterfaceVerification.NotImplemented -> Some notFound

    // ----- Building PawPrint's target for a host type -----

    /// The host assemblies a pair's types may come from, each with PawPrint's reading of the same image.
    type private Images = (Reflection.Assembly * DumpedAssembly) list

    let private typeInfoOf (images : Images) (t : Type) : TypeInfo<GenericParamFromMetadata, TypeDefn> =
        let assembly =
            images
            |> List.tryFind (fun (host, _) -> Object.ReferenceEquals (host, t.Assembly))
            |> Option.map snd
            |> Option.defaultWith (fun () -> failwith $"%O{t} is in none of the images under test")

        assembly.TypeDefs.[MetadataTokens.TypeDefinitionHandle (t.MetadataToken &&& 0x00FFFFFF)]

    let private primitives : (Type * PrimitiveType) list =
        [
            typeof<bool>, PrimitiveType.Boolean
            typeof<char>, PrimitiveType.Char
            typeof<sbyte>, PrimitiveType.SByte
            typeof<byte>, PrimitiveType.Byte
            typeof<int16>, PrimitiveType.Int16
            typeof<uint16>, PrimitiveType.UInt16
            typeof<int>, PrimitiveType.Int32
            typeof<uint32>, PrimitiveType.UInt32
            typeof<int64>, PrimitiveType.Int64
            typeof<uint64>, PrimitiveType.UInt64
            typeof<single>, PrimitiveType.Single
            typeof<double>, PrimitiveType.Double
            typeof<string>, PrimitiveType.String
            hostCoreLib.GetType "System.TypedReference", PrimitiveType.TypedReference
            typeof<nativeint>, PrimitiveType.IntPtr
            typeof<unativeint>, PrimitiveType.UIntPtr
            typeof<obj>, PrimitiveType.Object
        ]

    /// A part of PawPrint's side: closed, as the `TypeDefn` to concretise, or open, as its target.
    type private Built =
        | ClosedDefn of TypeDefn
        | OpenTarget of RuntimeTypeHandleTarget

    let private definitionDefn
        (state : IlMachineState)
        (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>)
        : TypeDefn
        =
        TypeDefn.FromDefinition (
            typeInfo.Identity,
            LoadedTypeInfo.signatureTypeKind bct state.TypeSystem._LoadedAssemblies typeInfo
        )

    let private targetOfBuilt (state : IlMachineState) (built : Built) : IlMachineState * RuntimeTypeHandleTarget =
        match built with
        | Built.OpenTarget target -> state, target
        | Built.ClosedDefn defn ->
            let state, handle =
                IlMachineState.concretizeType
                    loggerFactory
                    bct
                    state
                    corelib.DefinitionFullName
                    ImmutableArray.Empty
                    ImmutableArray.Empty
                    defn

            state, RuntimeTypeHandleTarget.Closed handle

    let private applyShape (shape : CompositeShape) (built : Built) : Built =
        match built with
        | Built.ClosedDefn defn ->
            match shape with
            | CompositeShape.Byref -> TypeDefn.Byref defn
            | CompositeShape.Pointer -> TypeDefn.Pointer defn
            | CompositeShape.OneDimArrayZero -> TypeDefn.OneDimensionalArrayLowerBoundZero defn
            | CompositeShape.Array rank -> TypeDefn.Array (defn, rank)
            |> Built.ClosedDefn
        | Built.OpenTarget target -> Built.OpenTarget (RuntimeTypeHandleTarget.composite shape target)

    let rec private build (images : Images) (state : IlMachineState) (t : Type) : IlMachineState * Built =
        if t.IsGenericParameter then
            match t.DeclaringMethod with
            | null ->
                state,
                Built.OpenTarget (
                    RuntimeTypeHandleTarget.GenericParameter (
                        (typeInfoOf images t.DeclaringType).Identity,
                        t.GenericParameterPosition
                    )
                )
            | method ->
                let handle =
                    ComparableMethodDefinitionHandle.Make (
                        MetadataTokens.MethodDefinitionHandle (method.MetadataToken &&& 0x00FFFFFF)
                    )

                state,
                Built.OpenTarget (
                    RuntimeTypeHandleTarget.MethodGenericParameter (
                        (typeInfoOf images method.DeclaringType).Identity,
                        handle,
                        t.GenericParameterPosition
                    )
                )
        elif t.IsByRef then
            let state, element = build images state (t.GetElementType ())
            state, applyShape CompositeShape.Byref element
        elif t.IsPointer then
            let state, element = build images state (t.GetElementType ())
            state, applyShape CompositeShape.Pointer element
        elif t.IsSZArray then
            let state, element = build images state (t.GetElementType ())
            state, applyShape CompositeShape.OneDimArrayZero element
        elif t.IsArray then
            let state, element = build images state (t.GetElementType ())
            state, applyShape (CompositeShape.Array (t.GetArrayRank ())) element
        elif t.IsFunctionPointer then
            failwith $"no PawPrint target is built here for the function pointer %O{t}"
        elif t.IsGenericTypeDefinition then
            state, Built.OpenTarget (RuntimeTypeHandleTarget.OpenGenericTypeDefinition (typeInfoOf images t).Identity)
        elif t.IsConstructedGenericType then
            let definition = typeInfoOf images (t.GetGenericTypeDefinition ())

            let state, arguments =
                ((state, []), t.GetGenericArguments ())
                ||> Array.fold (fun (state, acc) argument ->
                    let state, built = build images state argument
                    state, built :: acc
                )

            let arguments = List.rev arguments

            let closed =
                arguments
                |> List.choose (fun built ->
                    match built with
                    | Built.ClosedDefn defn -> Some defn
                    | Built.OpenTarget _ -> None
                )

            if closed.Length = arguments.Length then
                state,
                Built.ClosedDefn (
                    TypeDefn.GenericInstantiation (definitionDefn state definition, ImmutableArray.CreateRange closed)
                )
            else
                let state, targets =
                    ((state, []), arguments)
                    ||> List.fold (fun (state, acc) built ->
                        let state, target = targetOfBuilt state built
                        state, target :: acc
                    )

                state, Built.OpenTarget (RuntimeTypeHandleTarget.openConstructed definition.Identity (List.rev targets))
        else
            match primitives |> List.tryFind (fun (p, _) -> p = t) with
            | Some (_, primitive) -> state, Built.ClosedDefn (TypeDefn.PrimitiveType primitive)
            | None -> state, Built.ClosedDefn (definitionDefn state (typeInfoOf images t))

    /// The target a guest's `typeof` of `t` gives: the definition for a generic type definition, the
    /// variable for a generic parameter, and otherwise the type built from its parts.
    let private target
        (images : Images)
        (state : IlMachineState)
        (t : Type)
        : IlMachineState * RuntimeTypeHandleTarget
        =
        let state, built = build images state t
        targetOfBuilt state built

    let private verdict
        (images : Images)
        (state : IlMachineState)
        (receiver : Type)
        (interfaceType : Type)
        : IlMachineState * IlMachineRuntimeMetadata.InterfaceVerification
        =
        let state, receiverTarget = target images state receiver
        let state, interfaceTarget = target images state interfaceType
        IlMachineState.verifyInterfaceIsImplemented loggerFactory bct state receiverTarget interfaceTarget

    // ----- The shape of decision 9 -----

    /// A receiver which is an open value type, and a target which is a variant interface definition: the
    /// pairs whose answer may depend on CoreCLR's `MayHaveOpenInterfacesInInterfaceMap` flag, which
    /// PawPrint refuses rather than guess for an open value type.
    let private isOpenValueTypeOnVariantDefinition (receiver : Type) (interfaceType : Type) : bool =
        receiver.IsValueType
        && receiver.ContainsGenericParameters
        && not receiver.IsGenericParameter
        && not receiver.HasElementType
        && interfaceType.IsInterface
        && interfaceType.IsGenericTypeDefinition
        && interfaceType.GetGenericArguments ()
           |> Array.exists (fun p ->
               p.GenericParameterAttributes &&& GenericParameterAttributes.VarianceMask
               <> GenericParameterAttributes.None
           )

    let private isMayHaveOpenInterfacesRefusal (e : exn) : bool =
        e.Message.Contains "MayHaveOpenInterfacesInInterfaceMap"

    /// What a pair reached. Every case but `OpenValueTypeRefused` is read from the host's side alone.
    type private Label =
        | Passed
        | RaisedInvalidHandle
        | RaisedNotFoundOnTypeDesc
        | RaisedThisIsInterface
        | RaisedMustBeInterface
        | RaisedNotFound
        /// A pass where the target is not among the receiver's interfaces: by variance alone.
        | PassedByVarianceAlone
        /// A pass where the target is a generic interface definition: the special marker accepted.
        | PassedOnDefinition
        /// A receiver which is an open value type, against a variant interface definition.
        | OpenValueTypeOnVariantDefinition
        /// A pair of that shape which PawPrint refused.
        | OpenValueTypeRefused

    let private hostLabels (receiver : Type) (interfaceType : Type) (host : string option) : Label list =
        let shape =
            if isOpenValueTypeOnVariantDefinition receiver interfaceType then
                [ Label.OpenValueTypeOnVariantDefinition ]
            else
                []

        shape
        @ match host with
          | None ->
              [
                  yield Label.Passed
                  if interfaceType.IsGenericTypeDefinition then
                      yield Label.PassedOnDefinition
                  elif not (receiver.GetInterfaces () |> Array.contains interfaceType) then
                      yield Label.PassedByVarianceAlone
              ]
          | Some message when message = invalidHandle -> [ Label.RaisedInvalidHandle ]
          | Some message when message = thisIsInterface -> [ Label.RaisedThisIsInterface ]
          | Some message when message = mustBeInterface -> [ Label.RaisedMustBeInterface ]
          | Some message when message = notFound ->
              if receiver.IsPointer || receiver.IsByRef || receiver.IsFunctionPointer then
                  [ Label.RaisedNotFoundOnTypeDesc ]
              else
                  [ Label.RaisedNotFound ]
          | Some message ->
              failwith $"%O{receiver} / %O{interfaceType}: the host raised the unexpected message %s{message}"

    /// Compare PawPrint's verdict for one pair with the host's. Returns the state to carry on with and a
    /// description of any disagreement.
    let private comparePair
        (cover : Label -> unit)
        (images : Images)
        (state : IlMachineState)
        (receiver : Type)
        (interfaceType : Type)
        : IlMachineState * string option
        =
        let host = hostVerdict receiver interfaceType

        for label in hostLabels receiver interfaceType host do
            cover label

        let disagreement (actual : IlMachineRuntimeMetadata.InterfaceVerification) : string option =
            if messageOf actual = host then
                None
            else
                Some $"%O{receiver} / %O{interfaceType}: host %A{host}, PawPrint %A{actual}"

        if isOpenValueTypeOnVariantDefinition receiver interfaceType then
            match
                (try
                    Ok (verdict images state receiver interfaceType)
                 with e when isMayHaveOpenInterfacesRefusal e ->
                     Error e)
            with
            | Ok (state, actual) -> state, disagreement actual
            | Error _ ->
                cover Label.OpenValueTypeRefused
                state, None
        else
            let state, actual = verdict images state receiver interfaceType
            state, disagreement actual

    // ----- Example tests over a fixed corpus -----

    let private fixedSource : string =
        """
public interface IMarker { }
public interface IOne { void M(); }
public class COne : IOne { public void M() { } }
public interface IInC<in T> where T : class { }
public struct SGenInC<T> : IInC<object> { }
public static class Holder { public static void M<U>() { } }
"""

    let private fixedImage : byte[] =
        Roslyn.compileAssembly "InterfaceVerificationFixed" OutputKind.DynamicallyLinkedLibrary [] [ fixedSource ]

    let private fixedHost : Reflection.Assembly = Reflection.Assembly.Load fixedImage

    let private fixedDumped : DumpedAssembly =
        use stream = new MemoryStream (fixedImage)
        AssemblyApi.read loggerFactory None stream

    let private fixedImages : Images = [ hostCoreLib, corelib ; fixedHost, fixedDumped ]

    let private fixedState : IlMachineState = coreState.WithLoadedAssembly fixedDumped

    let private fixedType (name : string) : Type =
        match fixedHost.GetType name with
        | null -> failwith $"fixed corpus type %s{name} not found"
        | t -> t

    let private listT : Type =
        typedefof<System.Collections.Generic.List<obj>>.GetGenericArguments().[0]

    [<Test>]
    let ``Each arm of the QCall has its own verdict`` () : unit =
        let cases : (Type * Type * IlMachineRuntimeMetadata.InterfaceVerification) list =
            [
                fixedType "COne", fixedType "IOne", IlMachineRuntimeMetadata.InterfaceVerification.Implemented
                listT, typeof<IComparable>, IlMachineRuntimeMetadata.InterfaceVerification.ReceiverIsTypeVariable
                typeof<int>.MakePointerType (),
                typeof<IDisposable>,
                IlMachineRuntimeMetadata.InterfaceVerification.ReceiverIsTypeDesc
                fixedType "IOne", fixedType "IOne", IlMachineRuntimeMetadata.InterfaceVerification.ReceiverIsInterface
                fixedType "COne", typeof<string>, IlMachineRuntimeMetadata.InterfaceVerification.TargetIsNotInterface
                fixedType "COne", fixedType "IMarker", IlMachineRuntimeMetadata.InterfaceVerification.NotImplemented
                // Arm 2 before arm 4.
                typeof<int>.MakePointerType (),
                typeof<string>,
                IlMachineRuntimeMetadata.InterfaceVerification.ReceiverIsTypeDesc
            ]

        for receiver, interfaceType, expected in cases do
            let _, actual = verdict fixedImages fixedState receiver interfaceType

            if actual <> expected then
                failwith $"%O{receiver} / %O{interfaceType}: expected %A{expected}, PawPrint %A{actual}"

            // The host agrees, as far as its message can say.
            hostVerdict receiver interfaceType |> shouldEqual (messageOf expected)

    [<Test>]
    let ``TypeHandleTag.isTypeDesc is true exactly for the TypeDesc shapes`` () : unit =
        let images = fixedImages
        let state = fixedState

        let hostTarget (t : Type) : RuntimeTypeHandleTarget = snd (target images state t)

        let header : ComparableSignatureHeader =
            ComparableSignatureHeader.Make (
                SignatureHeader (SignatureKind.Method, SignatureCallingConvention.Default, SignatureAttributes.None)
            )

        let functionPointer (parameters : RuntimeTypeHandleTarget list) : RuntimeTypeHandleTarget =
            RuntimeTypeHandleTarget.functionPointer
                {
                    Header = header
                    ParameterTypes = parameters
                    GenericParameterCount = 0
                    RequiredParameterCount = parameters.Length
                    ReturnType = MethodReturnType.Void
                }

        let methodU = (fixedType "Holder").GetMethod("M").GetGenericArguments().[0]

        let cases : (string * RuntimeTypeHandleTarget * bool) list =
            [
                "a closed class", hostTarget (fixedType "COne"), false
                "int[]", hostTarget typeof<int[]>, false
                "int[,]", hostTarget (typeof<int>.MakeArrayType 2), false
                "int*", hostTarget (typeof<int>.MakePointerType ()), true
                "int&", hostTarget (typeof<int>.MakeByRefType ()), true
                "a closed function pointer", functionPointer [ hostTarget typeof<int> ], true
                "an open function pointer", functionPointer [ hostTarget listT ], true
                "a generic definition", hostTarget typedefof<System.Collections.Generic.List<obj>>, false
                "an open construction",
                hostTarget (typedefof<System.Collections.Generic.IEnumerable<obj>>.MakeGenericType listT),
                false
                "a type variable", hostTarget listT, true
                "a method's type variable", hostTarget methodU, true
                "T[]", hostTarget (listT.MakeArrayType ()), false
                "T[,]", hostTarget (listT.MakeArrayType 2), false
                "T*", hostTarget (listT.MakePointerType ()), true
                "T&", hostTarget (listT.MakeByRefType ()), true
                "the dynamic-methods class", RuntimeTypeHandleTarget.DynamicMethodsClass "Dyn", false
            ]

        for name, target, expected in cases do
            if TypeHandleTag.isTypeDesc target <> expected then
                failwith $"%s{name} (%O{target}): expected isTypeDesc %b{expected}"

    /// Decision 9 of the plan for #2270: `SGenInC<>`, a generic struct definition implementing the
    /// closed `IInC<object>`, against the variant definition `IInC<>`. The host raises "Interface not
    /// found.", and PawPrint refuses, because the answer depends on the open value type's
    /// `MayHaveOpenInterfacesInInterfaceMap` flag (#2295). Whoever answers it sees this test change.
    [<Test>]
    let ``An open struct against a variant interface definition is refused, where the host raises`` () : unit =
        let receiver = fixedType "SGenInC`1"
        let interfaceType = fixedType "IInC`1"

        hostVerdict receiver interfaceType |> shouldEqual (Some notFound)

        let e =
            Assert.Throws<Exception> (fun () -> verdict fixedImages fixedState receiver interfaceType |> ignore)

        Assert.That (isMayHaveOpenInterfacesRefusal e, Is.True, e.Message)

    // ----- The generated property -----

    type private Variance =
        | Invariant
        | Covariant
        | Contravariant

    /// A generic argument in a generated declaration.
    type private Atom =
        | AObject
        | AString
        | AInt
        /// The declaring entity's own type parameter at this position.
        | AParam of int
        /// The declaring class or struct itself, over its own parameter if it has one.
        | ASelf

    type private InterfaceUse =
        {
            Interface : int
            Arguments : Atom list
        }

    type private InterfaceParameter =
        {
            Variance : Variance
            ClassConstraint : bool
        }

    type private InterfaceDecl =
        {
            Parameters : InterfaceParameter list
            Bases : InterfaceUse list
        }

    type private TypeDecl =
        {
            IsStruct : bool
            /// `None` for a non-generic type; otherwise whether its one parameter is `where T : class`.
            Parameter : bool option
            /// A class's base, among the earlier classes, with its argument if that class is generic.
            Base : (int * Atom option) option
            Implements : InterfaceUse list
        }

    type private Corpus =
        {
            Interfaces : InterfaceDecl list
            Types : TypeDecl list
        }

    /// Whether `atom`, in the context of a declaration whose parameters' class constraints are
    /// `parameterIsClass` and which is a struct if `selfIsStruct`, is a reference type.
    let private isReference (parameterIsClass : int -> bool) (selfIsStruct : bool) (atom : Atom) : bool =
        match atom with
        | Atom.AObject
        | Atom.AString -> true
        | Atom.AInt -> false
        | Atom.AParam k -> parameterIsClass k
        | Atom.ASelf -> not selfIsStruct

    /// Replace each argument which breaks the class constraint of the parameter it instantiates.
    let private satisfyConstraints
        (interfaces : InterfaceDecl list)
        (parameterIsClass : int -> bool)
        (selfIsStruct : bool)
        (used : InterfaceUse)
        : InterfaceUse
        =
        let parameters = interfaces.[used.Interface].Parameters

        { used with
            Arguments =
                List.map2
                    (fun (p : InterfaceParameter) atom ->
                        if p.ClassConstraint && not (isReference parameterIsClass selfIsStruct atom) then
                            Atom.AObject
                        else
                            atom
                    )
                    parameters
                    used.Arguments
        }

    /// Replace each argument of a base interface which is not output-safe in an interface whose own
    /// parameters have `variances`: C# requires every base interface to be output-safe.
    let private makeOutputSafe
        (interfaces : InterfaceDecl list)
        (variances : Variance list)
        (used : InterfaceUse)
        : InterfaceUse
        =
        let parameters = interfaces.[used.Interface].Parameters

        { used with
            Arguments =
                List.map2
                    (fun (p : InterfaceParameter) atom ->
                        match atom with
                        | Atom.AParam k ->
                            let own = variances.[k]

                            let safe =
                                match p.Variance with
                                | Variance.Covariant -> own <> Variance.Contravariant
                                | Variance.Contravariant -> own <> Variance.Covariant
                                | Variance.Invariant -> own = Variance.Invariant

                            if safe then atom else Atom.AObject
                        | _ -> atom
                    )
                    parameters
                    used.Arguments
        }

    let private substitute (arguments : Atom list) (atom : Atom) : Atom =
        match atom with
        | Atom.AParam k -> arguments.[k]
        | other -> other

    /// `used` and every interface it inherits, with arguments in the user's context.
    let rec private closure (interfaces : InterfaceDecl list) (used : InterfaceUse) : InterfaceUse list =
        used
        :: (interfaces.[used.Interface].Bases
            |> List.collect (fun b ->
                closure
                    interfaces
                    { b with
                        Arguments = b.Arguments |> List.map (substitute used.Arguments)
                    }
            ))

    /// Whether two instantiations could be one type under some substitution of the declaring entity's
    /// parameters, as C#'s CS0695 asks. Conservative: any argument mentioning a parameter could unify.
    let private mayUnify (selfIsGeneric : bool) (a : InterfaceUse) (b : InterfaceUse) : bool =
        let mentionsParameter (atom : Atom) =
            match atom with
            | Atom.AParam _ -> true
            | Atom.ASelf -> selfIsGeneric
            | _ -> false

        a.Interface = b.Interface
        && a <> b
        && List.forall2 (fun x y -> x = y || mentionsParameter x || mentionsParameter y) a.Arguments b.Arguments

    /// Keep each declared interface whose closure neither repeats one already declared nor could unify
    /// with anything in `inherited` or the closures kept so far.
    let private dropConflicts
        (interfaces : InterfaceDecl list)
        (selfIsGeneric : bool)
        (inherited : InterfaceUse list)
        (declared : InterfaceUse list)
        : InterfaceUse list
        =
        let kept, _ =
            ((([], inherited)), declared)
            ||> List.fold (fun (kept, seen) used ->
                let mine = closure interfaces used

                if List.contains used kept then
                    kept, seen
                elif mine |> List.exists (fun m -> seen |> List.exists (mayUnify selfIsGeneric m)) then
                    kept, seen
                elif mine |> List.exists (fun m -> mine |> List.exists (mayUnify selfIsGeneric m)) then
                    kept, seen
                else
                    used :: kept, mine @ seen
            )

        List.rev kept

    /// Every interface a class inherits from its base chain, with arguments in its own context.
    let rec private inheritedFromBases (corpus : Corpus) (decl : TypeDecl) : InterfaceUse list =
        match decl.Base with
        | None -> []
        | Some (index, argument) ->
            let baseDecl = corpus.Types.[index]

            let arguments =
                match argument with
                | Some a -> [ a ]
                | None -> []

            let substituteSelf (used : InterfaceUse) : InterfaceUse =
                { used with
                    Arguments =
                        used.Arguments
                        |> List.map (fun atom ->
                            match atom with
                            // The base's own self is a type the derived class cannot be, and mentions
                            // the derived class's parameter exactly when its argument does.
                            | Atom.ASelf ->
                                match argument with
                                | Some (Atom.AParam _) -> Atom.AParam 0
                                | _ -> Atom.AObject
                            | other -> substitute arguments other
                        )
                }

            (baseDecl.Implements
             |> List.collect (closure corpus.Interfaces)
             |> List.map substituteSelf)
            @ (inheritedFromBases corpus baseDecl |> List.map substituteSelf)

    /// Make a generated corpus valid C#: every argument within its constraint, every base interface
    /// output-safe, and no two interfaces of one type that could unify.
    let private repair (corpus : Corpus) : Corpus =
        let interfaces =
            (([] : InterfaceDecl list), corpus.Interfaces)
            ||> List.fold (fun earlier decl ->
                let variances = decl.Parameters |> List.map (fun p -> p.Variance)
                let isClass k = decl.Parameters.[k].ClassConstraint

                let bases =
                    decl.Bases
                    |> List.map (makeOutputSafe earlier variances)
                    |> List.map (satisfyConstraints earlier isClass false)
                    |> dropConflicts earlier (not decl.Parameters.IsEmpty) []

                earlier
                @ [
                    { decl with
                        Bases = bases
                    }
                ]
            )

        let types =
            (([] : TypeDecl list), corpus.Types)
            ||> List.fold (fun earlier decl ->
                let isClass _ = decl.Parameter = Some true

                let baseClass =
                    match decl.Base with
                    | Some (index, argument) when not decl.IsStruct && not earlier.[index].IsStruct ->
                        let argument =
                            match earlier.[index].Parameter, argument with
                            | None, _ -> None
                            | Some _, None -> Some Atom.AObject
                            | Some baseIsClass, Some atom ->
                                match atom with
                                | Atom.AParam _ when decl.Parameter.IsNone -> Some Atom.AObject
                                | Atom.ASelf -> Some Atom.AObject
                                | atom when baseIsClass && not (isReference isClass false atom) -> Some Atom.AObject
                                | atom -> Some atom

                        Some (index, argument)
                    | _ -> None

                let decl =
                    { decl with
                        Base = baseClass
                    }

                let inherited =
                    inheritedFromBases
                        {
                            Interfaces = interfaces
                            Types = earlier
                        }
                        decl

                let implements =
                    decl.Implements
                    |> List.map (fun used ->
                        { used with
                            Arguments =
                                used.Arguments
                                |> List.map (fun atom ->
                                    match atom with
                                    | Atom.AParam _ when decl.Parameter.IsNone -> Atom.AObject
                                    | atom -> atom
                                )
                        }
                    )
                    |> List.map (satisfyConstraints interfaces isClass decl.IsStruct)
                    |> dropConflicts interfaces decl.Parameter.IsSome inherited

                earlier
                @ [
                    { decl with
                        Implements = implements
                    }
                ]
            )

        {
            Interfaces = interfaces
            Types = types
        }

    let private interfaceName (index : int) : string = $"I%d{index}"
    let private typeName (index : int) : string = $"D%d{index}"

    let private render (corpus : Corpus) : string =
        let builder = Text.StringBuilder ()

        let atomText (parameterName : int -> string) (self : string) (atom : Atom) : string =
            match atom with
            | Atom.AObject -> "object"
            | Atom.AString -> "string"
            | Atom.AInt -> "int"
            | Atom.AParam k -> parameterName k
            | Atom.ASelf -> self

        let useText (parameterName : int -> string) (self : string) (used : InterfaceUse) : string =
            match used.Arguments with
            | [] -> interfaceName used.Interface
            | arguments ->
                let args = arguments |> List.map (atomText parameterName self) |> String.concat ", "
                $"%s{interfaceName used.Interface}<%s{args}>"

        corpus.Interfaces
        |> List.iteri (fun index decl ->
            let parameterName k = $"P%d{k}"

            let parameters =
                match decl.Parameters with
                | [] -> ""
                | ps ->
                    ps
                    |> List.mapi (fun k p ->
                        match p.Variance with
                        | Variance.Invariant -> parameterName k
                        | Variance.Covariant -> $"out %s{parameterName k}"
                        | Variance.Contravariant -> $"in %s{parameterName k}"
                    )
                    |> String.concat ", "
                    |> sprintf "<%s>"

            let bases =
                match decl.Bases with
                | [] -> ""
                | bs -> " : " + (bs |> List.map (useText parameterName "") |> String.concat ", ")

            let constraints =
                decl.Parameters
                |> List.mapi (fun k p ->
                    if p.ClassConstraint then
                        $" where %s{parameterName k} : class"
                    else
                        ""
                )
                |> String.concat ""

            builder.Append ($"public interface %s{interfaceName index}%s{parameters}%s{bases}%s{constraints} {{ }}\n")
            |> ignore
        )

        corpus.Types
        |> List.iteri (fun index decl ->
            let parameterName _ = "T"

            let self =
                match decl.Parameter with
                | None -> typeName index
                | Some _ -> $"%s{typeName index}<T>"

            let baseText =
                match decl.Base with
                | None -> []
                | Some (b, None) -> [ typeName b ]
                | Some (b, Some atom) -> [ $"%s{typeName b}<%s{atomText parameterName self atom}>" ]

            let parents = baseText @ (decl.Implements |> List.map (useText parameterName self))

            let parentText =
                match parents with
                | [] -> ""
                | ps -> " : " + String.concat ", " ps

            let constraint' =
                match decl.Parameter with
                | Some true -> " where T : class"
                | _ -> ""

            let kind = if decl.IsStruct then "struct" else "class"

            builder.Append ($"public %s{kind} %s{self}%s{parentText}%s{constraint'} {{ }}\n")
            |> ignore
        )

        builder.ToString ()

    let private genInterfaceUse (interfaces : InterfaceDecl list) (atoms : Atom list) : Gen<InterfaceUse> =
        gen {
            let! index = Gen.choose (0, interfaces.Length - 1)
            let! arguments = Gen.listOfLength interfaces.[index].Parameters.Length (Gen.elements atoms)

            return
                {
                    Interface = index
                    Arguments = arguments
                }
        }

    let private genInterfaceDecl (earlier : InterfaceDecl list) : Gen<InterfaceDecl> =
        gen {
            let! arity = Gen.frequency [ 2, Gen.constant 0 ; 4, Gen.constant 1 ; 2, Gen.constant 2 ]

            let! parameters =
                Gen.listOfLength
                    arity
                    (Gen.map2
                        (fun variance classConstraint ->
                            {
                                Variance = variance
                                ClassConstraint = classConstraint
                            }
                        )
                        (Gen.elements [ Variance.Invariant ; Variance.Covariant ; Variance.Contravariant ])
                        (Gen.frequency [ 2, Gen.constant false ; 1, Gen.constant true ]))

            let atoms =
                [ Atom.AObject ; Atom.AString ; Atom.AInt ] @ List.init arity Atom.AParam

            let! baseCount =
                if earlier.IsEmpty then
                    Gen.constant 0
                else
                    Gen.choose (0, 2)

            let! bases = Gen.listOfLength baseCount (genInterfaceUse earlier atoms)

            return
                {
                    Parameters = parameters
                    Bases = bases
                }
        }

    let private genTypeDecl (interfaces : InterfaceDecl list) (earlier : TypeDecl list) : Gen<TypeDecl> =
        gen {
            let! isStruct = Gen.frequency [ 2, Gen.constant false ; 1, Gen.constant true ]

            let! parameter =
                Gen.frequency
                    [
                        1, Gen.constant None
                        1, Gen.constant (Some false)
                        1, Gen.constant (Some true)
                    ]

            let atoms =
                [ Atom.AObject ; Atom.AString ; Atom.AInt ; Atom.ASelf ]
                @ (if parameter.IsSome then [ Atom.AParam 0 ] else [])

            let classes =
                earlier
                |> List.indexed
                |> List.filter (fun (_, d) -> not d.IsStruct)
                |> List.map fst

            let! baseClass =
                if isStruct || classes.IsEmpty then
                    Gen.constant None
                else
                    Gen.frequency
                        [
                            1, Gen.constant None
                            1,
                            Gen.map2
                                (fun index atom -> Some (index, Some atom))
                                (Gen.elements classes)
                                (Gen.elements [ Atom.AObject ; Atom.AString ; Atom.AInt ; Atom.AParam 0 ])
                        ]

            let! count = Gen.choose (0, 3)
            let! implements = Gen.listOfLength count (genInterfaceUse interfaces atoms)

            return
                {
                    IsStruct = isStruct
                    Parameter = parameter
                    Base = baseClass
                    Implements = implements
                }
        }

    let private genCorpus : Gen<Corpus> =
        gen {
            let! interfaceCount = Gen.choose (1, 4)

            let! interfaces =
                (Gen.constant [], [ 1..interfaceCount ])
                ||> List.fold (fun acc _ ->
                    gen {
                        let! earlier = acc
                        let! next = genInterfaceDecl earlier
                        return earlier @ [ next ]
                    }
                )

            let! typeCount = Gen.choose (1, 4)

            let! types =
                (Gen.constant [], [ 1..typeCount ])
                ||> List.fold (fun acc _ ->
                    gen {
                        let! earlier = acc
                        let! next = genTypeDecl interfaces earlier
                        return earlier @ [ next ]
                    }
                )

            return
                repair
                    {
                        Interfaces = interfaces
                        Types = types
                    }
        }

    /// The pool of a corpus: each type's definition, its instantiations over `object`, `string` and
    /// `int` where its constraint admits them, its parameter, shapes over one parameter, and a few fixed
    /// CoreLib types.
    let private pool (corpus : Corpus) (host : Reflection.Assembly) : Type list =
        let hostType (name : string) : Type =
            match host.GetType name with
            | null -> failwith $"generated type %s{name} not found"
            | t -> t

        let instantiations (definition : Type) (classConstraints : bool list) : Type list =
            let candidates = [ typeof<obj> ; typeof<string> ; typeof<int> ]

            [
                for argument in candidates do
                    if argument <> typeof<int> || not (List.contains true classConstraints) then
                        yield
                            definition.MakeGenericType (List.replicate classConstraints.Length argument |> Array.ofList)
            ]

        let fromTypes =
            corpus.Types
            |> List.mapi (fun index decl ->
                match decl.Parameter with
                | None -> [ hostType (typeName index) ]
                | Some isClass ->
                    let definition = hostType $"%s{typeName index}`1"

                    definition
                    :: definition.GetGenericArguments().[0]
                    :: instantiations definition [ isClass ]
            )
            |> List.concat

        let fromInterfaces =
            corpus.Interfaces
            |> List.mapi (fun index decl ->
                match decl.Parameters with
                | [] -> [ hostType (interfaceName index) ]
                | ps ->
                    let definition = hostType $"%s{interfaceName index}`%d{ps.Length}"

                    definition
                    :: instantiations definition (ps |> List.map (fun p -> p.ClassConstraint))
            )
            |> List.concat

        let variable =
            corpus.Types
            |> List.indexed
            |> List.tryPick (fun (index, decl) ->
                match decl.Parameter with
                | Some _ -> Some ((hostType $"%s{typeName index}`1").GetGenericArguments().[0])
                | None -> None
            )
            |> Option.defaultValue listT

        fromTypes
        @ fromInterfaces
        @ [
            variable.MakeArrayType ()
            variable.MakeArrayType 2
            variable.MakePointerType ()
            variable.MakeByRefType ()
            typeof<int[]>
            typeof<string[]>
            typeof<Nullable<int>>
            typeof<System.Collections.Generic.IEnumerable<obj>>
            typeof<System.Collections.Generic.IList<int>>
            typeof<IComparable>
            typeof<string>
            typeof<int>
        ]

    let private checkCorpus (cover : Label -> unit) (corpus : Corpus) : unit =
        let source = render corpus

        let image =
            Roslyn.compileAssembly "InterfaceVerificationGenerated" OutputKind.DynamicallyLinkedLibrary [] [ source ]

        let dumped =
            use stream = new MemoryStream (image)
            AssemblyApi.read loggerFactory None stream

        let context =
            Runtime.Loader.AssemblyLoadContext ("interfaceVerification", isCollectible = true)

        try
            let host =
                use stream = new MemoryStream (image)
                context.LoadFromStream stream

            let images = [ hostCoreLib, corelib ; host, dumped ]
            let members = pool corpus host
            let caseState = coreState.WithLoadedAssembly dumped
            let disagreements = ResizeArray<string> ()

            for receiver in members do
                let mutable state = caseState

                for interfaceType in members do
                    let state', disagreement = comparePair cover images state receiver interfaceType
                    state <- state'

                    match disagreement with
                    | Some d -> disagreements.Add d
                    | None -> ()

            if disagreements.Count > 0 then
                failwith (
                    $"%d{disagreements.Count} pairs disagree with the host:\n"
                    + String.concat "\n" disagreements
                    + $"\nin the corpus:\n%s{source}"
                )
        finally
            context.Unload ()

    [<Test>]
    let ``Generated corpora: every pair's verdict agrees with the host's`` () : unit =
        let stopwatch = Stopwatch.StartNew ()

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 150)
                (Arb.fromGen genCorpus)
                (fun cover corpus -> checkCorpus cover corpus)

        TestContext.Progress.WriteLine $"generated corpora: %d{stopwatch.ElapsedMilliseconds} ms"

        // About half of what the fixed sample reaches. Decision 9's pairs are counted whichever way
        // PawPrint takes them, so the floor is on the shape; its refusals are counted, not floored.
        for label, floor in
            [
                Label.Passed, 1100
                Label.RaisedInvalidHandle, 4000
                Label.RaisedNotFoundOnTypeDesc, 4000
                Label.RaisedThisIsInterface, 24000
                Label.RaisedMustBeInterface, 20000
                Label.RaisedNotFound, 10000
                Label.PassedByVarianceAlone, 220
                Label.PassedOnDefinition, 11
                Label.OpenValueTypeOnVariantDefinition, 55
            ] do
            Assert.That (coverage.Count label, Is.GreaterThanOrEqualTo floor, $"%A{label}")

    // ----- CoreLib -----

    [<Test>]
    let ``CoreLib: every pair's verdict agrees with the host's`` () : unit =
        let stopwatch = Stopwatch.StartNew ()
        let images = [ hostCoreLib, corelib ]
        let exported = hostCoreLib.GetExportedTypes ()
        let receivers = exported |> Array.filter (fun t -> not t.IsInterface)

        let targets =
            Array.append
                (exported |> Array.filter (fun t -> t.IsInterface))
                [|
                    typeof<string>
                    typeof<int>
                    typedefof<System.Collections.Generic.List<obj>>
                |]

        let disagreements = ResizeArray<string> ()
        let mutable compared = 0
        let mutable passed = 0
        let mutable openValueType = 0
        let mutable refused = 0

        let cover (label : Label) : unit =
            match label with
            | Label.Passed -> passed <- passed + 1
            | Label.OpenValueTypeOnVariantDefinition -> openValueType <- openValueType + 1
            | Label.OpenValueTypeRefused -> refused <- refused + 1
            | _ -> ()

        for receiver in receivers do
            let mutable state = coreState

            for interfaceType in targets do
                let state', disagreement = comparePair cover images state receiver interfaceType
                state <- state'
                compared <- compared + 1

                match disagreement with
                | Some d -> disagreements.Add d
                | None -> ()

        TestContext.Progress.WriteLine
            $"CoreLib: %d{receivers.Length} receivers x %d{targets.Length} targets, %d{compared} pairs, %d{passed} passed, %d{openValueType} of an open value type against a variant definition (%d{refused} refused), %d{stopwatch.ElapsedMilliseconds} ms"

        List.ofSeq disagreements |> shouldEqual []
        // Agreement says nothing if the host passes everything or nothing.
        Assert.That (passed, Is.GreaterThan 0)
        Assert.That (passed, Is.LessThan compared)
