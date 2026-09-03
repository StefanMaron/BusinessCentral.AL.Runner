// RecordPatches.AlPermissionSetParser — permission set objects parsed out of the AL
// SOURCE the runner compiles itself.
//
// WHY THIS EXISTS
//   The "Metadata Permission Set" (2000000250) virtual table's rows are the permission
//   sets every published app declares (RecordPatches.MetadataPermissionSetVirtualTable.cs,
//   issue #2313/#2330). For a PRECOMPILED dependency .app that inventory comes off its
//   SymbolReference.json (BcAppSymbolCache.PermissionSetSymbol). For the bundle the
//   runner compiles from SOURCE there is no .app to read symbols from — its
//   `permissionset` objects only exist as AL syntax until this run's own compile — so
//   without a source-level registry a permission set declared in the app under test can
//   never appear in this table.
//
//   That is exactly what left #2357 half-fixed: Microsoft's own Tests-SINGLESERVER
//   bucket declares `permissionset 134611 TestSet` (TestSet.PermissionSet.al) alongside
//   Codeunit134614, and `Codeunit134614.TestAggregatePermissionSetsTable` does
//   `AggregatePermissionSet.Get(Scope::System, <this bundle's app id>, 'TestSet')` — a
//   role id that will never show up in ANY dependency .app's SymbolReference.json,
//   because it is declared by the very sources this run is compiling.
//
// THE DECLARING APP
//   Same rule as RecordPatches.AlProfileParser.cs (which this mirrors): a permission
//   set's declaring app for the table's "App ID" column is the app.json that owns the
//   source tree the file came from, resolved by walking up from the file
//   (ResolveOwningApp, shared with the profile parser — same partial class). A
//   permission set whose app.json cannot be found is DROPPED rather than attributed to
//   an invented app id, matching the profile parser's own rule.
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// One <c>permissionset Id "Name" { … }</c> declaration parsed from AL source, plus the
    /// identity of the app.json that owns the file it was declared in.
    /// </summary>
    internal sealed record ParsedAlPermissionSet(
        int Id, string Name, string? Caption, bool Assignable, Guid AppId, string AppName);

    // (AppId, Name) → declaration. Keyed by app too: two apps in one run may legally
    // declare a permission set of the same name, and a real tier's own dictionary
    // (NavAppGroup.PermissionSetGroupObjectMetadataSummaries) is per app GROUP, not global
    // — EnumerateKnownPermissionSets applies the "first (i.e. source-compiled) declaration
    // of a given name wins" rule the same way BC's own union does.
    private static readonly Dictionary<(Guid AppId, string Name), ParsedAlPermissionSet> _parsedPermissionSets
        = new();

    /// <summary>Snapshot of every permission set declaration parsed from AL source.</summary>
    internal static IReadOnlyCollection<ParsedAlPermissionSet> ParsedPermissionSets => _parsedPermissionSets.Values;

    /// <summary>
    /// Parse the permission set declarations of one AL source file. <paramref name="filePath"/>
    /// is what makes the declaring app knowable; when it is null (a caller that only has
    /// text) nothing is recorded, because a permission set with no app is not a row this
    /// table can answer — same rule as <see cref="TryParseProfileFile"/>.
    /// </summary>
    private static void TryParsePermissionSetFile(string text, string? filePath)
    {
        if (filePath == null) return;
        // Cheap reject before building a syntax tree: most .al files declare no
        // permissionset at all, and ParseAlObjects is memoized only for the file the
        // shared sweep is currently on.
        if (text.IndexOf("permissionset", StringComparison.OrdinalIgnoreCase) < 0) return;

        Guid appId = Guid.Empty;
        string appName = string.Empty;
        var identityResolved = false;

        foreach (var obj in ParseAlObjects(text))
        {
            if (obj is not NavSyntax.PermissionSetSyntax permissionSet) continue;
            if (ObjectIdOf(permissionSet) is not int id) continue;
            var name = IdentText((permissionSet as NavSyntax.ObjectSyntax)?.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!identityResolved)
            {
                identityResolved = true;
                if (ResolveOwningApp(filePath) is not { } owner) return;   // no app.json — drop
                (appId, appName) = owner;
            }

            var props = permissionSet.PropertyList;
            // AL's `Assignable` property defaults to true when a permission set declares
            // none — same rule BcAppSymbolCache.CollectPermissionSets already applies for
            // precompiled dependency .apps, so a source-compiled and a precompiled
            // declaration of the same shape answer identically.
            var assignable = !PropIs(props, "Assignable", "false");

            _parsedPermissionSets[(appId, name)] = new ParsedAlPermissionSet(
                id, name, PropertyTextFrom(PropValue(props, "Caption")), assignable, appId, appName);
        }
    }
}
