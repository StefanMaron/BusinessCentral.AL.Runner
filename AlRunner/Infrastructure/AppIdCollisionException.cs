// AppIdCollisionException — loud failure when two structurally different AL apps
// declare the same app.json `id`.
//
// See .claude/rules/loud-failures.md and issue #1850: the cross-bundle module
// identity dedup added for issue #1683 (DependencyLoader's AppId-keyed cache,
// exposed via TryGetByAppId / RegisterLoaded and DependencyLoader.LoadAll's own
// cache check) assumed the same AppId appearing twice in one process is always
// the SAME app arriving through two different discovery paths (its own bundle,
// and/or as a resolved dependency of a sibling bundle) — legitimately resolving
// to one shared module is both correct and required (#1683's TargetException
// came from having TWO live modules for one AL identity). That assumption
// breaks when two UNRELATED apps accidentally share a GUID (e.g. a scaffolded
// app.json copy-pasted without regenerating `id`): the loader silently reused
// the first app's module for the second, so the second app's own test
// codeunits never ran and the first app's tests that happened to share a
// module ran again in the second app's place — see #1850 for the concrete
// silent-drop/double-run this produced (4 tests dropped, 1 doubled, exit 0).
//
// The discriminator (issue #1850's fix): same AppId + same {Name, Publisher,
// Version} is treated as the same app — reuse is correct, and the comparison
// is free (those three fields are already read off app.json / the dependency
// manifest for every app, no extra I/O). Same AppId + a mismatch on any of
// those is a genuine collision between two different apps and must fail
// loudly instead of silently picking one.

namespace AlRunner.Infrastructure;

/// <summary>
/// Thrown when two distinct AL apps (differing in Name, Publisher, or Version)
/// are found declaring the identical app.json `id` — a genuine GUID collision,
/// not the legitimate #1683 same-app reuse case. Names both source paths and
/// the shared AppId so the developer can see immediately which app.json needs
/// a new id.
///
/// Two sub-cases get two different messages (PR #1862 review): Name+Publisher
/// matching but Version differing is "the same app, stale build" — advising
/// "regenerate the id" would be actively wrong, since the id is correct and the
/// fix is a rebuild or a package-cache clear instead. Anything else is a genuine
/// GUID collision between unrelated apps, where regenerating the id IS the fix.
/// Both cases still abort: two live modules for one AL identity is the
/// TargetException hazard #1683 exists to prevent, so silently picking whichever
/// version arrived first would be its own silent-wrong-answer.
/// </summary>
public sealed class AppIdCollisionException : Exception
{
    public Guid AppId { get; }
    public string ExistingSourcePath { get; }
    public string NewSourcePath { get; }

    /// <summary>
    /// True when the collision is the same app (Name + Publisher match) at two
    /// different versions, rather than two genuinely unrelated apps.
    /// </summary>
    public bool IsVersionSkew { get; }

    public AppIdCollisionException(
        Guid appId,
        string existingName, string existingPublisher, string existingVersion, string existingSourcePath,
        string newName, string newPublisher, string newVersion, string newSourcePath)
        : base(BuildMessage(
              appId, existingName, existingPublisher, existingVersion, existingSourcePath,
              newName, newPublisher, newVersion, newSourcePath))
    {
        AppId = appId;
        ExistingSourcePath = existingSourcePath;
        NewSourcePath = newSourcePath;
        IsVersionSkew = IsSameAppDifferentVersion(
            existingName, existingPublisher, existingVersion, newName, newPublisher, newVersion);
    }

    private static bool IsSameAppDifferentVersion(
        string existingName, string existingPublisher, string existingVersion,
        string newName, string newPublisher, string newVersion)
        => string.Equals(existingName, newName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existingPublisher, newPublisher, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(existingVersion, newVersion, StringComparison.Ordinal);

    private static string BuildMessage(
        Guid appId,
        string existingName, string existingPublisher, string existingVersion, string existingSourcePath,
        string newName, string newPublisher, string newVersion, string newSourcePath)
    {
        if (IsSameAppDifferentVersion(
                existingName, existingPublisher, existingVersion, newName, newPublisher, newVersion))
        {
            return $"duplicate app id {appId}: \"{existingPublisher}_{existingName}\" is loaded at two " +
                   $"different versions in this process under the SAME id — v{existingVersion} " +
                   $"({existingSourcePath}) and v{newVersion} ({newSourcePath}). This is the same app, " +
                   $"not a different one — one of these is a stale build. Rebuild it or clear the " +
                   $"package/AL-output cache rather than regenerating the id: reusing one version's " +
                   $"compiled module for the other's tests would silently run the wrong version's " +
                   $"tests (see issue #1850).";
        }
        return $"duplicate app id {appId}: two different apps declare the same app.json \"id\" — " +
               $"\"{existingPublisher}_{existingName}\" v{existingVersion} ({existingSourcePath}) was " +
               $"already loaded in this process, and \"{newPublisher}_{newName}\" v{newVersion} " +
               $"({newSourcePath}) declares the SAME id but is a different app. Regenerate the " +
               $"\"id\" in one of these two app.json files — reusing one app's compiled module for " +
               $"the other's tests would silently drop the second app's tests (see issue #1850).";
    }
}
