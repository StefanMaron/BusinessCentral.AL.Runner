# TestPage field lookups

`TestPage."Field".Lookup()` reaches BC's `NavTestField.ALLookup`, whose whole body is
`CheckError(() => testField.Lookup())` — so `ITestField.Lookup`, which this runner implements,
is the dispatch surface. From there the runner has to answer the question AL itself answers in
three different places.

## The three routes

AL spells a lookup three ways. They are not overloads of one another and nothing in the syntax
connects them; which one applies is decided in this order.

| # | where the lookup is declared | shape | who runs it |
|---|---|---|---|
| 1 | the **page control** | `trigger OnLookup(var Text: Text): Boolean` | `RunnerPageInstance.RaiseOnLookup` |
| 2 | the **source table field** | `trigger OnLookup()` — parameterless, writes into `Rec` | `RaiseSourceFieldOnLookup` → `NavRecord.LookupAsync` |
| 3 | the field's **`TableRelation`** | no trigger anywhere; the relation names a table | `RaiseTableRelationLookup` (#3518) |

Routes 1 and 2 landed with #2549, which deliberately left route 3 refusing. Route 3 landed with
#3518 and is the subject of the rest of this page.

The two triggers differ in more than shape. The control's returns a Boolean — that is how "the
user cancelled" is expressed — and the text it wrote back replaces the field's value. The table
field's is parameterless and writes into `Rec` itself, so there is nothing to hand back and
nothing to gate on. Corpus codeunit 60316 `TFL Tests` pins both, including that a control
trigger wins over a table-field one.

## TableRelation lookups

`AlRunner/Patches/RunnerPageInstance.RelationLookup.cs`.

Almost none of this route is runner logic. Both halves ship in `Microsoft.Dynamics.Nav.Ncl.dll`
and are called rather than reimplemented:

```
Microsoft.Dynamics.Nav.Runtime.Autofill.AutofillHelper
    .GetRelationForField(control, rec, field, out controlRelation, out tableRelation,
                         out relatedTable, out relatedFieldId)
    .ApplyRelationFilters(rec, relatedRec, relation, controlRelation)

Microsoft.Dynamics.Nav.Runtime.NavForm
    .RunModalAsync(bool isInLookupTrigger, bool isLookup, int formId, NavRecord record, int fieldNo)
```

What the runner adds is the join between them: build a record of the related table, let
Microsoft's helper filter it, run Microsoft's lookup-mode `RunModal` on it, and copy the picked
key into the host field with `ALValidate`. Every decision that could differ from BC — which
relation arm applies, which rows the relation allows, whether an outcome writes back — stays in
Microsoft's code.

`GetRelationForField` is worth calling rather than open-coding `EvaluateRelation`, because it
does three things a hand-rolled version would have to repeat: it picks the applicable arm (so a
conditional `where()` decides), it reads the related table off the arm's own
`SourceField.Parent` rather than by id lookup, and it defaults `relatedFieldId` to the related
table's **first primary key field** when the arm names none — the ordinary
`TableRelation = "Customer"` shape.

`RunModalAsync`'s body is the whole operation:

```csharp
form.LookupMode = isLookup;
FormResult formResult = await form.RunModalAsync(record, fieldNo);
if (record != null && isLookup && formResult != FormResult.Cancel
    && (!isInLookupTrigger || formResult == FormResult.LookupOK))
    record.Assign(form.SourceTable);
```

The modal dispatch to a declared `[ModalPageHandler]` works with no client present because of
the runner's existing Cecil redirect of the client callback (`RunnerModalDispatch`), exactly as
it already does for `Page.RunModal`.

### Which page opens

The related table's `LookupPageId`, falling back to its `DrillDownPageId` — BC's own precedence,
read off `NavRecord.GetPageToOpen`. The runner deliberately does **not** call that method: its
`CardFormID` follow-through is right for a "navigate to the related record" action, which wants
the card for one row, and wrong for a lookup, which wants the list to pick a row from.

### Two things that were measured, not assumed

Both changed the implementation, and both produced a silently wrong answer first.

| what | the wrong answer | what the measurement showed |
|---|---|---|
| `isInLookupTrigger` | `false`, reading BC's guard literally — a cancelled lookup then **wrote the row the handler had moved to** | a lookup-mode page reports a cancel as `FormResult.LookupCancel` (4), *not* `FormResult.Cancel`, so the first clause of BC's guard never fires and the write-back is carried entirely by the second, which needs `isInLookupTrigger: true` |
| which record `RunModalAsync` gets | the **host** record — the lookup page then bound to the host's own table, and the handler saw the host's rows | an AL probe printed the modal page's first row as `H1`, the host's key; the parameter means the **related** record, which BC binds the page to and assigns the picked row into |

The second is why a probe beats reasoning here: the handler ran, the page opened, and the only
symptom was `NavTestRowNotFoundException` from the handler's own `GoToKey` — which reads as a
broken test rather than a broken fix.

## What still refuses, and what does not

`loud-failures.md` requires a refusal for a surface the runner cannot support faithfully. Of the
two shapes with nothing to open, only **one** turned out to be a runner boundary.

| shape | answer | why |
|---|---|---|
| no trigger and **no `TableRelation`** | **does nothing**, faithfully | real BC raises nothing and opens nothing here — measured, see below |
| relation resolves, target table declares **no `LookupPageId` or `DrillDownPageId`** | **refuses** `testpage-lookup` — **and the refusal's premise is now known to be false** | real BC opens a modal page here, measured on all eight cloud legs (#4403); the refusal says "there is no page to open", which is wrong. It stays only until the page BC picks is identified — see below |
| the control is bound to a **page global**, not a source-table field | **refuses** `testpage-lookup` | no table field to fall back to and no relation to resolve |
| BC's metafield shape could not be read | **refuses** `BcShapeGapException` | the read could not be performed, which is not the same as the read saying "no trigger" |

The first row was a refusal in the first version of #3518, and that was wrong.

### The measurement that corrected it

Corpus codeunit 60569 `TRL Tests` put the claim in front of a real service tier. Run
`35445556865`, all **eight** cloud legs (27.0, 27.3, 27.5, 28.0, 28.1, 28.2, 28.3, 28.4)
answered identically:

```
FAIL  Lookup_NoTriggerAndNoTableRelation_OpensNothing
      NavNCLAssertErrorException: An error was expected inside an ASSERTERROR statement.
```

The other five claims in that codeunit passed on all eight legs of the same run. So BC's answer
for a field with neither trigger nor relation is silence — no error, no page — and the corpus
test now asserts that positively rather than asserting a refusal.

Returning silently is therefore *faithful*, not a silent default: there is no BC behaviour being
skipped, which is the test `loud-failures.md` sets.

**The second row is deliberately not extended to silence by analogy with the first.** That
analogy is exactly the unmeasured inference
`.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md` refuses; a corpus test would
settle it, and until one does, refusing by name is the honest answer.

### The measurement that refuted the second row (#4403)

Corpus PR
[#391](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/391) added that
test to the same codeunit, with fixture `TRL Pageless` (60570) — `TRL Related` minus
`LookupPageId`, and declaring no `DrillDownPageId` either.

**All eight cloud legs answered identically** — run `35493508143`, corpus `8bfd056f`, over
27.0, 27.3, 27.5, 28.0, 28.1, 28.2, 28.3, 28.4:

```
FAIL  Lookup_RelationToTableWithNoLookupPage_DoesNothing — Unhandled UI: ModalPage
```

At least two independent binaries agree: the 27.x artifacts provisioned here share one
`Ncl.dll` (`affa03c9…`) and the 28.x ones are a different file (`6f2cf682…`), so this is not
one measurement wearing eight labels.

`Unhandled UI: ModalPage` is raised from inside BC's own `NavTestExecution.ShowLookupForm`,
which reads `GetRegisteredForm(handle)` **before** looking for a handler — so a form was
already registered and BC had chosen something to open. A shape that opens nothing never
reaches that method, which is exactly how the first row's test passes on the same legs.

**So this row's stated premise — "there is no page to open" — is false.** The test was written
without `asserterror` precisely so this could come out: that form distinguishes all three
possible answers and cannot pass for the wrong reason, while an `asserterror` form would have
swallowed the opened page's error and could have reported green.

### Why the refusal has not yet been replaced

"A page opens" is not a rule the runner can implement. It needs the page **id**, and
`NavRecord.GetPageToOpen` answers `0` for such a table, so whatever picks it is downstream of
that `0` and is client-side — `find_usages` on `NavRecord.LookupFormId` returns nothing in
`Ncl.dll`, which is why only a service tier can answer.

Corpus PR #391 therefore carries a second fixture, `TRL Pageless List` (60571): a page whose
`SourceTable` is `TRL Pageless`, which that table does not name, plus a `[ModalPageHandler]`
bound to it.

**That probe has run, and it cannot answer the question — BC's own code says why.** On corpus
head `af4b986b` (run `35494023689`, six legs) both arms fail with a `NullReferenceException`
inside `ShowLookupForm` rather than with `Unhandled UI: ModalPage`. The tempting reading is
that declaring the handler let BC get further, so the page must match. `FindHandler` refutes it:

```csharp
if (appObject != null)                      // <- appObject is the registered form
{
    ...
    if (customAttributes2.Length != 1
        || ((NavObjectIdAttribute)customAttributes2[0]).ObjectId != appObject.ObjectId.ObjectNumber)
    { continue; }                           // wrong page -> keep looking
}
return method;
```

The page-id check is **inside** the null guard, so a null form skips it and any handler of the
right type is returned — after which `registeredForm.ObjectId` dereferences null. That is the
only branch producing an NRE: a non-null form would either match (handler invoked) or not
(`Unhandled UI`). So `GetRegisteredForm(handle)` answers **null**, and handler binding says
nothing about which page BC picked.

`ShowLookupForm` is byte-identical on `bc270` and `bc284` (`compare_symbols`:
`bodyChanged: false`), consistent with every leg agreeing.

**So BC decides to open a modal page for this shape and then fails to produce one.** The
refusal's premise stays false; its *conclusion* — that the runner cannot serve this shape — is
better supported than before, since BC cannot either. What no instrument here has yet
established is which page BC intended, and no `[ModalPageHandler]` probe can, because the
discriminating check is skipped exactly when the form is missing.

**The refusal therefore stays, as a known-wrong-premise refusal rather than an unmeasured
one.** Replacing it with silence would be a second unmeasured inference, and one the
measurement contradicts: silence is what BC does for the no-relation shape, not this one.

## Where the tests are

| claim | lives in |
|---|---|
| what BC does — the page opens, the selection writes back, a cancel does not, a relation's `where()` narrows the rowset, and a field with no relation does nothing | corpus codeunit 60569 `TRL Tests` (upstream; `bc-behavior-tests-go-upstream.md`) |
| which of the runner's paths a shape takes, and that the remaining refusal stays loud and names its cause | `tests/runner-extras/testpage-lookup-tablerelation-served`, `tests/runner-extras/testpage-lookup-tablerelation-oos` |
| routes 1 and 2, and that a control trigger wins over a table-field one | corpus codeunit 60316 `TFL Tests` |
