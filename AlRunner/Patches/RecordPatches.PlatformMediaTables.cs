// RecordPatches.PlatformMediaTables — the storage tables behind a Media field.
//
// WHY THEY HAVE TO BE DECLARED HERE
//   A Media field does not hold its own bytes: BC writes them to a platform table and keeps
//   only the media id on the record. NavMediaImport.ImportMediaObjectCoreAsync picks that
//   table with NavMediaHelper.GetMediaTableId(parentTableId) — 2000000181 (Media) for an
//   application-database table, 2000000184 (Tenant Media) for everything else — then does an
//   ordinary ALInsertAsync into it.
//
//   Those two tables are PLATFORM tables. They are not in any app's SymbolReference.json (the
//   System app ships zero table definitions), and they have no AL source in any .app, so
//   every source the runner's metadata builder knows about comes up empty and the record it
//   hands BC has no table behind it — ALInsertAsync then NREs. That is why storing ANY media
//   failed once the image-classification problem above it was fixed.
//
//   Declaring them as ordinary parsed tables is the whole fix: the existing pipeline builds
//   the NCLMetaTable and the in-memory store holds the rows, exactly as for a table read from
//   AL source. Nothing here is a provider or a special case — the tables simply exist now.
//
// FIELD LAYOUT
//   Taken from BC's own accesses in ImportMediaObjectCoreAsync, which is the definition that
//   matters: whatever BC writes by field NUMBER has to be there, with the type it writes.
//     1  ID            Guid       3  Content   BLOB      5  Height  Integer   7   Company Name Text
//     2  Description   Text       4  Mime Type Text      6  Width   Integer   11  File Name    Text
//   Field 1 is the primary key — the media id BC generates and stores on the parent record.

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int MediaTableId = 2000000181;
    internal const int TenantMediaTableId = 2000000184;

    /// <summary>
    /// The platform media tables, keyed by table id. Consulted before the .app scan in
    /// TryPopulateParsedTableFromBcApps, because no .app can supply them.
    /// </summary>
    private static ParsedTable? BuiltInPlatformTable(int tableId) => tableId switch
    {
        MediaTableId => MediaTable(MediaTableId, "Media"),
        TenantMediaTableId => MediaTable(TenantMediaTableId, "Tenant Media"),
        _ => null,
    };

    private static ParsedTable MediaTable(int tableId, string tableName) => new(
        tableId,
        tableName,
        new List<ParsedField>
        {
            new(1,  "ID",           "Guid",    0),
            new(2,  "Description",  "Text",    250),
            new(3,  "Content",      "BLOB",    0),
            new(4,  "Mime Type",    "Text",    100),
            new(5,  "Height",       "Integer", 0),
            new(6,  "Width",        "Integer", 0),
            new(7,  "Company Name", "Text",    30),
            new(11, "File Name",    "Text",    250),
        },
        new List<int> { 1 });
}
