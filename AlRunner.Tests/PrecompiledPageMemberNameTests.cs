// PrecompiledPageMemberNameTests — pins the runner's own C# mechanism for issues #2723 and
// #2517: TestPage trigger dispatch on a page that ships PRECOMPILED in a dependency .app
// could not reach any member whose AL name needed mangling to become a C# identifier.
//
// What this proves, and what it deliberately does NOT
// -----------------------------------------------------
// The BC-observable claim — invoking "Assign Serial No." on Base Application page 6510 runs
// its OnAction; setting "Company Name" on page 9875 runs its page OnValidate; action New on
// page 790 inserts a row — is proven end-to-end against real precompiled Base Application
// pages in tests/runner-extras/testpage-precompiled-member-names (seven arms, including the
// negative "RunObject-only action must STILL refuse loudly"). This file pins the three
// runner-only layers underneath it, each in isolation, so a regression names its layer:
//
//   1. RunnerPageInstance.EmittedIdentifier — the FORWARD mangle, now a port of BC's own
//      emitter (Microsoft.Dynamics.Nav.CodeAnalysis.Utilities.StringExtensions.
//      MangleIdentifierName + GetSafeCSharpIdentifierName, decompiled from 28.1). The
//      keyword arm is the one that was missing: "New" emits as _New_a45_OnAction because
//      NEW is a C# reserved keyword, and the pre-fix mangle produced New, so even a
//      SOURCE-parsed page's action New never matched. The negative rows (Delete, Record,
//      Setup) are what rules out "any keyword-looking word": Delete is no C# keyword,
//      record is only a CONTEXTUAL keyword and not in SyntaxFacts.GetReservedKeywordKinds.
//   2. BcAppSymbolCache.TryParsePageSymbol / TryParsePageExtensionSymbol — the declared
//      names keyed by BC's own member id, read off SymbolReference.json's Actions /
//      Controls trees (page) and ActionChanges[].Actions / ControlChanges[].Controls
//      (pageextension), including the #<appid># module-qualifier strip on TargetObject.
//   3. RecordPatches.TryGetPageMemberName / TryGetActionRefTarget /
//      GetPageExtensionIdsForPage — the dependency fallback that hands (2) to the forward
//      arm of (1) when _parsedPages / _parsedPageExtensions do not know the object.
//
// Negative direction at every layer: a member id the symbol file does not declare answers
// null (never a fabricated name), a modify(...) change with no added members contributes
// nothing, and a name that needs no mangling passes through EmittedIdentifier unchanged.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class EmittedIdentifierTests
{
    [Theory]
    // Space -> '_' (each one separately), other non-identifier characters -> 'a' + code point.
    [InlineData("Assign Serial No.", "Assign_Serial_Noa46")]
    [InlineData("Assign &Serial No.", "Assign_a38Serial_Noa46")]
    [InlineData("A&ttendee Scheduling", "Aa38ttendee_Scheduling")]
    [InlineData("Spaced Stamp", "Spaced_Stamp")]
    [InlineData("A  C", "A__C")]
    [InlineData("Ærø Løb", "Ærø_Løb")]
    [InlineData("Mode-X", "Modea45X")]
    [InlineData("Set Applies-to ID", "Set_Appliesa45to_ID")]
    // A first character that cannot START an identifier gets a '_' inserted before it.
    [InlineData("2Start", "_2Start")]
    // C# RESERVED keywords, matched upper-invariant, get the '_' prefix — the seven names
    // measured over the whole Base Application 28.1 DLL, plus one more keyword for good
    // measure (there is no Base App member called Class, so it is the rule, not the list).
    [InlineData("New", "_New")]
    [InlineData("Delegate", "_Delegate")]
    [InlineData("Default", "_Default")]
    [InlineData("Event", "_Event")]
    [InlineData("Internal", "_Internal")]
    [InlineData("Override", "_Override")]
    [InlineData("Finalize", "_Finalize")]   // BC's one non-keyword extra (OtherReservedWords)
    [InlineData("Class", "_Class")]
    // NOT prefixed: not a C# keyword at all, or only a contextual one — measured on the same
    // DLL as Delete_a45_OnAction / Record… / Setup_a45_OnAction with no leading underscore.
    [InlineData("Delete", "Delete")]
    [InlineData("Record", "Record")]
    [InlineData("Setup", "Setup")]
    [InlineData("Var", "Var")]
    [InlineData("Async", "Async")]
    [InlineData("ApplicationID", "ApplicationID")]
    // The keyword check runs on the MANGLED result — "New Item" mangles to New_Item, which is
    // no keyword, so no prefix.
    [InlineData("New Item", "New_Item")]
    public void EmittedIdentifier_MatchesBcsOwnEmitter(string declaredName, string expected)
        => Assert.Equal(expected, RunnerPageInstance.EmittedIdentifier(declaredName));
}

// BcAppSymbolCache.Get() resolves its on-disk cache path through the process-global
// CacheRoots override, and RecordPatches' dependency state (_bcAppPaths) is process-global
// too — same reason RecordPatchesGetPageControlFieldMapDependencyTests joins this collection.
[Collection(CacheRootsSerialCollection.Name)]
public class PrecompiledPageMemberNameTests
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

    // Distinctive ids: RecordPatches' dependency-page state is process-global, so reusing an
    // id another test declares would risk reading back that test's cached answer.
    private const int PageId = 88230401;
    private const int ExtensionId = 88230402;
    private const int SpacedControlId = 640645001;   // "Authentication Email"
    private const int GroupId = 640645002;           // FunctionsSupply (Kind 1)
    private const int SpacedActionId = 640645003;    // "Assign Serial No." (Kind 2)
    private const int KeywordActionId = 640645004;   // New (Kind 2)
    private const int PromotedRefId = 640645005;     // "Assign Serial No._Promoted" (Kind 4)
    private const int ExtActionId = 640645006;       // "Open Related" added by the pageextension
    private const int ExtControlId = 640645007;      // "Extra Field" added by the pageextension
    private const int UndeclaredId = 640645999;

    // Shapes copied from Base Application 28.1's own SymbolReference.json: a page's Actions
    // tree nests area -> group -> action, a Promoted area holds Kind-4 actionrefs carrying
    // TargetId/TargetName, and a pageextension carries ActionChanges/ControlChanges whose
    // added members sit under Actions/Controls — a modify(...) change has Properties only.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88230401,
              "Name": "PMN Dep Page",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Controls": [
                {
                  "Kind": 1, "Id": 1, "Name": "content",
                  "Controls": [
                    { "Kind": 8, "Id": 640645001, "Name": "Authentication Email",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Message" } ] }
                  ]
                }
              ],
              "Actions": [
                {
                  "Id": 2, "Name": "processing",
                  "Actions": [
                    {
                      "Kind": 1, "Id": 640645002, "Name": "FunctionsSupply",
                      "Actions": [
                        { "Kind": 2, "Id": 640645003, "Name": "Assign Serial No." },
                        { "Kind": 2, "Id": 640645004, "Name": "New" }
                      ]
                    }
                  ]
                },
                {
                  "Id": 3, "Name": "Promoted",
                  "Actions": [
                    { "Kind": 4, "Id": 640645005, "Name": "Assign Serial No._Promoted",
                      "TargetId": 640645003, "TargetName": "Assign Serial No." }
                  ]
                }
              ]
            }
          ],
          "PageExtensions": [
            {
              "Id": 88230402,
              "Name": "PMN Dep Ext",
              "TargetObject": "#63ca2fa44f034f2ba480172fef340d3f#PMN Dep Page",
              "ActionChanges": [
                { "Anchor": "processing", "ChangeKind": 1,
                  "Actions": [ { "Kind": 2, "Id": 640645006, "Name": "Open Related" } ] },
                { "Anchor": "New", "ChangeKind": 9,
                  "Properties": [ { "Name": "Visible", "Value": "false" } ] }
              ],
              "ControlChanges": [
                { "Anchor": "content", "ChangeKind": 1,
                  "Controls": [ { "Kind": 8, "Id": 640645007, "Name": "Extra Field",
                                  "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Message" } ] } ] }
              ]
            }
          ]
        }
        """;

    private static void WithApp(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(WriteApp(dir, SymbolReference)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PageSymbol_CarriesEveryMembersDeclaredNameKeyedByItsOwnId()
        => WithApp(appPath =>
        {
            var page = Assert.Single(BcAppSymbolCache.Get(appPath).Pages, p => p.Id == PageId);
            var names = page.MemberIdToName!;

            // Actions at every depth and of every Kind, and controls, by the file's own Id.
            Assert.Equal("Assign Serial No.", names[SpacedActionId]);
            Assert.Equal("New", names[KeywordActionId]);
            Assert.Equal("FunctionsSupply", names[GroupId]);
            Assert.Equal("Assign Serial No._Promoted", names[PromotedRefId]);
            Assert.Equal("Authentication Email", names[SpacedControlId]);

            // The actionref's target by NAME, and only for the actionref.
            Assert.Equal("Assign Serial No.", page.MemberIdToActionRefTarget![PromotedRefId]);
            Assert.False(page.MemberIdToActionRefTarget.ContainsKey(SpacedActionId),
                "a plain action is not an actionref and must not carry a target");

            // Negative: nothing is fabricated for an id the file does not declare.
            Assert.False(names.ContainsKey(UndeclaredId));
        });

    [Fact]
    public void PageExtensionSymbol_CarriesAddedMembers_AndStripsTheModuleQualifier()
        => WithApp(appPath =>
        {
            var ext = Assert.Single(BcAppSymbolCache.Get(appPath).PageExtensions!, e => e.Id == ExtensionId);

            Assert.Equal("PMN Dep Page", ext.TargetObjectName);
            Assert.Equal("Open Related", ext.MemberIdToName[ExtActionId]);
            Assert.Equal("Extra Field", ext.MemberIdToName[ExtControlId]);
            // The modify(...) change (ChangeKind 9, Properties only) adds no member: exactly
            // the two added members, nothing more.
            Assert.Equal(2, ext.MemberIdToName.Count);
            Assert.Empty(ext.MemberIdToActionRefTarget);
        });

    [Fact]
    public void RecordPatches_FallsBackToTheDependencySymbols_ForAnObjectItNeverParsed()
        => WithApp(appPath =>
        {
            RecordPatches.AddBcAppPath(appPath);

            // Page: the declared name FindTriggerOnTarget's forward arm needs.
            Assert.Equal("Assign Serial No.", RecordPatches.TryGetPageMemberName(PageId, SpacedActionId, isExtension: false));
            Assert.Equal("Authentication Email", RecordPatches.TryGetPageMemberName(PageId, SpacedControlId, isExtension: false));
            Assert.Equal("New", RecordPatches.TryGetPageMemberName(PageId, KeywordActionId, isExtension: false));
            // Actionref target, followed by name (#2113's resolution, now for a precompiled page).
            Assert.Equal("Assign Serial No.", RecordPatches.TryGetActionRefTarget(PageId, PromotedRefId, isExtension: false));
            Assert.Null(RecordPatches.TryGetActionRefTarget(PageId, SpacedActionId, isExtension: false));

            // Pageextension: its own id space, and discovered for its base page by name.
            Assert.Equal("Open Related", RecordPatches.TryGetPageMemberName(ExtensionId, ExtActionId, isExtension: true));
            Assert.Contains(ExtensionId, RecordPatches.GetPageExtensionIdsForPage(PageId));

            // Negative: an undeclared member, and the wrong id namespace, both answer null —
            // a page and a pageextension may share an object number (#1710), so the page's
            // members must not be visible through the extension's namespace or vice versa.
            Assert.Null(RecordPatches.TryGetPageMemberName(PageId, UndeclaredId, isExtension: false));
            Assert.Null(RecordPatches.TryGetPageMemberName(PageId, SpacedActionId, isExtension: true));
            Assert.Null(RecordPatches.TryGetPageMemberName(ExtensionId, ExtActionId, isExtension: false));
        });
}
