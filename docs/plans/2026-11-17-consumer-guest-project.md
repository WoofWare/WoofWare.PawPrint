# Regression guests for the libraries that run under PawPrint (issues #2092 and #2314)

Status: decided 2026-11-16 (see "Decisions"). PR 1 and PR 2 are built (see "As built (PR 1)" and "As built (PR 2)" at
the end); PR 3 is not started.

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

## Decisions

Decided 2026-11-16 by the delegated decider, against `origin/main` at `418a1b8df`. The numbers match the questions
above. Three things were checked or measured while deciding, and the decisions lean on them:

* **The claim that nothing in CI sees a package dependency holds** (read from `.github/workflows/ci.yaml` and
  `scripts/footprint.py`). `expected-pack` has no checkout and only counts nupkgs. `footprint.py` deals in repository
  paths, and its docstring lists NuGet packages under "what this cannot see"; `verify packages` packs the same project
  twice, so both packs carry the same new dependency. One nuance the study missed: today a new external package in a
  published project at least forces a refresh of `nix/deps.json`, which a reviewer can see. Once the guest project has
  put Moq and the others into `nix/deps.json`, a published project that references one of them needs no refresh, so
  even that hint is gone. That is a second reason the nuspec check must merge before the guest project does.
* **The six nupkgs of the merge-queue run for `418a1b8df` were read** (measured). Each has one `net8.0` dependency
  group. Every dependency on a sibling package carries exactly the version of the sibling's own nupkg from the same run
  (for example `WoofWare.PawPrint.Domain` at `0.12.67-g418a1b8dff` in all five packages that depend on it). Every
  dependency has `exclude="Build,Analyzers"`. No nuspec has `frameworkReferences` or `frameworkAssemblies`. Each
  package's `lib/` holds its own DLL and XML file, except `WoofWare.PawPrint`, which ships no XML file. So the rule for
  sibling versions passes on today's packages.
* **`dotnet exec --runtimeconfig <file> <dll> <args>` was run on real .NET 10.0.14 against the study's publish**
  (measured). With a config outside the publish directory naming `IsDynamicCodeSupported` as `true`, `moq-basic`
  printed its expected output and exited 0. With the same config naming `false`, Moq failed with
  `PlatformNotSupportedException`, although the publish's own `runtimeconfig.json` says `true`. The publish's own file
  was byte-identical afterwards. So the oracle can be given its properties without writing into the publish directory.

### 1. Where the packages live: option A

A sibling C# guest project, `WoofWare.PawPrint.Test.ConsumerCases`, in the solution, referenced by no project, named
by the test project as a `FootprintInput`, and published by the fixture.

Option B puts third-party libraries into the test host's own closure and makes the guest something other than the
real publish which was measured. Option C gives up building from source. Option A is the only one in which the
repository's existing checks do part of the work: the packable check in `footprint.py`, the Nix restore's demand for
`nix/deps.json`, and (after PR 1) the nuspec check.

Requirements on the project:

* `IsPackable` false, `PlatformTarget` AnyCPU, `net10.0`, `OutputType` Exe.
* It keeps `TreatWarningsAsErrors` from `Directory.Build.props`. Do not copy `CSharpExample`'s opt-out.
* Every `PackageReference` names an exact version. No floating versions and no ranges.
* A comment at the top of the project file states the owner's rule and says that no project may reference this one.

### 2. The nuspec check lands first, as its own pull request: yes

It closes #2323, which exists whether or not the guest project lands. PR 3
must not merge before it. The design follows.

**What it compares.** For every nupkg it is given, three things read from the package itself:

1. The nuspec's dependency groups: for each `targetFramework`, the set of dependencies, each with its `id`, its
   `version` string, and its `include` and `exclude` attributes exactly as written. Order is ignored. The groups must
   be exactly the expected ones: an extra or missing group is a difference.
2. The entries of the nupkg whose path begins `lib/`, as a set. A third-party DLL packed beside ours is a way into
   prod which the dependency list does not show, and this also turns the existing comment in `expected-pack` ("the
   assembly would simply be missing from lib/") into a check.
3. The set of packages. The package ids found must be exactly the expectation's keys, each found once. The
   expectation's keys must also be exactly the names of the top-level directories which hold a `version.json` (today a
   package's directory name is its `PackageId`; the script says so if that ever stops being true). So a new package
   which someone forgot to upload from `nuget-pack`, or forgot to add to the expectation, fails here.

A nuspec which has a `frameworkReferences` or `frameworkAssemblies` element, or a `dependency` outside a `group`, is
reported as a difference: the expectation has no way to state one, and none exists today. Read the nuspec by local
element names, because its XML namespace varies with the features a package uses. Take the package's id and version
from the nuspec, and require the file to be named `<id>.<version>.nupkg`, since `github-release-dry-run` computes a
tag from the file name.

**Sibling packages.** A dependency whose id is itself a key of the expectation is a sibling. The expectation gives it
no version (giving one makes the expectation malformed). The check requires its version in the nuspec to equal the
version of the sibling's own nupkg among the packages given. Every other dependency is external and the expectation
must give its version (question 3).

**The expectation file.** `scripts/package-dependencies.json`, beside the script. Not under `.github/` and not at the
repository root: `.github` is in the test footprint and every root file is in every package's footprint, so an edit
there would run the whole suite or move every package's version. A file under `scripts/` is in no footprint, and
`expected-pack` runs on every pull request regardless. Its shape:

```json
{
  "WoofWare.PawPrint.Loader": {
    "lib": ["lib/net8.0/WoofWare.PawPrint.Loader.dll", "lib/net8.0/WoofWare.PawPrint.Loader.xml"],
    "dependencies": {
      "net8.0": [
        {"id": "FSharp.Core", "version": "10.1.203", "exclude": "Build,Analyzers"},
        {"id": "WoofWare.PawPrint.Domain", "exclude": "Build,Analyzers"}
      ]
    }
  }
}
```

The expected contents today, from the measured artifacts (confirm them against the pull request's own
`nuget-pack` output): every package depends on `FSharp.Core` 10.1.203. `Domain` also depends on
`Microsoft.Extensions.Logging.Abstractions` 9.0.2. `Loader` and `Semantics` also depend on `Domain`. `TypeSystem` also
depends on `Domain` and `Loader`. `Analysis` also depends on `Domain`, `Loader`, `Semantics` and `TypeSystem`.
`WoofWare.PawPrint` also depends on those four, on `Microsoft.Extensions.Logging.Abstractions` 9.0.2 and on
`WoofWare.PosixKernel` 0.1.424.

**How it is maintained: by hand, with no mode that writes it.** The file is a statement of policy, not something
derived. With a `generate` command, "run generate and commit" becomes a reflex, and the reflex would add Moq as
readily as it adds a wanted dependency. A hand edit of a file whose only subject is "what consumers must install" is
one line, in the same commit as the change to the project, and a reviewer reads it as what it is. To make the edit
easy, a failing run prints the actual entry of each differing package in the file's own format.

**What failure looks like.** One line for each difference, naming the package, its version, the target framework and
the dependency or `lib/` entry, and saying which way it differs (unexpected, missing, or a changed attribute with
both values). Then the actual entry, then this paragraph or one like it:

> A published package's dependencies are what its consumers must install, and a dependency used only by tests must
> never become one. If this change is meant for consumers, edit scripts/package-dependencies.json in the same
> commit. Otherwise remove the reference from the project.

Exit status follows `footprint.py`: 0 when everything matches; 1 when the packages were read and differ from the
expectation (including an unexpected or missing package); 2 when the question could not be answered (a file that is
not a zip, a nupkg with no nuspec or with two, two nupkgs with one id, a malformed expectation).

**Where it runs.** `expected-pack` gains a checkout, downloads every `nuget-package-*` artifact with one pattern, and
runs `python3 -I scripts/check-package-dependencies.py` over the download directory. The script uses only the
standard library, so the job needs no Nix. The six "exactly one nupkg" steps are deleted, because the script's third
comparison says the same thing; keep their comments about why a missing sibling package matters, on the new step.
The same command works locally over `dotnet pack --configuration Release -o <dir>`.

**The script's own tests.** The script decides whether the owner's rule holds, so it gets an oracle, run as a flake
check beside `footprint` (which already has a Python with hypothesis). Write the comparison as a pure function from
an expectation and a list of parsed packages to a list of differences, with the zip and XML reading around it. The
properties, written before the script:

* For any generated set of packages, the expectation rendered from them yields no difference.
* Any single mutation of the packages or of the expectation is reported, and the report names the package: a
  dependency added, removed, or with one attribute changed; a group added or removed; a `lib/` entry added or
  removed; a sibling's version changed in one place; a package added or removed.
* Through real zip files built with `zipfile`: each unreadable shape listed above exits 2.

The pull request also shows, in its body, the script passing on its own CI artifacts and failing on a local pack of
`WoofWare.PawPrint` with a `PackageReference` to Moq temporarily added.

**What the check cannot see**, to be stated in the script's docstring: a `PackageReference` with
`PrivateAssets="all"` whose assembly our code then references. It is absent from the nuspec, and the shipped DLL
still needs it. That is #2324, and it is not part of this plan.

**Documentation in the same pull request.** The root `AGENTS.md` sentence about a new published package now says
that it needs an entry in `scripts/package-dependencies.json` and in the `nuget-pack` and `github-release-dry-run`
jobs (no longer in `expected-pack`). Its sentence about bumping `WoofWare.PosixKernel`, and the
`emulated-posix-kernel` skill's, gain "and its version in `scripts/package-dependencies.json`".

### 3. External packages are pinned by version: yes

By exact version string. The nuspec's version is the floor every consumer must meet, so a change to it is a change
to the published contract, and it should be visible in review. The cost is one line on each `WoofWare.PosixKernel`
bump, and a bump that forgets it fails `expected-pack` with a message naming the line.

### 4. One guest project for every library: yes, and Microsoft.Extensions.DependencyInjection joins it

Do not re-decide this when #2314 arrives. Both PawPrint and the oracle run the same publish, so a library that
notices another library's DLL beside it changes both sides alike; it cannot make the comparison fail falsely. A
difference that appears only under PawPrint when a package is added is an interpreter bug, which is what the fixture
is for. Split the project only if two libraries need different versions of one dependency, since one publish cannot
hold both.

Two guards against a scenario that passes without running:

* The guest returns a distinct non-zero exit code for a scenario name it does not know, and the fixture states the
  expected ending of every scenario (0 unless its table says otherwise), as `endingsEverywhere` does for the F#
  cases. Agreement with the oracle alone would pass a misspelt name, because both sides would refuse it alike.
* The guest prints its scenario names for the argument `list`. A test runs that on real .NET and requires the list
  to equal the fixture's, so a scenario added to the guest and not to the fixture is noticed.

### 5. The thirteen scenarios, on the Linux pack, in category `Guest`: yes

The thirteen in the study's table, along `FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform`,
through `BoundedRun` at its default bound. The two flavours agreed on every scenario measured, so the macOS pack would
double the cost to no purpose. Both the Net10 and Net11 legs run the fixture, as they run every guest fixture.

The other fourteen scenarios are not staged. There is no PR for them. Add a scenario when a bug shows that the
thirteen missed a path, and re-measure the category from the TRX files when #2314's modes join.

Two things to measure in PR 3 before trusting the fixture, because the study did not:

* **Real .NET 11.** The study compared PawPrint on .NET 11 with real .NET 10's output. The fixture compares with the
  framework under test, so run all thirteen with `PAWPRINT_TEST_RUNTIME=Net11` first, and park per runtime if a
  library prints something different there.
* **That the oracle's output is stable.** Real .NET randomises string hashing in each process and PawPrint does not.
  Run each scenario twenty times on real .NET and confirm that its stdout never varies. A scenario whose output
  varies is rewritten until it does not, or left out.

### 6. The AppContext properties are stated in the fixture and handed to both sides: yes

One value in `TestConsumerCases.fs`, of two properties:

* `System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported` = `true`
* `System.Globalization.Invariant` = `true`

The second is there because PawPrint's kernel seeds `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` by default, while the
oracle's process inherits the test host's environment and would otherwise use the host's ICU and the developer's
locale. The property takes precedence over the environment variable in CoreLib, so stating it on both sides makes
them agree whatever the machine.

The same value goes to `GuestConfig.AppContext` and to the oracle. A reader of the fixture can then see what both
sides ran with, which they could not if it came from the guest project's MSBuild.

The guest project also declares both (`RuntimeHostConfigurationOption` for the first, `InvariantGlobalization` for
the second), so that someone debugging a scenario can run the publish by hand, or through `WoofWare.PawPrint.App`
(which does read the `runtimeconfig.json`), and get the same behaviour. To stop the two from drifting, the fixture
parses the publish's `runtimeconfig.json` with `RuntimeConfig.parse` and fails unless each property the fixture
states has the same value there. The publish's file carries other properties which the SDK writes by default;
neither side is given those, and that is deliberate.

**The oracle (PR 2).** `RealRuntime` gains a capturing form for an assembly on disk, taking the
`AppContextProperties` and returning `CapturedRun`. Requirements:

* It runs the guest as its own process, through `runCapturing` and the muxer of the framework under test, as every
  other entry of `RealRuntime` does. Nothing here may run a guest inside the test host;
  `WoofWare.PawPrint.Test/AGENTS.md` says why.
* It passes the generated runtimeconfig as `dotnet exec --runtimeconfig <file> <dll> <args>`, with the file in a
  scratch directory of the call's own, removed afterwards. It does not write into the assembly's directory. The
  present mechanism replaces `<name>.runtimeconfig.json` beside the assembly and is safe under concurrency only
  because every caller writes identical contents; with properties as a parameter that would no longer be true, and
  two runs of one publish with different properties would read each other's file.
* `executeAssemblyInPlace` becomes this function with no properties, echoing its output, so that there is one
  mechanism and not two. Its docstring stops saying that it replaces the file. Its fourteen callers do not change.
* Tests, in the default suite, written first: a Roslyn-compiled guest which prints `AppContext.GetData` for the names
  it is given. (a) It prints the values passed. (b) A `runtimeconfig.json` placed beside the assembly, naming other
  values and a framework that does not exist, is not used and is byte-identical afterwards. (c) Two concurrent runs
  of one assembly with different properties each print their own. A small FsCheck property over generated property
  maps covers (a); keep its case count low, since each case starts a process.
* PR 2 changes how fourteen existing callers start their guests, so it runs the default suite and all four guest
  categories on both runtimes.

**What the fixture compares.** `CapturedRun.Stdout` is text rebuilt from lines, each ended by the host's newline,
not the bytes the guest wrote. So decode PawPrint's stdout as UTF-8 (failing on invalid input) and put it in the same
form before comparing, as `TestAspNetTwoProcess` does. The claim is "the same lines"; the comparison cannot see a
missing final newline, and the fixture's docstring should say so. Also require stderr to be empty on both sides for a
scenario that exits 0: every scenario measured writes nothing there, and a guest exception is printed to stdout by
the guest's own `Main`.

### 7. Share the publish code with `TestFSharpPureCases`: yes, in PR 3

Move the repository walk, the publish, and the check for files named as the framework's into a helper module, in the
pull request that adds the second user. `TestFSharpPureCases` keeps its behaviour; moving it onto `runtimeDirsFor`
and `BoundedRun` stays out of this plan. Hold the move to `scripts/check-docstring-attachment-branch.sh`.

The check that the publish holds the expected assemblies goes in the helper's caller for the consumer cases, inside
the `Guest` fixture, not in the default suite: a default-suite test that publishes would make a bare `dotnet test`
restore third-party packages. It asserts the exact set of top-level files in the publish directory, and each package
assembly's version.

### 8. The test step should not download packages: restore in the Build step

The `test-guest` job's Build step also runs `dotnet restore` on the guest project, in the footprint checkout, with a
comment saying why. The fixture's own `dotnet publish` then finds the restore up to date and fetches nothing, and a
nuget.org outage fails the Build step, where it reads as what it is.

The fixture keeps restore on (no `--no-restore`). With `--no-restore`, a fresh worktree running the guest category
as the root `AGENTS.md` says would fail thirteen tests until someone restored the guest project by hand, and that
cost falls on every local run to guard against a rare event on CI. The price of this choice is that nothing asserts
the test step made no network request; if the restore line is ever removed, the fixture quietly goes back to
fetching at test time. That is accepted.

**The offline devshell.** The fixture's `dotnet publish` is a child of the test host and inherits its environment,
so in the offline devshell it restores from the Nix store, provided `nix/deps.json` has the packages. Being in the
solution guarantees the refresh is demanded. To have a machine confirm that the fixture's exact publish works with no
network, PR 3 adds that publish command to the flake's `offline-restore` check, beside the `CSharpExample` publishes.

### 9. `NuGetAudit` false in the guest project alone: yes

With a comment in the project file. The audit reads an advisory feed at restore time, so with
`TreatWarningsAsErrors` a new advisory against a pinned package would fail every pull request with no change in the
repository. The SDK in use audits transitive packages too for a `net10.0` project, so `System.Diagnostics.EventLog`
6.0.0 counts. The guest is never shipped, and it pins these versions on purpose. The property is per project, so it
also covers the solution-wide restore in `nuget-pack`.

Leave the audit on everywhere else. The published projects are exposed to the same failure today, and there it is
arguably wanted.

### What the plan missed

Collected here; each is also in the decision it belongs to.

* Once the guest's packages are in `nix/deps.json`, a published project can reference one of them without any
  refresh (first bullet at the top).
* `PrivateAssets="all"` hides a package from the nuspec while the shipped DLL still needs it
  (#2324).
* A third-party DLL can be packed into `lib/` without being a dependency; the check compares `lib/` too.
* Agreement with the oracle passes a misspelt scenario name; the fixture must state each expected ending.
* The oracle's stdout is line-normalised text, so "byte-identical" is not what the fixture can claim.
* PawPrint runs with invariant globalisation by default and the oracle does not; state the property on both sides.
* The in-place oracle is safe under concurrency only while every caller writes the same runtimeconfig; pass the file
  with `dotnet exec --runtimeconfig` instead.
* Real .NET 11 was never run on these scenarios, and the stability of real .NET's output across processes was never
  measured.
* The publish and release jobs in `ci.yaml` (all disabled) name two of the six packages
  (#2325). Not part of this plan.

### Staging

Three pull requests. PR 1 and PR 2 are independent of each other; PR 3 needs both merged. Put one in flight at a
time.

**PR 1: the published packages' dependency check.** `scripts/check-package-dependencies.py`, its tests and flake
check, `scripts/package-dependencies.json`, the `expected-pack` job rewritten around it, and the `AGENTS.md` and skill
sentences. No project and no test changes. Closes #2323.

**PR 2: the capturing in-place oracle.** `RealRuntime` as in decision 6, with its tests. No guest project yet.

**PR 3: the consumer guest project and its fixture.** The project and its thirteen scenarios; the solution entry; the
`FootprintInput`; `nix/deps.json` refreshed; `footprint.py generate` output; the shared publish helper;
`TestConsumerCases.fs` with its per-runtime parking table, the vacuity guards and the two scenario-list guards; the
restore line in `test-guest`; the publish line in `offline-restore`; a short paragraph in
`WoofWare.PawPrint.Test/AGENTS.md` naming the mechanism and the file. Its body records the study's one-off leak
checks, the three mutation checks, the two measurements of decision 5, and each scenario's wall time from the CI TRX.
It refers to #2092 and does not close it.

There is no PR for the remaining mocking scenarios. Microsoft.Extensions.DependencyInjection (#2314) follows as its
own pull request into the same project.

### As built (PR 1)

Branch `ci-nuspec-dependency-check`, closing #2323. What it adds:

* `scripts/check-package-dependencies.py`, standard library only. It reads every `*.nupkg` beneath the directories
  it is given (recursively, since a pattern download puts each artifact in a directory of its own) and makes the
  three comparisons of decision 2, with the sibling rule, exact versions for external packages, the unstatable
  shapes reported as differences, the `<id>.<version>.nupkg` file name rule, and exit 1 or 2 as decided. Its
  docstring states what it cannot see (#2324, and anything outside `lib/` and the dependency groups).
* `scripts/package-dependencies.json`, written by hand and then checked to be byte-identical to the layout the script
  prints for a local pack. Its contents are the decision's list exactly.
* `scripts/test_check_package_dependencies.py`, run by the flake's new `package-dependencies` check. Every property
  builds real nupkgs from one generated model, renders the expectation and the version.json directories from the same
  model, and runs the script's `main`. A matching set passes (with its distribution asserted); each single change, on
  either the nupkg side or the expectation side, exits 1 and names the package and what differs; each unreadable shape
  exits 2; the printed entry, pasted into the expectation, removes the package's differences; the printed layout
  parses to the entry it stands for. Where a property has a few discrete shapes, every example runs all of them,
  because a first run showed the deterministic profile never drawing one shape of ten.
* `expected-pack`: a checkout, one `pattern: nuget-package-*` download into `$RUNNER_TEMP/packages`, and the script
  run with the runner's own `python3 -I`. The six "exactly one nupkg" steps are gone; their comment moved to the new
  step.
* `AGENTS.md` and the `emulated-posix-kernel` skill: a new published package needs an entry in the expectation, and a
  `WoofWare.PosixKernel` bump edits its version there too.

Choices the decisions left open, or which go slightly beyond them:

* Beyond the shapes decision 2 names, any element or attribute in a nuspec's `<dependencies>` other than groups,
  dependencies and their `id`, `version`, `include` and `exclude` is reported as unstatable (exit 1). A group or a
  dependency named twice in a nuspec, a `<dependency>` with no id, and a package named twice in the expectation (which
  `json.loads` would otherwise keep silently) are unreadable (exit 2).
* A dependency on a sibling whose nupkg was not given gets a line of its own naming the dependent, beside the
  "missing package" line.
* The packages are the top-level directories holding a `version.json` on disk, not in git's index, so that the CI job
  needs no history. `--expectation` and `--root` exist for the tests; CI uses the defaults beside the script.
* `flake.nix` is a root file, so this change moves every package's version, as any change to it does. `.github` is in
  the test footprint, so CI runs the suites on this change although it touches no F#.

Measured:

* A local `nix develop -c dotnet pack --configuration Release`, staged in the layout of a pattern download: exit 0,
  both at the plan's commit and at the implementation commit. The six artifacts of the merge-queue run for `418a1b8df`
  (downloaded with `gh run download --pattern 'nuget-package-*'`): exit 0, also with Python 3.12, the runner image's.
  So the sibling-version rule passes on today's packages.
* A local pack with `<PackageReference Include="Moq" Version="4.20.72" />` temporarily added to
  `WoofWare.PawPrint.fsproj`: exit 1, `WoofWare.PawPrint <version>: net8.0: unexpected dependency Moq
  version="4.20.72" exclude="Build,Analyzers"`, then the actual entry and the rule. A copy of the clean pack with Moq
  added to the PawPrint nuspec by hand and a `Castle.Core.dll` added to its `lib/`: exit 1, naming both.
* `actionlint` reports the same nine findings on the workflow before and after, none in `expected-pack`. The pinned
  `actions/download-artifact` (v8.0.1) takes `pattern`, and without `merge-multiple` extracts each artifact into a
  directory named after it.

Mutation testing: each comparison, the sibling rule and each refusal broken in turn in a copy of the script, with the
properties run against the copy. All killed except one equivalent mutant: skipping the special message for a sibling
whose nupkg is absent falls through to the general version-mismatch line, which also names the dependent; removing the
line altogether is killed by `test_a_missing_package`.

| mutant | tests that failed |
|---|---|
| unexpected group ignored | group added, group removed, printed entry |
| missing group ignored | group added, group removed |
| unexpected dependency ignored | removed dependency, added external dependency, printed entry |
| missing dependency ignored | removed dependency, added external dependency, printed entry |
| external version not compared | external version change, external dependency without its version, printed entry |
| `include` not compared | include or exclude change, printed entry |
| `exclude` not compared | include or exclude change |
| sibling version not compared with the sibling's nupkg | sibling at the wrong version |
| sibling treated as external | matching set, sibling at the wrong version, printed entry |
| sibling versions taken from the dependents' nuspecs | sibling at the wrong version, missing package |
| absent sibling nupkg reports nothing for the dependent | missing package |
| unexpected `lib/` entry ignored | changed `lib/` entry, printed entry |
| missing `lib/` entry ignored | changed `lib/` entry |
| `lib/` entries not read | changed `lib/` entry, matching set, printed entry |
| `version.json` without an entry ignored | `version.json` with no package |
| entry without a `version.json` ignored | package with no `version.json` |
| missing package ignored | missing package |
| unexpected package ignored | unexpected package |
| every top-level directory counted as a package | matching set, printed entry |
| file name not checked | file named other than its id and version |
| unstatable shapes ignored | what the expectation cannot state |
| `frameworkReferences` not seen | what the expectation cannot state |
| dependency outside a group not seen | what the expectation cannot state |
| group without `targetFramework` dropped | what the expectation cannot state |
| two nupkgs with one id accepted | two nupkgs with one id |
| two nuspecs accepted | unreadable nupkg |
| missing directory accepted | directory that does not exist |
| a dependency twice in one nuspec group accepted | unreadable nupkg |
| a group twice in one nuspec accepted | unreadable nupkg |
| expectation: sibling with a version accepted | sibling given a version in the expectation |
| expectation: external without a version accepted | external dependency without its version |
| expectation: unknown key accepted | unreadable expectation |
| printed entry keeps a sibling's version | printed entry |

Not verifiable before it runs on GitHub: that the job's checkout, download and `$RUNNER_TEMP` path behave as read
above, and that `nix flake check` on the Linux runner runs the new check (it ran on macOS through
`nix build .#checks.aarch64-darwin.package-dependencies`).

### As built (PR 2)

Branch `test-capturing-oracle`. Everything is in `WoofWare.PawPrint.Test`.

**The oracle.** `RealRuntime.captureAssemblyInPlace : AppContextProperties -> string[] -> string -> CapturedRun`
writes `runtimeConfigFor OracleFrameworks.NetCoreApp configProperties` as `<name>.runtimeconfig.json` into a
scratch directory of its own, and runs `dotnet exec --runtimeconfig <that file> <dll> <args>` through `runCapturing`
and the muxer of the framework under test, with the assembly's directory as the working directory. Nothing in the
assembly's directory is read or written. The host looks for the dev runtimeconfig beside the runtimeconfig it is
given (`get_runtime_config_paths_from_arg` in hostfxr's `fx_muxer.cpp`), so a `<name>.runtimeconfig.dev.json`
beside the assembly is not read either. `executeAssemblyInPlace` is `captureAssemblyInPlace
AppContextProperties.empty` followed by the existing `echo`; its signature and its fourteen call sites are unchanged,
and the overwrite of the runtimeconfig beside the assembly, with its staging file and rename, is deleted.

Codex found, over ten rounds, the ways in which the first version could report an ending for something other than
the guest it was asked to run, or let the guest see the oracle's files. The function now refuses, with an exception
rather than a `CapturedRun`:

* properties no runtimeconfig can deliver as given. This is in `runtimeConfigFor`, so `executeConcurrently` refuses
  them too: a name the hosting layer populates (checked with `RuntimeConfig.combine`, as for a runtimeconfig PawPrint
  reads), which the host refuses before the guest starts, with exit code 161 that would read as the guest's; and a
  name or value that is not well-formed UTF-16, which the runtimeconfig would carry as U+FFFD;
* an assembly path or temp directory with `..` in it. `Path.GetFullPath`, and so `File.Exists`, collapse `..` by its
  spelling, while after a symbolic link the operating system climbs from the link's target. An attempt to walk paths
  as the operating system does needed, round after round, more of `realpath`'s behaviour (search permission, Windows
  roots, drive-relative paths), so the oracle refuses the ambiguity instead. Without `..`, `Path.GetFullPath` only
  joins a relative path to the test host's working directory and drops `.`;
* a temp directory that does not exist, which `Directory.CreateTempSubdirectory` creates on some platforms;
* an assembly whose directory is the temp directory or above it, where the guest would see the scratch directory in
  its tree. Comparing spellings proved unsound (symbolic links, a linked assembly file, case folding, Unicode
  normalisation), so the check asks the filesystem: after creating the scratch directory, whose name is random, it
  tries the path down to it from each directory above it, starting from the guest's working directory and from the
  directory of the file the host runs (the target's, when the assembly's file is a link), and with the temp directory
  both as spelled and with its links resolved. That path exists only if the guest could reach the scratch directory.

The scratch directory comes from `Directory.CreateTempSubdirectory`, so its name is no other directory's and cleanup
removes only what this call made; its path is absolute, since `TMPDIR` may be relative.

**The tests**, in `TestRealRuntimeInPlace.fs`, in the default suite:

* decision 6's three: (a) an FsCheck property, 15 cases, over maps of up to four generated properties whose values mix
  JSON escapes, control characters, non-ASCII text and a character outside the BMP, the guest printing each value as
  UTF-16 hex and null for one it was not given; (b) a runtimeconfig beside the assembly, naming another value and
  framework version `0.0.1` with roll-forward disabled, is not read, and every file in the directory is unchanged
  afterwards; (c) eight runs of one assembly, three rounds, started on a barrier, each with its own value, all awaited
  before any failure is reported;
* stdout, stderr and the ending captured as lines (a `\r\n` and a missing final newline both come back as
  `Environment.NewLine`);
* what the existing callers rely on, through `executeAssemblyInPlace`: the endings (exit code, unhandled exception,
  FailFast), the command line (`Environment.GetCommandLineArgs` is the DLL's path and then the arguments), the
  working directory, the echo (read from NUnit's `TestExecutionContext.CurrentContext.CurrentResult.Output`), and
  that the guest runs the framework under test (CoreLib's whole directory, links resolved on both sides) whatever
  runtimeconfig the directory holds, which is what the overwrite gave the F# pure cases, published for `net10.0` and
  run on .NET 11;
* each refusal above: three host-owned property shapes, and one through `executeConcurrently` in
  `TestRealRuntimeConcurrent.fs`; a lone surrogate in a value and in a name; a path with `..` that would otherwise
  have run a decoy; an assembly directly in the temp directory, through a symbolic link to it, as a link to a file in
  it, and through another case of its name (ignored on a case-sensitive filesystem);
* a relative path, with a directory in it, names the same assembly. Its assembly is laid out under the test host's
  working directory, which must therefore be writable, since a relative path climbing out of it would be refused.

`TestFrameworkLocatorRatchet` gains one allowance: the guest source in the new file names `Assembly.Location` to
report where real .NET loaded the guest's CoreLib from.

**Choices the decisions left open.** The function takes no `OracleFrameworks`, environment or standard streams: no
caller, and none of PR 3's guests, needs more than `Microsoft.NETCore.App` with the defaults, and adding a parameter
later is mechanical. Test (c) starts eight runs three times rather than two once, since a shared runtimeconfig shows
only when the starts overlap, which no test can force. The guest prints paths that the host resolves (`/tmp` on
macOS, the Nix store's links) so that the test compares them with links resolved on both sides.

**Decision 6's premise about invariant globalisation is wrong, and PR 3 needs a decision.** Decision 6 says the
`System.Globalization.Invariant` property takes precedence over the environment variable in CoreLib. It is the other
way round: `GlobalizationMode.Settings.Invariant` is
`AppContextConfigHelper.GetBooleanConfig("System.Globalization.Invariant", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT")`,
which returns the environment variable's value whenever it is `0`, `1`, `true` or `false`, and reads the property
only otherwise (read in the pinned .NET 10 source). Codex measured it on .NET 10.0.14: with
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` inherited, a guest given the property `true` constructed
`CultureInfo("fr-FR")`, while `AppContext.GetData` still reported `true`. Every oracle entry overlays the test host's
environment on its children, so stating the property on both sides does not make them agree on a machine whose
environment sets the variable. Options for PR 3: give `captureAssemblyInPlace` an environment overlay, as
`executeConcurrently` has, and have the fixture pass the variable PawPrint's kernel seeds; clear `DOTNET_*` variables
from every oracle child; or accept the exposure and say so in the fixture. PR 2 builds the function as decision 6
specifies and leaves the choice to the decider.

**Codex findings not addressed here.**

* The oracle cannot tell, in general, a host that refused to start from a guest that exited. `RuntimeConfig.combine`
  refuses the shapes PawPrint models, so the two sides agree on what a launchable runtimeconfig is, but Codex
  measured one it does not model: `APP_PATHS` naming a relative path, without `SetAppPaths`, makes CoreCLR fail to
  start with exit code 137. Teaching `RuntimeConfig.combine` that rule would fix both sides at once.
* `RuntimeConfig.combine` refuses `APP_PATHS` when any spelling of `SetAppPaths` says `true`, although hostpolicy acts
  on whichever spelling it visits last. That conservative reading is `RuntimeConfig`'s documented choice, and the
  oracle refuses exactly what PawPrint refuses.
* Every entry overlays the test host's environment, so a `DOTNET_*` variable there reaches the real runtime: Codex
  measured `DOTNET_STARTUP_HOOKS` being prepended to a supplied `STARTUP_HOOKS`. The globalisation paragraph above is
  the same exposure; clearing such variables would change every entry.
* For an assembly lying directly in the temp directory, the scratch directory exists beside it for a moment before
  the refusal removes it. The refusal needs it to exist, since its random name is what makes the check sound, and no
  guest runs in that window.
* The relative-path test needs a writable working directory; see above.

**Measured.**

* Red, against the old `executeAssemblyInPlace` and a stub `captureAssemblyInPlace` that failed: six of seven tests
  failed. The capturing tests failed at the stub. The `executeAssemblyInPlace` tests of the endings, the command line,
  the echo and the framework passed against the old code, which is the behaviour the wrapper keeps; only the directory
  comparison failed, because the old mechanism replaced the runtimeconfig beside the assembly. Each later test was
  committed red before its fix, with the output in its commit message.
* `TestRealRuntimeInPlace` in the devshell with `TMPDIR=.`: every test passes, and before the scratch path was made
  absolute nine of thirteen failed. With `TMPDIR=<dir>/a/current/..`, where `current` links to `<dir>/b/sub`, every
  oracle call is refused, as intended, and no scratch directory is left in `a` or `b`.
* Which suites: the callers of `executeAssemblyInPlace` (directly, or through `CrossAssemblyHarness` and
  `FabricatedGuest`) are in the default suite and in `Guest` (`TestFSharpPureCases`, `TestDelegateBindFormalSignature`,
  `TestOpcodeFaultsOnPawPrint`); `executeConcurrently`, which the property refusal reaches, has its caller with
  properties in `GuestSolo` (`TestAspNetTwoProcess`). `GuestPure` and `GuestImpure` reach the oracle only through
  `executeWithRealRuntime` and its siblings, which this change leaves alone. All five ran on .NET 10 at the final
  commit, and the default suite and `Guest` on .NET 11.

| suite | .NET 10.0.14 (host) | .NET 11 (`PAWPRINT_TEST_RUNTIME=Net11`) |
|---|---|---|
| default | 5903 total: 5864 passed, 39 skipped | 5903 total: 5875 passed, 28 skipped |
| `Guest` | 234 passed | 234 passed |
| `GuestSolo` | 2 passed | not run |
| `GuestPure` | 1208 passed | not run |
| `GuestImpure` | 304 passed | not run |

Unrelated failures seen on the way, each in code this change does not reach and each passing when run again or at
the next round: `a guest that never reads and never exits is timed out` (a sixty-second wall-clock bound, which took
109 seconds on a loaded machine), an `OutOfMemoryException` in `each runtime's CoreLib declares that runtime's
StartCallback` on .NET 11, and an HTTP timeout in `TestDebuggerServerTrace`'s `pages concatenate to the same run
whatever their sizes` on .NET 11. The first default runs failed 24 tests on each runtime, all in
`TestFrameworkLocatorRatchet`, which flagged the new guest source's `Assembly.Location`; the allowance fixed them.

**Mutation testing.** Each mutant was applied to the committed `RealRuntime.fs` and the file's tests run against it;
where a row says "the code before", the mutant is the code the red commit ran against.

| mutant | tests that failed |
|---|---|
| properties not passed (an empty map written instead) | (a), (b), (c) |
| the runtimeconfig beside the assembly written as well | (b), the framework test |
| stdout not captured (emptied in the result) | six of seven, including (a), (b), (c) and the capture test |
| one shared scratch directory, deleted after each run | five of seven, including (c) |
| one shared scratch file, never deleted | (c), in each of four runs; once also (b) |
| `exec --runtimeconfig` dropped (the scratch file written, not passed) | all seven: the host finds no runtimeconfig |
| working directory moved to the scratch directory | the working-directory test (added for this mutant, which first survived) |
| `executeAssemblyInPlace` does not echo | the command-line test and the framework test |
| scratch directory not removed | none: survives |
| host-owned names not refused (the code before) | the three host-owned cases, and `executeConcurrently`'s |
| ill-formed UTF-16 not refused (the code before) | the lone-surrogate test |
| relative path not made absolute (the code before) | the relative-path test (`NormalExit 129`, the host's) |
| `..` not refused | the `..` test (the decoy ran) |
| temp-directory refusal removed | the four temp-directory tests |
| temp-directory refusal comparing spellings (the code before) | the symbolic-link, linked-file and other-case tests (each `NormalExit 0`) |
| scratch directory not made absolute, with `TMPDIR=.` (the code before) | nine of thirteen |

The surviving mutant survives because no test can see the scratch directory: other tests create and remove their own
beside it at the same moment, so a listing of the temp directory before and after a run cannot tell whose is whose.
The scratch directories of `executeWithTimeoutAndSeed` and `executeConcurrently` are untested in the same way.
