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

## Decisions

Decided 2026-11-19 by the delegated decider, against this plan at `7510a8e8f`. The numbers match the questions
above. Four decisions change published public API or another repository. Each of those is a recommendation marked
**Owner to confirm**, with the question to put to the owner.

Three things were found while deciding. They change the plan.

* **A self-contained publish is the documented way to run a program** (read). `AGENTS.md` ("Running the
  application"), `docs/user/run-a-program.md`, the debugger-server skill, `HelloWorld.fsproj` and the flake's
  publish check all use `--self-contained`. So Q7's premise, "no consumer needs it", is wrong. See decision 7.
* **A runtimeconfig can set `APP_PATHS` without the `SetAppPaths` switch** (read, not measured).
  `RuntimeConfig.fs` admits a `configProperties` entry named `APP_PATHS` as an ordinary property when the switch is
  off. CoreCLR reads any property of that exact name into its binder (`src/coreclr/vm/corhost.cpp:606`). So fact 18
  is incomplete: `APP_PATHS` can be set by the application's own config. PawPrint seeds the value and its binder
  ignores it, today and after the switch. Filed as #2353. See decision 10.
* **An abbreviation makes the refactor of Q3 free of any break** (measured for the language rule only). An F# type
  abbreviation is erased, so a signature that says `AssemblySource` where it said `string seq` is the same
  signature. See decision 3.

### 1. `GuestConfig` holds the TPA: option A — **Owner to confirm**

`GuestConfig.Assemblies : TrustedPlatformAssemblies` replaces `GuestConfig.DotnetRuntimeDirs`. The type is a
validated map from simple name to one absolute `.dll` path. It is built only through a function that returns a
`Result`. It has structural equality, because it is part of the replay contract. It has no `AppPaths` field.
`GuestConfig.Default` takes the TPA.

`UnsupportedRuntimeException` in `WoofWare.PawPrint.Domain` loses its `DotnetRuntimeDirs` member and that
constructor argument. Domain cannot see the TPA type, and `UnsupportedRuntime.CoreLibPath` already names the file.
The message names that path.

Why A:

* It is CoreCLR's own boundary. The host decides the TPA, and the runtime binds from it.
* A stored config keeps its binding when the function is later fixed. The run records the answer.
* B keeps the first-file rule alive as a fallback. A name outside the TPA would bind under PawPrint and fail on
  real .NET. That is the divergence which #2351 is about.
* C puts a case with no host counterpart (the in-memory guest) into `GuestConfig`, and ties replay to the version
  of the function. C can be added later as a helper that builds the TPA.

Two rules which the plan does not state are added to the function, so that a TPA from the function is one which
the binder can honour:

* **CoreLib.** CoreCLR binds CoreLib from the directory of `libcoreclr` before it reads the TPA (fact 19). So the
  function refuses, as "not reproducible", when `System.Private.CoreLib` is absent from the result, or resolves
  outside the directory that holds the runtime. That directory is the `Microsoft.NETCore.App` framework's, or the
  application's when the application is self-contained.
* **The entry assembly.** The input names the entry assembly's file in every application case. The function
  refuses when the result maps the entry's simple name to any other file. With an image held in memory, that
  means any file at all (Q6). This is the general form of Q6's refusal, and it puts the refusal in the pure
  function, where the transcription test reaches it. Fact 20 is read and not measured, so stage 3 measures it
  first: a directory with no `deps.json` whose entry assembly is named as a framework assembly, run on real .NET.
  If the host launches it, the refusal is "not reproducible" and stays. If the host fails, the refusal is "the
  host would fail this launch".

**Question for the owner.** `GuestConfig.DotnetRuntimeDirs` is public in `WoofWare.PawPrint` and part of the
replay contract. May it be replaced by `GuestConfig.Assemblies : TrustedPlatformAssemblies`, a validated map from
simple name to one absolute `.dll` path with no `AppPaths` field, and may `UnsupportedRuntimeException` in
`WoofWare.PawPrint.Domain` lose its `DotnetRuntimeDirs` member? The alternatives are: keep the directory list
beside the TPA as a fallback (no break, but a name outside the TPA binds under PawPrint and not on real .NET); or
store the host's inputs (the `deps.json` bytes and directories) and compute the TPA at launch.

### 2. The function lives in `WoofWare.PawPrint.Loader`: option A — **Owner to confirm**

Loader gains three public modules with their types: `TrustedPlatformAssemblies` (the validated map), `DepsJson`
(the parser) and `HostPolicy` (the function, its input and its refusals).

Why A: Loader already answers "which file does a name bind to", and Analysis sees Loader, so the escape analysis
binds as the interpreter does. A new project costs a new published package and enforces nothing new. The locator
is the wrong home: the TPA is `hostpolicy`'s job, and the refusals say what PawPrint can reproduce.

One change to Q10 belongs here, because it widens the input. The input also carries the host settings that turn
on existence checks, as values. See decision 10.

Until the owner answers, stages 2 and 3 land these modules as `internal`. Loader's internals are already visible
to the test project, so the tests are the same. Nothing public changes until stage 5.

**Question for the owner.** The function that computes the TPA, the `deps.json` parser and the TPA type would be
new public API in `WoofWare.PawPrint.Loader` (modules `HostPolicy`, `DepsJson` and `TrustedPlatformAssemblies`).
Is Loader the right home? The alternatives are a new published package `WoofWare.PawPrint.HostPolicy`, or
`WoofWare.DotnetRuntimeLocator`.

### 3. One break, after a refactor that breaks nothing: a new option D — **Owner to confirm**

The plan's options were A (one large break), and B or C (two breaks, with a public `AssemblySource` type in
between, kept afterwards as a wrapper round the TPA). Decision: neither.

* **Stage 4b, no break.** Loader gains a public type abbreviation, `type AssemblySource = string seq`. Every
  `dotnetRuntimeDirs : string seq` parameter in Loader, TypeSystem, Analysis and the interpreter becomes
  `assemblies : AssemblySource`. An abbreviation is erased, so every signature is unchanged, for binary and for
  source consumers. Public record fields keep their names and types. The only public change is the new name
  `AssemblySource`.
* **Stage 5, one break.** The semantic commit points the abbreviation at `TrustedPlatformAssemblies` and fixes the
  places that build or read the value. The roughly 500 annotated parameters do not change in that commit. The last
  commit of the PR replaces the token `AssemblySource` with `TrustedPlatformAssemblies` everywhere, deletes the
  abbreviation, and renames the public fields `IlMachineState.DotnetRuntimeDirs` and
  `ProgramLaunch.DotnetRuntimeDirs` to `Assemblies`. That commit is a token rename, and a word diff shows it.

Why D:

* The libraries' signatures break once, and `GuestConfig` breaks once. Under C the libraries break twice.
* The end state has one type. Under B and C a single-case wrapper outlives the migration and holds nothing but
  the TPA.
* The mechanical step still gets its own PR and its own full run of every guest category, as C wanted.
* The cost is a public name that lives for a few days and names a directory list.

If the owner wants no temporary public name, choose A, and structure the switch PR as the same three commits
(abbreviation, semantic change, token rename) so that the abbreviation never reaches `main`.

The language rule was checked in F# interactive only. Stage 4b confirms it in the build: the package's public
signatures before and after must be equal. If they are not, stop and report; do not fall back to C unasked.

**Question for the owner.** The binder's input is threaded as `dotnetRuntimeDirs : string seq` through about 500
places in the signatures of Loader, TypeSystem, Analysis and `WoofWare.PawPrint`. May a first PR add a public type
abbreviation `AssemblySource = string seq` to Loader and use it in those signatures (no binary or source break,
but a public name that lives until the switch), so that the switch PR then breaks those signatures exactly once
and deletes the abbreviation? The alternatives are: one PR that does everything, with no temporary name; or the
planner's two breaks, with a real `AssemblySource` type that stays as a wrapper round the TPA.

### 4. The locator returns each framework's name and directory, in `hostfxr`'s order: option B — **Owner to confirm**

`WoofWare.DotnetRuntimeLocator` gains a function beside `SelectForDll`. It returns, for a DLL, the frameworks as
`hostfxr` resolves them: name, version and directory, highest level first, following each framework's own
runtimeconfig to its base framework as `read_framework` does. It does not include the application's directory.
`SelectForDll` is unchanged. `hostfxr` is its oracle, as for the rest of the locator. Filed as
the locator issue (filed once the owner confirms decision 4), for the locator's repository.

Why B: resolving frameworks is `hostfxr`'s job, and the locator already transcribes it. Option A reads the
framework's name off a directory layout which is a convention, and C writes a second transcription of `hostfxr`.

The refusal for two frameworks that list one name stays in PawPrint's function, not in the locator. It is deleted
when the order is a tested input.

The App needs the locator only for a framework-dependent application. A self-contained application has no
frameworks (decision 7). The test harness needs no locator change: the devshell names each framework. So only
stage 5 waits for the locator's release.

**Question for the owner.** The App must tell the function each framework's name and directory in `hostfxr`'s
order. May `WoofWare.DotnetRuntimeLocator` gain a function that returns them (name, version, directory; highest
level first; base frameworks followed), leaving `SelectForDll` as it is? The alternatives are: the App derives
each name from the `shared/<name>/<version>` layout and takes the locator's present order; or PawPrint walks the
framework runtimeconfigs itself.

### 5. The pinned packs carry their `deps.json`: option A

`frameworkPack` copies `Microsoft.NETCore.App.deps.json` into the pack directory. The harness passes the pack as
`Microsoft.NETCore.App`, with its own `deps.json`. No input pairs one RID's `deps.json` with another RID's files,
and no input has a pack ahead of a framework.

The RID list which the harness passes is the list for the pack's RID, not the host's. The App passes the list for
its host. The RID list is a value in the input; the function never asks the host.

### 6. Three application cases: option A, with the entry assembly named in each

A directory with a `deps.json`; a directory with no `deps.json` and the names of its files; an image held in
memory. Each case also names the entry assembly's file (decision 1), which is where the Q6 refusal comes from.

One gap to close in stage 5, not to assume. The real-runtime oracle runs a Roslyn guest from a scratch directory
with no `deps.json`, and a filesystem seed's files land in that directory. A seeded `.dll` is therefore in the
host's TPA and not in PawPrint's. The harness must use the second case, with the seed's file names, for a guest
whose seed holds a `.dll` beside it, or refuse such a seed. The implementer picks one and tests it.

### 7. Self-contained publishes are supported from the first version: option B

This reverses the plan's recommendation. A self-contained publish is how the repository tells people to run a
program, and how its own playground and `HelloWorld` are built. Today such a publish binds every name from the
application's directory, because that directory is first on the list; that is also what the host does. Refusing
it would break the documented path, and the App would be left with only the path that waits for the locator.

What it costs:

* The parser already has the mode (stage 2: the self-contained parse reads `targets` only).
* The function takes an empty framework list with the self-contained mode. A non-empty framework list with that
  mode, or an empty one without it, is unrepresentable in the input type.
* The App decides the mode from the runtimeconfig as `hostfxr` does: an application whose runtimeconfig names no
  framework is self-contained. The App then needs no locator call at all.
* The CoreLib rule of decision 1 uses the application's directory.

The CoreLib of such a publish is the publish's RID's, which may differ from the emulated kernel's flavour. That
is so today and is not part of this work.

### 8. No refusal lands first: option B

As the plan argues: a name-collision refusal in the App would mostly refuse runs that bind correctly today, and
it would miss the missing-file and RID-specific cases. The exact refusal (option C) stays the fallback if stage 5
stalls after stage 3.

### 9. The guest does not read the TPA back: option A

`TRUSTED_PLATFORM_ASSEMBLIES` stays absent. No bind depends on it, and neither runtime's order can be reproduced.
Stage 5 rewrites the entry in `docs/divergences.md`: PawPrint now has a TPA and does not seed the property.

### 10. Unmodelled host settings are refused, by the function: option A, moved

The function models the host with existence checks off. The settings that turn them on are refused, as the plan
says. The change: the refusal is the function's, not the App's alone.

* The input carries the settings as values: additional probing paths, additional `deps.json` files, a shared
  store, a servicing directory. A non-empty list is a "not reproducible" refusal.
* The App and the test harness each gather the facts (their host reads) and pass them. Neither decides.
* Why: the harness's real-host test (stage 3) runs on CI hosts whose settings were not measured. If one has a
  store directory, the test must refuse and say so, not report a mismatch. One rule in one place does that.
* `DOTNET_RUNTIME_ID` joins the list of what the App and the harness refuse when set. Honouring it is an
  addition for later.
* `UseRidGraph=true` and `SetAppPaths=true` are refused as the plan says.

A config-supplied `APP_PATHS` is a separate matter, with its own issue (#2353). It is
wrong today, it does not depend on this work, and it can be fixed now.

### 11. The witness: option A, a `di-version` scenario, plus a package-free witness

`di-version` joins `WoofWare.PawPrint.Test.ConsumerCases` in the switch PR, as its first commit, with the
`PackageReference` at 9.0.2. The Roslyn-built directory with no `deps.json` is the second witness. A third is
added for decision 7: a self-contained publish run through the App's path.

### Staging

The plan's stage numbers are kept. Every stage commits its tests first, with the failing output in the PR body.

**Can start now. No owner answer can change them.**

* **Stage 1: the packs carry their `deps.json`** (decision 5). Contents and oracle as the plan says: for each
  pinned pack of each runtime, the `deps.json`'s runtime assets are exactly the pack's DLLs. The test fails when
  the file is absent.
* **Stage 4a: one helper for the harness's binding input.** `FrameworkUnderTest.assembliesFor platform
  application`, where `application` is one of decision 6's cases. In this stage it returns exactly the directory
  list which each call site builds today. Call sites move to it. Test code only. Oracle: the default suite and
  every guest category pass unchanged on both runtimes.
* **The `APP_PATHS` fix** (#2353). Its own small PR, measured first. Done in #2354: `RuntimeConfig.combine` and
  program start refuse a config-supplied `APP_PATHS`. `SetAppPaths=true` alone is still accepted; decision 10 covers
  it.

**Can start now, as `internal` code in Loader.** Nothing public changes. If the owner chooses a new package for
Q2, the files move unchanged. Only the locator option of Q2 would discard work: the function would be rewritten
in C#, and its generators, its transcription and its real-host test would carry over.

* **Stage 2: `DepsJson.parse`.** As the plan says, both parse modes included. Oracle as the plan says: the
  model-based property test, the version parser against a transcription of `version_t::parse`, a unit case for
  each refusal, and the real `deps.json` files of both pins, packs included once stage 1 is in.
* **Stage 3: the TPA type and `HostPolicy.resolveTpa`.** After stage 2. As the plan says, plus: the self-contained
  mode (decision 7); the CoreLib rule and the entry-assembly rule (decision 1); the existence-check settings
  (decision 10). Oracle as the plan says: the transcription at 10 000 cases or more, the metamorphic properties,
  the real-host test at about 25 cases, and the measured table as unit examples. Added:
  * the measurement of fact 20, then a unit example for whichever way it falls;
  * one real-host case on a self-contained publish, compared name by name as the others are;
  * the transcription covers the self-contained mode (it is the same loop with no frameworks);
  * mutants, beside the plan's: the CoreLib rule removed; the entry-assembly rule removed; a non-empty
    existence-check list accepted; the self-contained parse reading `runtimeTargets`.

**Wait for the owner.**

* **The locator PR** waits for Q4. It is in the locator's repository, with `hostfxr` as its oracle, and ends in a
  release. It can run beside every stage here.
* **Stage 4b: thread `AssemblySource`** waits for Q3. It needs no other stage. Contents as decision 3 says. Oracle:
  the public signatures of each package are equal before and after; `fantomas` is clean; the default suite and
  every guest category pass unchanged on both runtimes. If the owner chooses "one PR", this stage becomes the
  first commit of stage 5.
* **Stage 5: the switch** waits for Q1, Q2, Q3 and Q4, for stages 1, 3, 4a and 4b, and for the locator's
  release. Commits, in order:
  1. Red: `di-version` and its `PackageReference`; the no-`deps.json` witness; the missing-file guest; the
     self-contained witness if it is red (it may be green already, since today's binding agrees with the host
     there; say which in the PR body).
  2. Delete `GuestPublish`'s refusal. Record the wrong binding of `di-version` on Net11.
  3. The switch. The stage 3 modules become public. `AssemblySource` abbreviates `TrustedPlatformAssemblies`. The
     binder and `AssemblyBinding` read the TPA: a name in it binds to that file; a missing file is a miss; a name
     outside it is a miss; no directory is listed at bind time. `GuestConfig.Assemblies` replaces
     `DotnetRuntimeDirs`, and `UnsupportedRuntimeException` changes with it. `assembliesFor` builds the input and
     calls the function. The App reads the runtimeconfig, takes the self-contained path or asks the locator for
     the frameworks, reads the `deps.json` files, gathers the settings of decision 10, and calls the function.
     `WoofWare.PawPrint.Performance` moves the same way. `PatchedCoreLib`'s tests build a TPA that names the
     patched CoreLib.
  4. Docs, as the plan lists, and the divergence entry of decision 9.
  5. The token rename of decision 3, and nothing else.

  Oracle: the witnesses go green on both runtimes; the default suite and every guest category pass on Net10 and
  Net11; the plan's binder mutants die. Each guest whose binding changes is recorded in the PR body, with the
  reason.

  Stage 5 is the only large PR. Do not grow it. If the App's part proves large, the App's gathering of inputs
  (reading the runtimeconfig, the `deps.json` files and the settings into the function's input, and printing the
  refusal) may land before the switch as code which computes the TPA and does not yet use it.

After stage 5, PR 4 of the consumer-guest plan can start.

**Not in this work**, as the plan lists, less self-contained publishes: seeding the property (Q9 B); existence
checks (Q10 B); deleting the framework-overlap refusal once the locator's order is in use.

## As built (stage 1)

Branch `host-binding-1-pack-deps-json`, on `origin/main` at `c81deb2de`, after the plan and decisions commits.

* **The flake.** `frameworkPack` copies `runtimes/<rid>/lib/<tfm>/Microsoft.NETCore.App.deps.json` beside the
  DLLs it already copied. The comment above it says why. The fixed-output hashes are of the nupkgs, so none
  changed. Nothing in PawPrint reads the file yet.
* **The test.** `TestFrameworkUnderTest`, "A pinned runtime pack's deps.json lists exactly the pack's
  assemblies", in the default suite. It has one case per runtime and flavour: four packs, read whichever runtime
  is under test. Each case:
  * fails when the pack directory holds no `Microsoft.NETCore.App.deps.json`;
  * checks that `runtimeTarget` is `.NETCoreApp,Version=v<major>.0/<rid>`, for the pack's RID (read as a string or
    as an object with a `name`, as `deps_json_t::load` reads it);
  * collects the runtime assets which the host reads, each with the path at which the host looks for it;
  * requires each asset to be a file of its own name directly in the pack, so that its TPA name is the file's stem;
  * requires those paths to equal the set of files in the directory whose names end `.dll` in any case, as
    `AssemblyProbe` binds them. Each set difference is compared with the empty list, so a failure names the files.

  An asset whose path ends `/_._` is a placeholder, which `resolve_tpa_list` skips (read:
  `hostpolicy/deps_resolver.cpp:430-434`); the test skips it too. Like the host, the test reads `\` as `/` in an
  asset's path and `localPath` (read: `hostpolicy/deps_entry.h:20-25`), drops a leading `./` from the probed
  path, and skips a UTF-8 byte order mark before parsing.

  The host reads `Microsoft.NETCore.App`, the lowest framework, in self-contained mode (read:
  `hostpolicy/deps_resolver.h:68-87`, `deps_format.cpp:541-555`). That mode takes, for each package which
  `libraries` declares, the `runtime` assets of the runtime target, and reads no `runtimeTargets`. The test reads
  the same. A unit case on a synthetic manifest pins this: an undeclared package's asset and a `runtimeTargets`
  asset are not counted. Fact 3 above covers only the application's file; stages 2 and 3 need this rule for the
  frameworks too.

  The path at which the host looks for a runtime asset is `deps_entry_t::to_dir_path` (read:
  `hostpolicy/deps_entry.cpp:141-184`): the asset's `localPath` when it is set and non-empty; else, in a library of
  type `runtimepack`, the listed path; else the listed path's file name. A second unit case covers the three
  rules.

  Outside the Nix devshell the cases are ignored, as the other pack tests are: only the devshell names the packs.
  `FrameworkUnderTest.requireFrameworkPackDirectoryOf flavour runtime` is new, so that a test can name any
  runtime's pack and not only the runtime under test's.
* **Measured** (the pinned nupkgs, read with the test's rule). Each pack's `deps.json` has one library,
  `Microsoft.NETCore.App.Runtime.<rid>/<version>`, no `runtimeTargets`, and its runtime asset paths are bare file
  names. Runtime assets: 172 on 10.0.14 and 181 on 11.0.0-rc.3, for both RIDs, as the plan measured.
* **Decision 5's RID list is not implemented here.** Nothing reads the file yet. When the harness builds the
  function's input (stage 5), it passes the list for the pack's RID, not the host's: `linux-x64, linux, unix-x64,
  unix, any` for the Linux pack, and `osx-arm64, osx, unix-arm64, unix, any` for the Darwin pack. The test's
  private `packRid` holds the RID of each flavour's pack; stage 5 can move it into `FrameworkUnderTest`.

The red run, before the flake change: all four cases failed with "`<pack>` holds no Microsoft.NETCore.App.deps.json".

Mutants, each run against the four pack cases and the unit case:

| Mutant | How it failed |
|---|---|
| The flake does not copy the `deps.json` | "holds no Microsoft.NETCore.App.deps.json" |
| The flake drops `Microsoft.VisualBasic.dll` after the copy | listed but absent: `Microsoft.VisualBasic.dll` |
| The flake copies an extra DLL, `NotListed.dll` | present but unlisted: `NotListed.dll` |
| The test's expectation gains `NotInThePack.dll` | present but unlisted: `NotInThePack.dll` |
| The test swaps the two flavours' RIDs | the `runtimeTarget` check |
| The test counts packages which `libraries` does not declare | the unit case: `C.dll` counted |
| The test counts `runtimeTargets` assets | the unit case: `runtimes/rid/lib/net10.0/B.dll` counted |
| The flake gives `Microsoft.VisualBasic.dll` a `localPath` of `missing/Microsoft.VisualBasic.dll` | the asset is not a file of its own name |
| The flake swaps the `localPath`s of `Microsoft.VisualBasic.dll` and `Microsoft.CSharp.dll` | the asset is not a file of its own name |
| The same swap, with that check removed from the test | survives: the sets are equal, so only that check sees a swap |
| The test ignores `localPath` | the second unit case |
| The test treats a `runtimepack` library as a package | the second unit case |

| The flake copies an extra `NotListed.DLL` | present but unlisted: `NotListed.DLL` |
| The same, with the test's extension filter made case-sensitive | survives on macOS too: the filter is what sees it |
| The test does not skip placeholders | the first unit case: `_._` counted |
| The test reads `runtimeTarget` only as an object | the `runtimeTarget` unit case, string form |

| The test does not read `\` as `/` in an asset's path | the probed-path unit case |
| The test does not read `\` as `/` in a `localPath` | the probed-path unit case |
| The test keeps a leading `./` | the probed-path unit case |
| The test does not skip a byte order mark | the byte-order-mark unit case |

The flake mutants kill the four pack cases; the test mutants kill the unit case they name. The two surviving
double mutants show that the name check is the only thing that catches a swap, and that the case-insensitive
filter is what catches an upper-case extension.

Suites on the .NET 10 host, each its own invocation, at the branch tip after the four Codex rounds, on
`origin/main` at `c81deb2de`: default 5995 (5956 passed, 39 skipped), `Guest` 250, `GuestSolo` 2, `GuestPure` 1210,
`GuestImpure` 306, all passed. (They also all passed before the Codex rounds, on `origin/main` at `2b38151e8`.)

### Codex review

**Round 1.** Twelve findings. One was about stage 1's code, and was fixed:

* *The test counted assets the host does not read* (`runtimeTargets`, and packages `libraries` does not declare).
  Fixed tests first, as above.

The other eleven are about the plan's later stages. None changes stage 1, so none is acted on here. Each is
recorded for the stage it concerns. Codex states that it measured several of them on .NET 10; none is
re-measured here, so each is a claim for that stage to check before relying on it.

* Stage 5: `assembliesFor` should include ASP.NET Core only for a guest whose oracle selects it. Codex reads
  `RealRuntime.captureAssemblyInPlace` as naming only `Microsoft.NETCore.App`, and the .NET 10 ASP.NET Core
  `deps.json` as listing `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0.0, which would beat the
  `di-version` publish's 9.0.0.0 while the oracle binds 9.0.0.0.
* Stage 4b: an erased abbreviation keeps parameter types, not parameter names. Renaming `dotnetRuntimeDirs` to
  `assemblies` breaks a C# caller that passes the argument by name. Keep the names until stage 5, or accept that
  break.
* Stage 3, fact 10: Codex says `resolve_tpa_list` replaces an entry only when the winner resolves to a different
  path, and that a manifest listing one name twice in the application (versions 1, then 12) kept version 1 for
  the comparison with the framework.
* Stage 3, fact 14: Codex says a library of type `runtimepack` resolves an asset by its full relative path when
  `localPath` is absent (`sub/Test.dll` became `<app>/sub/Test.dll`).
* Stage 3, the TPA type: a path holding the path-list separator (`:` on Unix) cannot pass through the host's TPA
  string. Codex says .NET 10 failed to start from a directory named `colon:dir`.
* Stage 3, the metamorphic property "every output path lies in the directory of its manifest": a `localPath` of
  `../common/X.dll` resolves outside it. Refuse such paths, or drop the property.
* Decision 1, the entry-assembly rule: the refusal needs the entry image's metadata name, not its file name.
* Stage 4a and 5: outside the Nix devshell there is no pack. `assembliesFor` must then use the installed
  framework's own `deps.json` and the host's RID list, as `runtimeDirsFor` falls back today.
* Stage 3, the self-contained real-host case: `RealRuntime` runs a guest with a generated framework-dependent
  runtimeconfig, so it cannot exercise a self-contained publish. That case needs its own launch.
* "The two pins agree on the rule": Codex says they differ outside the subset the plan accepts (`.ni.dll` in a
  directory without `deps.json`; servicing probes). The claim should be limited to the accepted inputs.
* The stage 3 mutants "frameworks processed before the application" and "no `deps.json`: listed files given the
  framework's version" need witnesses whose winner changes: the equal-version tie row, and a strictly higher
  local version.

**Round 2.** Seven findings. One was about stage 1's code, and was fixed:

* *The test read an asset's JSON key as its file*, where the host looks at its `localPath` first. Codex gave
  `Microsoft.VisualBasic.dll` a `localPath` naming a missing file, and the test stayed green. Fixed tests first,
  as above.

The other six are about later stages, recorded as in round 1, unverified here:

* Decision 1, the CoreLib rule: an application's `deps.json` can name a native `libcoreclr`, which hostpolicy
  resolves before the framework's (`resolve_probe_dirs`). Codex says an absent one made .NET 10 exit 135. The rule
  should refuse an application-selected CoreCLR, or take its location as input.
* Stage 3, the real-host comparison: it must use the RID list and framework directories of the real child
  process, not the pack's. The pack's RID list is for the interpreted guest (decision 5). Foreign-pack selection
  needs its own test.
* Decision 11, `di-version`: `TestGuestParking`'s check that every consumer library has a scenario showing a
  generated type would fail for a `di` scenario, which prints a version. Stage 5 must register binding-only
  scenarios apart from that check.
* Decision 1's alternative "keep the directory list beside the TPA, no break": adding a field to the public
  `GuestConfig` record changes its constructor, so it is a break for code that constructs one. The owner's
  question should not call it "no break".
* Stage 4a: `TestFSharpPureCases` runs its publishes without a pack, and `TestConsumerCases` with one. One helper
  keyed on the platform and the application case cannot return both lists. Leave the F# caller out of 4a, or
  pass the pack choice explicitly.
* Q6's "directory with no `deps.json`" input: Codex says .NET 10 admitted a directory named `X.dll` into the TPA
  and omitted a dangling link named `Y.dll`. The input should be the entries the host's `pal::readdir` admits,
  or the gatherer should refuse other entry kinds.

**Round 3.** Six findings. Three were about stage 1's test, and were fixed:

* *A placeholder asset (`/_._`) was counted as a missing assembly.* Fixed tests first.
* *A string `runtimeTarget` made the test throw.* Fixed tests first.
* *On Linux, `GetFiles (pack, "*.dll")` misses `X.DLL`*, which `AssemblyProbe` binds. The test now filters the
  extension ignoring case. On this macOS host the search pattern already matched `X.DLL`, so the mutant was red
  here before the change; Codex's Linux claim is not measured here.

The other three are about later stages, recorded as before, unverified here:

* Stage 5, the binder: a name outside the TPA is a miss for the disk probe, not for the bind. Codex says .NET 10
  returned an already-loaded assembly from `Assembly.Load` though the TPA did not list it.
  `AssemblyBinding.tryBind`'s lookup in the loaded context must stay ahead of the TPA.
* Decision 1, the entry-assembly rule: `Program.prepare` takes the image apart from `HostConfig`, so a TPA built
  for one image can be reused with another. The refusal belongs where the image and the TPA meet, at startup, as
  well as in the function.
* Stage 3, the real-host test: the guest that prints the TPA must not itself load a name whose winner is a missing
  or dummy file. Codex says a missing app-local `System.Console.dll` at a higher version made the guest throw
  before it printed anything. Keep those names out of the generator, or read the host's trace instead.

**Round 4.** Five findings. Three were about stage 1's test, and were fixed tests first: a backslash in an asset
path or `localPath`; a leading `./` in a `localPath`; a UTF-8 byte order mark. Each made the test fail on a
manifest the host accepts.

The other two are about later stages, recorded as before, unverified here:

* Stage 3, the metamorphic property "permuting the frameworks leaves the TPA unchanged": the lowest framework is
  parsed in self-contained mode and the others in framework-dependent mode, so a framework whose only asset is a
  `runtimeTargets` entry contributes it above the lowest position and not in it. Permute only the frameworks
  above the lowest one.
* Decision 6's gap: `RealRuntime.executeInScratch` reserves `<guest>.dll` and `<guest>.runtimeconfig.json` but
  lets a filesystem seed supply `<guest>.deps.json`. The harness must use a seeded manifest, or reserve its name,
  as well as handle a seeded `.dll`.

Four rounds were run, as the brief allows; round 4 still had findings in the test, which were fixed after it
without a fifth review. Each round's test fixes made the test agree with the host on a manifest shape that the
pinned packs do not use. They are a small transcription of `hostpolicy`'s reading of a framework's runtime
assets, which stage 2's parser will own; once it lands, this test can read the pack through it.

**Round 5**, run after the rebase onto #2354. Seven findings. None was acted on in this PR:

* *A pack variable missing inside the devshell makes the case skip, and the run still succeeds.* True, and it holds
  for every test that reads a pack through `requireDevshellDirectory`, guests included. It predates this PR, so it
  is filed as #2356 rather than fixed here for one test.
* *A pack holding `X.dll` and `X.ni.dll` passes, where the host refuses the pair.* No pinned pack holds a `.ni.dll`.
  Stage 3's function owns that refusal (decision 10).
* *A non-string `localPath`, and a `libraries` entry with no target, make the test throw on manifests the host
  accepts.* Neither shape is in a pinned pack. Stage 2's parser owns them, and its tests should cover both.
* Stage 5: the gatherer must read the host-policy switches from the frameworks' runtimeconfigs as well as the
  application's. Codex says `SetAppPaths=true` set only in `Microsoft.NETCore.App.runtimeconfig.json` made .NET 10
  set `APP_PATHS` to the application directory. Merge the properties with the precedence of `corehost_init_t`,
  then apply decision 10 to the result.
* Stage 3 and decision 10: a configured `additionalProbingPaths` entry that does not exist does not turn on the
  existence checks. Codex says .NET 10 logged "Ignoring additional probing path" and launched normally. The
  gatherer should pass only the paths which `append_probe_fullpath` admits, after its `|arch|/|tfm|` expansion.
* The staging listed #2353 as work still to start. It is marked done above.

The findings about later stages are unverified here, as in the earlier rounds.

## As built (stage 2)

Branch `host-binding-2-deps-json`, cut from `origin/main` at `566f797b7` and rebased onto #2357. The tests came first, in a commit of their own,
and so did the tests of each Codex finding fixed.

### What it adds

`WoofWare.PawPrint.Loader/DepsJson.fs`, all `internal` until the owner confirms decision 2. Loader's public
surface is unchanged: a reflection dump of `WoofWare.PawPrint.Loader.dll`'s exported types and members, built with
and without the file, is identical (1702 lines each).

* `DepsJson.parse : EmulatedRuntime -> DepsJsonMode -> byte[] -> Result<DepsManifest, DepsJsonError>`. The shape
  differs from the plan's `mode -> rids -> byte[] -> Result<DepsEntry list, _>` in three ways, each for a reason in
  the host's code:
  * The RID list is part of the mode: `DepsJsonMode.FrameworkDependent of rids : string list`, beside
    `DepsJsonMode.SelfContained`. The self-contained parse never reads it (`load_self_contained` does not call
    `process_runtime_targets`).
  * The runtime is an input. The two pins' hostpolicy differ in one thing the parser sees: .NET 10 reads each
    read library's `sha512` unchecked, and .NET 11 does not read it at all. Codex measured a .NET 11 host launching
    an application whose `deps.json` had no `sha512`.
  * The result is a `DepsManifest`: the entries, and the packages for which `deps_json_t::has_package` holds. A
    framework entry is found in its framework's directory only if that framework's manifest `has_package
    (library_name, library_version)`, which looks up `name/version`. An entry from a library keyed `P` (no `/`)
    has name and version `P`, so the host looks up `P/P` and does not find it, and the launch fails; an entry from
    `P/P` looks the same and is found. Codex measured this on .NET 11. `DepsManifest.hasPackage` is the lookup.
    The packages are those of the runtime target with an asset-type member (even an empty one), and, when
    framework-dependent, those with RID-specific assets that survived RID selection, whether or not `libraries`
    names them. This is not the set of libraries the host reads: a package with only RID-specific assets, none for
    a listed RID, has its library read and is not a package.
* The entries come in the host's loop order (`reconcile_libraries_with_targets`): each `libraries` member in order;
  within a library, its runtime, then resources, then native assets; each in the order of the runtime target. The
  host keeps one list per asset type, and the order within each type is this order filtered. All three types are
  returned (stage 3 needs native assets for `libcoreclr`; see below). Placeholders are returned, not dropped.
* `DepsEntry` holds what `deps_entry_t` holds that hostpolicy reads without existence checks: the library's name
  and version (split at the first `/`; a key with no `/` gives the whole key for both, as the host's
  `substr (npos + 1)` does), its type (`DepsLibraryType.RuntimePack` for `runtimepack` in any ASCII casing, else
  `Other` as written), the asset type, the asset's name (file name without extension, and without a further `.ni`
  in any ASCII casing), the listed path with `\` read as `/`, `localPath` (`None` when absent, empty or not a
  string; `\` read as `/`), the two versions, and whether it came from `runtimeTargets`.
* `DepsVersion` is `version_t`: `Empty`, or two, three or four components. `DepsVersion.parse` follows
  `parse_internal`; `DepsVersion.compare` follows `version_t::compare`. The type has no structural comparison, so
  that nothing compares two versions any other way; structural equality agrees with the host's (a test checks it).
  The digits are read without .NET's number parsers, which read the current culture.
* `DepsEntry.isPlaceholder`: the listed path ends `/_._` (`resolve_tpa_list`).
* `DepsEntry.dirRelativePath`: the path at which `deps_entry_t::to_dir_path` looks: the `localPath` if set; else,
  for a `runtimepack` library, the listed path; else the file name, under the culture directory for a resource
  asset (`get_directory`, `get_filename` and `append_path` transcribed, including a path with no directory, which
  gives `<file>/<file>`). The host probes a framework's directory this way, and the application's too, except for
  a RID-specific entry with no `localPath`, which it probes at its listed path (`to_package_path`, fact 14's third
  case).

The parser owns the rules that stage 1's test transcribes: the lowest framework read in self-contained mode
(stage 3 chooses the mode), placeholders, `localPath` precedence and non-string values, `runtimepack`, a
`libraries` entry with no target (not read), backslashes, a UTF-8 byte order mark (one is skipped, as
`json_parser_t::parse_file` does), and `runtimeTarget` as a string or an object. A leading `./` is not a host rule:
the host keeps it in the path it builds (`append_path`). Stage 1's test drops it to compare paths with file names,
and stage 3 must decide how a TPA path with `./` in it compares. Once stage 1 is on `main`, its test can read each
pack through `DepsJson.parse runtime DepsJsonMode.SelfContained`, keeping runtime entries that are not
placeholders, at `DepsEntry.dirRelativePath`.

### Refusals

`DepsJsonError` has a case for each of these. Each path runs from the document's root.

* *What the host reads without checking.* Where hostpolicy reads a member through rapidjson's `operator[]`,
  `GetString`, `GetBool` or `GetObject` with no check, a missing member or one of another kind is undefined
  behaviour (`RAPIDJSON_ASSERT` is `assert`, compiled out). The parser requires: the root an object;
  `runtimeTarget` a string, or an object with a string `name`; `targets` an object holding the runtime target, an
  object of objects; each asset-type member and each of its files an object; in framework-dependent mode,
  `runtimeTargets` and each of its files an object, each file's `assetType` a string, and, for a type the host
  knows, its `rid` a string; `libraries` an object; and for each library the host reads (one whose package has an
  asset-type member, or in framework-dependent mode a known `runtimeTargets` file), the value an object,
  `serviceable` a boolean, `type` a string and, on .NET 10, `sha512` a string. Members the host reads with a check
  (`localPath`, the versions, `path`, `hashPath`) may be anything.
* *A version component above `Int32.MaxValue`* wherever the host parses a version (every `targets` file; in
  framework-dependent mode, every known `runtimeTargets` file). The host's `try_stou` calls `std::stoul`, which
  throws above `ULONG_MAX`, and `version_t` stores anything above `INT_MAX` wrapped into an `int`. The refusal
  fires as soon as the host would read such a component, even if a later component makes the parse fail.
* *What rapidjson and System.Text.Json may read differently*, anywhere in the document, read or not: invalid
  UTF-8 (the host does not validate it); anything System.Text.Json's defaults refuse (malformed JSON, comments,
  trailing commas, content after the root value, nesting past 64; rapidjson with the host's flags accepts
  comments and trailing content), reported with the reader's line and byte position, not its message, whose text
  depends on the process's resource settings; a duplicate member name (rapidjson's lookup finds the first, its
  iteration both); an unpaired surrogate escape; U+0000 in a string or name (the host's C strings end there); a
  number with an exponent, or longer than 300 characters (rapidjson fails the document with
  `kParseErrorNumberTooBig` on `0e309`, whose value is zero).

The parser does not classify its refusals into "the host would fail" and "PawPrint cannot reproduce". Stage 3's
refusal type needs that split; by case: `NotStrictJson` is mixed (malformed JSON fails the host; comments, trailing
content and deep nesting do not), `UnpairedSurrogate` and `UnreproducibleNumber` fail the host, `InvalidUtf8`,
`DuplicateMember` and `NulCharacter` do not, and `MissingMember`, `WrongKind` and `VersionComponentOutOfRange` are
undefined or wrapped.

### The two pins

`deps_format.cpp`, `deps_entry.*`, `version.cpp`, `json_parser.*` and `hostmisc/utils.cpp` differ between the pins
in ways the parser does not see, except two:

* .NET 11 no longer reads `sha512` (above).
* .NET 11 lowers a library's `type` with `minipal_tolower_invariant` per byte, where .NET 10 uses `::tolower`, so
  the two lower non-ASCII bytes differently. Only the comparison with `runtimepack` reads the result, and no
  non-ASCII byte lowers to ASCII under either, so the classification is the same. `DepsLibraryType.Other` keeps
  the type as written.

`get_directory` was rewritten in C for .NET 11 (`utils_get_directory`); it gives the same answers.

### For stage 3

Facts found while reading the host, which stage 3 needs and stage 2 does not act on:

* *A framework entry's probe asks `has_package`*, of the entry's own framework first and then of each lower one
  (`setup_probe_config` orders the framework probes highest level first, and `probe_deps_entry` skips those above
  the entry's level). Use `DepsManifest.hasPackage` on each framework's manifest.
* *An application's RID-specific asset without `localPath` is probed with `to_package_path`*, at its listed path
  (`<app>/runtimes/<rid>/lib/<tfm>/X.dll`), not at `dirRelativePath`.
* *Native assets.* Codex's stage-1 round 2 finding (an application `deps.json` can name a native `libcoreclr`,
  resolved before the framework's) is answerable from the parser's native entries.
* *`localPath` can name another file or leave the directory* (`../common/X.dll`); the parser reports it as
  written. The plan's TPA refusals (a name that is not the stem of the file it resolves to) apply to the probed
  path.
* *An empty file name.* A listed path ending `/` gives an empty `dirRelativePath`, and the host then probes the
  directory itself. Stage 3 should refuse it.

### Tests

* `TestDepsVersion`: `DepsVersion.parse` against a line-by-line transcription of `parse_internal` and `try_stou`
  (with `BigInteger`, so that it shares no overflow with the code under test), 10 000 fixed and 10 000 fresh
  cases, coverage asserted for each outcome (no dot; fails after a dot; two, three and four components; out of
  range); a unit case per branch; a case under a culture whose signs are digits; `DepsVersion.compare` against a
  transcription of `version_t::compare`, with structural equality checked against it.
* `TestDepsJson`, the model: a generated description of a manifest (either runtime; packages with asset-type
  members in any order, empty or absent, a wrong-case member the host ignores, `runtimeTargets` files with RIDs
  inside and outside the list and differing by case, `assetType` spellings that differ by ASCII case, by dotless
  or dotted i, or by long s; libraries in their own order, some without a package and some packages without a
  library, keys with zero, one and two slashes, `type` spellings including `runtimepack` lookalikes under
  `OrdinalIgnoreCase` and `ToLowerInvariant`; versions parsed, with leading zeros, malformed, empty, absent and not
  strings; `localPath` set, empty, absent and not a string; backslash and `./` directories; a decoy target; a byte
  order mark; indented or not; every non-alphanumeric character `\u`-escaped or not), with at most one fault
  injected (a bad library value, a `runtimeTargets` file without `assetType` or `rid`, an out-of-range version, in
  the read target or the decoy). The model computes, from the description alone, the entries with
  `isPlaceholder` and `dirRelativePath`, and the packages, or the one refusal. 3000 fixed and 3000 fresh cases;
  each of 59 labels must be reached at least 30 times in the fixed sample, including each refusal, a fault in a
  place the host does not read in that mode or on that runtime, a RID chosen that is neither first in the document
  nor first in the list, a package read only through `runtimeTargets`, and a library read whose package is not a
  package.
* A unit case per refusal, and per rule a unit case can name. Each parses under both runtimes and requires the
  same answer, except the `sha512` case.
* The real files: for each runtime (Net10, Net11), the shared `Microsoft.NETCore.App` and
  `Microsoft.AspNetCore.App` `deps.json` parse in both modes to the same manifest; every runtime entry is
  non-RID-specific, not a placeholder, has both versions, and its `dirRelativePath` stem is its name; the runtime
  entries' paths are exactly the directory's `.dll` files; the packages are the entries' libraries; and the
  runtime entry counts are the plan's measurements (172 and 141 on 10.0.14, 181 and 132 on 11.0.0-rc.3). The
  pack copies are not tested: stage 1, which puts a `deps.json` in each pack, is not on `main`.

Red runs: without the parser the test project does not compile. Against a stub with the final types, answering
`Ok []`, `Ok DepsVersion.Empty`, `0`, `false` and the listed path, 37 of the 51 tests failed, each on a wrong
answer, and 14 passed: the version cases whose answer is "parse fails", which the stub's `Empty` gives. Each Codex
fix's tests were red first, as each fix's test commit records.

### Mutants

Each was applied to `DepsJson.fs` alone, run against `TestDepsJson` and `TestDepsVersion`, and reverted. 67 in
all: 64 killed, and 3 equivalent. "Model" is the generated-manifest property; "transcription" is the
`version_t::parse` property.

| Rule | Mutant | Killed by |
|---|---|---|
| version: a `.` is needed | a string with no `.` read as one component | model; transcription; branch cases |
| version: a fifth component fails | truncated to four | model; transcription; branch cases |
| version: ASCII digits only | `Char.IsDigit` | model; transcription; branch case `1.٣` |
| version: an empty component fails | read as 0 | model; transcription; branch cases |
| version: `Int32.MaxValue` is in range | `>=` for `>` | out-of-range and culture unit cases; transcription; branch case |
| version: the digit-length guard | eleven digits read as a number | **equivalent**: the guard only keeps the accumulation inside `int64`, and every eleven-digit number is above `Int32.MaxValue` anyway |
| version: the digit-length guard | removed, so long digit strings overflow | transcription |
| version: out of range is refused | read as `Empty` | model; transcription; branch cases; `runtimeTargets` unit case |
| `version_t::compare` | revision ignored | compare transcription; `Empty` unit case |
| `version_t::compare` | `Empty` stored as zeros | compare transcription; `Empty` unit case |
| entry order | libraries reversed | model; order unit case |
| entry order | asset types reversed within a library | model; order unit case; RID unit case |
| asset-type members matched exactly | matched ignoring case | model |
| RID selection: first listed RID | first in the document among listed RIDs | model; RID unit case |
| RID selection: exact | RIDs compared ignoring case | model |
| RID-specific assets replace | added to the others | model; RID unit case |
| self-contained reads `targets` only | reads `runtimeTargets` | model; RID, libraries and `runtimeTargets` unit cases |
| framework-dependent: a package only in `runtimeTargets` is read | ignored | model; libraries unit case |
| `assetType` ignoring ASCII case | `ToUpperInvariant` | model (`reſources`) |
| `assetType` ignoring ASCII case | `OrdinalIgnoreCase` | **equivalent**: .NET's `OrdinalIgnoreCase` folds no non-ASCII character to ASCII (measured for `ı`, `İ`, `ſ` and the Kelvin sign), so against an ASCII name it is ASCII case-insensitive |
| `type` is `runtimepack` ignoring ASCII case | `ToLowerInvariant` | model (Kelvin sign); library-type unit case |
| `type` is `runtimepack` ignoring ASCII case | `OrdinalIgnoreCase` | **equivalent**, as above |
| placeholder | never | model; placeholder unit case |
| placeholder | `_._` without the `/` | model; placeholder unit case |
| `localPath` precedence | `runtimepack` path ahead of `localPath` | model; `dirRelativePath` unit case |
| `localPath` precedence | `localPath` ignored | model; `dirRelativePath` unit case |
| empty `localPath` is unset | set to `""` | model; most unit cases; real files |
| non-string `localPath` is unset | read as its JSON text | model; paths unit case |
| non-string version is empty | a number read as a version | model |
| `\` read as `/` | kept in the listed path | model; paths and placeholder unit cases |
| `\` read as `/` | kept in `localPath` | model; paths unit case |
| `.ni` stripped | not stripped | model; paths unit case |
| `.ni` in any ASCII case | lower case only | model |
| name after the last `/` or `\` | after the last `/` only | model; paths unit case |
| key split at the first `/` | at the last | model |
| key with no `/` | version empty instead of the whole key | model; key unit case |
| resources probed under their culture | probed like runtime assets | model; `dirRelativePath` unit case |
| `runtimeTarget` as a string | refused | model; `runtimeTarget` and `targets` unit cases |
| refusal: invalid UTF-8 | accepted | UTF-8 unit case |
| refusal: comments | accepted | strict-JSON unit case |
| refusal: trailing commas | accepted | strict-JSON unit case |
| one byte order mark skipped | two skipped | byte-order-mark unit case |
| one byte order mark skipped | none skipped | byte-order-mark unit case; model |
| refusal: unpaired surrogate | read as empty | surrogate unit case |
| refusal: duplicate member | accepted | duplicate unit case |
| refusal: NUL | accepted | NUL unit case |
| refusal: rapidjson's numbers | every number accepted | number unit case |
| refusal: rapidjson's numbers | only `e` refused, not `E` | number unit case |
| refusal: root not an object | unchecked | root unit case |
| refusal: `runtimeTarget` without `name` | read as empty | `runtimeTarget` unit case |
| refusal: package value not an object | unchecked | `targets` unit case |
| refusal: file value not an object | unchecked | `targets` unit case |
| refusal: target missing | read as empty | `targets` unit case |
| refusal: `runtimeTargets` not an object | unchecked | `runtimeTargets` unit case |
| refusal: `assetType` missing | file skipped | model; `runtimeTargets` unit case |
| refusal: `rid` missing | read as `""` | model; `runtimeTargets` unit case |
| refusal: `libraries` missing | read as empty | libraries unit case |
| refusal: `sha512` | unchecked | model; libraries unit case |
| refusal: `serviceable` | unchecked | model; libraries unit case |
| refusal: `type` not a string | read as `""` | model; libraries unit case |
| only read libraries are checked | every library checked | model; libraries unit case |
| `has_package`: RID assets must survive selection | RID-only packages counted when no RID matched | model; packages unit case |
| `has_package`: RID assets count | ignored | model; packages unit case |
| `hasPackage` looks up `name/version` | version ignored | packages unit case |
| only .NET 10 reads `sha512` | .NET 11 reads it too | model; `sha512` unit case |
| only .NET 10 reads `sha512` | .NET 10 does not | model; `sha512` unit case |
| `NotStrictJson` carries the position | no position | position unit case |

The `ToUpperInvariant` mutant was added once the first battery showed the `OrdinalIgnoreCase` one equivalent. The
`reſources` spelling, added to the generator in the third commit, kills it.

### Suite

The change touches no interpreter behaviour, so the default suite is the one run: at the branch tip
(`f8175ca6f`), on the macOS arm64 host with .NET 10 under test, 6046 tests, 6007 passed, 39 skipped, `Test Run Successful`. (Also green before the Codex rounds:
6042 tests, 6003 passed, 39 skipped.) `fantomas` is clean, `scripts/footprint.py generate` changes nothing, and
`scripts/check-docstring-attachment-branch.sh` passes.

### Codex review

Four rounds; the fourth found nothing.

* **Round 1.** Two findings.
  * *`DepsVersion.parse` read the current culture* (`Int64.Parse` for the range check; under a culture whose signs
    are digits, `"2.1"` threw). Fixed, tests first: the digits are accumulated by hand.
  * *.NET 11 does not read `sha512`*, so the parser refused a manifest .NET 11 launches. Not acted on in this
    round (a refusal, not a wrong answer); fixed in round 2, when it came back.
* **Round 2.** Three findings, all fixed, tests first.
  * *`sha512` again.* `DepsJson.parse` takes the runtime, and requires `sha512` only on .NET 10.
  * *Package membership.* Entries from libraries keyed `P` and `P/P` were indistinguishable, though
    `has_package` finds the second and not the first (Codex measured a .NET 11 launch failing to find CoreLib with
    `P`). The parser returns `DepsManifest`, with the packages for which `has_package` holds.
  * *`NotStrictJson` carried System.Text.Json's message*, whose text changes with
    `System.Resources.UseSystemResourceKeys`. It carries the line and byte position instead.
* **Round 3.** One finding, fixed: `DepsEntry.dirRelativePath`'s docstring claimed it is where the host probes
  every entry, but an application's RID-specific entry with no `localPath` is probed at its listed path.
* **Round 4.** No findings. Codex also counted the runtime assets of the Linux packs' `deps.json` files (172 and
  141 on 10.0.14, 181 and 132 on 11.0.0-rc.3, for linux-x64 and linux-arm64), which match the macOS counts the
  real-file test asserts.

## As built (stage 3)

Branch `host-binding-3-tpa`, built on `host-binding-2-deps-json` and rebased onto `main` after #2358 merged. The tests came first, in a commit of their own,
against a stub with the final types; so did the tests of each Codex finding fixed.

### What it adds

Two files in `WoofWare.PawPrint.Loader`, both `internal` until the owner confirms decision 2. Loader's public surface
is unchanged: a reflection dump of `WoofWare.PawPrint.Loader.dll`'s exported types and members, built with and without
the two files, is identical (1702 lines each).

* **`TrustedPlatformAssemblies`** (`TrustedPlatformAssemblies.fs`): a map from simple name to path, with structural
  equality, built only by `tryCreate : Map<string, string> -> Result<TrustedPlatformAssemblies, TpaRefusal>`. It holds
  the map to what CoreCLR's reading of the TPA string accepts (`GetNextTPAPath`, `SetupBindingPaths`) and to what
  PawPrint can bind from. `TpaRefusal` has a case for each: a relative path; a path holding NUL; one holding `:`, the
  path-list separator; one ending with a space, which CoreCLR trims; a file name outside ASCII; a path not ending
  `.dll`, `.exe`, `.ni.dll` or `.ni.exe`; a `.exe` or `.ni.*` file; a name which is not the file's stem; two names equal
  ignoring case. Entries are checked in ordinal order of name, and the first refusal is returned. `toMap` reads the map
  back. `tryFindIgnoringCase` looks a name up as CoreCLR's map does: upper-cased, with dotless `ı` and the long s read
  as `I` and `S`.
* **`HostPolicy.resolveTpa : HostPolicyInput -> Result<HostPolicyResolution, HostPolicyRefusal>`** (`HostPolicy.fs`).
  The input is:
  * `Runtime`: whose hostpolicy. The pins differ in what `get_dir_assemblies` takes (.NET 10: `.ni.dll`, `.dll`,
    `.ni.exe`, `.exe`; .NET 11: `.dll`, `.exe`), and in what stage 2's parser reads.
  * `Platform`: Linux or macOS, which names the CoreCLR library (`libcoreclr.so`, `libcoreclr.dylib`).
  * `Launch`: `FrameworkDependent (application, rids, higher, lowest)` or `SelfContained application`. A self-contained
    launch with frameworks, or a framework-dependent one without, is unrepresentable (decision 7); so is a
    self-contained application held in memory.
  * The application (decision 6): `OnDisk` (its directory, absolute and spelled as the host spells it, with links
    resolved; either its `deps.json` bytes, or its listing together with whether `libcoreclr` exists there, which the
    host asks with `file_exists_in_dir` apart from the listing; and the entry assembly's file name) or `InMemory`.
  * `EntryAssemblyName` and `EntryAssemblyCulture`, from the entry image's metadata (Codex, stage 1 round 1).
  * `Settings` (decision 10): `UseRidGraph`, `SetAppPaths`, additional probing paths, additional `deps.json` files,
    shared stores, a servicing directory, each as the caller found it.

  The result is the TPA and the CoreCLR library the host found, before links. The function reads no file and no
  environment. It follows hostpolicy with no file existence checks: the frameworks in host order, the lowest read
  without `runtimeTargets`, the application read without them when self-contained; `resolve_tpa_list` as a fold over
  the entries the host meets (the application's, or its directory's files, then each framework's); then the native
  and resource halves of `resolve_probe_dirs`, which find the CoreCLR library and the probe directories.

### Refusals

`HostPolicyRefusal` is `HostWouldFail of HostPolicyFailure` or `NotReproducible of HostPolicyUnreproducible`, checked in
this order:

| Phase | Refusal | Class | Source |
|---|---|---|---|
| settings | `UseRidGraph`; `SetAppPaths`; any additional probing path, additional `deps.json`, shared store or servicing directory | not reproducible | plan; decision 10; Codex round 1 (`SetAppPaths`) |
| manifests, in `deps_resolver_t::valid`'s order (application, then frameworks highest first) | a framework without `deps.json` | host fails | `deps_resolver.h` |
| | a `deps.json` with half a surrogate pair | host fails | rapidjson's `ParseStringToStream` refuses it |
| | any other `DepsJsonError` | not reproducible | stage 2's split: malformed JSON fails the host, but comments, trailing content and deep nesting do not, and a number with an exponent fails only when rapidjson overflows; the rest is undefined behaviour or a disagreement between readers |
| listing | two files of one extension with one stem (`X.dll`, `X.DLL`) | not reproducible | new: the host keeps the first its directory listing gives |
| frameworks | two frameworks list one runtime name, or both name the CoreCLR library | not reproducible | Q4, decision 4; widened to the CoreCLR library, whose order decides the CoreLib rule |
| walk | a name found again with another file name | host fails | `resolve_tpa_list` |
| walk, natives | a framework entry in no framework, from its own down, whose `deps.json` has its package (except a missing `apphost`) | host fails | `probe_deps_entry`, `report_missing_assembly_in_manifest` |
| natives | no CoreCLR library | host fails | `hostpolicy_context.cpp` |
| natives | a framework-dependent application supplies the CoreCLR library | not reproducible | Codex, stage 1 round 2 and round 1 here: the host loads it, and CoreLib, from wherever its links lead |
| TPA | a relative path; a path with no assembly extension (an empty file name included) | host fails | CoreCLR returns `E_INVALIDARG`; measured below |
| TPA | NUL; `:`; a trailing space; a file name outside ASCII; `.exe` or `.ni.*`; a name not the stem; names equal ignoring case | not reproducible | plan; Codex, stage 1 round 1 (`:`) and rounds 1 here |
| probe directories | a native or resource probe directory holding `:` | not reproducible | Codex round 1: a resource root with `:` made CoreCLR fail (`0x80070057`) |
| CoreLib | none in the TPA; not beside the CoreCLR library, compared without `.` components | not reproducible | decision 1 |
| entry | an entry assembly with a culture | not reproducible | Codex round 4: CoreCLR binds it through `<app>/<culture>/` |
| entry | the TPA maps the entry assembly's name to another file, compared without `.` components (any file, in memory) | not reproducible | decision 1; fact 20 measured below |

### Decisions taken here

Each with the alternative weighed.

* **Where the CoreCLR library is.** The CoreLib rule needs the directory CoreCLR loads CoreLib from. Options: take
  decision 1's directory (the `Microsoft.NETCore.App` framework's, or the application's when self-contained) as given;
  or find the library as `resolve_probe_dirs` does (the first native entry, application then frameworks, resolving to
  a file of that name; with no application `deps.json`, the one the existence probe finds in the application's
  directory). Chose the second: it is the host's own rule, it covers a self-contained application with no special
  case, and it shows when an application supplies the library. It needs the platform as an input, for the file name.
* **Links.** The host loads the CoreCLR library of its fixed name from the directory of the found library's real path,
  and CoreCLR loads CoreLib from beside the real path of that (Codex, rounds 2 to 4). The function cannot see links.
  Options: refuse every launch it cannot check, which is every self-contained one; take a resolved path as input,
  which the caller cannot know before the function has chosen the library; or return the library it chose, and state
  the check which the caller, holding the filesystem, must make. Chose the third: `HostPolicyResolution.CoreClr`'s
  docstring states the check (the library of the platform's name exists in the directory of the found file's real
  path; the CoreLib path the host appends there holds no `:`; the CoreLib beside that library's real path is the TPA's
  file), and the real-host test makes it. The function still checks by spelling, and refuses a framework-dependent
  application's own library outright.
* **Paths are kept as the host builds them.** `./` and `../` stay in the path (`append_path`), and a `localPath` may
  leave its directory; measured below. Only the CoreLib and entry comparisons drop `.` components (never `..`, which
  climbs from a link's target). The metamorphic property "every path lies in its manifest's directory" is therefore
  textual.
* **Case.** CoreCLR's map hashes and compares names upper-cased with `minipal`'s table, which takes only dotless `ı`
  and the long s from outside ASCII to an ASCII letter (read: `src/native/minipal/unicodedata.c`). .NET's
  `ToUpperInvariant` takes only the long s (checked over every UTF-16 code unit), and not even that under invariant
  globalization (Codex round 3). So TPA file names must be ASCII, and names fold explicitly, independent of the
  process's globalization mode.
* **Not in the input:** `DOTNET_RUNTIME_ID`, which decision 10 leaves to the App and the harness; the RID list is an
  input, so a caller honouring it can put it first.

### Measured

On the macOS arm64 host, real .NET 10.0.14 and 11.0.0-rc.3.26522.148, from scratch probes:

* **Fact 20.** A directory with no `deps.json` holding an entry assembly named `System.Web.HttpUtility` (version
  1.0.0.0): the host launched, the TPA mapped the name to the framework's file (the trace: "Replacing deps entry"),
  and CoreCLR ran the framework's assembly as the entry: `MissingMethodException: Entry point not found in assembly
  'System.Web.HttpUtility, Version=10.0.0.0, ..., PublicKeyToken=cc7b13ffcd2ddd51'`, exit 134 (11.0.0.0 on .NET 11).
  No MVID check fired. So the refusal is "not reproducible", and fact 20 as written (`FileLoadException` on an MVID
  mismatch) does not hold for the entry assembly.
* **Codex's stage 1 claims**, each on both runtimes:
  * One name twice in the application, at one path, stated 1.0.0.0 then 99.0.0.0: the framework's file won (the
    second entry did not replace the first, which kept 1.0.0.0). At two paths (a `localPath` on the second): the
    application's 99.0.0.0 won.
  * A `runtimepack` library's asset `sub/X.dll` with no `localPath`: `<app>/sub/X.dll`.
  * `localPath` `./X.dll` and `../common/Y.dll`: `<app>/./X.dll` and `<app>/../common/Y.dll`, as written.
  * An asset listed as `lib/` (an empty file name): "Failed to create CoreCLR, HRESULT: 0x80070057", exit 137.
  * A `localPath` ending `.txt` for a name the framework lists: the extension check failed the launch, exit 140.
* **Self-contained through `dotnet exec`.** A directory holding the framework's files, the guest, a self-contained
  `deps.json` and a runtimeconfig naming `includedFrameworks` runs under the muxer as "a self-contained app"; so the
  real-host self-contained case needs no apphost.

### Tests

* `TestTrustedPlatformAssemblies`: a unit case per `TpaRefusal`, acceptance cases, and `tryFindIgnoringCase` (any ASCII
  case; dotless `ı` finds `I`; the long s finds `S`; the Kelvin sign does not find `K`).
* `TestHostPolicy`:
  * Against a transcription, line by line, of `resolve_tpa_list`, `get_dir_assemblies`, `probe_deps_entry` and both
    halves of `resolve_probe_dirs`, with PawPrint's refusals written out beside it: 10 000 fixed and 10 000 fresh
    cases, requiring equal outcomes (the TPA and the CoreCLR path, or the same refusal). The generator draws the
    runtime and platform, the application's case, one to three frameworks, libraries keyed with and without `/`,
    runtime, native, resource and RID-specific assets over a pool of names that collide (by case too, and with
    dotless `ı`), versions below, equal to and above one another and unparsable, placeholders, `localPath`s (into a
    subdirectory, `./`, `../`, absolute, another file, empty, ending with a space, holding `:`), libcoreclr placed
    away from CoreLib, listings with case variants and with the existence probe disagreeing, malformed and missing
    framework manifests, entry cultures, and each setting. Every outcome, each refusal included, is required at least
    10 times in the fixed sample (the rarest, `CoreLibAbsent`, about 20), `Ok` over 1000 times, and nine events of the
    walk 50 times each.
  * Six metamorphic properties at the same count: permuting the frameworks above the lowest (frameworks with names of
    their own, so that the overlap refusal does not dominate) leaves the TPA unchanged, and keeps a refusal a refusal;
    an application entry stated at 999.0.0.0 wins its name; with no `deps.json`, each framework's names resolve into
    its directory; every path lies in a manifest's directory; the names are distinct ignoring case; permuting the
    directory listing leaves the TPA unchanged.
  * The plan's measured table, one example per row and runtime (16), and unit cases: a minimal launch; one name twice
    at one path; a RID-specific application entry at its listed path; a self-contained launch; `./Entry.dll`; a
    CoreLib at `./`; the existence probe; a missing `apphost`; and one per refusal.
* `TestHostPolicyRealHost`, in the default suite:
  * 25 generated application directories per run, fresh seed (printed), each run on the framework under test through
    `dotnet exec` with a guest that writes `TRUSTED_PLATFORM_ASSEMBLIES` and the location of the CoreLib it runs on.
    Each directory has a generated `deps.json` (versions varied around the framework's, `localPath`s, RID-specific
    assets for RIDs in and out of the host's list, names the framework has and has not, one entry in about seven
    cases that makes the launch fail) or none, and `Microsoft.AspNetCore.App` is added in one case in four. Files are
    junk or missing. The function's input comes from the child: its RID list and its frameworks' directories, as its
    host trace names them. The comparison is with CoreCLR's map of the string (first path per name ignoring case), so
    .NET 10's string and .NET 11's map rendering compare alike. A predicted host failure must end as the host ends
    that failure: no report, the low byte of the host's `StatusCode` for it, and its message on stderr; a guest crash,
    a killed process or another failure fails the test (Codex, round 6). A TPA must match, its CoreLib must be the one the guest ran on, the caller's link check must hold, and the trace must
    show no probe and no existence check the settings did not report. The child's environment loses the variables
    that turn on probing, and `DOTNET_STARTUP_HOOKS`; both trace spellings are set. Measured: 20 to 24 of 25 cases
    compare a TPA, the rest predict a host failure; about 2 to 3 s a run (25 launches), on both runtimes.
  * One self-contained case: the framework directory's files copied beside the guest, with a self-contained
    `deps.json`, launched through `dotnet exec` (about 1 s).
  * Unit cases for the oracle's confirmation of a host failure: each predicted failure is confirmed by its status
    and message as measured; a guest crash, a guest killed by SIGKILL (exit 137, CoreCLR's TPA-refusal status, with
    no message), a missing-assembly warning followed by a crash, and another host failure are not.
* `TestFrameworkLocatorRatchet` allows the real-host guest's one `.Location`.

The default suite at the branch tip, on the macOS arm64 host with .NET 10 under test: 6147 tests, 6108 passed, 39 skipped, Test Run Successful (at e2d6cce5e). The change touches
no interpreter behaviour, so the Guest categories were not run. `fantomas` is clean, `scripts/footprint.py generate`
changes nothing, and `scripts/check-docstring-attachment-branch.sh` passes.

Wall time of the new fixtures, run alone on this host: each property test 7 to 12 s (NUnit runs them in parallel), the
real-host test 2 to 3 s, the self-contained case about 1 s.

Red runs: against the stub (`tryCreate` accepting everything, `resolveTpa` refusing everything as using the RID
graph), 75 of 78 tests failed, each on the stub's answer; the three passes were the two maps `tryCreate` must accept
and the RID-graph refusal. Each Codex round's tests were red first, as each test commit records.

### Mutants

Each was applied to `HostPolicy.fs` or `TrustedPlatformAssemblies.fs` alone, built, run against `TestHostPolicy`,
`TestHostPolicyRealHost` and `TestTrustedPlatformAssemblies`, and reverted; the two the plan assigns to the real-host
test (`localPath` ignored, and its RID-specific counterpart) ran against that test alone. The table is the final
battery, at the branch tip: 50 mutants, 49 killed, 1 equivalent. An earlier battery, at the first implementation,
found the apphost exception unexercised; its unit case came from that. The real-host test draws a fresh seed, so the
mutants it kills it kills by the cases it happens to generate; each of those was killed in both batteries.

| Source | Mutant | Killed by |
|---|---|---|
| plan | file version >= becomes > | table tie,Net11,framework; metamorphic: no deps.json; transcription; real host |
| plan | file version ignored on equal assembly versions | table file version + 1,Net11,app; transcription; real host |
| plan | assembly version ignored | metamorphic: application wins; table deps says 12,Net11,app; table deps says 12, file deleted,Net11,app; transcription; real host |
| plan | application versions read from the file (9.0.0.0) instead of the deps.json | unit: A second application entry resolving to the same path keeps the first one's versions; table deps says 12,Net11,app; table deps says 12, file deleted,Net11,app; table file version + 1,Net11,app; transcription; real host |
| plan | frameworks processed before the application | unit: A second application entry resolving to the same path keeps the first one's versions; unit: Refused: a name found again with another file name; table tie,Net11,framework; transcription; real host |
| plan | no deps.json: listed files compare as equal to the framework's entry | **equivalent** (see below) |
| plan | no deps.json: listed files given a version above the framework's | unit: Refused: an entry assembly on disk which the TPA maps to the framework's file; table no deps.json,Net11,framework; metamorphic: no deps.json; transcription; real host |
| plan | placeholder not skipped | transcription |
| plan | extension check removed | unit: Refused: a name found again with another file name; transcription |
| plan | localPath ignored | real host |
| plan | RID list order reversed | unit: An application's RID-specific entry without a localPath resolves to its listed path; metamorphic: no deps.json; transcription; real host |
| plan | case-insensitive collision refusal removed | unit: Names equal ignoring case are refused; unit: Refused: two names equal ignoring case; metamorphic: distinct names; transcription |
| plan | framework-overlap refusal removed | unit: Refused: two frameworks listing one name; unit: Refused: two frameworks naming the CoreCLR library; metamorphic: no deps.json; transcription |
| decisions | CoreLib rule removed | unit: Refused: a CoreLib away from the CoreCLR library; unit: Refused: no CoreLib; transcription |
| decisions | entry-assembly rule removed | unit: Refused: an entry assembly on disk which the TPA maps to the framework's file; unit: Refused: an in-memory entry assembly named as a framework assembly; transcription |
| decisions | a non-empty existence-check list accepted | unit: Refused: settings which turn on existence checks; transcription |
| decisions | the self-contained parse reads runtimeTargets | unit: A self-contained launch reads only the application; transcription |
| decisions | the lowest framework read with runtimeTargets | metamorphic: no deps.json; transcription |
| rule added | listing collision refusal removed | metamorphic: listing permuted; unit: Refused: two listed files of one stem and extension; transcription |
| rule added | a native entry that resolves nowhere does not fail | unit: Refused: a native framework asset whose package no framework has; transcription |
| rule added | the last CoreCLR library wins, not the first | unit: Refused: a framework-dependent application whose deps.json names the CoreCLR library; unit: Refused: a framework-dependent application whose directory holds the CoreCLR library; unit: The CoreCLR library of a directory without deps.json is the one the existence probe finds; transcription |
| rule added | the CoreCLR library of a directory without deps.json is not looked for | metamorphic: paths in manifests; unit: Refused: a framework-dependent application whose directory holds the CoreCLR library; unit: The CoreCLR library of a directory without deps.json is the one the existence probe finds; metamorphic: distinct names; transcription |
| rule added | the apphost exception removed | unit: A missing apphost asset does not fail the launch |
| rule added | an unpaired surrogate classed as not reproducible | unit: Refused: a deps.json the host fails to parse; transcription |
| rule added | a relative TPA path classed as not reproducible | unit: Refused: a relative path in the TPA; transcription |
| rule added | a framework entry resolves only in its own framework | transcription |
| rule added | an application's RID-specific entry without localPath probed at its file name | real host |
| rule added | a later entry at the same path replaces the earlier one's versions | unit: A second application entry resolving to the same path keeps the first one's versions; transcription |
| rule added | the application directory's trailing slashes kept | metamorphic: application wins; transcription |
| rule added | .NET 11 takes .ni files from a directory listing | transcription |
| rule added | tryFindIgnoringCase compares ordinally | unit: Refused: an in-memory entry assembly named as a framework assembly; transcription; unit: tryFindIgnoringCase compares upper-cased names; unit: tryFindIgnoringCase finds a name in any case; unit: tryFindIgnoringCase matches dotless i to I, as CoreCLR does on Unix |
| TPA | TPA: relative path accepted | unit: A relative path is refused; unit: Refused: a relative path in the TPA; unit: The first refusal in ordinal order of name is returned; transcription |
| TPA | TPA: path-list separator accepted | unit: A path holding the path-list separator is refused; unit: Refused: a TPA path holding the path-list separator; transcription |
| TPA | TPA: trailing space accepted | unit: A path ending with a space is refused; unit: Refused: a TPA path ending with a space; transcription |
| TPA | TPA: no assembly extension accepted | unit: A path with no assembly extension is refused("/app/"); unit: A path with no assembly extension is refused("/app/A"); unit: A path with no assembly extension is refused("/app/A.dll/"); unit: A path with no assembly extension is refused("/app/A.txt"); unit: Refused: a TPA path with no assembly extension; transcription |
| TPA | TPA: .exe and .ni.dll accepted | unit: A path to a file other than a dll is refused("A","/app/A.EXE"); unit: A path to a file other than a dll is refused("A","/app/A.NI.Dll"); unit: A path to a file other than a dll is refused("A","/app/A.exe"); unit: A path to a file other than a dll is refused("A","/app/A.ni.dll"); unit: A path to a file other than a dll is refused("A","/app/A.ni.exe"); unit: A path to a file other than a dll is refused("A.ni","/app/A.ni.dll"); unit: A per-entry refusal comes before a collision; unit: Refused: an exe in the TPA; transcription |
| TPA | TPA: a name other than the stem accepted | unit: A name which is not the file stem is refused("A","/app/A.dll.dll"); unit: A name which is not the file stem is refused("A","/app/B.dll"); unit: A name which is not the file stem is refused("A.dll","/app/A.dll"); unit: A name which is not the file stem is refused("a","/app/A.dll"); unit: Refused: a localPath naming another file; transcription |
| TPA | TPA: dotless i not read as I | unit: tryFindIgnoringCase matches dotless i to I, as CoreCLR does on Unix |
| TPA | TPA: long s not read as S | unit: tryFindIgnoringCase compares upper-cased names |
| Codex rounds | TPA: NUL accepted | unit: A path holding NUL is refused |
| Codex rounds | TPA: a file name outside ASCII accepted | unit: A file name outside ASCII is refused(305,"I"); unit: A file name outside ASCII is refused(383,"s"); unit: A file name outside ASCII is refused(8490,"K"); unit: Refused: a file name outside ASCII; transcription |
| Codex rounds | SetAppPaths accepted | unit: Refused: the AppPaths switch; transcription |
| Codex rounds | an application's CoreCLR library accepted | unit: Refused: a framework-dependent application whose deps.json names the CoreCLR library; unit: Refused: a framework-dependent application whose directory holds the CoreCLR library; unit: The CoreCLR library of a directory without deps.json is the one the existence probe finds; transcription |
| Codex rounds | a probe directory holding ':' accepted | unit: Refused: a resource probe directory holding the path-list separator; transcription |
| Codex rounds | resource roots not checked | unit: Refused: a resource probe directory holding the path-list separator; transcription |
| Codex rounds | the CoreCLR library of a directory without deps.json taken from the listing | unit: The CoreCLR library of a directory without deps.json is the one the existence probe finds; transcription |
| Codex rounds | the entry assembly's path compared by spelling | unit: An entry assembly listed at ./ is the entry assembly; transcription |
| Codex rounds | CoreLib's path compared by spelling | unit: A CoreLib listed at ./ is the one beside the CoreCLR library |
| Codex rounds | an entry assembly with a culture accepted | unit: Refused: an entry assembly with a culture; transcription |
| Codex rounds | the CoreCLR path not returned | unit: A minimal framework-dependent launch gives the application and the framework; unit: A self-contained launch reads only the application; unit: The CoreCLR library of a directory without deps.json is the one the existence probe finds; transcription; real host, self-contained; real host |

* *Equivalent:* a listed file's versions are empty, and a framework entry's are empty or parsed, so the later
  framework entry replaces the listed file whether that compares below it or equal. Codex (stage 1 round 1) named the
  witnesses this needs, a tie and a strictly higher local version; the second is the "version above the framework's"
  mutant, killed.
* *Frameworks before the application:* the metamorphic property "an application entry stated above every framework's
  version wins" does not kill it, since that entry replaces the framework's in either order; the transcription, the
  tie row and a unit case do.

Mutants of the real-host oracle's confirmation of a host failure (Codex, round 6), each run against
`TestHostPolicyRealHost`:

| Mutant | Killed by |
|---|---|
| the old rule: any nonzero exit with no report confirms the failure | unit: a guest crash; a killed guest; a warning followed by a crash; another host failure |
| the message not checked | unit: a killed guest |
| the exit status not checked | unit: a warning followed by a crash |

Round 7 added a case to the "warning followed by a crash" test: the duplicate's message and status, predicted as a
missing asset, must not be confirmed. It was red before the fix.

### For stage 5, and for the owner's questions

* **Decision 1 (`GuestConfig` holds the TPA).** The TPA type is as the decision describes, with one addition: file
  names must be ASCII. The CoreLib rule cannot be checked by the function alone, because the host follows links (see
  "Links"). Stage 5's caller must make the check `HostPolicyResolution.CoreClr` states, or `GuestConfig` could hold a
  TPA whose CoreLib is not the one CoreCLR loads. If `GuestConfig` is to carry the TPA alone, the check belongs where
  the TPA is built (the App and `assembliesFor`), not at bind time. The entry-assembly rule needs the entry's metadata
  name and culture, and the refusal belongs where image and TPA meet as well (Codex, stage 1 round 3).
* **Decision 2 (Loader).** Nothing found argues for another home. The function needed only Domain's `EmulatedRuntime`
  and stage 2's parser.
* **Decision 3 (abbreviation).** Not touched here.
* **Decision 4 (the locator returns frameworks in `hostfxr`'s order).** The function takes the frameworks in order and
  refuses an overlap of runtime names or of the CoreCLR library, so on today's disjoint frameworks a wrong order gives
  a refusal, not a wrong TPA. The overlap refusal does not cover package probes: an entry whose own framework's
  `deps.json` lacks its package (a library keyed without `/`) lands in the first lower framework which has the
  package, and that depends on the order (Codex, round 6). A unit case pins the example, accepted in both orders with
  different answers, and the refusal's docstrings say what it does not cover. No shared framework has such a key
  today. The real-host
  test takes the order and directories from the child's host trace; the App cannot.
* **The App's gatherer** must: give the application directory with links resolved; answer the existence probe for
  libcoreclr with `stat`, not from the listing (a link to a device is omitted by the listing and found by the probe);
  pass the listing as `pal::readdir` gives it (Codex, stage 1 round 2: a directory named `X.dll` is admitted); merge
  the frameworks' runtimeconfig properties before reading `SetAppPaths` (Codex, stage 1 round 5); pass only the
  additional probing paths the host admits (Codex, stage 1 round 5); and treat an entry of any kind at a store or
  servicing path as present (`pal::directory_exists` is `file_exists`).
* **The real-host test runs on Linux CI too**, where the host's RID list (read from the trace) is checked for the first
  time; it was run here only on macOS arm64.

### Codex review

Seven rounds: five against `host-binding-2-deps-json`, every finding acted on, tests first; then rounds 6 and 7
against `main`, after the rebase.

* **Round 1** (nine findings): an application-local libcoreclr linking to the framework's loaded the framework's
  CoreLib; CoreCLR's fold reads dotless `ı` as `I` (in names, and in `.nı.dll`); `SetAppPaths`; a resource root holding
  `:`; a case-only libcoreclr in a listing; NUL in a TPA path; the real-host test's trace variables and inherited
  settings. Fixed: the refusals above, the ASCII file-name rule, the resource walk, and the test's environment.
* **Round 2** (five): a self-contained libcoreclr link to the installed runtime; `./Entry.dll` refused as shadowed;
  the lookup folded lower case, which CoreCLR does not; a store of another architecture reported; a quadratic
  collision scan. Fixed: `HostPolicyResolution.CoreClr` and its check, path comparisons without `.` components, the
  upper-case fold, the store path, a one-pass scan.
* **Round 3** (five): the fold depended on invariant globalization; the caller's check did not follow the host's
  fixed-name library; CoreLib at `./` refused; libcoreclr found by the existence probe, not the listing; inherited
  startup hooks. Fixed: an explicit fold, the two-step check, the input's existence probe (the case-only refusal went),
  the hooks removed.
* **Round 4** (three): an entry assembly with a culture binds through the satellite directories; the appended CoreLib
  path holding `:`, and a missing fixed-name library, were not in the caller's check. Fixed: the culture refusal, and
  the check's statement and the test's checks.
* **Round 5** (two): the real-host guest's `.Location` broke `TestFrameworkLocatorRatchet` (all 24 cases; reproduced);
  a regular file at the store path also sets up a store probe. Fixed: an allowance, and the gatherer's existence test.
* **Round 6** (five), against `main`.
  * *Fixed, tests first:* the real-host oracle took any nonzero exit with no report for the predicted host failure, so
    a guest crash before its report passed as the host refusing. It now requires the host's status and message for
    the failure predicted (above).
  * *Narrowed, not fixed:* the overlap refusal does not cover package probes (see decision 4 above). Its docstrings
    now say so, and a unit case pins the order-sensitive example.
  * *Declined:* a listing holding `X.dll`, `X.exe` and `X.EXE` is refused as `ListingNamesCollide`, though the host
    always takes `X.dll` and skips both executables. The check starts afresh for each extension, where it need only
    look at stems no earlier extension claimed. That is a refusal of a launch the host could make, on a contrived
    listing, not a wrong answer. It is a candidate issue, or for stage 5.
  * *Declined:* two edges of the real-host oracle, on layouts the generator does not produce.
    `FrameworkUnderTest.resolveLinks` applies `..` before it follows links, where `realpath` follows the link first,
    so a path with a link followed by `..` would be compared wrongly. And `Path.Exists` counts a dangling link at the
    store or servicing path, where hostpolicy's `access(F_OK)` follows it and does not, which would make the test
    inconclusive rather than wrong.
* **Round 7** (four), against `main`; the last round.
  * *Fixed, tests first:* the oracle confirmed a predicted missing asset by text which the duplicate-extension
    message, of the same exit status, also holds; it now matches the missing-asset message's own ending. And the
    generated runtimeconfigs left `UseRidGraph` and `SetAppPaths` unset, so a framework's runtimeconfig could set them
    (Codex measured both on .NET 10) while `gatherSettings` reported them off; the application's runtimeconfig now
    sets both off, and hostfxr takes the application's properties first (read:
    `runtime_config_t::combine_properties`).
  * *Recorded, not done:* `HostPolicy.fs` has its own `join`, `fileNameOf` and `directoryOf`, beside stage 2's
    `HostStrings` in `DepsJson.fs`, which is private there; both run in one resolution, so a change to the host's
    separator handling would need making twice. Sharing them means making `HostStrings` internal; a candidate
    follow-up. Two test docstrings named the review round that found their bug; they now describe the regression
    alone.

## As built (stage 4a)

Branch `host-binding-4a-assemblies-for`, cut from `origin/main` at `da87e8582` and rebased onto #2359 (stage 3). Test
code only. The test came first,
in a commit of its own.

### The helper

`FrameworkUnderTest.assembliesFor framework application` returns the directory list a guest runs along.

* `GuestApplication` is decision 6's three cases: `InMemory`, `DirectoryWithoutDepsJson of directory` and
  `Publish of PublishedGuest`.
* `GuestFramework` says which `Microsoft.NETCore.App` the guest runs on: `PinnedPackOf platform`, which is
  `runtimeDirsFor platform`, or `Located`, which is `runtimeDirs ()`.
* The result is the application's directory, when it has one, ahead of the framework's directories. That is what each
  call site built before. Outside the Nix devshell `PinnedPackOf` falls back exactly as `runtimeDirsFor` does,
  because it calls it.

`GuestPublish.fs` now compiles before `FrameworkUnderTest.fs`, so that `Publish` can carry a `PublishedGuest`.

### The pack choice is an argument

The plan's helper took the platform and the application case. That cannot return both lists the harness uses for a
publish: `TestConsumerCases` runs on the pack, and `TestFSharpPureCases` does not. The same holds well beyond the
F# fixture. Of the call sites which moved, 113 had used `runtimeDirs ()`, without the pack, and 73 had used
`runtimeDirsFor`.

Two options were weighed.

* **Leave out every caller without the pack**, so the helper keys on the platform alone. The diff is small. But
  stage 5 gives every guest run a TPA, so stage 5 would then have to move those 113 sites itself, and stage 5 is the
  PR the plan says not to grow.
* **Make the framework an argument, and move every caller.** Chosen. The diff touches 131 files, a line or two in
  most, done by a script that refused any line building a list. Stage 5 then changes what one function returns.

The platform lives inside `PinnedPackOf`, because only the pack needs it. A `Located` run's framework is the host's
flavour, whatever its kernel, so stage 5 should give it the host's RID list, not the kernel's.

Two parts of decision 6 are not carried yet, since nothing reads them in this stage: the entry assembly's file name,
and the names of the files in a `DirectoryWithoutDepsJson`. Stage 5 adds them when the function needs them, and the
compiler finds every constructor.

### What moved, and what did not

* `InMemory` (177 sites): Roslyn-compiled guests, escape-analysis inputs, `IlMachineState.initial` states.
* `DirectoryWithoutDepsJson` (7): fabricated guests, `CrossAssemblyHarness`, `TestFabricatedSlotAliasing`, a
  `TestUnhandledExceptionReport` driver, the escape analysis's provider directory (twice), and the patched CoreLib
  of `TestEmulatedRuntime`. None of these directories holds a `deps.json`.
* `Publish` (2): `TestConsumerCases` on the pack, `TestFSharpPureCases` on `Located`.
* `LinuxCoreLibFlavour.runtimeDirsPreferringLinux` takes the linux-x64 pack's list from `assembliesFor`, and fails
  unless its argument is that pack. Every caller passes `requireLinuxFrameworkDirectory ()`, so the list is the same,
  and outside the devshell the caller is ignored before the list is built, as before.

`TestFrameworkLocatorRatchet` now allows `FrameworkUnderTest.runtimeDirs` and `runtimeDirsFor` only in
`FrameworkUnderTest.fs`, and where an allowance says why. The allowances are the tests of those two functions and of
the selection, and four uses that run no guest: the oracle's ASP.NET Core check, the consumer fixture's search for the
pack directory (to check its publish), and two sweeps of the framework's images (`TestImageAdmission`,
`TestGenericParamOwnerOracle`).

`assertFlavourServes` names `assembliesFor` as its remedy now, and the test of its message changed with it.

### For stage 5

* **No guest pins a publish's place on the list.** With the publish searched after the framework, every
  `TestFSharpPureCases` and `TestConsumerCases` case still passed on .NET 10. The F# cases bind FSharp.Core from the
  test host's `bin` directory then, and pass anyway. So a mistake in the order of a publish is caught only by this
  stage's unit test until stage 5's `di-version` witness lands.
* The four uses with allowances list or sweep framework directories. Under a TPA they need the framework's
  directories by another name, or the TPA's files.

### Tests

`TestFrameworkUnderTest`'s "assembliesFor is the directory list the harness runs each application along" checks
every framework (`Located`, and the pack of each of the three platforms) against every application case. The expected
list is spelled from `runtimeDirsFor` and `runtimeDirs ()` the way the call sites spelled it. Red first: the helper
did not exist, so the project did not compile (FS0039).

### Mutants

Each was applied to `assembliesFor`, built, run, and reverted, on the .NET 10 host inside the devshell.

| Mutant | Unit test | Guest |
| --- | --- | --- |
| the pinned pack dropped (`PinnedPackOf` gives `runtimeDirs ()`) | red | red: 11 of 13 `TestBoundedRun` tests, refused by `assertFlavourServes` |
| a sibling directory after the framework | red | red: all 4 cases of `TestEmulatedRuntime`'s "Startup refuses a CoreLib whose major is not supported" |
| a publish after the framework | red | survives: `TestFSharpPureCases` (14) and `TestConsumerCases` (16) pass |

### Suite

Each its own invocation, at `69e7011d4`, on the macOS arm64 host, all `Test Run Successful`:

| Selection | Runtime under test | Total | Passed | Skipped | `main` |
| --- | --- | --- | --- | --- | --- |
| default | .NET 10 | 6059 | 6020 | 39 | 6055 |
| `Guest` | .NET 10 | 250 | 250 | 0 | 250 |
| `GuestSolo` | .NET 10 | 2 | 2 | 0 | 2 |
| `GuestPure` | .NET 10 | 1210 | 1210 | 0 | 1210 |
| `GuestImpure` | .NET 10 | 306 | 306 | 0 | 306 |
| `GuestPure` | .NET 11 | 1210 | 1210 | 0 | 1210 |
| `GuestImpure` | .NET 11 | 306 | 306 | 0 | 306 |

The default suite gains four cases: the unit test, and three planted cases in the ratchet. `main`'s default total is
#2358's 6046 plus the nine cases of #2357, which its tip did not contain. The guest totals are those reported in
#2350, #2354 and #2357; this branch adds no guest. The .NET 11 `GuestPure` total is not reported for `main`; it is
what case discovery gives, since the cases come from the sources, which this branch does not change.

`fantomas` is clean, `scripts/footprint.py generate` changes nothing, and
`scripts/check-docstring-attachment-branch.sh` passes.

### Codex review

Two rounds; the second found nothing.

* **Round 1.** Two findings, both fixed.
  * *The ratchet missed `(FrameworkUnderTest.selected ()).RuntimeDirs`*, which is the list `runtimeDirs ()`
    returns. The member is renamed `FrameworkDirectories`, a name the ratchet can recognise, and the ratchet allows
    it only in the selection's own tests and in `TestDepsJson`, which reads an ASP.NET Core `deps.json` from it. A
    planted case checks the new spelling is caught.
  * *`assertFlavourServes` said the pack comes first*, but `assembliesFor` puts a guest's own directory ahead of it,
    so a CoreLib there shadows the pack. The message and the test project's `AGENTS.md` now say so.
* **Round 2.** No findings. Codex ran the framework and ratchet tests with .NET 10 and with .NET 11 selected.

The suites above ran before round 1's fixes. Those fixes rename a member and change a message and the ratchet, so
the default suite was run again, at `9f078b675`: 6060 tests, 6021 passed, 39 skipped, `Test Run Successful`. The
one test more than before is round 1's planted case. `fantomas` is clean, `scripts/footprint.py generate` changes
nothing, and `scripts/check-docstring-attachment-branch.sh` passes.

### The rebase onto stage 3

The branch was rebased onto `a4135a335`, which merged #2359. Git merged `TestFrameworkLocatorRatchet.fs` and the test
project file by itself; in this plan, stage 3's "As built" comes before this one. Stage 3's new test files read no
framework through `runtimeDirs`, `runtimeDirsFor` or `FrameworkDirectories`, so none moved and none needed an
allowance. `TestHostPolicyRealHost` reads `sharedFrameworkDirectory ()` and `aspNetCoreFrameworkDirectory ()` for the
real child process, which runs no guest under PawPrint; the ratchet does not cover those members.

After the rebase the whole solution builds with no warnings, and the ratchet, `TestFrameworkUnderTest` and stage 3's
fixtures pass (158 tests, 157 passed, 1 skipped). The default suite passes at `118db0a6e`: 6158 tests, 6119 passed, 39
skipped, `Test Run Successful`. That is #2359's reported 6153 plus this branch's five cases. Stage 3 changes no guest
run, so the guest suites were not run again.

**Codex, round 3** (after the rebase). One finding, fixed. *`GuestApplication.InMemory`'s docstring said every
assembly the guest references comes from the framework.* But on .NET 10 `runtimeDirs ()` includes the test host's
`bin` directory, so a guest can bind FSharp.Core from it. The docstring now says only that `assembliesFor` adds no
directory of the guest's. Only a docstring changed, so no suite was run again.
