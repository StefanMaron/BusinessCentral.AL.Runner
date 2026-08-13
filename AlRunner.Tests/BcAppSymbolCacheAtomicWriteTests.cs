// BcAppSymbolCacheAtomicWriteTests — pins that BcAppSymbolCache.TryWrite publishes the
// bc-symbols JSON cache atomically (issue #1809 follow-up, flagged during PR review:
// https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/1818).
//
// Gap being fixed
// ----------------
// TryWrite's cachePath is content-keyed (SHA-256 of appPath|hash:<.app content hash>|
// CacheVersion — the hash component switched from length+mtime to a content hash of the
// .app's bytes in #1820), so two subprocesses parsing the SAME dependency .app
// concurrently compute and write the SAME cachePath. The old implementation was a plain
// `File.WriteAllText(cachePath, json)`
// — FileMode.Create truncates the target to zero bytes the instant the call starts, then
// streams the new content in. A reader (BcAppSymbolCache.TryRead, or any other process's
// TryWrite about to overwrite the same path) that lands inside that window sees a
// zero-length or partial file, not old content and not new content. TryRead already
// treats that as a cache miss (catch-all around JsonSerializer.Deserialize), so this was
// never a crash — but it silently downgrades a cache HIT to a wasted MISS+reparse, and
// parallelizing AlRunner.Tests's subprocess collections (#1809) raises how often that
// window gets hit.
//
// The fix (already proven generically by AlCacheWriterTests.cs for issue #1810) is
// AlCacheWriter.AtomicPublish: write the new content to a temp file in the same
// directory, then File.Move(overwrite: true) it onto cachePath — atomic on both Linux
// rename(2) and Windows MoveFileEx. A reader can now only ever observe "old content, in
// full" or "new content, in full" — never a truncated file.
//
// Test strategy
// -------------
// A real concurrent-interleaving race is inherently non-deterministic (that is the whole
// nature of the bug), so — mirroring AlCacheWriterTests's own note that such races are
// "impractical to make deterministic" — this test instead makes the write SLOW ENOUGH
// (tens of thousands of synthetic ObjectSymbol entries, several MB of JSON) that a tight
// polling loop on another thread reliably lands inside the write window many times over
// its duration, then asserts the polling loop NEVER observed a truncated/unparseable
// file. Run against the pre-fix `File.WriteAllText` implementation this reliably fails
// (RED); against AlCacheWriter.AtomicPublish it reliably passes (GREEN) because the
// target path's content only ever transitions atomically.
using System.Text.Json;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcAppSymbolCacheAtomicWriteTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-symbol-cache-atomic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Cheap way to reach a symbols instance with a large-but-valid AppSymbols.Get/TryWrite
    // shape without hand-rolling ParsedTable/ReportSymbol trees: parse a tiny real
    // SymbolReference.json via GetFromJson to get an empty-but-valid AppSymbols, then only
    // the Objects list — the flat (Kind, Id, Name) sweep — needs inflating to make the
    // serialized payload large. BcAppSymbolCache.AppSymbols and ObjectSymbol are both
    // internal records, visible here via InternalsVisibleTo.
    private static BcAppSymbolCache.AppSymbols BuildLargeSymbols(int objectCount)
    {
        var objects = new List<BcAppSymbolCache.ObjectSymbol>(objectCount);
        for (int i = 0; i < objectCount; i++)
        {
            objects.Add(new BcAppSymbolCache.ObjectSymbol(
                "Codeunit", i,
                "Synthetic Object For Atomic Write Test Padding " + i,
                "Some Caption Text To Add More Bytes " + i));
        }
        return new BcAppSymbolCache.AppSymbols(
            new List<ParsedTable>(), new List<BcAppSymbolCache.EnumSymbol>(), new List<BcAppSymbolCache.QuerySymbol>(),
            objects, new List<BcAppSymbolCache.ReportSymbol>(), new List<BcAppSymbolCache.PageSymbol>());
    }

    [Fact]
    public void TryWrite_NeverExposesATruncatedOrUnparseableFile_ToAConcurrentReader()
    {
        var dir = NewTempDir();
        try
        {
            var cachePath = Path.Combine(dir, "entry.json");

            // Seed with valid "old" content so a torn write would visibly replace
            // complete-and-valid bytes with an incomplete file, not just create one.
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new { marker = "old-content" }));

            // #1820: TryWrite now takes the content hash directly, not a FileInfo — its
            // actual value is irrelevant to this atomic-publish test.
            var contentHash = "deadbeef";

            var bigSymbols = BuildLargeSymbols(objectCount: 60_000);

            var sawTornRead = false;
            string? tornSample = null;
            var stop = false;

            var reader = Task.Run(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    string content;
                    try
                    {
                        if (!File.Exists(cachePath)) continue;
                        content = File.ReadAllText(cachePath);
                    }
                    catch (IOException)
                    {
                        // Benign: landed exactly on the rename/replace syscall. Not the
                        // defect under test — the defect is CONTENT corruption, not a
                        // transient sharing violation.
                        continue;
                    }

                    if (content.Length == 0)
                    {
                        sawTornRead = true;
                        tornSample = "<empty>";
                        break;
                    }
                    try
                    {
                        using var doc = JsonDocument.Parse(content);
                    }
                    catch (JsonException)
                    {
                        sawTornRead = true;
                        tornSample = content.Length > 80 ? content[..80] + "…" : content;
                        break;
                    }
                }
            });

            BcAppSymbolCache.TryWrite(cachePath, contentHash, bigSymbols);

            Volatile.Write(ref stop, true);
            reader.Wait(TimeSpan.FromSeconds(10));

            Assert.False(sawTornRead,
                $"Concurrent reader observed a truncated/unparseable bc-symbols cache file (sample: {tornSample}). " +
                "TryWrite must publish atomically (AlCacheWriter.AtomicPublish), never via an in-place " +
                "File.WriteAllText that truncates the target before the new content is ready.");

            // And the final on-disk content is exactly the new payload, not a mix.
            var finalDoc = JsonDocument.Parse(File.ReadAllText(cachePath));
            Assert.Equal(60_000, finalDoc.RootElement.GetProperty("Objects").GetArrayLength());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TryWrite_LeavesNoTempFileBehindOnSuccess()
    {
        var dir = NewTempDir();
        try
        {
            var cachePath = Path.Combine(dir, "entry.json");

            BcAppSymbolCache.TryWrite(cachePath, "deadbeef", BuildLargeSymbols(objectCount: 1));

            var leftovers = Directory.GetFiles(dir).Where(f => !string.Equals(f, cachePath)).ToArray();
            Assert.Empty(leftovers);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
