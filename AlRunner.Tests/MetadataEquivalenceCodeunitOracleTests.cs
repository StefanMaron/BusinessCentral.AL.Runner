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
    /// <para>What the projection states is therefore exactly the five values the runner derives
    /// from SymbolReference.json, and it must NOT state the members BC's emitter adds. Asserting
    /// their absence is what pins the two sides apart.</para>
    /// </summary>
    [SkippableFact]
    public void The_runner_side_states_only_what_the_runner_derives()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

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

        // Present: the five the runner genuinely derives.
        Assert.Equal("26", root.GetAttribute("ID"));
        Assert.Equal("Confirm Management Impl.", root.GetAttribute("Name"));
        Assert.Equal("1", root.GetAttribute("SingleInstance"));
        Assert.Equal("Normal", root.GetAttribute("Subtype"));

        // Absent: everything only BC's emitter knows. If any of these appears, the runner side
        // has started carrying BC's own answers and the comparison has stopped measuring.
        foreach (var emitterOnly in new[]
                 { "ALNamespace", "InherentPermissions", "InherentEntitlements",
                   "TestIsolation", "EventSubscriberInstance", "MetadataVersion" })
            Assert.False(root.HasAttribute(emitterOnly),
                $"the runner's projection states '{emitterOnly}', which only BC's emitter " +
                "derives. Stating it would manufacture agreement with the ground truth and " +
                "turn a real gap (#3788) into a silent pass.");

        Assert.False(root.HasChildNodes,
            "the runner's projection carries a child element. It derives no methods and no " +
            "triggers, so a subtree here would be BC's own emitter output rather than the " +
            "runner's derivation.");
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

        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");
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
