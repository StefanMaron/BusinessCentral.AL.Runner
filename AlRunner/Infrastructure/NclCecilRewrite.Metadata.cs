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
    private static void RewriteNcl_Metadata(AssemblyDefinition asm)
    {
        {
            var nclMetadataT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLMetadata");
            var m = nclMetadataT?.Methods.FirstOrDefault(mm =>
                mm.Name == "EnsureAppGroupOwnedObjectsInitialized" && mm.HasBody &&
                mm.ReturnType.FullName == "System.Void" && mm.Parameters.Count == 2);
            if (m != null)
            {
                var body = m.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                body.GetILProcessor().Append(body.GetILProcessor().Create(OpCodes.Ret));
                body.MaxStackSize = 0;
                Console.Error.WriteLine("[Cecil] Rewrote NCLMetadata.EnsureAppGroupOwnedObjectsInitialized → no-op (skip skeleton app-group lazy init)");
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARN: NCLMetadata.EnsureAppGroupOwnedObjectsInitialized(NavAppGroup,string) not found — query metadata build may NRE");
            }
        }

        // NavSession.VerifyExecutePermission(...) void overloads → no-op.
        // The real query open path (NavQuery.VerifyPermissions → VerifyExecutePermission →
        // HasCachedExecutePermissions) NREs on the skeleton session's null permission cache.
        // There is a JmpHook for this (BcRuntime), but JmpHooks are disabled (Cecil-only), so
        // it never lands. The skeleton runs as SUPER — execute permission is always granted —
        // so no-op is faithful. (Codeunits don't hit this; our CreateTarget bypasses dispatch.)
        {
            var navSessionT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavSession");
            int vepCount = 0;
            foreach (var m in (navSessionT?.Methods ?? Enumerable.Empty<MethodDefinition>())
                .Where(mm => mm.Name == "VerifyExecutePermission" && mm.HasBody
                    && mm.ReturnType.FullName == "System.Void").ToList())
            {
                var body = m.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                body.GetILProcessor().Append(body.GetILProcessor().Create(OpCodes.Ret));
                body.MaxStackSize = 0;
                vepCount++;
            }
            Console.Error.WriteLine($"[Cecil] Rewrote {vepCount} NavSession.VerifyExecutePermission overload(s) → no-op");

            // Same session, same reason, the bool half: HasExecutePermission /
            // HasCachedExecutePermissions / the per-company variants. Nothing reached these
            // until the page path did — MergePageAndTable -> GetTableMetadata ->
            // TranslateCaptionClassOnMetaTableFields asks whether the session may execute
            // the object before translating its caption class, and it NREs on the same null
            // permission cache the void overloads above were neutered for. The skeleton runs
            // as SUPER, so `true` is the same answer the void overloads already imply by
            // never throwing; returning false here would instead silently drop captions.
            int hepCount = 0;
            foreach (var m in (navSessionT?.Methods ?? Enumerable.Empty<MethodDefinition>())
                .Where(mm => (mm.Name == "HasExecutePermission"
                              || mm.Name == "HasCachedExecutePermissions"
                              || mm.Name == "HasExecutePermissionForCompany"
                              || mm.Name == "HasExecutePermissionForAllCompanies")
                    && mm.HasBody && mm.ReturnType.FullName == "System.Boolean").ToList())
            {
                var body = m.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                hepCount++;
            }
            if (hepCount == 0)
                throw new InvalidOperationException(
                    "NavSession.HasExecutePermission overloads not found — Ncl shape changed; do not commit");
            Console.Error.WriteLine($"[Cecil] Rewrote {hepCount} NavSession.Has*ExecutePermission* overload(s) → true (skeleton session runs as SUPER)");
        }

        // NavSession.NCLMetadata → NavGlobal.NCLMetadata.
        //
        // The real body is `this.SystemTenant.NCLMetadata`, and SystemTenant is null on the
        // skeleton session, so any BC code reaching the metadata cache THROUGH THE SESSION
        // NREs — while the identical code reaching it through NavGlobal works fine. That
        // asymmetry is invisible until something takes the session route, which is what
        // NavForm.InitializeFromMetadata does (`base.Session.NCLMetadata.GetMetaTableById`).
        //
        // There is exactly one metadata cache in the runner — NavGlobal's, the one every
        // other patch populates and reads — so routing the session property to it is not a
        // substitute, it is the same object BC would have handed back had the tenant been
        // wired. Single-tenant by construction (see docs/scope.md), so there is no second
        // cache this could pick the wrong one of.
        {
            var navSessionT2 = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavSession")
                ?? throw new InvalidOperationException("NavSession type not found — do not commit");
            var navGlobalT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavGlobal")
                ?? throw new InvalidOperationException("NavGlobal type not found — do not commit");
            var globalGetter = navGlobalT.Methods
                .FirstOrDefault(mm => mm.Name == "get_NCLMetadata" && mm.IsStatic && mm.Parameters.Count == 0)
                ?? throw new InvalidOperationException("NavGlobal.get_NCLMetadata not found — do not commit");
            var sessionGetter = navSessionT2.Methods
                .FirstOrDefault(mm => mm.Name == "get_NCLMetadata" && mm.HasBody && mm.Parameters.Count == 0)
                ?? throw new InvalidOperationException("NavSession.get_NCLMetadata not found — do not commit");
            if (sessionGetter.ReturnType.FullName != globalGetter.ReturnType.FullName)
                throw new InvalidOperationException(
                    $"NCLMetadata getter return types differ ({sessionGetter.ReturnType.FullName} vs "
                    + $"{globalGetter.ReturnType.FullName}) — do not commit");

            var body = sessionGetter.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Call, globalGetter));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Rewrote NavSession.NCLMetadata → NavGlobal.NCLMetadata (skeleton session has no SystemTenant)");
        }

        // NavForm.UpdateAllowedOperationsFromPermissions() → no-op.
        //
        // The last fault on the page-initialisation path: InitializeFromMetadata calls it to
        // narrow the page's insert/modify/delete flags by the SESSION'S PERMISSIONS, and it
        // NREs on the skeleton's permission state. Note that it is reached even when a caller
        // only wants SetSourceTable — that method funnels through EnsureMetadataLoaded, so
        // there is no way to bind a record to a page without passing through here.
        //
        // Same justification as the Has*ExecutePermission* rewrites just above: the skeleton
        // session runs as SUPER, so permissions can only ever WIDEN nothing and NARROW
        // nothing. Skipping the narrowing leaves the page with the operations its AL actually
        // declared (InsertAllowed / ModifyAllowed / DeleteAllowed), which is what an AL test
        // asserts against — and what the runner's own TestPage.Creatable already reads from
        // the parsed page properties.
        {
            var navFormT2 = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavForm")
                ?? throw new InvalidOperationException("NavForm type not found — do not commit");
            int uaoCount = 0;
            foreach (var m in navFormT2.Methods
                .Where(mm => mm.Name == "UpdateAllowedOperationsFromPermissions" && mm.HasBody
                          && mm.ReturnType.FullName == "System.Void").ToList())
            {
                var body = m.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                body.GetILProcessor().Append(body.GetILProcessor().Create(OpCodes.Ret));
                body.MaxStackSize = 0;
                uaoCount++;
            }
            if (uaoCount == 0)
                throw new InvalidOperationException(
                    "NavForm.UpdateAllowedOperationsFromPermissions not found — Ncl shape changed; do not commit");
            Console.Error.WriteLine(
                $"[Cecil] Rewrote {uaoCount} NavForm.UpdateAllowedOperationsFromPermissions overload(s) → no-op (skeleton session runs as SUPER)");
        }

        // NavForm.RegisterExpressionsFromCustomizationControls() → no-op.
        //
        // Immediately after the above on the same path. It walks the controls a DESIGNER or
        // profile customization added on top of the AL-declared page and registers source
        // expressions for them; it NREs because the skeleton has no customization store.
        //
        // The runner has no page designer, no profiles and no personalization (the same
        // reason LoadPageDataPersonalization returns default above), so there are no
        // customization controls to register — an empty set is not an approximation of the
        // real answer here, it IS the real answer. The AL-declared controls are registered
        // separately, by the page's own OnMetadataLoaded.
        {
            var navFormT3 = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavForm")
                ?? throw new InvalidOperationException("NavForm type not found — do not commit");
            int recCount = 0;
            foreach (var m in navFormT3.Methods
                .Where(mm => mm.Name == "RegisterExpressionsFromCustomizationControls" && mm.HasBody
                          && mm.Parameters.Count == 0
                          && mm.ReturnType.FullName == "System.Void").ToList())
            {
                var body = m.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                body.GetILProcessor().Append(body.GetILProcessor().Create(OpCodes.Ret));
                body.MaxStackSize = 0;
                recCount++;
            }
            if (recCount == 0)
                throw new InvalidOperationException(
                    "NavForm.RegisterExpressionsFromCustomizationControls() not found — Ncl shape changed; do not commit");
            Console.Error.WriteLine(
                $"[Cecil] Rewrote {recCount} NavForm.RegisterExpressionsFromCustomizationControls overload(s) → no-op (no page designer / profiles / personalization in the runner)");
        }

        // SessionTransactionExtensions.SetRecordConsistent / SetRecordInconsistent → no-op.
        // These extension methods mark a record's transaction-consistency state via the
        // session's DataAccessSource, which is null on the skeleton session. The posting
        // preview-mode manager (Codeunit 9500 StopPreviewMode/SetPreviewMode) calls
        // SetRecordConsistent unconditionally during a normal (non-preview) post, so the real
        // body NREs in SessionTransactionExtensions.SetRecordConsistent and aborts the whole
        // post. There is a JmpHook for this (BcRuntime → NoOp2) but JmpHooks are disabled
        // (Cecil-only), so it never lands — migrate it here. With no SQL transaction backend the
        // record-consistency marking is observably a no-op: AL code cannot observe a difference
        // because there is no deferred-consistency commit to honor or reject. (The in-memory
        // store applies writes immediately.) void return → single ret.
        {
            var sessTxExtT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.SessionTransactionExtensions");
            int srcCount = 0;
            foreach (var m in (sessTxExtT?.Methods ?? Enumerable.Empty<MethodDefinition>())
                .Where(mm => (mm.Name == "SetRecordConsistent" || mm.Name == "SetRecordInconsistent")
                    && mm.HasBody && mm.ReturnType.FullName == "System.Void").ToList())
            {
                var body = m.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                body.GetILProcessor().Append(body.GetILProcessor().Create(OpCodes.Ret));
                body.MaxStackSize = 0;
                srcCount++;
            }
            Console.Error.WriteLine($"[Cecil] Rewrote {srcCount} SessionTransactionExtensions.SetRecord(In)Consistent overload(s) → no-op");
        }

        // ALDatabase.{set,get}_ALLockTimeout / {set,get}_ALLockTimeoutDuration → trivial.
        // Each accessor reaches DataAccessSource.CreateAppDataProvider() (via CreateAppDataAccess),
        // which NREs on the skeleton session's null DataAccessSource. Posting calls
        // LockTimeout(false) (RunWithCheck → set_ALLockTimeoutDuration) on every non-GUI post,
        // so the real setter aborts the whole post. Lock-timeout is a SQL-transaction concept the
        // in-memory store has no analogue for; a setter no-op + getter default is observably
        // faithful (AL code only reads back what posting itself sets, and the value has no effect
        // without a SQL lock manager). JmpHooks exist for these (BcRuntime) but are disabled
        // (Cecil-only), so migrate here. Setters (void): single ret. Getters: push default + ret.
        {
            var alDbT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.ALDatabase");
            int ltCount = 0;
            foreach (var m in (alDbT?.Methods ?? Enumerable.Empty<MethodDefinition>())
                .Where(mm => mm.HasBody && (
                    mm.Name == "set_ALLockTimeout" || mm.Name == "get_ALLockTimeout" ||
                    mm.Name == "set_ALLockTimeoutDuration" || mm.Name == "get_ALLockTimeoutDuration")).ToList())
            {
                var body = m.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                var il = body.GetILProcessor();
                var rt = m.ReturnType.FullName;
                if (rt == "System.Void")
                {
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 0;
                }
                else if (rt == "System.Boolean" || rt == "System.Int32")
                {
                    il.Append(il.Create(OpCodes.Ldc_I4_0));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                }
                else
                {
                    // get_ALLockTimeoutDuration may return a richer duration type; load default.
                    il.Append(il.Create(OpCodes.Ldc_I4_0));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                }
                ltCount++;
            }
            Console.Error.WriteLine($"[Cecil] Rewrote {ltCount} ALDatabase.ALLockTimeout(Duration) accessor(s) → trivial");
        }

        // NavReport sync wrappers + DataItemIterator.SetTableView.
        //
        // RunReportAsync is async ValueTask (forbidden to Cecil-rewrite — see checkpoint 002),
        // so we replace the sync wrapper bodies before they enter the async path:
        //
        //   Run / RunModal (all overloads, void return)
        //     → call NavReportSync.SyncRun(this) (or this static helper resolves the instance for
        //       static overloads); ret. The helper invokes OnPreReport / per-DataItem Pre+Post /
        //       OnPostReport reflectively against the same NavReport instance.
        //
        //   SaveAsPdf / SaveAsHtml / SaveAsExcel / SaveAsWord / SaveAsDocx (sync, bool return)
        //     → throw NavNCLDialogException("out-of-scope: NavReport.<name>") — layout rendering
        //       requires a service tier the runner does not have. Tests rewrite these as
        //       `asserterror` + `Assert.ExpectedError('out-of-scope: NavReport.SaveAs')`.
        //
        //   RunRequestPage (any sync overload, string return)
        //     → throw NavNCLDialogException("out-of-scope: NavReport.RunRequestPage") —
        //       request-page UI rendering requires a service tier.
        //

    }

    private static void AddMetadataOwned(HashSet<string> set)
    {
        // (no entries currently assigned to this area)
    }

}
