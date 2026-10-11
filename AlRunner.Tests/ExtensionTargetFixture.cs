// ExtensionTargetFixture — the inventory shared by the two #5474 test classes
// (AllObjWithCaptionExtensionTargetAnswersTests, AllObjWithCaptionExtensionTargetIndexTests).
//
// It is ADVERSARIAL on purpose, for the five *extension kinds whose AllObjWithCaption "Object
// Subtype" is the target's id. Every name is prefixed "ET " so nothing else in the test host can
// answer to it, and every expected id below is written out by hand, never derived from the code
// under test:
//   * the same name declared twice in one kind (first declared wins, not the lowest id);
//   * names differing only by case (OrdinalIgnoreCase);
//   * one name present in several kinds (each extension kind resolves in its own kind only);
//   * one name present in the source tier AND a dependency .app (the source tier wins);
//   * one name present in two dependency .apps (the first registered wins);
//   * a target no kind declares (resolves to the empty string).
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;

namespace AlRunner.Tests;

internal static class ExtensionTargetFixture
{
    internal const int B = 88530000;

    private static readonly Type RP = typeof(RecordPatches);

    /// <summary>One extension object and the id text AllObjWithCaption must report for it.</summary>
    internal sealed record Ext(string Kind, int Id, string Name, string Target, string Expected);

    private static string Id(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Extension objects declared in source. Expected values are hand-listed.</summary>
    internal static readonly Ext[] SourceExtensions =
    {
        new("PageExtension", B + 401, "ET PE Dup", "et dup page", Id(B + 1)),
        new("PageExtension", B + 402, "ET PE DupUpper", "ET DUP PAGE", Id(B + 1)),
        new("PageExtension", B + 403, "ET PE Tie", "ET Tie Page", Id(B + 9)),
        new("PageExtension", B + 404, "ET PE Shared", "ET Shared Name", Id(B + 20)),
        new("PageExtension", B + 405, "ET PE Missing", "ET Missing", ""),
        new("PageExtension", B + 406, "ET PE Both", "ET Both Tiers", Id(B + 40)),
        new("PageExtension", B + 407, "ET PE DepPage", "ET Dep Page", Id(B + 101)),
        new("PageExtension", B + 408, "ET PE DepReport", "ET Dep Report", ""),
        new("PageExtension", B + 409, "ET PE DepTie", "ET Dep Tie Page", Id(B + 102)),
        new("PageExtension", B + 410, "ET PE PermName", "ET Perm A", ""),
        new("TableExtension", B + 411, "ET TE Shared", "ET SHARED NAME", Id(B + 22)),
        new("TableExtension", B + 412, "ET TE Both", "ET Both Table", Id(B + 41)),
        new("TableExtension", B + 413, "ET TE Dep", "ET Dep Table", Id(B + 142)),
        new("TableExtension", B + 414, "ET TE DepReportName", "ET Dep Report", Id(B + 143)),
        new("TableExtension", B + 415, "ET TE PageName", "ET Dep Page", ""),
        new("EnumExtension", B + 421, "ET EE Shared", "ET Shared Name", Id(B + 24)),
        new("EnumExtension", B + 422, "ET EE PageName", "ET Dup Page", ""),
        new("PermissionSetExtension", B + 431, "ET PSE A", "ET PERM A", Id(B + 30)),
        new("PermissionSetExtension", B + 432, "ET PSE Dep", "ET Dep Perm", Id(B + 150)),
        new("PermissionSetExtension", B + 433, "ET PSE SharedName", "ET Shared Name", ""),
        new("ReportExtension", B + 441, "ET RE Shared", "ET Shared Name", Id(B + 21)),
        new("ReportExtension", B + 442, "ET RE Dep", "ET Dep Report", Id(B + 110)),
        new("ReportExtension", B + 443, "ET RE DepPageName", "ET Dep Page", Id(B + 111)),
        new("ReportExtension", B + 444, "ET RE TieName", "ET Tie Page", ""),
    };

    /// <summary>Extension objects declared by the second dependency .app (they surface as rows
    /// through <c>symbols.Objects</c> with the target name in <c>TargetObject</c>).</summary>
    internal static readonly Ext[] DependencyExtensions =
    {
        new("PageExtension", B + 501, "ET Dep PE", "ET Dep Page", Id(B + 101)),
        new("TableExtension", B + 502, "ET Dep TE", "ET Dep Table", Id(B + 142)),
        new("TableExtension", B + 503, "ET Dep TE Both", "ET Both Table", Id(B + 41)),
        new("PageExtension", B + 504, "ET Dep PE Missing", "ET Nowhere", ""),
    };

    private static string SourceOf(Ext e)
    {
        var kw = e.Kind.ToLowerInvariant();
        var body = e.Kind == "EnumExtension" ? "value(50; ETValue) { }" : "";
        return $"{kw} {e.Id} \"{e.Name}\" extends \"{e.Target}\" {{ {body} }}";
    }

    private static string Table(int id, string name) =>
        $"table {id} \"{name}\" {{ fields {{ field(1; \"Code\"; Code[20]) {{ }} }} keys {{ key(PK; \"Code\") {{ Clustered = true; }} }} }}";

    /// <summary>The base objects, in the INSERTION ORDER the tie-breaking expectations rest on.</summary>
    private static IEnumerable<string> BaseSources()
    {
        yield return $"page {B + 1} \"ET Dup Page\" {{ PageType = Card; }}";
        yield return $"page {B + 2} \"et dup page\" {{ PageType = Card; }}";
        yield return $"page {B + 9} \"ET Tie Page\" {{ PageType = Card; }}";
        yield return $"page {B + 3} \"ET Tie Page\" {{ PageType = Card; }}";
        yield return $"page {B + 20} \"ET Shared Name\" {{ PageType = Card; }}";
        yield return $"report {B + 21} \"ET Shared Name\" {{ }}";
        yield return Table(B + 22, "ET Shared Name");
        yield return Table(B + 23, "ET Shared Name");
        yield return $"permissionset {B + 30} \"ET Perm A\" {{ Assignable = true; }}";
        yield return $"permissionset {B + 31} \"et perm a\" {{ Assignable = true; }}";
        yield return $"page {B + 40} \"ET Both Tiers\" {{ PageType = Card; }}";
        yield return Table(B + 41, "ET Both Table");
    }

    /// <summary>Clears the parser statics, then registers the source objects, the enums and the
    /// two dependency .apps (in that registration order). Returns the temp directory to delete.</summary>
    internal static void Seed(string scratch)
    {
        RecordPatches.ResetForReload();
        foreach (var text in BaseSources().Concat(SourceExtensions.Select(SourceOf)))
            RP.GetMethod("ParseSourceFileIntoAllExtractorsCore", BindingFlags.NonPublic | BindingFlags.Static)!
              .Invoke(null, new object?[] { text, null });

        // Registered highest id first: the registry hands them out sorted by id, so the lowest
        // id wins the tie whatever the registration order was.
        AlEnumMetadataRegistry.Register(B + 27, "ET Shared Name", new[] { "A" }, new[] { 0 });
        AlEnumMetadataRegistry.Register(B + 24, "et shared name", new[] { "A" }, new[] { 0 });

        RecordPatches.AddBcAppPath(WriteApp(scratch,
            tables: new[] { (B + 142, "ET Dep Table"), (B + 141, "ET Both Table"), (B + 143, "ET Dep Report") },
            pages: new[] { (B + 101, "ET Dep Page"), (B + 102, "ET Dep Tie Page"), (B + 140, "ET Both Tiers") },
            reports: new[] { (B + 110, "ET Dep Report"), (B + 111, "ET Dep Page") },
            permissionSets: new[] { (B + 150, "ET Dep Perm") },
            extensions: Array.Empty<Ext>()));
        RecordPatches.AddBcAppPath(WriteApp(scratch,
            tables: new[] { (B + 144, "ET Dep Table") },
            pages: new[] { (B + 103, "et dep tie page"), (B + 104, "ET Dep Page") },
            reports: new[] { (B + 112, "ET Dep Report") },
            permissionSets: new[] { (B + 151, "ET DEP PERM") },
            extensions: DependencyExtensions));
    }

    internal static string WriteApp(string scratch,
        (int Id, string Name)[] tables, (int Id, string Name)[] pages, (int Id, string Name)[] reports,
        (int Id, string Name)[] permissionSets, Ext[] extensions)
    {
        static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        var sb = new StringBuilder("{ \"RuntimeVersion\": \"15.1\"");
        string Named(string label, (int Id, string Name)[] items) =>
            items.Length == 0 ? "" : $", \"{label}\": [" + string.Join(",", items.Select(i => $"{{ \"Id\": {i.Id}, \"Name\": {Q(i.Name)} }}")) + "]";
        if (tables.Length > 0)
            sb.Append(", \"Tables\": [" + string.Join(",", tables.Select(t =>
                $"{{ \"Id\": {t.Id}, \"Name\": {Q(t.Name)}, \"Properties\": [], \"Fields\": [ {{ \"Id\": 1, \"Name\": \"Code\", " +
                "\"TypeDefinition\": { \"Name\": \"Code\", \"Subtype\": { \"Name\": \"20\" } } } ] }")) + "]");
        sb.Append(Named("Pages", pages)).Append(Named("Reports", reports)).Append(Named("PermissionSets", permissionSets));
        foreach (var g in extensions.GroupBy(e => e.Kind))
            sb.Append($", \"{g.Key}s\": [" + string.Join(",", g.Select(e =>
                $"{{ \"Id\": {e.Id}, \"Name\": {Q(e.Name)}, \"TargetObject\": {Q(e.Target)} }}")) + "]");
        sb.Append(" }");

        var appPath = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".app");
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), new UTF8Encoding(false)))
            w.Write(sb.ToString());
        return appPath;
    }
}
