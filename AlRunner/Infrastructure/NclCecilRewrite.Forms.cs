// Part of NclCecilRewrite (see NclCecilRewrite.cs for the driver + shared helpers).
// Split out per #2631 so a new rewrite in this area does not have to edit the other
// area files or the driver. Behavior-preserving move only — see #2631.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;


namespace AlRunner.Infrastructure;

public static partial class NclCecilRewrite
{
    private static void RewriteNcl_Forms(AssemblyDefinition asm)
    {

        // NavForm.GetMasterPage → return null/default (R2R-trapped; Cecil-rewrite is the only path)
        var navFormType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavForm");
        if (navFormType == null)
            throw new InvalidOperationException("NavForm type not found in Ncl.dll — Ncl shape changed; do not commit");

        // GetMasterPage is GUARDED, not replaced. Its real body asks
        // NavGlobal.MetadataProvider for the page's MasterPage, which is the page's parsed
        // control tree — the thing a TestPage needs and the thing a flat "return default"
        // makes permanently unavailable. Callers that were happy with the default (the
        // report request-page chain) still get it; only forms the runner opted in run the
        // real lookup. Same rationale as the form-init trio below; see RunnerFormInit.cs.
        var shouldRunFormInitRef = asm.MainModule.ImportReference(
            typeof(AlRunner.Patches.RunnerFormInit).GetMethod(
                nameof(AlRunner.Patches.RunnerFormInit.ShouldRunRealFormInit),
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "RunnerFormInit.ShouldRunRealFormInit not found — do not commit"));

        var shouldResolveMasterPageRef = asm.MainModule.ImportReference(
            typeof(AlRunner.Patches.RunnerFormInit).GetMethod(
                nameof(AlRunner.Patches.RunnerFormInit.ShouldResolveMasterPage),
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "RunnerFormInit.ShouldResolveMasterPage not found — do not commit"));

        int getMasterPageRewroteCount = 0;
        foreach (var method in navFormType.Methods.Where(mm => mm.Name == "GetMasterPage").ToList())
        {
            var returnType = method.ReturnType;
            if (returnType.FullName.StartsWith("System.Threading.Tasks.Task`"))
                throw new InvalidOperationException($"GetMasterPage returns Task<T> ({returnType.FullName}) — cannot safely emit default; do not commit");

            // Guarded by ShouldResolveMasterPage, which is WIDER than the form-init opt-in:
            // it also covers a page BC opens on AL's own behalf (SomePage.RunModal()), whose
            // instance the runner never sees. See RunnerFormInit.
            GuardWithDefaultReturn(asm.MainModule, method, shouldResolveMasterPageRef);
            getMasterPageRewroteCount++;
        }
        if (getMasterPageRewroteCount == 0)
            throw new InvalidOperationException("GetMasterPage method not found in NavForm — Ncl shape changed; do not commit");
        Console.Error.WriteLine($"[Cecil] Guarded {getMasterPageRewroteCount} GetMasterPage overload(s) → real lookup only for runner-opted-in forms");

        // MetadataProvider.GetRelativeHelpUrl(pageId) → "".
        //
        // Reached from GetMasterPage -> GetMasterPageUnsolved -> GetMergedMasterPage on
        // EVERY masterpage merge, so it sits directly in front of the page path this work
        // opens up. Its body opens system table 2000000198 (page documentation) via
        // `new NavRecord(session, 2000000198, …)`, which NREs here because the runner has
        // no metadata for that table.
        //
        // Returning the empty string is not a silent fake: it is what BC itself produces
        // for a tenant with no page-documentation rows, and the runner genuinely has none
        // (no help server, no tenant help configuration — see docs/scope.md). The value is
        // a UI help link with no AL-observable surface, so nothing an AL test can assert
        // changes. If table 2000000198 ever gets real metadata here, delete this and let
        // BC's own body run — it will then answer the same way for the same reason.
        var metadataProviderType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.XmlMetadata.MetadataProvider")
            ?? throw new InvalidOperationException("MetadataProvider type not found in Ncl.dll — Ncl shape changed; do not commit");
        int helpUrlRewroteCount = 0;
        foreach (var method in metadataProviderType.Methods.Where(mm => mm.Name == "GetRelativeHelpUrl" && mm.HasBody).ToList())
        {
            if (method.ReturnType.FullName != "System.String")
                throw new InvalidOperationException(
                    $"GetRelativeHelpUrl returns {method.ReturnType.FullName}, expected System.String — do not commit");
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldstr, string.Empty));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            helpUrlRewroteCount++;
        }
        if (helpUrlRewroteCount == 0)
            throw new InvalidOperationException("MetadataProvider.GetRelativeHelpUrl not found — Ncl shape changed; do not commit");
        Console.Error.WriteLine($"[Cecil] Rewrote {helpUrlRewroteCount} GetRelativeHelpUrl overload(s) → \"\" (no tenant help data in the runner)");

        // MetadataProvider.VersionNumber(NavTenant) → 0.
        //
        // The last step of GetMasterPageUnsolved: stamp the merged MasterPage with the
        // tenant's metadata version, which the client uses to decide whether its CACHED
        // page metadata is stale. It NREs on the skeleton tenant, and it is the only thing
        // standing between the page path and a fully merged MasterPage.
        //
        // The runner has one process, no client-side metadata cache and no tenant metadata
        // versioning, so there is nothing for the stamp to invalidate and no AL surface
        // that can read it. 0 is the "no version recorded" value BC itself carries before a
        // tenant has been versioned.
        int versionNumberRewroteCount = 0;
        foreach (var method in metadataProviderType.Methods.Where(mm => mm.Name == "VersionNumber" && mm.HasBody).ToList())
        {
            if (method.ReturnType.FullName != "System.Int64")
                throw new InvalidOperationException(
                    $"VersionNumber returns {method.ReturnType.FullName}, expected System.Int64 — do not commit");
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Conv_I8));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            versionNumberRewroteCount++;
        }
        if (versionNumberRewroteCount == 0)
            throw new InvalidOperationException("MetadataProvider.VersionNumber not found — Ncl shape changed; do not commit");
        Console.Error.WriteLine($"[Cecil] Rewrote {versionNumberRewroteCount} VersionNumber overload(s) → 0 (no tenant metadata versioning in the runner)");

        // MetadataProvider.EffectiveVersionNumber(NavSession) → 0. Same rewrite, same
        // justification as VersionNumber above — but reached through a different door.
        //
        // A 28.1 SERVICE UPDATE (present in 28.1.49838.53249, absent in .50794) added a
        // scoped metadata cache and rerouted the metadata-version reads onto this new
        // static: the master-page merge (GetMasterPageUnsolved, MergePageAndTable, the
        // NavTestPage paths) and metaQuery.MetadataToken all call it now, and only the
        // legacy tenant-scoped VersionNumber(NavTenant) still uses the method we rewrite
        // above. So on the newer build our patch guarded a door nobody walks through and
        // the merge threw again — the AL page object was never registered, which is what
        // surfaced as "no AL page object was built for this page".
        //
        // The body dereferences session.Tenant.MetadataVersionTracker and
        // ServerUserSettings.Instance.EnableScopedMetadataCache; the skeleton session has
        // neither. 0 is the same "no version recorded" value for the same reason: one
        // process, no client metadata cache, no scoped-cache snapshots to invalidate, and
        // no AL surface that can read the stamp.
        //
        // NOT a hard error when missing: the method genuinely does not exist on 28.1 builds
        // before the update, and the runner must still work against those. VersionNumber
        // above stays mandatory — it is present on every build we support.
        int effectiveVersionRewroteCount = 0;
        foreach (var method in metadataProviderType.Methods
                     .Where(mm => mm.Name == "EffectiveVersionNumber" && mm.HasBody).ToList())
        {
            if (method.ReturnType.FullName != "System.Int64")
                throw new InvalidOperationException(
                    $"EffectiveVersionNumber returns {method.ReturnType.FullName}, expected System.Int64 — do not commit");
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Conv_I8));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            effectiveVersionRewroteCount++;
        }
        Console.Error.WriteLine(effectiveVersionRewroteCount > 0
            ? $"[Cecil] Rewrote {effectiveVersionRewroteCount} EffectiveVersionNumber overload(s) → 0 (no scoped metadata cache in the runner)"
            : "[Cecil] MetadataProvider.EffectiveVersionNumber absent — pre-scoped-metadata-cache BC build");

        // NavPageDataPersonalizationHelper.LoadPageDataPersonalization<T>(...) → default(T).
        //
        // Reached from MergePageAndTable -> SolveDefaultFilterColumnProperty. It opens the
        // per-user page-personalization system table, which the runner has no metadata for,
        // so `new NavRecord(...)` NREs.
        //
        // User personalization is out of scope by construction: the runner has no user
        // profile, no personalization store and no UI to produce one. default(T) is exactly
        // what BC returns for a user who has never personalized the page, which is every
        // user here — so the merged page keeps its AL-declared layout, which is what an AL
        // test is asserting about in the first place.
        var personalizationHelperType = asm.MainModule.GetType(
            "Microsoft.Dynamics.Nav.Runtime.NavPageDataPersonalizationHelper");
        if (personalizationHelperType != null)
        {
            int personalizationRewroteCount = 0;
            foreach (var method in personalizationHelperType.Methods
                .Where(mm => mm.Name == "LoadPageDataPersonalization" && mm.HasBody).ToList())
            {
                var body = method.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                var retType = method.ReturnType;
                if (retType.FullName == "System.Void")
                    il.Append(il.Create(OpCodes.Ret));
                else if (!retType.IsValueType && !retType.IsGenericParameter)
                {
                    il.Append(il.Create(OpCodes.Ldnull));
                    il.Append(il.Create(OpCodes.Ret));
                }
                else
                {
                    // Generic parameter or value type — default(T) via initobj.
                    var local = new VariableDefinition(retType);
                    body.Variables.Add(local);
                    body.InitLocals = true;
                    il.Append(il.Create(OpCodes.Ldloca_S, local));
                    il.Append(il.Create(OpCodes.Initobj, retType));
                    il.Append(il.Create(OpCodes.Ldloc_S, local));
                    il.Append(il.Create(OpCodes.Ret));
                }
                body.MaxStackSize = 1;
                personalizationRewroteCount++;
            }
            if (personalizationRewroteCount == 0)
                throw new InvalidOperationException(
                    "NavPageDataPersonalizationHelper.LoadPageDataPersonalization not found — Ncl shape changed; do not commit");
            Console.Error.WriteLine(
                $"[Cecil] Rewrote {personalizationRewroteCount} LoadPageDataPersonalization overload(s) → default (no user personalization in the runner)");
        }

        // NavForm.RequiresExecutePermissionCheck(MasterPage) → return false
        // GetMasterPage() now returns null/default, so its callers pass null into this method,
        // which then NREs when it dereferences the parameter. Since this is a permission-guard
        // inside InitializeFromMetadata, returning false (= no extra permission check needed)
        // is the safe stub behaviour for the runner environment (R2R-trapped; Cecil is only path).
        int requiresExecPermCheckRewroteCount = 0;
        foreach (var method in navFormType.Methods
            .Where(mm => mm.Name == "RequiresExecutePermissionCheck").ToList())
        {
            Console.Error.WriteLine($"[Cecil] Rewriting {method.FullName} → return false");
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            requiresExecPermCheckRewroteCount++;
        }
        if (requiresExecPermCheckRewroteCount == 0)
            throw new InvalidOperationException("RequiresExecutePermissionCheck method not found in NavForm — Ncl shape changed; do not commit");
        Console.Error.WriteLine($"[Cecil] Rewrote {requiresExecPermCheckRewroteCount} RequiresExecutePermissionCheck overload(s) → return false");

        // NavForm.InitializeFromMetadata() → prepend null-guard on this.masterPage field.
        // The method reads this.masterPage at ~15 separate IL sites. When masterPage is null
        // (because no BC metadata is available in the runner), each site NREs in a cascade.
        // Adding an early-return when masterPage==null lets the form proceed without
        // metadata-dependent initialisation. Passing tests that have a non-null masterPage
        // field are unaffected — they fall through to the original code normally.
        var initFromMetadataMethod = navFormType.Methods
            .FirstOrDefault(mm => mm.Name == "InitializeFromMetadata" && mm.Parameters.Count == 0)
            ?? throw new InvalidOperationException("InitializeFromMetadata() not found in NavForm — Ncl shape changed; do not commit");
        var masterPageField = navFormType.Fields
            .FirstOrDefault(f => f.Name == "masterPage")
            ?? throw new InvalidOperationException("masterPage field not found in NavForm — Ncl shape changed; do not commit");
        {
            var body = initFromMetadataMethod.Body;
            var il = body.GetILProcessor();
            var firstOriginalInstr = body.Instructions[0];
            // Prepend: if (this.masterPage == null) return;
            var ldarg0 = il.Create(OpCodes.Ldarg_0);
            var ldfld  = il.Create(OpCodes.Ldfld, asm.MainModule.ImportReference(masterPageField));
            var brtrue = il.Create(OpCodes.Brtrue_S, firstOriginalInstr);
            var ret    = il.Create(OpCodes.Ret);
            il.InsertBefore(firstOriginalInstr, ldarg0);
            il.InsertBefore(firstOriginalInstr, ldfld);
            il.InsertBefore(firstOriginalInstr, brtrue);
            il.InsertBefore(firstOriginalInstr, ret);
        }
        Console.Error.WriteLine("[Cecil] Prepended masterPage null-guard to NavForm.InitializeFromMetadata → early return when masterPage is null");

        // NavForm.get_PageExtensions → lazily init the backing field to an empty list.
        // The `pageExtensions` field is only populated by the full page-extension load
        // path (not run on the runner's report request page). Many methods
        // (CallOnMetadataLoadedExtensionMethod, OnAfterGetRecordAsync, etc.) do
        // `PageExtensions.Count`/`.ForEach` assuming non-null and NRE on the skeleton.
        // An empty List<NavFormExtension> is the faithful "no page extensions" state and
        // fixes every call site at once.
        {
            var getter = navFormType.Methods
                .FirstOrDefault(mm => mm.Name == "get_PageExtensions" && mm.Parameters.Count == 0)
                ?? throw new InvalidOperationException("get_PageExtensions not found in NavForm — Ncl shape changed; do not commit");
            var pageExtField = navFormType.Fields
                .FirstOrDefault(f => f.Name == "pageExtensions")
                ?? throw new InvalidOperationException("pageExtensions field not found in NavForm — Ncl shape changed; do not commit");
            var listCtor = asm.MainModule.ImportReference(
                new Mono.Cecil.MethodReference(".ctor", asm.MainModule.TypeSystem.Void, pageExtField.FieldType)
                {
                    HasThis = true,
                });
            var fieldRef = asm.MainModule.ImportReference(pageExtField);
            var body = getter.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            var loadAndRet = il.Create(OpCodes.Ldarg_0);
            // if (this.pageExtensions == null) this.pageExtensions = new List<NavFormExtension>();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, fieldRef));
            il.Append(il.Create(OpCodes.Brtrue_S, loadAndRet));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Newobj, listCtor));
            il.Append(il.Create(OpCodes.Stfld, fieldRef));
            // return this.pageExtensions;
            il.Append(loadAndRet);
            il.Append(il.Create(OpCodes.Ldfld, fieldRef));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 2;
            Console.Error.WriteLine("[Cecil] Rewrote NavForm.get_PageExtensions → lazily init empty list (no page extensions on skeleton)");
        }

        // NavForm.GetAutoFormatStringAsync → return default/empty (R2R-trapped; cluster #2 in CORPUS-CLASSIFICATION-2026-05-19-FINAL.md)
        int getAutoFormatRewroteCount = 0;
        foreach (var method in navFormType.Methods.Where(mm => mm.Name == "GetAutoFormatStringAsync").ToList())
        {
            var returnType = method.ReturnType;
            Console.Error.WriteLine($"[Cecil] Rewriting {method.FullName} (ReturnType={returnType.FullName})");

            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();

            if (returnType.FullName.StartsWith("System.Threading.Tasks.ValueTask`1<"))
            {
                // ValueTask.FromResult<string>("") — returns completed ValueTask<string> with Result=""
                var fromResultGenericDef = typeof(System.Threading.Tasks.ValueTask)
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "FromResult" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    ?? throw new InvalidOperationException("ValueTask.FromResult<T> not found via reflection");
                var fromResultRef = asm.MainModule.ImportReference(
                    fromResultGenericDef.MakeGenericMethod(typeof(string)));
                il.Append(il.Create(OpCodes.Ldstr, ""));
                il.Append(il.Create(OpCodes.Call, fromResultRef));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
            }
            else if (returnType.FullName.StartsWith("System.Threading.Tasks.Task`1<"))
            {
                // Task.FromResult<string>("") — returns completed Task<string> with Result=""
                var fromResultMethodInfo = typeof(System.Threading.Tasks.Task)
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "FromResult" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    ?? throw new InvalidOperationException("Task.FromResult<T> not found via reflection");
                var fromResultRef = asm.MainModule.ImportReference(
                    fromResultMethodInfo.MakeGenericMethod(typeof(string)));
                il.Append(il.Create(OpCodes.Ldstr, ""));
                il.Append(il.Create(OpCodes.Call, fromResultRef));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
            }
            else
            {
                throw new InvalidOperationException(
                    $"GetAutoFormatStringAsync has unexpected return type: {returnType.FullName} — log and STOP; do not commit");
            }
            getAutoFormatRewroteCount++;
        }
        if (getAutoFormatRewroteCount == 0)
            throw new InvalidOperationException("GetAutoFormatStringAsync method not found in NavForm — Ncl shape changed; do not commit");
        Console.Error.WriteLine($"[Cecil] Rewrote {getAutoFormatRewroteCount} GetAutoFormatStringAsync overload(s) → return default ValueTask");

        // NavTestPageBase.get_ServerForm() → return RuntimeHelpers.GetUninitializedObject(NavForm) when serverform is null.
        //
        // Root cause of the 115-test GetAutoFormatStringAsync cluster: the existing rewrite makes the
        // BODY of NavForm.GetAutoFormatStringAsync safe, but NavTestPageBase.get_ServerForm() still
        // returns null in the runner (Session.Company.GetRegisteredForm returns null — no BC service
        // tier). BC-generated code then does null.GetAutoFormatStringAsync(…) via callvirt, and the
        // CLR NREs at the dispatch site BEFORE the rewritten body executes.
        //
        // Fix: rewrite get_ServerForm() so that when serverform==null it creates an uninitialised
        // NavForm via RuntimeHelpers.GetUninitializedObject and caches it in the field. The
        // uninitialised object has valid type metadata (vtable dispatch works) and the rewritten
        // GetAutoFormatStringAsync body never dereferences `this`, so the call is safe.
        var navTestPageBaseType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestPageBase")
            ?? throw new InvalidOperationException("NavTestPageBase type not found in Ncl.dll — Ncl shape changed; do not commit");
        var serverformField = navTestPageBaseType.Fields
            .FirstOrDefault(f => f.Name == "serverform")
            ?? throw new InvalidOperationException("serverform field not found on NavTestPageBase — Ncl shape changed; do not commit");
        var getServerFormMethod = navTestPageBaseType.Methods
            .FirstOrDefault(m => m.Name == "get_ServerForm" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("get_ServerForm() not found on NavTestPageBase — Ncl shape changed; do not commit");

        var getTypeFromHandleMethodInfo = typeof(Type)
            .GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) })
            ?? throw new InvalidOperationException("Type.GetTypeFromHandle not found via reflection");
        // Resolve or create — issue #2514: an unconditional GetUninitializedObject(NavForm)
        // has a null ITreeObject.Tree, which is faithful for GetAutoFormatStringAsync (whose
        // rewritten body never dereferences `this`) but throws "Parent.Tree cannot be null"
        // the moment PageBackgroundTask/EnqueueBackgroundTask try to root a child scope under
        // it. RunnerServerFormRegistry.ResolveOrCreateUninitialized hands back the TestPage's
        // real live NavForm (real Tree) when TestPageFactory built one, and only falls back
        // to the tree-less stub when it didn't — see that class's header comment.
        var resolveOrCreateMethodInfo = typeof(AlRunner.Patches.RunnerServerFormRegistry)
            .GetMethod(nameof(AlRunner.Patches.RunnerServerFormRegistry.ResolveOrCreateUninitialized),
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("RunnerServerFormRegistry.ResolveOrCreateUninitialized not found — do not commit");

        var getTypeFromHandleRef = asm.MainModule.ImportReference(getTypeFromHandleMethodInfo);
        var resolveOrCreateRef    = asm.MainModule.ImportReference(resolveOrCreateMethodInfo);
        var navFormTypeRef         = asm.MainModule.ImportReference(navFormType);
        var serverformFieldRef     = asm.MainModule.ImportReference(serverformField);

        {
            var body = getServerFormMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();

            //   ldarg.0
            //   ldfld serverform
            //   dup
            //   brtrue.s RETURN          ; already set — return it
            //   pop
            //   ldarg.0                  ; this (for stfld)
            //   ldarg.0                  ; testPageBase (for ResolveOrCreateUninitialized)
            //   ldtoken NavForm
            //   call Type.GetTypeFromHandle
            //   call RunnerServerFormRegistry.ResolveOrCreateUninitialized(object, Type)
            //   castclass NavForm
            //   stfld serverform
            //   ldarg.0
            //   ldfld serverform
            // RETURN:
            //   ret

            var retInstr   = il.Create(OpCodes.Ret);

            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld,  serverformFieldRef));
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Brtrue_S, retInstr));   // non-null → return it
            il.Append(il.Create(OpCodes.Pop));
            il.Append(il.Create(OpCodes.Ldarg_0));              // this (for stfld)
            il.Append(il.Create(OpCodes.Ldarg_0));              // testPageBase arg
            il.Append(il.Create(OpCodes.Ldtoken,  navFormTypeRef));
            il.Append(il.Create(OpCodes.Call,     getTypeFromHandleRef));
            il.Append(il.Create(OpCodes.Call,     resolveOrCreateRef));
            il.Append(il.Create(OpCodes.Castclass, navFormTypeRef));
            il.Append(il.Create(OpCodes.Stfld,   serverformFieldRef));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld,   serverformFieldRef));
            il.Append(retInstr);

            body.MaxStackSize = 3;
        }
        Console.Error.WriteLine("[Cecil] Rewrote NavTestPageBase.get_ServerForm → RunnerServerFormRegistry.ResolveOrCreateUninitialized (issue #2514)");

        // NavTenant.CanCreateSession(bool) → return true unconditionally.
        //
        // Issue #2514: a page background task run synchronously (the shape BC's own test
        // framework always takes — PageBackgroundTask.CanPageBackgroundTaskRunAsync is false
        // without a real service-tier scheduler) opens a REAL child NavSession
        // (NavChildSessionTaskRuntime<T>.RunAsync -> childSession.Open()), unlike the runner's
        // root session, which is built via GetUninitializedObject and never goes through
        // NavSession.Open() at all. NavSession.Open() -> CheckPreconditions ->
        // NavTenant.CanCreateSession(), whose real body (RefreshState -> Monitor.TryEnter on a
        // `stateLock` object, then FailedToMount/IsTenantDismounting/State checks against a live
        // NavDatabase) exists to answer "is the SQL-backed tenant database currently mounted and
        // operational" — a question that presupposes a service tier the runner does not have.
        // Every one of those preconditions is unconditionally true in the runner's single
        // always-mounted, never-dismounting, never-syncing skeleton tenant (FailedToMount is
        // never set, IsTenantDismounting reflects an always-open ManualResetGate, State never
        // reaches Mounting/NonOperational) — "return true" is what CanCreateSession's own real
        // body would answer for that tenant if it could reach the check at all; the rewrite
        // just skips the SQL-shaped machinery that gets it there.
        var navTenantTypeForSession = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavTenant")
            ?? throw new InvalidOperationException("NavTenant type not found in Ncl.dll — Ncl shape changed; do not commit");
        var canCreateSessionMethod = navTenantTypeForSession.Methods
            .FirstOrDefault(m => m.Name == "CanCreateSession" && m.Parameters.Count == 1 && m.HasBody)
            ?? throw new InvalidOperationException(
                "NavTenant.CanCreateSession(bool) not found — Ncl shape changed; do not commit");
        {
            var body = canCreateSessionMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
        }
        Console.Error.WriteLine("[Cecil] Rewrote NavTenant.CanCreateSession(bool) → return true (issue #2514)");

        // Page background task SYNCHRONOUS execution → run INLINE against the current session.
        //
        // Issue #2514: both CurrPage.EnqueueBackgroundTask (from an AL trigger) and
        // TestPage.RunPageBackgroundTask run their child task synchronously in BC's own test
        // framework (PageBackgroundTask.CanPageBackgroundTaskRunAsync is false without a real
        // scheduler — always true here). BC's own dispatch bodies
        // (NavForm.EnqueueBackgroundTask(NavSession,...) and
        // NavTestPage.ALRunPageBackgroundTask(PageBackgroundTask,bool)) both funnel through
        // `new NavChildSessionTaskRuntime<PageBackgroundChildSessionTask>(...).RunAsync(...)
        // .AsTask().GetAwaiter().GetResult()`, which creates a brand-new NavSession purely to
        // isolate the worker codeunit, then really Open()s/OpenCompanyAsync()s it — a full
        // service-tier session/company bootstrap the runner's in-process, no-SQL skeleton
        // cannot faithfully answer, and an isolation guarantee the AL-observable contract does
        // not depend on (AfterRunTaskAsync/AfterRunTaskErrorAsync — which raise
        // OnPageBackgroundTaskCompleted/OnPageBackgroundTaskError — are already invoked by real
        // BC against the PARENT session, not the child one; see RunnerPageBackgroundTaskGap.cs's
        // header for the full decompiled trail and the differential against a real BC 28.4
        // container).
        //
        // Rewrite both dispatch bodies to call RunnerPageBackgroundTaskGap's inline
        // reimplementation of that same synchronous branch instead of
        // NavChildSessionTaskRuntime<T>.RunAsync (an async state-machine method this cannot
        // safely rewrite directly via Cecil):
        //   - NavForm.EnqueueBackgroundTask(NavSession, PageBackgroundTask, ...) — the static
        //     dispatcher CurrPage.EnqueueBackgroundTask funnels through.
        //   - NavTestPage.ALRunPageBackgroundTask(PageBackgroundTask, bool) — the internal
        //     static helper both TestPage.RunPageBackgroundTask() overloads funnel through.
        var enqueueInlineMethodInfo = typeof(AlRunner.Patches.RunnerPageBackgroundTaskGap)
            .GetMethod(nameof(AlRunner.Patches.RunnerPageBackgroundTaskGap.EnqueueBackgroundTaskInline),
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("RunnerPageBackgroundTaskGap.EnqueueBackgroundTaskInline not found — do not commit");
        var runPbtInlineMethodInfo = typeof(AlRunner.Patches.RunnerPageBackgroundTaskGap)
            .GetMethod(nameof(AlRunner.Patches.RunnerPageBackgroundTaskGap.RunPageBackgroundTaskInline),
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("RunnerPageBackgroundTaskGap.RunPageBackgroundTaskInline not found — do not commit");

        var navFormTypeForPbt = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavForm")
            ?? throw new InvalidOperationException("NavForm type not found in Ncl.dll — Ncl shape changed; do not commit");
        var enqueueDispatchMethod = navFormTypeForPbt.Methods
            .FirstOrDefault(m => m.Name == "EnqueueBackgroundTask" && m.Parameters.Count == 3
                              && m.Parameters[0].ParameterType.Name == "NavSession" && m.HasBody)
            ?? throw new InvalidOperationException(
                "NavForm.EnqueueBackgroundTask(NavSession, PageBackgroundTask, IPageBackgroundTaskCompletionTrigger) not found — Ncl shape changed; do not commit");
        {
            var body = enqueueDispatchMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Call, asm.MainModule.ImportReference(enqueueInlineMethodInfo)));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 3;
        }
        Console.Error.WriteLine("[Cecil] Rewrote NavForm.EnqueueBackgroundTask(NavSession,...) → RunnerPageBackgroundTaskGap.EnqueueBackgroundTaskInline (issue #2514)");

        var navTestPageTypeForPbt = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestPage")
            ?? throw new InvalidOperationException("NavTestPage type not found in Ncl.dll — Ncl shape changed; do not commit");
        var runPbtDispatchMethod = navTestPageTypeForPbt.Methods
            .FirstOrDefault(m => m.Name == "ALRunPageBackgroundTask" && m.Parameters.Count == 2
                              && m.Parameters[0].ParameterType.Name == "PageBackgroundTask" && m.HasBody)
            ?? throw new InvalidOperationException(
                "NavTestPage.ALRunPageBackgroundTask(PageBackgroundTask, bool) not found — Ncl shape changed; do not commit");
        {
            var body = runPbtDispatchMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Call, asm.MainModule.ImportReference(runPbtInlineMethodInfo)));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 2;
        }
        Console.Error.WriteLine("[Cecil] Rewrote NavTestPage.ALRunPageBackgroundTask(PageBackgroundTask,bool) → RunnerPageBackgroundTaskGap.RunPageBackgroundTaskInline (issue #2514)");

        // ── NavTestPage / NavTestPageBase vtable-dispatch cluster fix ─────────────────────────
        //
        // Root cause (115-test cluster): NavTestPageHandle_CreateTarget (JmpHook in BcRuntime)
        // used to return a Page{id} : NavForm instead of a NavTestPage.  Storing a NavForm
        // subtype in a NavTestPage-typed field bypasses CLR type-safety; callvirt on the
        // wrong vtable then resolves GetField() → GetAutoFormatStringAsync() because they sit
        // at the same slot index in the two inheritance hierarchies.
        //
        // Fix (C# side): NavTestPageHandle_CreateTarget now returns a real NavTestPage via
        // its internal 3-arg ctor, passing a MockITestPage.  The Cecil side neutralises three
        // additional barriers that would crash in the runner without a live BC service tier:
        //
        //  1. NavTestPageBase.InternalClear sets testPage=null; remove that 3-instruction
        //     sequence so the MockITestPage reference is preserved across ALOpenNew/Edit/View.
        //
        //  2. NavTestPage.Open(ViewMode) calls NavCurrentThread.Session.TestExecution
        //       .ClientSession.CreatePage(...) — no TestExecution exists in the runner.
        //     Rewrite to delegate only to NavTestPageBase.Open (which calls InternalClear).
        //
        //  3. CheckPageOpened throws NavTestPageNotOpenedException when testPage.IsOpened()
        //     returns false.  MockITestPage.IsOpened()=false (so the "already open" guard in
        //     NavTestPageBase.Open passes), but that means CheckPageOpened would throw too.
        //     Rewrite CheckPageOpened to be a no-op: the mock is always usable.
        //
        //  4. GetField / GetAction / GetDataItem / GetPart / GetBuiltInAction / FindBuiltInAction
        //     pass the raw ITest* result through TestClientProxy<T>.Proxy() which tries to
        //     load Microsoft.Dynamics.Nav.Client.TestPageClient — not present in the runner.
        //     Remove the Proxy call from each method; the raw mock interface value works fine.

        var navTestPageType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestPage")
            ?? throw new InvalidOperationException("NavTestPage not found in Ncl.dll");

        // 1. InternalClear — remove `ldarg.0 / ldnull / stfld testPage` (first 3 instructions).
        {
            var method = navTestPageBaseType.Methods
                .FirstOrDefault(m => m.Name == "InternalClear" && m.Parameters.Count == 0)
                ?? throw new InvalidOperationException("InternalClear not found on NavTestPageBase");
            var body = method.Body;
            var il   = body.GetILProcessor();
            // First 3 instructions: ldarg.0, ldnull, stfld testPage
            // Verify before removing to guard against shape changes.
            if (body.Instructions.Count >= 3 &&
                body.Instructions[0].OpCode == OpCodes.Ldarg_0 &&
                body.Instructions[1].OpCode == OpCodes.Ldnull &&
                body.Instructions[2].OpCode == OpCodes.Stfld)
            {
                il.Remove(body.Instructions[2]);
                il.Remove(body.Instructions[1]);
                il.Remove(body.Instructions[0]);
                Console.Error.WriteLine("[Cecil] Removed testPage=null from NavTestPageBase.InternalClear");
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARNING: InternalClear shape unexpected — skipping testPage=null removal");
            }
        }

        // 2. NavTestPage.Open(ViewMode) — replace body with: ldarg.0; ldarg.1; call NavTestPageBase.Open; ret
        {
            var openTestPageViewModeMethod = navTestPageType.Methods
                .FirstOrDefault(m => m.Name == "Open" && m.Parameters.Count == 1)
                ?? throw new InvalidOperationException("NavTestPage.Open(ViewMode) not found");
            var baseOpenMethod = navTestPageBaseType.Methods
                .FirstOrDefault(m => m.Name == "Open" && m.Parameters.Count == 1)
                ?? throw new InvalidOperationException("NavTestPageBase.Open(ViewMode) not found");

            var body = openTestPageViewModeMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Call, asm.MainModule.ImportReference(baseOpenMethod)));
            // ...then tell the attached page it is open, AND which mode it opened in. BC
            // would learn "open" by attaching the page here (from ClientSession.CreatePage);
            // the runner attaches at construction instead, so it has to be recorded
            // explicitly — otherwise NavTestPageBase.Close() never forwards and a New() is
            // never persisted. The ViewMode goes with it because ALOpenNew() is nothing but
            // Open(ViewMode.Create): the blank row it starts is the client's doing, so
            // without the mode an OpenNew() was indistinguishable from an OpenEdit().
            // See RunnerTestPageState.
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Call, asm.MainModule.ImportReference(
                typeof(AlRunner.Patches.RunnerTestPageState).GetMethod(
                    nameof(AlRunner.Patches.RunnerTestPageState.MarkOpened),
                    BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RunnerTestPageState.MarkOpened not found — do not commit"))));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 2;
            Console.Error.WriteLine("[Cecil] Rewrote NavTestPage.Open → NavTestPageBase.Open + MarkOpened  (skip ClientSession.CreatePage)");
        }

        // 3. CheckPageOpened — replace body with `ret` (no-op).
        {
            var method = navTestPageBaseType.Methods
                .FirstOrDefault(m => m.Name == "CheckPageOpened" && m.Parameters.Count == 0)
                ?? throw new InvalidOperationException("CheckPageOpened not found on NavTestPageBase");
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 0;
            Console.Error.WriteLine("[Cecil] Rewrote NavTestPageBase.CheckPageOpened → no-op (ret)");
        }

        // 3b. NavTestExecution.TestHandleModalForm and TestHandleForm — replace the CLIENT
        //     callback with the runner's own dispatch.
        //
        //     BC finds the test's [ModalPageHandler], pushes a delegate that will run it onto
        //     dialogHandlerStack, then asks the client to run the form modally:
        //         ldarg.0
        //         call    NavTestExecution::get_ServiceConnection()
        //         callvirt IService::get_CallbackHandler()
        //         call    TestClientProxy<IClientCallbackHandler>::Proxy(!0)
        //         ldloc   <display class> ; ldfld runRequest
        //         callvirt IClientContract::FormRunModal(FormRunModalRequest)
        //     A real client opens the page and calls back into ShowDialog, which pops that
        //     delegate. Here the call reached BC's HeadlessClientCallback, whose entire job is
        //     to refuse — so no modal page ever reached its handler.
        //
        //     Dropping the three middle instructions leaves `ldarg.0` (the NavTestExecution)
        //     where the callback receiver was, so the stack arriving at the call is exactly
        //     [NavTestExecution][FormRun(Modal)Request] — the signature of the replacement,
        //     which performs the step the client would have caused using BC's own ShowDialog /
        //     ShowForm. Satisfying this one call through a real IService implementation would
        //     mean ~130 members that exist only to throw.
        //
        //     TestHandleForm is the non-modal twin (Page.Run) and carries the identical chain;
        //     it is redirected the same way. Leaving it alone was worse than leaving it
        //     refused — see the comment at that call below and issue #2349.
        {
            var navTestExecutionType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestExecution")
                ?? throw new InvalidOperationException("NavTestExecution type not found in Ncl — do not commit");
            var method = navTestExecutionType.Methods
                .FirstOrDefault(m => m.Name == "TestHandleModalForm" && m.HasBody)
                ?? throw new InvalidOperationException("NavTestExecution.TestHandleModalForm not found — do not commit");

            // Both dispatch methods end in the SAME receiver chain, so redirect them the same
            // way rather than twice by hand — a shape check that only one of them enforced is
            // how TestHandleForm was left behind in the first place (issue #2349).
            void RedirectClientCallback(string methodName, string callName, string replacementName)
            {
                var target = navTestExecutionType.Methods
                    .FirstOrDefault(m => m.Name == methodName && m.HasBody)
                    ?? throw new InvalidOperationException(
                        $"NavTestExecution.{methodName} not found — do not commit");

                var il = target.Body.GetILProcessor();
                var call = target.Body.Instructions.FirstOrDefault(i =>
                    (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call)
                    && i.Operand is MethodReference mr && mr.Name == callName
                    && mr.Parameters.Count == 1)
                    ?? throw new InvalidOperationException(
                        $"{methodName} has no {callName} call — Ncl shape changed; do not commit");

                // Walk back over the receiver chain: Proxy, get_CallbackHandler, get_ServiceConnection.
                var toRemove = new List<Instruction>();
                for (var cursor = call.Previous; cursor != null && toRemove.Count < 8; cursor = cursor.Previous)
                {
                    if (cursor.Operand is not MethodReference m2) continue;
                    if (m2.Name is "Proxy" or "get_CallbackHandler" or "get_ServiceConnection")
                    {
                        toRemove.Add(cursor);
                        if (m2.Name == "get_ServiceConnection") break;
                    }
                }
                if (toRemove.Count != 3)
                    throw new InvalidOperationException(
                        $"{methodName} receiver chain shape changed (matched {toRemove.Count} of 3) — do not commit");

                foreach (var ins in toRemove) il.Remove(ins);

                var replacement = asm.MainModule.ImportReference(
                    typeof(AlRunner.Patches.RunnerModalDispatch).GetMethod(
                        replacementName, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        $"RunnerModalDispatch.{replacementName} not found — do not commit"));
                il.Replace(call, il.Create(OpCodes.Call, replacement));

                Console.Error.WriteLine(
                    $"[Cecil] Rewrote NavTestExecution.{methodName} client callback → "
                    + $"RunnerModalDispatch.{replacementName}");
            }

            RedirectClientCallback("TestHandleModalForm", "FormRunModal",
                nameof(AlRunner.Patches.RunnerModalDispatch.FormRunModal));

            // NavTestExecution.TestHandleForm — the NON-MODAL twin, reached by Page.Run /
            // NavForm.RunAsync. Its receiver chain is identical, and here ServiceConnection is
            // not merely refusing: it is null, because the runner pokes testClientSession
            // without testServiceConnection (only CreateTestClientSession sets the latter, and
            // the poke exists precisely to skip it). So get_CallbackHandler NRE'd in
            // TestHandleForm's own frame, and every [PageHandler] in a non-modal Page.Run was
            // unreachable. RunnerModalDispatch.FormRun performs the step the client would have
            // caused, through BC's own ShowForm.
            RedirectClientCallback("TestHandleForm", "FormRun",
                nameof(AlRunner.Patches.RunnerModalDispatch.FormRun));

            // NavSession.SetServerFormRequestData — called by TestHandleModalForm just BEFORE
            // the dispatch above, and its real body throws NotSupportedException outright when
            // there is no service connection. See RunnerModalDispatch for why setting
            // FormHandle is the whole of what this process needs from it.
            var navSessionType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession")
                ?? throw new InvalidOperationException("NavSession type not found in Ncl — do not commit");
            var setData = navSessionType.Methods
                .FirstOrDefault(m => m.Name == "SetServerFormRequestData" && m.Parameters.Count == 3 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "NavSession.SetServerFormRequestData(3) not found — Ncl shape changed; do not commit");
            var setDataBody = setData.Body;
            setDataBody.Instructions.Clear();
            setDataBody.Variables.Clear();
            setDataBody.ExceptionHandlers.Clear();
            var setDataIl = setDataBody.GetILProcessor();
            setDataIl.Append(setDataIl.Create(OpCodes.Ldarg_0));
            setDataIl.Append(setDataIl.Create(OpCodes.Ldarg_1));
            setDataIl.Append(setDataIl.Create(OpCodes.Ldarg_2));
            setDataIl.Append(setDataIl.Create(OpCodes.Box, setData.Parameters[1].ParameterType));
            setDataIl.Append(setDataIl.Create(OpCodes.Ldarg_3));
            setDataIl.Append(setDataIl.Create(OpCodes.Call, asm.MainModule.ImportReference(
                typeof(AlRunner.Patches.RunnerModalDispatch).GetMethod(
                    nameof(AlRunner.Patches.RunnerModalDispatch.SetServerFormRequestData),
                    BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RunnerModalDispatch.SetServerFormRequestData not found — do not commit"))));
            setDataIl.Append(setDataIl.Create(OpCodes.Ret));
            setDataBody.MaxStackSize = 4;
            Console.Error.WriteLine(
                "[Cecil] Rewrote NavSession.SetServerFormRequestData → RunnerModalDispatch (no service connection here)");

            // Strip TestClientProxy<T>.Proxy(T) from NavTestExecution and its compiler-generated
            // closures, the same way step 4 does for NavTestPageBase.
            //
            // Proxy wraps the value in the TestPageClient's dispatcher, which needs a UI session
            // ("The UISessionManager was expected to be initialized"). The runner hands BC a real
            // in-process ITestPage that needs no marshalling, so removing the call leaves exactly
            // that value on the stack — which is the argument Proxy was given.
            int proxyStripped = 0;
            foreach (var proxyHost in new[] { navTestExecutionType }.Concat(navTestExecutionType.NestedTypes))
            {
                foreach (var m in proxyHost.Methods.Where(mm => mm.HasBody))
                {
                    var mIl = m.Body.GetILProcessor();
                    foreach (var ins in m.Body.Instructions
                        .Where(i => i.Operand is MethodReference pr
                                    && pr.Name == "Proxy"
                                    && pr.DeclaringType.Name.StartsWith("TestClientProxy", StringComparison.Ordinal))
                        .ToList())
                    {
                        mIl.Remove(ins);
                        proxyStripped++;
                    }
                }
            }
            Console.Error.WriteLine(
                $"[Cecil] Stripped {proxyStripped} TestClientProxy.Proxy call(s) from NavTestExecution "
                + "(the runner's ITestPage is in-process; no client dispatcher exists)");
        }

        // 4. Remove TestClientProxy<T>.Proxy(T) call from all NavTestPageBase methods that use it.
        //    Removing the call leaves the raw ITest* value on the stack in the right place.
        {
            var methodsToFix = new[] { "GetField", "GetAction", "GetDataItem", "GetPart", "GetBuiltInAction", "FindBuiltInAction" };
            int removedCount = 0;
            foreach (var methodName in methodsToFix)
            {
                foreach (var method in navTestPageBaseType.Methods
                    .Where(m => m.Name == methodName && m.HasBody))
                {
                    var il = method.Body.GetILProcessor();
                    var proxyCalls = method.Body.Instructions
                        .Where(i => i.OpCode == OpCodes.Call &&
                               i.Operand is MethodReference mr &&
                               mr.Name == "Proxy" &&
                               mr.DeclaringType.Name.StartsWith("TestClientProxy"))
                        .ToList();
                    foreach (var instr in proxyCalls)
                    {
                        il.Remove(instr);
                        removedCount++;
                    }
                }
            }
            Console.Error.WriteLine($"[Cecil] Removed {removedCount} TestClientProxy.Proxy call(s) from NavTestPageBase");
        }

        // 5. NavTestPageBase.LoadMetadata() — replace body with `ldnull; ret`.
        //    LoadMetadata calls NavGlobal.MetadataProvider.GetPageDefinition() which crashes
        //    (MetadataProvider is null in runner).  We return null; NavTestPageBase.ctor stores
        //    the result in metaPage which is then unused via our other rewrites.
        {
            var loadMetadata = navTestPageBaseType.Methods
                .FirstOrDefault(m => m.Name == "LoadMetadata" && m.Parameters.Count == 0)
                ?? throw new InvalidOperationException("LoadMetadata not found on NavTestPageBase");
            var il = loadMetadata.Body.GetILProcessor();
            loadMetadata.Body.Instructions.Clear();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            Console.Error.WriteLine("[Cecil] Rewrote NavTestPageBase.LoadMetadata → return null (skip MetadataProvider)");
        }

        // 7. NavTestPageBase.Close() — prepend a flush of the row started by TestPage.New().
        //    BC's Close() drives the real client's "commit the row you are standing on" step,
        //    but it never calls back into ITestPage.Close(), so LiveNavTestPage never learned
        //    the page was closing and silently dropped the pending row (the AL test then saw
        //    Count = 0 after New() + SetValue + Close()). Prepend rather than replace: the rest
        //    of Close()'s body still runs, so disposal state stays exactly as BC left it.
        {
            var closeMethod = navTestPageBaseType.Methods
                .FirstOrDefault(m => m.Name == "Close" && m.Parameters.Count == 0 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] NavTestPageBase.Close() not found — Ncl shape changed; do not commit");

            var flushMi = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NavTestPageBase_FlushPendingNewRow),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NavTestPageBase_FlushPendingNewRow not found");

            var body = closeMethod.Body;
            var il = body.GetILProcessor();
            var first = body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, asm.MainModule.ImportReference(flushMi)));
            if (body.MaxStackSize < 1) body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Prepended pending-new-row flush to NavTestPageBase.Close");
        }

        // 6. NavTestPageBase.ALGoToRecord(DataError, NavRecord) — delegate to
        //    BcRuntime.NavTestPageBase_ALGoToRecord, which resolves the page's SourceTable
        //    primary key and positions the backing LiveNavTestPage on the matching row.
        //
        //    This replacement already existed, but only as a Hook(...) registration — i.e.
        //    on the JmpHook layer, which is OFF by default (Cecil-only). It was therefore a
        //    silent no-op and BC's own ALGoToRecord body ran instead, NREing on the runner's
        //    skeleton state (14 Pageworks tests; see tests/runner-extras/testpage-gotorecord).
        //    Migrating it here makes the patch actually take effect, and the CecilOwned entry
        //    makes the legacy JmpHook skip it so the two mechanisms can never coexist.
        {
            var goToRecord = navTestPageBaseType.Methods
                .FirstOrDefault(m => m.Name == "ALGoToRecord" && m.Parameters.Count == 2 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] NavTestPageBase.ALGoToRecord(2) not found — Ncl shape changed; do not commit");

            var helperMi = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NavTestPageBase_ALGoToRecord),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NavTestPageBase_ALGoToRecord not found");

            // Shape guard: the helper forwards (this, arg0, arg1) positionally, so a change
            // in either signature must fail loudly at rewrite time rather than produce
            // invalid IL that only crashes at runtime.
            if (goToRecord.ReturnType.FullName != "System.Boolean" || helperMi.ReturnType != typeof(bool))
                throw new InvalidOperationException(
                    "[Cecil] ALGoToRecord/helper return type is not Boolean — Ncl shape changed; do not commit");

            ReplaceBodyWithHelper(asm.MainModule, goToRecord, helperMi);
            Console.Error.WriteLine("[Cecil] Rewrote NavTestPageBase.ALGoToRecord → BcRuntime.NavTestPageBase_ALGoToRecord");
        }

        // 7. NavTestPageBase.GetMetaTable() — the sibling orphan of #6, and left behind when
        //    ALGoToRecord was migrated. BC's body reads
        //    `MetaPage.Properties.SourceObject.SourceTable`, which NREs on the runner's page
        //    metadata, so every TestPage API routed through PrimaryKeyFields —
        //    GoToKey, FindFirstField, FindNextField, FindPreviousField, Filter — died
        //    before reaching the backing LiveNavTestPage.
        //
        //    The replacement resolves the SourceTable from the AL source the runner parsed,
        //    which is the same map TestPageFactory already builds the page's record from, so
        //    the two cannot disagree.
        {
            var getMetaTable = navTestPageBaseType.Methods
                .FirstOrDefault(m => m.Name == "GetMetaTable" && m.Parameters.Count == 0 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] NavTestPageBase.GetMetaTable() not found — Ncl shape changed; do not commit");

            var metaTableHelper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NavTestPageBase_GetMetaTable),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NavTestPageBase_GetMetaTable not found");

            if (getMetaTable.ReturnType.FullName != metaTableHelper.ReturnType.FullName)
                throw new InvalidOperationException(
                    "[Cecil] GetMetaTable/helper return type mismatch — Ncl shape changed; do not commit");

            ReplaceBodyWithHelper(asm.MainModule, getMetaTable, metaTableHelper);
            Console.Error.WriteLine("[Cecil] Rewrote NavTestPageBase.GetMetaTable → BcRuntime.NavTestPageBase_GetMetaTable");
        }

        // 8. NCLMetaXmlPort.CreateObjectInstance(ITreeObject) — BC invokes
        //    base.ApplicationObjectConstructor, which the runner forces to null for every
        //    object type (see RecordPatches.CreateObjectInstance), substituting a per-type
        //    construction path. XmlPort had one only for the handle path
        //    (NavXmlPortHandle.CreateTarget); the STATIC XmlPort.Import(id, …) /
        //    XmlPort.Export(id, …) forms come through here instead and NREd on the null
        //    delegate. Both paths now construct from the same CLR type.
        {
            var metaXmlPortType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLMetaXmlPort")
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaXmlPort type not found — Ncl shape changed; do not commit");

            var createInstance = metaXmlPortType.Methods
                .FirstOrDefault(m => m.Name == "CreateObjectInstance" && m.Parameters.Count == 1 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaXmlPort.CreateObjectInstance(1) not found — Ncl shape changed; do not commit");

            var createHelper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NCLMetaXmlPort_CreateObjectInstance),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NCLMetaXmlPort_CreateObjectInstance not found");

            if (createInstance.ReturnType.FullName != createHelper.ReturnType.FullName)
                throw new InvalidOperationException(
                    "[Cecil] NCLMetaXmlPort.CreateObjectInstance/helper return type mismatch — do not commit");

            ReplaceBodyWithHelper(asm.MainModule, createInstance, createHelper);
            Console.Error.WriteLine("[Cecil] Rewrote NCLMetaXmlPort.CreateObjectInstance → BcRuntime.NCLMetaXmlPort_CreateObjectInstance");
        }

        // 8b. NCLMetaQuery.CreateObjectInstance(ITreeObject, SecurityFiltering) — the query
        //     twin of the rewrite above. The handle form (an AL `Query "Foo"` variable) has
        //     its own construction path; the STATIC Query.SaveAsXml(id, stream) /
        //     SaveAsCsv(id, …) / SaveAsJson(id, …) forms come through here and NREd on the
        //     null ApplicationObjectConstructor delegate.
        {
            var metaQueryType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLMetaQuery")
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaQuery type not found — Ncl shape changed; do not commit");

            var qCreate = metaQueryType.Methods
                .FirstOrDefault(m => m.Name == "CreateObjectInstance" && m.Parameters.Count == 2 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaQuery.CreateObjectInstance(2) not found — Ncl shape changed; do not commit");

            var qHelper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NCLMetaQuery_CreateObjectInstance),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NCLMetaQuery_CreateObjectInstance not found");

            if (qCreate.ReturnType.FullName != qHelper.ReturnType.FullName)
                throw new InvalidOperationException(
                    "[Cecil] NCLMetaQuery.CreateObjectInstance/helper return type mismatch — do not commit");

            ReplaceBodyWithHelper(asm.MainModule, qCreate, qHelper);
            Console.Error.WriteLine("[Cecil] Rewrote NCLMetaQuery.CreateObjectInstance → BcRuntime.NCLMetaQuery_CreateObjectInstance");
        }

        // 8b2. NCLMetaForm.CreateObjectInstance(NavRecord) — #1897, the form/page twin of
        //      the XmlPort/Query pair above. The AL-variable page form
        //      (`P: Page "X"; P.SetRecord(Rec); P.RunModal();`) already has its own working
        //      construction path (NavFormHandle.CreateTarget); the STATIC forms
        //      (Page.RunModal(id[, Record]), and transitively Base App Codeunit 700
        //      "Page Management".PageRunModal/PageRun) reach NavForm.RunModalAsync
        //      (static) → NCLMetadata.GetMetaFormById(id).CreateObjectInstance(record)
        //      instead, and NRE on the null ApplicationObjectConstructor delegate.
        //      NCLMetaForm declares THREE CreateObjectInstance overloads — (), (NavRecord),
        //      (string personalizationId) — only the (NavRecord) one is rewritten here; the
        //      0-arg overload chains to it (`CreateObjectInstance((NavRecord)null)`, covered
        //      automatically) and the personalizationId overload is unrelated.
        {
            var metaFormType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLMetaForm")
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaForm type not found — Ncl shape changed; do not commit");

            var fCreate = metaFormType.Methods
                .FirstOrDefault(m => m.Name == "CreateObjectInstance" && m.HasBody
                    && m.Parameters.Count == 1
                    && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavRecord")
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaForm.CreateObjectInstance(NavRecord) not found — Ncl shape changed; do not commit");

            var fHelper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NCLMetaForm_CreateObjectInstance),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NCLMetaForm_CreateObjectInstance not found");

            if (fCreate.ReturnType.FullName != fHelper.ReturnType.FullName)
                throw new InvalidOperationException(
                    "[Cecil] NCLMetaForm.CreateObjectInstance/helper return type mismatch — do not commit");

            ReplaceBodyWithHelper(asm.MainModule, fCreate, fHelper);
            Console.Error.WriteLine("[Cecil] Rewrote NCLMetaForm.CreateObjectInstance(NavRecord) → BcRuntime.NCLMetaForm_CreateObjectInstance");
        }

        // 8b3. NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions — #1896. Real BC
        //      resolves each Enum-typed page control's OptionString/OptionCaption/OptionValues
        //      through NCLMetadata.TryGetMetaApplicationObject(ObjectType.Enum, ...), which the
        //      runner never populates for Enum objects (see PageEnumFieldMetadataPatches.cs for
        //      the full root-cause writeup). Two overloads share the name — MetaPageDefinition
        //      (the frozen/cached path NavForm.GetMasterPage reaches) and PageDefinition (the
        //      thawed/mutable sibling) — both rewritten here.
        {
            var metaFormType2 = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLMetaForm")
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaForm type not found — Ncl shape changed; do not commit");

            var enumMetaCandidates = metaFormType2.Methods
                .Where(m => m.Name == "ApplyAppGroupAwareEnumMetadataToPageExpressions" && m.HasBody
                    && m.Parameters.Count == 1)
                .ToList();
            if (enumMetaCandidates.Count != 2)
                throw new InvalidOperationException(
                    $"[Cecil] NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions: expected "
                    + $"exactly 2 overloads (MetaPageDefinition, PageDefinition), found "
                    + $"{enumMetaCandidates.Count} — Ncl shape changed; do not commit");

            var metaOverload = enumMetaCandidates.FirstOrDefault(m =>
                m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.Metadata.MetaPageDefinition")
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions(MetaPageDefinition) "
                    + "not found — Ncl shape changed; do not commit");
            var pageOverload = enumMetaCandidates.FirstOrDefault(m =>
                m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.Metadata.PageDefinition")
                ?? throw new InvalidOperationException(
                    "[Cecil] NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions(PageDefinition) "
                    + "not found — Ncl shape changed; do not commit");

            var metaHelper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NCLMetaForm_ApplyEnumMetadataToMetaPageExpressions),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NCLMetaForm_ApplyEnumMetadataToMetaPageExpressions not found");
            var pageHelper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NCLMetaForm_ApplyEnumMetadataToPageExpressions),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NCLMetaForm_ApplyEnumMetadataToPageExpressions not found");

            if (metaOverload.ReturnType.FullName != NormalizeTypeName(metaHelper.ReturnType.FullName ?? ""))
                throw new InvalidOperationException(
                    "[Cecil] NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions(MetaPageDefinition)"
                    + "/helper return type mismatch — do not commit");
            if (pageOverload.ReturnType.FullName != NormalizeTypeName(pageHelper.ReturnType.FullName ?? ""))
                throw new InvalidOperationException(
                    "[Cecil] NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions(PageDefinition)"
                    + "/helper return type mismatch — do not commit");

            ReplaceBodyWithHelper(asm.MainModule, metaOverload, metaHelper);
            ReplaceBodyWithHelper(asm.MainModule, pageOverload, pageHelper);
            Console.Error.WriteLine(
                "[Cecil] Rewrote NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions"
                + "(MetaPageDefinition|PageDefinition) → BcRuntime.NCLMetaForm_ApplyEnumMetadataTo*Expressions");
        }

        // 8c. ALSystemOperatingSystem.get_ALGuiAllowed → true. This is AL's GuiAllowed().
        //
        //     BC's body is `NavCurrentThread.Session.CallbackAllowed`, which walks
        //     serviceConnection / AccessLock / ClientCallbackOrNull — all null on the
        //     skeleton session, so it reports false.
        //
        //     False is the wrong answer, not a conservative one: AL code branches on
        //     GuiAllowed() to decide whether to raise UI at all, so a false answer silently
        //     skips the very Message/Confirm/StrMenu/page calls the runner exists to route
        //     into [MessageHandler]/[ConfirmHandler]/[PageHandler]. The runner IS a
        //     UI-capable session — it dispatches those callbacks, and refuses unhandled UI
        //     with "Unhandled UI" (see corpus CU60706) exactly as BC's test runner does.
        //     True is what real BC reports under the test framework.
        {
            var alOsType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.ALSystemOperatingSystem")
                ?? throw new InvalidOperationException(
                    "[Cecil] ALSystemOperatingSystem type not found — Ncl shape changed; do not commit");

            var guiAllowed = alOsType.Methods
                .FirstOrDefault(m => m.Name == "get_ALGuiAllowed" && m.Parameters.Count == 0 && m.HasBody
                                     && m.ReturnType.FullName == "System.Boolean")
                ?? throw new InvalidOperationException(
                    "[Cecil] ALSystemOperatingSystem.get_ALGuiAllowed not found — do not commit");

            var gaBody = guiAllowed.Body;
            gaBody.Instructions.Clear();
            gaBody.ExceptionHandlers.Clear();
            gaBody.Variables.Clear();
            var gaIl = gaBody.GetILProcessor();
            gaIl.Append(gaIl.Create(OpCodes.Ldc_I4_1));
            gaIl.Append(gaIl.Create(OpCodes.Ret));
            gaBody.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Rewrote ALSystemOperatingSystem.get_ALGuiAllowed → true (runner dispatches UI to test handlers)");
        }

        // 9. NavCurrentThread.get_Session — see BcRuntime.NavCurrentThread_get_Session.
        //    BC reads an AsyncLocal the runner sets once on the bootstrap thread; any flow
        //    the ExecutionContext does not reach gets a silent null that BC's callers do
        //    not null-check. The replacement prefers the AsyncLocal and falls back to the
        //    skeleton session, which — the runner being single-session by construction —
        //    is the same instance that context would have carried.
        {
            var navCurrentThreadType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavCurrentThread")
                ?? throw new InvalidOperationException(
                    "[Cecil] NavCurrentThread type not found — Ncl shape changed; do not commit");

            var getSession = navCurrentThreadType.Methods
                .FirstOrDefault(m => m.Name == "get_Session" && m.IsStatic
                                     && m.Parameters.Count == 0 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] NavCurrentThread.get_Session not found — Ncl shape changed; do not commit");

            var sessionHelper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NavCurrentThread_get_Session),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.NavCurrentThread_get_Session not found");

            if (getSession.ReturnType.FullName != sessionHelper.ReturnType.FullName)
                throw new InvalidOperationException(
                    "[Cecil] NavCurrentThread.get_Session/helper return type mismatch — do not commit");

            ReplaceBodyWithHelper(asm.MainModule, getSession, sessionHelper);
            Console.Error.WriteLine("[Cecil] Rewrote NavCurrentThread.get_Session → BcRuntime.NavCurrentThread_get_Session");
        }

        // 8d. NavTestExecution.FindHandler — refuse unhandled UI even without a TestRunner codeunit.
        //
        //     BC's body:
        //         if (methodInfo == null && throwIfNotFound
        //             && executingTestRunner != null && executingTestMethod != null)
        //             throw new NavNCLMissingUIHandlerException(Lang.MissingUIHandler, …)   // "Unhandled UI: {0} {1}"
        //
        //     `executingTestRunner` is only set by EnterTestRunner(NavTestRunnerCodeUnit) — i.e.
        //     when an AL codeunit with `Subtype = TestRunner` is driving the run. The runner
        //     invokes test methods itself, so that field is always null and the throw could
        //     never fire. The consequence is not a missing diagnostic but a WRONG one: with no
        //     [ModalPageHandler], TestHandleModalForm returned false, NavForm.RunModalAsync
        //     fell through to the real client-callback path, and AL saw "A page with the
        //     specified handle has not been registered" instead of BC's "Unhandled UI".
        //     Worse, other unhandled-UI surfaces would silently continue.
        //
        //     `executingTestMethod` (set by EnterTestMethod) IS populated here — it is what
        //     makes handler lookup work at all for the tests that do declare handlers. So the
        //     fix is to drop the runner conjunct and keep the method one: rewrite the single
        //     `ldfld executingTestRunner` in this method to load `executingTestMethod`, which
        //     leaves the guard as `executingTestMethod != null && executingTestMethod != null`.
        //     No new type or member references are introduced — both fields already belong to
        //     this type — so R2R callers keep their token offsets.
        {
            var testExecType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavTestExecution")
                ?? throw new InvalidOperationException(
                    "[Cecil] NavTestExecution type not found — Ncl shape changed; do not commit");

            // The 4-arg overload: (NavHandlerType, NavApplicationObjectBase, bool, string).
            var findHandler = testExecType.Methods
                .FirstOrDefault(m => m.Name == "FindHandler" && m.Parameters.Count == 4 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] NavTestExecution.FindHandler(4) not found — Ncl shape changed; do not commit");

            var fTestMethod = testExecType.Fields.FirstOrDefault(f => f.Name == "executingTestMethod")
                ?? throw new InvalidOperationException(
                    "[Cecil] NavTestExecution.executingTestMethod not found — Ncl shape changed; do not commit");

            var runnerLoads = findHandler.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldfld
                            && i.Operand is FieldReference fr && fr.Name == "executingTestRunner")
                .ToList();
            if (runnerLoads.Count != 1)
                throw new InvalidOperationException(
                    $"[Cecil] NavTestExecution.FindHandler(4) has {runnerLoads.Count} executingTestRunner " +
                    "loads, expected exactly 1 — Ncl shape changed; do not commit");

            runnerLoads[0].Operand = fTestMethod;
            Console.Error.WriteLine(
                "[Cecil] NavTestExecution.FindHandler → unhandled UI now throws without a TestRunner codeunit");
        }

        // 8e. CompanyHelper.ValidateUserHasAccessToCompany — see
        //     CompanyAccessPatches.CompanyHelper_ValidateUserHasAccessToCompany.
        //     The real body reads the Company system table through the tenant database and
        //     the user's entitlements; the runner has neither, and this is the single
        //     decision behind AL's Record.ChangeCompany(<name>).
        {
            var companyHelperType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.CompanyHelper")
                ?? throw new InvalidOperationException(
                    "[Cecil] CompanyHelper type not found — Ncl shape changed; do not commit");

            var validateAccess = companyHelperType.Methods
                .FirstOrDefault(m => m.Name == "ValidateUserHasAccessToCompany"
                                     && m.IsStatic && m.Parameters.Count == 3 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] CompanyHelper.ValidateUserHasAccessToCompany(3) not found — Ncl shape changed; do not commit");

            var validateHelper = typeof(AlRunner.Patches.CompanyAccessPatches).GetMethod(
                nameof(AlRunner.Patches.CompanyAccessPatches.CompanyHelper_ValidateUserHasAccessToCompany),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] CompanyAccessPatches.CompanyHelper_ValidateUserHasAccessToCompany not found");

            ReplaceBodyWithHelper(asm.MainModule, validateAccess, validateHelper);
            Console.Error.WriteLine(
                "[Cecil] Rewrote CompanyHelper.ValidateUserHasAccessToCompany → single-company check");
        }

        // 8f. SessionTransactionExtensions.Rollback — see RecordPatches.RollbackToCommitPoint.
        //
        //     BC implements AL's "an error rolls the database back to the last COMMIT" here:
        //     NavMethodScope.AssertError catches the error and calls session.Rollback(). The
        //     real body goes through session.DataAccessSource's transaction manager, which the
        //     runner has no equivalent of — its tables are in-memory TempTableDataProviders.
        //
        //     This used to be a JmpHook to a no-op, and JmpHook has been disabled by default
        //     since the Cecil migration, so nothing rolled back at all: an asserterror left
        //     every write made before it in place. Corpus TestTriggerRollback pins the real
        //     behaviour in three directions (see RecordPatches.TransactionSnapshot).
        {
            var sessTxType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.SessionTransactionExtensions")
                ?? throw new InvalidOperationException(
                    "[Cecil] SessionTransactionExtensions type not found — Ncl shape changed; do not commit");

            var rollbackMethod = sessTxType.Methods
                .FirstOrDefault(m => m.Name == "Rollback" && m.IsStatic
                                     && m.Parameters.Count == 1 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] SessionTransactionExtensions.Rollback(NavSession) not found — Ncl shape changed; do not commit");

            var rollbackHelper = typeof(AlRunner.Patches.RecordPatches).GetMethod(
                nameof(AlRunner.Patches.RecordPatches.RollbackToCommitPoint),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] RecordPatches.RollbackToCommitPoint not found");

            ReplaceBodyWithHelper(asm.MainModule, rollbackMethod, rollbackHelper);
            Console.Error.WriteLine("[Cecil] Rewrote SessionTransactionExtensions.Rollback → row-store rollback");
        }

        // 8g. SessionTransactionExtensions.EndTransactionWorldAndTransaction — see
        //     RecordPatches.NoteTransactionEnd.
        //
        //     AL Runner#1946 (revised by #2413): BC's own APIs wrap their internal work in one
        //     of two kinds of nested transaction. A TRANSACTION WORLD
        //     (Session.BeginTransactionWorldAndTransaction(); ...; finally {
        //     Session.EndTransactionWorldAndTransaction(commit); } — the guarded `Codeunit.Run`
        //     form, and `Ok := XmlPort.Import(...)`, i.e. DataError.TrapError, which AL's
        //     compiler picks whenever the call's boolean result is captured into a variable)
        //     commits durably: a real `commit == true` there is exactly as durable, from AL's
        //     point of view, as an explicit Commit() statement, so a LATER, unrelated
        //     asserterror in the caller must not roll it back. A PLAIN NESTED transaction
        //     (Session.BeginTransaction(); ...; finally { Session.EndTransaction(commit); } —
        //     Query.Open, statement-form XmlPort.Import i.e. DataError.ThrowError) is not a
        //     commit at all: it joins the caller's already-open transaction and
        //     EndTransaction(true) at that depth only pops it, so nothing reaches the database
        //     until the OUTER transaction (the test framework's own boundary, or an explicit
        //     AL Commit()) completes.
        //
        //     #1946 hooked BOTH methods on the strength of a reproduction that turned out not
        //     to distinguish them — no XmlPort involved, just Insert() then an unrelated
        //     trapped Error(), which #2402 later measured keeps the row on real BC only when an
        //     EARLIER test method had committed the same key, not because EndTransaction is a
        //     commit point. #2413 measured the two kinds directly against real BC: hooking the
        //     plain EndTransaction wrongly turns Query.Open and statement-form XmlPort.Import
        //     into commit points, so writes made BEFORE them survive an asserterror that real
        //     BC rolls back (P1, P2, P3, P8 in #2413). Only EndTransactionWorldAndTransaction
        //     gets the hook now.
        //
        //     Prepend, not replace: the original body
        //     (SessionTransactionManager.EndTransactionWorldAndTransaction) already runs safely
        //     today (every passing guarded-Codeunit.Run / `Ok := XmlPort.Import` test goes
        //     through it), so this only adds the missing commit-point bookkeeping alongside it.
        {
            var sessTxType2 = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.SessionTransactionExtensions")
                ?? throw new InvalidOperationException(
                    "[Cecil] SessionTransactionExtensions type not found — Ncl shape changed; do not commit");

            var noteTransactionEndHelper = typeof(AlRunner.Patches.RecordPatches).GetMethod(
                nameof(AlRunner.Patches.RecordPatches.NoteTransactionEnd),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] RecordPatches.NoteTransactionEnd not found");

            var endTransactionMethod = sessTxType2.Methods
                .FirstOrDefault(m => m.Name == "EndTransactionWorldAndTransaction" && m.IsStatic
                                     && m.Parameters.Count == 2 && m.HasBody
                                     && m.Parameters[1].ParameterType.FullName == "System.Boolean")
                ?? throw new InvalidOperationException(
                    "[Cecil] SessionTransactionExtensions.EndTransactionWorldAndTransaction(NavSession, bool) not found — Ncl shape changed; do not commit");

            PrependStaticCall(asm.MainModule, endTransactionMethod, noteTransactionEndHelper, argSlots: 2);
        }


        // 9b. TreeHandler.get_Session — see BcRuntime.TreeHandler_get_Session.
        //     The real body is `=> session`, a readonly field set in the ctor from
        //     `parentHandler.session ?? (hostObject as NavSession)`. The runner's root tree
        //     handler carries no session, so every tree node inherits null. Callers do not
        //     guard: `NavFieldRef.ALValidateSafe()` passes it straight into
        //     `NavRecord.GetCallerRecord(session)`, which derefs `session.CurrentMethodScope`
        //     and NREs — AL's parameterless `FieldRef.Validate()` could never run.
        //
        //     This replacement used to be a JmpHook, which has been disabled by default since
        //     the Cecil migration, so the call site was a silent no-op. Migrating it, not
        //     re-enabling JmpHook.
        //
        //     Returning the skeleton session unconditionally is equivalent to returning the
        //     field when it is set: the runner is single-session by construction, so the only
        //     NavSession that can ever reach a tree handler IS the skeleton session.
        {
            var treeHandlerType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.TreeHandler")
                ?? throw new InvalidOperationException(
                    "[Cecil] TreeHandler type not found — Ncl shape changed; do not commit");

            var thGetSession = treeHandlerType.Methods
                .FirstOrDefault(m => m.Name == "get_Session" && !m.IsStatic
                                     && m.Parameters.Count == 0 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] TreeHandler.get_Session not found — Ncl shape changed; do not commit");

            var thHelper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.TreeHandler_get_Session),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] BcRuntime.TreeHandler_get_Session not found");

            ReplaceBodyWithHelper(asm.MainModule, thGetSession, thHelper);
            Console.Error.WriteLine("[Cecil] Rewrote TreeHandler.get_Session → BcRuntime.TreeHandler_get_Session");
        }

        // 10. NavSession.ThrowSessionTerminatedExceptionIfStopping() → no-op.
        //     The body is `AccessLock.ThrowSessionTerminatedExceptionIfStopping()`, and
        //     AccessLock is null on the skeleton session, so this pure guard NREs instead
        //     of guarding. It is inlined into its callers, which is why the NRE surfaced
        //     as NavXmlPortExporter.ProcessTableElement IL_0000 — the exporter opens with
        //     this call.
        //
        //     No-op is faithful: the method's entire contract is "throw if this session is
        //     being terminated". The runner has no session manager and no way to stop a
        //     session mid-run — one session runs to process exit (docs/scope.md) — so the
        //     guard's condition is permanently false and BC would return without throwing.
        {
            var navSessionType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavSession")
                ?? throw new InvalidOperationException(
                    "[Cecil] NavSession type not found — Ncl shape changed; do not commit");

            var guard = navSessionType.Methods
                .FirstOrDefault(m => m.Name == "ThrowSessionTerminatedExceptionIfStopping"
                                     && m.Parameters.Count == 0 && m.HasBody
                                     && m.ReturnType.FullName == "System.Void")
                ?? throw new InvalidOperationException(
                    "[Cecil] NavSession.ThrowSessionTerminatedExceptionIfStopping not found — do not commit");

            var noOp = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NoOp_OneArg), BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("[Cecil] BcRuntime.NoOp_OneArg not found");
            ReplaceBodyWithHelper(asm.MainModule, guard, noOp);
            Console.Error.WriteLine("[Cecil] Rewrote NavSession.ThrowSessionTerminatedExceptionIfStopping → no-op (skeleton session never stops)");
        }

        // 11. SessionTransactionExtensions.HasWriteTransaction(NavSession) — AL's
        //     Database.IsInWriteTransaction(). BC's body asks
        //     session.DataAccessSource.SessionTransactionManager.AnyHasWriteTransactionStarted(),
        //     and the runner's in-memory provider never opens one of BC's transactions, so it
        //     always answered false. ALDatabasePatches tracks the AL-observable state instead:
        //     set on the first non-temporary AL row write, cleared by Commit.
        {
            var stExtType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.SessionTransactionExtensions")
                ?? throw new InvalidOperationException(
                    "[Cecil] SessionTransactionExtensions type not found — Ncl shape changed; do not commit");

            var hasWriteTx = stExtType.Methods
                .FirstOrDefault(m => m.Name == "HasWriteTransaction" && m.IsStatic
                                     && m.Parameters.Count == 1 && m.HasBody
                                     && m.ReturnType.FullName == "System.Boolean")
                ?? throw new InvalidOperationException(
                    "[Cecil] SessionTransactionExtensions.HasWriteTransaction(NavSession) not found — do not commit");

            ReplaceBodyWithHelper(asm.MainModule, hasWriteTx,
                typeof(AlRunner.Patches.ALDatabasePatches).GetMethod(
                    nameof(AlRunner.Patches.ALDatabasePatches.HasWriteTransaction),
                    BindingFlags.Public | BindingFlags.Static)!);
            Console.Error.WriteLine("[Cecil] Rewrote SessionTransactionExtensions.HasWriteTransaction → runner write-transaction state");
        }

        // 12. ALDatabase.CurrentTransactionType — AL's Database.CurrentTransactionType().
        //     BC keeps it on TransactionManager's current LogicalTransaction, which the
        //     runner does not have. ALDatabasePatches holds the value and reproduces BC's
        //     own transition state machine, including the throw once a transaction has
        //     begun. See ALDatabasePatches for the per-transition table.
        {
            var alDbType = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.ALDatabase")
                ?? throw new InvalidOperationException(
                    "[Cecil] ALDatabase type not found — Ncl shape changed; do not commit");

            var ttGet = alDbType.Methods.FirstOrDefault(m =>
                m.Name == "get_ALCurrentTransactionType" && m.Parameters.Count == 0 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] ALDatabase.get_ALCurrentTransactionType not found — do not commit");
            var ttSet = alDbType.Methods.FirstOrDefault(m =>
                m.Name == "set_ALCurrentTransactionType" && m.Parameters.Count == 1 && m.HasBody)
                ?? throw new InvalidOperationException(
                    "[Cecil] ALDatabase.set_ALCurrentTransactionType not found — do not commit");

            ReplaceBodyWithHelper(asm.MainModule, ttGet,
                typeof(AlRunner.Patches.ALDatabasePatches).GetMethod(
                    nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_GetCurrentTransactionType),
                    BindingFlags.Public | BindingFlags.Static)!);
            ReplaceBodyWithHelper(asm.MainModule, ttSet,
                typeof(AlRunner.Patches.ALDatabasePatches).GetMethod(
                    nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_SetCurrentTransactionType),
                    BindingFlags.Public | BindingFlags.Static)!);
            Console.Error.WriteLine("[Cecil] Rewrote ALDatabase.CurrentTransactionType get/set → runner transaction-type state");
        }


    }

    private static void AddFormsOwned(HashSet<string> set)
    {
        // ALDatabase row-version getters (Cecil-migrated onto the in-process monotonic
        // clock in ALDatabasePatches). Their JmpHooks were orphaned — silently no-ops
        // once the JmpHook layer went off by default — so BC's SQL body ran and NRE'd.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALDatabase::ALLastUsedRowVersion/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALDatabase::ALMinimumActiveRowVersion/0");
        // ALDatabase.get_ALSerialNumber (#1883 follow-up) — genuinely NREs standalone
        // (NavSession.get_License() chain), confirmed empirically. Cecil-migrated onto the
        // ReturnStandalone_0Args sentinel; legacy JmpHook registration deleted from BcRuntime.cs.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALDatabase::get_ALSerialNumber/0");
        // NavTestPageBase.ALGoToRecord — migrated off the (disabled) JmpHook layer onto the
        // Cecil rewrite in step 6 of the TestPage cluster. Registering it here makes the
        // legacy JmpHook a no-op so the two mechanisms cannot coexist on this method.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavTestPageBase::ALGoToRecord/2");
        // NCLMetaXmlPort.CreateObjectInstance — the runner forces ApplicationObjectConstructor
        // to null for every object type; this is XmlPort's per-type construction path for the
        // STATIC XmlPort.Import/Export forms (the handle path has its own).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaXmlPort::CreateObjectInstance/1");
        // NCLMetaQuery.CreateObjectInstance — same null-ApplicationObjectConstructor story,
        // for the STATIC Query.SaveAsXml/Csv/Json(id, …) forms.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaQuery::CreateObjectInstance/2");
        // NCLMetaForm.CreateObjectInstance(NavRecord) — #1897, the form/page twin of the
        // XmlPort/Query pair above. Static Page.RunModal(id[, Record]) (and, transitively,
        // Base App Codeunit 700 "Page Management".PageRunModal/PageRun) reach this instead
        // of NavFormHandle.CreateTarget (the AL-variable path, which already had its own
        // construction) and NRE on the null ApplicationObjectConstructor delegate.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaForm::CreateObjectInstance/1");
        // NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions — #1896. Real BC
        // resolves each Enum-typed page control's OptionString/OptionCaption/OptionValues via
        // NCLMetadata.TryGetMetaApplicationObject(ObjectType.Enum, ...), which the runner
        // never populates (AL enums are served through the separate NCLEnumMetadata.Create(int)
        // hook, a different codepath page materialisation never calls). Both overloads
        // (MetaPageDefinition and PageDefinition) share this Name+paramCount key.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaForm::ApplyAppGroupAwareEnumMetadataToPageExpressions/1");
        // ALSystemOperatingSystem.get_ALGuiAllowed — AL's GuiAllowed(). True, because the
        // runner registers a client callback and dispatches UI to test handlers.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALSystemOperatingSystem::get_ALGuiAllowed/0");
        // NavCurrentThread.get_Session — AsyncLocal-backed; falls back to the skeleton
        // session on any flow the bootstrap ExecutionContext does not reach.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavCurrentThread::get_Session/0");
        // CompanyHelper.ValidateUserHasAccessToCompany — the only decision behind AL's
        // Record.ChangeCompany(<name>); the real body needs a tenant database + entitlements.
        set.Add("Microsoft.Dynamics.Nav.Runtime.CompanyHelper::ValidateUserHasAccessToCompany/3");
        // SessionTransactionExtensions.Rollback — AL's write-transaction rollback, which
        // NavMethodScope.AssertError calls after catching an error.
        set.Add("Microsoft.Dynamics.Nav.Runtime.SessionTransactionExtensions::Rollback/1");
        // TreeHandler.get_Session — `=> session`, and that field is null on every tree the
        // runner builds (the root handler has no session to propagate). Callers do not
        // null-check it: NavFieldRef.ALValidateSafe() hands it straight to
        // NavRecord.GetCallerRecord, which derefs it.
        set.Add("Microsoft.Dynamics.Nav.Runtime.TreeHandler::get_Session/0");
        // SessionTransactionExtensions.HasWriteTransaction — AL's Database.IsInWriteTransaction().
        set.Add("Microsoft.Dynamics.Nav.Runtime.SessionTransactionExtensions::HasWriteTransaction/1");
        // ALDatabase.CurrentTransactionType — AL's Database.CurrentTransactionType().
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALDatabase::get_ALCurrentTransactionType/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALDatabase::set_ALCurrentTransactionType/1");
        // NavSession.ThrowSessionTerminatedExceptionIfStopping — pure guard over a null
        // AccessLock on the skeleton session.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::ThrowSessionTerminatedExceptionIfStopping/0");
        // NavSession getters
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_DataAccessSource/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_Database/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavTenant::get_Database/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_SortingProperties/0");
        // GetDataAccessForQuery(NCLMetaQueryDefinition) — single-dataitem returns the one
        // in-memory DataAccess; multi-dataitem (join) over EMPTY tables returns the root
        // DataAccess (faithful no-rows), join WITH data throws RunnerOutOfScopeException.
        set.Add("Microsoft.Dynamics.Nav.Runtime.DataAccessSource::GetDataAccessForQuery/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider::.ctor/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider::CalcNumeric/1");
        // Collation comparers
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavDatabase::get_CollationAwareStringComparer/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecordId::get_CollationAwareStringComparer/0");
        // NavRecord no-ops
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecord::Dispose/1");
        // NavRecord.GetCallerRecord(NavSession) — faithful reimplementation reading the actual
        // tracked NavSession.CurrentMethodScope backing field (see GetCallerRecordPatches.cs
        // and #1781: nested Validate re-snapshotting xRec because this used to be forced null).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecord::GetCallerRecord/1");
        // NavSession getter cluster + GetActiveCompany + NavStream.Target (same atomic path)
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_CurrentMethodScope/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_NavAppGroup/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_LocalLanguageNoFallback/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_IsLocalLanguage/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::GetSecurityFilters/5");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::PushDynamicCaptionStack/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::SyncFormatSettings/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_Culture/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::get_WindowsCulture/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::GetActiveCompany/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavStream::get_Target/0");
        // NavSession.GetPermissionSet (Batch 8) — both 3-arg overloads (ByObjectId
        // + ByObjectIds). Leaf of the CalcSums permission-verify path.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavSession::GetPermissionSet/3");
        // Truncate + security-filtering cluster (Batch 8).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecord::ValidateTruncateSupport/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::SetSecurityFiltering/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.DataProvider::TruncateAsync/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.PermissionManagement::SessionHasSuperOrSecurityPermissionsForUser/2");
        // ALSystemOperatingSystem GetUrl family (Batch 8) — all 7-arg overloads.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALSystemOperatingSystem::GetUrlCore/7");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALSystemOperatingSystem::ALGetUrl/7");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALSystemOperatingSystem::ALGetUrlInternal/7");
    }

}
