// What BC's compiler read through its IFileSystem during one compile, so the change model can hash
// it beside the `.al` files (#5087). The population is whatever the compiler actually asks for, not
// a list kept here: a layout file, a ControlAddIn resource, the Translations folder today, and
// anything BC adds later. Rules: docs/server-mode.md#affectedonly-and-files-the-compile-reads.
using System.Security.Cryptography;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner;

internal sealed class CompileFileReads
{
    internal const string FilePrefix = "file:";
    private const string ListPrefix = "list:";
    internal const string DirPrefix = "dir:";
    private const string Missing = "<missing>";

    private readonly object _lock = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys
    {
        get { lock (_lock) return _keys.ToList(); }
    }

    private void Add(string key) { lock (_lock) _keys.Add(key); }

    /// <summary>The file system BC's compiler is handed, recording every read it makes through <paramref name="inner"/>.</summary>
    internal NavCA.IFileSystem Wrap(NavCA.IFileSystem inner) => new Recording(inner, this);

    internal static string AbsoluteKey(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
    }

    /// <summary>
    /// The current fingerprint of each key: a file's content hash (or "&lt;missing&gt;"), a listing's
    /// names, a directory's existence. The same call at record time and at diff time, so equal means
    /// "the compiler would read the same thing".
    /// </summary>
    internal static Dictionary<string, string> Fingerprint(string appRootDir, IEnumerable<string> keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        NavCA.IFileSystem? listing = null;
        foreach (var key in keys)
        {
            if (key.StartsWith(FilePrefix, StringComparison.Ordinal))
            {
                var path = key[FilePrefix.Length..];
                try
                {
                    result[key] = File.Exists(path)
                        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                        : Missing;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result[key] = "<unreadable>";
                }
            }
            else if (key.StartsWith(DirPrefix, StringComparison.Ordinal))
            {
                result[key] = Directory.Exists(key[DirPrefix.Length..]) ? "1" : "0";
            }
            else if (key.StartsWith(ListPrefix, StringComparison.Ordinal))
            {
                var body = key[ListPrefix.Length..];
                var split = body.IndexOf('|');
                var directory = body[..split];
                var pattern = body[(split + 1)..];
                listing ??= new NavCA.RelativeFileSystem(appRootDir);
                try
                {
                    // Names relative to the app root: the same tree under another directory lists the same
                    // (#5368, a cache entry moves with its bundle). A rename is a change, a count is not the identity.
                    var names = (directory.Length == 0 ? listing.GetFiles(pattern) : listing.GetFiles(directory, pattern))
                        .Select(n => (Path.IsPathRooted(n) ? Path.GetRelativePath(appRootDir, n) : n).Replace('\\', '/'))
                        .OrderBy(n => n, StringComparer.Ordinal);
                    result[key] = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", names))));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    result[key] = "<unlistable>";
                }
            }
            else
            {
                throw new InvalidOperationException($"unknown compile-input key '{key}'");
            }
        }
        return result;
    }

    private sealed class Recording : NavCA.IFileSystem
    {
        private readonly NavCA.IFileSystem _inner;
        private readonly CompileFileReads _reads;

        public Recording(NavCA.IFileSystem inner, CompileFileReads reads) { _inner = inner; _reads = reads; }

        private void File_(string path) => _reads.Add(FilePrefix + AbsoluteKey(_inner.GetAbsolutePath(path)));
        private void List(string directory, string pattern) => _reads.Add($"{ListPrefix}{directory}|{pattern}");

        // Reads: every one of these decides what the compile sees, so each is a recorded input.
        public byte[] ReadBytes(string path) { File_(path); return _inner.ReadBytes(path); }
        public byte[] ReadBytes(string path, int count) { File_(path); return _inner.ReadBytes(path, count); }
        public bool Exists(string path) { File_(path); return _inner.Exists(path); }
        public Stream OpenRead(string path) { File_(path); return _inner.OpenRead(path); }
        public long GetFileSize(string path) { File_(path); return _inner.GetFileSize(path); }
        public Stream OpenFile(string filePath, FileMode mode, FileAccess access, FileShare share = FileShare.None,
            int bufferSize = 4096, FileOptions options = FileOptions.None)
        {
            if (access == FileAccess.Read) File_(filePath);
            return _inner.OpenFile(filePath, mode, access, share, bufferSize, options);
        }
        public IEnumerable<string> GetFiles(string searchPattern) { List("", searchPattern); return _inner.GetFiles(searchPattern); }
        public IEnumerable<string> GetFiles(string directory, string searchPattern) { List(directory, searchPattern); return _inner.GetFiles(directory, searchPattern); }
        public IEnumerable<string> GetFilesRecursively(string directory) { List(directory, "**"); return _inner.GetFilesRecursively(directory); }
        public bool DirectoryExists(string directory) { _reads.Add(DirPrefix + AbsoluteKey(_inner.GetAbsolutePath(directory))); return _inner.DirectoryExists(directory); }

        // Not reads of a file's content or of the tree: pass through.
        public bool DirectoryExistsForFile(string path) => _inner.DirectoryExistsForFile(path);
        public string GetDirectoryPath() => _inner.GetDirectoryPath();
        public string GetAbsolutePath(string relativePath) => _inner.GetAbsolutePath(relativePath);
        public void WriteBytes(string path, byte[] content) => _inner.WriteBytes(path, content);
        public void CreateDirectoryForFile(string path) => _inner.CreateDirectoryForFile(path);
        public Stream CreateFile(string path) => _inner.CreateFile(path);
        public Stream OpenWrite(string path) => _inner.OpenWrite(path);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
    }
}
