namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Runtime.InteropServices
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.EmitScopeGen

/// `EmitScopeDefinitions.defineCustomAttribute` and `classifyCustomAttribute`, `HasCustomAttribute.tryOfToken`,
/// `EmitScope.addCustomAttribute` and `EmitScope.typeOfCustomAttributeOwner`: each against a model, and the classifier
/// and the rows against the host's own emitter, read raw through CoreLib's internal `MetadataImport`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeCustomAttributes =

    /// The two names `RegMeta::DefineCustomAttribute` treats specially after its known attributes.
    let private suppressName : string * string =
        "System.Security", "SuppressUnmanagedCodeSecurityAttribute"

    let private dynamicSecurityName : string * string =
        "System.Security", "DynamicSecurityMethodAttribute"

    let private ctorSignature : ImmutableArray<byte> =
        ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x01uy |]

    /// A scope with `Holder` (TypeDef 2), which has a method `M` (MethodDef 1) with a parameter and a generic parameter,
    /// a field, a property, an event and a generic parameter of its own; and an owner of each of the nine kinds.
    let private withOwners () : EmitScope * HasCustomAttribute list =
        let scope = emptyScope Guid.Empty

        let holder, scope =
            match EmitScopeDefinitions.defineTopLevelTypeDef "Holder" TypeAttributes.Public None [] scope with
            | TypeDefDefinition.Defined (rid, scope) -> rid, scope
            | TypeDefDefinition.Duplicate _ -> failwith "Holder is not yet defined"

        let method, scope =
            EmitScopeDefinitions.defineMethod
                holder
                "M"
                (ImmutableArray.Create<byte> [| 0x10uy ; 0x01uy ; 0x01uy ; 0x01uy ; 0x08uy |])
                (MethodAttributes.Public ||| MethodAttributes.Static)
                scope

        let param, scope =
            match EmitScopeDefinitions.defineParam method 1 "x" 0 scope with
            | ParamDefinition.Defined (rid, scope) -> rid, scope
            | ParamDefinition.StrandsLaterMethods _ -> failwith "one parameter strands nothing"

        let field, scope =
            EmitScopeDefinitions.defineField
                holder
                "F"
                (ImmutableArray.Create<byte> [| 0x06uy ; 0x08uy |])
                FieldAttributes.Public
                scope

        let property, scope =
            EmitScopeDefinitions.defineProperty
                holder
                "P"
                (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                0
                scope

        let event, scope =
            EmitScopeDefinitions.defineEvent holder "E" (TypeDefOrRef.TypeDef holder) 0 scope

        let typeParameter, scope =
            EmitScopeDefinitions.defineGenericParam (TypeOrMethodDef.TypeDef holder) 0us 0 "T" [] scope

        let methodParameter, scope =
            EmitScopeDefinitions.defineGenericParam (TypeOrMethodDef.MethodDef method) 0us 0 "U" [] scope

        scope,
        [
            HasCustomAttribute.Assembly
            HasCustomAttribute.Module
            HasCustomAttribute.TypeDef holder
            HasCustomAttribute.MethodDef method
            HasCustomAttribute.Field field
            HasCustomAttribute.Param param
            HasCustomAttribute.Property property
            HasCustomAttribute.Event event
            HasCustomAttribute.GenericParam typeParameter
            HasCustomAttribute.GenericParam methodParameter
        ]

    /// Where a constructor's declaring type is named, as the emitter reads it.
    [<RequireQualifiedAccess>]
    type private Parent =
        /// A MemberRef on a TypeRef whose resolution scope is an AssemblyRef.
        | ReferenceInAssembly of ns : string * name : string
        /// A MemberRef on a TypeRef whose resolution scope is the module.
        | ReferenceInModule of ns : string * name : string
        /// A MemberRef on a TypeRef nested in another TypeRef.
        | NestedReference of ns : string * name : string
        /// A MemberRef on a top-level TypeDef.
        | MemberOfDefinition of ns : string * name : string
        /// A MethodDef of a top-level TypeDef.
        | MethodOfDefinition of ns : string * name : string
        /// A MethodDef of a TypeDef whose visibility is nested.
        | MethodOfNestedDefinition of ns : string * name : string
        /// A MemberRef on a TypeSpec, which has no name.
        | MemberOfSpecification
        /// A MemberRef on a MethodDef, a vararg call site's, which has no name.
        | MemberOfMethod

    let private typeDef (ns : string) (name : string) (flags : TypeAttributes) (scope : EmitScope) =
        let name, scope = EmitScope.internString name scope
        let ns, scope = EmitScope.internString ns scope

        EmitScope.addTypeDef
            {
                Flags = flags
                Name = name
                Namespace = ns
                Extends = None
            }
            scope

    let private typeRef (resolution : ResolutionScope) (ns : string) (name : string) (scope : EmitScope) =
        let name, scope = EmitScope.internString name scope
        let ns, scope = EmitScope.internString ns scope

        EmitScope.addTypeRef
            {
                ResolutionScope = resolution
                Name = name
                Namespace = ns
            }
            scope

    let private memberRef (parent : MemberRefParent) (scope : EmitScope) : MethodDefOrRef * EmitScope =
        let name, scope = EmitScope.internString ".ctor" scope
        let signature, scope = EmitScope.internBlob ctorSignature scope

        let rid, scope =
            EmitScope.addMemberRef
                {
                    Parent = parent
                    Name = name
                    Signature = signature
                }
                scope

        MethodDefOrRef.MemberRef rid, scope

    let private assemblyRef (scope : EmitScope) : AssemblyRefRid * EmitScope =
        let name, scope = EmitScope.internString "Other" scope

        EmitScope.addAssemblyRef
            {
                Version =
                    {
                        Major = 1us
                        Minor = 0us
                        Build = 0us
                        Revision = 0us
                    }
                Flags = enum<AssemblyFlags> 0
                PublicKeyOrToken = BlobOffset 0
                Name = name
                Culture = StringOffset 0
                HashValue = BlobOffset 0
            }
            scope

    /// `scope` with a constructor whose declaring type is named as `parent` says.
    let private constructorOf (parent : Parent) (scope : EmitScope) : MethodDefOrRef * EmitScope =
        match parent with
        | Parent.ReferenceInAssembly (ns, name) ->
            let other, scope = assemblyRef scope
            let target, scope = typeRef (ResolutionScope.AssemblyRef other) ns name scope
            memberRef (MemberRefParent.TypeRef target) scope
        | Parent.ReferenceInModule (ns, name) ->
            let target, scope = typeRef ResolutionScope.Module ns name scope
            memberRef (MemberRefParent.TypeRef target) scope
        | Parent.NestedReference (ns, name) ->
            let other, scope = assemblyRef scope
            let outer, scope = typeRef (ResolutionScope.AssemblyRef other) "" "Outer" scope
            let target, scope = typeRef (ResolutionScope.TypeRef outer) ns name scope
            memberRef (MemberRefParent.TypeRef target) scope
        | Parent.MemberOfDefinition (ns, name) ->
            let target, scope = typeDef ns name TypeAttributes.Public scope
            memberRef (MemberRefParent.TypeDef target) scope
        | Parent.MethodOfDefinition (ns, name) ->
            let target, scope = typeDef ns name TypeAttributes.Public scope

            let method, scope =
                EmitScopeDefinitions.defineMethod target ".ctor" ctorSignature MethodAttributes.Public scope

            MethodDefOrRef.MethodDef method, scope
        | Parent.MethodOfNestedDefinition (ns, name) ->
            let target, scope = typeDef ns name TypeAttributes.NestedPublic scope

            let method, scope =
                EmitScopeDefinitions.defineMethod target ".ctor" ctorSignature MethodAttributes.Public scope

            MethodDefOrRef.MethodDef method, scope
        | Parent.MemberOfSpecification ->
            let signature, scope =
                EmitScope.internBlob
                    (ImmutableArray.Create<byte> [| 0x15uy ; 0x12uy ; 0x08uy ; 0x01uy ; 0x08uy |])
                    scope

            let spec, scope =
                EmitScope.addTypeSpec
                    {
                        Signature = signature
                    }
                    scope

            memberRef (MemberRefParent.TypeSpec spec) scope
        | Parent.MemberOfMethod -> memberRef (MemberRefParent.MethodDef (EmitScope.tryMethodDefRid 1 scope).Value) scope

    /// What the emitter does with an attribute named `(ns, name)` on `owner`, whatever parent names it.
    let private expectedKind (ns : string, name : string) (owner : HasCustomAttribute) : CustomAttributeEmitKind =
        match
            KnownCustomAttribute.all
            |> List.tryFind (fun k -> KnownCustomAttribute.name k = (ns, name))
        with
        | Some known -> CustomAttributeEmitKind.Known known
        | None ->
            match owner with
            | HasCustomAttribute.TypeDef _
            | HasCustomAttribute.MethodDef _ when (ns, name) = suppressName ->
                CustomAttributeEmitKind.SuppressUnmanagedCodeSecurity
            | HasCustomAttribute.MethodDef _ when (ns, name) = dynamicSecurityName ->
                CustomAttributeEmitKind.DynamicSecurityMethod
            | _ -> CustomAttributeEmitKind.Ordinary

    [<Test>]
    let ``KnownCaList has twenty names, each once`` () : unit =
        KnownCustomAttribute.all |> List.length |> shouldEqual 20
        KnownCustomAttribute.all |> List.distinct |> List.length |> shouldEqual 20

        KnownCustomAttribute.all
        |> List.map KnownCustomAttribute.name
        |> List.distinct
        |> List.length
        |> shouldEqual 20

        KnownCustomAttribute.name KnownCustomAttribute.Serializable
        |> shouldEqual ("System", "SerializableAttribute")

        KnownCustomAttribute.name KnownCustomAttribute.WindowsRuntimeImport
        |> shouldEqual ("System.Runtime.InteropServices.WindowsRuntime", "WindowsRuntimeImportAttribute")

    [<Test>]
    let ``the classifier names each known attribute and security name, however its type is reached`` () : unit =
        let names =
            (KnownCustomAttribute.all |> List.map KnownCustomAttribute.name)
            @ [ suppressName ; dynamicSecurityName ]

        let shapes : ((string * string) -> Parent) list =
            [
                Parent.ReferenceInAssembly
                Parent.ReferenceInModule
                Parent.NestedReference
                Parent.MemberOfDefinition
                Parent.MethodOfDefinition
                Parent.MethodOfNestedDefinition
            ]

        for name in names do
            for shape in shapes do
                let scope, owners = withOwners ()
                let constructor, scope = constructorOf (shape name) scope

                for owner in owners do
                    EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                    |> shouldEqual (expectedKind name owner)

    [<Test>]
    let ``the classifier finds nothing in a near miss or a parent with no name`` () : unit =
        let names =
            (KnownCustomAttribute.all |> List.map KnownCustomAttribute.name)
            @ [ suppressName ; dynamicSecurityName ]

        let nearMisses (ns : string, name : string) : (string * string) list =
            [
                ns + ".Other", name
                ns, name.ToLowerInvariant ()
                ns, name.ToUpperInvariant ()
                ns, name + "2"
                "", ns + "." + name
                "", name
            ]

        for name in names do
            for miss in nearMisses name do
                for shape in [ Parent.ReferenceInAssembly ; Parent.MethodOfDefinition ] do
                    let scope, owners = withOwners ()
                    let constructor, scope = constructorOf (shape miss) scope

                    for owner in owners do
                        EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                        |> shouldEqual CustomAttributeEmitKind.Ordinary

        for shape in [ Parent.MemberOfSpecification ; Parent.MemberOfMethod ] do
            let scope, owners = withOwners ()
            let constructor, scope = constructorOf shape scope

            for owner in owners do
                EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                |> shouldEqual CustomAttributeEmitKind.Ordinary

    /// `scope` with constructors whose declaring types have known names, a security name, and an ordinary name.
    let private withNamedConstructors (scope : EmitScope) : EmitScope =
        [
            KnownCustomAttribute.name KnownCustomAttribute.Serializable
            KnownCustomAttribute.name KnownCustomAttribute.In
            suppressName
            dynamicSecurityName
            "System", "ObsoleteAttribute"
        ]
        |> List.fold (fun scope name -> constructorOf (Parent.ReferenceInModule name) scope |> snd) scope

    /// What one generated call to `defineCustomAttribute` reached.
    [<RequireQualifiedAccess>]
    type private DefineReach =
        | Defined
        | NotStored
        | EqualPair
        | EmptyBlob
        | Owner of string

    let private ownerKind (owner : HasCustomAttribute) : string =
        match owner with
        | HasCustomAttribute.Assembly -> "assembly"
        | HasCustomAttribute.Module -> "module"
        | HasCustomAttribute.TypeDef _ -> "type"
        | HasCustomAttribute.MethodDef _ -> "method"
        | HasCustomAttribute.Field _ -> "field"
        | HasCustomAttribute.Param _ -> "parameter"
        | HasCustomAttribute.Property _ -> "property"
        | HasCustomAttribute.Event _ -> "event"
        | HasCustomAttribute.GenericParam _ -> "generic parameter"

    let private ownerKinds : string list =
        [
            "assembly"
            "module"
            "type"
            "method"
            "field"
            "parameter"
            "property"
            "event"
            "generic parameter"
        ]

    [<Test>]
    let ``an attribute stored as given is a new row at the next RID, and any other adds nothing`` () : unit =
        let property (cover : DefineReach -> unit) (scope : EmitScope) (calls : (int * int * int * bool) list) : unit =
            let scope = withNamedConstructors scope
            let owners = customAttributeOwners scope
            let constructors = customAttributeConstructors scope
            let mutable scope = scope

            let mutable previous : (HasCustomAttribute * MethodDefOrRef * ImmutableArray<byte>) option =
                None

            for ownerChoice, constructorChoice, blobChoice, repeat in calls do
                let owner, constructor, value =
                    match previous with
                    | Some call when repeat -> call
                    | _ ->
                        (pick ownerChoice owners).Value,
                        (pick constructorChoice constructors).Value,
                        (pick blobChoice customAttributeBlobs).Value

                let kind = EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                let before = EmitScope.customAttributes scope
                cover (DefineReach.Owner (ownerKind owner))

                match EmitScopeDefinitions.defineCustomAttribute owner constructor value scope with
                | CustomAttributeDefinition.Defined (rid, after) ->
                    kind |> shouldEqual CustomAttributeEmitKind.Ordinary
                    rid.Rid |> shouldEqual (before.Count + 1)
                    let rows = EmitScope.customAttributes after
                    List.ofSeq (rows.RemoveAt before.Count) |> shouldEqual (List.ofSeq before)
                    let row = rows.[before.Count]
                    row.Parent |> shouldEqual owner
                    row.Constructor |> shouldEqual constructor
                    List.ofSeq (EmitScope.blobAt row.Value after) |> shouldEqual (List.ofSeq value)
                    // Nothing else changes but the blob heap, which may gain the blob.
                    EmitScopeExtension.violations ImmutableHashSet.Empty scope after
                    |> shouldEqual []

                    (EmitScope.typeDefs after).Count |> shouldEqual (EmitScope.typeDefs scope).Count
                    cover DefineReach.Defined

                    if value.IsEmpty then
                        cover DefineReach.EmptyBlob

                    if previous = Some (owner, constructor, value) && repeat then
                        cover DefineReach.EqualPair

                    scope <- after
                | CustomAttributeDefinition.NotStoredAsGiven reported ->
                    reported |> shouldEqual kind
                    kind |> shouldNotEqual CustomAttributeEmitKind.Ordinary
                    cover DefineReach.NotStored

                previous <- Some (owner, constructor, value)

        let genCalls =
            Gen.listOf (
                Gen.zip (Gen.zip3 choice choice choice) (Gen.frequency [ 4, Gen.constant false ; 1, Gen.constant true ])
            )
            |> Gen.map (List.ofSeq >> List.map (fun ((o, c, b), r) -> o, c, b, r))

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 300)
                (Arb.fromGen (Gen.zip genScope genCalls))
                (fun cover (scope, calls) -> property cover scope calls)

        coverage.Count DefineReach.Defined |> shouldBeGreaterThan 500
        coverage.Count DefineReach.NotStored |> shouldBeGreaterThan 50
        coverage.Count DefineReach.EqualPair |> shouldBeGreaterThan 30
        coverage.Count DefineReach.EmptyBlob |> shouldBeGreaterThan 30

        for kind in ownerKinds do
            coverage.Count (DefineReach.Owner kind) |> shouldBeGreaterThan 20

    [<Test>]
    let ``a token names an owner exactly when it is a row of one of the nine tables`` () : unit =
        let property (scope : EmitScope) : unit =
            let tables : (int * int * (int -> HasCustomAttribute)) list =
                [
                    0x02, (EmitScope.typeDefs scope).Count, (fun i -> HasCustomAttribute.TypeDef (TypeDefRid i))
                    0x06, (EmitScope.methodDefs scope).Count, (fun i -> HasCustomAttribute.MethodDef (MethodDefRid i))
                    0x04, (EmitScope.fields scope).Count, (fun i -> HasCustomAttribute.Field (FieldRid i))
                    0x08, (EmitScope.parameters scope).Count, (fun i -> HasCustomAttribute.Param (ParamRid i))
                    0x17, (EmitScope.properties scope).Count, (fun i -> HasCustomAttribute.Property (PropertyRid i))
                    0x14, (EmitScope.events scope).Count, (fun i -> HasCustomAttribute.Event (EventRid i))
                    0x2A,
                    (EmitScope.genericParams scope).Count,
                    (fun i -> HasCustomAttribute.GenericParam (GenericParamRid i))
                ]

            for table, count, owner in tables do
                for i in 1..count do
                    HasCustomAttribute.tryOfToken ((table <<< 24) ||| i) scope
                    |> shouldEqual (Some (owner i))

                HasCustomAttribute.tryOfToken ((table <<< 24) ||| (count + 1)) scope
                |> shouldEqual None

                HasCustomAttribute.tryOfToken (table <<< 24) scope |> shouldEqual None

            HasCustomAttribute.tryOfToken 0x00000001 scope
            |> shouldEqual (Some HasCustomAttribute.Module)

            HasCustomAttribute.tryOfToken 0x00000002 scope |> shouldEqual None
            HasCustomAttribute.tryOfToken 0x00000000 scope |> shouldEqual None

            HasCustomAttribute.tryOfToken 0x20000001 scope
            |> shouldEqual (Some HasCustomAttribute.Assembly)

            HasCustomAttribute.tryOfToken 0x20000002 scope |> shouldEqual None

            // Every other table, at a RID its own table may have.
            for table in
                [
                    0x01
                    0x09
                    0x0A
                    0x0C
                    0x0E
                    0x11
                    0x1A
                    0x1B
                    0x23
                    0x26
                    0x27
                    0x28
                    0x2B
                    0x2C
                    0x70
                ] do
                for i in 1..3 do
                    HasCustomAttribute.tryOfToken ((table <<< 24) ||| i) scope |> shouldEqual None

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 100, Prop.forAll (Arb.fromGen genScope) property)

        // A scope with no Assembly row has no assembly to own an attribute.
        HasCustomAttribute.tryOfToken 0x20000001 (EmitScope.create "M" Guid.Empty)
        |> shouldEqual None

    [<Test>]
    let ``a row naming an owner, a constructor or a blob the scope lacks is refused`` () : unit =
        let scope, owners = withOwners ()
        let constructor = MethodDefOrRef.MethodDef (EmitScope.tryMethodDefRid 1 scope).Value

        let row (owner : HasCustomAttribute) (constructor : MethodDefOrRef) (value : BlobOffset) : CustomAttributeRow =
            {
                Parent = owner
                Constructor = constructor
                Value = value
            }

        for owner in owners do
            EmitScope.addCustomAttribute (row owner constructor (BlobOffset 0)) scope
            |> fst
            |> _.Rid
            |> shouldEqual 1

        let absent =
            [
                HasCustomAttribute.TypeDef (TypeDefRid 3)
                HasCustomAttribute.MethodDef (MethodDefRid 2)
                HasCustomAttribute.Field (FieldRid 2)
                HasCustomAttribute.Param (ParamRid 2)
                HasCustomAttribute.Property (PropertyRid 2)
                HasCustomAttribute.Event (EventRid 2)
                HasCustomAttribute.GenericParam (GenericParamRid 3)
            ]

        for owner in absent do
            Assert.Throws<ArgumentException> (fun () ->
                EmitScope.addCustomAttribute (row owner constructor (BlobOffset 0)) scope
                |> ignore<CustomAttributeRid * EmitScope>
            )
            |> ignore<ArgumentException>

        Assert.Throws<ArgumentException> (fun () ->
            EmitScope.addCustomAttribute
                (row HasCustomAttribute.Assembly constructor (BlobOffset 0))
                (EmitScope.create "M" Guid.Empty)
            |> ignore<CustomAttributeRid * EmitScope>
        )
        |> ignore<ArgumentException>

        for constructor in
            [
                MethodDefOrRef.MethodDef (MethodDefRid 2)
                MethodDefOrRef.MemberRef (MemberRefRid 1)
            ] do
            Assert.Throws<ArgumentException> (fun () ->
                EmitScope.addCustomAttribute (row HasCustomAttribute.Module constructor (BlobOffset 0)) scope
                |> ignore<CustomAttributeRid * EmitScope>
            )
            |> ignore<ArgumentException>

        Assert.Throws<ArgumentException> (fun () ->
            EmitScope.addCustomAttribute (row HasCustomAttribute.Module constructor (BlobOffset 99999)) scope
            |> ignore<CustomAttributeRid * EmitScope>
        )
        |> ignore<ArgumentException>

    [<Test>]
    let ``an owner's type is found by walking the rows it belongs to`` () : unit =
        let model (scope : EmitScope) (owner : HasCustomAttribute) : TypeDefRid option =
            let methodOwner (method : MethodDefRid) =
                (EmitScope.methodDefs scope).[method.Rid - 1].Owner

            match owner with
            | HasCustomAttribute.Assembly
            | HasCustomAttribute.Module -> None
            | HasCustomAttribute.TypeDef t -> Some t
            | HasCustomAttribute.MethodDef m -> Some (methodOwner m)
            | HasCustomAttribute.Field f -> Some (EmitScope.fields scope).[f.Rid - 1].Owner
            | HasCustomAttribute.Param p -> Some (methodOwner (EmitScope.parameters scope).[p.Rid - 1].Owner)
            | HasCustomAttribute.Property p -> Some (EmitScope.properties scope).[p.Rid - 1].Owner
            | HasCustomAttribute.Event e -> Some (EmitScope.events scope).[e.Rid - 1].Owner
            | HasCustomAttribute.GenericParam g ->
                match (EmitScope.genericParams scope).[g.Rid - 1].Owner with
                | TypeOrMethodDef.TypeDef t -> Some t
                | TypeOrMethodDef.MethodDef m -> Some (methodOwner m)

        let property (cover : string -> unit) (scope : EmitScope) : unit =
            for owner in customAttributeOwners scope do
                EmitScope.typeOfCustomAttributeOwner owner scope
                |> shouldEqual (model scope owner)

                match owner with
                | HasCustomAttribute.Param p ->
                    let method = (EmitScope.parameters scope).[p.Rid - 1].Owner

                    // A parameter whose method's RID is a type's RID too, so that a walk that stops at the method
                    // gives a type, and a wrong one.
                    if method.Rid <> (EmitScope.methodDefs scope).[method.Rid - 1].Owner.Rid then
                        cover "parameter"
                | HasCustomAttribute.GenericParam g ->
                    match (EmitScope.genericParams scope).[g.Rid - 1].Owner with
                    | TypeOrMethodDef.MethodDef m when m.Rid <> (EmitScope.methodDefs scope).[m.Rid - 1].Owner.Rid ->
                        cover "method's generic parameter"
                    | _ -> ()
                | _ -> ()

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 200) (Arb.fromGen genScope) property

        coverage.Count "parameter" |> shouldBeGreaterThan 100
        coverage.Count "method's generic parameter" |> shouldBeGreaterThan 50

    // ---- The host's emitter, read raw through CoreLib's internal `MetadataImport`. ----

    let private coreLib : Assembly = typeof<obj>.Assembly

    let private requireType (name : string) : Type =
        match coreLib.GetType name with
        | null -> failwith $"CoreLib has no internal %s{name}, through which this test reads the host's rows"
        | t -> t

    let private instance : BindingFlags =
        BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

    let private requireMember (found : 'a) (what : string) : 'a =
        if isNull (box found) then
            failwith $"CoreLib has no internal %s{what}, through which this test reads the host's rows"

        found

    /// Calls CoreLib's internal `MetadataImport.EnumCustomAttributes` on a `RuntimeModule` and returns the tokens.
    let private enumerateCustomAttributes : Lazy<Func<Module, int, int[]>> =
        lazy
            let runtimeModule = requireType "System.Reflection.RuntimeModule"
            let metadataImport = requireType "System.Reflection.MetadataImport"
            let enumResult = requireType "System.Reflection.MetadataEnumResult"

            let getImport =
                (requireMember (runtimeModule.GetProperty ("MetadataImport", instance)) "RuntimeModule.MetadataImport")
                    .GetMethod

            let enumerate =
                requireMember (metadataImport.GetMethod ("EnumCustomAttributes", instance)) "EnumCustomAttributes"

            let getLength =
                (requireMember (enumResult.GetProperty ("Length", instance)) "MetadataEnumResult.Length").GetMethod

            let getItem =
                (requireMember (enumResult.GetProperty ("Item", instance)) "MetadataEnumResult.Item").GetMethod

            let dm =
                DynamicMethod (
                    "EnumCustomAttributes",
                    typeof<int[]>,
                    [| typeof<Module> ; typeof<int> |],
                    typeof<obj>.Module,
                    true
                )

            let il = dm.GetILGenerator ()
            let import = il.DeclareLocal metadataImport
            let result = il.DeclareLocal enumResult
            let array = il.DeclareLocal typeof<int[]>
            let index = il.DeclareLocal typeof<int>
            il.Emit (OpCodes.Ldarg_0)
            il.Emit (OpCodes.Castclass, runtimeModule)
            il.Emit (OpCodes.Call, getImport)
            il.Emit (OpCodes.Stloc, import)
            il.Emit (OpCodes.Ldloca, import)
            il.Emit (OpCodes.Ldarg_1)
            il.Emit (OpCodes.Ldloca, result)
            il.Emit (OpCodes.Call, enumerate)
            il.Emit (OpCodes.Ldloca, result)
            il.Emit (OpCodes.Call, getLength)
            il.Emit (OpCodes.Newarr, typeof<int>)
            il.Emit (OpCodes.Stloc, array)
            il.Emit (OpCodes.Ldc_I4_0)
            il.Emit (OpCodes.Stloc, index)
            let top = il.DefineLabel ()
            let test = il.DefineLabel ()
            il.Emit (OpCodes.Br, test)
            il.MarkLabel top
            il.Emit (OpCodes.Ldloc, array)
            il.Emit (OpCodes.Ldloc, index)
            il.Emit (OpCodes.Ldloca, result)
            il.Emit (OpCodes.Ldloc, index)
            il.Emit (OpCodes.Call, getItem)
            il.Emit (OpCodes.Stelem_I4)
            il.Emit (OpCodes.Ldloc, index)
            il.Emit (OpCodes.Ldc_I4_1)
            il.Emit (OpCodes.Add)
            il.Emit (OpCodes.Stloc, index)
            il.MarkLabel test
            il.Emit (OpCodes.Ldloc, index)
            il.Emit (OpCodes.Ldloc, array)
            il.Emit (OpCodes.Ldlen)
            il.Emit (OpCodes.Conv_I4)
            il.Emit (OpCodes.Blt, top)
            il.Emit (OpCodes.Ldloc, array)
            il.Emit (OpCodes.Ret)
            dm.CreateDelegate typeof<Func<Module, int, int[]>> :?> Func<Module, int, int[]>

    /// The rows the host's emitter holds for `owner` in `moduleBuilder`, in its enumeration order: each row's token,
    /// its constructor's token and its blob.
    let private hostRows (moduleBuilder : ModuleBuilder) (owner : int) : (int * int * byte list) list =
        let runtimeModule = requireType "System.Reflection.RuntimeModule"
        let metadataImport = requireType "System.Reflection.MetadataImport"
        let constArray = requireType "System.Reflection.ConstArray"
        let runtimeModuleBuilder = requireType "System.Reflection.Emit.RuntimeModuleBuilder"

        let internalModule =
            (requireMember (runtimeModuleBuilder.GetProperty ("InternalModule", instance)) "InternalModule").GetValue
                moduleBuilder

        let import =
            (requireMember (runtimeModule.GetProperty ("MetadataImport", instance)) "RuntimeModule.MetadataImport")
                .GetValue
                internalModule

        // `MetadataEnumResult` is a ref struct, which reflection cannot box, so a skip-visibility `DynamicMethod`
        // calls `EnumCustomAttributes` and copies the result into an array.
        let rows : int[] =
            enumerateCustomAttributes.Value.Invoke (internalModule :?> Module, owner)

        let props =
            requireMember (metadataImport.GetMethod ("GetCustomAttributeProps", instance)) "GetCustomAttributeProps"

        [
            for row in rows do
                let propsArgs = [| box row ; null ; null |]
                props.Invoke (import, propsArgs) |> ignore<obj>
                let blob = propsArgs.[2]

                let blobLength =
                    constArray.GetProperty("Length", instance).GetValue blob |> unbox<int>

                let pointer =
                    constArray.GetProperty("Signature", instance).GetValue blob |> unbox<nativeint>

                let bytes = Array.zeroCreate<byte> blobLength

                if blobLength > 0 then
                    Marshal.Copy (pointer, bytes, 0, blobLength)

                yield row, unbox<int> propsArgs.[1], List.ofArray bytes
        ]

    let private noArguments : byte[] = [| 1uy ; 0uy ; 0uy ; 0uy |]

    let private assemblyCount : int ref = ref 0

    let private newModule (name : string) : AssemblyBuilder * ModuleBuilder =
        let n = Threading.Interlocked.Increment assemblyCount

        let assembly =
            AssemblyBuilder.DefineDynamicAssembly (AssemblyName $"%s{name}%d{n}", AssemblyBuilderAccess.RunAndCollect)

        assembly, assembly.DefineDynamicModule name

    /// One constructor of the classifier's differential pool, and where the host's emitter finds its type's name.
    type private PoolEntry =
        {
            Label : string
            Constructor : ConstructorInfo
            Parent : Parent
        }

    /// The pool: CoreLib's known attributes and some ordinary ones, through MemberRefs; types `module` defines with
    /// each known and security name and some near misses, through their `ConstructorBuilder`s and their created
    /// constructors; the same names nested in a type of the module, and in a second dynamic assembly, top level and
    /// nested; and a generic type of a second dynamic assembly with a known name and no arity suffix, instantiated.
    let private classifierPool (moduleBuilder : ModuleBuilder) : PoolEntry list =
        let names =
            (KnownCustomAttribute.all |> List.map KnownCustomAttribute.name)
            @ [
                suppressName
                dynamicSecurityName
                "System", "Serializableattribute"
                "", "SerializableAttribute"
                "System.Security", "DynamicSecurityMethodAttribute2"
            ]

        let full (ns : string, name : string) =
            if ns = "" then name else ns + "." + name

        let coreLibTypes =
            [
                typeof<SerializableAttribute>
                typeof<NonSerializedAttribute>
                typeof<ComImportAttribute>
                typeof<PreserveSigAttribute>
                typeof<InAttribute>
                typeof<OutAttribute>
                typeof<OptionalAttribute>
                typeof<System.Runtime.CompilerServices.SpecialNameAttribute>
                typeof<System.Security.AllowPartiallyTrustedCallersAttribute>
                typeof<GuidAttribute>
                typeof<InterfaceTypeAttribute>
                typeof<ClassInterfaceAttribute>
                typeof<System.Runtime.CompilerServices.MethodImplAttribute>
                typeof<MarshalAsAttribute>
                typeof<StructLayoutAttribute>
                typeof<FieldOffsetAttribute>
                typeof<TypeLibVersionAttribute>
                typeof<ComCompatibleVersionAttribute>
                typeof<DllImportAttribute>
                typeof<System.Security.SuppressUnmanagedCodeSecurityAttribute>
                typeof<ObsoleteAttribute>
                typeof<FlagsAttribute>
                typeof<ThreadStaticAttribute>
                typeof<CLSCompliantAttribute>
            ]

        let attributeType (owner : ModuleBuilder) (fullName : string) : TypeBuilder * ConstructorBuilder =
            let t = owner.DefineType (fullName, TypeAttributes.Public, typeof<Attribute>)
            t, t.DefineDefaultConstructor MethodAttributes.Public

        let ownDefinitions =
            names |> List.map (fun name -> name, attributeType moduleBuilder (full name))

        // Each nested type has an encloser of its own: reflection keys a nested type by its encloser and the part of
        // its name after the last dot, which two of the names share.
        let ownNested =
            names
            |> List.mapi (fun i name ->
                let outer = moduleBuilder.DefineType ($"Outer%d{i}", TypeAttributes.Public)

                let t =
                    outer.DefineNestedType (full name, TypeAttributes.NestedPublic, typeof<Attribute>)

                name, outer, t, t.DefineDefaultConstructor MethodAttributes.Public
            )

        let _, other = newModule "ClassifierOther"

        let otherTop =
            names
            |> List.map (fun name ->
                let t, _ = attributeType other (full name)
                name, t.CreateType().GetConstructor Type.EmptyTypes
            )

        let otherNested =
            names
            |> List.mapi (fun i name ->
                let outer = other.DefineType ($"Outer%d{i}", TypeAttributes.Public)

                let t =
                    outer.DefineNestedType (full name, TypeAttributes.NestedPublic, typeof<Attribute>)

                t.DefineDefaultConstructor MethodAttributes.Public |> ignore<ConstructorBuilder>
                outer.CreateType () |> ignore<Type>
                name, t.CreateType().GetConstructor Type.EmptyTypes
            )

        let generic =
            let _, third = newModule "ClassifierGeneric"

            let t =
                third.DefineType ("System.SerializableAttribute", TypeAttributes.Public, typeof<Attribute>)

            t.DefineGenericParameters "T" |> ignore<GenericTypeParameterBuilder[]>
            t.DefineDefaultConstructor MethodAttributes.Public |> ignore<ConstructorBuilder>
            t.CreateType().MakeGenericType(typeof<int>).GetConstructor Type.EmptyTypes

        let ownCreated =
            ownDefinitions
            |> List.map (fun (name, (t, _)) -> name, t.CreateType().GetConstructor Type.EmptyTypes)

        let ownNestedCreated =
            ownNested
            |> List.map (fun (name, outer, t, c) ->
                outer.CreateType () |> ignore<Type>
                t.CreateType () |> ignore<Type>
                name, (), c
            )

        [
            for t in coreLibTypes do
                yield
                    {
                        Label = $"CoreLib %s{t.FullName}"
                        Constructor = t.GetConstructors().[0]
                        Parent = Parent.ReferenceInAssembly (t.Namespace, t.Name)
                    }
            for (ns, name), (_, builder) in ownDefinitions do
                yield
                    {
                        Label = $"module's builder %s{ns}.%s{name}"
                        Constructor = builder
                        Parent = Parent.MethodOfDefinition (ns, name)
                    }
            for (ns, name), created in ownCreated do
                yield
                    {
                        Label = $"module's created %s{ns}.%s{name}"
                        Constructor = created
                        Parent = Parent.MethodOfDefinition (ns, name)
                    }
            for (ns, name), (), builder in ownNestedCreated do
                yield
                    {
                        Label = $"module's nested %s{ns}.%s{name}"
                        Constructor = builder
                        Parent = Parent.MethodOfNestedDefinition (ns, name)
                    }
            for (ns, name), created in otherTop do
                yield
                    {
                        Label = $"other assembly's %s{ns}.%s{name}"
                        Constructor = created
                        Parent = Parent.ReferenceInAssembly (ns, name)
                    }
            for (ns, name), created in otherNested do
                yield
                    {
                        Label = $"other assembly's nested %s{ns}.%s{name}"
                        Constructor = created
                        Parent = Parent.NestedReference (ns, name)
                    }
            yield
                {
                    Label = "other assembly's generic, instantiated"
                    Constructor = generic
                    Parent = Parent.MemberOfSpecification
                }
        ]

    /// What the host's emitter did with one attribute on a fresh owner.
    type private HostOutcome =
        {
            Threw : bool
            Rows : int
            HasSecurity : bool
            RequireSecObject : bool
        }

    [<Test>]
    let ``the classifier's answer is what the host's emitter does`` () : unit =
        let _, moduleBuilder = newModule "Classifier"
        let pool = classifierPool moduleBuilder
        let plain = typeof<ObsoleteAttribute>.GetConstructor Type.EmptyTypes
        let mutable count = 0
        let mutable reached = Set.empty

        for entry in pool do
            for ownerKind in [ "type" ; "method" ; "field" ] do
                count <- count + 1
                // `RegMeta::DefineCustomAttribute` compares a name it fills only for a TypeRef or TypeDef parent, so
                // an attribute named through a TypeSpec straight after a security name meets that name; an ordinary
                // attribute first leaves an ordinary name in its buffer.
                let buffer = moduleBuilder.DefineType ($"Buffer%d{count}", TypeAttributes.Public)
                buffer.SetCustomAttribute (plain, noArguments)

                let holder = moduleBuilder.DefineType ($"H%d{count}", TypeAttributes.Public)

                let method =
                    holder.DefineMethod ("M", MethodAttributes.Public ||| MethodAttributes.Static, typeof<Void>, [||])

                method.GetILGenerator().Emit OpCodes.Ret
                let field = holder.DefineField ("F", typeof<int>, FieldAttributes.Public)

                let ownerToken, apply =
                    match ownerKind with
                    | "type" ->
                        holder.MetadataToken, (fun () -> holder.SetCustomAttribute (entry.Constructor, noArguments))
                    | "method" ->
                        method.MetadataToken, (fun () -> method.SetCustomAttribute (entry.Constructor, noArguments))
                    | _ -> field.MetadataToken, (fun () -> field.SetCustomAttribute (entry.Constructor, noArguments))

                let threw =
                    try
                        apply ()
                        false
                    with :? ArgumentException ->
                        true

                let rows = hostRows moduleBuilder ownerToken |> List.length

                // The mirror: the same owner kind, and a constructor named as the host's emitter names it.
                let scope, owners = withOwners ()
                let constructor, scope = constructorOf entry.Parent scope

                let owner =
                    match ownerKind with
                    | "type" -> owners.[2]
                    | "method" -> owners.[3]
                    | _ -> owners.[4]

                let kind = EmitScopeDefinitions.classifyCustomAttribute owner constructor scope
                reached <- reached.Add (sprintf "%A" kind |> fun s -> s.Split(' ').[0])

                let flags () : HostOutcome =
                    let created = holder.CreateType ()

                    {
                        Threw = threw
                        Rows = rows
                        HasSecurity =
                            match ownerKind with
                            | "type" -> created.Attributes.HasFlag TypeAttributes.HasSecurity
                            | "method" -> (created.GetMethod "M").Attributes.HasFlag MethodAttributes.HasSecurity
                            | _ -> false
                        RequireSecObject =
                            ownerKind = "method"
                            && (created.GetMethod "M").Attributes.HasFlag MethodAttributes.RequireSecObject
                    }

                let describe = $"%s{entry.Label} on a %s{ownerKind}: classified %A{kind}"

                match kind with
                | CustomAttributeEmitKind.Known known ->
                    // The six attributes `KnownCaList` marks `bKEEPCA` are validated and then stored as rows, which a
                    // well-formed blob on a right target makes indistinguishable from an ordinary row here; the
                    // fixed table pins their names. Every other known attribute writes no row.
                    let kept =
                        [
                            KnownCustomAttribute.Guid
                            KnownCustomAttribute.InterfaceType
                            KnownCustomAttribute.ClassInterface
                            KnownCustomAttribute.TypeLibVersion
                            KnownCustomAttribute.ComCompatibleVersion
                            KnownCustomAttribute.AllowPartiallyTrustedCallers
                        ]

                    if threw then
                        reached <- reached.Add "Known, refused"
                    elif rows = 0 then
                        reached <- reached.Add "Known, no row"
                    elif not (List.contains known kept) || rows <> 1 then
                        failwith $"%s{describe}, but the host wrote %d{rows} row(s) and threw nothing"
                | CustomAttributeEmitKind.Ordinary ->
                    let outcome = flags ()

                    if
                        outcome
                        <> {
                               Threw = false
                               Rows = 1
                               HasSecurity = false
                               RequireSecObject = false
                           }
                    then
                        failwith $"%s{describe}, but the host did %A{outcome}"
                | CustomAttributeEmitKind.SuppressUnmanagedCodeSecurity ->
                    let outcome = flags ()

                    if
                        outcome
                        <> {
                               Threw = false
                               Rows = 1
                               HasSecurity = true
                               RequireSecObject = false
                           }
                    then
                        failwith $"%s{describe}, but the host did %A{outcome}"
                | CustomAttributeEmitKind.DynamicSecurityMethod ->
                    let outcome = flags ()

                    if
                        outcome
                        <> {
                               Threw = false
                               Rows = 0
                               HasSecurity = false
                               RequireSecObject = true
                           }
                    then
                        failwith $"%s{describe}, but the host did %A{outcome}"

        reached
        |> shouldEqual (
            Set.ofList
                [
                    "Ordinary"
                    "Known"
                    "Known, refused"
                    "Known, no row"
                    "SuppressUnmanagedCodeSecurity"
                    "DynamicSecurityMethod"
                ]
        )

    /// One generated `SetCustomAttribute` call of the rows' differential property.
    type private Call =
        {
            Owner : int
            Constructor : int
            Blob : int
        }

    type private RowsSchedule =
        {
            Types : int
            Calls : Call list
            /// The step at which every type is created; from then on only the assembly and the module get attributes.
            CreateAt : int
        }

    [<RequireQualifiedAccess>]
    type private RowsReach =
        | OwnerWithSeventeen
        | ModuleWithTwentyFive
        | BakedAmongLater
        | EmptyBlob
        | Owner of string

    [<Test>]
    let ``every owner's rows are what the host's emitter writes`` () : unit =
        let property (cover : RowsReach -> unit) (schedule : RowsSchedule) : unit =
            let assembly, host = newModule "RowsOracle"
            // An attribute type of the module, whose constructor is a MethodDef: TypeDef 2, MethodDef 1.
            let own = host.DefineType ("Own", TypeAttributes.Public, typeof<Attribute>)
            let ownCtor = own.DefineDefaultConstructor MethodAttributes.Public

            let types =
                [|
                    for i in 0 .. schedule.Types - 1 do
                        yield host.DefineType ($"T%d{i}", TypeAttributes.Public)
                |]

            let typeParameters =
                types |> Array.map (fun t -> (t.DefineGenericParameters [| "G" |]).[0])

            // Owners' tokens, forced in this order: each type's method, then its parameter, field, property and
            // event, as the scope below defines them.
            let methods =
                types
                |> Array.map (fun t ->
                    let m =
                        t.DefineMethod (
                            "M",
                            MethodAttributes.Public ||| MethodAttributes.Static,
                            typeof<Void>,
                            [| typeof<int> |]
                        )

                    m.GetILGenerator().Emit OpCodes.Ret
                    m
                )

            for m in methods do
                m.MetadataToken |> ignore<int>

            let parameters =
                methods
                |> Array.map (fun m -> m.DefineParameter (1, ParameterAttributes.None, "x"))

            let fields =
                types
                |> Array.map (fun t -> t.DefineField ("F", typeof<int>, FieldAttributes.Public))

            let properties =
                types
                |> Array.map (fun t -> t.DefineProperty ("P", PropertyAttributes.None, typeof<int>, [||]))

            let events =
                types
                |> Array.map (fun t -> t.DefineEvent ("E", EventAttributes.None, typeof<Action>))

            let pool : ConstructorInfo list =
                [
                    ownCtor
                    typeof<ObsoleteAttribute>.GetConstructor [| typeof<string> |]
                    typeof<CLSCompliantAttribute>.GetConstructor [| typeof<bool> |]
                    typeof<FlagsAttribute>.GetConstructor Type.EmptyTypes
                ]

            let poolTokens = pool |> List.map host.GetMethodMetadataToken

            // The scope: the same owners at the same RIDs, and as many MethodDef and MemberRef rows as the
            // constructors' tokens need.
            let mutable scope = emptyScope Guid.Empty

            let addType (name : string) : TypeDefRid =
                let name, s = EmitScope.internString name scope

                let rid, s =
                    EmitScope.addTypeDef
                        {
                            Flags = TypeAttributes.Public
                            Name = name
                            Namespace = StringOffset 0
                            Extends = None
                        }
                        s

                scope <- s
                rid

            let ownRid = addType "Own"

            scope <-
                EmitScopeDefinitions.defineMethod ownRid ".ctor" ctorSignature MethodAttributes.Public scope
                |> snd

            let typeRids = [| for i in 0 .. schedule.Types - 1 -> addType $"T%d{i}" |]

            let methodSignature =
                ImmutableArray.Create<byte> [| 0x00uy ; 0x01uy ; 0x01uy ; 0x08uy |]

            let methodRids =
                typeRids
                |> Array.map (fun t ->
                    let rid, s =
                        EmitScopeDefinitions.defineMethod
                            t
                            "M"
                            methodSignature
                            (MethodAttributes.Public ||| MethodAttributes.Static)
                            scope

                    scope <- s
                    rid
                )

            let paramRids =
                methodRids
                |> Array.map (fun m ->
                    match EmitScopeDefinitions.defineParam m 1 "x" 0 scope with
                    | ParamDefinition.Defined (rid, s) ->
                        scope <- s
                        rid
                    | ParamDefinition.StrandsLaterMethods _ -> failwith "one parameter each strands nothing"
                )

            let fieldRids =
                typeRids
                |> Array.map (fun t ->
                    let rid, s =
                        EmitScopeDefinitions.defineField
                            t
                            "F"
                            (ImmutableArray.Create<byte> [| 0x06uy ; 0x08uy |])
                            FieldAttributes.Public
                            scope

                    scope <- s
                    rid
                )

            let propertyRids =
                typeRids
                |> Array.map (fun t ->
                    let rid, s =
                        EmitScopeDefinitions.defineProperty
                            t
                            "P"
                            (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                            0
                            scope

                    scope <- s
                    rid
                )

            let eventRids =
                typeRids
                |> Array.map (fun t ->
                    let rid, s = EmitScopeDefinitions.defineEvent t "E" (TypeDefOrRef.TypeDef t) 0 scope

                    scope <- s
                    rid
                )

            // Each owner's token on the host is its RID in the scope.
            for i in 0 .. schedule.Types - 1 do
                types.[i].MetadataToken |> shouldEqual (0x02000000 ||| typeRids.[i].Rid)
                methods.[i].MetadataToken |> shouldEqual (0x06000000 ||| methodRids.[i].Rid)

                parameters.[i].GetType().GetField("_token", instance).GetValue parameters.[i]
                |> unbox<int>
                |> shouldEqual (0x08000000 ||| paramRids.[i].Rid)

                fields.[i].MetadataToken |> shouldEqual (0x04000000 ||| fieldRids.[i].Rid)

            let maxMemberRef =
                poolTokens
                |> List.filter (fun t -> t >>> 24 = 0x0A)
                |> List.map (fun t -> t &&& 0xFFFFFF)
                |> List.fold max 0

            scope <-
                (scope, [ 1..maxMemberRef ])
                ||> List.fold (fun scope _ -> memberRef (MemberRefParent.TypeDef ownRid) scope |> snd)

            let mirror (token : int) : MethodDefOrRef =
                match token >>> 24 with
                | 0x06 -> MethodDefOrRef.MethodDef (EmitScope.tryMethodDefRid (token &&& 0xFFFFFF) scope).Value
                | 0x0A -> MethodDefOrRef.MemberRef (EmitScope.tryMemberRefRid (token &&& 0xFFFFFF) scope).Value
                | _ -> failwith $"GetMethodMetadataToken gave 0x%08x{token}"

            let define (owner : HasCustomAttribute) (constructor : int) (value : byte[]) : int =
                match
                    EmitScopeDefinitions.defineCustomAttribute
                        owner
                        (mirror constructor)
                        (ImmutableArray.Create<byte> value)
                        scope
                with
                | CustomAttributeDefinition.Defined (rid, s) ->
                    scope <- s
                    rid.Rid
                | CustomAttributeDefinition.NotStoredAsGiven kind ->
                    failwith $"the pool holds no pseudo-attribute, but the emitter would %A{kind}"

            // Attributes of each type's generic parameter, queued until the type is created, as the host queues them.
            let queued = Array.init schedule.Types (fun _ -> ResizeArray<int * byte[]> ())
            let mutable created = false
            let mutable lastGenericRow = 0
            let mutable rowsAfterQueue = false

            let createAll () =
                for i in 0 .. schedule.Types - 1 do
                    types.[i].CreateType () |> ignore<Type>

                    let parameter, s =
                        EmitScopeDefinitions.defineGenericParam
                            (TypeOrMethodDef.TypeDef typeRids.[i])
                            0us
                            0
                            "G"
                            []
                            scope

                    scope <- s

                    for constructor, value in queued.[i] do
                        lastGenericRow <- define (HasCustomAttribute.GenericParam parameter) constructor value

                    if queued.[i].Count > 0 && rowsAfterQueue then
                        cover RowsReach.BakedAmongLater

                created <- true

            for step, call in List.indexed schedule.Calls do
                if step = schedule.CreateAt && not created then
                    createAll ()

                let kinds = if created then 2 else 9
                let index = call.Owner % schedule.Types
                let constructorIndex = call.Constructor % pool.Length
                let constructor = pool.[constructorIndex]
                let constructorToken = poolTokens.[constructorIndex]
                let value = (pick call.Blob customAttributeBlobs).Value |> Seq.toArray

                if value.Length = 0 then
                    cover RowsReach.EmptyBlob

                let kind = (call.Owner / 7) % kinds

                let owner, apply =
                    match kind with
                    | 0 -> HasCustomAttribute.Assembly, (fun () -> assembly.SetCustomAttribute (constructor, value))
                    | 1 -> HasCustomAttribute.Module, (fun () -> host.SetCustomAttribute (constructor, value))
                    | 2 ->
                        HasCustomAttribute.TypeDef typeRids.[index],
                        (fun () -> types.[index].SetCustomAttribute (constructor, value))
                    | 3 ->
                        HasCustomAttribute.MethodDef methodRids.[index],
                        (fun () -> methods.[index].SetCustomAttribute (constructor, value))
                    | 4 ->
                        HasCustomAttribute.Field fieldRids.[index],
                        (fun () -> fields.[index].SetCustomAttribute (constructor, value))
                    | 5 ->
                        HasCustomAttribute.Param paramRids.[index],
                        (fun () -> parameters.[index].SetCustomAttribute (constructor, value))
                    | 6 ->
                        HasCustomAttribute.Property propertyRids.[index],
                        (fun () -> properties.[index].SetCustomAttribute (constructor, value))
                    | 7 ->
                        HasCustomAttribute.Event eventRids.[index],
                        (fun () -> events.[index].SetCustomAttribute (constructor, value))
                    | _ ->
                        // The parameter's row does not exist yet: `owner` is a placeholder the queue replaces.
                        HasCustomAttribute.Module,
                        (fun () -> typeParameters.[index].SetCustomAttribute (constructor, value))

                apply ()

                if kind = 8 then
                    queued.[index].Add (constructorToken, value)
                    cover (RowsReach.Owner "generic parameter")
                else
                    define owner constructorToken value |> ignore<int>
                    cover (RowsReach.Owner (ownerKind owner))

                    if queued |> Array.exists (fun q -> q.Count > 0) then
                        rowsAfterQueue <- true

            if not created then
                createAll ()

            // Every owner, compared with the image of the scope through the index `Assembly.read` builds.
            let image = EmitScopeImage.write scope

            use dumped =
                Assembly.read NullLoggerFactory.Instance (Some "RowsOracle.dll") (new MemoryStream (image))

            let ownerTokens =
                [
                    yield 0x20000001
                    yield 0x00000001
                    for i in 0 .. schedule.Types - 1 do
                        yield 0x02000000 ||| typeRids.[i].Rid
                        yield 0x06000000 ||| methodRids.[i].Rid
                        yield 0x04000000 ||| fieldRids.[i].Rid
                        yield 0x08000000 ||| paramRids.[i].Rid
                        yield 0x17000000 ||| propertyRids.[i].Rid
                        yield 0x14000000 ||| eventRids.[i].Rid
                    for i in 1 .. (EmitScope.genericParams scope).Count do
                        yield 0x2A000000 ||| i
                ]

            for token in ownerTokens do
                let expected = hostRows host token

                let actual =
                    match dumped.CustomAttributesByParentToken.TryGetValue token with
                    | true, rows ->
                        [
                            for row in rows do
                                let attribute =
                                    dumped.Attributes.[System
                                        .Reflection
                                        .Metadata
                                        .Ecma335
                                        .MetadataTokens
                                        .CustomAttributeHandle (row &&& 0xFFFFFF)]

                                yield row, MetadataToken.toInt attribute.Constructor, List.ofSeq attribute.Value
                        ]
                    | false, _ -> []

                actual |> shouldEqual expected

                if expected.Length >= 17 then
                    cover RowsReach.OwnerWithSeventeen

            if hostRows host 0x00000001 |> List.length >= 25 then
                cover RowsReach.ModuleWithTwentyFive

        let genCall (favourite : int) : Gen<Call> =
            gen {
                let! owner = Gen.frequency [ 1, Gen.constant favourite ; 2, choice ]
                let! constructor = choice
                let! blob = choice

                return
                    {
                        Owner = owner
                        Constructor = constructor
                        Blob = blob
                    }
            }

        let genSchedule : Gen<RowsSchedule> =
            gen {
                let! types = Gen.choose (1, 3)
                let! favourite = choice
                let! count = Gen.choose (0, 80)
                let! calls = Gen.listOfLength count (genCall favourite)
                let! createAt = Gen.choose (0, count + 5)

                return
                    {
                        Types = types
                        Calls = List.ofSeq calls
                        CreateAt = createAt
                    }
            }

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 150) (Arb.fromGen genSchedule) property

        coverage.Count RowsReach.OwnerWithSeventeen |> shouldBeGreaterThan 10
        coverage.Count RowsReach.ModuleWithTwentyFive |> shouldBeGreaterThan 3
        coverage.Count RowsReach.BakedAmongLater |> shouldBeGreaterThan 10
        coverage.Count RowsReach.EmptyBlob |> shouldBeGreaterThan 50

        for kind in ownerKinds do
            coverage.Count (RowsReach.Owner kind) |> shouldBeGreaterThan 20
