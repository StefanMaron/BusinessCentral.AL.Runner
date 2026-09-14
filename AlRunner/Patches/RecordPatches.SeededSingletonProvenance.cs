// RecordPatches.SeededSingletonProvenance — read-only "did the install seed leave this field
// blank, or did the test?" for the missing-test-data diagnosis (#2277).
//
// Substitutes no BC behaviour and writes nothing, so it owes no observably-equivalent
// justification; like RecordPatches.StoredTableCensus.cs, every path that cannot read answers
// Unknown rather than a verdict. Provenance comes from the install baseline, not from an
// "every field is at its default" guess: a test that inserts its own blank row looks identical
// to a seeded one by value, and only the baseline can tell them apart.
using System.Collections;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>What the store and the install baseline say about one field of a one-row table.
    /// Only <see cref="SeededBlank"/> is evidence for the diagnosis; every other value is a
    /// reason to say nothing, and <see cref="Unknown"/> is kept apart from the negatives so a
    /// caller or a test can tell "could not read" from "read, and it was the test".</summary>
    internal enum SeededSingletonEvidence
    {
        /// <summary>The store, the baseline or the field could not be read unambiguously.</summary>
        Unknown,
        /// <summary>The live table does not hold exactly one row, or its field is not blank.</summary>
        NotApplicable,
        /// <summary>The rows came from a --test-data backup, so a blank is the company's own.</summary>
        FromBackup,
        /// <summary>The install baseline holds no row for the table: the test inserted it.</summary>
        NotInstallSeeded,
        /// <summary>The install seed wrote a value into the field: the test blanked it.</summary>
        SeededNotBlank,
        /// <summary>One row, seeded at install time with this field blank, and still blank.</summary>
        SeededBlank,
    }

    private const string SeededSingletonSurface = "seeded-singleton provenance (diagnostic)";

    /// <param name="fieldName">BC's NavTestFieldException.FieldName; matched against
    /// NCLMetaField.FieldName, then FieldCaption, and must resolve to exactly one field.</param>
    internal static SeededSingletonEvidence ClassifySeededSingletonField(int tableId, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return SeededSingletonEvidence.Unknown;
        if (BackupOwnsRowsFor(tableId)) return SeededSingletonEvidence.FromBackup;

        var live = ReadLiveSingleRowFieldBlank(tableId, fieldName);
        if (live == null) return SeededSingletonEvidence.Unknown;
        if (live == false) return SeededSingletonEvidence.NotApplicable;

        List<BaselineSource>? baseline;
        lock (_baselineMutationLock) baseline = _installBaseline;
        if (baseline == null) return SeededSingletonEvidence.Unknown;

        var seededRows = 0;
        bool? seededBlank = null;
        foreach (var source in StableBaselineSourcesForWalk(baseline))
            foreach (var table in source.Tables)
            {
                if (table.TableId != tableId) continue;
                foreach (var row in table.Rows)
                {
                    seededRows++;
                    if (table.MetaTable is not NCLMetaTable meta) return SeededSingletonEvidence.Unknown;
                    var blank = FieldValueIsBlank(meta, row, fieldName);
                    if (blank == null) return SeededSingletonEvidence.Unknown;
                    seededBlank = blank;
                }
            }

        if (seededRows == 0) return SeededSingletonEvidence.NotInstallSeeded;
        if (seededRows != 1) return SeededSingletonEvidence.NotApplicable;
        return seededBlank == true ? SeededSingletonEvidence.SeededBlank : SeededSingletonEvidence.SeededNotBlank;
    }

    /// <summary>True/false for the field's blankness when the live store holds exactly one row
    /// of the table across every DataAccessSource; false when it holds any other count; null
    /// when it cannot be read.</summary>
    private static bool? ReadLiveSingleRowFieldBlank(int tableId, string fieldName)
    {
        var rows = 0;
        bool? blank = null;
        foreach (var (_, perTable) in _dataAccessByTable)
        {
            if (!perTable.TryGetValue(tableId, out var dataAccess)) continue;
            object? metaTableObj;
            object? primaryTree;
            try
            {
                var provider = GetDataProvider(dataAccess);
                if (provider == null || provider.GetType().Name != "TempTableDataProvider") return null;
                var providerType = provider.GetType();
                metaTableObj = RequiredField(providerType, "table", SeededSingletonSurface).GetValue(provider);
                primaryTree = RequiredField(providerType, "primaryTree", SeededSingletonSurface).GetValue(provider);
            }
            catch (AlRunner.Infrastructure.BcShapeGapException) { return null; }   // see CollectCensus
            catch (TargetInvocationException) { return null; }

            if (metaTableObj is not NCLMetaTable meta) return null;
            if (primaryTree is not IEnumerable tree) continue;   // null tree = no row ever inserted
            foreach (var row in tree)
            {
                if (row is not TempTableRecordBuffer buffer) return null;
                rows++;
                NavValue[] values;
                try { values = buffer.ToArray(); }
                catch { return null; }
                blank = FieldValueIsBlank(meta, values, fieldName);
                if (blank == null) return null;
            }
        }
        if (rows != 1) return false;
        return blank;
    }

    /// <summary>Row values are indexed by field-definition order, as in TryGetMaxFieldValue.</summary>
    private static bool? FieldValueIsBlank(NCLMetaTable meta, NavValue[] values, string fieldName)
    {
        var index = FieldIndexByName(meta, fieldName);
        if (index < 0 || index >= values.Length || values[index] == null) return null;
        try { return values[index].IsZeroOrEmpty; }
        catch { return null; }
    }

    private static int FieldIndexByName(NCLMetaTable meta, string fieldName)
    {
        var byName = -1;
        var byCaption = -1;
        var nameHits = 0;
        var captionHits = 0;
        try
        {
            for (var fi = 0; fi < meta.FieldCount; fi++)
            {
                var field = meta.GetFieldByIndex(fi);
                if (field == null) continue;
                if (string.Equals(field.FieldName, fieldName, StringComparison.Ordinal)) { byName = fi; nameHits++; }
                string? caption;
                try { caption = field.FieldCaption; } catch { caption = null; }
                if (string.Equals(caption, fieldName, StringComparison.Ordinal)) { byCaption = fi; captionHits++; }
            }
        }
        catch { return -1; }
        if (nameHits == 1) return byName;
        if (nameHits == 0 && captionHits == 1) return byCaption;
        return -1;   // absent or ambiguous: unknown, never a guess
    }
}
