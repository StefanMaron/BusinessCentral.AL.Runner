// AllProfileWritePatches — BC's write rules for the "All Profile" (2000000178) system
// virtual table, enforced at the AL write entry points.
//
// WHY THIS EXISTS
//   RecordPatches.AllProfileVirtualTable.cs makes All Profile answer with real rows by
//   populating the in-memory TempTableDataProvider, which means AL's Insert / Modify /
//   Delete / Rename land on that store like any ordinary table's would. On a real tier
//   they do not: AllProfileDataProvider routes all four through
//   TenantProfileTableDataHandler, which REFUSES most of them.
//
//   Without this guard the runner would let AL delete a profile an installed app declares
//   and report success. That is exactly the silent-wrong-answer shape
//   .claude/rules/loud-failures.md exists to prevent, and Microsoft's own
//   AllProfile V2 Test.TestDeleteExtensionProfileFails asserts the refusal verbatim.
//
// THE RULES, AS BC STATES THEM (Ncl, TenantProfileTableDataHandler)
//   Every rule keys off one thing: whether the row is TENANT-OWNED, i.e. whether its
//   "App ID" is NavConfigurationDesignerExtension.ConfigurationAppId — which is
//   Guid.Empty (see that class: ConfigurationAppId = UserCreatedProfileAppId = Guid.Empty).
//
//   - InsertAsync   : `if (appId != ConfigurationAppId) throw ProfileCannotBeInsertedWithAppId`
//                     and, before that, an empty Profile ID throws
//                     ProfileCannotBeInsertedWithEmptyProfileId.
//   - DeleteAsync   : `if (appId != ConfigurationAppId) throw DeleteAppProfileNotAllowed`
//                     — "Cannot delete '<id>' profile from an Installed Application."
//   - ModifyAsync   : a KEY change (App ID or Profile ID) on an app-owned profile throws
//                     ModifySpecificFieldsOnAppProfileNotAllowed. A non-key modify is
//                     ALLOWED on app-owned profiles too — it writes the per-tenant profile
//                     settings ("Default Role Center" / "Disable Personalization"), which is
//                     what Microsoft's Cleanup() does to every profile in the table. So
//                     Modify is deliberately NOT guarded here; only Rename is.
//
// THE MESSAGES ARE BC'S OWN
//   Every message is read out of Ncl's own resource class at runtime rather than restated
//   here, so the runner cannot drift from the wording a test asserts, and a BC version that
//   rewords one is followed automatically. If the resource cannot be found the guard says
//   so loudly instead of inventing a message.
//
// PRECOMPILED-DLL RESPECT
//   No AL business-logic body is touched. These are static helpers Cecil PREPENDS to
//   NavRecord's own AL write entry points in the runtime engine (Ncl.dll) — the same
//   mechanism the rowversion clock already uses.
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static class AllProfileWritePatches
{
    /// <summary>Prepended to NavRecord.ALInsertAsync. No-op for every table but All Profile.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GuardAllProfileInsert(object? record)
    {
        if (Subject(record) is not { } rec) return;
        var (appId, profileId) = KeyOf(rec);

        if (string.IsNullOrEmpty(profileId))
            throw NavCSideError(
                Message("ProfileCannotBeInsertedWithEmptyProfileId"), profileId, appId);

        if (appId != RecordPatches.AllProfileTenantAppId)
            throw NavCSideError(Message("ProfileCannotBeInsertedWithAppId"), profileId, appId);
    }

    /// <summary>Prepended to NavRecord.ALDeleteAsync. No-op for every table but All Profile.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GuardAllProfileDelete(object? record)
    {
        if (Subject(record) is not { } rec) return;
        var (appId, profileId) = KeyOf(rec);
        if (appId != RecordPatches.AllProfileTenantAppId)
            throw NavCSideError(Message("DeleteAppProfileNotAllowed"), profileId);
    }

    /// <summary>Prepended to NavRecord.ALRenameAsync. No-op for every table but All Profile.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GuardAllProfileRename(object? record)
    {
        if (Subject(record) is not { } rec) return;
        var (appId, profileId) = KeyOf(rec);
        if (appId != RecordPatches.AllProfileTenantAppId)
            throw NavCSideError(Message("ModifySpecificFieldsOnAppProfileNotAllowed"), profileId);
    }

    /// <summary>
    /// The record under write when it is a NON-TEMPORARY All Profile record, else null.
    /// A `temporary Record "All Profile"` is a plain temp table on a real tier too — it
    /// never reaches AllProfileDataProvider — so none of these rules apply to it.
    /// </summary>
    private static NavRecord? Subject(object? record)
        => record is NavRecord { IsTemporary: false } rec
           && rec.MetaTable?.TableId == RecordPatches.AllProfileVirtualTableId
            ? rec
            : null;

    // Field numbers are read off the record's OWN metatable by field name, never hardcoded,
    // so a BC version that renumbers All Profile is followed rather than silently misread.
    private static int _appIdFieldNo, _profileIdFieldNo;

    private static (Guid AppId, string ProfileId) KeyOf(NavRecord rec)
    {
        if (_appIdFieldNo == 0)
        {
            _appIdFieldNo = FieldNoByName(rec, "App ID");
            _profileIdFieldNo = FieldNoByName(rec, "Profile ID");
        }
        var appIdValue = rec.GetFieldValue(_appIdFieldNo)?.ToString();
        var profileId = rec.GetFieldValue(_profileIdFieldNo)?.ToString() ?? string.Empty;
        return (Guid.TryParse(appIdValue, out var g) ? g : Guid.Empty, profileId);
    }

    private static int FieldNoByName(NavRecord rec, string fieldName)
    {
        foreach (var f in RecordPatches.GetAllFields(rec.MetaTable!) ?? Enumerable.Empty<NCLMetaField>())
            if (string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                return f.FieldNo;
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "All Profile (virtual table 2000000178)",
            $"all-profile-virtual-table — metatable has no \"{fieldName}\" field, so BC's write rules "
            + "cannot be applied; see docs/scope.md");
    }

    // Ncl's own resource class (internal, resx-generated). Located by the presence of the
    // very property we need rather than by a namespace-qualified name, so a BC build that
    // moves it is still found.
    private static Type? _langType;
    private static readonly Dictionary<string, string> _messageCache = new(StringComparer.Ordinal);

    private static string Message(string resourceName)
    {
        lock (_messageCache)
        {
            if (_messageCache.TryGetValue(resourceName, out var cached)) return cached;

            _langType ??= FindLangType()
                ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    "All Profile (virtual table 2000000178)",
                    "all-profile-virtual-table — Ncl's profile message resources could not be located, so "
                    + "BC's own refusal wording cannot be reproduced; see docs/scope.md");

            var prop = _langType.GetProperty(resourceName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    "All Profile (virtual table 2000000178)",
                    $"all-profile-virtual-table — Ncl states no '{resourceName}' message resource; "
                    + "see docs/scope.md");

            var text = prop.GetValue(null) as string
                ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    "All Profile (virtual table 2000000178)",
                    $"all-profile-virtual-table — Ncl's '{resourceName}' message resource is empty; "
                    + "see docs/scope.md");

            _messageCache[resourceName] = text;
            return text;
        }
    }

    /// <summary>
    /// Ncl's resx-generated resource class, found by the presence of the very property this
    /// guard needs. Assembly.GetTypes() on the BC assemblies routinely throws
    /// ReflectionTypeLoadException on the skeleton (a handful of types reference members of
    /// assemblies the runner never loads), and the types it DID load come back on the
    /// exception -- so a plain GetTypes() call finds nothing at all, which is what made the
    /// first version of this look like "BC has no such resource".
    /// </summary>
    private static Type? FindLangType()
    {
        foreach (var asm in new[] { typeof(NavRecord).Assembly }
                     .Concat(AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => a.GetName().Name?.StartsWith(
                             "Microsoft.Dynamics.Nav", StringComparison.Ordinal) == true))
                     .Distinct())
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (t.GetProperty("DeleteAppProfileNotAllowed",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null)
                    return t;
            }
        }
        return null;
    }

    private static Type? _navCSideExceptionType;

    /// <summary>
    /// Build the exception BC itself raises for these refusals — a NavCSideException, which
    /// is what AL's <c>asserterror</c> traps and whose Message is what
    /// <c>Assert.ExpectedError</c> reads.
    /// </summary>
    private static Exception NavCSideError(string format, params object?[] args)
    {
        var message = string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);
        _navCSideExceptionType ??= typeof(NavRecord).Assembly.GetType(
            "Microsoft.Dynamics.Nav.Runtime.NavCSideException");
        if (_navCSideExceptionType != null
            && Activator.CreateInstance(_navCSideExceptionType, message) is Exception typed)
            return typed;
        // Never swallow the refusal: an untyped exception still stops the write and still
        // carries BC's message, which is what AL observes.
        return new InvalidOperationException(message);
    }
}
