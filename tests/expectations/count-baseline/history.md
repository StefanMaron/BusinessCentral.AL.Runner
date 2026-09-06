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
