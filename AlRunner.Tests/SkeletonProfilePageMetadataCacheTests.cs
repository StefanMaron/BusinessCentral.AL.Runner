// Issue #2811 — the skeleton tenant's profile page-metadata cache.
//
// BC reads it while evaluating a page control's CAPTION.
// NavForm.CallEvaluateCaptionClassExtensionMethodAsync does:
//
//   if (!Session.ClientConnectionType.AllowsCustomizations(Session.WebConnectionType) || IsRequestPage) break;
//   var addedControls = Session.Tenant.NavProfilePageMetadataCache.GetAddedControls(Session.ProfileKey, formId);
//
// The skeleton session reports ClientConnectionType = UnknownClient (measured), and Types.dll's
// ConnectionTypeExtensions.AllowsCustomizations returns false only for Background and true by
// `default:` — so BC takes the customization branch. NavSystemTenant is built with
// GetUninitializedObject, so this property was null and BC dereferenced it: 21 first-chance
// NullReferenceExceptions for one TestPage control write, 20 of them here.
//
// These are runner-mechanism tests. There is no upstream claim: a real service tier has a real
// tenant with a real cache, so the situation does not arise there.
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SkeletonProfilePageMetadataCacheTests
{
    private readonly BcEngineFixture _engine;
    public SkeletonProfilePageMetadataCacheTests(BcEngineFixture engine) => _engine = engine;

    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [SkippableFact]
    public void SkeletonTenant_HasAProfilePageMetadataCache_SoBcDoesNotDereferenceNull()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var session = NavCurrentThread.Session;
        Assert.True(session != null, "the skeleton session is not wired — nothing to assert about.");

        var tenant = session!.GetType().GetProperty("Tenant", F)?.GetValue(session);
        Assert.True(tenant != null, "the skeleton session has no Tenant — see InjectSkeletonSystemTenant.");

        var cache = tenant!.GetType().GetProperty("NavProfilePageMetadataCache", F)?.GetValue(tenant);
        Assert.True(cache != null,
            "NavTenant.NavProfilePageMetadataCache is null on the skeleton — BC dereferences it while "
            + "evaluating a page control's caption (#2811).");
    }

    /// <summary>
    /// The property BC's own retriever relies on to stay off the database: a null profile key
    /// short-circuits to an empty dictionary before any repository access. The skeleton session's
    /// ProfileKey IS null, which is why seeding BC's own type needs no repository and is not a
    /// stand-in. If a BC build ever removed that guard, the seed would start reaching for a
    /// repository that is not there — so the guard is asserted, not assumed.
    /// </summary>
    [SkippableFact]
    public void TheCache_AnswersAnEmptyDictionary_ForTheSkeletonsNullProfileKey()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var session = NavCurrentThread.Session!;
        var tenant = session.GetType().GetProperty("Tenant", F)!.GetValue(session)!;
        var cache = tenant.GetType().GetProperty("NavProfilePageMetadataCache", F)!.GetValue(tenant);
        Skip.If(cache == null, "no cache to exercise — the sibling test above reports that.");

        Assert.Null(session.GetType().GetProperty("ProfileKey", F)?.GetValue(session));

        var getAddedControls = cache!.GetType().GetMethod("GetAddedControls", F);
        Assert.True(getAddedControls != null, "IProfilePageMetadataRetriever.GetAddedControls is gone — see #2811.");

        var result = getAddedControls!.Invoke(cache, new object?[] { null, 79301 });
        Assert.True(result is IDictionary, "GetAddedControls must answer a dictionary, not null.");
        Assert.Equal(0, ((IDictionary)result!).Count);
    }

    /// <summary>
    /// Rot guard on the rule that makes the null reachable in the first place. If a BC build made
    /// UnknownClient disallow customizations, BC would stop taking this branch and the seed would
    /// become dead code — worth knowing rather than carrying forever.
    /// </summary>
    [SkippableFact]
    public void UnknownClient_StillAllowsCustomizations_WhichIsWhyThisPathIsReached()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var types = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        Assert.True(types != null, "Microsoft.Dynamics.Nav.Types is not loaded.");

        var ext = types!.GetType("Microsoft.Dynamics.Nav.Types.ConnectionTypeExtensions");
        var allows = ext?.GetMethod("AllowsCustomizations", BindingFlags.Public | BindingFlags.Static);
        Assert.True(allows != null, "ConnectionTypeExtensions.AllowsCustomizations is gone — see #2811.");

        var connType = allows!.GetParameters()[0].ParameterType;
        var webType = allows.GetParameters()[1].ParameterType;
        Assert.Contains("UnknownClient", Enum.GetNames(connType));

        var unknown = Enum.Parse(connType, "UnknownClient");
        var none = Enum.GetValues(webType).Cast<object>().First();
        Assert.True((bool)allows.Invoke(null, new[] { unknown, none })!,
            "UnknownClient no longer allows customizations — BC would not reach the cache, so the "
            + "seed is now dead code. Re-read #2811 before removing it: the NRE may simply have moved.");
    }
}
