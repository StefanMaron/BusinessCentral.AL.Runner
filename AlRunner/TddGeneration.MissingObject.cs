// The missing-OBJECT half of --tdd generation: a codeunit (#5431) or a table (#5445) that no app declares, named
// by a variable type (`Missing: Codeunit "No Such Codeunit"`, `T: Record "No Such Table"`, AL0185).
// See docs/tdd-missing-object.md.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavDiag = Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace AlRunner;

/// <summary>An empty codeunit or a table with one placeholder field --tdd added to a file of the compile because no app
/// declares it. <see cref="UseSites"/> are the places the tests reach it from (file path and position), found in the
/// trees before it was added.</summary>
public sealed record TddMissingObject(TddGeneratedMember Member, IReadOnlyList<(string FilePath, int Position)> UseSites)
{
    /// <summary>The <see cref="TddGeneratedMember.MemberKind"/> of a generated codeunit.</summary>
    public const string MemberKind = "codeunit";
    /// <summary>The <see cref="TddGeneratedMember.MemberKind"/> of a generated table (#5445).</summary>
    public const string TableKind = "table";

    /// <summary>True for the kind of an object, as against the kind of a member added to one.</summary>
    public static bool IsObjectKind(string memberKind) => memberKind is MemberKind or TableKind;
}

public static partial class TddGeneration
{
    private const string MissingObjectDiagnosticId = "AL0185";

    /// <summary>What differs between the object kinds this generates: how a variable of the type is written, the
    /// symbol kind, the word the package cache and the messages use, and the id space (ids are per object type).</summary>
    private sealed record MissingKind(string TypeKeyword, string Word, NavCA.SymbolKind Symbol, string PackageKind);
    private static readonly MissingKind CodeunitKind = new("Codeunit", "codeunit", NavCA.SymbolKind.Codeunit, "Codeunit");
    private static readonly MissingKind TableKind = new("Record", "table", NavCA.SymbolKind.Table, "Table");

    /// <summary>The name of the one field a generated table is born with, and its primary key (#5445). AL0366: a table
    /// has to have at least one Normal field, so an empty table is not an option; the existing member generation adds
    /// the fields the tests assign (<c>Rec.Field := x</c>) next to it. AutoIncrement, because no test can name this
    /// field to give each row its own key, and a second Insert would be "already exists" (docs/tdd-missing-object.md).</summary>
    internal const string PlaceholderKeyField = "TDD Key";

    /// <summary>
    /// A codeunit named by a variable type that no app declares gets an EMPTY object with that name, appended to
    /// the first file (in file order) that names it, with the first id of the app's <c>idRanges</c> that no codeunit
    /// of the compile uses. Nothing of any other object is touched. The caller recompiles; the call sites then
    /// report the members they need as AL0132 against the new object, and <see cref="Generate"/> adds them as for
    /// any object of the app.
    /// Refused, with the reason on stderr and the tests failing as before: a name some module or namespace
    /// declares (the object exists, so an empty one would shadow it), a name another source bundle of the run
    /// declares, a name any package of the package folders declares (the dependency app.json leaves out, #5446), a name that
    /// is an id or carries a namespace, and no free id.
    /// Nothing follows the order the compile reported the diagnostics in: sites go in file then position order,
    /// names and ids in name order.
    /// </summary>
    public static IReadOnlyList<TddMissingObject> GenerateMissingObjects(
        NavCA.Compilation compilation, NavSyntax.SyntaxTree[] trees, NavCA.ParseOptions parseOptions,
        IEnumerable<NavDiag.Diagnostic>? emitDiagnostics, string? manifestAppJsonPath)
    {
        var originalTrees = (NavSyntax.SyntaxTree[])trees.Clone();
        var diagnostics = compilation.GetDeclarationDiagnostics().Concat(emitDiagnostics ?? Enumerable.Empty<NavDiag.Diagnostic>());
        // kind word and name (lower case) -> every AL0185 type reference naming it, de-duplicated by position.
        var byName = new SortedDictionary<string, List<(int TreeIdx, NavSyntax.SubtypedDataTypeSyntax Node, string Name, MissingKind Kind)>>(StringComparer.Ordinal);
        var refused = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            if (diag.Severity != NavDiag.DiagnosticSeverity.Error || diag.Id != MissingObjectDiagnosticId) continue;
            if (!diag.Location.IsInSource || diag.Location.SourceTree is not { } tree) continue;
            var treeIdx = Array.IndexOf(originalTrees, tree);
            if (treeIdx < 0) continue;
            var node = tree.GetRoot().FindToken(diag.Location.SourceSpan.Start).Parent?.AncestorsAndSelf()
                .OfType<NavSyntax.SubtypedDataTypeSyntax>().FirstOrDefault();
            if (node == null || node.Span != diag.Location.SourceSpan) continue;
            var kind = new[] { CodeunitKind, TableKind }.FirstOrDefault(k =>
                string.Equals(node.TypeName.ValueText, k.TypeKeyword, StringComparison.OrdinalIgnoreCase));
            if (kind == null) continue;
            var written = node.Subtype.ToString().Trim();
            var name = Unquote(written);
            if (!IsPlainObjectName(written, name))
            {
                if (refused.Add($"{kind.Word}|{name.ToLowerInvariant()}"))
                    Console.Error.WriteLine($"--tdd: {kind.Word} {written} not generated - it is named by an id or with a namespace, and only a plain name is generated");
                continue;
            }
            var key = $"{kind.Word}|{name.ToLowerInvariant()}";
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = new();
            if (!list.Any(e => e.TreeIdx == treeIdx && e.Node.SpanStart == node.SpanStart)) list.Add((treeIdx, node, name, kind));
        }
        if (byName.Count == 0) return Array.Empty<TddMissingObject>();

        var idRanges = TddPrecompiledStub.ReadIdRanges(manifestAppJsonPath);
        var reservedCodeunits = new HashSet<int>();
        var reservedTables = new HashSet<int>();
        var tablesToRefresh = new List<(int Id, string Text)>();
        var appended = new Dictionary<int, System.Text.StringBuilder>();
        var result = new List<TddMissingObject>();
        foreach (var (key, unordered) in byName)
        {
            var sites = unordered.OrderBy(e => e.TreeIdx).ThenBy(e => e.Node.SpanStart).ToList();
            var name = sites[0].Name;
            var kind = sites[0].Kind;
            var isTable = kind == TableKind;
            void Refuse(string reason) => Console.Error.WriteLine($"--tdd: {kind.Word} {Quote(name)} not generated - {reason}");
            try
            {
                if (compilation.GetApplicationObjectTypeSymbolsByNameAcrossModulesAndNamespaces(kind.Symbol, name).Length > 0)
                {
                    Refuse($"a {kind.Word} of that name exists in a module or namespace this code does not reach, and an empty one would shadow it");
                    continue;
                }
                if ((isTable ? TddCrossBundle.RunDeclaresTable(name) : TddCrossBundle.RunDeclaresCodeunit(name)) is { } declaredIn)
                {
                    Refuse($"another bundle of the run declares it ({Path.GetFileName(declaredIn)}) - declare the dependency on it; an empty {kind.Word} would shadow it");
                    continue;
                }
                if (FindPackageDeclaringObject(BcCompiler.ScannedPackagesForTdd(), name, kind.PackageKind) is { } inPackage)
                {
                    Refuse(inPackage);
                    continue;
                }
                var id = isTable
                    ? TddPrecompiledStub.FreeTableId(trees, idRanges, reservedTables)
                    : TddPrecompiledStub.FreeCodeunitId(trees, idRanges, reservedCodeunits);
                if (id == null)
                {
                    Refuse($"no free {kind.Word} id in the app's idRanges for it");
                    continue;
                }
                (isTable ? reservedTables : reservedCodeunits).Add(id.Value);
                var home = sites[0].TreeIdx;
                if (!appended.TryGetValue(home, out var sb)) appended[home] = sb = new();
                string objectText;
                if (isTable)
                {
                    var field = Quote(PlaceholderKeyField);
                    objectText = $"\ntable {id.Value} {Quote(name)}\n{{\n    fields\n    {{\n        field(1; {field}; Integer) {{ AutoIncrement = true; }}\n    }}\n" +
                        $"    keys\n    {{\n        key(PK; {field}) {{ Clustered = true; }}\n    }}\n}}\n";
                    tablesToRefresh.Add((id.Value, objectText));
                }
                else objectText = $"\ncodeunit {id.Value} {Quote(name)}\n{{\n}}\n";
                sb.Append(objectText);
                var uses = new List<(string, int)>();
                foreach (var s in sites) uses.AddRange(UseSitesOf(originalTrees, s.TreeIdx, s.Node));
                Console.Error.WriteLine($"--tdd: generated {kind.Word} {Quote(name)} (id {id.Value}) in {Path.GetFileName(trees[home].FilePath)}: no app of the run and no package it can read declares it" +
                    (isTable ? $"; its only field is the placeholder primary key {Quote(PlaceholderKeyField)}: Integer (AutoIncrement)" : ""));
                result.Add(new TddMissingObject(new TddGeneratedMember(name, kind.Word, id.Value.ToString()), uses));
            }
            catch (Exception ex)
            {
                Refuse($"{ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }
        foreach (var (idx, sb) in appended)
        {
            var text = trees[idx].GetText().ToString();
            trees[idx] = NavSyntax.SyntaxTree.ParseObjectText(text + sb, path: trees[idx].FilePath, encoding: null!, parseOptions, default);
        }
        // The record engine reads a table's shape from its own parse of the source files, which cannot see this
        // in-memory object; the same refresh a generated field needs (RecordPatches.TddReparse.cs).
        foreach (var (id, text) in tablesToRefresh) AlRunner.Patches.RecordPatches.TddReparseAndRefreshTable(id, text);
        return result;
    }

    /// <summary>
    /// The refusal reason when a package of <paramref name="packages"/> declares an object of
    /// <paramref name="packageKind"/> ("Codeunit" or "Table", the symbol cache's kind) named
    /// <paramref name="name"/> (any namespace, any case), or cannot be read to rule it out; null when none does (#5446, #5445).
    /// The compile resolves only the packages the app depends on, so a package it leaves out can declare the object
    /// that is AL0185 here and an empty codeunit added for it would shadow the real one. Packages are taken in name,
    /// version, path order, so the package named does not depend on the scan order.
    /// Trap: an unreadable package is not an absent one - it may be the object's home - so it refuses, unless its
    /// symbols TEXT proves the name is not in it (<see cref="SymbolTextMayMention"/>; #5450): then it is skipped with a
    /// <paramref name="note"/> (a `--tdd:` line on stderr when null), because one bad file must not turn the generation off for every name.
    /// </summary>
    internal static string? FindPackageDeclaringCodeunit(IReadOnlyList<BcCompiler.PackageScanEntry> packages, string name, Action<string>? note = null)
        => FindPackageDeclaringObject(packages, name, "Codeunit", note);

    internal static string? FindPackageDeclaringObject(IReadOnlyList<BcCompiler.PackageScanEntry> packages, string name,
        string packageKind, Action<string>? note = null)
    {
        var word = packageKind.ToLowerInvariant();
        foreach (var e in packages.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Version).ThenBy(p => p.Path, StringComparer.Ordinal))
        {
            var label = $"the package {e.Name} {e.Version} ({Path.GetFileName(e.Path)})";
            try
            {
                // Vanished or unreadable: falls to the text search below, which refuses naming the package unless the
                // text proves the name absent (DependencyAppSymbolWalkSourceGuardTests).
                if (Patches.BcAppSymbolCache.Get(e.Path).Objects.Any(o =>
                        o.Kind == packageKind && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return $"{label} declares it - if the test means that {word}, add the dependency on it to app.json; if it means a new one, give it another name; an empty {word} would shadow it";
            }
            catch (Exception ex)
            {
                var unread = $"{label} could not be read ({ex.GetType().Name}: {FirstLine(ex.Message)})";
                bool mayMention;
                try { mayMention = SymbolTextMayMention(e.Path, name); }
                catch { mayMention = true; } // no text to search is not a proof of absence
                if (mayMention)
                    return $"{unread}, so it cannot be ruled out as the home of the {word}; remove or replace that file";
                var skipped = $"{unread}, but its symbols text never mentions \"{name}\", so it cannot declare it and was skipped; remove or replace that file";
                if (note != null) note(skipped); else Console.Error.WriteLine($"--tdd: {skipped}");
            }
        }
        return null;
    }

    // One JSON escape, left to right so `\\` is consumed before the character after it. The first two groups are the
    // escapes decoded; `other` takes anything else, including a lone trailing backslash and a short \u.
    private static readonly System.Text.RegularExpressions.Regex JsonEscape = new(
        @"\\(?:u(?<hex>[0-9a-fA-F]{4})|(?<simple>[""\\/'nrtbf])|(?<other>[\s\S]?))",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True unless the text of the package's <c>SymbolReference.json</c> files (every module) proves they cannot declare
    /// <paramref name="name"/>: it is searched ignoring case after the JSON escapes are decoded (<c>\uXXXX</c> of either
    /// case, <c>\/ \" \\ \' \n \r \t \b \f</c>). When in doubt true, because a wrong false is a shadow again (#5446):
    /// BC reads symbols with Newtonsoft, more permissive than the reader that refused the file, so any other backslash
    /// sequence (invalid in both, Newtonsoft-only, future) means the text cannot be trusted and refuses; so does a name
    /// holding a backslash, quote or control character, and text holding a NUL (UTF-16 without a byte-order mark reads
    /// as interleaved NULs). Throws when the file cannot be read at all; the caller reads that as true too.
    /// </summary>
    private static bool SymbolTextMayMention(string packagePath, string name)
    {
        if (name.Any(c => c is '"' or '\\' || char.IsControl(c))) return true;
        foreach (var raw in Patches.BcAppSymbolCache.ReadSymbolReferences(packagePath))
        {
            if (raw.Contains('\0')) return true;
            var unknownEscape = false;
            var text = JsonEscape.Replace(raw, m =>
            {
                if (m.Groups["hex"].Success) return ((char)Convert.ToInt32(m.Groups["hex"].Value, 16)).ToString();
                if (!m.Groups["simple"].Success) { unknownEscape = true; return m.Value; }
                return m.Groups["simple"].Value[0] switch { 'n' => "\n", 'r' => "\r", 't' => "\t", 'b' => "\b", 'f' => "\f", var c => c.ToString() };
            });
            if (unknownEscape || text.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsPlainObjectName(string written, string name)
    {
        if (name.Length == 0 || name.All(char.IsDigit)) return false;
        if (written.StartsWith('"')) return written.Length >= 2 && written.EndsWith('"') && !name.Contains('"');
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>Where the code reaches the object from: the type reference itself, and, for a variable declared
    /// outside any procedure (a global of the test codeunit), every mention of its name in the same object.</summary>
    private static IEnumerable<(string, int)> UseSitesOf(NavSyntax.SyntaxTree[] trees, int treeIdx, NavSyntax.SubtypedDataTypeSyntax node)
    {
        var file = trees[treeIdx].FilePath;
        yield return (file, node.SpanStart);
        if (node.Ancestors().Any(a => a is NavSyntax.MethodOrTriggerDeclarationSyntax)) yield break;
        var declaration = node.Ancestors().OfType<NavSyntax.VariableDeclarationSyntax>().FirstOrDefault();
        var obj = node.Ancestors().OfType<NavSyntax.ObjectSyntax>().FirstOrDefault();
        if (declaration == null || obj == null) yield break;
        var variable = Unquote(declaration.Name.ToString().Trim());
        foreach (var id in obj.DescendantNodes().OfType<NavSyntax.IdentifierNameSyntax>())
            if (string.Equals(Unquote(id.ToString().Trim()), variable, StringComparison.OrdinalIgnoreCase)
                && id.Ancestors().Any(a => a is NavSyntax.MethodOrTriggerDeclarationSyntax))
                yield return (file, id.SpanStart);
    }
}
