// BackupCatalog — pure parsers for the backup reader's textual output.
//
// Kept free of I/O on purpose: the shapes below are the whole contract between the runner
// and a tool it does not own, so they are the part most worth pinning with tests that do not
// need a 900 MB backup on disk.
//
// `tables --symbols <apps>` emits one line per (company, physical table):
//     "<rows>  <kind>  <company>\t<table name>\t<resolution>"
// where <resolution> is `<id> "<name>" (<app>)` when the provided symbols define the table,
// and "-" when they do not. A company of "-" means the table is not company-scoped.
//
// `read --format json` emits one JSON object per row, keyed by AL FIELD NAME (plus BC's
// system columns). The AL table id comes from `tables --symbols`, and the field names are
// resolved against the runner's OWN NCLMetaTable — the very metatable the rows are inserted
// into — so a name the runner does not know refuses the table rather than silently dropping
// a column.
//
// `describe`'s fixed-width column output is deliberately NOT parsed. It pads rather than
// truncates, so a long AL type (`Enum "Bank Acc. Rec. Stmt. Type"`) pushes every later column
// right and any offset-based slice silently yields a WRONG field id — measured on 30+ CRONUS
// tables during #2258. Reading the names off the metatable the values are going into is both
// safer and one fewer subprocess per table.
using System.Globalization;
using System.Text.RegularExpressions;

namespace AlRunner.Infrastructure;

/// <summary>One row of `bcbak tables`.</summary>
internal sealed record BackupTableEntry(
    long RowCount, string Kind, string Company, string TableName, int? AlTableId, string? AppName)
{
    /// <summary>True for BC's table-extension companion (`&lt;table&gt;$ext`), which holds the
    /// fields contributed by extending apps. Out of scope for the first hydration slice — see
    /// TestDataProvisioner.</summary>
    internal bool IsExtensionCompanion => TableName.EndsWith("$ext", StringComparison.Ordinal);

    /// <summary>The base table an extension companion belongs to.</summary>
    internal string BaseTableName => IsExtensionCompanion ? TableName[..^"$ext".Length] : TableName;
}

internal static class BackupCatalog
{
    private static readonly Regex TablesLine = new(
        @"^\s*(?<rows>\d+)\s+(?<kind>\S+)\s+(?<rest>.*)$", RegexOptions.Compiled);

    private static readonly Regex ResolutionText = new(
        @"^(?<id>\d+)\s+""(?<name>.*)""\s+\((?<app>.*)\)$", RegexOptions.Compiled);

    internal static IReadOnlyList<BackupTableEntry> ParseTables(string stdout)
    {
        var result = new List<BackupTableEntry>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var m = TablesLine.Match(line);
            if (!m.Success)
                throw new BackupReaderException(
                    $"unrecognised `tables` line from the backup reader: '{line}'");

            var parts = m.Groups["rest"].Value.Split('\t');
            if (parts.Length < 2)
                throw new BackupReaderException(
                    $"`tables` line has no table-name column: '{line}'");

            var company = parts[0].Trim();
            var tableName = parts[1];
            int? alId = null;
            string? appName = null;
            if (parts.Length >= 3)
            {
                var resolution = parts[2].Trim();
                if (resolution.Length > 0 && resolution != "-")
                {
                    var rm = ResolutionText.Match(resolution);
                    if (!rm.Success)
                        throw new BackupReaderException(
                            $"unrecognised `tables` resolution column: '{resolution}' (line: '{line}')");
                    alId = int.Parse(rm.Groups["id"].Value, CultureInfo.InvariantCulture);
                    appName = rm.Groups["app"].Value;
                }
            }
            result.Add(new BackupTableEntry(
                long.Parse(m.Groups["rows"].Value, CultureInfo.InvariantCulture),
                m.Groups["kind"].Value, company, tableName, alId, appName));
        }
        return result;
    }

    internal static IReadOnlyList<string> ParseCompanies(string stdout)
        => stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
}
