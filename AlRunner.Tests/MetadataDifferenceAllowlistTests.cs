// MetadataDifferenceAllowlistTests — the allowlist's own contract.
//
// The allowlist is the only thing standing between "a difference we have decided about" and
// "a difference nobody has looked at". Three properties make it that rather than a mute:
// an undeclared difference fails; an entry that stops matching fails (so a landed fix must
// shrink the list instead of leaving cover behind); and an entry may cap its occurrences, so
// a declared defect getting WORSE still fails.

using AlRunner.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class MetadataDifferenceAllowlistTests
{
    private static MetadataDifference Diff(string type, string member, string objectKey = "Table 5")
        => new(objectKey, member, type, member, "bc", "ours");

    private static MetadataAllowlistEntry Entry(string member, string? objectKey = null)
        => new() { Member = member, Reason = "measured; tracked", Issue = 3545, ObjectKey = objectKey };

    [Fact]
    public void An_undeclared_difference_is_undeclared()
    {
        var list = new MetadataDifferenceAllowlist(new[] { Entry("MetaField.Editable") });

        var verdict = list.Classify(new[] { Diff("MetaField", "Editable"), Diff("MetaField", "EnumTypeId") });

        var undeclared = Assert.Single(verdict.Undeclared);
        Assert.Equal("MetaField.EnumTypeId", undeclared.Signature);
        Assert.False(verdict.Ok);
    }

    [Fact]
    public void A_fully_declared_difference_set_is_ok()
    {
        var list = new MetadataDifferenceAllowlist(new[] { Entry("MetaField.Editable") });

        var verdict = list.Classify(new[] { Diff("MetaField", "Editable") });

        Assert.True(verdict.Ok);
        Assert.Equal(1, verdict.OccurrencesByEntry["MetaField.Editable"]);
    }

    [Fact]
    public void An_entry_that_matches_nothing_is_reported_so_a_landed_fix_must_remove_it()
    {
        var list = new MetadataDifferenceAllowlist(new[] { Entry("MetaField.Editable"), Entry("MetaField.Lookup") });

        var verdict = list.Classify(new[] { Diff("MetaField", "Editable") });

        Assert.Equal("MetaField.Lookup", Assert.Single(verdict.UnusedEntries));
        Assert.False(verdict.Ok);
    }

    [Fact]
    public void A_runner_only_entry_does_not_cover_the_BC_only_direction()
    {
        // The blocking defect this fixes. The three merged-runtime entries are a permanent,
        // uncapped licence written for "the runner has a field BC's per-app emit does not".
        // Undirected they also covered the reverse — BC emitted a field and the runner built
        // none — which is a hard reader defect on every table.
        var list = new MetadataDifferenceAllowlist(new[]
        {
            new MetadataAllowlistEntry
            {
                Member = "MetaTable.Fields.<presence>", Reason = "runtime-merged versus build-time emit",
                OracleLimitation = true, Direction = MetadataAllowlistEntry.RunnerOnly,
                Doc = "docs/metadata-equivalence.md#the-oracle-is-build-time-per-app-metadata",
            },
        });

        var runnerOnly = new MetadataDifference("Table 242", "Fields[id=10]",
            "MetaTable", "Fields.<presence>", MetadataObjectDiff.Absent, "present");
        var bcOnly = new MetadataDifference("Table 242", "Fields[id=10]",
            "MetaTable", "Fields.<presence>", "present", MetadataObjectDiff.Absent);

        Assert.Empty(list.Classify(new[] { runnerOnly }).Undeclared);

        var undeclared = Assert.Single(list.Classify(new[] { runnerOnly, bcOnly }).Undeclared);
        Assert.Equal("present", undeclared.Expected);
        Assert.Equal(MetadataObjectDiff.Absent, undeclared.Actual);
    }

    [Fact]
    public void A_bc_only_entry_does_not_cover_the_runner_only_direction()
    {
        var list = new MetadataDifferenceAllowlist(new[]
        {
            new MetadataAllowlistEntry
            {
                Member = "MetaField.Relations.<presence>", Reason = "BC relates SystemCreatedBy to User; the runner does not",
                Issue = 3568, Direction = MetadataAllowlistEntry.BcOnly,
            },
        });

        var bcOnly = new MetadataDifference("Table 5", "Fields[id=2000000002].Relations[0]",
            "MetaField", "Relations.<presence>", "present", MetadataObjectDiff.Absent);
        var runnerInvented = new MetadataDifference("Table 5", "Fields[id=7].Relations[0]",
            "MetaField", "Relations.<presence>", MetadataObjectDiff.Absent, "present");

        Assert.Empty(list.Classify(new[] { bcOnly }).Undeclared);
        Assert.Single(list.Classify(new[] { bcOnly, runnerInvented }).Undeclared);
    }

    [Fact]
    public void An_unknown_direction_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new MetadataDifferenceAllowlist(
            new[]
            {
                new MetadataAllowlistEntry
                {
                    Member = "MetaField.Editable", Reason = "measured; tracked",
                    Issue = 3545, Direction = "sideways",
                },
            }));
        Assert.Contains("direction 'sideways'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MetaTable.Fields.<presence>", "Fields[id=10]")]
    [InlineData("MetaTable.FieldsById.<presence>", "FieldsById[key=10]")]
    [InlineData("MetaTable.#fieldsById.<presence>", "#fieldsById[key=10]")]
    public void The_CHECKED_IN_allowlist_leaves_a_missing_field_undeclared(string signature, string path)
    {
        // Against the real file, not a constructed one. "BC emitted this field and the runner
        // built none" must still fail on the very members whose permanent licence covers the
        // opposite direction — that is the whole point of scoping them.
        var list = MetadataDifferenceAllowlist.Load(MetadataEquivalencePaths.AllowlistFile());
        var i = signature.IndexOf('.', StringComparison.Ordinal);

        var runnerBuiltNothing = new MetadataDifference(
            "Table 242", path, signature[..i], signature[(i + 1)..],
            Expected: "present", Actual: MetadataObjectDiff.Absent);

        var undeclared = Assert.Single(list.Classify(new[] { runnerBuiltNothing }).Undeclared);
        Assert.Equal(signature, undeclared.Signature);
    }

    [Fact]
    public void Every_permanent_licence_in_the_checked_in_allowlist_is_direction_scoped()
    {
        // outOfScope and oracleLimitation never expire, so an undirected one licenses a defect
        // in the direction its reason says nothing about. The TranslationKey entries are a
        // value difference in both directions at once and are exempt.
        var list = MetadataDifferenceAllowlist.Load(MetadataEquivalencePaths.AllowlistFile());

        foreach (var e in list.Entries.Where(e => e.OracleLimitation))
            Assert.Equal(MetadataAllowlistEntry.RunnerOnly, e.Direction);
    }

    [Fact]
    public void An_entry_narrowed_to_one_object_does_not_cover_another()
    {
        var list = new MetadataDifferenceAllowlist(new[] { Entry("MetaField.Editable", "Table 5") });

        var verdict = list.Classify(new[]
        {
            Diff("MetaField", "Editable", "Table 5"),
            Diff("MetaField", "Editable", "Table 6"),
        });

        Assert.Equal("Table 6", Assert.Single(verdict.Undeclared).ObjectKey);
    }

    [Fact]
    public void A_placeholder_reason_is_refused_at_construction()
    {
        foreach (var placeholder in new[] { "", "  ", "n/a", "TBD", "none", "-" })
        {
            var ex = Assert.Throws<InvalidDataException>(() => new MetadataDifferenceAllowlist(
                new[] { new MetadataAllowlistEntry { Member = "MetaField.Editable", Reason = placeholder, Issue = 1 } }));
            Assert.Contains("placeholder reason", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_entry_that_says_nothing_about_WHY_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new MetadataDifferenceAllowlist(
            new[] { new MetadataAllowlistEntry { Member = "MetaField.Editable", Reason = "we think it is fine" } }));
        Assert.Contains("says nothing about WHY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_claiming_two_kinds_of_reason_is_refused()
    {
        // The four kinds need different evidence, so an entry hedging between them has
        // established none of them.
        var ex = Assert.Throws<InvalidDataException>(() => new MetadataDifferenceAllowlist(
            new[]
            {
                new MetadataAllowlistEntry
                {
                    Member = "TranslationKey.#Id1", Reason = "both, somehow",
                    Issue = 3568, CannotExpress = true,
                },
            }));
        Assert.Contains("more than one kind of reason", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_permanent_symbol_file_limit_needs_no_issue()
    {
        var list = new MetadataDifferenceAllowlist(new[]
        {
            new MetadataAllowlistEntry
            {
                Member = "SomeType.SomeMember",
                Reason = "not in the symbol file and not derivable from anything in it",
                CannotExpress = true,
            },
        });

        Assert.True(list.Classify(new[] { Diff("SomeType", "SomeMember") }).Ok);
    }

    [Fact]
    public void A_scope_or_oracle_boundary_without_a_Doc_pointer_is_refused()
    {
        // Both are PERMANENT licences to differ, unlike a tracked defect, so the claim has to
        // be somewhere a reviewer can find and disagree with. test_doc_pointers.py then keeps
        // the pointer resolving.
        foreach (var entry in new[]
                 {
                     new MetadataAllowlistEntry { Member = "TranslationKey.#Id1", Reason = "the runner reads no translation files", OutOfScope = true },
                     new MetadataAllowlistEntry { Member = "MetaTable.Fields.<presence>", Reason = "runtime-merged versus build-time emit", OracleLimitation = true },
                 })
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => new MetadataDifferenceAllowlist(new[] { entry }));
            Assert.Contains("no 'Doc' pointer", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_scope_boundary_with_a_Doc_pointer_covers_its_difference()
    {
        var list = new MetadataDifferenceAllowlist(new[]
        {
            new MetadataAllowlistEntry
            {
                Member = "TranslationKey.#Id1",
                Reason = "the runner reads no translation files, so no AL test can observe this",
                OutOfScope = true,
                Doc = "docs/metadata-equivalence.md#translation-keys-are-out-of-scope",
            },
        });

        Assert.True(list.Classify(new[] { Diff("TranslationKey", "#Id1") }).Ok);
    }

    [Fact]
    public void The_checked_in_allowlist_claims_no_permanent_symbol_file_limit()
    {
        // Not decoration. 36% of the diff was declared cannotExpress on evidence that turned
        // out to be about storage rather than derivability, and both TranslationKey and
        // ClrType proved recoverable. If a future entry claims one, it should have to change
        // this test and say why in the same commit.
        var list = MetadataDifferenceAllowlist.Load(MetadataEquivalencePaths.AllowlistFile());

        Assert.Empty(list.Entries.Where(e => e.CannotExpress).Select(e => e.Member));
    }

    [Fact]
    public void Every_scope_and_oracle_entry_in_the_checked_in_allowlist_points_at_docs()
    {
        var list = MetadataDifferenceAllowlist.Load(MetadataEquivalencePaths.AllowlistFile());

        foreach (var e in list.Entries.Where(e => e.OutOfScope || e.OracleLimitation))
        {
            Assert.StartsWith("docs/", e.Doc!, StringComparison.Ordinal);
            Assert.Contains('#', e.Doc!);
            Assert.True(File.Exists(Path.Combine(
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
                    e.Doc!.Split('#')[0])),
                $"{e.Member} points at '{e.Doc}', which does not exist.");
        }
    }

    [Fact]
    public void The_empty_allowlist_declares_nothing()
    {
        var verdict = MetadataDifferenceAllowlist.None.Classify(new[] { Diff("MetaField", "Editable") });
        Assert.Single(verdict.Undeclared);
        Assert.Empty(verdict.UnusedEntries);
    }

    // ---- versionContingent: the allowlist is measured on ONE build, evaluated on THREE -----

    // #3782. Four page entries correct on 27.0 and 28.4 matched nothing on 27.5 and reddened an
    // approved PR, because the only System Application page carrying a part-control ProviderID is
    // 4306 "Agent Tasks" on 27.5 and 8705 "Table Information Card" on 28.x — and 27.5's part
    // declares Editable while 28.x's does not. Deleting the entries would have broken the two legs
    // where the differences are real.

    private static MetadataAllowlistEntry Contingent(string member)
        => new()
        {
            Member = member, Reason = "population moves with the BC version", VersionContingent = true,
            OutOfScope = true, Doc = "docs/metadata-equivalence.md#the-allowlist",
        };

    [Fact]
    public void A_version_contingent_entry_matching_nothing_is_NOT_reported_as_stale()
    {
        var list = new MetadataDifferenceAllowlist(new[] { Contingent("InfopartPageDefinition.ProviderID") });

        var verdict = list.Classify(Array.Empty<MetadataDifference>());

        Assert.Empty(verdict.UnusedEntries);
        Assert.True(verdict.Ok);
    }

    [Fact]
    public void An_ORDINARY_entry_matching_nothing_is_still_reported_as_stale()
    {
        // The half that keeps the flag from being a blanket licence. Without this the two
        // branches would be indistinguishable and the exemption could be applied to everything.
        var list = new MetadataDifferenceAllowlist(new[] { Entry("MetaField.Editable") });

        var verdict = list.Classify(Array.Empty<MetadataDifference>());

        Assert.Equal("MetaField.Editable", Assert.Single(verdict.UnusedEntries));
        Assert.False(verdict.Ok);
    }

    [Fact]
    public void A_version_contingent_entry_still_COVERS_its_difference_where_one_exists()
    {
        // It relaxes the unused check and nothing else: on a version where the difference does
        // occur, the entry must still declare it rather than leaving it undeclared.
        var list = new MetadataDifferenceAllowlist(new[] { Contingent("InfopartPageDefinition.ProviderID") });

        var verdict = list.Classify(new[] { Diff("InfopartPageDefinition", "ProviderID", "Page 8705") });

        Assert.Empty(verdict.Undeclared);
        Assert.Equal(1, verdict.OccurrencesByEntry["InfopartPageDefinition.ProviderID (version-contingent)"]);
        Assert.True(verdict.Ok);
    }

    [Fact]
    public void A_version_contingent_entry_can_NEVER_hide_an_undeclared_difference()
    {
        // The property that makes the flag safe to grant: it exempts an entry from being
        // REQUIRED, never a difference from being DECLARED. A difference the entry does not
        // cover still fails, on every version.
        var list = new MetadataDifferenceAllowlist(new[] { Contingent("InfopartPageDefinition.ProviderID") });

        var verdict = list.Classify(new[] { Diff("InfopartPageDefinition", "Editable", "Page 4306") });

        var undeclared = Assert.Single(verdict.Undeclared);
        Assert.Equal("InfopartPageDefinition.Editable", undeclared.Signature);
        Assert.False(verdict.Ok);
    }

    [Fact]
    public void A_version_contingent_entry_with_no_Doc_pointer_is_refused()
    {
        // Exempting an entry from the unused check removes the signal that a landed fix must
        // shrink this file, so the reason has to be written down. Same bar as outOfScope.
        var ex = Assert.Throws<InvalidDataException>(() => new MetadataDifferenceAllowlist(new[]
        {
            new MetadataAllowlistEntry
            {
                Member = "InfopartPageDefinition.ProviderID", Reason = "moves with the version",
                VersionContingent = true, OutOfScope = true,
            },
        }));

        Assert.Contains("versionContingent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_checked_in_allowlist_loads_and_every_entry_is_well_formed()
    {
        // Loading IS the validation: the constructor refuses a placeholder reason and a
        // tolerated defect with no issue. A malformed entry therefore fails the build rather
        // than sitting in the file granting cover nobody reviewed.
        var path = MetadataEquivalencePaths.AllowlistFile();
        Assert.True(File.Exists(path), $"the checked-in allowlist is missing at {path}");

        var list = MetadataDifferenceAllowlist.Load(path);

        Assert.NotEmpty(list.Entries);
        Assert.All(list.Entries, e => Assert.Contains('.', e.Member));
    }
}
