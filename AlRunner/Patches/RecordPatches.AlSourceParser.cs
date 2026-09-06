// RecordPatches.AlSourceParser — parses AL `table` / `tableextension` declarations into
// ParsedTable records keyed by table ID. The output is consumed by NclMetaTableBuilder to
// produce real NCLMetaTable instances at runtime.
//
// Syntax-level extraction runs on Microsoft.Dynamics.Nav.CodeAnalysis' own AL parser — the
// same front end BcCompiler.Emit already runs over the very same files (#1696). It replaced
// a set of regexes over raw .al text whose failure mode was a SILENT WRONG VALUE rather than
// a crash: `[^;]+` could not cross a semicolon inside a string literal (`InitValue =
// 'Open; pending review'` captured `'Open`), a comment mentioning a property name was read
// as that property (#1690), quoting was captured inconsistently (#1674), and object
// boundaries were guessed by slicing between regex matches. A syntax tree answers all four
// structurally: comments are trivia and simply are not in it.
//
// `SyntaxTree.ParseObjectText` needs only a ParseOptions — no Compilation, no reference
// closure — so this works on every input the parser takes: real files, AL extracted from
// dependency .app archives, and the table text NclMetaTableBuilder synthesizes.
//
// CalcFormula is mapped structurally too: `sum/lookup/…` and `count/exist` are two different
// node types, and each filter condition carries its own type, so `X = field(Y)` filters are
// selected BY TYPE rather than by a pattern that happened not to match `const(...)`. The one
// regex left in this file extracts a length from a type's text (`Code[10]` → 10), which has no
// structure for a tree to add.
using System.Text.RegularExpressions;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Matches BcCompiler.Emit's options so this parse sees the same source the emit does —
    // notably the CLEANSCHEMA1..25 preprocessor symbols, which gate real field declarations
    // in the BaseApp, PLUS whatever the caller passed via --define / --preprocessor-symbols.
    // DocumentationMode.None: doc comments are trivia we never read.
    //
    // This MUST be a property recomputed on every call, not a `static readonly` field.
    // BcCompiler.SetExtraPreprocessorSymbols(...) runs at Program.cs:727, after this type
    // may already have been touched elsewhere in the same process — a `static readonly`
    // field would freeze at type-init with the empty symbol set, and a `.Concat(...)`
    // bolted onto that frozen field would look like a fix while changing nothing (#1900:
    // the compiler's two ParseOptions sites already merge `_extraPreprocessorSymbols` per
    // call; this parser was the one site that didn't). GetExtraPreprocessorSymbols() is
    // cheap (a lock plus a sorted copy of a handful of strings), so recomputing it per
    // parse call costs nothing worth caching.
    private static NavCA.ParseOptions AlParseOptions => new(
        runtimeVersion: null!,
        preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}")
            .Concat(AlRunner.BcCompiler.GetExtraPreprocessorSymbols()),
        documentationMode: NavCA.DocumentationMode.None);

    // Field type text still yields its length by pattern (`Code[10]` → 10). The type is one
    // token's text with no nesting, so there is nothing structural for a tree to add here.
    private static readonly Regex RxTypeLength = new(@"\[(\d+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Identifiers come off the tree with AL's quoting intact — <c>"Entry No."</c>, not
    /// <c>Entry No.</c>. Every consumer (key resolution, tableextension merge, metatable
    /// lookup) matches on the bare name, so the quotes come off exactly once, here.
    /// (<c>Unquote</c> itself lives in RecordPatches.NclMetaQueryBuilder.cs — same partial
    /// class, same rule.)
    /// </summary>
    private static string IdentText(NavSyntax.IdentifierNameSyntax? id) =>
        id == null ? "" : Unquote(id.Identifier.ValueText ?? id.Identifier.Text ?? "");

    /// <summary>
    /// The caption literal declared by <c>Caption = 'text'</c>, or null when the field
    /// declares none — in which case BC's own field-name fallback is the correct answer and
    /// must stand.
    /// <para>Only the LABEL LITERAL is the caption. A label may carry trailing parts
    /// (<c>Caption = 'It''s on', Comment='x';</c>) and the property value node's text spans
    /// all of them, so reading the node wholesale would append <c>, Comment='x'</c> to the
    /// caption. <see cref="NavSyntax.LabelSyntax.LabelText"/> is just the literal.
    /// Doubled single quotes are AL's escape for an embedded quote.</para>
    /// </summary>
    private static string? CaptionFrom(NavSyntax.PropertyValueSyntax? value)
    {
        if (value is not NavSyntax.LabelPropertyValueSyntax label) return null;
        var text = label.Value?.LabelText?.ToString();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') text = text[1..^1];
        return text.Replace("''", "'");
    }

    /// <summary>
    /// The declared object id of any AL object that has one, or null. `interface`,
    /// `controladdin` and `profile` have no object id at all — they do not derive from
    /// <c>ApplicationObjectSyntax</c> — which is the same set the id-keyed parsers already
    /// excluded, for the same reason (AllObj is keyed by (type, id); a synthetic id would be
    /// a fabrication).
    /// </summary>
    private static int? ObjectIdOf(NavCA.SyntaxNode obj) =>
        obj is NavSyntax.ApplicationObjectSyntax ao && ao.ObjectId?.Value.Value is int id ? id : null;

    /// <summary>
    /// The AL object-kind name used as the first half of the `(Kind, Id)` keys and as AllObj's
    /// "Object Type". These strings are a data contract — `XMLport`'s casing in particular is
    /// what the virtual table emits. Objects not listed are not tracked by the id-keyed parsers.
    /// </summary>
    private static string? AlObjectKindName(NavCA.SyntaxNode obj) => obj switch
    {
        NavSyntax.TableSyntax => "Table",
        NavSyntax.TableExtensionSyntax => "TableExtension",
        NavSyntax.PageSyntax => "Page",
        NavSyntax.PageExtensionSyntax => "PageExtension",
        NavSyntax.ReportSyntax => "Report",
        NavSyntax.ReportExtensionSyntax => "ReportExtension",
        NavSyntax.CodeunitSyntax => "Codeunit",
        NavSyntax.QuerySyntax => "Query",
        NavSyntax.XmlPortSyntax => "XMLport",
        NavSyntax.EnumTypeSyntax => "Enum",
        NavSyntax.EnumExtensionTypeSyntax => "EnumExtension",
        NavSyntax.PermissionSetSyntax => "PermissionSet",
        NavSyntax.PermissionSetExtensionSyntax => "PermissionSetExtension",
        _ => null,
    };

    /// <summary>
    /// An OBJECT-level <c>Caption</c>, matching what the old brace-depth-scoped
    /// <c>ReadTopLevelProperty</c> returned: the unescaped literal for <c>'…'</c>, the trimmed
    /// text for a bare value, and null when the object declares no Caption at all. Null is
    /// meaningful — "declares none", which the consumer turns into AL's name fallback — and is
    /// not the same as an empty caption.
    /// <para>Differs from <see cref="CaptionFrom"/> (field-level), which answers null for a
    /// non-label value because the field-level regex required quotes.</para>
    /// </summary>
    private static string? PropertyTextFrom(NavSyntax.PropertyValueSyntax? value)
    {
        if (value == null) return null;
        return CaptionFrom(value) ?? value.ToString()?.Trim();
    }

    /// <summary>
    /// The TEXT of a property whose AL value is a plain string literal — <c>ExternalName</c>,
    /// <c>ExternalType</c> and friends — with the surrounding single quotes removed and doubled
    /// quotes unescaped, or null when the property is absent or empty.
    /// <para>Neither existing helper covers this shape. <see cref="CaptionFrom"/> unwraps only a
    /// <c>LabelPropertyValueSyntax</c> (a Label/Caption, which may carry Comment/Locked), and
    /// <see cref="PropertyTextFrom"/> falls back to the node's raw text for anything else — so
    /// <c>ExternalName = 'alt_entity'</c> came through as the 12-character <c>'alt_entity'</c>,
    /// quotes included. <c>Unquote</c> in the query builder strips double quotes only, which an
    /// AL string literal never uses.</para>
    /// </summary>
    private static string? AlStringLiteralText(NavSyntax.PropertyValueSyntax? value)
    {
        var text = PropertyTextFrom(value)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') text = text[1..^1];
        text = text.Replace("''", "'");
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// The last name segment of a possibly-namespaced object reference:
    /// <c>Microsoft.Sales.History."Sales Invoice Header"</c> → <c>Sales Invoice Header</c>,
    /// <c>Customer</c> → <c>Customer</c>. Quote-aware, so a quoted name that itself contains a
    /// dot (<c>"Doc. No."</c>) survives intact — the old dot-collapse ran over the unquoted
    /// text and would have truncated it to <c>No.</c>.
    /// </summary>
    private static string LastNameSegment(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length >= 2 && s[^1] == '"')
        {
            var open = s.LastIndexOf('"', s.Length - 2);
            if (open >= 0) return s[(open + 1)..^1];
        }
        int dot = s.LastIndexOf('.');
        return dot >= 0 && dot < s.Length - 1 ? s[(dot + 1)..] : Unquote(s);
    }

    /// <summary>Property lookup by AL name, case-insensitive as AL itself is.</summary>
    private static NavSyntax.PropertyValueSyntax? PropValue(
        NavSyntax.PropertyListSyntax? list, string name)
    {
        if (list == null) return null;
        // Properties is a list of PropertySyntaxOrEmpty: a stray `;` in a property list is
        // legal AL and parses as an empty entry, which simply has no name to match.
        foreach (var entry in list.Properties)
        {
            if (entry is not NavSyntax.PropertySyntax p) continue;
            if (string.Equals(p.Name?.Identifier.ValueText, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        }
        return null;
    }

    /// <summary>
    /// A page-valued table property (<c>LookupPageId</c> / <c>DrillDownPageId</c>) as written:
    /// the last name segment of a page reference (<c>Microsoft.Sales."Customer List"</c> →
    /// <c>Customer List</c>), or the digits when the AL declared a bare id. Null when the
    /// property is absent — "declares none", which the Table Metadata provider turns into 0
    /// rather than a guess.
    /// </summary>
    private static string? PageRefText(NavSyntax.PropertyValueSyntax? value)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var segment = LastNameSegment(text);
        return string.IsNullOrWhiteSpace(segment) ? null : segment;
    }

    private static bool PropIs(NavSyntax.PropertyListSyntax? list, string name, string expected) =>
        string.Equals(PropValue(list, name)?.ToString()?.Trim(), expected,
            StringComparison.OrdinalIgnoreCase);

    // Single-slot memo of the most recently built syntax tree's object list, keyed on the
    // exact (text, active preprocessor symbols) pair that produced it. #1903: the eight
    // TryParse*File extractors (table, tableextension, page, report, query, xmlport,
    // object-decl, object-caption) each call ParseAlObjects on the SAME file text
    // back-to-back — RecordPatches.ParseSourceFileIntoAllExtractors is the shared call
    // site both AddSourceDirs and Register() route every file through — so remembering
    // only the LAST parse turns 8 identical tree builds per file into 1 real build plus 7
    // cache hits, with no change to AlParseOptions, to any TryParse*File signature, or to
    // the eight extractors' own code.
    //
    // The key is (text, symbols), never text alone. #1900 was exactly a parser that
    // silently stopped seeing --define symbols (a `static readonly` field froze the
    // preprocessor set at type-init before BcCompiler.SetExtraPreprocessorSymbols ran). A
    // memo keyed on text alone would reproduce that bug through a different door: two
    // calls for the same text under two different --define sets would incorrectly share
    // one cached tree. AlParseOptions (see above) is still recomputed on every miss:
    // caching here changes WHEN a tree is (re)built, never what determines whether it
    // must be.
    private static string? _lastParsedText;
    private static string[]? _lastParsedSymbols;
    private static IReadOnlyList<NavCA.SyntaxNode> _lastParsedObjects = Array.Empty<NavCA.SyntaxNode>();

    // #2588: a keyed cache BEHIND the single-slot memo above, so a tree survives
    // ResetForReload and a --watch save re-parses only the files that actually moved.
    //
    // The single-slot memo solves "eight extractors, one file" (#1903). It cannot solve
    // "one file edited, N-1 unchanged", because ResetForReload clears _sourceDirs and every
    // parsed dictionary and the AddSourceDirs that follows walks the whole tree again — so
    // every file misses the one slot and is re-parsed to service a one-file edit.
    //
    // Keyed on the CONTENT HASH, not the path and not the mtime. A git checkout, a formatter
    // no-op or an editor autosave rewrites identical bytes, and none of those is a reason to
    // re-parse; the same decision RadWorkspace.HashSourceTree already makes. The active
    // preprocessor symbols are part of the key for the reason the single-slot memo documents
    // above: #1900 was a parser that silently stopped seeing --define symbols, and a cache
    // keyed on content alone would reintroduce it through a different door.
    //
    // Nothing here needs invalidating. The key IS the content, so a stale entry cannot be
    // served — an edited file simply has a different key, and the entry for its previous
    // content is unreachable rather than wrong. That is what makes this safe to survive a
    // reload when the parsed dictionaries deliberately do not.
    //
    // Bounded by the total SOURCE bytes it has admitted, evicted in insertion order. A
    // syntax tree is much larger than the text it came from, so the budget is a proxy, not
    // an accounting — see DefaultTreeCacheSourceBudgetBytes for the measured ratio behind
    // the default. AL_RUNNER_PARSE_TREE_CACHE_BYTES overrides it; 0 disables the cache
    // entirely, which is the control the perf measurement uses.
    /// <summary>
    /// (content hash, active preprocessor symbols) — the pair that determines a parse.
    /// Returns null when the cache is disabled, so a disabled cache costs no hashing.
    /// </summary>
    private static string? TreeCacheKey(string text, string[] symbols)
    {
        if (TreeCacheBudgetBytes() == 0) return null;
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        return symbols.Length == 0 ? hash : hash + "|" + string.Join(",", symbols);
    }

    private readonly record struct TreeCacheEntry(IReadOnlyList<NavCA.SyntaxNode> Objects, int SourceLength);

    private static readonly Dictionary<string, TreeCacheEntry> _treeCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> _treeCacheOrder = new();
    private static long _treeCacheSourceBytes;

    /// <summary>
    /// Number of times <see cref="ParseAlObjects"/> served the keyed tree cache instead of
    /// building a tree. Exposed for the same reason <see cref="ParseObjectTextCallCount"/>
    /// is: the proving test asserts counts, never durations.
    /// </summary>
    internal static int ParseTreeCacheHitCount { get; private set; }

    /// <summary>Drop the keyed tree cache. Test-only seam; the cache is content-keyed and
    /// never needs clearing for correctness.</summary>
    internal static void ClearParseTreeCacheForTests()
    {
        _treeCache.Clear();
        _treeCacheOrder.Clear();
        _treeCacheSourceBytes = 0;
    }

    private const long DefaultTreeCacheSourceBudgetBytes = 8L * 1024 * 1024;

    private static long TreeCacheBudgetBytes()
    {
        var raw = Environment.GetEnvironmentVariable("AL_RUNNER_PARSE_TREE_CACHE_BYTES");
        return long.TryParse(raw, out var v) && v >= 0 ? v : DefaultTreeCacheSourceBudgetBytes;
    }

    private static void AdmitToTreeCache(string key, string text, IReadOnlyList<NavCA.SyntaxNode> objects)
    {
        var budget = TreeCacheBudgetBytes();
        if (budget == 0) return;
        if (text.Length > budget) return;   // one file larger than the whole budget

        if (!_treeCache.TryAdd(key, new TreeCacheEntry(objects, text.Length))) return;
        _treeCacheOrder.Enqueue(key);
        _treeCacheSourceBytes += text.Length;

        // Insertion-order eviction. The entry just admitted is never the one evicted, so a
        // budget smaller than one file degrades to "cache the newest file" rather than to an
        // empty cache that still pays the bookkeeping.
        while (_treeCacheSourceBytes > budget && _treeCacheOrder.Count > 1)
        {
            var oldest = _treeCacheOrder.Dequeue();
            if (_treeCache.Remove(oldest, out var evicted))
                _treeCacheSourceBytes -= evicted.SourceLength;
        }
    }

    /// <summary>
    /// Number of times <see cref="ParseAlObjects"/> has actually built a syntax tree (a real
    /// <c>SyntaxTree.ParseObjectText</c> call), as opposed to serving the single-slot memo
    /// above. #1903's proving test asserts THIS — a count, never a duration — to pin that N
    /// files registered through the eight extractors costs N tree builds, not 8N. Mirrors
    /// the discipline <see cref="PopulateNclMetadataCacheCallCount"/> established for #1833.
    /// </summary>
    internal static int ParseObjectTextCallCount { get; private set; }

    /// <summary>
    /// Parses every AL object in <paramref name="text"/> with BC's own parser and returns the
    /// object declarations. Never throws: this is fed arbitrary .al text — pages, codeunits,
    /// AL sliced out of dependency .app archives, and synthesized table text — and a parse it
    /// cannot make sense of must leave the caller's state untouched, not break the run.
    /// Diagnostics are ignored on purpose: a file that fails to compile for an unrelated
    /// reason still yields a usable table declaration, which is what the regexes did too.
    /// </summary>
    private static IReadOnlyList<NavCA.SyntaxNode> ParseAlObjects(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // GetExtraPreprocessorSymbols() is a lock plus a sorted copy of a handful of
        // strings (see AlParseOptions above) — cheap enough to call on every ParseAlObjects
        // invocation just to test the memo key, including on the 7-out-of-8 calls that end
        // up being cache hits.
        var symbols = AlRunner.BcCompiler.GetExtraPreprocessorSymbols().ToArray();
        if (_lastParsedText == text && _lastParsedSymbols != null &&
            symbols.AsSpan().SequenceEqual(_lastParsedSymbols))
        {
            return _lastParsedObjects;
        }

        // #2588: the keyed cache. Only consulted on a single-slot MISS, so the eight
        // extractors running back-to-back over one file still cost one dictionary probe
        // rather than eight hashes of the same text.
        var cacheKey = TreeCacheKey(text, symbols);
        if (cacheKey != null && _treeCache.TryGetValue(cacheKey, out var cached))
        {
            ParseTreeCacheHitCount++;
            _lastParsedText = text;
            _lastParsedSymbols = symbols;
            _lastParsedObjects = cached.Objects;
            return cached.Objects;
        }

        try
        {
            ParseObjectTextCallCount++;
            var tree = NavSyntax.SyntaxTree.ParseObjectText(
                text, path: "", encoding: null!, AlParseOptions, default);
            IReadOnlyList<NavCA.SyntaxNode> objects = tree.GetRoot() is NavSyntax.CompilationUnitSyntax root
                ? root.ChildNodes().ToList()
                : Array.Empty<NavCA.SyntaxNode>();
            _lastParsedText = text;
            _lastParsedSymbols = symbols;
            _lastParsedObjects = objects;
            if (cacheKey != null) AdmitToTreeCache(cacheKey, text, objects);
            return objects;
        }
        catch
        {
            // A malformed input is not a runner gap — the AL simply is not parseable, and the
            // caller's contract is "extract what you can". Callers that need a table and do
            // not get one already report that themselves ("AL source not parsed"). Don't let
            // a failed parse poison the memo as if it were this (text, symbols) pair's real
            // answer — clear the slot so the NEXT call (for unrelated input) can't accidentally
            // key-match leftover state from before the exception.
            _lastParsedText = null;
            _lastParsedSymbols = null;
            return [];
        }
    }

    /// <summary>
    /// Builds a <see cref="ParsedField"/> from one <c>field(...)</c> declaration.
    /// <para>Identical for a `table` field and a `tableextension` field: AL declares them the
    /// same way and BC gives them the same metadata. They used to differ — extension fields
    /// were parsed without OptionMembers and without AutoIncrement (#1711), which left an
    /// Option field added by a tableextension with no option string, so NCLOptionMetadata saw
    /// the wrong member count (#1674's defect class), and an AutoIncrement field added by a
    /// tableextension with no autoincrement semantics at all.</para>
    /// </summary>
    private static ParsedField? ParseFieldSyntax(NavSyntax.FieldSyntax f)
    {
        if (f.No.Value is not int fid) return null;
        var fname = IdentText(f.Name);
        var ftype = f.Type?.ToString()?.Trim() ?? "";
        int length = 0;
        var lm = RxTypeLength.Match(ftype);
        if (lm.Success) int.TryParse(lm.Groups[1].Value, out length);

        var props = f.PropertyList;
        bool isFlowField = PropIs(props, "FieldClass", "FlowField");
        // #1716 — FlowFilter is its own FieldClass, not a Normal field that happens to be
        // named "...Filter". BC keys two behaviours off it: DataHelper.PassesFieldFilters
        // SKIPS filters on FlowFilter fields (so `SetRange("Date Filter", ...)` never
        // excludes rows of the table declaring it), and FlowFieldsHelper dispatches
        // `field(...)` where-conditions on the value field's FieldClass — FlowFilter reads
        // the caller's FILTER, Normal reads the stored value. Leaving it Normal produced
        // both failures at once: the parent row vanished under its own flow filter, and the
        // FlowField compared the source column against a blank.
        bool isFlowFilter = PropIs(props, "FieldClass", "FlowFilter");

        ParsedCalcFormula? calcFormula = null;
        if (isFlowField)
            calcFormula = CalcFormulaFrom(PropValue(props, "CalcFormula"));

        // Option-type fields: OptionMembers is the comma-separated list BC's
        // NCLOptionMetadata constructor expects. Tokens are trimmed; empty entries are kept
        // (BC allows blank members, and #1674 depends on that). A member declared with AL's
        // quoted-identifier form (`" "`, `"Work Center"`) still carries its quotes after
        // Split+Trim -- BC's own compiler strips that quoting, so a token whose member is
        // named " " reached NCLOptionMetadata as the three characters `" "` instead of a
        // single space (#2345). Unquote (shared with IdentText above) removes one matched
        // pair of double quotes per token, same as any other AL identifier.
        string? optionMembers = null;
        if (ftype.Equals("Option", StringComparison.OrdinalIgnoreCase)
            && PropValue(props, "OptionMembers") is { } om)
        {
            optionMembers = string.Join(",", om.ToString().Split(',').Select(s => Unquote(s.Trim())));
        }

        // InitValue is passed to MetaField.initValue as RAW AL TEXT, quotes and all, because
        // NclMetaTableBuilder does the type-aware unquoting downstream — that split is what
        // #1674's blank-enum fix depends on. Do not "clean" it here without deleting the
        // stripping there in the same change.
        string? initValueText = PropValue(props, "InitValue")?.ToString()?.Trim();

        bool isAutoIncrement = PropIs(props, "AutoIncrement", "true");
        var caption = CaptionFrom(PropValue(props, "Caption"));

        // ObsoleteState / ObsoleteReason (#1780): the Field virtual table (2000000041) reports
        // these via BC's own FieldDataProvider.GetFieldRecordBuffer, which reads them off the
        // NCLMetaField that CreateFromMetaTable builds from OUR MetaField — so capturing the AL
        // declaration here and passing it to MetaField's obsoleteState/obsoleteReason ctor
        // params (see BuildMetaField) is the whole fix; BC's own factory does the rest.
        // ObsoleteState is an EnumPropertyValueSyntax whose text IS the member name ("Removed",
        // "Pending", "PendingMove", "Moved") — undeclared leaves it null, which the builder
        // treats as the AL/BC default "No". ObsoleteReason is a plain (non-multilanguage)
        // single-quoted string — ConstValueText's quote-stripping (shared with const(...)
        // conditions and InitValue) applies unchanged.
        var obsoleteStateText = PropValue(props, "ObsoleteState")?.ToString()?.Trim();
        var obsoleteState = string.IsNullOrEmpty(obsoleteStateText) ? "No" : obsoleteStateText;
        var obsoleteReasonRaw = PropValue(props, "ObsoleteReason")?.ToString();
        var obsoleteReason = obsoleteReasonRaw == null ? null : ConstValueText(obsoleteReasonRaw);

        // TableRelation: captured as a list of ARMS — the plain `Table` / `Table.Field`
        // shape is one condition-less arm, an `if (...) ... else ...` chain is one arm per
        // link (#1737, extending #1730's unconditional capture). Each arm carries its
        // if-conditions (fields of THIS table) and its where(...) filters (fields of the
        // related table); NavRecord.UpdateReferencesOnRenameAsync evaluates both exactly as
        // real BC does. A shape this code cannot carry faithfully refuses the WHOLE
        // relation: half-capturing (an arm without its conditions) would make Rename
        // rewrite rows real BC leaves alone — a silent wrong write, worse than the old
        // behaviour (no propagation).
        List<ParsedRelationArm>? relationArms = null;
        bool relationValidate = !PropIs(props, "ValidateTableRelation", "false");
        if (!isFlowField && !isFlowFilter
            && PropValue(props, "TableRelation") is NavSyntax.TableRelationPropertyValueSyntax tr)
        {
            relationArms = ParseRelationArms(tr, fname);
        }

        // MinValue / MaxValue (#2495): raw AL expression text, unquoted — these are numeric
        // literals (e.g. `MinValue = 0;`), never a quoted string, so no unescaping is needed.
        // Passed through to MetaField.minValue/maxValue (both plain strings); NCL's own
        // TestPage-control-write validation is what parses and enforces them, so the runner's
        // only job is carrying the declared text.
        var minValue = PropValue(props, "MinValue")?.ToString()?.Trim();
        var maxValue = PropValue(props, "MaxValue")?.ToString()?.Trim();

        return new ParsedField(fid, fname, ftype, length, isFlowField, calcFormula,
            optionMembers, initValueText, isAutoIncrement, caption,
            relationArms, relationValidate, isFlowFilter, obsoleteState, obsoleteReason,
            minValue, maxValue);
    }

    /// <summary>
    /// Walks a TableRelation's if/else chain into its arms. Each link of the chain is a
    /// <c>TableRelationPropertyValueSyntax</c>; the terminal <c>else</c> (and the plain,
    /// unconditional shape) is simply a link with no <c>IfExpression</c> — which is also
    /// exactly how real BC treats it: the else arm carries NO condition, not the complement
    /// of the earlier arms' conditions (verified against a real service tier; see corpus
    /// codeunit 60239, Record_Rename_ConditionalRelation_ElseTableRename_UpdatesIfArmRowsToo).
    /// Returns null — refusing the whole relation — on any arm this representation cannot
    /// carry faithfully.
    /// </summary>
    private static List<ParsedRelationArm>? ParseRelationArms(
        NavSyntax.TableRelationPropertyValueSyntax tr, string fieldName)
    {
        var arms = new List<ParsedRelationArm>();
        for (var node = tr; node != null; node = node.ElseExpression?.ElseTableRelationCondition)
        {
            var parts = RelationTargetNameParts(node.RelatedTableField);
            // 1 part = table; 2 parts = table + field, OR namespace + table — BuildMetaFieldRelations
            // tries both readings and that ambiguity is already its job. RelationTargetNameParts drops
            // any leading namespace segments (#2851), so reaching here with any other count means the
            // name did not read as a name at all, and the relation stays uncaptured.
            if (parts.Count is not (1 or 2))
            {
                Console.Error.WriteLine(
                    $"[TableRelation] REFUSED {fieldName}: {parts.Count}-part related-table name '{node.RelatedTableField}'");
                return null;
            }
            // The two lists differ in ONE way, and it is not cosmetic (#2518): a where(...)
            // filter may carry a `field(...)` link, an if(...) condition may not. BC models
            // the first as MetaFilter/FilterType.FIELD → NCLMetaFilterField and the second as
            // MetaCondition, whose NCLMetaFilter.CreateFromMetaCondition has CONST and FILTER
            // cases only and throws NotSupportedException on FIELD.
            var conditions = RelationConditionList(node.IfExpression?.IfTableRelationCondition,
                fieldName, allowFieldLinks: false);
            var filters = RelationConditionList(node.TableFilter?.Filter,
                fieldName, allowFieldLinks: true);
            if (conditions == null || filters == null) return null;
            arms.Add(new ParsedRelationArm(parts[0], parts.Count == 2 ? parts[1] : null,
                conditions, filters));
        }
        return arms;
    }

    /// <summary>
    /// The conditions of an <c>if (...)</c> arm, or the entries of a <c>where(...)</c>
    /// filter — the same <c>TableFilterExpressionSyntax</c> node, and the same shapes as a
    /// CalcFormula's where, so they reuse <see cref="ParsedCalcFilter"/>.
    /// <para><c>const(...)</c> and <c>filter(...)</c> are carried in both positions: they are
    /// what <c>MetaCondition</c> / <c>MetaFilter</c> hold as evaluable text.</para>
    /// <para><paramref name="allowFieldLinks"/> is the asymmetry, and it mirrors BC's own
    /// metadata rather than a runner preference (#2518). A <c>where(...)</c> entry becomes a
    /// <c>MetaFilter</c>, and <c>NCLMetaFilter.CreateFromMetaFilter</c> has a
    /// <c>FilterType.FIELD</c> case building an <c>NCLMetaFilterField</c> whose value is read
    /// from the referencing row at evaluation time — so <c>field(...)</c> and the three
    /// flow-filter spellings around it are representable there. An <c>if (...)</c> condition
    /// becomes a <c>MetaCondition</c>, and <c>NCLMetaFilter.CreateFromMetaCondition</c> has
    /// CONST and FILTER cases only, throwing <c>NotSupportedException</c> on FIELD; carrying
    /// one there would build metadata BC cannot load, so it still refuses the whole relation.
    /// </para>
    /// <para>Refusing returns null — the WHOLE relation is dropped, never half-captured. That
    /// is deliberate for an unrepresentable shape, but it is also why this list matters: a
    /// dropped relation leaves <c>FieldRef.Relation</c> answering 0, which is
    /// indistinguishable from "no TableRelation declared". Before #2518 every
    /// <c>where(... = field(...))</c> relation was dropped for exactly that reason — 826 of
    /// them in Base Application 28.1, including <c>Customer.City</c>.</para>
    /// </summary>
    private static List<ParsedCalcFilter>? RelationConditionList(
        NavSyntax.TableFilterExpressionSyntax? filter, string fieldName, bool allowFieldLinks)
    {
        var list = new List<ParsedCalcFilter>();
        if (filter == null) return list;
        foreach (var cond in filter.Conditions)
        {
            switch (cond)
            {
                // Kind = const(A)
                case NavSyntax.ConstExpressionSyntax ce:
                    list.Add(new ParsedCalcFilter(
                        Unquote(ce.LeftHandSide?.ToString()?.Trim() ?? ""),
                        ParsedCalcFilterKind.Const,
                        Value: ConstValueText(ce.Identifier?.ToString())));
                    break;

                // Status = filter(Open|Released)
                case NavSyntax.FilterExpressionSyntax fe:
                    list.Add(new ParsedCalcFilter(
                        Unquote(fe.LeftHandSide?.ToString()?.Trim() ?? ""),
                        ParsedCalcFilterKind.Filter,
                        Value: FilterValueText(fe.Filter?.ToString())));
                    break;

                // "Country/Region Code" = field("Country/Region Code") — and the three
                // flow-filter spellings, which BC models as the SAME FIELD link plus
                // MetaFilter's two mode flags (#1716), not as separate kinds.
                case NavSyntax.SimpleFieldExpressionSyntax sfe when allowFieldLinks:
                    list.Add(new ParsedCalcFilter(
                        Unquote(sfe.LeftHandSide?.ToString()?.Trim() ?? ""),
                        ParsedCalcFilterKind.Field,
                        ParentFieldName: Unquote(sfe.Identifier?.ToString()?.Trim() ?? "")));
                    break;

                case NavSyntax.FieldFilterExpressionSyntax ffe when allowFieldLinks:
                    list.Add(new ParsedCalcFilter(
                        Unquote(ffe.LeftHandSide?.ToString()?.Trim() ?? ""),
                        ParsedCalcFilterKind.Field,
                        ParentFieldName: Unquote(ffe.Identifier?.ToString()?.Trim() ?? ""),
                        ValueIsFilter: true));
                    break;

                case NavSyntax.FieldUpperLimitExpressionSyntax ule when allowFieldLinks:
                    list.Add(new ParsedCalcFilter(
                        Unquote(ule.LeftHandSide?.ToString()?.Trim() ?? ""),
                        ParsedCalcFilterKind.Field,
                        ParentFieldName: Unquote(ule.Identifier?.ToString()?.Trim() ?? ""),
                        OnlyMaxLimit: true));
                    break;

                case NavSyntax.FieldUpperLimitFilterExpressionSyntax ulf when allowFieldLinks:
                    list.Add(new ParsedCalcFilter(
                        Unquote(ulf.LeftHandSide?.ToString()?.Trim() ?? ""),
                        ParsedCalcFilterKind.Field,
                        ParentFieldName: Unquote(ulf.Identifier?.ToString()?.Trim() ?? ""),
                        ValueIsFilter: true, OnlyMaxLimit: true));
                    break;

                default:
                    Console.Error.WriteLine(
                        $"[TableRelation] REFUSED {fieldName}: unsupported " +
                        (allowFieldLinks ? "where() entry " : "if() condition ") +
                        $"{cond?.GetType().Name} ({cond})");
                    return null;
            }
        }
        return list;
    }

    /// <summary>Flattens a (possibly qualified) name into its unquoted identifier parts:
    /// <c>"ALT Relation Parent"."Code"</c> → ["ALT Relation Parent", "Code"].</summary>
    private static List<string> NameParts(NavSyntax.NameSyntax? name)
    {
        var parts = new List<string>();
        void Walk(NavSyntax.NameSyntax? n)
        {
            switch (n)
            {
                case NavSyntax.QualifiedNameSyntax q:
                    Walk(q.Left);
                    if (q.Right != null)
                        parts.Add(Unquote(q.Right.Identifier.ValueText ?? q.Right.Identifier.Text ?? ""));
                    break;
                case NavSyntax.SimpleNameSyntax s:
                    parts.Add(Unquote(s.Identifier.ValueText ?? s.Identifier.Text ?? ""));
                    break;
            }
        }
        Walk(name);
        return parts;
    }

    /// <summary>
    /// The parts of a TableRelation TARGET name, with any namespace qualification dropped:
    /// at most the last two (#2851).
    /// <para>AL lets a relation name its table through the table's namespace, and Base
    /// Application does — <c>Microsoft.Manufacturing.Capacity."Capacity Ledger Entry"</c>,
    /// <c>System.Azure.Identity.Plan."Plan ID"</c>, and six other shapes across 8 fields of
    /// 28.1.49838.53910. Those used to be refused for having three or more parts, which drops
    /// the WHOLE relation, so <c>FieldRef.Relation</c> answered 0 — the value that also means
    /// "this field declares no TableRelation" (#2851, the silent zero #2518 was reported as).
    /// </para>
    /// <para>Object names are global in AL — the namespace organises source, it does not
    /// namespace the name a relation resolves by — so the namespace segments carry no
    /// information the runner needs and are dropped here rather than plumbed through
    /// <see cref="ParsedRelationArm"/> and the on-disk symbol cache.</para>
    /// <para>Keeping the last TWO, not the last one, is what makes both shipped shapes work:
    /// the last part is the TABLE in <c>NS.NS.Table</c> but the FIELD in
    /// <c>NS.NS.Table."Field"</c>, and nothing here can tell them apart without symbol
    /// resolution. That ambiguity is not new and is not this method's to settle —
    /// <c>BuildMetaFieldRelations</c> already disambiguates a two-part name by trying
    /// <c>Table.Field</c> first and falling back to reading the last part as the table, so
    /// handing it the last two parts routes the namespace-qualified shapes through the resolver
    /// that already exists. Checked against the real 28.1 closure (Base Application + System
    /// Application + Business Foundation + System.app): every namespace segment Base
    /// Application uses in this position — <c>Capacity</c>, <c>Forecast</c>, <c>Identity</c>,
    /// <c>Reflection</c> — is not a table name, so the fallback fires and lands on the right
    /// table, while <c>Plan</c>, <c>AllObjWithCaption</c> and <c>Production Forecast Name</c>
    /// are real tables that really do carry the field named after them.</para>
    /// </summary>
    private static List<string> RelationTargetNameParts(NavSyntax.NameSyntax? name)
    {
        var parts = NameParts(name);
        return parts.Count <= 2 ? parts : parts.GetRange(parts.Count - 2, 2);
    }

    /// <summary>
    /// The last part of a (possibly namespace-qualified) name — the table name a CalcFormula
    /// source resolves by (#2851).
    /// <para>Unlike a TableRelation target this is unambiguous: <c>count</c>/<c>exist</c> carry
    /// a table and no field, and <c>sum</c>/<c>lookup</c>/<c>average</c>/<c>min</c>/<c>max</c>
    /// carry <c>Table.Field</c> as a qualified name whose Left half is the table, so in both
    /// positions the last part IS the table and no fallback reading is needed.</para>
    /// <para>This read the name's whole text before #2851, so a namespace-qualified source
    /// arrived as the literal <c>Microsoft.Manufacturing.Forecast."Production Forecast Entry"</c>,
    /// matched no table, and <c>BuildMetaCalcFormula</c> returned null — a FlowField that
    /// silently never computes. Four Base Application 28.1 FlowFields are that shape:
    /// Gen. Journal Line and Purchase Line's "Alloc. Acc. Modified by User",
    /// Item."Prod. Forecast Quantity (Base)" and User Group Plan."Plan Name".</para>
    /// <para><paramref name="fallbackText"/> is the pre-#2851 expression, used when the node is
    /// a NameSyntax shape <see cref="NameParts"/> does not walk. Without it an unrecognised
    /// shape would yield the empty string, which <c>CalcFormulaFrom</c> refuses — turning a
    /// formula that used to parse into a refused one, a regression rather than a fix.</para>
    /// </summary>
    private static string LastNamePart(NavSyntax.NameSyntax? name, string? fallbackText)
    {
        var parts = NameParts(name);
        return parts.Count > 0 ? parts[^1] : Unquote(fallbackText?.Trim() ?? "");
    }

    private static void TryParseTableFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            if (obj is not NavSyntax.TableSyntax table) continue;
            if (table.ObjectId?.Value.Value is not int tableId) continue;
            var tableName = IdentText(table.Name);

            var fields = new List<ParsedField>();
            if (table.Fields != null)
                foreach (var f in table.Fields.Fields)
                    if (ParseFieldSyntax(f) is { } pf)
                        fields.Add(pf);

            // First key is the PK; all subsequent keys are secondary.
            var pkFieldIds = new List<int>();
            var secondaryKeys = new List<ParsedKey>();
            bool firstKey = true;
            if (table.Keys != null)
            {
                foreach (var k in table.Keys.Keys)
                {
                    var keyName = IdentText(k.Name);
                    var keyFieldIds = new List<int>();
                    foreach (var kf in k.Fields)
                    {
                        var kn = IdentText(kf as NavSyntax.IdentifierNameSyntax);
                        var f = fields.FirstOrDefault(x =>
                            string.Equals(x.FieldName, kn, StringComparison.OrdinalIgnoreCase));
                        if (f != null) keyFieldIds.Add(f.FieldId);
                    }
                    if (firstKey)
                    {
                        pkFieldIds.AddRange(keyFieldIds);
                        firstKey = false;
                    }
                    else if (keyFieldIds.Count > 0)
                    {
                        secondaryKeys.Add(new ParsedKey(keyName, keyFieldIds));
                    }
                }
            }
            // Fallback: first field is PK
            if (pkFieldIds.Count == 0 && fields.Count > 0)
                pkFieldIds.Add(fields[0].FieldId);

            // DataPerCompany: AL's default is TRUE, so only the explicit opt-out is parsed.
            // MetaTable's own ctor default for isDataPerCompany is false — the opposite of
            // AL's — and BC's RecordImplementation.ChangeCompany returns true immediately for
            // a table that is not per-company.
            var isTableTypeTemporary = PropIs(table.PropertyList, "TableType", "Temporary");
            var tableTypeName = PropValue(table.PropertyList, "TableType")?.ToString()?.Trim();
            var dataPerCompany = !PropIs(table.PropertyList, "DataPerCompany", "false");
            // LookupPageId / DrillDownPageId feed the Table Metadata (2000000136) virtual
            // table. Kept as the written reference and resolved later: a page declared after
            // this table in compile order is not in the page inventory yet.
            var lookupPage = PageRefText(PropValue(table.PropertyList, "LookupPageId"));
            var drillDownPage = PageRefText(PropValue(table.PropertyList, "DrillDownPageId"));
            // DataClassification / ExternalName feed the Table Metadata (2000000136) columns of
            // the same name (#2938). Both are kept AS WRITTEN and null when undeclared: the
            // defaults (CustomerContent, blank) are applied at row-build time, so "declares
            // none" stays distinguishable from "declares the default" all the way through.
            // CustomerContent there is a MEASUREMENT, not AL documentation — Microsoft
            // documents ToBeClassified — settled on sixteen green BC legs by
            // StefanMaron/BusinessCentral.AL.Language.Tests#191 and cited at
            // RecordPatches.TableMetadataVirtualTable.cs's AlDefaultDataClassification (#3019).
            var dataClassification = PropValue(table.PropertyList, "DataClassification")?.ToString()?.Trim();
            // AlStringLiteralText, not the raw node text: ExternalName is an AL STRING LITERAL
            // (ExternalName = 'alt_entity'), so the node stringifies WITH its single quotes and a
            // raw read hands out "'alt_entity'" — measured, that is exactly what the Table
            // Metadata column reported before this call was corrected. Unquote() strips only
            // DOUBLE quotes and PropertyTextFrom only unwraps a LabelPropertyValueSyntax, which
            // this value is not; neither would have caught it. DataClassification above is an
            // identifier, not a literal, so it needs none of this.
            var externalName = AlStringLiteralText(PropValue(table.PropertyList, "ExternalName"));
            _parsedTables[tableId] = new ParsedTable(tableId, tableName, fields, pkFieldIds,
                secondaryKeys, isTableTypeTemporary, dataPerCompany, lookupPage, drillDownPage,
                TableTypeName: string.IsNullOrWhiteSpace(tableTypeName) ? null : tableTypeName.Trim(),
                DataClassificationName: string.IsNullOrWhiteSpace(dataClassification) ? null : dataClassification,
                ExternalName: string.IsNullOrWhiteSpace(externalName) ? null : externalName);
        }
    }

    private static void TryParseTableExtensionFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            if (obj is not NavSyntax.TableExtensionSyntax ext) continue;
            if (ext.ObjectId?.Value.Value is not int extId) continue;
            var extName = IdentText(ext.Name);
            var baseName = Unquote(ext.BaseObject?.ToString()?.Trim() ?? "");

            // Extension fields are parsed exactly like base-table fields — see
            // ParseFieldSyntax for what they used to lose (#1711).
            var fields = new List<ParsedField>();
            // OfType<FieldSyntax>: a tableextension's field list also holds `modify(...)`
            // entries, which declare no new field. The regex only ever matched
            // `field(N; Name; Type)` either, so this keeps the same set.
            if (ext.Fields != null)
                foreach (var f in ext.Fields.Fields.OfType<NavSyntax.FieldSyntax>())
                    if (ParseFieldSyntax(f) is { } pf)
                        fields.Add(pf);

            Console.Error.WriteLine($"[TableExt] parsed extension {extId} '{extName}' extends '{baseName}' with {fields.Count} fields");

            // Merge into _parsedExtensionFields, record the extension id (so its emitted
            // TableExtension{extId} CLR type can be instantiated and registered on each
            // record of the base table — record-level triggers + field-validate dispatch),
            // and evict any already-built NCLMetaTable for the base table so a rebuild picks
            // up these fields. All three steps — including the eviction, whose necessity is
            // explained on MergeExtensionFields itself (#2126) — happen atomically in the
            // shared helper so a second writer (RecordPatches.BcAppFallback.cs's
            // EnsureBcSymbolExtensionIndex) can't repeat this file's own former omission of it.
            MergeExtensionFields(baseName, extId, fields);
        }
    }

    /// <summary>
    /// Drop any cached NCLMetaTable built for <paramref name="baseTableName"/> before its
    /// tableextension fields were known, so the next lookup rebuilds it with them merged.
    /// No-op when the table has not been built yet (the common, in-order case).
    ///
    /// Also drops the table from <see cref="_fieldTriggersWiredTables"/> (issue #2463):
    /// <c>WireFieldTriggerHandlersForTable</c>/<c>WireFieldTriggerHandlersAll</c> wire a
    /// table's OWN compiled <c>[FieldTriggerHandler]</c> OnValidate/OnLookup methods onto
    /// the CURRENT NCLMetaTable instance's NCLMetaField.EventTriggerDataValue, and guard
    /// against re-wiring with a tableId-keyed "already wired" set. That guard does not know
    /// the metatable it wired was just replaced by a brand-new instance here — the new
    /// instance's fields carry no ValidateHandler at all, so every OnValidate body on that
    /// table (even on a precompiled Base App table with no involvement in the eviction)
    /// silently stops running for the rest of the process. Measured on a precompiled table
    /// (Purchase Line) evicted+rebuilt mid-run by an unrelated tableextension field merge:
    /// `Validate(Quantity, ...)` set the field but every side effect the compiled trigger
    /// computes (Outstanding Quantity, Qty. to Receive, Qty. to Invoice, ...) stayed at its
    /// pre-Validate value, with no error and no diagnostic — the same "wiring survives the
    /// table it was wired for" family as #2197/#2412 (table-level DB triggers) and #2453
    /// (TestPageFactory's record-construction chokepoint), just for the table's own
    /// compiled field-trigger methods instead of an event subscriber.
    ///
    /// Also purges the table from <see cref="EventSubscriberPatches"/>'s
    /// <c>_injectedSubscriberMethods</c> (issue #2510, the subscriber-side sibling of #2463
    /// left unfixed by #2506): that set is keyed by the subscriber's MethodInfo only, with no
    /// per-table index, so an event subscriber ([EventSubscriber] on Insert/Modify/Delete/
    /// Rename or on a field's OnBefore/OnAfterValidateEvent) already injected onto the OLD
    /// instance's event scope stayed marked "already injected" across this same rebuild and
    /// was silently never appended to the NEW instance's event scope — no error, no
    /// diagnostic, subscriber just stops firing for the rest of the process.
    /// </summary>
    private static void EvictCachedMetaTableForBaseTable(string baseTableName)
    {
        foreach (var kvp in _parsedTables)
        {
            if (!string.Equals(kvp.Value.TableName, baseTableName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_metaTableCache.TryRemove(kvp.Key, out _))
            {
                EventSubscriberPatches.ForgetInjectedForTable(kvp.Key);
                _fieldTriggersWiredTables.TryRemove(kvp.Key, out _);
                Console.Error.WriteLine(
                    $"[TableExt] evicted stale NCLMetaTable {kvp.Key} '{baseTableName}' " +
                    $"(built before its tableextension fields were parsed)");
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="ParsedCalcFormula"/> from a CalcFormula property value node.
    /// <para>AL has two shapes and the parser gives them two node types:
    /// <c>sum/average/min/max/lookup</c> carry a qualified <c>Table.Field</c>
    /// (<see cref="NavSyntax.FieldCalculationFormulaSyntax"/>), while <c>count/exist</c> carry a
    /// table alone (<see cref="NavSyntax.TableCalculationFormulaSyntax"/>) and no field.</para>
    /// </summary>
    private static ParsedCalcFormula? CalcFormulaFrom(NavSyntax.PropertyValueSyntax? value)
    {
        string formulaType;
        string sourceTableName;
        string? sourceFieldName;
        NavSyntax.WhereExpressionSyntax? where;
        string signText;

        switch (value)
        {
            case NavSyntax.FieldCalculationFormulaSyntax f:
                formulaType = f.FormulaKeywordToken.ValueText;
                sourceTableName = LastNamePart(f.Field?.Left, f.Field?.Left?.ToString());
                sourceFieldName = f.Field?.Right == null ? null : Unquote(f.Field.Right.ToString().Trim());
                where = f.WhereExpression;
                signText = f.Sign.ValueText ?? "";
                break;
            case NavSyntax.TableCalculationFormulaSyntax t:
                formulaType = t.FormulaKeywordToken.ValueText;
                sourceTableName = LastNamePart(t.Table, t.Table?.ToString());
                sourceFieldName = null; // count/exist have no field part
                where = t.WhereExpression;
                signText = t.Sign.ValueText ?? "";
                break;
            default:
                return null;
        }

        // #1708 — the sign. `-sum(...)` is a negated formula; AL also accepts the no-op `+`.
        // The sign is now carried on ParsedCalcFormula and honoured by NclMetaTableBuilder
        // (MetaCalcFormula.reverseSign) and FlowFieldPatches (NegateResult), so parsing it is
        // no longer a silent lie about the value. A sign token this code has never seen is
        // still refused rather than guessed at.
        bool negated;
        if (signText.Length == 0 || signText == "+") negated = false;
        else if (signText == "-") negated = true;
        else
        {
            Console.Error.WriteLine($"[CalcFormula] REFUSED {sourceTableName}: unrecognised sign '{signText}'");
            return null;
        }

        if (string.IsNullOrEmpty(sourceTableName)) return null;

        // #1709 — every condition shape, selected BY NODE TYPE. Dropping `const(...)` and
        // `filter(...)` made the FlowField aggregate rows AL had excluded: a plausible wrong
        // number, silently (the Base Application writes 1215 const and 285 filter conditions).
        var filters = new List<ParsedCalcFilter>();
        if (where?.Filter != null)
        {
            foreach (var cond in where.Filter.Conditions)
            {
                switch (cond)
                {
                    // "Document No." = field("Code")
                    case NavSyntax.SimpleFieldExpressionSyntax sfe:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(sfe.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Field,
                            ParentFieldName: Unquote(sfe.Identifier?.ToString()?.Trim() ?? "")));
                        break;

                    // Open = const(true)
                    case NavSyntax.ConstExpressionSyntax ce:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(ce.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Const,
                            Value: ConstValueText(ce.Identifier?.ToString())));
                        break;

                    // Status = filter(Open|Released)
                    case NavSyntax.FilterExpressionSyntax fe:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(fe.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Filter,
                            Value: FilterValueText(fe.Filter?.ToString())));
                        break;

                    // #1716 — the three flow-filter forms. All of them are FIELD links in
                    // BC's metadata; what distinguishes them is MetaFilter's two mode flags,
                    // which NCLMetaFilterField.CreateFromMetaFilter turns into
                    // NCLMetaFilterModes.ValueIsFilter / .OnlyMaxLimit. They are NOT a
                    // separate condition kind — modelling them as one is what left them
                    // unapplied — so they are carried as Field plus the flags.
                    //
                    //   "Account No." = field(filter(Totaling))                → ValueIsFilter
                    //   "Posting Date" = field(upperlimit("Date Filter"))      → OnlyMaxLimit
                    //   "Posting Date" = field(upperlimit(filter("Date Filter"))) → both
                    case NavSyntax.FieldFilterExpressionSyntax ffe:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(ffe.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Field,
                            ParentFieldName: Unquote(ffe.Identifier?.ToString()?.Trim() ?? ""),
                            ValueIsFilter: true));
                        break;
                    case NavSyntax.FieldUpperLimitExpressionSyntax ule:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(ule.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Field,
                            ParentFieldName: Unquote(ule.Identifier?.ToString()?.Trim() ?? ""),
                            OnlyMaxLimit: true));
                        break;
                    case NavSyntax.FieldUpperLimitFilterExpressionSyntax ulf:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(ulf.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Field,
                            ParentFieldName: Unquote(ulf.Identifier?.ToString()?.Trim() ?? ""),
                            ValueIsFilter: true, OnlyMaxLimit: true));
                        break;

                    default:
                        // A condition shape this code has never seen. Refuse the WHOLE formula:
                        // aggregating over only the conditions we did understand silently
                        // widens the row set.
                        Console.Error.WriteLine(
                            $"[CalcFormula] REFUSED {sourceTableName}: unsupported where-condition " +
                            $"{cond?.GetType().Name} ({cond})");
                        return null;
                }
            }
        }

        Console.Error.WriteLine($"[CalcFormula] parsed {sourceTableName}.{sourceFieldName ?? "*"} type={formulaType} negated={negated} filters={filters.Count}");
        return new ParsedCalcFormula(formulaType, sourceTableName, sourceFieldName, filters, negated);
    }

    /// <summary>
    /// The literal of a <c>const(...)</c> condition, as text.
    /// <para>Quotes come off for the same reason they do on InitValue (#1674):
    /// <c>NCLMetaFilterConst</c> evaluates this text against the SOURCE field's own type, and
    /// an option member named <c>On Hold</c> is never matched by the 9-character
    /// <c>"On Hold"</c>. AL's doubled-quote escape is resolved with it.</para>
    /// </summary>
    internal static string ConstValueText(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1].Replace("\"\"", "\"");
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s[1..^1].Replace("''", "'");
        return s;
    }

    /// <summary>
    /// The expression of a <c>filter(...)</c> condition, as text BC's filter grammar can read.
    /// <para>#2305. AL quotes an identifier with DOUBLE quotes; BC's filter grammar quotes a
    /// literal with SINGLE quotes and treats <c>"</c> as an ordinary character — Ncl 28.1's
    /// <c>FilterExpressionTokenizer.GetTokens</c> has a case for <c>'</c> (with <c>''</c> as its
    /// escape) and none for <c>"</c>. So each AL quoted identifier is re-quoted here: AL's
    /// <c>""</c> escape is resolved, then any <c>'</c> in the resulting name is doubled.</para>
    /// <para>This is NOT <see cref="ConstValueText"/>'s rule, and the difference matters. A
    /// const value is evaluated as a bare value, so its quotes simply come off; a filter value
    /// is PARSED AS AN EXPRESSION, and Base App members such as
    /// <c>Payment Discount (VAT Excl.)</c> carry parentheses the tokenizer would otherwise read
    /// as grouping. <c>filter(&lt;&gt; " ")</c> is the same hazard with whitespace: bare, the
    /// space is discarded and <c>&lt;&gt;</c> is left with no operand.</para>
    /// <para>Carried through verbatim, <c>filter("Initial Entry")</c> reached the runtime as the
    /// 15-character literal <c>"Initial Entry"</c>, which matches no option member, so
    /// <c>Vendor Ledger Entry."Original Amount"</c> — and the 87 Base App CalcFormulas with a
    /// <c>filter(...)</c> condition, 44 of them quoted — threw
    /// <c>NavInvalidFilterExpressionException</c> instead of calculating.</para>
    /// </summary>
    internal static string FilterValueText(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.IndexOf('"') < 0) return s;

        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '"') { sb.Append(s[i]); continue; }

            var name = new System.Text.StringBuilder();
            var closed = false;
            for (i++; i < s.Length; i++)
            {
                if (s[i] != '"') { name.Append(s[i]); continue; }
                // AL escapes a literal double quote inside an identifier by doubling it.
                if (i + 1 < s.Length && s[i + 1] == '"') { name.Append('"'); i++; continue; }
                closed = true;
                break;
            }
            if (!closed)
            {
                // Unterminated quote: the AL parser would not have produced this node, so
                // there is nothing to convert. Keep the text as written rather than invent
                // a closing quote.
                sb.Append('"').Append(name);
                break;
            }
            sb.Append('\'').Append(name.ToString().Replace("'", "''")).Append('\'');
        }
        return sb.ToString();
    }

    /// <summary>
    /// A report data item's <c>DataItemTableView</c>, rewritten from AL's spelling into the
    /// runtime table-view form BC's own parser reads.
    /// <para>#2305. BC applies this string with <c>NavRecord.ALSetView</c>
    /// (<c>DataItemIterator.ApplyDataItemTableViewAndRequestFormFilters</c>), and
    /// <c>TableViewParser</c>'s grammar uses a DIFFERENT quote character per clause:
    /// <c>SORTING</c> field names and a <c>CONST(...)</c> value read with <c>"</c>, while a
    /// <c>FILTER(...)</c> body reads with <c>'</c> — it is a filter expression, so it goes
    /// through the same grammar as <c>SetFilter</c>. Only the inside of a <c>filter(...)</c>
    /// is therefore rewritten; field names keep their AL quotes because the view grammar
    /// already reads them that way.</para>
    /// <para>Left alone, Base App's Report 321 "Vendor - Balance to Date" —
    /// <c>where("Entry Type" = filter(&lt;&gt; "Initial Entry"))</c> — threw
    /// <c>NavInvalidFilterExpressionException</c> the moment the report ran.</para>
    /// </summary>
    internal static string? TableViewText(string? view)
    {
        if (string.IsNullOrEmpty(view) || view.IndexOf('"') < 0) return view;

        var sb = new System.Text.StringBuilder(view.Length + 8);
        for (int i = 0; i < view.Length; i++)
        {
            // A quoted identifier outside filter(...) is a field name: BC's view grammar
            // reads it with '"' as the quote char, so it is copied through untouched — and
            // skipped over, so the `filter` keyword is never matched inside one (a field
            // named "Date Filter" is otherwise a false positive).
            if (view[i] == '"')
            {
                var end = SkipAlQuoted(view, i);
                sb.Append(view, i, end - i);
                i = end - 1;
                continue;
            }

            if (StartsFilterCall(view, i, out var open))
            {
                var close = MatchingCloseParen(view, open);
                if (close > 0)
                {
                    sb.Append(view, i, open + 1 - i);
                    sb.Append(FilterValueText(view.Substring(open + 1, close - open - 1)));
                    sb.Append(')');
                    i = close;
                    continue;
                }
            }

            sb.Append(view[i]);
        }
        return sb.ToString();
    }

    /// <summary>Index just past the closing quote of the AL quoted identifier starting at
    /// <paramref name="i"/> (which must be the opening <c>"</c>), treating <c>""</c> as an
    /// escape. An unterminated identifier returns the end of the string.</summary>
    private static int SkipAlQuoted(string s, int i)
    {
        for (i++; i < s.Length; i++)
        {
            if (s[i] != '"') continue;
            if (i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }
            return i + 1;
        }
        return s.Length;
    }

    /// <summary>True when <paramref name="i"/> begins the keyword <c>filter</c> as a whole
    /// word followed by <c>(</c>, whose index is returned in <paramref name="open"/>.</summary>
    private static bool StartsFilterCall(string s, int i, out int open)
    {
        open = -1;
        const string Keyword = "filter";
        if (i + Keyword.Length > s.Length) return false;
        if (string.Compare(s, i, Keyword, 0, Keyword.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')) return false;
        var j = i + Keyword.Length;
        while (j < s.Length && s[j] == ' ') j++;
        if (j >= s.Length || s[j] != '(') return false;
        open = j;
        return true;
    }

    /// <summary>Index of the <c>)</c> closing the <c>(</c> at <paramref name="open"/>, or -1.
    /// Parentheses inside a quoted identifier do not count — an option member such as
    /// <c>Payment Discount (VAT Excl.)</c> carries a balanced pair of its own.</summary>
    private static int MatchingCloseParen(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '"') { i = SkipAlQuoted(s, i) - 1; continue; }
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Text overload, kept for <c>BcAppSymbolCache</c>, which reconstructs a CalcFormula from
    /// <c>SymbolReference.json</c> and so has text rather than a node. The text is wrapped in a
    /// minimal table and run through the same parser, so both callers share one implementation.
    /// </summary>
    internal static ParsedCalcFormula? TryParseCalcFormula(string fieldBody)
    {
        if (string.IsNullOrWhiteSpace(fieldBody)) return null;
        // The wrapper id is irrelevant — nothing is registered, the tree is read and dropped.
        var wrapped = "table 50000 __CalcFormulaProbe { fields { field(1; __F; Decimal) { "
                    + fieldBody + " } } }";
        foreach (var obj in ParseAlObjects(wrapped))
        {
            if (obj is not NavSyntax.TableSyntax table || table.Fields == null) continue;
            foreach (var f in table.Fields.Fields)
                if (CalcFormulaFrom(PropValue(f.PropertyList, "CalcFormula")) is { } parsed)
                    return parsed;
        }
        return null;
    }

    /// <summary>
    /// Text overload of <see cref="ParseRelationArms"/>, for <c>BcAppSymbolCache</c> — a
    /// precompiled dependency table's fields arrive from <c>SymbolReference.json</c>, where
    /// <c>TableRelation</c> is a raw property STRING rather than a syntax node. Wrapped in a
    /// minimal table and run through the same parser, exactly as
    /// <see cref="TryParseCalcFormula"/> does, so both callers share one implementation and one
    /// set of refusal rules.
    ///
    /// <para>Issue #2528: without this, every field of every precompiled table reported
    /// <c>FieldRef.Relation = 0</c> and <c>Validate</c> skipped the relation check entirely —
    /// 7,787 Base Application fields carry a <c>TableRelation</c> and the runner read none of
    /// them. A bogus value assigned through <c>Validate</c> was accepted silently, which is a
    /// wrong ANSWER rather than a missing feature.</para>
    ///
    /// <para>Returns null when the property is absent or its shape is refused, which the caller
    /// must treat as "no relation" — the same discipline the node overload applies, and the
    /// reason a refusal is logged there rather than swallowed.</para>
    /// </summary>
    internal static List<ParsedRelationArm>? TryParseRelationArmsText(string? relationText, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(relationText)) return null;
        // The wrapper id and field type are irrelevant — nothing is registered, and a
        // TableRelation's grammar does not depend on the type of the field carrying it. The
        // tree is read and dropped.
        var wrapped = "table 50001 __TableRelationProbe { fields { field(1; __F; Code[20]) { "
                    + "TableRelation = " + relationText + "; } } }";
        foreach (var obj in ParseAlObjects(wrapped))
        {
            if (obj is not NavSyntax.TableSyntax table || table.Fields == null) continue;
            foreach (var f in table.Fields.Fields)
                if (PropValue(f.PropertyList, "TableRelation") is NavSyntax.TableRelationPropertyValueSyntax tr)
                    return ParseRelationArms(tr, fieldName);
        }
        return null;
    }

    /// <summary>
    /// Parses a query column's <c>ColumnFilter</c> property text (#2418) — a comma-separated
    /// list of <c>&lt;QueryColumnName&gt; = const(&lt;value&gt;) / filter(&lt;expr&gt;)</c>
    /// conditions, exactly BC's <c>MetaQueryColumnFilter.TypeOfFilter/Value</c> shape (verified
    /// against the decompiled <c>Microsoft.Dynamics.Nav.Types.Metadata.MetaQueryColumnFilter</c>
    /// and <c>NCLMetaQuery.BuildFilterExpressionCollection</c>).
    /// <para>Text-only (no AL syntax tree involved): the runner never parses a query's AL source
    /// into a query-object syntax tree — queries are read back from the compiled
    /// SymbolReference.json (<c>BcAppSymbolCache</c>), same for source-compiled and precompiled
    /// dep queries — so the property arrives as a plain string in both cases and there is no
    /// tree to reparse against. Reuses <see cref="ConstValueText"/> / <see cref="FilterValueText"/>
    /// so a quoted identifier inside the filter/const value is rewritten exactly the same way a
    /// CalcFormula/TableRelation condition already is (#2305).</para>
    /// <para>The LHS names a QUERY COLUMN of the same query, not a table field — BC's grammar has
    /// no <c>field(...)</c> link form here (there is no "referencing row" for ColumnFilter to
    /// read a field from, unlike CalcFormula's where-clause). A shape this parser does not
    /// recognise refuses the WHOLE property, matching the CalcFormula/TableRelation discipline
    /// (#1709/#1737): applying only the conditions understood would silently narrow-or-widen the
    /// filter versus what BC's compiler actually emitted.</para>
    /// </summary>
    internal static List<ParsedColumnFilter>? TryParseColumnFilterText(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return new List<ParsedColumnFilter>();

        var result = new List<ParsedColumnFilter>();
        foreach (var raw in SplitTopLevelCommas(s))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            var eq = TopLevelIndexOf(entry, '=');
            if (eq < 0)
            {
                Console.Error.WriteLine($"[ColumnFilter] REFUSED '{s}': no '=' in condition '{entry}'");
                return null;
            }
            var fieldName = Unquote(entry[..eq].Trim());
            var rhs = entry[(eq + 1)..].Trim();

            if (TryMatchCallKeyword(rhs, "const", out var constOpen))
            {
                var close = MatchingCloseParen(rhs, constOpen);
                if (close < 0)
                {
                    Console.Error.WriteLine($"[ColumnFilter] REFUSED '{s}': unterminated const(...) in '{entry}'");
                    return null;
                }
                result.Add(new ParsedColumnFilter(fieldName, ParsedColumnFilterKind.Const,
                    ConstValueText(rhs.Substring(constOpen + 1, close - constOpen - 1))));
            }
            else if (TryMatchCallKeyword(rhs, "filter", out var filterOpen))
            {
                var close = MatchingCloseParen(rhs, filterOpen);
                if (close < 0)
                {
                    Console.Error.WriteLine($"[ColumnFilter] REFUSED '{s}': unterminated filter(...) in '{entry}'");
                    return null;
                }
                result.Add(new ParsedColumnFilter(fieldName, ParsedColumnFilterKind.Filter,
                    FilterValueText(rhs.Substring(filterOpen + 1, close - filterOpen - 1))));
            }
            else
            {
                Console.Error.WriteLine($"[ColumnFilter] REFUSED '{s}': unsupported condition '{entry}'");
                return null;
            }
        }
        return result;
    }

    /// <summary>True when <paramref name="s"/> starts (ignoring case) with
    /// <paramref name="keyword"/> immediately followed by optional spaces and <c>(</c>, whose
    /// index is returned in <paramref name="open"/>.</summary>
    private static bool TryMatchCallKeyword(string s, string keyword, out int open)
    {
        open = -1;
        if (s.Length <= keyword.Length) return false;
        if (string.Compare(s, 0, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        var j = keyword.Length;
        while (j < s.Length && s[j] == ' ') j++;
        if (j >= s.Length || s[j] != '(') return false;
        open = j;
        return true;
    }

    /// <summary>Splits <paramref name="s"/> on top-level commas — ones not inside a quoted
    /// identifier or parentheses (so <c>const('A, B')</c> and nested <c>filter(...)</c> value
    /// commas are not mistaken for condition separators).</summary>
    private static List<string> SplitTopLevelCommas(string s)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') { i = SkipAlQuoted(s, i) - 1; continue; }
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// <summary>Index of the first top-level occurrence of <paramref name="c"/> in
    /// <paramref name="s"/> — outside any quoted identifier or parentheses — or -1.</summary>
    private static int TopLevelIndexOf(string s, char c)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') { i = SkipAlQuoted(s, i) - 1; continue; }
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == c && depth == 0) return i;
        }
        return -1;
    }
}

// ─── Data holders ────────────────────────────────────────────────────────────

/// <summary>
/// Which shape of <c>where(...)</c> condition a <see cref="ParsedCalcFilter"/> carries. AL
/// writes three, they are NOT interchangeable, and reading one as another is a silent wrong
/// value (#1709). The flow-filter forms are <see cref="Field"/> plus the mode flags on
/// <see cref="ParsedCalcFilter"/>, exactly as BC's <c>MetaFilter</c> models them (#1716).
/// </summary>
internal enum ParsedCalcFilterKind
{
    /// <summary><c>"Document No." = field("No.")</c> — link to a field of the PARENT record.
    /// Becomes a <c>MetaFilter</c> of FilterType FIELD whose filterValue is the parent field
    /// id.</summary>
    Field,
    /// <summary><c>Open = const(true)</c> — compare against a literal. Becomes FilterType
    /// CONST, filterValue = the literal's text, which <c>NCLMetaFilterConst</c> evaluates
    /// against the SOURCE field's own type.</summary>
    Const,
    /// <summary><c>Status = filter(Open|Released)</c> — a filter EXPRESSION. Becomes
    /// FilterType FILTER, filterValue = the expression text, parsed by BC's own filter parser
    /// (<c>NCLMetaFilterExpression</c>).</summary>
    Filter,
}

/// <param name="SourceFieldName">Field of the FlowField's SOURCE table being constrained.</param>
/// <param name="Kind">Which of AL's condition shapes this is.</param>
/// <param name="ParentFieldName">Set only for <see cref="ParsedCalcFilterKind.Field"/>.</param>
/// <param name="Value">Const literal / filter expression text — set for
/// <see cref="ParsedCalcFilterKind.Const"/> and <see cref="ParsedCalcFilterKind.Filter"/>.</param>
/// <param name="ValueIsFilter">AL's <c>field(filter(X))</c> — the parent field's value is a
/// filter EXPRESSION over the source field, not a value to compare against
/// (<c>MetaFilter.ValueIsFilter</c>). #1716.</param>
/// <param name="OnlyMaxLimit">AL's <c>field(upperlimit(X))</c> — only the upper bound of the
/// resolved filter constrains the source field (<c>MetaFilter.OnlyMaxLimit</c>). #1716.</param>
internal record ParsedCalcFilter(
    string SourceFieldName,
    ParsedCalcFilterKind Kind = ParsedCalcFilterKind.Field,
    string? ParentFieldName = null,
    string? Value = null,
    bool ValueIsFilter = false,
    bool OnlyMaxLimit = false);

/// <param name="Negated">The formula's leading <c>-</c> (#1708), carried through to
/// <c>MetaCalcFormula.reverseSign</c> → <c>NCLMetaCalculationFormula.NegateResult</c>.</param>
internal record ParsedCalcFormula(string FormulaType, string SourceTableName, string? SourceFieldName, List<ParsedCalcFilter> Filters, bool Negated = false);

/// <summary>One arm of a field's TableRelation — the plain shape is a single arm with no
/// conditions. <paramref name="Conditions"/> constrain fields of the REFERENCING table (the
/// one declaring the relation); <paramref name="Filters"/> (from <c>where(...)</c>) constrain
/// fields of the related source table. Both reuse the <see cref="ParsedCalcFilter"/> shapes,
/// restricted to Const/Filter by the parser.</summary>
internal record ParsedRelationArm(string TableName, string? FieldName, List<ParsedCalcFilter> Conditions, List<ParsedCalcFilter> Filters);

/// <param name="ObsoleteState">The AL member name as written — "No" (also the default when
/// the field declares no ObsoleteState at all), "Pending", "Removed", "PendingMove", or
/// "Moved" — matching <c>Microsoft.Dynamics.Nav.Types.Metadata.ObsoleteState</c>'s member
/// names exactly, so <c>Enum.Parse</c> in BuildMetaField needs no translation table (#1780).</param>
/// <param name="ObsoleteReason">The declared reason text, unquoted/unescaped, or null when the
/// field declares no ObsoleteReason (distinct from an explicit empty string).</param>
/// <param name="MinValue">The declared AL expression text for MinValue (e.g. "0"), or null when
/// undeclared. Passed through to MetaField.minValue (a string) unparsed — NCL's own field
/// validation on TestPage SetValue is what evaluates and formats it (#2495).</param>
/// <param name="MaxValue">Same shape as <see cref="MinValue"/>, for MaxValue.</param>
internal record ParsedField(int FieldId, string FieldName, string TypeName, int Length, bool IsFlowField = false, ParsedCalcFormula? CalcFormula = null, string? OptionMembers = null, string? InitValueText = null, bool IsAutoIncrement = false, string? Caption = null, List<ParsedRelationArm>? RelationArms = null, bool RelationValidate = true, bool IsFlowFilter = false, string ObsoleteState = "No", string? ObsoleteReason = null, string? MinValue = null, string? MaxValue = null);
internal record ParsedKey(string Name, List<int> FieldIds);

/// <summary>Which value shape a <see cref="ParsedColumnFilter"/> condition carries — matches
/// <c>Microsoft.Dynamics.Nav.Types.Metadata.FilterType</c>'s CONST/FILTER members exactly
/// (#2418).</summary>
internal enum ParsedColumnFilterKind { Const, Filter }

/// <summary>One condition of a query column's <c>ColumnFilter</c> property (#2418) —
/// <c>&lt;FieldName&gt; = const(&lt;Value&gt;)</c> or <c>&lt;FieldName&gt; = filter(&lt;Value&gt;)</c>.
/// <paramref name="FieldName"/> names a QUERY COLUMN of the same query (resolved to a
/// <c>QueryColumnId</c> by <c>RecordPatches.NclMetaQueryBuilder</c>, which is the only place
/// with every column's id in hand); <paramref name="Value"/> is already unquoted/rewritten by
/// <c>ConstValueText</c> / <c>FilterValueText</c> — ready to hand to
/// <c>MetaQueryColumnFilter.Value</c> as-is.</summary>
internal record ParsedColumnFilter(string FieldName, ParsedColumnFilterKind Kind, string Value);
/// <param name="LookupPageName">The table's declared <c>LookupPageId</c> as WRITTEN — a page
/// name (<c>"Customer List"</c>) or a bare id in text form. Both sources state it by name:
/// AL source writes the reference, and a dependency's SymbolReference.json records
/// <c>LookupPageID</c>/<c>LookupPageId</c> as the page's NAME, never its number (measured
/// against Base Application 28.1). Resolution to an id is therefore deferred to row-build
/// time, where the full page inventory is known. Null means the table declares none, which
/// is not the same as 0 — see <c>RecordPatches.TableMetadataVirtualTable.cs</c>.</param>
/// <param name="DrillDownPageName">Same, for <c>DrillDownPageId</c>.</param>
/// <param name="TableTypeName">The declared <c>TableType</c> as written (<c>CRM</c>,
/// <c>ExternalSQL</c>, <c>Exchange</c>, <c>MicrosoftGraph</c>, <c>Temporary</c>, ...); null when
/// the table declares none, i.e. Normal. <see cref="IsTableTypeTemporary"/> is the older
/// two-valued view of the same property and stays for its existing consumers. BC's
/// DataAccessSource routes every non-Normal, non-Temporary value through a table connection
/// rather than SQL, so collapsing them to Normal silently served CRM tables from a plain
/// temp store (#2725).</param>
/// <param name="DataClassificationName">The declared <c>DataClassification</c> as written
/// (<c>SystemMetadata</c>, <c>OrganizationIdentifiableInformation</c>, ...); null when the
/// table declares none, which AL defaults to <c>CustomerContent</c>. Feeds the Table Metadata
/// (2000000136) column of the same name (#2938) — before that the column answered a constant
/// ordinal 0, i.e. CustomerContent for every table including the 61 Base Application 28.1
/// tables that declare SystemMetadata.</param>
/// <param name="ExternalName">The declared <c>ExternalName</c> — the name the table carries in
/// the external system it is bound to, set on CRM/Exchange/Graph tables (61 of Base
/// Application 28.1's 1523 tables state one, e.g. "CDS BC Table Relation" ->
/// <c>dyn365bc_syntheticrelation</c>). Null when the table declares none, which is the blank
/// the Table Metadata column must then report (#2938).</param>
internal record ParsedTable(int TableId, string TableName,
    List<ParsedField> Fields, List<int> PkFieldIds, List<ParsedKey>? SecondaryKeys = null,
    bool IsTableTypeTemporary = false, bool DataPerCompany = true,
    string? LookupPageName = null, string? DrillDownPageName = null,
    string? TableTypeName = null,
    string? DataClassificationName = null, string? ExternalName = null);
