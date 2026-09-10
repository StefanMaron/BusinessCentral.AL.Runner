// MetadataEquivalenceReportEnumPermissionSetOracleTests — pins that the three BC types this
// harness uses as oracles for Report, PermissionSet and Enum actually READ the document they
// are handed.
//
// Issue #3782, steps 5-7. The reason this class exists is step 1's finding, and it is not
// hypothetical: BC's Types assembly ships PageDefinition AND MetaPageDefinition, both public,
// both taking (XmlNode), and NEITHER throws — the Meta one silently ignores the document and
// returns a default-constructed object. Handing that to the differ as both sides compared 235
// pages, found 0 differences, and left every other test in the suite green.
//
// Every type here has the same trap available to it: MetaEnum has a parameterless constructor
// alongside its (XmlNode) one, MetaReport's five-argument constructor takes two nullable
// delegates, and MetaPermissionSet is built through a static factory rather than a constructor
// at all. So "it did not throw" is worth nothing, and each is asserted to reproduce values that
// are IN the document — against the document itself rather than against a literal, so the
// assertions stay true on every BC build and every app.

using System.Reflection;
using System.Xml;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class MetadataEquivalenceReportEnumPermissionSetOracleTests
{
    private readonly BcEngineFixture _engine;

    public MetadataEquivalenceReportEnumPermissionSetOracleTests(BcEngineFixture engine) => _engine = engine;

    private static Type Resolve(string name)
        => Type.GetType($"Microsoft.Dynamics.Nav.Types.Metadata.{name}, Microsoft.Dynamics.Nav.Types")
           ?? throw new InvalidOperationException($"{name} is not reachable.");

    /// <summary>
    /// The first emitter document of a kind, or a skip. Deliberately a real emitter document
    /// rather than a hand-written one: a fixture written here could differ from what BC emits
    /// in exactly the way that makes a non-reading type look adequate.
    /// </summary>
    private (XmlDocument Doc, string Label) EmitterDocument(string kind, Func<XmlDocument, bool>? accept = null)
    {
        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0,
            "no metadata ground-truth bundle for this BC build; " +
            "tools/gen-metadata-ground-truth.sh --artifacts \"" +
            AlRunner.Infrastructure.BcArtifacts.ServiceTierDir + "\"");

        foreach (var bundle in bundles)
            foreach (var obj in bundle.Objects.Where(o => o.Kind == kind).OrderBy(o => o.Id))
            {
                var doc = new XmlDocument();
                doc.Load(Path.Combine(bundle.Directory, obj.File));
                if (accept is not null && !accept(doc)) continue;
                return (doc, $"{bundle.Label} {kind} {obj.Id} '{obj.Name}'");
            }

        throw new SkipException($"no bundle on this box carries a {kind} document.");
    }

    private static int DocumentId(XmlDocument doc)
    {
        var root = doc.DocumentElement!;
        var attribute = root.GetAttribute("ID");
        if (!string.IsNullOrEmpty(attribute)) return int.Parse(attribute);
        // Report, Query and XmlPort state the id as a CHILD element; every other kind as an
        // attribute. Both spellings are BC's, which is what the generator's ClassifyDocument
        // learned in #3782 steps 3-5.
        foreach (XmlNode child in root.ChildNodes)
            if (child is XmlElement e && (e.Name == "ID" || e.Name == "Id"))
                return int.Parse(e.InnerText);
        throw new InvalidOperationException("the document states no id in either spelling.");
    }

    [SkippableFact]
    public void MetaReport_parses_the_emitters_own_document()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        var (doc, label) = EmitterDocument("Report");
        var t = Resolve("MetaReport");
        var ctor = t.GetConstructors().FirstOrDefault(
            c => c.GetParameters() is { Length: 5 } ps && ps[0].ParameterType == typeof(XmlElement));
        Assert.True(ctor is not null, $"{label}: MetaReport has no (XmlElement, …) constructor.");

        var parsed = ctor!.Invoke(new object?[] { doc.DocumentElement, null, 0, 0, null });

        // Every one of these is stated by the document, so a reader that looked at it at all
        // reproduces them — and every one of them is a DEFAULT (0, null, false) on an object
        // that did not.
        Assert.Equal(DocumentId(doc), (int)t.GetProperty("Id")!.GetValue(parsed)!);
        Assert.Equal(doc.DocumentElement!["Name"]!.InnerText, (string?)t.GetProperty("Name")!.GetValue(parsed));
        // ALNamespace and TransactionType are stated as elements and are neither 0 nor null on a
        // reader that parsed. Asserted against the document, not against a literal.
        Assert.Equal(doc.DocumentElement["ALNamespace"]!.InnerText,
            (string?)t.GetProperty("ALNamespace")!.GetValue(parsed));
        Assert.Equal(doc.DocumentElement["TransactionType"]!.InnerText,
            t.GetProperty("TransactionType")!.GetValue(parsed)!.ToString());

        // ...and the document plainly carries all of it, so the assertions above cannot pass
        // vacuously against an empty document.
        Assert.NotEqual(0, DocumentId(doc));
        Assert.NotEmpty(doc.DocumentElement["Name"]!.InnerText);
    }

    [SkippableFact]
    public void MetaPermissionSet_Create_parses_the_emitters_own_document()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        // A set that actually declares includes, so the collection assertion below measures
        // something: an empty IncludedPermissionSets is what a non-reading factory also answers.
        var (doc, label) = EmitterDocument("PermissionSet",
            d => !string.IsNullOrEmpty(d.DocumentElement!.GetAttribute("IncludedPermissionSets")));
        var t = Resolve("MetaPermissionSet");
        var create = t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        Assert.True(create is not null, $"{label}: MetaPermissionSet has no static Create.");

        var parsed = create!.Invoke(null, new object?[] { doc.DocumentElement, 0, 0 })!;

        Assert.Equal(DocumentId(doc), (int)t.GetProperty("Id")!.GetValue(parsed)!);
        Assert.Equal(doc.DocumentElement!.GetAttribute("ALNamespace"),
            (string?)t.GetProperty("ALNamespace")!.GetValue(parsed));

        // BC UPPERCASES the name it reads. That is the factory's own behaviour, not the
        // document's — measured on BC 28.1.49838.53910, where 170 of 178 emitted permission-set
        // documents state a mixed-case Name and MetaPermissionSet.Create answers all 178
        // upper-cased. Asserting it here is what makes the runner-side difference in the
        // allowlist attributable to the READER rather than to the runner.
        var documentName = doc.DocumentElement.GetAttribute("Name");
        Assert.Equal(documentName.ToUpperInvariant(), (string?)t.GetProperty("Name")!.GetValue(parsed));

        var included = (System.Collections.ICollection)t.GetProperty("IncludedPermissionSets")!.GetValue(parsed)!;
        Assert.Equal(
            doc.DocumentElement.GetAttribute("IncludedPermissionSets").Split(',').Length,
            included.Count);
    }

    [SkippableFact]
    public void MetaEnum_parses_the_emitters_own_document()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        // A base enum, not an enumextension: the two share the <Enum> root and an extension
        // states bare <Value> children, so accepting the first document of the kind could land
        // on one and measure a different shape than the harness compares.
        var (doc, label) = EmitterDocument("Enum",
            d => d.DocumentElement!.ChildNodes.Cast<XmlNode>().Any(c => c.Name == "Values"));
        var t = Resolve("MetaEnum");
        var ctor = t.GetConstructor(new[] { typeof(XmlNode) });
        Assert.True(ctor is not null, $"{label}: MetaEnum has no (XmlNode) constructor.");

        var parsed = ctor!.Invoke(new object?[] { doc.DocumentElement })!;

        Assert.Equal(DocumentId(doc), (int)t.GetProperty("Id")!.GetValue(parsed)!);
        Assert.Equal(doc.DocumentElement!.GetAttribute("Name"), (string?)t.GetProperty("Name")!.GetValue(parsed));
        Assert.Equal(doc.DocumentElement.GetAttribute("ALNamespace"),
            (string?)t.GetProperty("ALNamespace")!.GetValue(parsed));

        // Values is what the whole enum comparison rests on, and it is an EMPTY immutable array
        // on an object that ignored the document.
        var values = t.GetProperty("Values")!.GetValue(parsed)!;
        var length = (int)values.GetType().GetProperty("Length")!.GetValue(values)!;
        var declared = doc.DocumentElement["Values"]!.ChildNodes.Cast<XmlNode>().Count(n => n.Name == "Value");
        Assert.Equal(declared, length);
        Assert.True(declared > 0, $"{label}: the document declares no value, so this measured nothing.");
    }

    [SkippableFact]
    public void MetaEnum_parameterless_reads_NOTHING_and_the_harness_does_not_use_it()
    {
        // The trap in the shape it is available here. MetaPageDefinition was a SECOND TYPE;
        // MetaEnum's is a second CONSTRUCTOR on the same type, which is easier to reach for by
        // accident and produces the identical silent-empty comparison.
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        var (doc, label) = EmitterDocument("Enum",
            d => d.DocumentElement!.ChildNodes.Cast<XmlNode>().Any(c => c.Name == "Values"));
        var t = Resolve("MetaEnum");

        var empty = t.GetConstructor(Type.EmptyTypes)!.Invoke(null);
        Assert.Equal(0, (int)t.GetProperty("Id")!.GetValue(empty)!);
        Assert.Null(t.GetProperty("Name")!.GetValue(empty));

        // ...while the document plainly carries both, so the emptiness above is the
        // constructor's and not the document's.
        Assert.NotEqual(0, DocumentId(doc));
        Assert.NotEmpty(doc.DocumentElement!.GetAttribute("Name"));
    }

    [SkippableFact]
    public void The_harness_reads_each_document_through_a_type_that_parses()
    {
        // Ties the tests above to the thing they are about. Without this they document facts
        // about BC and say nothing about which route this repository picked.
        //
        // Asserted against the harness's OWN report rather than by re-reading the source: were
        // any of the three oracles switched to a non-reading one, both sides would come out
        // empty and identical and the kind would report differences of ZERO. That is precisely
        // the state this class exists to make unreachable, and it is observable from the report
        // without naming a type at all.
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var differing = new Dictionary<string, int>(StringComparer.Ordinal);
        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

        foreach (var bundle in bundles)
        {
            var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
            Skip.If(app is null, $"{bundle.Label}: its .app is not on this box.");
            var report = MetadataEquivalenceHarness.Compare(bundle, app!);

            foreach (var (kind, prefix) in new[] { ("Report", "Report "), ("PermissionSet", "PermissionSet "), ("Enum", "Enum ") })
            {
                seen[kind] = seen.GetValueOrDefault(kind) + bundle.Census.GetValueOrDefault(kind);
                differing[kind] = differing.GetValueOrDefault(kind)
                    + report.Differences.Count(d => d.ObjectKey.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        foreach (var kind in new[] { "Report", "PermissionSet", "Enum" })
        {
            Assert.True(seen[kind] > 0, $"no bundle carried a {kind}, so this measured nothing.");
            // The runner derives a deliberate SUBSET of each of these documents — the three
            // #3806/#3807/#3808 gap issues say exactly which members — so SOME difference is
            // guaranteed for as long as that stays true. Zero here does not mean the derivation
            // became perfect; it means the oracle stopped parsing.
            Assert.True(differing[kind] > 0,
                $"{seen[kind]} {kind} object(s) compared and NOT ONE difference was found. The " +
                "runner derives only part of each of these documents, so zero differences means " +
                "the oracle stopped parsing — the MetaPageDefinition failure mode this class " +
                "pins. If the derivation genuinely became complete, delete this assertion in the " +
                "same change that made it true, and say so.");
        }
    }
}
