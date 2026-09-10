# History — precompiled-dll-respect

## "Reuse before you re-implement" (2026-09-10)

### What was measured

An investigation traced the page-metadata chain end to end, asking of each link: is this
Microsoft's code or ours?

| # | link | owner |
|---|---|---|
| 1 | symbol file → `PageSymbol` | ours — legitimate, BC ships no `SymbolReference.json` reader |
| 2 | `EmitPageXml` → document | ours — legitimate, BC emits only at publish time, which the runner never performs |
| 3 | document → `MetaPageDefinition` | **BC's** — `RunnerXmlMetadataLoader` implements BC's `INCLObjectXmlMetadataLoader`; the parse is `NCLMetaForm.LoadMetadata()` |
| 4 | live page init | **BC's** — `SetSourceTable` → `EnsureMetadataLoaded` → `InitializeFromMetadata` |
| 5 | definition → boolean | **ours** — `RunnerPageInstance`, ~9,246 lines |
| 6 | AL observes | **BC's** — `NavTestField.ALEditable` → `ITestField` |

Link 5 re-implements `LogicalControl.Editable` → `CommonDominatingValueHelper.CalculateValue`.

BC's **server** tier genuinely has no method answering "is this control editable on this live
page": `ControlDefinition.Editable` is a raw string, and `PropertyHelper.IsDynamic` handles
literals and explicitly gives up otherwise. So a shim was not unreasonable on its face.

But the computation ships on disk. `Microsoft.Dynamics.Nav.Client.TestPageClient.dll` and
`Microsoft.Dynamics.Framework.UI.dll` are present in every artifact directory the project holds
— verified at 27.0, 27.5, 28.1 and 28.4, all net8.0, no WinForms. And it is not a wire proxy:

- `TestServiceConnection.CallServer<T>(f) => f()` — a direct invocation
- `ServiceUrl` is a deliberately fake `"localhost/bla"`
- the dispatcher's `Invoke` throws `NotImplementedException`

Nothing is ever marshalled. It is BC's own server-side test harness.

### How it happened, which is the part worth remembering

Nobody decided to re-implement it. A strong-name `Assembly.Load` in
`NavTestExecution.CreateTestClientSession` failed, and the failure was **swallowed**. A shim
filled the gap. The shim's comments then recorded the DLL as *"does not exist in the runner"* /
*"not present in the runner"* — including `AlRunner/Infrastructure/NclCecilRewrite.Forms.cs:618`
— which is false as written, and gave every later reader a documented reason not to look again.

A silent failure became a workaround; the workaround became an assumption; the assumption was
written down as fact.

### Why the rule permitted it

The `What's allowed` table's fourth row reads *"New types we add — subclasses of MS types,
runner shims ✓"*, with no obligation to first check whether Microsoft ships the component. The
letter of the rule allowed 9,246 lines. The repository owner stated the intent it was missing:

> My understanding of fixing something is not necessarily reimplementing, because the outcome
> has to be fixed. How we get there is not defined. So reusing something existing that just
> works is always the better approach, especially when in this case it comes from Microsoft.

### What is still unproven

Whether `TestPageClient` **initializes headless** is a spike, not a conclusion. The disk facts
and the not-a-proxy facts are measured; the initialization is not. If the spike fails, the
parallel implementation is justified — and the correct follow-up is then to fix the two false
comments to state the *real* reason, so the question is not re-litigated from a false premise.

### Consequences beyond this rule

- Two source comments are wrong and mislead future work.
- `#3784` (page properties read from the symbol file) improves document fidelity the
  equivalence harness measures, but turns **no test green**: `Extensible` has no consumer, and
  BC's own `Page Metadata` table (2000000138) has no such column.
- Both measured failure clusters behind page metadata are `Assert.IsFalse` — #2460's 6
  ("Action Back must be disabled") and #3504's 114 ("should not be editable") — which is what
  an unconditional `true` predicts. `Assert.IsTrue` passes vacuously, and that count is
  unmeasured.
