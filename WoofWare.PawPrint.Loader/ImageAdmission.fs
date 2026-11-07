namespace WoofWare.PawPrint

open System.Reflection.Metadata
open System.Reflection.PortableExecutable

/// <summary>
/// Why CoreCLR will not load an image for execution. After the first, the cases are in the order
/// CoreCLR checks them: three when its binder takes up the image
/// (<c>BINDER_SPACE::Assembly::Init</c>), and two when the runtime creates the assembly
/// (<c>PEAssembly::ValidateForExecution</c>). Both run for every image CoreCLR loads: the entry
/// assembly, an assembly a reference binds to, and one loaded by name or from a stream.
/// </summary>
[<RequireQualifiedAccess>]
type ImageRefusal =
    /// <summary>
    /// The image is not IL-only (<c>PEDecoder::IsILOnly</c>: its COR header lacks
    /// <c>COMIMAGE_FLAGS_ILONLY</c> and it has no ReadyToRun header), so it holds native code,
    /// as a C++/CLI image does. CoreCLR runs such images on Windows alone. Where it refuses one
    /// on Unix depends on how the image is loaded (before the binder looks at it, when it comes
    /// from a stream), and PawPrint, which runs IL alone, refuses it before anything else.
    /// </summary>
    | NotILOnly of PEImageHeaders
    /// <summary>
    /// <c>TranslatePEToArchitectureType</c> finds no processor architecture for these headers: a
    /// PE32+ image marked 32-bit-required, or a machine CoreCLR does not know. CoreCLR raises
    /// <c>BadImageFormatException</c> (<c>COR_E_BADIMAGEFORMAT</c>) in every process.
    /// </summary>
    | NoArchitecture of PEImageHeaders
    /// <summary>
    /// The manifest's flags set a content type (a bit of <c>afContentType_Mask</c>, 0xE00): the
    /// WindowsRuntime one and every reserved one alike. CoreCLR raises
    /// <c>FileLoadException</c> (<c>FUSION_E_INVALID_NAME</c>, "The given assembly name was
    /// invalid").
    /// </summary>
    | ContentType of manifestFlags : int
    /// <summary>
    /// The image is built for one machine: it is marked 32-bit-required, names a machine other
    /// than I386, or is ReadyToRun code compiled from IL that was not platform-neutral. CoreCLR
    /// loads it in a process of that machine's architecture and raises
    /// <c>BadImageFormatException</c> in any other. PawPrint models no process architecture.
    /// </summary>
    | MachineSpecific of PEImageHeaders
    /// <summary>
    /// The manifest carries an attribute whose type is named
    /// <c>System.Runtime.CompilerServices.ReferenceAssemblyAttribute</c>, found as CoreCLR's
    /// <c>GetCustomAttributeByName</c> finds it (<see cref="NamedAttribute.ofAssembly"/>), whichever
    /// assembly defines it. CoreCLR raises <c>BadImageFormatException</c>
    /// (<c>COR_E_LOADING_REFERENCE_ASSEMBLY</c>, "Cannot load a reference assembly for
    /// execution.").
    /// </summary>
    | ReferenceAssembly
    /// <summary>
    /// The manifest's processor architecture is <c>afPA_NoPlatform</c> (0x70 under
    /// <c>afPA_FullMask</c>, 0xF0). CoreCLR raises <c>BadImageFormatException</c>
    /// (<c>COR_E_BADIMAGEFORMAT</c>).
    /// </summary>
    | NoPlatform of manifestFlags : int

    override this.ToString () : string =
        match this with
        | ImageRefusal.NotILOnly headers ->
            $"an image that is not IL-only (COR flags %O{headers.CorFlags}), whose native code CoreCLR does not run on Unix and PawPrint cannot run"
        | ImageRefusal.NoArchitecture headers ->
            $"an image of no processor architecture (machine %O{headers.Machine}, PE32+ %b{headers.IsPE32Plus}, COR flags %O{headers.CorFlags}), which CoreCLR refuses to load with BadImageFormatException (COR_E_BADIMAGEFORMAT)"
        | ImageRefusal.ContentType flags ->
            $"an image whose manifest flags 0x%08X{flags} set a content type, which CoreCLR refuses to load with FileLoadException (FUSION_E_INVALID_NAME)"
        | ImageRefusal.MachineSpecific headers ->
            $"an image built for one machine (machine %O{headers.Machine}, PE32+ %b{headers.IsPE32Plus}, COR flags %O{headers.CorFlags}, ReadyToRun %O{headers.ReadyToRunHeader}), which CoreCLR loads only in a process of that architecture; the process architecture is not modelled"
        | ImageRefusal.ReferenceAssembly ->
            "a reference assembly, which CoreCLR refuses to load for execution with BadImageFormatException (COR_E_LOADING_REFERENCE_ASSEMBLY)"
        | ImageRefusal.NoPlatform flags ->
            $"an image whose manifest flags 0x%08X{flags} mark it NoPlatform, which CoreCLR refuses to load for execution with BadImageFormatException (COR_E_BADIMAGEFORMAT)"

/// Whether CoreCLR would load an image for execution, which every loader must ask of each image
/// it reads before running anything from it.
[<RequireQualifiedAccess>]
module ImageAdmission =

    /// What `TranslatePEToArchitectureType` makes of an image's PE kind.
    [<RequireQualifiedAccess>]
    type private Architecture =
        /// `peMSIL`, which every process loads.
        | Neutral
        /// `peI386`, `peAMD64`, `peARM` or `peARM64`; or, for ReadyToRun code compiled from
        /// IL that was not platform-neutral, whatever the executing runtime makes of it.
        | Specific
        /// `ERROR_BAD_FORMAT`, in every process.
        | Invalid

    /// `TranslatePEToArchitectureType` of what `PEDecoder::GetPEKindAndMachine` reports.
    let private architecture (headers : PEImageHeaders) : Architecture =
        // CorPEKind, corhdr.h.
        let peILonly = 0x1
        let pe32BitRequired = 0x2
        let pe32Plus = 0x4

        let has (bit : int) (kind : int) : bool = kind &&& bit <> 0

        match PEKindAndMachine.ofHeaders headers with
        | PEKindReport.MachineOfNativeRuntime (kind, _) ->
            // The machine is the executing runtime's own or one no runtime knows, so only the
            // combination invalid whatever the machine has an answer without the process.
            if has pe32Plus kind && has pe32BitRequired kind then
                Architecture.Invalid
            else
                Architecture.Specific
        | PEKindReport.Reported reported ->
            let kind = reported.PEKind

            let machine =
                LanguagePrimitives.EnumOfValue<uint16, Machine> (uint16 reported.Machine)

            if
                has peILonly kind
                && not (has pe32Plus kind)
                && not (has pe32BitRequired kind)
                && machine = Machine.I386
            then
                Architecture.Neutral
            elif has pe32Plus kind then
                if has pe32BitRequired kind then
                    Architecture.Invalid
                elif machine = Machine.Arm64 || machine = Machine.Amd64 then
                    Architecture.Specific
                else
                    Architecture.Invalid
            elif machine = Machine.I386 || machine = Machine.ArmThumb2 then
                Architecture.Specific
            else
                Architecture.Invalid

    /// <summary>
    /// Why CoreCLR would refuse to load <paramref name="assembly"/> for execution, or <c>None</c>
    /// if it would load it in a process of any architecture.
    /// </summary>
    let refusal (assembly : DumpedAssembly) : ImageRefusal option =
        let headers = assembly.PEImageHeaders

        let manifestFlags =
            int (assembly.PeReader.GetMetadataReader().GetAssemblyDefinition().Flags)

        let isReferenceAssembly () : bool =
            not
                (NamedAttribute.ofAssembly "System.Runtime.CompilerServices.ReferenceAssemblyAttribute" assembly)
                    .IsEmpty

        let architecture = architecture headers

        if
            not (headers.CorFlags.HasFlag CorFlags.ILOnly)
            && headers.ReadyToRunHeader.IsNone
        then
            Some (ImageRefusal.NotILOnly headers)
        elif architecture = Architecture.Invalid then
            Some (ImageRefusal.NoArchitecture headers)
        elif manifestFlags &&& 0xE00 <> 0 then
            Some (ImageRefusal.ContentType manifestFlags)
        elif architecture = Architecture.Specific then
            Some (ImageRefusal.MachineSpecific headers)
        elif isReferenceAssembly () then
            Some ImageRefusal.ReferenceAssembly
        elif manifestFlags &&& 0xF0 = 0x70 then
            Some (ImageRefusal.NoPlatform manifestFlags)
        else
            None

    /// <summary>
    /// Fail unless CoreCLR would load <paramref name="assembly"/> for execution
    /// (<see cref="refusal"/>). <paramref name="image"/> names it in the failure.
    /// </summary>
    let require (image : string) (assembly : DumpedAssembly) : unit =
        match refusal assembly with
        | None -> ()
        | Some refusal ->
            // CoreCLR raises these in the guest, from wherever the load happens: when it
            // compiles the first method that needs the assembly, for one a reference names.
            failwith
                $"TODO: %s{image} is %O{refusal}. PawPrint does not raise that exception in the guest, so it refuses the image instead"
