// RecordPatches.AlObjectDeclParser — parses the AL object declarations that the
// existing per-kind parsers (table / page / report / query / xmlport) do NOT
// cover, purely for their (kind, id, name) tuple.
//
// WHY THIS EXISTS
//   The AllObj system virtual table (2000000038) must report every object the
//   runner knows about — including codeunits, enums and the *extension object
//   kinds, none of which had an (id, name) registry anywhere in the runner
//   (codeunits were only ever discovered lazily by CLR type-name convention
//   `Codeunit{id}`, which carries the id but not the AL name).
//
//   This parser is deliberately source-based rather than compiler-symbol based:
//   the emit pipeline's CaptureOutputter only fires on a compile-cache MISS, so
//   a registry fed from there would be empty on every warm run. `_sourceDirs`
//   is registered on every run, warm or cold.
//
//   Same regex-over-raw-text strategy (and same limitations) as the sibling
//   parsers in RecordPatches.Al*Parser.cs. Declarations are anchored to the
//   start of a line so an `Codeunit "X"` variable declaration or a
//   `Codeunit.Run(...)` call site cannot be mistaken for an object declaration.
using System.Text.RegularExpressions;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Object kinds handled here. Everything in this list is `<keyword> <id> <name>`;
    // AL kinds with no object id (interface, controladdin, profile) are excluded
    // because AllObj is keyed by (Object Type, Object ID) and a synthetic id would
    // be a fabrication.
    private static readonly (string Kind, Regex Rx)[] RxObjectDecls =
    {
        ("Codeunit",              MakeDeclRegex("codeunit")),
        ("Enum",                  MakeDeclRegex("enum")),
        ("EnumExtension",         MakeDeclRegex("enumextension")),
        ("PageExtension",         MakeDeclRegex("pageextension")),
        ("TableExtension",        MakeDeclRegex("tableextension")),
        ("PermissionSet",         MakeDeclRegex("permissionset")),
        ("PermissionSetExtension",MakeDeclRegex("permissionsetextension")),
    };

    private static Regex MakeDeclRegex(string keyword) => new(
        @"^\s*" + keyword + @"\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    // (kind, id) → declaration. Keyed per kind because AL id namespaces are
    // per-object-type (codeunit 50100 and enum 50100 may coexist).
    private static readonly Dictionary<(string Kind, int Id), ParsedAlObjectDecl> _parsedObjectDecls = new();

    private static void ParseAllObjectDeclSources()
    {
        foreach (var dir in _sourceDirs)
        {
            var files = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories);
            foreach (var file in files)
                TryParseObjectDeclFile(File.ReadAllText(file));
        }
    }

    private static void TryParseObjectDeclFile(string text)
    {
        text = AlCommentBlanker.Blank(text); // see AlPageParser — same reason (#1690/#1697)

        foreach (var (kind, rx) in RxObjectDecls)
        {
            foreach (Match m in rx.Matches(text))
            {
                if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
                var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                // `enumextension`/`permissionsetextension` also match the shorter
                // `enum`/`permissionset` keyword regexes' prefix? No — the shorter
                // regex requires whitespace + digits right after the keyword, which
                // "extension" does not satisfy. So no cross-kind contamination.
                _parsedObjectDecls[(kind, id)] = new ParsedAlObjectDecl(kind, id, name);
            }
        }
    }

    /// <summary>Snapshot of every non-table/page/report/query/xmlport AL object declaration parsed from source.</summary>
    internal static IReadOnlyCollection<ParsedAlObjectDecl> ParsedObjectDecls => _parsedObjectDecls.Values;
}

internal record ParsedAlObjectDecl(string Kind, int Id, string Name);
