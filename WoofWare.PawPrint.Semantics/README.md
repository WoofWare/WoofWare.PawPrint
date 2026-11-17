# WoofWare.PawPrint.Semantics

Rules of the CLI execution model, as inspectable data: tables of "what does this IL opcode do?".

Treat this as unstable; it's pre-1.0 because even the overall structure of this library isn't decided yet.

The point is that static analysis tooling needs to be able to compute with descriptions of what operations do, so this is pulled out of the WoofWare.PawPrint interpreter and stored as data.

## Currently modelled

* the IL execution model: what each instruction pops, pushes and raises by itself, where control
  goes after it, and the shape of the evaluation stack on entry to it
* what CoreCLR's JIT does with a body: what it compiles, which types it loads where, which
  signatures it refuses, and which instructions it folds away
* the methods CoreCLR implements itself rather than in CoreLib's IL: intrinsics, FCalls, QCalls,
  P/Invokes into the framework's own native libraries, and the VM's substitute bodies, with what
  each can raise; and the CoreLib fields the VM binds by name or lays out as one native object,
  and the CoreLib classes it reaches through a C++ layout of its own
* the platform a guest sees: the supported runtimes, the virtual CPU's hardware intrinsics, and how
  likely it is to matter whether an instruction is interleaved with another thread's instructions.

Most tables are checked by `WoofWare.PawPrint.Test` against the pinned runtime source, real .NET, or both.

