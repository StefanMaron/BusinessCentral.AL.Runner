// RecordPatches.MetadataPermissionSetVirtualTable — managed provider for the
// "Metadata Permission Set" system virtual table (2000000250).
//
// WHY THIS EXISTS
//   Creating a user runs entirely through Microsoft's own AL:
//
//     "Users - Create Super User"(codeunit 9000).AddUserAsSuper
//       -> GetSuperRole
//            MetadataPermissionSet.Get(<null guid>, 'SUPER')
//       -> AssignPermissionSetToUser -> AccessControl.Insert(true)
//
//   On the runner 2000000250 routed to the same empty in-memory store as every
//   other table, so the Get found nothing and BC raised
//   `NavCSideRecordNotFoundException: The Metadata Permission Set does not exist.
//   Identification fields and values: App ID='{00000000-...}',Role ID='SUPER'`
//   for every test that creates a user — 57 of them in Microsoft's
//   Tests-SINGLESERVER bucket alone (issue #2313).
//
// WHAT A ROW IS, ON A REAL TIER
//   MetadataPermissionSetDataProvider (Ncl.dll) enumerates
//   NavAppGroup.PermissionSetGroupObjectMetadataSummaries — one entry per
//   `permissionset` object the installed apps declare — and for each one emits
//
//     field 1 "App ID"     the owning app's id, EXCEPT for the two platform roles
//                          SUPER and SECURITY, which get Guid.Empty. That rule is
//                          SystemTableTriggers.IsPermissionSetAppIdNull(roleId),
//                          which returns true for exactly those two names, and it
//                          is why codeunit 9000 looks SUPER up under a null guid.
//     field 2 "Role ID"    the permission set object's NAME (not its caption) —
//                          confirmed by MetadataPermissionSetRelationDataProvider,
//                          which builds the same key with
//                          `new NavCode(len, permissionSet.Name)`.
//     field 3 "Name"       NCLMetaPermissionSet.Caption when the permission set
//                          declares one; when it does not, real BC substitutes the
//                          Role ID instead of leaving it blank — measured directly
//                          (StefanMaron/BusinessCentral.AL.Language.Tests#102,
//                          issue #2388). Base Application's "LOCAL" is one such
//                          set and lists Name = 'LOCAL' on every BC version 27.0-28.4.
//     field 4 "Assignable" NCLMetaPermissionSet.Assignable.
//
// WHERE THE ROWS COME FROM HERE
//   Each dependency .app's own SymbolReference.json, which states every
//   permissionset's Id, Name, Caption property and Assignable property verbatim
//   (BcAppSymbolCache.PermissionSetSymbol); the owning app id is that symbol
//   reference's own AppId (BcAppSymbolCache.AppSymbols.AppId), since every
//   permission set in one symbol file belongs to the same app. Nothing is inferred:
//   a role resolves if and only if an installed app really declares it, and an
//   unknown role still raises BC's own not-found error.
//
//   The rows go into the same in-memory store every other table uses, so BC's own
//   filter / sort / Find engine applies the AL filters — the primary key
//   (App ID, Role ID) included. `Get(<some app id>, 'SUPER')` therefore finds
//   nothing, exactly as on a real tier.
//
// WHAT IS DELIBERATELY NOT SERVED HERE
//   The two sibling tables BC's provider factory lists next to this one:
//   "Metadata Permission" (2000000251, one row per object permission) and
//   "Metadata Permission Set Relation" (2000000252, the include/exclude edges).
//   Neither is read by anything in the cluster this fixes, and serving them
//   truthfully needs the per-object Permissions arrays and the resolved
//   IncludedPermissionSets/ExcludedPermissionSets ids, which is a separate piece
//   of work. They keep answering empty rather than being half-populated here.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (VirtualDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer, TempTableDataProvider). No AL business-logic body is
//   touched — codeunit 9000 runs unmodified, and it is the metadata under it that
//   changes.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int MetadataPermissionSetVirtualTableId = 2000000250;

    // Per in-memory-provider guard so repeated data-access handouts only insert roles
    // that appeared since (idempotent, no duplicate-key throws).
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<string, byte>> _mpsPopulatedByProvider = new();

    private static bool _mpsReflectionReady;
    private static MethodInfo? _mpsNavGuidCreate;      // NavGuid.Create(Guid)
    private static MethodInfo? _mpsNavBooleanCreate;   // NavBoolean.Create(bool)
    private static ConstructorInfo? _mpsNavCodeCtor;   // NavCode(int maxLength, string)

    private static bool IsMetadataPermissionSetVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == MetadataPermissionSetVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind the Metadata Permission Set (2000000250) data
    /// access with one row per <c>permissionset</c> the installed dependency apps declare.
    /// </summary>
    private static void PopulateMetadataPermissionSetVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        // The AllObj block resolved the shared Ncl helpers (system-populated values,
        // buffer ctors, TempTableDataProvider.Insert, NavText/NavValue); reuse them.
        EnsureAllObjReflection(metaTable);
        EnsureMetadataPermissionSetReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Metadata Permission Set (virtual table 2000000250)",
                "metadata-permission-set-virtual-table — data access has no in-memory provider; see docs/scope.md");

        var done = _mpsPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));

        foreach (var (permissionSet, owningAppId, _) in EnumerateKnownPermissionSets())
        {
            if (!done.TryAdd(permissionSet.Name, 0)) continue;
            InsertMetadataPermissionSetRow(provider, metaTable, permissionSet, owningAppId);
        }

        // #2893: the same inventory BC's own permission metadata layer needs, and this is the
        // moment it is complete — the run has parsed its AL source and loaded its dependency
        // .app packages, and something is asking about permission sets. Populating here rather
        // than at session setup also means the app group is filled from the FULL inventory
        // instead of whatever was known before the bundle was read. Idempotent: it recomputes
        // only when the known count changed, and always installs a fresh lazy, so a later call
        // after the inventory grows is correct rather than a no-op.
        EnsurePermissionMetadataPopulated();
    }

    /// <summary>
    /// Every permission set the runner has a real declaration for, paired with the id of the
    /// app that declares it, one entry per role id. A role id is unique across an app group
    /// on a real tier — <c>PermissionSetGroupObjectMetadataSummaries</c> is a dictionary
    /// keyed by it — so the first declaration wins here rather than two apps producing two
    /// rows.
    /// <para>
    /// Two sources, same "source-compiled wins" rule <see cref="RecordPatches.AllProfileVirtualTable"/>
    /// already applies to profiles: permission sets the runner compiled from THIS run's own
    /// AL source (<see cref="RecordPatches.AlPermissionSetParser"/>) come first, then
    /// permission sets declared by a precompiled dependency .app fill in whatever the
    /// source did not already declare. Without the first source, a permission set declared
    /// only in the bundle under test — as Microsoft's own Tests-SINGLESERVER bucket does
    /// with `permissionset 134611 TestSet` — could never appear here, because it has no
    /// .app to read a SymbolReference.json from (#2357).
    /// </para>
    /// </summary>
    private static IEnumerable<(BcAppSymbolCache.PermissionSetSymbol PermissionSet, Guid OwningAppId, string OwningAppName)> EnumerateKnownPermissionSets()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Permission sets the runner compiled from source.
        foreach (var p in ParsedPermissionSets)
            if (seen.Add(p.Name))
                yield return (new BcAppSymbolCache.PermissionSetSymbol(p.Id, p.Name, p.Caption, p.Assignable), p.AppId, p.AppName ?? string.Empty);

        // 2. Permission sets declared by precompiled dependency .app packages.
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            List<BcAppSymbolCache.PermissionSetSymbol> permissionSets;
            Guid owningAppId;
            string owningAppName;
            try
            {
                var symbols = BcAppSymbolCache.Get(appPath);
                permissionSets = symbols.PermissionSets ?? new List<BcAppSymbolCache.PermissionSetSymbol>();
                // AppSymbols.AppId is the .app's own identity, stated at the root of its
                // SymbolReference.json. An app whose symbol file states none leaves the
                // column empty rather than getting an invented id.
                Guid.TryParse(symbols.AppId, out owningAppId);
                // #2893: the owning app's NAME travels with the id now. The permission
                // metadata layer's summaries carry a NavAppRuntimeMetadata owner, and an
                // owner whose Name is empty when SymbolReference.json states one would be a
                // value invented by omission.
                owningAppName = symbols.AppName ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] Metadata Permission Set: SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var p in permissionSets)
                if (seen.Add(p.Name))
                    yield return (p, owningAppId, owningAppName);
        }
    }

    /// <summary>
    /// True for the role ids BC's own
    /// <c>SystemTableTriggers.IsPermissionSetAppIdNull</c> blanks the App ID of: exactly
    /// SUPER and SECURITY, the two roles the platform owns regardless of which app
    /// happens to declare them (both live in the System Application today). Codeunit
    /// 9000's <c>MetadataPermissionSet.Get(NullGuid, 'SUPER')</c> depends on this — with
    /// the declaring app's id in the column the primary-key lookup finds nothing.
    /// </summary>
    private static bool IsPermissionSetAppIdNull(string roleId)
        => string.Equals(roleId, "SUPER", StringComparison.OrdinalIgnoreCase)
        || string.Equals(roleId, "SECURITY", StringComparison.OrdinalIgnoreCase);

    private static void InsertMetadataPermissionSetRow(object provider, NCLMetaTable metaTable, BcAppSymbolCache.PermissionSetSymbol permissionSet, Guid owningAppId)
    {
        // Virtual-record identity: (tableId, permission-set object id, hash(role id)) —
        // stable per role so repeated handouts produce the same SystemId.
        var roleKey = StringComparer.OrdinalIgnoreCase.GetHashCode(permissionSet.Name) & 0x7fffffff;
        var values = _aovSystemValues!.Invoke(
            metaTable, MetadataPermissionSetVirtualTableId, permissionSet.Id, roleKey, 0);

        foreach (var field in GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
        {
            var idx = field.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            if (values.GetValue(idx) != null) continue;   // BC already filled this slot

            values.SetValue(BuildMetadataPermissionSetValue(field, permissionSet, owningAppId), idx);
        }

        var readOnly = _aovCtorReadOnlyBuffer!.Invoke(new object?[] { metaTable, values });
        var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnly });
        try
        {
            _aovTtdpInsert!.Invoke(provider, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
        }
        catch (TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // Same (App ID, Role ID) already present — faithful to a virtual table where
            // that pair is the primary key.
        }
    }

    /// <summary>
    /// One column of a Metadata Permission Set row. Columns are matched by the metatable's
    /// own FIELD NAME (case/space/hyphen-insensitive) so the mapping tracks whatever the
    /// System package in the resolved artifact declares, rather than hardcoded numbers.
    /// Anything we cannot answer truthfully gets BC's own default for that field.
    /// </summary>
    private static object? BuildMetadataPermissionSetValue(NCLMetaField field, BcAppSymbolCache.PermissionSetSymbol permissionSet, Guid owningAppId)
    {
        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "appid":
                return _mpsNavGuidCreate!.Invoke(null, new object?[]
                {
                    IsPermissionSetAppIdNull(permissionSet.Name) ? Guid.Empty : owningAppId
                });
            case "roleid":
                // NavCode(maxLength, value) does NOT truncate — it requires value.Length <=
                // maxLength already and a later ModifyLength(shorter) on an over-length value
                // throws NavNCLStringLengthExceededException rather than losing data (#2357).
                // A real permissionset object's declared NAME can exceed Code[20] — the
                // System Application itself ships one 22 characters long ("System Execute -
                // Basic") — and AL's own Code[N] assignment semantics truncate silently, the
                // same way `SomeCode20Var := SomeLongerText;` does anywhere else in AL. Match
                // that here rather than passing the untruncated symbol name through.
                return RoleIdNavCode(permissionSet, field.FieldDefinedLength);
            case "name":
                // Measured against real BC 27.0-28.4 (StefanMaron/BusinessCentral.AL.Language.Tests#102,
                // StefanMaron/BusinessCentral.AL.Runner#2388): a permission set declaring no Caption is
                // listed with its Role ID substituted for Name, not a blank string. Base Application's
                // "LOCAL" (object id 1001) is one such set and lists Name = 'LOCAL' on every version.
                //
                // #2474: the fallback is the ROLE ID, not the permissionset object's declared name,
                // and those differ in case. A Code field is upper-case, so the Role ID of
                // `permissionset 60022 ALTPermissionSet` is ALTPERMISSIONSET — which is what real BC
                // listed as its Name on all 8 legs (the measurement in #2474, from the corpus test
                // that had asserted a blank and reported Actual:<ALTPERMISSIONSET>). Returning
                // permissionSet.Name here gave the object name verbatim, mixed case, so the runner
                // answered ALTPermissionSet where BC answers ALTPERMISSIONSET. Measured before this
                // change on a caption-less `permissionset 64981 PspMixedCase`: the Role ID column
                // already read PSPMIXEDCASE while Name read PspMixedCase, from the same object.
                //
                // The upper-casing is BC's own — this builds the SAME NavCode the "roleid" case
                // returns and reads its text back, rather than re-deriving the rule with a
                // ToUpper() here. That also carries the Code[20] truncation across for free, so a
                // 22-character permissionset name falls back to the same 20 characters the Role ID
                // column shows rather than to a longer string the two columns would disagree on.
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[]
                {
                    field.FieldDefinedLength,
                    string.IsNullOrEmpty(permissionSet.Caption)
                        ? RoleIdText(permissionSet)
                        : permissionSet.Caption
                });
            case "assignable":
                return _mpsNavBooleanCreate!.Invoke(null, new object?[] { permissionSet.Assignable });
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }


    /// <summary>Role ID of a permission set: BC's own NavCode over the object's declared name,
    /// truncated to the Code field's length first (see the "roleid" case for why the ctor cannot
    /// truncate itself). Shared with the Name fallback so the two columns can never disagree
    /// about case or length (#2474).</summary>
    private static object RoleIdNavCode(BcAppSymbolCache.PermissionSetSymbol permissionSet, int codeLength)
        => _mpsNavCodeCtor!.Invoke(new object?[]
        {
            codeLength,
            permissionSet.Name.Length > codeLength
                ? permissionSet.Name.Substring(0, codeLength)
                : permissionSet.Name
        });

    /// <summary>The Role ID as text, read back off BC's own NavCode so whatever that type does to
    /// a Code value — upper-casing above all — is BC's answer and not one re-derived here.</summary>
    private static string RoleIdText(BcAppSymbolCache.PermissionSetSymbol permissionSet)
    {
        var code = RoleIdNavCode(permissionSet, MetadataPermissionSetRoleIdLength);
        _mpsNavStringValueValue ??= code.GetType().GetProperty("Value",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[MetadataPermissionSet] {code.GetType().Name}.Value not found — the Name "
                + "fallback cannot read the Role ID back off BC's own NavCode");
        return _mpsNavStringValueValue.GetValue(code) as string ?? string.Empty;
    }

    /// <summary>"Role ID" is Code[20] on table 2000000250. Named rather than re-read per call
    /// because the Name column is a different length and must not be used for the Role ID.</summary>
    private const int MetadataPermissionSetRoleIdLength = 20;

    private static PropertyInfo? _mpsNavStringValueValue;   // NavStringValue.Value (string)

    private static void EnsureMetadataPermissionSetReflection(NCLMetaTable metaTable)
    {
        if (_mpsReflectionReady) return;

        var nclAsm = metaTable.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        var tNavGuid = ResolveType(rt + "NavGuid", "Microsoft.Dynamics.Nav.Types.NavGuid")
            ?? throw new InvalidOperationException("NavGuid type not found");
        _mpsNavGuidCreate = tNavGuid.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(Guid) }, modifiers: null)
            ?? throw new InvalidOperationException("NavGuid.Create(Guid) not found");

        var tNavBoolean = ResolveType(rt + "NavBoolean", "Microsoft.Dynamics.Nav.Types.NavBoolean")
            ?? throw new InvalidOperationException("NavBoolean type not found");
        _mpsNavBooleanCreate = tNavBoolean.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(bool) }, modifiers: null)
            ?? throw new InvalidOperationException("NavBoolean.Create(bool) not found");

        var tNavCode = ResolveType(rt + "NavCode", "Microsoft.Dynamics.Nav.Types.NavCode")
            ?? throw new InvalidOperationException("NavCode type not found");
        _mpsNavCodeCtor = tNavCode.GetConstructor(new[] { typeof(int), typeof(string) })
            ?? throw new InvalidOperationException("NavCode(int,string) ctor not found");

        _ = nclAsm;
        _mpsReflectionReady = true;
    }
}
