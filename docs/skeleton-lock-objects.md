# Skeleton lock objects: which are seeded, which stay null, and why

The runner builds several BC objects with `RuntimeHelpers.GetUninitializedObject`, which skips
every field initialiser. A `private readonly object x = new object()` lock field is therefore
`null`, and `lock (null)` raises `ArgumentNullException: Value cannot be null.` out of
`System.Threading.Monitor` — a stack that names neither BC nor the runner (#1883, #3932).

Seeding a lock field is only correct when BC's own body, once past the lock, answers faithfully
from state the skeleton has. A `readonly object` holds no readable value, so the seed itself
carries no risk; what it can do is move the failure from a loud refusal to a **silent wrong
answer**, which `loud-failures.md` forbids. So each lock gets its own decision.

Method: the skeleton was read through reflection in `AlRunner.Tests` (the local engine fixture,
BC 28.5.54151.x), and the method bodies were read in `Microsoft.Dynamics.Nav.Ncl.dll`
28.1.49838.53910 (sha256 prefix `49b11d9b`). The decisions are pinned by
`AlRunner.Tests/SkeletonSessionLockObjectsTests.cs`.

## Seeded

| field | locked by | past the lock |
|---|---|---|
| `NavSession.childSessionsStateLock` | `ChildSessionsStates` getter and setter | getter builds a `NavChildSessionsState` from `ServerUserSettings.ChildSessionsMaxConcurrency` and caches it; two reads return one instance |
| `NavTenant` — seven fields | see `BcRuntime.AuditedTenantLockFields` | #1883 |

No AL path reaches `ChildSessionsStates` in-process: its only reader is
`NavChildSessionTaskScheduler`, and the runner runs page background tasks inline, bypassing it
(`RunnerPageBackgroundTaskGap.cs`). The seed is justified by the measured getter, not by an AL
reproducer.

## Left null on purpose: `NCLMetadata`

| field | what seeding it does |
|---|---|
| `allMetaTablesSnapshotSyncRoot` | `GetSnapshotOfAllNonVirtualMetaTables` stops throwing and answers 102 platform tables, **none** of the application tables (ids below 2000000000). A service tier's snapshot adds every table the apps contribute; the skeleton's app group has none. A silent partial list replaces a refusal. |
| `appObjectInitializationChangeOrRemovalSyncRoot` | `InitializeBaseAppGroup` gets one line further and raises `NullReferenceException` on `fullyInitializedAppGroups`, which the skeleton never built. It would also have to read the System Application resource the runner does not ship. Nothing starts working. |
| `allObjectIdsSnapshotSyncRoot` | Not needed. Its only in-process reader, `GetSnapshotOfAllObjects`, has its body replaced by the runner (`NclCecilRewrite.Records.cs`), so the lock is never taken. The other reader is `RemoveAppGroup`. |

`EnsureAllAppGroupsInitialized` is the one `NCLMetadata` method that completes once the first
lock is seeded (its body is empty); its only caller is extension-operation handling, which is out
of scope.

Re-open this when the skeleton gains a populated app group or the System Application resource:
`ForcingTheSnapshotLock_AnswersWithNoApplicationTables` and
`ForcingTheAppObjectInitLock_StillFails_InInitializeBaseAppGroup` fail then, by name.

## Other skeleton objects with null lock fields (not seeded, not reachable)

A walk of every Microsoft object reachable from the runner's static state and the skeleton
session (depth 4) found these null `readonly object` fields beyond the ones above:

- `NavUser.readLockObj`, `NavUser.writeLockObj` — read by `TryUpdateUserGroupsFromTenantCache` and
  `set_Groups`, Windows-group refresh through `System.DirectoryServices` (authentication paths).
- `NavDatabase.syncLock` (four readers: schema extension, change listener, index-override cache,
  `Dispose`) and `NavDatabase.appInformationSyncLock` (no reader in `Ncl.dll`).

None has an in-process path, and each leads into a database or directory the skeleton lacks.
Tracked, with the measurement, in the follow-up issue linked from #3932.
