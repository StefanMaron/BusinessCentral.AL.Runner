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

## The order, as of #3698

Inside `TestExecutor`'s install-seed stage, in execution order:

1. per-bundle resets (`ResetUserSystemTableForNewBundle` puts the generated id back)
2. `TestDataProvisioner.Arm()` — the `--test-data` on-demand loader
3. the **dep-company baseline window** (#1867). On a MISS: the dependency apps' Published
   Application rows, **`EnsureUserSystemTableRowSeeded` — the ROW**, then the dependency apps'
   install triggers, `Company-Initialize`, and `CaptureInstallBaselineSnapshot`. On a HIT or
   DISK-HIT: `RestoreInstallBaselineSnapshot` and none of that work.
4. **`EnsureUserSystemTableRowSeeded` again — the identity DECISION**, re-made per app group on
   every path from whatever the `User` table now holds
5. `InstallTriggerRunner.RunTestAssemblyOnly()` — the bundle's **own** install triggers
6. the Company row, the bundle's Published Application row, the Access Control SUPER row
7. `CaptureInstallBaseline()` — the baseline every codeunit boundary restores to

Before #3268, step 4 ran between steps 5 and 6, so the bundle's own install code stored an id
the seed then changed under it. Before #3698 there was no call at step 3, so a **dependency's**
install trigger looked the session user up in an empty table and observed an identity that could
still move. `AlRunner.Tests/InstallTriggerSessionIdentityTests` measures the first;
`AlRunner.Tests/DepInstallTriggerSessionIdentityTests` measures the second — 4P/1F against the
pre-#3698 runner, cold and warm.

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
| `AlRunner.Tests/Fixtures/DepInstallTriggerSessionIdentity` | only observes: it records whether the session user was a row, and the id it saw (#3698) |
