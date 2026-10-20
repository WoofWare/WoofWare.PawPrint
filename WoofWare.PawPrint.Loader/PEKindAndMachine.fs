namespace WoofWare.PawPrint

open System.Reflection.PortableExecutable

/// <summary>
/// The pair <c>PEDecoder::GetPEKindAndMachine</c> computes: a <c>CorPEKind</c> bitfield
/// (<c>corhdr.h</c>) and an <c>IMAGE_FILE_MACHINE_*</c> value, which the managed caller
/// casts to <c>PortableExecutableKinds</c> and <c>ImageFileMachine</c> respectively.
/// </summary>
type PEKindAndMachine =
    {
        PEKind : int
        Machine : int
    }

/// <summary>
/// What <c>PEDecoder::GetPEKindAndMachine</c> reports of an image.
/// </summary>
[<RequireQualifiedAccess>]
type PEKindReport =
    /// The kind and machine, whichever runtime is executing.
    | Reported of PEKindAndMachine
    /// <summary>
    /// A ReadyToRun image without <c>READYTORUN_FLAG_PLATFORM_NEUTRAL_SOURCE</c>: CoreCLR reports
    /// this <c>CorPEKind</c>, and a machine that depends on which architecture's native runtime
    /// is executing. It rewrites an <c>IMAGE_FILE_MACHINE_NATIVE_NI</c> machine (that runtime's
    /// own architecture XORed with an OS discriminator) back to <c>IMAGE_FILE_MACHINE_NATIVE</c>,
    /// and leaves any other as it is. Both are fixed when that native runtime is compiled, and
    /// PawPrint has no such identity to answer with.
    /// </summary>
    /// <remarks>
    /// <c>SimulatedUnixPlatform</c> is not that identity, though two of its three cases do name
    /// an architecture: it models what the guest could learn by asking the OS, not which runtime
    /// build is executing. Do not source the machine from the kernel: the CoreLib flavour comes
    /// from the runtime-dir list, not the kernel, so a guest could read a linux-x64 native
    /// identity while interpreting a macOS-arm64 CoreLib, and the <c>Custom</c> case has no
    /// architecture at all. Every ReadyToRun assembly in a shipped shared framework sets the
    /// flag.
    /// </remarks>
    | MachineOfNativeRuntime of peKind : int * readyToRun : ReadyToRunHeader

[<RequireQualifiedAccess>]
module PEKindAndMachine =
    /// <summary>
    /// <c>PEDecoder::GetPEKindAndMachine</c> (<c>coreclr/inc/pedecoder.inl</c>), reproduced
    /// over the header fields it reads.
    /// </summary>
    /// <remarks>
    /// CoreCLR's leading <c>HasContents() &amp;&amp; HasNTHeaders()</c> guard, and its
    /// <c>pe32Unmanaged</c> arms, are both absent here because
    /// <see cref="PEImageHeaders"/> can only describe a managed image with NT headers — an
    /// image without either never becomes a <c>DumpedAssembly</c>. So is
    /// <c>PEAssembly::GetPEKindAndMachine</c>'s <c>IsReflectionEmit</c> case, which reports
    /// <c>(0, 0)</c>: a dynamic assembly's headers are ones PawPrint built, so the caller must
    /// answer for it without asking this.
    /// </remarks>
    let ofHeaders (headers : PEImageHeaders) : PEKindReport =
        // CorPEKind, corhdr.h.
        let peILonly = 0x1
        let pe32BitRequired = 0x2
        let pe32Plus = 0x4
        let pe32BitPreferred = 0x10

        let corFlags = int headers.CorFlags
        let isPE32Plus = headers.IsPE32Plus

        let mutable kind = if isPE32Plus then pe32Plus else 0
        let machine = int headers.Machine

        if corFlags &&& int CorFlags.ILOnly <> 0 then
            kind <- kind ||| peILonly

            // CoreCLR compiles this under `HOST_64BIT`, which every platform PawPrint
            // simulates is. It undoes the Windows shim's promotion of a PE32 IL-only header
            // to PE32+ in memory — an artifact of a loaded layout, so a file read faithfully
            // should never present the combination at all. Reproduced anyway: where CoreCLR
            // has a defined answer, matching it costs nothing.
            if isPE32Plus && machine = int Machine.I386 then
                kind <- kind &&& ~~~pe32Plus

        // COR_IS_32BIT_REQUIRED / COR_IS_32BIT_PREFERRED (corhdr.h). The two flags are one
        // two-bit field, not two independent bits: 32BITREQUIRED alone means "x86-only",
        // and both together mean "platform-neutral but prefers to run 32-bit".
        let is32BitFlag (flag : CorFlags) : bool =
            corFlags &&& (int CorFlags.Requires32Bit ||| int CorFlags.Prefers32Bit) = int flag

        if is32BitFlag CorFlags.Requires32Bit then
            kind <- kind ||| pe32BitRequired
        elif is32BitFlag (CorFlags.Requires32Bit ||| CorFlags.Prefers32Bit) then
            kind <- kind ||| pe32BitPreferred

        // "compensate for MC++ peculiarity", says CoreCLR: a managed PE32 image that is
        // neither IL-only nor flagged 32-bit would otherwise be indistinguishable from one
        // with no COR header at all.
        if kind = 0 then
            kind <- pe32BitRequired

        match headers.ReadyToRunHeader with
        | Some readyToRun when readyToRun.Flags &&& 0x1u = 0u -> PEKindReport.MachineOfNativeRuntime (kind, readyToRun)
        | Some _ ->
            // READYTORUN_FLAG_PLATFORM_NEUTRAL_SOURCE: the IL this image was compiled from
            // was platform-neutral, so report what that IL would have reported and the
            // assembly's name still looks the way it did before the AOT step.
            PEKindReport.Reported
                {
                    PEKind = peILonly
                    Machine = int Machine.I386
                }
        | None ->
            PEKindReport.Reported
                {
                    PEKind = kind
                    Machine = machine
                }
