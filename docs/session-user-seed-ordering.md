# Where the session-user seed sits in the install-seed stage, and why

The runner has no database that predates a run, so it *builds* the session user's row in the
`User` system table (2000000120) during `TestExecutor`'s install-seed stage
(`RecordPatches.EnsureUserSystemTableRowSeeded`, #2296). When a row carrying the session user's
name is already there, the seed cannot write its own — BC refuses two users under one name and
the runner reproduces that refusal — so it **adopts** that row's security id as the session's
own instead (#2983). Adoption makes `UserSecurityId()` a value that came out of the data, which
is why every adoption prints a `[warn]` line naming both ids.

That leaves one question, which is what this page is about: **when is the adoption decided,
relative to the install code that observes the answer?**

## The order, as of #3268

Inside `TestExecutor`'s install-seed stage, in execution order:

1. per-bundle resets (`ResetUserSystemTableForNewBundle` puts the generated id back)
2. `TestDataProvisioner.Arm()` — the `--test-data` on-demand loader
3. the **dep-company baseline window** (#1867): on a MISS, the dependency apps' install
   triggers plus `Company-Initialize`, then `CaptureInstallBaselineSnapshot`; on a HIT or
   DISK-HIT, `RestoreInstallBaselineSnapshot` and none of that work
4. **`EnsureUserSystemTableRowSeeded` — the seed, and the adoption decision**
5. `InstallTriggerRunner.RunTestAssemblyOnly()` — the bundle's **own** install triggers
6. the Company row, the bundle's Published Application row, the Access Control SUPER row
7. `CaptureInstallBaseline()` — the baseline every codeunit boundary restores to

Before #3268, step 4 ran between steps 5 and 6. AL that stored `UserSecurityId()` in an install
trigger therefore stored the runner-generated id, and the seed then moved the session onto an
adopted row: the stored id named a user the session was no longer, and a `TableRelation` from it
resolved to the wrong row or to none. That is the defect #3268 describes, and
`AlRunner.Tests/InstallTriggerSessionIdentityTests` measures it — 3P/2F against the pre-fix
runner, cold and warm.

The new position is also the more BC-faithful one. On a service tier the session user is a row
in the database long before any extension is installed, so install code cannot observe an
identity that later changes, and it cannot insert a second user under the session user's name
either.

## Why the seed cannot move inside the dep-company window instead

Moving it one step earlier — ahead of step 3 — would close the same window for a *dependency's*
install triggers as well. It is not safe, for a reason that is a property of what adoption is:

**Adoption is a poke at the skeleton session, not a row.** `TryAdoptSessionUserSecurityId`
writes `NavUser.userGuid`; nothing about it is stored in the table. The dep-company snapshot is
keyed on the dependency set, the runner build and the BC version, and is shared across app
groups and cached on disk across processes. A snapshot captured with the seed inside that window
would carry the *row* — so a later HIT would restore a user row whose identity came out of some
other run's data, while the session poke that made it the session's identity would not happen at
all. The two would silently disagree, and on the HIT path nothing re-computes the decision.

Keeping the seed **after** the window means the decision is re-made per app group from whatever
that group's `User` table actually holds, whether the rows arrived from a MISS's dependency
triggers, from a restored snapshot, or from a `--test-data` backup loaded on demand. The warm
arm of `InstallTriggerSessionIdentityTests` is exactly that path: on the second process the
dependency triggers never run, the stand-in row arrives from the DISK-HIT snapshot, and the
adoption is decided again against it.

## What is still open

A **dependency's** install trigger (step 3) runs before the seed, so it still observes the
generated id when the run is going to adopt. Closing that needs the split above — the row
capture inside the window and the identity decision outside it — rather than a move. Tracked
separately; #3268 covers the bundle's own install code, which is where the reproducer that
raised it lives.

## Fixtures

Because the seed now precedes a bundle's own install triggers, a fixture that has to establish
a `User` row **before** the seed writes it from a sibling dependency app instead:

| fixture | what its `dep` app writes |
|---|---|
| `AlRunner.Tests/Fixtures/SessionUserRowAlreadyPresent` | the session user's own row (the benign already-present refusal, #2941) |
| `AlRunner.Tests/Fixtures/SessionUserRowNameCollision` | a different user under the session user's name (the adoption, #2983) |
| `AlRunner.Tests/Fixtures/InstallTriggerSessionIdentity` | the same collision, with the bundle's own install trigger recording the identity it saw (#3268) |
