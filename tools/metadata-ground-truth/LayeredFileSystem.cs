// The IFileSystem BC's compiler resolves an app's resources through: ordered roots, so an
// overlay such as addin/src sits beside the package root, plus '\' -> '/', URL-decoding and
// case-folded lookup applied unconditionally. Microsoft's own apps rely on all three.

using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner.Tools.MetadataGroundTruth;

internal sealed class LayeredFileSystem : NavCA.IFileSystem
{
    private readonly string[] _roots;
    private readonly Dictionary<string, string> _caseFolded = new(StringComparer.OrdinalIgnoreCase);

    public LayeredFileSystem(IEnumerable<string> roots)
    {
        _roots = roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct().ToArray();
        foreach (var root in _roots)
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                _caseFolded.TryAdd(rel, file);
            }
    }

    public string Root => _roots.Length > 0 ? _roots[0] : "";

    private string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (Path.IsPathRooted(path) && File.Exists(path)) return path;
        foreach (var variant in Variants(path))
        {
            foreach (var root in _roots)
            {
                var candidate = Path.Combine(root, variant);
                if (File.Exists(candidate)) return candidate;
            }
            if (_caseFolded.TryGetValue(variant, out var folded)) return folded;
        }
        return _roots.Length > 0 ? Path.Combine(_roots[0], path.Replace('\\', '/')) : path;
    }

    private static IEnumerable<string> Variants(string path)
    {
        var normalized = (path ?? "").Replace('\\', '/').TrimStart('.', '/');
        yield return normalized;
        string decoded = normalized;
        try { decoded = Uri.UnescapeDataString(normalized); } catch { /* not a URL-escaped path */ }
        if (decoded != normalized) yield return decoded;
    }

    // BC calls GetFiles(searchPattern) with a whole path, which for a control add-in can be a
    // CDN URL. Real BC tolerates that, so enumerate defensively rather than throw.
    private static List<string> Safe(string dir, string pattern, SearchOption option)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, pattern, option).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    public byte[] ReadBytes(string path) => File.ReadAllBytes(Resolve(path));

    public byte[] ReadBytes(string path, int count)
    {
        using var s = File.OpenRead(Resolve(path));
        var buffer = new byte[count];
        var read = s.Read(buffer, 0, count);
        if (read == count) return buffer;
        Array.Resize(ref buffer, read);
        return buffer;
    }

    public void WriteBytes(string path, byte[] content) => File.WriteAllBytes(Resolve(path), content);
    public bool Exists(string path) => File.Exists(Resolve(path));
    public bool DirectoryExistsForFile(string path) => Directory.Exists(Path.GetDirectoryName(Resolve(path)));

    public bool DirectoryExists(string directory)
        => _roots.Any(r => Directory.Exists(Path.Combine(r, (directory ?? "").Replace('\\', '/'))))
           || Directory.Exists(directory ?? "");

    public string GetAbsolutePath(string relativePath) => Resolve(relativePath);

    public IEnumerable<string> GetFiles(string searchPattern)
    {
        var sp = (searchPattern ?? "").Replace('\\', '/');
        var slash = sp.LastIndexOf('/');
        var dir = slash >= 0 ? sp[..slash] : "";
        var pattern = slash >= 0 ? sp[(slash + 1)..] : sp;
        if (pattern.Length == 0) pattern = "*";
        return _roots.SelectMany(r => Safe(Path.Combine(r, dir), pattern, SearchOption.TopDirectoryOnly));
    }

    public IEnumerable<string> GetFiles(string directory, string searchPattern)
        => _roots.SelectMany(r => Safe(Path.Combine(r, (directory ?? "").Replace('\\', '/')),
            searchPattern ?? "*", SearchOption.TopDirectoryOnly));

    public IEnumerable<string> GetFilesRecursively(string directory)
        => _roots.SelectMany(r => Safe(Path.Combine(r, (directory ?? "").Replace('\\', '/')),
            "*", SearchOption.AllDirectories));

    public string GetDirectoryPath() => Root;
    public void CreateDirectoryForFile(string path) => Directory.CreateDirectory(Path.GetDirectoryName(Resolve(path))!);
    public Stream CreateFile(string path) => File.Create(Resolve(path));
    public Stream OpenRead(string path) => File.OpenRead(Resolve(path));
    public Stream OpenWrite(string path) => File.OpenWrite(Resolve(path));

    public Stream OpenFile(string filePath, FileMode mode, FileAccess access, FileShare share,
        int bufferSize, FileOptions options)
        => new FileStream(Resolve(filePath), mode, access, share, bufferSize, options);

    public void DeleteFile(string path) => File.Delete(Resolve(path));
    public long GetFileSize(string path) => new FileInfo(Resolve(path)).Length;
}
