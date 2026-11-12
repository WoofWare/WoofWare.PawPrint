namespace WoofWare.PawPrint

open System.Reflection
open Microsoft.Extensions.Logging

/// What CoreCLR's class loader makes of a method's return type when it decides which methods take
/// part in runtime async (`MethodReturnKind`, method.hpp; `ClassifyMethodReturnKind`, method.cpp).
[<RequireQualifiedAccess>]
type MethodReturnKind =
    /// Returns anything but one of the four promise types below.
    | NormalMethod
    /// Returns CoreLib's `System.Threading.Tasks.Task` or `ValueTask`.
    | NonGenericTaskReturningMethod
    /// Returns CoreLib's `System.Threading.Tasks.Task<T>` or `ValueTask<T>`.
    | GenericTaskReturningMethod

/// Why a method has no async variant for `RuntimeAsyncMethod.asyncVariantOf` to construct.
[<RequireQualifiedAccess>]
type AsyncVariantRefusal =
    /// The runtime synthesised the method: it has no MethodDef for CoreCLR to make variants of.
    | Synthesised
    /// The method is already its MethodDef's async variant.
    | AlreadyAsyncVariant
    /// The method does not declare `MethodImplAttributes.Async`. If it returns a promise type,
    /// CoreCLR's async variant of it is its own IL compiled as an async version (every `ret`
    /// awaiting the returned promise), which PawPrint does not construct.
    | NotDeclaredAsync
    /// The method declares `MethodImplAttributes.Async` and returns no promise type, so CoreCLR makes
    /// it one `MethodDesc`, which is the ordinary one and has the async calling convention.
    | ReturnsNoPromise
    /// Classifying the method's return type needed a type that does not bind.
    | ReturnTypeUnbound of TypeBindingFailure
    /// The method's type is a delegate, whose methods CoreCLR's class loader takes to return no
    /// promise type, whatever they return.
    | DelegateMethod
    /// Deciding whether the method's type is a delegate needed its base type, which does not bind.
    | BaseTypeUnbound of TypeBindingFailure
    /// The method's type is ComImport (`TypeAttributes.Import`). CoreCLR binds no await to an async
    /// variant of such a method (`CORINFO_TOKENKIND_Await`, jitinterface.cpp).
    | ComImportType
    /// CoreCLR gives the method an async variant, but its body is one whose async variant PawPrint
    /// does not model: a P/Invoke, an InternalCall, one the runtime provides, or none.
    | UnmodelledBody of MethodBody

/// Which methods CoreCLR's class loader makes callable with runtime async's calling convention.
[<RequireQualifiedAccess>]
module RuntimeAsyncMethod =

    /// `MethodImplAttributes.Async`, which the `System.Reflection` of this project's target
    /// framework does not name.
    let asyncImplAttribute : MethodImplAttributes = enum<MethodImplAttributes> 0x2000

    [<Literal>]
    let private PromiseNamespace = "System.Threading.Tasks"

    /// Reading an element type from a signature skips the custom modifiers before it
    /// (`SigPointer::GetElemType`).
    let rec private unmodified (ty : TypeDefn) : TypeDefn =
        match ty with
        | TypeDefn.Modified modified -> unmodified modified.Unmodified
        | ty -> ty

    /// Whether the type a signature names by `token` is defined in CoreLib
    /// (`IsTypeDefOrRefImplementedInSystemModule`, method.cpp): a TypeDef when `declaringAssembly`
    /// is CoreLib, and a TypeRef when it binds to a definition there.
    let private definedInCoreLib
        (loggerFactory : ILoggerFactory)
        (runtimeDirs : string seq)
        (coreLib : DumpedAssembly)
        (declaringAssembly : DumpedAssembly)
        (token : TypeDefn)
        (assemblies : LoadedAssemblies)
        : LoadedAssemblies * Result<bool, TypeBindingFailure>
        =
        match token with
        | TypeDefn.FromDefinition (identity, _) ->
            assemblies, Ok (identity.AssemblyFullName = coreLib.DefinitionFullName)
        | TypeDefn.FromReference (typeRef, _) ->
            match
                TypeResolution.tryResolveTypeRefIdentity loggerFactory runtimeDirs declaringAssembly typeRef assemblies
            with
            | assemblies, TypeReferenceIdentity.Resolved identity ->
                assemblies, Ok (identity.AssemblyFullName = coreLib.DefinitionFullName)
            | assemblies, TypeReferenceIdentity.AssemblyUnavailable reference ->
                assemblies, Error (TypeBindingFailure.AssemblyUnavailable reference)
            | assemblies, TypeReferenceIdentity.TypeAbsent miss ->
                assemblies, Error (TypeBindingFailure.TypeAbsent miss)
            | assemblies, TypeReferenceIdentity.Unmodelled unmodelled ->
                assemblies, Error (TypeBindingFailure.Unmodelled unmodelled)
        | token -> failwith $"logic error: %O{token} is not a TypeDef or TypeRef token"

    /// CoreCLR's `ClassifyMethodReturnKind` (method.cpp) of `returnType`, the return type of a
    /// method `declaringAssembly` declares. It compares the names a TypeDef or TypeRef row gives,
    /// and binds a TypeRef only when its name matches, which may load the assembly it names.
    let classifyReturn
        (loggerFactory : ILoggerFactory)
        (runtimeDirs : string seq)
        (coreLib : DumpedAssembly)
        (declaringAssembly : DumpedAssembly)
        (returnType : MethodReturnType<TypeDefn>)
        (assemblies : LoadedAssemblies)
        : LoadedAssemblies * Result<MethodReturnKind, TypeBindingFailure>
        =
        // The name and namespace of the row a signature's token names, and whether the signature
        // spells it `valuetype`.
        let rowName (token : TypeDefn) : (string * string * bool) option =
            match token with
            | TypeDefn.FromDefinition (identity, kind) ->
                let typeDef = declaringAssembly.TypeDefs.[identity.TypeDefinition.Get]
                Some (typeDef.Namespace, typeDef.Name, kind)
            | TypeDefn.FromReference (typeRef, kind) -> Some (typeRef.Namespace, typeRef.Name, kind)
            | _ -> None
            |> Option.map (fun (ns, name, kind) ->
                match kind with
                | System.Reflection.Metadata.SignatureTypeKind.ValueType -> ns, name, true
                | System.Reflection.Metadata.SignatureTypeKind.Class -> ns, name, false
                | kind ->
                    failwith
                        $"logic error: a signature spelled %s{ns}.%s{name} as neither class nor valuetype (%O{kind})"
            )

        let candidate =
            match returnType with
            | MethodReturnType.Void -> None
            | MethodReturnType.Returns ty ->
                match unmodified ty with
                | TypeDefn.GenericInstantiation (generic, typeArgs) when typeArgs.Length = 1 ->
                    let generic = unmodified generic

                    rowName generic
                    |> Option.bind (fun (ns, name, isValueTask) ->
                        let expected = if isValueTask then "ValueTask`1" else "Task`1"

                        if ns = PromiseNamespace && name = expected then
                            Some (generic, MethodReturnKind.GenericTaskReturningMethod)
                        else
                            None
                    )
                | token ->
                    rowName token
                    |> Option.bind (fun (ns, name, isValueTask) ->
                        let expected = if isValueTask then "ValueTask" else "Task"

                        if ns = PromiseNamespace && name = expected then
                            Some (token, MethodReturnKind.NonGenericTaskReturningMethod)
                        else
                            None
                    )

        match candidate with
        | None -> assemblies, Ok MethodReturnKind.NormalMethod
        | Some (token, kind) ->
            match definedInCoreLib loggerFactory runtimeDirs coreLib declaringAssembly token assemblies with
            | assemblies, Ok true -> assemblies, Ok kind
            | assemblies, Ok false -> assemblies, Ok MethodReturnKind.NormalMethod
            | assemblies, Error failure -> assemblies, Error failure

    /// CoreCLR's `MethodDesc::IsAsyncMethod` for the method `method`, which `declaringAssembly`
    /// declares: whether the class loader gives it the `AsyncCall` flag. This is the method a
    /// MethodDef row declares, never the async variant CoreCLR adds beside a method returning a
    /// promise type, which reflection does not see; `hasAsyncCallingConvention` answers for either.
    let isAsyncCall
        (loggerFactory : ILoggerFactory)
        (runtimeDirs : string seq)
        (coreLib : DumpedAssembly)
        (declaringAssembly : DumpedAssembly)
        (method : MetadataMethodFacts)
        (assemblies : LoadedAssemblies)
        : LoadedAssemblies * Result<bool, TypeBindingFailure>
        =
        // `MethodTableBuilder::EnumerateClassMethods`: a method declaring `Async` is `AsyncCall`
        // unless it returns a promise type, when its async variant is instead. Without `Async`, a
        // method is never `AsyncCall`, so nothing else need be read.
        //
        // The builder also takes every method of a delegate to return normally. That decides
        // nothing here: outside CoreLib a method that declares `Async` and is `AsyncCall` fails its
        // type's load, and CoreLib's delegates declare none.
        if not (method.ImplAttributes.HasFlag asyncImplAttribute) then
            assemblies, Ok false
        else

        match
            classifyReturn loggerFactory runtimeDirs coreLib declaringAssembly method.RawSignature.ReturnType assemblies
        with
        | assemblies, Ok MethodReturnKind.NormalMethod -> assemblies, Ok true
        | assemblies, Ok MethodReturnKind.NonGenericTaskReturningMethod
        | assemblies, Ok MethodReturnKind.GenericTaskReturningMethod -> assemblies, Ok false
        | assemblies, Error failure -> assemblies, Error failure

    /// The return column of a promise-returning method's async variant, as CoreCLR's class loader
    /// rewrites the signature blob (`methodtablebuilder.cpp`): the bytes of `Task<T>` or
    /// `ValueTask<T>` become those of `T`, and those of `Task` or `ValueTask` become `void`, after
    /// whatever custom modifiers precede them. `kind` is what `classifyReturn` made of `returnType`.
    let private unwrappedReturn
        (kind : MethodReturnKind)
        (returnType : MethodReturnType<TypeDefn>)
        : MethodReturnType<TypeDefn>
        =
        // The custom modifiers on the outside of the promise type, put back around its replacement.
        let rec replace (replacement : TypeDefn -> TypeDefn) (ty : TypeDefn) : TypeDefn =
            match ty with
            | TypeDefn.Modified modified ->
                TypeDefn.Modified
                    { modified with
                        Unmodified = replace replacement modified.Unmodified
                    }
            | promise -> replacement promise

        match kind, returnType with
        | MethodReturnKind.NormalMethod, _ ->
            failwith "logic error: asked to unwrap the return type of a method returning no promise type"
        | _, MethodReturnType.Void ->
            failwith "logic error: a method returning void was classified as returning a promise type"
        | MethodReturnKind.GenericTaskReturningMethod, MethodReturnType.Returns ty ->
            replace
                (fun promise ->
                    match promise with
                    | TypeDefn.GenericInstantiation (_, args) when args.Length = 1 -> args.[0]
                    | other -> failwith $"logic error: %O{other} was classified as Task<T> or ValueTask<T>"
                )
                ty
            |> MethodReturnType.Returns
        // A bare `void` decodes as `Void`, and one under custom modifiers as `Returns`, as
        // `TypeMethodSignature.make` decodes the blob.
        | MethodReturnKind.NonGenericTaskReturningMethod, MethodReturnType.Returns (TypeDefn.Modified _ as ty) ->
            MethodReturnType.Returns (replace (fun _ -> TypeDefn.Void) ty)
        | MethodReturnKind.NonGenericTaskReturningMethod, MethodReturnType.Returns _ -> MethodReturnType.Void

    /// <summary>
    /// The async variant of <paramref name="method" />, the ordinary variant of a method
    /// <paramref name="declaringAssembly" /> declares: the second <c>MethodDesc</c> CoreCLR's class
    /// loader makes for a MethodDef flagged <c>MethodImplAttributes.Async</c> that returns
    /// <c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c> or <c>ValueTask&lt;T&gt;</c>. It has the
    /// same token and IL, and the signature with the promise type unwrapped (<c>Task&lt;T&gt;</c> and
    /// <c>ValueTask&lt;T&gt;</c> to <c>T</c>, <c>Task</c> and <c>ValueTask</c> to <c>void</c>).
    /// </summary>
    /// <remarks>
    /// This is the only way to construct an async variant. Any other method is refused.
    /// </remarks>
    let asyncVariantOf
        (loggerFactory : ILoggerFactory)
        (runtimeDirs : string seq)
        (coreLib : DumpedAssembly)
        (declaringAssembly : DumpedAssembly)
        (method : MethodInfo<'typeGenerics, 'methodGenerics, TypeDefn>)
        (assemblies : LoadedAssemblies)
        : LoadedAssemblies * Result<MethodInfo<'typeGenerics, 'methodGenerics, TypeDefn>, AsyncVariantRefusal>
        =
        match method with
        | MethodInfo.Synthesised _ -> assemblies, Error AsyncVariantRefusal.Synthesised
        | MethodInfo.Metadata (_, _, MethodVariant.AsyncVariant) ->
            assemblies, Error AsyncVariantRefusal.AlreadyAsyncVariant
        | MethodInfo.Metadata (core, facts, MethodVariant.Ordinary) ->
            if not (facts.ImplAttributes.HasFlag asyncImplAttribute) then
                assemblies, Error AsyncVariantRefusal.NotDeclaredAsync
            else

            match
                classifyReturn
                    loggerFactory
                    runtimeDirs
                    coreLib
                    declaringAssembly
                    facts.RawSignature.ReturnType
                    assemblies
            with
            | assemblies, Error failure -> assemblies, Error (AsyncVariantRefusal.ReturnTypeUnbound failure)
            | assemblies, Ok MethodReturnKind.NormalMethod -> assemblies, Error AsyncVariantRefusal.ReturnsNoPromise
            | assemblies, Ok kind ->
                match core.Body with
                | MethodBody.AsyncIl _ -> ()
                | body ->
                    failwith
                        $"logic error: %O{method} declares MethodImplAttributes.Async and returns a promise type, but its body is %O{body} rather than runtime-async IL"

                let variant =
                    MethodInfo.Metadata (
                        { core with
                            Signature =
                                { core.Signature with
                                    ReturnType = unwrappedReturn kind core.Signature.ReturnType
                                }
                        },
                        facts,
                        MethodVariant.AsyncVariantMethodDesc
                    )

                assemblies, Ok variant

    /// <summary>
    /// Whether <paramref name="method" />, which <paramref name="declaringAssembly" /> declares, has
    /// runtime async's calling convention (a hidden continuation argument and return), and so runs
    /// a runtime-async body.
    /// </summary>
    /// <remarks>
    /// An async variant has it. An ordinary variant has it exactly when CoreCLR makes it
    /// <c>AsyncCall</c> (<see cref="isAsyncCall" />): a method declaring <c>Async</c> that returns no
    /// promise type, which has no async variant beside it. A synthesised method never has it.
    /// </remarks>
    let hasAsyncCallingConvention
        (loggerFactory : ILoggerFactory)
        (runtimeDirs : string seq)
        (coreLib : DumpedAssembly)
        (declaringAssembly : DumpedAssembly)
        (method : MethodInfo<'typeGenerics, 'methodGenerics, 'methodVars>)
        (assemblies : LoadedAssemblies)
        : LoadedAssemblies * Result<bool, TypeBindingFailure>
        =
        match method with
        | MethodInfo.Synthesised _ -> assemblies, Ok false
        | MethodInfo.Metadata (_, _, MethodVariant.AsyncVariant) -> assemblies, Ok true
        | MethodInfo.Metadata (_, facts, MethodVariant.Ordinary) ->
            isAsyncCall loggerFactory runtimeDirs coreLib declaringAssembly facts assemblies
