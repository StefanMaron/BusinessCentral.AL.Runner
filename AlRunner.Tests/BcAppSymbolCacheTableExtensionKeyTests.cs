// BcAppSymbolCacheTableExtensionKeyTests — issue #2846 case 2: BcAppSymbolCache answered the
// same question about the same file two different ways.
//
// The asymmetry
// -------------
// `BcAppSymbolCache.Get` identifies a `.app` by its CONTENT — `{fullPath}|hash:{contentHash}|
// v{CacheVersion}|shape:{...}`. That content hash replaced a `Length`/`LastWriteTimeUtc` stat
// in #1820, for the reason #1815 recorded one layer over: CI re-downloads every platform and
// test-toolkit `.app` on every run, so the mtime is fresh even when the bytes are byte-for-byte
// identical to the previous run's, and an mtime-keyed entry MISSes unconditionally regardless
// of content.
//
// `BcAppSymbolCache.GetTableExtensions`, in the same static class, over the same files, never
// got the same treatment:
//
//     var key = $"{Path.GetFullPath(appPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|v{CacheVersion}";
//
// Two consequences, and the first is the one with a cost attached:
//
//   1. A touched-but-unchanged package reparses every tableextension in it. For Base
//      Application that is the whole SymbolReference.json — the same parse #2712 measured as
//      worth 96 table extensions and 47 test results when it went wrong.
//   2. The two caches DISAGREE about which byte state of one file they are describing. The
//      table index and the table-extension index are built from the same package by the same
//      class, and nothing made them agree; one keyed on content, the other on a stat.
//
// What this does NOT claim
// ------------------------
// It does not make either cache notice a package rewritten in place mid-process.
// `ComputeAppContentHash` memoizes per full path for the life of the process, deliberately
// (see its comment: `Get()` is called once per virtual-table lookup, often a dozen times for
// the same `.app`). So after this change both caches are consistently pinned to the byte state
// first observed at that path — which is the point. Before it, `Get()` was pinned and
// `GetTableExtensions` was not, so a mid-process rewrite made the two indexes describe
// different byte states of one file. Agreement is the property under test here, not detection.

using Xunit;
using AlRunner.Infrastructure;
using AlRunner.Patches;

namespace AlRunner.Tests;

public sealed class BcAppSymbolCacheTableExtensionKeyTests : IDisposable
{
    private readonly string _scratch;

    public BcAppSymbolCacheTableExtensionKeyTests()
    {
        _scratch = TestScratch.Dir("al-runner-bcappsymbol-tableext-key");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// The defect. Touching a package's mtime without changing a byte must not reparse its
    /// table extensions — exactly what #1820 established for <c>Get</c>, which shares this
    /// class, these files and this CacheVersion.
    ///
    /// <para>Asserted by reference identity: a cache HIT hands back the very list instance it
    /// stored, a MISS builds a fresh one in ParseTableExtensions. The concrete parsed values
    /// are asserted alongside it so a cache that "hit" by handing back an empty or default
    /// list could not pass.</para>
    /// </summary>
    [SkippableFact]
    public void TouchingTheMtimeOfAnUnchangedPackage_DoesNotReparseItsTableExtensions()
    {
        TestArtifacts.SkipIfMissing();

        var app = WriteExtensionApp("touched", extensionId: 70001, fieldId: 50001, fieldName: "Loyalty Points");

        var first = BcAppSymbolCache.GetTableExtensions(app);
        AssertOneExtension(first, 70001, 50001, "Loyalty Points");

        // Content untouched; only the timestamp moves — the CI re-download shape from #1815.
        var before = new FileInfo(app).LastWriteTimeUtc;
        File.SetLastWriteTimeUtc(app, before.AddDays(7));
        Assert.NotEqual(before.Ticks, new FileInfo(app).LastWriteTimeUtc.Ticks);

        var second = BcAppSymbolCache.GetTableExtensions(app);

        Assert.Same(first, second);
        AssertOneExtension(second, 70001, 50001, "Loyalty Points");
    }

    /// <summary>
    /// The invariant the two caches must share: after the same mtime touch, <c>Get</c> and
    /// <c>GetTableExtensions</c> must BOTH still be describing the byte state they first saw.
    /// Before the fix <c>Get</c> hit and <c>GetTableExtensions</c> missed, so the table index
    /// and the table-extension index for one package were built from two different reads of it.
    /// </summary>
    [SkippableFact]
    public void AfterAnMtimeTouch_GetAndGetTableExtensions_AgreeOnTheSameIdentity()
    {
        TestArtifacts.SkipIfMissing();

        var app = WriteExtensionApp("agree", extensionId: 70011, fieldId: 50011, fieldName: "Agree Field");

        var symbolsBefore = BcAppSymbolCache.Get(app);
        var extsBefore = BcAppSymbolCache.GetTableExtensions(app);

        File.SetLastWriteTimeUtc(app, new FileInfo(app).LastWriteTimeUtc.AddDays(3));

        var symbolsAfter = BcAppSymbolCache.Get(app);
        var extsAfter = BcAppSymbolCache.GetTableExtensions(app);

        // Get already had this property (#1820). GetTableExtensions is the half that did not.
        Assert.Same(symbolsBefore, symbolsAfter);
        Assert.Same(extsBefore, extsAfter);
        AssertOneExtension(extsAfter, 70011, 50011, "Agree Field");
    }

    /// <summary>
    /// The direction that stops the fix from being "always hit": two genuinely different
    /// packages must still get their own parses and their own answers. A content key that
    /// over-shared — dropping the path, say — would pass the arms above and fail here.
    /// </summary>
    [SkippableFact]
    public void TwoDifferentPackages_EachGetTheirOwnTableExtensions()
    {
        TestArtifacts.SkipIfMissing();

        var appA = WriteExtensionApp("distinct-a", extensionId: 70021, fieldId: 50021, fieldName: "Alpha Field");
        var appB = WriteExtensionApp("distinct-b", extensionId: 70022, fieldId: 50022, fieldName: "Beta Field");

        var a = BcAppSymbolCache.GetTableExtensions(appA);
        var b = BcAppSymbolCache.GetTableExtensions(appB);

        Assert.NotSame(a, b);
        AssertOneExtension(a, 70021, 50021, "Alpha Field");
        AssertOneExtension(b, 70022, 50022, "Beta Field");
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────

    private static void AssertOneExtension(
        IReadOnlyList<TableExtensionSymbol> exts, int extensionId, int fieldId, string fieldName)
    {
        var ext = Assert.Single(exts);
        Assert.Equal(extensionId, ext.ExtensionId);
        Assert.Equal("Customer", ext.TargetTableName);
        var field = Assert.Single(ext.Fields);
        Assert.Equal(fieldId, field.FieldId);
        Assert.Equal(fieldName, field.FieldName);
        Assert.Equal("Integer", field.TypeName);
    }

    /// <summary>
    /// A registrable `.app` whose SymbolReference.json declares exactly one tableextension on
    /// "Customer". Written at its own directory: every arm here needs a DISTINCT path, because
    /// ComputeAppContentHash memoizes per full path for the process.
    /// </summary>
    private string WriteExtensionApp(string subdir, int extensionId, int fieldId, string fieldName)
    {
        var bundleDir = Path.Combine(_scratch, subdir, "src");
        var appPath = Path.Combine(_scratch, subdir, $"AL Runner_TableExt {extensionId}_1.0.0.0.app");
        var appId = new Guid($"2846d00{extensionId % 10}-1111-4222-8333-4444555566{extensionId % 100:D2}");

        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "TableExt {{extensionId}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{extensionId}}, "to": {{extensionId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(bundleDir, "Ext.al"), $$"""
        tableextension {{extensionId}} "TableExt {{extensionId}}" extends Customer
        {
            fields
            {
                field({{fieldId}}; "{{fieldName}}"; Integer) { DataClassification = CustomerContent; }
            }
        }
        """);

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            AppId = appId.ToString(),
            Name = $"TableExt {extensionId}",
            Publisher = "AL Runner",
            Version = "1.0.0.0",
            Tables = Array.Empty<object>(),
            TableExtensions = new[]
            {
                new
                {
                    Id = extensionId,
                    Name = $"TableExt {extensionId}",
                    TargetObject = "Customer",
                    Fields = new object[]
                    {
                        new { Id = fieldId, Name = fieldName, TypeDefinition = new { Name = "Integer" } },
                    },
                },
            },
            Codeunits = Array.Empty<object>(),
            Pages = Array.Empty<object>(),
            EnumTypes = Array.Empty<object>(),
            Queries = Array.Empty<object>(),
        });

        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(bundleDir, "app.json"))
            ?? throw new InvalidOperationException("could not read the identity just written");
        Directory.CreateDirectory(Path.GetDirectoryName(appPath)!);
        InProcessAppPackager.EmitAppPackageToFile(
            bundleDir, identity, appPath, System.Text.Encoding.UTF8.GetBytes(json));
        return appPath;
    }
}
