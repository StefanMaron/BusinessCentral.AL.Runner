// CodeunitMethodSubtreeDerivationTests — the codeunit <Methods> subtree, derived where the
// loaded assembly proves the symbol file's view of it is COMPLETE (#3788, the two
// MetaRuntimeInfo members PR #3917 left open and PR #3963 diagnosed).
//
// WHAT BC EMITS, AND WHY ONE INPUT IS NOT ENOUGH
//   BC's emitter writes the codeunit's ATTRIBUTED methods, in three kinds. #3963 measured the
//   split on 28.1.49838.53910 over System Application + Business Foundation — 558 documents,
//   145 with a <Methods> subtree, 326 <Method> elements, none unattributed:
//
//     EventPublisherAttribute             stated by SymbolReference.json, exactly
//     InherentPermissionsMethodAttribute  stated when the method is not local
//     EventSubscriberAttribute            stated NOWHERE in it
//
//   The symbol file is an app's consumer-facing API surface, so it carries no `local` method,
//   and an AL event subscriber is always local. So a symbol-file-only derivation is complete
//   for a codeunit with no subscriber and silently SHORT for one with subscribers.
//
// WHY SHORT IS WORSE THAN ABSENT, WHICH IS WHAT MADE #3963 DEFER
//   MetadataObjectDiff pairs Methods POSITIONALLY — Methods is not in PairByIdMembers, and
//   MetaMethod spells its id "MethodId", which IdPropertyNames does not name. So a subtree
//   missing the subscribers does not merely under-report: from the first missing element on, it
//   puts a DIFFERENT method in BC's slot. That is the runner asserting an association it has no
//   evidence for, which loud-failures.md ranks below stating nothing at all.
//
//   Modelled over both apps at 28.1: emitting whenever the symbol file states anything gives 76
//   codeunits and 168 methods, of which 8 are the runner naming the wrong method. Absence costs
//   326 one-directional differences and fabricates nothing.
//
// THE SECOND INPUT, AND WHY IT IS A WITNESS RATHER THAN A DATA SOURCE
//   The dependency's own R2R assembly carries [NavEventSubscriberAttribute] on exactly the
//   methods the symbol file cannot see. It is NOT used to supply them — its metadata-table order
//   is alphabetical and BC's document order is the AL SOURCE declaration order, which measured
//   22/70 against every ordering hypothesis tried (alphabetical, metadata-table, MethodId
//   ascending, kind-then-table) and 70/70 against the AL source order. Reconstructing order from
//   AL source is the parser treadmill #3491 describes.
//
//   It is used to answer ONE question: does this codeunit carry a subscriber? When it does not,
//   the symbol file's publisher list IS BC's document, in BC's order. Measured with that policy
//   over three BC builds — 27.5.46862.53931, 28.1.49838.53910 and 28.4.53241.54407, two
//   genuinely distinct binaries across the 27.x/28.x boundary:
//
//     build     emitted        exact    FABRICATED   absent (was)
//     27.5      66 cu / 151    66       0            169  (320)
//     28.1      66 cu / 152    66       0            174  (326)
//     28.4      66 cu / 153    66       0            175  (328)
//
//   Zero fabrications on every build, and the honest-absence count falls by about half.
//
// THE THIRD STATE IS THE POINT (guards-need-a-third-state.md)
//   The witness answers yes / no / UNKNOWN, and unknown must not read as no. A dependency whose
//   assembly never loaded — a symbol-only platform app, a load that failed — can say nothing
//   about its subscribers, and treating that silence as "no subscribers" is exactly how the 8
//   fabrications come back. Unknown abstains, which is today's behaviour, which is honest.
//
// WHY A RUNNER-SIDE MECHANISM TEST
//   BC's own emitter output is the ground truth and the metadata-equivalence harness compares
//   against it on every unit-test leg, so no BC-behaviour claim is at stake. What no AL test can
//   reach is the precompiled-dependency route: an AL bundle's own codeunits are source-parsed,
//   so neither the symbol-file spelling nor the assembly witness ever comes up. This file drives
//   that route directly — the same argument CodeunitSymbolNamespaceAndInherentMaskTests makes.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reaches the RecordPatches AL parse statics, which are process-wide and which xunit's
// parallel collections can clear or repopulate between a write and a read (#1696, #1712).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class CodeunitMethodSubtreeDerivationTests : IDisposable
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    /// <summary>Publishers only, and the assembly witness says this codeunit has no subscriber —
    /// the case the derivation exists for.</summary>
    private const int PublishersOnly = 61060;

    /// <summary>Publishers AND a subscriber the symbol file cannot see. The witness says so, and
    /// the subtree must stay absent rather than emit a short, mis-paired list.</summary>
    private const int PublishersAndSubscriber = 61061;

    /// <summary>States no attributed method at all: BC emits no &lt;Methods&gt; and neither does
    /// the runner.</summary>
    private const int NoAttributedMethods = 61062;

    /// <summary>A non-local InherentPermissions method, which the symbol file DOES state — so it
    /// belongs in the subtree alongside the publishers.</summary>
    private const int InherentPermissionsMethod = 61063;

    /// <summary>Publishers only, but in an app whose assembly never loaded. The witness is
    /// UNKNOWN, and unknown abstains rather than reading as "no subscribers".</summary>
    private const int PublishersButNoWitness = 61064;

    /// <summary>Publishers whose attribute arguments exercise the POSITIONAL read: the two
    /// signatures put IncludeSender and Isolated in different slots, and one of them has no
    /// sender argument at all.</summary>
    private const int PublisherFlags = 61065;

    /// <summary>InherentPermissions methods whose arguments exercise all four of BC's emitted
    /// attributes independently — a different object type, a different object id, a different
    /// mask and each of the three scope ordinals (#4339).</summary>
    private const int InherentPermissionArguments = 61066;

    /// <summary>InherentPermissions methods the runner CANNOT read: one argument list too short
    /// to carry the three required values, and one unreadable value in each of the three
    /// positions that can have one (#4339).</summary>
    private const int InherentPermissionsUnreadable = 61067;

    private readonly string _root;

    public CodeunitMethodSubtreeDerivationTests()
    {
        _root = TestScratch.Dir("al-runner-codeunit-method-subtree");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The method shapes a real symbol file presents. Microsoft writes the publisher's AL
    /// attribute under <c>Attributes</c> — <c>IntegrationEvent</c>, <c>InternalEvent</c> or
    /// <c>BusinessEvent</c> — alongside the method's compiler-assigned <c>Id</c>, which is the
    /// value BC's emitter writes as <c>&lt;Method ID&gt;</c>. Ordinary procedures carry no
    /// attribute and BC emits none of them, so they are here to prove the filter runs.
    /// </summary>
    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "6f1b6d3e-4f0a-4c2e-9a1d-7b4c2f8e0a11",
          "Name": "Codeunit Method Subtree Fixture",
          "Namespaces": [
            {
              "Name": "Fixture",
              "Codeunits": [
                {
                  "Id": {{PublishersOnly}},
                  "Name": "Publishers Only",
                  "Properties": [],
                  "Methods": [
                    { "Id": 100, "Name": "PlainProcedure", "Attributes": [] },
                    { "Id": 111, "Name": "OnBeforeDoWork",
                      "Attributes": [ { "Name": "IntegrationEvent" } ] },
                    { "Id": 222, "Name": "OnAfterDoWork",
                      "Attributes": [ { "Name": "InternalEvent" } ] },
                    { "Id": 101, "Name": "AnotherPlainProcedure", "Attributes": [] },
                    { "Id": 333, "Name": "OnWorkCompleted",
                      "Attributes": [ { "Name": "BusinessEvent" } ] }
                  ]
                },
                {
                  "Id": {{PublishersAndSubscriber}},
                  "Name": "Publishers And Subscriber",
                  "Properties": [],
                  "Methods": [
                    { "Id": 444, "Name": "OnBeforeOther",
                      "Attributes": [ { "Name": "IntegrationEvent" } ] },
                    { "Id": 555, "Name": "OnAfterOther",
                      "Attributes": [ { "Name": "IntegrationEvent" } ] }
                  ]
                },
                {
                  "Id": {{NoAttributedMethods}},
                  "Name": "No Attributed Methods",
                  "Properties": [],
                  "Methods": [
                    { "Id": 102, "Name": "JustAProcedure", "Attributes": [] }
                  ]
                },
                {
                  "Id": {{InherentPermissionsMethod}},
                  "Name": "Inherent Permissions Method",
                  "Properties": [],
                  "Methods": [
                    { "Id": 666, "Name": "OnBeforeGuarded",
                      "Attributes": [ { "Name": "IntegrationEvent" } ] },
                    { "Id": 777, "Name": "GuardedProcedure",
                      "Attributes": [ { "Name": "InherentPermissions" } ] }
                  ]
                },
                {
                  "Id": {{PublishersButNoWitness}},
                  "Name": "Publishers But No Witness",
                  "Properties": [],
                  "Methods": [
                    { "Id": 888, "Name": "OnBeforeUnwitnessed",
                      "Attributes": [ { "Name": "IntegrationEvent" } ] }
                  ]
                },
                {
                  "Id": {{PublisherFlags}},
                  "Name": "Publisher Flags",
                  "Properties": [],
                  "Methods": [
                    { "Id": 901, "Name": "OnIntegrationPlain",
                      "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                        { "Value": "False" }, { "Value": "False" } ] } ] },
                    { "Id": 902, "Name": "OnIntegrationSender",
                      "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                        { "Value": "True" }, { "Value": "False" } ] } ] },
                    { "Id": 903, "Name": "OnIntegrationIsolated",
                      "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                        { "Value": "False" }, { "Value": "False" }, { "Value": "True" } ] } ] },
                    { "Id": 904, "Name": "OnInternalIsolated",
                      "Attributes": [ { "Name": "InternalEvent", "Arguments": [
                        { "Value": "False" }, { "Value": "True" } ] } ] },
                    { "Id": 905, "Name": "OnInternalPlain",
                      "Attributes": [ { "Name": "InternalEvent", "Arguments": [
                        { "Value": "True" } ] } ] }
                  ]
                },
                {
                  "Id": {{InherentPermissionArguments}},
                  "Name": "Inherent Permission Arguments",
                  "Properties": [],
                  "Methods": [
                    { "Id": 1001, "Name": "ScopeDefaulted",
                      "Attributes": [ { "Name": "InherentPermissions", "Arguments": [
                        { "Value": "TableData" }, { "Value": "9008" }, { "Value": "r" } ] } ] },
                    { "Id": 1002, "Name": "ScopePermissions",
                      "Attributes": [ { "Name": "InherentPermissions", "Arguments": [
                        { "Value": "TableData" }, { "Value": "8912" }, { "Value": "ri" },
                        { "Value": "Permissions" } ] } ] },
                    { "Id": 1003, "Name": "ScopeEntitlements",
                      "Attributes": [ { "Name": "InherentPermissions", "Arguments": [
                        { "Value": "Codeunit" }, { "Value": "1306" }, { "Value": "X" },
                        { "Value": "Entitlements" } ] } ] },
                    { "Id": 1004, "Name": "ScopeBothIsTheDefaultOrdinal",
                      "Attributes": [ { "Name": "InherentPermissions", "Arguments": [
                        { "Value": "Page" }, { "Value": "9005" }, { "Value": "rimd" },
                        { "Value": "Both" } ] } ] }
                  ]
                },
                {
                  "Id": {{InherentPermissionsUnreadable}},
                  "Name": "Inherent Permissions Unreadable",
                  "Properties": [],
                  "Methods": [
                    { "Id": 1101, "Name": "ArgumentsTooShort",
                      "Attributes": [ { "Name": "InherentPermissions", "Arguments": [
                        { "Value": "TableData" }, { "Value": "9008" } ] } ] },
                    { "Id": 1102, "Name": "ObjectIdIsNotANumber",
                      "Attributes": [ { "Name": "InherentPermissions", "Arguments": [
                        { "Value": "TableData" }, { "Value": "No. Series Line" },
                        { "Value": "r" } ] } ] },
                    { "Id": 1103, "Name": "MaskLetterIsUnreadable",
                      "Attributes": [ { "Name": "InherentPermissions", "Arguments": [
                        { "Value": "TableData" }, { "Value": "9008" }, { "Value": "rq" } ] } ] },
                    { "Id": 1104, "Name": "ScopeNameIsUnreadable",
                      "Attributes": [ { "Name": "InherentPermissions", "Arguments": [
                        { "Value": "TableData" }, { "Value": "9008" }, { "Value": "r" },
                        { "Value": "Sideways" } ] } ] }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Registers the fixture .app and the assembly witness the real load path registers from
    /// <c>DependencyLoader.RegisterAppAssemblies</c>. Four of the five codeunits are witnessed;
    /// <see cref="PublishersButNoWitness"/> is deliberately left out of the witnessed set by
    /// registering a witness for a DIFFERENT app path, so its answer is UNKNOWN rather than
    /// "no subscribers".
    /// </summary>
    private string Register(bool withWitness = true)
    {
        var appPath = Path.Combine(_root, "method-subtree.app");
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

        if (withWitness)
        {
            // What the assembly scan found: this codeunit declares a [NavEventSubscriber]
            // method. Everything else in the same app is witnessed as carrying none — which is
            // the claim the witness makes, and the reason the set is registered per APP rather
            // than per codeunit.
            RecordPatches.RegisterCodeunitSubscriberWitness(
                appPath,
                new[] { PublishersAndSubscriber },
                new[] { PublishersOnly, PublishersAndSubscriber, NoAttributedMethods,
                        InherentPermissionsMethod, PublisherFlags, InherentPermissionArguments,
                        InherentPermissionsUnreadable });
        }

        return appPath;
    }

    /// <summary>
    /// Reads the projection the metadata-equivalence harness compares — the same
    /// <c>EnumerateKnownCodeunitMetadata</c> row CodeUnit Metadata (2000000137) answers from.
    /// </summary>
    private static XmlElement Projection(int codeunitId)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(codeunitId);
        Assert.True(xml is not null, $"the runner derived no metadata for codeunit {codeunitId}");
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!;
    }

    /// <summary>Every <c>&lt;Method&gt;</c> of a projection as (id, name), in document order —
    /// which is the order MetadataObjectDiff pairs in, so order is part of the assertion rather
    /// than something a set comparison would hide.</summary>
    private static List<(int Id, string Name)> Methods(XmlElement codeunit)
    {
        var methods = codeunit.GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().FirstOrDefault();
        if (methods is null) return new List<(int, string)>();
        return methods.ChildNodes.OfType<XmlElement>()
            .Where(e => e.LocalName == "Method")
            .Select(e => (int.Parse(e.GetAttribute("ID")), e.GetAttribute("Name")))
            .ToList();
    }

    /// <summary>The <c>MethodAttributes</c> child element name of each method, in document
    /// order — which of BC's three attribute kinds the runner wrote.</summary>
    private static List<string> AttributeKinds(XmlElement codeunit)
        => AttributeElements(codeunit).Select(e => e.LocalName).ToList();

    /// <summary>The <c>Name</c> of each attribute element — the AL attribute identifier, which
    /// BC's own reader requires (see the test that pins it).</summary>
    private static List<string> AttributeNames(XmlElement codeunit)
        => AttributeElements(codeunit).Select(e => e.GetAttribute("Name")).ToList();

    private static IEnumerable<XmlElement> AttributeElements(XmlElement codeunit)
    {
        var methods = codeunit.GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().FirstOrDefault();
        if (methods is null) yield break;
        foreach (var method in methods.ChildNodes.OfType<XmlElement>())
        {
            if (method.LocalName != "Method") continue;
            var attributes = method.ChildNodes.OfType<XmlElement>()
                .FirstOrDefault(e => e.LocalName == "MethodAttributes");
            var kind = attributes?.ChildNodes.OfType<XmlElement>().FirstOrDefault();
            if (kind is not null) yield return kind;
        }
    }

    /// <summary>
    /// The positive case: a witnessed codeunit with no subscriber renders exactly the symbol
    /// file's attributed methods, with their stated ids, in the symbol file's order — which is
    /// BC's document order, measured 70/70 on System Application 28.1 (#3963).
    ///
    /// <para>The unattributed procedures are the discriminator: a derivation that emitted the
    /// codeunit's METHODS rather than its ATTRIBUTED methods would put PlainProcedure first and
    /// every subsequent element in the wrong slot.</para>
    /// </summary>
    [Fact]
    public void A_codeunit_the_witness_clears_renders_its_publishers_with_stated_ids_in_order()
    {
        Register();

        Assert.Equal(
            new[] { (111, "OnBeforeDoWork"), (222, "OnAfterDoWork"), (333, "OnWorkCompleted") },
            Methods(Projection(PublishersOnly)));
    }

    /// <summary>
    /// A non-local InherentPermissions method is one of the three kinds BC emits and the symbol
    /// file states it, so it belongs in the subtree — interleaved with the publishers in the
    /// symbol file's order rather than grouped after them, and under its own attribute element
    /// rather than the publishers'.
    /// </summary>
    [Fact]
    public void A_stated_InherentPermissions_method_is_carried_alongside_the_publishers()
    {
        Register();

        var projection = Projection(InherentPermissionsMethod);
        Assert.Equal(
            new[] { (666, "OnBeforeGuarded"), (777, "GuardedProcedure") },
            Methods(projection));

        // The two carry DIFFERENT attribute elements, so the kind is read per method rather
        // than assumed from the codeunit.
        Assert.Equal(
            new[] { "EventPublisherAttribute", "InherentPermissionsMethodAttribute" },
            AttributeKinds(projection));
    }

    /// <summary>
    /// Every attribute element carries a <c>Name</c>, and omitting it is not a difference but a
    /// CRASH: BC's own <c>MetaCodeunit(XmlNode)</c> throws <c>NullReferenceException</c> on an
    /// attribute element that has none.
    ///
    /// <para>Measured against the live constructor rather than reasoned: a bare
    /// <c>&lt;EventPublisherAttribute /&gt;</c> threw, <c>Name</c> alone was enough, and BC's own
    /// fuller form (<c>IncludeSender</c>, <c>GlobalVarAccess</c>) also parsed. It reached the
    /// metadata-equivalence harness as "the runner produced no comparable metadata for CodeUnit
    /// 310 'No. Series'" — a whole object dropped from the comparison, not a member reported
    /// wrong, which is why this is asserted here and not left to the difference count.</para>
    ///
    /// <para>The value is the AL attribute identifier the symbol file states, so
    /// <c>IntegrationEvent</c>, <c>InternalEvent</c> and <c>BusinessEvent</c> all render under
    /// <c>EventPublisherAttribute</c> — BC's document does not distinguish them at the element
    /// level — while keeping their own names.</para>
    /// </summary>
    [Fact]
    public void Every_attribute_element_states_the_AL_attribute_name_BCs_reader_requires()
    {
        Register();

        Assert.Equal(
            new[] { "IntegrationEvent", "InternalEvent", "BusinessEvent" },
            AttributeNames(Projection(PublishersOnly)));

        Assert.Equal(
            new[] { "IntegrationEvent", "InherentPermissions" },
            AttributeNames(Projection(InherentPermissionsMethod)));
    }

    /// <summary>
    /// The refusal that is the whole point. The witness says this codeunit carries a subscriber,
    /// the symbol file cannot state it, so a subtree built from the symbol file alone would put
    /// OnBeforeOther in a slot that may not be BC's. The element is omitted entirely — an honest
    /// absence, which is a one-directional difference, rather than a fabricated association.
    /// </summary>
    [Fact]
    public void A_codeunit_the_witness_says_has_a_subscriber_emits_no_Methods_subtree()
    {
        Register();

        var projection = Projection(PublishersAndSubscriber);
        Assert.Empty(Methods(projection));
        Assert.Empty(projection.GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>());

        // The refusal is about THIS codeunit, not about the projection: a sibling in the same
        // app, cleared by the same witness, does render its methods.
        Assert.NotEmpty(Methods(Projection(PublishersOnly)));
    }

    /// <summary>
    /// The third state (guards-need-a-third-state.md). No witness was registered for this app,
    /// so nothing has measured whether its codeunits carry subscribers — and an unmeasured
    /// codeunit must not be treated as a cleared one. The subtree stays absent, which is exactly
    /// the behaviour before this change and the honest answer.
    ///
    /// <para>This is the assertion that separates "the assembly said no subscribers" from "no
    /// assembly said anything". Collapsing the two is how the 8 fabricated slots #3963 modelled
    /// would come back, and no aggregate count would show it.</para>
    /// </summary>
    [Fact]
    public void A_codeunit_with_no_witness_at_all_abstains_rather_than_reading_as_cleared()
    {
        Register(withWitness: false);

        Assert.Empty(Methods(Projection(PublishersButNoWitness)));

        // Same codeunit, same symbol file, WITH a witness clearing it -> it renders. So the
        // abstention above is the witness's absence and not something about this codeunit.
        var appPath = Register();
        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { PublishersButNoWitness });
        Assert.Equal(
            new[] { (888, "OnBeforeUnwitnessed") },
            Methods(Projection(PublishersButNoWitness)));
    }

    /// <summary>
    /// <c>IncludeSender</c> and <c>Isolated</c> come from the publisher attribute's POSITIONAL
    /// arguments, and the two AL signatures put them in DIFFERENT slots:
    /// <c>IntegrationEvent(IncludeSender, GlobalVarAccess[, Isolated])</c> and
    /// <c>InternalEvent(GlobalVarAccess[, Isolated])</c>, the latter having no sender argument
    /// at all.
    ///
    /// <para>This test exists because the real population does not discriminate the slots:
    /// System Application 28.1 has 3 two-argument <c>InternalEvent</c>s and none carrying
    /// <c>Isolated</c> at any index, so reading an <c>InternalEvent</c>'s isolation from
    /// <c>IntegrationEvent</c>'s slot 2 left the metadata-equivalence harness GREEN over all 558
    /// codeunits. Found by running that mutation, not by reading the code (tdd.md).</para>
    ///
    /// <para><c>Isolated</c> is asserted through presence rather than value because BC's own
    /// emitter omits it when false — 9 of 149 elements carry it on 28.1 — so writing
    /// <c>"False"</c> on the rest would state a value where BC states absence.</para>
    /// </summary>
    [Fact]
    public void The_publisher_flags_are_read_from_each_signatures_own_argument_slots()
    {
        Register();

        var flags = AttributeElements(Projection(PublisherFlags)).ToList();
        Assert.Equal(5, flags.Count);

        // IntegrationEvent: IncludeSender is slot 0, Isolated is slot 2.
        Assert.Equal("False", flags[0].GetAttribute("IncludeSender"));
        Assert.False(flags[0].HasAttribute("Isolated"));

        Assert.Equal("True", flags[1].GetAttribute("IncludeSender"));
        Assert.False(flags[1].HasAttribute("Isolated"));

        Assert.Equal("False", flags[2].GetAttribute("IncludeSender"));
        Assert.Equal("True", flags[2].GetAttribute("Isolated"));

        // InternalEvent: NO sender argument, so IncludeSender is always False and Isolated is
        // slot 1. This is the pair that discriminates the signatures — 904 states
        // ("False", "True") and must read Isolated from slot 1, while 905 states ("True") alone,
        // whose single argument is GlobalVarAccess and must NOT be read as either flag.
        Assert.Equal("False", flags[3].GetAttribute("IncludeSender"));
        Assert.Equal("True", flags[3].GetAttribute("Isolated"));

        Assert.Equal("False", flags[4].GetAttribute("IncludeSender"));
        Assert.False(flags[4].HasAttribute("Isolated"));
    }

    /// <summary>
    /// BC writes FIVE attributes on an <c>InherentPermissionsMethodAttribute</c> element, and the
    /// four beyond <c>Name</c> come from the AL attribute's own POSITIONAL arguments —
    /// <c>InherentPermissions(ObjectType, ObjectId, Mask[, Scope])</c> (#4339).
    ///
    /// <para>Each of the four is asserted through a value that VARIES across the fixture's rows,
    /// so a renderer that dropped one, or that wrote a constant, reds this arm rather than
    /// riding along on a value another row also has: the object types are TableData / Codeunit /
    /// Page, the ids 9008 / 8912 / 1306 / 9005, the masks r / ri / X / rimd, and the scopes
    /// defaulted / Permissions / Entitlements / Both.</para>
    ///
    /// <para><b>Where each value comes from, measured rather than inferred.</b> A probe app
    /// carrying exactly these shapes was compiled through BC's own emitter at
    /// 28.1.49838.53910 (<c>Microsoft.Dynamics.Nav.Ncl.dll</c> sha256 <c>49b11d9b…</c>) — see
    /// docs/codeunit-metadata-from-bc.md#the-inherentpermissions-method-attribute. Type and id
    /// pass through VERBATIM, the mask is the shared letter decode, and the scope is the
    /// ORDINAL of <c>Microsoft.Dynamics.Nav.Runtime.Permissions.InherentPermissionsScope</c>,
    /// whose decompiled body is <c>{ Both, Permissions, Entitlements }</c> — so <c>Both</c> and
    /// an absent argument both answer 0.</para>
    /// </summary>
    [Fact]
    public void The_four_InherentPermission_attributes_are_read_from_the_attributes_arguments()
    {
        Register();

        var elements = AttributeElements(Projection(InherentPermissionArguments)).ToList();
        Assert.Equal(4, elements.Count);
        Assert.All(elements, e => Assert.Equal("InherentPermissionsMethodAttribute", e.LocalName));

        // Argument 0, verbatim: three different object types, none of them inferred from the
        // element's own name.
        Assert.Equal(
            new[] { "TableData", "TableData", "Codeunit", "Page" },
            elements.Select(e => e.GetAttribute("InherentPermissionObjectType")));

        // Argument 1, verbatim: already numeric in the symbol file, so this is a READ and not a
        // table-name resolution.
        Assert.Equal(
            new[] { "9008", "8912", "1306", "9005" },
            elements.Select(e => e.GetAttribute("InherentPermissionObjectId")));

        // Argument 2, through the shared letter decoder: Read 1 / Insert 2 / Modify 4 /
        // Delete 8 / Execute 16, each again at n+5 for a lowercase letter. "X" is 16 and "r"
        // is 32, which is what makes case significant here as everywhere else.
        Assert.Equal(
            new[] { "32", "96", "16", "480" },
            elements.Select(e => e.GetAttribute("InherentPermissionPermissionValue")));

        // Argument 3, the enum ORDINAL. The first and last rows are the pair that matters: an
        // absent argument and an explicit "Both" both answer 0, so a renderer hardcoding 0
        // passes those two and fails the middle two.
        Assert.Equal(
            new[] { "0", "1", "2", "0" },
            elements.Select(e => e.GetAttribute("InherentPermissionScope")));
    }

    /// <summary>
    /// The four attributes are written on the <c>InherentPermissions</c> element ONLY. An
    /// <c>EventPublisherAttribute</c> element carries <c>IncludeSender</c> and its own flags and
    /// none of these, which is what stops the #4339 fix from writing them everywhere.
    ///
    /// <para>The four absences are asserted beside a POSITIVE drawn from the same run, so this
    /// cannot pass because the renderer stopped writing them altogether: the same assertion run
    /// against a renderer that writes the set unconditionally reds here, and against one that
    /// writes it nowhere reds on the positive.</para>
    /// </summary>
    [Fact]
    public void The_four_attributes_are_written_on_the_InherentPermissions_element_only()
    {
        Register();

        var mixed = AttributeElements(Projection(InherentPermissionsMethod)).ToList();
        Assert.Equal(2, mixed.Count);

        var publisher = mixed[0];
        Assert.Equal("EventPublisherAttribute", publisher.LocalName);
        Assert.False(publisher.HasAttribute("InherentPermissionObjectType"));
        Assert.False(publisher.HasAttribute("InherentPermissionObjectId"));
        Assert.False(publisher.HasAttribute("InherentPermissionPermissionValue"));
        Assert.False(publisher.HasAttribute("InherentPermissionScope"));

        // The positive that makes the four absences above mean something: in the SAME run, an
        // InherentPermissions element whose attribute states its arguments carries all four.
        var stated = AttributeElements(Projection(InherentPermissionArguments)).First();
        Assert.Equal("InherentPermissionsMethodAttribute", stated.LocalName);
        Assert.True(stated.HasAttribute("InherentPermissionObjectType"));
        Assert.True(stated.HasAttribute("InherentPermissionObjectId"));
        Assert.True(stated.HasAttribute("InherentPermissionPermissionValue"));
        Assert.True(stated.HasAttribute("InherentPermissionScope"));
    }

    /// <summary>
    /// An <c>InherentPermissions</c> attribute stating NO arguments keeps its <c>Name</c> and
    /// states none of the four — the runner's whole pre-#4339 output, and still the right answer
    /// for a symbol file that carries nothing to write.
    ///
    /// <para>Writing a default instead would be the manufactured-agreement failure this
    /// projection exists to avoid: BC emits 0 for <c>InherentPermissionScope</c> on all 24
    /// elements measured, so a hardcoded 0 would look correct against the shipped apps while
    /// stating a value the runner never read (loud-failures.md). <c>Both</c> and absent are the
    /// same ordinal, which is exactly what makes that mistake invisible.</para>
    /// </summary>
    [Fact]
    public void An_InherentPermissions_attribute_with_no_arguments_states_Name_alone()
    {
        Register();

        var bare = AttributeElements(Projection(InherentPermissionsMethod)).Last();
        Assert.Equal("InherentPermissionsMethodAttribute", bare.LocalName);
        Assert.Equal("InherentPermissions", bare.GetAttribute("Name"));

        Assert.False(bare.HasAttribute("InherentPermissionObjectType"));
        Assert.False(bare.HasAttribute("InherentPermissionObjectId"));
        Assert.False(bare.HasAttribute("InherentPermissionPermissionValue"));
        Assert.False(bare.HasAttribute("InherentPermissionScope"));
    }

    /// <summary>
    /// A value the runner cannot read refuses the WHOLE set rather than contributing a default:
    /// the element keeps its <c>Name</c> and states none of the four, so it reads as stating
    /// nothing rather than as stating three-quarters of an association
    /// (guards-need-a-third-state.md).
    ///
    /// <para>Four separate shapes, because they take four different paths through the reader and
    /// a single arm would not say which one fired: an argument list too SHORT to carry the three
    /// required values, a non-numeric object id, a mask letter outside
    /// <c>PermissionMaskLetters</c>, and a scope name outside BC's three. The fourth is the one
    /// a lenient reader would silently turn into 0 — which is the value BC writes on every
    /// element the shipped apps contain, so nothing downstream would look wrong.</para>
    ///
    /// <para>Non-vacuity is carried by the second assertion: the same run's readable codeunit
    /// still renders all four, so these four refusals are each row's property rather than the
    /// reader having stopped answering.</para>
    /// </summary>
    [Fact]
    public void An_unreadable_InherentPermissions_value_refuses_all_four_rather_than_defaulting()
    {
        Register();

        var refused = AttributeElements(Projection(InherentPermissionsUnreadable)).ToList();
        Assert.Equal(4, refused.Count);

        foreach (var element in refused)
        {
            Assert.Equal("InherentPermissionsMethodAttribute", element.LocalName);
            Assert.Equal("InherentPermissions", element.GetAttribute("Name"));
            Assert.False(element.HasAttribute("InherentPermissionObjectType"));
            Assert.False(element.HasAttribute("InherentPermissionObjectId"));
            Assert.False(element.HasAttribute("InherentPermissionPermissionValue"));
            Assert.False(element.HasAttribute("InherentPermissionScope"));
        }

        // The readable codeunit in the SAME run still states all four, so the refusals above
        // are about these four rows and not about the reader having gone quiet.
        Assert.Equal(
            new[] { "32", "96", "16", "480" },
            AttributeElements(Projection(InherentPermissionArguments))
                .Select(e => e.GetAttribute("InherentPermissionPermissionValue")));
    }

    /// <summary>
    /// A codeunit whose only methods are unattributed emits no <c>&lt;Methods&gt;</c> element at
    /// all — BC omits the subtree rather than writing an empty one, and 396 of System
    /// Application 28.1's 533 codeunits are in exactly this state (#3963).
    /// </summary>
    [Fact]
    public void A_codeunit_stating_no_attributed_method_emits_no_Methods_element()
    {
        Register();

        var projection = Projection(NoAttributedMethods);
        Assert.Empty(projection.GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>());

        // Non-vacuity: the same fixture renders a subtree for a codeunit that states one, so an
        // empty result here is this codeunit's property rather than the renderer doing nothing.
        Assert.NotEmpty(Methods(Projection(PublishersOnly)));
    }
}
