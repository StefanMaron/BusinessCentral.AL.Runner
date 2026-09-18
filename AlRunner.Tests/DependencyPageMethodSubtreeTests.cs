// DependencyPageMethodSubtreeTests — the PAGE <Methods> subtree (#4267), the page-side twin of
// CodeunitMethodSubtreeDerivationTests (#3788).
//
// WHAT BC EMITS FOR A PAGE, AND WHY 107 IS NOT THE NUMBER
//   #4267 measured that SymbolReference.json carries a non-empty Methods array on 107 of the 235
//   pages the metadata-equivalence harness covers, and concluded the runner has a datum it never
//   reads. The first half reproduces exactly; the second needs one correction, and it is the
//   whole design of this file.
//
//   Re-measured on BC 28.1.49838.53910 (Ncl.dll sha256 49b11d9b…) over the two apps
//   tests/expectations/metadata-equivalence/apps.json declares — Business Foundation (11 pages)
//   and System Application (224) — by cross-tabulating each app's shipped SymbolReference.json
//   against the PageDefinition documents tools/gen-metadata-ground-truth.sh produces from BC's
//   own emitter:
//
//     235 pages total
//     107 state a non-empty "Methods" array          <- the issue's figure, reproduced exactly
//      13 state a method carrying ANY attribute
//       5 state an event-publisher attribute
//       5 PageDefinition documents carry <Methods>   <- BC's own emitter
//
//   The 5 are the SAME five, by id: 2610 Feature Management, 4333 Agent Consumption Overview,
//   7775 Copilot AI Capabilities, 9801 User Subform, 9995 Word Template Creation Wizard. Every
//   one carries exactly one <Method>, with an EventPublisherAttribute.
//
//   So emitting for all 107 would MANUFACTURE a difference on 102 pages where BC writes nothing.
//   The 105 pages in the gap state ordinary public procedures, which BC's ObjectMetadataEmitter
//   does not write; the 8 in the smaller gap state Scope / Obsolete / NonDebuggable, which it
//   does not write either. The filter is EmittedMethodAttributeKinds, shared with the codeunit
//   path, so the two cannot drift apart.
//
// WHY A PAGE NEEDS THE SUBSCRIBER WITNESS TOO
//   An AL event subscriber is always `local`, so the symbol file — an app's consumer-facing API
//   surface — never states one, for a page any more than for a codeunit. The question is whether
//   a PAGE can host one at all, and it can: Base Application 28.1.49838.53910 ships
//   src/Modules/System/EventRecorder/EventRecorder.Page.al, which declares an [EventSubscriber],
//   and it is the only one of that app's 2,772 page/pageext AL files that does.
//
//   One is enough. MetadataObjectDiff pairs Methods POSITIONALLY, so a subtree that omits a
//   subscriber does not merely under-report: from the first missing element on it puts a
//   DIFFERENT method in BC's slot — the runner asserting an association it has no evidence for
//   (loud-failures.md). So the page path is gated on the same assembly witness, reading Page<N>
//   types instead of Codeunit<N>.
//
// WHY A RUNNER-SIDE MECHANISM TEST
//   EmitPageXml runs only for a page the runner never source-compiles — one shipped compiled
//   inside a dependency .app. A corpus test compiles its pages from source, which takes the real
//   compiler's own metadata path and never reaches this synthesizer (the structural case in
//   bc-behavior-tests-go-upstream.md, and the same argument DependencyPagePropertiesFromSymbolTests
//   makes for #3784). BC's own emitter output is the ground truth, and the metadata-equivalence
//   harness compares against it on every unit-test leg.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as DependencyPagePropertiesFromSymbolTests: BcAppSymbolCache.Get() resolves
// through the process-global CacheRoots override, and the per-id metadata-xml memo is
// process-global too.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPageMethodSubtreeTests : IDisposable
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    // Distinctive ids in their own block: the dependency-page state is process-global, so an id
    // another test also declares would read back that test's cached document instead of this one's.

    /// <summary>Publishers only, and the witness says this page declares no subscriber — the
    /// case the derivation exists for. Mirrors System Application page 2610.</summary>
    private const int PublishersOnlyPageId = 88267001;

    /// <summary>Publishers AND a subscriber the symbol file cannot see. The witness says so, and
    /// the subtree must stay absent rather than emit a short, mis-paired list.</summary>
    private const int PublishersAndSubscriberPageId = 88267002;

    /// <summary>States a non-empty Methods array of ORDINARY procedures. This is the shape 105
    /// of the measured 107 are in, and BC emits no &lt;Methods&gt; for it.</summary>
    private const int OrdinaryProceduresOnlyPageId = 88267003;

    /// <summary>States Scope/Obsolete attributes only — attributed, but not with a kind BC's
    /// emitter writes. The shape the other 8 of the 13 are in.</summary>
    private const int UnemittedAttributesPageId = 88267004;

    /// <summary>Publishers, but in an app whose assemblies were never witnessed. UNKNOWN, and
    /// unknown abstains rather than reading as "no subscribers".</summary>
    private const int PublishersButNoWitnessPageId = 88267005;

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

    /// <summary>
    /// The method shapes a real page's symbol entry presents. Microsoft writes the publisher's
    /// AL attribute under <c>Attributes</c> alongside the compiler-assigned <c>Id</c>, which is
    /// the value BC's emitter writes as <c>&lt;Method ID&gt;</c>. The <c>Scope</c> /
    /// <c>Obsolete</c> entries are copied from the real shapes System Application 28.1 states on
    /// pages 9810 "Password Dialog" and 8887 "Email Accounts".
    /// </summary>
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 88267001,
              "Name": "P4267 Publishers Only",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Methods": [
                { "Id": 700, "Name": "PlainProcedure", "Attributes": [] },
                { "Id": 1327903663, "Name": "OnOpenFeatureMgtPage",
                  "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "False" } ] } ] },
                { "Id": 701, "Name": "AnotherPlainProcedure", "Attributes": [] },
                { "Id": -2087241454, "Name": "OnPermissionSetNotFound",
                  "Attributes": [ { "Name": "InternalEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "True" } ] } ] }
              ]
            },
            {
              "Id": 88267002,
              "Name": "P4267 Publishers And Subscriber",
              "Properties": [ { "Name": "PageType", "Value": "Card" } ],
              "Methods": [
                { "Id": 810, "Name": "OnBeforeOther",
                  "Attributes": [ { "Name": "IntegrationEvent" } ] },
                { "Id": 811, "Name": "OnAfterOther",
                  "Attributes": [ { "Name": "IntegrationEvent" } ] }
              ]
            },
            {
              "Id": 88267003,
              "Name": "P4267 Ordinary Procedures Only",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Methods": [
                { "Id": 900, "Name": "SetSelection", "Attributes": [] },
                { "Id": 901, "Name": "GetSelection", "Attributes": [] }
              ]
            },
            {
              "Id": 88267004,
              "Name": "P4267 Unemitted Attributes",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Methods": [
                { "Id": 1000, "Name": "GetPasswordSecretValue",
                  "Attributes": [ { "Name": "Scope", "Arguments": [ { "Value": "OnPrem" } ] } ] },
                { "Id": 1001, "Name": "FilterConnectorV2Accounts",
                  "Attributes": [ { "Name": "Obsolete", "Arguments": [
                    { "Value": "Replaced by FilterConnectorV3Accounts." }, { "Value": "26.0" } ] } ] }
              ]
            },
            {
              "Id": 88267005,
              "Name": "P4267 Publishers But No Witness",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Methods": [
                { "Id": 1100, "Name": "OnBeforeUnwitnessed",
                  "Attributes": [ { "Name": "IntegrationEvent" } ] }
              ]
            }
          ]
        }
        """;

    private readonly string _dir;

    public DependencyPageMethodSubtreeTests()
    {
        _dir = TestScratch.Dir("al-runner-dep-page-methods-4267");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// Registers the fixture .app and the page witness the real load path registers from
    /// <c>DependencyLoader.RegisterAppAssemblies</c>. Four of the five pages are witnessed;
    /// <see cref="PublishersButNoWitnessPageId"/> is deliberately left out of the SCANNED set, so
    /// its answer is UNKNOWN rather than "no subscribers".
    /// </summary>
    private string Register(bool withWitness = true)
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();

        var appPath = WriteApp(_dir, SymbolReference);
        RecordPatches.AddBcAppPath(appPath);

        if (withWitness)
            RecordPatches.RegisterPageSubscriberWitness(
                appPath,
                new[] { PublishersAndSubscriberPageId },
                new[] { PublishersOnlyPageId, PublishersAndSubscriberPageId,
                        OrdinaryProceduresOnlyPageId, UnemittedAttributesPageId });

        return appPath;
    }

    private static XmlElement PageDocument(int pageId)
    {
        var xml = RecordPatches.TryBuildDependencyPageMetadata(pageId);
        Assert.True(xml is not null, $"the runner derived no metadata for page {pageId}");
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!;
    }

    /// <summary>Every <c>&lt;Method&gt;</c> of a page document as (id, name), in document order —
    /// which is the order MetadataObjectDiff pairs in, so order is part of the assertion rather
    /// than something a set comparison would hide.</summary>
    private static List<(int Id, string Name)> Methods(int pageId)
    {
        var methods = PageDocument(pageId).GetElementsByTagName("Methods", MetaNs)
            .OfType<XmlElement>().FirstOrDefault();
        if (methods is null) return new List<(int, string)>();
        return methods.ChildNodes.OfType<XmlElement>()
            .Where(e => e.LocalName == "Method")
            .Select(e => (int.Parse(e.GetAttribute("ID")), e.GetAttribute("Name")))
            .ToList();
    }

    private static bool HasMethodsElement(int pageId)
        => PageDocument(pageId).GetElementsByTagName("Methods", MetaNs).Count > 0;

    private static List<XmlElement> AttributeElements(int pageId)
    {
        var result = new List<XmlElement>();
        var methods = PageDocument(pageId).GetElementsByTagName("Methods", MetaNs)
            .OfType<XmlElement>().FirstOrDefault();
        if (methods is null) return result;
        foreach (var method in methods.ChildNodes.OfType<XmlElement>())
        {
            if (method.LocalName != "Method") continue;
            var attributes = method.ChildNodes.OfType<XmlElement>()
                .FirstOrDefault(e => e.LocalName == "MethodAttributes");
            var kind = attributes?.ChildNodes.OfType<XmlElement>().FirstOrDefault();
            if (kind is not null) result.Add(kind);
        }
        return result;
    }

    /// <summary>
    /// The positive case: a witnessed page states publishers, so the subtree is written, and it
    /// carries BC's own ids and names in the symbol file's array order — with the two ordinary
    /// procedures declared BETWEEN them filtered out, which is what makes this an assertion about
    /// the filter rather than about a copy.
    /// </summary>
    [Fact]
    public void WitnessedPageStatingPublishers_EmitsThoseMethodsInDocumentOrder()
    {
        Register();

        Assert.Equal(
            new List<(int, string)>
            {
                (1327903663, "OnOpenFeatureMgtPage"),
                (-2087241454, "OnPermissionSetNotFound"),
            },
            Methods(PublishersOnlyPageId));
    }

    /// <summary>
    /// The attribute element BC's own reader requires: <c>EventPublisherAttribute</c> carrying
    /// the AL attribute identifier as <c>Name</c>, plus the two positional flags. Both AL forms
    /// are here because they put the flags in DIFFERENT slots — <c>IntegrationEvent</c> is
    /// (IncludeSender, GlobalVarAccess, Isolated) and <c>InternalEvent</c> is (GlobalVarAccess,
    /// Isolated) with no sender argument at all — so a reader that ignored the difference would
    /// answer Isolated for the wrong one.
    /// </summary>
    [Fact]
    public void PublisherAttributes_CarryTheAlIdentifierAndThePositionalFlags()
    {
        Register();

        var attributes = AttributeElements(PublishersOnlyPageId);
        Assert.Equal(2, attributes.Count);

        Assert.Equal("EventPublisherAttribute", attributes[0].LocalName);
        Assert.Equal("IntegrationEvent", attributes[0].GetAttribute("Name"));
        Assert.Equal("False", attributes[0].GetAttribute("IncludeSender"));
        Assert.Equal(string.Empty, attributes[0].GetAttribute("Isolated"));

        Assert.Equal("EventPublisherAttribute", attributes[1].LocalName);
        Assert.Equal("InternalEvent", attributes[1].GetAttribute("Name"));
        // InternalEvent has no IncludeSender argument, so BC writes False for every one measured.
        Assert.Equal("False", attributes[1].GetAttribute("IncludeSender"));
        // Isolated is argument 1 on this form, not argument 2 — and BC writes it only when true.
        Assert.Equal("True", attributes[1].GetAttribute("Isolated"));
    }

    /// <summary>
    /// The witness saying "this page declares a subscriber" must suppress the subtree entirely.
    /// A short list is worse than none: MetadataObjectDiff pairs positionally, so the missing
    /// subscriber would put a different method in BC's slot.
    /// </summary>
    [Fact]
    public void PageWitnessedAsCarryingASubscriber_EmitsNoMethodsSubtree()
    {
        Register();

        Assert.False(HasMethodsElement(PublishersAndSubscriberPageId));
    }

    /// <summary>
    /// The UNKNOWN state, which must not read as "no subscribers". The page is absent from the
    /// SCANNED set, so nothing was measured about it, and abstaining is the honest answer.
    /// </summary>
    [Fact]
    public void PageTheWitnessNeverScanned_EmitsNoMethodsSubtree()
    {
        Register();

        Assert.False(HasMethodsElement(PublishersButNoWitnessPageId));
    }

    /// <summary>
    /// No witness registered for the app at all — the platform-symbol-only case. Same abstention,
    /// different cause, and this arm is what stops the previous one passing for the wrong reason:
    /// with no witness the page that DOES state publishers and IS otherwise clear stays absent.
    /// </summary>
    [Fact]
    public void AppWithNoWitnessAtAll_EmitsNoMethodsSubtree()
    {
        Register(withWitness: false);

        Assert.False(HasMethodsElement(PublishersOnlyPageId));
    }

    /// <summary>
    /// The 105-of-107 shape: a non-empty <c>Methods</c> array of ordinary public procedures. BC's
    /// emitter writes no <c>&lt;Methods&gt;</c> for these, so neither may the runner — this is
    /// the arm that would fail if the derivation keyed on the array being non-empty rather than
    /// on the attribute kind.
    /// </summary>
    [Fact]
    public void WitnessedPageStatingOnlyOrdinaryProcedures_EmitsNoMethodsSubtree()
    {
        Register();

        Assert.False(HasMethodsElement(OrdinaryProceduresOnlyPageId));
    }

    /// <summary>
    /// The 8-of-13 shape: attributed methods whose attribute is not one BC's emitter writes
    /// (<c>Scope</c>, <c>Obsolete</c>). Distinct from the arm above, which has no attributes at
    /// all — a derivation keyed on "any attribute" would pass that one and fail this.
    /// </summary>
    [Fact]
    public void WitnessedPageStatingOnlyUnemittedAttributes_EmitsNoMethodsSubtree()
    {
        Register();

        Assert.False(HasMethodsElement(UnemittedAttributesPageId));
    }
}
