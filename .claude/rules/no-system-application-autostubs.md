---
paths:
  - "AlRunner/StubGenerator.cs"
  - "AlRunner/RoslynRewriter.cs"
  - "AlRunner/stubs/**"
  - "AlRunner/Runtime/**"
---

# Do not ship real implementations of System Application codeunits

Auto-generating **blank shells** for every codeunit/object pulled in from dependencies is normal — that is how the runner avoids needing the full System Application as a compile dependency. Blank shells are fine; they let AL compile and the test exercises whatever the user actually wrote.

What is **forbidden** is shipping a *real implementation* of a System Application codeunit inside the runner — i.e. AL or C# code that actually does the work of an SA codeunit (Image processing, File Mgt., Cryptography, Email, Document Sharing, Web Service Mgt., …) so that AL calling into it gets a "working" answer without the developer providing one.

**The only exceptions are test-automation libraries** that ship with the runner because the whole point of the runner is to execute tests. The approved list is maintained in `docs/limitations.md` "System Application codeunits — scope policy" (always check there for the definitive list):

| Codeunit ID | Name | File |
|---|---|---|
| 130 | `"Assert"` (Library Assert) | `AlRunner/stubs/LibraryAssert.al` + `AlRunner/Runtime/MockAssert.cs` |
| 131 | `"Library Assert"` (alias) | `AlRunner/stubs/Assert.al` |
| 130000 / 130002 | BC test toolkit aliases | routing only, no extra file |
| 131004 | `"Library - Variable Storage"` | `AlRunner/stubs/LibraryVariableStorage.al` + `AlRunner/Runtime/MockVariableStorage.cs` |
| 130440 | `"Library - Random"` | `AlRunner/stubs/LibraryRandom.al` (pure AL) |
| 130500 | `"Any"` | `AlRunner/stubs/LibraryAny.al` (pure AL) |
| 131003 | `"Library - Utility"` | `AlRunner/stubs/LibraryUtility.al` (pure AL) |
| 132250 | `"Library - Test Initialize"` | `AlRunner/stubs/LibraryTestInitialize.al` (event publishers only) |
| 131100 | `"AL Runner Config"` | `AlRunner/stubs/AlRunnerConfig.al` (runner-only) |

Adding a new entry is a high bar: it must be a *test-automation* library (something a test codeunit uses to assert / orchestrate), not a piece of business logic.

**What this rule blocks:**
- A new AL file in `AlRunner/stubs/` that implements an SA business-logic codeunit (e.g. an Image, Cryptography, File Mgt. real implementation).
- A new C# class in `AlRunner/Runtime/` that re-creates SA business behavior (e.g. parsing image headers, computing hashes, encoding base64 the way SA does) and exposes it as the answer to a real SA call.
- A `RoslynRewriter.cs` change that maps an SA codeunit call to a runner-supplied real implementation rather than to the auto-generated blank shell.

**What this rule does NOT block:**
- Auto-generating blank shells for SA codeunits via `StubGenerator.cs` — that is the runner's normal operating mode.
- Implementing **runtime primitives** that AL itself depends on (record store, scope, dialog, FieldRef, etc.) — those are not SA codeunits.
- Implementing **test handlers / mock infrastructure** (HandlerRegistry, MockTestPageHandle, MockNotification, …) — these exist so tests can drive AL, not so AL gets free SA behavior.

**Why:** the moment the runner starts shipping real SA implementations, it inherits the burden of keeping them faithful to the actual System Application across every BC version. AL that depends on real SA behavior compiles and runs green against the runner's reimplementation, and the developer has no idea their test is no longer testing what BC will do. The runner's contract is "I can compile and run any AL that does not need a service tier" — it is not "I am a re-implementation of the System Application."

**If you hit a case where blank shells aren't enough** (the AL under test really needs SA behavior to mean anything), the answer is to file a runner-gap issue describing the AL pattern and let the design conversation happen — not to silently land a real implementation.
