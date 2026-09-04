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
    private static void RewriteNcl_Media(AssemblyDefinition asm)
    {

        // ── Batch 5: NavMediaValueBase.get_ALMediaId → Cecil body rewrite ────────────────
        //
        // Real body: `return Key.Value` — a trivial getter the JIT inlines into call sites,
        // bypassing JmpHook. Previous approach: mark NoInlining so the precode patch lands.
        // New approach: rewrite the body to call MediaSetPatches.NavMediaSet_get_ALMediaId(self),
        // registered in CecilOwned so the JmpHook skips this method entirely.
        // No re-entrancy: helper only touches an in-memory ConditionalWeakTable.
        {
            var navMediaValueBaseType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavMediaValueBase");
            if (navMediaValueBaseType != null)
            {
                var alMediaIdGetter = navMediaValueBaseType.Methods
                    .FirstOrDefault(m => m.Name == "get_ALMediaId" && m.Parameters.Count == 0 && m.HasBody);
                if (alMediaIdGetter != null)
                {
                    var helperMi = typeof(AlRunner.Patches.MediaSetPatches).GetMethod(
                        nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_get_ALMediaId),
                        BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException("[Cecil] MediaSetPatches.NavMediaSet_get_ALMediaId not found");
                    ReplaceBodyWithHelper(asm.MainModule, alMediaIdGetter, helperMi);
                }
                else
                {
                    Console.Error.WriteLine("[Cecil] WARNING: get_ALMediaId not found on NavMediaValueBase");
                }
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARNING: NavMediaValueBase not found in Ncl");
            }
        }

        // ── Batch 5: NavMediaSet AL methods → Cecil body rewrites ────────────────────────
        //
        // Real bodies reach the BC service-tier database layer (NavMediaSetTable etc.) which
        // NREs on the skeleton runtime. Previous approach: JmpHook. New approach: rewrite
        // bodies to call MediaSetPatches helpers (in-memory ConditionalWeakTable store).
        // Registered in CecilOwned so JmpHooks auto-skip. No re-entrancy.
        {
            var navMediaSetCecilType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavMediaSet");
            if (navMediaSetCecilType != null)
            {
                var patchTypeMi = typeof(AlRunner.Patches.MediaSetPatches);
                int mediaSetRewrote = 0;

                // ALInsert(DataError errorLevel, Guid mediaId) → bool
                var mInsert = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "ALInsert" && m.HasBody && m.Parameters.Count == 2);
                if (mInsert != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_ALInsert), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mInsert, h);
                    mediaSetRewrote++;
                }
                else
                {
                    Console.Error.WriteLine("[Cecil] WARNING: NavMediaSet.ALInsert(DataError, Guid) not found — hook not installed");
                }

                // ALRemove(DataError errorLevel, Guid mediaId) → bool
                var mRemove = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "ALRemove" && m.HasBody && m.Parameters.Count == 2);
                if (mRemove != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_ALRemove), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mRemove, h);
                    mediaSetRewrote++;
                }
                else
                {
                    Console.Error.WriteLine("[Cecil] WARNING: NavMediaSet.ALRemove(DataError, Guid) not found — hook not installed");
                }

                // get_ALCount() → int
                var mCount = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "get_ALCount" && m.HasBody && m.Parameters.Count == 0);
                if (mCount != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_get_ALCount), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mCount, h);
                    mediaSetRewrote++;
                }
                else
                {
                    Console.Error.WriteLine("[Cecil] WARNING: NavMediaSet.get_ALCount() not found — hook not installed");
                }

                // ALItem(int index) → Guid
                var mItem = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "ALItem" && m.HasBody && m.Parameters.Count == 1);
                if (mItem != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_ALItem), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mItem, h);
                    mediaSetRewrote++;
                }
                else
                {
                    Console.Error.WriteLine("[Cecil] WARNING: NavMediaSet.ALItem(int) not found — hook not installed");
                }

                // ALImport overloads — file-based (second param is string fileName)
                {
                    int fileImportRewrote = 0;
                    foreach (var m in navMediaSetCecilType.Methods.Where(m2 => m2.Name == "ALImport" && m2.HasBody))
                    {
                        var ps = m.Parameters;
                        if (ps.Count < 3 || ps[1].ParameterType.FullName != "System.String") continue;
                        var replName = ps.Count == 3
                            ? nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_ALImport_File2)
                            : nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_ALImport_File3);
                        var h = patchTypeMi.GetMethod(replName, BindingFlags.Public | BindingFlags.Static);
                        if (h != null) { ReplaceBodyWithHelper(asm.MainModule, m, h); mediaSetRewrote++; fileImportRewrote++; }
                    }
                    if (fileImportRewrote == 0)
                        Console.Error.WriteLine("[Cecil] WARNING: NavMediaSet.ALImport(DataError, string, string[, string]) file overloads not found — hook not installed");
                }

                // ALExport(DataError, string fileBaseName) → int
                var mExport = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "ALExport" && m.HasBody && m.Parameters.Count == 2);
                if (mExport != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_ALExport), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mExport, h);
                    mediaSetRewrote++;
                }
                else
                {
                    Console.Error.WriteLine("[Cecil] WARNING: NavMediaSet.ALExport(DataError, string) not found — hook not installed");
                }

                // AddMediaToSetAsync(NavSession, Guid setId, Guid mediaId) → ValueTask<Guid>  (BC 28+)
                // AddMediaToSet(Guid setId, Guid mediaId) → Guid                     (BC 27.x, synchronous)
                //
                // The shared internal helper every AL-facing import path (ImportStream's
                // NavStream-based ALImport overloads chief among them — see #1773 / the
                // MediaSetPatches file header) funnels through to add a media id to the set.
                // Its real body reaches an undeclared "Media Set" platform table via
                // NavSession.GetGlobalRecordInstance + ALGetAsync/ALInsertAsync, silently
                // discarding the insert's success/failure — so the membership never lands
                // anywhere our ALInsert/get_ALCount/ALItem patches above can see, even
                // though the real body's earlier content-storage step (into the ALREADY
                // real/declared Media/TenantMedia tables) succeeds.
                //
                // BC 27.x has NO async surface on NavMediaSet at all — the whole class is
                // synchronous there (issue #1802, confirmed by decompiling
                // Microsoft.Dynamics.Nav.Ncl.dll from the 27.0.38460.53552 and
                // 28.1.49838.50794 cached service tiers: same logic, `AddMediaToSet(Guid,
                // Guid) -> Guid`, no NavSession parameter). This is a genuine
                // version-conditional pair, not an optional hook: if NEITHER shape
                // resolves, every MediaSet membership operation silently degrades to
                // Count()==0 with no error (see #1802) — a wrong answer, not a missing
                // feature. Per loud-failures.md, an unknown BC shape here must hard-error
                // the whole run rather than let that happen again.
                // Resolution + the hard-error-on-neither-shape is extracted to
                // ResolveMediaSetAddToSetTarget (below) so it's independently testable —
                // see MediaSetAddToSetResolutionTests.cs.
                var mAddToSetTarget = ResolveMediaSetAddToSetTarget(navMediaSetCecilType);
                var addToSetHelperName = mAddToSetTarget.Name == "AddMediaToSetAsync"
                    ? nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_AddMediaToSetAsync)
                    : nameof(AlRunner.Patches.MediaSetPatches.NavMediaSet_AddMediaToSet);
                {
                    var h = patchTypeMi.GetMethod(addToSetHelperName, BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mAddToSetTarget, h);
                    mediaSetRewrote++;
                }

                Console.Error.WriteLine($"[Cecil] Batch 5: rewrote {mediaSetRewrote} NavMediaSet method(s) → MediaSetPatches helpers");
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARNING: NavMediaSet not found in Ncl — batch 5 MediaSet skipped");
            }
        }

        // ── NavDialog.ALStrMenu / ALConfirm: BC's own bodies, deliberately ─────────────
        //
        // These were rewritten to constants — ALConfirm returned false, ALStrMenu returned
        // its default. That is a silent fake of the kind loud-failures.md forbids, and it
        // made a test lie rather than fail: the al-language corpus test
        // Confirm_Question_HandlerRepliesFalse asserts Confirm() is false, and passed because
        // the constant happened to match, while its declared [ConfirmHandler] never ran.
        //
        // BC's real bodies already do the right thing:
        //     if (session.TestExecution.TestHandleConfirm(message, ref confirmation)) return confirmation;
        //     return session.ClientCallback.DialogConfirm(...);
        // The first line IS the handler dispatch an AL test declares with [HandlerFunctions];
        // the second refuses loudly when no handler was declared, which is exactly the error
        // real BC raises for an unhandled dialog. Replacing the body removed both.


    }

    private static void AddMediaOwned(HashSet<string> set)
    {
        // NavDialog.ALStrMenu / ALConfirm are NO LONGER owned here — BC's own bodies
        // dispatch to session.TestExecution.TestHandleConfirm / TestHandleStrMenu, which is
        // precisely the [ConfirmHandler] / [StrMenuHandler] routing an AL test declares.
        // See the removed Batch 5 block below for what replacing them cost.
        // NavDialog.ALOpenAsync<T> / ALUpdateAsync (Batch 5 — headless progress-dialog no-op).
        // Instance methods; Key arity excludes `this`. Owned by the Cecil body rewrite so the
        // legacy ALUpdateAsync JmpHook block in BcRuntime auto-skips (no coexistence spin).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavDialog::ALOpenAsync/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavDialog::ALUpdateAsync/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavDialog::ALUpdateAsync/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavDialog::ALUpdateAsync/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavDialog::ALClose/0");
        // NavMediaSet / NavMediaValueBase (Batch 5 — in-memory shim; no re-entrancy).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALInsert/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALRemove/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMediaSet::get_ALCount/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALItem/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALImport/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALImport/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALExport/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMediaValueBase::get_ALMediaId/0");
    }

}
