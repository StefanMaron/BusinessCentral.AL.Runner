// RecordPatches.PublishedApplicationSystemTable — list the apps the runner loaded in the
// Published Application table, the way a service tier lists them after publishing (#2963).
//
// WHY THIS EXISTS
//   Published Application (2000000206) is ordinary application-database storage
//   (SystemTables.ApplicationDatabaseTables), not a computed virtual table, so it reads
//   cleanly in the runner and simply had no rows. Nothing in the runner ever wrote any:
//   in real BC it is app PUBLISHING that writes them, and the runner has no publish step.
//
//   That is not an obscure read. System Application code gates module-level permissions on
//   it. `Reten. Pol. Allowed Tbl. Impl.ModuleOwnsTable`, reached from every
//   `Reten. Pol. Allowed Tables.AddAllowedTable` call:
//
//       PublishedApplication.SetRange("ID", CallerModuleInfo.Id);
//       PublishedApplication.SetRange("Version Major",    CallerModuleInfo.AppVersion.Major);
//       ... Minor / Build / Revision ...
//       PublishedApplication.SetFilter("Tenant ID", '%1|%2', '', TenantInformation.GetTenantId());
//       if not PublishedApplication.FindFirst() then begin
//           RetentionPolicyLog.LogWarning(...);          // warns, does NOT raise
//           exit(false);
//       end;
//
//   With no rows the FindFirst always failed, so every AddAllowedTable declined and no table
//   was ever on the retention-policy allowed list. BC logs a warning rather than raising, so
//   nothing announced it — until #2932 stopped table-event subscribers' errors being
//   discarded, and codeunit 2 "Company-Initialize" started dying on the downstream
//   "Table 405 Change Log Entry is not in the list of allowed tables". Measured: with that
//   fix and without this one, Company Information / General Ledger Setup / Inventory Setup /
//   Warehouse Setup / Source Code Setup all went from 1 row to 0.
//
// WHAT THIS DOES
//   One row per distinct loaded app id (BcRuntime.RegisteredModules(), which covers the
//   precompiled Microsoft apps, source-compiled dependencies and the bundle under test),
//   carrying only columns the runner can answer truthfully:
//
//     ID / Name / Publisher              ← the app manifest, the same values
//                                          NavApp.GetModuleInfo already reports
//     Version Major/Minor/Build/Revision ← parsed from that manifest's version
//     Tenant ID                          ← "" — matches BC's own '%1|%2' filter against
//                                          ('', tenant id) without inventing a tenant id
//     Runtime Package ID / Package ID    ← AppPackageIdentity, the deterministic per-app
//                                          value ALSO stamped onto this app's AllObj rows.
//                                          ONE GUID in both columns, which is what a real
//                                          tier reports for a freshly published app — see
//                                          AppPackageIdentity's header for the measurement.
//
//   It also seeds INSTALLED APPLICATION (2000000212), one row per loaded app, because that is
//   what BC's own metadata makes `Published Application.Installed` mean (#3066):
//
//       field(13; Installed; Boolean)
//       {
//           CalcFormula = Exist("Installed Application"
//                               WHERE("Runtime Package ID" = FIELD("Runtime Package ID")));
//           FieldClass  = FlowField;
//       }
//
//   2000000212 is ordinary application-database storage like 2000000206, not a computed
//   virtual table, so the runner can answer it truthfully rather than fabricate a value: an
//   app the runner LOADED and whose install triggers it FIRED is installed. The FlowField is
//   then evaluated by BC's own CalcFields against BC's own provider — nothing writes to the
//   FlowField itself, which would be silently discarded.
//
//   This file used to say Installed "is a FlowField with nothing behind it here", and the
//   runner-local test pinned it reading FALSE. BusinessCentral.AL.Language.Tests#187 put that
//   to a real service tier: `PublishedApplication_CalcFields_Installed_IsTrueForThisApp` passed
//   on all eight OnPrem legs, 27.0 through 28.4. There WAS something behind it — a table the
//   runner had simply never seeded.
//
//   Still deliberately NOT filled: "Tenant Visible" and "PerTenant Or Installed" are
//   Lookup FlowFields over "NAV App Extra" (2000000157), which is a VIRTUAL table (System.app
//   ships it under src/Virtual Tables/), so there is no row for the runner to insert the way
//   there is for 2000000212. They keep reading as the Boolean default here. What a real tier
//   reports for them is unmeasured, and is tracked as #3072 rather than asserted from a
//   reading — which is the mistake this file's own header records two of.
//
//   Every other column keeps NCLMetaField.EmptyValue, BC's own per-field default — nothing is
//   invented. Fields are located by NAME off the metatable at runtime, so a BC metadata change
//   says so instead of writing into the wrong slot.
//
//   It runs once per bundle, immediately before CaptureInstallBaseline(), for the same reason
//   the Company row does: seeded after the baseline, the first codeunit boundary drops it.
//
// PRECOMPILED-DLL RESPECT
//   No BC business-logic body is touched. The row is built with BC's own value factory and
//   inserted through BC's own in-memory provider Insert, exactly as the Company row and the
//   test-data hydration path do.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int PublishedApplicationSystemTableId = 2000000206;

    /// <summary>Installed Application — the table <c>Published Application.Installed</c>'s
    /// <c>Exist(...)</c> FlowField is computed over. Seeded alongside 2000000206 (#3066).</summary>
    internal const int InstalledApplicationSystemTableId = 2000000212;

    private static bool _publishedApplicationBundleRowSeededForThisBundle;

    internal static void ResetPublishedApplicationSystemTableForNewBundle()
        => _publishedApplicationBundleRowSeededForThisBundle = false;

    /// <summary>
    /// Seed the Published Application rows for the loaded DEPENDENCY apps — everything except
    /// the bundle under test. Call before <c>InstallTriggerRunner.RunDependenciesOnly()</c>:
    /// System Application install triggers call <c>AddAllowedTable</c>, which needs its own
    /// app's row to already be there.
    ///
    /// Split from the bundle's own row deliberately, mirroring
    /// <c>RunDependenciesOnly</c>/<c>RunTestAssemblyOnly</c>. These rows are captured into the
    /// dependency+company baseline snapshot, whose cache key
    /// (<c>InstallTriggerRunner.CurrentDependencySetKey()</c>) covers the dependency set and
    /// NOT the bundle — so seeding the bundle's row here would let one app group's row be
    /// restored into another app group that shares the same dependency closure.
    /// </summary>
    internal static void EnsurePublishedApplicationDependencyRowsSeeded()
        => SeedPublishedApplicationRows(includeBundle: false, "dependency");

    /// <summary>
    /// Seed the Published Application row for the bundle under test, once per bundle. Call
    /// after its own install triggers and BEFORE <c>CaptureInstallBaseline()</c>, the same
    /// point and for the same reason as the Company row: seeded after the baseline, the first
    /// codeunit boundary would drop it.
    /// </summary>
    internal static void EnsurePublishedApplicationBundleRowSeeded()
    {
        if (_publishedApplicationBundleRowSeededForThisBundle) return;
        SeedPublishedApplicationRows(includeBundle: true, "bundle");
        // Set AFTER the work, never before it. Setting it first makes the flag mean "someone
        // started" rather than "the row is there", so a throw out of the seed leaves it latched
        // and a later call returns early having done nothing — the run then proceeds against a
        // table this method reports as seeded. #2941's review rejected exactly this pattern
        // next door; the same objection applies here.
        _publishedApplicationBundleRowSeededForThisBundle = true;
    }

    private enum SeedOutcome { Inserted, AlreadyPresent, Failed }

    private static void SeedPublishedApplicationRows(bool includeBundle, string what)
    {
        var meta = EnsureTableInMetadataCache(PublishedApplicationSystemTableId);
        if (meta == null)
            // No Published Application metatable in this bundle's closure — a bundle with no
            // Microsoft platform apps has nothing that reads it. Same shape as the Company
            // seeder's "no Base App in this bundle" early return.
            return;

        // Installed Application (2000000212) is what the Installed FlowField on 2000000206 is
        // computed over. Its ABSENCE is not fatal in the way a missing 2000000206 would be:
        // a closure can carry the one and not the other, and the FlowField then reads false as
        // it did before #3066. A closure that has 2000000206 has always had 2000000212 in
        // practice (both ship in System.app), so this is a guard, not an expected path.
        var installedMeta = EnsureTableInMetadataCache(InstalledApplicationSystemTableId);

        var source = ResolveSkeletonDataAccessSource();
        if (source == null)
        {
            // #3068: `[warn]`, not `[PublishedApplication]` — Log.cs drops component-tagged lines
            // at default verbosity, so this explanation for a whole run's worth of declined
            // module-ownership checks was reaching nobody unless --verbose was set.
            Console.Error.WriteLine(
                "[warn] PublishedApplication: the skeleton session has no DataAccessSource yet, so no "
                + "Published Application rows (2000000206) were seeded — System Application "
                + "module-ownership checks will decline. See AlRunner#2963.");
            return;
        }

        var bundleAppId = AlRunner.BcRuntime.GetCurrentModuleAppInfo().AppId;
        var modules = AlRunner.BcRuntime.RegisteredModules()
            .Where(m => includeBundle ? m.AppId == bundleAppId : m.AppId != bundleAppId)
            .ToList();
        if (modules.Count == 0) return;

        int seeded = 0, alreadyPresent = 0, installedSeeded = 0, installedAlreadyPresent = 0;
        var failed = new List<string>();

        SeedOutcome Run(string label, Action insert)
        {
            try
            {
                insert();
                return SeedOutcome.Inserted;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : ex;
                if (inner.GetType().Name == "NavRecordAlreadyExistsException")
                    return SeedOutcome.AlreadyPresent;   // already present — not a failure.
                failed.Add($"{label}: {inner.GetType().Name}: {inner.Message}");
                return SeedOutcome.Failed;
            }
        }

        foreach (var m in modules)
        {
            var moduleLabel = $"{m.Publisher}_{m.Name} v{m.Version}";

            switch (Run(moduleLabel, () => InsertPublishedApplicationRow(meta, source, m)))
            {
                case SeedOutcome.Inserted: seeded++; break;
                case SeedOutcome.AlreadyPresent: alreadyPresent++; break;
            }

            if (installedMeta == null) continue;

            // Deliberately attempted even when the Published Application row was already
            // present: the two tables are seeded together but are separate inserts, and
            // skipping this one on "already present" would leave an app listed as published
            // and not installed — precisely the mismatch this seeding exists to avoid.
            switch (Run($"{moduleLabel} (installed)",
                        () => InsertInstalledApplicationRow(installedMeta, source, m)))
            {
                case SeedOutcome.Inserted: installedSeeded++; break;
                case SeedOutcome.AlreadyPresent: installedAlreadyPresent++; break;
            }
        }

        // A PARTIAL row set must not be allowed to continue, and specifically must not be
        // captured. The dependency half of this seeding runs inside the install-baseline cache
        // MISS branch, and that branch's snapshot is PERSISTED to disk: a transient failure
        // here would bake a short row set into the cache, and every later run would restore it
        // as a HIT and print nothing at all. The apps missing from it would silently fail
        // module-ownership checks — the runner answering "this app does not own its own table"
        // with no indication that anything went wrong, which is the exact silent-wrong-answer
        // shape .claude/rules/loud-failures.md forbids. Per-row stderr is not enough: nobody
        // reads it on the run that poisons the cache, and there is nothing to read on the runs
        // that consume it. The Installed Application half is held to the same bar for the same
        // reason: a short row set there makes Installed read false for some apps and true for
        // others, from a cache, silently.
        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"[PublishedApplication] seeded only {seeded + alreadyPresent} of {modules.Count} "
                + $"{what} row(s) into table {PublishedApplicationSystemTableId} and "
                + $"{installedSeeded + installedAlreadyPresent} into table "
                + $"{InstalledApplicationSystemTableId}; refusing to continue "
                + "because a partial row set would be captured into the install baseline and "
                + "restored silently by later runs. Failures: " + string.Join(" | ", failed)
                + " — see AlRunner#2963.");

        if (seeded > 0 || installedSeeded > 0)
            PerfTrace.Log(
                $"PublishedApplication: seeded {seeded} {what} row(s)"
                + (alreadyPresent > 0 ? $" ({alreadyPresent} already present)" : "")
                + $", {installedSeeded} installed-application row(s)"
                + (installedAlreadyPresent > 0 ? $" ({installedAlreadyPresent} already present)" : ""));
    }

    /// <summary>
    /// Split a manifest version string into BC's four Published Application columns. A part
    /// that is absent or not a number becomes 0, which is what a two- or three-part manifest
    /// version means; it is never guessed at from another part.
    /// </summary>
    internal static (int Major, int Minor, int Build, int Revision) SplitManifestVersion(string? version)
    {
        var parts = (version ?? string.Empty).Split('.');
        int At(int i) => i < parts.Length && int.TryParse(parts[i], out var v) ? v : 0;
        return (At(0), At(1), At(2), At(3));
    }

    private static void InsertPublishedApplicationRow(
        NCLMetaTable meta, object source,
        (Guid AppId, string Name, string Publisher, string Version) module)
    {
        var (major, minor, build, revision) = SplitManifestVersion(module.Version);

        InsertApplicationTableRow(
            PublishedApplicationSystemTableId, meta, source,
            // "ID" is the column BC filters on to find an app at all. Its absence means this is
            // not the Published Application table this file was written against.
            requiredFieldName: "ID",
            fill: Set =>
            {
                Set("ID", module.AppId);
                Set("Name", module.Name);
                Set("Publisher", module.Publisher);
                Set("Version Major", major);
                Set("Version Minor", minor);
                Set("Version Build", build);
                Set("Version Revision", revision);
                // BC filters Tenant ID with '%1|%2' against ('', TenantInformation.GetTenantId()),
                // so the empty string matches without the runner inventing a tenant id it does
                // not have.
                Set("Tenant ID", string.Empty);
                Set("Runtime Package ID", AppPackageIdentity.RuntimePackageIdFor(module.AppId));
                Set("Package ID", AppPackageIdentity.PackageIdFor(module.AppId));
            });
    }

    /// <summary>
    /// One Installed Application (2000000212) row per loaded app, carrying the SAME runtime
    /// package id as that app's Published Application row — which is the join
    /// <c>Published Application.Installed</c>'s <c>Exist(...)</c> FlowField makes. Seeding a
    /// different id here, or none, would make the FlowField read false and the runner would be
    /// back to reporting a loaded, install-triggered app as not installed (#3066).
    /// </summary>
    private static void InsertInstalledApplicationRow(
        NCLMetaTable meta, object source,
        (Guid AppId, string Name, string Publisher, string Version) module)
    {
        InsertApplicationTableRow(
            InstalledApplicationSystemTableId, meta, source,
            // The whole point of the row: the column the FlowField matches on.
            requiredFieldName: "Runtime Package ID",
            fill: Set =>
            {
                Set("Runtime Package ID", AppPackageIdentity.RuntimePackageIdFor(module.AppId));
                Set("Package ID", AppPackageIdentity.PackageIdFor(module.AppId));
                // Part of this table's primary key ("Runtime Package ID", "Tenant ID") and the
                // same blank the Published Application row carries, for the same reason.
                Set("Tenant ID", string.Empty);
            });
    }

    /// <summary>
    /// Insert one row into an ordinary application-database system table through BC's own value
    /// factory and BC's own in-memory provider Insert. Fields are located by NAME off the
    /// metatable at runtime, so a BC metadata change says so instead of writing into the wrong
    /// slot, and every column the caller does not set keeps <c>NCLMetaField.EmptyValue</c> —
    /// BC's own per-field default. Nothing is invented.
    /// </summary>
    private static void InsertApplicationTableRow(
        int tableId, NCLMetaTable meta, object source,
        string requiredFieldName, Action<Action<string, object?>> fill)
    {
        var fieldByName = new Dictionary<string, NCLMetaField>(StringComparer.OrdinalIgnoreCase);
        for (var fi = 0; fi < meta.FieldCount; fi++)
        {
            var f = meta.GetFieldByIndex(fi);
            fieldByName[f.FieldName] = f;
        }

        if (!fieldByName.ContainsKey(requiredFieldName))
            throw new InvalidOperationException(
                $"Table {tableId} metatable has no \"{requiredFieldName}\" field "
                + $"[fields={string.Join("/", fieldByName.Values.Select(f => $"{f.FieldNo}:{f.FieldName}"))}] "
                + "— BC metadata shape changed");

        var values = new NavValue[meta.FieldCount];
        for (var fi = 0; fi < meta.FieldCount; fi++)
        {
            var f = meta.GetFieldByIndex(fi);
            var idx = f.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            values[idx] = f.EmptyValue;
        }

        void Set(string fieldName, object? value)
        {
            if (value == null) return;
            if (!fieldByName.TryGetValue(fieldName, out var f)) return;
            var idx = f.FieldIndex;
            if (idx < 0 || idx >= values.Length) return;
            values[idx] = value is NavValue already ? already : NavValue.CreateNavValueFromObject(f, value);
        }

        fill(Set);

        var perTable = _dataAccessByTable.GetValue(source,
            static _ => new System.Collections.Concurrent.ConcurrentDictionary<int, object>());
        var dataAccess = perTable.GetOrAdd(tableId,
            _ => _mCreateTempDataAccess!.Invoke(source, new object[] { meta })!);
        var provider = GetDataProvider(dataAccess)
            ?? throw new InvalidOperationException(
                $"Table {tableId} data access exposes no in-memory DataProvider");

        var insert = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Insert" && m.GetParameters().Length == 4
                     && m.GetParameters()[0].ParameterType == typeof(int));
        var insertOptions = Enum.ToObject(insert.GetParameters()[2].ParameterType, 0);

        var mutableCtor = typeof(ReadOnlyRecordBuffer).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.MutableRecordBuffer")
            ?.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: new[] { typeof(ReadOnlyRecordBuffer) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "MutableRecordBuffer(ReadOnlyRecordBuffer) not found — BC metadata shape changed");

        var readOnly = new ReadOnlyRecordBuffer(meta, values);
        var mutable = mutableCtor.Invoke(new object[] { readOnly });
        insert.Invoke(provider, new object?[] { 0, mutable, insertOptions, null });
    }
}
