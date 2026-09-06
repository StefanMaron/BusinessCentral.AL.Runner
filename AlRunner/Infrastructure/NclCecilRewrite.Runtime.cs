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
    private static void RewriteNcl_Runtime(AssemblyDefinition asm)
    {
        {
            // (a) NavReport.GetLayoutCore(DataError, int, ReportModel, NavInStream)
            //     Original: looks up the registered layout from ReportLayoutSelection
            //     and copies its bytes into `inStream`. With no service tier, there
            //     IS no layout selection. Per DataError contract:
            //       TrapError  → return false  (truthful: "no layout available")
            //       ThrowError → throw         (AL-observable error)
            var navReportT = asm.MainModule.Types.FirstOrDefault(t =>
                t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavReport");
            if (navReportT != null)
            {
                var getLayoutCore = navReportT.Methods.FirstOrDefault(m =>
                    m.Name == "GetLayoutCore" && m.IsStatic && m.HasBody && m.Parameters.Count == 4);
                if (getLayoutCore != null)
                {
                    var oosCtorInfo2 = typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!;
                    var oosCtor2 = asm.MainModule.ImportReference(oosCtorInfo2);
                    var body = getLayoutCore.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    // if (errorLevel == TrapError /*0*/) return false;
                    var throwLbl = il.Create(OpCodes.Ldstr, "out-of-scope: NavReport.<Layout> — layout rendering requires service tier — see docs/scope.md#report-rendering");
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldc_I4_0));
                    il.Append(il.Create(OpCodes.Bne_Un_S, throwLbl));
                    il.Append(il.Create(OpCodes.Ldc_I4_0));
                    il.Append(il.Create(OpCodes.Ret));
                    il.Append(throwLbl);
                    il.Append(il.Create(OpCodes.Newobj, oosCtor2));
                    il.Append(il.Create(OpCodes.Throw));
                    body.MaxStackSize = 2;
                    Console.Error.WriteLine("[Cecil] Rewrote NavReport.GetLayoutCore → TrapError=>false; else throw OOS (was: ReportLayoutSelection NRE)");
                }

                // (b) NavReport.WordXmlPart(bool) — instance method.
                //     Original: calls OfficeCustomXmlPart.Generate(base.Metadata, …)
                //     which NREs on our stub MetaReport (no Datasets/Columns).
                //     Replacement: return string.Empty. The Word custom XML part is
                //     a metadata-derived document; "no metadata" → empty is correct,
                //     not a silent no-op.
                var wordXml = navReportT.Methods.FirstOrDefault(m =>
                    m.Name == "WordXmlPart" && !m.IsStatic && m.HasBody && m.Parameters.Count == 1);
                if (wordXml != null)
                {
                    var body = wordXml.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldstr, ""));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine("[Cecil] Rewrote NavReport.WordXmlPart(bool) → \"\" (was: OfficeCustomXmlPart.Generate NRE on stub MetaReport)");
                }
            }

            // (c) DataItemIterator.ObjectID(bool useCaption) — abstract base method
            //     for NavReport / NavQuery / NavXmlPort. Original:
            //       if (useCaption) return GetCaption(true);    // metadata path → NRE
            //       return string.Format("{0} {1}", IteratorType, ObjectId.ObjectNumber);
            //     The non-caption branch is fully self-contained on data we already
            //     populate (IteratorType is a const override, ObjectId is set in the
            //     ctor). Force useCaption=false at method entry so we always take
            //     the truthful "Report 50015" / "Query 50100" formatted-string path.
            var dataIterT = asm.MainModule.Types.FirstOrDefault(t =>
                t.FullName == "Microsoft.Dynamics.Nav.Runtime.DataItemIterator");
            if (dataIterT != null)
            {
                var objIdMeth = dataIterT.Methods.FirstOrDefault(m =>
                    m.Name == "ObjectID" && !m.IsStatic && m.HasBody && m.Parameters.Count == 1
                    && m.Parameters[0].ParameterType.FullName == "System.Boolean");
                if (objIdMeth != null)
                {
                    var il = objIdMeth.Body.GetILProcessor();
                    var first = objIdMeth.Body.Instructions[0];
                    il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
                    il.InsertBefore(first, il.Create(OpCodes.Starg_S, objIdMeth.Parameters[0]));
                    Console.Error.WriteLine("[Cecil] Rewrote DataItemIterator.ObjectID(bool) → force useCaption=false (skip GetCaption metadata NRE)");
                }
            }

            // (d) NavSession.TestReportLanguagePermission(int lcid) — license check
            //     that derefs Session.License (null on skeleton). AL has no observable
            //     license model in the runner; the headless runner trusts all locales.
            //     Replacement: ret.
            var navSessionT = asm.MainModule.Types.FirstOrDefault(t =>
                t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavSession");
            if (navSessionT != null)
            {
                var permMeth = navSessionT.Methods.FirstOrDefault(m =>
                    m.Name == "TestReportLanguagePermission" && m.HasBody && m.Parameters.Count == 1);
                if (permMeth != null)
                {
                    var body = permMeth.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 0;
                    Console.Error.WriteLine("[Cecil] Rewrote NavSession.TestReportLanguagePermission → ret (no license model in runner)");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // STARTUP-CRITICAL hooks — JmpHook→Cecil migration (Batch 1).
        //
        // These are installed by BcRuntime.ApplyAllPatches via JmpHook today. Under
        // AL_RUNNER_NO_JMPHOOK=1 the JMPs are no-ops, so the real (Windows-only /
        // service-tier) bodies run and crash at startup. Cecil-rewriting the bodies
        // makes them work runtime-agnostically and coexists idempotently with the
        // JmpHook (both redirect to the same BcRuntime helper / same no-op shape).
        // See .claude/rules/precompiled-dll-respect.md — Ncl.dll is runtime engine.
        // ─────────────────────────────────────────────────────────────────────
        {
            var nclMod = asm.MainModule;

            // 1) NavEnvironment..cctor — neutralise ONLY the Linux-fatal line.
            //    The real cctor's sole non-portable instruction is
            //      call WindowsIdentity::GetCurrent()   →  stsfld serviceAccount
            //    which throws PlatformNotSupportedException on Linux. Every other
            //    instruction (ManualResetGate, Guid.NewGuid, HashSets, the
            //    StandardServiceTopology backing-field init, …) is portable.
            //
            //    We do NOT replace the whole body with a BcRuntime helper call:
            //    doing so deadlocks/spins in normal (JmpHook-on) mode because the
            //    helper re-enters type initialisation (Activator.CreateInstance of
            //    StandardServiceTopology) while the CLR holds NavEnvironment's class-
            //    init lock and the JmpHook simultaneously JITs/redirects the cctor —
            //    a recursive-cctor busy spin (safepoint-free, dotnet-stack hangs).
            //
            //    Instead, surgically replace `call WindowsIdentity::GetCurrent()` with
            //    `ldnull` (same stack effect: 0 args in → 1 ref out), so the cctor runs
            //    fully NATIVELY and just stores serviceAccount=null. The serviceAccount
            //    field is never read directly — get_ServiceAccount is fully replaced
            //    below (2a) to return a synthetic SecurityIdentifier. The JmpHook
            //    NavEnvironmentCctorReplacement remains installed and idempotent.
            //    Token-safe: replaces one call's *use* of an existing memberRef with a
            //    no-operand ldnull; adds no new typeRef/memberRef.
            {
                var cctor = FindNclMethod(nclMod,
                    "Microsoft.Dynamics.Nav.Runtime.NavEnvironment", ".cctor", 0);
                var il = cctor.Body.GetILProcessor();
                var getCurrentCalls = cctor.Body.Instructions
                    .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                        && i.Operand is MethodReference mr
                        && mr.DeclaringType.FullName == "System.Security.Principal.WindowsIdentity"
                        && mr.Name == "GetCurrent"
                        && mr.Parameters.Count == 0)
                    .ToList();
                if (getCurrentCalls.Count != 1)
                    throw new InvalidOperationException(
                        $"[Cecil] expected exactly 1 WindowsIdentity.GetCurrent() in NavEnvironment..cctor, found {getCurrentCalls.Count} — Ncl shape changed; do not commit");
                il.Replace(getCurrentCalls[0], il.Create(OpCodes.Ldnull));
                Console.Error.WriteLine("[Cecil] Neutralised WindowsIdentity.GetCurrent() in NavEnvironment..cctor → ldnull (serviceAccount=null; portable cctor)");
            }

            // 2a) NavEnvironment.get_ServiceAccount → BcRuntime.GetServiceAccountReplacement().
            //     Helper returns object?; the property returns SecurityIdentifier, so cast.
            {
                var getSvcAcct = FindNclMethod(nclMod,
                    "Microsoft.Dynamics.Nav.Runtime.NavEnvironment", "get_ServiceAccount", 0);
                var helperMi = typeof(AlRunner.BcRuntime).GetMethod(
                    nameof(AlRunner.BcRuntime.GetServiceAccountReplacement),
                    BindingFlags.Public | BindingFlags.Static)!;
                var helperRef = nclMod.ImportReference(helperMi);
                var body = getSvcAcct.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Call, helperRef));
                il.Append(il.Create(OpCodes.Castclass, getSvcAcct.ReturnType));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Replaced NavEnvironment.get_ServiceAccount → BcRuntime.GetServiceAccountReplacement (cast SecurityIdentifier)");
            }

            // 2b) NavEnvironment.get_ServiceAccountName → BcRuntime.GetServiceAccountNameReplacement().
            //     Both return string — direct forward.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, "Microsoft.Dynamics.Nav.Runtime.NavEnvironment", "get_ServiceAccountName", 0),
                nameof(AlRunner.BcRuntime.GetServiceAccountNameReplacement));

            // 3) NavEnvironment.EmitServerStartupTraceEvents(NavDiagnostics, ServerUserSettings) → void no-op.
            //    Only the static 2-arg overload exists; it emits server-startup telemetry
            //    (no AL semantic effect). JmpHook routes it to NoOp2; the equivalent Cecil
            //    body simply returns, leaving the unused args as ignored slots.
            ReplaceBodyConst(
                FindNclMethod(nclMod, "Microsoft.Dynamics.Nav.Runtime.NavEnvironment", "EmitServerStartupTraceEvents", 2),
                ConstResult.Void);

            // NOTE: ExecutionListener..cctor is deliberately NOT migrated in this batch.
            // It is not startup-critical — the runner boots fine under NO_JMPHOOK=1
            // without it (the real cctor's first-invoke is tolerable on the boot path).
            // A Cecil rewrite of it (body → BcRuntime.ExecutionListenerCctorReplacement,
            // which leaves Instance null) makes normal-mode test execution SPIN: the
            // ALFunctionTimingExecutionListener path reached during AL method execution
            // hangs (60s watchdog timeout) when the Cecil-rewritten cctor coexists with
            // the JmpHook. Left to the existing JmpHook (normal mode) + a future batch
            // that models Instance faithfully. Bisected: it is the sole regressor here.

            // 5) NavOpenTelemetryLogger construction — its ctor opens an OpenTelemetry
            //    pipeline (Geneva ETW exporter) that throws on Linux. The ctor lives in
            //    Types.dll, which the Cecil pass does NOT rewrite. Instead we neutralise
            //    the SOLE construction call-site, which is inside NavEnvironment..ctor (an
            //    Ncl method): `newobj NavOpenTelemetryLogger(Verbosity, Dictionary, String)`
            //    immediately followed by `call NavDiagnostics.set_OpenTelemetryLogger(...)`.
            //    Replace the newobj with `pop;pop;pop;ldnull` so the 3 ctor args are
            //    discarded and null is assigned — exactly what the JmpHook path produces
            //    (BcRuntime sets NavDiagnostics.OpenTelemetryLogger = null after ctor; every
            //    trace call routes through the `?.` null-conditional and skips telemetry).
            //    Token-safe: removes a memberRef *use*, adds no new typeRef/memberRef.
            {
                var envCtor = FindNclMethod(nclMod,
                    "Microsoft.Dynamics.Nav.Runtime.NavEnvironment", ".ctor", 1);
                var il = envCtor.Body.GetILProcessor();
                var newobjs = envCtor.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Newobj
                        && i.Operand is MethodReference mr
                        && mr.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Diagnostic.NavOpenTelemetryLogger"
                        && mr.Name == ".ctor")
                    .ToList();
                if (newobjs.Count != 1)
                    throw new InvalidOperationException(
                        $"[Cecil] expected exactly 1 NavOpenTelemetryLogger newobj in NavEnvironment..ctor, found {newobjs.Count} — Ncl shape changed; do not commit");
                var newobj = newobjs[0];
                int ctorArgs = ((MethodReference)newobj.Operand).Parameters.Count; // 3
                // Replace newobj in-place with ldnull (top result), then insert (ctorArgs)
                // pops BEFORE the ldnull so the pushed args are discarded first, leaving the
                // stack as it was minus the args, plus null for set_OpenTelemetryLogger to consume.
                var ldnull = il.Create(OpCodes.Ldnull);
                il.Replace(newobj, ldnull);
                for (int i = 0; i < ctorArgs; i++)
                    il.InsertBefore(ldnull, il.Create(OpCodes.Pop));
                Console.Error.WriteLine($"[Cecil] Neutralised NavOpenTelemetryLogger construction in NavEnvironment..ctor → pop x{ctorArgs}; ldnull (no Types.dll edit)");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // NavMethodScope cluster — JmpHook→Cecil migration (Batch 2).
        //
        // NavMethodScope is the per-AL-frame execution unit. Under NO_JMPHOOK=1 the
        // real ctor (and its siblings) run against the skeleton session and NRE — the
        // ctor's `parent` arg is null, throwing ArgumentNullException, which fails every
        // corpus test. These replacements ALREADY exist as BcRuntime statics (installed
        // by JmpHook today). We forward the bodies to them via Cecil, runtime-agnostically.
        //
        // The ctor replacement deliberately does NOT call base (it sets base fields via
        // FieldPoke), so `body → call helper; ret` with no base ctor call is correct and
        // matches the JmpHook semantics exactly. Coexists idempotently with the JmpHook.
        // See AlRunner/Patches/MethodScopePatches.cs for the helper bodies.
        // ─────────────────────────────────────────────────────────────────────
        {
            var nclMod = asm.MainModule;
            const string MsType = "Microsoft.Dynamics.Nav.Runtime.NavMethodScope";
            const string AlMsType = "Microsoft.Dynamics.Nav.Runtime.ALMethodScope";

            // 1) NavMethodScope..ctor(NavApplicationObjectBase, MethodScopeFlags, bool)
            //    → BcRuntime.NavMethodScopeCtorReplacement(self, applicationObject, object flags, bool).
            //    The `flags` arg is the VALUE-TYPE MethodScopeFlags but the helper declares it as
            //    `object` — ReplaceBodyWithHelper now emits `box MethodScopeFlags`. Match the
            //    specific 3-arg ctor (param0 = NavApplicationObjectBase, param2 = bool) the same
            //    way the JmpHook does, so we don't grab a different 3-arg ctor by paramCount alone.
            {
                var msTypeDef = nclMod.GetType(MsType)
                    ?? throw new InvalidOperationException(
                        $"[Cecil] type {MsType} not found — Ncl shape changed; do not commit");
                var ctor3 = msTypeDef.Methods.FirstOrDefault(m =>
                    m.IsConstructor && m.HasBody && m.Parameters.Count == 3
                    && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase"
                    && m.Parameters[2].ParameterType.FullName == "System.Boolean")
                    ?? throw new InvalidOperationException(
                        $"[Cecil] NavMethodScope 3-arg ctor (NavApplicationObjectBase,MethodScopeFlags,bool) not found — Ncl shape changed; do not commit");
                ReplaceBodyWithHelper(nclMod, ctor3, nameof(AlRunner.BcRuntime.NavMethodScopeCtorReplacement));
            }

            // 2) NavMethodScope.Dispose(bool) → BcRuntime.NavMethodScope_Dispose(object, bool).
            //    Paired recursion-counter balance with the ctor. `this` is a reference type;
            //    `bool disposing` matches the helper's `bool` param exactly — no boxing.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, MsType, "Dispose", 1),
                nameof(AlRunner.BcRuntime.NavMethodScope_Dispose));

            // NavMethodScope.AssertError(Action) and NavMethodScope.ProcessException(Exception)
            // — migrated to Cecil in Batch 3. Batch 2 left these JmpHook'd because their Cecil
            // form drove NORMAL mode into a safepoint-free 100%-CPU spin. That spin was COEXISTENCE
            // (JmpHook + Cecil both active on a re-entrant replacement); the Cecil-owned skip
            // registry removes coexistence, so the Cecil form is now safe. The helpers are on the
            // BcRuntime partial (MethodScopePatches.cs); both keys are registered in CecilOwned.
            // AssertError(Action): void, `this`→object widening + Action ref param, no box.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, MsType, "AssertError", 1),
                nameof(AlRunner.BcRuntime.NavMethodScope_AssertError));
            // ProcessException(Exception): bool return, `this`→object? widening + Exception? param.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, MsType, "ProcessException", 1),
                nameof(AlRunner.BcRuntime.NavMethodScope_ProcessException));

            // 3) ALMethodScope.AssignScopeId() → BcRuntime.ALMethodScope_AssignScopeId(object) (no-op).
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, AlMsType, "AssignScopeId", 0),
                nameof(AlRunner.BcRuntime.ALMethodScope_AssignScopeId));

            // 4) NavMethodScope.ThrowStackOverflow — stack-depth check uses a non-NavMethodScope
            //    sentinel and false-positives in the headless harness. JmpHook routes it to a
            //    NoOp; the Cecil equivalent is a plain void return (ignored args). It may be
            //    instance or static; ReplaceBodyConst(Void) emits `ret` either way.
            {
                var tso = TryFindNclMethod(nclMod, MsType, "ThrowStackOverflow")
                    ?? throw new InvalidOperationException(
                        "[Cecil] NavMethodScope.ThrowStackOverflow not found — Ncl shape changed; do not commit");
                if (tso.ReturnType.FullName != "System.Void")
                    throw new InvalidOperationException(
                        $"[Cecil] NavMethodScope.ThrowStackOverflow returns {tso.ReturnType.FullName}, expected void — Ncl shape changed; do not commit");
                ReplaceBodyConst(tso, ConstResult.Void);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // NavMethodScope.StmtHit(int) / CStmtHit(int[, bool]) — --coverage hook (issue
        // #1922, first slice of #1640).
        //
        // BC's own AL compiler already instruments every AL statement with a StmtHit(N)
        // (plain statements) or CStmtHit(N) (if/while/repeat CONDITIONS, folded into the
        // boolean expression: `if (CStmtHit(1) & (this.flag))`) call, where N indexes the
        // scope class's [SourceSpans] attribute. Decompiling StmtHit confirmed it does
        // exactly two things (CStmtHit's two overloads are the same shape, returning bool
        // so they compose into an expression):
        //
        //   public void StmtHit(int currentStatementNumber)
        //   {
        //       statementNumber = currentStatementNumber;
        //       ExecutionListener.Instance?.ProcessStatementHit(this);
        //   }
        //
        // `statementNumber` backs NavMethodScope.StatementNumber, which
        // AlCallStackCapture reads to produce "line L" in every AL stack trace — so this
        // rewrite MUST NOT replace or reorder that assignment. It only PREPENDS a call to
        // AlCoverageTracker.OnStmtHit(this, currentStatementNumber) before each method's
        // existing first instruction, leaving the rest of the body — and therefore
        // StatementNumber tracking — completely untouched. Regression-tested by
        // AlCallStackLineRegressionTests (stack-trace lines identical with the rewrite
        // active, --coverage on or off).
        //
        // (ExecutionListener.Instance is permanently null in this runtime — its cctor and
        // AddListener/RemoveListener are already no-op'd elsewhere in this file/BcRuntime
        // for R2R-stability reasons predating this issue — so that line was already inert
        // before this rewrite and stays inert after it.)
        //
        // PrependStaticCall (used elsewhere in this file) can't be reused here: it only
        // forwards reference-typed arg slots (to avoid boxing), and the second argument
        // here is `int currentStatementNumber` — a value type that must reach
        // OnStmtHit's `int` parameter unboxed. So this block emits its own
        // `ldarg.0; ldarg.1; call` prologue instead.
        {
            var nclMod = asm.MainModule;
            const string MsType = "Microsoft.Dynamics.Nav.Runtime.NavMethodScope";
            var hookMi = typeof(AlRunner.Infrastructure.AlCoverageTracker).GetMethod(
                nameof(AlRunner.Infrastructure.AlCoverageTracker.OnStmtHit), BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] AlCoverageTracker.OnStmtHit not found — runner-side rename?");
            var hookRef = nclMod.ImportReference(hookMi);

            foreach (var (name, paramCount) in new[] { ("StmtHit", 1), ("CStmtHit", 1), ("CStmtHit", 2) })
            {
                var target = FindNclMethod(nclMod, MsType, name, paramCount);
                if (target.Parameters[0].ParameterType.FullName != "System.Int32")
                    throw new InvalidOperationException(
                        $"[Cecil] NavMethodScope.{name}/{paramCount}'s first parameter is "
                        + $"{target.Parameters[0].ParameterType.FullName}, expected System.Int32 — Ncl shape changed; do not commit");

                var body = target.Body;
                var il = body.GetILProcessor();
                var first = body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
                il.InsertBefore(first, il.Create(OpCodes.Call, hookRef));
                if (body.MaxStackSize < 2) body.MaxStackSize = 2;
                Console.Error.WriteLine($"[Cecil] Prepended AlCoverageTracker.OnStmtHit to NavMethodScope.{name}/{paramCount}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // NavMethodScope.Exit() — --capture-values hook (issue #1640, second slice;
        // --coverage above was the first, #1922).
        //
        // NOT a StmtHit hook, despite the coverage block right above being the obvious
        // template — see AlValueCapture.cs's file header for why a StmtHit-based "keep
        // overwriting the latest hit" design is provably one statement stale (BC calls
        // StmtHit(N) BEFORE statement N's own side effect, so the LAST hit never sees the
        // LAST statement's result). Exit() is decompiled as:
        //   internal void Exit() { statementNumber = int.MaxValue; ...; Dispose(); }
        // called from Run()'s `finally` — i.e. exactly once per scope, unconditionally
        // (success or AL error), strictly AFTER every OnRun() statement has completed and
        // strictly BEFORE Dispose() (confirmed by decompile: Dispose() only touches
        // Tree/session bookkeeping, never AL-declared fields). Prepending BEFORE Exit()'s
        // own first instruction means AlValueCapture.OnExit sees both the true final field
        // values AND the real last-executed statementNumber (not yet stomped to
        // int.MaxValue).
        //
        // #2074 later ALSO fed AlValueCapture from the StmtHit hook above (via
        // AlCoverageTracker.OnStmtHit -> AlValueCapture.OnStmtHit — no new Cecil rewrite,
        // it reuses the same call site), for every INTERMEDIATE execution. This Exit()
        // hook stays exactly as it was: it is still the only observation point for
        // whatever the TRUE FINAL statement changed, since there is no StmtHit call after
        // it — the "one statement stale" problem this comment describes for a naive
        // overwrite-on-every-hit design still applies to that one, unavoidable case.
        {
            var nclMod = asm.MainModule;
            const string MsType = "Microsoft.Dynamics.Nav.Runtime.NavMethodScope";
            var hookMi = typeof(AlRunner.Infrastructure.AlValueCapture).GetMethod(
                nameof(AlRunner.Infrastructure.AlValueCapture.OnExit), BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] AlValueCapture.OnExit not found — runner-side rename?");
            var hookRef = nclMod.ImportReference(hookMi);

            var target = FindNclMethod(nclMod, MsType, "Exit", 0);
            if (target.ReturnType.FullName != "System.Void")
                throw new InvalidOperationException(
                    $"[Cecil] NavMethodScope.Exit()'s return type is {target.ReturnType.FullName}, "
                    + "expected void — Ncl shape changed; do not commit");

            var body = target.Body;
            var il = body.GetILProcessor();
            var first = body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, hookRef));
            if (body.MaxStackSize < 1) body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Prepended AlValueCapture.OnExit to NavMethodScope.Exit()");
        }

        // ─────────────────────────────────────────────────────────────────────
        // NavMethodScope.StmtHit(int) / CStmtHit(int[, bool]) — --dap breakpoint hook
        // (issue #1642). A THIRD unconditional prepend on the exact same methods the
        // --coverage block above already hooks — same shape (`ldarg.0; ldarg.1; call`,
        // int unboxed), same reasoning for why it's safe to add a second prepend to an
        // already-once-rewritten method: each rewrite only ever prepends before the
        // CURRENT first instruction, so this call runs AFTER AlCoverageTracker.OnStmtHit
        // (inserted first, above) but still strictly BEFORE StmtHit's own body — the
        // `statementNumber = currentStatementNumber` assignment AlCallStackCapture
        // depends on is untouched either way.
        //
        // AlDapSession.OnStmtHit may BLOCK the calling thread (a breakpoint pause) —
        // unlike AlCoverageTracker.OnStmtHit and AlValueCapture.OnExit, which are both
        // side-effect-free beyond recording. That is intentional and safe: a normal
        // (non-paused) StmtHit still returns immediately (Enabled gate, or the
        // breakpoint-set lookup misses), so the added cost on every AL statement of
        // every test — --dap or not — is one more near-zero-cost volatile read plus a
        // dictionary lookup, not a block.
        {
            var nclMod = asm.MainModule;
            const string MsType = "Microsoft.Dynamics.Nav.Runtime.NavMethodScope";
            var hookMi = typeof(AlRunner.Infrastructure.AlDapSession).GetMethod(
                nameof(AlRunner.Infrastructure.AlDapSession.OnStmtHit), BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "[Cecil] AlDapSession.OnStmtHit not found — runner-side rename?");
            var hookRef = nclMod.ImportReference(hookMi);

            foreach (var (name, paramCount) in new[] { ("StmtHit", 1), ("CStmtHit", 1), ("CStmtHit", 2) })
            {
                var target = FindNclMethod(nclMod, MsType, name, paramCount);
                if (target.Parameters[0].ParameterType.FullName != "System.Int32")
                    throw new InvalidOperationException(
                        $"[Cecil] NavMethodScope.{name}/{paramCount}'s first parameter is "
                        + $"{target.Parameters[0].ParameterType.FullName}, expected System.Int32 — Ncl shape changed; do not commit");

                var body = target.Body;
                var il = body.GetILProcessor();
                var first = body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
                il.InsertBefore(first, il.Create(OpCodes.Call, hookRef));
                if (body.MaxStackSize < 2) body.MaxStackSize = 2;
                Console.Error.WriteLine($"[Cecil] Prepended AlDapSession.OnStmtHit to NavMethodScope.{name}/{paramCount}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ALFunctionTimingExecutionListener cluster — JmpHook→Cecil (Batch 2).
        //
        // Telemetry/diagnostic-only listener reached during AL method execution via
        // NavMethodScope.Run(). Under NO_JMPHOOK=1 it NREs (Start dereferences
        // methodScope.Session.ExtensionMetrics, null on our minimal scopes), which is
        // the dominant failure (1667 tests) after the NavMethodScope ctor unblock. All
        // three methods are void statics the JmpHook already no-ops in normal mode
        // (BcRuntime.cs:404/415/420) with no AL-semantic effect — the Cecil equivalent
        // is an identical void return. NOTE: this is the listener's Start/Exit/
        // EnsureRegistered, NOT its ..cctor — the cctor is the one Batch 1 found to spin
        // when Cecil-rewritten and is left alone.
        // ─────────────────────────────────────────────────────────────────────
        {
            var nclMod = asm.MainModule;
            const string ListenerType = "Microsoft.Dynamics.Nav.Runtime.ALFunctionTimingExecutionListener";
            foreach (var (name, pc) in new[] { ("EnsureRegistered", 0), ("Start", 1), ("Exit", 1) })
            {
                var m = FindNclMethod(nclMod, ListenerType, name, pc);
                if (m.ReturnType.FullName != "System.Void")
                    throw new InvalidOperationException(
                        $"[Cecil] {ListenerType}.{name} returns {m.ReturnType.FullName}, expected void — Ncl shape changed; do not commit");
                ReplaceBodyConst(m, ConstResult.Void);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // CreateTarget family — JmpHook→Cecil migration (Batch 3, the CRUX).
        //
        // CreateTarget() chains through NCLMetadata.GetMetaApplicationObject →
        // NavMetadataNotFoundException because the skeleton has no NCLMetadata; the
        // helper bypasses it by reflectively constructing the target object from the
        // loaded test assembly (re-entering AL execution).
        //
        // A Cecil `ldarg.0; call helper; ret` rewrite was previously REVERTED because
        // it regressed normal mode into a 60s-watchdog safepoint-free spin: the helper
        // re-enters AL execution, and with BOTH the Cecil body AND the JmpHook active on
        // CreateTarget the re-entry double-dispatches and spins (COEXISTENCE).
        //
        // Batch 3 eliminates coexistence with the Cecil-owned skip registry (see
        // CecilOwned at the top of this file + JmpHook.Apply): once a method's key is
        // registered, its JmpHook install becomes a no-op, so the method is owned by
        // EXACTLY ONE mechanism. With coexistence gone, the Cecil rewrite is safe in
        // DEFAULT mode. The helpers live on the BcRuntime partial (CodeunitPatches.cs,
        // XmlPortPatches.cs) and on RecordPatches.
        //
        // Helper return types are `object` for testpage/form/report/query/xmlport but
        // the Ncl CreateTarget() overrides return the concrete Nav* type, so
        // ReplaceBodyWithHelper emits a castclass to the declared return type.
        // ─────────────────────────────────────────────────────────────────────
        {
            var nclMod = asm.MainModule;

            // Helper: resolve the 0-arg, instance, protected-override CreateTarget()
            // specifically (NavRecordHandle also has a 1-arg CreateTarget(NCLMetaTable)).
            static MethodDefinition CreateTarget0(ModuleDefinition mod, string typeFullName)
            {
                var t = mod.GetType(typeFullName)
                    ?? throw new InvalidOperationException(
                        $"[Cecil] type {typeFullName} not found — Ncl shape changed; do not commit");
                return t.Methods.FirstOrDefault(m =>
                    m.Name == "CreateTarget" && m.HasThis && m.HasBody && m.Parameters.Count == 0)
                    ?? throw new InvalidOperationException(
                        $"[Cecil] {typeFullName}.CreateTarget() (0-arg instance) not found — Ncl shape changed; do not commit");
            }

            // 1) NavCodeunitHandle.CreateTarget() → BcRuntime.NavCodeunitHandle_CreateTarget(self).
            //    Helper returns NavCodeunit, matching the override's return — no cast.
            ReplaceBodyWithHelper(nclMod,
                CreateTarget0(nclMod, "Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle"),
                typeof(AlRunner.BcRuntime).GetMethod(
                    "NavCodeunitHandle_CreateTarget", BindingFlags.Public | BindingFlags.Static)!);

            // 2) NavRecordHandle.CreateTarget() → RecordPatches.NavRecordHandle_CreateTarget(self).
            //    Helper lives on RecordPatches (NOT BcRuntime); returns NavRecord — no cast.
            ReplaceBodyWithHelper(nclMod,
                CreateTarget0(nclMod, "Microsoft.Dynamics.Nav.Runtime.NavRecordHandle"),
                typeof(AlRunner.Patches.RecordPatches).GetMethod(
                    "NavRecordHandle_CreateTarget", BindingFlags.Public | BindingFlags.Static)!);

            // 3-7) TestPage / Form / Report / Query / XmlPort CreateTarget() →
            //       BcRuntime.Nav{X}Handle_CreateTarget(object) ; helper returns object,
            //       so castclass to the concrete Nav* return type.
            foreach (var (typeFull, helperName) in new[]
            {
                ("Microsoft.Dynamics.Nav.Runtime.NavTestPageHandle", "NavTestPageHandle_CreateTarget"),
                ("Microsoft.Dynamics.Nav.Runtime.NavFormHandle",     "NavFormHandle_CreateTarget"),
                ("Microsoft.Dynamics.Nav.Runtime.NavReportHandle",   "NavReportHandle_CreateTarget"),
                ("Microsoft.Dynamics.Nav.Runtime.NavQueryHandle",    "NavQueryHandle_CreateTarget"),
                ("Microsoft.Dynamics.Nav.Runtime.NavXmlPortHandle",  "NavXmlPortHandle_CreateTarget"),
            })
            {
                ReplaceBodyWithHelper(nclMod,
                    CreateTarget0(nclMod, typeFull),
                    typeof(AlRunner.BcRuntime).GetMethod(
                        helperName, BindingFlags.Public | BindingFlags.Static)!);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // NavApplicationObjectBase..ctor — JmpHook→Cecil migration (Batch 4 keystone).
        //
        // Every AL codeunit/page/report/Record inherits NavApplicationObjectBase. Its
        // real ctor does `session = base.Tree.Session` (null on our skeleton tree) and
        // `NavCurrentThread.ResolveAppGroup(session)` (NREs). Under NO_JMPHOOK=1 the
        // unpatched ctor leaves session null, so NavRecord..ctor throws
        // ArgumentNullException("A NavRecord must have a parent session ...") — the
        // single dominant Cecil-only failure (≈1371 corpus tests).
        //
        // The helper (ApplicationObjectBasePatches.NavApplicationObjectBaseCtorReplacement)
        // rebuilds the tree via TreeHandler.CreateTreeHandler + FieldPokes the skeleton
        // session, deliberately NOT chaining to the base TreeObject ctor (it sets the tree
        // field directly) — identical reasoning to the NavMethodScope ctor migration above.
        // So `body → ldarg.0..3; call helper; ret` exactly matches the JmpHook semantics.
        //
        // Match the SAME ctor the JmpHook selects: param0 = ITreeObject, param1 =
        // ApplicationObjectId (3 declared params; helper has 4 = self + 3). All params are
        // reference types, so no boxing. Token-safe: imports only our own helper methodRef.
        // ─────────────────────────────────────────────────────────────────────
        {
            var nclMod = asm.MainModule;
            const string AoType = "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase";
            var aoTypeDef = nclMod.GetType(AoType)
                ?? throw new InvalidOperationException(
                    $"[Cecil] type {AoType} not found — Ncl shape changed; do not commit");
            var aoCtor = aoTypeDef.Methods.FirstOrDefault(m =>
                m.IsConstructor && m.HasBody && m.Parameters.Count == 3
                && m.Parameters[0].ParameterType.Name == "ITreeObject"
                && m.Parameters[1].ParameterType.Name == "ApplicationObjectId")
                ?? throw new InvalidOperationException(
                    $"[Cecil] NavApplicationObjectBase 3-arg ctor (ITreeObject,ApplicationObjectId,NCLStaticMetadata) not found — Ncl shape changed; do not commit");
            ReplaceBodyWithHelper(nclMod, aoCtor,
                typeof(AlRunner.BcRuntime).GetMethod(
                    "NavApplicationObjectBaseCtorReplacement", BindingFlags.Public | BindingFlags.Static)!);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Record / session / data-access path — JmpHook→Cecil ATOMIC migration
        // (Batch 6, the LINCHPIN).
        //
        // Batch 4 proved that migrating ANY SINGLE record-path method piecemeal
        // (even just TempTableDataProvider..ctor) HANGS default mode: the still-
        // JmpHook'd remainder of the write/find/getter path reaches the migrated
        // method through R2R-precompiled / inlined call sites, and a Cecil body
        // coexisting with the JmpHook'd remainder produces the safepoint-free
        // 100%-CPU coexistence spin (same class Batch 3 removed for CreateTarget).
        //
        // The fix is to migrate the ENTIRE remaining record/session/data-access set
        // to Cecil in ONE build so the whole path is single-mechanism (Cecil) — no
        // coexistence → no spin. Each method below already has a working JmpHook
        // replacement helper (RecordPatches.* / TelemetryPatches.* / HelperShims.*);
        // the migration is mechanical: rewrite the Ncl body to forward to that same
        // helper (ReplaceBodyWithHelper handles arg-boxing + return-cast), and add
        // every key to CecilOwned so the existing Hook(...) install auto-no-ops.
        //
        // The *Async no-op targets (VerifySecurityFilters*, MoveLinksAsync,
        // UpdateReferencesOnRenameAsync) all return the NON-generic ValueTask and map
        // to HelperShims.ReturnValueTask{2..5} (which return `default` ValueTask) —
        // an exact value-type return match, so no completed-task shim is needed.
        // InternalFindRecordWithoutCheckingValuesAsync returns ValueTask<bool> and is
        // forwarded to its existing ValueTask<bool>-returning replacement.
        //
        // Token-safety: ReplaceBodyWithHelper / ReplaceBodyConst import only our own
        // helper memberRefs and emit only ldc/box/castclass against types already in
        // Ncl's tables — no new Ncl typeRefs/memberRefs (no R2R caller corruption).
        // ─────────────────────────────────────────────────────────────────────
        {
            var nclMod = asm.MainModule;
            const string Rt = "Microsoft.Dynamics.Nav.Runtime.";

            // Resolve a method by (type, name, exact ordered param-type SimpleNames) so
            // we pick the SAME overload the JmpHook install picks (several targets have
            // sibling overloads). Loud-fails if the shape changed.
            MethodDefinition ByParams(string typeFull, string name, params string[] paramTypeNames)
            {
                var t = nclMod.GetType(typeFull)
                    ?? throw new InvalidOperationException(
                        $"[Cecil] type {typeFull} not found — Ncl shape changed; do not commit");
                var m = t.Methods.FirstOrDefault(x =>
                    x.Name == name && x.HasBody
                    && x.Parameters.Count == paramTypeNames.Length
                    && x.Parameters.Select((p, i) => p.ParameterType.Name == paramTypeNames[i]).All(b => b))
                    ?? throw new InvalidOperationException(
                        $"[Cecil] {typeFull}.{name}({string.Join(",", paramTypeNames)}) not found — Ncl shape changed; do not commit");
                return m;
            }

            MethodInfo H(Type cls, string name) =>
                cls.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"[Cecil] helper {cls.Name}.{name} not found");

            var recordPatches = typeof(AlRunner.Patches.RecordPatches);
            var helperShims   = typeof(AlRunner.BcRuntime);          // NoOp*/ReturnValueTask*/ReturnTrue etc are BcRuntime statics
            var telemetry     = typeof(AlRunner.BcRuntime);          // NavServerEventSource_* are BcRuntime partials too
            var navRecordIdP  = typeof(AlRunner.Patches.NavRecordIdPatches);

            // ── NCLMetadata lookups (RecordPatches.cs:150-189) ──────────────────
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NCLMetadata", "GetMetaTableById", "Int32", "Boolean", "Int32"),
                H(recordPatches, "NCLMetadata_GetMetaTableById"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NCLMetadata", "GetMetaApplicationObject", "ObjectType", "Int32", "Boolean", "Int32"),
                H(recordPatches, "NCLMetadata_GetMetaApplicationObjectByType"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NCLMetadata", "GetMetaApplicationObject", "ApplicationObjectId", "Boolean", "Int32"),
                H(recordPatches, "NCLMetadata_GetMetaApplicationObjectById"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NCLMetaTable", "GetFieldByNo", "Int32", "Int32"),
                H(recordPatches, "NCLMetaTable_GetFieldByNoExt"));

            // ── NavRecord TestField navigation-action guards (#1938) ──────────────
            // See NavRecordTestFieldNavigationPatches.cs for the full mechanism. Both
            // GetPageToOpen and TryAddTestFieldAction can independently throw
            // NavMetadataNotFoundException for a Base App page the runner never loaded,
            // hijacking the real TestField error the caller was about to throw — guard both.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecord", "GetPageToOpen", "NCLMetaTable"),
                H(recordPatches, "NavRecord_GetPageToOpen"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecord", "TryAddTestFieldAction", "NCLMetaField"),
                H(recordPatches, "NavRecord_TryAddTestFieldAction"));

            // ── NavSession getters / DataAccessSource (RecordWritePatches.cs:84-104,482) ──
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_DataAccessSource", 0),
                H(recordPatches, "NavSession_get_DataAccessSource"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_Database", 0),
                H(recordPatches, "NavSession_get_Database"));
            // NavTenant.get_Database — R2R inlines NavSession.Database → NavTenant.Database
            // past the redirect above, so callers (ALNavApp.ALGetModuleInfo via telemetry)
            // hit NavTenant.Database directly. It throws ArgNull("NavDatabase") on the
            // skeleton (database LazyEx null by design). Return the skeleton NavDatabase.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavTenant", "get_Database", 0),
                H(recordPatches, "NavTenant_get_Database"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_SortingProperties", 0),
                H(recordPatches, "NavSession_get_SortingProperties"));

            // ── DataAccessSource.GetDataAccessForTable (RecordWritePatches.cs:106) ──
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "DataAccessSource", "GetDataAccessForTable", "NCLMetaTable", "Boolean"),
                H(recordPatches, "NavDataAccessSource_GetDataAccessForTable"));

            // ── DataAccessSource.GetDataAccessForQuery(NCLMetaQueryDefinition) ──
            // Multi-dataitem (join) query support: single source → original behaviour;
            // empty-table join → root DataAccess (no rows); join with data → OOS throw.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "DataAccessSource", "GetDataAccessForQuery", "NCLMetaQueryDefinition"),
                H(recordPatches, "DataAccessSource_GetDataAccessForQuery"));

            // ── TempTableDataProvider ctor (NavSession,NCLMetaTable) + CalcNumeric ──
            {
                var ttdp = nclMod.GetType(Rt + "TempTableDataProvider")
                    ?? throw new InvalidOperationException("[Cecil] TempTableDataProvider not found — do not commit");
                var ttdpCtor = ttdp.Methods.FirstOrDefault(m =>
                    m.IsConstructor && m.HasBody && m.Parameters.Count == 2
                    && m.Parameters[0].ParameterType.Name == "NavSession"
                    && m.Parameters[1].ParameterType.Name == "NCLMetaTable")
                    ?? throw new InvalidOperationException("[Cecil] TempTableDataProvider(NavSession,NCLMetaTable) ctor not found — do not commit");
                ReplaceBodyWithHelper(nclMod, ttdpCtor, H(recordPatches, "TempTableDataProviderCtorReplacement"));
            }
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "TempTableDataProvider", "CalcNumeric", "CalcNumericProviderRequest"),
                H(recordPatches, "TempTableDataProvider_CalcNumeric"));

            // ── TempTableDataProvider.{Exists,CalcMinMax,CalcSums} — the Date store's safety
            //    net under per-request materialisation (issue #2648) ────────────────────────
            // The find, count and keyed-Get guards on DataAccess materialise exactly what their
            // request can select. Three read paths never reach DataAccess at all — a FlowField
            // calculation (FlowFieldsHelper) and a TableRelation check
            // (RecordImplementation.ValidateRelation) go straight to the provider — so they carry
            // no request the guards could read. MEASURED on this branch with a prepend on
            // DataAccess.ExistsAsync / CalcMinMaxAsync / CalcSumsAsync instead: the prepend
            // applied and never fired once, and `count(Date …)` went 73,049 -> 0,
            // `exist(Date …)` Yes -> No, `min(Date."Period Start")` 1900-01-01 -> blank.
            //
            // The helper materialises the whole configured window on the first such read of the
            // Date store and is a ConditionalWeakTable miss for every other table. CalcNumeric is
            // not in this list because Cecil REPLACES its body above; the same call sits at the
            // top of the replacement instead.
            //
            // It takes the PROVIDER REQUEST as well as the provider (#3044). Every one of these
            // three reads carries a DataProviderRequest, and a DataProviderRequest carries the
            // same FiltersAndMarks a DataCacheRequest does — on the Exists path it is literally
            // the same object, since DataAccess.ExistsAsync passes request.FiltersAndMarks
            // straight through. That lets the net tell "nothing has narrowed this request" from
            // "a DataAccess-level guard already materialised every row this request can select",
            // which is what Record.IsEmpty() over a closed range hits: the ExistsAsync guard
            // materialises 25 rows and the net used to materialise 86,885 more behind it.
            foreach (var providerRead in new[]
                     {
                         ("Exists", "ExistsProviderRequest"),
                         ("CalcMinMax", "CalcMinMaxProviderRequest"),
                         ("CalcSums", "CalcSumsProviderRequest"),
                     })
            {
                PrependStaticCall(nclMod,
                    ByParams(Rt + "TempTableDataProvider", providerRead.Item1, providerRead.Item2),
                    H(recordPatches, "EnsureDateStoreCoversProviderRequest"),
                    argSlots: 2); // `this` — the provider — and the provider request
            }

            // ── BLOB store isolation for database-backed tables (issue #1751) ──────
            // Ncl's TempTableDataProvider.Insert copies the record's NavBLOB into the
            // stored row BY REFERENCE and only CloneBlobs()es the dirty ones, so a BLOB
            // that carried no value at Insert stays shared with the record variable —
            // and a later `CreateOutStream`+write with no Modify() mutates the stored
            // row. Real BC does exactly this for a `temporary` record and the opposite
            // for a database-backed one (corpus 60940, green on BC 27.5 and 28.3), so
            // the runner cannot simply always copy: every runner table is backed by
            // this same provider.
            //
            // Two prepends. The first latches which kind of provider this insert is
            // for; the second detaches the stored row's BLOBs when it is the SQL
            // stand-in. Both leave the original bodies intact, so the dirty-BLOB clone
            // Ncl already performs is unchanged. See Patches/BlobStoreIsolationPatches.cs.
            {
                var blobIsolation = typeof(AlRunner.Patches.BlobStoreIsolationPatches);

                PrependStaticCall(nclMod,
                    ByParams(Rt + "TempTableDataProvider", "Insert",
                        "Int32", "MutableRecordBuffer", "InsertOptions", "ReadOnlyRecordBuffer&"),
                    H(blobIsolation, "OnBeforeStoreInsert"),
                    argSlots: 1); // `this` — the provider

                PrependStaticCall(nclMod,
                    ByParams(Rt + "TempTableRecordBuffer", "CloneBlobs", "MutableRecordBuffer"),
                    H(blobIsolation, "DetachStoredBlobs"),
                    argSlots: 1); // `this` — the freshly stored row

                // ── Rowversion stamping (issue #1980) ────────────────────────────────
                // SQL assigns a rowversion on every insert/update; the SQL stand-in never
                // did, so NavRecord.HasBeenInserted (== "timestamp field is non-zero") was
                // false for every stored row and NavForm.SaveRecordAsync always chose
                // Insert — CurrPage.SaveRecord() in a field OnValidate dup-keyed on rows
                // reached via GoToRecord. Stamp the record buffer before Insert/Modify run;
                // the guard inside the helper keeps `temporary` records at timestamp 0,
                // exactly like real BC. See Patches/RowVersionPatches.cs for the audit.
                var rowVersion = typeof(AlRunner.Patches.RowVersionPatches);

                PrependStaticCall(nclMod,
                    ByParams(Rt + "TempTableDataProvider", "Insert",
                        "Int32", "MutableRecordBuffer", "InsertOptions", "ReadOnlyRecordBuffer&"),
                    H(rowVersion, "OnBeforeInsert"),
                    argSlots: 3); // this, companyToken, recordBuffer

                PrependStaticCall(nclMod,
                    ByParams(Rt + "TempTableDataProvider", "Modify",
                        "Int32", "MutableRecordBuffer", "Boolean", "ReadOnlyRecordBuffer&"),
                    H(rowVersion, "OnBeforeModify"),
                    argSlots: 3); // this, companyToken, recordBuffer

                // ── Rename store-aliasing boundary for `temporary` records (issue #1765) ──
                // A temporary record's BLOB committed with Modify() is LOST across a
                // subsequent Rename() on real BC (corpus 60944, green on BC 27.5/28.3) —
                // the mirror-image surprise to the leak fixed above. Rename() routes
                // through this SAME ModifyAllTrees (RecordImplementation.RenameRecordAsync
                // calls dataAccess.ModifyAsync, same as a plain Modify), so one more
                // prepend here marks the renamed row's carried-over (non-dirty) BLOB as
                // ineligible for FlowFieldPatches.LoadBlobField's by-key CalcFields
                // reload — see BlobStoreIsolationPatches.OnModifyAllTrees for the full
                // reasoning and why the database-backed shape is untouched.
                PrependStaticCall(nclMod,
                    ByParams(Rt + "TempTableDataProvider", "ModifyAllTrees",
                        "MutableRecordBuffer", "TempTableRecordBuffer", "TempTableRecordBuffer", "Boolean"),
                    H(blobIsolation, "OnModifyAllTrees"),
                    argSlots: 4); // `this`, mutableRecordBuffer, workTableBuffer, storedTableBuffer
            }

            // ── TempTableDataProvider.Find / FindFromPosition (query column projection) ──
            // Single-dataitem query reads route through GetDataAccessForQuery → the same
            // in-memory TempTableDataProvider that holds the inserted rows. The provider is
            // TABLE-shaped: it returns ReadOnlyRecordBuffers indexed by table field
            // ColumnIndex, but NavQuery.GetColumnValue reads CurrentDataRow[queryColumn
            // .ColumnIndex] (the QUERY result slot), so columns come back as 0. Real BC's
            // SQL provider projects via a SELECT; the temp provider never does. These two
            // replacements forward to the provider's own private FindImplementation /
            // FindByPositionImplementation (storage/filter/sort untouched) and, ONLY when
            // the request targets an NCLMetaQuery, re-shape each table buffer into a
            // query-shaped buffer. Ordinary table reads pass straight through. See
            // RecordPatches.QueryProjection.cs.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "TempTableDataProvider", "Find", "FindProviderRequest", "Func`1"),
                H(recordPatches, "TempTableDataProvider_Find"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "TempTableDataProvider", "FindFromPosition", "PositionedFindProviderRequest", "Func`1"),
                H(recordPatches, "TempTableDataProvider_FindFromPosition"));

            // ── DataAccess.InnerFindAsync — virtual Field table (2000000041) managed bypass ──
            // The virtual Field system table cannot go through BC's native InnerFindAsync: its
            // SQL transactional-cache prologue (per-object SystemId/PrimaryKey caches +
            // table-version tokens, keyed by ObjectId) AVs because the virtual table is never
            // registered in those structures (the service tier serves it from a dedicated
            // VirtualDataProvider that bypasses this cache; crash file-proven, even with zero
            // rows and TableType=Temporary). We PREPEND a guard to InnerFindAsync that, ONLY when
            // request.MetaApplicationObject.ObjectId == 2000000041, calls our managed find
            // (provider.Find → ResultSet → ResultSetEnumerator — InnerFindAsync's own safe tail)
            // and returns; every other table falls through to the ORIGINAL InnerFindAsync IL
            // untouched. We target InnerFindAsync (NOT the tiny FindAsync, which is R2R-inlined
            // into its callers and so a rewrite of it never fires under default R2R — file-traced).
            // The helper returns a boxed ValueTask<ResultSetEnumerator>; the prepended IL
            // unbox.any's it to the declared return type. See RecordPatches.FieldFindIntercept.cs.
            //
            // The predicate is also where the Date virtual table (2000000007) gets its window
            // guard: a Date find passes through the predicate on its way to the ORIGINAL
            // InnerFindAsync, and the predicate widens the materialised Date window to cover
            // the closed bounds that find's "Period Start" filter names (or throws past the
            // row cap). The find request is the only place the runner ever sees that filter.
            PrependFieldFindGuard(nclMod,
                ByParams(Rt + "DataAccess", "InnerFindAsync", "FindCacheRequest", "Boolean", "Func`1"),
                H(recordPatches, "DataAccess_IsManagedFindRequest"),
                H(recordPatches, "DataAccess_FieldFindManaged"));

            // ── DataAccess.CountAsync — Date virtual table (2000000007) window guard ─────
            // Record.Count() reaches a CountCacheRequest, not a FindCacheRequest, so the find
            // guard above never sees it. Without this prepend a Count over a range outside the
            // materialised Date window would answer with however many rows the window happens
            // to hold — the silent short answer the window guard exists to prevent. The helper
            // widens the window (or throws past the row cap) and returns; the original
            // CountAsync body then runs unchanged, for this and every other table.
            //
            // This comment used to read "Record.Count() / IsEmpty()". IsEmpty() has never
            // reached CountAsync — see the ExistsAsync prepend below (#3006).
            PrependStaticCall(nclMod,
                ByParams(Rt + "DataAccess", "CountAsync", "CountCacheRequest"),
                H(recordPatches, "DataAccess_DateWindowGuardForCount"),
                argSlots: 2); // `this` — the DataAccess — and the count request

            // ── DataAccess.CountAsync — virtual Field table (2000000041) on-demand populate ──
            // Same gap, one table over. The Field table's rows for a given TableNo are built on
            // demand, and until #2792 the ONLY place that happened for a table nothing else had
            // materialised was the find guard above. Record.Count() / IsEmpty() build a
            // CountCacheRequest, so they missed it and answered over an empty store.
            //
            // Measured on main, one Base Application bundle, table 5802 never opened as a Record:
            //   Field.SetRange(TableNo, 5802); Count()   -> 0   IsEmpty() -> true
            //   Field.SetRange(TableNo, 5802); FindSet() -> 85 rows
            // Same filter, same table, two different answers — and 0 is indistinguishable from
            // "this table has no fields". A service tier computes the virtual table per request
            // and answers 85 both ways.
            PrependStaticCall(nclMod,
                ByParams(Rt + "DataAccess", "CountAsync", "CountCacheRequest"),
                H(recordPatches, "DataAccess_FieldGuardForCount"),
                argSlots: 2); // `this` — the DataAccess — and the count request

            // ── DataAccess.InternalTryGetByPrimaryKeyAsync — Aggregate Permission Set live
            //    redrive (issue #2504) ──────────────────────────────────────────────────
            // Record.Get() with a full primary key never reaches InnerFindAsync at all — it
            // takes DataAccess's OWN primary-key path (InternalTryGetByPrimaryKeyAsync →
            // provider.TryGetByPrimaryKeyAsync/TryGetBySystemIdAsync, confirmed by decompiling
            // Ncl.dll), so the InnerFindAsync guard above never sees a plain Get(). Real BC's
            // own VirtualAndTempTransactionalDataCache.TryGetByPrimaryKey unconditionally
            // returns Unknown (a cache miss) for every request, so a real service tier
            // recomputes this table on every single Get() too, not only the first. We target
            // InternalTryGetByPrimaryKeyAsync (not the tiny public TryGetByPrimaryKeyAsync,
            // which just forwards to it) for the same R2R-inlining reason InnerFindAsync is
            // targeted over FindAsync above. For every table but Aggregate Permission Set this
            // is one int comparison and returns; the original body then runs unchanged.
            PrependStaticCall(nclMod,
                ByParams(Rt + "DataAccess", "InternalTryGetByPrimaryKeyAsync", "PrimaryKeyCacheRequest"),
                H(recordPatches, "DataAccess_AggregatePermissionSetGuardForGet"),
                argSlots: 2); // `this` — the DataAccess — and the primary-key request

            // ── DataAccess.InternalTryGetByPrimaryKeyAsync — Date window guard (issue #2648) ──
            // The Date table shares the primary-key path described above and was left behind
            // when #2504 fixed it for Aggregate Permission Set: a keyed Date.Get() reached
            // neither the InnerFindAsync guard nor the CountAsync one, so the materialised
            // window was never extended for it. Measured on main, in separate processes:
            // Date.Get(Date, 18500101D) answered FALSE while a FindFirst over the same day
            // answered TRUE. Same table, same period, opposite answers.
            PrependStaticCall(nclMod,
                ByParams(Rt + "DataAccess", "InternalTryGetByPrimaryKeyAsync", "PrimaryKeyCacheRequest"),
                H(recordPatches, "DataAccess_DateWindowGuardForGet"),
                argSlots: 2); // `this` — the DataAccess — and the primary-key request

            // ── DataAccess.InternalTryGetByPrimaryKeyAsync — virtual Field table (#2792) ──────
            // The third of the three request paths, and the Field table was left behind on it by
            // both #2504 and #2648. Measured on main, table 5803 never opened as a Record:
            // Field.Get(5803, 1) answered FALSE, where a service tier answers TRUE with
            // FieldName "Entry No.". The guard builds that table's Field rows from the RECORD ID
            // (a keyed Get carries its key there and may carry no TableNo filter at all) and
            // then returns; the original body runs unchanged, for this and every other table.
            PrependStaticCall(nclMod,
                ByParams(Rt + "DataAccess", "InternalTryGetByPrimaryKeyAsync", "PrimaryKeyCacheRequest"),
                H(recordPatches, "DataAccess_FieldGuardForGet"),
                argSlots: 2); // `this` — the DataAccess — and the primary-key request

            // ── DataAccess.ExistsAsync — Date virtual table (2000000007), the FOURTH path ───
            // Record.IsEmpty() does not take the count path. RecordImplementation.IsEmptyAsync
            // calls its OWN ExistsAsync, which builds an ExistsCacheRequest and reaches
            // DataAccess.ExistsAsync — decompiled from Ncl.dll 28.1, and not what the count
            // guard's comment claimed for a whole release. Measured on main, one process, one
            // record variable, on consecutive lines:
            //
            //   Date.SetRange("Period Start", 18500101D..18500107D);
            //   IsEmpty() -> TRUE      Count() -> 7
            //
            // A service tier computes this table across years 1..9999 and answers 7 both ways,
            // so TRUE is a wrong answer, not a missing feature — and the quiet kind, because
            // "this range holds no periods" is what IsEmpty() returning true normally means.
            // ExistsAsync is a large async state machine, so unlike the tiny FindAsync it is
            // not R2R-inlined past the prepend.
            PrependStaticCall(nclMod,
                ByParams(Rt + "DataAccess", "ExistsAsync", "ExistsCacheRequest"),
                H(recordPatches, "DataAccess_DateWindowGuardForExists"),
                argSlots: 2); // `this` — the DataAccess — and the exists request

            // ── DataAccess.ExistsAsync — virtual Field table (2000000041), the FOURTH path ────
            // Record.IsEmpty() does not take the count path. RecordImplementation.IsEmptyAsync
            // calls its own ExistsAsync, which builds an ExistsCacheRequest and reaches
            // DataAccess.ExistsAsync — decompiled from Ncl.dll. The Date count guard's comment
            // used to claim otherwise; #3006 corrected it and added the Date half of this same
            // prepend directly above. With the count and Get guards in place but not this
            // one, Field.SetRange(TableNo, 270); IsEmpty() still answered TRUE while Count()
            // over the same filter answered 2. ExistsAsync is a large async state machine, so
            // unlike the tiny FindAsync it is not R2R-inlined past the prepend.
            PrependStaticCall(nclMod,
                ByParams(Rt + "DataAccess", "ExistsAsync", "ExistsCacheRequest"),
                H(recordPatches, "DataAccess_FieldGuardForExists"),
                argSlots: 2); // `this` — the DataAccess — and the exists request

            // ── NavDatabase / NavRecordId collation comparers ───────────────────
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavDatabase", "get_CollationAwareStringComparer", 0),
                H(recordPatches, "NavDatabase_get_CollationAwareStringComparer"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavRecordId", "get_CollationAwareStringComparer", 0),
                H(navRecordIdP, "NavRecordId_get_CollationAwareStringComparer"));

            // ── NavDialog progress dialog: no-op, exactly as BC does headless ─────────────
            //
            // Dialog.Open / Update / Close have no AL-observable effect on data — they drive a
            // window. BC itself already skips the whole body for a non-interactive caller:
            //     if (IsWebServiceClientRequest(base.Tree.Session)) return default(ValueTask);
            // which is precisely the runner's situation, so a no-op here is BC's own answer for
            // this case rather than a substitution of one. (Confirm/StrMenu are NOT no-oped — see
            // the note above: those carry a real answer and route to the test's handler.)
            //
            // These were listed as Cecil-owned but their rewrite block had been removed, so
            // nothing replaced them AND the legacy JmpHook auto-skipped on the ownership claim.
            // The original body then NRE'd at its first instruction on a null base.Tree, taking
            // out every AL path that opens a progress window — including Base App codeunit 2
            // "Company-Initialize", which is why the runner's company had no Company Information.
            {
                var navDialog = nclMod.GetType(Rt + "NavDialog")
                    ?? throw new InvalidOperationException("[Cecil] NavDialog not found in Ncl — shape changed");

                var alOpenAsync = navDialog.Methods.FirstOrDefault(m =>
                    m.Name == "ALOpenAsync" && m.HasBody && m.HasThis && m.Parameters.Count == 3)
                    ?? throw new InvalidOperationException("[Cecil] NavDialog.ALOpenAsync/3 not found — do not commit");
                ReplaceBodyWithHelper(nclMod, alOpenAsync, H(helperShims, "ReturnValueTask4"));

                int dialogNoOps = 1;
                foreach (var m in navDialog.Methods.Where(m =>
                             m.Name == "ALUpdateAsync" && m.HasBody && m.HasThis && m.Parameters.Count <= 2))
                {
                    ReplaceBodyWithHelper(nclMod, m,
                        H(helperShims, m.Parameters.Count switch
                        {
                            0 => "ReturnValueTask2",   // +1 for `this`; shim arity counts it
                            1 => "ReturnValueTask2",
                            _ => "ReturnValueTask3",
                        }));
                    dialogNoOps++;
                }

                // ALClose belongs with them. Its own body checks BC's "is the dialog open"
                // state, which Open no longer sets now that Open is a no-op — so leaving Close
                // real turned a silent NRE into a loud "The operation failed because the dialog
                // box is not open." on the very next line of any AL that opens a progress
                // window. Open and Close are one no-op or neither.
                var alClose = navDialog.Methods.FirstOrDefault(m =>
                    m.Name == "ALClose" && m.HasBody && m.HasThis && m.Parameters.Count == 0)
                    ?? throw new InvalidOperationException("[Cecil] NavDialog.ALClose/0 not found — do not commit");
                ReplaceBodyWithHelper(nclMod, alClose, H(helperShims, "NoOp_OneArg"));
                dialogNoOps++;

                Console.Error.WriteLine($"[Cecil] NavDialog: {dialogNoOps} progress-dialog method(s) → headless no-op");
            }

            // ── NavRecord no-ops (Dispose) ──
            // Dispose(bool) → NoOp2.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecord", "Dispose", "Boolean"),
                H(helperShims, "NoOp2"));

            // NavRecord.GetCallerRecord(NavSession) — faithful reimplementation (#1781: nested
            // Validate re-snapshotting xRec because this used to be a blanket ReturnNull hook).
            // See GetCallerRecordPatches.NavRecord_GetCallerRecord for why this reads the
            // tracked CurrentMethodScope backing field directly instead of going through
            // NavSession.CurrentMethodScope's own (deliberately flattened) getter.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavRecord", "GetCallerRecord", 1),
                H(helperShims, "NavRecord_GetCallerRecord"));
            // NavRecord.IsGlobalTriggerImplemented is NOT rewritten: BC's body is
            // `(GlobalTriggers.GetTriggersOnTable(TableID) & wanted) != 0`, which now works
            // because GetTriggersOnTable is real again. It used to be forced to false, so
            // the write pipeline skipped global-trigger dispatch entirely.
            //
            // NavRecord.UpdateReferencesOnRenameAsync(List,NavRecord) is NOT rewritten
            // either (it was a ReturnValueTask3 no-op until issue #1730): BC's real body
            // implements rename propagation — for every validated TableRelation pointing
            // at the renamed table it rewrites the referencing rows via ModifyAllAsync /
            // RenameAsync with triggers off. That path runs entirely on metaTable relation
            // metadata and the in-memory DataAccess, both of which the runner populates,
            // so the real body is the faithful behaviour. No-opping it silently left child
            // rows pointing at the old key.
            //
            // ── NCLMetaTable.ComputeReferencingRelations(NavAppGroup,NCLMetaTable) ──
            // The one thing that real body needs and the skeleton cannot give it: BC
            // computes the referencing-relations reverse index over
            // ObjectLoader.MetadataCache.GetSnapshotOfAllNonVirtualMetaTables(...), and
            // ObjectLoader is null on runner-built meta tables (NRE). The replacement
            // computes the identical index over the runner's metatable cache — see the
            // equivalence note on RecordPatches.NCLMetaTable_ComputeReferencingRelations.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NCLMetaTable", "ComputeReferencingRelations", "NavAppGroup", "NCLMetaTable"),
                H(recordPatches, "NCLMetaTable_ComputeReferencingRelations"));

            // ── RecordLink.MoveLinksAsync(NavRecord,NavRecord) static → ReturnValueTask2 ──
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "RecordLink", "MoveLinksAsync", "NavRecord", "NavRecord"),
                H(helperShims, "ReturnValueTask2"));

            // ── NavManagementTasks.CopyCompany(String,String) instance void → NoOp3 ──
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavManagementTasks", "CopyCompany", "String", "String"),
                H(helperShims, "NoOp3"));

            // ── NCLMetaApplicationObject (CheckApplicationObjectIsValid / ClrType) ──
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NCLMetaApplicationObject", "CheckApplicationObjectIsValid", "NavApplicationObjectBase"),
                H(helperShims, "NoOp2"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NCLMetaApplicationObject", "get_ApplicationObjectClrType", 0),
                H(recordPatches, "NCLMetaApplicationObject_get_ApplicationObjectClrType"));
            // ── NCLMetaApplicationObject.get_ApplicationObjectConstructor (Batch 7) ──
            // The real getter calls CompileAndLoadClrObject under a lock on a null
            // `nclMetaObjectCLRTypeContainer` on a skeleton meta → NRE in Monitor.
            // The JmpHook returns null (ReturnNull_OneArg); callers like
            // NCLMetaTable.CreateObjectInstance fall back to constructing NavRecord
            // directly via `new NavRecord(parent, TableId, this, …)`. The reference-
            // type-null return is exactly `ldnull; ret`. Migrating it to Cecil makes
            // the whole AL insert/construction path (ALInsertAsync → get_OldRecord →
            // CreateObjectInstance → this getter) single-mechanism, killing the
            // JmpHook+Cecil coexistence spin that hangs default mode.
            ReplaceBodyConst(
                FindNclMethod(nclMod, Rt + "NCLMetaApplicationObject", "get_ApplicationObjectConstructor", 0),
                ConstResult.Null);
            // ── NCLMetaApplicationObject.Populate() / CompileAndLoadClrObject() (Batch 7) ──
            // These are the direct cluster siblings of get_ApplicationObjectConstructor:
            // the real getter and CreateObjectInstance both walk through Populate /
            // CompileAndLoadClrObject, which NRE on a hand-built skeleton meta (null
            // nclMetaObjectCLRTypeContainer / null ObjectLoader). Both are JmpHook'd to
            // NoOp_OneArg in MetadataPatches; migrating them to Cecil void no-ops keeps
            // the whole construction path single-mechanism — the still-JmpHook'd siblings
            // were the coexistence partners that spun default mode on the AL-Validate /
            // construction path (e.g. Library - No. Series.CreateNoSeriesLine). Both are
            // 0-param instance void → ReplaceBodyConst(Void) emits `ret`.
            ReplaceBodyConst(
                FindNclMethod(nclMod, Rt + "NCLMetaApplicationObject", "Populate", 0),
                ConstResult.Void);
            ReplaceBodyConst(
                FindNclMethod(nclMod, Rt + "NCLMetaApplicationObject", "CompileAndLoadClrObject", 0),
                ConstResult.Void);

            // ── NCLMetaTable.CreateObjectInstance (concrete-type-aware) ──────────
            // With ApplicationObjectConstructor forced null above, the original
            // CreateObjectInstance falls back to `new NavRecord(...)` (base type). For
            // OldRecord (the xRec before-image of a concrete record) that base NavRecord
            // then fails the compiled `xRec => (Record{Id})OldRecord` cast with
            // InvalidCastException. The replacement builds the concrete Record{Id} CLR
            // type (and binds table extensions), matching what the real constructor
            // delegate would produce. See RecordPatches.CreateObjectInstance.cs.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NCLMetaTable", "CreateObjectInstance",
                         "ITreeObject", "Boolean", "NavRecord", "String", "SecurityFiltering"),
                H(recordPatches, "NCLMetaTable_CreateObjectInstance"));

            // ── RecordImplementation path (perms / find / security / IsOpen) ─────
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "RecordImplementation", "VerifyPermissions", "PermissionMask", "Boolean"),
                H(helperShims, "NoOp3"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "RecordImplementation", "InternalFindRecordWithoutCheckingValuesAsync",
                         "DataError", "PrimaryKeyCacheRequest", "Boolean", "Boolean"),
                H(helperShims, "RecordImpl_InternalFindRecordWithoutCheckingValuesAsync"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "RecordImplementation", "VerifySecurityFiltersOnRecordAsync",
                         "IRecordBuffer", "FilterFieldDictionary", "Boolean", "Boolean"),
                H(helperShims, "ReturnValueTask5"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "RecordImplementation", "VerifySecurityFiltersAsync",
                         "MutableRecordBuffer", "SecurityFilterType"),
                H(helperShims, "ReturnValueTask3"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "RecordImplementation", "get_IsOpen", 0),
                H(helperShims, "ReturnTrue"));

            // ── NavServerEventSource (telemetry) ────────────────────────────────
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavServerEventSource", "get_Log", 0),
                H(telemetry, "NavServerEventSource_get_Log"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavServerEventSource", "WritePermissionUncheckedEvent",
                         "String", "String", "String", "String", "Int32", "Int32", "Int32", "Int32", "Int32", "Int32"),
                H(telemetry, "NavServerEventSource_WritePermissionUncheckedEvent"));

            // ── SequentialUuidCreator.NativeMethods.NewSequentialId() → Guid ─────
            {
                var nm = nclMod.GetType(Rt + "Data.SequentialUuidCreator/NativeMethods");
                var newSeq = nm?.Methods.FirstOrDefault(m => m.Name == "NewSequentialId" && m.HasBody && m.Parameters.Count == 0);
                if (newSeq != null)
                    ReplaceBodyWithHelper(nclMod, newSeq, H(recordPatches, "NewSequentialId_Replacement"));
            }

            // ── TempTableStatistics.ReportIncrementChange(int,int,int) → NoOp4 ──
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "TempTableStatistics", "ReportIncrementChange", "Int32", "Int32", "Int32"),
                H(helperShims, "NoOp4"));

            // NOTE: NavSystemCodeunitGlobalTriggers.GetTriggersOnTable is NOT rewritten.
            // It used to return Triggers.None unconditionally, which silently disabled every
            // global/database table trigger in the runner. BC's own body invokes
            // GetDatabaseTableTriggerSetup on the Global Triggers codeunit (2000000002) and
            // lets the AL subscribers decide the per-table mask — which is the answer AL
            // authors wrote and the only faithful one.
            // ── NavSession getter cluster + GetActiveCompany + NavStream.Target ──
            // These are installed via Hook(...) from the main ApplyAllPatches (BcRuntime.cs
            // ~525-570 / 2291 / 2380), NOT ApplyRecordPatches — but they sit on the same
            // record/session R2R-inlined call path (CloneRecord → GetActiveCompany,
            // NavMethodScope.Run → SyncFormatSettings, etc.), so they MUST migrate in the
            // same atomic build to keep the path single-mechanism.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_CurrentMethodScope", 0),
                H(helperShims, "GetCurrentMethodScopeReplacement"));
            // NavSystemCodeunit.Session walks codeunitHandle.Tree.Session — the handle's
            // tree is skeleton-null. Route to the same skeleton session as everything else
            // (report-execution chain: ReportingTriggers.CreateRecordRefFromRecord etc.).
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSystemCodeunit", "get_Session", 0),
                H(helperShims, "GetSessionReplacement"));
            // The real InvokeAsync wraps dispatch in usage-suppression + diagnostics
            // walking skeleton-null session state; keep only the faithful dispatch
            // (handle.Target.InvokeAsync → real AL body → real IntegrationEvents).
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSystemCodeunit", "InvokeAsync", 2),
                H(helperShims, "NavSystemCodeunit_InvokeAsync"));
            // NavDirectorySecurity.CreateSecurityForDomainDirectory → null. The real
            // body constructs System.Security.AccessControl.DirectorySecurity, a
            // Windows-only API that throws PlatformNotSupported on Linux (reached via
            // NavFile's cctor → TempPathHelper.InitializeFolders on the report path).
            // null = "no ACL", exactly what BC itself returns for non-local (Azure)
            // topologies; CreateDirectoryWithFolderSecurity then only does
            // Directory.CreateDirectory, which is cross-platform.
            {
                var nds = FindNclMethod(nclMod, Rt + "NavDirectorySecurity", "CreateSecurityForDomainDirectory", 0);
                var body = nds.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Replaced NavDirectorySecurity.CreateSecurityForDomainDirectory → null (no ACL on non-Windows)");
            }

            // TempPathHelper..ctor(string) → runner temp root. The real ctor roots
            // server temp paths under ProductApplicationData.ServerPath (unwritable
            // /usr/share/… on Linux) and pre-creates the full folder tree with ACLs.
            {
                var tph = nclMod.GetType(Rt + "TempPathHelper")
                    ?? throw new InvalidOperationException("TempPathHelper not found — Ncl shape changed; do not commit");
                var ctorTph = tph.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic
                        && m.Parameters.Count == 1
                        && m.Parameters[0].ParameterType.FullName == "System.String" && m.HasBody)
                    ?? throw new InvalidOperationException("TempPathHelper..ctor(string) not found — Ncl shape changed; do not commit");
                var tphHelper = typeof(AlRunner.NavReportSync).GetMethod("TempPathHelper_Ctor",
                        BindingFlags.Static | BindingFlags.Public)
                    ?? throw new InvalidOperationException("NavReportSync.TempPathHelper_Ctor not found");
                var tphRef = nclMod.ImportReference(tphHelper);
                var body = ctorTph.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                // NOTE: object ctor chain — call base System.Object..ctor is skippable for
                // classes (object ctor is a no-op) but verifier-friendly IL keeps it out;
                // CoreCLR does not require it for non-COM classes at runtime.
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_1));
                il.Append(il.Create(OpCodes.Call, tphRef));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 2;
                Console.Error.WriteLine("[Cecil] Rewrote TempPathHelper..ctor(string) → runner temp root (unwritable /usr/share on Linux)");
            }

            // NavSession.MaximizePermissions / RemoveMaximizedPermissions → no-op.
            // The runner has no permission system (equivalent to SUPER everywhere);
            // the real bodies walk Database.SecurityAndLicense (null on skeleton).
            // Reached from ReportLayoutSelection's MaximizedPermissionScope.
            foreach (var permName in new[] { "MaximizePermissions", "RemoveMaximizedPermissions" })
            {
                var sessT = nclMod.GetType(Rt + "NavSession");
                foreach (var pm in sessT!.Methods.Where(mm => mm.Name == permName && mm.HasBody).ToList())
                {
                    var body = pm.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 0;
                }
            }
            Console.Error.WriteLine("[Cecil] Replaced NavSession.MaximizePermissions/RemoveMaximizedPermissions → no-op (no permission system in runner)");

            // NavTenant.GetReportSettingsOverride(int) → null. The real body lazily
            // reads the tenant's "Report Settings Override" table by spinning up a
            // full SYSTEM SESSION (NavUserAuthentication etc. — service-tier only).
            // A fresh tenant has no overrides; null is the real no-override result.
            {
                var gso = FindNclMethod(nclMod, Rt + "NavTenant", "GetReportSettingsOverride", 1);
                var body = gso.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Replaced NavTenant.GetReportSettingsOverride → null (no tenant overrides)");
            }
            // NavTenant.GetObjectAccessIntent(session, objectType, objectId) → Undefined.
            // The real body consults per-object read-only-replica access-intent overrides
            // (reads system table 2000000205 "Object Access Intent Override" via
            // TryGetIntentFromTheTenantDatabase, and the read-only-application-objects
            // definition). That whole path is a service-tier read-only-replica concept and
            // its skeleton system-table reads NRE. It is reached from
            // NavReport.GetConnectionIntent() on the report-execution path. The runner has
            // no read-only replica; NavObjectAccessIntent.Undefined (0) is the faithful
            // "no specific intent — use the default read-write connection" result, exactly
            // as on a live tier with no overrides configured.
            {
                var goai = FindNclMethod(nclMod, Rt + "NavTenant", "GetObjectAccessIntent", 3);
                var body = goai.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Replaced NavTenant.GetObjectAccessIntent → Undefined (no read-only-replica access intent)");
            }
            // NavTenant.get_PartnerTelemetryClient → CreatePartnerTelemetryClient().
            // The real getter returns `partnerTelemetryClient.Value`, but that LazyEx field
            // is only wired up by the full tenant-initialisation path (not run on the
            // skeleton), so `.Value` NREs. It is reached from
            // DataItemIterator.ExecuteDataItemIteratorAsync on the report path (partner
            // diagnostics trace scope). CreatePartnerTelemetryClient() itself is the real
            // factory — for the runner's system tenant it returns the environment's no-op
            // DummyClient, exactly what a live tier hands back when partner telemetry has
            // no normal-tenant client. Bypassing the LazyEx just skips caching.
            {
                var getter = FindNclMethod(nclMod, Rt + "NavTenant", "get_PartnerTelemetryClient", 0);
                var factory = FindNclMethod(nclMod, Rt + "NavTenant", "CreatePartnerTelemetryClient", 0);
                var body = getter.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, factory));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Replaced NavTenant.get_PartnerTelemetryClient → CreatePartnerTelemetryClient() (skeleton LazyEx unset)");
            }
            // ReportMetadataXmlRuntime.GenerateReportMetadataXml → null-safe owningApp.
            // The method reads `owningApp = reportInstance.NclMetaReport.OwningApp` into
            // loc.0, then writes the report-info XML's Extension{Id,Name,Publisher,Version}
            // attributes from owningApp.{AppId,FriendlyDisplayName,Publisher,Version}.
            // OwningApp is resolved from MetadataAppGroup.GetObjectOwner and is null for a
            // runner-compiled report whose object isn't owned by a published app package
            // (MetadataAppGroup.GroupId==0). The first deref (owningApp.AppId) NREs. There
            // is no service-tier app-package registry to make this non-null globally (a
            // blanket OwningApp override would break every object's LoadClrType path), so
            // we surgically guard just these 4 extension-attribute assignments: when
            // owningApp is null, emit empty extension identity (faithful "no owning app
            // package" — the dataset/report-info spine still generates) and skip the four
            // deref setters. Everything below (ReportId/Name/About/HelpLink + base call)
            // runs unchanged, reusing the method's own MethodReference/String.Empty operands.
            {
                var grmx = nclMod.GetType("Microsoft.Dynamics.Nav.Runtime.Report.ReportMetadataXmlRuntime")
                    ?.Methods.FirstOrDefault(mm => mm.Name == "GenerateReportMetadataXml")
                    ?? throw new InvalidOperationException("ReportMetadataXmlRuntime.GenerateReportMetadataXml not found — Ncl shape changed; do not commit");
                var body = grmx.Body;
                var instrs = body.Instructions;

                // Locate operands from the existing body.
                Mono.Cecil.MethodReference SetterRef(string name) =>
                    instrs.Select(i => i.Operand as Mono.Cecil.MethodReference)
                          .FirstOrDefault(mr => mr != null && mr.Name == name)
                        ?? throw new InvalidOperationException($"{name} setter call not found in GenerateReportMetadataXml — Ncl shape changed; do not commit");
                var setExtId = SetterRef("set_ExtensionIdValue");
                var setExtName = SetterRef("set_ExtensionNameValue");
                var setExtPub = SetterRef("set_ExtensionPublisherValue");
                var setExtVer = SetterRef("set_ExtensionVersionValue");
                var stringEmpty = instrs.Select(i => i.Operand as Mono.Cecil.FieldReference)
                        .FirstOrDefault(fr => fr != null && fr.Name == "Empty" && fr.DeclaringType.FullName == "System.String")
                    ?? throw new InvalidOperationException("String.Empty ldsfld not found in GenerateReportMetadataXml — Ncl shape changed; do not commit");

                // The first of the four extension setters is the `ldarg.0` that precedes
                // the `call set_ExtensionIdValue`. Find that call, then walk back to its
                // owning `ldarg.0` (2 instructions before: ldarg.0, ldloc.0, callvirt AppId,
                // callvirt ToString, call setter — but the guard only needs the block start).
                var firstSetterCall = instrs.First(i => (i.Operand as Mono.Cecil.MethodReference)?.Name == "set_ExtensionIdValue");
                // Block start = ldarg.0 that begins "ldarg.0; ldloc.0; callvirt get_AppId; callvirt ToString; call setter".
                int callIdx = instrs.IndexOf(firstSetterCall);
                var blockStart = instrs[callIdx - 4]; // ldarg.0
                // Continuation after the four setters = instruction right after set_ExtensionVersionValue.
                var lastExtSetterCall = instrs.First(i => (i.Operand as Mono.Cecil.MethodReference)?.Name == "set_ExtensionVersionValue");
                var afterExtBlock = instrs[instrs.IndexOf(lastExtSetterCall) + 1]; // ldarg.0 of ReportIdValue block

                var il = body.GetILProcessor();
                // Insert before blockStart:  if (owningApp == null) { set 4 empties; goto afterExtBlock; }
                var guard = new[]
                {
                    il.Create(OpCodes.Ldloc_0),
                    il.Create(OpCodes.Brtrue, blockStart),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldsfld, stringEmpty), il.Create(OpCodes.Call, setExtId),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldsfld, stringEmpty), il.Create(OpCodes.Call, setExtName),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldsfld, stringEmpty), il.Create(OpCodes.Call, setExtPub),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldsfld, stringEmpty), il.Create(OpCodes.Call, setExtVer),
                    il.Create(OpCodes.Br, afterExtBlock),
                };
                foreach (var g in guard) il.InsertBefore(blockStart, g);
                body.MaxStackSize = Math.Max(body.MaxStackSize, 2);
                Console.Error.WriteLine("[Cecil] Guarded ReportMetadataXmlRuntime.GenerateReportMetadataXml → empty extension identity when OwningApp is null");
            }
            // NavCompany.get_CompanyDisplayName → return companyName (coalesced to empty).
            // The real getter opens the Company system table (2000000006) via
            // `new NavRecord(Parent, 2000000006)` to read the localized display name, where
            // `Parent => (NavSession)base.Tree.Parent`. The skeleton company's Tree is not
            // wired, so get_Parent NREs. It is reached from ReportRequestXmlRuntime.
            // GenerateReportRequestXml on the SaveAs path. The real fallback when no Company
            // row exists is GetCompanyDisplayNameDefaulted(Empty, companyName) → companyName,
            // so returning the company name directly (empty when unset) is the faithful
            // result without touching the unwired Tree/Company table.
            {
                var cdn = FindNclMethod(nclMod, Rt + "NavCompany", "get_CompanyDisplayName", 0);
                var companyNameField = nclMod.GetType(Rt + "NavCompany").Fields
                    .FirstOrDefault(f => f.Name == "companyName")
                    ?? throw new InvalidOperationException("NavCompany.companyName field not found — Ncl shape changed; do not commit");
                var stringEmptyRef = nclMod.ImportReference(
                    typeof(string).GetField(nameof(string.Empty)));
                var body = cdn.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                var ret = il.Create(OpCodes.Ret);
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, nclMod.ImportReference(companyNameField)));
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Brtrue_S, ret));
                il.Append(il.Create(OpCodes.Pop));
                il.Append(il.Create(OpCodes.Ldsfld, stringEmptyRef));
                il.Append(ret);
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Replaced NavCompany.get_CompanyDisplayName → companyName (skeleton Company table/Tree unwired)");
            }
            // DataItemIterator.SetLoadFieldsBasedOnMetadata(DataItem) → no-op.
            // The real body is a partial-records optimization: it reads
            // `dataItem.Record.Session.Tenant.TenantSettings.GetEnablePartialRecordsForReports()`
            // and, when enabled, narrows the record's loaded field set to only the
            // metadata-referenced fields. The skeleton tenant's `tenantSettings` field is
            // null (the tenant is built via GetUninitializedObject, not the ctor that seeds
            // it), so the `.TenantSettings` member access NREs before the setting is even
            // read. Skipping this optimization means the record loads all its fields —
            // strictly a superset of what partial records would load, so every column the
            // dataset references is present. Making it a no-op is the faithful "partial
            // records disabled" behavior and unblocks the data-item loop.
            {
                var slf = FindNclMethod(nclMod, Rt + "DataItemIterator", "SetLoadFieldsBasedOnMetadata", 1);
                var body = slf.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 0;
                Console.Error.WriteLine("[Cecil] Replaced DataItemIterator.SetLoadFieldsBasedOnMetadata → no-op (partial records disabled; full field load)");
            }
            // ExecutePermissionsValidatedEx get/set consult
            // session.Database.PermissionSetupMonitor (null on skeleton). Permissions are
            // static in the runner — plain backing-field semantics are equivalent.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavApplicationObjectBase", "get_ExecutePermissionsValidatedEx", 0),
                H(helperShims, "NavAppObjBase_GetExecutePermissionsValidatedEx"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavApplicationObjectBase", "set_ExecutePermissionsValidatedEx", 1),
                H(helperShims, "NavAppObjBase_SetExecutePermissionsValidatedEx"));
            // NavSession getter cluster — every one of these reads skeleton state that is
            // null/zeroed because the runner never builds a real tenant/DB/culture stack:
            //   get_NavAppGroup            — tenant.NavAppGroup NREs (tenant null); return NavAppGroup.BaseGroup.
            // NOTE: get_LocalLanguageNoFallback / get_LocalFormatRegion / get_IsLocalLanguage
            // used to be replaced here too, because they read globalLanguageStack /
            // globalFormatRegionStack, which GetUninitializedObject left null on the skeleton
            // session. Those stacks are now planted for real (BcRuntime), so the replacements
            // are gone: they answered "no language override" unconditionally, which meant a
            // report that set CurrReport.Language / CurrReport.FormatRegion read its own value
            // back as the session default / empty string.
            //   GetSecurityFilters         — Database.SecurityAndLicense NREs; return null (RecordImplementation treats null as "no filtering").
            //   PushDynamicCaptionStack    — language-stack manipulation NREs; return false (bool caller falls through to the sync FieldCaption path).
            //   SyncFormatSettings         — cultureSettings null; return new FormatSettings().
            //   get_Culture / get_WindowsCulture — CultureInfo.GetCultureInfo(0) throws ArgumentOutOfRangeException; return InvariantCulture.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_NavAppGroup", 0),
                H(helperShims, "NavSession_NavAppGroup"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavSession", "GetSecurityFilters",
                         "Int32", "Int32", "SecurityFilterType", "NavApplicationObjectBase", "NavApplicationObjectBase"),
                H(helperShims, "NavSession_GetSecurityFilters"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavSession", "PushDynamicCaptionStack", "Int32", "Int32"),
                H(helperShims, "ReturnFalse_3Args"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "SyncFormatSettings", 0),
                H(helperShims, "NavSession_SyncFormatSettings"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_Culture", 0),
                H(helperShims, "NavSession_get_Culture"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_WindowsCulture", 0),
                H(helperShims, "NavSession_get_Culture"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "RecordImplementation", "GetActiveCompany", 0),
                H(helperShims, "RecordImplementation_GetActiveCompany"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavStream", "get_Target", 0),
                H(helperShims, "NavStream_get_Target"));

            // NavMediaImage.GetImageWithContentHeaderValidation — decide "is this an image"
            // from the content header instead of System.Drawing, which is unsupported on this
            // platform. The point is NOT image support: it is that BC's own
            // "not an image → application/octet-stream" fallback keys off an
            // ArgumentException inner, and the platform exception is a different type, so
            // EVERY media write failed — including a report layout, which was never an image.
            // See MediaPatches.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "Media.NavMediaImage", "GetImageWithContentHeaderValidation", 1),
                H(typeof(AlRunner.Patches.MediaPatches), "NavMediaImage_GetImageWithContentHeaderValidation"));

            // NavMediaImage's STATIC state has to go too, not just that one method. Its two
            // static fields are built from System.Drawing at class-init time:
            //     ImageTypeCollection = new Dictionary<ImageFormat, string> { … }
            //     SupportedMimeTypes  = ImageCodecInfo.GetImageDecoders() …
            // so merely TOUCHING the type throws TypeInitializationException here — which is
            // unavoidable, because NavMediaFactory.ProcessMediaObject calls
            // NavMediaImage.IsSupportedMimeType on the way to every non-image branch.
            // Emptying the class constructor leaves both fields null, so IsSupportedMimeType
            // is rewritten to answer false — the honest answer on a platform with no image
            // decoders at all, and the same answer BC's own body would give from an empty
            // decoder set. Everything that would read the null fields is image-only and is
            // refused by name before it can be reached (MediaPatches).
            {
                var navMediaImageT = nclMod.Types
                    .FirstOrDefault(t => t.FullName == Rt + "Media.NavMediaImage")
                    ?? throw new InvalidOperationException(
                        "[Cecil] type " + Rt + "Media.NavMediaImage not found — Ncl shape changed; do not commit");
                var cctor = navMediaImageT.Methods.FirstOrDefault(m => m.Name == ".cctor")
                    ?? throw new InvalidOperationException(
                        "[Cecil] NavMediaImage has no static constructor — Ncl shape changed; do not commit");
                cctor.Body.Instructions.Clear();
                cctor.Body.Variables.Clear();
                cctor.Body.ExceptionHandlers.Clear();
                cctor.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
                Console.Error.WriteLine("[Cecil] Emptied NavMediaImage..cctor (System.Drawing statics unavailable on this platform)");
            }
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "Media.NavMediaImage", "IsSupportedMimeType", 1),
                H(helperShims, "ReturnFalse_1Arg"));

            // NavMediaFactory.ProcessMediaObject(Stream,bool,string) — #2570. Surgical
            // PREPEND (original body untouched, same shape as PrependFieldFindGuard below),
            // not a body replacement: MediaPatches.TryClassifyPngBySignature sniffs the
            // stream's first 8 bytes for the PNG signature BEFORE the real body decides
            // anything. When it returns "image/png" we `starg` the mimeType PARAMETER to
            // that value and fall through into the real, unmodified body — which (mimeType
            // now non-empty) skips GetImageWithContentHeaderValidation entirely, and —
            // because NavMediaImage.IsSupportedMimeType is rewritten to always return false
            // just above — cascades past every image/document branch straight to BC's own
            // `new NavMediaBinaryFile(mediaStream, mimeType)` fallback, unmodified. When it
            // returns null, nothing changes (falls straight through to the original first
            // instruction) — every other mimeType/content shape, including every
            // already-working non-PNG-image / non-image classification, is untouched.
            // Signature-only (no chunk/CRC/IHDR validation) is deliberate: two full rounds
            // of upstream corpus CI (StefanMaron/BusinessCentral.AL.Language.Tests#138, all
            // 8 BC legs both times) measured that real BC accepts a PNG-signature-prefixed
            // stream regardless of chunk CRCs, IHDR presence, or declared width/height — so
            // anything stricter here would make the runner reject content BC accepts (the
            // same class of defect as #2641, opposite direction). RED→GREEN: AlRunner.Tests
            // MediaFactoryProcessMediaObjectPngPrependTests.cs (mechanism) +
            // tests/runner-extras/standalone-suites/media-non-image-content (unchanged,
            // pins the JPEG-still-refuses claim so this cannot silently widen back) + the
            // upstream BC-behaviour claim, StefanMaron/BusinessCentral.AL.Language.Tests#138
            // (tests/al-language/media/TestMediaPngImport.al).
            {
                var processMediaObject = FindNclMethod(nclMod, Rt + "Media.NavMediaFactory", "ProcessMediaObject", 3);
                var helperMi = typeof(AlRunner.Patches.MediaPatches).GetMethod(
                    nameof(AlRunner.Patches.MediaPatches.TryClassifyPngBySignature),
                    BindingFlags.Public | BindingFlags.Static)!;
                var helperRef = nclMod.ImportReference(helperMi);

                var body = processMediaObject.Body;
                var il = body.GetILProcessor();
                var first = body.Instructions[0];

                var newMimeTypeLocal = new VariableDefinition(nclMod.ImportReference(typeof(string)));
                body.Variables.Add(newMimeTypeLocal);

                // ldarg.0 (mediaStream); ldarg.2 (mimeType); call helper -> string?
                // stloc newMimeTypeLocal; ldloc; brfalse <first>; ldloc; starg.s mimeType;
                // <first> (original body, unchanged, falls through here in both cases)
                var ldStream = il.Create(OpCodes.Ldarg_0);
                var ldMime = il.Create(OpCodes.Ldarg_2);
                var callHelper = il.Create(OpCodes.Call, helperRef);
                var stloc = il.Create(OpCodes.Stloc, newMimeTypeLocal);
                var ldlocCheck = il.Create(OpCodes.Ldloc, newMimeTypeLocal);
                var brFalse = il.Create(OpCodes.Brfalse, first);
                var ldlocApply = il.Create(OpCodes.Ldloc, newMimeTypeLocal);
                var starg = il.Create(OpCodes.Starg, processMediaObject.Parameters[2]);

                il.InsertBefore(first, ldStream);
                il.InsertBefore(first, ldMime);
                il.InsertBefore(first, callHelper);
                il.InsertBefore(first, stloc);
                il.InsertBefore(first, ldlocCheck);
                il.InsertBefore(first, brFalse);
                il.InsertBefore(first, ldlocApply);
                il.InsertBefore(first, starg);

                body.MaxStackSize = Math.Max(body.MaxStackSize, 2);
                Console.Error.WriteLine("[Cecil] Prepended PNG-signature mimeType classification to NavMediaFactory.ProcessMediaObject");
            }

            // ── NavRecordRef cluster (Batch 8) — get_Target + ALOpen ─────────────
            // get_Target's real body NREs on base.Tree.Session.Company.SharedObjects
            // on the headless skeleton; the replacement constructs a SharedRecordRef
            // parented to the process-wide skeleton container (NavRecordRefPatches).
            // The 6 ALOpen overloads build the Record via OpenRecordRefById (which
            // itself calls NavRecordRef_get_Target). These all sit on the SAME
            // record/RecordRef R2R-inlined path as get_Target, so they migrate together
            // — a Cecil get_Target coexisting with a JmpHook'd ALOpen (or vice-versa)
            // reproduced the Batch-7b coexistence spin / NRE. Atomic.
            //
            // #2783: CheckIsOpenAllowed(CompilationTarget, Int32) and its
            // IsOpenAllowed(CompilationTarget, Int32) helper used to be replaced here
            // too — with NoOp3 / ReturnTrue_ThreeArgs, on the (mistaken) grounds that
            // they "gate Open against permissions that are absent on the skeleton".
            // They do not check permissions at all: they are BC's compilation-target
            // scope gate, refusing an id in SystemTables.InternalTables (or an
            // OnPrem-scoped system table outside
            // SystemTables.OnPremSystemTableRecordRefAllowed) for a non-OnPrem target.
            // Neutering them let a "target": "Cloud" bundle open system tables a real
            // service tier refuses. Both bodies run unmodified now — they are
            // skeleton-safe (SystemTables is static data, PlatformMetadataProvider
            // reads Ncl's embedded system symbols, and the trace-tag call resolves
            // through DiagnosticsResolver's NavDiagnostics.GlobalInstance fallback) —
            // and NavRecordRefPatches' three ALOpen(CompilationTarget, …) helpers
            // invoke CheckIsOpenAllowed, which their replaced bodies otherwise skip.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavRecordRef", "get_Target", 0),
                H(typeof(AlRunner.BcRuntime), "NavRecordRef_get_Target"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "Int32"),
                H(typeof(AlRunner.BcRuntime), "NavRecordRef_ALOpen_Int"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "Int32", "Boolean"),
                H(typeof(AlRunner.BcRuntime), "NavRecordRef_ALOpen_IntBool"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "Int32", "Boolean", "String"),
                H(typeof(AlRunner.BcRuntime), "NavRecordRef_ALOpen_IntBoolCompany"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "CompilationTarget", "Int32"),
                H(typeof(AlRunner.BcRuntime), "NavRecordRef_ALOpen_TargetInt"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "CompilationTarget", "Int32", "Boolean"),
                H(typeof(AlRunner.BcRuntime), "NavRecordRef_ALOpen_TargetIntBool"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "CompilationTarget", "Int32", "Boolean", "String"),
                H(typeof(AlRunner.BcRuntime), "NavRecordRef_ALOpen_TargetIntBoolCompany"));

            // ── NavSession.GetPermissionSet (Batch 8) — both 3-arg overloads ─────
            // Real body NREs reaching the skeleton's (null) Permissions object.
            // Both return the all-granted PermissionSet singleton. This is the leaf
            // of the CalcSums path (ALCalcSumsAsync → CalcSumsAsync →
            // VerifyPermissionsCalculatedFields → GetPermissionSet) which NREs in
            // Cecil-only mode; migrating it fixes that whole cluster.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavSession", "GetPermissionSet",
                         "NavApplicationObjectBase", "Int32", "ApplicationObjectId"),
                H(typeof(AlRunner.BcRuntime), "NavSession_GetPermissionSet_ByObjectId"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavSession", "GetPermissionSet",
                         "NavApplicationObjectBase", "Int32", "IEnumerable`1"),
                H(typeof(AlRunner.BcRuntime), "NavSession_GetPermissionSet_ByObjectIds"));

            // ── NavCodeunit run path (Batch 8) — DoRunAsync + RunCodeunit ───────
            // DoRunAsync's first line builds a timing scope via
            // DiagnosticsResolver.GetMostSpecificInstance(Session) which NREs on the
            // skeleton; the static RunCodeunit reaches the same inlined body. Both
            // dispatch OnRun directly via the helpers. NavCodeunitHandle.CreateTarget
            // (already CecilOwned, Batch 3) is on the same path → migrate together.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavCodeunit", "DoRunAsync", "DataError", "NavRecord"),
                H(typeof(AlRunner.BcRuntime), "NavCodeunit_DoRunAsync"));
            // RunCodeunit has TWO 3-arg overloads ((DataError,Int32,NavRecord) and
            // (DataError,String,NavRecord)); mirror the JmpHook by targeting only the
            // Int32 one. The String/lower-arity overloads forward into it.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavCodeunit", "RunCodeunit", "DataError", "Int32", "NavRecord"),
                H(typeof(AlRunner.BcRuntime), "NavCodeunit_RunCodeunit"));

            // ── Truncate + security-filtering cluster (Batch 8) ─────────────────
            // ValidateTruncateSupport throws NavPermissionException on the skeleton
            // (a security filter is set); SetSecurityFiltering / DataProvider.Truncate
            // Async / SessionHasSuperOr… all sit on the same record/security R2R path.
            // Migrate together so the path is single-mechanism.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecord", "ValidateTruncateSupport", "NavRecord"),
                H(helperShims, "NoOp_OneArg"));
            // SetSecurityFiltering was NoOp2 — which also dropped `securityFiltering = filtering`,
            // so Record.SecurityFiltering() could never observe a mode change. The helper stores
            // the field and invalidates the result set; see SecurityFilteringPatches for why
            // omitting the GetSecurityFilters arms stays observably equivalent here.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "RecordImplementation", "SetSecurityFiltering", "SecurityFiltering"),
                H(typeof(AlRunner.Patches.SecurityFilteringPatches),
                  "RecordImplementation_SetSecurityFiltering"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "DataProvider", "TruncateAsync",
                         "Int32", "NCLMetaTable", "FiltersAndMarks", "Boolean"),
                H(helperShims, "DataProvider_TruncateAsync"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "PermissionManagement", "SessionHasSuperOrSecurityPermissionsForUser",
                         "NavSession", "Guid"),
                H(helperShims, "ReturnTrue_TwoArgs"));

            // ── NavApplicationObjectBase.TryInvoke (Batch 8) — AL TryFunction ───
            // Real body needs session.CurrentMethodScope, absent on the skeleton →
            // NRE. Helper runs the Action and reports success/failure (AL TryFunction
            // semantics). Same AL-invoke R2R path as the migrated CreateTarget family.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavApplicationObjectBase", "TryInvoke", "NavSession", "Action"),
                H(typeof(AlRunner.BcRuntime), "NavApplicationObjectBase_TryInvoke"));

            // ── NavApplicationObjectBase.TryInvokeAsync — async TryFunction ──────
            // Same skeleton gap as the sync TryInvoke (session.CurrentMethodScope NRE),
            // but via the async state-machine path that CU3800/3801.InitializeFromCurrentApp
            // uses. Once running, the Azure Key Vault SDK load path hits the patched
            // NavDotNet.CreateNavServerHandle catch block → RunnerOutOfScopeException.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavApplicationObjectBase", "TryInvokeAsync", "NavSession", "Func`1"),
                H(typeof(AlRunner.BcRuntime), "NavApplicationObjectBase_TryInvokeAsync"));

            // ── BitArrayHelpers.Equals (Batch 8) — .NET API drift ───────────────
            // Real body calls GetIntArray which reads the removed private field
            // System.Collections.BitArray.m_array → MissingFieldException on current
            // .NET. Helper compares via the public indexer. (Reached from
            // FieldLoadInfo.Equals on the SetBaseLoadFields path.)
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "Utility.BitArrayHelpers", "Equals", "BitArray", "BitArray"),
                H(typeof(AlRunner.BcRuntime), "BitArrayHelpers_Equals"));

            // ── Event-binding metadata cluster (Batch 8) ────────────────────────
            // NavCodeunit.get_MetaCodeunit + NCLMetaCodeunit.get_IsEventManualBinding.
            // The real getters traverse the metadata cache / dereference
            // ApplicationObjectClrType (null on the skeleton) → NRE. Helpers build a
            // skeleton NCLMetaCodeunit from the AL-emitted Codeunit{N} CLR type.
            // Reached via Bind/UnbindSubscription. Migrate together (companion getters).
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavCodeunit", "get_MetaCodeunit", 0),
                H(typeof(AlRunner.BcRuntime), "NavCodeunit_get_MetaCodeunit"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NCLMetaCodeunit", "get_IsEventManualBinding", 0),
                H(typeof(AlRunner.BcRuntime), "NCLMetaCodeunit_get_IsEventManualBinding"));

            // ── ALSystemOperatingSystem GetUrl family (Batch 8) ─────────────────
            // GetUrlCore / ALGetUrl / ALGetUrlInternal (all 7-arg) reach the absent
            // service-instance URL infrastructure → NRE. Helper returns a stub URL.
            foreach (var urlName in new[] { "GetUrlCore", "ALGetUrl", "ALGetUrlInternal" })
                ReplaceBodyWithHelper(nclMod,
                    ByParams(Rt + "ALSystemOperatingSystem", urlName,
                             "NavClientType", "String", "NavObjectType", "Int32", "Object", "Boolean", "String"),
                    H(helperShims, "ALSystemOperatingSystem_GetUrlCore"));

            // ── NavNotification.ALSend / ALRecall (Batch 8) ─────────────────────
            // Real body reaches the absent notification-dispatch layer → NRE. Helper
            // populates NotificationInfo.Id (mirror) and returns true.
            // Send and Recall route to DIFFERENT handler types, so they cannot share a helper:
            // the AL test declares [SendNotificationHandler] or [RecallNotificationHandler] and
            // BC picks by NavHandlerType.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavNotification", "ALSend", "DataError"),
                H(helperShims, "NavNotification_ALSend"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavNotification", "ALRecall", "DataError"),
                H(helperShims, "NavNotification_ALRecall"));

            // ── FlowField CalcFieldsAsync(2)/(3) — already Cecil-body-rewritten above
            //    (see ~line 1751). FlowFieldPatches.Register additionally JmpHook.Apply's
            //    a (dead-under-R2R) fallback on the SAME methods; registering their keys
            //    in CecilOwned turns that fallback into a no-op so the path is strictly
            //    single-mechanism. No rewrite here — the body replace is upstream.
        }

        RewriteExecutionSchedulerThreadToBackground(asm.MainModule);
    }

    /// <summary>
    /// #2704 — ExecutionScheduler..ctor starts a FOREGROUND OS thread ("BC Execution
    /// Scheduler", SchedulerLoop) that only ever stops when <c>Dispose()</c> is called, and
    /// nothing in Ncl or the runner calls it. <c>NavEnvironment.Instance.ExecutionScheduler</c>
    /// is a process-lifetime lazy that roughly ten Ncl call sites realize as a side effect
    /// (the captured #2704 trigger was Base App's Feature Telemetry disposing a helper
    /// NavSession — <c>NavSession.InnerDispose</c> reads the lazy). On a service tier the
    /// process never exits anyway; in a one-shot CLI process the thread keeps the CLR alive
    /// after Main returns — the summary prints and the process hangs (#2650 was the same
    /// defect through the PBT-enqueue trigger; #2628 removed that trigger, not the defect).
    ///
    /// Fix at the constructor, so every trigger — enumerated or not — is covered: insert
    /// <c>dup; ldc.i4.1; callvirt Thread::set_IsBackground</c> before the single
    /// <c>callvirt Thread::Start()</c>. The thread is parked in <c>Monitor.Wait</c>, so
    /// tearing it down at process exit is harmless; during a run nothing changes, and
    /// --server/--watch keep the process alive via the main thread regardless.
    ///
    /// Token-safe: <c>Thread::set_IsBackground(bool)</c> is ALREADY a memberRef in Ncl
    /// (NavEnvironment's CollectAndCompactHeap thread sets it), and that existing
    /// <see cref="MethodReference"/> is reused verbatim — no new typeRef/memberRef, so R2R
    /// callers' token offsets are untouched. If either shape is missing this throws rather
    /// than importing a fresh reference, per the token-shift rule.
    /// </summary>
    private static void RewriteExecutionSchedulerThreadToBackground(ModuleDefinition nclMod)
    {
        const string ThreadFullName = "System.Threading.Thread";

        var navEnvT = nclMod.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment")
            ?? throw new InvalidOperationException(
                "[Cecil] NavEnvironment not found — Ncl shape changed; do not commit");
        var setIsBackground = navEnvT.Methods
            .Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Callvirt
                && i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == ThreadFullName
                && mr.Name == "set_IsBackground"
                && mr.Parameters.Count == 1)
            .Select(i => (MethodReference)i.Operand)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "[Cecil] no existing Thread::set_IsBackground memberRef in NavEnvironment — " +
                "cannot make ExecutionScheduler's thread background without adding a memberRef " +
                "(R2R token-shift rule); Ncl shape changed; do not commit");

        var ctor = FindNclMethod(nclMod,
            "Microsoft.Dynamics.Nav.Runtime.ExecutionScheduler", ".ctor", 6);
        var starts = ctor.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Callvirt
                && i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == ThreadFullName
                && mr.Name == "Start"
                && mr.Parameters.Count == 0)
            .ToList();
        if (starts.Count != 1)
            throw new InvalidOperationException(
                $"[Cecil] expected exactly 1 Thread::Start() in ExecutionScheduler..ctor, found {starts.Count} — Ncl shape changed; do not commit");

        // Stack at Start(): [thread]. dup → [thread, thread]; ldc.i4.1 → [thread, thread, 1];
        // callvirt set_IsBackground → [thread]; then the original Start() consumes it.
        var il = ctor.Body.GetILProcessor();
        var start = starts[0];
        il.InsertBefore(start, il.Create(OpCodes.Dup));
        il.InsertBefore(start, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(start, il.Create(OpCodes.Callvirt, setIsBackground));
        ctor.Body.MaxStackSize += 2;
        Console.Error.WriteLine("[Cecil] ExecutionScheduler..ctor: SchedulerLoop thread → IsBackground=true before Start() (#2704: foreground thread outlived Main)");
    }

    private static void AddRuntimeOwned(HashSet<string> set)
    {
        // ALFunctionTimingExecutionListener (Batch 2).
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALFunctionTimingExecutionListener::EnsureRegistered/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALFunctionTimingExecutionListener::Start/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALFunctionTimingExecutionListener::Exit/1");
        // CreateTarget family (Batch 3 — the coexistence-killer). The 0-arg
        // protected-override CreateTarget() on each handle type.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle::CreateTarget/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecordHandle::CreateTarget/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavTestPageHandle::CreateTarget/0");
        // NavTestPageBase.GetMetaTable — same orphaned-JmpHook story as ALGoToRecord above.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavTestPageBase::GetMetaTable/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavFormHandle::CreateTarget/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReportHandle::CreateTarget/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavQueryHandle::CreateTarget/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavXmlPortHandle::CreateTarget/0");
        // NavServerEventSource telemetry
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavServerEventSource::get_Log/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavServerEventSource::WritePermissionUncheckedEvent/10");
        // Misc
        set.Add("Microsoft.Dynamics.Nav.Runtime.Data.SequentialUuidCreator+NativeMethods::NewSequentialId/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.TempTableStatistics::ReportIncrementChange/3");
        // NavCodeunit run path (Batch 8). DoRunAsync/2 + RunCodeunit/3. Only the
        // (DataError,Int32,NavRecord) RunCodeunit is JmpHook'd/Cecil'd; the sibling
        // (DataError,String,NavRecord) /3 is never hooked, so the by-arity key is safe.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavCodeunit::DoRunAsync/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavCodeunit::RunCodeunit/3");
        // BitArrayHelpers.Equals (Batch 8) — static (BitArray,BitArray) overload.
        set.Add("Microsoft.Dynamics.Nav.Runtime.Utility.BitArrayHelpers::Equals/2");
        // Event-binding metadata cluster (Batch 8).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavCodeunit::get_MetaCodeunit/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaCodeunit::get_IsEventManualBinding/0");
        // NavMediaFactory.ProcessMediaObject(Stream,bool,string) — surgical prepend so a
        // PNG-signature-prefixed stream classifies as image/png without decoding (#2570).
        // See the RewriteNcl block below for the detail. Additive: does not touch
        // NavForm.GetPart (#2600) or the page-background-task routing (#2628).
        set.Add("Microsoft.Dynamics.Nav.Runtime.Media.NavMediaFactory::ProcessMediaObject/3");
    }

}
