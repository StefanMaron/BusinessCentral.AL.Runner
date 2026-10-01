namespace AlRunner;

// Helpers for --server mode: per-bundle file-hash diffing (changedFiles) and
// --dump-csharp support. Split out of Program.cs (#2665) -- purely static, no
// captured state.
internal static partial class ProgramSupport
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    // SHA-256 each .al file reachable from the given folders → path→hash map, for the
    // server's changedFiles diff.
    internal static Dictionary<string, string> ComputeServerFileHashes(IReadOnlyList<string> folders)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var f in folders
            .Where(Directory.Exists)
            .SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Files(Path.GetFullPath(d), "*.al"))
            .Distinct())
        {
            try
            {
                using var fs = File.OpenRead(f);
                map[f] = Convert.ToHexString(sha.ComputeHash(fs));
            }
            catch { /* unreadable file — omit from the diff */ }
        }
        return map;
    }

    // #5079: what a bundle's own module was compiled from — every .al file by path relative to
    // the bundle root and content, app.json, and the resolved dependencies. Null when a file could
    // not be read, so an incomplete answer never matches another directory's.
    internal static string? BundleSourceFingerprint(string bucketRoot, IReadOnlyList<string> folders,
        Dictionary<string, string> fileHashes, IEnumerable<string> dependencyTerms)
    {
        var files = folders.Where(Directory.Exists)
            .SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Files(Path.GetFullPath(d), "*.al"))
            .Distinct().ToList();
        if (files.Count != fileHashes.Count || files.Any(f => !fileHashes.ContainsKey(f))) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var f in files.OrderBy(f => Path.GetRelativePath(bucketRoot, f).Replace('\\', '/'), StringComparer.Ordinal))
            sb.Append("al:").Append(Path.GetRelativePath(bucketRoot, f).Replace('\\', '/')).Append(':').Append(fileHashes[f]).Append('\n');
        var appJson = Path.Combine(bucketRoot, "app.json");
        try
        {
            sb.Append("app.json:").Append(File.Exists(appJson)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(appJson)))
                : "none").Append('\n');
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        foreach (var d in dependencyTerms) sb.Append("dep:").Append(d).Append('\n');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // Files added/removed/modified between the previously served request and this one.
    internal static List<string> DiffServerFiles(Dictionary<string, string>? prev, Dictionary<string, string> cur)
    {
        if (prev == null)
            return cur.Keys.Select(p => Path.GetFileName(p) ?? p).ToList();
        var changed = new List<string>();
        foreach (var kv in cur)
            if (!prev.TryGetValue(kv.Key, out var h) || h != kv.Value)
                changed.Add(Path.GetFileName(kv.Key) ?? kv.Key);
        foreach (var kv in prev)
            if (!cur.ContainsKey(kv.Key))
                changed.Add(Path.GetFileName(kv.Key) ?? kv.Key);
        return changed;
    }

    // ── --watch helpers ───────────────────────────────────────────────────────────
    // WaitForSourceChange / ArmSourceWatch moved to AlRunner.WatchSource (see #1822):
    // local functions declared here cannot be unit-tested, and the arm-before-announce
    // ordering contract needed a deterministic test.

    // #2151 removed the AL1081 carve-out that used to live here (IsKnownLayoutPathResolution-
    // Bug / ClassifyBlockingAlDiagnostics): the runner's Tier-3 source compile now resolves a
    // report's file-relative LayoutFile against the declaring .al file's own directory (see
    // ReportLayoutFileSystem), matching real BC, so the six al-language corpus reports that
    // needed tolerating compile clean and every AL-diagnostic compile-failure guard below can
    // go back to "any Error-severity AL diagnostic blocks", with no per-error-code exception.

    internal static void DumpCsharpSources(string dir, string moduleName, IReadOnlyList<EmittedSource> sources)
    {
        var bundleDir = Path.Combine(dir, SanitiseFilename(moduleName));
        Directory.CreateDirectory(bundleDir);
        int written = 0;
        foreach (var src in sources)
        {
            var name = SanitiseFilename(src.Name) + ".cs";
            File.WriteAllText(Path.Combine(bundleDir, name), src.Code);
            written++;
        }
        Console.WriteLine($"  [--dump-csharp] wrote {written} .cs file(s) to {bundleDir}");
    }

    internal static string SanitiseFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
