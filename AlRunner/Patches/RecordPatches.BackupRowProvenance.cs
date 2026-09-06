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
//   Two consumers, and they are two halves of one answer:
//     * PopulateObjectSystemTable defers to the backup when, and only when, the backup
//       contributed rows to 2000000001.
//     * CaptureInstallBaselineSnapshot leaves 2000000001 out of the baseline entirely when the
//       projection owns it — the #2272 treatment, for the same reason: the branch in
//       GetDataAccessForTableCore re-derives the projection on every access, so carrying it
//       across a boundary buys nothing and costs the ambiguity above. With the projection never
//       captured, the only rows a restored provider can hold for this table are a backup's, so
//       the confusion is gone by construction rather than by a better guess.
//
//   It deliberately does NOT weaken #2272's loud refusal. The self-populating virtual tables
//   are refused by AppendBaselineTable whatever this file says; provenance only ever decides
//   the question for a table that has both possible writers.
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
    /// least one row into, BEFORE it appends that table to the install baselines — so
    /// <see cref="IsProjectionOwnedSystemTableId"/> is already false by the time
    /// <see cref="AppendBaselineTable"/> checks it.</summary>
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

    /// <summary>
    /// True when <paramref name="tableId"/>'s rows in this run are a projection this runner
    /// synthesised, rather than anything a backup or an install trigger wrote.
    ///
    /// <para>Only Object (2000000001) can answer true. It is the one table that is BOTH
    /// projected from the loaded-object inventory on every access — the
    /// <see cref="IsSelfPopulatingVirtualTableId"/> description, word for word — and reachable
    /// by the --test-data on-demand loader, because its dispatch branch calls the loader
    /// explicitly instead of falling through. That combination is why it could not simply be
    /// added to that list.</para>
    ///
    /// <para>Object Metadata (2000000071) is deliberately NOT here. It is the same shape of
    /// table, but its synthesised row set is the fixed BC-declared application-database id list
    /// plus one process-constant emit version, so a replay of it is byte-identical to a fresh
    /// projection and there is nothing for a restore to get wrong. Object's row set is this
    /// run's object inventory, which is what makes the distinction load-bearing for it and not
    /// for its sibling.</para>
    /// </summary>
    internal static bool IsProjectionOwnedSystemTableId(int tableId)
        => tableId == ObjectSystemTableId && !BackupOwnsRowsFor(tableId);
}
