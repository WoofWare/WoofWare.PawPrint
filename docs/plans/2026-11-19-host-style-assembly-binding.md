# Bind assemblies as the host does: a TPA from the `deps.json` files (issue #2351)

Status: planned. The questions in "Questions for the decider" are open. Three of them change the public API of
published packages: Q1 (`GuestConfig`, in `WoofWare.PawPrint`), Q2 (new types in `WoofWare.PawPrint.Loader`) and Q3
(signatures in Loader, TypeSystem, Analysis and `WoofWare.PawPrint`). Q4 proposes a change to
`WoofWare.DotnetRuntimeLocator`. The owner wants to see those.

Measured 2026-11-19 on `origin/main` at `2b38151e8`, on the macOS arm64 host, inside the Nix devshell. "Real .NET 10"
is 10.0.14 and "real .NET 11" is 11.0.0-rc.3.26522.148, both from the devshell's combined `dotnet`. Each claim is
marked *read* (from the pinned source, `$DOTNET_RUNTIME_SRC_NET10` or `$DOTNET_RUNTIME_SRC_NET11`, or from this
repository) or *measured* (by running something). The probe is in "Appendix: the probe". Line numbers are those of
the .NET 10 pin unless the text says otherwise.

## What this is for

PawPrint binds an assembly reference by simple name to the first file of that name along
`GuestConfig.DotnetRuntimeDirs`. The real host builds a list of trusted platform assemblies (the TPA) from the
`deps.json` files of the application and its frameworks, and CoreCLR binds from that list. The two disagree whenever
an application ships an assembly which a framework also has. The measured case is a `net10.0` publish with
`Microsoft.Extensions.DependencyInjection` 9.0.2, run on .NET 11: the host binds the framework's
`Microsoft.Extensions.DependencyInjection.Abstractions` 11.0.0.0, and PawPrint binds the publish's 9.0.0.0.

The direction is already decided (consumer-guest plan, "Decisions for PR 4", decision 2): a pure function computes
the TPA from the `deps.json` files; the interpreter binds from it; nothing compares versions across directories at
bind time; `GuestPublish`'s refusal is deleted once the fixtures use the function. This plan answers the structural
questions which that decision left open, and stages the work.

## What the host does

Paths below are relative to `src/native/corehost/` unless they start with `src/coreclr/`.

### Which `deps.json` files, in which order

1. **The frameworks come in `hostfxr`'s order, highest level first** (read). `fx_resolver_t::read_framework`
   (`fxr/fx_resolver.cpp:444-540`) walks the application's framework references depth first. A framework met a
   second time is rotated to the back of the list (`:534-536`). So an application which references
   `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` gets `[app, Microsoft.AspNetCore.App, Microsoft.NETCore.App]`.
2. **Each framework's file is `<framework dir>/<framework name>.deps.json`** (read: `hostpolicy/deps_resolver.h:178-184`).
   A missing framework `deps.json` fails the launch ("missing dependencies manifest", `deps_resolver.h:99-105`). A
   missing application `deps.json` is allowed (`deps_format.cpp:586-593`).
3. **The application's file is parsed as framework-dependent unless the application is self-contained**
   (read: `deps_resolver.h:68-87`). The framework-dependent parse reads `runtimeTargets` (RID-specific assets); the
   self-contained parse reads only `targets` (`deps_format.cpp:503-555`).
4. **Entries come in document order: each `libraries` member in order, then that library's assets in the order of the
   target** (read: `reconcile_libraries_with_targets`, `deps_format.cpp:104-182`). A target package with no
   `libraries` member contributes nothing. A library's RID-specific assets of one type, if any survive RID selection,
   replace its non-RID assets of that type (`:513-536`).
5. **RID selection, by default, walks a fixed list** (read: `deps_format.cpp:186-215`, `:269-298`). The list is the
   host's compile-time RID, then its OS, then `unix-<arch>`, `unix`, `any`. `DOTNET_RUNTIME_ID`, if set, is tried
   first. On this host the list is `osx-arm64, osx, unix-arm64, unix, any` (measured, from the host trace). On a
   portable linux-x64 host the list is `linux-x64, linux, unix-x64, unix, any` (read). The RID graph is used instead
   only when the runtimeconfig sets `System.Runtime.Loader.UseRidGraph` to `true` (`hostpolicy_context.cpp:152-160`).
6. **The versions are what the `deps.json` states, not what the file says** (read: `deps_format.cpp:469-486`;
   measured, below). An absent or unparsable version is "empty", `-1.-1.-1.-1`, which is below every parsed version
   (`hostpolicy/version.cpp`).

### `resolve_tpa_list`

`deps_resolver_t::resolve_tpa_list` (`hostpolicy/deps_resolver.cpp:415-580`; .NET 11 `:404`) builds a map from asset
name to one resolved path:

7. **The application's entries first, then each framework's in order** (read: `:516-570`). With no application
   `deps.json`, the application directory's files are added after the (empty) application entries
   (`get_dir_assemblies`, `:116-178`): `.ni.dll`, then `.dll`, then `.ni.exe`, then `.exe`, one per file stem, with
   empty versions.
8. **Placeholders are skipped**: an asset whose path ends `/_._` (read: `:430-434`).
9. **A name not yet in the map is probed and added** (read: `:441-458`). A name already there is first checked for its
   extension: a different file name fails the launch (`:461-473`, the message "has already been found but with a
   different file extension").
10. **The replacement rule** (read: `:477-508`; measured, below). A later entry replaces the existing one when its
    assembly version is higher, or when the assembly versions are equal and its file version is higher *or equal*. So
    a tie goes to the later entry, which is the framework. If the later entry cannot be probed, that fails the launch
    for a framework entry and is ignored for an application entry.
11. **The map is keyed case-sensitively** (read: `deps_resolver.h:37`, a `std::unordered_map<pal::string_t, ...>`). Two
    names differing only by case both reach the TPA string.
12. **The TPA string is the map in iteration order, then the CoreLib beside `libcoreclr`** (read:
    `deps_resolver.cpp:572-577`, `hostpolicy_context.cpp:240-257`). The iteration order of a `std::unordered_map` is
    unspecified. The appended CoreLib uses `libcoreclr`'s real path. On this host that is a different spelling of the
    same file, so the string names CoreLib twice (measured, .NET 10).

### Where a probe looks, and whether it checks the file

13. **No file existence check, unless something turns it on** (read: `deps_resolver.cpp:181-255`, `:705-706`,
    `deps_entry.cpp:93-107`). The checks turn on when a servicing directory exists (`CORE_SERVICING`, or
    `/opt/coreservicing`), a shared store exists (`DOTNET_SHARED_STORE`, or `<dotnet root>/store/<arch>/<tfm>`),
    `additionalProbingPaths` are given, or additional `deps.json` files are given (`DOTNET_ADDITIONAL_DEPS`). On this
    host none is (measured: every probe in the trace says "Skipped file existence check").
14. **So, without the checks, an entry always resolves to its own directory** (read: `probe_deps_entry`,
    `deps_resolver.cpp:276-369`). The application's probe comes before any framework's, and with no existence check it
    always matches. An application entry resolves to `<app>/<localPath>` if `localPath` is set, else
    `<app>/<file name>`, else, for a RID-specific asset without `localPath`, `<app>/<relative path>` (for example
    `<app>/runtimes/unix/lib/net8.0/X.dll`) (`deps_entry.cpp:141-184`). A framework entry resolves to its own
    framework directory.
15. **A missing file still enters the TPA** (measured, below). The launch succeeds, and the bind fails later.

### What CoreCLR does with the TPA and `APP_PATHS`

16. **CoreCLR never reads a `deps.json`.** It parses the TPA string into a map keyed by the file name's stem,
    compared ignoring case, and keeps the first entry for a stem (read: `src/coreclr/binder/applicationcontext.cpp:81-215`;
    `src/coreclr/binder/utils.cpp:146`, which also refuses a relative path or an extension other than `.dll`, `.exe`,
    `.ni.dll`, `.ni.exe`).
17. **A name in the TPA binds to that file. If the file is not there, the bind falls to `APP_PATHS`, and only then
    fails** (read: `BindByTpaList`, `src/coreclr/binder/assemblybindercommon.cpp:826-993`). A name not in the TPA also
    goes to `APP_PATHS`.
18. **`APP_PATHS` is unset unless the runtimeconfig sets `Microsoft.NETCore.DotNetHostPolicy.SetAppPaths` to `true`**,
    and then it is the application directory (read: `hostpolicy_context.cpp:299-328`; measured: `APP_PATHS` is null in
    every run below). So by default a name outside the TPA does not bind.
19. **CoreLib binds from the directory of `libcoreclr` first, and from the TPA only if it is not there** (read:
    `BindToSystem`, `assemblybindercommon.cpp:253-330`).
20. **A load by path into the default context still goes through the TPA** (read: `BindUsingPEImage`,
    `assemblybindercommon.cpp:1174`; not measured for the entry assembly). If the TPA names the image's simple name,
    CoreCLR binds that file and compares MVIDs; a mismatch is a `FileLoadException`.

### What the guest reads back

21. **.NET 10: the host's string, as the host built it** (measured). `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`
    equals the host's property, entry for entry and in order, CoreLib twice included (177 entries).
22. **.NET 11: the binder's map, rendered by CoreCLR** (measured and read). The host passed 185 entries; the guest read
    184, in a different order, with CoreLib once. .NET 11 no longer stores the four probing properties as strings
    (`src/coreclr/dlls/mscoree/exports.cpp:82-88`); `AppContext.GetData` asks the QCall
    `AppContext_TryGetHostPropertyValue` (`src/coreclr/vm/appdomainnative.cpp:144`, .NET 11 pin), which walks the
    binder's hash map.

So the order is unspecified on both runtimes, and the two runtimes give different strings for one TPA.

### The two pins agree on the rule

Diffing `hostpolicy/` between the pins (read) shows refactors only: `deps_resolved_asset_t` holds a pointer to its
asset, `probe_deps_entry` returns an enum, the placeholder suffix uses `DIR_SEPARATOR_STR`, `to_dir_path` is
restructured. The replacement rule, the order and the existence checks are the same.

## Measured host TPAs

A `net10.0` probe with a `PackageReference` to `Microsoft.Extensions.DependencyInjection` 9.0.2, published
framework-dependent, portable. It prints the TPA and the version and location of the assembly which
`IServiceCollection` binds to. .NET 11 runs used `DOTNET_ROLL_FORWARD=LatestMajor` and
`DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`. Each variant copies the publish and changes one thing. "Abs" is
`Microsoft.Extensions.DependencyInjection.Abstractions`; the framework's `deps.json` on .NET 11 states it at
assembly version 11.0.0.0, file version 11.0.26.52348, and .NET 10's framework does not have it.

| Variant | .NET 10: Abs in TPA, bound | .NET 11: Abs in TPA, bound |
|---|---|---|
| Publish as is (deps says 9.0.0.0 / 9.0.225.6610) | app, 9.0.0.0 | framework, 11.0.0.0 |
| No `deps.json` | app, 9.0.0.0 | framework, 11.0.0.0 |
| Abs file deleted | app, `FileNotFoundException` | framework, 11.0.0.0 |
| deps says 11.0.0.0 / 11.0.26.52348 (a tie) | app, 9.0.0.0 | framework, 11.0.0.0 |
| deps says 11.0.0.0 / 11.0.26.52349 | app, 9.0.0.0 | app, 9.0.0.0 |
| deps says 12.0.0.0 / 1.0.0.0 | app, 9.0.0.0 | app, 9.0.0.0 |
| deps says 12.0.0.0, file deleted | app, `FileNotFoundException` | app, `FileNotFoundException` |
| deps states no versions | app, 9.0.0.0 | framework, 11.0.0.0 |

All runs exited 0 (the probe catches the bind failure). What the table shows:

* The tie goes to the framework, and one more file version gives it to the application: the `>=` is real.
* The versions come from the `deps.json`. Stating 12.0.0.0 makes the host bind a file whose own version is 9.0.0.0.
* A missing file stays in the TPA and fails the bind. The host does not fall back to the framework's copy.
* With no `deps.json`, or no stated versions, the framework wins every name it lists.

Other measurements:

* **TPA sizes.** .NET 10: 177 entries, 172 from the framework, 4 from the application, plus the appended CoreLib.
  .NET 11: 185 from the host (181 framework, 3 application, CoreLib appended), 184 read back.
* **The host's trace** on .NET 11 says "Replacing deps entry [`<app>`/Microsoft.Extensions.DependencyInjection.Abstractions.dll,
  AssemblyVersion:9.0.0.0, FileVersion:9.0.225.6610] with [`<framework>`/Microsoft.Extensions.DependencyInjection.Abstractions.dll,
  AssemblyVersion:11.0.0.0, FileVersion:11.0.26.52348]", as #2351 records.
* **Self-contained** (`-r osx-arm64 --self-contained`): the trace says "Executing as a self-contained app"; all 177
  TPA entries are in the application directory; `FX_DEPS_FILE` is empty; CoreLib appears twice. The probe bound Abs
  9.0.0.0 from the application directory.
* **The shared frameworks are disjoint.** No runtime asset name is in both `Microsoft.NETCore.App` and
  `Microsoft.AspNetCore.App`, on 10.0.14 (172 and 141 names) or on 11.0.0-rc.3 (181 and 132). Neither lists a
  RID-specific runtime asset. Every `.dll` in each framework directory is in its `deps.json`, and every runtime asset
  in the `deps.json` is a `.dll` in the directory. So on today's frameworks the order between the two frameworks does
  not change the TPA.

## What PawPrint does today

### Where binding happens (read)

* `AssemblyProbe.tryReadFromRuntimeDirs` (`WoofWare.PawPrint.Loader/AssemblyProbe.fs`) reads `<name>.dll`, ignoring
  case, from the first directory which holds it. It is called from two places:
  * `TypeResolution.tryLoadAssembly`, through `TypeResolution.directoryLoader`, which binds every assembly reference
    the interpreter and the escape analysis meet;
  * `AssemblyBinding` (`WoofWare.PawPrint.Loader/AssemblyBinding.fs:353`), which binds `Assembly.Load` and
    assembly-qualified `Type.GetType` requests, one directory at a time.
* The directory list reaches them as `dotnetRuntimeDirs : string seq`, threaded through Loader, TypeSystem and the
  interpreter (465 mentions in Loader and TypeSystem, 73 uses of `state.DotnetRuntimeDirs` in 21 interpreter files).
  `EscapeAnalysis.create` takes its own `runtimeDirs`.
* Nothing reads a `deps.json`, and nothing looks in a subdirectory, so a RID-specific asset in `runtimes/<rid>/lib/`
  is never found.

### What `WoofWare.PawPrint.App` passes (measured)

`DotnetRuntime.SelectForDll dllPath` from `WoofWare.DotnetRuntimeLocator` 0.5.8, unchanged. For the probe's publish
it returned `[<app dir>, <shared>/Microsoft.NETCore.App/10.0.14]`, and with the roll-forward variables set it returned
`[<app dir>, <shared>/Microsoft.NETCore.App/11.0.0-rc.3.26522.148]`. So the application directory always comes first.

What the locator does (read, from its 0.5.8 source): it reads the runtimeconfig, resolves each framework the
runtimeconfig names (honouring `DOTNET_ROLL_FORWARD` and `DOTNET_ROLL_FORWARD_TO_PRERELEASE`), and prepends the
DLL's directory. It returns directories without framework names. It does not follow a framework's own runtimeconfig
to its base framework, and its framework order is that of a dictionary built from the runtimeconfig, not `hostfxr`'s.
For the test assembly, whose runtimeconfig names `Microsoft.NETCore.App` then `Microsoft.AspNetCore.App`, it
returned `[<test bin>, NETCore.App/10.0.14, AspNetCore.App/10.0.14]` (measured), where `hostfxr` orders the two
frameworks the other way round.

### What the test harness passes (read, and measured where marked)

* **Roslyn-compiled guests** are compiled to a `MemoryStream` and run along `FrameworkUnderTest.runtimeDirsFor
  platform`. That is the pinned pack of the kernel's flavour, then `FrameworkUnderTest.runtimeDirs ()`.
  * On Net10, `runtimeDirs ()` is `SelectForDll` of the test assembly, so the list is `[pack, <test bin>,
    NETCore.App, AspNetCore.App]` (measured, as above). The test host's own `bin` directory is on the guest's list.
    It holds 41 DLLs (FSharp.Core, NUnit, Roslyn, PawPrint's own), none named as a framework file (measured), so
    nothing is shadowed today. But a guest under PawPrint on Net10 can bind them, and the same guest on real .NET
    cannot.
  * On Net11, `runtimeDirs ()` is the two directories the devshell names, so the list is `[pack, NETCore.App,
    AspNetCore.App]`. No `bin` directory.
* **The real-runtime oracle** writes the guest and a generated runtimeconfig into a scratch directory, with no
  `deps.json` (`RealRuntime.executeInScratch`). So the host trusts every assembly in that directory and the
  framework's (fact 7). Files of a filesystem seed are materialised in the same directory.
* **Fabricated guests** (`FabricatedGuest.fs`) put a temporary directory holding the driver and the fabricated
  assembly ahead of `runtimeDirsFor`. There is no `deps.json` there either.
* **Published guests.** `TestConsumerCases` runs `[publish, pack, ...runtimeDirs ()]`. `TestFSharpPureCases` runs
  `[publish, ...runtimeDirs ()]`, with no pack, and relies on the publish's FSharp.Core shadowing the test host's.
  `GuestPublish.publish` refuses a publish holding a file named as a framework file.
* **`PatchedCoreLib`** puts a directory holding a CoreLib with a different major at the head of the list, to test
  `UnsupportedRuntimeException`.

### The pinned packs (measured)

* **A pack directory holds no `Microsoft.NETCore.App.deps.json`.** It holds only DLLs: 172 for 10.0.14, 181 for
  11.0.0-rc.3, the same names as the shared framework directory of that version. `flake.nix`'s `frameworkPack` copies
  `runtimes/<rid>/lib/<tfm>/*.dll` and nothing else.
* **The pack's nupkg does hold one**, at `runtimes/<rid>/lib/<tfm>/Microsoft.NETCore.App.deps.json`, beside
  `data/RuntimeList.xml`, which lists every file with its assembly and file version.
* **The osx-arm64 pack's `deps.json` is byte-identical to the macOS shared framework's** (10.0.14).
* **The linux-x64 pack's `deps.json` lists the same runtime assets, at the same assembly and file versions, as the
  macOS shared framework's**, for 10.0.14 (172 assets) and for 11.0.0-rc.3 (181 assets). It differs in its
  `runtimeTarget` (`.NETCoreApp,Version=v10.0/linux-x64`), its library key
  (`Microsoft.NETCore.App.Runtime.linux-x64/10.0.14`) and its native assets.

So the pack-then-framework list has a host-shaped reading: the pack *is* a `Microsoft.NETCore.App` for the pack's
RID, with a `deps.json` of its own, and every name it lists is one the shared framework lists at the same versions.

### What a guest reads back under PawPrint today (read)

On .NET 10, `TRUSTED_PLATFORM_ASSEMBLIES` is null: PawPrint seeds only `configProperties` (`docs/divergences.md`, "The
host-populated `AppContext` properties are absent"). On .NET 11, `GetData` would ask the QCall
`AppContext_TryGetHostPropertyValue`, which has no handler yet (the multi-runtime plan, stage 7, says no guest has
reached it).

## The design in brief

The decided direction, made concrete. The questions below choose between the options for each part.

* **A value: the TPA.** A map from simple name to one absolute `.dll` path, built only by a parser that checks what
  CoreCLR checks (fact 16) and refuses what PawPrint cannot reproduce: two names equal ignoring case, an extension
  other than `.dll`, a key which is not the file's stem.
* **A pure function: what the host would put in it.** Its inputs are values:
  * the application: its directory and the bytes of its `deps.json`, or its directory and the names of the files in
    it when there is no `deps.json`;
  * the frameworks, in `hostfxr`'s order: each one's name, directory and `deps.json` bytes;
  * the RID list.

  Its output is the TPA, or a refusal. A refusal is either "the host would fail this launch" (a duplicate name with a
  different extension; a missing framework `deps.json`; a `deps.json` the host cannot parse) or "PawPrint cannot
  reproduce the host's answer" (below). It follows `resolve_tpa_list` in the mode without existence checks (fact
  13), which is the only mode it models. It reads no file.
* **The binder reads the TPA.** A name in the TPA binds to that file, and a missing file is a miss, not a reason to
  look elsewhere (fact 17, with `APP_PATHS` unset). A name not in the TPA is a miss. No directory is listed at bind
  time.
* **The App and the fixtures build the inputs** (their IO), call the function, and put the TPA in `GuestConfig`.

What the function refuses as "not reproducible", in its first version:

* `UseRidGraph=true` in the runtimeconfig (the caller passes it; fact 5).
* Two output names equal ignoring case (fact 11: CoreCLR keeps whichever comes first in an unspecified order).
* An asset whose name is not the stem of the file it resolves to (`localPath` naming another file), and any `.exe`
  or `.ni.*` asset.
* A version component above `Int32.MaxValue` (the host parses unsigned and stores signed).
* A `deps.json` which `System.Text.Json` and rapidjson might read differently: duplicate member names, comments,
  trailing commas, anything but strict JSON. Also a document whose shape the host would crash on (`runtimeTarget`
  absent, `libraries` absent).
* A self-contained application (Q6).

The caller refuses the host settings which turn on existence checks or extra `deps.json` files: `additionalProbingPaths`,
`DOTNET_ADDITIONAL_DEPS`, `DOTNET_SHARED_STORE`, a `store` directory beside `dotnet`, a servicing directory, and
`SetAppPaths=true`. These are reads of the host, so they belong to the App, never to the library.

## Questions for the decider

### Q1. The shape of `GuestConfig` — **changes public API** of `WoofWare.PawPrint`

`GuestConfig.DotnetRuntimeDirs` is public, and part of the replay contract.

* **A. Replace it with the TPA.** `GuestConfig.Assemblies : TrustedPlatformAssemblies`. The run records the answer
  the host would give, which is also what CoreCLR receives. No directory list survives. `GuestConfig.Default` and
  `HostConfig.Default` take the TPA. `UnsupportedRuntimeException.DotnetRuntimeDirs` (in `WoofWare.PawPrint.Domain`)
  becomes the TPA's CoreLib path.
  * Correctness: binding is exactly the TPA's. A name the host could not bind does not bind.
  * Replay: a stored `GuestConfig` keeps its binding even if the function is later fixed, because the run records
    the answer, not the question.
  * Blast radius: every caller of `GuestConfig` (121 files in the repository, the App, `docs/user/run-a-program.md`),
    and every consumer of the package.
  * Reversibility: a public break. Undoing it is a second break.
  * Information: it keeps which file each name binds to. It loses why (which `deps.json` said what), which a guest
    could ask about through `APP_CONTEXT_DEPS_FILES`, a property PawPrint does not populate.
* **B. Keep `DotnetRuntimeDirs` and add the TPA beside it**, the directories serving names the TPA lacks (the role
  `APP_PATHS` plays). Additive, so no break.
  * Correctness: a name outside the TPA binds under PawPrint and fails on real .NET, where `APP_PATHS` is unset by
    default (fact 18). That is a silent divergence of the kind #2351 is about.
  * It keeps two sources of truth for good, and a test written against one can pass against the other.
* **C. Replace it with the host's inputs**: the application's `deps.json` bytes or directory listing, the frameworks
  with their `deps.json` bytes, the RID list. `Program.prepare` calls the function.
  * Information: the most. PawPrint could later populate `APP_CONTEXT_DEPS_FILES` and `FX_DEPS_FILE` from it.
  * Replay: a fix to the function changes the binding of a stored config. The replay contract then includes the
    function's version.
  * The test harness's in-memory guests have no `deps.json` and no directory, so `GuestConfig` would carry a case
    with no host counterpart.

**Recommendation: A.** It is CoreCLR's own boundary: the host decides the TPA and the runtime binds from it. B keeps
the divergence alive as a fallback. C can be added later as a helper which builds the TPA, without changing
`GuestConfig` again.

The TPA type itself: a validated map, built only through a function returning `Result`. No `AppPaths` field: it
would be empty for every run PawPrint can reproduce, so `SetAppPaths=true` is refused (above) rather than modelled.
If a guest ever needs it, the field is an addition.

### Q2. Which project holds the function — **adds public API** to `WoofWare.PawPrint.Loader`

* **A. `WoofWare.PawPrint.Loader`.** It "binds references between images: which definition, in which assembly, a
  reference names", and the TPA is the answer to "which file does a simple name name". `AssemblyProbe` and
  `AssemblyBinding` are already there. The escape analysis (`WoofWare.PawPrint.Analysis`) sees Loader, so it can take
  the same TPA and resolve exactly as the interpreter does, which is the point of the project split. Parsing a
  `deps.json` needs only `System.Text.Json`, which is in the BCL for `net8.0`, so no new package dependency.
* **B. A new project, `WoofWare.PawPrint.HostPolicy`.** A transcription of `hostpolicy` in its own boundary. It costs
  a new published package: `PackageId`, `version.json`, footprint, and entries in three CI jobs. The TPA type must
  still be visible to Loader's binder, so either Loader references the new project, or the type lives in Domain and
  the function in the new project. The split buys a smaller Loader and nothing the compiler would enforce that it
  does not enforce now.
* **C. `WoofWare.DotnetRuntimeLocator`.** It answers "what would the host launch?", and `hostfxr` is already its
  oracle. But the TPA is `hostpolicy`'s job, not `hostfxr`'s, and the refusals are PawPrint's (what PawPrint can
  reproduce of CoreCLR's choice). Every change would cross two repositories and a release. The locator is C#; the
  rest of PawPrint's `Result`-shaped refusals are F#.

**Recommendation: A**, the types and the function in Loader. Q4 covers the part which does belong in the locator.

### Q3. What changes in the published libraries' signatures — **changes public API** of Loader, TypeSystem, Analysis and `WoofWare.PawPrint`

Whatever Q1 decides, the binder's input type changes. Today `dotnetRuntimeDirs : string seq` is a parameter of public
functions in Loader (`AssemblyProbe.tryReadFromRuntimeDirs`, `TypeResolution.directoryLoader`, `AssemblyBinding`'s
binder), in TypeSystem (465 mentions across the two), in Analysis (`EscapeAnalysis.create`), and a public field of
`IlMachineState` and `ProgramLaunch`.

* **A. One break, at the switch.** The stage that makes binding read the TPA also changes every signature.
  The PR is large, and semantic and mechanical changes are mixed in it.
* **B. Two breaks.** A pure refactor first threads a new type, `AssemblySource`, whose one case holds the directory
  list, through every signature, `GuestConfig` included; no behaviour changes. The switch then replaces the case's
  contents with the TPA. The switch's diff is then mostly semantic. A consumer who upgrades between the two sees two
  breaks.
* **C. B, but the refactor does not touch `GuestConfig`** (it converts in `Program.prepare`). The library-internal
  signatures break twice; `GuestConfig` breaks once.

**Recommendation: C.** It keeps the switch reviewable, and it breaks `GuestConfig`, the API most consumers use, only
once. Releases of the packages between the two stages are possible but carry the intermediate type; if the owner
prefers no intermediate public type, choose A.

### Q4. Where the framework list comes from — **proposes a change to `WoofWare.DotnetRuntimeLocator`**

The function needs each framework's name (to find `<name>.deps.json`) and `hostfxr`'s order. `SelectForDll`
returns directories only, in dictionary order, and does not follow a framework's runtimeconfig to its base
framework (read, above).

* **A. The App derives the names from the directory layout** (`.../shared/<name>/<version>`), takes the locator's
  order, and the function refuses when two frameworks list one name. The refusal makes the order irrelevant, and it
  never fires on today's frameworks (measured: disjoint). No locator change. But the layout is a convention of the
  `dotnet` root, not a contract, and the base-framework walk stays missing.
* **B. The locator gains a function returning `(name, directory)` per framework, in `hostfxr`'s order**, following
  each framework's runtimeconfig as `read_framework` does. This is `hostfxr`'s job, which the locator already
  transcribes, with `hostfxr` as its oracle. One locator PR and a version bump here.
* **C. PawPrint walks the framework runtimeconfigs itself.** A second transcription of `hostfxr`, beside the
  locator's.

**Recommendation: B**, blocking only the App's part of the switch (stage 5). The test harness does not need it: it
knows each framework's name from the devshell variables. Keep A's refusal (two frameworks listing one name) in the
function anyway, until the order is a tested input: it costs nothing and turns a wrong order into a refusal.

### Q5. What the function is given for the pinned packs

The pack-then-framework list has no counterpart on a real host. The measurements say the pack is a complete
`Microsoft.NETCore.App` for its RID.

* **A. Copy the pack's `Microsoft.NETCore.App.deps.json` into the pack directory** (one more `cp` in `frameworkPack`'s
  `installPhase`; the source hash is unchanged). The harness then passes the pack directory, with its own
  `deps.json`, as `Microsoft.NETCore.App`, and the shared `Microsoft.AspNetCore.App` directory with its `deps.json`.
  This is what a real host of the pack's RID would compute with those frameworks installed. No list has "a pack, then
  a framework" any more.
* **B. Pass the shared framework's `deps.json` with the pack's directory.** No flake change. It rests on the measured
  equality of runtime assets and versions, which a test would then have to assert for every pin. The input pairs a
  `deps.json` for one RID with the files of another.
* **C. An overlay input**: a directory whose files replace same-named TPA entries after the host's computation. A
  concept with no host counterpart, inside a function whose job is to be the host.

**Recommendation: A**, with a test that the pack's `deps.json` lists exactly the DLLs in the pack directory (failing
on absence, not skipping, as the test project's `AGENTS.md` asks). The `sync-dotnet-runtime` skill's bump steps need
no change: the pack hashes are of the nupkg.

### Q6. Applications with no `deps.json`, and guests with no directory

The host trusts every assembly in the application's directory, with empty versions, so the framework wins every name
it lists (facts 7 and 10; measured). The oracle's scratch directory and the fabricated guests' directories are of
this kind. Roslyn-compiled guests have no directory at all: their image is a `MemoryStream`.

* **A. The input has three application cases**: a directory with a `deps.json`; a directory with no `deps.json` and
  the names of its files; and "no application directory" for an image held in memory. In the third case the TPA holds
  only the frameworks' entries, and the launch refuses if the TPA names the entry assembly's simple name (fact 20:
  CoreCLR would bind that file instead and fail the MVID check).
* **B. Give in-memory guests a synthetic directory** holding one name, the guest's. The TPA then names a file that
  does not exist, which is harmless (the entry assembly is loaded before anything binds by name) but is a path that
  describes nothing.
* **C. Write every Roslyn guest to disk** and treat it as a no-`deps.json` directory, as the oracle does. One more
  file write per guest, and no case without a host counterpart; but the harness's `MemoryStream` path exists so that
  no sidecar file can leak in, and C gives that up.

**Recommendation: A.** The third case is a fact about how PawPrint is driven, and saying so in the type is better
than a fabricated path. The refusal covers the only observable difference (a guest named as a framework assembly).

### Q7. Self-contained publishes

The host processes only the application's `deps.json`, parsed in the self-contained mode, and loads CoreLib from the
application directory (measured: 177 entries, all in the application directory). The App today runs such a publish
on the installed shared framework, after the application directory, because the locator resolves
`includedFrameworks` against installed frameworks (read).

* **A. Refuse it** in the function (and so in the App) for now. Representable later: an empty framework list and the
  self-contained parse mode are additions to the input.
* **B. Support it now.** Small in the function, but the CoreLib it brings is the publish's RID's, which may not be the
  emulated kernel's flavour, and nothing yet asks for it.

**Recommendation: A.** No consumer needs it; the refusal names the reason.

### Q8. Whether a refusal lands first

Until the switch lands, the App runs a clashing publish on the wrong assembly without a word.

* **A. A name-collision refusal in the App now**: refuse a publish holding a file named as a file of a framework
  directory, as `GuestPublish` does. A day's work, deleted by the switch. But the SDK drops a package asset which the
  target framework already has at the same or a higher version, so a publish holds such a file mostly when its copy is
  *newer* than the target framework's. On the target runtime that is a publish which both the host and PawPrint bind
  to the application's copy. So A refuses mostly runs which are correct today, and it misses the missing-file and
  RID-specific cases.
* **B. No refusal PR.** The function lands first (stages 2 and 3), then the refactor and the switch.
* **C. An exact refusal once the function exists**: the App computes the TPA and refuses when any name would bind
  elsewhere along its directories. Correct, but it needs stage 3 first, and the switch is then two PRs away, so it
  would live for days.

**Recommendation: B.** If the switch stalls after stage 3, C is the fallback, and it is small.

### Q9. What the guest reads back

`AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`. The real order is unspecified, and the two runtimes give
different strings for one TPA (facts 21 and 22).

* **A. Keep it absent** (null on .NET 10; the .NET 11 QCall answers "not found" when it gets a handler). Update the
  divergence entry: the reason is no longer that PawPrint has no TPA, but that it does not seed the property.
* **B. Seed a canonical rendering**: on .NET 10 through `AppContext.Setup`, with CoreLib appended as the host does;
  on .NET 11 through the QCall, rendering the TPA once per name. In ordinal order. A guest that reads the list as a
  set sees the host's answer; one that depends on order sees an order the host could also have produced, but not the
  one it would have.
* **C. Reproduce the host's string exactly.** Not possible: it is `std::unordered_map` iteration order on .NET 10 and
  `SHash` order on .NET 11.

**Recommendation: A in this work.** No binding depends on it, and the tests below read the TPA from real .NET, not
from PawPrint. B is a follow-up when a guest needs the property; it should come with its own decision about
`APP_CONTEXT_BASE_DIRECTORY` and `APP_CONTEXT_DEPS_FILES`, which the same divergence entry covers.

### Q10. Host settings the function does not model

The function models the host without existence checks and without additional `deps.json` files (fact 13).

* **A. The App refuses each setting which would change the mode** (listed under "The design in brief"). The library
  takes no host reads.
* **B. Model existence checks**: the input gains the set of files that exist, and the probe order of fact 14 becomes
  live (an application entry can then resolve into a framework directory, and a servicing or store directory can
  win).

**Recommendation: A.** None of these settings is used on any host this project runs on (measured on this host).

### Q11. Where the end-to-end witness lives

The issue asks for a guest that prints the version of `typeof(IServiceCollection).Assembly`.

* **A. A scenario in `WoofWare.PawPrint.Test.ConsumerCases`**, `di-version`, with the `PackageReference` at 9.0.2.
  PR 4 of the consumer-guest plan then reuses the reference, as its decision 2 asks. Adding the package makes
  `GuestPublish`'s refusal fail every consumer scenario on Net11 until the switch, so the scenario lands in the
  switch's PR, as its first commits (below).
* **B. A guest project of its own.** A new project, footprint and `nix/deps.json` entries for one scenario, which
  PR 4 would then duplicate or absorb.

**Recommendation: A.** Add a second witness which needs no package: a Roslyn-built directory with no `deps.json`
holding the guest and an assembly named as a framework assembly. The host binds the framework's on both runtimes;
PawPrint today binds the directory's.

## Stages

Implement this plan with each stage on its own branch, stacked as necessary on previous branches, so that a reviewer
can review each branch in isolation. (Per `AGENTS.md`, prefer merging each before branching the next.)

Every stage commits its tests first, with the failing output in the PR body.

### Stage 1: the pinned packs carry their `deps.json`

**Dependencies**: none. **Implements**: Q5.

* `frameworkPack` also copies `Microsoft.NETCore.App.deps.json`. `flake.nix`'s comment above it says why.
* **Oracle**: a test in `TestFrameworkUnderTest` that, for each pinned pack of each runtime, the `deps.json`'s runtime
  assets are exactly the pack's DLLs, by name. Red before the flake change (the file is absent), green after.

### Stage 2: parsing a `deps.json`

**Dependencies**: none. **Implements**: "The design in brief", the entry order (facts 3 to 6, 8).

* Loader: `DepsJson.parse : mode -> rids -> byte[] -> Result<DepsEntry list, DepsJsonError>`, where an entry is the
  library name and version, its type, the asset's relative path, `localPath`, assembly and file version (with
  "empty"), and whether it is RID-specific. Entries in the host's order. Refusals as listed.
* **Oracle**:
  * A property test: generate a model of a `deps.json` (libraries, targets with `runtime` and `runtimeTargets`,
    versions drawn from parsed, absent and malformed strings), render it to JSON, parse it, and compare with the
    model's expected entry list computed directly from the model. High count.
  * Version parsing against a transcription of `version_t::parse` (empty on failure; two to four components).
  * Each refusal has a unit case: duplicate member names, a comment, a missing `runtimeTarget`.
  * Real `deps.json` files: both pins' shared frameworks and packs parse, and give the counts measured above.

### Stage 3: the TPA and the function

**Dependencies**: stage 2. **Implements**: Q1's TPA type, Q2, facts 7 to 14, the refusals.

* Loader: `TrustedPlatformAssemblies` (validated map; `tryCreate` returns the refusals of fact 16 and of the design),
  and `HostPolicy.resolveTpa : HostPolicyInput -> Result<TrustedPlatformAssemblies, HostPolicyRefusal>`, with the
  application cases of Q6, the frameworks of Q4, the RID list, and `UseRidGraph`.
* **Oracle**:
  * **Against a transcription, high count.** A second implementation in the test project, written as a line-by-line
    transcription of `resolve_tpa_list` and `get_dir_assemblies`: a mutable dictionary keyed case-sensitively, the
    two loops, the extension check, the replacement rule. The function under test is written independently, as a
    fold over entries. Generate inputs: application entries and framework entries over a small pool of names (so
    that collisions are common), names differing only by case, versions drawn below, equal to and above each
    other component by component, empty versions, placeholders, `localPath`s, RID-specific assets, `.exe` assets,
    no `deps.json` with a listing, missing framework `deps.json`. Require equal outcomes, refusals included, at
    10 000 cases or more.
  * **Metamorphic properties** at the same count: permuting the frameworks when no two list a name leaves the TPA
    unchanged; raising an application entry's stated assembly version above every framework's makes it win; with no
    `deps.json` every framework-listed name resolves into its framework's directory; every output path lies in the
    directory of the manifest which listed it; output names are distinct ignoring case.
  * **Against the real host, low count.** Generate an application directory with a generated `deps.json` (and,
    sometimes, none), for names the framework under test also has, with versions below, equal to and above the
    framework's, and some missing files. Files need not be real assemblies: the host reads only the `deps.json`
    (measured: a stated 12.0.0.0 bound a 9.0.0.0 file). Put in a real guest that prints the TPA. Run it on real .NET
    through `RealRuntime`, and compare with the function's map name by name, after CoreCLR's own dedup (first entry
    per name ignoring case; on .NET 10 the appended CoreLib is the second spelling). Each case starts a process, so
    keep it to about 25 cases per run. It runs in the default suite (it starts no PawPrint guest), on both runtime
    legs, and on Linux CI as well as macOS, which is what checks the linux RID list.
  * **The measured table above** as unit examples, one per row.

### Stage 4: thread the binding source (pure refactor)

**Dependencies**: none (may run beside 2 and 3). **Implements**: Q3 option C.

* Loader: `AssemblySource`, with one case holding today's directory list. Every `dotnetRuntimeDirs : string seq` in
  Loader, TypeSystem, Analysis and the interpreter becomes `AssemblySource`; `IlMachineState.DotnetRuntimeDirs` and
  `ProgramLaunch.DotnetRuntimeDirs` become `Assemblies`. `GuestConfig` is unchanged; `Program.prepare` converts.
* Test helpers: one function, `FrameworkUnderTest.assembliesFor platform application`, where `application` says
  which of Q6's cases the test runs (in memory; a directory without `deps.json`; a publish). In this stage it returns
  exactly the directory list each call site builds today, the F# cases' list without the pack included. Call sites
  move to it.
* **Oracle**: it compiles, `fantomas` is clean, and the default suite and every Guest category pass unchanged on
  both runtimes. No test changes except call sites.

### Stage 5: bind from the TPA (the switch)

**Dependencies**: stages 1, 3, 4, and the locator release of Q4. **Implements**: the decided direction, Q1 option A,
Q6, Q7, Q10, Q11.

Commits, in order:

1. **Red, the witnesses.** The `di-version` consumer scenario and the `PackageReference`. On Net11 every consumer
   scenario fails at `GuestPublish`'s refusal: record the output. The no-`deps.json` Roslyn witness (Q11), red on
   both runtimes: PawPrint binds the directory's copy. A guest whose `deps.json` names a missing file which the
   framework also has at a lower version: real .NET throws `FileNotFoundException`, PawPrint today binds the
   framework's.
2. **Delete `GuestPublish`'s refusal.** Still red, now for the binding: `di-version` prints 9.0.0.0 under PawPrint
   and 11.0.0.0 on real .NET 11. Record it.
3. **The switch.** `AssemblySource` holds the TPA; the binder and `AssemblyBinding` read it (fact 17 with `APP_PATHS`
   unset; a missing file is a miss); the entry-name refusal of Q6; `GuestConfig.Assemblies` replaces
   `DotnetRuntimeDirs`; `FrameworkUnderTest.assembliesFor` builds the input (pack as `Microsoft.NETCore.App`, the
   shared ASP.NET Core) and calls the function; the App reads the files, refuses the settings of Q10 and Q7, and
   calls the function; `PatchedCoreLib`'s tests build a TPA naming the patched CoreLib.
4. **Docs.** `docs/user/run-a-program.md`; `docs/divergences.md` (the host-properties entry, per Q9); the test
   project's `AGENTS.md` ("binding is by simple name and takes the first hit"); `FrameworkUnderTest`'s docstrings.

* **Oracle**: the witnesses go green on both runtimes; the default suite and every Guest category pass on Net10 and
  Net11; the mutants below die.
* Expect some guests to change binding. On Net10 the test host's `bin` directory leaves the list, so a guest that
  bound FSharp.Core or NUnit from it now fails to bind, as it does on real .NET. Each such change is a correction:
  record it in the PR body.

### Later, not in this work

* Q9 B, seeding the property, with the other host properties.
* Q7 B, self-contained publishes.
* Q10 B, existence checks.
* Deleting Q4's overlap refusal once the order is a tested input.

## Mutants the implementer must run

Each is temporary and never committed; commit before the first and rebuild after each revert. Record the table in
the PR body.

Function (stage 3; each must fail the transcription test and, where named, a measured example):

| Mutant | Must kill it |
|---|---|
| file version `>=` becomes `>` | transcription; the tie row (framework wins) |
| file version ignored on equal assembly versions | transcription; the "file version + 1" row |
| assembly version ignored | transcription; the "deps says 12" row |
| versions read from the file instead of the `deps.json` | the "deps says 12" row |
| frameworks processed before the application | transcription; metamorphic "application wins when higher" |
| no `deps.json`: listed files given the framework's version | the "no `deps.json`" row |
| placeholder `_._` not skipped | transcription |
| extension check removed | transcription (the host fails the launch) |
| `localPath` ignored | the real-host test (paths differ) |
| RID list order reversed | transcription; the real-host test on a RID-specific case |
| the case-insensitive collision refusal removed | transcription |
| the framework-overlap refusal removed | its unit case |

Binder (stage 5; each must fail an end-to-end witness):

| Mutant | Must kill it |
|---|---|
| bind by first hit along the TPA's directories (the old behaviour) | `di-version` on Net11; the no-`deps.json` witness |
| a missing TPA file falls back to another file of that name | the missing-file guest |
| a name outside the TPA binds from the application directory (an `APP_PATHS` stand-in) | a guest referencing an assembly in its directory but not in its `deps.json`: real .NET fails to bind |
| the entry-name refusal removed | its unit case |

## Appendix: the probe

`TpaProbe.csproj`: `net10.0`, `Exe`, `UseAppHost=false`, `PackageReference`s to
`Microsoft.Extensions.DependencyInjection` 9.0.2 and `WoofWare.DotnetRuntimeLocator` 0.5.8. Published with
`dotnet publish -c Release -o <dir>`, and for the self-contained row with `-r osx-arm64 --self-contained`.

```csharp
foreach (var key in new[] { "RUNTIME_IDENTIFIER", "APP_PATHS", "APP_CONTEXT_DEPS_FILES", "FX_DEPS_FILE", "PROBING_DIRECTORIES" })
    Console.WriteLine($"PROP {key}={AppContext.GetData(key) ?? "<null>"}");

var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
var entries = tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
Console.WriteLine($"TPACOUNT {entries.Length}");
foreach (var e in entries) Console.WriteLine($"TPA {e}");

if (args.Contains("di"))
{
    try
    {
        var asm = Type.GetType("Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.DependencyInjection.Abstractions", throwOnError: true)!.Assembly;
        Console.WriteLine($"DI {asm.GetName().Version} {asm.Location}");
    }
    catch (Exception ex) { Console.WriteLine($"DIERR {ex.GetType().FullName}: {ex.Message}"); }
}

foreach (var a in args.Where(a => a.StartsWith("select=")))
    foreach (var d in WoofWare.DotnetRuntimeLocator.DotnetRuntime.SelectForDll(a.Substring("select=".Length)))
        Console.WriteLine($"SELECT {d}");
```

Host traces came from the same runs with `COREHOST_TRACE=1` and `COREHOST_TRACEFILE`. The variants of the table
edited the copied publish's `deps.json` (the Abs asset's `assemblyVersion` and `fileVersion`) or deleted files. The
pack comparisons read `runtimes/<rid>/lib/<tfm>/Microsoft.NETCore.App.deps.json` out of the pinned nupkgs in the Nix
store and compared each runtime asset's name, assembly version and file version with the shared framework's.
