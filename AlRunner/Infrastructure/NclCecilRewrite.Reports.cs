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
    private static void RewriteNcl_Reports(AssemblyDefinition asm, MethodReference oosCtor)
    {
        {
            var navReportT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavReport");
            var ioeCtor = asm.MainModule.ImportReference(
                typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
            var syncRunRequestPageRef = asm.MainModule.ImportReference(
                typeof(AlRunner.NavReportSync).GetMethod(
                    nameof(AlRunner.NavReportSync.SyncRunRequestPage),
                    new[] { typeof(object), typeof(int), typeof(string) })
                ?? throw new InvalidOperationException(
                    "NavReportSync.SyncRunRequestPage(object,int,string) not found — do not commit"));
            var syncStaticRunRef = asm.MainModule.ImportReference(
                typeof(AlRunner.NavReportSync).GetMethod(
                    nameof(AlRunner.NavReportSync.SyncStaticRun),
                    new[] { typeof(int), typeof(bool), typeof(bool), typeof(object) })
                ?? throw new InvalidOperationException(
                    "NavReportSync.SyncStaticRun(int,bool,bool,object) not found — do not commit"));
            // NavNCLDialogException is the AL Error() carrier; ctor takes (PrivacyClassification, string).
            // Resolving cross-assembly type refs here is brittle (Diagnostic enum lives in
            // Microsoft.Dynamics.Nav.Diagnostic.dll) — InvalidOperationException is caught by AL
            // `asserterror` just as well (verified on the NavQuery suite). Use it.
            int reportRewrites = 0;
            if (navReportT != null)
            {
                foreach (var method in navReportT.Methods.ToList())
                {
                    if (!method.HasBody) continue;
                    var ps = method.Parameters;

                    // NavReport.Add(DataItem, string) — overrides DataItemIterator.Add.
                    // The override derefs base.Metadata.DataItems[...] / GetDataItemByName,
                    // which NRE because Metadata is null (we no-op BeginInitialization).
                    // The override's only purpose is to populate dataItem.MetaData and
                    // process column option captions — neither is observable by AL
                    // tests in the runner. Forward straight to base.Add (which just
                    // appends to the dataItems list).
                    // NavReport.Add(DataItem, string) — overrides DataItemIterator.Add.
                    // Route to NavReportSync.ReportAdd: with REAL metadata (emit-captured
                    // MetaReport) it binds DataItem.MetaData/PrintOnlyIfDetail/AutoCalc
                    // faithfully; with the legacy stub it degrades to a plain list append
                    // (the old base.Add-forward behavior).
                    if (method.Name == "Add"
                        && ps.Count == 2
                        && ps[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.DataItem"
                        && ps[1].ParameterType.FullName == "System.String"
                        && method.ReturnType.FullName == "System.Void"
                        && method.IsVirtual && !method.IsNewSlot)
                    {
                        var reportAddInfo = typeof(AlRunner.NavReportSync).GetMethod("ReportAdd",
                            BindingFlags.Static | BindingFlags.Public)
                            ?? throw new InvalidOperationException("NavReportSync.ReportAdd not found via reflection");
                        var reportAddRef = asm.MainModule.ImportReference(reportAddInfo);
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Ldarg_1));
                        il.Append(il.Create(OpCodes.Ldarg_2));
                        il.Append(il.Create(OpCodes.Call, reportAddRef));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 3;
                        reportRewrites++;
                        continue;
                    }

                    // BeginInitialization (sync, void, 0-arg) —
                    // The real body sync-over-asyncs into BeginInitializationAsync
                    // which dereferences base.Tree.Session.MetadataProvider (null
                    // on the skeleton Session) to populate base.Metadata. We
                    // instead route to NavReportSync.StubInitializeMetadata which
                    // installs an uninitialized MetaReport whose `masterPage`
                    // field points at an empty MasterPage. That makes the
                    // BC-emitted Report{N}.InitializeComponent tail line
                    // `RequestOptionsPage = new RequestPage(this, Metadata.RequestFormMetadata)`
                    // null-safe (RequestFormMetadata calls EnsureMasterPageLoaded
                    // → CreateMasterPage which early-returns when masterPage is
                    // already non-null), so IC runs to completion and the
                    // DataItems list populates.
                    //
                    // EndInitialization remains a plain `ret` — the real body
                    // also sync-over-asyncs and runs metadata-bound side
                    // effects (DefaultPaperSourceKindRaw, PreviewMode,
                    // UseRequestForm, OnInitReport via EndInitializationAsync)
                    // that are not AL-observable. OnInitReport is fired
                    // explicitly by NavReportSync.SyncRun.
                    if (method.Name == "BeginInitialization"
                        && ps.Count == 0
                        && method.ReturnType.FullName == "System.Void")
                    {
                        var stubInfo = typeof(AlRunner.NavReportSync).GetMethod("StubInitializeMetadata",
                            BindingFlags.Static | BindingFlags.Public)
                            ?? throw new InvalidOperationException("NavReportSync.StubInitializeMetadata not found via reflection");
                        var stubRef = asm.MainModule.ImportReference(stubInfo);
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Call, stubRef));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 1;
                        reportRewrites++;
                        continue;
                    }

                    if (method.Name == "EndInitialization"
                        && ps.Count == 0
                        && method.ReturnType.FullName == "System.Void")
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 0;
                        reportRewrites++;
                        continue;
                    }

                    // Instance Run() / RunModal() — void. We Cecil-rewrite the
                    // body to call NavReportSync.SyncRun(this) directly. (The
                    // previous JmpHook-based approach proved unreliable on the
                    // tiny Cecil-rewritten body — the JIT inlined the `ret` and
                    // the entry-point trampoline never fired. Cecil-emitted
                    // managed call gets full JIT integration.)
                    if ((method.Name == "Run" || method.Name == "RunModal")
                        && !method.IsStatic
                        && method.Parameters.Count == 0
                        && method.ReturnType.FullName == "System.Void")
                    {
                        var syncRunInfo = typeof(AlRunner.NavReportSync).GetMethod("SyncRun",
                            BindingFlags.Static | BindingFlags.Public)
                            ?? throw new InvalidOperationException("NavReportSync.SyncRun not found via reflection");
                        var syncRunRef = asm.MainModule.ImportReference(syncRunInfo);
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Call, syncRunRef));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 1;
                        reportRewrites++;
                    }
                    // Static Run(id[, requestWindow[, systemPrinter[, record]]]) /
                    // RunModal(same shapes) → NavReportSync.SyncStaticRun(id, requestWindow,
                    // systemPrinter, record). #1771: these bodies used to be blanked to a bare
                    // `ret`, with a separate JmpHook in ReportPatches.cs throwing an OOS
                    // InvalidOperationException on top. That JmpHook never actually fired
                    // under the default Cecil-only runtime (JmpHook.Apply silently skips
                    // methods it doesn't own unless AL_RUNNER_ENABLE_JMPHOOK=1), so the call
                    // fell straight through the `ret` — a silent no-op, not the intended
                    // loud OOS throw. Cecil-own the body directly (like the instance
                    // Run/RunModal rewrite above) so the redirect is real IL, not a hook that
                    // can silently fail to bind. Missing trailing args get BC's own
                    // documented defaults (RequestWindow=true, SystemPrinter=false) — inert
                    // today since SyncStaticRun does not raise a dialog, but correct in case a
                    // future implementation reads them.
                    //
                    // The one shape NOT handled here is the ReportRunOptions overload
                    // (Run(ReportRunOptions) only — RunModal has no such overload): its single
                    // parameter isn't `int`, so it falls through to the "unrecognised shape"
                    // branch below and throws loud OOS instead of silently no-op'ing, exactly
                    // like RunRequestPage's unknown-shape branch.
                    else if ((method.Name == "Run" || method.Name == "RunModal")
                        && method.IsStatic
                        && method.ReturnType.FullName == "System.Void")
                    {
                        var sps = method.Parameters;
                        bool known = sps.Count >= 1 && sps.Count <= 4
                            && sps[0].ParameterType.FullName == "System.Int32"
                            && (sps.Count < 2 || sps[1].ParameterType.FullName == "System.Boolean")
                            && (sps.Count < 3 || sps[2].ParameterType.FullName == "System.Boolean")
                            && (sps.Count < 4 || !sps[3].ParameterType.IsValueType);

                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();

                        if (!known)
                        {
                            il.Append(il.Create(OpCodes.Ldstr,
                                $"out-of-scope: static NavReport.{method.Name} (unrecognised overload shape)"));
                            il.Append(il.Create(OpCodes.Newobj, ioeCtor));
                            il.Append(il.Create(OpCodes.Throw));
                            body.MaxStackSize = 1;
                        }
                        else
                        {
                            il.Append(il.Create(OpCodes.Ldarg_0)); // reportId
                            il.Append(sps.Count >= 2 ? il.Create(OpCodes.Ldarg_1) : il.Create(OpCodes.Ldc_I4_1)); // requestWindow (BC default: true)
                            il.Append(sps.Count >= 3 ? il.Create(OpCodes.Ldarg_2) : il.Create(OpCodes.Ldc_I4_0)); // systemPrinter (BC default: false)
                            il.Append(sps.Count >= 4 ? il.Create(OpCodes.Ldarg_3) : il.Create(OpCodes.Ldnull));   // record (no filter)
                            il.Append(il.Create(OpCodes.Call, syncStaticRunRef));
                            il.Append(il.Create(OpCodes.Ret));
                            body.MaxStackSize = 4;
                        }
                        reportRewrites++;
                    }
                    // RunRequestPage (any sync overload returning string) →
                    // NavReportSync.SyncRunRequestPage(selfOrNull, reportId, parameters).
                    //
                    // This used to throw out-of-scope on the grounds that a request page needs
                    // a client to draw it. Under test it does not: BC dispatches it to the
                    // test's own [RequestPageHandler] via TestHandleModalForm and renders
                    // nothing. AL calling RunRequestPage to capture a report's
                    // RequestPageParameters XML is ordinary in-scope AL, and refusing it left
                    // the declared handler unexecuted — which is itself a failure by BC's own
                    // unexecuted-handler check.
                    //
                    // Four shapes exist; each is mapped to the one managed entry point:
                    //   static   (int)            -> (null, arg0, null)
                    //   static   (int, string)    -> (null, arg0, arg1)
                    //   instance ()               -> (this, 0,    null)
                    //   instance (string)         -> (this, 0,    arg0)
                    else if (method.Name == "RunRequestPage"
                        && method.ReturnType.FullName == "System.String")
                    {
                        var rpParams = method.Parameters;
                        bool isStatic = method.IsStatic;
                        // Only the shapes above are understood. Anything else keeps the old
                        // loud refusal rather than being silently mis-wired.
                        bool known =
                            (isStatic && rpParams.Count == 1 && rpParams[0].ParameterType.FullName == "System.Int32")
                            || (isStatic && rpParams.Count == 2 && rpParams[0].ParameterType.FullName == "System.Int32"
                                && rpParams[1].ParameterType.FullName == "System.String")
                            || (!isStatic && rpParams.Count == 0)
                            || (!isStatic && rpParams.Count == 1 && rpParams[0].ParameterType.FullName == "System.String");

                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();

                        if (!known)
                        {
                            il.Append(il.Create(OpCodes.Ldstr,
                                "out-of-scope: NavReport.RunRequestPage (unrecognised overload shape)"));
                            il.Append(il.Create(OpCodes.Newobj, ioeCtor));
                            il.Append(il.Create(OpCodes.Throw));
                            body.MaxStackSize = 1;
                        }
                        else
                        {
                            // arg 1: the report instance, or null for the static overloads
                            if (isStatic) il.Append(il.Create(OpCodes.Ldnull));
                            else il.Append(il.Create(OpCodes.Ldarg_0));
                            // arg 2: the report id (0 for the instance overloads — the
                            // instance already IS the report, so no lookup is needed)
                            if (isStatic) il.Append(il.Create(OpCodes.Ldarg_0));
                            else il.Append(il.Create(OpCodes.Ldc_I4_0));
                            // arg 3: the parameters string, when the overload carries one
                            if (isStatic && rpParams.Count == 2) il.Append(il.Create(OpCodes.Ldarg_1));
                            else if (!isStatic && rpParams.Count == 1) il.Append(il.Create(OpCodes.Ldarg_1));
                            else il.Append(il.Create(OpCodes.Ldnull));
                            il.Append(il.Create(OpCodes.Call, syncRunRequestPageRef));
                            il.Append(il.Create(OpCodes.Ret));
                            body.MaxStackSize = 3;
                        }
                        reportRewrites++;
                    }
                    // SaveAs* sync wrappers now call through to the real SaveAsAsync
                    // chain (in-process dataset execution). The OOS boundary lives at
                    // the ReportResultSetProcessorFactory fork — external processors
                    // (RDLC/Word/Excel render, print server, document service) throw
                    // there; Xml dataset + application-handled custom layouts run.
                }
            }
            // DataItemIterator.SetTableView(NavRecord) keeps BC'S OWN BODY.
            //
            // It used to be blanked to nothing but `SetTableViewUsed = true`, with a TODO
            // saying "filter is not yet applied to the source". That TODO was the whole
            // defect: SetTableView is how Report.SaveAs(…, recordRef) — the overload every
            // "print this one document" path in AL uses — gets its record filter onto the
            // matching data item. Dropping it made a document report either refuse to run
            // ("You must specify one or more filters to avoid accidentally printing all
            // documents", which is what report 1306 raises) or silently render every row in
            // the table instead of the one that was asked for.
            //
            // BC's own body only touches DataItemIterator state the runner already builds
            // (dataItems, TableViewRecord, TableViewIsSet), so there is nothing to stand in
            // for — the original is both correct and sufficient.
            Console.Error.WriteLine($"[Cecil] Rewrote {reportRewrites} NavReport/DataItemIterator method(s) (Run/RunModal→SyncRun; Add→ReportAdd; RunRequestPage→OOS-throw)");
        }

        // NavXmlPort static Run(id[, requestWindow[, import[, record]]]) — #1800.
        //
        // These four overloads are a genuine, permanent out-of-scope surface
        // (docs/scope.md#file-storage — the same "browser round-trip" bucket as
        // NavFile.ALUpload/ALDownload, see FilePatches.cs): decompiling BC's real, unpatched
        // Ncl.dll body shows every overload's RunXmlPort() unconditionally calls
        // NavFile.InternalUpload/InternalDownload with displayDialog:true, which resolves to
        // Session.ClientCallback.UploadFileAction/DownloadFileAction — a client callback the
        // runner's non-interactive skeleton session cannot satisfy. Cecil-own them to our
        // typed OOS throw instead of a JmpHook registration that can silently fail to bind.
        //
        // The instance Export/Import/Run/RunXmlPort/SetTableView/BeginInitialization/
        // EndInitialization/Add(*Node) methods in the same cluster are the OPPOSITE case — BC's
        // real body is already correct there, nothing to redirect to — and are deliberately NOT
        // Cecil-owned. Full decompiled-source evidence and the misdiagnosis-and-correction
        // record for those eight already-correct methods live once, canonically, in the big
        // comment block above NavXmlPort_StaticRun1..4 in AlRunner/Patches/XmlPortPatches.cs.
        // See also tests/runner-extras/standalone-suites/xmlport-cluster-hooks-1800 for the
        // RED→GREEN proof.
        {
            var navXmlPortT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavXmlPort");
            int xmlPortRewrites = 0;
            const string NavRecordName = "Microsoft.Dynamics.Nav.Runtime.NavRecord";

            // Static XMLPORT.RUN(id[, requestWindow[, import[, record]]]) — 4 overloads, all
            // redirected to a typed OOS throw (see the comment above this block).
            if (navXmlPortT != null)
            {
                void RedirectStaticRun(TypeReference[] sig, string replName)
                {
                    var m = navXmlPortT.Methods.FirstOrDefault(mm =>
                        mm.Name == "Run" && mm.IsStatic
                        && mm.Parameters.Count == sig.Length
                        && mm.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(sig.Select(s => s.FullName)));
                    if (m == null || !m.HasBody) return;

                    var replParamTypes = sig.Select(s => s.FullName switch
                    {
                        "System.Int32" => typeof(int),
                        "System.Boolean" => typeof(bool),
                        _ => typeof(object),
                    }).ToArray();
                    var replInfo = typeof(AlRunner.BcRuntime).GetMethod(replName,
                        BindingFlags.Static | BindingFlags.Public, null, replParamTypes, null)
                        ?? throw new InvalidOperationException($"BcRuntime.{replName} not found via reflection — do not commit");
                    var replRef = asm.MainModule.ImportReference(replInfo);

                    var body = m.Body;
                    body.Instructions.Clear();
                    body.ExceptionHandlers.Clear();
                    body.Variables.Clear();
                    var il = body.GetILProcessor();
                    for (int i = 0; i < sig.Length; i++)
                        il.Append(il.Create(OpCodes.Ldarg, i));
                    il.Append(il.Create(OpCodes.Call, replRef));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = Math.Max(1, sig.Length);
                    xmlPortRewrites++;
                }

                var tInt32 = asm.MainModule.ImportReference(typeof(int));
                var tBool = asm.MainModule.ImportReference(typeof(bool));
                // NavRecord is defined IN Ncl.dll itself (Runtime namespace, unlike DataError
                // above), so — unlike the FindType/MainModule.Types trap that silently dropped
                // Export/Import — resolving it via MainModule.Types here is safe and correct.
                var navRecordT = asm.MainModule.Types.FirstOrDefault(t => t.FullName == NavRecordName);
                var refNavRecordForStatic = navRecordT != null ? asm.MainModule.ImportReference(navRecordT) : null;
                RedirectStaticRun(new[] { tInt32 }, nameof(AlRunner.BcRuntime.NavXmlPort_StaticRun1));
                RedirectStaticRun(new[] { tInt32, tBool }, nameof(AlRunner.BcRuntime.NavXmlPort_StaticRun2));
                RedirectStaticRun(new[] { tInt32, tBool, tBool }, nameof(AlRunner.BcRuntime.NavXmlPort_StaticRun3));
                if (refNavRecordForStatic != null)
                    RedirectStaticRun(new[] { tInt32, tBool, tBool, refNavRecordForStatic }, nameof(AlRunner.BcRuntime.NavXmlPort_StaticRun4));
            }

            Console.Error.WriteLine($"[Cecil] Rewrote {xmlPortRewrites} NavXmlPort static Run overload(s) to OOS-throw (ctor-scaffolding and instance Export/Import/Run/SetTableView left to BC's real, unpatched body)");
        }

        // §report-processor-factory — the TRUE out-of-scope boundary for report
        // rendering. SaveAs/Run execute the real dataset chain in-process; only
        // the processors that need a genuinely external surface throw:
        //   Rdlc / Word / Excel render        → report-rendering-external
        //   ReportServerResultSetProcessor    → printing
        //   ReportResultSetDocumentServiceDecorator → document-service
        // Xml dataset (ReportProcessorXmlGenerator) and the application/custom
        // merger (ReportProcessorCustomGenerator → OnCustomDocumentMergerEx) are
        // IN scope and left untouched.
        {
            var factoryT = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.Report.ReportResultSetProcessorFactory")
                ?? throw new InvalidOperationException("ReportResultSetProcessorFactory not found in Ncl.dll — Ncl shape changed; do not commit");
            // `oosCtor` (corlib InvalidOperationException(string), already imported by
            // the enclosing method's earlier rewrites) is reused — token-shift rule:
            // no NEW memberRefs beyond what the rewrite pass already introduces.
            void ThrowBody(Mono.Cecil.MethodDefinition m, string msg)
            {
                var body = m.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldstr, msg));
                il.Append(il.Create(OpCodes.Newobj, oosCtor));
                il.Append(il.Create(OpCodes.Throw));
                body.MaxStackSize = 1;
            }
            int factoryRewrites = 0;
            foreach (var m in factoryT.Methods.Where(mm => mm.HasBody).ToList())
            {
                // Message shape is the documented convention
                //     out-of-scope: <api> — <reason> — see docs/scope.md#<anchor>
                // (Infrastructure.OutOfScopeMessage). The <api> slot must name the
                // BC API that was touched and the <reason> slot must LEAD with the
                // scope.md anchor, because tests/expectations/ matches expect-oos
                // entries on that anchor (#1743). Free-text detail goes after a
                // further em-dash.
                string? reason = m.Name switch
                {
                    "GetRdlcResultSetProcessor" =>
                        "out-of-scope: ReportResultSetProcessorFactory.GetRdlcResultSetProcessor — report-rendering-external — RDLC layout processing requires an external renderer — see docs/scope.md#report-rendering",
                    "GetWordResultSetProcessor" =>
                        "out-of-scope: ReportResultSetProcessorFactory.GetWordResultSetProcessor — report-rendering-external — Word layout merge (Aspose) requires an external renderer — see docs/scope.md#report-rendering",
                    "GetExcelResultSetProcessor" =>
                        "out-of-scope: ReportResultSetProcessorFactory.GetExcelResultSetProcessor — report-rendering-external — Excel layout rendering (Aspose) requires an external renderer — see docs/scope.md#report-rendering",
                    "GetExcelDatasetResultSetProcessor" =>
                        "out-of-scope: ReportResultSetProcessorFactory.GetExcelDatasetResultSetProcessor — report-rendering-external — Excel dataset rendering (Aspose) requires an external renderer — see docs/scope.md#report-rendering",
                    _ => null,
                };
                if (reason == null) continue;
                ThrowBody(m, reason);
                factoryRewrites++;
            }
            if (factoryRewrites != 4)
                throw new InvalidOperationException(
                    $"ReportResultSetProcessorFactory external-processor rewrite count changed (got {factoryRewrites}, want 4) — Ncl shape changed; do not commit");

            var printProcT = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ReportServerResultSetProcessor");
            int printCtorRewrites = 0;
            if (printProcT != null)
            {
                foreach (var ctor in printProcT.Methods.Where(mm => mm.IsConstructor && !mm.IsStatic && mm.HasBody).ToList())
                {
                    ThrowBody(ctor,
                        "out-of-scope: ReportServerResultSetProcessor..ctor — printing — physical/print-server printing requires an external print service — see docs/scope.md#report-rendering");
                    printCtorRewrites++;
                }
            }
            var docSvcT = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.Report.ReportResultSetDocumentServiceDecorator");
            int docSvcCtorRewrites = 0;
            if (docSvcT != null)
            {
                foreach (var ctor in docSvcT.Methods.Where(mm => mm.IsConstructor && !mm.IsStatic && mm.HasBody).ToList())
                {
                    ThrowBody(ctor,
                        "out-of-scope: ReportResultSetDocumentServiceDecorator..ctor — document-service — document-service upload requires an external service — see docs/scope.md#report-rendering");
                    docSvcCtorRewrites++;
                }
            }
            Console.Error.WriteLine($"[Cecil] Report OOS boundary → processor factory ({factoryRewrites} external getters, {printCtorRewrites} print-processor ctors, {docSvcCtorRewrites} doc-service ctors throw)");
        }

        // §report-layout-selection — layout-resolution NRE peel for the non-XML
        // SaveAs/Run path. In a live tier the layout is resolved from the
        // tenant/user layout-selection virtual tables (2000000233 / 2000000234 /
        // 2000000231) keyed by the report's OwningApp package id. A
        // runner-compiled report has no OwningApp, so those app-package lookups
        // yield nothing AND opening the virtual tables NREs in the NavRecord ctor
        // (GetMetaTableById → null → metaTable.TableType). The faithful answer for
        // the standalone runner: no persisted layout selections exist, and the
        // report's own requested layout format drives the processor fork. We
        //   (a) short-circuit GetLayoutSelections → empty list (no selection rows);
        //   (b) rewrite TryGetSelectedLayoutOrDefault so a concrete requested
        //       format yields a built-in default ReportLayout of that Format with
        //       empty media — the processor factory dispatches on Format, and the
        //       RDLC/Word/Excel getters throw report-rendering-external before
        //       touching the bytes, while the Xml/Custom processors stay in scope.
        //       A None request returns false (caller throws NoLayout, unchanged).
        // Both rewrites reuse operands already present in their target bodies
        // (List<LayoutSelection>..ctor, ReportLayout..ctor(5), Array.Empty<byte>()),
        // so no new member tokens are introduced (token-shift rule respected).
        // Corpus-safe: this path currently NREs, and the corpus gate has 0 errors,
        // so no corpus test reaches it today.
        {
            var rlsT = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ReportLayoutSelection")
                ?? throw new InvalidOperationException("ReportLayoutSelection not found in Ncl.dll — Ncl shape changed; do not commit");

            // (a) GetLayoutSelections(session, reportId, companyName) → new List<LayoutSelection>(0)
            var getSel = rlsT.Methods.FirstOrDefault(m => m.Name == "GetLayoutSelections" && m.HasBody)
                ?? throw new InvalidOperationException("ReportLayoutSelection.GetLayoutSelections not found — Ncl shape changed; do not commit");
            var listCtor = getSel.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Newobj)
                .Select(i => i.Operand as MethodReference)
                .FirstOrDefault(mr => mr != null && mr.Name == ".ctor" && mr.DeclaringType.Name == "List`1" && mr.Parameters.Count == 1)
                ?? throw new InvalidOperationException("List<LayoutSelection>..ctor(int) operand not found in GetLayoutSelections — Ncl shape changed; do not commit");
            {
                var body = getSel.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Newobj, listCtor));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
            }

            // (b) TryGetSelectedLayoutOrDefault(session, reportId, reportModel, out layout)
            var trySel = rlsT.Methods.FirstOrDefault(m => m.Name == "TryGetSelectedLayoutOrDefault" && m.HasBody && m.Parameters.Count == 4)
                ?? throw new InvalidOperationException("ReportLayoutSelection.TryGetSelectedLayoutOrDefault not found — Ncl shape changed; do not commit");
            var layoutCtor = trySel.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Newobj)
                .Select(i => i.Operand as MethodReference)
                .FirstOrDefault(mr => mr != null && mr.DeclaringType.Name == "ReportLayout" && mr.Parameters.Count == 5)
                ?? throw new InvalidOperationException("ReportLayout..ctor(5) operand not found in TryGetSelectedLayoutOrDefault — Ncl shape changed; do not commit");
            var arrEmptyByte = trySel.Body.Instructions
                .Select(i => i.Operand as MethodReference)
                .FirstOrDefault(mr => mr != null && mr.Name == "Empty" && mr.DeclaringType.Name == "Array")
                ?? throw new InvalidOperationException("Array.Empty<byte>() operand not found in TryGetSelectedLayoutOrDefault — Ncl shape changed; do not commit");
            // Resolve the render format via the runner: honour a concrete requested
            // model, else read the report's REAL DefaultLayout (Custom vs RDLC/Word/
            // Excel). The NCL skeleton only carries the enum default (RDLC) so an
            // in-body GetMetaReportById(reportId).DefaultLayout would misclassify
            // Custom-layout reports as RDLC → wrongly out-of-scope.
            var resolveMi = typeof(AlRunner.NavReportSync).GetMethod(
                nameof(AlRunner.NavReportSync.ResolveDefaultReportModel),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("NavReportSync.ResolveDefaultReportModel not found");
            var resolveRef = asm.MainModule.ImportReference(resolveMi);
            // ReportModel.None == 100 (verified against the RDLC-fallback compare in
            // this same body: `ldarg.2; ldc.i4.s 100; bne.un`).
            const int ReportModelNone = 100;
            var reportModelTr = layoutCtor.Parameters[3].ParameterType;
            {
                var body = trySel.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var fmtVar = new Mono.Cecil.Cil.VariableDefinition(reportModelTr);
                body.Variables.Add(fmtVar);
                var il = body.GetILProcessor();
                // fmt = NavReportSync.ResolveDefaultReportModel(reportId, reportModel);
                il.Append(il.Create(OpCodes.Ldarg_1));         // reportId
                il.Append(il.Create(OpCodes.Ldarg_2));         // reportModel (requested)
                il.Append(il.Create(OpCodes.Call, resolveRef));
                il.Append(il.Create(OpCodes.Stloc, fmtVar));
                // if (fmt == None) { *layout = null; return false; }
                var build = il.Create(OpCodes.Ldarg_3);
                il.Append(il.Create(OpCodes.Ldloc, fmtVar));
                il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)ReportModelNone));
                il.Append(il.Create(OpCodes.Bne_Un_S, build));
                il.Append(il.Create(OpCodes.Ldarg_3));
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Stind_Ref));
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ret));
                // build: *layout = new ReportLayout(session, reportId, "DEFAULT", fmt, Array.Empty<byte>()); return true;
                il.Append(build);                              // ldarg.3 — address for stind.ref
                il.Append(il.Create(OpCodes.Ldarg_0));         // session
                il.Append(il.Create(OpCodes.Ldarg_1));         // reportId
                il.Append(il.Create(OpCodes.Ldstr, "DEFAULT"));
                il.Append(il.Create(OpCodes.Ldloc, fmtVar));   // fmt → Format
                il.Append(il.Create(OpCodes.Call, arrEmptyByte));
                il.Append(il.Create(OpCodes.Newobj, layoutCtor));
                il.Append(il.Create(OpCodes.Stind_Ref));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 6;
            }
            Console.Error.WriteLine("[Cecil] Rewrote ReportLayoutSelection.GetLayoutSelections → empty; TryGetSelectedLayoutOrDefault → NavReportSync.ResolveDefaultReportModel default-format layout (was: virtual-table 2000000233/234/231 NRE)");
        }

        // §report-layout-hydration — complete the layout objects the runner hands to
        // report rendering. A Type=Custom layout can never reach BC's own content lookup
        // (FetchLayoutFromApplication is gated on Format <= 1, i.e. RDLC/Word), and the
        // §report-layout-selection rewrite above synthesises a default layout with EMPTY
        // media, so the custom-render path feeds the AL document merger an empty template
        // and a payload whose layoutmimetype is "". See ReportLayoutHydration for the full
        // reasoning, including why this hooks ReportLayout itself rather than
        // ReportResultSet (they are different instances — measured).
        {
            var rlT = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.Report.ReportLayout")
                ?? throw new InvalidOperationException(
                    "[Cecil] Report.ReportLayout not found — Ncl shape changed; do not commit");

            void PrependProbe(string methodName, string helperName)
            {
                var m = rlT.Methods.FirstOrDefault(mm => mm.Name == methodName && mm.HasBody)
                    ?? throw new InvalidOperationException(
                        $"[Cecil] ReportLayout.{methodName} not found — Ncl shape changed; do not commit");
                var mi = typeof(AlRunner.Patches.ReportLayoutHydration).GetMethod(
                    helperName, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"[Cecil] ReportLayoutHydration.{helperName} not found");
                var b = m.Body;
                var il2 = b.GetILProcessor();
                var first2 = b.Instructions[0];
                il2.InsertBefore(first2, il2.Create(OpCodes.Ldarg_0));
                il2.InsertBefore(first2, il2.Create(OpCodes.Call, asm.MainModule.ImportReference(mi)));
                if (b.MaxStackSize < 1) b.MaxStackSize = 1;
            }

            PrependProbe("get_LayoutStream", nameof(AlRunner.Patches.ReportLayoutHydration.HydrateLayoutStream));
            PrependProbe("CalculateMimetype", nameof(AlRunner.Patches.ReportLayoutHydration.HydrateMimetype));
            Console.Error.WriteLine("[Cecil] Prepended layout hydration to ReportLayout.get_LayoutStream + CalculateMimetype");
        }

        // NavMethodScope.RegisterCancellationToken — root-scope early-return.
        // Report execution installs resource-governance cancellation handlers
        // (NavReportMaxDocumentCancellationHandler / NavResultSetEndOfRowCancellationHandler /
        // NavReportEndOfRowClientHandler) that call
        // session.CurrentMethodScope.RegisterCancellationToken(...). The runner runs
        // tests at the RootMethodScope, whose RegisterCancellationToken THROWS
        // ("Cancellation tokens cannot be registered on the root method scope."). The
        // runner enforces no MaxRows/MaxDocuments/Timeout resource limits, so skipping
        // registration is observably equivalent (the report runs to completion, never
        // cancelled). The handlers read scope.CancellationToken afterwards — on the root
        // scope that yields the never-cancelled root token, exactly what "no limits" means.
        // Prepend `if (this is RootMethodScope) return;` to both overloads.
        {
            var scopeT = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavMethodScope")
                ?? throw new InvalidOperationException("NavMethodScope not found in Ncl.dll — Ncl shape changed; do not commit");
            var rootScopeT = scopeT.NestedTypes.FirstOrDefault(t => t.Name == "RootMethodScope")
                ?? throw new InvalidOperationException("NavMethodScope/RootMethodScope not found in Ncl.dll — Ncl shape changed; do not commit");
            var rootScopeRef = asm.MainModule.ImportReference(rootScopeT);
            int regRewrites = 0;
            foreach (var m in scopeT.Methods.Where(mm => mm.Name == "RegisterCancellationToken" && mm.HasBody).ToList())
            {
                var first = m.Body.Instructions[0];
                var il = m.Body.GetILProcessor();
                //   ldarg.0
                //   isinst RootMethodScope
                //   brfalse.s <original first instr>
                //   ret
                var ldarg0 = il.Create(OpCodes.Ldarg_0);
                il.InsertBefore(first, ldarg0);
                il.InsertBefore(first, il.Create(OpCodes.Isinst, rootScopeRef));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                regRewrites++;
            }
            if (regRewrites == 0)
                throw new InvalidOperationException(
                    "NavMethodScope.RegisterCancellationToken not found — Ncl shape changed; do not commit");
            Console.Error.WriteLine($"[Cecil] Prepended root-scope early-return to {regRewrites} NavMethodScope.RegisterCancellationToken overload(s) (no resource governance in runner)");
        }

        // NCLMetaReport.CreateObjectInstance(ITreeObject, bool) — the skeleton
        // NCLMetaReport has no ApplicationObjectConstructor delegate (NREs).
        // Route to NavReportSync.CreateReportInstance which constructs the
        // compiled Report{id} directly and runs the same post-construction steps
        // (InitializeReportValues + FinalizeDataItemLoading).
        {
            var nclMetaReportT = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaReport")
                ?? throw new InvalidOperationException("NCLMetaReport not found in Ncl.dll — Ncl shape changed; do not commit");
            var createInst = nclMetaReportT.Methods.FirstOrDefault(m =>
                m.Name == "CreateObjectInstance" && m.HasBody && m.Parameters.Count == 2
                && m.Parameters[1].ParameterType.FullName == "System.Boolean")
                ?? throw new InvalidOperationException("NCLMetaReport.CreateObjectInstance(ITreeObject,bool) not found — Ncl shape changed; do not commit");
            var helperInfo = typeof(AlRunner.NavReportSync).GetMethod("CreateReportInstance",
                BindingFlags.Static | BindingFlags.Public)
                ?? throw new InvalidOperationException("NavReportSync.CreateReportInstance not found via reflection");
            var helperRef = asm.MainModule.ImportReference(helperInfo);
            var bodyCI = createInst.Body;
            bodyCI.Instructions.Clear();
            bodyCI.ExceptionHandlers.Clear();
            bodyCI.Variables.Clear();
            var ilCI = bodyCI.GetILProcessor();
            ilCI.Append(ilCI.Create(OpCodes.Ldarg_0));
            ilCI.Append(ilCI.Create(OpCodes.Ldarg_1));
            ilCI.Append(ilCI.Create(OpCodes.Ldarg_2));
            ilCI.Append(ilCI.Create(OpCodes.Call, helperRef));
            var navReportTypeDef = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavReport")
                ?? throw new InvalidOperationException("NavReport type not found — Ncl shape changed; do not commit");
            ilCI.Append(ilCI.Create(OpCodes.Castclass, navReportTypeDef));
            ilCI.Append(ilCI.Create(OpCodes.Ret));
            bodyCI.MaxStackSize = 3;
            Console.Error.WriteLine("[Cecil] Rewrote NCLMetaReport.CreateObjectInstance → NavReportSync.CreateReportInstance");
        }

        // RequestPageBase ctors — the 2-arg ctor (NavApplicationObjectBase, MasterPage)
        // chains `: this(parent, parent.Session.Company.SharedObjects, masterPage, null)`
        // which dereferences `parent.Session.Company.SharedObjects` — Session.Company may be
        // null on the runner skeleton. The 3-arg overload (NavApplicationObjectBase,
        // MasterPage, NCLStaticMetadata) has the same deref. Both are called from
        // BC-emitted Report{N}.RequestPage and Report{N}.RequestPage : RequestPageBase via
        // `: base(parent, metaForm)` in Report{N}.InitializeComponent. Rewrite them to
        // bypass the Session.Company.SharedObjects deref by calling NavForm 2-arg ctor
        // directly, which assigns masterPage and runs the rest of NavForm init using
        // `parent` (the report instance) as the ITreeObject. RequestPageBase.Parent is
        // left null — not observable by AL tests; if needed later, set it explicitly.
        {
            var requestPageBaseT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.RequestPageBase");
            var navFormT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavForm");
            if (requestPageBaseT != null && navFormT != null)
            {
                var navFormCtor2 = navFormT.Methods
                    .FirstOrDefault(m => m.IsConstructor
                        && m.Parameters.Count == 2
                        && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.ITreeObject"
                        && m.Parameters[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.Metadata.MasterPage");
                var navFormCtor3 = navFormT.Methods
                    .FirstOrDefault(m => m.IsConstructor
                        && m.Parameters.Count == 3
                        && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.ITreeObject"
                        && m.Parameters[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.Metadata.MasterPage"
                        && m.Parameters[2].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLStaticMetadata");

                int rpRewrites = 0;
                foreach (var ctor in requestPageBaseT.Methods.Where(m => m.IsConstructor && m.HasBody).ToList())
                {
                    var ps = ctor.Parameters;
                    // (NavApplicationObjectBase parent, MasterPage masterPage)
                    if (ps.Count == 2
                        && ps[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase"
                        && ps[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.Metadata.MasterPage"
                        && navFormCtor2 != null)
                    {
                        var body = ctor.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Ldarg_1));
                        il.Append(il.Create(OpCodes.Ldarg_2));
                        il.Append(il.Create(OpCodes.Call, navFormCtor2));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 3;
                        rpRewrites++;
                    }
                    // (NavApplicationObjectBase parent, MasterPage masterPage, NCLStaticMetadata staticMetadata)
                    else if (ps.Count == 3
                        && ps[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase"
                        && ps[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.Metadata.MasterPage"
                        && ps[2].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLStaticMetadata"
                        && navFormCtor3 != null)
                    {
                        var body = ctor.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Ldarg_1));
                        il.Append(il.Create(OpCodes.Ldarg_2));
                        il.Append(il.Create(OpCodes.Ldarg_3));
                        il.Append(il.Create(OpCodes.Call, navFormCtor3));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 4;
                        rpRewrites++;
                    }
                }
                Console.Error.WriteLine($"[Cecil] Rewrote {rpRewrites} RequestPageBase ctor(s) → skip Session.Company.SharedObjects deref, call NavForm ctor directly");
            }
        }

        // NavForm 5-arg PRIVATE ctor — final stop of the RequestPageBase → NavForm 2-arg → NavForm 5-arg
        // chain. The real body derefs `base.Session.NavAppGroup` (NavExtensionMetricsFormatter ctor on
        // line 42099) which NREs because NavAppGroup is unset on the skeleton session. Also calls
        // NavCurrentThread.DrillDownPersonalizationId / FormPersonalizationId statics, sets formId
        // from base.ObjectId.ObjectNumber, etc. — we keep only the bare minimum required for
        // AL-observable correctness: chain base NavApplicationObjectBase ctor (already JmpHooked
        // for skeleton-session injection) and set the masterPage field. Drop everything else.
        {
            var navFormT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavForm");
            if (navFormT != null)
            {
                var navFormCtor5 = navFormT.Methods.FirstOrDefault(m =>
                    m.IsConstructor && !m.IsStatic && m.HasBody
                    && m.Parameters.Count == 5
                    && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.ITreeObject"
                    && m.Parameters[1].ParameterType.FullName == "System.Int32"
                    && m.Parameters[2].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.Metadata.MasterPage"
                    && m.Parameters[3].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavRecord"
                    && m.Parameters[4].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLStaticMetadata");
                if (navFormCtor5 != null)
                {
                    // Discover required references by scanning NavForm 5-arg's existing IL.
                    MethodReference? appObjIdNewobj = null;
                    MethodReference? baseCtorRef = null;
                    FieldReference? masterPageFld = null;
                    int objectTypePageValue = -1;
                    var instrs = navFormCtor5.Body.Instructions;
                    for (int i = 0; i < instrs.Count; i++)
                    {
                        var ins = instrs[i];
                        if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference mrNew &&
                            mrNew.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Types.ApplicationObjectId")
                        {
                            appObjIdNewobj = mrNew;
                            for (int j = i - 1; j >= 0 && j >= i - 4; j--)
                            {
                                var p = instrs[j];
                                int? val = null;
                                if (p.OpCode == OpCodes.Ldc_I4) val = (int)p.Operand;
                                else if (p.OpCode == OpCodes.Ldc_I4_S) val = (sbyte)p.Operand;
                                else if (p.OpCode.Code >= Code.Ldc_I4_0 && p.OpCode.Code <= Code.Ldc_I4_8)
                                    val = (int)(p.OpCode.Code - Code.Ldc_I4_0);
                                if (val.HasValue && val.Value > 0 && val.Value < 256)
                                {
                                    objectTypePageValue = val.Value;
                                    break;
                                }
                            }
                        }
                        if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mrBase &&
                            mrBase.Name == ".ctor" &&
                            mrBase.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase")
                        {
                            baseCtorRef = mrBase;
                        }
                        if (ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference fr &&
                            fr.Name == "masterPage" &&
                            fr.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavForm")
                        {
                            masterPageFld = fr;
                        }
                    }

                    if (appObjIdNewobj == null || baseCtorRef == null || masterPageFld == null || objectTypePageValue < 0)
                    {
                        throw new InvalidOperationException(
                            $"NavForm 5-arg ctor rewrite: missing IL refs " +
                            $"(appObjIdNewobj={appObjIdNewobj?.FullName ?? "null"}, " +
                            $"baseCtor={baseCtorRef?.FullName ?? "null"}, " +
                            $"masterPageFld={masterPageFld?.FullName ?? "null"}, " +
                            $"otPage={objectTypePageValue})");
                    }

                    // TRUNCATE rather than rebuild. The original ctor is, in order:
                    //   1. ~9 pure field initialisers (sourceExpressions, uiParts,
                    //      selections, pageCaption, the trigger maps, the background-task
                    //      dictionaries, pageExtensions, parameterSet, ParentFormHandle) —
                    //      all `newobj Dictionary/List` or Empty constants, none of which
                    //      can throw or touch session state;
                    //   2. the base ctor call and `this.masterPage = masterPage`;
                    //   3. a tail that DOES touch skeleton state — NavCurrentThread
                    //      personalization ids, Session.NavAppGroup (NavExtensionMetrics),
                    //      SetSourceTable and an awaited SyncTempTableWithSourceTableAsync.
                    //
                    // Only (3) was ever the problem. Rebuilding the body from scratch also
                    // discarded (1), which left `sourceExpressions` null forever — and that
                    // dictionary is the whole control -> value binding table a page
                    // publishes, so no TestPage could ever resolve a control bound to
                    // anything but a Rec field. Keeping BC's own instructions up to and
                    // including the masterPage store, then returning, is a strict superset
                    // of what the rebuild produced and adds no call the rebuild avoided.
                    var body = navFormCtor5.Body;
                    var instructions = body.Instructions;

                    // Cut structurally, at the first `base.Session` read: everything before
                    // it is session-free (field initialisers, the base ctor, masterPage,
                    // personalizationId, handle, formId, UpdatePropagation) and everything
                    // from it on is the part that actually needs a real session — the
                    // NavExtensionMetrics construction off Session.NavAppGroup, then
                    // SetSourceTable and the awaited SyncTempTableWithSourceTableAsync.
                    // A fixed instruction index or a "stop after masterPage" rule would be
                    // guessing; this one states the actual criterion.
                    int sessionAt = -1;
                    for (int i = 0; i < instructions.Count; i++)
                        if (instructions[i].OpCode == OpCodes.Call
                            && instructions[i].Operand is MethodReference sessRef
                            && sessRef.Name == "get_Session"
                            && sessRef.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase")
                        { sessionAt = i; break; }
                    if (sessionAt < 0)
                        throw new InvalidOperationException(
                            "NavForm 5-arg ctor rewrite: no NavApplicationObjectBase.get_Session call found to cut at — Ncl shape changed, do not commit");

                    // The cut has to land on a STATEMENT boundary, not merely before the
                    // session read: `ldarg.0` for the next store already sits on the stack
                    // by then, and truncating there leaves the evaluation stack unbalanced
                    // at `ret` — which the CLR rejects with InvalidProgramException rather
                    // than anything that points at the cause. So simulate the stack depth
                    // and cut at the last instruction before the session read where it is
                    // back to zero.
                    int depth = 0, cut = -1;
                    for (int i = 0; i < sessionAt; i++)
                    {
                        depth += StackDelta(instructions[i]);
                        if (depth < 0)
                            throw new InvalidOperationException(
                                $"NavForm 5-arg ctor rewrite: stack simulation went negative at IL_{instructions[i].Offset:x4} — do not commit");
                        if (depth == 0) cut = i;
                    }
                    if (cut < 0)
                        throw new InvalidOperationException(
                            "NavForm 5-arg ctor rewrite: no stack-neutral cut point before the session read — do not commit");

                    int masterPageAt = -1;
                    for (int i = 0; i <= cut; i++)
                        if (instructions[i].OpCode == OpCodes.Stfld
                            && ReferenceEquals(instructions[i].Operand, masterPageFld))
                        { masterPageAt = i; break; }
                    if (masterPageAt < 0)
                        throw new InvalidOperationException(
                            "NavForm 5-arg ctor rewrite: the masterPage store is not inside the kept prefix — do not commit");

                    // The kept prefix contains one branch (the ?? on the personalization
                    // id). Truncating is only safe if every branch target stays inside it.
                    var kept = new HashSet<Instruction>();
                    for (int i = 0; i <= cut; i++) kept.Add(instructions[i]);
                    for (int i = 0; i <= cut; i++)
                    {
                        if (instructions[i].Operand is Instruction target && !kept.Contains(target))
                            throw new InvalidOperationException(
                                "NavForm 5-arg ctor rewrite: a kept instruction branches past the cut — do not commit");
                        if (instructions[i].Operand is Instruction[] targets && targets.Any(x => !kept.Contains(x)))
                            throw new InvalidOperationException(
                                "NavForm 5-arg ctor rewrite: a kept switch branches past the cut — do not commit");
                    }

                    var il = body.GetILProcessor();
                    while (instructions.Count > cut + 1)
                        il.Remove(instructions[instructions.Count - 1]);
                    body.ExceptionHandlers.Clear();
                    il.Append(il.Create(OpCodes.Ret));
                    Console.Error.WriteLine(
                        $"[Cecil] Truncated NavForm 5-arg private ctor before its first base.Session read "
                        + $"({cut + 2} of {instructions.Count} instructions kept: field initialisers, base ctor, "
                        + "masterPage, personalizationId, handle, formId; dropped the Session.NavAppGroup / "
                        + "SetSourceTable tail)");
                }
                else
                {
                    Console.Error.WriteLine("[Cecil] WARN: NavForm 5-arg private ctor not found — RequestPageBase chain may NRE in InitializeComponent");
                }
            }
        }

        // NavForm form-initialization methods called from {Report}.RequestPage.InitializeComponent.
        // These touch skeleton-session state (PageExtensions list, base.Session.IsCompanyOpen,
        // MasterPage.Expressions) that is unset in headless mode. For ProcessingOnly reports the
        // request-page subgraph is never rendered, and non-ProcessingOnly reports already throw
        // OOS at Run time, so collapsing these to safe early-returns has no AL-observable effect.
        // (Aligned with the "no real form rendering" architectural limit; documented in docs/scope.md.)
        {
            var navFormT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavForm");
            if (navFormT != null)
            {
                // These bodies are no longer DELETED, they are GUARDED. Emptying them was
                // safe only while no page was ever driven: RegisterSourceExpression is how a
                // page publishes its control -> value bindings, so a blanket no-op leaves
                // NavForm.SourceExpressions permanently null and makes a control bound to a
                // page variable (rather than to a Rec field) unresolvable — which is the
                // TestPage cluster. See RunnerFormInit.cs.
                //
                // The injected prologue is:
                //     if (!RunnerFormInit.ShouldRunRealFormInit(this)) return;
                //     <original body>
                // so every caller that used to get an immediate `ret` still does, byte for
                // byte, and only forms the runner explicitly opted in run for real.
                var shouldRunRef = asm.MainModule.ImportReference(
                    typeof(AlRunner.Patches.RunnerFormInit).GetMethod(
                        nameof(AlRunner.Patches.RunnerFormInit.ShouldRunRealFormInit),
                        BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "RunnerFormInit.ShouldRunRealFormInit not found — do not commit"));

                // RegisterSourceExpression gets a WIDER guard than the other two. A page AL
                // opens with RunModal is an instance the runner never constructed and so
                // never marked — yet the test's [ModalPageHandler] is handed a TestPage over
                // that form and must drive it. Registration is what publishes the control ->
                // value bindings, so on the narrow gate every page-variable-bound control on
                // a modal page was unresolvable. See RunnerFormInit.ShouldRegisterSourceExpressions.
                var shouldRegisterRef = asm.MainModule.ImportReference(
                    typeof(AlRunner.Patches.RunnerFormInit).GetMethod(
                        nameof(AlRunner.Patches.RunnerFormInit.ShouldRegisterSourceExpressions),
                        BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "RunnerFormInit.ShouldRegisterSourceExpressions not found — do not commit"));

                int rewrites = 0;
                foreach (var m in navFormT.Methods)
                {
                    if (!m.HasBody) continue;
                    bool target = false;
                    var guardRef = shouldRunRef;
                    if (m.Name == "CallInitializeComponentExtensionMethod" && m.Parameters.Count == 0) target = true;
                    else if (m.Name == "InitializeForm" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Void") target = true;
                    else if (m.Name == "RegisterSourceExpression") { target = true; guardRef = shouldRegisterRef; }
                    if (!target) continue;
                    // NEVER rewrite an async ValueTask body (CoreCLR segfault risk).
                    if (m.ReturnType.FullName.StartsWith("System.Threading.Tasks.ValueTask")) continue;
                    var body = m.Body;
                    var il = body.GetILProcessor();
                    var first = body.Instructions[0];
                    var ret = il.Create(OpCodes.Ret);
                    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(first, il.Create(OpCodes.Call, guardRef));
                    il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
                    il.InsertBefore(first, ret);
                    body.MaxStackSize = Math.Max(body.MaxStackSize, 2);
                    rewrites++;
                }
                Console.Error.WriteLine($"[Cecil] Guarded {rewrites} NavForm form-init method(s) (CallInitComponentExt/InitializeForm/RegisterSourceExpression) → real body only for runner-opted-in forms");
            }
        }

        // NavForm.GetPart(int) — issue #2201's page-globals shape. GetPart(int) is the one
        // door BOTH the host's own compiled AL (CurrPage.<part> compiles to
        // base.Parent.CurrPage.GetPart(controlId) — see MockTestPage.cs's GetPart doc
        // comment) and the runner's own AdoptFromHost go through to reach a subpage part
        // object. Appending a call to RunnerFormInit.OnSubpagePartResolved right before every
        // `ret` — after the original body has already computed its return value, which stays
        // on the stack for the `ret` untouched — gives the runner a hook at the EARLIEST
        // point common to both callers, so a page-globals part's OnOpenPage can run before
        // EITHER side's first real touch, not just before the runner's own.
        //
        // Appending after (not guarding before, unlike the block above) is deliberate:
        // GetPart(int)'s own body must always run for real — it is BC's un-guarded lookup
        // machinery, used by every page whether the runner is driving it or not — this only
        // adds an observer, it does not gate anything.
        //
        // `dup; call OnSubpagePartResolved; ret` before EVERY ret in a non-void method is
        // correct regardless of how many return points the JIT/compiler emits: IL
        // well-formedness guarantees exactly one value is on the evaluation stack
        // immediately before any `ret` in a non-void method, so this needs no assumption
        // about a single-return-point shape.
        {
            var navFormT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavForm");
            var getPartM = navFormT?.Methods.FirstOrDefault(m =>
                m.Name == "GetPart" && m.HasBody && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == "System.Int32"
                && m.ReturnType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavForm");
            if (getPartM != null)
            {
                var hookRef = asm.MainModule.ImportReference(
                    typeof(AlRunner.Patches.RunnerFormInit).GetMethod(
                        nameof(AlRunner.Patches.RunnerFormInit.OnSubpagePartResolved),
                        BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "RunnerFormInit.OnSubpagePartResolved not found — do not commit"));

                var il = getPartM.Body.GetILProcessor();
                var rets = getPartM.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
                foreach (var ret in rets)
                {
                    il.InsertBefore(ret, il.Create(OpCodes.Dup));
                    il.InsertBefore(ret, il.Create(OpCodes.Call, hookRef));
                }
                getPartM.Body.MaxStackSize = Math.Max(getPartM.Body.MaxStackSize, 3);
                Console.Error.WriteLine($"[Cecil] Hooked NavForm.GetPart(int) → RunnerFormInit.OnSubpagePartResolved at {rets.Count} return point(s)");
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARN: NavForm.GetPart(int) not found — page-globals subpage OnOpenPage ordering (issue #2201) may be wrong");
            }
        }

        // Diagnostic prepend (gated by env AL_RUNNER_DIAG_IC=1): print marker at
        // entry of each Ncl method called by Report{N}.InitializeComponent.
        {
            var diagMi = typeof(AlRunner.NavReportSync).GetMethod("Diag",
                BindingFlags.Static | BindingFlags.Public);
            if (diagMi != null)
            {
                var diagRef = asm.MainModule.ImportReference(diagMi);
                var targets = new[]
                {
                    ("Microsoft.Dynamics.Nav.Runtime.NavRecordHandle", ".ctor", 4, "NavRecordHandle..ctor(ITreeObject,int,bool,SecurityFiltering)"),
                    ("Microsoft.Dynamics.Nav.Runtime.DataItem",        ".ctor", 2, "DataItem..ctor(ITreeObject,NavRecordHandle)"),
                    ("Microsoft.Dynamics.Nav.Runtime.DataItem",        "set_OnAfterGetRecord", 1, "DataItem.set_OnAfterGetRecord"),
                    ("Microsoft.Dynamics.Nav.Runtime.DataItemIterator","Add", 2, "DataItemIterator.Add(DataItem,string)"),
                    ("Microsoft.Dynamics.Nav.Runtime.DataItemIterator","EndInitialization", 0, "DataItemIterator.EndInitialization"),
                    ("Microsoft.Dynamics.Nav.Runtime.DataItemIterator","get_Metadata", 0, "DataItemIterator.get_Metadata"),
                    ("Microsoft.Dynamics.Nav.Runtime.NavReport",       "set_RequestOptionsPage", 1, "NavReport.set_RequestOptionsPage"),
                };
                // Also instrument TreeObjectReference..ctor(2) — nested type lookup.
                int diagPrepends = 0;
                // Nested TreeObjectReference under TreeHandler.
                var treeHandlerT = asm.MainModule.Types.FirstOrDefault(tt => tt.FullName == "Microsoft.Dynamics.Nav.Runtime.TreeHandler");
                if (treeHandlerT != null)
                {
                    foreach (var nested in treeHandlerT.NestedTypes.Where(n => n.Name == "TreeObjectReference"))
                    {
                        foreach (var m in nested.Methods.Where(mm => mm.Name == ".ctor" && mm.Parameters.Count == 2 && mm.HasBody))
                        {
                            if (m.ReturnType.FullName.StartsWith("System.Threading.Tasks.ValueTask")) continue;
                            var il = m.Body.GetILProcessor();
                            var first = m.Body.Instructions[0];
                            il.InsertBefore(first, il.Create(OpCodes.Ldstr, "TreeObjectReference..ctor(parent,initialTarget)"));
                            il.InsertBefore(first, il.Create(OpCodes.Call, diagRef));
                            diagPrepends++;
                        }
                    }
                }
                foreach (var (typeName, methName, paramCount, msg) in targets)
                {
                    var t = asm.MainModule.Types.FirstOrDefault(tt => tt.FullName == typeName);
                    if (t == null) continue;
                    foreach (var m in t.Methods.Where(mm => mm.Name == methName && mm.Parameters.Count == paramCount && mm.HasBody))
                    {
                        if (m.ReturnType.FullName.StartsWith("System.Threading.Tasks.ValueTask")) continue;
                        var body = m.Body;
                        var il = body.GetILProcessor();
                        var first = body.Instructions[0];
                        il.InsertBefore(first, il.Create(OpCodes.Ldstr, msg));
                        il.InsertBefore(first, il.Create(OpCodes.Call, diagRef));
                        diagPrepends++;
                    }
                }
                // Also prepend on MetaReport.get_RequestFormMetadata in Types.dll? Cannot — different asm.
                Console.Error.WriteLine($"[Cecil] Prepended {diagPrepends} IC diagnostic marker(s) (AL_RUNNER_DIAG_IC=1 to enable output)");
            }
        }

        // NavReport..ctor(ITreeObject, int, NCLStaticMetadata) — original body:
        //   : base(parent, new ApplicationObjectId(Report, objectId), staticMetadata)
        //   PreviewCanPrint = true;
        //   parent.Tree.Session.Company.RegisterReport(this);   // NREs on skeleton (Company is null)
        // We must keep the base-ctor chain (DataItemIterator..ctor → NavApplicationObjectBase..ctor)
        // because DataItemIterator has a field initializer `dataItems = new List<DataItem>()` whose
        // emitted IL lives in DataItemIterator's ctor body. Skipping that chain (e.g. via JmpHook)
        // would leave `dataItems` null and IC's `Add(dataItem, "...")` would NRE.
        // Strategy: clear the body, chain base via the existing DataItemIterator..ctor reference,
        // set PreviewCanPrint=true, skip RegisterReport.
        {
            var navReportT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavReport");
            if (navReportT != null)
            {
                var ctor3 = navReportT.Methods.FirstOrDefault(m =>
                    m.IsConstructor && !m.IsStatic && m.HasBody
                    && m.Parameters.Count == 3
                    && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.ITreeObject"
                    && m.Parameters[1].ParameterType.FullName == "System.Int32"
                    && m.Parameters[2].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLStaticMetadata");
                if (ctor3 == null)
                {
                    Console.Error.WriteLine("[Cecil] WARN: NavReport 3-arg StaticMetadata ctor not found");
                }
                else
                {
                    // Discover refs by scanning the existing IL.
                    MethodReference? appObjIdNewobj = null;
                    MethodReference? baseCtorRef = null;
                    MethodReference? previewCanPrintSetter = null;
                    int objectTypeReportValue = -1;
                    var instrs = ctor3.Body.Instructions;
                    for (int i = 0; i < instrs.Count; i++)
                    {
                        var ins = instrs[i];
                        if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference mrNew &&
                            mrNew.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Types.ApplicationObjectId")
                        {
                            appObjIdNewobj = mrNew;
                            for (int j = i - 1; j >= 0 && j >= i - 4; j--)
                            {
                                var p = instrs[j];
                                int? val = null;
                                if (p.OpCode == OpCodes.Ldc_I4) val = (int)p.Operand;
                                else if (p.OpCode == OpCodes.Ldc_I4_S) val = (sbyte)p.Operand;
                                else if (p.OpCode.Code >= Code.Ldc_I4_0 && p.OpCode.Code <= Code.Ldc_I4_8)
                                    val = (int)(p.OpCode.Code - Code.Ldc_I4_0);
                                if (val.HasValue && val.Value > 0 && val.Value < 256)
                                {
                                    objectTypeReportValue = val.Value;
                                    break;
                                }
                            }
                        }
                        if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mrBase &&
                            mrBase.Name == ".ctor" &&
                            (mrBase.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Runtime.DataItemIterator"
                             || mrBase.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase"))
                        {
                            baseCtorRef = mrBase;
                        }
                        if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                            && ins.Operand is MethodReference mrPC
                            && mrPC.Name == "set_PreviewCanPrint")
                        {
                            previewCanPrintSetter = mrPC;
                        }
                    }

                    if (appObjIdNewobj == null || baseCtorRef == null || objectTypeReportValue < 0)
                    {
                        throw new InvalidOperationException(
                            $"NavReport 3-arg ctor rewrite: missing IL refs " +
                            $"(appObjIdNewobj={appObjIdNewobj?.FullName ?? "null"}, " +
                            $"baseCtor={baseCtorRef?.FullName ?? "null"}, " +
                            $"otReport={objectTypeReportValue})");
                    }

                    var body = ctor3.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();

                    // base(parent, new ApplicationObjectId(Report, objectId), staticMetadata)
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldarg_1));                          // parent
                    il.Append(il.Create(OpCodes.Ldc_I4, objectTypeReportValue));    // ObjectType.Report
                    il.Append(il.Create(OpCodes.Ldarg_2));                          // objectId
                    il.Append(il.Create(OpCodes.Newobj, appObjIdNewobj));
                    il.Append(il.Create(OpCodes.Ldarg_3));                          // staticMetadata
                    il.Append(il.Create(OpCodes.Call, baseCtorRef));

                    if (previewCanPrintSetter != null)
                    {
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Ldc_I4_1));
                        il.Append(il.Create(OpCodes.Call, previewCanPrintSetter));
                    }
                    // Re-create the NavReport collection field initializers this body
                    // rewrite drops (reportRecords, reportLabels, extension maps, …).
                    // Report execution (GetReportRecords, label processing) NREs without.
                    var initCollectionsInfo = typeof(AlRunner.NavReportSync).GetMethod(
                        "InitializeNavReportCollections", BindingFlags.Static | BindingFlags.Public)
                        ?? throw new InvalidOperationException("NavReportSync.InitializeNavReportCollections not found");
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Call, asm.MainModule.ImportReference(initCollectionsInfo)));
                    // Skip parent.Tree.Session.Company.RegisterReport(this) — Company is null on skeleton.

                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 4;
                    Console.Error.WriteLine($"[Cecil] Rewrote NavReport..ctor(ITreeObject,int,NCLStaticMetadata) → base ctor chain + set_PreviewCanPrint; skip Company.RegisterReport (base->{baseCtorRef.DeclaringType.Name})");
                }

                // NavReport.set_RequestOptionsPage — original body:
                //   if (requestOptionsPage != null && requestOptionsPage.SaveValues) { /* unsub */ }
                //   new TreeObjectReference(this, value);                  // tree bookkeeping
                //   requestOptionsPage = value;
                //   if (requestOptionsPage.SaveValues) { /* +event */ }    // NREs through RequestPage.SaveValues → EnsureMetadataLoaded → ApplicationObjectRootScope ctor
                // Rewrite: simply assign the backing field. AL only observes the getter
                // (returns the field). TreeObjectReference is internal disposal bookkeeping;
                // ApplyReportOptions/GetReportOptions events are internal NCL hooks fired
                // only when a real UI applies saved options — never on the headless ProcessingOnly
                // path. SaveValues itself requires service-tier metadata which we don't have.
                {
                    var setter = navReportT.Methods.FirstOrDefault(m =>
                        m.Name == "set_RequestOptionsPage" && !m.IsStatic && m.HasBody && m.Parameters.Count == 1);
                    if (setter != null)
                    {
                        // Find the backing field via the IL: look for `stfld requestOptionsPage`.
                        FieldReference? backing = null;
                        foreach (var ins in setter.Body.Instructions)
                        {
                            if (ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference fr
                                && fr.Name == "requestOptionsPage")
                            {
                                backing = fr;
                                break;
                            }
                        }
                        if (backing == null)
                        {
                            Console.Error.WriteLine("[Cecil] WARN: NavReport.set_RequestOptionsPage backing field not found — leaving original IL (will NRE through SaveValues)");
                        }
                        else
                        {
                            var body = setter.Body;
                            body.Instructions.Clear();
                            body.Variables.Clear();
                            body.ExceptionHandlers.Clear();
                            var il = body.GetILProcessor();
                            il.Append(il.Create(OpCodes.Ldarg_0));
                            il.Append(il.Create(OpCodes.Ldarg_1));
                            il.Append(il.Create(OpCodes.Stfld, backing));
                            il.Append(il.Create(OpCodes.Ret));
                            body.MaxStackSize = 2;
                            Console.Error.WriteLine("[Cecil] Rewrote NavReport.set_RequestOptionsPage → assign backing field (skip TreeObjectReference + SaveValues event-subscribe; both untriggerable headless)");
                        }
                    }
                }
            }
        }

        // ─── Standalone-mode metadata short-circuits ───────────────────────────────
        // None of these are silent no-ops: they all return the truthful value for
        // a runner that has no service-tier metadata layer (no installed layouts,
        // no license, no metadata-derived doc XML). The alternative — letting the
        // real code path execute — would NRE inside service-tier metadata lookups.
        // For each rewrite we document what the original method does and why the
        // chosen replacement is the AL-faithful answer for the standalone runner.

    }

    private static void AddReportsOwned(HashSet<string> set)
    {
        // NavXmlPort static Run(id[, requestWindow[, import[, record]]]) — #1800. These four
        // overloads are a genuine, permanent out-of-scope surface (docs/scope.md#file-storage,
        // same bucket as NavFile.ALUpload/ALDownload's browser round-trip): BC's real body
        // always routes through NavFile.InternalUpload/InternalDownload → the client-callback
        // file-browse dialog, for every overload and argument combination. Cecil-own them to
        // our typed OOS throw instead of a JmpHook registration that can silently fail to bind.
        // The instance Export/Import/Run/RunXmlPort/SetTableView/BeginInitialization/
        // EndInitialization/Add(*Node) methods in the same cluster are the OPPOSITE case — BC's
        // real body is already correct there, nothing to redirect to — and are deliberately NOT
        // listed below. Full evidence (decompiled source) and the
        // misdiagnosis-and-correction record for the eight already-correct methods live once,
        // canonically, in the big comment block above NavXmlPort_StaticRun1..4 in
        // AlRunner/Patches/XmlPortPatches.cs. See also
        // tests/runner-extras/standalone-suites/xmlport-cluster-hooks-1800 for the RED→GREEN
        // proof.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavXmlPort::Run/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavXmlPort::Run/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavXmlPort::Run/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavXmlPort::Run/4");
    }

}
