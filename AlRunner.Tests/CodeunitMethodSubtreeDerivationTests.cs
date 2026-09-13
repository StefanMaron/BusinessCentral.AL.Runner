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
                        InherentPermissionsMethod });
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
    /// symbol file's order rather than grouped after them.
    /// </summary>
    [Fact]
    public void A_stated_InherentPermissions_method_is_carried_alongside_the_publishers()
    {
        Register();

        Assert.Equal(
            new[] { (666, "OnBeforeGuarded"), (777, "GuardedProcedure") },
            Methods(Projection(InherentPermissionsMethod)));
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
