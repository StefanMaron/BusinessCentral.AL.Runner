// BcAppSymbolCachePartialParseTests — proves #2712 at the parse layer: a table-extension
// parse that fails part-way through must THROW, never return (and cache) the partial list
// it had collected so far.
//
// What went wrong
// ---------------
// BcAppSymbolCache.ParseTableExtensions caught every exception, logged it only to PerfTrace
// (off unless AL_RUNNER_PERF=1) and returned `result.Values.ToList()` — whatever it had
// parsed before the failure. GetTableExtensions then stored that partial list in
// TableExtensionCache, so one failure permanently poisoned the process with an incomplete
// extension index. Reported with an OutOfMemoryException while parsing Base Application's
// SymbolReference.json: 90 of 96 table extensions dropped, 47 tests flipped, exit code
// unchanged.
//
// Reproducer used here (deterministic, no memory pressure needed)
// ----------------------------------------------------------------
// A SymbolReference.json whose TableExtensions[] holds one valid entry followed by one with a
// field whose "Id" is a string. JsonElement.TryGetInt32 THROWS InvalidOperationException when
// the element is not a number (it does not return false), so the second entry fails after the
// first has already been collected — exactly the partial-then-fail shape of the OOM case.
// BcAppSymbolCache.Parse (behind Get) reads an extension's Id/Name/Properties for the object
// list but never its Fields, so the same file reads fine through Get; only the extension
// parse is affected, which is what makes the partial invisible everywhere else.

using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same collection as BcAppSymbolCacheTableExtTests (#1821): GetTableExtensions consults the
// process-global CacheRoots override, so it must not race CacheRootsTests's SetOverride calls.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class BcAppSymbolCachePartialParseTests : IDisposable
{
    private readonly string _dir;

    public BcAppSymbolCachePartialParseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "al-runner-2712-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteApp(string symbolReferenceJson)
    {
        var appPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    private const string ValidExtension = """
        {
          "TargetObject": "#f3552374a1f24356848e196002525837#Source Code Setup",
          "Fields": [
            { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": 2, "Name": "Sales" }
          ],
          "Id": 243,
          "Name": "SourceCodeSetupExt"
        }
        """;

    // The FIELD's "Id" is a string: TryGetInt32 throws InvalidOperationException on a
    // non-Number element. The poison sits inside Fields[] on purpose — BcAppSymbolCache.Parse
    // reads an extension entry's Id/Name/Properties for the object list but never its Fields,
    // so Get() succeeds on this file and only the extension parse fails.
    private const string PoisonExtension = """
        {
          "TargetObject": "Customer",
          "Fields": [
            { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": "not-a-number", "Name": "PoisonField" }
          ],
          "Id": 244,
          "Name": "PoisonExt"
        }
        """;

    [Fact]
    public void GetTableExtensions_FailureAfterFirstEntry_ThrowsAndCachesNothing()
    {
        var appPath = WriteApp($$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [],
              "TableExtensions": [ {{ValidExtension}}, {{PoisonExtension}} ]
            }
            """);

        // [WHEN] the parse fails on the second entry
        // [THEN] it throws — it must NOT hand back the one extension it had already collected.
        var ex = Assert.Throws<BcAppSymbolReadException>(() => BcAppSymbolCache.GetTableExtensions(appPath));
        Assert.Equal(appPath, ex.AppPath);
        Assert.Contains(Path.GetFileName(appPath), ex.Message);
        Assert.Contains("table extensions", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);

        // [THEN] nothing was cached for that key: a second call re-parses and fails the same
        // way. Before the fix the second call returned the cached partial list (one entry).
        Assert.Throws<BcAppSymbolReadException>(() => BcAppSymbolCache.GetTableExtensions(appPath));
    }

    [Fact]
    public void GetTableExtensions_CompleteParse_ReturnsAllAndCachesOneInstance()
    {
        var appPath = WriteApp($$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [],
              "TableExtensions": [ {{ValidExtension}} ]
            }
            """);

        var first = BcAppSymbolCache.GetTableExtensions(appPath);
        var ext = Assert.Single(first);
        Assert.Equal(243, ext.ExtensionId);
        Assert.Equal("Source Code Setup", ext.TargetTableName);
        Assert.Equal(2, Assert.Single(ext.Fields).FieldId);

        // A COMPLETE parse is still cached: the second call is served the same instance.
        var second = BcAppSymbolCache.GetTableExtensions(appPath);
        Assert.Same(first, second);
    }
}
