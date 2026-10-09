namespace AlRunner.Infrastructure;

/// <summary>
/// The metadata virtual tables affectedOnly keys when an object changes (#5084). A test that reads
/// object metadata through one (AllObj, Table Metadata, Page Control Field, ...) records only that
/// table's own <c>tbl|Table|&lt;id&gt;</c> key, which no changed object keyed, so adding a codeunit
/// selected none of the tests that count or look up codeunits. Rules and the population:
/// docs/server-mode.md#affectedonly-and-metadata-virtual-tables.
/// </summary>
internal static class AffectedMetadataTables
{
    /// <summary>What a table's rows are computed from, as far as a changed object is concerned.</summary>
    internal enum Source
    {
        /// <summary>Rows list the objects of <see cref="Entry.Kinds"/> (every kind when null): a whole-object change of one keys the table.</summary>
        ObjectListing,
        /// <summary>Rows come from kinds that <see cref="AffectedEventSelection.UnkeyedKindChange"/> already turns into a full run.</summary>
        UnkeyedKind,
        /// <summary>Rows come from the host, the session, a fixed BC list or the runtime, not from AL objects.</summary>
        NotObjectDerived,
    }

    /// <param name="Predicate">The <c>Is...</c> predicate <c>RecordPatches.GetDataAccessForTableCore</c> dispatches on, without the <c>Is</c>.</param>
    /// <param name="TableIds">The table ids it serves.</param>
    /// <param name="Kinds">The object kinds whose rows the table lists; null for every kind.</param>
    internal sealed record Entry(string Predicate, int[] TableIds, Source Source, string[]? Kinds, string Why);

    private static readonly string[] TableKinds = { "Table", "TableExtension" };
    private static readonly string[] PageKinds = { "Page", "PageExtension" };
    private static readonly string[] ProfileListingKinds = { "Profile", "Page" };
    private static readonly string[] PermissionKinds = { "PermissionSet", "PermissionSetExtension" };

    /// <summary>
    /// EVERY table <c>GetDataAccessForTableCore</c> serves from a branch of its own, classified. A new
    /// branch fails <c>AffectedMetadataTablesTests</c> until it is added here, so a table the runner
    /// starts serving from the object registry cannot silently escape the keying.
    /// </summary>
    internal static readonly IReadOnlyList<Entry> Population = new Entry[]
    {
        new("FieldVirtualTable", new[] { 2000000041 }, Source.ObjectListing, TableKinds, "one row per field of each table"),
        new("AllObjVirtualTable", new[] { 2000000038 }, Source.ObjectListing, null, "one row per object"),
        new("AllObjWithCaptionVirtualTable", new[] { 2000000058 }, Source.ObjectListing, null, "one row per object, with its caption and subtype"),
        new("IntegerVirtualTable", new[] { 2000000026 }, Source.NotObjectDerived, null, "computed over the requested range"),
        new("CodeCoverage", new[] { 2000000049, 2000000288, 2000000289 }, Source.NotObjectDerived, null, "the session's code coverage log"),
        new("TableRelationsMetadataVirtualTable", new[] { 2000000141 }, Source.ObjectListing, TableKinds, "one row per table relation of each table's fields"),
        new("KeyVirtualTable", new[] { 2000000063 }, Source.ObjectListing, TableKinds, "one row per key of each table"),
        new("PageActionVirtualTable", new[] { 2000000143 }, Source.ObjectListing, PageKinds, "one row per action of each page, those a pageextension adds included"),
        new("QueryMetadataVirtualTable", new[] { 2000000142 }, Source.ObjectListing, new[] { "Query" }, "one row per query"),
        new("XmlPortMetadataVirtualTable", new[] { 2000000280 }, Source.ObjectListing, new[] { "XmlPort" }, "one row per xmlport"),
        // #5452: the RoleCenter page NAME of each row is resolved to a page id (RecordPatches.PageIdsByName),
        // so renumbering or renaming a page changes the rows too. Nothing else reads a declared profile.
        new("AllProfileVirtualTable", new[] { 2000000178 }, Source.ObjectListing, ProfileListingKinds, "one row per profile object, its role center page resolved to an id"),
        new("DateVirtualTable", new[] { 2000000007 }, Source.NotObjectDerived, null, "computed per period"),
        new("EventSubscriptionVirtualTable", new[] { 2000000140 }, Source.ObjectListing, null, "one row per subscriber declared in any object"),
        new("ReportLayoutListVirtualTable", new[] { 2000000234 }, Source.ObjectListing, new[] { "Report" }, "the layouts each report declares"),
        new("ReportMetadataVirtualTable", new[] { 2000000139 }, Source.ObjectListing, new[] { "Report" }, "one row per report"),
        new("ReportDataItemsVirtualTable", new[] { 2000000203 }, Source.ObjectListing, new[] { "Report" }, "one row per data item of each report"),
        new("MetadataPermissionSetVirtualTable", new[] { 2000000250 }, Source.ObjectListing, PermissionKinds, "the permission sets the apps declare"),
        new("PermissionSetSystemTable", new[] { 2000000004 }, Source.ObjectListing, PermissionKinds, "the assignable permission sets the apps declare"),
        new("PermissionFamilyTable", new[] { 2000000005, 2000000251, 2000000254 }, Source.ObjectListing, PermissionKinds, "the permissions of the declared permission sets"),
        new("AggregatePermissionSetVirtualTable", new[] { 2000000167 }, Source.ObjectListing, PermissionKinds, "the union of the system and tenant permission sets"),
        new("TableMetadataVirtualTable", new[] { 2000000136 }, Source.ObjectListing, new[] { "Table" }, "one row per table"),
        new("PageMetadataVirtualTable", new[] { 2000000138 }, Source.ObjectListing, new[] { "Page" }, "one row per page"),
        new("TimeZoneVirtualTable", new[] { 2000000164 }, Source.NotObjectDerived, null, "the host's time zones"),
        new("SessionVirtualTable", new[] { 2000000009 }, Source.NotObjectDerived, null, "the reading session"),
        new("FeatureKeyVirtualTable", new[] { 2000000211 }, Source.NotObjectDerived, null, "BC's own feature list"),
        new("WindowsLanguageVirtualTable", new[] { 2000000045 }, Source.NotObjectDerived, null, "the host's cultures"),
        new("NavAppExtraVirtualTable", new[] { 2000000157 }, Source.NotObjectDerived, null, "one row per app (an app change is an environment diff, not an object change)"),
        new("CodeunitMetadataVirtualTable", new[] { 2000000137 }, Source.ObjectListing, new[] { "Codeunit" }, "one row per codeunit"),
        new("ObjectMetadataSystemTable", new[] { 2000000071 }, Source.NotObjectDerived, null, "a fixed list of BC's application database table ids"),
        new("PageControlFieldVirtualTable", new[] { 2000000192 }, Source.ObjectListing, PageKinds, "one row per field control of each page, those a pageextension adds included"),
    };

    /// <summary>The table ids whose rows a whole-object change of <paramref name="kind"/> can change.</summary>
    internal static IEnumerable<int> TablesListing(string kind)
        => Population
            .Where(e => e.Source == Source.ObjectListing && (e.Kinds == null || e.Kinds.Contains(kind, StringComparer.Ordinal)))
            .SelectMany(e => e.TableIds);

    /// <summary>
    /// The <c>tbl|Table|&lt;id&gt;</c> keys of the metadata virtual tables a changed object's rows
    /// belong to. Only a whole-object change counts: a change narrowed to one procedure's statements
    /// moves no row of any of them. A full run is forced, with a reason, when the recording cannot say
    /// which tests read such a table: it holds no record of reads, or a record of it was held outside
    /// any one test (a test codeunit's global, a SingleInstance codeunit), the same rule as for any
    /// other table.
    /// </summary>
    /// <param name="wholeObjectChanged">Whether the object's change is not narrowed to a procedure.</param>
    internal static AffectedEventSelection.Result ChangedKeys(
        IEnumerable<AffectedObjectId> changed, Func<AffectedObjectId, bool> wholeObjectChanged,
        HashSet<string>? recordedBundleWide)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in changed)
        {
            if (!wholeObjectChanged(o)) continue;
            foreach (var table in TablesListing(o.Kind))
            {
                if (recordedBundleWide == null)
                    return new(keys, $"{o.Kind} {(o.Id.HasValue ? o.Id.Value.ToString() : o.Name)} changed and the coverage baseline has no record of which tests read the metadata tables that list it");
                keys.Add(AlEventRaiseTracker.TableKey("Table", table));
            }
        }
        if (keys.Count == 0) return new(keys, null);
        var held = keys.Where(recordedBundleWide!.Contains).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        return held == null
            ? new(keys, null)
            : new(keys, $"a record of metadata table {held.Substring("tbl|Table|".Length)} was held outside any one test "
                + "(built before the test ran, or by a SingleInstance codeunit), so the tests reading it are not recorded");
    }
}
