// MetadataEquivalenceCodeunitOracleTests — pins that the CodeUnit comparison has a real oracle
// and a real runner side, so it cannot go green having measured nothing (#3782, step 2 of 8).
//
// WHY THIS FILE EXISTS
//   Step 1 of #3782 handed BC's page document to MetaPageDefinition, which constructs without
//   throwing and returns a DEFAULT object. The harness then compared that empty object against
//   itself: 235 pages compared, 0 differences, every other test in the suite still green. A
//   comparison whose two sides are both empty is indistinguishable from a comparison that
//   agrees, and nothing in the harness can tell them apart.
//
//   The codeunit side has the same two ways to go quietly wrong, so both are pinned here:
//     1. the ORACLE could parse nothing — asserted against real values from a real document;
//     2. the RUNNER side could be BC's own emitter output — which would compare BC against BC.

using System.Reflection;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class MetadataEquivalenceCodeunitOracleTests
{
    /// <summary>BC's own emitter namespace, which the projection renders into.</summary>
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    private readonly BcEngineFixture _engine;

    public MetadataEquivalenceCodeunitOracleTests(BcEngineFixture engine) => _engine = engine;

    private static Type CodeunitType() =>
        Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaCodeunit, Microsoft.Dynamics.Nav.Types")
        ?? throw new InvalidOperationException("MetaCodeunit is not reachable.");

    private static object Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var ctor = CodeunitType().GetConstructor(new[] { typeof(XmlNode) })
                   ?? throw new InvalidOperationException("MetaCodeunit has no (XmlNode) constructor.");
        return ctor.Invoke(new object?[] { doc.DocumentElement })!;
    }

    private static object? Read(object meta, string property) =>
        CodeunitType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetValue(meta);

    /// <summary>
    /// The oracle READS the document. Every value here is one BC's emitter actually wrote, so a
    /// type that ignored the document — the MetaPageDefinition failure — answers 0 / null and
    /// fails on the first assertion rather than comparing nothing against nothing.
    /// </summary>
    [SkippableFact]
    public void MetaCodeunit_parses_the_emitters_own_document_rather_than_ignoring_it()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        // The emitter's own shape for System Application codeunit 26, attribute for attribute.
        var meta = Parse(
            """
            <CodeUnit xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" MetadataVersion="130000"
                      ID="26" Name="Confirm Management Impl." ALNamespace="System.Utilities"
                      InherentEntitlements="16" InherentPermissions="16" SingleInstance="1"
                      EventSubscriberInstance="StaticAutomatic" Subtype="Normal" TestIsolation="Disabled" />
            """);

        Assert.Equal(26, Read(meta, "Id"));
        Assert.Equal("Confirm Management Impl.", Read(meta, "Name"));
        Assert.Equal("System.Utilities", Read(meta, "ALNamespace"));
        // Enum-valued, so compared by name rather than by a hardcoded ordinal.
        Assert.Equal("Normal", Read(meta, "SubType")!.ToString());
        Assert.Equal("Execute", Read(meta, "InherentPermissions")!.ToString());
        Assert.Equal("Execute", Read(meta, "InherentEntitlements")!.ToString());
    }

    /// <summary>
    /// The oracle DISCRIMINATES: a different document produces different values. Without this,
    /// the test above would still pass against a type that returned one hardcoded object.
    /// </summary>
    [SkippableFact]
    public void MetaCodeunit_answers_differently_for_a_different_document()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var meta = Parse(
            """
            <CodeUnit xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" ID="9557"
                      Name="Table Key" ALNamespace="System.Reflection" Subtype="Test"
                      SingleInstance="0" TableNo="2000000001" />
            """);

        Assert.Equal(9557, Read(meta, "Id"));
        Assert.Equal("Table Key", Read(meta, "Name"));
        Assert.Equal("System.Reflection", Read(meta, "ALNamespace"));
        Assert.Equal("Test", Read(meta, "SubType")!.ToString());
        // Absent on this document, present on the one above — so the reader is reading, not
        // returning a constant.
        Assert.Equal("None", Read(meta, "InherentPermissions")!.ToString());
    }

    /// <summary>
    /// The runner's side is the runner's OWN derivation, not BC's emitter output.
    ///
    /// <para>This is the assertion that stops the whole comparison becoming BC-against-BC. The
    /// codeunit document sitting in <c>AlObjectMetadataRegistry</c> is what BC's emitter
    /// produced, captured at compile time — <c>RecordPatches.CodeunitMetadataFromBcDocument.cs</c>
    /// says so at its head — and it is the SAME document the ground truth holds. Feeding it to
    /// the harness would report zero differences having measured nothing.</para>
    ///
    /// <para>What the projection states is therefore exactly the values the runner derives from
    /// SymbolReference.json, and it must NOT state the members only BC's emitter knows.</para>
    ///
    /// <para><b>Since #3788 that set is eight, not five</b> — ALNamespace, InherentEntitlements
    /// and InherentPermissions joined it, because the symbol file states all three and the runner
    /// now reads them. So absence is no longer what pins the two sides apart for those three, and
    /// this test says what does: the projection's values must be the ones the SYMBOL FILE
    /// produces, which for the two masks is a DIFFERENT SPELLING from the registry document's.
    /// A projection that started echoing BC's document would be caught by the mask assertions
    /// below, not by an absence check.</para>
    ///
    /// <para>The members BC's emitter alone derives — TestIsolation, EventSubscriberInstance,
    /// MetadataVersion, the whole &lt;Methods&gt; subtree — are still asserted absent, and that is
    /// still the BC-against-BC guard for them.</para>
    /// </summary>
    [SkippableFact]
    public void The_runner_side_states_only_what_the_runner_derives()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceBundleGate.RequireBundles();

        var bundle = bundles.First(b => b.AppName == "System Application");
        var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
        Skip.If(app is null, "the System Application .app for this bundle is not on this box.");
        RecordPatches.AddBcAppPath(app!);

        // Codeunit 26 is in System Application and BC's emitter states ALNamespace,
        // InherentPermissions, InherentEntitlements and TestIsolation for it.
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(26);
        Assert.True(xml is not null,
            "the runner derives no metadata for System Application codeunit 26, so the CodeUnit " +
            "comparison would report it unbuildable rather than comparing it.");

        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        var root = doc.DocumentElement!;

        // Present: the eight the runner genuinely derives from SymbolReference.json.
        Assert.Equal("26", root.GetAttribute("ID"));
        Assert.Equal("Confirm Management Impl.", root.GetAttribute("Name"));
        Assert.Equal("1", root.GetAttribute("SingleInstance"));
        Assert.Equal("Normal", root.GetAttribute("Subtype"));

        // The three #3788 added. The namespace comes from the Namespaces tree path — nothing in
        // the registry document would supply it to a projection that never read the tree.
        Assert.Equal("System.Utilities", root.GetAttribute("ALNamespace"));

        // The two masks, which are the sharper half of the BC-against-BC guard now. The symbol
        // file states the AL LETTER "X" and the runner decodes it to 16; a projection that had
        // started echoing BC's captured document would be reading an attribute that is ALREADY
        // "16" without decoding anything — so these assertions do not discriminate on their own,
        // and the fixture-driven CodeunitSymbolNamespaceAndInherentMaskTests is what proves the
        // decode: it feeds "x" and requires 512, a value BC's document for codeunit 26 does not
        // contain at all.
        Assert.Equal("16", root.GetAttribute("InherentEntitlements"));
        Assert.Equal("16", root.GetAttribute("InherentPermissions"));

        // Still absent: what only BC's emitter knows. If any of these appears, the runner side
        // has started carrying BC's own answers and the comparison has stopped measuring.
        foreach (var emitterOnly in new[]
                 { "TestIsolation", "EventSubscriberInstance", "MetadataVersion" })
            Assert.False(root.HasAttribute(emitterOnly),
                $"the runner's projection states '{emitterOnly}', which only BC's emitter " +
                "derives. Stating it would manufacture agreement with the ground truth and " +
                "turn a real gap into a silent pass.");

        // The ONLY child element the runner derives is <Methods>, and only for a codeunit whose
        // loaded code proves the symbol file's view of it is complete (#3788). Anything else
        // here would be BC's own emitter output rather than the runner's derivation, which is
        // the failure this guard exists to catch — so the check is narrowed to that one name
        // rather than dropped.
        foreach (var child in root.ChildNodes.OfType<XmlElement>())
            Assert.True(child.LocalName == "Methods",
                $"the runner's projection carries a <{child.LocalName}> child. It derives no " +
                "triggers and no subtree other than <Methods>, so this would be BC's own " +
                "emitter output rather than the runner's derivation.");

        // Codeunit 26 declares exactly one attributed method — the IntegrationEvent
        // OnBeforeGuiAllowed, stated by SymbolReference.json with its compiler-assigned id —
        // and carries no event subscriber, so the witness clears it and the subtree is
        // derived. Asserted by VALUE rather than by presence: the id and name come from the
        // symbol file, and a projection that had started echoing BC's captured document would
        // be indistinguishable on presence alone.
        var methods = root.GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().Single();
        var method = methods.ChildNodes.OfType<XmlElement>().Single();
        Assert.Equal("Method", method.LocalName);
        Assert.Equal("550275561", method.GetAttribute("ID"));
        Assert.Equal("OnBeforeGuiAllowed", method.GetAttribute("Name"));
        Assert.Equal(
            "EventPublisherAttribute",
            method.GetElementsByTagName("MethodAttributes", MetaNs).OfType<XmlElement>()
                .Single().ChildNodes.OfType<XmlElement>().Single().LocalName);
    }

    /// <summary>
    /// A codeunit declaring no <c>TableNo</c> omits the attribute rather than writing 0.
    /// BC's emitter omits it too — 12 of System Application's 533 documents carry it — so
    /// writing a 0 would state a value where BC states absence.
    /// </summary>
    [SkippableFact]
    public void A_codeunit_declaring_no_TableNo_omits_the_attribute_rather_than_writing_zero()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceBundleGate.RequireBundles();
        var bundle = bundles.First(b => b.AppName == "System Application");
        var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
        Skip.If(app is null, "the System Application .app for this bundle is not on this box.");
        RecordPatches.AddBcAppPath(app!);

        var without = new XmlDocument();
        without.LoadXml(RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(26)!);
        Assert.False(without.DocumentElement!.HasAttribute("TableNo"));

        // Codeunit 8888 "Email Dispatcher" declares TableNo = 8888 in BC's own document, so the
        // omission above is a property of that codeunit rather than of the projection.
        var with = new XmlDocument();
        with.LoadXml(RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(8888)!);
        Assert.Equal("8888", with.DocumentElement!.GetAttribute("TableNo"));
    }
}
