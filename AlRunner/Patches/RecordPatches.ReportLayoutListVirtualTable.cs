// RecordPatches.ReportLayoutListVirtualTable — managed provider for the
// "Report Layout List" system virtual table (2000000234).
//
// WHY THIS EXISTS
//   Selecting a report layout BY NAME
//   (`ReportLayoutSelection.SetTempLayoutSelectedName('Foo')` + Report.Run/SaveAs)
//   runs entirely through BC's own code:
//
//     ReportLayoutSelection.TryGetTemporarySelectionAsync
//       -> NavSystemCodeunitReportingTriggers.InvokeSelectReportLayoutCode  (AL side, works)
//       -> ReportLayoutSelection.ParseAndSelectLayoutFromIDAsync
//       -> ReportLayoutSelection.GetLayoutByNameAndAppIDAsync
//            new NavRecord(2000000234)
//            SetFilter(field 1 "Report ID", =reportId)
//            SetFilter(field 2 "Name",      =layoutName)
//            [SetFilter(field 11 "App ID",  =appId) only when an app id was given]
//            FindFirst -> ReportLayout.Create(session, reportId, <that row>)
//
//   On the runner that table routed to the same empty in-memory store as every
//   other table, so FindFirst returned nothing and BC raised
//   `NavNCLReportNoLayoutException: Report N does not have a valid layout`
//   for every by-name selection. The layout NAME was, in effect, discarded.
//
// WHAT THIS DOES
//   Populates that in-memory store with one row per `layout(Name) { ... }`
//   declared in the report's `rendering` block, using the values the AL compiler
//   itself recorded (AlReportLayoutRegistry: Name / Type / MimeType / Caption /
//   Summary / LayoutFile). BC's own filter+find engine then runs over those rows
//   and BC's own ReportLayout.Create builds the layout — no MS body is rewritten
//   and no layout is invented: a name resolves if and only if the report really
//   declares it. An undeclared name still finds nothing and still raises BC's own
//   no-valid-layout error.
//
//   Row values are laid out the way every other virtual table in this runner is:
//   BC's own VirtualDataProvider.GetSystemPopulatedVirtualRecordValues fills the
//   timestamp / SystemId / audit slots, we write the columns we can answer
//   truthfully (by FIELD NAME, resolved from the metatable itself - never a
//   hardcoded field-number table), and BC's own NavValue.GetDefaultNavValue fills
//   the rest.
//
// SCOPE — LAYOUT CONTENT IS NOT SERVED HERE
//   These rows carry the layout's identity (name, format, MIME type, caption),
//   which is what selection needs. They deliberately do NOT claim to carry the
//   layout's bytes: the "Layout Media ID"-style columns get BC's own default
//   (empty GUID), exactly as an application-provided layout row does on a real
//   tier, where the bytes are fetched separately from the app package
//   (ReportLayout.FetchLayoutFromApplication). Rendering is out of scope for the
//   runner, so nothing downstream of selection is faked here.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (VirtualDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer, TempTableDataProvider). No AL business-logic body is
//   touched.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int ReportLayoutListVirtualTableId = 2000000234;

    // Per in-memory-provider guard so repeated data-access handouts only insert
    // layouts that appeared since (idempotent, no duplicate-key throws).
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<(int ReportId, string Name), byte>> _rllPopulatedByProvider = new();

    private static bool IsReportLayoutListVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == ReportLayoutListVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind the Report Layout List (2000000234) data
    /// access with one row per layout the runner knows a report declares.
    /// </summary>
    private static void PopulateReportLayoutListVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        // The AllObj block resolved exactly the same set of Ncl helpers; reuse it.
        EnsureAllObjReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Report Layout List (virtual table 2000000234)",
                "report-layout-list-virtual-table — data access has no in-memory provider; see docs/scope.md");

        var done = _rllPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<(int, string), byte>());

        foreach (var layout in AlReportLayoutRegistry.Snapshot())
        {
            if (!done.TryAdd((layout.ReportId, layout.Name), 0)) continue;
            InsertReportLayoutListRow(provider, metaTable, layout);
        }
    }

    private static void InsertReportLayoutListRow(object provider, NCLMetaTable metaTable, AlReportLayoutInfo layout)
    {
        // Virtual-record identity: (tableId, reportId, hash(name)) — stable per
        // (report, layout) so repeated handouts produce the same SystemId.
        var nameKey = System.StringComparer.Ordinal.GetHashCode(layout.Name) & 0x7fffffff;
        var values = _aovSystemValues!.Invoke(
            metaTable, ReportLayoutListVirtualTableId, layout.ReportId, nameKey, 0);

        foreach (var field in GetAllFields(metaTable) ?? System.Linq.Enumerable.Empty<NCLMetaField>())
        {
            var idx = field.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            if (values.GetValue(idx) != null) continue;   // BC already filled this slot

            values.SetValue(BuildReportLayoutListValue(field, layout), idx);
        }

        var readOnly = _aovCtorReadOnlyBuffer!.Invoke(new object?[] { metaTable, values });
        var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnly });
        try
        {
            _aovTtdpInsert!.Invoke(provider, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
        }
        catch (System.Reflection.TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // Same (Report ID, Name) already present — faithful to a virtual table
            // where that pair is the primary key.
        }
    }

    /// <summary>
    /// One column of a Report Layout List row. Columns are matched by the metatable's own
    /// FIELD NAME (case/space/hyphen-insensitive) so the mapping tracks whatever the System
    /// Application package in the resolved artifact declares, rather than hardcoded numbers.
    /// Anything we cannot answer truthfully gets BC's own default for that field.
    /// </summary>
    private static object? BuildReportLayoutListValue(NCLMetaField field, AlReportLayoutInfo layout)
    {
        object? Default() => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });

        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "reportid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { layout.ReportId });
            case "name":
                return Text(layout.Name);
            case "caption":
                return Text(string.IsNullOrEmpty(layout.Caption) ? layout.Name : layout.Caption);
            case "description":
            case "summary":
                return Text(layout.Summary);
            case "mimetype":
                return Text(layout.MimeType);
            case "layoutformat":
                {
                    var ordinal = ResolveOptionOrdinalByName(field, layout.LayoutType);
                    // A layout Type this BC version's option set does not know is not
                    // something we may guess at — BC's format fork keys off this value.
                    if (ordinal < 0)
                        throw new RunnerOutOfScopeException(
                            "Report Layout List (virtual table 2000000234)",
                            $"report-layout-list-virtual-table — report {layout.ReportId} layout '{layout.Name}' declares "
                            + $"Type = '{layout.LayoutType}', which is not in this BC version's "
                            + $"\"{field.FieldName}\" option set ('{field.FieldOptionMetadata?.OptionString}'); see docs/scope.md");
                    return _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, ordinal });
                }
            // Every other column — Company Name, App ID, Layout/Media GUIDs, User Defined,
            // Obsolete State, Excel sheet configuration, … — is exactly what an
            // application-provided (non-tenant, non-user-defined) layout row carries on a
            // real tier for a report in the current extension: the type's default value.
            default:
                return Default();
        }
    }

    /// <summary>
    /// Ordinal of <paramref name="optionName"/> in the field's own option string, matched
    /// name-insensitively. -1 when the option set does not contain it.
    /// </summary>
    private static int ResolveOptionOrdinalByName(NCLMetaField field, string optionName)
    {
        var optionString = field.FieldOptionMetadata?.OptionString;
        if (string.IsNullOrEmpty(optionString) || string.IsNullOrEmpty(optionName)) return -1;
        var wanted = NormalizeObjectTypeName(optionName);
        var parts = optionString.Split(',');
        for (int i = 0; i < parts.Length; i++)
            if (NormalizeObjectTypeName(parts[i]) == wanted) return i;
        return -1;
    }
}
