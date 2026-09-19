# Preserving the AL handler's stack through BC's `InvokeHandler` (#3500)

The derivation behind the one-instruction Cecil rewrite in
`AlRunner/Infrastructure/NclCecilRewrite.Runtime.cs` and the helper it calls,
`AlRunner/Patches/HandlerStackPreservation.cs`.

## <a name="the-defect"></a>The defect

`Microsoft.Dynamics.Nav.Runtime.NavTestExecution.InvokeHandler` is the single frame every AL
`[HandlerFunctions]` callback is invoked through — `[ModalPageHandler]`, `[MessageHandler]`,
`[ConfirmHandler]`, `[RequestPageHandler]` and the rest. BC's body:

```csharp
private object InvokeHandler(MethodInfo handler, params object[] parameters)
{
    try
    {
        if (handler.IsAwaitable(out var isGenericTask))
            return InvokeHandlerAsync(handler, isGenericTask, parameters).GetAwaiter().GetResult();
        return handler.Invoke(executingTestCodeUnit, parameters);
    }
    catch (TargetInvocationException ex)
    {
        throw ex.GetBaseException();
    }
}
```

`throw <an existing exception object>` resets that object's stack trace. Every frame the AL
handler was actually raised in is discarded and the reported origin becomes `InvokeHandler`
itself — a frame at which nothing was raised.

Measured on Microsoft BaseApp surface run `34169134540` (BC 28.4.53241.54387, runner head
`7d077ccc`): **126 failures** with that top frame, of which 68 `NullReferenceException`s had no
attributable origin at all, spread across Tests-SCM (29), Tests-ERM (20), Tests-VAT (8),
Tests-General Journal (4) and five more buckets.

## <a name="binaries-verified"></a>Which binaries this was verified against

The version label does not identify the binary (`CLAUDE.md` § 2c), so the premise was checked
against every **distinct** `Microsoft.Dynamics.Nav.Ncl.dll` provisioned on the development box
— six files across the thirteen version directories — by reading
`NavTestExecution.InvokeHandler`'s IL body through `System.Reflection.Metadata`, resolving the
method by name because metadata tokens are not stable across binaries.

| sha256 (first 8) | version directories carrying it | IL length | exception regions | handler tail |
|---|---|---:|---|---|
| `affa03c9` | 27.0.38460.53934, 27.3.44313.53909, 27.5.46862.53931 | 59 | 1 catch, `TargetInvocationException`, `[0x0033+6]` | `6F <tok> 7A` |
| `0a6ce45e` | 27.5.46862.48827 | 59 | same | `6F <tok> 7A` |
| `49b11d9b` | 28.1.49838.53910 | 59 | same | `6F <tok> 7A` |
| `6f2cf682` | 28.0.46665.54338, 28.1.49838.54308, 28.2.50931.54319, 28.3.52162.54347, 28.4.53241.54346 | 59 | same | `6F <tok> 7A` |
| `108b8c6b` | 28.4.53241.54407 | 59 | same | `6F <tok> 7A` |
| `cfe5e3b2` | 28.4.53241.54447 | 59 | same | `6F <tok> 7A` |

`6F <tok> 7A` is `callvirt <Exception::GetBaseException>; throw` — the two instructions that
make up the whole catch handler. The per-file IL hashes differ only because the embedded method
tokens differ; the instruction sequence and the handler region are identical in all six.

Three of the six are also loaded as `bc-decompiler` contexts (`bc270` = `affa03c9`,
`bc281` = `49b11d9b`, `bc284` = `6f2cf682`), and `compare_symbols` over
`NavTestExecution.InvokeHandler` in `body` mode reports `bodyChanged: false` between each pair.

## <a name="why-one-instruction"></a>Why one instruction rather than the whole body

`InvokeHandler` calls the **private** `NavTestExecution.InvokeHandlerAsync` and reads the
**private** field `executingTestCodeUnit`, so a `ReplaceBodyWithHelper` would have to
re-implement both reflectively — more code, and a standing risk of drifting from whatever BC's
own body does next.

It is also unnecessary. Both invocation paths funnel their `TargetInvocationException` into the
one catch arm:

- the **direct** path throws it from `MethodBase.Invoke`;
- the **awaitable** path's `InvokeHandlerAsync` calls `handler.Invoke` as its own first
  statement, so a synchronous throw faults the returned `Task`, and
  `.GetAwaiter().GetResult()` on a faulted `Task` rethrows the single inner exception rather
  than an `AggregateException`.

So substituting the arm's `callvirt GetBaseException` for a call to
`HandlerStackPreservation.RethrowPreservingStack` covers both paths at once and leaves BC's try
block, its two `leave` targets and its exception-handler region untouched.

The substitution is an in-place `ILProcessor.Replace`, not an insert-plus-remove pair, so every
branch and handler boundary that pointed at the old instruction keeps pointing at the new one.

## <a name="stack-shape"></a>What a preserved trace shows

Measured with a probe raising a `DivideByZeroException` three frames deep inside a stand-in
handler, caught exactly as `InvokeHandler` catches it, and rethrown both ways.

**Before** — BC's `throw ex.GetBaseException()`, one frame, naming the rethrow site:

```
System.DivideByZeroException: Attempted to divide by zero.
   at <the rethrow site>
```

**After** — `RethrowPreservingStack`, six frames:

```
System.DivideByZeroException: Attempted to divide by zero.
   at <handler frame 3>
   at <handler frame 2>
   at <handler frame 1>
   at InvokeStub_<handler>(Object, Object, IntPtr*)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
--- End of stack trace from previous location ---
   at AlRunner.Patches.HandlerStackPreservation.RethrowPreservingStack(Exception ex)
   at <the rethrow site>
```

Note what the `--- End of stack trace from previous location ---` marker means for a reader:
everything BC's version reported is still there, **below** the marker. The change is additive —
the frames that name the origin are prepended, and nothing that was visible before is removed.

## <a name="not-a-behaviour-fix"></a>This is diagnostic, not a behaviour fix

None of the 126 failures starts passing. The same exception **object** — same reference, same
runtime type, same message, same `Data` and `InnerException` chain — still propagates to the
same caller, and `ExceptionDispatchInfo.Throw()` raises from the same frame BC's `throw` raised
from, so every enclosing handler still sees it. What changes is that the trace it carries names
where it was raised, so 126 currently unattributable failures become triageable.

## <a name="where-the-tests-live"></a>Where the proving tests live, and why not the corpus

`AlRunner.Tests/HandlerRethrowStackPreservationTests.cs`, twelve tests in three groups: the IL
that the Cecil pass emitted into the loaded Ncl image, the behaviour of the helper against a
control measuring BC's own construct, and the locator's scoping against constructed bodies.

They are **runner-internal** claims. A stack trace's contents is a runner diagnostic rather than
an AL-observable value: AL's own error surface exposes no reading of a .NET frame list, so there
is no assertion a corpus test on a real service tier could make that would distinguish the two
constructs. What *is* a BC claim here — that `InvokeHandler` catches
`TargetInvocationException` and rethrows its base exception — is not being changed and is read
directly out of the shipped binaries above rather than asserted.
