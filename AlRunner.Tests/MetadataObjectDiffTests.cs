// MetadataObjectDiffTests — the differ's own contract, provable with plain C# fixtures and
// no BC service tier.
//
// The claim under test is TOTALITY: the differ must report a member it was never told about.
// So the fixtures below deliberately carry properties with names nothing in the differ (or in
// these tests' assertions) mentions, and the assertions are made against the SET of reported
// paths rather than against a hand-written list of members — a test that named the members
// would have exactly the blind spot this differ exists to remove.
//
// The real end-to-end measurement, against BC's own emitter output, is
// MetadataEquivalenceHarnessTests; it needs the in-process engine and a ground-truth bundle.

using AlRunner.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class MetadataObjectDiffTests
{
    // Namespace-prefixed so the differ's default recursion rule treats these as metadata
    // objects. The prefix is the only thing that matters; the shapes are ours.
    private const string Prefix = "AlRunner.Tests";

    private static MetadataObjectDiffOptions Opts(params string[] pairById) => new()
    {
        RecurseNamespacePrefixes = new[] { Prefix },
        PairByIdMembers = new HashSet<string>(pairById.Length == 0
            ? new[] { "FakeTable.Fields" } : pairById, StringComparer.Ordinal),
    };

    private sealed class FakeField
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public bool Editable { get; init; } = true;
        public string? Classification { get; init; }
        public FakeRelation? Relation { get; init; }
    }

    private sealed class FakeRelation
    {
        public int TargetTable { get; init; }
        public string? Condition { get; init; }
    }

    private sealed class FakeTable
    {
        public int Id { get; init; }
        public string? Caption { get; init; }
        public Scope Scope { get; init; }
        public List<FakeField> Fields { get; init; } = new();
        public List<string> CaptionFields { get; init; } = new();
    }

    private enum Scope { Cloud, OnPrem }

    private sealed class Node
    {
        public string? Name { get; set; }
        public Node? Next { get; set; }
    }

    private static FakeTable Table(params FakeField[] fields)
        => new() { Id = 5, Caption = "T", Fields = fields.ToList() };

    // ---- totality ----------------------------------------------------------------------

    [Fact]
    public void Compare_reports_a_member_the_caller_never_named()
    {
        // `Classification` appears in no argument to Compare and in no option. If the differ
        // only looked at members it was told about, this would come back empty.
        var left = Table(new FakeField { Id = 1, Name = "No.", Classification = "SystemMetadata" });
        var right = Table(new FakeField { Id = 1, Name = "No.", Classification = "CustomerContent" });

        var diff = MetadataObjectDiff.Compare(left, right, "Table 5", Opts());

        var d = Assert.Single(diff);
        Assert.Equal("Fields[id=1].Classification", d.Path);
        Assert.Equal("FakeField.Classification", d.Signature);
        Assert.Equal("SystemMetadata", d.Expected);
        Assert.Equal("CustomerContent", d.Actual);
    }

    [Fact]
    public void Compare_reports_nothing_when_every_member_agrees()
    {
        var left = Table(new FakeField { Id = 1, Name = "No.", Classification = "X" });
        var right = Table(new FakeField { Id = 1, Name = "No.", Classification = "X" });

        Assert.Empty(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
    }

    [Fact]
    public void Compare_walks_into_a_nested_metadata_object()
    {
        var left = Table(new FakeField { Id = 1, Relation = new FakeRelation { TargetTable = 2000000120 } });
        var right = Table(new FakeField { Id = 1, Relation = new FakeRelation { TargetTable = 0 } });

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal("Fields[id=1].Relation.TargetTable", d.Path);
        Assert.Equal("2000000120", d.Expected);
        Assert.Equal("0", d.Actual);
    }

    // ---- both directions ---------------------------------------------------------------

    [Fact]
    public void Compare_reports_a_value_the_RIGHT_side_states_and_the_left_does_not()
    {
        // The direction that is easy to miss and is real here: the runner carries a table
        // Caption where BC's is null (50 System Application tables).
        var left = new FakeTable { Id = 5, Caption = null };
        var right = new FakeTable { Id = 5, Caption = "Data Subjects" };

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal("Caption", d.Path);
        Assert.Equal(MetadataObjectDiff.Null, d.Expected);
        Assert.Equal("Data Subjects", d.Actual);
    }

    [Fact]
    public void Compare_reports_an_element_only_the_RIGHT_side_has()
    {
        var left = Table(new FakeField { Id = 1 });
        var right = Table(new FakeField { Id = 1 }, new FakeField { Id = 2000000000, Name = "SystemId" });

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal("Fields[id=2000000000]", d.Path);
        // The presence difference names the COLLECTION member, so an allowlist entry can cover
        // extra Fields without also covering missing FieldGroups.
        Assert.Equal("FakeTable.Fields." + MetadataObjectDiff.PresenceMember, d.Signature);
        Assert.Equal(MetadataObjectDiff.Absent, d.Expected);
        Assert.Equal("present", d.Actual);
    }

    [Fact]
    public void Compare_reports_an_element_only_the_LEFT_side_has()
    {
        var left = Table(new FakeField { Id = 1 }, new FakeField { Id = 7 });
        var right = Table(new FakeField { Id = 1 });

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal("Fields[id=7]", d.Path);
        Assert.Equal("present", d.Expected);
        Assert.Equal(MetadataObjectDiff.Absent, d.Actual);
    }

    // ---- pairing -----------------------------------------------------------------------

    [Fact]
    public void Fields_pair_by_id_so_an_extra_element_does_not_cascade()
    {
        // Positionally these two lists disagree on every element from index 0. Paired by id
        // there is exactly one difference: the element that is missing.
        var left = Table(new FakeField { Id = 1, Name = "a" }, new FakeField { Id = 2, Name = "b" });
        var right = Table(new FakeField { Id = 2, Name = "b" });

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal("Fields[id=1]", d.Path);
    }

    [Fact]
    public void Id_paired_elements_still_report_a_reordering()
    {
        // Pairing by id would otherwise HIDE order, and order is meaningful in AL.
        var left = Table(new FakeField { Id = 1 }, new FakeField { Id = 2 });
        var right = Table(new FakeField { Id = 2 }, new FakeField { Id = 1 });

        var diff = MetadataObjectDiff.Compare(left, right, "Table 5", Opts());
        Assert.Equal(2, diff.Count);
        Assert.All(diff, d => Assert.Equal("#Ordinal", d.Member));
        Assert.Contains(diff, d => d.Path == "Fields[id=1].#Ordinal" && d.Expected == "0" && d.Actual == "1");
        Assert.Contains(diff, d => d.Path == "Fields[id=2].#Ordinal" && d.Expected == "1" && d.Actual == "0");
    }

    [Fact]
    public void A_collection_not_declared_id_paired_is_compared_by_position()
    {
        var left = Table(new FakeField { Id = 1 });
        var right = Table(new FakeField { Id = 1 });
        left.CaptionFields.AddRange(new[] { "a", "b" });
        right.CaptionFields.AddRange(new[] { "a" });

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal("CaptionFields", d.Path);
        Assert.Equal("[a,b]", d.Expected);
        Assert.Equal("[a]", d.Actual);
    }

    // ---- rendering -----------------------------------------------------------------------

    [Fact]
    public void An_enum_is_a_leaf_reported_against_the_property_that_holds_it()
    {
        // Regression: recursing into an enum reported this as `Scope.#value__` — against the
        // ENUM type, not the property — so an allowlist could not name `FakeTable.Scope`, and
        // one entry would have covered every property sharing that enum type.
        var left = new FakeTable { Id = 5, Scope = Scope.OnPrem };
        var right = new FakeTable { Id = 5, Scope = Scope.Cloud };

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal("Scope", d.Path);
        Assert.Equal("FakeTable.Scope", d.Signature);
        Assert.Equal("OnPrem", d.Expected);
        Assert.Equal("Cloud", d.Actual);
    }

    [Fact]
    public void Null_and_empty_string_are_distinguishable()
    {
        var left = new FakeTable { Id = 5, Caption = null };
        var right = new FakeTable { Id = 5, Caption = "" };

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal(MetadataObjectDiff.Null, d.Expected);
        Assert.Equal("<empty>", d.Actual);
    }

    // ---- termination ---------------------------------------------------------------------

    [Fact]
    public void A_cycle_terminates_and_still_reports_the_difference_inside_it()
    {
        var left = new Node { Name = "a" };
        left.Next = left;
        var right = new Node { Name = "b" };
        right.Next = right;

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Node", Opts()));
        Assert.Equal("Name", d.Path);
    }

    [Fact]
    public void Exceeding_the_depth_limit_is_REPORTED_not_silently_truncated()
    {
        // A walk that quietly stops short is the same class of wrong answer as a test that
        // names its properties: it reports "no differences" for a region it never looked at.
        static Node Chain(int depth, string leaf)
        {
            var head = new Node { Name = "n" };
            var cur = head;
            for (int i = 0; i < depth; i++) { cur.Next = new Node { Name = "n" }; cur = cur.Next; }
            cur.Name = leaf;
            return head;
        }

        var diff = MetadataObjectDiff.Compare(
            Chain(6, "left"), Chain(6, "right"), "Node",
            new MetadataObjectDiffOptions { MaxDepth = 3, RecurseNamespacePrefixes = new[] { Prefix } });

        var d = Assert.Single(diff);
        Assert.Equal(MetadataObjectDiff.DepthMember, d.Member);
        Assert.Contains("depth", d.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void A_getter_that_throws_on_one_side_only_is_reported()
    {
        var left = new Throwing { Explode = false };
        var right = new Throwing { Explode = true };

        var diff = MetadataObjectDiff.Compare(left, right, "Throwing", Opts());
        var d = Assert.Single(diff, x => x.Path == "Value");
        Assert.Equal("ok", d.Expected);
        Assert.StartsWith("<throw:", d.Actual, StringComparison.Ordinal);
    }

    private sealed class Throwing
    {
        public bool Explode { get; init; }
        public string Value => Explode ? throw new InvalidOperationException("boom") : "ok";
    }

    [Fact]
    public void A_private_field_with_no_property_is_still_compared()
    {
        // MetaTable.fieldsById is exactly this shape: real state reachable only as a field.
        var left = new WithPrivateField(1);
        var right = new WithPrivateField(2);

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "WithPrivateField", Opts()));
        Assert.Equal("#_hidden", d.Path);
        Assert.Equal("1", d.Expected);
        Assert.Equal("2", d.Actual);
    }

    private sealed class WithPrivateField
    {
#pragma warning disable CS0414 // read reflectively; that IS the subject of the test
        private readonly int _hidden;
#pragma warning restore CS0414
        public WithPrivateField(int hidden) => _hidden = hidden;
    }

    [Fact]
    public void An_auto_property_is_reported_once_not_twice()
    {
        // Auto-property backing fields are skipped precisely so a single disagreement does
        // not arrive as two, which would double every count the allowlist ratchets on.
        var left = new FakeTable { Id = 5, Caption = "a" };
        var right = new FakeTable { Id = 5, Caption = "b" };

        Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
    }

    [Fact]
    public void Two_collection_members_on_one_type_get_DIFFERENT_presence_signatures()
    {
        // Regression: both used to arrive as `FakeTwo.<presence>`, so one allowlist entry
        // covered both — declaring one difference would have silently declared the other.
        var left = new FakeTwo { A = { new FakeField { Id = 1 } }, B = { new FakeField { Id = 2 } } };
        var right = new FakeTwo();

        var diff = MetadataObjectDiff.Compare(left, right, "Two",
            new MetadataObjectDiffOptions { RecurseNamespacePrefixes = new[] { Prefix } });

        Assert.Equal(2, diff.Count);
        Assert.Contains(diff, d => d.Signature == "FakeTwo.A.<presence>");
        Assert.Contains(diff, d => d.Signature == "FakeTwo.B.<presence>");
    }

    private sealed class FakeTwo
    {
        public List<FakeField> A { get; } = new();
        public List<FakeField> B { get; } = new();
    }

    [Fact]
    public void A_nested_member_present_on_one_side_only_names_the_MEMBER_not_its_type()
    {
        // Regression: reported as `FakeRelation.<presence>`, which cannot distinguish two
        // members of the same type on the same object.
        var left = Table(new FakeField { Id = 1, Relation = new FakeRelation { TargetTable = 7 } });
        var right = Table(new FakeField { Id = 1, Relation = null });

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Table 5", Opts()));
        Assert.Equal("FakeField.Relation.<presence>", d.Signature);
        Assert.Equal("present", d.Expected);
        Assert.Equal(MetadataObjectDiff.Null, d.Actual);
    }

    [Fact]
    public void A_dictionary_is_paired_by_KEY_not_by_position()
    {
        // Regression: MetaTable.FieldsById enumerates in insertion order, which differs
        // between the two sides, and positional pairing turned that into 74 fabricated
        // differences on Business Foundation alone.
        var left = new FakeDict { ById = { [2] = "b", [1] = "a" } };
        var right = new FakeDict { ById = { [1] = "a", [2] = "b" } };

        Assert.Empty(MetadataObjectDiff.Compare(left, right, "Dict",
            new MetadataObjectDiffOptions { RecurseNamespacePrefixes = new[] { Prefix } }));
    }

    [Fact]
    public void A_dictionary_key_present_on_one_side_only_is_reported()
    {
        var left = new FakeDict { ById = { [1] = "a" } };
        var right = new FakeDict { ById = { [1] = "a", [2] = "b" } };

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Dict",
            new MetadataObjectDiffOptions { RecurseNamespacePrefixes = new[] { Prefix } }));
        Assert.Equal("ById[key=2]", d.Path);
        Assert.Equal("FakeDict.ById.<presence>", d.Signature);
        Assert.Equal(MetadataObjectDiff.Absent, d.Expected);
    }

    private sealed class FakeDict
    {
        public Dictionary<int, string> ById { get; } = new();
    }

    // ---- id pairing works for BOTH spellings of the id property ------------------------

    // BC spells it two ways, and reflection's name lookup is CASE-SENSITIVE: MetaField has
    // `Id`, every page control type has `ID`. With only "Id" tried, TryPairById returned false
    // for page controls and the differ fell back to POSITIONAL pairing — silently, because
    // falling back is the correct outcome for a collection whose elements have no id at all.
    //
    // The cost of that silence, measured on BC 28.1.49838.53910 System Application (#3782):
    // BC's Controls list holds ordinary field controls the runner deliberately does not
    // reconstruct, so positional pairing shifted every part after the first and reported 12
    // Name/ID/PagePartID triples across 7 pages as disagreements between DIFFERENT controls —
    // e.g. page 4312 'InputMessagePart' vs 'LogsPart'. Not one was a difference about a control.

    private sealed class UpperIdElement
    {
        public int ID { get; init; }
        public string? Name { get; init; }
    }

    private sealed class UpperIdHolder
    {
        public List<UpperIdElement> Items { get; } = new();
    }

    private static MetadataObjectDiffOptions UpperOpts() => new()
    {
        RecurseNamespacePrefixes = new[] { Prefix },
        PairByIdMembers = new HashSet<string>(StringComparer.Ordinal) { "UpperIdHolder.Items" },
    };

    [Fact]
    public void Elements_whose_id_property_is_spelled_ID_still_pair_by_id()
    {
        // Positionally these disagree on every element: the left has an extra element FIRST,
        // so left[0] is 'a' against right[0] 'b', and left[1] 'b' against nothing. Paired by
        // id there is exactly one difference — the element the right side does not have.
        var left = new UpperIdHolder
        {
            Items = { new UpperIdElement { ID = 1, Name = "a" }, new UpperIdElement { ID = 2, Name = "b" } },
        };
        var right = new UpperIdHolder { Items = { new UpperIdElement { ID = 2, Name = "b" } } };

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Holder", UpperOpts()));
        Assert.Equal("Items[id=1]", d.Path);
        Assert.Equal("UpperIdHolder.Items." + MetadataObjectDiff.PresenceMember, d.Signature);
        Assert.Equal("present", d.Expected);
        Assert.Equal(MetadataObjectDiff.Absent, d.Actual);
    }

    [Fact]
    public void An_ID_spelled_collection_reports_a_real_per_element_difference_not_a_shift()
    {
        // The other half: pairing must still report a genuine disagreement, and report it
        // against the id rather than against a position.
        var left = new UpperIdHolder
        {
            Items = { new UpperIdElement { ID = 1, Name = "a" }, new UpperIdElement { ID = 2, Name = "b" } },
        };
        var right = new UpperIdHolder
        {
            Items = { new UpperIdElement { ID = 1, Name = "a" }, new UpperIdElement { ID = 2, Name = "CHANGED" } },
        };

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Holder", UpperOpts()));
        Assert.Equal("Items[id=2].Name", d.Path);
        Assert.Equal("b", d.Expected);
        Assert.Equal("CHANGED", d.Actual);
    }

    [Fact]
    public void An_element_with_NEITHER_id_spelling_still_falls_back_to_position()
    {
        // The constraint that keeps the fix from over-reaching: a collection whose elements
        // carry no int id at all must keep the positional behaviour, because that is the
        // legitimate answer rather than a failure.
        var left = new NoIdHolder { Items = { new NoId { Name = "a" }, new NoId { Name = "b" } } };
        var right = new NoIdHolder { Items = { new NoId { Name = "a" }, new NoId { Name = "z" } } };

        var d = Assert.Single(MetadataObjectDiff.Compare(left, right, "Holder", new MetadataObjectDiffOptions
        {
            RecurseNamespacePrefixes = new[] { Prefix },
            PairByIdMembers = new HashSet<string>(StringComparer.Ordinal) { "NoIdHolder.Items" },
        }));
        Assert.Equal("Items[1].Name", d.Path);
        Assert.Equal("b", d.Expected);
        Assert.Equal("z", d.Actual);
    }

    private sealed class NoId
    {
        public string? Name { get; init; }
    }

    private sealed class NoIdHolder
    {
        public List<NoId> Items { get; } = new();
    }
}
