# Count-baseline history

Why every number in `test-count-baseline.json` is the number it is. One entry per bump, so
that two PRs bumping different suites append to different sections and git merges them
itself (#2485).

**Where to write.** A corpus pin bump goes under `## al-language`: say which upstream corpus
PRs came in, what they assert, and that the count was measured on a real run rather than
computed. A runner-extras change usually needs nothing here — the group entry it adds to
`test-count-baseline.json` already names the app group and its test count. Write an entry
when the *reason* is not obvious from that line (a suite that only exists from BC 28.0 on, a
count that moved without any file being added, a number you had to re-measure after a
rebase).

Newest last, within each section.

## al-language

### 2554 -> 2599 (pin aa49fb4f -> ab6fbefa, PR #2941)

The pin advanced to consume StefanMaron/BusinessCentral.AL.Language.Tests#174, the upstream
half of #2296 (the session user must be a row in the User table). Corpus history is linear, so
five other merged corpus PRs came with it and all of them contribute tests:

| corpus PR | what it pins |
|---|---|
| #168 | opening a TestPage is not a Commit |
| #169 | running a page modally inside a test is not a Commit |
| #170 | how `TestPage.Previous()` walks a page's rowset backwards |
| #171 | `min()`, `max()` and `average()` CalcFormula aggregates |
| #173 | the Table Metadata, AllObj and AllObjWithCaption virtual tables |
| #174 | the session user is a row in the User table (this PR's own upstream test) |

**2599 is the number the guard itself reported**, not one computed from the old total plus a
count of added tests (#2803). Measured on BC 28.1.49838.53910 by running the corpus with
`--strict --count-baseline`; the run failed with
`GROWTH: suite 'al-language' tests count: expected 2554, actual 2599 (BC 28.1)` and 2599 is that
`actual`. Re-run after the bump: 2599/2599, exit 0.

The guard was also confirmed ARMED rather than silently skipping, since it prints nothing on a
match: re-running against a copy of this file carrying 2600 exits 4 with
`DROP: ... expected 2600, actual 2599`.

Of the 2599, 5 are new `expect-fail-known-gap` entries covering pre-existing runner gaps the
bump made visible — 4 under #2938 (Table Metadata constants) and 1 under #2970 (FlowField
CalcFormula type validation). Both issues stay open after this PR merges. The 4 tests from #174
pass, because this PR is the fix for them; no entry was written for those.

Ten gaps were measured at the first pass, not five. The other five were the
`Codeunit60756.TestPage_Previous_*` family, entered under #2901 (SourceTableView not applied,
reaching those tests through `Previous()`). Between that measurement and this branch's final
merge, #2861 landed on `main` and FIXED SourceTableView, which also deleted
`known-gaps-testpage-sourcetableview.json` and its four codeunit 60822 entries. Re-measuring
after the merge rather than reusing the earlier number is what caught it: all nine now pass, so
`pass-known-gap` reads 17 rather than 26, and leaving the five entries in would have failed the
run with "Test passed cleanly but manifest declares expect-fail-known-gap". The total stays 2599
either way — a reclassification, not a count change.

**#2984 — `al-language-internals-fixture` gets a line of its own; `al-language` does not move.**
The corpus leg used to be handed one path, `tests/al-language/tests/al-language`, so the corpus
had exactly one suite key however many apps the submodule carried. It now enumerates
(`scripts/corpus-app-dirs.py`) and passes each corpus app as its own bundle root, so the
dependency-only fixture app becomes its own suite: `{ "tests": 0, "appGroups": 1 }`, the same
shape a dependency-only `runner-extras` group has. Measured, not computed — BC 28.1, pinned
corpus `aa49fb4`: 2 buckets, 2554 tests, exit 0, against 2554 in 1 bucket for the single-app
invocation it replaces (72.9s → 74.6s cold wall, the 1.7s being the fixture's own compile).
`al-language` stays at 2554 because nothing about that app's run changed. Rebased onto a
`main` that has since bumped the pin to `ab6fbefa` (the 2554 -> 2599 entry above): 2554 is
the number at the pin this was measured against, and enumerating the corpus's apps does not
move whatever that number is. The fixture app is still a separate, test-free app at
`ab6fbefa`, so its `{ "tests": 0, "appGroups": 1 }` line is unchanged by the bump.

### 2676 -> 2681 (pin 6e198a97 -> b0c6248a, issue #3121)

Written against pin `861a5662` and re-measured after `main` moved its own pin to `6e198a97`
(#3152) while this PR was open: `main`'s pin is an ancestor of this one, so the merge keeps
`b0c6248a` and the number moves 2676 -> 2681 rather than 2665 -> 2681. Two corpus PRs are what
that difference is: #201 and #199. The full list of what this pin carries over `861a5662`:

- #199 — `record/TestCalcFieldsPrecompiledTableExtFlowField.al`, the four tests this PR's fix
  makes pass: `CalcFields` on a FlowField a Base Application TABLEEXTENSION contributes
  (`Customer` 5912 "Outstanding Serv.Invoices(LCY)", `Stockkeeping Unit` 99000777
  "Qty. on Prod. Order"), each seeding rows that must count and rows that must not.
- #201 — `Subtype = Install` reads back as Normal in `CodeUnit Metadata`.
- #198 — the row order of the two codeunit inventories.
- #194 — whether a refused `Page.Run` leaves its target unopened.
- #192 — five review findings from the #185/#186/#187 merges, including the modal
  close-lifecycle tests (`MQC Tests`, codeunit 60276).
- #190 — `ALTRelationWhereField` made able to detect a swapped `where()` role.
- #195 — stopped encoding the manifest version in a test's name.

2681 is measured, not computed: full corpus runs on BC 28.1.49838.53910 over the three corpus
app roots (`scripts/corpus-app-dirs.py tests/al-language`). The first, against the previous
number, reported `expected 2665, actual 2681`; the run after merging `main` reported no
count-baseline line at all with 2681 in place, 2700 tests over the three roots, exit 0. CI
agreed on every leg of run 34046877973 — no leg printed a count-baseline message.

### 2689 -> 2757, and onprem 19 -> 25 (pin 7394c15 -> c3531ec)

The pin advances to consume StefanMaron/BusinessCentral.AL.Language.Tests#204, which pins that a
user's SUPER status is backed by an Access Control row — the fix in this PR. Corpus history in
this range is linear, so ten other merged corpus PRs come with it. Every one of them needed a
runner fix or needed nothing, and all of those fixes are now on `main`:

| corpus PR | what it pins | runner gap | state |
|---|---|---|---|
| #197 | what the legacy Object (2000000001) table holds, OnPrem app | #3071 | merged, PR #3265 |
| #206 | what a `Database::<Object>` const in a `where()` clause resolves to | — | needs nothing |
| #204 | a user's SUPER status is backed by an Access Control row | **#3176** | **this PR** |
| #203 | what a list page's built-in View and Edit actions do | — | needs nothing |
| #207 | a tableextension-contributed TableRelation is enforced by Validate | #3177 | merged, PR #3197 |
| #209 | a second control over one page binding resolves | — | needs nothing |
| #210 | what the Session virtual table (2000000009) holds | #2940 | merged, PR #3234 |
| #211 | what an action's RunPageLink does to its RunObject target | — | needs nothing |
| #208 | a tableextension adds keys and field properties, not just columns | #3216 | merged, PR #3257 |
| #212 | whether AL short-circuits and/or | — | needs nothing |
| #215 | the SecretText runtime surface | — | needs nothing (16/16 measured) |

`c3531ec` is deliberately NOT the corpus tip, which is `0bda9d6`. Walking forward past it, the
next commit that needs a runner fix nobody has written is `b1fdb6d` (corpus #216, a CalcFormula
and a TableRelation naming a system field). Measured at the tip against this branch's build:
**19 failures**, in three clusters, each already tracked by an open runner issue:

| corpus PR | failing codeunit | tests | open issue holding it |
|---|---|---|---|
| #216 | Codeunit60818 | 5 | #3178 — a CalcFormula over a system field never builds |
| #217 | Codeunit60823 | 4 | #3263 — a CalcFormula never resolves a tableextension field |
| #218 | Codeunit60338 | 9 | #3284 — the FormResult for a handler that invokes nothing is OK, not Cancel |

Corpus #219 and #220 pass and need nothing; they are held back only because they sit behind
#216 in a linear history. So `c3531ec` is the newest commit all of whose predecessors are
satisfied, per the third case in `.claude/rules/al-language-submodule.md`, and #3178, #3263 and
#3284 hold the remainder. All three stay open after this PR merges.

Both numbers are MEASURED, by a run of this branch's build at the new pin on BC 28.1, not
computed: three roots, `Tests: 2782 total, pass: 2782, fail: 0`, exit 0 — and exit 0 is itself
the count-baseline check passing, since `--count-baseline` exits 4 on a mismatch in either
direction. 2757 cloud + 25 OnPrem + 0 internals-fixture = 2782.

Counted independently per file as a cross-check, with AL comments, string literals and
preprocessor-disabled code excluded: the same 2757 and 25. That per-file method reproduces the
outgoing pin's 2689 exactly as well. The gap between a raw source count and what a run reports
is exactly 2 tests, behind `session/TestFinalCoverage.al`'s `#if BC143PLUS`, which is never
defined — the corpus's only preprocessor-disabled block, unchanged across this range.

The OnPrem suite moves because #197 added `record/TestObjectSystemTable.al` (6 tests) to the
OnPrem app — the legacy Object table is `Scope = OnPrem`, so the test could not live in the
cloud app.

No per-version override is needed: the range adds no preprocessor version guards, so `default`
is correct for every leg. Measured on BC 28.1; the other seven legs are CI's word.

### 2814 -> 2887, and onprem 25 -> 29 (pin 2cba52d4 -> 17b015e, corpus #220, #214, #221, #222, #225, #226, #228)

A catch-up bump: every corpus PR in the range was already adjudicated on a real service tier
upstream, and no runner fix was pending for any of the seven commits taken. It is green on its
own, which is why it is its own PR (`al-language-submodule.md`, the catch-up case).

`al-language` 2814 -> 2887 (+73) and `al-language-onprem` 25 -> 29 (+4). Both numbers were
read off a run, not computed: the run at this pin reported them in its `[count-baseline]
GROWTH` lines, and the run with these numbers in place exits 0. The onprem suite moves because
corpus #228 added four `Published Application` FlowField tests to
`record/TestPublishedApplicationSysTable.al`, and that table is `Scope = OnPrem`.

Cross-checked by hand against the corpus source, per file, with comments and string literals
stripped: 2889 `[Test]` procedures in the `al-language` app, of which two --
`JsonObject_ReadFromYaml_OnCloudRuntime_IsNotSupported` and its `WriteToYaml` sibling in
`session/TestFinalCoverage.al` -- sit behind `#if BC143PLUS`, which is not defined, so they
compile out. 2889 - 2 = 2887, the runner's number exactly. A raw `[Test]` count over that
subtree is 2889 and is wrong; the preprocessor is the reason.

No per-version override is needed: none of the arriving files adds a preprocessor version
guard. Measured on BC 28.1; the other seven legs are CI's word.

**The pin stops at `17b015e`, six commits short of corpus `master` as of 2026-09-07,**
because corpus history is linear and the two commits after it fail against the runner
today: `0bbe376` (corpus #227, TestPart, codeunit 60346, 11 tests) and `d025203` (corpus
#229, TestFilter, codeunit 60350, 6 tests). Measured at the tip on this same build: 17
failures, exactly those two codeunits and nothing else. Tracked by issues #3312 and #3313
for the TestPart cluster -- #3009 was cited here too when this was written and has since
closed via PR #3323, so that cluster is 8 rather than 11 -- and by #3316 for five of the
six TestFilter failures (`TestPage.Filter.CurrentKey` reporting field numbers,
`SetCurrentKey`/`Ascending` not changing the walk order); the sixth is #3312's part-page
id 0 problem again.
No `tests/expectations/` entry was added for any of them -- declaring a live, owned gap as
settled classification is what `ask-the-corpus-before-claiming-bc-behavior.md` forbids, and
leaving the two commits unpinned keeps the gap honest instead.

`runner-extras` is untouched by this PR.

Written by agent impl-1 (automated implementation agent).

## 2026-09-07 — corpus pin `17b015ef` → `0bbe376` (al-language 2887 → 2915)

One commit, and one is the whole of what the runner can take today. The pin advances to
corpus #227 (`0bbe376`, TestPart — 5 fixture pages/tables plus `TestTestPart.al`), which the
entry immediately above listed as blocked. It is no longer blocked: the two runner fixes it
was waiting on merged earlier today, #3312 via PR #3336 and #3313 via PR #3338, and this
pin bump is the catch-up that consumes them.

2915 is measured, not computed — a full corpus run on this branch at this pin, BC 28.1,
`--package-cache ~/.al-runner/platform-apps --strict`: **2915 total, 2915 pass, 0 fail,
0 error, exit 0** (2 `pass-oos`, 11 `pass-known-gap`, 1 `pass-divergence`). The +28 over
2887 is corpus #227's codeunit 60346 and nothing else; `0bbe376` touches no file under
`al-language-onprem`, so that suite stays at 29 and `runner-extras` is untouched.

**The wall is `d025203` (corpus #229, TestFilter), and it was measured rather than
inferred.** Three runs on one build decided the prefix: the tip `3331aa6` gives 2966 tests
with 5 failures, `0bbe376` gives 2915/2915 clean, and `d025203` gives 2927 with the same 5
failures — so the first red commit is `d025203` and every commit past it is untested behind
it, because corpus history here is linear (0 merge commits across the 8).

All 5 are codeunit 60350 and all are one shape, already tracked as **#3316**:

| test | expected | runner answered |
|---|---|---|
| `TestFilter_CurrentKey_NamesTheKeyThePageIsWalking` | `Entry No.` a substring | `` (empty) |
| `TestFilter_SetCurrentKey_ChangesBothTheReportedKeyAndTheWalkOrder` | `Rank` a substring | `3` |
| `TestFilter_SetCurrentKey_AcceptsACompositeKey` | `Grp` a substring | `2, 3` |
| `TestFilter_Ascending_False_ReversesTheWalkAndIsReportedBack` | `3|2|1` | `1|2|3` |
| `TestFilter_Ascending_AppliesToTheKeySetBySetCurrentKey` | `1|3|2` | `1|2|3` |

`MockTestPage`'s `ITestFilter` members are a write-only store. `CurrentKey` is
`string.Join(", ", _currentKeyFields)` over raw field *numbers*, which is where `3` and
`2, 3` come from; it is empty on a freshly-opened page because nothing seeds
`_currentKeyFields` from the table's primary key. And `GetCurrentKeyFields` and `_ascending`
have **zero** consumers anywhere in `AlRunner/` — measured, not read off — so the last two
rows are the same defect seen through the walk order rather than through the reported string.
BC's own `NavTestFilter.ALCurrentKey` is `filter.CurrentKey`, delegating to the `ITestFilter`
the runner supplies, so the gap is entirely runner-side.

No `tests/expectations/` entry is added for those 5. They stay unpinned behind the wall,
for the same reason the entry above gives: classifying a live, owned gap as settled is what
`ask-the-corpus-before-claiming-bc-behavior.md` forbids, and leaving `d025203` and the six
commits after it unpinned keeps the gap honest.

Written by agent stma-auto-1 (automated implementation agent).

## 2026-09-07 — corpus pin `408c39fe` → `ddb9b5ab` (al-language 2969 → 2978)

Two commits. This bump exists to consume corpus
[#241](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/241) (`ddb9b5ab`,
codeunit 60291 `"Test Permissions Mock Lifecyc"`), the upstream half of **#3343**. `ddb9b5ab`
is corpus `master`'s tip; corpus history is linear, so corpus #242 (`21e2fed`, partial
records) comes along with it and there is no earlier commit that contains #241.

The starting point is `408c39fe`/2969, not `0bbe376`/2915: the entry above records
`0bbe376` → `408c39fe` (+54) from #3345, which merged while this branch was open. This branch
merged `main` and rebuilt its number from that base rather than carrying its own first draft
(`0bbe376` → `ddb9b5ab`, +63) forward, which would have double-counted #3345's step.

`al-language` 2969 → **2978** (+9), measured on a real 3-bundle run on BC 28.1 with the
workflow's own cache paths, not computed: **3007 tests total** across the three corpus apps, of
which `al-language-onprem` contributes 29 and `al-language-internals-fixture` 0 — both
unchanged, since neither of the two commits touches those apps. `runner-extras` is untouched.
Corpus #241's own contribution is 3 of the +9; corpus #242's is 6.

One `default` rather than per-version entries, checked rather than assumed: neither of the two
commits carries a preprocessor gate at all, and the run reports the same 3007/2978 on BC 27.0,
27.5 and 28.4.

### The four tests corpus #242 brings, and why they are declared

Codeunit 60775 `"Test Record Partial Load"` measures the half of partial records nothing
covered: which fields BC silently ADDS to a `SetLoadFields` request, and what reading an
omitted field does. Four of its tests fail here, and every one fails on its **precondition** —
the arrange step establishing that a field is not loaded — rather than on the behaviour it is
about. `Record.AreFieldsLoaded` answers true for every field, always: the runner loads whole
rows and has no partial load set.

Declared `expect-fail-known-gap` in `known-gaps-record-partial-load.json` against **#3358**,
which stays open after this PR merges. Note what is NOT broken: reading an omitted field yields
its real stored value here, which is what BC does too — `SetLoadFields` is a performance hint,
never a data filter — so this is a reporting gap, not a data-correctness one.

Nothing else in this file is this PR's to move. The 60774 `expect-oos` entry for corpus #239's
SaveAs(Pdf) content test and the five 60350 TestFilter `expect-fail-known-gap` entries against
#3316 both arrived on `main` with #3345 and are left exactly as that PR wrote them; this
branch's own duplicate of the first was dropped in favour of `main`'s during the merge.

After the declarations the run reports **3007 total, 3007 pass (4 `pass-oos`,
20 `pass-known-gap`), 0 fail**, and `--expectations-require-match` audits **all 25 entries
matched a discovered test**.

Written by agent fbk-1 (automated implementation agent).

## runner-extras

### object-metadata-system-table 4 -> 6 (PR for #2771)

One test replaced by three, so the group gains two.
`MetadataPayloadColumns_ReadBlank_DeclaredDivergence` asserted the exact blanks the nine
compiled-metadata payload columns read; it is gone, because those columns now refuse by name.
What replaces it is deliberately three tests and not one rewrite:

- `MetadataPayloadBlobs_RefuseByName_RatherThanReadingAnEmptyPayload` and
  `MetadataPayloadScalars_RefuseByName_RatherThanReadingBlank` are split because the two kinds
  of column are caught at two different seams — the runner's own blob load inside
  `FlowFieldPatches.RecordImpl_CalcFieldsAsync_3` for the BLOBs, and
  `NavRecord.GetFieldValueSafe` for the scalars. One test over all nine would have gone green
  with either seam missing, since the first `asserterror` it reached would have satisfied it.
- `RefusingAPayloadColumn_LeavesAllFourRequestPathsWorking` is the control, and it is the
  reason the count moved by two rather than one. #2519's whole argument for tolerating the
  blanks was that refusing at row-build time takes `FindSet` / `Count` / `IsEmpty` / keyed
  `Get` down with it. That test asserts all four still answer, each with a negative twin, so a
  refusal that spread past the one column it names cannot ship green. `IsEmpty()` is asserted
  separately from `Count()` on purpose: `RecordImplementation.IsEmptyAsync` calls its own
  `ExistsAsync`, the same fourth-path assumption that let #3006 sit unnoticed.

It passed before the fix and must keep passing after it, which makes it the one test in the
group whose value is entirely in not moving.


### date-virtual-table-window 5 -> 9 (PRs for #3006 and #2965)

Four tests added to an existing app group, so no new group line. Written down because two of
the four assert something the group's name does not suggest and the reason is worth keeping:

- `Date_IsEmptyBeforeTheWindow_WidensTheWindowLikeCountDoes` and
  `Date_ClosedRangePastTheRowCap_ThrowsOnTheIsEmptyPathToo` cover `IsEmpty()`, which is a
  FOURTH `DataAccess` request path (`ExistsAsync`/`ExistsCacheRequest`) and not a spelling of
  `Count()` — the assumption that let #3006 sit unnoticed.
- `Date_IsEmptyInsideTheWindow_StillAnswersTrueWhenNothingMatches` is its negative arm: a
  materialised range that genuinely holds no Week period must still answer `true`.
- `Date_RowCapRefusal_TearsThroughATryFunction_InsteadOfReadingAsFalse` is #2965's: it asserts
  the runtime consequence of the refusal's claim, not its wording.

Measured by running the group, not computed: `9P/0F/0E across 9 tests`, cold and warm.

### navapp-moduleinfo-main 10 -> 15 (#2960 / PR #3225)

Five tests added to an existing app group, so no new group line and no app-group count change.
The group has no `absentOn`, so the same +5 lands on every BC version. Re-measured against
`origin/main` at 216c481c (corpus pin c3531ec6), the 28.x endpoint is 344 -> 349 and the 27.x
one 333 -> 338.

Those two endpoints are the least durable thing in this entry and have now been rewritten five
times (301/312, then 305/316, then 321/332, then 319/330, now 344/333) without this group's own
number ever changing. They move whenever ANY other group's count moves, which on a busy branch is
every few merges. Read the endpoints off `test-count-baseline.json` at whatever base you are
on; the durable fact is +5 on every BC version, and `--count-baseline` is what enforces the
sum rather than this paragraph.

Three came with the by-id `NavApp.GetModuleInfo` fix, which the two stack-walk patches did not
cover — the boolean/statement not-found arms and the derived PackageId. Two more came out of
the review of that PR and are worth naming, because neither is about the reported bug:

- `ModuleInfo_ForOneApp_AgreesOnPackageIdAcrossEntryPoints` — real BC has ONE module-info
  implementation (`ALGetCurrentModuleInfo` and `ALGetCallerModuleInfo` both forward into
  `ALGetModuleInfo`), so AL asking about one app three ways cannot get three answers. The
  runner patches the entry points independently, so fixing only the by-id one made this bundle
  report two different PackageIds for ITSELF. The test fails against that state and is what
  holds the five sites on one shared constructor.
- `GetModuleInfo_ByEmptyGuid_RefusesLoudlyInsteadOfAnsweringNotInstalled` — folding the
  source-compiled polyfill onto the shared implementation changed first-party AL behaviour:
  the old private copy answered plain `false` for `Guid.Empty`, which BC never does on that
  branch. Now it refuses loudly.

Measured by running the group, not computed. The whole suite under `--count-baseline` on BC
28.1, re-run on the tree rebased onto 216c481c: `349 total / 349 pass / 0 fail / 0 error`,
exit 0. The 27.x endpoint is that figure less the eleven tests in the five `absentOn`
groups, and is not locally measurable here — a self-built runner compiles against Ncl 28.x —
so CI measures it.

## Migrated log (everything above 2026-09-05, verbatim)

This is the `_comment` string `test-count-baseline.json` used to carry: 40,178 characters on
a single JSON line, which every count-changing PR had to append to and therefore conflicted
on. The text is unchanged; only line breaks and paragraph splits were added, so nothing that
was recorded about a past bump is lost.

Consumed by --count-baseline (AlRunner/Infrastructure/CountBaseline.cs), a DIFFERENT schema from
the oos-/known-gaps-/divergence-/disabled- files in this directory: those declare the expected
CLASSIFICATION of one named test, this declares the expected EXACT COUNT of a whole suite -- an
exact match, not a floor: a mismatch in EITHER direction (drop OR growth) fails the run (exit
4). See #1880 and PR #1882's review for why growth is also a hard failure, not just a notice.

Suite keys are the basename of the bundle directory CI passes on the command line
(tests/al-language/tests/al-language -> 'al-language', tests/runner-extras -> 'runner-extras'),
matching the '--out <name>-results.json' convention CI already uses.

Values below are read off actual Test Matrix CI runs (see PR discussion for run URLs), not
guessed: al-language's 2073->2076 bump reflects 3 corpus tests merged upstream between when the
local baseline was drafted and when CI first ran against it.

The 2161->2164 bump (issue #2146) reflects 3 more corpus tests merged in
StefanMaron/BusinessCentral.AL.Language.Tests#74 (HAVING-style filters on an aggregated column,
and a multi-dataitem JOIN + GROUP BY).

The 2164->2167 bump (issue #2133) reflects 3 more merged in
StefanMaron/BusinessCentral.AL.Language.Tests#75 (the write-transaction scoping rule around
Codeunit.Run: guarded form refused with an uncommitted write, statement form allowed, guarded
form allowed after Commit).

The 2167->2176 bump (issue #2089) reflects 9 more merged in
StefanMaron/BusinessCentral.AL.Language.Tests#76 (the implicit new-row line an editable,
insert-allowed repeater carries past its data: Next() past the last data row lands on it and
reads blank, it follows ALL data rows, walking onto it inserts nothing, a ListPart on a modal
host carries its own -- and the five suppression arms, OpenView / Editable = false /
InsertAllowed = false / a part on a read-only host / First() on an empty editable list, which
all answer false).

The 2176->2178 bump (also issue #2089) reflects 2 more merged in
StefanMaron/BusinessCentral.AL.Language.Tests#77, covering page-level TestPage.Editable() on a
page the test never opened -- reached through a [ModalPageHandler], where there is no open mode
to answer from: a page declaring Editable = false reports false, and a page declaring no
Editable property reports true. Every other Editable() test in the corpus opens the page itself,
so the handler path was previously unmeasured.

The 2178->2183 bump (issue #2090) reflects 5 more merged in
StefanMaron/BusinessCentral.AL.Language.Tests#78 (commit 2ddd9715), covering a subpage part on a
host reached with TestPage.OpenEdit rather than through a [ModalPageHandler]: the part reads its
seeded row, an empty part still answers First() = false, the part walks both data rows in key
order, the host's own header-field OnValidate reaches into the part page, and the same read on a
host that DOES have a SourceTable is the control arm. Suite 60734 had measured only the
handler-driven half of that shape, so the directly-opened host was previously unmeasured.
runner-extras' appGroups 23->21 byBcVersion override on 27.0/27.3/27.5 is the SAME
preprocessor-gated-surface split that already explains its tests count divergence: fewer AL
surfaces compile pre-28.0, which drops whole app groups, not just individual tests within a
group.

Issue #2113 moves BOTH suites at once, in one PR, because the pin bump and the runner fix cannot
be separated: a TestPage Invoke() of an actionref never followed the reference to its target
action, so every promoted Invoke() was refused as the page declaring no OnAction trigger for it.
al-language 2183->2191 reflects 8 corpus tests merged in
StefanMaron/BusinessCentral.AL.Language.Tests#79 (commit
c98be5488f07cd0fb63d3fa731055e9698f018ae), green on real BC 27.5 and 28.3: a promoted actionref
runs its target's OnAction, runs it against the page's current row, works nested in a promoted
category group, runs ONLY its own target, propagates an Error raised inside that target, and
does both of those across the pageextension boundary (a pageextension's promoted ref pointing at
its own action, and at a BASE PAGE action) -- plus the direct-invoke control arm. runner-extras
189->198 tests / 33->34 appGroups is the one new app group
tests/runner-extras/testpage-promoted-actionref carrying 9 tests: the runner-repo-local stopgap
for those same arms plus the one genuinely runner-specific claim (an actionref pointing at a
triggerless RunObject action must still raise the loud testpage-action refusal, naming its
target). Its 27.x override moves by the same +9/+1 because that suite compiles on every
supported BC version. al-language 2191->2199 (issue #2195) reflects 8 corpus tests merged in
StefanMaron/BusinessCentral.AL.Language.Tests#80 (commit
ef52b7e9110005d6e9b8306dbf1a22595654349c), green on real BC 27.5 and 28.3: a subpage part whose
OWN page declares no SourceTable -- a CardPart bound to page globals, the info-box shape -- read
on a host that has a SourceTable and on one that does not, under TestPage.OpenEdit and under
RunModal + a [ModalPageHandler], plus a write through the part control, the durable proof its
OnValidate ran, an asserterror arm proving an Error raised inside the part surfaces, and the
host's own header-field OnValidate reaching the part page through CurrPage.<part>.Page. Suites
60734 and 60763 had measured only the mirror axis (a part WITH a source table on a host without
one), so a part page with no source table of its own was previously unmeasured -- and the runner
refused it out-of-scope. runner-extras does NOT move: the proving tests are all BC-behaviour
claims and live upstream; the runner-repo-local half is a C# mechanism test
(AlRunner.Tests/LiveNavTestPartRecordlessTests.cs), which no AL suite counts. al-language
2204->2221 (issue #2317) reflects three upstream merges the pin bump carries at once: 6 corpus
tests in StefanMaron/BusinessCentral.AL.Language.Tests#87 (commit 7197a79), green on real BC
27.0-28.4, pinning what the All Profile system table (2000000178) answers -- the row an
installed app's profile produces (Caption, ProfileDescription, RoleCenter page id, Enabled,
Promoted, declaring App Name), that only ProfileDescription and not the legacy Description
property feeds the row Description, that Scope::System is empty while Scope::Tenant is not, that
Get() on an undeclared profile id returns false, that deleting an app-owned profile is refused
with the platform's own message, and that a tenant-owned profile (App ID = the empty GUID)
inserts, reads back and deletes; plus 6 in #84 (NumberSequence failure modes raising trappable
AL errors) and 5 in #86 (the experience-tier round trip that sets the session's application
areas), which merged upstream ahead of #87 and are carried along because a pin moves the whole
corpus. Those last two are why known-gaps-number-sequence-trappable.json and
known-gaps-session-application-area.json exist: 10 of the 17 new tests fail here against open
issues #2311/#2320 and #2315, owned by other work in flight -- whoever lands those fixes removes
the matching entries. runner-extras does NOT move: #2317's proving tests are BC-behaviour claims
and live upstream. al-language 2337->2343 (issues #2444/#2455) reflects 6 corpus tests merged in
StefanMaron/BusinessCentral.AL.Language.Tests#112 (commit 53863e42), green on real BC 27.0-28.4:
a static ColumnFilter on a MULTI-DATAITEM JOIN query's columns -- HAVING-style on an aggregated
(Method = Sum) column, dropping whole groups; WHERE-style on a plain column, dropping raw joined
rows; a runtime SetFilter on the SAME aggregated column REPLACING the static one rather than
combining with it; and the no-match arm -- plus a FlowField column selected alongside an
aggregated column in the same join, calculated per joined row and then taking part in the
query's implicit GROUP BY, with a zero-rows negative arm. The single-dataitem path already had
both shapes covered (TestQueryColumnFilter.al, #2418); the JOIN path had neither, which is what
let #2444 and #2455 ship. runner-extras does NOT move: both claims are BC behaviour and live
upstream. al-language 2348->2361 reflects the pin moving from c94093c to 5619e95, which carries
six upstream merges at once, four of them adding tests:
StefanMaron/BusinessCentral.AL.Language.Tests#116 (commit e31d8a0, 1 test -- the Aggregate
Permission Set virtual table reflecting a row written to Tenant Permission Set at runtime), #117
(4e405df, 7 tests -- TestPage enforcement of a field's MinValue/MaxValue properties), #113
(7cb197f, 2 tests -- a TestPage resolving its SourceTable to a table that ships precompiled in a
dependency .app rather than in the bundle), and #119 (6e61554, 3 tests -- a failed guarded
Codeunit.Run trapping AND rolling back its own writes, for BOTH the static Codeunit.Run(...) and
the instance SomeVar.Run(...) spellings).

#118 (1be9f32) and #120 (5619e95) are carried too but add no AL: they are the corpus CI's
object-id checks, which replace branch protection's require-branches-to-be-up-to-date setting as
the mechanism stopping two simultaneously-open PRs from each claiming the same object id.

Full corpus run at 5619e95 against main at 20d68d72: 2361/2361 pass, 0 fail, 0 error -- so this
pin bump is GREEN on its own, unlike the usual case al-language-submodule.md describes, because
every runner fix the four carried test PRs prove had already merged ahead of the pin. The +13
was also derived independently by counting [Test] attributes across the pin range, which agrees
with the runner's measured 2361. runner-extras does NOT move: none of the six carried PRs touch
it.

ANY PR that changes a suite's test or app-group count -- growth included -- MUST bump the
matching number here in the SAME PR, or that PR's CI goes red with a [count-baseline] DROP or
GROWTH diagnostic naming the exact expected/actual numbers to use. runner-extras 198->200 tests
(issue #2238): profile-object-emit-crash adds 2 tests to the existing standalone-suites app
group (no new app.json, so appGroups stays 34) -- proves a codeunit compiled alongside a
crashing `profile` object still runs. The `profile` object type compiles on every supported BC
version, so the byBcVersion 27.0/27.3/27.5 override moves by the same +2 (187->189). al-language
2199->2204 (issue #2251) reflects 5 corpus tests merged in
StefanMaron/BusinessCentral.AL.Language.Tests#81 (commit 5454eaff), green on real BC 27.5 and
28.3: the corpus previously contained zero profile objects.

Adds a profile whose RoleCenter page lives in the same app (binds without trouble) and one whose
RoleCenter page lives in a DEPENDENCY app (the shape that actually broke a compiler in
StefanMaron/BusinessCentral.AL.Runner#2238), each proven by a codeunit declared alongside the
profile asserting concrete computed values, including cross-app codeunit dispatch for the
dependency case. runner-extras does NOT move: #2238's proving test is runner-specific (a
RoleCenter page that resolves nowhere at all, so real BC would reject the app outright) and
already lives in tests/runner-extras/standalone-suites/profile-object-emit-crash. al-language
2221->2224 (issue #2323) reflects 3 corpus tests merged in
StefanMaron/BusinessCentral.AL.Language.Tests#88 (commit 390dbad7), green on real BC on every
minor from 27.0 to 28.4. They extend the existing codeunit 60912 'CFS Tests' and its CFS Header
fixture, which already covered the signed-sum and where-condition halves of the CalcFormula
family, with the exist half: an exist FlowField is Boolean by construction while its source
field is whatever the where clause names, so a leading '-' on one has to be a logical NOT rather
than an arithmetic negation of the source field's type. The two negated fields differ only in
whether that first where-clause field is a Code or an Integer -- a distinction AL does not
expose, and an implementation that negates by the source field's type gets one right and one
wrong. runner-extras does NOT move: the claim is BC behaviour and lives upstream. runner-extras
200->202 (issue #2312): two tests added to the existing aldatabase-cluster-1883 suite inside the
standalone-suites app group (no new app.json, so appGroups stays 34), covering AL's Sid() with a
NON-EMPTY account name on a host with no Windows identity store. Database.Sid compiles on every
supported BC version, so the byBcVersion 27.0/27.3/27.5 override moves by the same +2
(189->191). al-language does NOT move: this claim could not be sent upstream, because the only
Linux-capable BC service tier the corpus CI has (StefanMaron/MsDyn365Bc.On.Linux) replaces
ALDatabase.ALSid(string) in its own StartupHook and so cannot adjudicate it -- see
bc-behavior-tests-go-upstream.md's 'no verdict available' clause and the measurement in issue
#2312. al-language 2224->2250 (issue #2346) reflects two upstream merges this pin bump carries
at once. 24 of the 26 are StefanMaron/BusinessCentral.AL.Language.Tests#90 (commit bebd7e9),
green on real BC 27.0-28.4 in run 33593916797: the backfill for four runner fixes that landed
with C# unit tests only and no upstream proof.

#2308 -- a blank enum member, spelled value(0; " "), matched by the empty string in a filter,
plus the trim, the case-insensitivity, the numeric fallback, and the Option member named with
nothing at all that must NOT match because its name is zero length.

#2310 -- a CalcFormula filter() and a report data item's DataItemTableView both taking an AL
quoted identifier: a name with a space, a name with parentheses, an alternation of a quoted and
an unquoted name, a negated blank member.

#2321 -- an enum's three implementation slots resolving in order: the value's own
Implementation, then DefaultImplementation, then UnknownValueImplementation for an ordinal the
enum does not declare.

#2340 -- Init() applying a Time field's InitValue, with a declared non-zero time, a declared
midnight and no InitValue at all kept apart. Each of the four was confirmed to go RED against
main with its own fix reverted, except the two DataItemTableView tests, which pass either way: a
source-compiled report's view never reaches RecordPatches.AlReportParser, so only
AlRunner.Tests/ReportTableViewQuotingTests.cs guards that half. The other 2 come from #89 (a
guarded Codeunit.Run ending its own transaction), which merged upstream ahead of #90 and is
carried along because a pin moves the whole corpus. That is why
known-gaps-guarded-run-own-transaction.json exists: both fail here against open issue #2332,
whose fix is in flight in PR #2338 -- that PR removes the entries.
known-gaps-option-member-quoting.json is the one gap #90 itself uncovered: an Option field's
OptionMembers keeps AL's identifier quoting here, so its space-named blank member is not matched
by the empty string, tracked as #2345. runner-extras does NOT move: every claim in #90 is BC
behaviour and lives upstream. runner-extras 202->205 tests / 34->35 appGroups (issue #2309): the
one new app group tests/runner-extras/date-virtual-table-window carrying 3 tests. The Date
system virtual table (2000000007) is computed per request on the service tier and covers years 1
through 9999, which the runner cannot materialise whole; it materialises a window and widens it
on demand. These 3 tests pin the runner-specific half of that -- a closed filter bound outside
the default window is materialised on demand (through both the find path and the count path,
which carry different request types), and a range past the row cap raises
RunnerOutOfScopeException on both paths instead of answering with fewer rows. What the rows
themselves say is plain BC behaviour and lives upstream. The suite uses no version-gated
surface, so the byBcVersion 27.0/27.3/27.5 override moves by the same +3/+1 (191->194 tests,
29->30 appGroups). al-language 2263->2267 (issue #2341) reflects 4 corpus tests merged in
StefanMaron/BusinessCentral.AL.Language.Tests#92 (commit db69a6f), green on real BC on every
minor from 27.0 to 28.4 in run 33600418226. They are the corpus's first TestPage suite over a
page the test app does NOT declare -- Base Application page 5 'Currencies', a List over table 4
Currency, reached only through the app.json dependency -- because that is the only way to state
that a TestPage resolves its SourceTable from the page's own declaration wherever the page came
from. FindFirstField positions on the second-seeded row, a non-key control on that same row
reads that row's value, the first-seeded row is reachable too, and a value no row carries raises
BC's whole row-not-found message. All four went RED against main with 'TestPage 5 was never
parsed from AL source' before the fix in this PR, which makes NavTestPageBase_GetMetaTable
consult a dependency .app's SymbolReference.json instead of refusing every page the runner did
not AL-source-compile. runner-extras does NOT move: the claim is BC behaviour and lives
upstream; the runner-repo-local half is a C# mechanism test
(AlRunner.Tests/DependencyPageShapeResolutionTests.cs), which no AL suite counts. runner-extras
205->207 tests / 35->36 appGroups (issue #2197): the one new app group
tests/runner-extras/db-trigger-inject-timing carrying 2 tests. Table-level trigger subscribers
(Insert/Modify/Delete/Rename ordinals) were injected only in bulk passes over NCLMetaTables
already built, so a precompiled Base App table first touched mid-codeunit missed every pass that
could have wired its subscriber and stayed silently unwired depending on test-codeunit order.
This suite pins the runner-specific half of that (the lazy per-table injection timing, against a
real precompiled table, Job, that no other runner-extras suite touches) -- the BC-behaviour half
(a subscriber fires on Delete(true) at all) is already covered upstream by
TestTableEventDispatch (codeunit 60208), against a corpus-owned fixture table built eagerly at
bundle start, which can never exercise this ordering. The suite uses no version-gated surface,
so the byBcVersion 27.0/27.3/27.5 override moves by the same +2/+1 (194->196 tests, 30->31
appGroups). al-language 2267->2302 (issue #2388) reflects the pin moving from db69a6f to
f595446, which carries 11 upstream commits at once:
StefanMaron/BusinessCentral.AL.Language.Tests#91 (8 tests pinning what the Metadata Permission
Set virtual table, 2000000250, answers -- including
MetadataPermissionSet_EveryListedRoleCarriesAName, which was the one failure blocking this bump,
since the runner listed a blank Name for a Caption-less permission set where real BC substitutes
the Role ID), #93 (non-modal Page.Run dispatch to [PageHandler] and TestPage.Trap), #94 (a page
control's own OnValidate error reaching TestPage.SetValue's caller), #95 (the statement-form
BindSubscription throw and its leak-across-tests shape), #96 (a field write with no
First()/New() landing on an editable, insert-allowed list's new-row line), #97 (IncludeSender
subscriber sender position: first/middle/last), #98 (asserterror rollback for multi-write and
in-statement shapes), #99 (a wildcard SetFilter on a query column reading against its source
field), #100 (a query FlowField column reading its calculated value), #101 (a source-defined
query over a dependency table), and #102 (this issue's own settling test: a Caption-less
permission set's Name falls back to its Role ID, measured directly against real BC 27.0-28.4 by
probing Base Application's LOCAL permission set, object id 1001).

Full corpus run at f595446: 2302/2302 pass. runner-extras does NOT move: none of the eleven
carried PRs touch it. runner-extras 207->209 tests / 36->37 appGroups (issue #2411): the one new
app group tests/runner-extras/testpage-trigger-inject-timing carrying 2 tests.

#2412 fixed the lazy trigger/validate-subscriber injection gap at the three sites that construct
a bare Record variable or open a RecordRef; #2411 audited every other NCLMetaTable-building call
site and found one more that hands a live NavRecord to AL without it -- TestPageFactory
.TryBuildBlankRecord, shared by a directly-opened TestPage and a plain Page-variable's Rec. This
suite pins the runner-specific half against a real precompiled table, Warehouse Employee, that
no other runner-extras suite touches; it is a regression/contract guard rather than a RED/GREEN
proof of that specific diff, because BC's own SetSourceTable/NewRecordAsync machinery already
wires the subscriber via one of #2412's three already-fixed sites (xRec construction) before
Insert can dispatch on any live TestPage/Page-variable with a real compiled page object --
measured by stack trace during this issue's investigation, on two unrelated precompiled tables
tried. The suite uses no version-gated surface, so the byBcVersion 27.0/27.3/27.5 override moves
by the same +2/+1 (196->198 tests, 31->32 appGroups). al-language 2329->2333 (issue #2466)
reflects StefanMaron/BusinessCentral.AL.Language.Tests#110, green on real BC 27.0-28.4 in run
33721610602: two new files/contracts pinning where BindSubscription's binding is scoped.
TestEventManualBindingCrossCodeunit (codeunit 60244/60245) is the corpus half of #2466's own fix
-- a manual subscription left open by one test CODEUNIT does not survive into the NEXT test
codeunit's run, the CROSS-codeunit boundary TestIsolation = Codeunit starts fresh at.
TestEventManualBinding's new Contract 10 (2 tests) is the WITHIN-codeunit half: a binding made
through a LOCAL codeunit variable does NOT survive into the next [Test] on the same codeunit
(unlike Contract 9's GLOBAL-variable case, which does and is unchanged) -- the runner does not
yet implement the per-instance disposal real BC does here, tracked as known-gaps-events.json /
#2476. runner-extras does NOT move: neither addition touches it. runner-extras 209->211 tests /
37->38 appGroups (issue #2452): the one new app group
tests/runner-extras/bundle-page-over-dep-table carrying 2 tests, covering a bundle-compiled page
whose SourceTable names a table shipping precompiled in a dependency .app (Base Application
Salesperson/Purchaser). Deliberately its OWN app group rather than folded into
microsoft-dependencies or standalone-suites: both of those already declare a Record variable of
a dependency table elsewhere in the suite, which pre-populates the runner-internal by-ID lookup
during that OTHER codeunit's compile and masks the exact by-NAME resolution gap this suite
exists to prove. The suite declares application: 27.0.0.0 so it compiles on every supported BC
version, moving the byBcVersion 27.0/27.3/27.5 override by the same +2/+1 (198->200 tests,
32->33 appGroups). al-language 2343->2346 reflects 3 corpus tests merged in
StefanMaron/BusinessCentral.AL.Language.Tests#114 (commit f8855db), green on real BC 27.5 and
28.3: TestTransactionModelAutoRollback (codeunit 60899) pins that a [Test] procedure carrying
[TransactionModel(TransactionModel::AutoRollback)] gets its own uncommitted writes rolled back
the moment it finishes, even under TestIsolation = Codeunit (the default), which on its own
leaves an uncommitted write visible to the next [Test] on the same codeunit instance
(TestIsolationRollbackScope, 60897) -- the attribute is a per-test-method override, not a
codeunit-wide setting. The control arm (a plain [Test] with no override) confirms the default
behaviour is unaffected. runner-extras does NOT move: the claim is BC behaviour and lives
upstream. al-language 2346->2348 (issue #2490) reflects the pin moving to c94093c, which carries
StefanMaron/BusinessCentral.AL.Language.Tests#115 (2 tests, codeunit 60022, TestPage control
coverage for a tableextension-added field -- the corpus proof for #2490's runner fix:
GetPageControlFieldMap/ResolveDependencyControlField/TryResolveDependencyFieldId never consulted
_parsedExtensionFields, and TestPageFactory.TryBuildBlankRecord never called
RegisterParsedTableExtensions, so a TestPage control bound to an extension field threw
testpage-control-binding and, once that was fixed, its OnValidate failed to dispatch).
runner-extras does NOT move: the claim is BC behaviour and lives upstream. runner-extras
213->215 tests / 39->41 appGroups (issue #2463): two new app groups,
tableext-eviction-field-trigger-timing-dep (0 tests -- an Install-subtype codeunit that
materializes a record during its own install, forcing the table's field-trigger wiring before
the sibling app's tableextension is parsed) and tableext-eviction-field-trigger-timing (2
tests), covering RecordPatches.EvictCachedMetaTableForBaseTable dropping a base table's cached
NCLMetaTable on a tableextension field merge without also dropping it from
_fieldTriggersWiredTables -- the rebuilt NCLMetaTable's fields carried no ValidateHandler at
all, so a completely unrelated field's OnValidate trigger silently stopped running for the rest
of the process. Neither app declares application, so both suites compile on every supported BC
version; the byBcVersion 27.0/27.3/27.5 override moves by the same +2/+2 (202->204 tests, 34->36
appGroups). This is a runner-internal caching defect, not a BC-behaviour claim, so it stays in
runner-extras rather than the upstream corpus. runner-extras 215->218 tests / 41->42 appGroups
(issue #2514): the one new app group tests/runner-extras/page-background-task-oos carrying 3
tests, covering CurrPage.EnqueueBackgroundTask and TestPage.RunPageBackgroundTask refusing
loudly with RunnerOutOfScopeException instead of crashing on an internal NavSession/NavTenant
exception the runner's skeleton cannot yet answer. The suite declares application: 27.0.0.0 and
uses no version-gated surface, so it compiles on every supported BC version and the byBcVersion
27.0/27.3/27.5 override moves by the same +3/+1 (204->207 tests, 36->37 appGroups). al-language
2361->2417 (issue #2201) reflects the pin moving from 5619e95 to 040fbdd, folded into #2600's
own fix PR per al-language-submodule.md.

#130 (commit 040fbdd) is #2201's own proving content: 3 new tests in existing codeunit 60807
(SourceTableTemporary part sharing one instance with its host, including the host pushing rows
from its own OnOpenPage before the TestPage side ever touches the part, deleting the positioned
row, and the modal-handler shape) plus 2 new tests in existing codeunit 60803 (a page-globals
part's host write visible through the SAME instance from the TestPage side, direct-open and
modal-handler -- the shape #2201's own repro used). The other 56 new tests are five unrelated
upstream merges the pin carries along because a pin moves the whole corpus, none touched by
#2201's fix: #121 (3661f1d, 1 test -- SourceTable Integer ListPart self-loaded), #124 (25dac77,
TableCaption coverage) and #126 (4cff8bc, quoted-identifier option members) both already pass
here because their runner-side fixes had already landed on main ahead of this bump;
#125/#127/#122/#123/#128/#129/#131/#132 add the rest. Of those, three test areas surfaced
GENUINE runner gaps this bump does not fix: codeunit 60624
TempIntegerPart_SelfLoaded_FirstRowIsNumberOne (#121, decimal formatting: '10' vs real BC's
'10.00' -- known-gaps-testpage-part-instance-pin-bump.json, #2634), codeunit 60835
TestInstallEvent_* (#129, an integration event raised from OnInstallAppPerCompany never reaches
its subscriber -- same file, #2635), codeunit 60958 Record_FeatureKey_* (#132, the Feature Key
virtual table 2000000211 has no materialisation yet and answers zero rows -- same file, #2636),
and codeunit 60263 TwoControlsOverOneField_AnswerIndependently (#128, two TestPage controls
bound to the same source field do not answer independently -- same file, #2637).

Full corpus run at 040fbdd: 2410/2417 pass, 7 known-gap failures classified above. runner-extras
does NOT move: none of the six carried PRs touch it.runner-extras 218->222 tests / 42->43
appGroups (issue #2581): the one new app group tests/runner-extras/windows-language-license-stub
carrying 4 tests. The Windows Language virtual table (2000000045) has six license-derived
columns and four installed-resource columns that the runner cannot answer from any source -- BC
fills them from License.HasLanguagePermission and from satellite assemblies, and the runner has
neither, with get_License() throwing rather than returning a no-license answer to copy. Their
values are therefore CHOSEN (permitted, and none, respectively) and this suite is what makes
that choice declared rather than silent: it pins both seams (StubbedLicensePermission /
StubbedLocalizationResources) plus a control arm proving the stub did not leak into the six
columns that DO have a source. What the table says about a language is BC behaviour and lives
upstream. The suite declares platform 27.0.0.0 and uses no version-gated surface, so it compiles
on every supported BC version and the byBcVersion 27.0/27.3/27.5 override moves by the same
+4/+1 (207->211 tests, 37->38 appGroups). issue #2514's page-background-task-oos suite (3 tests
/ 1 appGroup) is REMOVED by this issue's own fix: CurrPage.EnqueueBackgroundTask and
TestPage.RunPageBackgroundTask now run the worker codeunit inline against the current session
instead of refusing loudly, so there is nothing left to prove out-of-scope; the BC-behaviour
claim moved upstream (see issue #2514 for the corpus PR). runner-extras drops by 3 tests / 1
appGroup off whatever main's own count was at rebase time. al-language 2417->2441 (issue #2514)
reflects the pin moving from 5619e95 to 5d519c1, which carries seventeen upstream merges at
once, sixteen of them adding tests: StefanMaron/BusinessCentral.AL.Language.Tests#121
(GoToRecord duplicate field captions), #122 (GoToRecord not-found probe keeps the
originally-positioned row), #123 (TableCaption differing from the table name), #124 (two
controls over the same source field), #125 (TestPage control property expressions beyond a bare
identifier), #126 (quoted-identifier option members), #127 (CodeUnit Metadata virtual table),
#128 (SourceTable Integer temporary part opens empty), #129 (an integration event raised from an
install trigger), #130 (Time Zone virtual table), #131 (a SourceTableTemporary part sharing one
instance with its host), #132 (Feature Key virtual table), #133 (when a control's
Visible/Editable/Enabled are evaluated), #134 (Windows Language virtual table), #136 (Query
ReverseSign column property), #139 (Record SystemId uniqueness on Insert and immutability on
Modify), and this issue's own #135 (page background tasks under a TestPage).

#120 is carried too but adds no AL: the corpus CI's own duplicate-id check. Two of the seventeen
carried tests fail here against pre-existing runner gaps the newer corpus content happened to
newly cover, unrelated to #2514's own changes -- known-gaps-record-systemid-duplicate.json
(#2657, Record_Insert_DuplicateSystemId_Refused, #139) and
known-gaps-testpage-visible-reopen.json (#2658, ReopeningThePage_IsHowTheNewVisibleIsObserved,
#133). This issue's own EnqueueBackgroundTask_UnhandledErrorPropagates (#135) also fails here,
against #2656 -- a general TestPage error-teardown behaviour #2514 did not pursue.

Full corpus run at 5d519c1 with those three known-gap entries in place: 2441/2441 pass (2
pass-oos, 7 pass-known-gap, 1 pass-divergence), 0 fail, 0 error. runner-extras does not move:
none of the seventeen carried PRs touch it.

The 2441->2448 bump reflects 7 tests merged upstream in
StefanMaron/BusinessCentral.AL.Language.Tests#138 (codeunit 60130 "Test Media Png Import": Media
field ImportStream/ExportStream over a valid PNG, and five malformed-PNG arms -- corrupt IHDR
CRC, signature only, truncated mid-IHDR, zero-width IHDR, and non-PNG content falling back to
octet-stream). All 7 pass on the runner unchanged, so this bump carries no new expectation
entries.

Full corpus run at a026d84: 2448/2448 pass (2 pass-oos, 5 pass-known-gap, 1 pass-divergence), 0
fail, 0 error. pass-known-gap moves 7->5 because #2647 dropped two Feature Key entries that main
already passes; it is not a corpus change. runner-extras does not move: #138 adds AL tests only
and #140 changes a corpus-repo CI script. al-language 2448->2456 (issue #2656) reflects the pin
moving to 1725c51, which carries two upstream merges:
StefanMaron/BusinessCentral.AL.Language.Tests#141 (3 tests, subpage part firing order for
OnAfterGetCurrRecord -- unrelated to #2656, carried along because a pin moves the whole corpus)
and this issue's own #142 (5 tests, codeunit 60795 'TestPage ErrTeardown Tests': an unhandled
OnAfterGetRecord error during GoToRecord on an already-open TestPage tears the page down --
GoToRecord itself, a following Close(), and a following field read all raise BC's own 'The
TestPage is not open.' -- and, as the negative control, an unhandled OnValidate or OnAction
error does NOT tear the page down, propagating its own text instead with Close() succeeding
normally afterward). This bump also flips the pre-existing codeunit 60793 'Test Page BgTask
Tests' EnqueueBackgroundTask_UnhandledErrorPropagates GREEN (from #2514's own
known-gaps-page-background-task-testpage-teardown.json entry, now removed) without moving the
total, since that test already existed in the corpus at the prior pin. runner-extras does not
move: neither carried PR touches it. runner-extras 219->221 tests / 42->44 appGroups (issue
#2510): two new app groups, tableext-eviction-subscriber-timing-dep (0 tests -- an
Install-subtype codeunit that materializes and validates a record during its own install,
forcing the table's event-subscriber wiring before the sibling app's tableextension is parsed)
and tableext-eviction-subscriber-timing (2 tests), covering the subscriber-side sibling of #2463
left unfixed by #2506: EventSubscriberPatches._injectedSubscriberMethods is keyed by MethodInfo
only, with no per-table index, so a table-level and a field-validate event subscriber already
injected onto the OLD NCLMetaTable instance were silently skipped on the re-injection pass for
the instance RecordPatches.EvictCachedMetaTableForBaseTable rebuilds after a tableextension
merge. Neither app declares application, so both suites compile on every supported BC version;
the byBcVersion 27.0/27.3/27.5 override moves by the same +2/+2 (208->210 tests, 37->39
appGroups). This is a runner-internal caching defect, not a BC-behaviour claim, so it stays in
runner-extras rather than the upstream corpus. The runner-extras 232->234 / 45->46 (27.x:
221->223 / 40->41) bump (issue #2725) is the new table-connection-live-oos suite: two tests, one
app group, no version gating.

The al-language 2456->2464 bump (issue #2725) is the corpus pin moving to 466ccf57, which merged
StefanMaron/BusinessCentral.AL.Language.Tests#146: eight tests pinning the CRM table-connection
contract and the '@@test@@' test provider (RegisterTableConnection / HasTableConnection / Set-
and GetDefaultTableConnection / UnregisterTableConnection, plus Insert into a TableType = CRM
table over the test connection and BC's own 'not registered' error without one). Measured on an
actual run against the new pin, not computed: 2464/2464.

The 234->237 bump (issue #2729) adds three PrecompiledPage_* tests to
runner-extras/microsoft-dependencies, pinning that a precompiled dependency page's lifecycle
triggers run at all: the runner resolved the SYNC trigger name, which on a page BC emitted in
the async flavour binds NavForm's empty base body, so every Base Application page opened with
dead triggers. microsoft-dependencies declares no BC28-only dependency, so all three run on
every supported version and the byBcVersion 27.0/27.3/27.5 override moves by the same +3
(223->226). appGroups is unchanged: the tests joined an existing bundle. The runner-extras
237->243 / 46->47 (27.x: 226->232 / 41->42) bump (issue #2528) is the new
precompiled-table-relation suite: six tests, one app group, no version gating.

The 243->246 bump (issue #2733) adds three PrecompiledCodeunit_StartSession_* tests to
runner-extras/microsoft-dependencies, pinning that StartSession on a PRECOMPILED worker codeunit
runs its OnRun body: the runner resolved the SYNC trigger name, which on a codeunit BC emitted
as OnRunAsync binds NavCodeunit's empty base body, so StartSession returned true having executed
nothing. microsoft-dependencies declares no BC28-only dependency and Base Application codeunits
7002/7003 exist on every supported version, so the byBcVersion 27.0/27.3/27.5 override moves by
the same +3 (232->235). appGroups is unchanged: the tests joined an existing bundle. The
runner-extras 246->250 / 47->48 (27.x: 235->239 / 42->43) bump (issue #2519) is the new
object-metadata-system-table suite: four tests, one app group, no version gating, so the
byBcVersion 27.0/27.3/27.5 override moves by the same +4 and appGroups by the same +1. It pins
what the runner synthesises for the Object Metadata application-database system table
(2000000071), which had no rows at all, so a FindLast over it raised. NOTE for whoever resolves
the next conflict here: the appGroups half of this bump does NOT show up as a git conflict. Both
main and the branch read 47/42 -- main by adding #2528's suite to the base 46/41, the branch by
adding this one -- so git auto-merges them to 47 and silently loses one app group. The tests
half conflicts and gets attention; the appGroups half does not. Re-measure both halves rather
than trusting a clean auto-merge.

The runner-extras 250->256 / 48->49 bump (27.x: 239->245 / 43->44, issue #2524) adds the
temporary-virtual-table-isolation suite: six tests, one app group, no application and no
BC28-only dependency, so the byBcVersion override moves by the same +6/+1. This file conflicted
on EVERY rebase of that PR -- four other suites landed on main while it was open (#2781, #2752,
#2793, #2778) -- and on the first of those git auto-merged the VALUES half cleanly while
flagging only the _comment string, silently keeping the then-current numbers. Every figure here
was re-measured on the rebased tree; none was carried across a rebase or derived by arithmetic
alone.

The al-language 2464->2496 bump is the corpus pin moving 466ccf57 -> bce7c87f, carrying
StefanMaron/BusinessCentral.AL.Language.Tests#155 (6 tests, the upstream half of #2524) and --
corpus master being linear -- also #144, #151, #152 and #154 from other agents, 26 further
tests. Ten of those fail here for reasons unrelated to #2524 and are declared in
known-gaps-testpage-blank-temporal.json (#2361) and known-gaps-testpage-control-property.json
(#2596); both issues stay open after this PR merges and their fixes remove the entries.

The al-language 2496->2500 bump (issue #2783) is the corpus pin moving bce7c87 -> e493094, which
is two upstream commits: StefanMaron/BusinessCentral.AL.Language.Tests#157 (a CI workflow
change, no .al files, 0 tests) and #159 (4 tests, codeunit 60270, RecordRef.Open scope-checked
against the app's compilation target -- the upstream half of this issue). Neither file is
version-gated, so the count is identical on every leg and stays a plain 'default' with no
byBcVersion override, and appGroups is unchanged at 1 because the tests joined the existing
bundle. Measured on an actual run against the new pin, not computed.

The al-language 2500->2523 bump finishes the catch-up, moving the corpus pin from e493094 to
a307df8 after the partial bumps that took it to bce7c87 and then e493094. Six more upstream test
PRs (StefanMaron/BusinessCentral.AL.Language.Tests #144, #145, #147, #148, #149 and #150) plus a
CI-only change (#157): TestPage action dispatch for C#-keyword action names, SubPageLink
const()/filter() coverage, SubPageLink stamping on a non-key field, New()'s buffer reset and
whether it validates the field it stamped, ModifyAll refusing the SystemId field, and
StartSession's refusal inside a test codeunit. Measured on an actual run against the new pin,
not computed. appGroups is unchanged: every new test joined the single existing al-language app
group, and al-language carries no byBcVersion override -- CI's eight legs confirm whether one is
needed. Only the al-language number moved here; the runner-extras values are main's, taken
unmodified through a rebase conflict in this file.

The runner-extras `testpage-lookup-tablerelation-oos` group (3 tests, issue #2775) is a new app
group, so it adds a line rather than moving a number: expected tests on every leg go up by 3 and
expected app groups by 1, both derived from the line and neither written out anywhere. No
`absentOn` -- the bundle declares `platform`/`application` 27.0.0.0, so it runs on all eight
legs. The 3 is measured from an actual run of that bundle, not counted off the source.

The runner-extras `session-user-row` group (4 tests, issue #2296) is a new bundle: the runner
seeds its own session user into the User system table (2000000120), and the suite pins that the
row exists with the identity BcRuntime put on the skeleton NavSession, that it carries the User
Property (2000000121) companion row BC creates alongside every user, that a TableRelation to
User."User Security ID" accepts UserSecurityId(), and -- the negative control -- that a security
id belonging to no user is still refused. No `absentOn`: the bundle declares
`"platform": "27.0.0.0"`, names only System-application tables that exist on 27.0 and carries no
preprocessor gating, so it contributes the same 4 tests on every leg.

The group's own line carries the only number that gates -- `"session-user-row": { "tests": 4 }`
-- and the suite total is derived from the lines rather than written down, so no aggregate here
is load-bearing. An earlier draft of this paragraph recorded "268 tests across 51 app groups, up
from 264/50" as the suite total; that was true when it was measured and had already gone stale
by the time this branch merged, because several other groups landed on `main` in between. The
final run on this branch reports **282**. The lesson, not the number, is the point: a suite-wide
total written into this file dates the moment it was measured, while the per-group line does
not.

The al-language number moves 2599 -> 2610 with the submodule pin bump to corpus
`9ba6f581`, which takes two corpus merges. Eight of the eleven are codeunit 60996 "TPDL
Tests" (corpus PR #176), pinning what typing into the draft line of a subpage part that
carries a SubPageLink creates -- the upstream half of issue #2923. The other three extend
`TestQueryFlowFieldColumn` with how a flow filter reaches a query's FlowField column
(corpus PR #175); their runner-side fix is already on main (#2947), which is why the bump
does not need a second fix folded in for them.

2610 is measured from an actual run against the new pin, not counted off the source.
appGroups is unchanged at 1: every new test joined the single existing al-language app
group, and al-language carries no byBcVersion override, so CI's eight legs are what confirm
one is not needed. Only the al-language number moved; runner-extras and
al-language-internals-fixture are main's values, untouched.

Worth recording about codeunit 60996 specifically, because it is the reason two of its
assertions read the way they do: a service tier refuted the test twice before it merged.
Run 33995429394 answered `H1` where the file asserted the draft line reads blank in the
column a SubPageLink constrains, and run 33997895349 answered `NEWREC` where it asserted
the page's OnNewRecord had not yet run for that line. Both are now asserted at the measured
value. The runner change in this PR follows those measurements rather than the other way
round.

The al-language number moves 2610 -> 2645 with the submodule pin bump to corpus `0309cec6`,
which takes seven corpus merges rather than one. Eight of the thirty-five are codeunit 60276
"MQC Tests" (corpus PR #186), pinning the close lifecycle of a page the platform closes for a
`[ModalPageHandler]` / `[PageHandler]` -- the upstream half of issue #3050, whose runner fix is
folded into this same PR. 2645 is measured from an actual run against the new pin, not counted
off the source. appGroups is unchanged at 1. The other twenty-seven arrived because the
maintainer's pin sat at `9ba6f581` while six further corpus PRs merged on top of it, and a pin
only moves forward as a whole; all twenty-seven pass on all eight CI legs.

The same bump is the first pin to contain the `tests/al-language-onprem` app at all -- which
`scripts/corpus-app-dirs.py` enumerates automatically, so it began executing 19 tests nothing
had declared for. A suite line appears for it here at **19**, and two of its tests get entries:
codeunit 61201 "Test Published App Sys Table",
`PublishedApplication_ThisApp_PackageIdIsItsRuntimePackageId` and
`PublishedApplication_CalcFields_Installed_IsTrueForThisApp` -> #3066, which already records
that a real service tier contradicts the runner on both. Seven other tests in that codeunit
pass, and the pair fails identically on every one of the eight legs. `--count-baseline` accepted
the new suite with no line at all, so the line is added deliberately rather than because the
gate demanded it: a suite the baseline does not name is a suite whose disappearance the gate
cannot notice.

Worth recording, because it cost two CI rounds. Three known-gap entries were added here first
on the strength of a local run: codeunit 60455 "TPARO Tests" (5 tests), 60405
`AppCanRegisterItsOwnTableOnTheAllowedList`, and 60490
`TableEventSubscriberInAnotherApp_ErrorReachesTheCaller`. The matrix split them in two:

- **60455 does not fail on CI at all.** The manifest's drift guard said so directly -- "Test
  passed cleanly but manifest declares expect-fail-known-gap" on the 27.0, 27.3 and 28.1 legs of
  run 34026008142 -- and the entry is gone. Those five failed only on this developer box, whose
  BC artifact is 28.1.49838.53910 against the matrix's 28.1.49838.54308.
- **60405 and 60490 do fail on CI**, and their entries stay. The evidence is the pair of runs:
  with the entries present, run 34025479051 reported the corpus 2645P/0F/0E on every leg with no
  drift complaint, which only happens if both genuinely failed and were reclassified; with the
  entries removed, run 34026591322's BC 27.3 leg went red naming exactly those two.

Both halves are the drift guard doing its job in each direction, which is what made a wrong
entry cheap to find. The general lesson stands: one local run is not the measurement that
decides a manifest entry.

Written by agent fbk-1 (automated implementation agent).

### 2645 -> 2648 (pin 0309cec6 -> 83b54a91, PR #3067)

One corpus commit, and it is the whole reason for the bump:
StefanMaron/BusinessCentral.AL.Language.Tests#188, three tests in codeunit 60293 "Test Reten
Pol Allowed Tables" pinning that Base Application's own table 405 "Change Log Entry" is on the
retention-policy allowed list, that the registration carries the concrete date field
(2000000001 SystemCreatedAt), and that a table nobody registers (18 "Customer") is absent.

They are the upstream proof for #3054. Without them the eight legs run a corpus that cannot
observe #3067's loader fix at all: on `main` the Company-Initialize abort is swallowed, so
every affected test passes either way and the PR would carry no CI evidence for its own claim.
Two of the three fail on a red BC build before the fix and pass after it.

Nothing else comes with the bump. `83b54a91` is the immediate child of the pin `main` already
carries, so this is one corpus commit rather than the ten-commit jump an earlier revision of
this branch had to take before `main` caught up — and no new `expect-fail-known-gap` entry is
owed, because `main` already declares the three that arrived with the intermediate commits
(#3066, #3049, #2932) and #3061 fixed the fourth (60276, OnQueryClosePage on a handler-driven
page).

2648 is measured from the runner's own `--count-baseline` GROWTH output ("expected 2645, actual
2648"), on BC 27.3 and BC 28.1, not counted off the source. Both legs reported the same number,
so no `byBcVersion` override; the eight CI legs are what confirm that. appGroups is unchanged at
1 — all three tests joined the single existing al-language app group. `runner-extras`,
`al-language-internals-fixture` and `al-language-onprem` are `main`'s values, untouched.

## 2648 -> 2661 — corpus pin 83b54a91 (#188) -> 3268bf1b (#191)

Folded into the fix PR for #3012, which is what the bump exists to enable: corpus #189 added
`codeunit 60444 "CalcFields Field Class Tests"` (7 tests), the RED -> GREEN for
`fix(record): refuse a CalcFields field that is not a FlowField or a BLOB`. Three corpus
commits come in with it, because the corpus history is linear: **#185** (360e1f0, a RunObject
action with no handler bound), **#189** (3060794, the CalcFields refusals) and **#191**
(3268bf1, Table Metadata for a table declaring no `DataClassification`).

+13 tests, read off the guard's own GROWTH line ("expected 2648, actual 2661") on BC 28.1, not
counted off the source. No file in the range carries a `#if` version gate, so the count is
uniform across the eight legs and stays a single `default`; appGroups is unchanged at 1, and
`runner-extras`, `al-language-internals-fixture` and `al-language-onprem` are `main`'s values,
untouched.

**Not pinned at corpus master head, deliberately.** Master moved on to 861a566 (#193, the close
lifecycle of a page that closes itself) while this was in flight. 3268bf1b is the last commit
this branch has actually measured, and #193 lands in the handlers/close-lifecycle area where
#3061 has just been fixed and more is open — taking it unverified is how the previous revision
of this branch ended up carrying #188's failures for a defect (#3054) that belonged to another
PR. A later bump can take it after measuring it.

Two of the thirteen fail, both from #185, and both get entries in
`known-gaps-testpage-runobject-no-handler.json` linking **#2975**, which stays open: real BC
opens a RunObject target with no handler bound and runs its OnOpenPage, and the runner raises
`NavNCLMissingUIHandlerException`. #2951 made an action's RunObject perform its target, so the
sibling eight-test suite (codeunit 60455) passes; the no-handler arm was held out of that suite
deliberately while the question was open.

Those two entries are **confirmed on all eight legs**, not on one local run: at the previous
revision of this branch (head `ac70dac1`, run 34029802786) no leg reported "Test passed cleanly
but manifest declares expect-fail-known-gap", including the three legs that were otherwise
green. That is the drift complaint which retired codeunit 60455's five entries two bumps ago,
and it settles the caveat this file recorded for them.

Written by agent stma-auto-1 (automated implementation agent).

## 2661 -> 2665 — corpus pin 3268bf1b (#191) -> 861a5662 (#193)

Folded into the fix PR for #3091, which is what the bump exists to enable: corpus #193 added
`codeunit 60296 "MQC Self Close Tests"` (4 tests), the RED -> GREEN for a page that closes
ITSELF -- `CurrPage.Close()` from its own action's OnAction, under a `[ModalPageHandler]`. Those
four are the entire delta. The corpus commit is the direct child of `3268bf1b`, the pin the
section above deliberately stopped at, so nothing else rides along and there is no collateral
to classify.

+4 tests, measured from an actual run against the new pin, not counted off the source.
appGroups is unchanged at 1; `runner-extras`, `al-language-internals-fixture` (0) and
`al-language-onprem` (19) are `main`'s values, untouched.

**No new known-gap entries, and the earlier revision of this branch was wrong to add two files
of them.** At head `b1215021` the branch was based on a `main` whose pin was still `83b54a91`,
so bumping to `861a5662` pulled in #185, #189 and #191 as collateral and eight of their tests
failed. Both families were declared here. Then `main` moved: PR #3079 merged as `1aef9e75` and
brought exactly that collateral with it, correctly classified. What was left on this branch was
worse than redundant --

- `known-gaps-runobject-no-handler.json` declared the same two `TPARONH Tests` methods as
  `main`'s own `known-gaps-testpage-runobject-no-handler.json`, and
  `AlRunner/Infrastructure/ExpectationManifest.cs:120-131` throws
  `Expectation for {CodeunitName}.{Method} declared in multiple files` — a hard load failure on
  every leg, before a single test runs. Not drift; the run would not have started.
- `known-gaps-calcfields-field-class.json` declared six tests that #3079's fix makes pass, and
  linked **#3012**, which that same PR closed. An entry pointing at a closed issue is precisely
  the failure this file records having avoided with #2931 one bump earlier.

Both files are deleted. `main`'s classification stands, and the only thing this bump adds to the
manifest is nothing at all.

Written by agent fbk-1 (automated implementation agent).

## `microsoft-dependencies` 24 -> 26 (issue #2860)

Two tests added to `MicrosoftDependencyTests`, pinning `PopulateAllFields` end to end on a page
that ships PRECOMPILED inside a dependency `.app` — the shape whose runtime metadata comes from
`RecordPatches.DependencyPageMetadataXml` rather than from the AL compiler.

They are a pair, and only the pair proves anything. Base Application page 367 "Post Codes"
declares `PopulateAllFields = true` over table 225, primary key `(Code, City)`, so filtering
`"Country/Region Code"` (field 4, outside the key) and calling `New()` must carry the filter onto
the new row. Page 427 "Payment Methods" declares nothing over table 289, key `(Code)`, so
filtering `Description` (field 2) and calling `New()` must leave it blank. An implementation that
wrote the attribute unconditionally would fail the second; one that never wrote it fails the
first.

No `absentOn`: both pages and both tables exist on every supported leg, and the bundle already
declares `"application": "27.0.0.0"`. The 26 is measured from a run of the bundle, not counted
off the source.

## +6 runner-extras tests: `permission-set-assignment` (#3039)

`tests/runner-extras/permission-set-assignment` is new, so `runner-extras` goes from 55 app
groups / 306 tests to 56 / 312. No existing group's count moves. (Rebased onto the
`microsoft-dependencies` 24 -> 26 bump above, which is why the starting total is 306 and not
the 304 measured before that bump landed.)

The suite is the RED -> GREEN for #3039: BC's
`PermissionManagement.IsPermissionSetAssignedAsync` ends in
`session.Permissions.HasRole(...)`, `NavSession.Permissions` is null on the skeleton session,
and every AL path through `NavUserAccountHelper.IsPermissionSetAssigned` therefore raised
`NavNCLDotNetInvokeException` on a valid `User.Modify`. Five of the six tests fail without the
fix; the sixth is the `asserterror` control that must keep passing either way, because it
proves codeunit 9002's subscriber still runs rather than having been bypassed.

No `absentOn`: the bundle declares `"platform": "27.0.0.0"` / `"application": "27.0.0.0"` and
uses only codeunit 152 `"User Permissions"` and table 2000000053 `"Access Control"`, both of
which exist across the supported range. That was not validated locally — a self-built runner
is compiled against Ncl 28.x and cannot run 27.x artifacts — so the 27.0/27.3/27.5 legs are
what adjudicate it. If any of them discovers a different number, the exit-4 message names it
and the line gains an `absentOn`.

Written by agent impl-13 (automated implementation agent).

## 2665 -> 2676 — corpus pin 861a5662 (#193) -> 6e198a97 (#195, #190, #192, #194, #198)

+11 tests, and the number is the guard's own `actual`, read off the run that reported

```
[count-baseline] GROWTH: suite 'al-language' tests count: expected 2668, actual 2676 (BC 28.1)
```

not computed from the diff. It was read twice, because the pin moved mid-task: the first four
corpus commits put it at 2668, and corpus #198 merged while this branch was being measured and
took it to 2676. Both numbers came from the guard.

Where the eleven come from:

| corpus PR | tests | what it adds |
|---|---|---|
| #190 | +1 | `Validate_RelationWithWhereFieldLink_NarrowsToTheReferencingRowsOwnGroup` in `fieldref/TestFieldRefRelation.al`, alongside the rename/renumber of `ALTRelationWhereField` that makes a swapped `where(A = field(B))` role detectable at all |
| #192 | +1 | `PlainModal_HasNoBuiltInCancelAction` in `handlers/TestPageModalQueryClose_Tests.al` |
| #194 | +1 | `ControlPageRunOnTheLoggingTargetWithoutAHandlerIsRefusedAndOpensNothing` in `handlers/TestPageActionRunObjectNoHandler_Tests.al`, plus the `SingleInstance` probe codeunit 60286 it reads |
| #195 | 0 | renames a test; the version literal moves into a `Label` |
| #198 | +8 | `record/TestCodeunitInventoryOrder.al`, codeunit 60964, pinning the row order of the Codeunit Metadata and AllObjWithCaption inventories |

#192 also renames `Modal_HandlerInvokesNothing_ObservedCloseLifecycle` to `LookupModal_...` and
`LookupCancelHandler` to `CancelHandler`, and drops two log assertions it argues are
unfalsifiable; none of that moves the count.

`appGroups` is unchanged at 1. `runner-extras` (304 tests, run separately and green),
`al-language-internals-fixture` (0) and `al-language-onprem` (19) are `main`'s values,
untouched — #192's only OnPrem change derives four `Published Application` version parts from
`ModuleInfo` instead of writing `1/0/0/0` out, which adds no test.

**One runner gap, fixed in this PR rather than declared.** `PlainModal_HasNoBuiltInCancelAction`
was the only red test of the eleven: real BC refuses `TestPage.Cancel()` on a non-lookup page
whose PageType gives the client no dialog chrome, and the runner offered it. `LiveNavTestPage`
now gates plain `Cancel` on the PageType. **No new expectation entries** — the other ten passed
unchanged, including all eight of #198's, which their author expected to pass only by
coincidence.

Written by agent stma-auto-1 (automated implementation agent), cycle 138.

## +2 runner-extras tests: `task-scheduler-oos` 6 -> 8 (issue #3212)

`TskPageOpen.Page.al` adds table 65604 and two pages inside the suite's existing
`65600-65609` range, and `TskTests.Codeunit.al` adds two tests over them. No new app group:
the suite already existed, so `runner-extras` gains two tests and no group.

The pair proves a defect found while fixing #3212, not the task scheduler itself.
`RunnerTestPageState.MarkOpened` drives a page's `OnOpenPage` from inside BC's rewritten
`NavTestPage.Open`, behind a catch-all filtered on `ex is not NavBaseException`. A
`RunnerOutOfScopeException` is deliberately a plain `System.Exception`, so that filter
swallowed every refusal raised from an `OnOpenPage` — the page opened as though the trigger
had succeeded and the test failed later on something downstream, with the surface and the
reason gone. `PageOnOpenPage_Refusal_ReachesTheCaller` failed with "An error was expected
inside an ASSERTERROR statement" before the fix. `PageOnOpenPage_WithoutARefusal_
StillRunsAndThePageOpens` is the scoping control and passed both before and after: widening
the filter must not turn into letting unrelated failures escape.

The task-scheduler surface stands in for `System.Drawing` because it refuses without needing
anything from the Base Application, and this suite already owned it. `al-language`,
`al-language-internals-fixture`, `al-language-onprem` and every other `runner-extras` group
are untouched; no corpus pin moves in this PR.

Written by agent stma-auto-1 (automated implementation agent), cycle 147.

## runner-extras 316 → 326 — `user-system-table-triggers` (#2983, #2356)

+10 tests in one new `tests/runner-extras/user-system-table-triggers` suite (id range
65620-65639), proving that the runner now runs BC's `SystemTableTriggers` arms for the User
system table (2000000120): the two uniqueness refusals its `OnBeforeInsertAsync` arm raises
(#2983) and the four table cascades its `OnAfterDeleteAsync` arm runs (#2356).

**316 is measured off `main`, not carried over.** Three earlier drafts of this entry said
`304 -> 312`, `312 -> 320` and `314 -> 324`; every one was stale by the time it was written,
because `runner-extras` keeps moving underneath an open PR (`task-scheduler-oos` 6 -> 8 landed
in between, and `main` has since taken #3240, #3243 and #3248). 316 is the sum of the 56 group
entries in `main`'s own `test-count-baseline.json` at the rebase this entry was last written
against, and 326 is the sum of the 57 entries here. Re-measure both ends from the file rather
than copying either number forward:

```
python3 -c "import json;g=json.load(open('tests/expectations/count-baseline/test-count-baseline.json'))['suites']['runner-extras']['groups'];print(len(g),sum(v['tests'] for v in g.values()))"
```

**Why 10 and not 8.** Review found the first draft's combined
`…TakesItsAccessControlAndIsolatedStorageRows` test could pass against a cascade that does
nothing: it asserted `Count() = 0` after the delete without ever asserting the row was there
first, so an `Insert()` that silently failed to persist would read 0 both times. AL also stops a
test at its first failing assertion, and the Access Control assertion came first — so the
Isolated Storage half was never evaluated in the RED run at all. It is now one test per cascade
target, each with a present-before-the-delete precondition, and Tenant Report Layout Selection
(2000000233) — which had no test at all — is the third. That is 8 -> 10.

Three of the ten are controls, so no refusal can be satisfied by refusing more: a second user
with a *different* name inserts, two users with *empty* Windows SIDs both insert, and deleting
one user leaves another user's rows in all three cascade tables alone. Only the first two of
those pass in the RED baseline; the survival control also asserts the deleted user's own
companion row is gone, which the unfixed runner does not do. `al-language` (2676), `appGroups`
(1), `al-language-internals-fixture` (0) and `al-language-onprem` (19) are unchanged; no corpus
pin bump is involved.

Written by agent impl-24 (automated implementation agent).

## al-language 2681 → 2689 — corpus pin `b0c6248a` → `7394c15f` (#3057, corpus #202)

+8 tests, all in the `tests/al-language` app, from the three corpus commits between the two
pins. `git -C tests/al-language diff --name-only b0c6248a..7394c15f` touches nothing outside
that app, so `runner-extras` (330), `appGroups` (1), `al-language-internals-fixture` (0) and
`al-language-onprem` (19) are `main`'s values, untouched.

Where the 8 come from, counted per file rather than inferred from the totals:

| corpus commit | corpus PR | file | `[Test]` delta |
|---|---|---|---|
| `cd824ef` | #196 | `record/TestPageMetadataVirtualTable.al` | 3 → 4 (+1) |
| `def7430` | #200 | `handlers/TestPageSubscriberRefusal_Tests.al` | new, +3 |
| `7394c15` | #202 | `handlers/TestPageQueryCloseError_Tests.al` | new, +4 |

**Why the bump is folded into this PR and not its own.** Of those 8, exactly 2 are red without
this PR's fix, and both are #202's — the two arms that assert an AL error raised inside
`OnQueryClosePage` arrives as BC's own unhandled-message refusal. Measured, not predicted: a
build of `main` (`d6776eeb`) run against the corpus at `7394c15f` gives 2689 tests, 2687 pass,
2 fail, and the two are `ErrorInQueryClosePage_ArrivesAsAnUnhandledMessage` and
`ErrorInQueryClosePage_TestPageClose_ArrivesAsAnUnhandledMessage`. The same corpus on this
branch is 2689/2689. So the bump alone would be red by construction and belongs here.

The other 6 already pass on `main` — the runner fixes for corpus #196 and #200 merged as #3128
and #3180 earlier, ahead of their pin. An earlier draft of this PR predicted 5 failures at this
pin, attributing four more to corpus #199 and one to #196; those were measured before #3128 and
#3180 merged, and all five are now green.

**2689 is measured at this head, not carried forward.** The prediction in the PR body was also
2689, but it was made against a different `main` and was re-run rather than copied.

Written by agent impl-4 (automated implementation agent).

## runner-extras 330 → 335 — `user-table-baseapp-subscriber` (#2381)

+5 tests in one new `tests/runner-extras/user-table-baseapp-subscriber` suite (id range
65640-65649). No corpus pin moves and no other group changes; `al-language` (2689),
`appGroups` (1), `al-language-internals-fixture` (0) and `al-language-onprem` (19) are
untouched.

**Why the entry is here at all.** The README says per-bump rationale goes in this file and
never as a prose line in `test-count-baseline.json`. The first version of this PR bumped the
JSON with no entry here at all, which is the thing that rule exists to stop.

**330 is measured off `main`**, not carried over — it is the sum of the 58 group entries in
`main`'s own `test-count-baseline.json`, re-read after this branch was rebased onto `d6776eeb`,
and 335 is the sum of the 59 entries here. `runner-extras` moved five times while this PR was
open, so every number computed earlier was already stale: earlier drafts of this entry said
`304 -> 309`, `314 -> 319` and `332 -> 337`. The last of those was stale within eight minutes,
because #3265 landed and took `object-system-table` from 5 to 3. Re-measure both ends from the
file rather than copying either number forward:

```
python3 -c "import json;g=json.load(open('tests/expectations/count-baseline/test-count-baseline.json'))['suites']['runner-extras']['groups'];print(len(g),sum(v['tests'] for v in g.values()))"
```

**This suite is the AL-level regression coverage for #2979, not a new fix.** #2381 reported that
Base App table-trigger subscribers on the User system table never fire; the report was accurate,
and commit `8ef72629` (#2979) fixed it by observing the `ValueTask` a precompiled async
subscriber returns. #2979 shipped one C# unit test and a known-gap deletion — nothing exercised
the fixed path from AL against a real Microsoft subscriber. Two of the five tests raise through
Base Application codeunit 418 "User Management"; the other three are controls (a supported
licence type must still insert and modify cleanly, and SaaS is asserted separately as the
precondition both raising tests rest on), so a build that refused every User write cannot pass
this suite.

**Interaction with #3224, now settled.** That PR added `user-system-table-triggers` to the same
file and merged first; #3257, #3180, #3101 and #3265 also moved `runner-extras`, and the corpus
count reached 2689 via #3101's bump to 2681 and #3057's pin move to `7394c15f`. This branch is rebased onto all of it. The conflict was the
add/add of two adjacent lines predicted here, resolved by keeping both keys in sorted order —
`user-system-table-triggers` (10) and `user-table-baseapp-subscriber` (5) are distinct keys and
the file carries no total, so neither side replaces the other. #3265's reduction of
`object-system-table` from 5 to 3 is a separate line and merged without a conflict at all.

**The id range moved from 65630-65639 to 65640-65649 after the rebase**, and that is the one
non-mechanical change in it. #3224's `user-system-table-triggers` declares 65620-65639, so once
it merged the two manifests both claimed 65630-65639 and
`RunnerExtrasIdRangeGuardTests.NoTwoAppGroups_InTheSameBundleRoot_DeclareOverlappingIdRanges`
failed the BC 27.5 leg — the only failure in the run, against 3825 passes. No object actually
collided (this suite uses 65640/65641, that one 65620/65621), but every app group in a bundle
root shares one Object table, so the guard fails on the declared overlap rather than waiting for
a live collision to surface somewhere unrelated. Renumbering was preferred to an entry in the
guard's `KnownOverlaps` allowlist: that list is documented debt tracked in #3160, and a suite
using two ids has no reason to join it.

Written by agent impl-24 (automated implementation agent).

## 2026-09-07 — corpus pin c3531ec6 → 2cba52d4 (al-language 2757 → 2814, al-language-onprem unchanged at 25)

Bumped by the fix PR for #3263, #3178 and #3279. The pin has to move to the corpus tip because
that is where both of this PR's own corpus tests live: #216 (six tests pinning a CalcFormula
and a TableRelation that name a system field) and #217 (seven tests pinning a CalcFormula that
names a tableextension field, codeunit 60823). Corpus history in this range is linear, so
neither can be taken without the corpus commits merged before them; that is where the other 50
al-language tests come from, not from anything this PR wrote. The from-values are against `main`
as it stands when this merges (pin c3531ec6, al-language 2757): earlier drafts of this entry
quoted b0c6248a / 2681 / onprem 19 → 25, which was this branch's own merge base and another PR's
onprem move — `main` reached 25 without this PR, and it stays 25.

Thirteen of the newly-arrived tests failed on the runner for reasons unrelated to this fix.
Four of those families resolved while this PR sat in review, as their own pull requests merged:
codeunit 60677 (an error raised in OnQueryClosePage, corpus #202) with #3181, codeunit 60827 (a
source-parsed tableextension's TableRelation not enforced by Validate, corpus #207) with #3197,
and codeunit 60889 (Access Control backing the session user's SUPER status, corpus #204) with
#3287. None of them was ever declared here.

The ninth-and-last family IS declared, in
`tests/expectations/known-gaps-testpage-builtin-actions.json`: codeunit 60338's nine tests on
the dialog page types' built-in OK/Cancel (corpus #218), five against #3283 (which built-in a
PageType offers) and four against #3284 (the FormResult substituted for an unattended close).
Both are open and both are fixed by PR #3285. The declaration exists ONLY to break a cycle
through the corpus pin: #3285 needs this PR's CalcFormula/TableRelation fix for corpus codeunits
60818, 60823 and 60827, and this PR needs #3285's fix for these nine. This PR merges first with
them declared; #3285 merges second and deletes that file. Nothing in this PR touches TestPage
built-in actions.

Two families that arrived failing WERE declared here for a while, and went green mid-review as
#3234 (the Session virtual table, #2940) and #3265 (the legacy Object registry, #3071) merged.
The manifest caught that itself: a test that passes under an `expect-fail-known-gap` entry
fails the run with "remove the entry", so the staleness was corrected rather than shipped.

`runner-extras` is untouched by this PR.

Written by agent fbk-2 (automated implementation agent).

## 2026-09-07 — `runner-extras` `published-application-system-table` 7 → 9 (#3072)

Two tests added to `tests/runner-extras/published-application-system-table`, which the BC 27.0
leg reported as `[count-baseline] GROWTH: suite 'runner-extras' tests count: expected 338,
actual 340`. Both are the runner-mechanism half of #3072 — that `NAV App Extra` (2000000157)
now has a provider, so `Published Application`'s `"Tenant Visible"` and `"PerTenant Or
Installed"` Lookup FlowFields compute `true` through BC's own `CalcFields` instead of reading
the Boolean default for every app:

- `TenantVisibleAndPerTenantOrInstalledReadTrueThroughNavAppExtra`
- `NavAppExtraAnswersPerRuntimePackageIdRatherThanForEverything`

Only the `runner-extras` figure moves, and it is a per-group edit, so what it adds up to
depends on which leg you ask. Re-measured against `main` at `6027df55` rather than carried
forward from the first draft of this entry — the endpoint totals here go stale as soon as
another PR moves a sibling group:

| leg | declared runner-extras total before | after |
|---|---|---|
| BC 27.0 | 338 | 340 |
| BC 28.1 | 349 | 351 |

The two legs differ because five groups carry `absentOn` for the 27.x line; the +2 is the same
on both, and the group's own entry is what moved (7 → 9). The 27.0 pair is unchanged from the
first measurement, which is a fact about which groups the intervening merges touched, not a
reason to skip re-measuring.

Nothing else here is this PR's to move. `al-language` is 2814 and the corpus pin is `2cba52d4`,
both arriving from the #3263/#3178/#3279 entry immediately above rather than from here — this
branch rebased onto it and kept both entries. The BC-behaviour half of #3072 is corpus PR #228,
which has now **merged** (`17b015ef`), but its pin bump is deliberately NOT taken here: it sits
past `2cba52d4` and is sequenced separately (#3304). So `al-language-onprem` stays at 25 and
picks up its own +4 with that later bump. Growth is the expected direction; this file exists to
catch the count going down.

Written by agent stma-auto-3 (automated implementation agent).

## 2026-09-07 — `runner-extras` new app group `encryption-key-mgmt-3329` (+10, #3329)

New group, one line under `groups`, no `absentOn`. Its `app.json` declares
`"platform": "27.0.0.0"` / `"application": "27.0.0.0"` and it compiles and runs on every leg,
which is measured rather than assumed: the BC 27.0 leg of job `101668243177` discovered and
passed all ten of `Codeunit65750` before exiting 4 on the missing baseline entry.

The group proves the tenant encryption key ledger that #3329 adds — `CREATEKEY`, `DELETEKEY`,
`EXPORTKEY` and `IMPORTKEY` used to raise a bare `ArgumentException` out of
`NavSqlTenantProperties..ctor`, taking all 32 tests of MS's `Tests-Cash Flow` `Codeunit135203`
with them. Ten tests and not fewer because each closes a distinct hole: the reported path
(`Codeunit 1266 DisableEncryption`), the reverse (`EnableEncryption` restoring a working key),
and eight refusals and invariants that a create/delete pair alone would not catch — a create
over an existing key, an export with no key, an import with a wrong password, a missing file,
a different key over a stored one, the `DecryptTenantData` invariant that keeps a
`SetEncrypted` isolated-storage value readable after the key is deleted, the
`EncryptPendingData` round trip that puts it back, and a new key refusing to decrypt the old
key's ciphertext. Every negative one names the message it expects, because a bare `asserterror`
is satisfied by the very `ArgumentException` the fix removes.

Totals are derived, so what this adds up to depends on the leg. Measured off this branch's
file:

| leg | declared runner-extras groups / total before | after |
|---|---|---|
| BC 27.0 / 27.3 / 27.5 | 55 / 340 | 56 / 350 |
| BC 28.0 / 28.1 / 28.4 | 60 / 351 | 61 / 361 |

The two lines differ because five groups carry `absentOn` for the 27.x legs; the +10 is the
same on both. Nothing else in this file is this PR's to move: `al-language` stays at 2887 and
the corpus pin is untouched, because #3329's proving tests are deliberately not upstream — the
corpus tier patches this exact surface out (bc-linux StartupHook Patch #26), so a result there
would measure the patch rather than BC. Growth is the expected direction here.

Written by agent coord-1 (automated implementation agent).

## 2026-09-07 — corpus pin `0bbe376` → `408c39fe` (al-language 2915 → 2969)

The bump this repository needs for the fix in #3342: corpus PR
[#240](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/240) adds the
two page-background-task temporary-write tests that prove it, and corpus history is linear,
so nine other merged PRs come along with it. `408c39fe` **is** corpus `master`'s tip, so
there is no earlier commit that contains #240 — the "pin the newest commit whose
predecessors are all satisfied" option in `al-language-submodule.md` does not exist here.

The four issues #3304 named as holding this bump back — #3283, #3284, #3178, #3263 — are all
**closed** now, so the 18 failures it measured are gone.

`al-language` 2915 → **2969** (+54), measured on a real 3-bundle run on BC 28.1, not computed:
2998 tests total across the three corpus apps, of which `al-language-onprem` contributes 29
and `al-language-internals-fixture` 0 — both unchanged. Growth is the expected direction.

The starting point is `0bbe376`/2915, not `17b015ef`/2887: this branch was cut before #3349
merged, and its first draft of this entry named `17b015ef` accordingly. #3349's own entry
above already records `17b015ef` → `0bbe376` (+28), so naming `17b015ef` here a second time
would double-count it. Only this PR's step belongs here.

Written by agent fbk-2 (automated implementation agent).

## 2026-09-07 — corpus pin `ddb9b5ab` → `9ee6bbc` (al-language 2978 → 2977)

**The count goes DOWN, which is the unusual part.** A pin bump almost always grows the
count, so this entry exists mainly to say why this one shrinks: corpus PR
[#250](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/250) **deletes
codeunit 60878 "Test Report SaveAs Pdf"** — the whole file, carrying its single `[Test]`.
Upstream's rationale is that 60878 asserted only that `Report.SaveAs(..., ReportFormat::Pdf,
...)` returns false, which is true on the Linux tier (RDLC stubbed) and false on a Windows
tier that renders, so it was red on Windows by construction. Codeunit 60774 "Test Report
SaveAs Pdf Body" asserts a strict superset — it branches on the same return value and
additionally reads the blob back, requiring an empty stream on false and a `%PDF-` signature
on true. No coverage is lost; one test is.

This is a **catch-up** bump (`al-language-submodule.md`): every fix these four commits need
had already merged, so nothing here is folded into a runner fix. Corpus history is linear, so
all four come as a prefix:

| corpus PR | what it changes | effect on the count |
|---|---|---|
| [#250](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/250) | deletes cu 60878, superseded by cu 60774 | **−1** |
| [#251](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/251) | `.github/workflows/` only — nightly artifact type | ±0 |
| [#252](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/252) | rewrites one existing assertion in `TestMediaPngImport.al` (dimensions, not byte length) | ±0 |
| [#253](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/253) | `.github/` scripts and workflows, plus comments only in `TestIsolatedStorage.al` | ±0 |

**2977 is the number the guard itself reported**, not one computed by subtracting 1 from 2978.
Measured on BC 28.1.49838.53910 on a real 3-bundle run: with the pin moved and the baseline
still at 2978 the run exited 4 with
`DROP: suite 'al-language' tests count: expected 2978, actual 2977 (BC 28.1)`, and 2977 is that
`actual`. Re-run after the bump: **3006 tests total, 3006 pass, 0 fail, exit 0**, of which
`al-language-onprem` contributes 29 and `al-language-internals-fixture` 0 — both unchanged, and
neither reported a mismatch.

The guard is symmetric in practice, not just in the README: running the **old** pin against
this PR's 2977 exits 1 with
`GROWTH: suite 'al-language' tests count: expected 2977, actual 2978 (BC 28.1)`. So the
decrease is attributable to these four commits and to nothing else in the tree.

**One entry in `tests/expectations/` had to go with it**, and it is not optional — the run
cannot be green without it. `oos-reports.json` declared an `expect-oos` for
`Test Report SaveAs Pdf.SaveAsPdf_RdlcLayout_ReturnsFalseWithLastErrorTextOnLinux` (cu 60878).
With that codeunit deleted upstream, `--expectations-require-match` reports
`UNMATCHED: ... matched no test in this run — no codeunit named "Test Report SaveAs Pdf"
(id 60878) was loaded in this run`, which is exit 5. The sibling entry for cu 60774 stays and
still matches (`PASS (oos)`), so the same permanently-out-of-scope surface
(`docs/scope.md#report-rendering`) remains declared — the deleted entry was the redundant half,
exactly as upstream's supersession implies. Expectation entries now: 24, all matched.

The starting point is `ddb9b5ab`/2978, not `408c39fe`/2969: `5ad50bc2` on `main` moved both the
pin and the count in one commit, and its own step is already recorded by that PR.

Written by agent stma-auto-11 (automated implementation agent).

### 2977 -> 2978 (pin 9ee6bbcd -> e6a0a0cd, catch-up bump)

One upstream commit, [#262](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/262),
which fixes corpus issue #261: three test codeunits shared one `SingleInstance` cache fixture
that latches on first read, so they passed or failed depending on execution order once harness
commit `26d88ffc` stopped giving each codeunit a fresh session. The fix gives each test codeunit
its own fixture codeunit and adds a `.github/` script that fails the corpus build when one
`SingleInstance` codeunit is instantiated from more than one test codeunit.

The net +1 is a split, not new coverage of a new surface:

| | test |
|---|---|
| removed | `Codeunit60600.TestCodeunit_SingleInstance_DoesNotLeakAcrossTests` |
| added | `Codeunit60600.TestCodeunit_SingleInstance_SurvivesACodeunitBoundary` |
| added | `Codeunit60600.TestCodeunit_NonSingleInstance_DoesNotSurviveACodeunitBoundary` |

**2978 is the number the guard itself reported**, not one computed by adding 1 to 2977.
Measured on BC 28.1.49838.53910 on a real 3-bundle run: with the pin moved and the baseline
still at 2977 the run exited 4 with
`GROWTH: suite 'al-language' tests count: expected 2977, actual 2978 (BC 28.1)`, and 2978 is
that `actual`. Re-run after the bump: **3007 tests total, 3007 pass, 0 fail, exit 0**, of which
`al-language-onprem` contributes 29 and `al-language-internals-fixture` 0 — both unchanged and
neither reporting a mismatch.

**No newly-failing test, so no expectations entry moved.** The classification split is
unchanged across the bump: 3 `pass-oos`, 11 `pass-known-gap`, 1 `pass-divergence`, and
`--expectations-require-match` reported no unmatched entry in either direction.

This is a **catch-up** bump per `.claude/rules/al-language-submodule.md`: #262 is a corpus-side
fixture fix needing no runner change, so a bump alone is green rather than red by construction.

Written by agent stma-auto-22 (automated implementation agent).

## 2026-09-07 — corpus pin `e6a0a0cd` → `920a7bed` (al-language 2978 → 2993)

The **fold** case in `al-language-submodule.md`: corpus PR
[#254](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/254) adds the
nine `PermissionSet_` tests that prove the Permission Set (2000000004) fix in this same PR,
so this bump alone is red by construction and belongs in the fix PR rather than in one of its
own. Corpus history is linear, so one earlier commit comes along as a prefix:

| corpus commit | what it adds | effect on the count |
|---|---|---|
| `29042fc` (corpus [#264](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/264)) | what `AllObjWithCaption`'s `Object Subtype` answers per object kind | **+6** |
| `920a7bed` (corpus [#254](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/254)) | what the Permission Set table (2000000004) answers | **+9** |

The starting point is `e6a0a0cd`/2978, not `9ee6bbcd`/2977: the entry above already records
`9ee6bbcd` → `e6a0a0cd` (+1), which landed on `main` while this branch was being measured, so
naming it again here would double-count it.

`920a7bed` is deliberately **not** corpus `master`'s tip. The next commit, `c9b5cc83`
(corpus #260, eleven `RecordLink` tests), needs a runner fix that is still open as PR #3381,
so pinning it here would be red by construction — "pin the newest commit whose predecessors
are all satisfied", and that commit is `920a7bed`.

**2993 is a measured number, not `2978 + 15`.** Counting `[Test]` lines across the range gives
one too many: one of them sits inside a comment block in
`codeunit/SICSessionScopedPrimer.Codeunit.al`, a deliberately non-test codeunit. The run is
what decides. Measured on BC 28.1 on a real 3-bundle run: **3022 tests, 3022 pass, 0 fail,
exit 0**, of which `al-language-onprem` contributes 29 and `al-language-internals-fixture` 0 —
both unchanged and neither reporting a mismatch.

**Four expectation entries had to go in with it**, in
`tests/expectations/known-gaps-allobj-subtype.json`, all linking
[#2326](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2326). Corpus #264's
six new tests arrive ahead of the runner fix for the gap they pin: the runner leaves
`Object Subtype` empty on every `AllObj` / `AllObjWithCaption` row, so the four that assert a
NON-empty subtype fail, while the two asserting an empty one already pass. That is
pre-existing and not caused by the Permission Set change here — the identical four methods
fail under the runner at `main` as well — the packed `2.10.0-local.5ad50bc2` tool, a `main`
commit predating this branch, with PR #3391 unmerged — measured both ways on this same pin
(BC 28.1.49838.53910). #2326 stays
open, and its fix is the maintainer's open PR #3391; these four entries are deleted when that
lands, at which point the drift guard demands it in the other direction.

`pass-known-gap` therefore moves 11 → **15**, and the manifest 15 → **19** entries, all of
which the run matched (`match audit: all 19 entries matched a discovered test`) under
`--expectations-require-match`.

Written by agent fbk-2 (automated implementation agent).

### 2993 -> 3004 (pin 920a7bed -> c9b5cc83, PR #3381)

The pin advances by exactly one corpus commit, `c9b5cc83` — corpus
[#260](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/260), the upstream
half of [#3378](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3378) (the
Record Link table and the AL link surface are one store). `git -C tests/al-language diff --stat
920a7bed..c9b5cc83` is two files and nothing else: `record/ALTLinkHost.Table.al` and
`record/TestRecordLinkTable.al` (codeunit 60777, eleven `RecordLink*` tests).

This is the **fold** case, not a catch-up bump: the eleven tests fail without this PR's runner
fix, so the bump belongs in the fix PR and would be red on its own. Measured on this pin at BC
28.1.49838.53910, `--test RecordLink`, on the corpus app: **10 of 11 fail** under the packed
`2.10.0-local.5ad50bc2` tool (a `main` commit predating this branch) and **11 of 11 pass** under
this branch's build.

`c9b5cc83` is deliberately not corpus `master`'s tip. The next commit, `69ae759` (corpus #259),
belongs to a different open runner PR, so pinning past `c9b5cc83` would pull in tests whose
fix has not landed — "pin the newest commit whose predecessors are all satisfied".

**3004 is the guard's own printed `actual`**, not `2993 + 11`. The three-app run reported
`al-language` 3004 with `al-language-onprem` at 29 and `al-language-internals-fixture` at 0,
both unchanged.

Written by agent fbk-2 (automated implementation agent).

### 3004 -> 3008 (pin c9b5cc83 -> 69ae7598, +4)

Folded into PR #3379, the runner fix for #3373. ONE corpus commit: #259 `69ae759`, which pins
that `CurrPage.Update()` raises the page's `OnAfterGetCurrRecord`, and raises it after the
trigger that called it has returned. Its four tests are green here because of this PR's own
fix -- alone the bump would be red by construction, which is the *fold* case in
`.claude/rules/al-language-submodule.md`.

Nothing is declared in `tests/expectations/` for this bump. The two known-gap files an earlier
revision of this branch carried are gone, and neither was deleted on a guess:

- `known-gaps-allobj-subtype-corpus264.json` (#264's AllObjWithCaption "Object Subtype" tests,
  issue #2326) -- PR #3360 landed those same four entries in the existing
  `known-gaps-allobj-subtype.json`, which now holds five against #2326. Each of the four
  method names was checked against that file before this one was removed; keeping both would
  have double-declared them.
- `known-gaps-record-link-table.json` (#260's Record Link tests, issue #3378) -- PR #3381
  implements the surface, so those ten tests now pass and a known-gap entry would be drift in
  the "remove the entry" direction.

Written by the fbk-1 agent.

## 2026-09-07 — corpus pin `69ae7598` → `8678dc2` (al-language 3008 → 3026)

The pin advanced to consume StefanMaron/BusinessCentral.AL.Language.Tests#263, the upstream
half of #3384 (a TestPage control could not be written with a Date, DateTime or Time in any
spelling). Corpus history is linear, so the two merged corpus PRs sitting between the old pin
and #263 came with it, and both contribute tests:

| corpus PR | commit | what it pins | tests |
|---|---|---|---|
| #265 | `98eec27` | how far the Integer virtual table reaches, and that an open filter is answered | +3 |
| #270 | `ce2c3af` | the AL call-depth ceiling, from both sides | +5 |
| #263 | `8678dc2` | what a TestPage control does with a Date, DateTime or Time (this PR's own upstream test) | +10 |

**3026 is the number the guard itself reported**, not one computed from the old total plus a
count of added tests (#2803). Measured on BC 28.1.49838.53910; with the bump in place the run
is 3055/3055 across the three corpus app groups, and re-running against a deliberately wrong
baseline prints
`DROP: suite 'al-language' tests count: expected 9999, actual 3026 (BC 28.1)` and exits 4 — so
the guard is ARMED here rather than silently skipping, and 3026 is its own `actual`.

**Read the suite counts, not the run total.** `--count-baseline` is per suite, and the run
total 3055 is `al-language` 3026 + `al-language-onprem` 29 + `al-language-internals-fixture` 0.
Passing the single parent path `tests/al-language` instead of the three app directories CI
passes (`scripts/corpus-app-dirs.py`) folds all three into one suite called `al-language`, which
reports 3055 against a 3026 baseline and looks like a 29-test growth that is not there. That
cost a wrong number on the way to this entry; the invocation to copy is the one in
`bc-tests.yml`, both `--package-cache` arguments included.

`appGroups` for `al-language` stays **1** for the same reason: it reads 3 under the folded
invocation and 1 under CI's, and nothing in these three commits adds an app.

Three of the newly-pulled-in tests do not pass yet, both declared `expect-fail-known-gap`
against issues that stay OPEN after this PR merges:

- `known-gaps-integer-virtual-table.json` — #265's two reach tests, issue #3438. The Integer
  table is materialised over `[-1000..100000]` while real BC serves `[-1e9..1e9]`. Since #3393
  the request is refused loudly instead of truncated silently, which is what closed #2350; the
  remaining reach is #3438 and was filed for this bump.
- `known-gaps-codeunit-recursion-depth.json` — #270's `RecursionDepth_JustInsideTheCeiling_Completes`,
  issue #3405. The runner's guard throws at 500 frames where BC's ceiling is 1000, so only the
  arm sitting between the two ceilings fails; #270's other four pass, which is what identifies
  the cause.

Written by the fbk-1 agent.

## 3026 → 3057 — pin `8678dc28` → `ccc10f12` (page platform trigger events, #3436 / PR #3445)

Six corpus commits, 31 tests. `ccc10f12` is this PR's own upstream half
(`StefanMaron/BusinessCentral.AL.Language.Tests`#274, nine tests pinning BC's implicit page
trigger events); corpus history is linear, so it cannot be taken without #267, #266, #268,
#269 and #271 sitting under it.

Thirteen of the newly-pulled-in tests do not pass yet. Every one is declared
`expect-fail-known-gap` against an issue that stays OPEN after this PR merges, and every one
of those issues already has its own open fix PR whose merge deletes the entry:

- `known-gaps-testpage-draft-line.json` — #266's five `ONRC Tests`, issue #3029, open PR #3414.
  The runner raises a page's `OnNewRecord` a different number of times than BC for a draft
  line (`EnterNewRowLine` and the promotion both call `TryNewRecord`).
- `known-gaps-session-company-information.json` — #269's four
  `Test Session Comp Info Close`, issue #2382, open PR #3413.
  `NavUserAccountHelper.GetEffectivePermissionForObject` throws `NullReferenceException` on the
  skeleton session, whose `Permissions` is null; three of the four reach it through Company
  Information page 1's `OnOpenPage`.
- `known-gaps-testpage-samevalue-setvalue.json` — #271's `SetValue_WithTheSameValue_DoesNotRunOnModify`,
  issue #3055, open PR #3427. A same-value write runs `OnModify` once where BC runs it not at all.
- `known-gaps-testpage-blank-temporal.json` — #267's
  `TestPageField_AssertEquals_BlankDateTimeVariable_IsRefusedByAPopulatedControl`, issue #2361,
  open PR #3410. **Appended to the existing file**, not given one of its own: the drift guard
  treats a second declaration of the same test as an error, and five siblings of this gap were
  already declared there.
- `known-gaps-page-trigger-events.json` — two of #274's own nine, issues #3440 and #3441. These
  are not the gap this PR fixes: the events now fire and reach their subscriber. They are two
  separate defects the fix made observable for the first time — a stale `xRec` after a
  page-driven save, and a page-driven insert deferred until every control is written instead of
  committing on the key. Both were filed with the corpus test named as their proving test.

The other seven newly-pulled-in tests pass, and nothing that passed before regressed:
3044 pass / 13 fail, against 3026 pass / 0 fail at the old pin.

Written by the fbk-2 agent.

### 3057 -> 3071 (pin ccc10f12 -> ec8a9c23, PR #3452)

The pin advanced to consume StefanMaron/BusinessCentral.AL.Language.Tests#276, the upstream
half of #3449 (`[CommitBehavior(...)]` must change what `Commit()` does). Corpus history is
linear, so one other merged corpus PR came with it:

| corpus PR | commit | what it pins |
|---|---|---|
| #275 | `6c6a1d12` | `OnFindRecord`/`OnNextRecord` decide which rows the client walks |
| #276 | `ec8a9c23` | `CommitBehavior::Ignore` makes `Commit()` a no-op; `::Error` makes it raise |

3071 is the `--count-baseline` guard's own printed `actual` on a full three-app run at the new
pin, not a computed figure: `expected 3057, actual 3071 (BC 28.1)`.

#276's five all pass with this PR's fix, which is the RED -> GREEN this PR exists to show.

One known-gap file is added, for #275's five:

- `known-gaps-page-find-record.json` — the five `ALT Page Find Record Tests` (codeunit 60679),
  issue #3439, open PR #3448. A page declaring `OnFindRecord`/`OnNextRecord` serves its own
  rowset on real BC; the runner never dispatches those triggers and walks the table instead,
  so the TestPage lands on a different row (`Expected:<L0007> Actual:<L0008>` and siblings).
  Not caused by this PR and not fixed by it — #3439 stays open after it merges, and #3448
  removes this file.

Nothing that passed before regressed: 3095 pass / 5 fail, against 3057 pass / 0 fail at the
old pin. All five failures are the #275 tests declared above.

Written by the fbk-1 agent.

## 3071 stays 3071 — `known-gaps-page-find-record.json` removed (PR #3448, issue #3439)

**No number changed in `test-count-baseline.json`, and that file is untouched by this PR.**
The entry above advanced the pin to `ec8a9c23` and set 3071; this PR inherits that pin through
a merge of `main` rather than moving it, and adds no corpus tests of its own — its tests are a
runner fixture under `AlRunner.Tests/`. So the guard's `actual` is 3071 again, unchanged.

What changed is the composition, not the count. The five `ALT Page Find Record Tests`
(codeunit 60679) that the entry above declared as `expect-fail-known-gap` now PASS, because
#3448 is the fix for #3439 that entry named. Their file is deleted, which is required rather
than tidy: `--expectations-require-match` fails a run whose test passes with an entry still
declared. Measured on the full three-app run at this pin, after the deletion —
`3100 total, 3100 pass, 0 fail, 0 error`, exit 0, with `pass-known-gap` falling from 36 to 31
and the five appearing as plain `PASS`. Codeunit 60680's two `Copy`-onto-temporary tests pass
here and passed before the fix as well; they pin behaviour rather than record a gap.

Written by the fbk-3 agent.
## 2026-09-07 — `runner-extras/permission-set-assignment` 6 → 8 (issue #2382)

Two tests added to the existing `PSA Tests` codeunit (65612), pinning the two halves of the
`PermissionManagement.GetEffectivePermissionForObjectAsync` fix:

* `EffectivePermissionsForAnotherUser_IsRefusedByName` — asking about a user other than the
  session's own is refused, matched on the reason anchor `effective-permissions-other-user`
  rather than by a bare `asserterror` (which would also have passed on the NRE the fix removes).
* `EffectivePermissionsForTheSessionUser_IsAnsweredNotRefused` — the positive direction and
  the guard against a blanket refusal. Narrowed from an earlier draft that asserted the five
  direct permissions by value: that was a plain BC-behaviour claim written runner-locally, so
  it had encoded the runner's own error as its expectation. The value half is pinned upstream
  in corpus codeunit 60702 instead; what stays here is the runner-owned half.

They landed in this bundle rather than a new one because they assert the same runner-owned
permission model as the six #3039 tests already there, against a sibling method in the same
BC class. No al-language change here, but NOT for the reason first recorded. An earlier
draft of this entry said the pin was `e6a0a0cd`, that it predated corpus PR #269, and that a catch-up bump
was follow-up once #269 merged. All three were already false when this landed: #269 merged at
2026-09-07T18:57Z as `c5b8123d`, the pin this branch carried was `ec8a9c23` and already
contained it, and corpus codeunit 60702 therefore ran in this PR's own CI — which is what
adjudicated the Execute-on-table-data claim the fix rests on. There was no outstanding
catch-up for these tests.

Written by agent stma-auto-5 (automated implementation agent).

## 2026-09-08 — al-language 3071 → 3077, pin `ec8a9c23` → `af01bbbc` (issue #3451)

Fold bump: three corpus commits, six new tests.

* `af01bbbc` (corpus #278, this agent's) — five arms appended to codeunit 60899
  "Test TxModel AutoRollback", pinning that BC refuses an explicit `Commit()` while a
  `[TransactionModel(TransactionModel::AutoRollback)]` test method is in force, that the refusal
  follows the executing test method into an unattributed callee, that `AutoCommit` is the
  control, that `[CommitBehavior(CommitBehavior::Ignore)]` exempts the refusal, and that
  `[CommitBehavior(CommitBehavior::Error)]` does not outrank it. Red without the runner fix in
  this PR, which is why the bump is folded here rather than landing as a catch-up.
* `2549e35` (corpus #257) — one reinstated encryption round-trip test in `session/`.
* `7195a4b` (corpus #258) — nightly encryption-key cmdlet binding and a restored RDLC
  error-text assertion.

3077 is the guard's own printed actual (`[count-baseline] GROWTH: suite 'al-language' tests
count: expected 3071, actual 3077 (BC 28.1)`), not a computed number. **Neither `2549e35` nor
`7195a4b` needed a known-gap entry**: the full three-app run at this pin, with CI's own
invocation and both package caches, came back `3106 total, 3106 pass, 0 fail, 0 error` — the
only non-zero exit was 4, the baseline this entry bumps. Codeunit 60899 is 8/8 in that full
run rather than under a `--test` filter, which matters because #3468 makes the codeunit's
position in the run observable.

Written by the fbk-1 agent.

## 2026-09-08 — runner-extras 390 → 391 (`integer-virtual-table-window` 9 → 10)

Issue #3438. `tests/runner-extras/integer-virtual-table-window` (codeunit 64591) is rewritten
around what the Integer virtual table refuses now that rows are materialised per request: the
four window refusals it used to pin no longer exist, and the suite pins the row cap on three
request paths, the two half-open refusals, and a closed span past the base window being
answered rather than refused. Nine tests out, ten in. The al-language counts are untouched —
this fix adds no corpus test, it makes two existing ones pass and deletes their known-gap
entries.

Written by the fbk-1 agent.

## 2026-09-08 — al-language 3077 → 3089 (pin `af01bbbc` → `24106565`, two commits)

Issue #3468. The pin advances across two corpus commits, and the second is the one this PR
needs; the first cannot be skipped, because a pin cannot name a commit without its
predecessors.

* `466dd46` (corpus #277) — six tests in a new `autoformat/` area, codeunit 60605 "ALT
  AutoFormat Tests", asking what a `TestPage` reads from a Decimal control carrying
  `AutoFormatType` and `DecimalPlaces`. **Not this PR's work** — it is the maintainer's
  `stma-auto-2` for issue #3406.
* `2410656` (corpus #279) — six tests, codeunit 60878 "Test Write Tx Test Boundary", asking
  whether a write transaction survives a test-METHOD boundary under both transaction models.
  Red without the runner fix in this PR (3/6), which is why the bump is folded here rather
  than landing as a catch-up.

3089 is the guard's own printed actual (`[count-baseline] GROWTH: suite 'al-language' tests
count: expected 3077, actual 3089 (BC 28.1)`), not a computed number.

Five of `466dd46`'s six tests fail at this pin and are declared in the new
`tests/expectations/known-gaps-testpage-autoformat.json` against **#3406, which stays open
after this PR merges**. They are pulled in by the bump, not caused by it: the runner rewrites
`NavForm.GetAutoFormatStringAsync` to return `""` unconditionally, so a Decimal control reads
through a hardcoded two-decimal default. The runner fix for that is PR #3469, and that PR is
what deletes the known-gaps file. The sixth test of `466dd46` passes.

Codeunit 60878 is **6/6 in the full three-app run** with CI's own invocation and both package
caches — not under a `--test` filter, which matters here because #3468 is precisely about a
codeunit's neighbours in the run being observable. Full run at this pin: `3118 total, 3113
pass, 5 fail, 0 error`, the five being the declared known gaps above.

Written by the fbk-1 agent.
