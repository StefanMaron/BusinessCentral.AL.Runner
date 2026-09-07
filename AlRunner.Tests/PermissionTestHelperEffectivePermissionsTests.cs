// PermissionTestHelperEffectivePermissionsTests — proves the AlRunner#3343 fix: after
// NclCecilRewrite has run, NavTestExecution's effective-permission seam ROUND-TRIPS on the
// headless skeleton session instead of NREing on the null NavSession.Permissions.
//
// This is deliberately a RUNNER-INTERNAL claim about our own Cecil rewrite, not a BC-behaviour
// one. The BC-behaviour claim — that codeunit 131006 "Permissions Mock" Start()/Stop() flips
// IsStarted(), and that ClearAssignments() and a stopped-mock Assign() are accepted — lives
// upstream in StefanMaron/BusinessCentral.AL.Language.Tests
// (tests/al-language/permissions/TestPermissionsMockLifecycle.al, codeunit 60291), where a real
// BC service tier adjudicates it. This file pins the C#-side mechanism that makes it pass here.
//
// THE SEAM. Microsoft's own PermissionTestHelper (its own tiny assembly, constructed by
// "Permissions Mock".Start) does nothing but bind three static NavTestExecution methods by
// reflection and call them:
//
//     NavTestExecution.IsInTestMode()
//     NavTestExecution.GetEffectiveTestPermissions()
//     NavTestExecution.SetEffectiveTestPermissions(HashSet<string>)
//
// The last one is `session.TestExecution.EffectivePermissionSets = value`, whose real setter
// dereferences `Tree.Session.Permissions` twice — null here by design (docs/limitations.md,
// "Permission-set assignment"). So these three calls ARE the AL-visible surface, and driving
// them directly is driving exactly what BC's own AL drives.
//
// MEASURED RED (this file unchanged, the rewrite in NclCecilRewrite.Runtime.cs reverted):
//     SetEffectiveTestPermissions_Null_DoesNotThrow_OnTheSkeletonSession
//         System.NullReferenceException : Object reference not set to an instance of an object.
//     SetEffectiveTestPermissions_NonNullSet_RoundTripsThroughTheSeam
//         System.NullReferenceException : Object reference not set to an instance of an object.
// and GREEN after. The round-trip arm is what stops the rewrite being satisfiable by a no-op:
// a setter rewritten to `ret` never stores, so GetEffectiveTestPermissions() answers null and
// the assertion on the stored set fails.
using System;
using System.Collections.Generic;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// Loads Ncl types in-process (must share the serial bc-engine collection — see
// BcEngineCollection.cs comment header).
[Collection(BcEngineCollection.Name)]
public sealed class PermissionTestHelperEffectivePermissionsTests
{
    private readonly BcEngineFixture _engine;

    public PermissionTestHelperEffectivePermissionsTests(BcEngineFixture engine) => _engine = engine;

    private static (MethodInfo Get, MethodInfo Set) ResolveSeam()
    {
        var nclAsm = typeof(ITreeObject).Assembly;
        var navTestExecution = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestExecution")
            ?? throw new InvalidOperationException("NavTestExecution not found in the loaded Ncl.");
        // The exact two members PermissionTestHelper's ReflectionTypeLoader binds:
        // GetMethod(name, BindingFlags.Static | BindingFlags.Public).
        var get = navTestExecution.GetMethod("GetEffectiveTestPermissions", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("NavTestExecution.GetEffectiveTestPermissions not found.");
        var set = navTestExecution.GetMethod("SetEffectiveTestPermissions", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("NavTestExecution.SetEffectiveTestPermissions not found.");
        return (get, set);
    }

    private static void Set(MethodInfo set, HashSet<string>? value)
    {
        try { set.Invoke(null, new object?[] { value }); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Surface the real failure (a NullReferenceException out of the un-rewritten
            // setter) rather than the reflection wrapper, so a RED run names the defect.
            throw tie.InnerException;
        }
    }

    private static HashSet<string>? Get(MethodInfo get)
    {
        try { return (HashSet<string>?)get.Invoke(null, Array.Empty<object>()); }
        catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
    }

    [SkippableFact]
    public void SetEffectiveTestPermissions_Null_DoesNotThrow_OnTheSkeletonSession()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (get, set) = ResolveSeam();
        var original = Get(get);
        try
        {
            // This is literally PermissionTestHelper's constructor path: Clear() ->
            // ReflectionSetPermissionSets(null) -> SetEffectiveTestPermissions(null).
            Set(set, null);

            // ... and the seam answers the cleared state rather than a stale set.
            Assert.Null(Get(get));
        }
        finally { Set(set, original); }
    }

    [SkippableFact]
    public void SetEffectiveTestPermissions_NonNullSet_RoundTripsThroughTheSeam()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (get, set) = ResolveSeam();
        var original = Get(get);
        try
        {
            Set(set, null);

            // PermissionTestHelper.AddEffectivePermissionSet's path: read the current set,
            // add to it, write it back. A non-null value takes the setter's OTHER arm, the
            // one guarded on Tree.Session.Permissions.IsSuperForAllCompanies.
            var assigned = new HashSet<string>(StringComparer.Ordinal) { "D365 BASIC", "ALL OBJECTS" };
            Set(set, assigned);

            // The specific stored contents, not merely "something non-null": a rewrite that
            // dropped the field assignment would answer null here, and one that stored a
            // fresh empty set would answer 0.
            var readBack = Get(get);
            Assert.NotNull(readBack);
            Assert.Equal(2, readBack!.Count);
            Assert.Contains("D365 BASIC", readBack);
            Assert.Contains("ALL OBJECTS", readBack);

            // The negative direction, and PermissionTestHelper.Dispose()'s path: clearing
            // really clears, so a seam that ignored its argument fails here.
            Set(set, null);
            Assert.Null(Get(get));
        }
        finally { Set(set, original); }
    }
}
