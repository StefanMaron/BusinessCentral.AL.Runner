// RecordPatches.AllProfileVirtualTable — managed provider for the "All Profile"
// (2000000178) system virtual table.
//
// WHY THIS EXISTS
//   "All Profile" is THE table AL reads to discover which roles exist: the Profile List /
//   Profile Card pages are bound to it, Conf./Personalization Mgt. resolves a user's role
//   centre through it, and User Personalization's (Scope, App ID, Profile ID) triple points
//   at one of its rows. It is virtual on the service tier — NavDataAccessSource's
//   GetVirtualDataProvider returns AllProfileDataProvider, whose GetAllItems is
//   TenantProfileTableDataHandler.GetAllItemsAsync: the designer-created rows of Tenant
//   Profile (2000000177) plus NavAppProfileManagement.GetAllProfileKeysFromResources, i.e.
//   every profile every published app declares.
//
//   The runner routes every table to the in-memory TempTableDataProvider, and nothing
//   populated 2000000178, so `Record "All Profile"` was ALWAYS empty and every read of it
//   raised "There is no All Profile within the filter." That is 20 tests of Microsoft's
//   Tests-SINGLESERVER bucket that pass on a real tier (issue #2317).
//
// WHERE THE ROWS COME FROM (two sources, neither invented)
//   1. Profiles the runner compiles from source — parsed from their AL
//      (RecordPatches.AlProfileParser.cs), attributed to the app.json that owns the file.
//   2. Profiles declared by a PRECOMPILED dependency .app — read from its
//      SymbolReference.json `Profiles` array (BcAppSymbolCache.ProfileSymbol), attributed
//      to that package's own AppId/Name. This is the only route for an R2R app.
//   Source-compiled wins for the same (App ID, Profile ID) — the source is what this run
//   actually compiled.
//
// COLUMN LAYOUT
//   Mirrors BC's own TenantProfileTableDataHandler.CreateRecordBuffer exactly, including
//   the two things that look surprising until you read it:
//     - Scope is ALWAYS Tenant. System-scope profiles are deprecated; BC hardcodes
//       NavOption.Create(ScopeField.FieldOptionMetadata, 1) for every row, app-owned ones
//       included, and Microsoft's own NoAppProfilesArePresent test asserts Scope::System
//       is empty.
//     - Fields 7-12 (Use Comments / Use Notes / Use Record Notes / Record Notebook /
//       Use Page Notes / Page Notebook, all ObsoleteState=Pending) are false/empty on a
//       real row too — BC writes literal NavBoolean.False / NavText.Empty into them — so
//       the type default this file leaves there is the faithful value, not a stub.
//   "Default Role Center" and "Disable Personalization" come from the per-tenant profile
//   SETTINGS (Tenant Profile Setting, 2000000083) on a real tier, not from the profile
//   object. A tenant that has never chosen has none, so they start false and are then
//   whatever AL last wrote through Modify — which is exactly what the in-memory store
//   gives us, and what Microsoft's tests do (Cleanup() clears every profile's default and
//   then sets one).
//
// WRITES ARE NOT FREE-FOR-ALL
//   The table is writable, but only for tenant-owned profiles. See
//   RecordPatches.AllProfileWriteGuard.cs for the rules and where they are enforced.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (VirtualDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer, TempTableDataProvider), reached through the same helpers the
//   AllObj provider resolves. No AL business-logic body is touched.
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int AllProfileVirtualTableId = 2000000178;

    /// <summary>
    /// The "Scope" option ordinal every All Profile row carries. BC's own row builder
    /// hardcodes 1 (= Tenant) for every profile including app-owned ones; system-scope
    /// profiles are deprecated and the option's second member is Tenant on every BC
    /// version the runner supports.
    /// </summary>
    private const int AllProfileScopeTenant = 1;

    /// <summary>
    /// The app id a TENANT-owned (user-created) profile carries:
    /// <c>NavConfigurationDesignerExtension.ConfigurationAppId</c>, which is
    /// <c>Guid.Empty</c>. Every write rule on this table keys off it.
    /// </summary>
    internal static readonly Guid AllProfileTenantAppId = Guid.Empty;

    // Populated exactly ONCE per in-memory provider. Unlike AllObj (which tops up as more
    // objects become known) this table is WRITTEN by AL: a top-up on a later handout would
    // resurrect a row the test under way had just deleted.
    private static readonly ConditionalWeakTable<object, object> _apvPopulatedProviders = new();

    private static MethodInfo? _apvNavCodeCreateTruncated;   // NavCode.CreateTruncated(int, string)
    private static MethodInfo? _apvNavGuidCreate;            // NavGuid.Create(Guid)
    private static bool _apvReflectionReady;

    /// <summary>True if <paramref name="table"/> is the All Profile system virtual table.</summary>
    private static bool IsAllProfileVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == AllProfileVirtualTableId;

    /// <summary>
    /// One All Profile row, already resolved: the RoleCenter page NAME the profile declares
    /// has become a page id, and the declaring app is known.
    /// </summary>
    private sealed record AllProfileRow(
        Guid AppId, string AppName, string ProfileId, string Caption, string Description,
        int RoleCenterId, bool Enabled, bool Promoted);

    /// <summary>
    /// Populate the in-memory store behind All Profile (2000000178) with one row per profile
    /// the runner knows about. Idempotent per provider, and deliberately populate-once — see
    /// <see cref="_apvPopulatedProviders"/>.
    /// </summary>
    private static void PopulateAllProfileVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);   // NavBoolean.Create
        EnsureAllProfileReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "All Profile (virtual table 2000000178)",
                "all-profile-virtual-table — data access has no in-memory provider; see docs/scope.md");

        if (_apvPopulatedProviders.TryGetValue(provider, out _)) return;
        _apvPopulatedProviders.Add(provider, new object());

        foreach (var row in EnumerateKnownProfiles())
            InsertVirtualRow(provider, metaTable, AllProfileSystemIdArgs(row),
                field => BuildAllProfileValue(field, row));
    }

    /// <summary>
    /// The four ints BC's MetadataSystemId takes for one row. All Profile's real primary key
    /// is (Scope, App ID, Profile ID) — a GUID and a Code, neither of which is an int — so
    /// the last two slots are stable hashes of those two. They only have to be
    /// distinct per row: nothing in AL reads an All Profile SystemId as a meaningful number,
    /// and BC's own provider derives it from the same two key parts.
    /// </summary>
    private static object[] AllProfileSystemIdArgs(AllProfileRow row) => new object[]
    {
        AllProfileVirtualTableId,
        AllProfileScopeTenant,
        StableHash(row.AppId.ToString("N")),
        StableHash(row.ProfileId.ToUpperInvariant()),
    };

    /// <summary>
    /// FNV-1a over UTF-16 code units. String.GetHashCode is randomized per process in
    /// .NET Core, which would make a row's SystemId differ between two runs of the same
    /// bundle; this does not.
    /// </summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }

    /// <summary>
    /// One column of an All Profile row, matched by the metatable's own FIELD NAME so the
    /// mapping tracks whatever the System package in the resolved artifact declares rather
    /// than a hardcoded field-number table.
    /// </summary>
    private static object? BuildAllProfileValue(NCLMetaField field, AllProfileRow row)
    {
        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "scope":
                return _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, AllProfileScopeTenant });
            case "appid":
                return _apvNavGuidCreate!.Invoke(null, new object?[] { row.AppId });
            case "profileid":
                return _apvNavCodeCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, row.ProfileId });
            case "description":
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, row.Description });
            case "rolecenterid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.RoleCenterId });
            case "appname":
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, row.AppName });
            case "enabled":
                return NavBoolean(row.Enabled);
            case "caption":
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, row.Caption });
            case "promoted":
                return NavBoolean(row.Promoted);
            // "Default Role Center" / "Disable Personalization" come from the per-tenant
            // profile settings, of which a tenant that has never chosen has none — so their
            // type default (false) is what a real row carries too, and AL's own Modify is
            // what moves them. Fields 7-12 are the deprecated notes/comments capacity, which
            // BC writes as literal false/empty; the type default is the same value.
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    /// <summary>
    /// Every profile the runner has real metadata for. Source-parsed profiles of the app
    /// under test and of any source-compiled dependency first, then profiles declared by the
    /// SymbolReference.json of every registered precompiled dependency .app.
    ///
    /// <para>Not memoized: this is called once per provider (populate-once), and the
    /// inventory has to reflect every dependency registered up to that point — the FIRST
    /// handout of this table can happen before the bundle's dependencies are registered,
    /// and a snapshot taken then would permanently hide every Base Application profile.</para>
    /// </summary>
    private static List<AllProfileRow> EnumerateKnownProfiles()
    {
        var rows = new Dictionary<(Guid, string), AllProfileRow>();
        var (pageIdsByName, _) = BuildObjectIndexes();
        var unresolvedRoleCenters = new List<string>();

        int ResolveRoleCenter(string? name, string profileId)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;      // declares none — truthful 0
            if (int.TryParse(name, out var literal) && literal > 0) return literal;
            if (pageIdsByName.TryGetValue(name!, out var resolved)) return resolved;
            unresolvedRoleCenters.Add($"profile '{profileId}' RoleCenter -> page '{name}'");
            return 0;
        }

        // 1. Profiles the runner compiled from source.
        foreach (var p in ParsedProfiles)
            rows[(p.AppId, Key(p.ProfileId))] = new AllProfileRow(
                p.AppId, p.AppName, p.ProfileId,
                // AL's own default caption for a profile that declares none is its name —
                // BC's own GetProfileCaption falls back to the profile id.
                string.IsNullOrEmpty(p.Caption) ? p.ProfileId : p.Caption!,
                p.Description ?? string.Empty,
                ResolveRoleCenter(p.RoleCenterPageName, p.ProfileId),
                p.Enabled, p.Promoted);

        // 2. Profiles declared by precompiled dependency .app packages.
        foreach (var (appId, appName, symbol) in EnumerateBcAppProfileSymbols())
        {
            var key = (appId, Key(symbol.ProfileId));
            if (rows.ContainsKey(key)) continue;                // source-compiled wins
            rows[key] = new AllProfileRow(
                appId, appName, symbol.ProfileId,
                string.IsNullOrEmpty(symbol.Caption) ? symbol.ProfileId : symbol.Caption!,
                symbol.Description ?? string.Empty,
                ResolveRoleCenter(symbol.RoleCenterPageName, symbol.ProfileId),
                symbol.Enabled, symbol.Promoted);
        }

        if (unresolvedRoleCenters.Count > 0)
            Console.Error.WriteLine(
                $"[RecordPatches] All Profile: {unresolvedRoleCenters.Count} declared RoleCenter "
                + "reference(s) could not be resolved to a page id and are reported as 0: "
                + string.Join("; ", unresolvedRoleCenters.Take(10))
                + (unresolvedRoleCenters.Count > 10 ? $" (+{unresolvedRoleCenters.Count - 10} more)" : string.Empty));

        if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_ALL_PROFILE") == "1")
            Console.Out.WriteLine(
                $"[all-profile] {rows.Count} profile(s): "
                + string.Join(", ", rows.Values.Select(r => $"{r.ProfileId}@{r.AppName}->{r.RoleCenterId}")));

        return rows.Values.ToList();

        // "Profile ID" is a Code field, which BC compares case-insensitively.
        static string Key(string profileId) => profileId.ToUpperInvariant();
    }

    private static IEnumerable<(Guid AppId, string AppName, BcAppSymbolCache.ProfileSymbol Symbol)>
        EnumerateBcAppProfileSymbols()
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            BcAppSymbolCache.AppSymbols symbols;
            try
            {
                symbols = BcAppSymbolCache.Get(appPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] All Profile: SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            if (symbols.Profiles is not { Count: > 0 } profiles) continue;
            if (!Guid.TryParse(symbols.AppId, out var appId))
            {
                // Every row carries the declaring app's id as a column. Without one there is
                // no truthful row to build, and Guid.Empty would claim these are
                // tenant-owned profiles — which would make them deletable.
                Console.Error.WriteLine(
                    $"[RecordPatches] All Profile: {Path.GetFileName(appPath)} declares {profiles.Count} "
                    + "profile(s) but its SymbolReference.json states no AppId — they are omitted rather "
                    + "than listed under an invented app id");
                continue;
            }
            foreach (var p in profiles)
                yield return (appId, symbols.AppName ?? string.Empty, p);
        }
    }

    /// <summary>
    /// NavCode.CreateTruncated(int,string) and NavGuid.Create(Guid) — the two value helpers
    /// this table needs beyond the set the AllObj provider already resolves. Bound off the
    /// metatable's own assembly with a hard throw when absent, never a silently skipped
    /// column.
    /// </summary>
    private static void EnsureAllProfileReflection(NCLMetaTable metaTable)
    {
        if (_apvReflectionReady) return;

        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        var tNavCode = ResolveType(rt + "NavCode", "Microsoft.Dynamics.Nav.Types.NavCode")
            ?? throw new InvalidOperationException("NavCode type not found — BC metadata shape changed");
        _apvNavCodeCreateTruncated = tNavCode.GetMethod("CreateTruncated",
            BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(int), typeof(string) }, modifiers: null)
            ?? throw new InvalidOperationException("NavCode.CreateTruncated(int,string) not found — BC metadata shape changed");

        var tNavGuid = ResolveType(rt + "NavGuid", "Microsoft.Dynamics.Nav.Types.NavGuid")
            ?? throw new InvalidOperationException("NavGuid type not found — BC metadata shape changed");
        _apvNavGuidCreate = tNavGuid.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(Guid))
            ?? throw new InvalidOperationException("NavGuid.Create(Guid) not found — BC metadata shape changed");

        _apvReflectionReady = true;
    }
}
