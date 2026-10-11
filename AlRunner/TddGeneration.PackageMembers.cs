// The package-table half of --tdd generation (#5430): a FIELD the tests assign on a record of a table that arrives as a
// package (no tree of the compile and no source bundle of the run declares it) is generated beside the table, as a
// tableextension of the test's own compile. Nothing of the package is changed (precompiled-dll-respect.md) and nothing
// is written to disk. See docs/tdd-precompiled.md.
using System.Text;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavDiag = Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace AlRunner;

public static partial class TddGeneration
{
    internal static string TableExtensionName(int id) => $"TDD Table Extension {id}";

    /// <summary>
    /// One tableextension per package table, holding every field of that table the tests assign and the table does not
    /// declare, appended to the FIRST file (in file order) that names one: an extension is one object, and two files
    /// adding the same field would clash. A field is generated only from an assignment <c>Rec.Field := &lt;expr&gt;</c>, as for a
    /// source table; its id is the first of the app's <c>idRanges</c> that no field of the table nor of a tableextension of
    /// the compile uses. A table that cannot get an extension id, a field no site types, or a field id is refused with
    /// the reason on stderr and the tests fail as before. Trap: if the home file is dropped by the compile, the other
    /// files naming the field fail with the AL0132 again (loud, as for a generated object, #5447).
    /// </summary>
    private static void GenerateFieldsBesidePackageTables(
        NavCA.Compilation compilation, NavSyntax.SyntaxTree[] originalTrees, NavSyntax.SyntaxTree[] trees,
        NavCA.ParseOptions parseOptions, string? manifestAppJsonPath,
        List<(NavDiag.Diagnostic Diag, Target Target)> entries,
        Dictionary<string, TddGeneratedMember> generatedByKey, Dictionary<string, List<NavDiag.Diagnostic>> diagsByKey)
    {
        if (entries.Count == 0) return;
        var idRanges = TddPrecompiledStub.ReadIdRanges(manifestAppJsonPath);
        var reservedExtensions = new HashSet<int>();
        var appended = new Dictionary<int, StringBuilder>();
        var pending = new List<(string Key, TddGeneratedMember Member, List<NavDiag.Diagnostic> Diags)>();

        // Tables in name order, fields in name order, sites in file then position order: the compile reports the
        // diagnostics in an order that changes between runs, and no id nor shape may follow it.
        foreach (var table in entries.GroupBy(e => TableKeyOf(e.Target)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var tableName = DisplayNameOf(table.First().Target);
            void Refuse(string field, string reason) =>
                Console.Error.WriteLine($"--tdd: not generated beside package table {tableName}: \"{field}\" - {reason}");
            try
            {
                var fields = new List<(string Name, string Type, List<NavDiag.Diagnostic> Diags, int Home)>();
                foreach (var byField in table.GroupBy(e => e.Target.MemberName.ToLowerInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var sites = byField
                        .OrderBy(e => Array.IndexOf(originalTrees, e.Diag.Location.SourceTree))
                        .ThenBy(e => e.Diag.Location.SourceSpan.Start)
                        .ToList();
                    var name = sites[0].Target.MemberName;
                    string? type = null;
                    foreach (var (_, target) in sites)
                        if (target.Mae!.Parent is NavSyntax.AssignmentStatementSyntax asn && SpanEq(asn.Target, target.Mae)
                            && InferAlTypeText(compilation, asn.Source) is { } inferred)
                        {
                            type = inferred;
                            break;
                        }
                    if (type == null)
                    {
                        Refuse(name, "no assignment `Rec.Field := <expression>` fixes its type (a field that is only read, or assigned a Text, has none to infer)");
                        continue;
                    }
                    var home = sites.Select(s => Array.IndexOf(originalTrees, s.Diag.Location.SourceTree)).Min();
                    if (home < 0)
                    {
                        Refuse(name, "a site of it is in a file this compile does not own");
                        continue;
                    }
                    fields.Add((name, type, sites.Select(s => s.Diag).ToList(), home));
                }
                if (fields.Count == 0) continue;

                if (TableSymbolOf(table.First().Target.TargetType) is { IsExtensible: false })
                {
                    foreach (var f in fields) Refuse(f.Name, "the table is not extensible, so a tableextension cannot add a field to it");
                    continue;
                }
                var used = UsedFieldIds(compilation, table.First().Target, trees);
                if (used == null)
                {
                    foreach (var f in fields) Refuse(f.Name, "the field ids of the table could not be read, so a free one cannot be chosen");
                    continue;
                }
                var extensionId = TddPrecompiledStub.FreeTableExtensionId(trees, idRanges, reservedExtensions);
                if (extensionId == null)
                {
                    foreach (var f in fields) Refuse(f.Name, "no free tableextension id in the app's idRanges for it");
                    continue;
                }
                var reservedFields = new HashSet<int>();
                var withIds = new List<(string Name, string Type, List<NavDiag.Diagnostic> Diags, int Home, int FieldId)>();
                foreach (var f in fields)
                {
                    var fieldId = TddPrecompiledStub.FreeIdInRanges(idRanges, used, reservedFields);
                    if (fieldId == null) { Refuse(f.Name, "no free field id in the app's idRanges for it"); continue; }
                    reservedFields.Add(fieldId.Value);
                    withIds.Add((f.Name, f.Type, f.Diags, f.Home, fieldId.Value));
                }
                if (withIds.Count == 0) continue;

                reservedExtensions.Add(extensionId.Value);
                var homeIdx = withIds.Min(f => f.Home);
                var text = new StringBuilder();
                text.Append($"\ntableextension {extensionId.Value} {Quote(TableExtensionName(extensionId.Value))} extends {ExtendsText(table.First().Target)}\n{{\n    fields\n    {{\n");
                foreach (var f in withIds) text.Append($"        field({f.FieldId}; {Quote(f.Name)}; {f.Type}) {{ }}\n");
                text.Append("    }\n}\n");
                if (!appended.TryGetValue(homeIdx, out var sb)) appended[homeIdx] = sb = new();
                sb.Append(text);
                var extension = new TddPackageExtension("table", extensionId.Value, TableExtensionName(extensionId.Value), tableName, text.ToString());
                Console.Error.WriteLine(
                    $"--tdd: table extension {Quote(TableExtensionName(extensionId.Value))} (id {extensionId.Value}) beside package table {tableName} in " +
                    $"{Path.GetFileName(trees[homeIdx].FilePath)}: {string.Join(", ", withIds.Select(f => $"{Quote(f.Name)} (field {f.FieldId})"))}");
                foreach (var f in withIds)
                    pending.Add(($"beside-field|{TableKeyOf(table.First().Target)}|{f.Name.ToLowerInvariant()}",
                        new TddGeneratedMember(tableName, "field", $"{Quote(f.Name)}: {f.Type}") { PackageExtension = extension }, f.Diags));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"--tdd: not generated beside package table {tableName}: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }
        if (appended.Count == 0) return;
        foreach (var (idx, sb) in appended)
            trees[idx] = NavSyntax.SyntaxTree.ParseObjectText(trees[idx].GetText().ToString() + sb, path: trees[idx].FilePath,
                encoding: null!, parseOptions, default);
        foreach (var (key, member, diags) in pending)
        {
            generatedByKey[key] = member;
            diagsByKey[key] = diags;
        }
    }

    /// <summary>
    /// Run once the compile has settled (after the exclusion rounds), with the trees that were COMPILED. A package extension that
    /// is in them is registered with the record engine (a table's, see <c>RecordPatches.TddRegisterTableExtension</c>); one whose
    /// file the compile dropped is not, its members are taken out of <paramref name="members"/> (the closing list and the
    /// tests' annotations must not claim a field nobody declares), and the run says so. Returns true when a table extension
    /// was registered: the record engine now holds state the trees alone do not reproduce, so no incremental baseline may be
    /// recorded for this compile (<c>BcCompiler.Emit</c>; #5430).
    /// Observably equivalent to the extension being on disk: registered only if compiled, from the text a file would hold.
    /// </summary>
    internal static bool SettlePackageExtensions(NavSyntax.SyntaxTree[] compiledTrees, bool compiled, List<TddGeneratedMember> members)
    {
        var registered = false;
        foreach (var ext in members.Where(m => m.PackageExtension != null).Select(m => m.PackageExtension!).Distinct().ToList())
        {
            if (compiled && IsDeclaredIn(compiledTrees, ext))
            {
                if (ext.RecordEngineText == null) continue;
                AlRunner.Patches.RecordPatches.TddRegisterTableExtension(ext.RecordEngineText);
                registered = true;
                continue;
            }
            var dropped = members.Where(m => m.PackageExtension == ext).ToList();
            Console.Error.WriteLine(
                $"--tdd: dropped with its file: {ext.Word} extension {Quote(ext.Name)} beside package {ext.Word} {ext.ObjectName}, which the compile dropped, so it is not " +
                $"declared: {string.Join(", ", dropped.Select(m => m.Signature))} is not reported as generated and the record engine was not told of it");
            members.RemoveAll(m => m.PackageExtension == ext);
        }
        return registered;
    }

    private static bool IsDeclaredIn(NavSyntax.SyntaxTree[] trees, TddPackageExtension ext)
        => trees.Any(t => t.GetRoot() is NavSyntax.CompilationUnitSyntax root && root.Objects.Any(o =>
            (o switch
            {
                NavSyntax.TableExtensionSyntax te when ext.Word == "table" => te.ObjectId?.Value.Value as int?,
                NavSyntax.EnumExtensionTypeSyntax ee when ext.Word == "enum" => ee.ObjectId?.Value.Value as int?,
                _ => null,
            }) == ext.Id && Unquote(IdentTextOf(o.Name)) == ext.Name));

    internal static string EnumExtensionName(int id) => $"TDD Enum Extension {id}";

    /// <summary>
    /// The same for an enum (#5430): one enumextension per package enum, holding every value the tests name that the enum does
    /// not declare, appended to the first file that names one. Refused when the enum is not <c>Extensible</c> (the
    /// compile would reject the extension and take its file down). Each value takes the first ordinal of the app's
    /// <c>idRanges</c> that neither the enum nor an enumextension of the compile uses.
    /// </summary>
    private static void GenerateValuesBesidePackageEnums(
        NavCA.Compilation compilation, NavSyntax.SyntaxTree[] originalTrees, NavSyntax.SyntaxTree[] trees, NavCA.ParseOptions parseOptions,
        string? manifestAppJsonPath, List<(NavDiag.Diagnostic Diag, Target Target)> entries,
        Dictionary<string, TddGeneratedMember> generatedByKey, Dictionary<string, List<NavDiag.Diagnostic>> diagsByKey)
    {
        if (entries.Count == 0) return;
        var idRanges = TddPrecompiledStub.ReadIdRanges(manifestAppJsonPath);
        var reservedExtensions = new HashSet<int>();
        var appended = new Dictionary<int, StringBuilder>();
        var pending = new List<(string Key, TddGeneratedMember Member, List<NavDiag.Diagnostic> Diags)>();

        foreach (var enumGroup in entries.GroupBy(e => TableKeyOf(e.Target)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var target = enumGroup.First().Target;
            var enumName = DisplayNameOf(target);
            void Refuse(string value, string reason) =>
                Console.Error.WriteLine($"--tdd: not generated beside package enum {enumName}: \"{value}\" - {reason}");
            try
            {
                var values = new List<(string Name, List<NavDiag.Diagnostic> Diags, int Home)>();
                foreach (var byValue in enumGroup.GroupBy(e => e.Target.MemberName.ToLowerInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var sites = byValue.ToList();
                    var home = sites.Select(s => Array.IndexOf(originalTrees, s.Diag.Location.SourceTree)).Min();
                    if (home < 0) { Refuse(sites[0].Target.MemberName, "a site of it is in a file this compile does not own"); continue; }
                    values.Add((sites[0].Target.MemberName, sites.Select(s => s.Diag).ToList(), home));
                }
                if (values.Count == 0) continue;
                if (target.TargetType is not NavCA.IEnumBaseTypeSymbol enumSymbol)
                {
                    foreach (var v in values) Refuse(v.Name, "the enum could not be read, so its ordinals are unknown");
                    continue;
                }
                if (!enumSymbol.IsExtensible)
                {
                    foreach (var v in values) Refuse(v.Name, "the enum is not Extensible, so an enumextension cannot add a value to it");
                    continue;
                }
                var used = UsedOrdinals(compilation, enumSymbol, target, trees);
                var extensionId = TddPrecompiledStub.FreeEnumExtensionId(trees, idRanges, reservedExtensions);
                if (extensionId == null)
                {
                    foreach (var v in values) Refuse(v.Name, "no free enumextension id in the app's idRanges for it");
                    continue;
                }
                var reservedOrdinals = new HashSet<int>();
                var withOrdinals = new List<(string Name, List<NavDiag.Diagnostic> Diags, int Home, int Ordinal)>();
                foreach (var v in values)
                {
                    var ordinal = TddPrecompiledStub.FreeIdInRanges(idRanges, used, reservedOrdinals);
                    if (ordinal == null) { Refuse(v.Name, "no free ordinal in the app's idRanges for it"); continue; }
                    reservedOrdinals.Add(ordinal.Value);
                    withOrdinals.Add((v.Name, v.Diags, v.Home, ordinal.Value));
                }
                if (withOrdinals.Count == 0) continue;

                reservedExtensions.Add(extensionId.Value);
                var homeIdx = withOrdinals.Min(v => v.Home);
                var text = new StringBuilder();
                text.Append($"\nenumextension {extensionId.Value} {Quote(EnumExtensionName(extensionId.Value))} extends {ExtendsText(target)}\n{{\n");
                foreach (var v in withOrdinals) text.Append($"    value({v.Ordinal}; {Quote(v.Name)}) {{ }}\n");
                text.Append("}\n");
                if (!appended.TryGetValue(homeIdx, out var sb)) appended[homeIdx] = sb = new();
                sb.Append(text);
                Console.Error.WriteLine(
                    $"--tdd: enum extension {Quote(EnumExtensionName(extensionId.Value))} (id {extensionId.Value}) beside package enum {enumName} in " +
                    $"{Path.GetFileName(trees[homeIdx].FilePath)}: {string.Join(", ", withOrdinals.Select(v => $"{Quote(v.Name)} (ordinal {v.Ordinal})"))}");
                var extension = new TddPackageExtension("enum", extensionId.Value, EnumExtensionName(extensionId.Value), enumName, null);
                foreach (var v in withOrdinals)
                    pending.Add(($"beside-value|{TableKeyOf(target)}|{v.Name.ToLowerInvariant()}",
                        new TddGeneratedMember(enumName, "enum value", $"{Quote(v.Name)} = {v.Ordinal}") { PackageExtension = extension }, v.Diags));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"--tdd: not generated beside package enum {enumName}: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }
        if (appended.Count == 0) return;
        foreach (var (idx, sb) in appended)
            trees[idx] = NavSyntax.SyntaxTree.ParseObjectText(trees[idx].GetText().ToString() + sb, path: trees[idx].FilePath,
                encoding: null!, parseOptions, default);
        foreach (var (key, member, diags) in pending)
        {
            generatedByKey[key] = member;
            diagsByKey[key] = diags;
        }
    }

    /// <summary>The ordinals the enum already has (its own, from the symbol), those of every enumextension of the compile's own trees
    /// that extends an enum of that name, and those another app's enumextension of this very enum adds (#5491).</summary>
    private static HashSet<int> UsedOrdinals(NavCA.Compilation compilation, NavCA.IEnumBaseTypeSymbol enumSymbol, Target target, NavSyntax.SyntaxTree[] trees)
    {
        var used = new HashSet<int>(enumSymbol.Values.Select(v => v.Ordinal));
        foreach (var ext in compilation.GetApplicationObjectExtensionTypeSymbolsAcrossModules(enumSymbol))
            if (ext is NavCA.IEnumExtensionTypeSymbol enumExt)
                foreach (var v in enumExt.Values) used.Add(v.Ordinal);
        foreach (var tree in trees)
            if (tree.GetRoot() is NavSyntax.CompilationUnitSyntax root)
                foreach (var ext in root.Objects.OfType<NavSyntax.EnumExtensionTypeSyntax>())
                {
                    if (!ExtendsName(ext.BaseObject, target.TargetObjectName)) continue;
                    foreach (var v in ext.Values)
                        if (int.TryParse(v.Id.Text, out var n)) used.Add(n);
                }
        return used;
    }

    /// <summary>True when an <c>extends</c> target is written as an object of that name, in any namespace.</summary>
    private static bool ExtendsName(NavSyntax.ObjectNameOrIdSyntax? baseObject, string name)
    {
        var written = baseObject?.ToString().Trim() ?? "";
        var lastPart = Unquote(written.Contains('"') ? written[written.IndexOf('"')..] : written.Split('.').Last());
        return string.Equals(lastPart, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Name first, so tables and enums are taken (and take their ids) in name order whichever namespace they are in.</summary>
    private static string TableKeyOf(Target target)
        => $"{target.TargetObjectName}|{NamespaceOf(target.TargetType)}".ToLowerInvariant();

    /// <summary>The table a `Record "X"` type is a record of.</summary>
    private static NavCA.ITableTypeSymbol? TableSymbolOf(NavCA.ITypeSymbol? type)
        => type is NavCA.IRecordTypeSymbol record ? record.BaseTable : type as NavCA.ITableTypeSymbol;

    private static string NamespaceOf(NavCA.ITypeSymbol? type)
    {
        var ns = (TableSymbolOf(type) as NavCA.ITypeSymbol ?? type)?.ContainingNamespace;
        return ns == null || ns.IsGlobalNamespace || string.IsNullOrEmpty(ns.QualifiedName) ? "" : ns.QualifiedName;
    }

    /// <summary>The name a message and an annotation use: qualified by the namespace when there is one, because two
    /// tables of one name may sit in two namespaces and the reader must be able to tell which got the member.</summary>
    private static string DisplayNameOf(Target target)
    {
        var ns = NamespaceOf(target.TargetType);
        return ns.Length == 0 ? target.TargetObjectName : $"{ns}.{target.TargetObjectName}";
    }

    /// <summary>The table written as an <c>extends</c> target: qualified by its namespace, because the file the extension
    /// is appended to need not import it.</summary>
    private static string ExtendsText(Target target)
    {
        var ns = NamespaceOf(target.TargetType);
        return ns.Length == 0 ? Quote(target.TargetObjectName) : $"{ns}.{Quote(target.TargetObjectName)}";
    }

    /// <summary>The ids of the fields the table already has: its own (the symbol), those of every tableextension of the
    /// compile's own trees that extends a table of that name, and those another app's tableextension of this very table
    /// adds (#5491). Null when the symbol is not a table the fields can be read from.</summary>
    private static HashSet<int>? UsedFieldIds(NavCA.Compilation compilation, Target target, NavSyntax.SyntaxTree[] trees)
    {
        if (TableSymbolOf(target.TargetType) is not { } table) return null;
        var used = new HashSet<int>(table.Fields.Select(f => f.Id));
        foreach (var ext in compilation.GetApplicationObjectExtensionTypeSymbolsAcrossModules(table))
            if (ext is NavCA.ITableExtensionTypeSymbol tableExt)
                foreach (var f in tableExt.AddedFields) used.Add(f.Id);
        foreach (var tree in trees)
            if (tree.GetRoot() is NavSyntax.CompilationUnitSyntax root)
                foreach (var ext in root.Objects.OfType<NavSyntax.TableExtensionSyntax>())
                {
                    if (!ExtendsName(ext.BaseObject, target.TargetObjectName)) continue;
                    foreach (var f in ext.Fields?.Fields.OfType<NavSyntax.FieldSyntax>() ?? Enumerable.Empty<NavSyntax.FieldSyntax>())
                        if (int.TryParse(f.No.Text, out var n)) used.Add(n);
                }
        return used;
    }
}
