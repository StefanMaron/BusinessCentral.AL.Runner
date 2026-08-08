// RecordPatches.AlPageParser — parses AL `page` / `pageextension` declarations
// into ParsedPage records keyed by page ID. Mirror of AlSourceParser for tables.
//
// We only need the (id, name, base-id-for-extensions) tuple — the cache slot
// just has to be non-null so NCLMetadata.GetMetaApplicationObjectInternal
// finds an entry. Field/action/group layout is irrelevant: every page-level
// property getter on NCLMetaForm reads `metadataAppGroupPageDefinition.Item`
// which is a default struct on a hand-built skeleton; those getters aren't
// reached by the metadata lookup path itself.
using System.Text.RegularExpressions;
using Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly Regex RxPage = new(
        @"\bpage\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxPageExtension = new(
        @"\bpageextension\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))\s+extends\s+(?:""([^""]+)""|([A-Za-z_]\w*))[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxPageSourceTable = new(
        @"\bSourceTable\s*=\s*(?:""([^""]+)""|([A-Za-z_]\w*))\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // `InsertAllowed = false;` — AL's default when the property is absent is TRUE, so only
    // an explicit `false` needs matching. Drives ITestPage.Creatable, which BC's
    // NavTestPageBase.New() checks before inserting.
    private static readonly Regex RxPageInsertAllowed = new(
        @"\bInsertAllowed\s*=\s*(true|false)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxPageField = new(
        @"\bfield\s*\(\s*(?:""([^""]+)""|([A-Za-z_]\w*))\s*;\s*Rec\.(?:""([^""]+)""|([A-Za-z_]\w*))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void ParseAllPageSources()
    {
        foreach (var dir in _sourceDirs)
        {
            var files = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories);
            foreach (var file in files)
                TryParsePageFile(File.ReadAllText(file));
        }
    }

    private static void TryParsePageFile(string text)
    {
        // Comments first — every regex below matches property names and object headers as
        // bare text, so a comment naming one is otherwise read as the declaration itself.
        // #1690 fixed this for the table parser; the sibling parsers had the same exposure
        // (#1697): a comment mentioning SourceTable rebound the page, one mentioning
        // InsertAllowed flipped a behaviour flag, and a commented-out `page N "X" {` became
        // real metadata. Blanking is length-preserving, so every SliceObjectText / match-offset
        // calculation below is unaffected.
        text = AlCommentBlanker.Blank(text);

        // `page N "Name"` — plain pages
        foreach (Match m in RxPage.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            var pageText = SliceObjectText(text, m.Index);
            var sourceTableName = TryReadSourceTableName(pageText);
            _parsedPages[id] = new ParsedPage(id, name, IsExtension: false,
                sourceTableName, ParsePageFieldBindings(id, pageText),
                InsertAllowed: TryReadInsertAllowed(pageText));
        }

        // `pageextension N "Name" extends "Base"` — pageextensions
        foreach (Match m in RxPageExtension.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            _parsedPages[id] = new ParsedPage(id, name, IsExtension: true,
                SourceTableName: string.Empty, ControlIdToFieldName: new Dictionary<int, string>(),
                InsertAllowed: TryReadInsertAllowed(SliceObjectText(text, m.Index)));
        }
    }

    /// <summary>
    /// Whether the page permits inserts (AL's <c>InsertAllowed</c>, default TRUE when the
    /// property is absent). Drives ITestPage.Creatable, which BC's NavTestPageBase.New()
    /// checks before inserting. Unknown pages default to true — same as AL.
    /// </summary>
    internal static bool GetInsertAllowedForPage(int pageId)
        => !_parsedPages.TryGetValue(pageId, out var page) || page.InsertAllowed;

    private static bool TryReadInsertAllowed(string pageText)
    {
        var m = RxPageInsertAllowed.Match(pageText);
        // Absent => AL's default, which is true.
        return !m.Success || !string.Equals(m.Groups[1].Value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the AL source parser has seen this page at all. Lets callers tell
    /// "the page genuinely declares no SourceTable" (BC's SourceTable==0 case, a legal
    /// AL page) apart from "we never parsed this page", which is a runner gap and must
    /// be reported loudly rather than answered with a default.
    /// </summary>
    internal static bool IsPageParsed(int pageId) => _parsedPages.ContainsKey(pageId);

    /// <summary>
    /// Whether a parsed page declares a SourceTable in AL. False for a parsed page with
    /// no SourceTable property (BC returns a null NCLMetaTable for those).
    /// </summary>
    internal static bool PageDeclaresSourceTable(int pageId)
        => _parsedPages.TryGetValue(pageId, out var page)
           && !string.IsNullOrWhiteSpace(page.SourceTableName);

    internal static int GetSourceTableIdForPage(int pageId)
    {
        if (!_parsedPages.TryGetValue(pageId, out var page) || string.IsNullOrWhiteSpace(page.SourceTableName))
            return 0;

        foreach (var table in _parsedTables.Values)
            if (NamesEqual(table.TableName, page.SourceTableName))
                return table.TableId;

        return 0;
    }

    internal static IReadOnlyDictionary<int, int> GetPageControlFieldMap(int pageId)
    {
        if (!_parsedPages.TryGetValue(pageId, out var page) || string.IsNullOrWhiteSpace(page.SourceTableName))
            return new Dictionary<int, int>();

        var table = _parsedTables.Values.FirstOrDefault(t => NamesEqual(t.TableName, page.SourceTableName));
        if (table == null) return new Dictionary<int, int>();

        var result = new Dictionary<int, int>();
        foreach (var kvp in page.ControlIdToFieldName)
        {
            var field = table.Fields.FirstOrDefault(f => NamesEqual(f.FieldName, kvp.Value));
            if (field != null) result[kvp.Key] = field.FieldId;
        }
        return result;
    }

    internal static int[] GetPrimaryKeyFieldIdsForTable(int tableId)
        => _parsedTables.TryGetValue(tableId, out var table)
            ? table.PkFieldIds.ToArray()
            : Array.Empty<int>();

    private static string TryReadSourceTableName(string pageText)
    {
        var m = RxPageSourceTable.Match(pageText);
        if (!m.Success) return string.Empty;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    private static Dictionary<int, string> ParsePageFieldBindings(int pageId, string pageText)
    {
        var result = new Dictionary<int, string>();

        foreach (Match m in RxPageField.Matches(pageText))
        {
            var controlName = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var fieldName = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            result[IdSpace.GetMemberId(pageId, controlName)] = fieldName;
        }

        return result;
    }

    private static string SliceObjectText(string text, int start)
    {
        var nextObject = Regex.Match(text[(start + 1)..], @"\b(page|pageextension|table|tableextension|codeunit|report|xmlport|query)\s+\d+\b",
            RegexOptions.IgnoreCase);
        return nextObject.Success
            ? text.Substring(start, nextObject.Index + 1)
            : text[start..];
    }

    private static bool NamesEqual(string left, string right)
        => string.Equals(left.Replace(" ", ""), right.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
}

internal record ParsedPage(
    int Id,
    string Name,
    bool IsExtension,
    string SourceTableName,
    IReadOnlyDictionary<int, string> ControlIdToFieldName,
    bool InsertAllowed = true);
