// RecordPatches.NavAppExtraVirtualTable — managed provider for the "NAV App Extra"
// (2000000157) system virtual table, which is what Published Application's other two
// FlowFields are computed over (#3072).
//
// WHY THIS EXISTS
//   Published Application (2000000206) carries three Boolean FlowFields. #3066 settled the
//   first one — Installed — by seeding the ordinary application-database table it is an
//   Exist() over. The other two are Lookups over a DIFFERENT table:
//
//       field(30; "Tenant Visible"; Boolean)
//       {
//           CalcFormula = Lookup("NAV App Extra"."Tenant Visible"
//                                WHERE("Runtime Package ID" = FIELD("Runtime Package ID")));
//       }
//       field(31; "PerTenant Or Installed"; Boolean)
//       {
//           CalcFormula = Lookup("NAV App Extra"."PerTenant Or Installed"
//                                WHERE("Runtime Package ID" = FIELD("Runtime Package ID")));
//       }
//
//   2000000157 is VIRTUAL — System.app ships it under src/Virtual Tables/ and Ncl's own
//   NavAppExtraDataProvider computes every row — so there is no row to seed the way there was
//   for Installed Application (2000000212). It routed to the same empty in-memory store as
//   every other unprovided table here, so it had zero rows and both FlowFields read FALSE.
//
//   That false is the shape this repository has already been wrong about once. Both columns
//   are Boolean, so "computed no" and "computed nothing" are the same observable, and #3066's
//   header records the runner having pinned Installed as false on exactly that reasoning
//   before a real tier said true. "Tenant Visible" gates what the extension-management pages
//   and several System Application callers consider visible to a tenant, so answering false
//   for every app is the runner saying "no app is visible to this tenant" — a plausible-looking
//   wrong answer that stays invisible until something unrelated fails.
//
// WHAT BC'S OWN PROVIDER COMPUTES (Ncl.dll, measured on 27.5 — NavAppExtraDataProvider)
//   Four columns, and the two the FlowFields read are both derived, never stored:
//
//       GetAllItems():
//           buffer[0] = RuntimePackageId
//           buffer[1] = IsAppVisibleToTenant(app, session tenant)
//           buffer[2] = app.Scope == NavAppScope.Tenant || app is in this tenant's app group
//           buffer[3] = PackageId
//
//       IsAppVisibleToTenant(app, tenantId):
//           Scope == Global -> true
//           Scope == Tenant -> app.TenantId == tenantId
//           otherwise       -> false
//
//   So for a GLOBALLY-scoped app that is installed in the session's tenant, both columns are
//   true. That is the reading, and it is not what this file rests on — see the next section.
//
// BC'S OWN PROVIDER IS TRIED FIRST, AND THE FALLBACK IS NOT A SECOND OPINION
//   PopulateNavAppExtraVirtualTable constructs Ncl's NavAppExtraDataProvider on the skeleton
//   session and takes its rows, exactly the way the Feature Key populator does. When that
//   works, the rows are BC's own and nothing here decides anything.
//
//   MEASURED, not predicted: on BC 28.1.49838.53910 it produces **0 rows** here, and the
//   fallback below is what answers. The reason is structural rather than incidental — the
//   provider reads NavEnvironment.Instance.NavAppMetadataRetriever.GetAllMetadata() and
//   NavCurrentThread.Session.Tenant.NavAppGroup. The runner publishes nothing and installs
//   nothing, so the retriever is empty and the app group is NavAppGroup.BaseGroup — see
//   BcRuntime's OverriddenAppGroup note. An empty retriever makes the provider return an empty
//   row set, and an empty row set is indistinguishable from "no app is visible" at every later
//   read.
//
//   It is still tried first, and that is deliberate rather than decorative: the day the runner
//   populates NavAppMetadataRetriever — which is a plausible future, since several other
//   surfaces would want it — BC's own rows are the ones that should win, without this file
//   needing to be found and changed. Nothing about the fallback makes the BC path harder to
//   reach; it only fires on an empty result.
//
//   So the fallback builds the same four columns from the SAME app list the Published
//   Application seeder uses — BcRuntime.RegisteredModules(), the manifests the runner already
//   parses for NavApp.GetModuleInfo — with the SAME AppPackageIdentity values, so a row here
//   and that app's 2000000206 row can never disagree about which app they describe. The two
//   derived columns then answer for what the runner can actually establish about an app it
//   LOADED into this session:
//
//       Tenant Visible          — the runner has one tenant and one session, and every app it
//                                 loaded is loaded FOR that session. There is no per-tenant
//                                 scoping to be excluded by, so BC's Global branch is the one
//                                 that applies: true.
//       PerTenant Or Installed  — the "Or Installed" half. #3066 already seeds an Installed
//                                 Application (2000000212) row for every one of these apps, on
//                                 the same runtime package id, and Published Application.Installed
//                                 reads true from it on a real tier. An app the runner reports
//                                 as installed cannot also report as not-installed here without
//                                 the two tables contradicting each other: true.
//
//   Both are stated as what the runner can establish, not as a claim about what a service tier
//   reports for an arbitrary app. The tier's answer is asked upstream, in the corpus's
//   Target = OnPrem app (BusinessCentral.AL.Language.Tests#228), because
//   .claude/rules/bc-behavior-tests-go-upstream.md is the rule #3066 exists because nobody
//   followed.
//
// WHAT IS NOT ANSWERED, AND THROWS RATHER THAN GUESSING
//   A column this file does not recognise keeps BC's own GetDefaultNavValue — the same
//   treatment every other populator here gives a column it has no source for. What it does NOT
//   do is invent a row set: if the runner has no loaded modules, or the store is not there, or
//   the metatable is not the shape BC's own provider fills, it REFUSES. Answering with an
//   empty table would put back precisely the false-for-every-app reading this file exists to
//   remove, and .claude/rules/loud-failures.md is explicit that replacing one silent wrong
//   answer with another is the failure mode.
//
// PRECOMPILED-DLL RESPECT
//   NavAppExtraDataProvider, NavSession and ReadOnlyRecordBuffer are runtime-engine types,
//   which .claude/rules/precompiled-dll-respect.md makes ours to drive. No AL business-logic
//   body is touched: the rows go into BC's own in-memory provider through BC's own Insert, and
//   BC's own CalcFields computes the FlowFields off them. Nothing writes to a FlowField, which
//   would be silently discarded.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for
    /// why the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945).
    /// </summary>
    /// <remarks>
    /// Category (2) for all of them. Every one fires where the runner cannot answer at all —
    /// no in-memory store, no loaded modules to describe, or a metatable that is not the shape
    /// BC's own NavAppExtraDataProvider fills. None of them is a value choice: the two derived
    /// columns are answers this file gives on purpose and never throws for.
    /// </remarks>
    internal static RunnerOutOfScopeException NavAppExtraShapeGap(string detail)
        => VirtualTableShapeGap("NAV App Extra (virtual table 2000000157)", "nav-app-extra-virtual-table", detail);

    internal const int NavAppExtraVirtualTableId = 2000000157;

    // Per (store, runtime package id), NOT one-shot per store — and that distinction is the
    // whole correctness argument for this ledger.
    //
    // BcRuntime.RegisteredModules() is a LIVE view of registration state, not a fixed list:
    // the Published Application seeder itself splits into a dependency pass and a later
    // bundle pass for exactly that reason (EnsurePublishedApplicationDependencyRowsSeeded /
    // EnsurePublishedApplicationBundleRowSeeded). A one-shot latch here would therefore be a
    // race with a silent wrong answer on the losing side: the first AL read of 2000000157
    // that happens before the bundle registers would populate the table WITHOUT the bundle's
    // row, latch, and every later read — including the bundle asking about itself — would get
    // false from a table that looks populated. Two writers of the same state, one of them
    // holding an invariant the other does not.
    //
    // Topping up per app id makes repeated handouts idempotent and late registrations visible,
    // at the cost of one dictionary lookup per app per handout.
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<Guid, byte>> _naeSeededByStore = new();

    // Separate, and only for BC's own provider: its rows are pre-built buffers whose key this
    // file does not read, so they cannot go in the ledger above. Once BC's provider has
    // answered for a store there is nothing to top up — its row set is the session's app
    // metadata, which the runner does not add to mid-run.
    private static readonly ConditionalWeakTable<object, object> _naeBcProviderAnswered = new();

    private static bool _naeReflectionReady;
    private static Type? _naeProviderType;          // Microsoft.Dynamics.Nav.Runtime.NavAppExtraDataProvider
    private static ConstructorInfo? _naeProviderCtor;   // .ctor(NavSession)
    private static MethodInfo? _naeGetAllItems;     // protected IEnumerable<ReadOnlyRecordBuffer> GetAllItems(out bool)

    private static bool IsNavAppExtraVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == NavAppExtraVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind NAV App Extra (2000000157). BC's own
    /// NavAppExtraDataProvider is tried first and its rows are used whenever it produces any;
    /// otherwise the rows are built from the same loaded-module list and the same
    /// <see cref="AppPackageIdentity"/> values the Published Application seeder uses, so the
    /// two tables can never disagree about which app a runtime package id names.
    ///
    /// <para>Never answers with an empty table: an empty 2000000157 makes both
    /// <c>Published Application</c> Lookup FlowFields read false for every app, which is a
    /// wrong answer rather than a missing one (#3072).</para>
    ///
    /// <para>Idempotent and topping-up, called on every 2000000157 data-access handout, so an
    /// app registered after the first read still gets its row — see the ledger's own comment
    /// for why a one-shot latch here would be a race with a silent wrong answer.</para>
    /// </summary>
    private static void PopulateNavAppExtraVirtualTable(object dataAccess, NCLMetaTable metaTable, object session)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);   // NavBoolean.Create(bool)
        EnsureDataAccessProviderReflection(dataAccess);

        var store = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw NavAppExtraShapeGap("data access has no in-memory provider");

        // 1. BC's own provider, once per store. When the session carries real app metadata
        //    this is the whole answer and nothing below runs.
        var seeded = _naeSeededByStore.GetValue(store, static _ => new ConcurrentDictionary<Guid, byte>());
        var inserted = 0;

        if (!_naeBcProviderAnswered.TryGetValue(store, out _))
        {
            inserted = TryPopulateNavAppExtraFromBcProvider(store, session);
            if (inserted > 0) _naeBcProviderAnswered.Add(store, new object());
        }

        // 2. The runner's own app list, topped up on every handout. Reached when BC's
        //    retriever has nothing in it, which is the normal state here: the runner
        //    publishes and installs nothing, so NavAppMetadataRetriever is empty and
        //    NavAppGroup is BaseGroup. Measured on BC 28.1: BC's provider returns 0 rows and
        //    this is the path that answers.
        if (!_naeBcProviderAnswered.TryGetValue(store, out _))
            inserted += PopulateNavAppExtraFromLoadedModules(store, metaTable, seeded);

        // Only the FIRST handout may refuse. A later one legitimately inserts nothing — every
        // registered app already has its row — and raising there would turn the idempotent
        // top-up into a failure on the second read of a correctly populated table.
        if (seeded.IsEmpty && inserted == 0)
            throw NavAppExtraShapeGap(
                "neither BC's own NavAppExtraDataProvider nor the runner's loaded-module list "
                + "produced a single row. Answering with an empty table would make "
                + "Published Application's \"Tenant Visible\" and \"PerTenant Or Installed\" "
                + "read false for every app — the runner reporting that no app is visible to "
                + "this tenant, which is a wrong answer and not a missing one. See AlRunner#3072");
    }

    /// <summary>
    /// Take the rows BC's own NavAppExtraDataProvider produces, when it produces any. Returns
    /// the number inserted; 0 means the provider is not usable on this session (its metadata
    /// retriever is empty, or it threw), which is the expected case in the runner and is
    /// handled by the caller rather than raised here.
    /// </summary>
    private static int TryPopulateNavAppExtraFromBcProvider(object store, object session)
    {
        if (!TryEnsureNavAppExtraReflection()) return 0;

        object provider;
        try
        {
            provider = _naeProviderCtor!.Invoke(new object?[] { session });
        }
        catch
        {
            // The provider reads the session's tenant and app group in its base constructor.
            // On the skeleton session that can throw; it is not a shape gap, it is the case
            // the loaded-module fallback exists for.
            return 0;
        }

        object? rows;
        try
        {
            rows = _naeGetAllItems!.Invoke(provider, new object?[] { false });
        }
        catch
        {
            return 0;
        }

        if (rows is not System.Collections.IEnumerable enumerable) return 0;

        var inserted = 0;
        try
        {
            foreach (var buffer in enumerable)
            {
                if (buffer == null) continue;
                InsertPreBuiltVirtualRow(store, buffer);
                inserted++;
            }
        }
        catch
        {
            // Enumeration is lazy in BC's provider, so a throw can arrive here rather than
            // above. Rows already inserted stay: they are BC's own and are not wrong. The
            // caller tops the table up from the loaded-module list only when NOTHING arrived,
            // so a partial BC answer is never mixed with a synthesised one.
            return inserted;
        }

        return inserted;
    }

    /// <summary>
    /// Build one row per app the runner loaded, from the same <c>BcRuntime.RegisteredModules()</c>
    /// list and the same <see cref="AppPackageIdentity"/> values that produce this app's
    /// Published Application (2000000206) and Installed Application (2000000212) rows — so a
    /// runtime package id names the same app in all three tables.
    /// </summary>
    private static int PopulateNavAppExtraFromLoadedModules(
        object store, NCLMetaTable metaTable, ConcurrentDictionary<Guid, byte> seeded)
    {
        var modules = AlRunner.BcRuntime.RegisteredModules();
        var inserted = 0;

        foreach (var m in modules)
        {
            var runtimePackageId = AppPackageIdentity.RuntimePackageIdFor(m.AppId);
            // The ledger does double duty. Within one call it absorbs an app id listed more
            // than once (an app split across R2R chunks — #3067). Across calls it is what
            // makes the top-up idempotent, so a second handout inserts nothing rather than
            // colliding on the primary key. Either way the row COUNT stays equal to the number
            // of distinct apps, which is what makes "one row per published app" a checkable
            // property rather than a coincidence.
            if (!seeded.TryAdd(runtimePackageId, 0)) continue;

            var packageId = AppPackageIdentity.PackageIdFor(m.AppId);
            InsertVirtualRow(store, metaTable,
                new object[] { NavAppExtraVirtualTableId, seeded.Count, 0, 0 },
                field => BuildNavAppExtraValue(field, runtimePackageId, packageId));
            inserted++;
        }

        return inserted;
    }

    /// <summary>
    /// One column of a NAV App Extra row, matched by the metatable's own FIELD NAME so the
    /// mapping tracks what the System package in the resolved artifact declares rather than a
    /// hardcoded field-number table.
    ///
    /// <para>The two Boolean columns are the ones Published Application's FlowFields read.
    /// Both answer <c>true</c>, and the header states what that rests on: every app in this
    /// list was loaded into this session's single tenant, so BC's own Global branch of
    /// <c>IsAppVisibleToTenant</c> is the one that applies, and every one of them already has
    /// an Installed Application row that makes <c>Published Application.Installed</c> read
    /// true. A <c>false</c> here would have the runner contradicting its own 2000000212 rows.</para>
    /// </summary>
    private static object? BuildNavAppExtraValue(NCLMetaField field, Guid runtimePackageId, Guid packageId)
    {
        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            // new NavGuid(Guid) is exactly what BC's own NavAppExtraDataProvider writes into
            // these two slots — buffer[0] and buffer[3] of its GetAllItems.
            case "runtimepackageid":
                return new NavGuid(runtimePackageId);
            case "packageid":
                return new NavGuid(packageId);
            case "tenantvisible":
                return NavBoolean(true);
            case "pertenantorinstalled":
                return NavBoolean(true);
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    /// <summary>
    /// Resolve BC's own NavAppExtraDataProvider. Unlike the Feature Key populator this does
    /// NOT throw when the type is absent or the wrong shape: BC's provider is an optimisation
    /// here rather than the only source, and the loaded-module fallback answers the same four
    /// columns. Returning false routes to it instead of failing a run over a type the runner
    /// does not strictly need.
    /// </summary>
    private static bool TryEnsureNavAppExtraReflection()
    {
        if (_naeReflectionReady) return _naeProviderCtor != null && _naeGetAllItems != null;
        _naeReflectionReady = true;

        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        _naeProviderType = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavAppExtraDataProvider");

        _naeProviderCtor = _naeProviderType?.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                                 && c.GetParameters()[0].ParameterType.Name == "NavSession");

        // BcShape.FindMethod, not Type.GetMethod (#3069). A bare-name lookup throws
        // AmbiguousMatchException the day Microsoft ships a second GetAllItems overload on
        // this provider, and MethodScopePatches.NavMethodScope_AssertError rethrows only
        // BcShapeGapException while absorbing everything else — so that throw would be
        // SWALLOWED under an AL `asserterror`, passing it on a call real BC performs fine.
        //
        // The signature is pinned to the one BC's own provider declares —
        // `protected override IEnumerable<ReadOnlyRecordBuffer> GetAllItems(out bool)`, so
        // one `bool&` parameter — which makes an ADDED overload refuse by name rather than
        // being picked between. FindMethod still answers null on absence, and null is the
        // route this file already handles: TryEnsureNavAppExtraReflection returns false and
        // the loaded-module fallback answers, which is what happens on every BC build
        // measured so far anyway (BC's provider returns 0 rows here).
        _naeGetAllItems = _naeProviderType == null ? null : BcShape.FindMethod(
            _naeProviderType, "GetAllItems",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            "NAV App Extra (virtual table 2000000157)",
            "NavAppExtraDataProvider.GetAllItems",
            "it is the row set BC's own provider computes for this table, so the runner "
            + "cannot pick an overload on the table's behalf",
            new[] { typeof(bool).MakeByRefType() });

        return _naeProviderCtor != null && _naeGetAllItems != null;
    }
}
