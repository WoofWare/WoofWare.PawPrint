namespace WoofWare.PawPrint

open System
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Security.Cryptography
open System.Text

/// The strong-name facts about public key blobs that CoreCLR checks before it hashes one.
[<RequireQualifiedAccess>]
module StrongName =
    let private ecmaKey : ImmutableArray<byte> =
        ImmutableArray.Create<byte> (Convert.FromHexString "00000000000000000400000000000000")

    /// <summary>
    /// <c>StrongNameIsValidPublicKey</c> (md/runtime/strongnameinternal.cpp): does this blob look like a
    /// public key, in the sense the strong-name code checks before hashing one? CoreCLR's
    /// <c>StrongNameTokenFromPublicKey</c> refuses any other with <c>CORSEC_E_INVALID_PUBLICKEY</c>, which
    /// its callers throw as a <c>SecurityException</c>.
    /// </summary>
    /// <remarks>
    /// The blob is a <c>PublicKeyBlob</c>: a signature algorithm, a hash algorithm and a key length as
    /// little-endian 32-bit words, then the CryptoAPI key bytes. It must be at least the unpacked struct's
    /// sixteen bytes, its key length must account for everything after the twelve-byte header, an
    /// algorithm that is given must be of the right class (and the hash no weaker than SHA-1), and the key
    /// must announce itself as a <c>PUBLICKEYBLOB</c>. The sixteen-byte ECMA key passes without those
    /// checks, since it is not a real key.
    /// </remarks>
    let isValidPublicKey (blob : ImmutableArray<byte>) : bool =
        if blob.AsSpan().SequenceEqual (ecmaKey.AsSpan ()) then
            true
        elif blob.Length < 16 then
            false
        else
            let word (offset : int) : uint32 =
                uint32 blob.[offset]
                ||| (uint32 blob.[offset + 1] <<< 8)
                ||| (uint32 blob.[offset + 2] <<< 16)
                ||| (uint32 blob.[offset + 3] <<< 24)

            let sigAlgId = word 0
            let hashAlgId = word 4
            let keyLength = word 8

            // GET_ALG_CLASS keeps bits 13-15; GET_ALG_SID keeps the low nine. ALG_CLASS_HASH is
            // 4 << 13, ALG_CLASS_SIGNATURE is 1 << 13, ALG_SID_SHA1 is 4 and PUBLICKEYBLOB is 6.
            let algClass (id : uint32) = id &&& (7u <<< 13)
            let algSid (id : uint32) = id &&& 511u

            keyLength = uint32 (blob.Length - 12)
            && (hashAlgId = 0u || (algClass hashAlgId = (4u <<< 13) && algSid hashAlgId >= 4u))
            && (sigAlgId = 0u || algClass sigAlgId = (1u <<< 13))
            && blob.[12] = 6uy

/// <summary>
/// What CoreCLR's emitter is asked to reference when a dynamic module refers to a type of another
/// assembly: the arguments <c>AssemblySpec::EmitToken</c> passes to <c>DefineAssemblyRef</c>, read
/// from that assembly's own <c>Assembly</c> row.
/// </summary>
type AssemblyRefRequest =
    {
        Name : string
        /// As the referenced assembly's row holds it. A component of 65535 means "not given" to the
        /// emitter, which leaves that column of a new row 0.
        Version : MetadataVersion
        Culture : string
        /// The referenced assembly's public key token, or empty if it has no public key: the emitter
        /// never writes a full key into a reference it makes for a type.
        PublicKeyToken : ImmutableArray<byte>
        Flags : AssemblyFlags
    }

/// Reading the reference CoreCLR's emitter makes to an assembly from that assembly's metadata.
[<RequireQualifiedAccess>]
module AssemblyRefRequest =
    /// <summary>
    /// The token of a public key (ECMA-335 II.6.2.1.3): the last eight bytes of the key's SHA-1, in
    /// reverse order, as CoreCLR's <c>StrongNameTokenFromPublicKey</c> computes it.
    /// </summary>
    let publicKeyToken (publicKey : ImmutableArray<byte>) : ImmutableArray<byte> =
        if publicKey.IsDefaultOrEmpty then
            invalidArg (nameof publicKey) "an assembly with no public key has no public key token"

        if not (StrongName.isValidPublicKey publicKey) then
            invalidArg
                (nameof publicKey)
                $"a %d{publicKey.Length}-byte blob that StrongNameIsValidPublicKey rejects has no token: CoreCLR refuses it with CORSEC_E_INVALID_PUBLICKEY"

        let hash = SHA1.HashData (publicKey.AsSpan ())
        ImmutableArray.CreateRange (Seq.init 8 (fun i -> hash.[hash.Length - 1 - i]))

    /// <summary>
    /// The reference CoreCLR's emitter makes to the assembly whose metadata <paramref name="reader"/>
    /// reads: <c>AssemblySpec::InitializeSpec</c> over its <c>Assembly</c> row, with the key reduced to
    /// its token as <c>AssemblySpec::EmitToken</c> does.
    /// </summary>
    let ofDefinition (reader : MetadataReader) : AssemblyRefRequest =
        let definition = reader.GetAssemblyDefinition ()
        let key = reader.GetBlobContent definition.PublicKey

        {
            Name = reader.GetString definition.Name
            Version =
                {
                    Major = uint16 definition.Version.Major
                    Minor = uint16 definition.Version.Minor
                    Build = uint16 definition.Version.Build
                    Revision = uint16 definition.Version.Revision
                }
            Culture = reader.GetString definition.Culture
            PublicKeyToken =
                if key.IsEmpty then
                    ImmutableArray.Empty
                else
                    publicKeyToken key
            // `InitializeSpec` marks a row with a key as holding one, and `EmitToken` passes the
            // token instead, unmarked.
            Flags =
                if key.IsEmpty then
                    definition.Flags
                else
                    definition.Flags &&& ~~~AssemblyFlags.PublicKey
        }

/// What an <c>AssemblyRef</c> row says identifies an assembly's key: nothing, the key's token, or the
/// full key. A row says which by its <c>PublicKey</c> flag, which only key bytes give meaning to.
[<RequireQualifiedAccess>]
type PublicKeyOrToken =
    | Absent
    | Token of ImmutableArray<byte>
    | Key of ImmutableArray<byte>

/// <summary>
/// An <c>AssemblyRef</c> of another module, as CoreCLR's
/// <c>ImportHelper::CreateAssemblyRefFromAssemblyRef</c> copies it into a dynamic module: every column as
/// it is.
/// </summary>
type AssemblyRefCopy =
    {
        Name : string
        Version : MetadataVersion
        Culture : string
        PublicKeyOrToken : PublicKeyOrToken
        /// The row's flags without <c>PublicKey</c>, which a copy sets exactly for a full key.
        Flags : AssemblyFlags
        HashValue : ImmutableArray<byte>
    }

/// Reading an <c>AssemblyRef</c> of another module as the emitter copies it.
[<RequireQualifiedAccess>]
module AssemblyRefCopy =
    /// <summary>The <c>AssemblyRef</c> <paramref name="handle"/> of the module <paramref name="reader"/> reads.</summary>
    /// <exception cref="ArgumentException">
    /// The row is marked as holding a full key but holds no key bytes, which ECMA-335 II.22.5 does not
    /// allow and which a copy could not say.
    /// </exception>
    let ofReference (reader : MetadataReader) (handle : AssemblyReferenceHandle) : AssemblyRefCopy =
        let reference = reader.GetAssemblyReference handle
        let bytes = reader.GetBlobContent reference.PublicKeyOrToken
        let marked = reference.Flags.HasFlag AssemblyFlags.PublicKey

        let key =
            if bytes.IsEmpty then
                if marked then
                    invalidArg
                        (nameof handle)
                        $"AssemblyRef %d{MetadataTokens.GetRowNumber (AssemblyReferenceHandle.op_Implicit handle : EntityHandle)} (%s{reader.GetString reference.Name}) is marked as holding a full public key, but holds no key bytes"

                PublicKeyOrToken.Absent
            elif marked then
                PublicKeyOrToken.Key bytes
            else
                PublicKeyOrToken.Token bytes

        {
            Name = reader.GetString reference.Name
            Version =
                {
                    Major = uint16 reference.Version.Major
                    Minor = uint16 reference.Version.Minor
                    Build = uint16 reference.Version.Build
                    Revision = uint16 reference.Version.Revision
                }
            Culture = reader.GetString reference.Culture
            PublicKeyOrToken = key
            Flags = reference.Flags &&& ~~~AssemblyFlags.PublicKey
            HashValue = reader.GetBlobContent reference.HashValue
        }

/// A token in another module's signature that <c>EmitScopeReferences.importMethodSignature</c> or
/// <c>importFieldSignature</c> does not translate into the dynamic module, the first the walk meets.
[<RequireQualifiedAccess>]
type SignatureTokenRefusal =
    /// A TypeRef of the source module whose outermost resolution scope is nil, which CoreCLR's
    /// <c>ImportTypeRef</c> looks up among the source assembly's ExportedTypes.
    | TypeRefWithoutScope of TypeReferenceHandle
    /// A TypeRef of the source module whose outermost resolution scope is the source module itself,
    /// which CoreCLR scopes by the AssemblyRef it makes for the source assembly.
    | TypeRefOfModule of TypeReferenceHandle
    /// A TypeRef of the source module whose outermost resolution scope is a ModuleRef, which CoreCLR
    /// scopes by the AssemblyRef it makes for the source assembly.
    | TypeRefOfModuleRef of TypeReferenceHandle
    /// A TypeRef of the source module whose outermost resolution scope is an AssemblyRef naming the
    /// emitting assembly, which CoreCLR makes name a type of the emitting module.
    | TypeRefOfEmittingAssembly of TypeReferenceHandle
    /// A TypeRef of the source module whose outermost resolution scope is an AssemblyRef holding a full
    /// key that <c>StrongNameIsValidPublicKey</c> rejects. CoreCLR copies such a reference, but throws a
    /// <c>SecurityException</c> from any later search that reduces the key to its token, which PawPrint
    /// does not model, so it refuses to copy it at all.
    | TypeRefOfInvalidPublicKey of TypeReferenceHandle
    /// A TypeSpec of the source module, which CoreCLR translates in turn and finds or adds.
    | TypeSpec of TypeSpecificationHandle
    /// A token whose tag names no table (<c>mdtBaseType</c>), given as the encoded token.
    | NoTable of coded : int
    /// A TypeDef of an unsigned source assembly, named here, to which the scope has no AssemblyRef.
    /// CoreCLR would add one holding eight uninitialised bytes as its token. The managed caller always
    /// asks for the declaring type's TypeRef first, which makes the AssemblyRef, so it never reaches this.
    | UnreferencedUnsignedAssembly of assembly : string

/// What CoreCLR's emitter writes into a dynamic module for a method or field signature read from another
/// module (<c>TranslateSigWithScope</c>).
[<RequireQualifiedAccess>]
[<NoEquality>]
[<NoComparison>]
type SignatureImport =
    /// The bytes the emitter writes, and the scope with the AssemblyRef and TypeRefs the translation
    /// added, which is the scope it was given if it added none.
    | Imported of signature : ImmutableArray<byte> * scope : EmitScope
    /// The signature names a type by a token the translation does not handle yet, and nothing is added.
    | Refused of SignatureTokenRefusal

/// A token in a dynamic module's own signature that <c>EmitScopeReferences.importOwnMethodSignature</c> or
/// <c>importOwnFieldSignature</c> does not translate into the module itself, the first the walk meets.
[<RequireQualifiedAccess>]
type OwnSignatureRefusal =
    /// A TypeSpec of the scope, whose signature CoreCLR translates in turn and finds or adds by its
    /// translated bytes.
    | TypeSpec of TypeSpecRid
    /// A token whose tag names no table (<c>mdtBaseType</c>), given as the encoded token.
    | NoTable of coded : int

/// <summary>
/// The references CoreCLR's emitter adds to a dynamic module's scope, each found among the rows the
/// scope already has before one is added, as the emitter checks for duplicates in a dynamic module.
/// </summary>
[<RequireQualifiedAccess>]
module EmitScopeReferences =
    /// <summary>
    /// A type name as <c>ModuleBuilder_GetTypeRef</c> stores it: each backslash that escapes one of the
    /// characters a type name reserves (<c>, [ ] &amp; * + \</c>) is dropped, and the character after
    /// it kept. Every other character, a backslash before any other character included, is kept.
    /// </summary>
    let unescapeTypeName (name : string) : string =
        let result = StringBuilder name.Length
        let mutable i = 0

        while i < name.Length do
            if
                name.[i] = '\\'
                && i + 1 < name.Length
                && TypeNameEscaping.isReserved name.[i + 1]
            then
                result.Append name.[i + 1] |> ignore<StringBuilder>
                i <- i + 2
            else
                result.Append name.[i] |> ignore<StringBuilder>
                i <- i + 1

        result.ToString ()

    /// <summary>
    /// The namespace and name a <c>TypeRef</c> row holds for a full type name, as CoreCLR's
    /// <c>ns::SplitPath</c> divides it: at the last <c>.</c>, unless that is the name's first
    /// character, and at the <c>.</c> before it if there is one, so that <c>A..ctor</c> is the name
    /// <c>.ctor</c> in the namespace <c>A</c>. With no such <c>.</c>, the namespace is empty.
    /// </summary>
    let splitTypeName (fullName : string) : string * string =
        let last = fullName.LastIndexOf '.'

        if last <= 0 then
            "", fullName
        else
            let separator = if fullName.[last - 1] = '.' then last - 1 else last
            fullName.Substring (0, separator), fullName.Substring (separator + 1)

    /// <summary>
    /// The first <c>AssemblyRef</c> row with this name, culture, version and key, as CoreCLR's
    /// <c>ImportHelper::FindAssemblyRef</c> finds it. Build and revision are not compared for a row named
    /// <c>mscorlib</c> or <c>Microsoft.VisualC</c>, in any case. Of the flags, only <c>PublicKey</c> is
    /// read, and only of a row holding key bytes: a full key and a token are compared by reducing the key
    /// to its token.
    /// </summary>
    let private findAssemblyRef
        (name : string)
        (culture : string)
        (version : MetadataVersion)
        (key : PublicKeyOrToken)
        (scope : EmitScope)
        : AssemblyRefRid option
        =
        let same (x : ImmutableArray<byte>) (y : ImmutableArray<byte>) : bool = x.AsSpan().SequenceEqual (y.AsSpan ())

        let matches (row : AssemblyRefRow) : bool =
            let rowName = EmitScope.stringAt row.Name scope
            let held = EmitScope.blobAt row.PublicKeyOrToken scope
            let heldIsKey = row.Flags.HasFlag AssemblyFlags.PublicKey

            let unified =
                String.Equals (rowName, "mscorlib", StringComparison.OrdinalIgnoreCase)
                || String.Equals (rowName, "microsoft.visualc", StringComparison.OrdinalIgnoreCase)

            let keyMatches =
                match key with
                | PublicKeyOrToken.Absent -> held.IsEmpty
                | PublicKeyOrToken.Token token ->
                    not held.IsEmpty
                    && (if heldIsKey then
                            same (AssemblyRefRequest.publicKeyToken held) token
                        else
                            same held token)
                | PublicKeyOrToken.Key publicKey ->
                    not held.IsEmpty
                    && (if heldIsKey then
                            same held publicKey
                        else
                            same held (AssemblyRefRequest.publicKeyToken publicKey))

            String.Equals (rowName, name, StringComparison.Ordinal)
            && String.Equals (EmitScope.stringAt row.Culture scope, culture, StringComparison.Ordinal)
            && row.Version.Major = version.Major
            && row.Version.Minor = version.Minor
            && (unified
                || row.Version.Build = version.Build && row.Version.Revision = version.Revision)
            && keyMatches

        EmitScope.assemblyRefs scope
        |> Seq.tryFindIndex matches
        |> Option.map (fun index -> AssemblyRefRid (index + 1))

    /// <summary>
    /// The <c>AssemblyRef</c> row that CoreCLR's <c>ImportHelper::FindAssemblyRef</c> finds for
    /// <paramref name="request"/>: the first with the same name, culture, version and public key token,
    /// comparing a row holding a full key by its token. Build and revision are not compared for a row
    /// named <c>mscorlib</c> or <c>Microsoft.VisualC</c>, in any case.
    /// </summary>
    let tryFindAssemblyRef (request : AssemblyRefRequest) (scope : EmitScope) : AssemblyRefRid option =
        let key =
            if request.PublicKeyToken.IsDefaultOrEmpty then
                PublicKeyOrToken.Absent
            else
                PublicKeyOrToken.Token request.PublicKeyToken

        findAssemblyRef request.Name request.Culture request.Version key scope

    let private storedVersion (version : MetadataVersion) : MetadataVersion =
        let given (part : uint16) : uint16 =
            if part = UInt16.MaxValue then 0us else part

        {
            Major = given version.Major
            Minor = given version.Minor
            Build = given version.Build
            Revision = given version.Revision
        }

    let private storedFlags (flags : AssemblyFlags) : AssemblyFlags =
        // `afPA_Specified` and `afPA_FullMask`, which `PrepareForSaving` reads.
        let architectureSpecified = enum<AssemblyFlags> 0x0080
        let architecture = enum<AssemblyFlags> 0x00F0

        if flags.HasFlag architectureSpecified then
            flags &&& ~~~architectureSpecified
        else
            flags &&& ~~~architecture

    /// <summary>
    /// The <c>AssemblyRef</c> row for <paramref name="request"/>: the one <c>tryFindAssemblyRef</c>
    /// finds, or a new one, as <c>RegMeta::DefineAssemblyRef</c> adds it. A new row's version
    /// components are the request's, each 65535 stored as 0; its flags are the request's, with the
    /// processor-architecture bits cleared unless the request marks them specified, and that mark
    /// cleared (<c>PrepareForSaving</c>); and its hash value is empty.
    /// </summary>
    let defineAssemblyRef (request : AssemblyRefRequest) (scope : EmitScope) : AssemblyRefRid * EmitScope =
        match tryFindAssemblyRef request scope with
        | Some row -> row, scope
        | None ->
            // `_SetAssemblyRefProps` puts the token, the name, the culture and the hash, in that order.
            let token, scope = EmitScope.internBlob request.PublicKeyToken scope
            let name, scope = EmitScope.internString request.Name scope
            let culture, scope = EmitScope.internString request.Culture scope
            let hash, scope = EmitScope.internBlob ImmutableArray.Empty scope

            EmitScope.addAssemblyRef
                {
                    Version = storedVersion request.Version
                    Flags = storedFlags request.Flags
                    PublicKeyOrToken = token
                    Name = name
                    Culture = culture
                    HashValue = hash
                }
                scope

    /// <summary>
    /// Whether <paramref name="row"/> is a row that <c>defineAssemblyRef</c> could have returned for
    /// <paramref name="request"/>: the one <c>tryFindAssemblyRef</c> finds, or a row holding what it
    /// adds. These differ for a version component of 65535, which a new row stores as 0, so that the
    /// row it adds is never found again.
    /// </summary>
    let isAssemblyRefFor (request : AssemblyRefRequest) (row : AssemblyRefRid) (scope : EmitScope) : bool =
        if tryFindAssemblyRef request scope = Some row then
            true
        else
            match EmitScope.tryAssemblyRefRid row.Rid scope with
            | None -> false
            | Some _ ->
                let held = (EmitScope.assemblyRefs scope).[row.Rid - 1]

                String.Equals (EmitScope.stringAt held.Name scope, request.Name, StringComparison.Ordinal)
                && String.Equals (EmitScope.stringAt held.Culture scope, request.Culture, StringComparison.Ordinal)
                && held.Version = storedVersion request.Version
                && held.Flags = storedFlags request.Flags
                && (EmitScope.blobAt held.PublicKeyOrToken scope)
                    .AsSpan()
                    .SequenceEqual (request.PublicKeyToken.AsSpan ())
                && (EmitScope.blobAt held.HashValue scope).IsEmpty

    /// <summary>
    /// The <c>TypeRef</c> row with this resolution scope, namespace and name, compared ordinally, as
    /// CoreCLR's <c>ImportHelper::FindTypeRefByName</c> finds it.
    /// </summary>
    let tryFindTypeRef
        (resolutionScope : ResolutionScope)
        (ns : string)
        (name : string)
        (scope : EmitScope)
        : TypeRefRid option
        =
        // Equal strings are one heap entry, so a row matches exactly when it holds these offsets.
        match EmitScope.tryStringOffset ns scope, EmitScope.tryStringOffset name scope with
        | Some ns, Some name ->
            EmitScope.typeRefs scope
            |> Seq.tryFindIndex (fun row ->
                row.ResolutionScope = resolutionScope && row.Namespace = ns && row.Name = name
            )
            |> Option.map (fun index -> TypeRefRid (index + 1))
        | _ -> None

    /// <summary>
    /// The <c>TypeRef</c> row for a full type name in <paramref name="resolutionScope"/>, divided by
    /// <c>splitTypeName</c>: the one <c>tryFindTypeRef</c> finds, or a new one, as
    /// <c>RegMeta::DefineTypeRefByName</c> adds it, interning the namespace before the name.
    /// </summary>
    let defineTypeRefByName
        (resolutionScope : ResolutionScope)
        (fullName : string)
        (scope : EmitScope)
        : TypeRefRid * EmitScope
        =
        let ns, name = splitTypeName fullName

        match tryFindTypeRef resolutionScope ns name scope with
        | Some row -> row, scope
        | None ->
            let ns, scope = EmitScope.internString ns scope
            let name, scope = EmitScope.internString name scope

            EmitScope.addTypeRef
                {
                    ResolutionScope = resolutionScope
                    Name = name
                    Namespace = ns
                }
                scope

    /// `CompareMemberRefs` skips the name or the signature when it is empty, and the linear search
    /// `FindMemberRef` falls back to differs from the hashed one there; a method's name and signature
    /// are never empty, so neither is admitted.
    let private requireMemberRefKey (operation : string) (name : string) (signature : ImmutableArray<byte>) : unit =
        if String.IsNullOrEmpty name then
            invalidArg (nameof name) $"%s{operation}: a member reference's name is never empty"

        if signature.IsDefaultOrEmpty then
            invalidArg (nameof signature) $"%s{operation}: a member reference's signature is never empty"

    /// <summary>
    /// The <c>MemberRef</c> row with this parent, name and signature, as CoreCLR's
    /// <c>CMiniMdRW::CompareMemberRefs</c> finds it: the first whose parent is the same, whose name is
    /// the same UTF-8, and whose signature is the same bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The name or the signature is empty.</exception>
    let tryFindMemberRef
        (parent : MemberRefParent)
        (name : string)
        (signature : ImmutableArray<byte>)
        (scope : EmitScope)
        : MemberRefRid option
        =
        requireMemberRefKey "EmitScopeReferences.tryFindMemberRef" name signature

        // Equal strings, and equal blobs, are one heap entry each, so a row matches exactly when it
        // holds these offsets.
        match EmitScope.tryStringOffset name scope, EmitScope.tryBlobOffset signature scope with
        | Some name, Some signature ->
            EmitScope.memberRefs scope
            |> Seq.tryFindIndex (fun row -> row.Parent = parent && row.Name = name && row.Signature = signature)
            |> Option.map (fun index -> MemberRefRid (index + 1))
        | _ -> None

    /// <summary>
    /// The <c>MemberRef</c> row for a member of <paramref name="parent"/>: the one
    /// <c>tryFindMemberRef</c> finds, or a new one, as <c>RegMeta::DefineMemberRef</c> adds it in a
    /// dynamic module, whose emitter checks for duplicate member references. A new row interns the
    /// name before the signature.
    /// </summary>
    /// <exception cref="ArgumentException">The name or the signature is empty.</exception>
    let defineMemberRef
        (parent : MemberRefParent)
        (name : string)
        (signature : ImmutableArray<byte>)
        (scope : EmitScope)
        : MemberRefRid * EmitScope
        =
        match tryFindMemberRef parent name signature scope with
        | Some row -> row, scope
        | None ->
            let name, scope = EmitScope.internString name scope
            let signature, scope = EmitScope.internBlob signature scope

            EmitScope.addMemberRef
                {
                    Parent = parent
                    Name = name
                    Signature = signature
                }
                scope

    /// <summary>
    /// The <c>StandAloneSig</c> row holding <paramref name="signature"/>: the first with the same bytes,
    /// as <c>ImportHelper::FindStandAloneSig</c> finds it in a dynamic module, whose emitter checks for
    /// duplicate signatures, or a new one, as <c>RegMeta::GetTokenFromSig</c> adds it.
    /// </summary>
    /// <exception cref="ArgumentException">The signature is empty.</exception>
    let defineStandAloneSig (signature : ImmutableArray<byte>) (scope : EmitScope) : StandAloneSigRid * EmitScope =
        if signature.IsDefaultOrEmpty then
            invalidArg (nameof signature) "a StandAloneSig's signature is never empty"

        // Equal blobs are one heap entry, so a row matches exactly when it holds this offset.
        let existing =
            EmitScope.tryBlobOffset signature scope
            |> Option.bind (fun offset ->
                EmitScope.standAloneSigs scope
                |> Seq.tryFindIndex (fun row -> row.Signature = offset)
            )

        match existing with
        | Some index -> StandAloneSigRid (index + 1), scope
        | None ->
            let offset, scope = EmitScope.internBlob signature scope

            EmitScope.addStandAloneSig
                {
                    Signature = offset
                }
                scope

    /// <summary>
    /// The <c>TypeSpec</c> row holding <paramref name="signature"/>: the first with the same bytes, as
    /// <c>ImportHelper::FindTypeSpec</c> finds it in a dynamic module, whose emitter checks for
    /// duplicate TypeSpecs (<c>MDDupTypeSpec</c>), or a new one, as <c>RegMeta::GetTokenFromTypeSpec</c>
    /// adds it.
    /// </summary>
    /// <exception cref="ArgumentException">The signature is empty.</exception>
    let defineTypeSpec (signature : ImmutableArray<byte>) (scope : EmitScope) : TypeSpecRid * EmitScope =
        if signature.IsDefaultOrEmpty then
            invalidArg (nameof signature) "a TypeSpec's signature is never empty"

        // Equal blobs are one heap entry, so a row matches exactly when it holds this offset.
        let existing =
            EmitScope.tryBlobOffset signature scope
            |> Option.bind (fun offset ->
                EmitScope.typeSpecs scope
                |> Seq.tryFindIndex (fun row -> row.Signature = offset)
            )

        match existing with
        | Some index -> (EmitScope.tryTypeSpecRid (index + 1) scope).Value, scope
        | None ->
            let offset, scope = EmitScope.internBlob signature scope

            EmitScope.addTypeSpec
                {
                    Signature = offset
                }
                scope

    /// <summary>
    /// The <c>MethodSpec</c> row instantiating <paramref name="method"/> as
    /// <paramref name="instantiation"/> says: the first with the same method and the same bytes, as
    /// <c>ImportHelper::FindMethodSpecByMethodAndInstantiation</c> finds it in a dynamic module, whose
    /// emitter checks for duplicate MethodSpecs (<c>MDDupMethodSpec</c>), or a new one, as
    /// <c>RegMeta::DefineMethodSpec</c> adds it.
    /// </summary>
    /// <exception cref="ArgumentException">The instantiation is empty, or the scope has no such method.</exception>
    let defineMethodSpec
        (method : MethodDefOrRef)
        (instantiation : ImmutableArray<byte>)
        (scope : EmitScope)
        : MethodSpecRid * EmitScope
        =
        if instantiation.IsDefaultOrEmpty then
            invalidArg (nameof instantiation) "a MethodSpec's instantiation is never empty"

        // Equal blobs are one heap entry, so a row matches exactly when it holds this offset.
        let existing =
            EmitScope.tryBlobOffset instantiation scope
            |> Option.bind (fun offset ->
                EmitScope.methodSpecs scope
                |> Seq.tryFindIndex (fun row -> row.Method = method && row.Instantiation = offset)
            )

        match existing with
        | Some index -> MethodSpecRid (index + 1), scope
        | None ->
            let offset, scope = EmitScope.internBlob instantiation scope

            EmitScope.addMethodSpec
                {
                    Method = method
                    Instantiation = offset
                }
                scope

    /// The bytes of a compressed unsigned integer (ECMA-335 II.23.2) in its shortest form.
    let private compressed (value : int) : byte list =
        if value < 0x80 then
            [ byte value ]
        elif value < 0x4000 then
            [ byte (0x80 ||| (value >>> 8)) ; byte value ]
        else
            [
                byte (0xC0 ||| (value >>> 24))
                byte (value >>> 16)
                byte (value >>> 8)
                byte value
            ]

    /// <summary>
    /// The <c>AssemblyRef</c> row CoreCLR's <c>ImportHelper::CreateAssemblyRefFromAssemblyRef</c> gives
    /// for <paramref name="copy"/>: the one <c>FindAssemblyRef</c> finds, or a new one holding every
    /// column as it is, marked <c>PublicKey</c> exactly for a full key.
    /// </summary>
    let defineAssemblyRefCopy (copy : AssemblyRefCopy) (scope : EmitScope) : AssemblyRefRid * EmitScope =
        match findAssemblyRef copy.Name copy.Culture copy.Version copy.PublicKeyOrToken scope with
        | Some row -> row, scope
        | None ->
            let bytes, flags =
                match copy.PublicKeyOrToken with
                | PublicKeyOrToken.Absent -> ImmutableArray.Empty, copy.Flags
                | PublicKeyOrToken.Token token -> token, copy.Flags
                | PublicKeyOrToken.Key key -> key, copy.Flags ||| AssemblyFlags.PublicKey

            // `CreateAssemblyRefFromAssemblyRef` puts the key or token, the name, the culture and the
            // hash, in that order.
            let bytes, scope = EmitScope.internBlob bytes scope
            let name, scope = EmitScope.internString copy.Name scope
            let culture, scope = EmitScope.internString copy.Culture scope
            let hash, scope = EmitScope.internBlob copy.HashValue scope

            EmitScope.addAssemblyRef
                {
                    Version = copy.Version
                    Flags = flags
                    PublicKeyOrToken = bytes
                    Name = name
                    Culture = culture
                    HashValue = hash
                }
                scope

    /// <summary>
    /// Whether <paramref name="reference"/> names the assembly whose <c>Assembly</c> row the scope holds,
    /// as CoreCLR's <c>ImportHelper::CompareAssemblyRefToAssembly</c> decides it: the same name, culture
    /// and four version parts, and, where either holds a key, a token equal to the definition's key's.
    /// A reference holding a full key never names it, since after comparing the keys the function
    /// compares the reference's bytes with the definition's token too. False for a scope with no
    /// <c>Assembly</c> row, for which CoreCLR does not compare.
    /// </summary>
    let private namesEmittingAssembly (reference : AssemblyRefCopy) (scope : EmitScope) : bool =
        match EmitScope.assembly scope with
        | None -> false
        | Some definition ->
            let definitionKey = EmitScope.blobAt definition.PublicKey scope

            definition.Version = reference.Version
            && String.Equals (EmitScope.stringAt definition.Name scope, reference.Name, StringComparison.Ordinal)
            && String.Equals (EmitScope.stringAt definition.Culture scope, reference.Culture, StringComparison.Ordinal)
            && (
                match reference.PublicKeyOrToken with
                | PublicKeyOrToken.Absent -> definitionKey.IsEmpty
                | PublicKeyOrToken.Key _ -> false
                | PublicKeyOrToken.Token token ->
                    not definitionKey.IsEmpty
                    && (AssemblyRefRequest.publicKeyToken definitionKey).AsSpan().SequenceEqual (token.AsSpan ())
            )

    /// <summary>
    /// The <c>AssemblyRef</c> row that CoreCLR's <c>ImportHelper::CreateAssemblyRefFromAssembly</c>
    /// gives for the assembly <paramref name="request"/> was read from: the one
    /// <c>tryFindAssemblyRef</c> finds, or a new one holding the request's version and flags as they are
    /// (unlike <c>defineAssemblyRef</c>'s), its token, and an empty hash. <c>None</c> for an unsigned
    /// assembly that the scope has no row for, for which CoreCLR writes an uninitialised token.
    /// </summary>
    let private assemblyRefFromAssembly
        (request : AssemblyRefRequest)
        (scope : EmitScope)
        : (AssemblyRefRid * EmitScope) option
        =
        match tryFindAssemblyRef request scope with
        | Some row -> Some (row, scope)
        | None when request.PublicKeyToken.IsDefaultOrEmpty -> None
        | None ->
            // `CreateAssemblyRefFromAssembly` puts the token, the name, the culture and the hash, in that
            // order.
            let token, scope = EmitScope.internBlob request.PublicKeyToken scope
            let name, scope = EmitScope.internString request.Name scope
            let culture, scope = EmitScope.internString request.Culture scope
            let hash, scope = EmitScope.internBlob ImmutableArray.Empty scope

            EmitScope.addAssemblyRef
                {
                    Version = request.Version
                    Flags = request.Flags
                    PublicKeyOrToken = token
                    Name = name
                    Culture = culture
                    HashValue = hash
                }
                scope
            |> Some

    /// <summary>
    /// The last <c>TypeRef</c> of a chain, given outermost first as namespaces and names, each scoped by
    /// the one before and the first by <paramref name="resolution"/>, as CoreCLR's
    /// <c>ImportHelper::CreateNesterHierarchy</c> gives it: found from the outermost, and from the first
    /// it does not find, the rest added without looking.
    /// </summary>
    let private nesterHierarchy
        (resolution : ResolutionScope)
        (chain : (string * string) list)
        (scope : EmitScope)
        : TypeRefRid * EmitScope
        =
        let rec define
            (resolution : ResolutionScope)
            (looking : bool)
            (chain : (string * string) list)
            (scope : EmitScope)
            : TypeRefRid * EmitScope
            =
            match chain with
            | [] -> invalidArg (nameof chain) "a chain of TypeRefs is never empty"
            | (ns, name) :: inner ->
                let found =
                    if looking then
                        tryFindTypeRef resolution ns name scope
                    else
                        None

                let row, scope =
                    match found with
                    | Some row -> row, scope
                    | None ->
                        // An empty namespace is the heap's empty string, which interning finds.
                        let ns, scope = EmitScope.internString ns scope
                        let name, scope = EmitScope.internString name scope

                        EmitScope.addTypeRef
                            {
                                ResolutionScope = resolution
                                Name = name
                                Namespace = ns
                            }
                            scope

                match inner with
                | [] -> row, scope
                | _ -> define (ResolutionScope.TypeRef row) found.IsSome inner scope

        define resolution true chain scope

    /// <summary>
    /// The <c>TypeRef</c> row CoreCLR's <c>ImportHelper::ImportTypeDef</c> gives for a TypeDef of
    /// another assembly: the chain of the TypeDef and the types enclosing it, with their own namespaces
    /// and names, scoped by <paramref name="assemblyRef"/>.
    /// </summary>
    let private importTypeDef
        (source : MetadataReader)
        (assemblyRef : AssemblyRefRid)
        (handle : TypeDefinitionHandle)
        (scope : EmitScope)
        : TypeRefRid * EmitScope
        =
        let rec chain (handle : TypeDefinitionHandle) (inner : (string * string) list) : (string * string) list =
            let definition = source.GetTypeDefinition handle
            let named = source.GetString definition.Namespace, source.GetString definition.Name
            let enclosing = definition.GetDeclaringType ()

            if enclosing.IsNil then
                named :: inner
            else
                chain enclosing (named :: inner)

        nesterHierarchy (ResolutionScope.AssemblyRef assemblyRef) (chain handle []) scope

    /// <summary>
    /// The <c>TypeRef</c> row CoreCLR's <c>ImportHelper::ImportTypeRef</c> gives for a TypeRef of
    /// another assembly: the chain of it and the TypeRefs scoping it, with their own namespaces and
    /// names, scoped by a copy of the outermost's AssemblyRef. Refused for any other outermost scope, and
    /// for an AssemblyRef that names the emitting assembly.
    /// </summary>
    let private importTypeRef
        (source : MetadataReader)
        (handle : TypeReferenceHandle)
        (scope : EmitScope)
        : Result<TypeRefRid * EmitScope, SignatureTokenRefusal>
        =
        let rec chain (current : TypeReferenceHandle) (inner : (string * string) list) =
            let reference = source.GetTypeReference current
            let named = source.GetString reference.Namespace, source.GetString reference.Name
            let resolution = reference.ResolutionScope

            if not resolution.IsNil && resolution.Kind = HandleKind.TypeReference then
                chain (TypeReferenceHandle.op_Explicit resolution) (named :: inner)
            else
                resolution, named :: inner

        let outermost, chain = chain handle []

        if outermost.IsNil then
            Error (SignatureTokenRefusal.TypeRefWithoutScope handle)
        else
            match outermost.Kind with
            | HandleKind.AssemblyReference ->
                let copy =
                    AssemblyRefCopy.ofReference source (AssemblyReferenceHandle.op_Explicit outermost)

                if namesEmittingAssembly copy scope then
                    Error (SignatureTokenRefusal.TypeRefOfEmittingAssembly handle)
                else

                match copy.PublicKeyOrToken with
                | PublicKeyOrToken.Key key when not (StrongName.isValidPublicKey key) ->
                    Error (SignatureTokenRefusal.TypeRefOfInvalidPublicKey handle)
                | _ ->
                    let assemblyRef, scope = defineAssemblyRefCopy copy scope
                    Ok (nesterHierarchy (ResolutionScope.AssemblyRef assemblyRef) chain scope)
            | HandleKind.ModuleDefinition -> Error (SignatureTokenRefusal.TypeRefOfModule handle)
            | HandleKind.ModuleReference -> Error (SignatureTokenRefusal.TypeRefOfModuleRef handle)
            | kind ->
                invalidArg
                    (nameof handle)
                    $"TypeRef %d{MetadataTokens.GetRowNumber (TypeReferenceHandle.op_Implicit handle : EntityHandle)}'s resolution scope is a %O{kind}, which ECMA-335 II.22.38 does not allow"

    /// What `TranslateSigWithScope` is translating: a method's signature or a field's.
    [<RequireQualifiedAccess>]
    type private SignatureKind =
        | Method
        | Field

    let private importSignature
        (kind : SignatureKind)
        (source : MetadataReader)
        (signature : ImmutableArray<byte>)
        (scope : EmitScope)
        : SignatureImport
        =
        if signature.IsDefaultOrEmpty then
            invalidArg (nameof signature) "a signature is never empty"

        let output = ResizeArray<byte> ()
        let position = ref 0
        let scope = ref scope
        let refusal : SignatureTokenRefusal option ref = ref None
        // `CreateAssemblyRefFromAssembly` reads the source's `Assembly` row afresh for each TypeDef.
        let assemblyRequest = lazy (AssemblyRefRequest.ofDefinition source)

        let fail (reason : string) : 'a =
            invalidArg
                (nameof signature)
                $"%s{reason} at byte %d{position.Value} of the method signature %A{List.ofSeq signature}"

        let readCompressed () : int =
            let at = position.Value

            if at >= signature.Length then
                fail "the signature ends early"

            let first = int signature.[at]

            let length =
                if first &&& 0x80 = 0 then 1
                elif first &&& 0xC0 = 0x80 then 2
                elif first &&& 0xE0 = 0xC0 then 4
                else fail "a compressed integer has no valid length"

            if at + length > signature.Length then
                fail "the signature ends early"

            position.Value <- at + length

            match length with
            | 1 -> first
            | 2 -> ((first &&& 0x3F) <<< 8) ||| int signature.[at + 1]
            | _ ->
                ((first &&& 0x1F) <<< 24)
                ||| (int signature.[at + 1] <<< 16)
                ||| (int signature.[at + 2] <<< 8)
                ||| int signature.[at + 3]

        let copyFrom (start : int) : unit =
            for i in start .. position.Value - 1 do
                output.Add signature.[i]

        // `CorIsModifierElementType`: `PTR`, `BYREF`, and every element type with bit 0x40, such as
        // `SENTINEL` and `PINNED`.
        let isModifier (element : int) : bool =
            element = 0x0F || element = 0x10 || element &&& 0x40 <> 0

        // `CorSigUncompressToken`'s table for each tag of a `TypeDefOrRefOrSpecEncoded` token.
        let translateToken (coded : int) : int option =
            let rid = coded >>> 2

            match coded &&& 3 with
            | 0 ->
                if rid < 1 || rid > source.TypeDefinitions.Count then
                    fail
                        $"TypeDef %d{rid} is not a row of the source module, which has %d{source.TypeDefinitions.Count}"

                match assemblyRefFromAssembly assemblyRequest.Value scope.Value with
                | None ->
                    refusal.Value <-
                        Some (SignatureTokenRefusal.UnreferencedUnsignedAssembly assemblyRequest.Value.Name)

                    None
                | Some (assemblyRef, withAssemblyRef) ->
                    let row, translated =
                        importTypeDef source assemblyRef (MetadataTokens.TypeDefinitionHandle rid) withAssemblyRef

                    scope.Value <- translated
                    Some ((row.Rid <<< 2) ||| 1)
            | 1 ->
                if rid < 1 || rid > source.TypeReferences.Count then
                    fail $"TypeRef %d{rid} is not a row of the source module, which has %d{source.TypeReferences.Count}"

                match importTypeRef source (MetadataTokens.TypeReferenceHandle rid) scope.Value with
                | Error refused ->
                    refusal.Value <- Some refused
                    None
                | Ok (row, translated) ->
                    scope.Value <- translated
                    Some ((row.Rid <<< 2) ||| 1)
            | 2 ->
                refusal.Value <- Some (SignatureTokenRefusal.TypeSpec (MetadataTokens.TypeSpecificationHandle rid))
                None
            | _ ->
                refusal.Value <- Some (SignatureTokenRefusal.NoTable coded)
                None

        // Each returns false, as soon as it meets a token it refuses, having set `refusal`.
        let rec methodSignature () : bool =
            let start = position.Value
            let header = readCompressed ()

            match header &&& 0x0F with
            // `FIELD`, `LOCAL_SIG`, `PROPERTY`, `GENERICINST`, and kinds past the last.
            | 0x06
            | 0x07
            | 0x08
            | 0x0A -> fail $"calling convention 0x%02x{header} is not a method's"
            | kind when kind > 0x0B -> fail $"calling convention 0x%02x{header} is not a method's"
            | _ -> ()

            if header &&& 0x10 <> 0 then
                readCompressed () |> ignore<int>

            let count = readCompressed ()
            // The calling convention and the counts are copied as they are encoded.
            copyFrom start

            let rec parameters (remaining : int) : bool =
                remaining = 0 || (fieldType () && parameters (remaining - 1))

            fieldType () && parameters count

        // `MergeUpdateTokenInSig` writes a field's calling convention in its shortest form, then its type.
        and fieldSignature () : bool =
            let header = readCompressed ()

            if header &&& 0x0F <> 0x06 then
                fail $"calling convention 0x%02x{header} is not a field's"

            output.AddRange (compressed header)
            fieldType ()

        and fieldType () : bool =
            let start = position.Value
            let mutable element = readCompressed ()

            while isModifier element do
                element <- readCompressed ()

            copyFrom start

            match element with
            // `SZARRAY`
            | 0x1D -> fieldType ()
            // `GENERICINST`, whose argument count is written in its shortest form.
            | 0x15 ->
                fieldType ()
                && (let count = readCompressed ()
                    output.AddRange (compressed count)

                    let rec arguments (remaining : int) : bool =
                        remaining = 0 || (fieldType () && arguments (remaining - 1))

                    arguments count)
            // `VAR` and `MVAR`
            | 0x13
            | 0x1E ->
                output.AddRange (compressed (readCompressed ()))
                true
            // `ARRAY`, whose shape is copied as it is encoded. A rank of 0 is all of the shape the
            // emitter reads.
            | 0x14 ->
                fieldType ()
                && (let start = position.Value

                    if readCompressed () <> 0 then
                        for _ in 1 .. readCompressed () do
                            readCompressed () |> ignore<int>

                        // A compressed signed integer's length is read as an unsigned one's.
                        for _ in 1 .. readCompressed () do
                            readCompressed () |> ignore<int>

                    copyFrom start
                    true)
            // `FNPTR`
            | 0x1B -> methodSignature ()
            // `VALUETYPE` and `CLASS`; and `CMOD_REQD` and `CMOD_OPT`, whose token is followed by the
            // type they modify.
            | 0x11
            | 0x12
            | 0x1F
            | 0x20 ->
                match translateToken (readCompressed ()) with
                | None -> false
                | Some translated ->
                    output.AddRange (compressed translated)
                    element = 0x11 || element = 0x12 || fieldType ()
            // `ELEMENT_TYPE_MAX` and past it, and `VALUEARRAY`, which the emitter refuses as a bad
            // signature.
            | 0x17 -> fail $"element type 0x%02x{element} is refused"
            | _ when element >= 0x22 -> fail $"element type 0x%02x{element} is refused"
            | _ -> true

        let walked =
            match kind with
            | SignatureKind.Method -> methodSignature ()
            | SignatureKind.Field -> fieldSignature ()

        if walked then
            SignatureImport.Imported (ImmutableArray.CreateRange output, scope.Value)
        else
            match refusal.Value with
            | Some refusal -> SignatureImport.Refused refusal
            | None -> failwith "unreachable: the walk stopped without refusing a token"

    /// <summary>
    /// What <c>TranslateSigWithScope</c> writes into a dynamic module for a method signature (ECMA-335
    /// II.23.2.1) of another module, read by <paramref name="source"/>, walking it as
    /// <c>ImportHelper::MergeUpdateTokenInSig</c> does. A class or value type, a generic type it
    /// instantiates, or a custom modifier names a type by token, which is rewritten where the walk meets
    /// it, so that a row added for one token is found for a later one. A TypeDef of the source module
    /// names the TypeRef <c>ImportTypeDef</c> finds or adds, scoped by the AssemblyRef
    /// <c>CreateAssemblyRefFromAssembly</c> finds or adds for the source assembly. A TypeRef of the source
    /// module scoped, at its outermost, by an AssemblyRef names the TypeRef <c>ImportTypeRef</c> finds or
    /// adds, scoped by a copy of that AssemblyRef. Any other token is refused. Of the rest, the index of a
    /// type or method parameter is written in its shortest form, every other byte is copied, and bytes
    /// after the signature's last type are dropped.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The signature is empty, ends early, is not a method signature, holds an element type the emitter
    /// refuses, or names a TypeDef or TypeRef the source module does not have; or the source module holds
    /// a TypeRef or AssemblyRef that ECMA-335 does not allow.
    /// </exception>
    let importMethodSignature
        (source : MetadataReader)
        (signature : ImmutableArray<byte>)
        (scope : EmitScope)
        : SignatureImport
        =
        importSignature SignatureKind.Method source signature scope

    /// <summary>
    /// What <c>TranslateSigWithScope</c> writes into a dynamic module for a field signature (ECMA-335
    /// II.23.2.4) of another module, read by <paramref name="source"/>: its calling convention in its
    /// shortest form, then its type, translated as <c>importMethodSignature</c> translates a parameter's.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The signature is empty, ends early, is not a field signature, holds an element type the emitter
    /// refuses, or names a TypeDef or TypeRef the source module does not have; or the source module holds
    /// a TypeRef or AssemblyRef that ECMA-335 does not allow.
    /// </exception>
    let importFieldSignature
        (source : MetadataReader)
        (signature : ImmutableArray<byte>)
        (scope : EmitScope)
        : SignatureImport
        =
        importSignature SignatureKind.Field source signature scope

    /// <summary>
    /// What <c>TranslateSigWithScope</c> writes into a dynamic module for a method signature (ECMA-335
    /// II.23.2.1) of that module itself, walking it as <c>importMethodSignature</c> does. CoreCLR's
    /// <c>ImportTypeDef</c> and <c>ImportTypeRef</c> recognise the source as the emitting scope and keep
    /// each TypeDef and TypeRef token, so nothing is added to the scope. A TypeSpec, or a token whose tag
    /// names no table, is refused. Of the rest, the index of a type or method parameter is written in its
    /// shortest form, every other byte is copied, and bytes after the signature's last type are dropped.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The signature is empty, ends early, is not a method signature, holds an element type the emitter
    /// refuses, or names a TypeDef, TypeRef or TypeSpec the scope does not have.
    /// </exception>
    let importOwnMethodSignature
        (signature : ImmutableArray<byte>)
        (scope : EmitScope)
        : Result<ImmutableArray<byte>, OwnSignatureRefusal>
        =
        failwith "TODO: importOwnMethodSignature is not yet implemented"

    /// <summary>
    /// What <c>TranslateSigWithScope</c> writes into a dynamic module for a field signature (ECMA-335
    /// II.23.2.4) of that module itself: its calling convention in its shortest form, then its type,
    /// translated as <c>importOwnMethodSignature</c> translates a parameter's.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The signature is empty, ends early, is not a field signature, holds an element type the emitter
    /// refuses, or names a TypeDef, TypeRef or TypeSpec the scope does not have.
    /// </exception>
    let importOwnFieldSignature
        (signature : ImmutableArray<byte>)
        (scope : EmitScope)
        : Result<ImmutableArray<byte>, OwnSignatureRefusal>
        =
        failwith "TODO: importOwnFieldSignature is not yet implemented"
