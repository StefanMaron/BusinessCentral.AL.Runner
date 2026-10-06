// BcCompiler.CompileInputs — the change model's view of the non-.al files a compile reads (#5087).
// Rules and the population: docs/server-mode.md#affectedonly-and-files-the-compile-reads.
using System.Text.RegularExpressions;

namespace AlRunner;

public sealed partial class BcCompiler
{
    private static readonly Regex QuotedLiteralRx = new(@"'((?:[^']|'')*)'", RegexOptions.Compiled);

    /// <summary>The recorded inputs whose fingerprint differs from what the compiler would read now.</summary>
    internal static IReadOnlyList<string> ChangedCompileInputs(IReadOnlyDictionary<string, string> recorded, string? appRootDir)
    {
        if (recorded.Count == 0 || appRootDir == null) return Array.Empty<string>();
        var now = CompileFileReads.Fingerprint(appRootDir, recorded.Keys);
        return recorded.Where(kv => !string.Equals(now[kv.Key], kv.Value, StringComparison.Ordinal))
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Turns a changed compile input into a change to the object that names it: each `.al` file
    /// whose quoted text resolves to the changed file, against the app root or against its own
    /// directory (the two ways BC resolves a path), is added to <paramref name="modifiedPaths"/>, so
    /// it is classified and recompiled as an edit of its object. False, with the file in
    /// <paramref name="reason"/>, when no `.al` file names a changed input (a Translations file, a
    /// listing, a file no source mentions): which object it belongs to cannot be read from the change
    /// set, and the caller must not narrow.
    /// </summary>
    private static bool FoldCompileInputChanges(
        RadBaseline baseline, string? appRootDir, IReadOnlyList<string> alFiles,
        List<string> addedPaths, List<string> removedPaths, List<string> modifiedPaths, out string reason)
    {
        reason = "";
        var changed = ChangedCompileInputs(baseline.CompileInputs, appRootDir);
        if (changed.Count == 0) return true;
        var root = appRootDir!;

        var unnamed = changed.FirstOrDefault(k => !k.StartsWith(CompileFileReads.FilePrefix, StringComparison.Ordinal));
        if (unnamed != null)
        {
            reason = $"a directory the compile listed or probed changed ({unnamed}), which no object names";
            return false;
        }

        var wanted = changed.ToDictionary(k => k[CompileFileReads.FilePrefix.Length..], k => false, StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var al in alFiles)
        {
            string text;
            try { text = TddSourceOverlay.ReadAllText(al); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var declaringDir = Path.GetDirectoryName(Path.GetFullPath(al)) ?? root;
            foreach (Match m in QuotedLiteralRx.Matches(text))
            {
                var literal = m.Groups[1].Value.Replace("''", "'").Replace('\\', '/');
                if (literal.Length == 0 || literal.AsSpan().IndexOfAny('*', '?') >= 0) continue;
                foreach (var baseDir in new[] { root, declaringDir })
                {
                    string resolved;
                    try { resolved = Path.GetFullPath(Path.Combine(baseDir, literal)); }
                    catch (ArgumentException) { continue; }
                    if (wanted.ContainsKey(resolved)) { wanted[resolved] = true; owners.Add(al); }
                }
            }
        }

        var orphan = wanted.FirstOrDefault(kv => !kv.Value).Key;
        if (orphan != null)
        {
            reason = $"'{orphan}' is read by the compile and changed, and no .al file names it, so which object it belongs to cannot be read from the change set";
            return false;
        }

        foreach (var owner in owners)
            if (!addedPaths.Contains(owner) && !removedPaths.Contains(owner) && !modifiedPaths.Contains(owner))
                modifiedPaths.Add(owner);
        return true;
    }
}
