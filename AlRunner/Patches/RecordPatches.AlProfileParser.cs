// RecordPatches.AlProfileParser — profile objects parsed out of the AL SOURCE the runner
// compiles itself.
//
// WHY THIS EXISTS
//   The "All Profile" (2000000178) virtual table's rows are the profiles every published
//   app declares. For a PRECOMPILED dependency .app that inventory comes off its
//   SymbolReference.json (BcAppSymbolCache.ProfileSymbol). For the bundle the runner
//   compiles from source there is no .app to read, so the profile objects have to come
//   from the same syntax-tree sweep every other source-parsed object kind uses.
//
//   A profile has no object id, so — unlike codeunits/pages/tables — it can never appear
//   in AllObj, which is why RecordPatches.AlObjectDeclParser deliberately leaves the kind
//   out. Its identity is its NAME, which is also its "Profile ID", so it needs its own
//   registry rather than a synthetic id in the (Kind, Id) one.
//
// THE DECLARING APP
//   Every "All Profile" row carries the declaring app's id and name as columns of its own,
//   and for a source-parsed profile the declaring app is exactly the app.json that owns the
//   source tree the file came from. That is read off disk by walking up from the file, so a
//   multi-app run (bundle + source-compiled dependencies) attributes each profile to its own
//   app instead of to whichever app happened to be current at parse time. A profile whose
//   app.json cannot be found is DROPPED rather than attributed to an invented app — an
//   All Profile row under the wrong App ID is a wrong answer, not a partial one.
using System.IO;
using System.Text.Json;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// One <c>profile "Name" { … }</c> declaration parsed from AL source, plus the identity
    /// of the app.json that owns the file it was declared in.
    /// </summary>
    internal sealed record ParsedAlProfile(
        string ProfileId, string? Caption, string? Description, string? RoleCenterPageName,
        bool Enabled, bool Promoted, Guid AppId, string AppName);

    // (AppId, ProfileId) → declaration. Keyed by app too: two apps in one run may legally
    // declare a profile of the same name, and All Profile's own primary key
    // (Scope, App ID, Profile ID) keeps them apart.
    private static readonly Dictionary<(Guid AppId, string ProfileId), ParsedAlProfile> _parsedProfiles
        = new();

    /// <summary>Snapshot of every profile declaration parsed from AL source.</summary>
    internal static IReadOnlyCollection<ParsedAlProfile> ParsedProfiles => _parsedProfiles.Values;

    /// <summary>
    /// Parse the profile declarations of one AL source file. <paramref name="filePath"/> is
    /// what makes the declaring app knowable; when it is null (a caller that only has text)
    /// nothing is recorded, because a profile with no app is not a row this table can answer.
    /// </summary>
    private static void TryParseProfileFile(string text, string? filePath)
    {
        if (filePath == null) return;
        // Cheap reject before building a syntax tree: the vast majority of .al files
        // declare no profile at all, and ParseAlObjects is memoized only for the file the
        // shared sweep is currently on.
        if (text.IndexOf("profile", StringComparison.OrdinalIgnoreCase) < 0) return;

        Guid appId = Guid.Empty;
        string appName = string.Empty;
        var identityResolved = false;

        foreach (var obj in ParseAlObjects(text))
        {
            if (obj is not NavSyntax.ProfileSyntax profile) continue;
            var name = IdentText((profile as NavSyntax.ObjectSyntax)?.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!identityResolved)
            {
                identityResolved = true;
                if (ResolveOwningApp(filePath) is not { } owner) return;   // no app.json — drop
                (appId, appName) = owner;
            }

            var props = profile.PropertyList;
            // AL's own defaults for a profile that declares neither: Enabled true,
            // Promoted false (matches BC's ProfileMetadata defaults, and the 16 of the
            // platform apps' 44 profiles that state no Enabled property are all enabled).
            var enabled = !PropIs(props, "Enabled", "false");
            var promoted = PropIs(props, "Promoted", "true");

            _parsedProfiles[(appId, name)] = new ParsedAlProfile(
                name,
                PropertyTextFrom(PropValue(props, "Caption")),
                // ProfileDescription ONLY. A profile may also declare a Description property,
                // but that is a different AL property and a service tier leaves the All
                // Profile row's Description empty for it — measured on BC 27.0-28.4 by the
                // corpus's TestAllProfileTable.al, whose ALT Profile SameApp fixture declares
                // Description and reads back an empty row Description.
                PropertyTextFrom(PropValue(props, "ProfileDescription")),
                PageRefText(PropValue(props, "RoleCenter")),
                enabled, promoted, appId, appName);
        }
    }

    // Directory → owning app identity, memoized: one app.json read per directory chain
    // rather than one per .al file.
    private static readonly Dictionary<string, (Guid AppId, string AppName)?> _owningAppByDir
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The (app id, app name) of the nearest <c>app.json</c> at or above
    /// <paramref name="filePath"/>, or null when there is none / it is unreadable.
    /// </summary>
    private static (Guid AppId, string AppName)? ResolveOwningApp(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (dir == null) return null;
        if (_owningAppByDir.TryGetValue(dir, out var memo)) return memo;

        (Guid, string)? found = null;
        for (var probe = dir; probe != null; probe = Path.GetDirectoryName(probe))
        {
            var manifest = Path.Combine(probe, "app.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var idText = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var nameText = doc.RootElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (Guid.TryParse(idText, out var id))
                    found = (id, nameText ?? string.Empty);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] All Profile: could not read {manifest} for the declaring app "
                    + $"of profiles under it: {ex.Message}");
            }
            break;
        }

        _owningAppByDir[dir] = found;
        return found;
    }
}
