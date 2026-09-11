// Issue #1883 — the skeleton tenant's unconstructed lock objects and IC-replication flag.
//
// NavSystemTenant is built with GetUninitializedObject, so every field initialiser BC's ctor
// would have run is skipped. For a `readonly object` that means `lock (null)`, which does not
// NRE: it reaches Monitor.ReliableEnter and raises ArgumentNullException("Value cannot be
// null."), a stack naming System.Threading rather than anything BC or runner.
//
// Measured on 28.1 through NavDataTransfer.CheckIsOpen ->
// NavTenant.IsIntelligentCloudReplicationEnabled: every AL DataTransfer call after SetTables
// raised that CLR exception instead of BC's own "DataTransfer is only usable during upgrade
// and installation code." AL error.
//
// Runner-mechanism tests: a real service tier runs the real ctor, so none of this arises
// upstream. The BC-behaviour half is corpus codeunit 60875.
using System;
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SkeletonTenantLockObjectsTests
{
    private readonly BcEngineFixture _engine;
    public SkeletonTenantLockObjectsTests(BcEngineFixture engine) => _engine = engine;

    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private object SkeletonTenant()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var session = NavCurrentThread.Session;
        Assert.True(session != null, "the skeleton session is not wired — nothing to assert about.");
        var tenant = session!.GetType().GetProperty("Tenant", F)?.GetValue(session);
        Assert.True(tenant != null, "the skeleton session has no Tenant — see InjectSkeletonSystemTenant.");
        return tenant!;
    }

    private static FieldInfo[] ReadonlyObjectFields(object tenant)
    {
        var found = new System.Collections.Generic.List<FieldInfo>();
        for (var t = tenant.GetType(); t != null; t = t.BaseType)
        {
            found.AddRange(t.GetFields(F).Where(f =>
                f.DeclaringType == t && f.FieldType == typeof(object) && f.IsInitOnly));
            if (t.Name == "NavTenant") break;
        }
        return found.ToArray();
    }

    [SkippableFact]
    public void EveryReadonlyObjectFieldOnTheSkeletonTenant_IsANonNullLockTarget()
    {
        var tenant = SkeletonTenant();
        var fields = ReadonlyObjectFields(tenant);

        // Without this the assertion below passes on an empty set — the failure mode where the
        // walk read the wrong type and measured nothing at all. BC 28.1 declares seven; a floor
        // rather than an equality so a BC build adding one does not fail for being fixed too.
        Assert.True(fields.Length >= 5,
            $"expected NavTenant to declare several readonly object lock fields, found {fields.Length} — "
            + "the field walk is reading the wrong type, so the null check below proves nothing.");

        var nulls = fields.Where(f => f.GetValue(tenant) == null).Select(f => f.Name).ToArray();
        Assert.True(nulls.Length == 0,
            "these lock fields are null on the skeleton tenant, so any BC body locking one raises "
            + "ArgumentNullException out of Monitor.ReliableEnter instead of doing its job: "
            + string.Join(", ", nulls));
    }

    /// <summary>
    /// The observable the DataTransfer cluster depends on. BC's getter returns the cached value
    /// when it has one; with none it locks, and on a tenant with no database answers false.
    /// Seeding false is that same answer taken one line earlier.
    /// </summary>
    [SkippableFact]
    public void IsIntelligentCloudReplicationEnabled_AnswersFalse_RatherThanThrowing()
    {
        var tenant = SkeletonTenant();
        var prop = tenant.GetType().GetProperty("IsIntelligentCloudReplicationEnabled", F);
        Assert.True(prop != null, "NavTenant.IsIntelligentCloudReplicationEnabled is gone — see #1883.");

        object? value;
        try
        {
            value = prop!.GetValue(tenant);
        }
        catch (TargetInvocationException ex)
        {
            throw new Xunit.Sdk.XunitException(
                "reading IsIntelligentCloudReplicationEnabled on the skeleton tenant threw "
                + $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}. BC's own body answers "
                + "false for a tenant with no database; it cannot get there while the skeleton is "
                + "unconstructed (#1883).");
        }

        Assert.Equal(false, value);
    }

    /// <summary>
    /// The premise that makes false BC's own answer rather than a stand-in: BC returns false
    /// whenever the database Lazy was never materialised, without opening a connection. If a BC
    /// build ever gave the skeleton a database, the seeded value would stop being derivable and
    /// this fails rather than going quietly wrong.
    /// </summary>
    [SkippableFact]
    public void TheSkeletonTenantHasNoDatabase_WhichIsWhyFalseIsBcsOwnAnswer()
    {
        var tenant = SkeletonTenant();
        var navTenant = tenant.GetType();
        while (navTenant != null && navTenant.Name != "NavTenant") navTenant = navTenant.BaseType;
        Assert.True(navTenant != null, "NavSystemTenant no longer derives from NavTenant — see #1883.");

        var database = navTenant!.GetField("database", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(database != null, "NavTenant.database is gone — see #1883.");
        Assert.Null(database!.GetValue(tenant));
    }
}
