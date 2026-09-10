// DependencyPagePropertiesFromSymbolTests — issue #3784: the page properties
// SymbolReference.json states and RecordPatches.EmitPageXml did not read.
//
// WHY A RUNNER-SIDE MECHANISM TEST AND NOT A CORPUS TEST
//   The defect is reachable only through a PRECOMPILED dependency: EmitPageXml runs for a page
//   the runner never source-compiles, and a corpus test compiles its pages from source, which
//   takes the real compiler's own metadata path and never reaches this synthesizer at all
//   (bc-behavior-tests-go-upstream.md's structural case). So the proving test pins the runner's
//   own reader against a fixture symbol file, and the VALUES it asserts are the ones BC's own
//   emitter writes for the same input — measured, not chosen.
//
// WHAT THE EXPECTED VALUES REST ON
//   BC 28.4.53241.54407, Business Foundation (11 pages) + System Application (225 pages), by
//   parsing each app's shipped SymbolReference.json and the PageDefinition documents
//   tools/gen-metadata-ground-truth.sh produces from BC's own emitter, and cross-tabulating
//   every (symbol value -> emitted attribute) pair over all 236 pages:
//
//     Extensible          sym absent -> BC "1" (110 pages); sym "0" -> "0" (105); "1" -> "1" (21)
//     RefreshOnActivate   sym absent -> BC "0" (207);       sym "1" -> "1" (29)
//     UsageCategory       written iff stated, verbatim (68 pages, 6 distinct enum names)
//     HelpLink            written iff stated, verbatim (6 pages state it)
//     InsertAllowed       sym "0" -> "0" (121), "1" -> "1" (1), absent -> absent (114)
//     ModifyAllowed       sym "0" -> "0" (80),  "1" -> "1" (11), absent -> absent (145)
//     DeleteAllowed       sym "0" -> "0" (101), "1" -> "1" (6),  absent -> absent (129)
//     DelayedInsert       sym "1" -> "1" (14),  "0" -> "0" (1),  absent -> absent (221)
//     MultipleNewLines    sym "0" -> "0" (3),   "1" -> "1" (1),  absent -> absent (232)
//     AboutTitle/AboutText/AdditionalSearchTerms/InstructionalText -> "ENU=" + the value
//     InherentEntitlements / InherentPermissions   sym "X" -> BC "16"
//
//   PermissionMask.Execute is 16 in Microsoft.Dynamics.Nav.Types, which is what makes the last
//   row a DECODE of the AL permission letters rather than a guess — see EmitInherentMask.
//
// WHAT IS DELIBERATELY NOT HERE
//   AnalysisModeEnabled and OnAfterGetCurrentRecordEnabled are NOT symbol reads and are left
//   alone: cross-tabulated over the same 236 pages, AnalysisModeEnabled tracks PageType
//   (List/Worksheet -> "1", every other type -> absent) on 94 pages whose symbol file states
//   nothing at all, and OnAfterGetCurrentRecordEnabled tracks trigger presence. Deriving either
//   is a different change from reading one. IndirectPermissions, CardFormID and DataCaptionExpr
//   are likewise excluded: they need table/page NAME resolution or BC's own
//   "DataCaptionExprCode" marker, none of which the symbol file states.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as DependencyPageMetadataXmlTests: BcAppSymbolCache.Get() resolves through the
// process-global CacheRoots override, and the per-id metadata-xml memo is process-global too.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPagePropertiesFromSymbolTests
{
    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    // Distinctive ids in their own block, for the reason DependencyPageMetadataXmlTests states:
    // the dependency-page state is process-global, so an id another test also declares would
    // read back that test's cached document instead of this one's.
    private const int NotExtensiblePageId = 88784001;
    private const int ExtensiblePageId = 88784002;
    private const int SilentPageId = 88784003;
    private const int RichPageId = 88784004;
    private const int NoSourceTableFlagsPageId = 88784005;
    private const int UnreadableMaskPageId = 88784006;

    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 88784001,
              "Name": "P3784 Not Extensible",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "Extensible", "Value": "0" },
                { "Name": "InsertAllowed", "Value": "0" },
                { "Name": "ModifyAllowed", "Value": "0" },
                { "Name": "DeleteAllowed", "Value": "0" }
              ]
            },
            {
              "Id": 88784002,
              "Name": "P3784 Extensible",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "Extensible", "Value": "1" },
                { "Name": "InsertAllowed", "Value": "1" },
                { "Name": "ModifyAllowed", "Value": "1" },
                { "Name": "DeleteAllowed", "Value": "1" }
              ]
            },
            {
              "Id": 88784003,
              "Name": "P3784 Silent",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "700" }
              ]
            },
            {
              "Id": 88784004,
              "Name": "P3784 Rich",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "UsageCategory", "Value": "Administration" },
                { "Name": "RefreshOnActivate", "Value": "1" },
                { "Name": "HelpLink", "Value": "https://go.microsoft.com/fwlink/?linkid=2149387" },
                { "Name": "AboutTitle", "Value": "About performance profiling" },
                { "Name": "AboutText", "Value": "Record a slow scenario." },
                { "Name": "AdditionalSearchTerms", "Value": "Database,Size,Storage" },
                { "Name": "InstructionalText", "Value": "Assign email scenarios" },
                { "Name": "IsPreview", "Value": "1" },
                { "Name": "InherentEntitlements", "Value": "X" },
                { "Name": "InherentPermissions", "Value": "X" },
                { "Name": "DelayedInsert", "Value": "1" },
                { "Name": "MultipleNewLines", "Value": "0" }
              ]
            },
            {
              "Id": 88784005,
              "Name": "P3784 No Source Table Flags",
              "Properties": [
                { "Name": "PageType", "Value": "NavigatePage" },
                { "Name": "InsertAllowed", "Value": "0" },
                { "Name": "ModifyAllowed", "Value": "0" },
                { "Name": "DeleteAllowed", "Value": "0" },
                { "Name": "DelayedInsert", "Value": "1" }
              ]
            },
            {
              "Id": 88784006,
              "Name": "P3784 Unreadable Mask",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "InherentEntitlements", "Value": "Q" },
                { "Name": "InherentPermissions", "Value": "X" }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// THE HEADLINE. <c>Extensible</c> was written as a literal <c>"1"</c> for every page, so it
    /// was a WRONG answer rather than a missing one on the 105 of 236 System Application +
    /// Business Foundation pages BC's own emitter writes <c>"0"</c> for — all 105 of which state
    /// <c>Extensible = "0"</c> in the symbol file, with zero omissions.
    ///
    /// <para>Asserted in BOTH directions on purpose: a hardcode in either direction fails one of
    /// the two, so this cannot be satisfied by swapping one constant for another.</para>
    /// </summary>
    [Fact]
    public void Extensible_IsReadFromTheSymbolFile_NotHardcoded()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-props-3784");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            Assert.Equal("0", ReadProperties(NotExtensiblePageId).GetAttribute("Extensible"));
            Assert.Equal("1", ReadProperties(ExtensiblePageId).GetAttribute("Extensible"));
            // The AL default, which BC's emitter writes for the 110 pages stating nothing.
            Assert.Equal("1", ReadProperties(SilentPageId).GetAttribute("Extensible"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The Insert/Modify/Delete trio and the two line flags, on a page that HAS a source table.
    /// Both values of each, because the runner's previous rule ("write only a false one") could
    /// not express an explicitly-stated <c>true</c>, which BC's emitter writes on 18 of the 236
    /// pages measured.
    /// </summary>
    [Fact]
    public void SourceObjectFlags_CarryTheStatedValue_InBothDirections()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-props-3784");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            var off = ReadSourceObject(NotExtensiblePageId);
            Assert.Equal("0", off.GetAttribute("InsertAllowed"));
            Assert.Equal("0", off.GetAttribute("ModifyAllowed"));
            Assert.Equal("0", off.GetAttribute("DeleteAllowed"));

            var on = ReadSourceObject(ExtensiblePageId);
            Assert.Equal("1", on.GetAttribute("InsertAllowed"));
            Assert.Equal("1", on.GetAttribute("ModifyAllowed"));
            Assert.Equal("1", on.GetAttribute("DeleteAllowed"));

            // A page stating none of them gets none of the attributes — BC's reader defaults
            // all three to true, so writing "1" here would be indistinguishable from the AL
            // stating it, which BC's Equals()/Freeze() can tell apart.
            var silent = ReadSourceObject(SilentPageId);
            Assert.False(silent.HasAttribute("InsertAllowed"));
            Assert.False(silent.HasAttribute("ModifyAllowed"));
            Assert.False(silent.HasAttribute("DeleteAllowed"));
            Assert.False(silent.HasAttribute("DelayedInsert"));
            Assert.False(silent.HasAttribute("MultipleNewLines"));

            // DelayedInsert/MultipleNewLines likewise carry a stated "0", which the old
            // write-only-a-true rule dropped. BC writes DelayedInsert="0" on 1 page and
            // MultipleNewLines="0" on 3 of the 236.
            var rich = ReadSourceObject(RichPageId);
            Assert.Equal("1", rich.GetAttribute("DelayedInsert"));
            Assert.Equal("0", rich.GetAttribute("MultipleNewLines"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The same five flags on a page with NO source table, which the old code could not write at
    /// all because they sat inside its <c>SourceTable &gt; 0</c> branch. Five System Application
    /// pages are in exactly this shape — 502 OAuth2ControlAddIn, 2718 Page Summary Settings,
    /// 4326 Agent Creation Control, 7775 Copilot AI Capabilities, 9260 Customer Experience
    /// Survey — and BC's emitter writes the attributes for all five.
    ///
    /// <para>The empty-<c>SourceObject</c> rule from #2451 is unaffected and re-asserted here:
    /// no <c>SourceTable</c> attribute is invented alongside them.</para>
    /// </summary>
    [Fact]
    public void SourceObjectFlags_AreWrittenForAPageWithNoSourceTable()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-props-3784");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            var so = ReadSourceObject(NoSourceTableFlagsPageId);
            Assert.Equal("0", so.GetAttribute("InsertAllowed"));
            Assert.Equal("0", so.GetAttribute("ModifyAllowed"));
            Assert.Equal("0", so.GetAttribute("DeleteAllowed"));
            Assert.Equal("1", so.GetAttribute("DelayedInsert"));

            // #2451 stands: no SourceTable is invented for a page that declares none.
            Assert.False(so.HasAttribute("SourceTable"));
            Assert.False(so.HasAttribute("SourceTableTemporary"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The <c>PageProperties</c> scalars: written if and only if the symbol file states one,
    /// carrying the value it states. <c>UsageCategory</c> and <c>HelpLink</c> go through
    /// verbatim; the four ML strings take BC's own <c>ENU=</c> prefix, which is the form its
    /// emitter writes on all 21/21/28/6 pages stating them.
    /// </summary>
    [Fact]
    public void PageProperties_ScalarsAreWrittenOnlyWhenStated()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-props-3784");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            var rich = ReadProperties(RichPageId);
            Assert.Equal("Administration", rich.GetAttribute("UsageCategory"));
            Assert.Equal("1", rich.GetAttribute("RefreshOnActivate"));
            Assert.Equal("https://go.microsoft.com/fwlink/?linkid=2149387", rich.GetAttribute("HelpLink"));
            Assert.Equal("ENU=About performance profiling", rich.GetAttribute("AboutTitleML"));
            Assert.Equal("ENU=Record a slow scenario.", rich.GetAttribute("AboutTextML"));
            Assert.Equal("ENU=Database,Size,Storage", rich.GetAttribute("AdditionalSearchTermsML"));
            Assert.Equal("ENU=Assign email scenarios", rich.GetAttribute("InstructionalTextML"));
            Assert.Equal("1", rich.GetAttribute("IsPreview"));

            // The negative half: a page stating none of them gets none of the attributes. BC's
            // UsageCategorySpecified bit is raised by the SETTER, so writing UsageCategory="None"
            // for a silent page would be a different document from the one BC emits.
            var silent = ReadProperties(SilentPageId);
            Assert.False(silent.HasAttribute("UsageCategory"));
            Assert.False(silent.HasAttribute("HelpLink"));
            Assert.False(silent.HasAttribute("AboutTitleML"));
            Assert.False(silent.HasAttribute("AboutTextML"));
            Assert.False(silent.HasAttribute("AdditionalSearchTermsML"));
            Assert.False(silent.HasAttribute("InstructionalTextML"));
            Assert.False(silent.HasAttribute("IsPreview"));
            // RefreshOnActivate's AL default is false and BC's emitter writes "0" for a page
            // stating nothing, so this one IS written unconditionally — the same shape as
            // Extensible, and the opposite of the seven above.
            Assert.Equal("0", silent.GetAttribute("RefreshOnActivate"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>InherentEntitlements</c> / <c>InherentPermissions</c> are <c>Int32</c> permission masks
    /// on BC's <c>PageProperties</c>, and the symbol file states them as AL permission LETTERS.
    /// Across Base Application + System Application + Business Foundation (2,852 pages) the only
    /// value either property ever takes is <c>"X"</c>, which BC's emitter writes as <c>16</c> —
    /// <c>PermissionMask.Execute</c> in Microsoft.Dynamics.Nav.Types.
    /// </summary>
    [Fact]
    public void InherentMasks_AreDecodedFromThePermissionLetters()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-props-3784");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            var rich = ReadProperties(RichPageId);
            Assert.Equal("16", rich.GetAttribute("InherentEntitlements"));
            Assert.Equal("16", rich.GetAttribute("InherentPermissions"));

            // Absent when unstated, so BC's InherentEntitlementsSpecified bit stays down.
            var silent = ReadProperties(SilentPageId);
            Assert.False(silent.HasAttribute("InherentEntitlements"));
            Assert.False(silent.HasAttribute("InherentPermissions"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A permission letter the decoder does not know is REFUSED and SAID, never folded into 0 —
    /// the same rule <c>EmitSourceObjectPropertiesXml</c> already applies to an unreadable
    /// boolean and to a <c>DataCaptionFields</c> value of the wrong shape. A mask of 0 and an
    /// absent attribute are different documents to BC (the <c>Specified</c> bit), and inventing
    /// either from a value nobody could read is the defect shape this whole change fixes.
    ///
    /// <para>The page states a second, READABLE mask, so this cannot pass by the synthesizer
    /// giving up on the page.</para>
    /// </summary>
    [Fact]
    public void InherentMask_StatedInAFormItCannotRead_IsRefusedLoudly()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-props-3784");
        Directory.CreateDirectory(dir);
        var previousError = Console.Error;
        var captured = new StringWriter();
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            // The document is memoized per id and only this test asks for this page, so the one
            // and only build happens inside the capture.
            Console.SetError(captured);
            var props = ReadProperties(UnreadableMaskPageId);
            Console.SetError(previousError);

            Assert.False(props.HasAttribute("InherentEntitlements"),
                "an unreadable permission letter must not be invented into a mask");
            // …and the readable sibling on the same page is unaffected.
            Assert.Equal("16", props.GetAttribute("InherentPermissions"));

            var diagnostic = captured.ToString();
            Assert.Contains("InherentEntitlements", diagnostic);
            Assert.Contains(UnreadableMaskPageId.ToString(), diagnostic);
            // The value itself, so a reader can see WHAT the file said.
            Assert.Contains("Q", diagnostic);
            // The readable one must not be reported as a problem.
            Assert.DoesNotContain("InherentPermissions", diagnostic);
        }
        finally
        {
            Console.SetError(previousError);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static XmlElement ReadProperties(int pageId)
    {
        var xml = RecordPatches.TryBuildDependencyPageMetadata(pageId);
        Assert.NotNull(xml);

        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        return (XmlElement)doc.DocumentElement!.SelectSingleNode("m:Properties", ns)!;
    }

    private static XmlElement ReadSourceObject(int pageId)
    {
        var props = ReadProperties(pageId);
        var ns = new XmlNamespaceManager(props.OwnerDocument.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        return (XmlElement)props.SelectSingleNode("m:SourceObject", ns)!;
    }
}
