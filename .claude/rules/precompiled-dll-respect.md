# Precompiled-DLL respect (the load-chain contract)

The runner exists so AL test code can run against **unmodified** MS-AL-compiled
DLLs (`Microsoft.Dynamics.Nav.SystemApplication.dll`,
`Microsoft.Dynamics.Nav.BaseApplication.dll`, etc., shipped inside MS `.app`
files) and ISV-AL-compiled DLLs (third-party extensions). The whole point is
that integration tests can exercise **real MS / ISV business logic** without us
re-implementing or re-compiling it. v1 spent enormous effort trying to compile
those DLLs ourselves and failing; v2 sidesteps that by accepting them as-is.

Therefore the only hard constraint is: **the public type surface and method
bodies of any precompiled AL-business-logic DLL must behave exactly as they did
when MS/the ISV compiled them**. Everything outside that — the runtime engine
that invokes those bodies, the dispatch infrastructure, the framework wrappers,
the skeleton state they read from — is ours to modify however we need.

## What's allowed (use freely)

| Layer | Examples | Modify? |
|---|---|---|
| **Runtime engine / framework** | `Microsoft.Dynamics.Nav.Ncl.dll`, `Microsoft.Dynamics.Nav.Types.dll` | ✓ Cecil-rewrite (the live mechanism), subclass, field-poke, EventPipe — anything. The legacy JmpHook layer is off by default; a new `Hook(...)` call site is a silent no-op. |
| **Skeleton state** their methods read | `NavSession`, `NavMethodScope`, threadlocals, etc. | ✓ Populate any fields we need |
| **Our AL test output, freshly emitted** | DLLs emitted by our `Compilation.Emit` pipeline, in-process, not yet written to a cache | ✓ Modify only as part of the **compile pipeline** (Roslyn rewriters, Cecil passes that run before the DLL is finalised). Once finalised the same precompiled-DLL contract applies — see below. |
| **New types we add** | subclasses of MS types, runner shims | ✓ Add as long as nothing renames an existing type |

## What's forbidden

| Action | Why |
|---|---|
| **Rewriting method bodies in MS-AL or ISV-AL business-logic DLLs** | Defeats the whole point — those bodies *are* the business logic the integration tests exist to validate |
| **Renaming or removing types/members in any precompiled DLL** | Breaks linking for every other precompiled DLL in the load chain (R2R native code holds offsets, not just names) |
| **Changing method signatures of methods called from precompiled DLLs** | Same |
| **Reordering instance fields in a layout-sensitive way** | R2R-precompiled callers hold pointer offsets |

## Our AL output is meant to be cacheable

A first-class capability requirement: **AL code we compile must produce DLLs
that can be cached on disk and reused on subsequent runs the same way MS's
precompiled DLLs are reused.** That means once our compile pipeline finalises
a DLL, that DLL becomes part of the precompiled load chain like any MS or ISV
DLL — and the rules above apply to it too. Cecil/Roslyn rewrites must happen
**inside the compile pipeline** (before the DLL is written), never as a
load-time pass against an already-cached artifact, otherwise the cache and
the runtime behaviour diverge.

Practical consequence: if a test failure points at our own AL output, the
fix is either (a) in the runtime engine the output calls into, or (b) in
the compile pipeline that produced the output — never in the cached DLL
itself.

## Mental model

> AL business-logic semantics (as the AL author wrote them) are the contract.
> Everything else — async wrappers, dispatcher infrastructure, framework
> plumbing, calling-convention machinery — is implementation detail we control.
> If a test fails, the answer is **always** "fix runtime/framework code,"
> never "patch the AL business logic."

## Concrete guidance for new patches

When you find a method that NREs / misbehaves on the skeleton runtime:

1. **Is it inside an AL-business-logic DLL** (`*.SystemApplication.dll`,
   `*.BaseApplication.dll`, an ISV extension)? **Stop. Don't touch its body.**
   The fix is upstream — in the framework method it calls into, or in the
   skeleton state it reads. Find that and patch it.

2. **Is it inside the runtime engine** (`Ncl.dll`, `Types.dll`, dispatchers,
   wrappers)? Anything goes — Cecil, EventPipe, subclass, field-poke, etc.
   Pick the cheapest mechanism that's faithful to the AL-observable
   semantics. **Not JmpHook** — that layer is disabled by default, so a hook
   with no Cecil owner ships as a silent no-op (`AL_RUNNER_HOOK_AUDIT=1` lists them).

3. **Is it our own AL output?** We can rewrite freely, but in practice the
   right fix for our output is almost always a runtime-engine patch instead,
   because the same fix then also helps integration tests of MS/ISV code.
