// MetadataEquivalenceReportEnumPermissionSetOracleTests — pins that the four BC readers this
// harness uses as oracles for Report, PermissionSet, Enum and MetadataRuntimeDeltas actually
// READ the document they are handed.
//
// Issue #3782, steps 5-8. The reason this class exists is step 1's finding, and it is not
// hypothetical: BC's Types assembly ships PageDefinition AND MetaPageDefinition, both public,
// both taking (XmlNode), and NEITHER throws — the Meta one silently ignores the document and
// returns a default-constructed object. Handing that to the differ as both sides compared 235
// pages, found 0 differences, and left every other test in the suite green.
//
// Every reader here has the same trap available to it: MetaEnum has a parameterless constructor
// alongside its (XmlNode) one, MetaReport's five-argument constructor takes two nullable
// delegates, and MetaPermissionSet and NavAppObjectMetadataRuntimeDeltas are built through static
// factories rather than constructors at all. So "it did not throw" is worth nothing, and each is
// asserted to reproduce values that are IN the document — against the document itself rather than
// against a literal, so the assertions stay true on every BC build and every app.
//
// AND ONE OF THEM IS IN A THIRD ASSEMBLY. Every oracle but the deltas one comes from
// Microsoft.Dynamics.Nav.Types or .Ncl, and MetadataRuntimeDeltas was first reported as having no
// reader anywhere on the strength of a census over exactly those two. It is in
// Microsoft.Dynamics.Nav.Apps.dll, one of 501 assemblies in the artifact directory. A negative
// about BC's surface is only as wide as the search behind it.

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
    public void NavAppObjectMetadataRuntimeDeltas_FromXml_parses_every_emitted_deltas_document()
    {
        // This kind was first reported as having NO BC reader at all, and the census behind that
        // claim searched two assemblies — Types and Ncl — because every other oracle in this
        // harness comes from one of them. The reader is in a THIRD:
        // Microsoft.Dynamics.Nav.Apps.dll. Re-measured over the whole artifact directory, 501
        // assemblies carry 308 types whose name contains "Delta", 54 of them in that one.
        //
        // So the assertion here is not decoration: it is the check that turns "I did not find a
        // reader" into "this document parses", and it is asserted over EVERY document rather
        // than a sample, because the first bare probe of it parsed only 6 of 11 — a
        // WindowsLanguageHelper static-init fault from loading outside the harness, which the
        // skeleton this collection already has removes. Verified rather than assumed: 11 of 11.
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

        var apps = System.Reflection.Assembly.LoadFrom(Path.Combine(
            AlRunner.Infrastructure.BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.Apps.dll"));
        var t = apps.GetType("Microsoft.Dynamics.Nav.Apps.MetadataDeltas.NavAppObjectMetadataRuntimeDeltas");
        Assert.True(t is not null,
            "NavAppObjectMetadataRuntimeDeltas is gone from Microsoft.Dynamics.Nav.Apps.dll — the "
            + "MetadataRuntimeDeltas oracle has moved or been removed; re-measure before trusting "
            + "either side of that comparison.");

        // The root element BC's own reader keys on, asserted against the documents rather than
        // against a literal: this is what makes "the oracle is for THIS document shape" a
        // measurement instead of an assumption.
        var baseType = apps.GetType("Microsoft.Dynamics.Nav.Apps.MetadataDeltas.NavAppObjectMetadataDeltaBase")!;
        var rootName = baseType.GetProperty("MetadataRuntimeDeltasXName",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!.ToString();
        Assert.Equal("{urn:schemas-microsoft-com:dynamics:NAV:MetaObjects}MetadataRuntimeDeltas", rootName);

        var fromXml = t!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "FromXml"
                                 && m.GetParameters() is { Length: 1 } ps
                                 && ps[0].ParameterType == typeof(System.Xml.Linq.XDocument));
        Assert.True(fromXml is not null, "NavAppObjectMetadataRuntimeDeltas has no static FromXml(XDocument).");

        var seen = 0;
        var withDeltas = 0;
        foreach (var bundle in bundles)
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "MetadataRuntimeDeltas"))
            {
                var doc = System.Xml.Linq.XDocument.Load(Path.Combine(bundle.Directory, obj.File));
                Assert.Equal(rootName, doc.Root!.Name.ToString());

                var parsed = fromXml!.Invoke(null, new object?[] { doc });
                Assert.True(parsed is not null,
                    $"{bundle.Label} MetadataRuntimeDeltas {obj.Id} '{obj.Name}': FromXml returned null.");
                seen++;

                var all = (System.Collections.IEnumerable)parsed!.GetType()
                    .GetProperty("AllDeltas", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)!
                    .GetValue(parsed)!;
                if (all.Cast<object>().Any()) withDeltas++;
            }

        Assert.True(seen > 0, "no bundle carried a MetadataRuntimeDeltas document, so this measured nothing.");

        // Parsing without throwing is what MetaPageDefinition also did. The claim that separates
        // a real reader from a default object is that it read CONTENT out of the document — so
        // at least one document must yield a non-empty AllDeltas. Measured on BC
        // 28.1.49838.53910: 6 of the 11 carry deltas (12 on page 774, 5 on 4318, 4 on 2515, 2 on
        // 324, 1 on 9862) and 5 are genuinely empty <MetadataRuntimeDeltas/> elements, so a
        // floor rather than an equality — but a floor of zero would be the vacuous claim.
        Assert.True(withDeltas > 0,
            $"{seen} MetadataRuntimeDeltas document(s) parsed and NOT ONE yielded a delta. Several "
            + "of these documents plainly carry ControlAdd/ActionAdd/Expression content, so zero "
            + "means FromXml stopped reading the document — the MetaPageDefinition failure mode.");
    }

    [SkippableFact]
    public void Enum_values_are_paired_by_ordinal_and_not_by_position()
    {
        // Found by mutation-checking this PR's own work: deleting EnumDiffOptions from the
        // comparison changes NOT ONE difference count, so every other test in this file and in
        // MetadataEquivalenceHarnessTests stays green without the pairing. The allowlist is
        // keyed on DeclaringType.Member, and positional pairing reports the same MEMBERS — it
        // moves the PATHS. So the allowlist structurally cannot see this, and without this test
        // the option would be inert cover of exactly the kind #3802 documents.
        //
        // Measured on BC 28.1.49838.53910 over both bundles: with the option, 2,678 of the 3,155
        // enum-value differences carry an `id=` path and 477 do not; without it, all 3,155 are
        // positional. The assertion is on the SHAPE — that a substantial majority pair by
        // ordinal — rather than on either number, so it cannot go inert when the build moves.
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

        var paired = 0;
        var positional = 0;
        foreach (var bundle in bundles)
        {
            var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
            Skip.If(app is null, $"{bundle.Label}: its .app is not on this box.");

            foreach (var d in MetadataEquivalenceHarness.Compare(bundle, app!).Differences)
            {
                if (!d.ObjectKey.StartsWith("Enum ", StringComparison.Ordinal)) continue;
                var at = d.Path.IndexOf("Values[", StringComparison.Ordinal);
                if (at < 0) continue;
                if (d.Path.AsSpan(at + "Values[".Length).StartsWith("id=")) paired++;
                else positional++;
            }
        }

        Assert.True(paired + positional > 0,
            "no enum-value difference was reported at all, so this measured nothing — either the "
            + "enum comparison stopped running or the derivation became perfect. Both need saying "
            + "out loud rather than passing quietly.");

        // The majority pair by ordinal. The positional remainder is not a gap in the option: it
        // is enum 2616, whose runner-side ordinals contain a duplicate (#3805) so TryPairById
        // refuses the key set and falls back — which is why the residue is asserted as a
        // MINORITY rather than as zero, and why fixing #3805 should move it to zero.
        Assert.True(paired > positional,
            $"{paired} enum-value difference(s) paired by ordinal and {positional} by position. "
            + "Ordinal pairing is what stops BC's name-ordered value list being compared against "
            + "the runner's declaration-ordered one element by element, so a positional majority "
            + "means EnumDiffOptions stopped being applied.");
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
