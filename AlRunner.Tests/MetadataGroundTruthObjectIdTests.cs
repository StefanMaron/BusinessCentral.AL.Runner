// MetadataGroundTruthObjectIdTests — every object in a ground-truth bundle states a real id.
//
// Issue #3782, steps 3 and 4. The generator's ClassifyDocument read the id from the root
// ATTRIBUTE only, and BC's emitter uses two spellings: Query, XmlPort and Report state it as a
// direct child <ID> ELEMENT, every other kind as an attribute. So all 12 of those documents in
// a System Application bundle carried Id = 0 — a value that keys nothing, is not unique, and
// reads exactly like a real id.
//
// Why this is a test rather than a comment on the fix. The harness keys its comparison on
// (kind, id), so a whole kind reporting 0 would have collapsed 7 queries onto one object key
// with no error anywhere; the generator's own file naming had already worked around the same
// fact for filenames, which is how it survived unnoticed. The bundle is the artifact every
// later step reads, so the invariant belongs on the bundle.

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class MetadataGroundTruthObjectIdTests
{
    private readonly BcEngineFixture _engine;

    public MetadataGroundTruthObjectIdTests(BcEngineFixture engine) => _engine = engine;

    private IReadOnlyList<GroundTruthBundle> Bundles()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        var bundles = MetadataEquivalenceBundleGate.RequireBundles();
        return bundles;
    }

    [SkippableFact]
    public void Every_object_in_a_bundle_states_a_non_zero_id()
    {
        // Deliberately over ALL kinds, not just the ones compared today. The next steps of
        // #3782 add Report, PermissionSet, Enum and MetadataRuntimeDeltas, and Report is the
        // third kind that states its id as a child element — so this fails for step 5 before
        // step 5 is written, rather than after.
        foreach (var bundle in Bundles())
        {
            var zero = bundle.Objects.Where(o => o.Id == 0)
                .GroupBy(o => o.Kind)
                .Select(g => $"{g.Key} x{g.Count()} (e.g. '{g.First().Name}' in {g.First().File})")
                .ToArray();

            Assert.True(zero.Length == 0,
                $"{bundle.Label}: {zero.Length} kind(s) report id 0 for every document, which " +
                "keys nothing and is not unique. BC's emitter states the id as a root ATTRIBUTE " +
                "for most kinds and as a direct child <ID> ELEMENT for Query, XmlPort and " +
                "Report; ClassifyDocument in tools/metadata-ground-truth/Program.cs must read " +
                "both. Regenerate the bundle after fixing it:" + Environment.NewLine +
                string.Join(Environment.NewLine, zero));
        }
    }

    [SkippableFact]
    public void No_two_objects_of_an_id_keyed_kind_share_an_id()
    {
        // The property the harness actually depends on. The generator asserts this at write
        // time; this asserts it on the bundle a test process is really about to read, which is
        // not the same claim on a box holding a bundle written by an older generator.
        string[] idKeyed = { "MetaTable", "PageDefinition", "CodeUnit", "Query", "XmlPort" };

        foreach (var bundle in Bundles())
        {
            var clashes = bundle.Objects
                .Where(o => idKeyed.Contains(o.Kind, StringComparer.Ordinal))
                .GroupBy(o => (o.Kind, o.Id)).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key.Kind} {g.Key.Id}: {string.Join(", ", g.Select(o => $"'{o.Name}'"))}")
                .ToArray();

            Assert.True(clashes.Length == 0,
                $"{bundle.Label}: two documents share a (kind, id) the harness keys on, so one " +
                "would be compared and the other silently dropped:" + Environment.NewLine +
                string.Join(Environment.NewLine, clashes));

            // Non-vacuity: "no clashes among zero objects" is how this passes having checked
            // nothing, which is the exact shape of the defect above.
            var keyed = bundle.Objects.Count(o => idKeyed.Contains(o.Kind, StringComparer.Ordinal));
            Assert.True(keyed > 0,
                $"{bundle.Label}: the bundle carries no object of any id-keyed kind, so this " +
                "test measured nothing. Regenerate it.");
        }
    }

    [SkippableFact]
    public void The_kinds_that_state_their_id_as_a_child_element_are_read_correctly()
    {
        // The specific regression, pinned against the DOCUMENT rather than against the manifest
        // alone — so a generator that wrote a plausible-looking wrong id (rather than 0) would
        // also be caught. Reads the id straight out of the XML and requires the manifest to
        // agree.
        foreach (var bundle in Bundles())
            foreach (var obj in bundle.Objects.Where(o => o.Kind is "Query" or "XmlPort" or "Report"))
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(Path.Combine(bundle.Directory, obj.File));

                string? stated = null;
                foreach (System.Xml.XmlNode child in doc.DocumentElement!.ChildNodes)
                    if (child is System.Xml.XmlElement e && e.Name is "ID" or "Id")
                    { stated = e.InnerText.Trim(); break; }

                Assert.True(stated is not null,
                    $"{bundle.Label} {obj.Kind} '{obj.Name}': its document has no direct child " +
                    "<ID>, so the assumption this test rests on no longer holds for this kind. " +
                    "Re-measure where BC states the id before changing ClassifyDocument.");
                Assert.Equal(int.Parse(stated!), obj.Id);
            }
    }
}
