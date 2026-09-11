# Why the runner does not reuse `TestPageClient.dll` (and what was false about the old reason)

Microsoft ships `Microsoft.Dynamics.Nav.Client.TestPageClient.dll`, its own server-side
`TestPage` harness, in every BC artifact directory. `precompiled-dll-respect.md` §
"Reuse before you re-implement" says a shim needs Microsoft not to ship the component
first — so this page records what was measured when that question was finally asked of
`RunnerPageInstance` / `MockTestPage`, and what the answer turned out to be.

**Short version: the DLL is present, it loads, and it runs — but it resolves fields only
through a control tree, and for a precompiled page the runner synthesizes none. That, not
absence, is why the shim exists.** (#3799)

## What was written down before, and why it was wrong

Two comments recorded the DLL as absent:

| file | claim |
|---|---|
| `AlRunner/Patches/RunnerTestClientSession.cs` | "the TestPageClient, which does not exist in the runner" |
| `AlRunner/Infrastructure/NclCecilRewrite.Forms.cs` | "tries to load … TestPageClient — not present in the runner" |

Both are false, and the second was wrong twice over: `TestClientProxy<T>.Proxy` does not
load that assembly at all. It is a `System.Reflection.DispatchProxy` defined in
`Microsoft.Dynamics.Nav.Types.dll`, which contains **no** `"Microsoft.Dynamics.Nav.Client.TestPageClient"`
string literal — it wraps its argument in a dispatcher that needs an initialized
`UISessionManager`. The two neighbouring comments in that same file (steps 3 and 4) had
the reason right the whole time: *"The UISessionManager was expected to be initialized."*

## The swallowed load failure

`NavTestExecution.CreateTestClientSession` is the load site, and BC's own body explains how
a real failure became a false absence:

```csharp
try {
    string fullName = Assembly.GetExecutingAssembly().FullName;
    Assembly assembly = Assembly.Load("Microsoft.Dynamics.Nav.Client.TestPageClient"
                                      + fullName.Substring(fullName.IndexOf(',')));
    ...
}
catch (FileNotFoundException) { }
throw new NavTestTestClientNotInstalledException();
```

It catches `FileNotFoundException` **only**, and then throws an exception whose *name*
asserts the client is not installed. So any failure to reach the type — including a missing
transitive dependency — surfaces as "test client not installed". The exception name was read
as a diagnosis, and the diagnosis was written into the comments.

**A strong-name mismatch, a missing transitive dependency, and a genuine absence are three
different answers with three different remedies.** Here it was the second, never the third.

## What was measured

Driving BC's exact `Assembly.Load` string under the runner's own resolver shape
(`DependencyLoader.EnsureResolverInstalled`, which serves any `Microsoft.Dynamics.*` request
from the artifact directory by filename probe):

| BC version | `TestPageClient.dll` | `Assembly.Load` | `TestPageClientSession.Create` |
|---|---|---|---|
| 27.0.38460.53934 | present | **OK**, `Version=27.0.0.0, PublicKeyToken=31bf3856ad364e35` | resolves, 6 args |
| 27.5.46862.53931 | present | OK | resolves |
| 28.1.49838.53910 | present | **OK**, `Version=28.0.0.0` | resolves, 6 args |
| 28.4.53241.54346 | present | **OK**, `Version=28.0.0.0` | resolves, 6 args |

Strong-name binding was never the problem — the requested and actual identities match
exactly. `TestPageClient.dll` is present in all nine provisioned artifact directories
(27.0, 27.3, 27.5 ×2, 28.0, 28.1, 28.2, 28.3, 28.4).

### The one failure, and it is a provisioning artifact

`27.5.46862.48827` loads the assembly but `GetType(..., throwOnError: true)` fails:

```
FileNotFoundException: Could not load file or assembly
  'Microsoft.Dynamics.Framework.UI, Version=27.0.0.0, PublicKeyToken=31bf3856ad364e35'
```

That directory is missing `Microsoft.Dynamics.Framework.UI.dll`; its sibling
`27.5.46862.53931` has it, as do the other eight. So this is an incomplete artifact
directory, not a version-shape difference — and it is precisely the **missing transitive
dependency** case BC's `catch (FileNotFoundException)` converts into "not installed".

## Why reuse still does not follow

The blocker is in BC's proxy design, and it is measured (issue #3799, spike comments):

`TestPageProxy.GetField(id)` searches the control tree **by name** —
`form.ContainedControls.FindAll(c => c.Name != null && string.Compare(c.Name, s, ...) == 0)`
— and returns `null` when nothing matches. `TestFieldProxy` holds only a `LogicalControl` /
`RepeaterControl`; `Value` is `Control.StringValue`. **There is no record-side fallback
anywhere in the assembly.**

So the whole field / part / action surface depends on a control tree, and:

| page | kind | control nodes | data controls | `GetField` |
|---|---|---|---|---|
| 70601 (fixture) | source-compiled | 50 | 9 | real `TestFieldProxy` |
| 46 Sales Order Subform | precompiled | 16 | **0** | **null** |
| 21 (Aged Acc. Receivable) | precompiled | 8 | **0** | **null** |

For a precompiled page the runner deliberately synthesizes no control tree —
`AlRunner/Patches/DependencyPageMetadataXml.cs` says so in its own header, and gives the
reason: `SourceExpression` in `SymbolReference.json` is AL text (`Rec."No."`), not the
compiled field-number `DataColumnName` the real metadata XML carries, so reconstructing one
would add guessed data with no way to prove it faithful. BC's `PageBuilder` builds exactly
what the metadata declares: no controls declared, no controls built, no `Editable` to
resolve.

**The open work is entirely on precompiled surfaces** (#3504, #2460, #3825), so adopting MS's
proxy would cover only source-compiled pages while `MockTestPage` + `RunnerPageInstance` stay
in full for the precompiled path — strictly more code, two behaviours to keep consistent, and
divergent AL-observable messages. That is the measured basis on which the shim stands.

## What is still unproven

- Whether the `Editable`/`Enabled` dominating-value resolution could be reused for
  **source-compiled** pages alone. It works there (BC evaluated a page-variable-backed AL
  expression and changed its answer), but see the paragraph above on why partial adoption
  is a net loss.
- Every field answered `Editable=False` on the source-compiled page including one declared
  editable, consistent with `RuntimeEditable=False` in View mode but not confirmed in Edit
  mode.
- A write succeeded on a field declared `Editable = false`. Unresolved.

## Reproducing the load measurement

The load half of this page is a dozen lines: install a `Resolving` handler that probes the
artifact directory by filename, `Assembly.LoadFrom` Ncl to get its identity, then run BC's
own concatenation — `"Microsoft.Dynamics.Nav.Client.TestPageClient" + fullName.Substring(fullName.IndexOf(','))`
— through `Assembly.Load`, and call `GetType("…TestPageClientSession", throwOnError: true)`.
Pointing it at a directory missing `Framework.UI.dll` reproduces the
`FileNotFoundException` that BC swallows.
