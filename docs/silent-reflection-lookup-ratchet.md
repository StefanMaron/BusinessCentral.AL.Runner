# The silent-reflection-lookup ratchet

`AlRunner.Tests/SilentReflectionLookupRatchetTests.cs` counts reflection member lookups under
`AlRunner/Patches/` whose failure is **absorbed at the expression**, and asserts the number does
not grow. This page holds the measurement behind that number, why the number differs from the
one in the issue that asked for it, and what to do when the guard fails.

Filed against #3663.

## The defect being paced

A reflection lookup fails, `?.` propagates the null, and the null check exits with a value
meaning "nothing here" — indistinguishable from a legitimate empty answer. A BC rename then
makes the runner **answer wrong** rather than fail.

Three instances were fixed in one day, each found while fixing the one before it:

| issue | site | what a failed lookup produced |
|---|---|---|
| #3647 | `GetSingleDataItemTableFilterTuples` | `yield break` — read as "this data item has no filters" |
| #3656 | `BuildTableFindAllRequest` | `FiltersAndMarks.Empty` — read as "no filters" |
| #3660 | `GetStaticColumnFilters` | nothing yielded — read as "no static filters" |

In every case a query returns **more rows than it should** and nothing says why. That is what
`.claude/rules/loud-failures.md` forbids, and `AlRunner/Infrastructure/BcShapeGapException.cs`
was built for it — with 27 call sites against a candidate population in the hundreds, and
nothing driving adoption.

## The first thing it caught

Not a hypothetical, and it happened before the guard had merged. **#3659 merged while this PR
was open and added five more instances of the same shape**, in a method written that day.

`RecordPatches.PageControlFieldFromBcDocument.GetMetaFieldEditable` walks five reflection hops
to reach BC's original `Types.Metadata.MetaField` — `metadataAppGroupMetaTable` → `Item` →
`Fields` → `Id` → `Editable` — and answers `true` when **any** of them fails:

```csharp
var original = meta.GetType().GetField("metadataAppGroupMetaTable", ...)?.GetValue(meta);
var metaTable = original?.GetType().GetProperty("Item", ...)?.GetValue(original);
if (metaTable?.GetType().GetProperty("Fields", ...)?.GetValue(metaTable) is not IEnumerable fields)
    return true;
...
    return t.GetProperty("Editable")?.GetValue(f) is not bool e || e;
```

`true` means **editable**. It is not a neutral sentinel: it is a specific, plausible, invented
answer that a caller cannot distinguish from BC genuinely reporting the field editable. A BC
rename of any one of those five members silently makes every field on every page read as
editable, and nothing says why — the #3663 shape exactly, arriving the same day the ratchet
did.

The guard reported all five by file, line, receiver and member on first contact with the code:

```
5 NEW silent reflection lookup(s) under AlRunner/Patches/.
  RecordPatches.PageControlFieldFromBcDocument.cs:520  meta.GetType().GetField("metadataAppGroupMetaTable", ...)
  RecordPatches.PageControlFieldFromBcDocument.cs:524  original?.GetType().GetProperty("Item", ...)
  RecordPatches.PageControlFieldFromBcDocument.cs:527  metaTable?.GetType().GetProperty("Fields", ...)
  RecordPatches.PageControlFieldFromBcDocument.cs:535  t.GetProperty("Id")
  RecordPatches.PageControlFieldFromBcDocument.cs:536  t.GetProperty("Editable")
```

They are **recorded in the allowlist, not converted.** The defaulting rule is that method's
stated design — its own header argues `true` is what BC substitutes when it has no field — and
revisiting it is that method's decision, not this ratchet's. What the ratchet changes is that
the choice is now written down and counted instead of being invisible at the call site. That is
the whole claim of #3663, demonstrated on live code within a day of the issue being filed.

## The measurement

Every `GetProperty` / `GetMethod` / `GetField` / `GetConstructor` / `GetNestedType` call in
`AlRunner/Patches/**/*.cs` (171 files), classified by what happens to the null a failed lookup
returns. Comments and string literals are blanked before scanning, so prose describing a shape
is not counted as one.

| | count |
|---|---:|
| lookups scanned | 744 |
| — excluded as not-reflection (`JsonElement`, `StackFrame`) | 21 |
| **considered** | **723** |
| **silent — the null is absorbed** | **125** |
| loud — `?? throw` | 105 |
| null-forgiving `!` — a different ratchet's population (#3051) | 26 |
| chain — `?? <another lookup>`, an alternate member *name* | 13 |
| explicit `!= null` test at the expression | 2 |
| stored to a variable, or otherwise consumed; the failure path is a property of the method, not of the expression | 452 |

The buckets sum to 723, and 723 + 21 excluded = 744.

The 125 sit in **42 files**. The heaviest:

| file | silent sites |
|---|---:|
| `NavReportSync.cs` | 9 |
| `RecordPatches.UserSystemTable.cs` | 8 |
| `CodeunitPatches.cs` | 7 |
| `RecordPatches.NclMetaTableFromBcDocument.cs` | 6 |
| `EventSubscriberPatches.cs` | 5 |
| `HelperShims.cs` | 5 |
| `MetadataPatches.cs` | 5 |
| `RecordPatches.cs` | 5 |
| `SessionPatches.cs` | 5 |

### A conversion does not necessarily lower the count

An earlier draft of this page said nine sites in `RecordPatches.QueryJoin.cs` (#3664) and
`RecordPatches.QueryProjection.cs` (#3665) would fall out of the baseline when those PRs
merged. That was wrong twice over: the real counts are **2 and 3**, and **neither set falls**.

#3664 has since merged, with a genuine conversion, and the count did not move. Its
`StaticMember` helper probes two spellings of one BC static and refuses the pair on the
*following* statement:

```csharp
var v = t.GetField(member, BcShape.AnyStatic)?.GetValue(null)
     ?? t.GetProperty(member, BcShape.AnyStatic)?.GetValue(null);
return v ?? throw new BcShapeGapException(JoinSurface, $"{t.Name}.{member}", "...");
```

That code is **loud** — a failed lookup cannot escape the method — and both lookups still
classify as **silent**, because each one individually ends in a `?.` and this classifier reads
only the expression. #3665 carries the same shape and will behave the same way.

So: **a conversion landing with no ratchet movement is the guard working as specified**, not a
defect in it. Recording this because the alternative reading — "the guard missed a conversion"
— is the one a future reader will reach for first. `ARefusalOneStatementAway_DoesNotMakeTheLookupsLoud`
pins the behaviour so it cannot drift silently.

The honest cost is stated rather than argued away: these five are counted as silent while being
loud, so the baseline slightly over-counts. Following `v` to its refusal is the dataflow
analysis this guard deliberately does not attempt (see the section above), and the alternative
— a classifier that guesses — false-fails on correct code, which is how a ratchet gets turned
off.

## Why 120 and not 339

#3663 measured 745 lookups and reported **167 loud / 339 silent / 239 unclear**, and said
plainly that its classifier was a heuristic and 339 should be read as an order of magnitude.
Re-deriving it produced 740 lookups at the time — the five-lookup gap is #3647 and #3660, both
fixed that same day — so the *scan* reproduces. (The tree has since moved to 744/723/125; the
comparison below uses the figures as measured against #3663's own tree, so the two sides are
like for like.) The *classification* does not, and the reason is specific
and worth recording:

**The issue's classifier asked "does anything within seven lines raise?"** A window is a
proximity test, not a dataflow test, and the commonest loud shape in this tree is

```csharp
_pDaSession = tDataAccess.GetProperty("Session", Flags)
    ?? throw new InvalidOperationException("DataAccess.Session not found");
```

There are **105** of those. They are as loud as a lookup gets — the null cannot escape the
statement — and they are also, unavoidably, within seven lines of a great deal of other code.
A window classifier bucketing on proximity puts them on the wrong side, and 105 of 339 is most
of the gap.

The other half of the gap is the ~450 "stored" sites, which the issue's run split across its
`silent` and `unclear` buckets. Those genuinely need dataflow: the null goes into a variable and
what happens next is a property of the enclosing method. **A text scanner cannot decide them,
and this guard does not try** — see the next section.

Neither number is wrong for what it measured. 339 is the right order of magnitude for "lookups
worth looking at"; 120 is the count of "lookups whose silence is visible in the expression
itself", which is the only population a ratchet can hold without a false positive.

## What the classifier counts, and why it is deliberately local

The rule reads only the lookup expression — never the surrounding method:

| shape | verdict | why |
|---|---|---|
| `x.GetProperty(...)?.GetValue(y)` | **silent** | the null is absorbed here |
| `x?.GetType().GetProperty(...)` | **silent** | a null-conditional receiver does the same |
| `x.GetProperty(...) ?? <value>` | **silent** | the null becomes an ordinary value |
| `x.GetProperty(...) ?? throw ...` | loud | the null cannot escape |
| `x.GetProperty(...) ?? x.GetProperty(...)` | chain | asks for an alternate member *name*, which is handling this exact failure |
| `x.GetProperty(...)!` | not this population | NREs at first use; `BcInternalsNullForgivingGuardTests` owns it (#3051) |
| `var p = x.GetProperty(...);` | not counted | the failure path is a property of the method |
| `x.GetProperty(...).GetValue(y)` | not counted | consumed immediately; a failed lookup NREs rather than answering, but the trailing token is a call rather than a null-absorbing operator, so it falls in the same "not decidable at the expression" bucket |

Locality is the design. A ratchet has to be reproducible by the next person from the code
alone and must not fail on code that is fine; a classifier that abstains on ~450 sites and
guesses at the rest can do neither.

**The chain row is not a technicality.** It is BC's own `FieldNo` → `No` rename, handled at
`RecordPatches.FieldFindIntercept.cs:569`:

```csharp
_pFieldNo = tMetaField.GetProperty("FieldNo", BindingFlags.Public | BindingFlags.Instance)
    ?? tMetaField.GetProperty("No", BindingFlags.Public | BindingFlags.Instance);
```

Counting that as silent would flag a site that already does the right thing.

## The excluded classes

Two BCL methods share a name with a reflection lookup and are not one. Both are live in this
tree, so these are exclusions against measured code:

- **`JsonElement.GetProperty(string)`** — 20 sites, in `EnumMetadataPatches`,
  `ObjectMetadataRegistry`, `PageMetadataRegistry`, `ReportLayoutRegistry`,
  `ReportMetadataRegistry` and `XmlPortMetadataRegistry`. All read JSON out of a
  `foreach (var e in arr.EnumerateArray())`.
- **`StackFrame.GetMethod()`** — one site, `NavAppResourcePatches.cs:329`, inside
  `trace.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly`. Note it *is* behind `?.`, so the
  exclusion is what removes it, not the silence rule.

They are discriminated on **argument shape first** — a reflection lookup in this tree passes
`BindingFlags` or a type array, and neither of these ever does — then on how the receiver was
bound. Inferring from the receiver's *name* was tried first and is not sound: the JSON sites
bind their receiver in a `foreach`, so there is no declaration to read, and a first attempt at
name inference missed all 20.

## What to do when the guard fails

**It says a site appeared.** You wrote a lookup whose failure is absorbed. Give it an explicit
refusal:

```csharp
// preferred — names the surface and the member, and cannot be absorbed by an expectations entry
var p = BcShape.Property(t, "Member", flags, "the surface this serves");

// also fine where a BcShape overload does not fit
var p = t.GetProperty("Member", flags) ?? throw new InvalidOperationException("...");
```

If absence really is a legitimate answer at that site — `BcShapeGapException`'s header draws
the line: *raise when the read could not be performed, do not raise when the read succeeded and
the answer was merely unwelcome* — then say so in a comment at the call site, add the entry to
`KnownSites`, and raise `Baseline`. That is a reviewable decision rather than a silent default,
which is the whole point.

**It says a site vanished.** You converted one. Delete its `KnownSites` entry, lower `Baseline`
by the same amount, and name the site in the PR body.

## What this guard does not claim

It does **not** claim all 125 are bugs. Per #3663 and `BcShapeGapException`'s own header, some
fraction are correct as they stand, and telling which is which needs the per-site adjudication
#3657 spent its review on — in that one method, four exits turned out to be genuine answers
that had to stay silent, and getting one wrong in that direction breaks an ordinary query on
**every** BC version rather than only on a future one.

So this is a population to triage, and the ratchet paces the triage. What is not defensible,
and what it fixes, is that the choice between "gap" and "answer" was invisible at the call site
and unenforced anywhere.

## See also

- `AlRunner/Infrastructure/BcShapeGapException.cs` — the line between a gap and an answer
- `AlRunner.Tests/BcInternalsNullForgivingGuardTests.cs` — the sibling ratchet, for `!`
- `.claude/rules/loud-failures.md` — no silent out-of-scope failures
- #2994 — converting *already-loud* refusals onto `BcShapeGapException`; a different population
