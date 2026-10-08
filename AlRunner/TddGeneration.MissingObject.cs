// The missing-OBJECT half of --tdd generation (#5431): a codeunit that no app declares, named by a variable
// type (`Missing: Codeunit "No Such Codeunit"`, AL0185). See docs/tdd-missing-object.md.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavDiag = Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace AlRunner;

/// <summary>An empty codeunit --tdd added to a file of the compile because no app declares it. <see cref="UseSites"/>
/// are the places the tests reach it from (file path and position), found in the trees before it was added.</summary>
public sealed record TddMissingObject(TddGeneratedMember Member, IReadOnlyList<(string FilePath, int Position)> UseSites)
{
    /// <summary>The <see cref="TddGeneratedMember.MemberKind"/> of an object, as against its procedures.</summary>
    public const string MemberKind = "codeunit";
}

public static partial class TddGeneration
{
    private const string MissingObjectDiagnosticId = "AL0185";

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
        // name (lower case) -> every AL0185 type reference naming it, de-duplicated by position.
        var byName = new SortedDictionary<string, List<(int TreeIdx, NavSyntax.SubtypedDataTypeSyntax Node, string Name)>>(StringComparer.Ordinal);
        var refused = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            if (diag.Severity != NavDiag.DiagnosticSeverity.Error || diag.Id != MissingObjectDiagnosticId) continue;
            if (!diag.Location.IsInSource || diag.Location.SourceTree is not { } tree) continue;
            var treeIdx = Array.IndexOf(originalTrees, tree);
            if (treeIdx < 0) continue;
            var node = tree.GetRoot().FindToken(diag.Location.SourceSpan.Start).Parent?.AncestorsAndSelf()
                .OfType<NavSyntax.SubtypedDataTypeSyntax>().FirstOrDefault();
            if (node == null || node.Span != diag.Location.SourceSpan
                || !string.Equals(node.TypeName.ValueText, "Codeunit", StringComparison.OrdinalIgnoreCase)) continue;
            var written = node.Subtype.ToString().Trim();
            var name = Unquote(written);
            if (!IsPlainObjectName(written, name))
            {
                if (refused.Add(name.ToLowerInvariant()))
                    Console.Error.WriteLine($"--tdd: codeunit {written} not generated - it is named by an id or with a namespace, and only a plain name is generated");
                continue;
            }
            var key = name.ToLowerInvariant();
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = new();
            if (!list.Any(e => e.TreeIdx == treeIdx && e.Node.SpanStart == node.SpanStart)) list.Add((treeIdx, node, name));
        }
        if (byName.Count == 0) return Array.Empty<TddMissingObject>();

        var idRanges = TddPrecompiledStub.ReadIdRanges(manifestAppJsonPath);
        var reserved = new HashSet<int>();
        var appended = new Dictionary<int, System.Text.StringBuilder>();
        var result = new List<TddMissingObject>();
        foreach (var (key, unordered) in byName)
        {
            var sites = unordered.OrderBy(e => e.TreeIdx).ThenBy(e => e.Node.SpanStart).ToList();
            var name = sites[0].Name;
            void Refuse(string reason) => Console.Error.WriteLine($"--tdd: codeunit {Quote(name)} not generated - {reason}");
            try
            {
                if (compilation.GetApplicationObjectTypeSymbolsByNameAcrossModulesAndNamespaces(NavCA.SymbolKind.Codeunit, name).Length > 0)
                {
                    Refuse("a codeunit of that name exists in a module or namespace this code does not reach, and an empty one would shadow it");
                    continue;
                }
                if (TddCrossBundle.RunDeclaresCodeunit(name) is { } declaredIn)
                {
                    Refuse($"another bundle of the run declares it ({Path.GetFileName(declaredIn)}) - declare the dependency on it; an empty codeunit would shadow it");
                    continue;
                }
                if (FindPackageDeclaringCodeunit(BcCompiler.ScannedPackagesForTdd(), name) is { } inPackage)
                {
                    Refuse(inPackage);
                    continue;
                }
                var id = TddPrecompiledStub.FreeCodeunitId(trees, idRanges, reserved);
                if (id == null)
                {
                    Refuse("no free codeunit id in the app's idRanges for it");
                    continue;
                }
                reserved.Add(id.Value);
                var home = sites[0].TreeIdx;
                if (!appended.TryGetValue(home, out var sb)) appended[home] = sb = new();
                sb.Append($"\ncodeunit {id.Value} {Quote(name)}\n{{\n}}\n");
                var uses = new List<(string, int)>();
                foreach (var s in sites) uses.AddRange(UseSitesOf(originalTrees, s.TreeIdx, s.Node));
                Console.Error.WriteLine($"--tdd: generated codeunit {Quote(name)} (id {id.Value}) in {Path.GetFileName(trees[home].FilePath)}: no app of the run and no package it can read declares it");
                result.Add(new TddMissingObject(new TddGeneratedMember(name, TddMissingObject.MemberKind, id.Value.ToString()), uses));
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
        return result;
    }

    /// <summary>
    /// The refusal reason when a package of <paramref name="packages"/> declares a codeunit named
    /// <paramref name="name"/> (any namespace, any case), or cannot be read to rule it out; null when none does (#5446).
    /// The compile resolves only the packages the app depends on, so a package it leaves out can declare the object
    /// that is AL0185 here and an empty codeunit added for it would shadow the real one. Packages are taken in name,
    /// version, path order, so the package named does not depend on the scan order. Trap: an unreadable package is
    /// not an absent one - it may be the object's home - so it refuses too.
    /// </summary>
    internal static string? FindPackageDeclaringCodeunit(IReadOnlyList<BcCompiler.PackageScanEntry> packages, string name)
    {
        foreach (var e in packages.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Version).ThenBy(p => p.Path, StringComparer.Ordinal))
        {
            var label = $"the package {e.Name} {e.Version} ({Path.GetFileName(e.Path)})";
            try
            {
                // Vanished or unreadable: refuses below, naming the package (DependencyAppSymbolWalkSourceGuardTests).
                if (Patches.BcAppSymbolCache.Get(e.Path).Objects.Any(o =>
                        o.Kind == "Codeunit" && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return $"{label} declares it - add the dependency on it to app.json; an empty codeunit would shadow it";
            }
            catch (Exception ex)
            {
                return $"{label} could not be read ({ex.GetType().Name}: {FirstLine(ex.Message)}), so it cannot be ruled out as the home of the codeunit";
            }
        }
        return null;
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
