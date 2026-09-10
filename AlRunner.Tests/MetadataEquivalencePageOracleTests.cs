// MetadataEquivalencePageOracleTests — pins WHICH BC type is the oracle for a PageDefinition
// document, because the wrong one produces a green comparison that measures nothing.
//
// Issue #3782, step 1. BC's Types assembly ships two page types with the same public shape:
//
//     Microsoft.Dynamics.Nav.Types.Metadata.PageDefinition(XmlNode)      <- parses
//     Microsoft.Dynamics.Nav.Types.Metadata.MetaPageDefinition(XmlNode)  <- does NOT parse
//
// Both are public, both take an XmlNode, and NEITHER throws on the emitter's own document. The
// Meta one ignores it: it returns a default-constructed object. Handing that to the differ as
// both sides compares nothing against nothing, and every other test in
// MetadataEquivalenceHarnessTests stays green because they all measure DIFFERENCES — and there
// are none between two empty objects. That is the exact "green tick over an unrun measurement"
// this whole harness exists to prevent, so the discrimination is asserted rather than left as a
// comment on the line that picks the type.
//
// Measured on BC 28.1.49838.53910, Business Foundation Page 257 "Source Codes". This asserts
// the SHAPE (one parses, the other does not), never the build's own numbers, so it cannot go
// inert when the build moves.

using System.Xml;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class MetadataEquivalencePageOracleTests
{
    private readonly BcEngineFixture _engine;

    public MetadataEquivalencePageOracleTests(BcEngineFixture engine) => _engine = engine;

    private static Type Resolve(string name)
        => Type.GetType($"Microsoft.Dynamics.Nav.Types.Metadata.{name}, Microsoft.Dynamics.Nav.Types")
           ?? throw new InvalidOperationException($"{name} is not reachable.");

    /// <summary>
    /// One PageDefinition document from a ground-truth bundle, or a skip. Deliberately a real
    /// emitter document rather than a hand-written one: a fixture I wrote could differ from
    /// what BC emits in exactly the way that makes the wrong type look adequate.
    /// </summary>
    private (XmlElement Root, string Label) EmitterPageDocument()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceBundleGate.RequireBundles();

        foreach (var bundle in bundles)
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "PageDefinition").OrderBy(o => o.Id))
            {
                var doc = new XmlDocument();
                doc.Load(Path.Combine(bundle.Directory, obj.File));
                return (doc.DocumentElement!, $"{bundle.Label} Page {obj.Id} '{obj.Name}'");
            }

        throw new SkipException("no bundle on this box carries a PageDefinition document.");
    }

    [SkippableFact]
    public void PageDefinition_parses_the_emitters_own_document()
    {
        var (root, label) = EmitterPageDocument();
        var t = Resolve("PageDefinition");

        var parsed = t.GetConstructor(new[] { typeof(XmlNode) })!.Invoke(new object?[] { root });

        // The id and name are in the document's own root attributes, so a reader that did
        // anything at all reproduces them. Asserting against the DOCUMENT rather than against a
        // literal keeps this true on every BC build and every app.
        Assert.Equal(int.Parse(root.GetAttribute("ID")), (int)t.GetProperty("ID")!.GetValue(parsed)!);
        Assert.Equal(root.GetAttribute("Name"), (string?)t.GetProperty("Name")!.GetValue(parsed));

        // Properties is what the whole page comparison rests on: PageType, Editable and
        // SourceObject all hang off it, and every runner consumer reads it.
        Assert.NotNull(t.GetProperty("Properties")!.GetValue(parsed));
        Assert.NotNull(t.GetProperty("Content")!.GetValue(parsed));
    }

    [SkippableFact]
    public void MetaPageDefinition_accepts_the_same_document_and_reads_NOTHING_from_it()
    {
        // The trap, asserted so it cannot be reintroduced by someone reaching for the
        // "Meta"-prefixed type by analogy with MetaTable. If BC ever fixes this constructor,
        // THIS test fails — which is the correct outcome: the harness may then use either type,
        // and the choice stops being load-bearing.
        var (root, label) = EmitterPageDocument();
        var t = Resolve("MetaPageDefinition");

        var ctor = t.GetConstructor(new[] { typeof(XmlNode) });
        Assert.True(ctor is not null,
            $"{label}: MetaPageDefinition no longer has an (XmlNode) constructor, so the " +
            "asymmetry this test pins has changed shape. Re-measure before trusting either type.");

        object parsed;
        try
        {
            parsed = ctor!.Invoke(new object?[] { root })!;
        }
        catch (Exception ex)
        {
            // Also a fine outcome, and NOT this test's claim: a constructor that throws is
            // loud, and nobody could have shipped a silent empty comparison with it.
            Assert.Fail(
                $"{label}: MetaPageDefinition's (XmlNode) constructor now THROWS " +
                $"({(ex.InnerException ?? ex).GetType().Name}). That is a safe failure mode " +
                "rather than the silent one this test pins, but the shape changed — re-measure.");
            return;
        }

        // This is the finding: accepted, and empty. Every one of these would be populated by a
        // reader that looked at the document at all.
        Assert.Equal(0, (int)t.GetProperty("ID")!.GetValue(parsed)!);
        Assert.Null(t.GetProperty("Name")!.GetValue(parsed));
        Assert.Null(t.GetProperty("Properties")!.GetValue(parsed));
        Assert.Null(t.GetProperty("Content")!.GetValue(parsed));

        // ...while the document plainly carries all of it. Without this half the test above
        // would pass against an empty document just as happily.
        Assert.NotEqual(0, int.Parse(root.GetAttribute("ID")));
        Assert.NotEmpty(root.GetAttribute("Name"));
    }

    [SkippableFact]
    public void The_harness_reads_the_document_through_the_type_that_parses()
    {
        // Ties the two tests above to the thing they are about. Without it they document an
        // asymmetry in BC and say nothing about which one this repository picked.
        //
        // Asserted against the harness's OWN comparison rather than by re-reading the source:
        // if the harness were switched back to MetaPageDefinition, both sides would come out
        // empty and identical, so it would report pages compared and ZERO page differences.
        // That is precisely the state this whole test class exists to make unreachable, and it
        // is observable from the report without naming a type at all.
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceBundleGate.RequireBundles();

        var pagesSeen = 0;
        var pageDifferences = 0;
        foreach (var bundle in bundles)
        {
            var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
            Skip.If(app is null, $"{bundle.Label}: its .app is not on this box.");

            var report = MetadataEquivalenceHarness.Compare(bundle, app!);
            pagesSeen += report.Bundle.Census.GetValueOrDefault("PageDefinition");
            pageDifferences += report.Differences
                .Count(d => d.ObjectKey.StartsWith("Page ", StringComparison.Ordinal));
        }

        Assert.True(pagesSeen > 0, "no bundle carried a PageDefinition, so this measured nothing.");

        // The runner reconstructs a deliberate SUBSET of a page document
        // (DependencyPageMetadataXml.cs's header lists what and why), so SOME difference is
        // guaranteed for as long as that stays true. Zero here does not mean the derivation
        // became perfect — it means the oracle stopped reading the document.
        Assert.True(pageDifferences > 0,
            $"{pagesSeen} page(s) compared and NOT ONE difference was found. The runner "
            + "reconstructs only part of a page document, so zero differences means the page "
            + "oracle stopped parsing — the MetaPageDefinition failure mode this class pins. "
            + "If the derivation genuinely became complete, delete this assertion in the same "
            + "change that made it true, and say so.");
    }
}
