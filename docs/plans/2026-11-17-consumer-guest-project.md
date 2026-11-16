# Regression guests for the libraries that run under PawPrint (issues #2092 and #2314)

Status: proposed, not decided. Nothing below is implemented. The questions at the end are for the decider.

Measured 2026-11-16 on `origin/main` at `418a1b8df`. Real .NET means 10.0.14 on the macOS host. PawPrint runs used
the test project's own harness (`BoundedRun.runWith`, Release build) through a throwaway NUnit fixture that was never
committed, with the guest published framework-dependent as `TestFSharpPureCases` publishes its cases. Each claim is
marked *measured* or *read*. The probe sources are the scenarios already measured for #2290 and for the 2026-11-16
next-target survey; this plan reuses them rather than writing new ones.

## What this is for

Twenty-two scenarios across the four mocking libraries of #2092 (Castle.Core 5.1.1, Moq 4.20.72, NSubstitute 5.1.0,
FakeItEasy 8.0.0), and the five FakeItEasy scenarios measured for #2290, now exit 0 under PawPrint with the same
stdout as real .NET. Nothing in the repository runs any of them. Each QCall on the way has its own guest, so each is
protected alone, but not their composition, and the interpreter takes several merges a day. #2314 asks for the same
kind of guest for Microsoft.Extensions.DependencyInjection's compiled resolvers, and says it belongs in the project
this plan introduces.

Every one of these guests needs a third-party `PackageReference`. The owner's rule is: "Fine to add test dependencies
as long as they don't end up as dependencies of prod." So third-party packages may go in a project that only the tests
use, and no published package may gain one as a dependency. The plan is as much about making that rule
machine-checked as about the guests.

## Study

### How `WoofWare.PawPrint.Test.FSharpPureCases` is built and run (read)

* It is an F# `Exe` project at the repository root, `net10.0`, `IsPackable` false, listed in
  `WoofWare.PawPrint.slnx`. No project references it.
* The test project names it only as a `FootprintInput` (`<FootprintInput Include="..\WoofWare.PawPrint.Test.FSharpPureCases" />`),
  so that CI's footprint-only checkout holds it. The glob `..\*\*.csproj;..\*\*.fsproj` already puts every top-level
  project file in the test footprint, but not the rest of the project's directory.
* `TestFSharpPureCases.fs` finds the repository by walking up to `WoofWare.PawPrint.slnx` (not `__SOURCE_DIRECTORY__`,
  which CI's deterministic build remaps), and in a `Lazy` runs
  `dotnet publish --configuration Release --no-self-contained -p:UseAppHost=false --output <project>/bin/Release/pawprint-test-publish`.
  Framework-dependent, so both PawPrint and the oracle run the framework under test, not the SDK's runtime pack; no
  apphost, so the oracle runs the DLL through the muxer of the framework under test. It deletes the output directory
  first, and fails if the publish holds a file named as one in the framework under test (`frameworkFilesIn`), since
  PawPrint would bind that file ahead of the framework's own.
* Each case runs `Program.run` directly (not `BoundedRun`) with the publish directory ahead of
  `FrameworkUnderTest.runtimeDirs ()`, and argv naming the case. Alongside, `RealRuntime.executeAssemblyInPlace` runs
  the published DLL on real .NET. Only the ending is compared (exit code, or "both threw"), never stdout.
* Parking is per runtime through `onRuntime : EmulatedRuntime -> GuestCasesOnRuntime<string>`, with
  `parkedEverywhere`, `parkedOnNet11` and `endingsEverywhere` tables. A parked case's real-runtime ending is still
  checked.
* It carries `[<Category("Guest")>]` and `[<Explicit>]`.

Two things in that fixture do not carry over unchanged:

* **It runs on the host's flavour of CoreLib.** It uses `runtimeDirs ()`, not `runtimeDirsFor platform`, and calls
  `Program.run`, so `BoundedRun`'s `assertFlavourServes` never checks it. The kernel is `KernelConfig.Default`, which
  impersonates Linux. `WoofWare.PawPrint.Test/AGENTS.md` says a guest runs on the pack of its kernel's flavour on every
  host; this fixture predates that rule. A new fixture should follow the rule (`runtimeDirsFor`, `BoundedRun`).
* **It compares no stdout.** For these libraries the output is most of the claim: a failed `Verify`'s message, an
  interceptor's trace, a proxy's type name.

### What the consumer guests need that the F# cases do not

**Dynamic code.** PawPrint's default policy is `IsDynamicCodeSupported` false (#853); without it every mocking
scenario fails at the first proxy with `PlatformNotSupportedException` (measured for #2290). Real CoreCLR defaults it
to true. A `RuntimeHostConfigurationOption` in the guest's project writes it into the published
`runtimeconfig.json` (measured), but neither side of the fixture reads that file:

* The PawPrint library never reads a runtimeconfig. Only the App does (`HostRuntimeConfig.fs`); the library takes
  `GuestConfig.AppContext` (read, and the `appcontext` skill). So the fixture must pass
  `System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported = "true"` in `GuestConfig.AppContext`, as
  the `TestImpureCases` type-builder cases do.
* `RealRuntime.executeAssemblyInPlace` *replaces* the publish's runtimeconfig with a generated one holding no
  `configProperties` (read). Real .NET then runs with dynamic code on by default and without the publish's
  `InvariantGlobalization`. Every scenario's output measured here is culture-independent, but the oracle should be
  given the same properties as PawPrint, so that a difference in setup can never be read as an interpreter bug.

So the AppContext properties should be stated once, in the fixture, and handed to both sides. (The alternative,
parsing the publish's runtimeconfig with `RuntimeConfig.parse`, has to happen before the oracle overwrites the file,
and it makes the guest project's MSBuild the source of a test's configuration; see question 6.)

**Stdout from the oracle.** `RealRuntimeResult` carries no stdout. `RealRuntime.runCapturing` captures it but is
private; `executeAssemblyInPlace` echoes it and drops it, and `executeConcurrently`, which returns `CapturedRun`s,
needs the guest as bytes in a scratch directory with no siblings, which these guests cannot use. A capturing form of
`executeAssemblyInPlace`, taking the AppContext properties to write into the generated runtimeconfig and returning
`CapturedRun`, is a small change to `RealRuntime.fs`. PawPrint's stdout is
`OutputLogEntry.bytesFor FileDescriptorRole.StandardOutput state.Kernel.OutputLog`, as `FabricatedGuest.stdoutOnPawPrint`
reads it.

**The step budget.** `BoundedRun.defaultMaxSteps` is 20,000,000. The most expensive scenario measured retires 3.08
million steps (measured, table below), so the default bound has more than six times headroom.

**What the publish holds (measured).** The guest's transitive closure is five packages: Castle.Core 5.1.1,
System.Diagnostics.EventLog 6.0.0 (Castle.Core's dependency for `net6.0`), Moq 4.20.72, NSubstitute 5.1.0 and
FakeItEasy 8.0.0. Each ships one `lib/net6.0` assembly. The publish directory holds those five DLLs and the guest, its
`deps.json` and `runtimeconfig.json`, and `runtimes/win/lib/net6.0/System.Diagnostics.EventLog{,.Messages}.dll`. None
of these is named as a file in the framework under test, so `frameworkFilesIn` passes (measured against the pinned
linux-x64 pack). The packages are pure IL; PawPrint refuses a machine-specific image, and all of them ran.

### The packages' AnyCPU images and the Linux CoreLib pack (measured)

Thirteen scenarios, at least two per library (the list is in "Measured timings"), were run through `BoundedRun.runWith`
twice each: once with `KernelConfig.Default` (Linux) along `FrameworkUnderTest.runtimeDirsFor` of that platform, which
puts the pinned linux-x64 pack first, and once impersonating `SimulatedUnixPlatform.macOsArm64` along the osx-arm64
pack. Each run had the publish directory at the head of the runtime-dir list and the dynamic-code property in
`GuestConfig.AppContext`. `BoundedRun` asserted the flavour of each run's CoreLib. All 26 runs exited 0, and every
run's stdout was byte-identical to real .NET 10's for the same scenario. The step counts differ between the flavours by
at most 0.2%.

So a guest with package dependencies works on the Linux pack on a macOS host, and nothing in these scenarios depends on
the flavour. The real-runtime oracle runs on the host's own framework, so on a macOS host a Linux-flavour run is
compared with real .NET on macOS. `WoofWare.PawPrint.Test/AGENTS.md` says such a comparison is sound only for facts that
hold across flavours. The scenarios' output (proxy traces, messages, computed values) is such a fact: it does not mention
the OS, and the measurement above is the evidence.

### Measured timings

All under the real harness, Release, `NUnit.NumberOfTestWorkers=4` (a CI runner's four vCPUs), on an 18-core M-series
Mac which another agent was also using for test suites, so the absolute numbers are noisy. Wall time is per run, with
four running at once.

| scenario | what it covers | steps (Linux) | wall, Linux pack | wall, macOS pack |
|---|---|---:|---:|---:|
| `castle-generic` | interface proxy with and without target, generic interface, generic method, `out` argument | 1,238,267 | 17.8 s | 17.5 s |
| `castle-class` | class proxy, virtual property, generic method, proxy type name | 974,112 | 13.6 s | 13.5 s |
| `castle-target` | class proxy with target and a mixin | 1,210,666 | 11.5 s | 11.3 s |
| `moq-basic` | `Setup`/`Returns`/`Verify` | 1,525,815 | 13.1 s | 12.4 s |
| `moq-verifyfail` | a failed `Verify`'s message | 1,585,524 | 12.4 s | 12.0 s |
| `moq-strict` | a strict mock's `MockException` | 1,590,520 | 12.3 s | 11.9 s |
| `nsub-basic` | `Returns`/`Received`/`DidNotReceive` | 1,380,191 | 11.4 s | 11.1 s |
| `nsub-fail` | a failed `Received`'s message | 1,490,668 | 12.1 s | 11.7 s |
| `nsub-class` | `ForPartsOf` an abstract class | 868,092 | 7.2 s | 7.0 s |
| `fie-callto` | #2092's snippet | 2,586,898 | 26.7 s | 25.7 s |
| `fie-mhhfail` | a failed `MustHaveHappened`'s message (#2291's `Log10`) | 2,617,908 | 25.3 s | 24.8 s |
| `fie-generic` | a generic method, `A.CallTo(fake).Where(...)` | 3,053,363 | 31.3 s | 30.1 s |
| `fie-wrap` | a wrapping fake, a class fake calling base methods | 3,081,209 | 33.1 s | 32.4 s |
| **sum** | | | **228 s** | **221 s** |

The whole run of 26 took 1 min 53 s. FakeItEasy is the dearest library, as the survey found, but each scenario costs
about a third of what the survey measured under the App with six to eight guests at once (26 to 103 s there).

With .NET 11 as the framework under test (`PAWPRINT_TEST_RUNTIME=Net11`), five of the scenarios were run the same way,
ten runs at four at a time (1 min 25 s in all). All exited 0 with stdout byte-identical to real .NET 10's; real .NET 11
was not run here, but #2092's comment records that real .NET 10 and 11 print the same for the FakeItEasy scenarios. The
steps rise by 6 to 8%, and the wall time more:

| scenario | steps (Linux) | wall, Linux pack | wall, macOS pack |
|---|---:|---:|---:|
| `castle-generic` | 1,328,856 | 19.0 s | 18.8 s |
| `moq-basic` | 1,604,782 | 20.0 s | 19.3 s |
| `nsub-basic` | 1,476,298 | 18.4 s | 17.9 s |
| `fie-callto` | 2,677,783 | 32.0 s | 31.0 s |
| `fie-wrap` | 3,187,279 | 48.7 s | 47.6 s |

So the .NET 11 leg should be budgeted at about 1.3 to 1.5 times the .NET 10 leg's cost.

The fixture's own `dotnet publish` is paid once per test run: 3 to 4 s here with a warm package cache (measured, after
deleting the project's `obj/` and `bin/`, and again with them in place). On CI the five packages (a few megabytes of
nupkgs; the published DLLs total 1.3 MB) are also downloaded from nuget.org on each run.

What this means for CI. CI's runners are slower per core than this machine and come in two speeds about 2x apart
(see the TRX history). Assuming the slow class at 2x, the thirteen scenarios on the Linux pack alone cost about
450 s of runner CPU, which on four vCPUs is about two minutes of wall time on the .NET 10 leg and up to three on the
.NET 11 leg. The whole set of
twenty-seven mocking scenarios would cost roughly twice that. The `Guest` category's other fixtures cost about
2,500 s of CPU per leg as of the 2026-11-06 TRX, so the thirteen add about a fifth to that runner.

### Which category

`WoofWare.PawPrint.Test/AGENTS.md` divides guest fixtures between the `test-guest` matrix's runners by measured CI cost:
`Guest` holds most; a fixture costing about as much as the rest of `Guest` gets a category of its own (`GuestPure`,
`GuestImpure`); `GuestSolo` holds fixtures whose single tests each run about as long as all of `Guest`. A runner costs
a fixed overhead (checkout, Nix, build) of several minutes whatever it runs.

With thirteen scenarios on one flavour, the fixture costs about a fifth of `Guest`, and no single test is long, so it
belongs in `Guest`. A category of its own would add two runners per pull request, each paying several minutes of
overhead for about two minutes of tests. If the scenario set grows to the full twenty-seven, or DI's seven modes join
(each 32 to 48 s under the App with four at once, per #2314), the fixture approaches half of `Guest` and the question
should be re-measured from the TRX files; a new category then needs the root `AGENTS.md` filter, the `test-guest`
matrix and nothing else (`TestCiSelectsEveryExplicitFixture` reads the matrix).

### How the repository's tooling treats a new project with package references (read)

**`scripts/footprint.py`.** A footprint is a set of repository paths. A project's footprint is its directory, the
tracked files its evaluation names (including `FootprintInput` items), the root files, and the footprints of the
projects it references. NuGet packages are explicitly outside it ("Inputs from outside the repository ... NuGet
packages (pinned by version, in project files)"). Consequences:

* A packed project that gained a `ProjectReference` to the guest project would gain the guest's directory in its
  `pathFilters`, so `generate` would change that package's `version.json`, and `check` (the `check-footprints` job)
  would fail until someone committed it. That leak is visible.
* A packed project that gained a `PackageReference` directly, or through `Directory.Build.props`/`.targets` (root
  files, already in every footprint), changes no `pathFilters`. `verify packages` packs it from its footprint and from
  the whole tree and gets the same bytes, because both packs carry the same new dependency. **Neither footprint check
  can see a package dependency.** That is the leak the owner's rule is about.
* `generate` refuses a solution in which the projects with a `version.json` are not exactly the packable ones. An SDK
  `Exe` project is packable by default (measured: `IsPackable` evaluates to `true` for the scratch guest). So if the
  guest project is in the solution and forgets `IsPackable` false, `generate` and `check` fail. That guard exists only
  if the project is in `WoofWare.PawPrint.slnx`, because `generate` evaluates the solution's projects and their
  references.
* `generate` runs `dotnet restore` on the solution first, so in the offline devshell it needs the new packages in
  `nix/deps.json`.

**`nix/deps.json` and the Nix build.** The flake's `default` package is `buildDotnetModule` with
`projectFile = WoofWare.PawPrint/WoofWare.PawPrint.fsproj` and `testProjectFile = WoofWare.PawPrint.slnx`, so its
restore, and `fetch-deps`, cover the whole solution. A guest project in the solution therefore has its packages in
`nix/deps.json` after `nix build .#default.fetch-deps && ./result nix/deps.json`, and if someone forgets the refresh,
`build-nix` and `flake-check` (whose `offline-restore` check restores the solution from the pinned packages alone) fail.
That is an automatic check that the pins were refreshed. `nix/deps.json` is in no package's `pathFilters` (the
packages name `flake.nix` and `flake.lock`, not `nix/`), so refreshing it moves no package's version. The package
installs only `WoofWare.PawPrint`'s output, so its runtime closure should not grow; that is worth confirming once with
`nix path-info -r` before and after. Today's `nix/deps.json` has 94 entries and none of the five new packages.

**The offline devshell.** It restores only from the Nix store (`RestoreSources` and `NUGET_FALLBACK_PACKAGES` from
`offlineNuget`, built from `nix/deps.json`), and sets `NuGetAudit` false. A project outside the solution would not be in
`nix/deps.json`, and its publish would fail offline.

**CI, as it stands (read from `.github/workflows/ci.yaml`).**

* `build` and `test-guest` check out the test footprint alone, build only the test projects
  (`footprint.py test-projects`), and run them. The guest project is not a test project, so it is built only when the
  fixture publishes it, at test time, in the default (online) devshell. Its packages are then downloaded from nuget.org
  during the test run. `TestFSharpPureCases` already publishes at test time, but its only package, FSharp.Core, is the
  one the test project's own build restored. So this adds a network dependency to the guest runner's test step.
* `nuget-pack` runs `dotnet restore`, `dotnet build` and `dotnet pack` on the whole solution, and uploads each
  package's nupkg by an explicit path. A guest project in the solution is restored and built there, and, being
  `IsPackable` false, packs nothing.
* `expected-pack` downloads each package's artifact and checks only that it holds exactly one nupkg. It has no
  checkout step, and reads no nuspec. Its comments say the Semantics, Loader and TypeSystem checks exist because
  PawPrint's nuspec depends on them, but nothing checks the nuspec says so either.
* `github-release-dry-run` computes a tag from each artifact's file name and runs the release action with
  `dry-run: true`. It is indifferent to dependencies.

### The CI gap, and what closing it takes

Nothing in CI reads a nuspec, and the footprint checks cannot see a package dependency, so a published package could
gain Moq as a dependency and every job would stay green. Fable's recommendation was to close that first, in its own
pull request, and the study above strengthens it: the nuspec is the only artefact in which the owner's condition is
visible.

What a nuspec check needs:

* **Input.** Each package's `.nuspec` is an entry at the root of its nupkg, a zip. Its `<dependencies>` holds one
  `<group targetFramework="net8.0">` per target framework, each with `<dependency id version exclude>` elements. For
  example, an older build's PawPrint nuspec (read) depends on `WoofWare.PawPrint.Domain`, `WoofWare.PosixKernel`,
  `FSharp.Core` and `Microsoft.Extensions.Logging.Abstractions`. Today's `WoofWare.PawPrint.fsproj` also references
  Loader, Semantics and TypeSystem, so its nuspec will list those too. `PrivateAssets="all"` references
  (Nerdbank.GitVersioning, Myriad) do not appear.
* **Expectation.** A committed file listing, for each package, its exact dependency set per target framework. The
  repository's own packages carry a version that Nerdbank.GitVersioning computes per commit, so the expectation names
  them without a version, and the check instead requires each such version to equal the version of the nupkg that the
  same `nuget-pack` run produced for that package. That also turns the existing comments in `expected-pack` into a
  check: a dependency on a sibling package that was not produced, or was produced at another version, fails. External
  packages are named with their version, or by id alone (question 3).
* **Where it runs.** A script (Python, beside `footprint.py`, using only the standard library's `zipfile` and
  `xml.etree`) run by `expected-pack` over the downloaded artifacts. That job needs a checkout step to have the script
  and the expectation. The same script can run locally over `dotnet pack -o <dir>` output.
* **Its own test.** The script decides whether the owner's rule holds, so it needs an oracle of its own, as
  `check-docstring-attachment.py` has in the flake's `docstring-attachment` check: synthetic nupkgs with a matching
  dependency set, an extra dependency, a missing one, a changed external version, a sibling at a version no artifact
  has, an extra target-framework group, and a nupkg with no nuspec. Each must give the expected verdict.

## Options

### Decision 1: where the packages and the guest's sources live

#### A. A sibling guest project in the solution, published at test time (Fable's recommendation)

A C# project `WoofWare.PawPrint.Test.ConsumerCases` at the repository root, `net10.0`, `Exe`, `IsPackable` false,
`PlatformTarget` AnyCPU, holding the `PackageReference`s and one `Program.cs` whose `Main` switches on a scenario name.
It is in `WoofWare.PawPrint.slnx`, referenced by nothing, and named by the test project as a `FootprintInput`. A new
fixture, `TestConsumerCases.fs`, publishes it framework-dependent once per run and runs each scenario through
`BoundedRun` on the Linux pack and through a capturing oracle, comparing ending and stdout.

* Leak risk: lowest of the in-repository options. The packages are in no project that anything references, and no
  project anything packs. Being in the solution brings three automatic guards: `footprint.py`'s packable check, the Nix
  restore's demand for `nix/deps.json` entries, and (after PR 1) the nuspec check.
* Footprint: the test footprint gains one directory; no `version.json` changes.
* CI time: the scenarios' cost (about two minutes per leg for thirteen) plus one restore and publish at test time.
  The network fetch at test time is new.
* A later library: one more `PackageReference` and one more scenario function. DI (#2314) joins this way.
* Cost: the fixture duplicates some of `TestFSharpPureCases`'s publish code, which could be shared (question 7).

#### B. `PackageReference`s in `WoofWare.PawPrint.Test`, guests compiled by the Roslyn harness

The test project references the five packages. Each scenario is a C# file compiled in-process by `Roslyn.compile...`
with the package assemblies as metadata references, located from the test host's own output directory, and run with
that directory in the runtime-dir list. The oracle gets a scratch directory with the guest and copies of the package
DLLs beside it.

* Leak risk: also none for the published packages, because `WoofWare.PawPrint.Test` is not packed and nothing
  references it. But the packages enter the test host's own dependency closure, and the test host's output directory
  becomes the place the guest binds third-party assemblies from, beside NUnit, FsCheck and Roslyn. A guest could then
  bind a test-host dependency by accident, which `frameworkFilesIn`-style checks would have to be extended to forbid.
* Footprint: unchanged. No new project.
* CI time: the same scenario cost, no publish; restore happens in the build step, not at test time, so no network at
  test time. The test project's restore and build grow slightly.
* A later library: one more `PackageReference` in the test project, and the Roslyn harness must learn each library's
  reference set. DI's transitive closure (Abstractions and more) would all enter the test host.
* Cost: the harness must learn to compile against non-framework references and to stage siblings for the oracle; both
  are new code in shared test infrastructure. The guest is no longer a real publish with a `deps.json`, which is what
  was measured.

#### C. Committed prebuilt guest binaries

Publish the guest once, commit the publish directory (the guest DLL and the five package DLLs) under the test project,
and run it from there. No project anywhere references a package, so no restore, no `nix/deps.json` change and nothing
for a nuspec to leak.

* Leak risk: none by construction.
* Footprint: one directory of binaries.
* CI time: the scenario cost only; no publish, no network.
* A later library: re-publish and re-commit, by hand, with no record in the repository of how the binaries were built
  except a note. A scenario's source and binary can drift apart unnoticed.
* Cost: third-party binaries in Git (Apache-2.0, BSD-3 and MIT licences, which allow it), a build that is not
  reproducible from the repository, and a scenario that cannot be edited without a manual step. This is the opposite
  of the project's deterministic-build stance.

**Recommendation: A.** It is the only option in which the guest is built from source in the repository, offline, and
with three existing mechanisms enforcing parts of the owner's rule. B's saving (no project, no test-time network) is
real but pays for it by putting third-party libraries in the test host. C trades every other property for leak
safety, which A gets anyway once the nuspec check lands.

### Decision 2: one guest project for every library, or one per library

* **One project** (A1). One restore and publish per run; DI and the mocking libraries share one `Main`. A library that
  scans loaded or co-located assemblies sees the others' DLLs; FakeItEasy's bootstrapper scans loaded assemblies, so
  this is likely harmless, but it is a coupling.
* **One per library** (A2). Isolated publishes and closures, a scenario that cannot be disturbed by an unrelated
  library, at the price of one publish each (and one more `FootprintInput` and solution entry each).

**Recommendation: one project** for the four mocking libraries, which already share Castle.Core 5.1.1 and were
measured together. Decide again when DI joins, by measuring the mocking scenarios with DI's packages in the publish.

### Decision 3: what the fixture compares

* **Ending and stdout against the oracle, at test time** (recommended). Needs the capturing `executeAssemblyInPlace`
  described above. The comparison covers library-defined messages without the guest having to know them.
* **Ending against the oracle, stdout against committed expected output.** No oracle change, but the expected files
  are written by a person from one machine's run, and a library message that changes between .NET 10 and 11 would need
  per-runtime files.
* **Self-checking guests: exit code only.** Each scenario asserts its own results and returns non-zero otherwise. This
  is the cheapest, but a failed `Verify`'s message cannot be checked without hard-coding the library's text in the
  guest.

The guests should also be self-checking where they can be (a scenario returns non-zero when a value it computed is
wrong), so that a failure says which assertion broke rather than only "stdout differs".

### Decision 4: which scenarios, and on which flavours

* **Thirteen scenarios on the Linux pack** (recommended): the table above. It covers each library's basic shape, a
  class proxy, each library's failure message (which reached #2291's `Log10`), and FakeItEasy's `GetInterfaceMap` path.
  About two minutes per leg on CI.
* **One per library** (four or five scenarios): about a minute per leg; loses the failure messages and the class
  proxies.
* **All twenty-seven**: about four minutes per leg; better composition coverage, and the measured cost is low enough
  that this is a fair choice.
* **Both flavours**: doubles the cost. The two flavours agreed on every scenario measured, and the rule in
  `WoofWare.PawPrint.Test/AGENTS.md` is that a guest runs on its kernel's flavour, which is Linux by default.
  Recommended: Linux only.

## Questions for the decider

1. **Where do the packages live: A (sibling guest project in the solution), B (the test project, Roslyn-compiled
   guests) or C (committed binaries)?** Recommended: A.
2. **Should the nuspec dependency check land first, as its own pull request?** Recommended: yes. It closes a gap that
   exists today whether or not this guest project lands, and it is the only check that sees a package dependency.
3. **In the nuspec expectation, are external packages pinned by version, or named by id alone?** By version catches
   an unintended upgrade of a published package's floor (as `Directory.Build.targets` already guards for FSharp.Core),
   but every `WoofWare.PosixKernel` bump then edits the expectation too. Recommended: by version; the edit is one line
   in the same commit as the bump, and it makes the bump visible in review.
4. **One guest project for every library, or one per library?** Recommended: one, re-measured when DI joins.
5. **Which scenarios, on which flavours?** Recommended: the thirteen above, Linux pack only, in category `Guest`.
6. **Where do the guest's AppContext properties come from: stated in the fixture and handed to both PawPrint and the
   oracle, or parsed from the publish's `runtimeconfig.json`?** Recommended: stated in the fixture. Parsing is closer to
   what a host does, but the oracle overwrites that file, and a test's configuration would then live in the guest
   project's MSBuild.
7. **Should the fixture share the publish, frame-check and repository-walk code with `TestFSharpPureCases`, by moving
   it to a helper module?** Recommended: yes, in the guest pull request, since two copies of the publish's
   correctness checks would drift. Moving `TestFSharpPureCases` onto `runtimeDirsFor` and `BoundedRun` is a separate
   change and not part of this plan.
8. **May the guest runner's test step download packages from nuget.org?** If not, the `test-guest` Build step should
   also restore the guest project, so that the fetch happens before the tests and fails as a build failure.
   Recommended: add that restore; it costs a few seconds and makes a nuget.org outage read as what it is.
9. **Should the guest project turn NuGet's vulnerability audit off?** With `TreatWarningsAsErrors` from
   `Directory.Build.props`, an advisory published later against a pinned package (NU1901 to NU1904) would fail the
   guest's online restore with no change in the repository. The offline devshell already sets `NuGetAudit` false. The
   guest is never shipped, so an advisory against it has no consumer. Recommended: `NuGetAudit` false in the guest
   project alone.

## Stages

Each stage is a pull request of its own.

### PR 1: assert each nuspec's dependency list in `expected-pack`

* `scripts/check-nuspec-dependencies.py`: reads every nupkg in the directories it is given, extracts the nuspec's
  `<dependencies>`, and compares it with a committed expectation (say `.github/nuspec-dependencies.json`), with the
  version rule for the repository's own packages above. It exits 1 naming each package, target framework and
  dependency that differs, and 2 when it cannot answer (no nuspec, an unreadable zip, a package the expectation does not
  name, or an expectation naming a package no artifact holds).
* `expected-pack` gains a checkout and one step after the downloads that runs the script over all six artifact
  directories.
* A flake check, like `docstring-attachment`, running the script's oracle over synthetic nupkgs (the shapes under "The
  CI gap").
* Measured in the pull request: the expectation is generated once from a local `dotnet pack` of each package and read
  against the projects' references by a person; the script passes on that output and fails on a hand-edited copy of
  one nupkg with an extra dependency.

This stage changes no project and no test, so the suites are unaffected.

### PR 2: the consumer guest project and its fixture

* `WoofWare.PawPrint.Test.ConsumerCases/` (with the decided option): the project, `IsPackable` false, `NuGetAudit` per
  question 9, the dynamic-code `RuntimeHostConfigurationOption` (for the record; the fixture states the property), and
  the decided scenarios, reused from the #2290 and next-target probes.
* `WoofWare.PawPrint.slnx` entry; `FootprintInput` in `WoofWare.PawPrint.Test.fsproj`; `nix/deps.json` refreshed;
  `footprint.py generate` output committed.
* `RealRuntime`: the capturing form of `executeAssemblyInPlace` with AppContext properties.
* `TestConsumerCases.fs`: publish once, run each scenario through `BoundedRun` along
  `FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform` with the publish first, compare with the
  oracle, park per runtime with an `onRuntime` table as the F# cases do, and check parked cases on real .NET.
* The PR body records the wall time of each scenario from the CI TRX, and the manual leak checks below.

### PR 3 (only if the decider staged the scenarios)

The remaining mocking scenarios, with re-measured CI cost, and a category change if the TRX says so.

### Later: Microsoft.Extensions.DependencyInjection (#2314)

A `PackageReference` and the seven modes, per #2314's notes (the stack-trace proof that the emitted resolver ran, and a
bound that fails the guest loudly). PR 1's check shows that no published package gained the dependency; the Nix
restore forces the `nix/deps.json` refresh. Re-decide question 4 then.

## Tests and checks

### The fixture

* For each scenario: PawPrint's ending equals the oracle's, and stdout is byte-identical. A guest exception is caught by
  the guest's own `Main` and printed with a distinct exit code (3 in the probes), so a library failure reads as a wrong
  exit code with its message in stdout, not as an unhandled exception.
* **Vacuity guards.** Each library must be shown to run generated code, or the test could pass on a path that never
  emits anything. `castle-class` already prints the proxy's type name (`ConcreteProxy`), which only a Castle-generated
  type has. Each library gets at least one scenario whose output can only come from a dynamic type or method, and a
  test asserts that line is present in PawPrint's stdout.
* **Mutation checks**, run once and recorded in the PR body: with the dynamic-code property removed from
  `GuestConfig.AppContext`, every scenario must fail (measured for #2290: `FakeCreationException` wrapping
  `PlatformNotSupportedException`); with `RuntimeTypeHandle_GetInterfaceMethodImplementation` made to answer the
  interface method itself, `fie-callto` must fail; with `Math.Log10` broken, `fie-mhhfail` must fail.
* The publish is checked as `TestFSharpPureCases` checks its own: no file named as one in the framework under test,
  and the guest DLL present.
* A test (default suite, no guest) that the publish holds exactly the expected package assemblies at the pinned
  versions, so that a restore that resolved something else is noticed. NuGet resolves a bare `Version="x"` to the lowest
  version at or above `x`, which is `x` when it exists, but the check costs nothing.

### Making the leak checks automatic

| leak | what catches it | automatic? |
|---|---|---|
| a published package gains a package dependency (directly, through `Directory.Build.*`, or transitively) | the nuspec check, in `expected-pack` | yes, after PR 1 |
| a packed project gains a `ProjectReference` to the guest project | `footprint.py check` (its `pathFilters` change); also the nuspec check | yes, already |
| the guest project becomes packable | `footprint.py check` (a packable project without a `version.json`) | yes, already, while it is in the solution |
| `nix/deps.json` not refreshed | `build-nix` and `flake-check`'s `offline-restore` | yes, already, while it is in the solution |
| a package's version moves because of the guest | `pathFilters` unchanged by construction; the nuspec check pins external versions | yes |

The checks Fable listed, run once and shown in PR 2's body:

* `nix develop -c dotnet list <project> package --include-transitive` for each of the six packed projects, before and
  after: identical.
* `nix develop -c python3 scripts/footprint.py generate`: changes `.github/test-footprint.json` (one new path) and no
  `version.json`.
* `nix develop -c python3 scripts/footprint.py verify packages`: passes.
* `git diff --no-ext-diff --stat origin/main -- nix/deps.json`: only the five packages added.
* `nix path-info -r` of the default package before and after: the same closure.

The first of these is subsumed by the nuspec check and need not be automated separately. The suites to run for PR 2
are the default suite and the four guest categories, each as its own invocation, on Net10 and Net11.

## Probe files

The measurement guest is a trimmed copy of the 2026-11-16 survey's `consumers/Program.cs` (the thirteen modes above,
with #2290's `callto` and `mhhfail` FakeItEasy scenarios added) and a project naming only the four libraries. The
throwaway fixture ran it with `BoundedRun.runWith` at a 400,000,000-step bound on both flavours and wrote each run's
stdout, ending, step count and wall time; a script compared the stdout with real .NET's. None of it is committed; the
guest's scenarios are reused in PR 2.
