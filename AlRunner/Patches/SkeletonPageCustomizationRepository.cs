// SkeletonPageCustomizationRepository — the page-customization store a headless test session has.
//
// WHY THIS EXISTS
//   BC reads user PERSONALIZATION while applying page customizations.
//   MetadataProvider.TryApplyPageCustomizations does:
//
//       configurationDeltas   = GetProfilePageMetadataDeltas(session, pageDefinition.ID, session.ProfileKey);
//       personalizationDeltas = session.UserPageMetadataCache.Get(pageDefinition.ID);
//
//   and NavSession.UserPageMetadataCache is null on the skeleton, so the second line NREs.
//   That whole block sits inside a bare `catch (Exception)` which converts anything thrown into
//   a NavAppObjectMetadataException reading "An error occurred while applying changes from the
//   '<app>' app to the application object of type 'Page' with the ID '<id>'. The error was:
//   NullReferenceException" — a message about app deltas for a defect that has nothing to do
//   with them. Issue #2811; the tenant-side half of the same shape is seeded in
//   MetadataPatches.InjectSkeletonSystemTenant.
//
//   BC's own CachingDatabaseUserPageMetadataRetriever cannot be seeded the cheap way its
//   profile-side sibling could. That one short-circuits on a null ProfileKey before touching
//   anything; this one does not:
//
//       if (!userPageMetadataDeltas.ContainsKey(pageId))
//           userPageMetadataDeltas[pageId] = pageCustomizationRepository.GetPersonalizationDelta(pageId);
//
//   — no null guard, so handing it a null repository just moves the NRE a frame deeper. It needs
//   a real INavPageCustomizationRepository, which is what this is.
//
// READS ARE FAITHFUL, NOT FAKE
//   A headless test session has no saved personalization and no profile configuration: nothing
//   ever created one, and there is no store they could have come from. "Nothing customized" is
//   therefore the TRUE answer here, not a convenient default — which is why these return empty
//   rather than throwing. Returning empty is also exactly what BC's own database-backed
//   implementation returns for a tenant with no rows.
//
// WRITES REFUSE LOUDLY
//   A write is a different claim. Nothing here persists it and nothing reads it back, so
//   accepting one silently would mean AL that saves a personalization and reads it back gets a
//   wrong answer with no signal — the silent-fake shape .claude/rules/loud-failures.md names.
//   So every Save/Delete throws RunnerOutOfScopeException with the API and a reason.
//
//   That costs nothing on any path the runner drives today, and this is measured rather than
//   assumed: in BC 28.1's Ncl, SavePersonalization has NO callers at all, and SaveConfiguration
//   is called only from NavConfigurationDesignerExtension and NavConfigurationImporterExtension
//   — the in-client designer and its importer, neither reachable from a headless AL test. If a
//   future path does reach one, it will say so by name instead of silently succeeding.
using System;
using System.Collections.Generic;
using Microsoft.Dynamics.Nav.Apps.MetadataDeltas;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.XmlMetadata;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

internal sealed class SkeletonPageCustomizationRepository : INavPageCustomizationRepository
{
    private static AlRunner.Infrastructure.RunnerOutOfScopeException Refuse(string api)
        => new(
            api,
            "page-customization-store — a headless test session has no store to persist a page "
            + "personalization or profile configuration into, and nothing would read it back. "
            + "Accepting the write silently would make a later read answer wrongly with no signal. "
            + "See docs/scope.md");

    // ── reads: a session with no customizations, which is the truth here ────────────────────

    public NavAppObjectMetadataRuntimeDeltas GetPersonalizationDelta(int pageId)
        => new();

    public IEnumerable<string> GetPersonalizationALCode(Guid userId)
        => Array.Empty<string>();

    public IDictionary<int, string> GetPersonalizationALCodeDictionary(Guid userId)
        => new Dictionary<int, string>();

    public IEnumerable<NavAppObjectMetadataRuntimeDeltas> GetConfigurationDelta(
        NavProfileKey profileKey, int pageId, ProfilePageMetadataOwner owner)
        => Array.Empty<NavAppObjectMetadataRuntimeDeltas>();

    public IEnumerable<string> GetConfigurationALCode(
        NavProfileKey profileKey, ProfilePageMetadataOwner owner, int? pageId)
        => Array.Empty<string>();

    public IDictionary<int, string> GetTenantConfigurationALCode(NavProfileKey profileKey)
        => new Dictionary<int, string>();

    public IEnumerable<int> GetTenantConfiguredPages(NavProfileKey profileKey)
        => Array.Empty<int>();

    // ── writes: refuse by name ──────────────────────────────────────────────────────────────

    public void SavePersonalization(int pageId, string delta, string alCode)
        => throw Refuse("INavPageCustomizationRepository.SavePersonalization");

    public void SavePersonalization(Guid userId, int pageId, string delta, string alCode)
        => throw Refuse("INavPageCustomizationRepository.SavePersonalization");

    public void SaveConfiguration(NavProfileKey profileKey, int pageId, string delta, string alCode,
        ProfilePageMetadataOwner owner)
        => throw Refuse("INavPageCustomizationRepository.SaveConfiguration");

    public void SaveConfigurationForCurrentUserProfile(int pageId, string delta, string alCode,
        ProfilePageMetadataOwner owner)
        => throw Refuse("INavPageCustomizationRepository.SaveConfigurationForCurrentUserProfile");

    public void DeletePersonalization(int pageId)
        => throw Refuse("INavPageCustomizationRepository.DeletePersonalization");

    public void DeletePersonalization(Guid userId, int pageId)
        => throw Refuse("INavPageCustomizationRepository.DeletePersonalization");

    public void DeleteConfiguration(NavProfileKey profileKey, string pageIdFilter)
        => throw Refuse("INavPageCustomizationRepository.DeleteConfiguration");
}
