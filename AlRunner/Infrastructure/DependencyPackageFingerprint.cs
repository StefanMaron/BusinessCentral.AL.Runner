using System.Security.Cryptography;

namespace AlRunner.Infrastructure;

/// <summary>
/// #4973: the content of the packaged dependencies a --server bundle resolved, as a segment of
/// affectedOnly's environment key. Replacing such a package, even with a same-version rebuild,
/// changes the key and forces a full run, which is what lets selection ignore the statements the
/// package executes. See docs/server-mode.md#affectedonly-and-packaged-dependencies.
/// </summary>
internal static class DependencyPackageFingerprint
{
    private const string Marker = "|pkg:";

    /// <summary>
    /// One entry per resolved package that is not <paramref name="excludePath"/>'d and not
    /// published by Microsoft, as <c>|pkg:&lt;appId&gt;=&lt;sha256&gt;</c>, ordered by AppId.
    /// Microsoft packages are left to the key's BC-version and package-directory parts: they are
    /// large, and none of their statements is ever attributed to a source file.
    /// </summary>
    public static string KeySegment(
        IEnumerable<(AppManifest Manifest, string AppPath)> resolved, Func<string, bool> excludePath)
    {
        var entries = new SortedDictionary<Guid, string>();
        foreach (var (manifest, appPath) in resolved)
        {
            if (manifest.AppId == Guid.Empty || excludePath(appPath)) continue;
            if (string.Equals(manifest.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            // An unreadable package cannot be vouched for, so the key must differ from every
            // readable state and from every other unreadable request: never a stable placeholder.
            string hash;
            try
            {
                using var fs = File.OpenRead(appPath);
                hash = Convert.ToHexString(SHA256.HashData(fs));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hash = "unreadable-" + Guid.NewGuid().ToString("N");
            }
            entries[manifest.AppId] = hash;
        }
        return string.Concat(entries.Select(e => $"{Marker}{e.Key:D}={e.Value}"));
    }

    /// <summary>
    /// The registered source directories whose statements come from a package this key covers:
    /// the app.json in the directory names an AppId in <paramref name="packagedAppIds"/>, and the
    /// directory neither lies inside nor contains a request bundle, whose files the change model
    /// tracks and which must never be ignored.
    /// </summary>
    public static IReadOnlyList<string> PackagedSourceRoots(
        IEnumerable<string> registeredDirs, IEnumerable<string> requestRoots, IReadOnlySet<Guid> packagedAppIds)
    {
        var requests = requestRoots.Select(Normalize).ToList();
        var roots = new List<string>();
        if (packagedAppIds.Count == 0) return roots;
        foreach (var dir in registeredDirs.Select(Normalize).Distinct(StringComparer.Ordinal))
        {
            if (requests.Any(r => IsUnder(dir, r) || IsUnder(r, dir))) continue;
            var identity = InProcessAppPackager.ReadIdentity(Path.Combine(dir, "app.json"));
            if (identity != null && packagedAppIds.Contains(identity.AppId)) roots.Add(dir);
        }
        return roots;
    }

    public static bool IsUnderAny(string path, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return false;
        var full = Normalize(path);
        return roots.Any(r => IsUnder(full, r));
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsUnder(string path, string root)
        => string.Equals(path, root, StringComparison.Ordinal)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The AppIds whose package content <paramref name="environmentKey"/> carries.</summary>
    public static IReadOnlySet<Guid> AppIdsIn(string environmentKey)
    {
        var ids = new HashSet<Guid>();
        var at = environmentKey.IndexOf(Marker, StringComparison.Ordinal);
        while (at >= 0)
        {
            var start = at + Marker.Length;
            var eq = environmentKey.IndexOf('=', start);
            if (eq > start && Guid.TryParse(environmentKey.AsSpan(start, eq - start), out var id)) ids.Add(id);
            at = environmentKey.IndexOf(Marker, start, StringComparison.Ordinal);
        }
        return ids;
    }
}
