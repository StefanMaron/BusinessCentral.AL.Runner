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
// `describe --table <t> --company <c> --symbols <apps>` emits a header naming the AL table,
// then a fixed-width column table. The column offsets are read FROM ITS OWN HEADER LINE
// rather than hardcoded, and a data line whose slice boundaries do not land on whitespace is
// refused instead of silently mis-sliced — a truncated or overflowing column would otherwise
// turn into a wrong field id, which is the one error class that would corrupt hydrated rows
// without failing anything.
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

/// <summary>One column of `bcbak describe`.</summary>
internal sealed record BackupColumn(int? AlFieldId, string AlName, string AlType, string SqlColumn, string SqlType)
{
    /// <summary>A column with no AL field id: the SQL `timestamp` rowversion and BC's
    /// `$system*` columns. They are addressed by name, never by id.</summary>
    internal bool IsSystemColumn => AlFieldId == null;
}

internal sealed record BackupTableSchema(int AlTableId, string AlTableName, string AppName, IReadOnlyList<BackupColumn> Columns);

internal static class BackupCatalog
{
    private static readonly Regex TablesLine = new(
        @"^\s*(?<rows>\d+)\s+(?<kind>\S+)\s+(?<rest>.*)$", RegexOptions.Compiled);

    private static readonly Regex ResolutionText = new(
        @"^(?<id>\d+)\s+""(?<name>.*)""\s+\((?<app>.*)\)$", RegexOptions.Compiled);

    private static readonly Regex DescribeHeader = new(
        @"^Table\s+(?<id>\d+)\s+""(?<name>.*?)""\s+\p{Pd}\s+app\s+""(?<app>.*?)""", RegexOptions.Compiled);

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

    internal static BackupTableSchema ParseDescribe(string stdout, string forTableName)
    {
        var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var headerLine = lines.FirstOrDefault(l => DescribeHeader.IsMatch(l))
            ?? throw new BackupReaderException(
                $"`describe {forTableName}` produced no 'Table <id> \"<name>\" — app \"<app>\"' header:\n{stdout}");
        var hm = DescribeHeader.Match(headerLine);
        var tableId = int.Parse(hm.Groups["id"].Value, CultureInfo.InvariantCulture);

        var columnHeaderIndex = lines.FindIndex(l =>
            l.Contains("AL name", StringComparison.Ordinal)
            && l.Contains("AL type", StringComparison.Ordinal)
            && l.Contains("SQL column", StringComparison.Ordinal)
            && l.Contains("SQL type", StringComparison.Ordinal));
        if (columnHeaderIndex < 0)
            throw new BackupReaderException(
                $"`describe {forTableName}` produced no column header line:\n{stdout}");

        var header = lines[columnHeaderIndex];
        var idEnd = header.IndexOf("Id", StringComparison.Ordinal) + 2;
        var nameStart = header.IndexOf("AL name", StringComparison.Ordinal);
        var typeStart = header.IndexOf("AL type", StringComparison.Ordinal);
        var sqlColStart = header.IndexOf("SQL column", StringComparison.Ordinal);
        var sqlTypeStart = header.IndexOf("SQL type", StringComparison.Ordinal);

        var columns = new List<BackupColumn>();
        for (var i = columnHeaderIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (line.Length <= sqlColStart)
                throw new BackupReaderException(
                    $"`describe {forTableName}` column line is shorter than its own header declares: '{line}'");

            // Overflow guard: every column start must land on whitespace, otherwise the value
            // to its left ran past its width and every slice after it is wrong.
            foreach (var start in new[] { nameStart, typeStart, sqlColStart, sqlTypeStart })
                if (start > 0 && start < line.Length && !char.IsWhiteSpace(line[start - 1]))
                    throw new BackupReaderException(
                        $"`describe {forTableName}` column line overflows its fixed-width layout at "
                        + $"offset {start}, so its field ids cannot be read reliably: '{line}'");

            var idText = line[..Math.Min(idEnd, line.Length)].Trim();
            var alName = Slice(line, nameStart, typeStart);
            var alType = Slice(line, typeStart, sqlColStart);
            var sqlColumn = Slice(line, sqlColStart, sqlTypeStart);
            var sqlType = sqlTypeStart < line.Length ? line[sqlTypeStart..].Trim() : "";

            int? fieldId = null;
            if (idText != "-" && idText.Length > 0)
            {
                if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    throw new BackupReaderException(
                        $"`describe {forTableName}` has a non-numeric field id '{idText}': '{line}'");
                fieldId = parsed;
            }
            columns.Add(new BackupColumn(fieldId, alName, alType, sqlColumn, sqlType));
        }

        if (columns.Count == 0)
            throw new BackupReaderException($"`describe {forTableName}` listed no columns:\n{stdout}");

        return new BackupTableSchema(tableId, hm.Groups["name"].Value, hm.Groups["app"].Value, columns);
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length) return "";
        var stop = Math.Min(end, line.Length);
        return stop <= start ? "" : line[start..stop].Trim();
    }

    internal static IReadOnlyList<string> ParseCompanies(string stdout)
        => stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
}
