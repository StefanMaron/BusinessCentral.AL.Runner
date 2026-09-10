# Where the session-user seed sits in the install-seed stage, and why

The runner has no database that predates a run, so it *builds* the session user's row in the
`User` system table (2000000120) during `TestExecutor`'s install-seed stage
(`RecordPatches.EnsureUserSystemTableRowSeeded`, #2296). When a row carrying the session user's
name is already there, the seed cannot write its own — BC refuses two users under one name and
the runner reproduces that refusal — so it **adopts** that row's security id as the session's
own instead (#2983). Adoption makes `UserSecurityId()` a value that came out of the data, which
is why every adoption prints a `[warn]` line naming both ids.

That leaves one question, which is what this page is about: **when is the identity settled,
relative to the install code that observes the answer?**

## The order, as of #3757

Inside `TestExecutor`'s install-seed stage, in execution order:

1. per-bundle resets (`ResetUserSystemTableForNewBundle` puts the generated id back;
   `ResetCompanySystemTableForNewBundle` and `ResetAccessControlSeedForNewBundle` clear their
   latches)
2. `TestDataProvisioner.Arm()` — the `--test-data` on-demand loader
3. the **dep-company baseline window** (#1867). On a MISS: the dependency apps' Published
   Application rows, **`EnsureUserSystemTableRowSeeded` — the ROW**,
   **`EnsureCompanySystemTableRowSeeded`** and **`EnsureAccessControlSuperRowSeeded`** (#3757),
   then the dependency apps' install triggers, `Company-Initialize`, and
   `CaptureInstallBaselineSnapshot`. On a HIT or DISK-HIT: `RestoreInstallBaselineSnapshot` and
   none of that work.
4. **`EnsureUserSystemTableRowSeeded` again — the identity DECISION**, re-made per app group on
   every path from whatever the `User` table now holds
5. the Company row, the bundle's Published Application row and the Access Control SUPER row
   again — the calls a cache HIT needs, and the ones that re-decide after an adoption (#3757)
6. `InstallTriggerRunner.RunTestAssemblyOnly()` — the bundle's **own** install triggers
7. `CaptureInstallBaseline()` — the baseline every codeunit boundary restores to

Steps 5 and 6 are the #3757 swap. Before it, every one of those three rows was written *after*
both sets of install triggers, so install code — a dependency's inside step 3, the bundle's own
at step 6 — ran against a `Company` table with no row for the company it was initialising, a
`Published Application` table missing the bundle's own row, and an `Access Control` table with
no SUPER grant for a session user the runner reports as SUPER everywhere else.

Before #3268, the identity decision ran after the bundle's own install triggers, so that install
code stored an id the seed then changed under it. Before #3698 there was no call at step 3, so a **dependency's**
install trigger looked the session user up in an empty table and observed an identity that could
still move. `AlRunner.Tests/InstallTriggerSessionIdentityTests` measures the first;
`AlRunner.Tests/DepInstallTriggerSessionIdentityTests` measures the second — 4P/1F against the
pre-#3698 runner, cold and warm. The three sibling seeds are measured the same way:
`DepInstallTriggerSessionIdentityTests` (7P/2F against the pre-#3757 runner, cold and warm) for
what a dependency's install code sees, and
`AlRunner.Tests/BundleInstallTriggerSeedVisibilityTests` (1P/3F, cold and warm) for what the
bundle's own install code sees.

The position is also the more BC-faithful one. On a service tier the session user is a row in
the database long before any extension is installed, so install code cannot observe an identity
that later changes, and it cannot insert a second user under the session user's name either.

## Why the split — the row is cacheable, the decision is not

**Adoption is a poke at the skeleton session, not a row.** `TryAdoptSessionUserSecurityId`
writes `NavUser.userGuid`; nothing about it is stored in the table. The dep-company snapshot is
keyed on the dependency set, the runner build and the BC version, and is shared across app
groups and cached on disk across processes. So the two halves of the seed cache differently:

| | belongs | why |
|---|---|---|
| the **row** in `User` (2000000120) | inside the window (step 3) | a table row is exactly what the snapshot carries, and dependency install code has to find it |
| the **identity decision** | outside it (step 4) | a HIT restores the row without re-making the decision, so on that path nothing would set the session's id |

`EnsureUserSystemTableRowSeeded` is therefore called at both points and has **no
already-seeded latch**: it decides its outcome from the table each time. On a MISS the first
call inserts and the second finds that row present; on a HIT only the second runs, against
rows that arrived from the snapshot; and where install code or a `--test-data` load replaced
the row in between, the second call sees the replacement and adopts.

## The three sibling seeds, and where each one's split falls (#3757)

The `User` row's split — the row inside the window, the decision after it — is the template.
Each sibling answers the cacheability question for itself, and the answers differ:

| seed | inside the window? | why |
|---|---|---|
| `EnsureCompanySystemTableRowSeeded` (2000000006) | **yes**, and again after | the row is process-invariant, so seeding it inside the window is safe; the snapshot does not carry it — the after-window call writes it on every HIT (measured: `seeded` after `Restore`) |
| `EnsureAccessControlSuperRowSeeded` (2000000053) | **yes**, and again after | process-invariant too, and this one the snapshot **does** carry (measured: `AlreadyPresent` after `Restore`); the second call re-decides after an adoption |
| `EnsurePublishedApplicationBundleRowSeeded` (2000000206 / 2000000153 / 2000000212) | **no** — after the window, before the bundle's own triggers | the row identifies the bundle, and the snapshot key does not |

**The Company row is process-invariant, so seeding it inside the window is safe**: `BcRuntime`
pokes the skeleton `NavCompany` with the literal company name `"My Company"` and a fixed
`companyTableId` guid, both compiled in, so every app group and every process on a machine builds
the identical row.

**The Access Control latch is keyed on the security id it seeded, not on a bool.** The seed can
now run before *and* after the identity decision, and the decision can move the session onto an
adopted row (#2983). A bool latch would leave the SUPER grant naming the id the session no longer
has — a permission row for a user that, in the adoption case, the adopting data has already
replaced. So `_accessControlSuperRowSeededFor` records the id, the second call re-seeds when the
settled id differs, and `TryDeleteAccessControlSuperRow` withdraws the row the seeder itself
wrote for the old id first. It withdraws only the exact shape this seeder writes — the
all-companies SUPER row for that one security id — never a grant install code contributed.

**Measured, on the fixture that actually moves the identity.** On
`AlRunner.Tests/Fixtures/InstallTriggerSessionIdentity` the table ends with **exactly one** SUPER
row, naming the adopted id, on both the cold and the warm arm —
`ItsiExactlyOneSuperRowAndItNamesTheAdoptedId` asserts the count and the id. What makes the
withdrawal cheap rather than load-bearing there is BC's own cascade: a `User` delete cascades to
Access Control (2000000053) — `UserTableTriggerPatches.CascadeDeleteForUser`, BC's first cascade
target — and adoption arises from the colliding row REPLACING the seeded one, so the pre-adoption
grant is already gone by the time the mismatch is detected. The cold run says so in as many words:
`the session identity moved to {D41F…}; the SUPER row for {C0A1…} was already gone`. On the warm
arm the snapshot carries no SUPER row at all, because the capture happens between the cascade and
the post-window seed.

So the withdrawal is a guard on a path BC's cascade normally clears first, and its `ALDelete` is
not exercised by any fixture today — what the fixtures pin is the branch (the log line above) and
the outcome (one row, right id). A collision that adopts WITHOUT deleting the previous row would be
the case that exercises the delete; nothing arranges one, because adoption follows from the
replacement.

**The bundle's Published Application row cannot move into the window at all**, and that is
structural rather than a preference. The window's snapshot is keyed on the dependency set and is
shared by every app group with the same closure; the capture walks the live store, so a row
seeded there is captured and restored into *other* app groups — which would report an app that is
not loaded as published. It therefore sits between the window and the bundle's own install
triggers, which is also what a service tier does: an app is published before its own install
triggers run, and its dependencies were installed before it was published at all. What a
dependency's install code sees — its own row present, the bundle's absent — is asserted by
`DisiTheDependencySawItsOwnAppInstalledAndNotTheBundle`.

<a id="what-constrains-the-order"></a>

## What constrains the order

Two constraints are real and one measurement retired a third.

1. **Every seed runs before `CaptureInstallBaseline()`.** The per-codeunit boundary restores the
   store to that baseline, so a row written after the capture survives only until the first
   boundary — green on a single-test invocation, red on a full one.
2. **The Access Control row runs after the `User` row.** Its `"User Security ID"` relates to
   `User."User Security ID"`, which holds no row until the User seed has run.
3. **The Published Application row does NOT have to precede the `User` row.** It used to, and
   defensively: a comment claimed the `User` table's insert subscribers reach a module-ownership
   check that needs 2000000206 seeded. #3268 inverted the order and measured the claim rather
   than reasoning about it. Across the 9,441 `.al` files in System Application, Base Application
   and Business Foundation there are **9** subscribers on `Database::User` and exactly **one** on
   insert — `BaseApp/src/System/User/UserManagement.Codeunit.al:499`,
   `ValidateLicenseTypeOnAfterInsertUser` → `ValidateLicenseTypeOnSaaS` →
   `EnvironmentInformation.IsSaaS()`, which reads `"Server Setting"` and never Published
   Application. `User Property` has no subscribers at all, and every caller of `AddAllowedTable` /
   `ModuleOwnsTable` sits behind `OnRefreshAllowedTables`, an install codeunit, or
   `Company-Initialize.OnBeforeOnRun` — none of them reachable from a `User` insert.

   Why the `User` row is the one that differs at all: #2296 routes it through
   `NavRecord.ALInsert` on purpose, to pick up the `User Property` companion row
   `UserTableTriggerPatches` prepends, so it does fire the table's event subscribers. The Company
   and Published Application seeds hand a row straight to the in-memory provider's `Insert` — no
   AL runs, and neither reads `User`.

## The adoption timing, and what is not fixture-tested

A collision the runner can adopt comes from data that predates the dependency install triggers
— in practice a `--test-data` backup carrying its own `TESTUSER`. That case settles at step 3:
`Arm()` runs at step 2, and the seed's own probe of table 2000000120 is what fires the on-demand
load, so the rows are there before the decision is made and before any dependency trigger runs.

No fixture measures that, because arranging it needs a real backup. What the fixtures measure
instead is a collision arranged **by a dependency install trigger**, which then settles at step
4 — the same path a DISK-HIT takes.

## Fixtures

A fixture that has to establish a `User` row the seed did not write does it from a sibling
dependency app, and — since the seed's row is written ahead of the dependency triggers — by
**replacing** the seeded row rather than inserting alongside it. A bare insert is refused, by
the primary key or by BC's user-name uniqueness rule, exactly as it would be on a service tier.

| fixture | what its `dep` app does |
|---|---|
| `AlRunner.Tests/Fixtures/SessionUserRowAlreadyPresent` | modifies the seeded row (the benign already-present refusal, #2941) |
| `AlRunner.Tests/Fixtures/SessionUserRowNameCollision` | deletes it and inserts a different user under the session user's name (the adoption, #2983) |
| `AlRunner.Tests/Fixtures/InstallTriggerSessionIdentity` | the same replacement, with the bundle's own install trigger recording the identity it saw (#3268) |
| `AlRunner.Tests/Fixtures/DepInstallTriggerSessionIdentity` | only observes: it records whether the session user was a row, and the id it saw (#3698), plus the Company row, the SUPER row and the installed-app registry it saw (#3757) |
| `AlRunner.Tests/Fixtures/BundleInstallTriggerSeedVisibility` | no dependency app at all: the BUNDLE's own install trigger records the Company row, the SUPER row and its own Published Application row it saw (#3757) |
