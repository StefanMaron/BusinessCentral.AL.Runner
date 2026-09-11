// ExtensionRuntimeDeltasTests — pins that the runner produces a MetadataRuntimeDeltas side for
// an extension object, addressed by the EXTENSION'S OWN (object type, id).
//
// Issue #3809. The issue asked for "the runner tracking an extension's contribution addressably
// by the extension's own id". Measuring it first found the addressability was already there —
// BcAppSymbolCache.PageExtensionSymbol.Id and TableExtensionSymbol.ExtensionId ARE the
// extension's own ids, read straight off SymbolReference.json — and that two other things were
// not:
//
//   1. The runner had no route from that id to anything shaped like a deltas document, so the
//      equivalence harness had nothing to compare against BC's 11.
//   2. The one member typed to answer, RunnerXmlMetadataLoader.GetExtensionDeltasForAppObject,
//      was reached through a call site that hardcoded an ObjectType of Page — so a
//      tableextension and a pageextension sharing an id were ONE question, not two. BC's own
//      NCLObjectXmlMetadataLoader.GetExtensionDeltasForAppObject matches on
//      `s.ObjectType == objectId.ObjectType && s.ObjectId == objectId.ObjectNumber`, so the
//      PAIR is the key. Business Foundation + System Application 28.1 carry exactly that
//      collision: tableextension 774 and pageextension 774, both named "Plan User Details".
//
// WHY A RENDER AND NOT AN OBJECT. NavAppObjectMetadataRuntimeDeltas exposes AllDeltas get-only
// over a private List<Delta> on NavAppObjectMetadataDeltaCreator<Delta>, and its only public
// constructor is parameterless — there is no route that sets deltas on an instance. So the one
// way to produce a populated one is to render the document BC's own FromXml reads, the same
// shape and for the same reason as TryBuildEnumMetadataEquivalenceXml (#3807).
//
// A VALUE THE RUNNER DOES NOT DERIVE IS LEFT OFF, NEVER DEFAULTED. BC's emitted deltas carry
// ControlGUID, SourceExtensionType and several *TranslationKey hashes that SymbolReference.json
// does not state. Writing any value for those would manufacture agreement, which is the failure
// the equivalence harness exists to catch.
//
// The fixture is a SYNTHETIC .app rather than a real artifact so the claims are hermetic and the
// collision is reproduced deliberately; the real-population figures that shaped it are in
// docs/metadata-equivalence.md.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get() resolves its on-disk cache path through the process-global CacheRoots
// override, and RecordPatches' dependency state (_bcAppPaths) is process-global too — the same
// reason PrecompiledPageMemberNameTests joins this collection.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class ExtensionRuntimeDeltasTests
{
    private const string Ns = "{urn:schemas-microsoft-com:dynamics:NAV:MetaObjects}";

    // One id carried by BOTH a tableextension and a pageextension, which is the shape BC's own
    // bundles have on 774. Distinctive, because the dependency state is process-global.
    internal const int SharedExtensionId = 88380901;
    private const int SharedExtId = SharedExtensionId;
    private const int TableOnlyExtId = 88380902;
    private const int AddedActionId = 640938001;
    private const int AddedControlId = 640938002;
    private const int UndeclaredId = 88380999;

    // Shapes copied from Business Foundation / System Application 28.1's own
    // SymbolReference.json: a pageextension's added members sit under ActionChanges[].Actions
    // and ControlChanges[].Controls, and every one of the six real tableextensions declares
    // only Obsolete=Moved/Removed fields — which is exactly why BC emits an EMPTY
    // <MetadataRuntimeDeltas/> for each of them.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "TableExtensions": [
            {
              "Id": 88380901,
              "Name": "Shared Name Ext",
              "TargetObject": "ERD Target Table",
              "Fields": [
                { "Id": 50001, "Name": "Moved Away",
                  "Properties": [ { "Name": "ObsoleteState", "Value": "Moved" } ] }
              ]
            },
            {
              "Id": 88380902,
              "Name": "ObsoleteOnlyExt",
              "TargetObject": "ERD Other Table",
              "Fields": [
                { "Id": 50002, "Name": "Gone",
                  "Properties": [ { "Name": "ObsoleteState", "Value": "Removed" } ] }
              ]
            }
          ],
          "PageExtensions": [
            {
              "Id": 88380901,
              "Name": "Shared Name Ext",
              "TargetObject": "ERD Target Page",
              "ActionChanges": [
                { "Anchor": "Processing", "ChangeKind": 2,
                  "Actions": [ { "Kind": 2, "Id": 640938001, "Name": "Permissions",
                                 "Properties": [ { "Name": "Caption", "Value": "Permissions" },
                                                 { "Name": "Image", "Value": "Permission" } ] } ] }
              ],
              "ControlChanges": [
                { "Anchor": "Has SUPER permission set", "ChangeKind": 3,
                  "Controls": [ { "Kind": 8, "Id": 640938002, "Name": "User Plans",
                                  "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Plans" } ] } ] }
              ]
            }
          ]
        }
        """;

    /// <summary>The fixture package, exposed so the BC-reader arm can write the same one.</summary>
    internal static string WriteFixtureApp(string dir) => WriteApp(dir);

    private static string WriteApp(string dir) => WriteAppWith(dir, SymbolReference);

    private static string WriteAppWith(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    private static void WithApp(Action<string> body)
    {
        var dir = TestScratch.Dir("al-runner-extension-runtime-deltas-tests");
        Directory.CreateDirectory(dir);
        try { body(WriteApp(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static XDocument? Render(string appPath, string objectType, int extensionId)
    {
        var xml = RecordPatches.TryBuildExtensionRuntimeDeltasXmlForApp(appPath, objectType, extensionId);
        return xml is null ? null : XDocument.Parse(xml);
    }

    [Fact]
    public void A_pageextension_renders_a_deltas_root_carrying_its_OWN_id_and_name()
        => WithApp(appPath =>
        {
            var doc = Render(appPath, "Page", SharedExtId);
            Assert.NotNull(doc);

            var root = doc!.Root!;
            Assert.Equal($"{Ns}MetadataRuntimeDeltas", root.Name.ToString());

            // The extension's OWN id and name, not the target page's — the whole point of the
            // issue. "ERD Target Page" is what it extends and appears nowhere on this root.
            Assert.Equal(SharedExtId.ToString(), root.Attribute("ID")!.Value);
            Assert.Equal("Shared Name Ext", root.Attribute("Name")!.Value);
        });

    [Fact]
    public void A_pageextension_renders_the_members_it_adds_with_the_ids_and_names_AL_states()
        => WithApp(appPath =>
        {
            var doc = Render(appPath, "Page", SharedExtId);
            Assert.NotNull(doc);

            var actionAdds = doc!.Root!.Elements($"{Ns}ActionAdd").ToArray();
            var controlAdds = doc.Root!.Elements($"{Ns}ControlAdd").ToArray();
            var actions = actionAdds.Elements($"{Ns}Actions").ToArray();
            var controls = controlAdds.Elements($"{Ns}Controls").ToArray();

            // Concrete values, every one of them stated in SymbolReference.json.
            Assert.Equal(AddedActionId.ToString(), Assert.Single(actions).Attribute("ID")!.Value);
            Assert.Equal("Permissions", actions[0].Attribute("Name")!.Value);

            Assert.Equal(AddedControlId.ToString(), Assert.Single(controls).Attribute("ID")!.Value);
            Assert.Equal("User Plans", controls[0].Attribute("Name")!.Value);

            // SemanticKind follows the declaring container, and Operation is the ChangeKind
            // mapping measured against BC's own documents: the fixture's action change is
            // ChangeKind 2 (ContentLast, as on real ext 9862 and 4318) and its control change is
            // ChangeKind 3 (ContentBefore, as on real ext 774).
            Assert.Equal("Action", Assert.Single(actionAdds).Attribute("SemanticKind")!.Value);
            Assert.Equal("ContentLast", actionAdds[0].Attribute("Operation")!.Value);

            Assert.Equal("Content", Assert.Single(controlAdds).Attribute("SemanticKind")!.Value);
            Assert.Equal("ContentBefore", controlAdds[0].Attribute("Operation")!.Value);

            // An Anchor naming a SIBLING (addbefore/addafter) becomes AnchorName; an addlast's
            // Anchor names a container and BC writes no AnchorName, so neither does this.
            Assert.Equal("Has SUPER permission set", controlAdds[0].Attribute("AnchorName")!.Value);
            Assert.Null(actionAdds[0].Attribute("AnchorName"));

            // AnchorId is a hash BC computes and the runner cannot derive, so it is left off
            // rather than invented — the same contract as the *TranslationKey attributes.
            Assert.Null(actionAdds[0].Attribute("AnchorId"));
            Assert.Null(controlAdds[0].Attribute("AnchorId"));

            // xsi:type is what tells BC's reader which Delta subclass to build. Measured as one
            // of exactly three attributes FromXml requires; without it the document does not
            // parse at all.
            var xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");
            Assert.Equal("ActionDefinition", actions[0].Attribute(xsi + "type")!.Value);
            Assert.Equal("ControlDefinition", controls[0].Attribute(xsi + "type")!.Value);

            // ParentContainer is an ENUM to BC's reader, and the AL anchor is in the OTHER
            // vocabulary. "Processing" is an AL action AREA, translated to its runtime container
            // name; "Has SUPER permission set" is a sibling member, so it is not a container at
            // all and falls back to the one the member's own kind implies. The two happen to be
            // the same word here only because ActionAreaKind.Processing maps to ActionItems —
            // An_AL_action_area_anchor_is_translated_to_BCs_own_container_name is what separates
            // the two mechanisms.
            Assert.Equal("ActionItems", actionAdds[0].Attribute("ParentContainer")!.Value);
            Assert.Equal("ContentArea", controlAdds[0].Attribute("ParentContainer")!.Value);
        });

    [Fact]
    public void An_anchor_that_names_a_BC_container_is_passed_through_as_ParentContainer()
    {
        // The other half of the ParentContainer rule, and the case BC writes through verbatim:
        // real ext 324 anchors on "Prompting" and ext 2515 on "Promoted", and BC's documents
        // carry exactly those as ParentContainer with no AnchorName. Asserted on its own fixture
        // so the main one keeps exercising the fallback.
        var dir = TestScratch.Dir("al-runner-extension-runtime-deltas-anchor");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteAppWith(dir, """
                {
                  "RuntimeVersion": "17.0",
                  "PageExtensions": [
                    {
                      "Id": 88380903,
                      "Name": "Anchored Ext",
                      "TargetObject": "ERD Target Page",
                      "ActionChanges": [
                        { "Anchor": "Prompting", "ChangeKind": 1,
                          "Actions": [ { "Kind": 2, "Id": 640938003, "Name": "Generate" } ] }
                      ]
                    }
                  ]
                }
                """);

            var doc = Render(appPath, "Page", 88380903);
            Assert.NotNull(doc);

            var wrapper = Assert.Single(doc!.Root!.Elements($"{Ns}ActionAdd"));
            Assert.Equal("Prompting", wrapper.Attribute("ParentContainer")!.Value);
            Assert.Equal("ContentFirst", wrapper.Attribute("Operation")!.Value);

            // addfirst: BC writes no AnchorName, because the anchor IS the container.
            Assert.Null(wrapper.Attribute("AnchorName"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// An AL area anchor whose RUNTIME container name is a DIFFERENT word is translated, not
    /// passed through and not fallen back on. <c>addlast(Navigation)</c> is
    /// <c>ParentContainer="RelatedInformation"</c>.
    ///
    /// <para>Real object: System Application pageextension 2516 "AppSourceMarketPlaceExtension",
    /// whose SymbolReference states <c>Anchor: "Navigation"</c> and whose BC-emitted document
    /// states <c>ParentContainer="RelatedInformation"</c> (27.5 ground truth). That object has a
    /// deltas document on 27.5 and none on 28.1, which is why the metadata-equivalence harness
    /// only reported it on the 27.5 leg (#3923).</para>
    ///
    /// <para>The two vocabularies are BC's own and they are not the same set: AL source names
    /// the area <c>ActionAreaKind</c> (CodeAnalysis), the runtime names the container
    /// <c>ActionContainerType</c> (Types), and <c>MetadataEmitterHelper.GetContainerType</c> is
    /// the translation BC's own emitter applies. Measured by invoking that method for every
    /// enum member rather than inferred — see <c>ActionAreaToContainer</c>.</para>
    /// </summary>
    [Theory]
    // The four AL area names whose container name is a different word — the cases a
    // pass-through or a fallback gets wrong.
    [InlineData("Navigation", "RelatedInformation")]
    [InlineData("Reporting", "Reports")]
    [InlineData("Creation", "NewDocumentItems")]
    [InlineData("Embedding", "HomeItems")]
    [InlineData("Sections", "ActivityButtons")]
    // ...and the ones that happen to spell the same, which must keep working.
    [InlineData("Processing", "ActionItems")]
    [InlineData("Promoted", "Promoted")]
    [InlineData("Prompting", "Prompting")]
    [InlineData("SystemActions", "SystemActions")]
    [InlineData("PromptGuide", "PromptGuide")]
    public void An_AL_action_area_anchor_is_translated_to_BCs_own_container_name(
        string alArea, string containerName)
    {
        var dir = TestScratch.Dir("al-runner-extension-runtime-deltas-area");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteAppWith(dir, $$"""
                {
                  "RuntimeVersion": "17.0",
                  "PageExtensions": [
                    {
                      "Id": 88380904,
                      "Name": "Area Ext",
                      "TargetObject": "ERD Target Page",
                      "ActionChanges": [
                        { "Anchor": "{{alArea}}", "ChangeKind": 2,
                          "Actions": [ { "Kind": 2, "Id": 640938004, "Name": "Gallery" } ] }
                      ]
                    }
                  ]
                }
                """);

            var wrapper = Assert.Single(Render(appPath, "Page", 88380904)!.Root!.Elements($"{Ns}ActionAdd"));
            Assert.Equal(containerName, wrapper.Attribute("ParentContainer")!.Value);

            // The anchor names an AREA, so it is the container and NOT a sibling: BC writes no
            // AnchorName for these, whatever the change kind.
            Assert.Null(wrapper.Attribute("AnchorName"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The control-side half of the same translation: an AL <c>AreaKind</c> anchor.
    /// <c>FactBoxes</c> is <c>FactBoxArea</c>, which neither a pass-through nor the
    /// <c>ContentArea</c> fallback produces.
    /// </summary>
    [Theory]
    [InlineData("FactBoxes", "FactBoxArea")]
    [InlineData("RoleCenter", "RoleCenterArea")]
    [InlineData("Prompt", "PromptArea")]
    [InlineData("PromptOptions", "PromptOptionsArea")]
    [InlineData("Content", "ContentArea")]
    public void An_AL_control_area_anchor_is_translated_to_BCs_own_container_name(
        string alArea, string containerName)
    {
        var dir = TestScratch.Dir("al-runner-extension-runtime-deltas-carea");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteAppWith(dir, $$"""
                {
                  "RuntimeVersion": "17.0",
                  "PageExtensions": [
                    {
                      "Id": 88380905,
                      "Name": "CArea Ext",
                      "TargetObject": "ERD Target Page",
                      "ControlChanges": [
                        { "Anchor": "{{alArea}}", "ChangeKind": 2,
                          "Controls": [ { "Kind": 8, "Id": 640938005, "Name": "Extra" } ] }
                      ]
                    }
                  ]
                }
                """);

            var wrapper = Assert.Single(Render(appPath, "Page", 88380905)!.Root!.Elements($"{Ns}ControlAdd"));
            Assert.Equal(containerName, wrapper.Attribute("ParentContainer")!.Value);
            Assert.Null(wrapper.Attribute("AnchorName"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// An anchor naming a SIBLING MEMBER is still not a container: it keeps the
    /// kind-implied fallback and is written as <c>AnchorName</c> for the anchored change kinds.
    ///
    /// <para>This is the case the runner genuinely cannot derive — BC writes the container the
    /// SIBLING sits in, which SymbolReference does not state from the extension's side. Real
    /// object: pageextension 774, whose three ActionAdds anchor on view names and whose
    /// ParentContainer is <c>ViewActions</c>, not the <c>ActionItems</c> fallback (#3926).</para>
    /// </summary>
    [Fact]
    public void An_anchor_naming_a_sibling_member_is_not_treated_as_an_area()
    {
        var dir = TestScratch.Dir("al-runner-extension-runtime-deltas-sibling");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteAppWith(dir, """
                {
                  "RuntimeVersion": "17.0",
                  "PageExtensions": [
                    {
                      "Id": 88380906,
                      "Name": "Sibling Ext",
                      "TargetObject": "ERD Target Page",
                      "ActionChanges": [
                        { "Anchor": "Refresh", "ChangeKind": 4,
                          "Actions": [ { "Kind": 2, "Id": 640938006, "Name": "Gallery" } ] }
                      ]
                    }
                  ]
                }
                """);

            var wrapper = Assert.Single(Render(appPath, "Page", 88380906)!.Root!.Elements($"{Ns}ActionAdd"));
            Assert.Equal("ActionItems", wrapper.Attribute("ParentContainer")!.Value);
            Assert.Equal("ContentAfter", wrapper.Attribute("Operation")!.Value);
            Assert.Equal("Refresh", wrapper.Attribute("AnchorName")!.Value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Values_the_runner_cannot_derive_are_left_off_rather_than_defaulted()
        => WithApp(appPath =>
        {
            // BC's emitted documents carry ControlGUID, SourceExtensionType and the
            // *TranslationKey hashes. SymbolReference.json states none of them, so the render
            // must omit them: writing "" or a computed placeholder would be manufactured
            // agreement, in the direction that is hardest to notice.
            var doc = Render(appPath, "Page", SharedExtId);
            Assert.NotNull(doc);

            var action = doc!.Root!.Elements($"{Ns}ActionAdd").Elements($"{Ns}Actions").Single();
            foreach (var absent in new[]
                     {
                         "ControlGUID", "SourceExtensionType", "CaptionTranslationKey",
                         "ToolTipTranslationKey", "AboutTextTranslationKey", "AboutTitleTranslationKey",
                     })
                Assert.Null(action.Attribute(absent));
        });

    [Fact]
    public void The_object_type_discriminates_a_tableextension_from_a_pageextension_on_one_id()
        => WithApp(appPath =>
        {
            // The collision this fix exists for, and the reason the key is (type, id) rather than
            // id. Asking by id alone cannot tell these two apart.
            var asPage = Render(appPath, "Page", SharedExtId);
            var asTable = Render(appPath, "Table", SharedExtId);

            Assert.NotNull(asPage);
            Assert.NotNull(asTable);

            // Same id AND same name, so neither separates them...
            Assert.Equal(SharedExtId.ToString(), asPage!.Root!.Attribute("ID")!.Value);
            Assert.Equal(SharedExtId.ToString(), asTable!.Root!.Attribute("ID")!.Value);
            Assert.Equal("Shared Name Ext", asPage.Root!.Attribute("Name")!.Value);
            Assert.Equal("Shared Name Ext", asTable.Root!.Attribute("Name")!.Value);

            // ...and the CONTENT is what differs, in the direction BC's own documents do.
            Assert.NotEmpty(asPage.Root!.Elements());
            Assert.Empty(asTable.Root!.Elements());
        });

    [Fact]
    public void A_tableextension_of_obsoleted_fields_renders_an_EMPTY_deltas_document()
        => WithApp(appPath =>
        {
            // The majority case in the real population: all 6 tableextensions across the two 28.1
            // bundles declare only Obsolete=Moved/Removed fields, and BC emits an empty
            // <MetadataRuntimeDeltas/> for every one. Inventing a FieldAdd here would disagree
            // with BC on 6 of the 11 documents.
            var doc = Render(appPath, "Table", TableOnlyExtId);
            Assert.NotNull(doc);

            Assert.Equal(TableOnlyExtId.ToString(), doc!.Root!.Attribute("ID")!.Value);
            Assert.Equal("ObsoleteOnlyExt", doc.Root!.Attribute("Name")!.Value);
            Assert.Empty(doc.Root!.Elements());
        });

    [Fact]
    public void An_unreadable_package_raises_rather_than_reading_as_declaring_none()
    {
        // The census entry in DependencyAppSymbolWalkSourceGuardTests claims this seam neither
        // swallows nor skips. Asserted rather than asserted-in-prose: "declares no extensions"
        // is the one wrong answer that would make a render silently empty, and an empty render
        // compares clean against BC's five empty documents.
        var dir = TestScratch.Dir("al-runner-extension-runtime-deltas-unreadable");
        Directory.CreateDirectory(dir);
        try
        {
            var bad = Path.Combine(dir, "not-a-package.app");
            File.WriteAllText(bad, "this is not a zip");

            Assert.ThrowsAny<Exception>(
                () => RecordPatches.TryBuildExtensionRuntimeDeltasXmlForApp(bad, "Page", SharedExtId));

            // And a path that does not exist at all, which must not read as "declares none".
            Assert.ThrowsAny<Exception>(
                () => RecordPatches.TryBuildExtensionRuntimeDeltasXmlForApp(
                    Path.Combine(dir, "absent.app"), "Page", SharedExtId));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_id_no_extension_declares_answers_null_rather_than_an_empty_document()
        => WithApp(appPath =>
        {
            // BC returns null when no Extension-format summary matches, so "no such extension"
            // and "an extension with no deltas" are different answers there and must stay
            // different here — an empty document would claim an extension exists.
            Assert.Null(Render(appPath, "Page", UndeclaredId));
            Assert.Null(Render(appPath, "Table", UndeclaredId));

            // TableOnlyExtId is declared as a tableextension only, so asking for a PAGE
            // extension with that id is a genuine absence, not an empty document.
            Assert.Null(Render(appPath, "Page", TableOnlyExtId));

            // And an object type that is no extension kind the runner tracks.
            Assert.Null(Render(appPath, "Codeunit", SharedExtId));
        });

}

// The BC-reader arm lives in its own class because it needs the bc-engine-serial skeleton.
// Without it NavAppObjectMetadataRuntimeDeltas.FromXml throws a WindowsLanguageHelper
// static-init fault on any document carrying content — including BC's OWN — so a run outside
// the collection measures the fault rather than the document (#3809, and the issue's note that
// a bare probe parsed only 6 of 11).
[Collection(BcEngineCollection.Name)]
public sealed class ExtensionRuntimeDeltasBcReaderTests
{
    private readonly BcEngineFixture _engine;

    public ExtensionRuntimeDeltasBcReaderTests(BcEngineFixture engine) => _engine = engine;

    [SkippableFact]
    public void BCs_own_reader_parses_what_the_runner_renders()
    {
        // The claim that makes the render worth anything: it is not merely well-formed XML, it
        // is a document BC's OWN NavAppObjectMetadataRuntimeDeltas.FromXml reads. Without this
        // the render could drift into a shape only these tests accept — and it did on the first
        // attempt, which is how the three required attributes were found.
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var appsDll = Path.Combine(
            AlRunner.Infrastructure.BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.Apps.dll");
        Skip.IfNot(File.Exists(appsDll),
            $"Microsoft.Dynamics.Nav.Apps.dll is not on this box ({appsDll}); "
            + "the BC-side reader cannot be loaded, so this measures nothing.");

        var dir = TestScratch.Dir("al-runner-extension-runtime-deltas-reader");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = ExtensionRuntimeDeltasTests.WriteFixtureApp(dir);
            var xml = RecordPatches.TryBuildExtensionRuntimeDeltasXmlForApp(
                appPath, "Page", ExtensionRuntimeDeltasTests.SharedExtensionId);
            Assert.NotNull(xml);

            var apps = Assembly.LoadFrom(appsDll);
            var t = apps.GetType("Microsoft.Dynamics.Nav.Apps.MetadataDeltas.NavAppObjectMetadataRuntimeDeltas")!;
            var fromXml = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "FromXml"
                            && m.GetParameters() is { Length: 1 } p
                            && p[0].ParameterType == typeof(XDocument));

            var parsed = fromXml.Invoke(null, new object?[] { XDocument.Parse(xml!) });
            Assert.NotNull(parsed);

            var all = (System.Collections.IEnumerable)parsed!.GetType()
                .GetProperty("AllDeltas", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)!
                .GetValue(parsed)!;

            // Not just "it parsed": it read CONTENT — the ActionAdd and the ControlAdd this
            // extension declares. A reader that ignored the document answers 0 here, which is
            // the MetaPageDefinition failure mode this harness exists to make unreachable.
            Assert.Equal(2, all.Cast<object>().Count());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
