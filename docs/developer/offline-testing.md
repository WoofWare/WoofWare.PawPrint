# Building and testing offline

The default devshell (`nix develop`) restores NuGet packages from nuget.org as usual.
In a sandbox with no network, or to maximise caching, you can use the `offline` devshell instead.

```bash
nix develop .#offline -c dotnet test WoofWare.PawPrint.Test/WoofWare.PawPrint.Test.fsproj
```

It resolves NuGet packages and SDK packs only from the Nix store, including self-contained publishing packs for `linux-x64`, `linux-arm64` and `osx-arm64`.
Nix may need network access to populate the store on first use, but once its closure is present, no further work (restore, build, test) needs to hit nuget.org.
NuGet then uses a gitignored `packages/` directory at the root of the checkout for its local writable package cache.

.NET local tools use their own NuGet configuration.
Bootstrap the manifest's tools from the offline shell's installable feed before first run:

```bash
nix develop .#offline -c sh -c 'dotnet tool restore --configfile "$PAWPRINT_TOOL_NUGET_CONFIG"'
nix develop .#offline -c dotnet fantomas .
```

Packages can't be restored offline via Nix unless they're pinned in `nix/deps.json`, so after changing package references, you must regenerate the pins from an environment with network access:

```bash
nix build .#default.fetch-deps && ./result nix/deps.json
```

`nix flake check` verifies that the whole solution and the local tools restore offline with empty package, tool and HTTP caches, that both tools start, and that `CSharpExample` publishes as a self-contained application for each supported RID.

