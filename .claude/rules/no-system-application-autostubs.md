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

**The only exceptions are test-automation libraries** that ship with the runner because the whole point of the runner is to execute tests. Currently:
- `AlRunner/stubs/LibraryAssert.al` — codeunit 130
- `AlRunner/stubs/LibraryVariableStorage.al` — codeunit 131004

Adding a new file here is a high bar: it must be a *test-automation* library (something a test codeunit uses to assert / orchestrate), not a piece of business logic.

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
