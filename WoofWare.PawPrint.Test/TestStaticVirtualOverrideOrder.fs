namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// Which of a dynamic type's MethodImpl rows a static virtual call runs when more than one matches: PawPrint's
/// own resolver (`StaticVirtualDispatch.resolve`), over a scope built with the calls the emit handlers make,
/// against real .NET's `TypeBuilder` in this process, read through `GetInterfaceMap`, which resolves afresh at
/// every call (measured on .NET 10 and 11). And the measurement behind PawPrint's refusal: real .NET caches a
/// resolution at the call site that made it, so a site that resolved before the module reached 25 MethodImpl
/// rows keeps the earlier answer.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestStaticVirtualOverrideOrder =

    let private fixtureSource : string =
        """
public interface IDef { static virtual int M() => 0; }
public interface IFill { int F(); }

public static class Caller
{
    public static int CallDef<T>() where T : IDef => T.M();
    public static int CallDefAgain<T>() where T : IDef => T.M();
}
"""

    let private fixtureImage : byte[] Lazy =
        lazy
            (Roslyn.compileAssembly
                "StaticVirtualOverrideOrderFixture"
                OutputKind.DynamicallyLinkedLibrary
                []
                [ fixtureSource ])

    /// The fixture, loaded into this process once.
    let private hostFixture : Assembly Lazy = lazy (Assembly.Load fixtureImage.Value)

    let private corelib : DumpedAssembly Lazy =
        lazy
            (let _, loggerFactory = LoggerFactory.makeTest ()
             Assembly.readFile loggerFactory HostImage.coreLibPath)

    /// The fixture as PawPrint reads it.
    let private fixture : DumpedAssembly Lazy =
        lazy
            (let _, loggerFactory = LoggerFactory.makeTest ()

             Assembly.read
                 loggerFactory
                 (Some "StaticVirtualOverrideOrderFixture.dll")
                 (new MemoryStream (fixtureImage.Value)))

    let private baseLoaded : LoadedAssemblies Lazy =
        lazy (LoadedAssemblies.ofAssemblies [ corelib.Value ; fixture.Value ])

    let private bct : BaseClassTypes<DumpedAssembly> Lazy =
        lazy (BaseClassTypes.ofCorelib corelib.Value)

    let private baseConcreteTypes : AllConcreteTypes Lazy =
        lazy (Corelib.concretizeAll baseLoaded.Value bct.Value AllConcreteTypes.Empty)

    /// `count` types, each implementing `IFill` with one MethodImpl row, as `Fill` in the guests defines them.
    let private hostFill (modul : ModuleBuilder) (count : int) (prefix : string) : unit =
        let iFill = hostFixture.Value.GetType "IFill"

        for i in 0 .. count - 1 do
            let t = modul.DefineType ($"%s{prefix}%d{i}", TypeAttributes.Public)
            t.AddInterfaceImplementation iFill

            let f =
                t.DefineMethod (
                    "F",
                    MethodAttributes.Public
                    ||| MethodAttributes.Virtual
                    ||| MethodAttributes.NewSlot
                    ||| MethodAttributes.Final
                    ||| MethodAttributes.HideBySig,
                    typeof<int>,
                    [||]
                )

            let il = f.GetILGenerator ()
            il.Emit (OpCodes.Ldc_I4, i)
            il.Emit OpCodes.Ret
            t.DefineMethodOverride (f, iFill.GetMethod "F")

    let private hostStaticBody (t : TypeBuilder) (body : int) : MethodBuilder =
        let m =
            t.DefineMethod (
                $"B%d{body}",
                MethodAttributes.Public
                ||| MethodAttributes.Static
                ||| MethodAttributes.HideBySig,
                typeof<int>,
                [||]
            )

        let il = m.GetILGenerator ()
        il.Emit (OpCodes.Ldc_I4, body)
        il.Emit OpCodes.Ret
        m

    [<Test>]
    let ``real .NET keeps a static virtual resolution at the call site that made it`` () : unit =
        let iDef = hostFixture.Value.GetType "IDef"
        let caller = hostFixture.Value.GetType "Caller"

        let call (name : string) (t : Type) : int =
            caller.GetMethod(name).MakeGenericMethod([| t |]).Invoke ((null : obj), [||])
            |> unbox<int>

        let assembly =
            AssemblyBuilder.DefineDynamicAssembly (AssemblyName "Cached", AssemblyBuilderAccess.RunAndCollect)

        let modul = assembly.DefineDynamicModule "Cached"
        let c = modul.DefineType ("C", TypeAttributes.Public)
        c.AddInterfaceImplementation iDef
        c.DefineMethodOverride (hostStaticBody c 1, iDef.GetMethod "M")
        c.DefineMethodOverride (hostStaticBody c 2, iDef.GetMethod "M")
        let created = c.CreateType ()

        call "CallDef" created |> shouldEqual 1
        hostFill modul 23 "F"
        // The site that resolved below 25 rows keeps its answer; a site resolving now walks the rows in reverse.
        call "CallDef" created |> shouldEqual 1
        call "CallDefAgain" created |> shouldEqual 2

    /// Whose the schedule's rows for `IDef.M` are, and what `C` is.
    [<RequireQualifiedAccess>]
    type private Shape =
        /// The rows are `C`'s own.
        | OnClass
        /// The rows are those of an interface `I2 : IDef`, and `C : I2`, created straight after `I2`, has none.
        | OnInterface
        /// As `OnInterface`, but `I3 : I2`, created straight after `I2` with one row of its own (`B9`), is more
        /// specific than `I2`, and `C : I3`.
        | Overridden

    /// A module's MethodImpl rows, in the order added: `Before` rows of other types, then those of the type the
    /// shape gives them to, one naming `IDef.M` for each of `Rows` (a body number, repeats allowed), then
    /// `Between` rows of other types; that type is created, with `C` (and `I3`, with its row) straight after; then
    /// `After` rows of other types.
    type private Schedule =
        {
            Shape : Shape
            Before : int
            Rows : int list
            Between : int
            After : int
        }

        /// The module's row count when the type holding `Rows` is created.
        member this.AtCreation : int = this.Before + this.Rows.Length + this.Between

        member this.Total : int =
            this.AtCreation + (if this.Shape = Shape.Overridden then 1 else 0) + this.After

    let private interfaceAttributes : TypeAttributes =
        TypeAttributes.Public ||| TypeAttributes.Interface ||| TypeAttributes.Abstract

    /// The body `GetInterfaceMap` reports for `IDef.M` on real .NET's `C`, when `C` is created and at the end.
    let private hostAnswers (schedule : Schedule) : string * string =
        let iDef = hostFixture.Value.GetType "IDef"

        let assembly =
            AssemblyBuilder.DefineDynamicAssembly (AssemblyName "Order", AssemblyBuilderAccess.RunAndCollect)

        let modul = assembly.DefineDynamicModule "Order"
        hostFill modul schedule.Before "F"

        let owner =
            match schedule.Shape with
            | Shape.OnClass -> modul.DefineType ("C", TypeAttributes.Public)
            | Shape.OnInterface
            | Shape.Overridden -> modul.DefineType ("I2", interfaceAttributes)

        owner.AddInterfaceImplementation iDef
        let bodies = Collections.Generic.Dictionary<int, MethodBuilder> ()

        for body in schedule.Rows do
            let m =
                match bodies.TryGetValue body with
                | true, m -> m
                | false, _ ->
                    let m = hostStaticBody owner body
                    bodies.[body] <- m
                    m

            owner.DefineMethodOverride (m, iDef.GetMethod "M")

        hostFill modul schedule.Between "G"
        let ownerCreated = owner.CreateType ()

        let created =
            match schedule.Shape with
            | Shape.OnClass -> ownerCreated
            | Shape.OnInterface
            | Shape.Overridden ->
                let implemented =
                    match schedule.Shape with
                    | Shape.Overridden ->
                        let i3 = modul.DefineType ("I3", interfaceAttributes)
                        i3.AddInterfaceImplementation ownerCreated
                        i3.DefineMethodOverride (hostStaticBody i3 9, iDef.GetMethod "M")
                        i3.CreateType ()
                    | _ -> ownerCreated

                let c = modul.DefineType ("C", TypeAttributes.Public)
                c.AddInterfaceImplementation implemented
                c.CreateType ()

        let read () =
            (created.GetInterfaceMap iDef).TargetMethods |> Array.exactlyOne |> _.Name

        let atCreation = read ()
        hostFill modul schedule.After "H"
        atCreation, read ()

    let private dynamicParts : DynamicAssemblyName =
        {
            SimpleName = "Order"
            Version = Version (0, 0, 0, 0)
            Culture = ""
            PublicKey = ImmutableArray<byte>.Empty
            Flags = enum<System.Reflection.AssemblyFlags> 0
            HashAlgorithm = System.Reflection.AssemblyHashAlgorithm.Sha1
        }

    /// `ldc.i4 value; ret`, as the emitter stores it.
    let private constantBody (value : int) : ImmutableArray<byte> =
        let code =
            ImmutableArray.Create<byte> (Array.concat [ [| 0x20uy |] ; BitConverter.GetBytes value ; [| 0x2Auy |] ])

        EmitScopeDefinitions.methodBody false 8us code None []

    /// What PawPrint's resolver gives for `IDef.M` on `C`, at the module's end and when `C` was created: the body's
    /// name, or the refusal's message.
    let private pawPrintAnswers (schedule : Schedule) : Result<string, string> * Result<string, string> =
        let _, loggerFactory = LoggerFactory.makeTest ()
        let fixture = fixture.Value
        let corelib = corelib.Value
        let scope = DynamicAssemblyImage.scope dynamicParts Guid.Empty

        let fixtureRef, scope =
            EmitScopeReferences.defineAssemblyRef
                (AssemblyRefRequest.ofDefinition (fixture.PeReader.GetMetadataReader ()))
                scope

        let corelibRef, scope =
            EmitScopeReferences.defineAssemblyRef
                (AssemblyRefRequest.ofDefinition (corelib.PeReader.GetMetadataReader ()))
                scope

        let objectRef, scope =
            EmitScopeReferences.defineTypeRefByName (ResolutionScope.AssemblyRef corelibRef) "System.Object" scope

        let iDefRef, scope =
            EmitScopeReferences.defineTypeRefByName (ResolutionScope.AssemblyRef fixtureRef) "IDef" scope

        let iFillRef, scope =
            EmitScopeReferences.defineTypeRefByName (ResolutionScope.AssemblyRef fixtureRef) "IFill" scope

        let mRef, scope =
            EmitScopeReferences.defineMemberRef
                (MemberRefParent.TypeRef iDefRef)
                "M"
                (ImmutableArray.Create<byte> [| 0x00uy ; 0x00uy ; 0x08uy |])
                scope

        let fRef, scope =
            EmitScopeReferences.defineMemberRef
                (MemberRefParent.TypeRef iFillRef)
                "F"
                (ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x08uy |])
                scope

        let defineType (name : string) (implemented : TypeDefOrRef) (scope : EmitScope) : TypeDefRid * EmitScope =
            match
                EmitScopeDefinitions.defineTopLevelTypeDef
                    name
                    TypeAttributes.Public
                    (Some (TypeDefOrRef.TypeRef objectRef))
                    [ implemented ]
                    scope
            with
            | TypeDefDefinition.Defined (rid, scope) -> rid, scope
            | TypeDefDefinition.Duplicate _ -> failwith $"the schedule defines %s{name} twice"

        let defineMethod
            (owner : TypeDefRid)
            (name : string)
            (signature : byte[])
            (flags : MethodAttributes)
            (value : int)
            (scope : EmitScope)
            : MethodDefRid * EmitScope
            =
            let rid, scope =
                EmitScopeDefinitions.defineMethod owner name (ImmutableArray.Create<byte> signature) flags scope

            rid, EmitScope.setMethodBody rid (constantBody value) scope

        let fill (count : int) (prefix : string) (scope : EmitScope) : EmitScope =
            (scope, [ 0 .. count - 1 ])
            ||> List.fold (fun scope i ->
                let t, scope = defineType $"%s{prefix}%d{i}" (TypeDefOrRef.TypeRef iFillRef) scope

                let f, scope =
                    defineMethod
                        t
                        "F"
                        [| 0x20uy ; 0x00uy ; 0x08uy |]
                        (MethodAttributes.Public
                         ||| MethodAttributes.Virtual
                         ||| MethodAttributes.NewSlot
                         ||| MethodAttributes.Final
                         ||| MethodAttributes.HideBySig)
                        i
                        scope

                EmitScope.addMethodImpl
                    {
                        Class = t
                        Body = MethodDefOrRef.MethodDef f
                        Declaration = MethodDefOrRef.MemberRef fRef
                    }
                    scope
            )

        let defineInterface (name : string) (extended : TypeDefOrRef) (scope : EmitScope) : TypeDefRid * EmitScope =
            match EmitScopeDefinitions.defineTopLevelTypeDef name interfaceAttributes None [ extended ] scope with
            | TypeDefDefinition.Defined (rid, scope) -> rid, scope
            | TypeDefDefinition.Duplicate _ -> failwith $"the schedule defines %s{name} twice"

        let addRow (owner : TypeDefRid) (body : MethodDefRid) (scope : EmitScope) : EmitScope =
            EmitScope.addMethodImpl
                {
                    Class = owner
                    Body = MethodDefOrRef.MethodDef body
                    Declaration = MethodDefOrRef.MemberRef mRef
                }
                scope

        let staticBody (owner : TypeDefRid) (body : int) (scope : EmitScope) : MethodDefRid * EmitScope =
            defineMethod
                owner
                $"B%d{body}"
                [| 0x00uy ; 0x00uy ; 0x08uy |]
                (MethodAttributes.Public
                 ||| MethodAttributes.Static
                 ||| MethodAttributes.HideBySig)
                body
                scope

        let scope = fill schedule.Before "F" scope

        let owner, scope =
            match schedule.Shape with
            | Shape.OnClass -> defineType "C" (TypeDefOrRef.TypeRef iDefRef) scope
            | Shape.OnInterface
            | Shape.Overridden -> defineInterface "I2" (TypeDefOrRef.TypeRef iDefRef) scope

        let scope, _ =
            ((scope, Map.empty), schedule.Rows)
            ||> List.fold (fun (scope, bodies) body ->
                let m, scope, bodies =
                    match Map.tryFind body bodies with
                    | Some m -> m, scope, bodies
                    | None ->
                        let m, scope = staticBody owner body scope
                        m, scope, Map.add body m bodies

                addRow owner m scope, bodies
            )

        let atOwnerCreation = fill schedule.Between "G" scope

        let loaded, dynamicAssembly =
            match baseLoaded.Value.WithDynamicAssembly loggerFactory atOwnerCreation with
            | Ok loaded -> loaded
            | Error existing -> failwith $"unexpected collision with %s{existing.DefinitionFullName}"

        let name = dynamicAssembly.DefinitionFullName

        let loaded =
            (loaded.WithAssemblyRefTarget name fixtureRef fixture).WithAssemblyRefTarget name corelibRef corelib

        let loaded = loaded.WithCreatedType name owner

        // `C`, and for `Overridden` `I3` before it, each created as soon as it is defined.
        let c, atCreationScope, loaded =
            match schedule.Shape with
            | Shape.OnClass -> owner, atOwnerCreation, loaded
            | Shape.OnInterface ->
                // `C` implements `I2`, not `IDef`, as real .NET's `TypeBuilder` records it.
                let c, scope = defineType "C" (TypeDefOrRef.TypeDef owner) atOwnerCreation
                let loaded, _ = loaded.WithEmitScope loggerFactory name scope
                c, scope, loaded.WithCreatedType name c
            | Shape.Overridden ->
                let i3, scope = defineInterface "I3" (TypeDefOrRef.TypeDef owner) atOwnerCreation
                let b9, scope = staticBody i3 9 scope
                let scope = addRow i3 b9 scope
                let loaded, _ = loaded.WithEmitScope loggerFactory name scope
                let loaded = loaded.WithCreatedType name i3
                let c, scope = defineType "C" (TypeDefOrRef.TypeDef i3) scope
                let loaded, _ = loaded.WithEmitScope loggerFactory name scope
                c, scope, loaded.WithCreatedType name c

        let finalScope = fill schedule.After "H" atCreationScope

        let resolve (loaded : LoadedAssemblies) : Result<string, string> =
            let dirs = HostImage.runtimeDirs
            let bct = bct.Value

            let state =
                { TypeSystemState.Empty with
                    _LoadedAssemblies = loaded
                    ConcreteTypes = baseConcreteTypes.Value
                }

            let state, constrained =
                TypeSystemState.concretizeType
                    loggerFactory
                    dirs
                    bct
                    state
                    name
                    ImmutableArray.Empty
                    ImmutableArray.Empty
                    (TypeDefn.FromDefinition (
                        ResolvedTypeIdentity.ofDefinitionInAssembly name (MetadataTokens.TypeDefinitionHandle c.Rid),
                        SignatureTypeKind.Class
                    ))

            let constrained = BindingResult.value constrained

            let m =
                fixture.TypeDefs.Values
                |> Seq.find (fun ty -> ty.Name = "IDef")
                |> _.Methods
                |> List.exactlyOne

            let state, concretized =
                MethodConcretisation.concretizeMethodWithAllGenerics
                    loggerFactory
                    dirs
                    bct
                    ImmutableArray.Empty
                    m
                    ImmutableArray.Empty
                    state
                |> BindingResult.bound

            match
                (try
                    Ok (
                        StaticVirtualDispatch.resolve loggerFactory dirs bct constrained (fst concretized) state
                        |> snd
                    )
                 with e ->
                     Error e.Message)
            with
            | Error message -> Error message
            | Ok (Ok (VirtualImplementation.Found found)) -> Ok found.Definition.Name
            | Ok other -> failwith $"expected IDef.M to resolve to a body of C, got %A{other}"

        let atCreation = resolve loaded
        let loaded, _ = loaded.WithEmitScope loggerFactory name finalScope
        resolve loaded, atCreation

    [<RequireQualifiedAccess>]
    type private Reach =
        /// Created from 25 rows, with two bodies first in the two orders.
        | Reversed
        /// Below 25 rows at the end, with two bodies first in the two orders.
        | InOrder
        /// Created below 25 rows and resolved from 25, the two orders giving different bodies.
        | WindowRefused
        /// Created below 25 rows and resolved from 25, several rows, the two orders giving one body.
        | WindowAgreed
        /// An interface's rows, created below 25 rows and resolved from 25, the two orders giving different
        /// bodies, and no more specific interface: refused.
        | InterfaceRefused
        /// As `InterfaceRefused`, but a more specific interface supplies the body: it runs.
        | OverriddenInWindow

    [<Test>]
    let ``static virtual resolution over a dynamic module's rows agrees with real .NET's TypeBuilder`` () : unit =
        let property (cover : Reach -> unit) (schedule : Schedule) : unit =
            let hostAtCreation, hostAtEnd = hostAnswers schedule
            let atEnd, atCreation = pawPrintAnswers schedule

            // When `C` is created, PawPrint's module has the rows real .NET's had then.
            let show (answer : Result<string, string>) : string =
                match answer with
                | Ok body -> $"runs %s{body}"
                | Error message -> $"refused: %s{message}"

            show atCreation |> shouldEqual $"runs %s{hostAtCreation}"

            let forward = $"B%d{List.head schedule.Rows}"
            let backward = $"B%d{List.last schedule.Rows}"
            let window = schedule.AtCreation < 25 && schedule.Total >= 25

            if window && hostAtCreation <> hostAtEnd then
                // The two orders are real .NET's answers before and after the crossing.
                (hostAtCreation, hostAtEnd) |> shouldEqual (forward, backward)

                match atEnd with
                | Error message ->
                    message |> shouldContainText "TODO: static virtual dispatch"
                    cover Reach.WindowRefused

                    if schedule.Shape = Shape.OnInterface then
                        cover Reach.InterfaceRefused
                | Ok answer -> failwith $"expected a refusal in the window, got %s{answer} (real .NET: %s{hostAtEnd})"
            else
                show atEnd |> shouldEqual $"runs %s{hostAtEnd}"

                match schedule.Shape with
                | Shape.Overridden ->
                    hostAtEnd |> shouldEqual "B9"

                    if window && forward <> backward then
                        cover Reach.OverriddenInWindow
                | Shape.OnClass
                | Shape.OnInterface ->
                    if forward <> backward then
                        if schedule.Total >= 25 then
                            cover Reach.Reversed
                        else
                            cover Reach.InOrder

                    if window && schedule.Rows.Length > 1 then
                        cover Reach.WindowAgreed

        let gen =
            gen {
                let! shape = Gen.elements [ Shape.OnClass ; Shape.OnInterface ; Shape.Overridden ]
                let! length = Gen.choose (1, 4)
                let! rows = Gen.listOfLength length (Gen.choose (1, 3))
                // Half the schedules create the rows' type below 25 rows and end at 25 or more, near the crossing.
                let! window = Gen.elements [ true ; false ]

                let! before, between, after =
                    if window then
                        gen {
                            let! before = Gen.choose (0, 12)
                            let! between = Gen.choose (0, 12 - length)
                            let! beyond = Gen.choose (0, 4)
                            return before, between, 25 - (before + length + between) + beyond
                        }
                    else
                        gen {
                            let! before = Gen.choose (0, 20)
                            let! between = Gen.choose (0, 20)
                            let! after = Gen.frequency [ 1, Gen.constant 0 ; 1, Gen.choose (0, 30) ]
                            return before, between, after
                        }

                return
                    {
                        Shape = shape
                        Before = before
                        Rows = List.ofSeq rows
                        Between = between
                        After = after
                    }
            }

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 150)
                (Arb.fromGen gen)
                (fun cover schedule -> property cover schedule)

        coverage.Count Reach.Reversed |> shouldBeGreaterThan 5
        coverage.Count Reach.InOrder |> shouldBeGreaterThan 4
        coverage.Count Reach.WindowRefused |> shouldBeGreaterThan 15
        coverage.Count Reach.WindowAgreed |> shouldBeGreaterThan 6
        coverage.Count Reach.InterfaceRefused |> shouldBeGreaterThan 8
        coverage.Count Reach.OverriddenInWindow |> shouldBeGreaterThan 6
