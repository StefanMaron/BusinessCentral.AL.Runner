// BakTableReader — spike CLI for AL Runner issue #2241: read BC demo-database
// table rows directly out of the .bak that ships in a sandbox artifact,
// without SQL Server. NOT a production reader -- see the issue and PR for the
// full report on what this proves and what a real implementation would still
// need (SQL-type -> AL-type mapping, BLOB/LOB resolution, page-dictionary
// compression, general heap/IAM traversal for fragmented tables).
//
// Usage:
//   dotnet run --project tools/BakTableReader -- <path-to.bak> <sql-name-substring>
using BakTableReader;
using BakTableReader.RecordDecoding;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: BakTableReader <path-to.bak> <sql-name-substring>");
    return 1;
}

string path = args[0];
string needle = args[1];

using var file = BakFile.Open(path);
Console.WriteLine($"indexed {file.Index.PageCount} pages");

var catalog = new SqlCatalog(file);
var matches = catalog.SysSchObjs.Where(o => o.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
Console.WriteLine($"{matches.Count} sysschobjs match \"{needle}\":");
foreach (var m in matches)
    Console.WriteLine($"  id={m.Id,-12} type={m.Type} name={m.Name}");

var userTable = matches.FirstOrDefault(m => m.Type == "U ");
if (userTable is null)
{
    Console.WriteLine("no USER_TABLE match -- nothing to walk");
    return 0;
}

var (rowset, allocUnit) = catalog.GetTableStorage(userTable.Id);
Console.WriteLine($"\n{userTable.Name}: rowset={rowset.RowsetId} rows={rowset.RowCount} " +
                   $"compression={rowset.CompressionLevel} pgFirst={allocUnit.PgFirst}");

if (allocUnit.PgFirst == PagePointer.Zero)
{
    Console.WriteLine("(no rows)");
    return 0;
}

if (rowset.CompressionLevel == 0)
{
    foreach (var record in catalog.WalkFixedVarRows(allocUnit.PgFirst))
        Console.WriteLine($"  row: {record.NumberOfColumns} cols, {record.FixedLengthData.Length} fixed bytes, " +
                           $"{record.VariableLengthColumns.Count} var cols");
}
else
{
    foreach (var record in catalog.WalkCompressedRows(allocUnit.PgFirst))
    {
        Console.WriteLine($"  row: {record.NumberOfColumns} cols (cmpr level {rowset.CompressionLevel})");
        for (int i = 0; i < record.NumberOfColumns; i++)
        {
            byte[]? bytes;
            try { bytes = record.GetColumnBytes(i); }
            catch (NotSupportedException ex) { Console.WriteLine($"    [{i}] {record.Indicators[i]}: {ex.Message}"); continue; }
            string preview = bytes is null ? "NULL" : PreviewAscii(bytes);
            Console.WriteLine($"    [{i}] {record.Indicators[i],-16} {preview}");
        }
    }
}

return 0;

static string PreviewAscii(byte[] bytes)
{
    if (bytes.Length == 0)
        return "''";
    bool looksAscii = bytes.All(b => b == 0 || (b >= 0x20 && b < 0x7F));
    if (looksAscii)
        return $"'{System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0')}'";
    return Convert.ToHexString(bytes);
}
