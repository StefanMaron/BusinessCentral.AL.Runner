// DependencyMetadataMemoInvalidationTests — issue #2889: the synthesized-metadata memos for
// PRECOMPILED dependency objects must not outlive the registration set they were derived
// from.
//
// The state under test
// --------------------
//   RecordPatches._depPageMetadataXml    (AlRunner/Patches/DependencyPageMetadataXml.cs)
//   RecordPatches._depReportMetadataXml  (AlRunner/Patches/DependencyReportMetadata.cs)
//
// Two ConcurrentDictionary<int, string?> memos, each filled by a GetOrAdd whose factory
// walks _bcAppPaths and reads each registered .app's SymbolReference.json. Both cached the
// NEGATIVE answer as well as the positive one, and both justified it identically: "Result
// cached per id (including the null), since the answer is a property of the loaded
// dependency set." That premise is only sound while the loaded dependency set cannot
// change under the memo — and it changes twice:
//
//   * AddBcAppPath GROWS it, on every dependency registration, and its last act is
//     InvalidateBcAppIndexes() precisely so "newly-added .app gets picked up on next miss".
//     Every index derived from _bcAppPaths was dropped there; these two memos were not.
//   * ResetForReload SHRINKS it, on every --server request and every --watch cycle: since
//     #2755 / PR #2873 it calls ClearPerBundleBcAppPaths, which REMOVES the previous
//     bundle's registrations and then funnels through that same InvalidateBcAppIndexes().
//     So a memo surviving a roll answers from an .app the process no longer has registered.
//
// Why this is a mechanism test and not a corpus test
// -------------------------------------------------
// The claim is about cache lifetime across a registration-set change inside ONE runner
// process — multi-bundle / server-mode wiring, which
// .claude/rules/bc-behavior-tests-go-upstream.md names as explicitly runner-specific. No
// BC service tier has an opinion about it: real BC has no such memo, and the AL a test
// would run is identical either way. What AL observes when a page's metadata IS available
// is plain BC behaviour and is already pinned upstream; this file pins only that the
// runner keeps answering from the currently-registered set.
//
// Both polarities are covered because a positive-only test passes on the broken build for
// the half that matters most: the cached null is the one that makes an object that DOES
// exist permanently invisible, with no error and an unchanged exit code.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection, not CacheRootsSerialCollection: this class calls
// RecordPatches.ResetForReload() directly, which ParserStaticsIsolationGuardTests requires.
// Both collections set DisableParallelization and xUnit runs every such collection serially
// relative to every other one (CollectionCostOrderer.cs), so BcAppSymbolCache.Get()'s
// process-global CacheRoots resolution is still uncontended here — the same reasoning
// RecordPatchesWarmReloadExtensionIndexTests records for the same pair of collections.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class DependencyMetadataMemoInvalidationTests : IDisposable
{
    private readonly string _root;

    public DependencyMetadataMemoInvalidationTests()
    {
        _root = TestScratch.Dir("al-runner-2889-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Ids process-wide unique among AlRunner.Tests statics (_bcAppPaths and both memos are
    // process-global and nothing in the test host unregisters an .app), and outside every
    // neighbouring file's block: DependencyPageMetadataXmlTests owns 881234xx and
    // DependencyReportProcessingOnlyTests owns 881236xx, so this file uses 881235xx.
    private const int UnrelatedPageId = 88123510;
    private const int LatePageId = 88123511;
    private const int ReloadPageId = 88123512;
    private const int UnrelatedReportId = 88123550;
    private const int LateReportId = 88123551;

    private string WriteApp(string symbolReferenceJson)
    {
        var appPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    private static string PagesJson(params (int Id, string Name, string PageType)[] pages)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"RuntimeVersion\": \"15.1\", \"Pages\": [");
        for (var i = 0; i < pages.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($$"""
                {
                  "Id": {{pages[i].Id}},
                  "Name": "{{pages[i].Name}}",
                  "Properties": [
                    { "Name": "PageType", "Value": "{{pages[i].PageType}}" },
                    { "Name": "SourceTable", "Value": "700" }
                  ]
                }
                """);
        }
        sb.Append("] }");
        return sb.ToString();
    }

    private static string ReportsJson(params (int Id, string Name)[] reports)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"RuntimeVersion\": \"15.1\", \"Namespaces\": [ { \"Name\": \"DMMI\", \"Reports\": [");
        for (var i = 0; i < reports.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($$"""
                {
                  "Id": {{reports[i].Id}},
                  "Name": "{{reports[i].Name}}",
                  "Properties": [ { "Name": "Caption", "Value": "{{reports[i].Name}}" } ]
                }
                """);
        }
        sb.Append("] } ] }");
        return sb.ToString();
    }

    /// <summary>
    /// The cached NULL half, for pages. Bundle 1 asks about a page no registered .app
    /// declares; bundle 2's own dependency declares it. Before the fix the memoized null
    /// was served forever, so RunnerXmlMetadataLoader fell through to its out-of-scope
    /// throw for a page whose metadata was sitting in a registered symbol file.
    /// </summary>
    [Fact]
    public void ANullPageAnswer_DoesNotSurviveALaterAppDeclaringThatPage()
    {
        RecordPatches.AddBcAppPath(WriteApp(PagesJson((UnrelatedPageId, "DMMI Unrelated Page", "List"))));

        // Precondition, and it memoizes the null. Asserted rather than assumed: if some
        // other test had already registered an .app declaring this id, the "after"
        // assertion below could pass without the fix.
        Assert.False(RecordPatches.HasDependencyPageMetadata(LatePageId));
        Assert.Null(RecordPatches.TryBuildDependencyPageMetadata(LatePageId));

        RecordPatches.AddBcAppPath(WriteApp(PagesJson((LatePageId, "DMMI Late Page", "Card"))));

        // Positive control: the live lookup never memoized anything, so it is already
        // right. That is what pins the failure below to the memo rather than to the
        // registration not having happened.
        Assert.True(RecordPatches.HasDependencyPageMetadata(LatePageId),
            "the second .app is registered and readable, so the live symbol lookup must see the page");

        var xml = RecordPatches.TryBuildDependencyPageMetadata(LatePageId);
        Assert.NotNull(xml);

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml!);
        var root = doc.DocumentElement!;
        Assert.Equal("PageDefinition", root.LocalName);
        Assert.Equal(LatePageId.ToString(), root.GetAttribute("ID"));
        Assert.Equal("DMMI Late Page", root.GetAttribute("Name"));
        var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        var properties = (System.Xml.XmlElement)root.SelectSingleNode("m:Properties", ns)!;
        Assert.Equal("Card", properties.GetAttribute("PageType"));
    }

    /// <summary>
    /// The same cached NULL half for the sibling memo one file over, which #2889 does not
    /// name and which is byte-for-byte the same shape — same GetOrAdd, same doc comment,
    /// same _bcAppPaths-derived factory.
    /// </summary>
    [Fact]
    public void ANullReportAnswer_DoesNotSurviveALaterAppDeclaringThatReport()
    {
        RecordPatches.AddBcAppPath(WriteApp(ReportsJson((UnrelatedReportId, "DMMI Unrelated Report"))));

        Assert.Null(RecordPatches.TryBuildDependencyReportMetadata(LateReportId));

        RecordPatches.AddBcAppPath(WriteApp(ReportsJson((LateReportId, "DMMI Late Report"))));

        var xml = RecordPatches.TryBuildDependencyReportMetadata(LateReportId);
        Assert.NotNull(xml);

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml!);
        Assert.Equal("Report", doc.DocumentElement!.LocalName);
        Assert.Equal(LateReportId.ToString(), doc.SelectSingleNode("/Report/ID")!.InnerText);
        Assert.Equal("DMMI Late Report", doc.SelectSingleNode("/Report/Name")!.InnerText);
    }

    /// <summary>
    /// The POSITIVE half: a synthesized document must not outlive the .app it was
    /// synthesized from, when a bundle roll unregisters that .app.
    ///
    /// <para>This is live on <c>main</c> as of #2755 / PR #2873, not latent.
    /// <c>ResetForReload</c> now calls <c>ClearPerBundleBcAppPaths</c>, which REMOVES the
    /// previous bundle's registrations from <c>_bcAppPaths</c> before funnelling through
    /// <see cref="RecordPatches"/>' <c>InvalidateBcAppIndexes</c>. So immediately after a
    /// roll the only honest answer for a page declared solely by bundle 1's .app is null,
    /// and a memo the roll did not clear answers with bundle 1's document instead — a
    /// document synthesized from an .app that is no longer registered at all.</para>
    ///
    /// <para>The assertion deliberately sits BETWEEN the roll and bundle 2's registration.
    /// Re-registering first and then asserting would prove nothing about the roll:
    /// <c>AddBcAppPath</c> funnels through the same invalidation, and (having early-returned
    /// on a path already in the list) it treats the re-registration as new, so it would clear
    /// the memo itself and the test would pass on a build where only the grow path clears —
    /// which is the half tests 1 and 2 already cover. Bundle 2 is registered afterwards, with
    /// a DIFFERENT PageType for the same id, so the rebuild is checked to answer with bundle
    /// 2's shape rather than bundle 1's.</para>
    ///
    /// <para>The no-roll arm is a control in both directions — it proves the memo really is a
    /// memo (so the roll arm is testing something), and the "synthesized" line asserted at the
    /// end proves stderr capture works here (so its <c>DoesNotContain</c> cannot pass by the
    /// capture silently returning nothing).</para>
    /// </summary>
    [Fact]
    public void ASynthesizedPageDocument_DoesNotSurviveTheBundleRollThatUnregistersItsApp()
    {
        var bundle1App = WriteApp(PagesJson((ReloadPageId, "DMMI Reload Page", "List")));
        RecordPatches.AddBcAppPath(bundle1App);

        var first = RecordPatches.TryBuildDependencyPageMetadata(ReloadPageId);
        Assert.NotNull(first);
        Assert.Equal("List", PageTypeOf(first!));

        var withoutRoll = CaptureStderr(() => RecordPatches.TryBuildDependencyPageMetadata(ReloadPageId));
        Assert.DoesNotContain($"synthesized Page {ReloadPageId}", withoutRoll);

        // The bundle roll.
        RecordPatches.ResetForReload();

        // Precondition, asserted rather than assumed: the roll really did unregister
        // bundle 1's .app. Without this the null below could mean the memo was cleared OR
        // that the page was never findable, and only the first is the claim.
        Assert.DoesNotContain(
            RecordPatches.RegisteredBcAppPathsForTests(),
            p => string.Equals(p, bundle1App, StringComparison.OrdinalIgnoreCase));
        Assert.False(RecordPatches.HasDependencyPageMetadata(ReloadPageId),
            "the live symbol walk must agree the page is gone, so null is the correct answer here");

        // ...so serving the memoized document would be serving one built from an .app the
        // process no longer has registered. Before the fix, that is exactly what happened.
        Assert.Null(RecordPatches.TryBuildDependencyPageMetadata(ReloadPageId));

        // And the roll did not leave a memoized NULL behind either: bundle 2 registers its
        // own .app declaring the same id with a different PageType, and the answer is
        // bundle 2's shape, freshly synthesized.
        RecordPatches.AddBcAppPath(WriteApp(PagesJson((ReloadPageId, "DMMI Reload Page", "Card"))));

        string? second = null;
        var afterRoll = CaptureStderr(() => second = RecordPatches.TryBuildDependencyPageMetadata(ReloadPageId));
        Assert.Contains($"synthesized Page {ReloadPageId}", afterRoll);
        Assert.NotNull(second);
        Assert.Equal("Card", PageTypeOf(second!));
        Assert.NotEqual(first, second);
    }

    /// <summary>The PageType stated by a synthesized PageDefinition document.</summary>
    private static string PageTypeOf(string pageDefinitionXml)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(pageDefinitionXml);
        var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        var properties = (System.Xml.XmlElement)doc.DocumentElement!.SelectSingleNode("m:Properties", ns)!;
        return properties.GetAttribute("PageType");
    }

    private static string CaptureStderr(Action action)
    {
        var saved = Console.Error;
        var buffer = new StringWriter();
        try
        {
            Console.SetError(buffer);
            action();
        }
        finally
        {
            Console.SetError(saved);
        }
        return buffer.ToString();
    }
}
