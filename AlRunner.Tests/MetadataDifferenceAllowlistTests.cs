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

    private static MetadataAllowlistEntry Entry(string member, int? max = null, string? objectKey = null)
        => new() { Member = member, Reason = "measured; tracked", Issue = 3545, MaxOccurrences = max, ObjectKey = objectKey };

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
    public void Exceeding_a_declared_occurrence_cap_fails()
    {
        var list = new MetadataDifferenceAllowlist(new[] { Entry("MetaField.Editable", max: 1) });

        var verdict = list.Classify(new[]
        {
            Diff("MetaField", "Editable", "Table 5"),
            Diff("MetaField", "Editable", "Table 6"),
        });

        Assert.Empty(verdict.Undeclared);
        Assert.Contains("2 occurrences, declared max 1", Assert.Single(verdict.ExceededEntries), StringComparison.Ordinal);
        Assert.False(verdict.Ok);
    }

    [Fact]
    public void Staying_within_a_declared_occurrence_cap_passes()
    {
        var list = new MetadataDifferenceAllowlist(new[] { Entry("MetaField.Editable", max: 2) });

        Assert.True(list.Classify(new[] { Diff("MetaField", "Editable") }).Ok);
    }

    [Fact]
    public void An_entry_narrowed_to_one_object_does_not_cover_another()
    {
        var list = new MetadataDifferenceAllowlist(new[] { Entry("MetaField.Editable", objectKey: "Table 5") });

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
