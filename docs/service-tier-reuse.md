# Fixing runner gaps by reusing the service tier

This document records a deliberate strategy for closing AL Runner gaps: **reuse
Business Central's own service-tier code, patching the runtime engine where it
assumes Windows or a live server, instead of hand-faking behaviour.** It also
records the concrete proof that this works on Linux, and the boundaries of the
approach.

## The principle

The runner already loads BC's shipped DLLs and executes real MS/ISV business
logic (see `.claude/rules/precompiled-dll-respect.md`). Two layers are ours to
modify freely: the **runtime engine** (`Microsoft.Dynamics.Nav.Ncl.dll`,
`...Types.dll`, `NavEnvironment`, dispatchers) and the **skeleton state** those
methods read. Only AL-business-logic DLL *bodies* are off-limits.

So when a gap appears, the preferred fix order is:

1. **Reuse** the real service-tier code path that already implements the
   behaviour, patching only the runtime-engine plumbing that blocks it headless
   (Windows ACL calls, service-install paths, SQL bootstraps).
2. Failing that, **populate the skeleton state** the real code reads.
3. Hand-faking a behaviour is the last resort — every fake is a future gap.

This is the opposite of the early instinct ("reimplement what BC does"). BC's
implementation is the reference; we make it run, we don't rewrite it.

## Proven: BC's compiler runs headless on Linux (spike round 2)

Branch `v2-spike-servicetier-compile` (`spike/servicetier-compile/`) proves the
full chain **in-process on Linux, no container, no SQL**:

```
AL source → NavCA.Compilation.Emit()            → C#
          → CSharpCompiler.CompileCSharpFilesAsync() → IL DLL
          → Assembly.Load() → invoke method → returns the expected value
```

The earlier "NO-GO" conclusion (BC's `CSharpCompiler` is "Windows-only") was
**wrong**: the Windows-ACL throw is in runtime-engine code we are allowed to
patch, and SQL is not on the compile path when a pre-built `Compilation` is
supplied. The decisive evidence was that a real BC service tier compiles AL on
Linux today by patching these same runtime DLLs (see reference below).

### The four patches that unblock it

Lifted from the bc-linux reference (see below), implemented in
`AlRunner/Patches/CompilePatches.cs`:

| Patch | Source (bc-linux) | What it does | Why faithful |
|---|---|---|---|
| **#9** Topology proxy | `StartupHook.cs` | `IsServiceRunningInLocalEnvironment = false` | gates ACL only; no emission impact |
| **#2b** TempPathHelper redirect | new (bc-linux runs as root) | `InitializeFolders()` → `/tmp/bc-alrunner/<guid>/` | scratch space only |
| **#14** Cecil type-forwarding | `StartupHook.cs` | `IsTypeForwardingCircular → false` | .NET guarantees finite chains |
| **#15** Assembly-probing filter | `StartupHook.cs` | exclude empty-path byte-loaded assemblies | empty paths are never valid candidates |

**Faithfulness rule:** no patch may change the *emitted IL*. bc-linux maintains a
`_disabledPatches` set of patches known to cause "AL→C# emission drift" — those
are forbidden here. A no-op'd Windows directory-ACL call is faithful because it
only governs Windows file permissions, which are irrelevant to compile output.

### Status / caveat

Proven on a **trivial synthetic app**. Compiling a **real app** (RecoverySolutions)
through `RecompileFullPackage` still fails with `NavTypeKind None` /
`ConversionKind NoConversion` emit errors — a **symbol-closure / Compilation
construction** gap (our code), not a platform gap. Closing it (build the real
`Compilation` via `BcCompiler`'s no-SQL reference path) is the next step before
this can replace `BcAssembler`.

## Reference implementation: bc-linux

`StefanMaron/MsDyn365Bc.On.Linux` (locally `community/bc-linux`) runs BC's
service tier + AL compiler **natively** on Linux (not Wine) by patching the same
artifact DLLs we load. It is the answer key for "what must be patched":

- `src/StartupHook/StartupHook.cs` — a numbered list of runtime patches applied
  via `DOTNET_STARTUP_HOOKS`. Patch **#2** (NavEnvironment `.cctor` without
  `WindowsIdentity`), **#5** (ETW/OpenTelemetry), **#3** (kernel32 P/Invoke), and
  `SetupStubWithResolver("System.Security.Principal.Windows")` are the
  compile-relevant ones.
- The `_disabledPatches` set + its emission-drift comment — authoritative list of
  patches that change AL→C# output (do not apply those).
- `src/tools/PatchNclTestPage/PatchNavTypes.cs` — Cecil patches.
- `src/stubs/WindowsPrincipalStub/` — the SSPW stub.

When a new headless-on-Linux blocker appears, check bc-linux first.

## Two directions this opens

1. **Replace the compile half.** Swap `BcAssembler`'s Roslyn C#→IL step (and its
   `CallSiteArgWrap`/polyfill plumbing) for BC's own `CSharpCompiler` via
   `BcRuntime.EnsureCompilePatches()`. Future-proof: BC's compiler is complete by
   definition, so "can we compile feature X?" is answered once, for all of AL,
   including Base-Application scale. Gated on closing the symbol-closure caveat.
2. **Shrink runtime-skeleton gaps.** Most of the 125 corpus failures and the RS
   `NCLMetaTable` gaps are places where our hand-built fake of session / metadata
   / company state diverges from real BC. Booting more of BC's *real* (patched)
   initialization reduces those by using real code instead of fakes.

## Runtime packages (ISV)

For an ISV `.app` whose payload is compiled IL (a *runtime package*: no
`src/*.al`, no R2R `publishedartifacts/*.dll`),
`NavAppPackageCompiler.ExtractEmittedContent(stream) → byte[]` extracts the
compiled DLL **without SQL or `CSharpCompiler`**. The runner has no detection or
extraction case for this yet — a clean future addition.

## Hard boundary: the data layer stays ours

This strategy applies to **compilation** and to **runtime-engine plumbing**. It
does **not** extend to running BC's data layer: records, FlowFields, posting, and
`Insert/Modify/Find` all require SQL, which the runner deliberately replaces with
an in-memory provider (`RecordPatches`, the skeleton `NavDatabase`). That
substitution is the whole point of the runner — SQL-free execution — and is *not*
something to delete in favour of "just run the service tier." Compile is
replaceable; the in-memory data plumbing is the crown jewel.

## See also

- `.claude/rules/precompiled-dll-respect.md` — what we may/may not modify.
- `.claude/rules/loud-failures.md` — the faithfulness/audit obligation for patches.
- Spike: branch `v2-spike-servicetier-compile`, `spike/servicetier-compile/FINDINGS.md`.
