// RecordPatches.BackupRowProvenance — which system tables a --test-data backup actually put
// rows into, recorded by the writer that did it rather than inferred from the store afterwards.
//
// WHY THIS EXISTS (issue #2875)
//   Object (2000000001) and Object Metadata (2000000071) are real application-database SQL
//   tables, not virtual ones, so a restored --test-data backup can genuinely carry rows for
//   them AND the runner can synthesise rows for them. When both are possible, something has to
//   decide which set wins, and both tables decided it by asking their in-memory store "do you
//   already hold a row?" (ProviderHasAnyRow).
//
//   That question has the wrong shape. It answers "are there rows", and the decision needs
//   "did somebody OTHER THAN this projection put rows here". The two come apart the moment an
//   install-baseline restore is in play: a restore builds a BRAND-NEW TempTableDataProvider, so
//   the ConditionalWeakTable guard that remembers "this projection owns this provider" is empty
//   for it, and rows THIS projection wrote before the capture read back as somebody else's.
//   The projection then latched itself off for a provider holding its own stale output — for
//   Object, whose row set is this run's object inventory rather than a fixed list, that is a
//   silently partial answer with an unchanged exit code.
//
//   #2842 narrowed it by additionally requiring that a --test-data loader exist at all, which
//   closes it for every run without --test-data. The residue was --test-data together with an
//   install baseline, and narrowing further along the same axis cannot close it: no property of
//   the STORE distinguishes the two writers.
//
// WHAT THIS DOES INSTEAD
//   Stops inferring. TestDataProvisioner.LoadOnDemand is the only other writer of these tables,
//   and it knows exactly what it loaded, so it says so: one call per table that actually got
//   rows out of the backup. Everything downstream then asks a fact instead of a symptom.
//
// WHAT HAPPENED TO ITS TWO ORIGINAL CONSUMERS (#3071)
//   Both were about Object (2000000001), and both are gone — not because the question got
//   easier, but because the projection that raised it turned out to be wrong. Corpus codeunit
//   61202 (StefanMaron/BusinessCentral.AL.Language.Tests#197) asked a real service tier what
//   the legacy registry holds and got "present, readable and EMPTY" on seven BC OnPrem legs.
//   The runner therefore synthesises no rows for that table at all, so:
//     * PopulateObjectSystemTable no longer exists, and
//     * CaptureInstallBaselineSnapshot no longer has to leave 2000000001 out — the only rows
//       it can hold are a backup's, which a baseline SHOULD carry.
//   IsProjectionOwnedSystemTableId went with them; it was defined as "2000000001 and no backup
//   behind it", which now describes an empty table rather than a projection.
//
// WHY THE RECORDER STAYS
//   What it records is still true, still cheap, and still the only place the fact exists:
//   TestDataProvisioner.LoadOnDemand is the one writer that can put rows into these tables, and
//   nothing downstream of a store can reconstruct that afterwards. Issue #3236 is the named
//   consumer — the SAME wrong-shaped question, ProviderHasAnyRow, still decides whether Object
//   Metadata's (2000000071) #2771 payload refusal is armed, and an install-baseline restore
//   replaying that table's synthesised rows disarms it. #3236 has the table and the reason
//   BackupOwnsRowsFor alone is not the whole fix there.
//
//   It deliberately does NOT weaken #2272's loud refusal. The self-populating virtual tables
//   are refused by AppendBaselineTable whatever this file says.
//
// LIFETIME
//   Process-wide and monotonic within a run, which matches what it records: "this run's armed
//   backup has rows for table N" does not stop being true because a later app group looked at
//   the table through a different provider. TestDataProvisioner.ResetForTests clears it, the
//   same place it clears the loader itself.
using System.Collections.Concurrent;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>Table ids the --test-data on-demand load actually put rows into. A set rather
    /// than a count: the only question anyone asks is membership.</summary>
    private static readonly ConcurrentDictionary<int, byte> _backupContributedRows = new();

    /// <summary>Called by <c>TestDataProvisioner.LoadOnDemand</c> for every table it loaded at
    /// least one row into, before it appends that table to the install baselines.</summary>
    internal static void NoteBackupContributedRows(int tableId) => _backupContributedRows[tableId] = 0;

    /// <summary>True when a --test-data backup put rows into <paramref name="tableId"/> in this
    /// run. False for every table in a run without --test-data, and for a table the armed
    /// backup's plan does not offer or whose rows it holds none of.</summary>
    internal static bool BackupOwnsRowsFor(int tableId) => _backupContributedRows.ContainsKey(tableId);

    /// <summary>Drop everything recorded here. Paired with clearing
    /// <see cref="TestDataOnDemandLoader"/>: the two describe the same armed backup, and a
    /// provenance record outliving the loader that produced it would let one run's backup
    /// speak for the next.</summary>
    internal static void ResetBackupRowProvenance() => _backupContributedRows.Clear();
}
