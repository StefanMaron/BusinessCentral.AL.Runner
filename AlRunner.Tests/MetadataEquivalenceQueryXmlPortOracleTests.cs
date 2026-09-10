// MetadataEquivalenceQueryXmlPortOracleTests — pins that the two BC types the harness uses as
// oracles for Query and XmlPort actually READ the emitter's document, for the reason
// MetadataEquivalencePageOracleTests exists: a reader that accepts a document and ignores it
// produces a green comparison over an unrun measurement, and every other test in the harness
// stays green because they all measure DIFFERENCES.
//
// Issue #3782, steps 3 and 4. Step 1 lost a cycle to MetaPageDefinition, which is public, takes
// an XmlNode, does not throw, and returns a default object — 235 pages compared, 0 differences.
// So no type is trusted here on its signature; each is measured against a real emitter document
// and pinned.
//
// What each of the two needed checking for is DIFFERENT, and that is why both are here:
//
//   MetaQuery(XmlNode, int, int)          — the same shape as the trap. Proven to parse.
//   MetaXmlPort(XmlDocument, …, …, …, …)  — takes an XmlDocument and two DELEGATES, both of
//                                           which the harness passes null. That null is the
//                                           thing to check: if it silently emptied the object,
//                                           the comparison would be empty-against-empty.
//
// Every assertion is against the DOCUMENT rather than a literal, so nothing here goes inert or
// wrong when the BC build moves.

using System.Reflection;
using System.Xml;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class MetadataEquivalenceQueryXmlPortOracleTests
{
    private readonly BcEngineFixture _engine;

    public MetadataEquivalenceQueryXmlPortOracleTests(BcEngineFixture engine) => _engine = engine;

    private static Type Resolve(string name)
        => Type.GetType($"Microsoft.Dynamics.Nav.Types.Metadata.{name}, Microsoft.Dynamics.Nav.Types")
           ?? throw new InvalidOperationException($"{name} is not reachable.");

    /// <summary>
    /// Every document of one kind from the bundles on this box, in id order. Real emitter
    /// output rather than a hand-written fixture: a fixture I wrote could differ from what BC
    /// emits in exactly the way that makes a non-parsing type look adequate.
    /// </summary>
    private List<(XmlDocument Doc, int Id, string Label)> EmitterDocuments(string kind)
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0,
            "no metadata ground-truth bundle for this BC build; " +
            "tools/gen-metadata-ground-truth.sh --artifacts \"" +
            AlRunner.Infrastructure.BcArtifacts.ServiceTierDir + "\"");

        var found = new List<(XmlDocument, int, string)>();
        foreach (var bundle in bundles)
            foreach (var obj in bundle.Objects.Where(o => o.Kind == kind).OrderBy(o => o.Id))
            {
                var doc = new XmlDocument();
                doc.Load(Path.Combine(bundle.Directory, obj.File));
                found.Add((doc, obj.Id, $"{bundle.Label} {kind} {obj.Id} '{obj.Name}'"));
            }

        // Only System Application carries either kind, so a box whose bundles are Business
        // Foundation only is a legitimate skip rather than a failure.
        Skip.If(found.Count == 0, $"no bundle on this box carries a {kind} document.");
        return found;
    }

    /// <summary>The direct child element's text, which is where these two kinds state their
    /// scalars — unlike MetaTable and PageDefinition, which use root attributes.</summary>
    private static string? Child(XmlDocument doc, string name)
    {
        foreach (XmlNode c in doc.DocumentElement!.ChildNodes)
            if (c is XmlElement e && e.Name == name) return e.InnerText;
        return null;
    }

    [SkippableFact]
    public void MetaQuery_parses_the_emitters_own_document()
    {
        var documents = EmitterDocuments("Query");
        var t = Resolve("MetaQuery");
        var ctor = t.GetConstructor(new[] { typeof(XmlNode), typeof(int), typeof(int) });
        Assert.True(ctor is not null,
            "MetaQuery has no (XmlNode, int, int) constructor — the harness's oracle for Query " +
            "has changed shape. Re-measure before trusting it.");

        foreach (var (doc, id, label) in documents)
        {
            var parsed = ctor!.Invoke(new object?[] { doc.DocumentElement, 0, 0 })!;

            // Read back from the document, never a literal, so this holds on any BC build.
            Assert.Equal(id, (int)t.GetProperty("Id")!.GetValue(parsed)!);
            Assert.Equal(Child(doc, "Name"), (string?)t.GetProperty("Name")!.GetValue(parsed));

            // DataItems is what the comparison actually rests on — every column, its
            // compiler-assigned id and its FieldNo hang off it. A reader that filled the two
            // scalars and left this empty would still be useless as an oracle.
            var dataItems = t.GetProperty("DataItems")!.GetValue(parsed) as System.Collections.ICollection;
            Assert.True(dataItems is { Count: > 0 },
                $"{label}: MetaQuery parsed the id and name but produced no DataItems. Every " +
                "query in the bundle declares at least one, so an empty list means the reader " +
                "stopped at the top level and the column comparison is measuring nothing.");
        }
    }

    [SkippableFact]
    public void MetaQuery_discriminates_between_two_different_documents()
    {
        // The half that catches the MetaPageDefinition failure mode directly: a type returning
        // a default object answers the SAME thing for every document, and the test above would
        // only catch that if the default happened to disagree with the document. Here two real
        // documents must produce two different answers.
        var documents = EmitterDocuments("Query");
        Skip.If(documents.Count < 2, "need two Query documents to compare answers.");

        var t = Resolve("MetaQuery");
        var ctor = t.GetConstructor(new[] { typeof(XmlNode), typeof(int), typeof(int) })!;

        var ids = new HashSet<int>();
        var names = new HashSet<string?>();
        foreach (var (doc, _, _) in documents)
        {
            var parsed = ctor.Invoke(new object?[] { doc.DocumentElement, 0, 0 })!;
            ids.Add((int)t.GetProperty("Id")!.GetValue(parsed)!);
            names.Add((string?)t.GetProperty("Name")!.GetValue(parsed));
        }

        Assert.True(ids.Count == documents.Count,
            $"{documents.Count} distinct Query documents produced {ids.Count} distinct Id(s). A " +
            "reader that ignores the document answers one constant for all of them — the " +
            "MetaPageDefinition failure mode this class exists to make unreachable.");
        Assert.True(names.Count == documents.Count,
            $"{documents.Count} distinct Query documents produced {names.Count} distinct Name(s).");
    }

    [SkippableFact]
    public void MetaXmlPort_parses_the_emitters_own_document_with_both_delegates_null()
    {
        // The null delegates are the point. MetaXmlPort's signature is
        // (XmlDocument, MetaReport.CreateRequestForm, int, int, RemoveItemsOnPage…), and the
        // harness passes null for both callbacks. BC's own body guards the only use with
        // `if (createRequestForm != null && val != null)`, so a null leaves RequestFormMetadata
        // unbuilt on BOTH sides and cannot skew the comparison — but that is a claim about a
        // decompiled body, and this is the measurement of it.
        var documents = EmitterDocuments("XmlPort");
        var t = Resolve("MetaXmlPort");
        var ctor = t.GetConstructors()
            .FirstOrDefault(c => c.GetParameters() is { Length: 5 } ps
                                 && ps[0].ParameterType == typeof(XmlDocument));
        Assert.True(ctor is not null,
            "MetaXmlPort has no (XmlDocument, …) constructor — the harness's oracle for XmlPort " +
            "has changed shape. Re-measure before trusting it.");

        foreach (var (doc, id, label) in documents)
        {
            var parsed = ctor!.Invoke(new object?[] { doc, null, 0, 0, null })!;

            Assert.Equal(id, (int)t.GetProperty("Id")!.GetValue(parsed)!);
            Assert.Equal(Child(doc, "Name"), (string?)t.GetProperty("Name")!.GetValue(parsed));

            // Nodes is the whole xmlport comparison: 182 of the 306 differences step 4 measured
            // are node presence. A reader producing none would report the runner as agreeing
            // with BC about a tree neither side has.
            var nodes = t.GetProperty("Nodes")!.GetValue(parsed) as System.Collections.ICollection;
            var declared = doc.DocumentElement!.ChildNodes.Cast<XmlNode>().Count(n => n.Name == "Node");
            Assert.True(nodes is not null && nodes.Count == declared,
                $"{label}: the document declares {declared} <Node> element(s) and MetaXmlPort " +
                $"produced {nodes?.Count.ToString() ?? "<null>"}. The node tree is what the " +
                "xmlport comparison measures, so a mismatch here means the oracle is not " +
                "reading the document this harness is comparing.");
        }
    }

    [SkippableFact]
    public void MetaXmlPort_refuses_a_document_it_does_not_understand()
    {
        // Why this kind cannot fail the way pages did, pinned rather than asserted in prose.
        // MetaXmlPort's body is a switch over the uppercased child element name that ends in
        // `throw new ArgumentException(name)` for anything it does not know — so it cannot
        // silently ignore a document. That property is also what the runner-side projection
        // depends on: RecordPatches.TryBuildXmlPortMetadataEquivalenceXml may only write element
        // names this reader accepts, and a typo there would throw rather than be dropped.
        EmitterDocuments("XmlPort");   // for the skip conditions only
        var t = Resolve("MetaXmlPort");
        var ctor = t.GetConstructors()
            .First(c => c.GetParameters() is { Length: 5 } ps
                        && ps[0].ParameterType == typeof(XmlDocument));

        var doc = new XmlDocument();
        var root = doc.CreateElement("XmlPort");
        doc.AppendChild(root);
        var bogus = doc.CreateElement("NotAnXmlPortMemberAtAll");
        bogus.InnerText = "x";
        root.AppendChild(bogus);

        var ex = Assert.ThrowsAny<Exception>(() => ctor.Invoke(new object?[] { doc, null, 0, 0, null }));
        var real = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
        Assert.True(real is ArgumentException,
            "MetaXmlPort accepted an element it does not know instead of throwing " +
            $"ArgumentException (it threw {real.GetType().Name}). The runner-side projection " +
            "relies on that refusal to catch a mis-spelled element name, so if this changed, " +
            "the projection needs its own validation.");
    }

    [SkippableFact]
    public void The_harness_reads_both_documents_through_types_that_parse()
    {
        // Ties the tests above to the harness itself, the way the page class does: if either
        // oracle were swapped for a non-parsing type, both sides would come out empty and
        // identical and the kind would report objects compared with ZERO differences.
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

        var seen = new Dictionary<string, int> { ["Query"] = 0, ["XmlPort"] = 0 };
        var differences = new Dictionary<string, int> { ["Query"] = 0, ["XmlPort"] = 0 };

        foreach (var bundle in bundles)
        {
            var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
            Skip.If(app is null, $"{bundle.Label}: its .app is not on this box.");

            var report = MetadataEquivalenceHarness.Compare(bundle, app!);
            foreach (var kind in seen.Keys.ToArray())
            {
                seen[kind] += report.Bundle.Census.GetValueOrDefault(kind);
                var prefix = kind == "Query" ? "Query " : "XmlPort ";
                differences[kind] += report.Differences
                    .Count(d => d.ObjectKey.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        Skip.If(seen["Query"] == 0 && seen["XmlPort"] == 0,
            "no bundle on this box carries a Query or XmlPort; nothing to measure.");

        foreach (var kind in new[] { "Query", "XmlPort" })
        {
            if (seen[kind] == 0) continue;
            Assert.True(differences[kind] > 0,
                $"{seen[kind]} {kind}(s) compared and NOT ONE difference was found. The runner " +
                $"derives a strict subset of what BC emits for this kind (#3798 for Query, " +
                "#3797 for XmlPort), so zero differences means the oracle stopped parsing — the " +
                "MetaPageDefinition failure mode. If a derivation fix genuinely closed the whole " +
                "gap, delete this assertion in the same change that made it true, and say so.");
        }
    }
}
