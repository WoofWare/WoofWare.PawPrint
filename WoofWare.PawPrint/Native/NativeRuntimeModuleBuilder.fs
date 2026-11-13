namespace WoofWare.PawPrint

open System
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

open NativeRuntimeTypeHelpers

/// QCalls declared on `System.Reflection.Emit.RuntimeModuleBuilder`, each of which adds to a dynamic
/// module's metadata what CoreCLR's emitter adds (vm/commodule.cpp), and hands back the token.
[<RequireQualifiedAccess>]
module NativeRuntimeModuleBuilder =
    /// `mdtTypeRef`, the table byte of a `TypeRef` token.
    let private typeRefTable : int = 0x01

    /// `mdtTypeDef`, the table byte of a `TypeDef` token.
    let private typeDefTable : int = 0x02

    /// `mdtFieldDef`, the table byte of a `FieldDef` token.
    let private fieldDefTable : int = 0x04

    /// `mdtMethodDef`, the table byte of a `MethodDef` token.
    let private methodDefTable : int = 0x06

    /// `mdtMemberRef`, the table byte of a `MemberRef` token.
    let private memberRefTable : int = 0x0A

    /// `mdtTypeSpec`, the table byte of a `TypeSpec` token.
    let private typeSpecTable : int = 0x1B

    /// The signature of <paramref name="described"/>, of another module, as <c>TranslateSigWithScope</c>
    /// writes it into <paramref name="moduleName"/>, with the scope holding the rows the translation
    /// added; or a TODO naming the token it refused.
    let private translatedSignature
        (operation : string)
        (described : string)
        (moduleName : string)
        (import : SignatureImport)
        : System.Collections.Immutable.ImmutableArray<byte> * EmitScope
        =
        let typeRef (handle : TypeReferenceHandle) : int =
            MetadataTokens.GetToken (TypeReferenceHandle.op_Implicit handle : EntityHandle)

        // The translation's AssemblyRefs are not recorded with `WithAssemblyRefTarget`: CoreCLR records
        // only `GetTypeRef`'s in the module's rid map, so a translated reference resolves through the
        // binder, as the source module's own does.
        match import with
        | SignatureImport.Imported (imported, translated) -> imported, translated
        | SignatureImport.Refused (SignatureTokenRefusal.TypeRefWithoutScope handle) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} names TypeRef 0x%08x{typeRef handle} of its module, whose outermost resolution scope is nil, which CoreCLR's ImportTypeRef looks up among the source assembly's ExportedTypes; PawPrint translates only a TypeRef scoped by an AssemblyRef"
        | SignatureImport.Refused (SignatureTokenRefusal.TypeRefOfModule handle) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} names TypeRef 0x%08x{typeRef handle} of its module, scoped by the module itself, which CoreCLR's ImportTypeRef scopes by the AssemblyRef it makes for the source assembly; PawPrint translates only a TypeRef scoped by an AssemblyRef"
        | SignatureImport.Refused (SignatureTokenRefusal.TypeRefOfModuleRef handle) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} names TypeRef 0x%08x{typeRef handle} of its module, scoped by a ModuleRef, which CoreCLR's ImportTypeRef scopes by the AssemblyRef it makes for the source assembly; PawPrint translates only a TypeRef scoped by an AssemblyRef"
        | SignatureImport.Refused (SignatureTokenRefusal.TypeRefOfEmittingAssembly handle) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} names TypeRef 0x%08x{typeRef handle} of its module, scoped by an AssemblyRef naming %s{moduleName} itself, which CoreCLR's ImportTypeRef makes name a type of the emitting module; PawPrint does not"
        | SignatureImport.Refused (SignatureTokenRefusal.TypeRefOfInvalidPublicKey handle) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} names TypeRef 0x%08x{typeRef handle} of its module, scoped by an AssemblyRef holding a full key that StrongNameIsValidPublicKey rejects; CoreCLR copies it, and throws SecurityException from any later search that reduces the key, which PawPrint does not raise"
        | SignatureImport.Refused (SignatureTokenRefusal.TypeSpec handle) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} names TypeSpec 0x%08x{MetadataTokens.GetToken (TypeSpecificationHandle.op_Implicit handle : EntityHandle)} of its module, which CoreCLR translates in turn and finds or adds in %s{moduleName}; PawPrint translates only a TypeDef or TypeRef of the source module"
        | SignatureImport.Refused (SignatureTokenRefusal.NoTable coded) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} holds the encoded token 0x%x{coded}, whose tag names no table; CoreCLR copies it as an mdtBaseType, which no compiler writes"
        | SignatureImport.Refused (SignatureTokenRefusal.UnreferencedUnsignedAssembly assembly) ->
            failwith
                $"%s{operation}: the signature of %s{described} names a TypeDef of the unsigned assembly %s{assembly}, which %s{moduleName} has no AssemblyRef for, though the managed caller asks for the declaring type's TypeRef, which makes one, first"

    /// The signature of <paramref name="described"/>, of <paramref name="moduleName"/> itself, as
    /// <c>TranslateSigWithScope</c> writes it into the module, which keeps each TypeDef and TypeRef token and
    /// adds no row; or a TODO naming the token it refused.
    let private ownSignature
        (operation : string)
        (described : string)
        (moduleName : string)
        (import : Result<System.Collections.Immutable.ImmutableArray<byte>, OwnSignatureRefusal>)
        : System.Collections.Immutable.ImmutableArray<byte>
        =
        match import with
        | Ok imported -> imported
        | Error (OwnSignatureRefusal.TypeSpec typeSpec) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} names TypeSpec 0x%08x{(typeSpecTable <<< 24) ||| typeSpec.Rid} of %s{moduleName} itself, which CoreCLR translates in turn and finds or adds by its translated bytes; PawPrint translates only a TypeDef or TypeRef of the module itself"
        | Error (OwnSignatureRefusal.NoTable coded) ->
            failwith
                $"TODO: %s{operation}: the signature of %s{described} holds the encoded token 0x%x{coded}, whose tag names no table; CoreCLR copies it as an mdtBaseType, which no compiler writes"

    /// The row of <paramref name="moduleName"/>'s scope that the parent token <paramref name="tr"/> names, for
    /// a TypeRef or TypeSpec token; or `None` for a token of another table, which each handler answers in its
    /// own way. A TypeRef or TypeSpec token naming no row is PawPrint's fault, since an earlier QCall handed
    /// it out.
    let private typeRefOrSpecParent
        (operation : string)
        (described : string)
        (moduleName : string)
        (held : EmitScope)
        (tr : int)
        : MemberRefParent option
        =
        match tr >>> 24 with
        | table when table = typeRefTable ->
            match EmitScope.tryTypeRefRid (tr &&& 0x00FFFFFF) held with
            | Some rid -> Some (MemberRefParent.TypeRef rid)
            | None ->
                failwith
                    $"%s{operation}: parent token 0x%08x{tr} for %s{described} names no TypeRef row of %s{moduleName}"
        | table when table = typeSpecTable ->
            match EmitScope.tryTypeSpecRid (tr &&& 0x00FFFFFF) held with
            | Some rid -> Some (MemberRefParent.TypeSpec rid)
            | None ->
                failwith
                    $"%s{operation}: parent token 0x%08x{tr} for %s{described} names no TypeSpec row of %s{moduleName}"
        | _ -> None

    /// The token of the MemberRef named <paramref name="name"/> with the translated signature under
    /// <paramref name="parent"/>, found or added (`DefineMemberRef` under `MDDupMemberRef`) in the scope the
    /// translation left; and the state holding the resulting scope of <paramref name="moduleName"/>, whose
    /// scope was <paramref name="held"/> before the translation.
    let private memberRefOf
        (ctx : NativeCallContext)
        (state : IlMachineState)
        (moduleName : string)
        (held : EmitScope)
        (parent : MemberRefParent)
        (name : string)
        (signature : System.Collections.Immutable.ImmutableArray<byte>, translated : EmitScope)
        : int * IlMachineState
        =
        let memberRef, extended =
            EmitScopeReferences.defineMemberRef parent name signature translated

        let state =
            // Neither translated nor added, so there is nothing to re-read.
            if Object.ReferenceEquals (extended, held) then
                state
            else
                state.WithEmitScope ctx.LoggerFactory moduleName extended |> fst

        (memberRefTable <<< 24) ||| memberRef.Rid, state

    let tryExecuteQCall (entryPoint : string) (ctx : NativeCallContext) : NativeHandlerResult option =
        let state = ctx.State
        let instruction = ctx.Instruction

        match
            entryPoint,
            ctx.TargetAssembly.Name.Name,
            ctx.TargetType.Namespace,
            ctx.TargetType.Name,
            instruction.ExecutingMethod.Signature.ParameterTypes,
            instruction.ExecutingMethod.Signature.ReturnType
        with
        | "ModuleBuilder_GetStringConstant",
          "System.Private.CoreLib",
          "System.Reflection.Emit",
          "RuntimeModuleBuilder",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", moduleGenerics)
            ConcretePointer (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.UInt16)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32 ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) when
            moduleGenerics.IsEmpty
            ->
            let operation = "ModuleBuilder_GetStringConstant"

            if instruction.Arguments.Length <> 3 then
                failwith $"%s{operation}: expected three native arguments, got %d{instruction.Arguments.Length}"

            let moduleName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            let held =
                match state.TypeSystem._LoadedAssemblies.TryDynamicScope moduleName with
                | Some scope -> scope
                | None ->
                    failwith
                        $"%s{operation}: %s{moduleName} is not a dynamic assembly, and only a RuntimeModuleBuilder's module reaches this QCall"

            let length = NativeCall.int32Argument operation instruction.Arguments.[2]

            if length < 0 then
                failwith
                    $"%s{operation}: a negative length %d{length}; the managed caller passes the string's own length"

            // The managed caller pins the string and passes its length, so the code units are counted,
            // not terminated: the string may hold a NUL, and a lone surrogate passes through as it is.
            let value =
                let bytes =
                    NativeCall.readCountedBytes
                        operation
                        ctx.BaseClassTypes
                        state
                        (NativeCall.managedPointerOfPointerArgument operation "pwzValue" instruction.Arguments.[1])
                        (2 * length)

                System.String (Array.init length (fun i -> char (int bytes.[2 * i] ||| (int bytes.[2 * i + 1] <<< 8))))

            let offset, extended = EmitScope.internUserString value held

            // `RegMeta::DefineUserString` refuses an offset that needs the token's table byte.
            if offset.Offset &&& 0xFF000000 <> 0 then
                failwith
                    $"TODO: %s{operation}: the #US heap of %s{moduleName} is full at offset 0x%x{offset.Offset}, for which CoreCLR raises META_E_STRINGSPACE_FULL"

            let state, _ = state.WithEmitScope ctx.LoggerFactory moduleName extended

            state
            |> IlMachineState.pushToEvalStack
                (CliType.Numeric (CliNumericType.Int32 (0x70000000 ||| offset.Offset)))
                ctx.Thread
            |> NativeHandlerResult.completed
            |> Some
        | "ModuleBuilder_GetTokenFromTypeSpec",
          "System.Private.CoreLib",
          "System.Reflection.Emit",
          "RuntimeModuleBuilder",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", moduleGenerics)
            ConcretePointer (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Byte)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32 ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) when
            moduleGenerics.IsEmpty
            ->
            let operation = "ModuleBuilder_GetTokenFromTypeSpec"

            if instruction.Arguments.Length <> 3 then
                failwith $"%s{operation}: expected three native arguments, got %d{instruction.Arguments.Length}"

            let moduleName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            let held =
                match state.TypeSystem._LoadedAssemblies.TryDynamicScope moduleName with
                | Some scope -> scope
                | None ->
                    failwith
                        $"%s{operation}: %s{moduleName} is not a dynamic assembly, and only a RuntimeModuleBuilder's module reaches this QCall"

            // A signature naming a type the managed caller could not spell as a token carries
            // `ELEMENT_TYPE_INTERNAL` and a pointer, which `readCountedBytes` refuses.
            let signature =
                NativeCall.readCountedBytes
                    operation
                    ctx.BaseClassTypes
                    state
                    (NativeCall.managedPointerOfPointerArgument operation "pSignature" instruction.Arguments.[1])
                    (NativeCall.int32Argument operation instruction.Arguments.[2])
                |> System.Collections.Immutable.ImmutableArray.CreateRange

            let typeSpec, extended = EmitScopeReferences.defineTypeSpec signature held
            let state, _ = state.WithEmitScope ctx.LoggerFactory moduleName extended

            state
            |> IlMachineState.pushToEvalStack
                (CliType.Numeric (CliNumericType.Int32 (0x1B000000 ||| typeSpec.Rid)))
                ctx.Thread
            |> NativeHandlerResult.completed
            |> Some
        | "ModuleBuilder_GetMemberRefFromSignature",
          "System.Private.CoreLib",
          "System.Reflection.Emit",
          "RuntimeModuleBuilder",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", moduleGenerics)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32
            ConcretePointer (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.UInt16)
            ConcretePointer (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Byte)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32 ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) when
            moduleGenerics.IsEmpty
            ->
            let operation = "ModuleBuilder_GetMemberRefFromSignature"

            if instruction.Arguments.Length <> 5 then
                failwith $"%s{operation}: expected five native arguments, got %d{instruction.Arguments.Length}"

            let moduleName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            let held =
                match state.TypeSystem._LoadedAssemblies.TryDynamicScope moduleName with
                | Some scope -> scope
                | None ->
                    failwith
                        $"%s{operation}: %s{moduleName} is not a dynamic assembly, and only a RuntimeModuleBuilder's module reaches this QCall"

            let tr = NativeCall.int32Argument operation instruction.Arguments.[1]

            // `RegMeta::DefineMemberRef` takes any MemberRefParent. `GetMemberRefToken` passes a
            // TypeSpec for a member of a generic instantiation, a TypeRef for a generic method of
            // another module, and for a vararg call site a TypeRef (a method of another module) or a
            // MethodDef (one of this module). A user-derived MemberInfo passes its type's token.
            let parent =
                match tr >>> 24 with
                | table when table = typeSpecTable ->
                    match EmitScope.tryTypeSpecRid (tr &&& 0x00FFFFFF) held with
                    | Some rid -> MemberRefParent.TypeSpec rid
                    | None -> failwith $"%s{operation}: the parent 0x%08x{tr} names no TypeSpec row of %s{moduleName}"
                | table when table = typeRefTable ->
                    match EmitScope.tryTypeRefRid (tr &&& 0x00FFFFFF) held with
                    | Some rid -> MemberRefParent.TypeRef rid
                    | None -> failwith $"%s{operation}: the parent 0x%08x{tr} names no TypeRef row of %s{moduleName}"
                | _ ->
                    failwith
                        $"TODO: %s{operation}: the parent 0x%08x{tr} is neither a TypeRef nor a TypeSpec; a vararg call site or a user-derived MemberInfo passes another kind, and PawPrint adds only a member of another module or of a generic instantiation"

            let name =
                match
                    NativeCall.managedPointerOfPointerArgument operation "wszMemberName" instruction.Arguments.[2]
                with
                | ManagedPointerSource.Null ->
                    failwith $"%s{operation}: a null member name; the managed callers pass the member's Name"
                | namePtr -> NativeCall.readNullTerminatedUtf16 operation ctx.BaseClassTypes state namePtr

            // A signature naming a type the managed caller could not spell as a token carries
            // `ELEMENT_TYPE_INTERNAL` and a pointer, which `readCountedBytes` refuses.
            let signature =
                NativeCall.readCountedBytes
                    operation
                    ctx.BaseClassTypes
                    state
                    (NativeCall.managedPointerOfPointerArgument operation "pSignature" instruction.Arguments.[3])
                    (NativeCall.int32Argument operation instruction.Arguments.[4])
                |> System.Collections.Immutable.ImmutableArray.CreateRange

            // A vararg call site's signature carries a sentinel and the call's extra arguments, and its
            // duplicate rules want measuring on their own.
            if signature.Length > 0 && signature.[0] &&& 0x0Fuy = 0x05uy then
                failwith
                    $"TODO: %s{operation}: %s{name}'s signature is vararg (0x%02x{signature.[0]}); PawPrint does not add a vararg call site's MemberRef"

            let memberRef, extended =
                EmitScopeReferences.defineMemberRef parent name signature held

            let state =
                // Found rather than added, so there is nothing to re-read.
                if Object.ReferenceEquals (extended, held) then
                    state
                else
                    state.WithEmitScope ctx.LoggerFactory moduleName extended |> fst

            state
            |> IlMachineState.pushToEvalStack
                (CliType.Numeric (CliNumericType.Int32 ((memberRefTable <<< 24) ||| memberRef.Rid)))
                ctx.Thread
            |> NativeHandlerResult.completed
            |> Some
        | "ModuleBuilder_GetTypeRef",
          "System.Private.CoreLib",
          "System.Reflection.Emit",
          "RuntimeModuleBuilder",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", moduleGenerics)
            ConcretePointer (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.UInt16)
            CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", refedGenerics)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32 ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) when
            moduleGenerics.IsEmpty && refedGenerics.IsEmpty
            ->
            let operation = "ModuleBuilder_GetTypeRef"

            if instruction.Arguments.Length <> 4 then
                failwith $"%s{operation}: expected four native arguments, got %d{instruction.Arguments.Length}"

            let moduleName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            let held =
                match state.TypeSystem._LoadedAssemblies.TryDynamicScope moduleName with
                | Some scope -> scope
                | None ->
                    failwith
                        $"%s{operation}: %s{moduleName} is not a dynamic assembly, and only a RuntimeModuleBuilder's module reaches this QCall"

            let fullName =
                match NativeCall.managedPointerOfPointerArgument operation "strFullName" instruction.Arguments.[1] with
                | ManagedPointerSource.Null ->
                    failwith
                        $"TODO: %s{operation}: a null type name, for which CoreCLR raises ArgumentNullException; the managed caller passes Type.FullName, which it never lets be null"
                | namePtr -> NativeCall.readNullTerminatedUtf16 operation ctx.BaseClassTypes state namePtr

            let refedName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[2] |> EvalStackValue.ofCliType)

            let tkResolution = NativeCall.int32Argument operation instruction.Arguments.[3]
            let name = EmitScopeReferences.unescapeTypeName fullName

            let token, state =
                if refedName = moduleName then
                    // The module's own type, which CoreCLR finds among its TypeDefs (`FindTypeDefByName`):
                    // nothing is added, and no AssemblyRef is recorded.
                    if tkResolution &&& 0x00FFFFFF <> 0 then
                        failwith
                            $"TODO: %s{operation}: %s{name} is nested in 0x%08x{tkResolution} of %s{moduleName}, its own module, whose TypeDef CoreCLR finds among those its NestedClass rows nest there; an emit scope has no NestedClass table yet, so no nested type of it can be created"

                    let ns, simple = EmitScopeReferences.splitTypeName name

                    match EmitScopeDefinitions.tryFindTopLevelTypeDef ns simple held with
                    | Some typeDef -> (typeDefTable <<< 24) ||| typeDef.Rid, state
                    | None ->
                        failwith
                            $"TODO: %s{operation}: %s{moduleName} has no top-level TypeDef named %s{name}, for which CoreCLR's FindTypeDefByName fails with CLDB_E_RECORD_NOTFOUND and the QCall raises a COMException; only a user-derived Type claiming the module reaches it, since a created type's TypeDef is in the module"
                else
                    // A non-zero RID is the TypeRef of the enclosing type, which an earlier call handed out:
                    // the reference is to a nested type. A zero one asks for a top-level type, scoped by the
                    // AssemblyRef of the assembly that defines it.
                    let resolution, scope, emittedFor =
                        if tkResolution &&& 0x00FFFFFF <> 0 then
                            if tkResolution >>> 24 <> typeRefTable then
                                failwith
                                    $"%s{operation}: resolution token 0x%08x{tkResolution} for %s{name} is not a TypeRef; the managed caller passes only its enclosing type's TypeRef"

                            match EmitScope.tryTypeRefRid (tkResolution &&& 0x00FFFFFF) held with
                            | Some enclosing -> ResolutionScope.TypeRef enclosing, held, None
                            | None ->
                                failwith
                                    $"%s{operation}: resolution token 0x%08x{tkResolution} for %s{name} names no TypeRef row of %s{moduleName}"
                        else
                            let refed =
                                state.LoadedAssembly refedName
                                |> Option.defaultWith (fun () ->
                                    failwith $"%s{operation}: the module of %s{name}, %s{refedName}, is not loaded"
                                )

                            let request = AssemblyRefRequest.ofDefinition (refed.PeReader.GetMetadataReader ())
                            let assemblyRef, scope = EmitScopeReferences.defineAssemblyRef request held
                            ResolutionScope.AssemblyRef assemblyRef, scope, Some (assemblyRef, refed)

                    let typeRef, extended =
                        EmitScopeReferences.defineTypeRefByName resolution name scope

                    let state =
                        // Found rather than added, so there is nothing to re-read.
                        if Object.ReferenceEquals (extended, held) then
                            state
                        else
                            state.WithEmitScope ctx.LoggerFactory moduleName extended |> fst

                    // `ModuleBuilder_GetTypeRef` records the assembly in the module's rid map, where a row
                    // already recorded keeps its first assembly.
                    let state =
                        match emittedFor with
                        | None -> state
                        | Some (assemblyRef, refed) -> state.WithAssemblyRefTarget moduleName assemblyRef refed

                    (typeRefTable <<< 24) ||| typeRef.Rid, state

            state
            |> IlMachineState.pushToEvalStack (CliType.Numeric (CliNumericType.Int32 token)) ctx.Thread
            |> NativeHandlerResult.completed
            |> Some
        | "ModuleBuilder_GetMemberRef",
          "System.Private.CoreLib",
          "System.Reflection.Emit",
          "RuntimeModuleBuilder",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", moduleGenerics)
            CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", refedGenerics)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32 ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) when
            moduleGenerics.IsEmpty && refedGenerics.IsEmpty
            ->
            let operation = "ModuleBuilder_GetMemberRef"

            if instruction.Arguments.Length <> 4 then
                failwith $"%s{operation}: expected four native arguments, got %d{instruction.Arguments.Length}"

            let moduleName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            let held =
                match state.TypeSystem._LoadedAssemblies.TryDynamicScope moduleName with
                | Some scope -> scope
                | None ->
                    failwith
                        $"%s{operation}: %s{moduleName} is not a dynamic assembly, and only a RuntimeModuleBuilder's module reaches this QCall"

            let refedName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[1] |> EvalStackValue.ofCliType)

            let tr = NativeCall.int32Argument operation instruction.Arguments.[2]
            let defToken = NativeCall.int32Argument operation instruction.Arguments.[3]

            let refed =
                state.LoadedAssembly refedName
                |> Option.defaultWith (fun () -> failwith $"%s{operation}: %s{refedName} is not loaded")

            let reader = refed.PeReader.GetMetadataReader ()
            let rid = defToken &&& 0x00FFFFFF

            // The member's name and signature, as CoreCLR reads them from the module the managed caller names
            // as the defining one: a MethodDef's for a MethodDef token, and a FieldDef's for any other. For a
            // `FieldBuilder` of a generic type that is the referring module, even when the field is another's.
            let name, declaringTypeHandle, signature, importOther, importOwn =
                if
                    defToken >>> 24 = methodDefTable
                    && rid >= 1
                    && rid <= reader.MethodDefinitions.Count
                then
                    let definition =
                        reader.GetMethodDefinition (MetadataTokens.MethodDefinitionHandle rid)

                    reader.GetString definition.Name,
                    definition.GetDeclaringType (),
                    reader.GetBlobContent definition.Signature,
                    EmitScopeReferences.importMethodSignature reader,
                    EmitScopeReferences.importOwnMethodSignature
                elif
                    defToken >>> 24 = fieldDefTable
                    && rid >= 1
                    && rid <= reader.FieldDefinitions.Count
                then
                    let definition =
                        reader.GetFieldDefinition (MetadataTokens.FieldDefinitionHandle rid)

                    reader.GetString definition.Name,
                    definition.GetDeclaringType (),
                    reader.GetBlobContent definition.Signature,
                    EmitScopeReferences.importFieldSignature reader,
                    EmitScopeReferences.importOwnFieldSignature
                else
                    failwith
                        $"TODO: %s{operation}: 0x%08x{defToken} names no MethodDef or FieldDef row of %s{refedName}; the managed callers pass a builder's own token, so only a user-derived builder passes another, for which CoreCLR's metadata lookup fails and the QCall throws, which PawPrint does not raise"

            let declaringType = reader.GetTypeDefinition declaringTypeHandle

            let described =
                $"%s{reader.GetString declaringType.Namespace}.%s{reader.GetString declaringType.Name}::%s{name} of %s{refedName}"

            // No assembly PawPrint loads is collectible, so the emitter's collectible-assembly check never
            // applies.
            let translated =
                if refedName = moduleName then
                    // A member of an instantiation of the module's own generic type, whose signature
                    // `ImportTypeDef` and `ImportTypeRef` translate into the module itself, recognising its scope
                    // by its MVID.
                    importOwn signature held |> ownSignature operation described moduleName, held
                else
                    importOther signature held |> translatedSignature operation described moduleName

            let parent =
                match typeRefOrSpecParent operation described moduleName held tr with
                | Some parent -> parent
                | None when tr >>> 24 = typeDefTable ->
                    failwith
                        $"TODO: %s{operation}: parent token 0x%08x{tr} for %s{described} is a TypeDef of %s{moduleName}, for which CoreCLR first makes a TypeRef (DefineTypeRefHelper); the managed callers pass a TypeRef for a builder of another module, so only a user-derived builder claiming a type of this module passes a TypeDef"
                | None ->
                    failwith
                        $"%s{operation}: parent token 0x%08x{tr} for %s{described} is not a TypeDef, TypeRef or TypeSpec, which are all the managed callers pass"

            let token, state = memberRefOf ctx state moduleName held parent name translated

            state
            |> IlMachineState.pushToEvalStack (CliType.Numeric (CliNumericType.Int32 token)) ctx.Thread
            |> NativeHandlerResult.completed
            |> Some
        | "ModuleBuilder_GetMemberRefOfMethodInfo",
          "System.Private.CoreLib",
          "System.Reflection.Emit",
          "RuntimeModuleBuilder",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", moduleGenerics)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32
            CorelibType state.TypeSystem.ConcreteTypes ("System", "RuntimeMethodHandleInternal", methodGenerics) ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) when
            moduleGenerics.IsEmpty && methodGenerics.IsEmpty
            ->
            let operation = "ModuleBuilder_GetMemberRefOfMethodInfo"

            if instruction.Arguments.Length <> 3 then
                failwith $"%s{operation}: expected three native arguments, got %d{instruction.Arguments.Length}"

            let moduleName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            let held =
                match state.TypeSystem._LoadedAssemblies.TryDynamicScope moduleName with
                | Some scope -> scope
                | None ->
                    failwith
                        $"%s{operation}: %s{moduleName} is not a dynamic assembly, and only a RuntimeModuleBuilder's module reaches this QCall"

            let tr = NativeCall.int32Argument operation instruction.Arguments.[1]

            let identity =
                NativeRuntimeMethodHandle.resolveMetadataIdentityFromArg operation state instruction.Arguments.[2]

            let declaringName = identity.GetAssemblyFullName ()

            let declaring =
                state.LoadedAssembly declaringName
                |> Option.defaultWith (fun () -> failwith $"%s{operation}: %s{declaringName} is not loaded")

            let reader = declaring.PeReader.GetMetadataReader ()

            let definition =
                reader.GetMethodDefinition (identity.GetMethodDefinitionHandle().Get)

            let name = reader.GetString definition.Name
            let declaringType = reader.GetTypeDefinition (definition.GetDeclaringType ())

            let described =
                $"%s{reader.GetString declaringType.Namespace}.%s{reader.GetString declaringType.Name}::%s{name} of %s{declaringName}"

            let token, state =
                if declaringName = moduleName then
                    // CoreCLR hands back the method's MethodDef token, whatever parent it was given: the
                    // managed caller asks only for a method of a created type, which is in the scope.
                    let token =
                        MetadataTokens.GetToken (
                            MethodDefinitionHandle.op_Implicit (identity.GetMethodDefinitionHandle().Get) : EntityHandle
                        )

                    match EmitScope.tryMethodDefRid (token &&& 0x00FFFFFF) held with
                    | Some _ -> token, state
                    | None ->
                        failwith
                            $"%s{operation}: %s{described} has MethodDef 0x%08x{token}, which names no MethodDef row of the module's scope"
                else
                    // An instantiated generic method's reference is to its definition: CoreCLR reads the name
                    // and signature of the MethodDesc's MethodDef. No assembly PawPrint loads is collectible, so
                    // the emitter's collectible-assembly check never applies.
                    let signature = reader.GetBlobContent definition.Signature

                    let translated =
                        EmitScopeReferences.importMethodSignature reader signature held
                        |> translatedSignature operation described moduleName

                    let parent =
                        match typeRefOrSpecParent operation described moduleName held tr with
                        | Some parent -> parent
                        | None ->
                            failwith
                                $"%s{operation}: parent token 0x%08x{tr} for %s{described} is neither a TypeRef nor a TypeSpec; the managed caller passes its declaring type's token, which is a TypeDef only for a type of this module"

                    memberRefOf ctx state moduleName held parent name translated

            state
            |> IlMachineState.pushToEvalStack (CliType.Numeric (CliNumericType.Int32 token)) ctx.Thread
            |> NativeHandlerResult.completed
            |> Some
        | "ModuleBuilder_GetMemberRefOfFieldInfo",
          "System.Private.CoreLib",
          "System.Reflection.Emit",
          "RuntimeModuleBuilder",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallModule", moduleGenerics)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32
            CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices",
                                                        "QCallTypeHandle",
                                                        typeGenerics)
            ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32 ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) when
            moduleGenerics.IsEmpty && typeGenerics.IsEmpty
            ->
            let operation = "ModuleBuilder_GetMemberRefOfFieldInfo"

            if instruction.Arguments.Length <> 4 then
                failwith $"%s{operation}: expected four native arguments, got %d{instruction.Arguments.Length}"

            let moduleName =
                NativeCall.qCallModuleToAssemblyFullName
                    operation
                    state
                    (instruction.Arguments.[0] |> EvalStackValue.ofCliType)

            let held =
                match state.TypeSystem._LoadedAssemblies.TryDynamicScope moduleName with
                | Some scope -> scope
                | None ->
                    failwith
                        $"%s{operation}: %s{moduleName} is not a dynamic assembly, and only a RuntimeModuleBuilder's module reaches this QCall"

            let tr = NativeCall.int32Argument operation instruction.Arguments.[1]
            let tkField = NativeCall.int32Argument operation instruction.Arguments.[3]

            if tkField >>> 24 <> fieldDefTable then
                failwith
                    $"%s{operation}: 0x%08x{tkField} is not a FieldDef token, which the managed caller passes as the RuntimeFieldInfo's MetadataToken"

            let token, state =
                if tr >>> 24 = typeDefTable then
                    // A type of the module itself, for which CoreCLR hands back the field's own FieldDef
                    // token unread. The managed caller passes a created type's TypeDef and its field's
                    // token, so a field the scope lacks, or one the parent does not own, is PawPrint's
                    // fault.
                    let fields = EmitScope.fields held
                    let rid = tkField &&& 0x00FFFFFF

                    if rid < 1 || rid > fields.Count then
                        failwith
                            $"%s{operation}: 0x%08x{tkField} names no field of %s{moduleName}, though its parent 0x%08x{tr} is a TypeDef of the module"

                    if fields.[rid - 1].Owner.Rid <> (tr &&& 0x00FFFFFF) then
                        failwith
                            $"%s{operation}: field 0x%08x{tkField} of %s{moduleName} belongs to TypeDef %d{fields.[rid - 1].Owner.Rid}, not to its parent 0x%08x{tr}"

                    tkField, state
                else
                    // The field is read from the module of its declaring type, which for an instantiation is its
                    // definition's.
                    let declaringName =
                        NativeCall.typeAssemblyFullName
                            operation
                            ctx.BaseClassTypes
                            state
                            (NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget
                                operation
                                state
                                (instruction.Arguments.[2] |> EvalStackValue.ofCliType))

                    let declaring =
                        state.LoadedAssembly declaringName
                        |> Option.defaultWith (fun () -> failwith $"%s{operation}: %s{declaringName} is not loaded")

                    let reader = declaring.PeReader.GetMetadataReader ()
                    let fieldRid = tkField &&& 0x00FFFFFF

                    if fieldRid < 1 || fieldRid > reader.FieldDefinitions.Count then
                        failwith $"%s{operation}: 0x%08x{tkField} names no field of %s{declaringName}"

                    let definition =
                        reader.GetFieldDefinition (MetadataTokens.FieldDefinitionHandle fieldRid)

                    let name = reader.GetString definition.Name
                    let declaringType = reader.GetTypeDefinition (definition.GetDeclaringType ())

                    let described =
                        $"%s{reader.GetString declaringType.Namespace}.%s{reader.GetString declaringType.Name}::%s{name} of %s{declaringName}"

                    let signature = reader.GetBlobContent definition.Signature

                    // No assembly PawPrint loads is collectible, so the emitter's collectible-assembly check
                    // never applies.
                    let translated =
                        if declaringName = moduleName then
                            // A field of an instantiation of the module's own created generic type, whose
                            // signature `ImportTypeDef` and `ImportTypeRef` translate into the module itself,
                            // recognising its scope by its MVID.
                            EmitScopeReferences.importOwnFieldSignature signature held
                            |> ownSignature operation described moduleName,
                            held
                        else
                            EmitScopeReferences.importFieldSignature reader signature held
                            |> translatedSignature operation described moduleName

                    let parent =
                        match typeRefOrSpecParent operation described moduleName held tr with
                        | Some parent -> parent
                        | None ->
                            failwith
                                $"%s{operation}: parent token 0x%08x{tr} for %s{described} is not a TypeDef, TypeRef or TypeSpec, which are all the managed caller passes"

                    memberRefOf ctx state moduleName held parent name translated

            state
            |> IlMachineState.pushToEvalStack (CliType.Numeric (CliNumericType.Int32 token)) ctx.Thread
            |> NativeHandlerResult.completed
            |> Some
        | _ -> None
