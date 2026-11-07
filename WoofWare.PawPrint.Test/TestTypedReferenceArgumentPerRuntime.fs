namespace WoofWare.PawPrint.Test

open System.Collections.Immutable
open System.Reflection.Metadata
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// Whether the loader and the type system refuse `System.TypedReference` as an instantiation's
/// argument, judged by the runtime whose CoreLib they hold, on each supported runtime's own CoreLib
/// whichever runtime is under test.
///
/// CoreCLR's `Generics::CheckInstantiation` refuses an argument whose element type is `TYPEDBYREF`.
/// .NET 10's VM reports `TypedReference` so; .NET 11's treats it as an ordinary value type
/// (measured in `TestFabricatedTypedReferenceInstantiation`).
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTypedReferenceArgumentPerRuntime =

    let private loggerFactory = snd (LoggerFactory.makeTest ())

    let everyRuntime : TestCaseData list = FrameworkUnderTest.everyRuntime

    let private refusedOn (runtime : EmulatedRuntime) : bool =
        match runtime with
        | EmulatedRuntime.Net10 -> true
        | EmulatedRuntime.Net11 -> false

    [<TestCaseSource(nameof everyRuntime)>]
    let ``TypedReference is a malformed argument exactly where the CoreLib's runtime refuses it``
        (runtime : EmulatedRuntime)
        : unit
        =
        let corelib =
            Assembly.readFile loggerFactory (FrameworkUnderTest.requireCoreLibPathOf runtime)

        EmulatedRuntime.ofCoreLib corelib |> shouldEqual runtime

        let bct = BaseClassTypes.ofCorelib corelib

        // The type system, over a handle.
        let state = TypeSystemState.Empty.WithLoadedAssembly corelib

        let concreteTypes =
            Corelib.concretizeAll state._LoadedAssemblies bct AllConcreteTypes.Empty

        let typedReference =
            AllConcreteTypes.findExistingNonGenericConcreteType concreteTypes bct.TypedReference.Identity
            |> Option.get

        let expected =
            if refusedOn runtime then
                Some (0, MalformedArgument.TypedReference)
            else
                None

        AllConcreteTypes.malformedArgument bct concreteTypes [ typedReference ]
        |> shouldEqual expected

        // The loader, over a spelling: `IEnumerable<TypedReference>`.
        let spelling =
            TypeDefn.GenericInstantiation (
                TypeDefn.FromDefinition (bct.IEnumerableGeneric.Identity, SignatureTypeKind.Class),
                ImmutableArray.Create (
                    TypeDefn.FromDefinition (bct.TypedReference.Identity, SignatureTypeKind.ValueType)
                )
            )

        let _, resolved =
            TypeResolution.resolveTypeFromDefn
                loggerFactory
                ImmutableArray.Empty
                bct
                spelling
                ImmutableArray.Empty
                ImmutableArray.Empty
                corelib
                state._LoadedAssemblies

        match resolved with
        | Error (TypeBindingFailure.Refused (TypeLoadRefusal.InvalidInstantiation (definition,
                                                                                   0,
                                                                                   MalformedArgument.TypedReference))) when
            refusedOn runtime
            ->
            definition |> shouldEqual bct.IEnumerableGeneric.Identity
        | Ok (_, info) when not (refusedOn runtime) -> info.Identity |> shouldEqual bct.IEnumerableGeneric.Identity
        | other -> failwith $"on %O{runtime}, IEnumerable<TypedReference> resolved to %A{other}"
