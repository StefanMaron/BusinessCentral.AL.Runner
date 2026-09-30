using System.Text.RegularExpressions;

namespace AlRunner.Infrastructure;

/// <summary>
/// The filesystem primitives <see cref="NclFilePublisher"/> uses. Virtual so a test can stand
/// in for Windows behaviour (a refused rename, a delete refused because another process has the
/// file mapped) on any OS. <see cref="Real"/> is the only production instance.
/// </summary>
internal class NclFileOps
{
    public static readonly NclFileOps Real = new();

    public virtual bool Exists(string path) => File.Exists(path);

    /// <summary>True for a regular file; false for a missing path or a symlink, whose content
    /// can change underneath us without our path being written.</summary>
    public virtual bool IsRegularFile(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.LinkTarget == null;
    }

    /// <summary>Opens with <see cref="FileShare.Delete"/> and <see cref="FileShare.ReadWrite"/>
    /// so the read never blocks a sibling's rename over the same name.</summary>
    public virtual byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    public virtual void WriteAllBytes(string path, byte[] contents) => File.WriteAllBytes(path, contents);

    /// <summary>A single rename that replaces the target: rename(2) on Unix, MoveFileEx with
    /// MOVEFILE_REPLACE_EXISTING on Windows. The name never stops existing.</summary>
    public virtual void Move(string source, string dest) => File.Move(source, dest, overwrite: true);

    /// <summary>Win32 ReplaceFile on Windows: renames the old file aside, then moves the new one
    /// in, so the name is briefly absent (#5018) and the renamed-aside file is left behind as
    /// <c>~RF*.TMP</c> when another process has it loaded (#5019).</summary>
    public virtual void Replace(string source, string dest) => File.Replace(source, dest, destinationBackupFileName: null);

    public virtual void Delete(string path) => File.Delete(path);

    public virtual IEnumerable<string> EnumerateFiles(string dir, string searchPattern) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, searchPattern,
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 })
            : Array.Empty<string>();

    public virtual void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
}

/// <summary>
/// Publishes the Cecil-rewritten Ncl.dll (and its ncl-cecil cache entry) without ever
/// truncating a file in place, and without rewriting a destination that already holds the
/// exact bytes. See docs/ncl-shadow-runtime.md#publishing-ncldll-5018-5019.
/// </summary>
internal static class NclFilePublisher
{
    internal enum Outcome { AlreadyCurrent, Written }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="destPath"/> unless it is already a
    /// regular file holding exactly those bytes. Every process sharing one shadow dir or one
    /// install dir computes the same bytes, so after the first start this is a no-op: no
    /// ReplaceFile window for a starting sibling to fall into (#5018) and no <c>~RF*.TMP</c>
    /// backup left behind by a sibling that has the file loaded (#5019).
    /// </summary>
    internal static Outcome PublishIfChanged(string destPath, byte[] contents, NclFileOps ops)
    {
        if (IsAlreadyCurrent(destPath, contents, ops)) return Outcome.AlreadyCurrent;
        AtomicReplace(destPath, contents, ops);
        return Outcome.Written;
    }

    /// <summary>
    /// True only when equality was positively established. A symlink, a missing file, or a read
    /// that keeps failing answers false, and the caller writes — the old always-replace
    /// behaviour — so an unreadable destination can never be mistaken for a current one.
    /// </summary>
    internal static bool IsAlreadyCurrent(string destPath, byte[] contents, NclFileOps ops)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (!ops.IsRegularFile(destPath)) return false;
                var existing = ops.ReadAllBytesShared(destPath);
                return existing.AsSpan().SequenceEqual(contents);
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) when (attempt < maxAttempts) { ops.Sleep(250); }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { ops.Sleep(250); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    /// <summary>
    /// Publishes by writing a sibling temp file and renaming it over the destination. Never
    /// truncate-and-rewrite a DLL in place: a live process has it memory-mapped, and rewriting
    /// the mapped inode is the SIGBUS / exit-135 crash class.
    ///
    /// Order on the first attempt: the single-step rename (<see cref="NclFileOps.Move"/>) first,
    /// because it never makes the name disappear (#5018); ReplaceFile second, because it gets
    /// past a real-time antivirus scanner holding the fresh temp file, which defeats MoveFileEx
    /// (#1650, measured on Windows 11 with Defender); then bounded, backed-off rename retries.
    /// ReplaceFile is therefore reached only when a rename was refused.
    /// </summary>
    internal static void AtomicReplace(string destPath, byte[] contents, NclFileOps ops)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
        var tempPath = Path.Combine(dir, Path.GetFileName(destPath) + ".tmp." + Guid.NewGuid().ToString("N"));
        try
        {
            ops.WriteAllBytes(tempPath, contents);

            const int maxAttempts = 60;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (attempt == 1)
                    {
                        try { ops.Move(tempPath, destPath); return; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

                        if (ops.Exists(destPath))
                        {
                            try { ops.Replace(tempPath, destPath); return; }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
                        }
                        ops.Sleep(500);
                        continue;
                    }
                    ops.Move(tempPath, destPath);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts) { ops.Sleep(500); }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts) { ops.Sleep(500); }
            }
        }
        catch
        {
            try { if (ops.Exists(tempPath)) ops.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Deletes the <c>&lt;fileName&gt;~RF&lt;hex&gt;.TMP</c> backups Win32 ReplaceFile leaves
    /// beside <paramref name="fileName"/> when it cannot delete the replaced file (#5019). A
    /// backup another live process still has loaded is exactly the file ReplaceFile itself could
    /// not delete, so the delete is refused here too and the file is left for a later start.
    /// Returns how many were removed.
    /// </summary>
    internal static int ReapReplaceBackups(string dir, string fileName, NclFileOps ops)
    {
        var exact = new Regex("^" + Regex.Escape(fileName) + @"~RF[0-9A-Fa-f]+\.TMP$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var removed = 0;
        IEnumerable<string> candidates;
        try { candidates = ops.EnumerateFiles(dir, fileName + "~RF*.TMP").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }

        foreach (var path in candidates)
        {
            if (!exact.IsMatch(Path.GetFileName(path))) continue;
            try { ops.Delete(path); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (removed > 0)
            Console.Error.WriteLine($"[Cecil] Removed {removed} leftover {fileName}~RF*.TMP backup file(s) from {dir}");
        return removed;
    }
}
