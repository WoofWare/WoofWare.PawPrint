namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// `IlMachineState.isRuntimeTypeHandleTargetAssignableTo` is CoreCLR's `TypeHandle::CanCastTo`,
/// which is what reflection's `Type.IsAssignableFrom` asks of two runtime types (through
/// `CanCastToForReflection`, whose only extra rule, `T` to `Nullable<T>`, relates closed types). Here
/// a pool of types is built twice, as PawPrint targets and as host `Type`s: closed types, the
/// generic parameters of a few corpus definitions, and arrays, rank-one multi-dimensional arrays,
/// jagged arrays, pointers, byrefs and constructions over those parameters. For every ordered pair
/// with at least one side open, PawPrint's verdict must equal the host's.
///
/// The host is an outside oracle: nothing about the expected verdict is read out of the structure
/// under test. The pool has no `Nullable<T>`, so `IsAssignableFrom` and `CanCastTo` agree on it.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestCompositeTypeVariableCasts =

    let private corpusSource : string =
        """
using System;

public interface IMarker { }
public class Base { }
public class Derived : Base, IMarker { }

// Variables spread over what CoreCLR's `CanCastParam` asks of an array's element: whether the
// variable is an object reference (`ConstrainedAsObjRef`), and what it casts to.
public class N<T> { }
public class C<T> where T : class { }
public class B<T> where T : Base { }
public class V<T> where T : struct { }
"""

    let private image : byte[] =
        Roslyn.compileAssembly "CompositeTypeVariableCastCorpus" OutputKind.DynamicallyLinkedLibrary [] [ corpusSource ]

    let private hostAssembly : Reflection.Assembly = Reflection.Assembly.Load image

    // The factory is intentionally undisposed: the DumpedAssembly loggers close over its sinks and
    // outlive this scope.
    let private loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory =
        let _, loggerFactory = LoggerFactory.makeTest ()
        loggerFactory

    let private corelib : DumpedAssembly =
        Assembly.readFile loggerFactory HostImage.coreLibPath

    let private guest : DumpedAssembly =
        use stream = new MemoryStream (image)
        AssemblyApi.read loggerFactory None stream

    let private bct : BaseClassTypes<DumpedAssembly> = BaseClassTypes.ofCorelib corelib

    let private initialState : IlMachineState =
        let withAssemblies =
            (IlMachineState.initial loggerFactory ImmutableArray.Empty guest).WithLoadedAssembly corelib

        { withAssemblies with
            TypeSystem =
                { withAssemblies.TypeSystem with
                    ConcreteTypes =
                        Corelib.concretizeAll withAssemblies.TypeSystem._LoadedAssemblies bct AllConcreteTypes.Empty
                }
        }

    let private guestType (name : string) : TypeInfo<GenericParamFromMetadata, TypeDefn> =
        guest.TypeDefs.Values
        |> Seq.tryFind (fun ty -> ty.Name = name)
        |> Option.defaultWith (fun () -> failwith $"corpus type %s{name} not found in the PawPrint-read image")

    let private hostType (name : string) : Type =
        match hostAssembly.GetType name with
        | null -> failwith $"corpus type %s{name} not found in the host-loaded image"
        | ty -> ty

    let private corelibType (ns : string) (name : string) : TypeInfo<GenericParamFromMetadata, TypeDefn> =
        corelib.TryGetTopLevelTypeDef ns name
        |> Option.defaultWith (fun () -> failwith $"%s{ns}.%s{name} not found in corelib")

    let private definitionDefn (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>) : TypeDefn =
        TypeDefn.FromDefinition (
            typeInfo.Identity,
            LoadedTypeInfo.signatureTypeKind bct initialState.TypeSystem._LoadedAssemblies typeInfo
        )

    /// A pool member, spelled once for each side. `Owner` is the corpus definition whose first
    /// parameter `Defn`'s `!0` names, if it names one.
    type private Member =
        {
            Name : string
            Defn : TypeDefn
            Owner : TypeInfo<GenericParamFromMetadata, TypeDefn> option
            Host : Type
        }

    let private closed (name : string) (defn : TypeDefn) (host : Type) : Member =
        {
            Name = name
            Defn = defn
            Owner = None
            Host = host
        }

    let private closedCorelib (ns : string) (name : string) (host : Type) : Member =
        closed (string<Type> host) (definitionDefn (corelibType ns name)) host

    let private closedGuest (name : string) : Member =
        closed name (definitionDefn (guestType name)) (hostType name)

    /// Each shape over `element`, on both sides at once.
    let private szArray (element : Member) : Member =
        { element with
            Name = $"%s{element.Name}[]"
            Defn = TypeDefn.OneDimensionalArrayLowerBoundZero element.Defn
            Host = element.Host.MakeArrayType ()
        }

    let private mdArray (rank : int) (element : Member) : Member =
        { element with
            Name = $"%s{element.Name}[rank %d{rank}]"
            Defn = TypeDefn.Array (element.Defn, rank)
            Host = element.Host.MakeArrayType rank
        }

    let private pointer (element : Member) : Member =
        { element with
            Name = $"%s{element.Name}*"
            Defn = TypeDefn.Pointer element.Defn
            Host = element.Host.MakePointerType ()
        }

    let private byref (element : Member) : Member =
        { element with
            Name = $"%s{element.Name}&"
            Defn = TypeDefn.Byref element.Defn
            Host = element.Host.MakeByRefType ()
        }

    let private constructed (ns : string) (name : string) (hostDefinition : Type) (argument : Member) : Member =
        { argument with
            Name = $"%s{name}<%s{argument.Name}>"
            Defn =
                TypeDefn.GenericInstantiation (
                    definitionDefn (corelibType ns name),
                    ImmutableArray.Create argument.Defn
                )
            Host = hostDefinition.MakeGenericType argument.Host
        }

    let private iList : Member -> Member =
        constructed "System.Collections.Generic" "IList`1" typedefof<System.Collections.Generic.IList<obj>>

    let private iEnumerable : Member -> Member =
        constructed "System.Collections.Generic" "IEnumerable`1" typedefof<System.Collections.Generic.IEnumerable<obj>>

    let private variable (definition : string) : Member =
        let owner = guestType definition

        {
            Name = $"%s{definition}.T"
            Defn = TypeDefn.GenericTypeParameter 0
            Owner = Some owner
            Host = (hostType definition).GetGenericArguments().[0]
        }

    let private pool : Member list =
        let object' =
            closed "object" (TypeDefn.PrimitiveType PrimitiveType.Object) typeof<obj>

        let string' =
            closed "string" (TypeDefn.PrimitiveType PrimitiveType.String) typeof<string>

        let int' = closed "int" (TypeDefn.PrimitiveType PrimitiveType.Int32) typeof<int>
        let base' = closedGuest "Base"
        let derived = closedGuest "Derived"

        let closedMembers =
            [
                object'
                string'
                int'
                base'
                derived
                closedGuest "IMarker"
                closedCorelib "System" "Array" typeof<Array>
                closedCorelib "System" "ICloneable" typeof<ICloneable>
                closedCorelib "System" "ValueType" typeof<ValueType>
                closedCorelib "System.Collections" "IList" typeof<System.Collections.IList>
                iList object'
                iList base'
                iList int'
                iEnumerable object'
                szArray object'
                szArray base'
                szArray derived
                szArray int'
                szArray (szArray object')
                mdArray 1 object'
                mdArray 2 object'
                pointer int'
                byref int'
                byref object'
            ]

        let openMembers =
            [
                for definition in [ "N`1" ; "C`1" ; "B`1" ; "V`1" ] do
                    let t = variable definition
                    yield t
                    yield szArray t
                    yield mdArray 1 t
                    yield mdArray 2 t
                    yield szArray (szArray t)
                    yield pointer t
                    yield byref t
                    yield iList t
                    yield iEnumerable (szArray t)
            ]

        closedMembers @ openMembers

    /// The target PawPrint reflects for `member`, read with its owner's formals unbound.
    let private target (state : IlMachineState) (member' : Member) : IlMachineState * RuntimeTypeHandleTarget =
        let typeVariables =
            match member'.Owner with
            | None -> ImmutableArray.Empty
            | Some owner ->
                Seq.init
                    owner.Generics.Length
                    (fun index -> RuntimeTypeHandleTarget.GenericParameter (owner.Identity, index))
                |> ImmutableArray.CreateRange

        ReflectedTypeTarget.reflectedTypeTarget
            loggerFactory
            bct
            "TestCompositeTypeVariableCasts"
            member'.Name
            guest
            {
                ReflectedTypeTarget.ReflectionTypeEnvironment.TypeVariables =
                    ReflectedTypeTarget.ReflectionVariableBinding.Open typeVariables
                ReflectedTypeTarget.ReflectionTypeEnvironment.MethodVariables =
                    ReflectedTypeTarget.ReflectionVariableBinding.Open ImmutableArray.Empty
            }
            state
            member'.Defn

    [<Test>]
    let ``every pair with an open side agrees with the host CLR's IsAssignableFrom`` () : unit =
        let mutable state = initialState
        let mutable disagreements = []
        let mutable hostAccepted = 0
        let mutable compared = 0

        for source in pool do
            for destination in pool do
                if source.Owner.IsSome || destination.Owner.IsSome then
                    let expected = destination.Host.IsAssignableFrom source.Host
                    let state', sourceTarget = target state source
                    let state', destinationTarget = target state' destination

                    let state', actual =
                        IlMachineState.isRuntimeTypeHandleTargetAssignableTo
                            loggerFactory
                            bct
                            state'
                            sourceTarget
                            destinationTarget

                    state <- state'
                    compared <- compared + 1

                    if expected then
                        hostAccepted <- hostAccepted + 1

                    if expected <> actual then
                        disagreements <-
                            $"%s{source.Name} -> %s{destination.Name}: host %b{expected}, PawPrint %b{actual}"
                            :: disagreements

        if not (List.isEmpty disagreements) then
            failwith (
                $"%d{List.length disagreements} of %d{compared} pairs disagree with the host CLR:\n"
                + String.concat "\n" (List.rev disagreements)
            )

        // Agreement says nothing if the host accepts everything or nothing.
        Assert.That (hostAccepted, Is.GreaterThan 0)
        Assert.That (hostAccepted, Is.LessThan compared)
