namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// Virtual dispatch of an async variant (decision 19 of `docs/plans/2026-11-11-runtime-async.md`):
/// the implementation of a variant on a receiver is the variant of the implementation of its
/// ordinary sibling, and where that implementation's variant does not return what the declaration's
/// does, the call is refused by name, since CoreCLR runs something PawPrint does not model there.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestAsyncVariantDispatch =

    let private client : string =
        """
using System;
using System.Threading.Tasks;

namespace Client;

public class Base
{
    public virtual Task<int> M() => Task.FromResult(1);
    public virtual Task N() => Task.CompletedTask;
    public virtual Task C() => Task.CompletedTask;
    public virtual ValueTask<string> V() => new ValueTask<string>("base");
}

public class Derived : Base
{
    public override Task<int> M() => Task.FromResult(2);
    public override ValueTask<string> V() => new ValueTask<string>("derived");
}

public class Leaf : Derived { }

public class Covariant : Base
{
    public override Task<int> C() => Task.FromResult(3);
}

public class MyTask : Task
{
    public MyTask() : base(() => { }) { }
}

public class SubclassReturn : Base
{
    public override MyTask N() => new MyTask();
}

public interface IAsks { Task<string> Ask(); }

public class Asks : IAsks
{
    public Task<string> Ask() => Task.FromResult("asked");
}
"""

    type private Fixture =
        {
            LoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
            RuntimeDirs : ImmutableArray<string>
            Corelib : DumpedAssembly
            Client : DumpedAssembly
            BaseClassTypes : BaseClassTypes<DumpedAssembly>
            State : TypeSystemState
        }

    let private fixture () : Fixture =
        let _, loggerFactory = LoggerFactory.makeTest ()
        let runtimeDirs = FrameworkUnderTest.runtimeDirs ()

        let image =
            Roslyn.compileAssembly "Client" OutputKind.DynamicallyLinkedLibrary [] [ client ]

        let clientAssembly =
            Assembly.read loggerFactory (Some "Client.dll") (new MemoryStream (image))

        let corelib =
            Assembly.readFile
                loggerFactory
                (Path.Combine (FrameworkUnderTest.sharedFrameworkDirectory (), "System.Private.CoreLib.dll"))

        let bct = BaseClassTypes.ofCorelib corelib
        let loaded = LoadedAssemblies.ofAssemblies [ corelib ; clientAssembly ]

        {
            LoggerFactory = loggerFactory
            RuntimeDirs = runtimeDirs
            Corelib = corelib
            Client = clientAssembly
            BaseClassTypes = bct
            State =
                { TypeSystemState.Empty with
                    _LoadedAssemblies = loaded
                    ConcreteTypes = Corelib.concretizeAll loaded bct AllConcreteTypes.Empty
                }
        }

    let private typeNamed (fixture : Fixture) (name : string) : TypeInfo<GenericParamFromMetadata, TypeDefn> =
        fixture.Client.TypeDefs.Values |> Seq.find (fun ty -> ty.Name = name)

    /// Dispatch of the async variant of `declaringName::methodName` on a receiver of type
    /// `receiverName`.
    let private dispatch
        (receiverName : string)
        (declaringName : string)
        (methodName : string)
        : Fixture * TypeSystemState * VirtualImplementation
        =
        let fixture = fixture ()

        let state, receiver =
            TypeSystemState.concretizeType
                fixture.LoggerFactory
                fixture.RuntimeDirs
                fixture.BaseClassTypes
                fixture.State
                fixture.Client.DefinitionFullName
                ImmutableArray.Empty
                ImmutableArray.Empty
                (TypeDefn.FromDefinition ((typeNamed fixture receiverName).Identity, SignatureTypeKind.Class))

        let receiver = BindingResult.value receiver

        let method =
            (typeNamed fixture declaringName).Methods
            |> List.find (fun m -> m.Name = methodName)

        let assemblies, variant =
            RuntimeAsyncMethod.asyncVariantOf
                fixture.LoggerFactory
                fixture.RuntimeDirs
                fixture.Corelib
                fixture.Client
                method
                state._LoadedAssemblies

        let variant =
            match variant with
            | Ok variant -> variant
            | Error refusal -> failwith $"%s{declaringName}::%s{methodName} has no async variant: %A{refusal}"

        let state =
            { state with
                _LoadedAssemblies = assemblies
            }

        let state, concretized =
            MethodConcretisation.concretizeMethodWithAllGenerics
                fixture.LoggerFactory
                fixture.RuntimeDirs
                fixture.BaseClassTypes
                ImmutableArray.Empty
                variant
                ImmutableArray.Empty
                state

        let concretized, _ = BindingResult.value concretized

        let state, resolved =
            ConcreteVirtualDispatch.tryResolveVirtualImplementation
                fixture.LoggerFactory
                fixture.RuntimeDirs
                fixture.BaseClassTypes
                concretized.Generics
                concretized
                receiver
                true
                state

        fixture, state, BindingResult.value resolved

    /// The receiver, the type declaring the method called, the method's name, and the type declaring
    /// the method whose variant dispatch must choose.
    let cases : TestCaseData list =
        [
            "Base", "Base", "M", "Base"
            "Derived", "Base", "M", "Derived"
            "Leaf", "Base", "M", "Derived"
            "Leaf", "Base", "V", "Derived"
            "Asks", "IAsks", "Ask", "Asks"
        ]
        |> List.map (fun (receiver, declaring, methodName, chosen) ->
            TestCaseData([| box receiver ; box declaring ; box methodName ; box chosen |])
                .SetArgDisplayNames ($"%s{receiver} %s{declaring}::%s{methodName}")
        )

    [<TestCaseSource(nameof cases)>]
    let ``dispatch of a variant chooses the variant of the ordinary sibling's implementation``
        (receiverName : string)
        (declaringName : string)
        (methodName : string)
        (chosenName : string)
        : unit
        =
        match dispatch receiverName declaringName methodName with
        | _, _, VirtualImplementation.Found found ->
            found.Definition.RequiredDeclaringType.Name |> shouldEqual chosenName
            found.Definition.Name |> shouldEqual methodName

            match found.Definition.TryVariant with
            | Some MethodVariant.AsyncVariant -> ()
            | other -> failwith $"dispatch chose %O{found.Definition}, the %A{other}, rather than its async variant"
        | _, _, other -> failwith $"expected dispatch to choose %s{chosenName}'s method, got %A{other}"

    /// `Covariant.C` returns `Task<int>` for `Base.C`'s `Task`: CoreCLR's builder gives the override's
    /// variant slot a thunk that drops the `int`, which PawPrint does not model.
    [<Test>]
    let ``dispatch to a covariant override whose variant returns a value the declaration's does not is refused``
        ()
        : unit
        =
        let e =
            Assert.Throws<Exception> (fun () -> dispatch "Covariant" "Base" "C" |> ignore)

        e.Message |> shouldContainText "TODO"
        e.Message |> shouldContainText "return-dropping thunk"

    /// `SubclassReturn.N` returns `MyTask`, which is no promise type, so it has no variant: CoreCLR
    /// runs the variant the override inherits, which PawPrint does not model.
    [<Test>]
    let ``dispatch to an override returning a subclass of Task is refused`` () : unit =
        let e =
            Assert.Throws<Exception> (fun () -> dispatch "SubclassReturn" "Base" "N" |> ignore)

        e.Message |> shouldContainText "TODO"
        e.Message |> shouldContainText "has no async variant"
