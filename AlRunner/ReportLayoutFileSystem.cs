// #2151: BC's compiler resolves a report/reportextension layout's LayoutFile property
// against the DECLARING .al FILE's own directory when the value is marked explicitly
// file-relative (a leading "./" or "../" — BC's own PathKind.RelativeToCurrentDirectory /
// RelativeToCurrentParent classification, confirmed by decompiling
// Microsoft.Dynamics.Nav.CodeAnalysis.Utilities.PathUtilities.GetPathKind during this
// issue's investigation), NOT against the app root — the al-language corpus's own upstream
// CI (a real BC service tier) compiles reports living in a subdirectory next to their own
// layout file clean, proving that is what real BC does.
//
// The runner's Tier-3 source compile instead attached a single NavCA.RelativeFileSystem
// anchored at the app root for every file-path property (#1899, for ControlAddIn resources)
// — correct for those, since every ControlAddIn resource in the corpus happens to live at
// the app root, but wrong for a report declared in a subdirectory. BC's own
// Compilation.WriteReportLayout calls `FileSystem.ReadBytes(current.LayoutFile)` with the
// bare property text and nothing else (decompiled during this investigation) — IFileSystem's
// interface takes only a path string, with no caller/declaring-file context — so the fix
// has to be computed OURSELVES, ahead of compilation, by scanning source text for LayoutFile
// declarations and building a small override table this file-relative wrapper consults
// before falling through to the untouched, still-working RelativeFileSystem underneath.
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner;

/// <summary>
/// Wraps a <see cref="NavCA.RelativeFileSystem"/> with a small override table for report
/// LayoutFile values BC would otherwise fail to resolve. Every method delegates unchanged to
/// the inner file system EXCEPT when the literal path text passed in is a key in the override
/// table, in which case the pre-resolved absolute path built at construction time is used
/// instead. This never changes resolution for anything that already worked (ControlAddIn
/// resources, a LayoutFile that genuinely lives at the app root) — the override table only
/// ever contains entries this issue's investigation proved BC resolves file-relative.
/// </summary>
internal sealed class ReportLayoutFileSystem : NavCA.IFileSystem
{
    private readonly NavCA.IFileSystem _inner;
    private readonly IReadOnlyDictionary<string, string> _overrides;

    public ReportLayoutFileSystem(NavCA.IFileSystem inner, IReadOnlyDictionary<string, string> overrides)
    {
        _inner = inner;
        _overrides = overrides;
    }

    private bool TryResolve(string path, out string absolute) => _overrides.TryGetValue(path, out absolute!);

    public byte[] ReadBytes(string path) =>
        TryResolve(path, out var abs) ? File.ReadAllBytes(abs) : _inner.ReadBytes(path);

    public byte[] ReadBytes(string path, int count)
    {
        if (!TryResolve(path, out var abs)) return _inner.ReadBytes(path, count);
        using var stream = File.OpenRead(abs);
        var buffer = new byte[count];
        var read = stream.Read(buffer, 0, count);
        return read == count ? buffer : buffer[..read];
    }

    public void WriteBytes(string path, byte[] content)
    {
        if (TryResolve(path, out var abs)) File.WriteAllBytes(abs, content);
        else _inner.WriteBytes(path, content);
    }

    public bool Exists(string path) => TryResolve(path, out var abs) ? File.Exists(abs) : _inner.Exists(path);

    public bool DirectoryExistsForFile(string path) =>
        TryResolve(path, out var abs) ? Directory.Exists(Path.GetDirectoryName(abs)) : _inner.DirectoryExistsForFile(path);

    // Search/enumeration APIs are never called with one of our override keys (those are
    // exact single-file literal path lookups, not search patterns) — pass through untouched.
    public IEnumerable<string> GetFiles(string searchPattern) => _inner.GetFiles(searchPattern);

    public IEnumerable<string> GetFiles(string directory, string searchPattern) =>
        _inner.GetFiles(directory, searchPattern);

    public IEnumerable<string> GetFilesRecursively(string directory) => _inner.GetFilesRecursively(directory);

    public string GetDirectoryPath() => _inner.GetDirectoryPath();

    public void CreateDirectoryForFile(string path)
    {
        if (TryResolve(path, out var abs)) Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        else _inner.CreateDirectoryForFile(path);
    }

    public Stream CreateFile(string path) => TryResolve(path, out var abs) ? File.Create(abs) : _inner.CreateFile(path);

    public Stream OpenRead(string path) => TryResolve(path, out var abs) ? File.OpenRead(abs) : _inner.OpenRead(path);

    public Stream OpenWrite(string path) => TryResolve(path, out var abs) ? File.OpenWrite(abs) : _inner.OpenWrite(path);

    public Stream OpenFile(string filePath, FileMode mode, FileAccess access, FileShare share = FileShare.None,
        int bufferSize = 4096, FileOptions options = FileOptions.None) =>
        TryResolve(filePath, out var abs)
            ? new FileStream(abs, mode, access, share, bufferSize, options)
            : _inner.OpenFile(filePath, mode, access, share, bufferSize, options);

    public void DeleteFile(string path)
    {
        if (TryResolve(path, out var abs)) File.Delete(abs);
        else _inner.DeleteFile(path);
    }

    public long GetFileSize(string path) => TryResolve(path, out var abs) ? new FileInfo(abs).Length : _inner.GetFileSize(path);

    public string GetAbsolutePath(string relativePath) =>
        TryResolve(relativePath, out var abs) ? abs : _inner.GetAbsolutePath(relativePath);

    public bool DirectoryExists(string directory) => _inner.DirectoryExists(directory);

    /// <summary>
    /// A leading "./" or "../" — BC's own PathKind.RelativeToCurrentDirectory /
    /// RelativeToCurrentParent (see Microsoft.Dynamics.Nav.CodeAnalysis.Utilities.
    /// PathUtilities.GetPathKind, decompiled for #2151) — is the marker real BC treats as
    /// "resolve against the declaring file", not the app root. A bare relative value with no
    /// such prefix (e.g. <c>LayoutFile = 'Layouts/Foo.rdlc'</c>) keeps resolving at the app
    /// root exactly as before — this predicate is deliberately narrow so nothing that already
    /// worked changes behaviour.
    /// </summary>
    internal static bool IsFileRelativeMarker(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '.') return false;
        if (value.Length == 1) return true;
        var c1 = value[1];
        if (c1 == '/' || c1 == '\\') return true;
        if (c1 != '.') return false;
        if (value.Length == 2) return true;
        var c2 = value[2];
        return c2 == '/' || c2 == '\\';
    }

    private static readonly Regex LayoutFilePropertyRx = new(
        @"(?im)^\s*LayoutFile\s*=\s*'((?:[^']|'')*)'", RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="alFiles"/> for <c>LayoutFile</c> property declarations and builds
    /// the override table an override-aware file system needs: for each explicitly
    /// file-relative value (see <see cref="IsFileRelativeMarker"/>) whose app-root-relative
    /// combine does NOT already exist as a real file, but whose combine against the
    /// DECLARING FILE's own directory does, map the literal declared text to that resolved
    /// absolute path. A value that already resolves at the app root is left alone — this
    /// never touches anything that isn't #2151's exact failure shape.
    ///
    /// The override key is the bare literal text BC itself reads — decompiling
    /// <c>Compilation.WriteReportLayout</c> shows it calls
    /// <c>FileSystem.ReadBytes(current.LayoutFile)</c> with nothing else, no caller/
    /// declaring-file identity reaches the file system at all. Two reports in DIFFERENT
    /// directories that declare the IDENTICAL file-relative literal (e.g. both write
    /// <c>LayoutFile = './Layout.rdl'</c>) are therefore indistinguishable to BC's own read
    /// call: whichever resolution this method installed under that key answers for BOTH
    /// reports. There is no way to give BC's read a per-report-unique path without
    /// rewriting the LayoutFile text itself — but that text is ALSO what
    /// <c>WriteReportLayout</c> embeds verbatim as the layout's file name
    /// (<c>outputter.AddReportLayout(..., current.LayoutFile, ...)</c>, and what
    /// <c>AlReportLayoutInfo.LayoutFile</c> later reports through the "Report Layout List"
    /// virtual table) — rewriting it to disambiguate would silently swap one wrong answer
    /// (the wrong file's bytes) for another (a LayoutFile value the AL source never
    /// declared). So a genuine collision — same literal, two DIFFERENT resolved files —
    /// throws <see cref="RunnerOutOfScopeException"/> naming both declaring files and the
    /// shared literal rather than picking a winner. Two objects that share the same literal
    /// AND resolve to the SAME actual file (e.g. two reports in one directory both using
    /// <c>'./Shared.rdlc'</c>) are not ambiguous and are left alone.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? BuildLayoutFileOverrides(
        IReadOnlyList<string> alFiles, string appRootDir)
    {
        Dictionary<string, string>? overrides = null;
        Dictionary<string, string>? declaredBy = null; // raw literal -> the .al file that first resolved it (for the collision message)
        foreach (var alFile in alFiles)
        {
            string src;
            try { src = File.ReadAllText(alFile); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (Match m in LayoutFilePropertyRx.Matches(src))
            {
                var raw = m.Groups[1].Value.Replace("''", "'");
                if (!IsFileRelativeMarker(raw)) continue;

                var rootRelativeAbs = SafeCombine(appRootDir, raw);
                if (rootRelativeAbs != null && File.Exists(rootRelativeAbs)) continue; // already resolves — leave it alone

                var declaringDir = Path.GetDirectoryName(Path.GetFullPath(alFile));
                if (declaringDir == null) continue;
                var fileRelativeAbs = SafeCombine(declaringDir, raw);
                if (fileRelativeAbs == null || !File.Exists(fileRelativeAbs)) continue;

                if (overrides != null && overrides.TryGetValue(raw, out var existing))
                {
                    if (string.Equals(existing, fileRelativeAbs, StringComparison.Ordinal))
                        continue; // same literal, same actual file — not ambiguous

                    throw new RunnerOutOfScopeException(
                        "BcCompiler.ReportLayoutFileResolution",
                        $"two reports declare the identical file-relative LayoutFile '{raw}' " +
                        $"from different directories, so BC's compiler (which reads a report's " +
                        $"LayoutFile with no caller context) cannot tell which one a given " +
                        $"report meant: '{declaredBy![raw]}' resolves it to '{existing}', " +
                        $"'{alFile}' resolves it to '{fileRelativeAbs}'. Rename one of the two " +
                        "layout files so the LayoutFile values are distinct.");
                }

                overrides ??= new Dictionary<string, string>();
                declaredBy ??= new Dictionary<string, string>();
                overrides[raw] = fileRelativeAbs;
                declaredBy[raw] = alFile;
            }
        }
        return overrides;
    }

    private static string? SafeCombine(string root, string relative)
    {
        try { return Path.GetFullPath(Path.Combine(root, relative)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Builds the IFileSystem BC's compiler should use for this compile: a plain
    /// <see cref="NavCA.RelativeFileSystem"/> anchored at <paramref name="appRootDir"/> when
    /// no report in <paramref name="alFiles"/> needs the file-relative override (the common
    /// case — unchanged from before #2151), or this wrapper carrying the override table when
    /// at least one does. Returns null exactly when the caller's own
    /// <c>appRootDir != null &amp;&amp; Directory.Exists(appRootDir)</c> guard would already
    /// have skipped WithFileSystem entirely, so every call site keeps its existing null-check
    /// shape unchanged.
    /// </summary>
    internal static NavCA.IFileSystem? Build(IReadOnlyList<string> alFiles, string? appRootDir)
    {
        if (appRootDir == null || !Directory.Exists(appRootDir)) return null;
        var inner = new NavCA.RelativeFileSystem(appRootDir);
        var overrides = BuildLayoutFileOverrides(alFiles, appRootDir);
        return overrides == null || overrides.Count == 0
            ? inner
            : new ReportLayoutFileSystem(inner, overrides);
    }
}
