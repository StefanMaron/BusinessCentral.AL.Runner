// Issue #3932 — the lock objects the skeleton NavSession and NCLMetadata never constructed.
//
// Both are built with GetUninitializedObject, so a `readonly object` lock field is null and
// `lock (null)` raises ArgumentNullException("Value cannot be null.") out of Monitor, naming
// neither BC nor the runner. Measured on 28.1.49838.53910 (Ncl.dll), reading the skeleton:
//
//   NavSession.childSessionsStateLock  ->  seeded; BC's own getter builds and caches its state
//   NCLMetadata.*SyncRoot (three)      ->  NOT seeded; each decision is pinned below, and the
//                                          derivation is docs/skeleton-lock-objects.md
//
// Runner-mechanism tests: a real service tier runs the real ctors, so none of this arises
// upstream, and no AL path reaches either lock in-process (ChildSessionsStates is read only by
// NavChildSessionTaskScheduler, and the runner replaces the body of NavForm.EnqueueBackgroundTask,
// the one Ncl.dll caller that could reach it; see docs/skeleton-lock-objects.md).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SkeletonSessionLockObjectsTests
{
    private readonly BcEngineFixture _engine;
    public SkeletonSessionLockObjectsTests(BcEngineFixture engine) => _engine = engine;

    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private NavSession Session()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var session = NavCurrentThread.Session;
        Assert.True(session != null, "the skeleton session is not wired — nothing to assert about.");
        return session!;
    }

    private object Metadata()
    {
        Session();   // skips when the engine is not ready
        var meta = BcRuntime.SkeletonNCLMetadata;
        Assert.True(meta != null, "the skeleton NCLMetadata is not wired — see InjectSkeletonSystemTenant.");
        return meta!;
    }

    private static T Inner<T>(Func<T> call)
    {
        try { return call(); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    // ---- NavSession.childSessionsStateLock: seeded ---------------------------------------------

    [SkippableFact]
    public void EveryReadonlyObjectFieldOnTheSkeletonSession_IsANonNullLockTarget()
    {
        var session = Session();
        var fields = typeof(NavSession).GetFields(F).Where(f =>
            f.DeclaringType == typeof(NavSession) && f.FieldType == typeof(object) && f.IsInitOnly).ToArray();

        // Without this the null check below passes on an empty set — the walk measured nothing.
        Assert.True(fields.Length >= 1,
            "expected NavSession to declare a readonly object lock field (childSessionsStateLock on 28.1), "
            + "found none — the field walk is reading the wrong type, so the null check below proves nothing.");

        var nulls = fields.Where(f => f.GetValue(session) == null).Select(f => f.Name).ToArray();
        Assert.True(nulls.Length == 0,
            "these lock fields are null on the skeleton session, so any BC body locking one raises "
            + "ArgumentNullException out of Monitor instead of doing its job: " + string.Join(", ", nulls));
    }

    [SkippableFact]
    public void EverySeededSessionLockField_IsOnTheAuditedRoster()
    {
        Session();
        Assert.True(BcRuntime.SeededSessionLockFields.Count > 0,
            "nothing is recorded as seeded on the session, so the assertion below would pass without "
            + "measuring anything — the BcRuntime statics read here are not the ones this process bootstrapped.");
        Assert.True(BcRuntime.UnauditedSeededSessionLockFields.Count == 0,
            "the skeleton session carries readonly object field(s) the #3932 audit does not cover, and the "
            + "runner has given each one a new object(): "
            + string.Join(", ", BcRuntime.UnauditedSeededSessionLockFields)
            + ". Confirm each is only ever a lock target and add it to BcRuntime.AuditedSessionLockFields, "
            + "or stop seeding it.");
    }

    /// <summary>The observable: BC's getter locks, then builds its own state from
    /// <c>ServerUserSettings.ChildSessionsMaxConcurrency</c>. Unseeded, this threw.</summary>
    [SkippableFact]
    public void ChildSessionsStates_ReturnsBcsOwnState_RatherThanThrowing()
    {
        var session = Session();
        var state = session.ChildSessionsStates;
        Assert.NotNull(state);
        Assert.Equal("NavChildSessionsState", state.GetType().Name);
    }

    /// <summary>BC's getter is double-checked: it caches under the lock. Two reads returning one
    /// instance proves the seeded token is the one BC's own body serialises on, not a value that
    /// is re-made per call.</summary>
    [SkippableFact]
    public void ChildSessionsStates_IsCachedAcrossReads()
    {
        var session = Session();
        Assert.Same(session.ChildSessionsStates, session.ChildSessionsStates);
    }

    /// <summary>The setter is the second lock site (<c>childSessionsStateLock</c> is locked in
    /// both accessors). Clearing the state and reading it back makes BC build a NEW one.</summary>
    [SkippableFact]
    public void ChildSessionsStatesSetter_TakesTheSameLock_AndTheGetterRebuilds()
    {
        var session = Session();
        var before = session.ChildSessionsStates;
        var setter = typeof(NavSession).GetProperty("ChildSessionsStates", F)!.GetSetMethod(nonPublic: true);
        Assert.True(setter != null, "NavSession.ChildSessionsStates has no setter any more — see #3932.");
        Inner<object?>(() => setter!.Invoke(session, new object?[] { null }));

        var after = session.ChildSessionsStates;
        Assert.NotNull(after);
        Assert.NotSame(before, after);
    }

    // ---- NCLMetadata: deliberately NOT seeded --------------------------------------------------

    private static readonly string[] MetaLocks =
    {
        "appObjectInitializationChangeOrRemovalSyncRoot", "allMetaTablesSnapshotSyncRoot", "allObjectIdsSnapshotSyncRoot",
    };

    private static FieldInfo MetaField(object meta, string name)
    {
        var f = meta.GetType().GetField(name, F);
        Assert.True(f != null, $"NCLMetadata.{name} is gone — re-measure #3932 against this BC build.");
        return f!;
    }

    [SkippableFact]
    public void TheNclMetadataLocksAreLeftNull_OnPurpose()
    {
        var meta = Metadata();
        foreach (var name in MetaLocks)
            Assert.True(MetaField(meta, name).GetValue(meta) == null,
                $"NCLMetadata.{name} is seeded. #3932 decided against that for every NCLMetadata lock: seeding "
                + "turns a loud refusal into a silent wrong answer or a deeper NullReferenceException "
                + "(docs/skeleton-lock-objects.md). Re-read the three tests below before keeping it.");
    }

    [SkippableFact]
    public void GetSnapshotOfAllNonVirtualMetaTables_StillRefusesLoudly()
    {
        var meta = Metadata();
        var m = meta.GetType().GetMethod("GetSnapshotOfAllNonVirtualMetaTables", F)!;
        Assert.Throws<ArgumentNullException>(() => Inner(() => m.Invoke(meta, new object[] { -1 })));
    }

    /// <summary>Why <c>allMetaTablesSnapshotSyncRoot</c> stays null: forced non-null, BC's body
    /// answers — with only platform tables. Every table the app under test declares is absent,
    /// where a service tier's snapshot adds each app-contributed table. A consumer would get a
    /// plausible, wrong list instead of a refusal.</summary>
    [SkippableFact]
    public void ForcingTheSnapshotLock_AnswersWithNoApplicationTables()
    {
        var meta = Metadata();
        var lockField = MetaField(meta, "allMetaTablesSnapshotSyncRoot");
        var cache = MetaField(meta, "allMetaTablesSnapshots");
        var cacheBefore = cache.GetValue(meta);
        var lockBefore = lockField.GetValue(meta);
        try
        {
            lockField.SetValue(meta, new object());
            var m = meta.GetType().GetMethod("GetSnapshotOfAllNonVirtualMetaTables", F)!;
            var tables = (IEnumerable<NCLMetaTable>)Inner(() => m.Invoke(meta, new object[] { -1 }))!;
            var ids = tables.Select(t => t.TableId).ToList();

            Assert.True(ids.Count > 0, "the forced snapshot is empty — the premise below has nothing to describe.");
            Assert.True(ids.All(id => id >= 2000000000),
                "the forced snapshot now lists application tables: " + string.Join(", ", ids.Where(id => id < 2000000000))
                + ". If the skeleton's app group is populated, re-evaluate seeding allMetaTablesSnapshotSyncRoot (#3932).");
        }
        finally
        {
            lockField.SetValue(meta, lockBefore);
            cache.SetValue(meta, cacheBefore);
        }
    }

    /// <summary>Why <c>appObjectInitializationChangeOrRemovalSyncRoot</c> stays null: forced
    /// non-null, <c>InitializeBaseAppGroup</c> proceeds one line and dereferences the app-group
    /// sets the skeleton never built, so the seed trades the misleading ArgumentNullException for
    /// a NullReferenceException and nothing works.</summary>
    [SkippableFact]
    public void ForcingTheAppObjectInitLock_StillFails_InInitializeBaseAppGroup()
    {
        var meta = Metadata();
        var lockField = MetaField(meta, "appObjectInitializationChangeOrRemovalSyncRoot");
        var lockBefore = lockField.GetValue(meta);
        try
        {
            lockField.SetValue(meta, new object());
            var m = meta.GetType().GetMethod("InitializeBaseAppGroup", F)!;
            Assert.Throws<NullReferenceException>(() => Inner(() => m.Invoke(meta, null)));
        }
        finally { lockField.SetValue(meta, lockBefore); }
    }

    /// <summary>Why <c>allObjectIdsSnapshotSyncRoot</c> needs no seed: its only in-process reader,
    /// <c>GetSnapshotOfAllObjects</c>, has its body replaced by the runner, so the lock is never
    /// taken. The other reader, <c>RemoveAppGroup</c>, is app-uninstall machinery.</summary>
    [SkippableFact]
    public void GetSnapshotOfAllObjects_NeverReachesItsLock()
    {
        var meta = Metadata();
        Assert.Null(MetaField(meta, "allObjectIdsSnapshotSyncRoot").GetValue(meta));
        var m = meta.GetType().GetMethod("GetSnapshotOfAllObjects", F)!;
        Assert.NotNull(Inner(() => m.Invoke(meta, new object[] { -1 })));
    }
}
