// SubscriberWitnessKindSeparationTests — the codeunit and page subscriber witnesses are stored
// APART, so what one kind witnessed can never answer a question about the other (#4267).
//
// WHY THIS IS ITS OWN TEST, AND WHY NO EXISTING ONE COVERS IT
//   #4267 widened RecordPatches' subscriber witness from one population to two, keyed on
//   (app path, type-name prefix). Every other test in this area drives ONE kind, or drives both
//   with EMPTY subscriber sets — and a merge of two cleared sets is still cleared, so the whole
//   suite stays green if the two keys collapse into one. Measured before this file existed:
//   changing PageTypePrefix from "Page" to "Codeunit" left DependencyPageMethodSubtreeTests,
//   DependencyPageMethodSubtreeRenderingParityTests, CodeunitMethodSubtreeDerivationTests and
//   CodeunitSubscriberWitnessMultiChunkTests at Failed: 0, Passed: 23.
//
//   So the separation was asserted in a PR body and tested by nothing. That is worse than an
//   untested property, because the next reader has a documented reason not to add the test.
//
// WHY IT IS NOT A THEORETICAL PROPERTY
//   WitnessAlObjectSubscribers scans ONE .app for both kinds in one pass, and AL page ids and
//   codeunit ids share no namespace — the same integer routinely names both, in Microsoft's own
//   apps. With a shared key, a codeunit N the scan SAW would mark page N as scanned even where
//   no Page<N> type exists in the assembly at all. Page N would then read as proven-complete,
//   emit a <Methods> subtree derived from a view nothing measured, and — because
//   MetadataObjectDiff pairs Methods positionally — mis-pair every element after the first
//   omission. That is the third state collapsing into the success state, which
//   guards-need-a-third-state.md forbids, reached through a merge rather than through a wrong
//   verdict.
//
// HOW IT DISCRIMINATES
//   One .app, two ids, each declaring BOTH a page and a codeunit — legal in AL, where object
//   kind and id together identify an object. The two witnesses are then registered with
//   DELIBERATELY OPPOSITE contents:
//
//     id  ...201   codeunit: SUBSCRIBER      page: scanned and clear
//     id  ...202   codeunit: scanned, clear  page: SUBSCRIBER
//
//   so each id has a subscriber in exactly one kind. Separated, page ...201 and codeunit ...202
//   emit; merged, the union marks both ids as subscriber-bearing in both kinds and both fall
//   silent. Both directions move, so a collapse cannot be half-caught.
//
//   The non-empty subscriber sets are the whole point: with the empty sets every other fixture
//   uses, a merge widens nothing and is invisible.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// The codeunit half reaches the RecordPatches parse statics, which are process-wide (#1696,
// #1712) — the collection CodeunitMethodSubtreeDerivationTests and
// DependencyPageMethodSubtreeRenderingParityTests both join for that reason. This file lives
// here rather than in DependencyPageMethodSubtreeTests (which is CacheRoots-serial and drives
// only the page emitter) because the claim is about both emitters at once.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class SubscriberWitnessKindSeparationTests : IDisposable
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    /// <summary>Declared as BOTH a page and a codeunit. The CODEUNIT carries the subscriber; the
    /// page is scanned and clear, so the page's subtree must be emitted.</summary>
    private const int CodeunitIsSubscriberId = 88267201;

    /// <summary>The mirror: the PAGE carries the subscriber and the codeunit is clear, so the
    /// codeunit's subtree must be emitted.</summary>
    private const int PageIsSubscriberId = 88267202;

    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Codeunits": [
            {
              "Id": 88267201,
              "Name": "P4267 Cross Kind Codeunit A",
              "Properties": [],
              "Methods": [
                { "Id": 3001, "Name": "OnCodeunitAEvent",
                  "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "False" } ] } ] }
              ]
            },
            {
              "Id": 88267202,
              "Name": "P4267 Cross Kind Codeunit B",
              "Properties": [],
              "Methods": [
                { "Id": 3002, "Name": "OnCodeunitBEvent",
                  "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "False" } ] } ] }
              ]
            }
          ],
          "Pages": [
            {
              "Id": 88267201,
              "Name": "P4267 Cross Kind Page A",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Methods": [
                { "Id": 3101, "Name": "OnPageAEvent",
                  "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "False" } ] } ] }
              ]
            },
            {
              "Id": 88267202,
              "Name": "P4267 Cross Kind Page B",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Methods": [
                { "Id": 3102, "Name": "OnPageBEvent",
                  "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "False" } ] } ] }
              ]
            }
          ]
        }
        """;

    private readonly string _dir;

    public SubscriberWitnessKindSeparationTests()
    {
        _dir = TestScratch.Dir("al-runner-witness-kind-separation-4267");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// ONE app path, both witnesses, opposite subscriber sets. Both ids are in both SCANNED sets,
    /// so "unknown" is never the reason a subtree is absent here — only a subscriber is, which is
    /// what keeps this test about the key rather than about the third state.
    /// </summary>
    private void Register()
    {
        var appPath = Path.Combine(_dir, "cross-kind.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }

        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        RecordPatches.AddBcAppPath(appPath);

        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath,
            subscriberCodeunitIds: new[] { CodeunitIsSubscriberId },
            scannedCodeunitIds: new[] { CodeunitIsSubscriberId, PageIsSubscriberId });

        RecordPatches.RegisterPageSubscriberWitness(
            appPath,
            subscriberPageIds: new[] { PageIsSubscriberId },
            scannedPageIds: new[] { CodeunitIsSubscriberId, PageIsSubscriberId });
    }

    private static bool PageHasMethods(int pageId)
    {
        var xml = RecordPatches.TryBuildDependencyPageMetadata(pageId);
        Assert.True(xml is not null, $"the runner derived no metadata for page {pageId}");
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!.GetElementsByTagName("Methods", MetaNs).Count > 0;
    }

    private static bool CodeunitHasMethods(int codeunitId)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(codeunitId);
        Assert.True(xml is not null, $"the runner derived no metadata for codeunit {codeunitId}");
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!.GetElementsByTagName("Methods", MetaNs).Count > 0;
    }

    /// <summary>
    /// A CODEUNIT witnessed as carrying a subscriber must not silence the PAGE with the same id.
    /// Both facts are asserted together: the codeunit correctly abstains, and the page correctly
    /// emits. Asserting only the page would pass against a witness that lost the codeunit's
    /// subscriber entirely, which is a different defect with the same symptom here.
    /// </summary>
    [Fact]
    public void ACodeunitsSubscriber_DoesNotSilenceThePageWithTheSameId()
    {
        Register();

        Assert.False(CodeunitHasMethods(CodeunitIsSubscriberId),
            "the codeunit carries a subscriber, so its method table must stay absent");
        Assert.True(PageHasMethods(CodeunitIsSubscriberId),
            "the PAGE with that id is scanned and clear in its own witness, so its method table "
            + "must be emitted — a codeunit's subscriber is not a fact about a page");
    }

    /// <summary>
    /// The mirror, and it is not redundant: the two witnesses are written by two entry points and
    /// read by two, so a leak can exist in one direction only. Under a shared key both arms fail;
    /// under a one-way leak exactly one does, which is the diagnosis the pair buys.
    /// </summary>
    [Fact]
    public void APagesSubscriber_DoesNotSilenceTheCodeunitWithTheSameId()
    {
        Register();

        Assert.False(PageHasMethods(PageIsSubscriberId),
            "the page carries a subscriber, so its method table must stay absent");
        Assert.True(CodeunitHasMethods(PageIsSubscriberId),
            "the CODEUNIT with that id is scanned and clear in its own witness, so its method "
            + "table must be emitted — a page's subscriber is not a fact about a codeunit");
    }
}
