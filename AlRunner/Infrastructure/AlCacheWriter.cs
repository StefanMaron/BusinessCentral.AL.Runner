// AlCacheWriter — atomic publish for AL-output cache entries.
//
// Issue #1810: the write path used to publish a cache entry with a plain in-place
// File.WriteAllBytes(cachePath, …) for the DLL, then write its sidecars afterwards.
// The DLL — the file whose presence AlCacheSidecars.IsCompleteEntry treats as "this
// entry exists" — became visible to concurrent readers BEFORE the sidecars it needs
// to be usable, and before it was even fully written: a reader's File.ReadAllBytes on
// a file another process is still writing is not an I/O error, it is a short read
// that silently hands back truncated bytes. Those bytes flowed straight to
// Assembly.Load with no validation and threw BadImageFormatException — far from the
// cache-classification code, reading as mysterious flakiness rather than a cache bug.
//
// Fix: every artifact is written to a temporary file in the SAME directory as its
// final path (same volume ⇒ File.Move(overwrite:true) is atomic on both Linux and
// Windows), then renamed into place. Callers publish sidecars first and the DLL last,
// so the DLL's appearance is the commit point IsCompleteEntry already assumes. A
// reader observing the directory at any point during a publish therefore either sees
// the previous complete entry (untouched, still readable), no entry (all temps are
// invisible under their real names), or the new complete entry — never a DLL with a
// missing or partial sidecar, and never a partial DLL.
//
// No locking is needed: concurrent writers of the SAME cache key produce byte-identical
// output (the key hashes sources + deps + runner fingerprint), so last-writer-wins
// between two atomic renames is correct.
namespace AlRunner.Infrastructure;

public static class AlCacheWriter
{
    /// <summary>
    /// Writes content to a temp file beside <paramref name="finalPath"/> (same
    /// directory, so the rename stays on one volume) via <paramref name="writeContent"/>,
    /// then atomically renames it into place. Returns whatever <paramref name="writeContent"/>
    /// returns, so callers that need a value out of the write (e.g. an entry count for
    /// logging) don't need a second helper.
    /// </summary>
    public static T AtomicPublish<T>(string finalPath, Func<string, T> writeContent)
    {
        var dir = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var tmp = Path.Combine(dir, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var result = writeContent(tmp);
            File.Move(tmp, finalPath, overwrite: true);
            return result;
        }
        finally
        {
            // writeContent may have thrown before creating tmp, or File.Move above may
            // already have consumed/renamed it away — only clean up if it's still there.
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup only */ }
            }
        }
    }

    /// <inheritdoc cref="AtomicPublish{T}(string, Func{string, T})"/>
    public static void AtomicPublish(string finalPath, Action<string> writeContent)
        => AtomicPublish<object?>(finalPath, tmp => { writeContent(tmp); return null; });
}
