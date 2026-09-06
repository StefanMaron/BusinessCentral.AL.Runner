using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// AlRunner#3015 — a system-table seeder must not silently skip a column it cannot find.
///
/// The runner seeds rows a real service tier would have written at publish / company-create
/// time: Company (2000000006), Published Application (2000000206) and Installed Application
/// (2000000212). Each locates its columns by NAME off the metatable, which is right. What was
/// wrong was the failure branch — both the Published Application and the Company seeder's
/// local <c>Set</c> helper read
///
///     if (!fieldByName.TryGetValue(fieldName, out var f)) return;
///
/// so a renamed column left BC's own default in the slot, the row was still inserted, and
/// still found by its key. `Reten. Pol. Allowed Tbl. Impl.ModuleOwnsTable` then compares
/// `AllObj."App Runtime Package ID"` against `PublishedApplication."Runtime Package ID"`,
/// the comparison fails for every app, and BC LOGS A WARNING rather than raising — so the
/// failure is invisible from AL too. That is the silent default `.claude/rules/loud-failures.md`
/// forbids, on the one column whose whole purpose is to be compared.
///
/// The third sibling seeder already got this right: <c>FieldByNameOnUser</c> in
/// RecordPatches.UserSystemTable.cs throws, citing the same rule. These tests pin the shared
/// mechanism that brings the other two up to it.
/// </summary>
public sealed class SeededRowColumnsTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>A stand-in metatable: column name → (field no, value-slot index).</summary>
    private static SeededRowColumns<(int FieldNo, int Slot)> Ledger(
        int slotCount, params (string Name, int FieldNo, int Slot)[] fields)
    {
        var byName = new Dictionary<string, (int FieldNo, int Slot)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields) byName[f.Name] = (f.FieldNo, f.Slot);
        return new SeededRowColumns<(int FieldNo, int Slot)>(
            tableLabel: "Published Application (system table 2000000206)",
            fieldByName: byName,
            slotOf: f => f.Slot,
            describeField: f => $"{f.FieldNo}:?",
            slotCount: slotCount);
    }

    // ---------------------------------------------------------------- positive

    [Fact]
    public void AResolvedColumnHandsBackItsOwnFieldAndSlot()
    {
        // Not "did not throw": the caller has to be handed the SLOT it writes into and the
        // FIELD it types the value with, or the value lands in the wrong column.
        var ledger = Ledger(
            slotCount: 4,
            ("ID", 3, 2), ("Runtime Package ID", 1, 0), ("Name", 4, 3));

        Assert.True(ledger.TryResolve("Runtime Package ID", out var field, out var slot));
        Assert.Equal(1, field.FieldNo);
        Assert.Equal(0, slot);

        Assert.True(ledger.TryResolve("Name", out field, out slot));
        Assert.Equal(4, field.FieldNo);
        Assert.Equal(3, slot);

        ledger.ThrowIfAnyColumnCouldNotBeWritten();   // every column resolved — nothing to report
        Assert.Empty(ledger.Unwritable);
    }

    [Fact]
    public void ColumnNamesAreMatchedTheWayTheMetatableDictionaryMatchesThem()
    {
        // The production dictionary is OrdinalIgnoreCase, and BC's own casing for these
        // columns has moved before ("Id" vs "ID" on Company). Resolution must not depend on
        // the literal the seeder happens to spell.
        var ledger = Ledger(slotCount: 2, ("Runtime Package ID", 1, 0));

        Assert.True(ledger.TryResolve("runtime package id", out _, out var slot));
        Assert.Equal(0, slot);
        Assert.Empty(ledger.Unwritable);
    }

    // ---------------------------------------------------------------- negative

    [Fact]
    public void AColumnTheTableDoesNotHaveIsRefusedByName()
    {
        // The #3015 case exactly: BC renames "Runtime Package ID" and the seeder's write
        // evaporates.
        var ledger = Ledger(slotCount: 3, ("ID", 3, 0), ("Name", 4, 1), ("Publisher", 5, 2));

        Assert.False(ledger.TryResolve("Runtime Package ID", out _, out var slot));
        Assert.Equal(-1, slot);

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => ledger.ThrowIfAnyColumnCouldNotBeWritten());

        // The message has to be actionable on its own: which table, which column, why, and
        // what the metatable actually states instead.
        Assert.Contains("Published Application (system table 2000000206)", ex.Message);
        Assert.Contains("\"Runtime Package ID\"", ex.Message);
        Assert.Contains("no field of that name", ex.Message);
        Assert.Contains("3:?", ex.Message);
        Assert.Contains("5:?", ex.Message);
        Assert.Contains("3015", ex.Message);

        // The anchor is load-bearing, not cosmetic. ApplicationObjectBasePatches
        // .IsPermanentOutOfScope lets an AL [TryFunction] absorb a refusal into `false` unless
        // the reason STARTS WITH "not-yet-implemented" — so a seeding gap without it would be
        // swallowed back into the silent default this whole change removes.
        Assert.StartsWith("not-yet-implemented — seeded-system-table-row:", ex.Reason,
            StringComparison.Ordinal);
        Assert.Equal("Published Application (system table 2000000206)", ex.Api);
        Assert.EndsWith(" — see docs/limitations.md#runtime-shape-gaps", ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AColumnWhoseSlotIsOutsideTheRowIsRefusedToo()
    {
        // The second silent branch the seeders carried: the name resolved, but FieldIndex
        // pointed outside the value array, so `Set` returned and the column stayed default.
        var ledger = Ledger(slotCount: 2, ("ID", 3, 0), ("Tenant ID", 12, 7));

        Assert.True(ledger.TryResolve("ID", out _, out _));
        Assert.False(ledger.TryResolve("Tenant ID", out _, out var slot));
        Assert.Equal(-1, slot);

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => ledger.ThrowIfAnyColumnCouldNotBeWritten());
        Assert.Contains("\"Tenant ID\"", ex.Message);
        Assert.Contains("slot 7", ex.Message);
        Assert.Contains("2 value slot", ex.Message);
        // The one that DID resolve must not be blamed.
        Assert.DoesNotContain("\"ID\"", ex.Message);
    }

    [Fact]
    public void EveryUnwritableColumnIsNamedNotOnlyTheFirst()
    {
        // A shape change usually moves more than one column. Reporting only the first turns
        // one fix into several rounds of trial and error.
        var ledger = Ledger(slotCount: 2, ("ID", 3, 0));

        Assert.False(ledger.TryResolve("Version Major", out _, out _));
        Assert.False(ledger.TryResolve("Version Minor", out _, out _));
        Assert.True(ledger.TryResolve("ID", out _, out _));

        Assert.Equal(2, ledger.Unwritable.Count);
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => ledger.ThrowIfAnyColumnCouldNotBeWritten());
        Assert.Contains("\"Version Major\"", ex.Message);
        Assert.Contains("\"Version Minor\"", ex.Message);
        Assert.Contains("2 of the column", ex.Message);
    }

    [Fact]
    public void AskingTwiceForTheSameMissingColumnReportsItOnce()
    {
        // The Published Application seeder runs this per app, and a repeated name would
        // otherwise multiply the message by the number of loaded modules.
        var ledger = Ledger(slotCount: 1, ("ID", 3, 0));

        Assert.False(ledger.TryResolve("Package ID", out _, out _));
        Assert.False(ledger.TryResolve("Package ID", out _, out _));

        Assert.Equal(1, ledger.Unwritable.Count);
    }

    // ------------------------------------------------- the production call sites

    /// <summary>
    /// The mechanism above is only worth anything if the seeders actually go through it.
    /// This is the link a unit test over the ledger alone cannot make: neither seeder can be
    /// driven from a test, because both need a real BC <c>NCLMetaTable</c> and a live
    /// DataAccessSource, and neither can be made to lose a column.
    ///
    /// IT IS A SOURCE-TEXT GREP AND IT PROVES LESS THAN IT LOOKS: a comment naming the type
    /// satisfies the first assertion, and the second only rules out the one silent-skip line
    /// #3015 was filed against, spelled exactly that way. It is a supplementary guard against
    /// the wiring being reverted, not evidence that the wiring is correct. What actually proves
    /// that is the runner-extras suite — with this change a required column that does not
    /// resolve aborts the run, so 306 green AL tests against a real BC metatable are the
    /// end-to-end half.
    /// </summary>
    [Theory]
    [InlineData("RecordPatches.PublishedApplicationSystemTable.cs")]
    [InlineData("RecordPatches.CompanySystemTable.cs")]
    public void EverySeederResolvesItsColumnsThroughTheCheckedPath(string fileName)
    {
        var path = Path.Combine(RepoRoot, "AlRunner", "Patches", fileName);
        Assert.True(File.Exists(path), path);
        var source = File.ReadAllText(path);

        Assert.Contains("SeededRowColumns", source);

        // The exact silent-skip shape #3015 was filed against, in either seeder's local
        // `Set` helper. It resolved a column name and, on failure, simply returned.
        Assert.DoesNotContain("if (!fieldByName.TryGetValue(fieldName, out var f)) return;", source);
    }
}
