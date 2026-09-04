// NclCecilRewrite — spike: rewrite Microsoft.Dynamics.Nav.Ncl.dll IL at load time
// to neutralize R2R-trapped methods that JmpHook and EventPipe-post-JIT can't reach.
//
// Allowed surface per .claude/rules/precompiled-dll-respect.md: Ncl.dll is runtime engine,
// not BaseApplication / SystemApplication / ISV business logic.
//
// This file is the driver: assembly load/setup for RewriteNcl, assembling the CecilOwned
// registry from each area's contribution, the shared Key()/helper primitives, and the
// on-disk rewrite cache. The actual rewrite bodies live in NclCecilRewrite.<Area>.cs
// (Forms, Media, Dispatch, Records, Queries, Metadata, Reports, Runtime) — split out per
// #2631 so a new rewrite lands in its own file instead of this single file serializing
// every runtime-engine fix. RewriteNcl below calls each area's RewriteNcl_<Area>(...) in
// the exact order the original single method executed those blocks; behavior-preserving
// move only, see #2631.

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
    private const int CACHE_VERSION = 130;

    // ─────────────────────────────────────────────────────────────────────────
    // Cecil-owned skip registry (JmpHook→Cecil migration enabler).
    //
    // Each Ncl method migrated to a Cecil IL rewrite is listed here by its
    // canonical key. JmpHook.Apply consults this set and SKIPS any method whose
    // key is present, so a migrated method is owned by EXACTLY ONE mechanism
    // (Cecil) — eliminating the JmpHook+Cecil COEXISTENCE that caused the
    // safepoint-free 100%-CPU spin when the replacement re-enters AL execution.
    //
    // Design A (hardcoded): the set is compiled in, so it is available in every
    // process — including the re-exec'd warm-cache child where RewriteNcl does NOT
    // re-run. Drift (a key listed but the method not actually Cecil-rewritten →
    // the method ends up unpatched) is caught by the corpus gate, because that
    // method's tests will fail.
    //
    // MAINTENANCE RULE: add a key here in the SAME commit that adds the Cecil
    // rewrite for that method. Use the exact MS type FullName + method Name +
    // param count, matching what Key(MethodDefinition)/Key(MethodBase) produce.
    // ─────────────────────────────────────────────────────────────────────────

    public static readonly HashSet<string> CecilOwned = BuildCecilOwned();

    // Built from each area's own Add<Area>Owned contribution (plus the small Setup
    // slice for entries whose only home is the assembly-load/setup section below) so
    // registering a new key never requires editing this shared list — see #2631.
    private static HashSet<string> BuildCecilOwned()
    {
        var set = new HashSet<string>();
        AddSetupOwned(set);
        AddFormsOwned(set);
        AddMediaOwned(set);
        AddDispatchOwned(set);
        AddRecordsOwned(set);
        AddQueriesOwned(set);
        AddMetadataOwned(set);
        AddReportsOwned(set);
        AddRuntimeOwned(set);
        return set;
    }


    private static void AddSetupOwned(HashSet<string> set)
    {
        // Static NavReport.Run(int, ...) / RunModal(int, ...) — #1771. No Hook(...) call site
        // registers these anymore (the pre-fix JmpHook in ReportPatches.cs was dead code: it
        // never fired under the default Cecil-only runtime, so the call silently fell through
        // the Cecil-blanked `ret` body instead of throwing). Registered here anyway, matching
        // the CreateTarget family above, so a future JmpHook re-registration against one of
        // these methods (accidental regression, or an AL_RUNNER_ENABLE_JMPHOOK=1 diagnostic
        // pass) is recognised as redundant rather than silently recreating the coexistence
        // double-dispatch this bug class is named for. Keyed by param count only (Key() does
        // not encode parameter types), so this also covers the ReportRunOptions overload of
        // Run/1 — also Cecil-owned now (throws an "unrecognised overload shape" OOS instead of
        // being routed to SyncStaticRun).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReport::Run/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReport::Run/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReport::Run/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReport::Run/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReport::RunModal/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReport::RunModal/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReport::RunModal/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavReport::RunModal/4");
        // NCLMetaApplicationObject
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::CheckApplicationObjectIsValid/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::get_ApplicationObjectClrType/0");
        // NCLEnumMetadata.Create(int) (Batch 7 — THE CreateNoSeriesLine spin fix).
        // Its body is Cecil-rewritten to forward to BcRuntime.NCLEnumMetadata_CreateByIdAlAware
        // (see RewriteNcl ~line 232), but BcRuntime.cs ALSO JmpHook'd it. That double-patch
        // (Cecil body + JmpHook JMP-precode) is the exact COEXISTENCE the registry exists to
        // prevent: the JmpHook precode thunk (ReportStubBlock<MethodCallThunk>) spins
        // safepoint-free when reached on the AL-Validate construction path (e.g.
        // Library - No. Series.CreateNoSeriesLine validating an enum/option field). Listing
        // its key here makes JmpHook.Apply skip it → single-mechanism (Cecil) → no spin.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLEnumMetadata::Create/1");
        // get_ApplicationObjectConstructor + Populate + CompileAndLoadClrObject (Batch 7
        // — completes the insert/construction path so ALInsertAsync→get_OldRecord→
        // CreateObjectInstance→{getter,Populate,CompileAndLoadClrObject} is single-mechanism).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::get_ApplicationObjectConstructor/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::Populate/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::CompileAndLoadClrObject/0");
        // ALCompiler.DotNetToNavOutStream — skeleton SharedObjects fallback for
        // .NET-stream → NavOutStream marshalling (Cryptography Management GenerateHash).
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALCompiler::DotNetToNavOutStream/2");
        // ALCompiler.DotNetToNavInStream — mirror of the DotNetToNavOutStream fallback
        // above (#2576): same skeleton SharedObjects issue, structurally identical real
        // body (three branches: null → Default, Stream → NavStreamProvider-backed
        // instance, else → NavNCLConversionException), just the InStream direction.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALCompiler::DotNetToNavInStream/2");
    }



    /// <summary>
    /// Canonical per-method key from a Cecil <see cref="MethodDefinition"/>:
    /// <c>DeclaringType.FullName + "::" + Name + "/" + paramCount</c>, with Cecil's
    /// nested-type separator '/' normalized to '+' so it matches the reflection form.
    /// </summary>
    public static string Key(MethodDefinition m)
        => NormalizeTypeName(m.DeclaringType.FullName) + "::" + m.Name + "/" + m.Parameters.Count;

    /// <summary>
    /// Canonical per-method key from a reflection <see cref="MethodBase"/>. Reflection
    /// already uses '+' for nested types, so normalization is a no-op here but kept
    /// symmetric with the Cecil form.
    /// </summary>
    public static string Key(MethodBase m)
        => NormalizeTypeName(m.DeclaringType?.FullName ?? "") + "::" + m.Name + "/" + m.GetParameters().Length;

    internal static MethodDefinition ResolveNumberSequenceEntryPoint(
        TypeDefinition type,
        string methodName,
        string returnType,
        params string[] parameterTypes)
    {
        var matches = type.Methods.Where(method =>
                method.Name == methodName &&
                method.IsPublic &&
                method.IsStatic &&
                method.HasBody &&
                method.ReturnType.FullName == returnType &&
                method.Parameters.Select(parameter => parameter.ParameterType.FullName)
                    .SequenceEqual(parameterTypes, StringComparer.Ordinal))
            .ToArray();

        if (matches.Length == 1)
            return matches[0];

        var expected = $"{returnType} {type.FullName}.{methodName}({string.Join(", ", parameterTypes)})";
        var available = string.Join("; ", type.Methods
            .Where(method => method.Name == methodName)
            .Select(method => method.FullName));
        throw new InvalidOperationException(
            $"[Cecil] expected exactly one {expected}, found {matches.Length}. " +
            $"Available overloads: {(available.Length == 0 ? "<none>" : available)}. " +
            "Ncl shape changed; do not commit (#2049).");
    }

    // Cecil uses '/' between an outer type and a nested type; reflection uses '+'.
    // Our targets are all top-level so neither separator appears, but normalize
    // anyway so the two Key() overloads are guaranteed to agree.
    private static string NormalizeTypeName(string fullName) => fullName.Replace('/', '+');

    private static readonly Dictionary<byte, System.Reflection.Emit.OpCode> SingleByteOpCodes = typeof(System.Reflection.Emit.OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(System.Reflection.Emit.OpCode))
        .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
        .Where(op => op.Size == 1)
        .ToDictionary(op => unchecked((byte)op.Value));

    private static readonly Dictionary<byte, System.Reflection.Emit.OpCode> DoubleByteOpCodes = typeof(System.Reflection.Emit.OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(System.Reflection.Emit.OpCode))
        .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
        .Where(op => op.Size == 2 && ((ushort)op.Value >> 8) == 0xFE)
        .ToDictionary(op => unchecked((byte)op.Value));


    /// <summary>
    /// Reads Ncl.dll bytes, rewrites IsEventSubscribed body to return true,
    /// strips R2R header, returns modified bytes ready for Assembly.Load.
    /// </summary>

    public static byte[] RewriteNcl(string nclPath)
    {
        var originalBytes = File.ReadAllBytes(nclPath);

        var resolver = new DefaultAssemblyResolver();
        var dir = Path.GetDirectoryName(nclPath)!;
        resolver.AddSearchDirectory(dir);

        using var inStream = new MemoryStream(originalBytes);
        var asm = AssemblyDefinition.ReadAssembly(inStream, new ReaderParameters { ReadWrite = false, AssemblyResolver = resolver });

        var type = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject");
        if (type == null)
            throw new InvalidOperationException("NCLMetaApplicationObject type not found in Ncl.dll");

        int rewroteCount = 0;
        foreach (var method in type.Methods.Where(mm => mm.Name == "IsEventSubscribed").ToList())
        {
            Console.Error.WriteLine($"[Cecil] Rewriting {method.FullName}");
            if (method.ReturnType.FullName != "System.Boolean")
            {
                Console.Error.WriteLine($"[Cecil]  - skipping: return type is {method.ReturnType.FullName}");
                continue;
            }
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            rewroteCount++;
        }
        if (rewroteCount == 0)
            throw new InvalidOperationException("IsEventSubscribed method not found");
        Console.Error.WriteLine($"[Cecil] Rewrote {rewroteCount} IsEventSubscribed overload(s) → return true");

        // NCLEnumMetadata.Create(int) — precompiled Microsoft dependency DLLs call
        // this directly (not through BcAssembler's emitted C# helper), and the real
        // body reaches NavGlobal.MetadataProvider/SystemTenant which is null in the
        // runner skeleton. Route it to the same registry-backed helper used by
        // runner-compiled code so enum construction is stable for dependency code.
        {
            var enumMetadataType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NCLEnumMetadata");
            var createById = enumMetadataType?.Methods.FirstOrDefault(m =>
                m.Name == "Create"
                && m.IsStatic
                && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == "System.Int32"
                && m.ReturnType.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLOptionMetadata");
            var helper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NCLEnumMetadata_CreateByIdAlAware),
                BindingFlags.Public | BindingFlags.Static);
            if (createById != null && helper != null)
            {
                var helperRef = asm.MainModule.ImportReference(helper);
                var body = createById.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, helperRef));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Replaced NCLEnumMetadata.Create(int) → BcRuntime.NCLEnumMetadata_CreateByIdAlAware");
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARN: NCLEnumMetadata.Create(int) not found — dependency enum metadata may NRE");
            }
        }

        // ALCompiler.ToInterface(ITreeObject, NavOption, int) relies on the
        // internal NCLOptionMetadata.GetImplementationCodeunitId virtual. Our
        // dependency enum metadata replacement cannot override that internal
        // member from the runner assembly, so route the small dispatcher through
        // a public helper that reads the same implementation ids from the symbol
        // registry.
        {
            var alCompiler = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALCompiler");
            if (alCompiler == null)
                throw new InvalidOperationException("ALCompiler type not found — Ncl shape changed");
            var toInterface = alCompiler.Methods.FirstOrDefault(m =>
                m.Name == "ToInterface"
                && m.IsStatic
                && m.Parameters.Count == 3
                && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.ITreeObject"
                && m.Parameters[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavOption"
                && m.Parameters[2].ParameterType.FullName == "System.Int32"
                && m.ReturnType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavInterfaceHandle");
            var helper = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.ALCompiler_ToInterfaceFromOption),
                BindingFlags.Public | BindingFlags.Static);
            if (toInterface == null || helper == null)
                throw new InvalidOperationException("ALCompiler.ToInterface(ITreeObject,NavOption,int) helper rewrite target not found — Ncl shape changed");
            var helperRef = asm.MainModule.ImportReference(helper);
            var body = toInterface.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Call, helperRef));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 3;
            Console.Error.WriteLine("[Cecil] Replaced ALCompiler.ToInterface(NavOption) → BcRuntime.ALCompiler_ToInterfaceFromOption");
        }

        // NavReport.SaveAsAsync now runs the REAL in-process execution chain
        // (SaveReportAsFormatCoreAsync → RunReportInternalCoreAsync →
        // ExecuteDataItemIteratorAsync). The out-of-scope boundary moved to the
        // ReportResultSetProcessorFactory fork — see the §report-processor-factory
        // rewrite block below (only genuinely external processors throw).
        var navReportType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavReport");
        if (navReportType == null)
            throw new InvalidOperationException("NavReport type not found in Ncl.dll — Ncl shape changed");

        var oosCtorInfo = typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })
            ?? throw new InvalidOperationException("InvalidOperationException(string) ctor not found via reflection");
        var oosCtor = asm.MainModule.ImportReference(oosCtorInfo);

        // NavReport.RunRequestPageAsync → throw OOS (request-page UI is out-of-scope)
        int runRequestPageRewroteCount = 0;
        foreach (var method in navReportType.Methods.Where(mm => mm.Name == "RunRequestPageAsync").ToList())
        {
            Console.Error.WriteLine($"[Cecil] Rewriting {method.FullName}");
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldstr, "out-of-scope: NavReport.RunRequestPage — request-page-ui — see docs/scope.md#report-rendering"));
            il.Append(il.Create(OpCodes.Newobj, oosCtor));
            il.Append(il.Create(OpCodes.Throw));
            body.MaxStackSize = 1;
            runRequestPageRewroteCount++;
        }
        if (runRequestPageRewroteCount == 0)
            throw new InvalidOperationException("RunRequestPageAsync method not found in NavReport — Ncl shape changed; do not commit");
        Console.Error.WriteLine($"[Cecil] Rewrote {runRequestPageRewroteCount} RunRequestPageAsync overload(s) → throw OOS");


        RewriteNcl_Forms(asm);
        RewriteNcl_Media(asm);
        RewriteNcl_Dispatch(asm, oosCtor);
        RewriteNcl_Records(asm);
        RewriteNcl_Queries(asm);
        RewriteNcl_Metadata(asm);
        RewriteNcl_Reports(asm, oosCtor);
        RewriteNcl_Runtime(asm);

        var outStream = new MemoryStream();
        asm.Write(outStream);
        var modifiedBytes = outStream.ToArray();
        ValidateRewrittenAssembly(modifiedBytes);

        StripR2RHeader(modifiedBytes);

        Console.Error.WriteLine($"[Cecil] Ncl rewrite complete: {originalBytes.Length} → {modifiedBytes.Length} bytes");
        return modifiedBytes;
    }


    // ─────────────────────────────────────────────────────────────────────────
    // Reusable Cecil primitives (extracted for the JmpHook→Cecil migration).
    //
    // These promote idioms that were previously inlined throughout RewriteNcl into
    // named helpers so each migrated hook is a one-liner. Behavior-preserving: they
    // emit exactly the IL the inline blocks did (see RecordLink ReplaceWithStaticHelper
    // at the precedent above, and the ALDatabase no-op / IsEventSubscribed const blocks).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Locate a method by (typeFullName, name, optional paramCount) with a loud guard.
    /// A null result means Ncl's shape changed and we must NOT commit a silently-inert
    /// rewrite — so it throws rather than returning null.
    /// </summary>
    private static MethodDefinition FindNclMethod(
        ModuleDefinition module, string typeFullName, string name, int? paramCount = null)
    {
        var type = module.GetType(typeFullName)
            ?? throw new InvalidOperationException(
                $"[Cecil] type {typeFullName} not found — Ncl shape changed; do not commit");
        var m = type.Methods.FirstOrDefault(x =>
            x.Name == name && (paramCount == null || x.Parameters.Count == paramCount) && x.HasBody);
        return m ?? throw new InvalidOperationException(
            $"[Cecil] method {typeFullName}.{name}"
            + (paramCount == null ? "" : $"({paramCount})")
            + " not found — Ncl shape changed; do not commit");
    }

    /// <summary>Same as FindNclMethod but returns null (no throw) when the type/method is absent.</summary>
    private static MethodDefinition? TryFindNclMethod(
        ModuleDefinition module, string typeFullName, string name, int? paramCount = null)
    {
        var type = module.GetType(typeFullName);
        return type?.Methods.FirstOrDefault(x =>
            x.Name == name && (paramCount == null || x.Parameters.Count == paramCount) && x.HasBody);
    }

    /// <summary>
    /// Resolves NavMediaSet's internal "add a media id to the set" method for whichever BC
    /// shape is present on this Ncl — BC 28+'s async
    /// <c>AddMediaToSetAsync(NavSession, Guid, Guid) -&gt; ValueTask&lt;Guid&gt;</c>, or BC
    /// 27.x's synchronous <c>AddMediaToSet(Guid, Guid) -&gt; Guid</c> (no NavSession param).
    /// See the Batch 5 NavMediaSet block for the full story (#1802): BC 27.x has NO async
    /// surface on NavMediaSet at all, confirmed by decompiling
    /// Microsoft.Dynamics.Nav.Ncl.dll from both the 27.0.38460.53552 and 28.1.49838.50794
    /// cached service tiers.
    ///
    /// Extracted to a standalone method — independently testable (see
    /// MediaSetAddToSetResolutionTests.cs) without needing to run the whole Cecil rewrite
    /// pipeline against a real Ncl assembly.
    ///
    /// This is a genuine version-conditional pair, not an optional hook: per
    /// loud-failures.md, if NEITHER shape resolves, the caller must NOT silently skip the
    /// hook — every MediaSet membership operation would then degrade to Count()==0 with no
    /// error (exactly what #1802 reported), a wrong answer rather than a missing feature.
    /// So an unresolved pair hard-errors the whole run instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Neither shape resolved — an unknown BC Ncl surface for the MediaSet membership
    /// funnel. The message names both candidate signatures so the fix is greppable.
    /// </exception>
    internal static MethodDefinition ResolveMediaSetAddToSetTarget(TypeDefinition navMediaSetCecilType)
    {
        var mAddToSetAsync = navMediaSetCecilType.Methods.FirstOrDefault(m =>
            m.Name == "AddMediaToSetAsync" && m.HasBody && m.Parameters.Count == 3);
        if (mAddToSetAsync != null) return mAddToSetAsync;

        var mAddToSetSync = navMediaSetCecilType.Methods.FirstOrDefault(m =>
            m.Name == "AddMediaToSet" && m.HasBody && m.Parameters.Count == 2
            && m.Parameters[0].ParameterType.FullName == "System.Guid"
            && m.Parameters[1].ParameterType.FullName == "System.Guid");
        if (mAddToSetSync != null) return mAddToSetSync;

        throw new InvalidOperationException(
            "[Cecil] FATAL: neither NavMediaSet.AddMediaToSetAsync(NavSession, Guid, Guid) "
            + "(BC 28+) nor NavMediaSet.AddMediaToSet(Guid, Guid) (BC 27.x) resolved on this "
            + "Ncl — unknown BC shape for the MediaSet membership funnel. Silently skipping "
            + "this hook makes ImportStream/ImportFile return a real MediaId while "
            + "MediaSet.Count() silently stays 0 (see #1802) — a wrong answer, not a missing "
            + "feature, so this aborts the run instead of degrading quietly. Decompile the "
            + "new Ncl and add a case for the new shape.");
    }

    /// <summary>
    /// Clear <paramref name="target"/>'s body and emit `ldarg.0..N; call BcRuntime.helper; ret`,
    /// forwarding every IL argument (incl. `this` for instance methods) to the static helper.
    /// The helper's return value (if any) is left on the stack as the method result.
    /// Generalises the inline RecordLink ReplaceWithStaticHelper.
    /// </summary>
    private static void ReplaceBodyWithHelper(
        ModuleDefinition module, MethodDefinition target, string bcRuntimeHelperName)
    {
        var helperMi = typeof(AlRunner.BcRuntime).GetMethod(
            bcRuntimeHelperName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"[Cecil] BcRuntime.{bcRuntimeHelperName} not found");
        ReplaceBodyWithHelper(module, target, helperMi);
    }

    /// <summary>
    /// Emit `ldarg.0..argSlots-1; call helper;` BEFORE <paramref name="target"/>'s existing
    /// first instruction, leaving the original body — and every branch target in it —
    /// untouched. Use when the patch is an observer/side-effect that must run first and
    /// BC's own behaviour must still happen (as opposed to ReplaceBodyWithHelper, which
    /// discards the body entirely).
    ///
    /// The helper must return void and take exactly <paramref name="argSlots"/> reference-typed
    /// parameters, taken from the front of the IL arg list (slot 0 is `this` on an instance
    /// method). No boxing is emitted, so only reference-typed slots may be forwarded.
    /// </summary>
    private static void PrependStaticCall(
        ModuleDefinition module, MethodDefinition target, MethodInfo helperMi, int argSlots)
    {
        if (helperMi.ReturnType != typeof(void))
            throw new InvalidOperationException(
                $"[Cecil] prepend helper {helperMi.DeclaringType?.Name}.{helperMi.Name} must return void");
        if (helperMi.GetParameters().Length != argSlots)
            throw new InvalidOperationException(
                $"[Cecil] prepend helper {helperMi.DeclaringType?.Name}.{helperMi.Name} takes "
                + $"{helperMi.GetParameters().Length} parameter(s) but {argSlots} arg slot(s) were requested");
        int available = target.Parameters.Count + (target.HasThis ? 1 : 0);
        if (argSlots > available)
            throw new InvalidOperationException(
                $"[Cecil] {target.DeclaringType.Name}.{target.Name} has only {available} arg slot(s), "
                + $"{argSlots} requested — Ncl shape changed; do not commit");

        var helperRef = module.ImportReference(helperMi);
        var body = target.Body;
        var il = body.GetILProcessor();
        var first = body.Instructions[0];
        for (int i = 0; i < argSlots; i++)
            il.InsertBefore(first, il.Create(OpCodes.Ldarg, i));
        il.InsertBefore(first, il.Create(OpCodes.Call, helperRef));
        if (body.MaxStackSize < argSlots) body.MaxStackSize = argSlots;
        Console.Error.WriteLine(
            $"[Cecil] Prepended {helperMi.DeclaringType?.Name}.{helperMi.Name} to "
            + $"{target.DeclaringType.Name}.{target.Name}");
    }

    /// <summary>
    /// Overload accepting a helper on ANY class (not just BcRuntime) — e.g. the
    /// CreateTarget helpers that live on CodeunitPatches / RecordPatches / XmlPortPatches.
    /// Same forwarding + per-arg boxing semantics as the name-based overload.
    /// </summary>
    private static void ReplaceBodyWithHelper(
        ModuleDefinition module, MethodDefinition target, MethodInfo helperMi)
    {
        var helperRef = module.ImportReference(helperMi);
        var helperParams = helperMi.GetParameters();
        // Total IL arg slots: declared params + 1 for `this` on an instance method.
        int argCount = target.Parameters.Count + (target.HasThis ? 1 : 0);
        var body = target.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        var il = body.GetILProcessor();
        for (int i = 0; i < argCount; i++)
        {
            il.Append(il.Create(OpCodes.Ldarg, i));

            // Per-arg boxing: when the target's IL arg is a VALUE TYPE but the helper's
            // corresponding parameter is a reference type (e.g. `object`), the raw `ldarg`
            // leaves an unboxed value on the stack — invalid IL. Emit `box <valueType>`.
            //
            // The IL arg at index i maps to:
            //   instance method: i==0 → `this` (always the declaring type, a reference type
            //                    for NavMethodScope etc., so never boxed); i>=1 → param i-1.
            //   static method:   i    → param i.
            int paramIdx = target.HasThis ? i - 1 : i;
            if (paramIdx < 0) continue; // `this` slot — declaring type is a reference type here.

            var targetParamType = target.Parameters[paramIdx].ParameterType;
            if (!targetParamType.IsValueType) continue; // already a reference — no box needed.

            // Helper param type (System.Type). The helper is a STATIC method whose first
            // parameter is the receiver (`self`) for an instance target — so the helper
            // param for target IL-arg slot i is helperParams[i] (NOT helperParams[paramIdx]).
            // i==0 (`this`) is handled above (paramIdx<0 → continue), so here i>=1 maps to
            // helperParams[i] for an instance method, or helperParams[i] for a static method
            // too — in both cases the helper param index equals the IL-arg slot index i.
            // If it's NOT a value type (object / interface / class), the value-type arg must
            // be boxed to satisfy the reference parameter.
            var helperParamType = i < helperParams.Length ? helperParams[i].ParameterType : null;
            bool helperWantsReference = helperParamType != null && !helperParamType.IsValueType;
            if (helperWantsReference)
                il.Append(il.Create(OpCodes.Box, targetParamType));
        }
        il.Append(il.Create(OpCodes.Call, helperRef));

        // Return-type adaptation: the helper may return a less-derived reference type
        // than the target's declared return (e.g. helper returns `object`, target's
        // CreateTarget() returns `NavTestPage`). The IL `ret` requires the stack value
        // to be assignable to the declared return type, so emit a `castclass` to the
        // target return type when the helper return is a wider/different reference type.
        // (For an exact-match or covariant-already-derived helper return, no cast is
        // needed; for value-type returns we leave as-is — none of our targets do that.)
        var targetRet = target.ReturnType;
        var helperRetType = helperMi.ReturnType;
        bool needsCast =
            targetRet.FullName != "System.Void"
            && !targetRet.IsValueType
            && helperRetType != typeof(void)
            && !helperRetType.IsValueType
            && targetRet.FullName != NormalizeTypeName(helperRetType.FullName ?? "");
        if (needsCast)
            il.Append(il.Create(OpCodes.Castclass, targetRet));

        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = Math.Max(1, argCount);
        Console.Error.WriteLine($"[Cecil] Replaced {target.FullName} → {helperMi.DeclaringType?.Name}.{helperMi.Name}"
            + (needsCast ? $" (castclass {targetRet.Name})" : ""));
    }

    /// <summary>
    /// PREPEND a conditional guard to the START of <paramref name="target"/> (a value-type-return
    /// method) WITHOUT replacing the original body. Emits, before the original first instruction:
    /// <code>
    ///   if (predicate(this, arg1)) {           // predicate: object,object → bool
    ///       return (TRet)(boxed) handler(this, arg1, arg2);   // handler: object,object,bool → object
    ///   }
    ///   // else fall through to the ORIGINAL body unchanged
    /// </code>
    /// Used to intercept DataAccess.InnerFindAsync for the virtual Field table only, leaving the
    /// original async body intact for every other table. The handler returns a boxed value-type
    /// (ValueTask&lt;ResultSetEnumerator&gt;); we unbox.any to the declared return type.
    /// Token-safe: imports only our two helper refs + the target's OWN return-type reference.
    /// Assumes target shape: instance method (this), arg1 = request (ref type), arg2 = bool.
    /// </summary>
    private static void PrependFieldFindGuard(
        ModuleDefinition module, MethodDefinition target, MethodInfo predicateMi, MethodInfo handlerMi)
    {
        if (!target.HasThis || target.Parameters.Count < 2)
            throw new InvalidOperationException($"[Cecil] {target.FullName} unexpected shape for PrependFieldFindGuard");
        if (predicateMi.ReturnType != typeof(bool))
            throw new InvalidOperationException($"[Cecil] {predicateMi.Name} must return bool");
        if (handlerMi.ReturnType != typeof(object))
            throw new InvalidOperationException($"[Cecil] {handlerMi.Name} must return object");
        var targetRet = target.ReturnType;
        if (!targetRet.IsValueType)
            throw new InvalidOperationException($"[Cecil] {target.FullName} return must be a value type");

        var predicateRef = module.ImportReference(predicateMi);
        var handlerRef = module.ImportReference(handlerMi);
        var body = target.Body;
        var il = body.GetILProcessor();
        var first = body.Instructions[0];

        // Build the prologue in REVERSE-insert order before `first`.
        // IL:
        //   ldarg.0                       // this
        //   ldarg.1                       // request
        //   call bool predicate(object,object)
        //   brfalse  <first>              // not Field → run original body
        //   ldarg.0                       // this
        //   ldarg.1                       // request
        //   ldarg.2                       // fromPosition (bool)
        //   call object handler(object,object,bool)
        //   unbox.any <TRet>
        //   ret
        var ldThis1 = il.Create(OpCodes.Ldarg_0);
        var ldReq1 = il.Create(OpCodes.Ldarg_1);
        var callPred = il.Create(OpCodes.Call, predicateRef);
        var brFalse = il.Create(OpCodes.Brfalse, first);
        var ldThis2 = il.Create(OpCodes.Ldarg_0);
        var ldReq2 = il.Create(OpCodes.Ldarg_1);
        var ldPos = il.Create(OpCodes.Ldarg_2);
        var callHandler = il.Create(OpCodes.Call, handlerRef);
        var unbox = il.Create(OpCodes.Unbox_Any, targetRet);
        var ret = il.Create(OpCodes.Ret);

        il.InsertBefore(first, ldThis1);
        il.InsertBefore(first, ldReq1);
        il.InsertBefore(first, callPred);
        il.InsertBefore(first, brFalse);
        il.InsertBefore(first, ldThis2);
        il.InsertBefore(first, ldReq2);
        il.InsertBefore(first, ldPos);
        il.InsertBefore(first, callHandler);
        il.InsertBefore(first, unbox);
        il.InsertBefore(first, ret);

        // Prologue pushes at most 3 slots (this,request,bool); ensure stack is large enough.
        body.MaxStackSize = Math.Max(body.MaxStackSize, 3);
        Console.Error.WriteLine($"[Cecil] Prepended Field-find guard to {target.FullName} → {handlerMi.DeclaringType?.Name}.{handlerMi.Name}");
    }

    /// <summary>
    /// Replace <paramref name="target"/>'s body with a constant/no-op return. For a void
    /// method (incl. cctor) emits just `ret` — the unused args stay as ignored slots.
    /// For a value return, emits the appropriate const push then `ret`.
    /// Models the existing IsEventSubscribed / ALHasTableConnection / ALCommit-no-op blocks.
    /// </summary>
    private static void ReplaceBodyConst(MethodDefinition target, ConstResult result)
    {
        var body = target.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        var il = body.GetILProcessor();
        switch (result)
        {
            case ConstResult.Void:
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 0;
                break;
            case ConstResult.True:
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                break;
            case ConstResult.False:
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                break;
            case ConstResult.Null:
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                break;
        }
        Console.Error.WriteLine($"[Cecil] Replaced {target.FullName} → const {result}");
    }

    private enum ConstResult { Void, True, False, Null }

    private static void ValidateRewrittenAssembly(byte[] bytes)
    {
        using var peReader = new PEReader(ImmutableArray.Create(bytes));
        var mr = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(peReader);
        foreach (var methodHandle in mr.MethodDefinitions)
        {
            var methodDef = mr.GetMethodDefinition(methodHandle);
            if (methodDef.RelativeVirtualAddress == 0) continue;

            var methodName = mr.GetString(methodDef.Name);
            System.Reflection.Metadata.MethodBodyBlock body;
            try
            {
                body = System.Reflection.Metadata.PEReaderExtensions.GetMethodBody(peReader, methodDef.RelativeVirtualAddress);
            }
            catch (BadImageFormatException ex)
            {
                throw new InvalidOperationException($"[Cecil] Rewritten Ncl has dangling metadata token in method '{methodName}': {ex.Message}", ex);
            }

            foreach (var region in body.ExceptionRegions)
            {
                if (region.Kind == System.Reflection.Metadata.ExceptionRegionKind.Catch && !region.CatchType.IsNil)
                    ValidateToken(mr, MetadataTokens.GetToken(region.CatchType), methodName, methodDef.RelativeVirtualAddress);
            }

            ValidateMethodBodyTokens(mr, body.GetILBytes(), methodName);
        }
    }

    private static void ValidateMethodBodyTokens(System.Reflection.Metadata.MetadataReader mr, byte[] il, string methodName)
    {
        ReadOnlySpan<byte> bytes = il;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var instructionOffset = offset;
            System.Reflection.Emit.OpCode opCode;
            var first = bytes[offset++];
            if (first == 0xFE)
            {
                if (offset >= bytes.Length || !DoubleByteOpCodes.TryGetValue(bytes[offset++], out opCode))
                    throw new InvalidOperationException($"[Cecil] Rewritten Ncl has malformed IL in method '{methodName}' at IL_{instructionOffset:X4}");
            }
            else if (!SingleByteOpCodes.TryGetValue(first, out opCode))
            {
                throw new InvalidOperationException($"[Cecil] Rewritten Ncl has malformed IL in method '{methodName}' at IL_{instructionOffset:X4}");
            }

            int token;
            switch (opCode.OperandType)
            {
                case System.Reflection.Emit.OperandType.InlineNone:
                    break;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                case System.Reflection.Emit.OperandType.ShortInlineI:
                case System.Reflection.Emit.OperandType.ShortInlineVar:
                    offset += 1;
                    break;
                case System.Reflection.Emit.OperandType.InlineVar:
                    offset += 2;
                    break;
                case System.Reflection.Emit.OperandType.InlineI:
                case System.Reflection.Emit.OperandType.InlineBrTarget:
                case System.Reflection.Emit.OperandType.ShortInlineR:
                    offset += 4;
                    break;
                case System.Reflection.Emit.OperandType.InlineI8:
                case System.Reflection.Emit.OperandType.InlineR:
                    offset += 8;
                    break;
                case System.Reflection.Emit.OperandType.InlineSwitch:
                    EnsureRemaining(bytes, offset, 4, methodName, instructionOffset);
                    var caseCount = ReadInt32(bytes, offset);
                    offset += 4 + (caseCount * 4);
                    break;
                case System.Reflection.Emit.OperandType.InlineField:
                case System.Reflection.Emit.OperandType.InlineMethod:
                case System.Reflection.Emit.OperandType.InlineSig:
                case System.Reflection.Emit.OperandType.InlineString:
                case System.Reflection.Emit.OperandType.InlineTok:
                case System.Reflection.Emit.OperandType.InlineType:
                    EnsureRemaining(bytes, offset, 4, methodName, instructionOffset);
                    token = ReadInt32(bytes, offset);
                    ValidateToken(mr, token, methodName, instructionOffset);
                    offset += 4;
                    break;
                case System.Reflection.Emit.OperandType.InlinePhi:
                    throw new InvalidOperationException($"[Cecil] Rewritten Ncl uses unsupported InlinePhi in method '{methodName}' at IL_{instructionOffset:X4}");
                default:
                    throw new InvalidOperationException($"[Cecil] Rewritten Ncl hit unknown operand type {opCode.OperandType} in method '{methodName}' at IL_{instructionOffset:X4}");
            }

            if (offset > bytes.Length)
                throw new InvalidOperationException($"[Cecil] Rewritten Ncl has truncated IL in method '{methodName}' at IL_{instructionOffset:X4}");
        }
    }

    private static void ValidateToken(System.Reflection.Metadata.MetadataReader mr, int token, string methodName, int instructionOffset)
    {
        try
        {
            var handle = MetadataTokens.Handle(token);
            if (handle.IsNil)
                return;

            switch (handle.Kind)
            {
                case System.Reflection.Metadata.HandleKind.TypeDefinition:
                    _ = mr.GetTypeDefinition((System.Reflection.Metadata.TypeDefinitionHandle)handle);
                    break;
                case System.Reflection.Metadata.HandleKind.TypeReference:
                    _ = mr.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)handle);
                    break;
                case System.Reflection.Metadata.HandleKind.TypeSpecification:
                    _ = mr.GetTypeSpecification((System.Reflection.Metadata.TypeSpecificationHandle)handle);
                    break;
                case System.Reflection.Metadata.HandleKind.FieldDefinition:
                    _ = mr.GetFieldDefinition((System.Reflection.Metadata.FieldDefinitionHandle)handle);
                    break;
                case System.Reflection.Metadata.HandleKind.MethodDefinition:
                    _ = mr.GetMethodDefinition((System.Reflection.Metadata.MethodDefinitionHandle)handle);
                    break;
                case System.Reflection.Metadata.HandleKind.MemberReference:
                    _ = mr.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)handle);
                    break;
                case System.Reflection.Metadata.HandleKind.MethodSpecification:
                    _ = mr.GetMethodSpecification((System.Reflection.Metadata.MethodSpecificationHandle)handle);
                    break;
                case System.Reflection.Metadata.HandleKind.StandaloneSignature:
                    _ = mr.GetStandaloneSignature((System.Reflection.Metadata.StandaloneSignatureHandle)handle);
                    break;
                case System.Reflection.Metadata.HandleKind.UserString:
                    _ = mr.GetUserString((System.Reflection.Metadata.UserStringHandle)handle);
                    break;
                default:
                    throw new BadImageFormatException($"unsupported token kind {handle.Kind}");
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException($"[Cecil] Rewritten Ncl has dangling metadata token: 0x{token:X8} in {methodName} at IL_{instructionOffset:X4}", ex);
        }
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> bytes, int offset, int size, string methodName, int instructionOffset)
    {
        if (offset + size > bytes.Length)
            throw new InvalidOperationException($"[Cecil] Rewritten Ncl has truncated IL in method '{methodName}' at IL_{instructionOffset:X4}");
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
        => bytes[offset]
         | (bytes[offset + 1] << 8)
         | (bytes[offset + 2] << 16)
         | (bytes[offset + 3] << 24);

    /// <summary>
    /// Zero the CorHeader.ManagedNativeHeader directory entry so CoreCLR sees the
    /// assembly as IL-only. Cecil's writer typically already drops the R2R native
    /// data because it rebuilds the PE; this is belt-and-suspenders.
    /// </summary>
    private static void StripR2RHeader(byte[] peBytes)
    {
        int peOffset = BitConverter.ToInt32(peBytes, 0x3C);
        int optHeaderOffset = peOffset + 4 + 20;
        ushort magic = BitConverter.ToUInt16(peBytes, optHeaderOffset);
        bool pe32Plus = magic == 0x20B;
        int dataDirOffset = optHeaderOffset + (pe32Plus ? 112 : 96);
        // Directory 14 (0-indexed) is the CLR header.
        int cliDirOffset = dataDirOffset + 14 * 8;
        uint cliRva = BitConverter.ToUInt32(peBytes, cliDirOffset);
        uint cliSize = BitConverter.ToUInt32(peBytes, cliDirOffset + 4);
        if (cliRva == 0 || cliSize == 0)
        {
            Console.Error.WriteLine("[Cecil] No CLI header found, skipping R2R strip");
            return;
        }
        int sectionCount = BitConverter.ToUInt16(peBytes, peOffset + 4 + 2);
        ushort sizeOfOptHeader = BitConverter.ToUInt16(peBytes, peOffset + 4 + 16);
        int sectionsStart = optHeaderOffset + sizeOfOptHeader;
        int cliFileOffset = -1;
        for (int i = 0; i < sectionCount; i++)
        {
            int secHdr = sectionsStart + i * 40;
            uint virtSize = BitConverter.ToUInt32(peBytes, secHdr + 8);
            uint virtAddr = BitConverter.ToUInt32(peBytes, secHdr + 12);
            uint rawAddr = BitConverter.ToUInt32(peBytes, secHdr + 20);
            if (cliRva >= virtAddr && cliRva < virtAddr + Math.Max(virtSize, 1u))
            {
                cliFileOffset = (int)(rawAddr + (cliRva - virtAddr));
                break;
            }
        }
        if (cliFileOffset < 0)
        {
            Console.Error.WriteLine("[Cecil] Could not locate CLI header in sections, skipping R2R strip");
            return;
        }
        // ManagedNativeHeader: offset 64 (8 bytes: RVA + Size)
        bool wasNonZero = false;
        for (int j = 0; j < 8; j++) if (peBytes[cliFileOffset + 64 + j] != 0) { wasNonZero = true; break; }
        for (int j = 0; j < 8; j++) peBytes[cliFileOffset + 64 + j] = 0;
        // Also clear the COMIMAGE_FLAGS_IL_LIBRARY/NATIVE_ENTRYPOINT bits if set.
        // CorHeader.Flags is at offset 16; bit 0x10 = COMIMAGE_FLAGS_NATIVE_ENTRYPOINT, bit 0x04 = COMIMAGE_FLAGS_IL_LIBRARY.
        uint flags = BitConverter.ToUInt32(peBytes, cliFileOffset + 16);
        uint clearedFlags = flags & ~0x10u; // clear NATIVE_ENTRYPOINT
        if (clearedFlags != flags)
        {
            var bytes = BitConverter.GetBytes(clearedFlags);
            Array.Copy(bytes, 0, peBytes, cliFileOffset + 16, 4);
        }
        Console.Error.WriteLine($"[Cecil] R2R ManagedNativeHeader zeroed (was non-zero: {wasNonZero}), Flags: 0x{flags:X8} → 0x{clearedFlags:X8}");
    }

    public static bool PreloadRewrittenNcl(string dir)
    {
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (alreadyLoaded)
        {
            Console.Error.WriteLine("[Cecil] WARNING: Ncl already loaded before Cecil preload — rewrite will NOT take effect");
            return false;
        }
        var nclPath = Path.Combine(dir, "Microsoft.Dynamics.Nav.Ncl.dll");
        var modifiedBytes = RewriteNcl(nclPath);

        Assembly? rewritten = null;
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (alc, name) =>
        {
            if (name.Name == "Microsoft.Dynamics.Nav.Ncl")
            {
                Console.Error.WriteLine($"[Cecil] ALC.Resolving Ncl → returning rewritten copy");
                return rewritten;
            }
            return null;
        };
        rewritten = Assembly.Load(modifiedBytes);
        Console.Error.WriteLine($"[Cecil] Loaded modified Ncl: {rewritten.FullName} (Location='{rewritten.Location}')");
        return true;
    }

    /// <summary>
    /// Rewrites Ncl from the BC artifacts dir and writes the result to the runner's
    /// bin path (overwriting the build-time copy). Runs BEFORE the CLR's TPA probe
    /// resolves Ncl, so when CLR loads Ncl by name it gets our rewritten bytes.
    /// Results are cached in $HOME/.cache/al-runner/ncl-cecil/ keyed by a SHA256 of
    /// the source Ncl bytes, the runner assembly's own CONTENT hash (issue #1871 —
    /// previously the runner assembly's mtime, which changes on every CI rebuild even
    /// when the runner's bytes, and therefore the rewrite it produces, are unchanged;
    /// see RunnerFingerprint.ComputeContentHash), and CACHE_VERSION. Set
    /// AL_RUNNER_NCL_CACHE=0 to force a fresh rewrite without reading or writing cache.
    /// </summary>
    /// <summary>
    /// Returns <c>true</c> when this call performed a FRESH Cecil rewrite (cache MISS
    /// or cache disabled). In that case the caller MUST re-exec the process before
    /// loading Ncl: a process that runs the Cecil rewrite and then memory-maps the
    /// byte-identical rewritten Ncl in-process intermittently fails the load with
    /// BadImageFormatException 0x80131124 ("Index not found"), whereas a fresh process
    /// loading the same bytes via cache HIT always succeeds. Re-execing turns every
    /// cold run into the known-good HIT path. Returns <c>false</c> on cache HIT (the
    /// load is safe — proceed in this process).
    /// </summary>
    /// <summary>
    /// Publishes <paramref name="contents"/> to <paramref name="destPath"/> by writing a
    /// sibling temp file and renaming it over the destination.
    ///
    /// Never truncate-and-rewrite a DLL in place. Every loaded assembly is memory-mapped, so
    /// overwriting the file's existing inode invalidates the mappings any live process holds
    /// and that process takes SIGBUS on its next page touch — the crash class behind the
    /// exit-135 (128+7) integration-test flakes and the al-runner SIGBUS coredumps.
    ///
    /// It also wedges the machine: a task that dies this way can leave its mmap_lock held, and
    /// every subsequent ps/pgrep/pkill blocks in __access_remote_vm reading its /proc entry.
    /// Those readers are unkillable and each new one blocks on the previous, so process listing
    /// stays broken until reboot even though CPU, memory and I/O are idle.
    ///
    /// rename(2) is atomic and only swaps the directory entry: existing mappings keep the OLD
    /// inode and stay valid, new opens see the new file. Same pattern already used for the
    /// cache entry itself.
    /// </summary>
    private static void AtomicReplace(string destPath, byte[] contents)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
        var tempPath = Path.Combine(dir, Path.GetFileName(destPath) + ".tmp." + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(tempPath, contents);

            // On Windows, a real-time antivirus scanner (confirmed: Defender) opens a
            // freshly-written file for scanning and holds it long enough that a plain
            // File.Move(overwrite:true) — MoveFileEx(MOVEFILE_REPLACE_EXISTING) under the
            // hood — fails with ERROR_ACCESS_DENIED. Reproduced on real Windows 11 (#1650
            // investigation): al-runner died here on every run without a manual Defender
            // exclusion. POSIX rename(2) (what this call becomes on Linux/macOS) has no
            // equivalent lock, so none of this fires there — File.Move succeeds first try.
            //
            // File.Replace (the ReplaceFile Win32 API — designed to swap a file that may
            // have open handles) clears the lock that defeats File.Move; try it first when
            // destPath already exists, then fall back to a bounded, backed-off File.Move
            // retry for the create case (ReplaceFile requires an existing destination) and
            // as a safety net if Replace itself is ever refused.
            const int maxAttempts = 60;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (attempt == 1 && File.Exists(destPath))
                    {
                        try { File.Replace(tempPath, destPath, destinationBackupFileName: null); break; }
                        catch { /* fall through to the File.Move retry loop below */ }
                    }
                    File.Move(tempPath, destPath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(500);
                }
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <inheritdoc cref="AtomicReplace(string, byte[])"/>
    private static void AtomicReplaceFrom(string sourcePath, string destPath)
        => AtomicReplace(destPath, ReadAllBytesWithRetry(sourcePath));

    /// <summary>
    /// #2489: <paramref name="path"/> here is always the shared <c>ncl-cecil</c> cache
    /// entry (keyed by content hash, so every process racing the same key writes
    /// byte-identical bytes) — under N concurrent shadow-dir builds/re-execs sharing one
    /// key, several processes legitimately read this SAME path around the same moment a
    /// sibling MISS-writer is <c>AtomicReplace</c>-ing it (temp-write + rename). On
    /// Windows a reader can catch that rename mid-flight and get
    /// <c>ERROR_SHARING_VIOLATION</c>. Same bounded retry/backoff shape as
    /// <see cref="AtomicReplace(string, byte[])"/>'s own writer-side retry, just for a
    /// reader instead.
    /// </summary>
    private static byte[] ReadAllBytesWithRetry(string path)
    {
        const int maxAttempts = 60;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException) when (attempt < maxAttempts) { System.Threading.Thread.Sleep(250); }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { System.Threading.Thread.Sleep(250); }
        }
        return File.ReadAllBytes(path); // final attempt — let it throw if still failing
    }

    public static bool RewriteInPlace(string srcDir, string binNclPath)
    {
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (alreadyLoaded)
        {
            Console.Error.WriteLine("[Cecil] WARNING: Ncl already loaded before in-place rewrite — no effect");
            return false;
        }

        var nclSrc = Path.Combine(srcDir, "Microsoft.Dynamics.Nav.Ncl.dll");

        if (Environment.GetEnvironmentVariable("AL_RUNNER_NCL_CACHE") == "0")
        {
            Console.Error.WriteLine("[Cecil] Cecil cache DISABLED via AL_RUNNER_NCL_CACHE=0");
            var bytes = RewriteNcl(nclSrc);
            AtomicReplace(binNclPath, bytes);
            Console.Error.WriteLine($"[Cecil] Wrote rewritten Ncl to {binNclPath} ({bytes.Length} bytes)");
            return true;
        }

        var cacheKey = ComputeCacheKey(nclSrc);
        var shortKey = cacheKey[..8];

        // #1821: was hardcoded to ~/.cache/al-runner/ncl-cecil regardless of --cache;
        // now follows the same isolation root al-out already honoured. Default (no
        // --cache) is unchanged, so CI's `smoke` job rm -rf still targets the right dir.
        var cacheDir = CacheRoots.Resolve("ncl-cecil");
        Directory.CreateDirectory(cacheDir);
        var cachePath = Path.Combine(cacheDir, $"{cacheKey}.dll");

        if (File.Exists(cachePath))
        {
            Console.Error.WriteLine($"[Cecil] Cecil cache HIT (key={shortKey})");

            // #2489: an earlier version of this method skipped this AtomicReplaceFrom
            // call entirely when the destination already held these exact bytes (a
            // shadow-re-exec CHILD's own Ncl.dll, already populated by the PARENT
            // process's EnsureShadowDir call before publishing). That optimization was
            // withdrawn — it could not be distinguished from a residual, hard-to-pin
            // regression measured under CI's real concurrent-subprocess load
            // (AlRunner.Tests classes racing this exact shared-key path), and
            // AtomicReplaceFrom's rename-based replace is ALREADY safe for concurrent
            // readers on its own (an open handle/mmap to the old inode keeps working
            // through a rename; nothing here truncates a file in place). The
            // BcArtifacts.VerifyEngineConsistency read this comment used to cite as the
            // race motivating the skip is hardened directly instead — see its own retry
            // wrapper — so removing the skip does not reopen that hole.
            AtomicReplaceFrom(cachePath, binNclPath);
            Console.Error.WriteLine($"[Cecil] Copied cached Ncl to {binNclPath}");
            PruneCacheFiles(cacheDir, cachePath, keepNewest: 8);
            return false;
        }

        Console.Error.WriteLine($"[Cecil] Cecil cache MISS — rewrote and cached (key={shortKey})");
        var modifiedBytes = RewriteNcl(nclSrc);

        // Write to cache atomically via temp-file-then-rename so concurrent runners
        // never read a partially-written cache entry. Routed through AtomicReplace for
        // the same Windows AV-lock retry it applies to binNclPath below.
        AtomicReplace(cachePath, modifiedBytes);
        Console.Error.WriteLine($"[Cecil] Saved to cache ({modifiedBytes.Length} bytes)");

        // Produce binNclPath via File.Copy from the freshly-written cache entry,
        // mirroring the cache-HIT path above. (Note: this alone does NOT prevent the
        // cold-run load crash — a process that ran the Cecil rewrite then loads the
        // byte-identical Ncl in-process still intermittently fails with
        // BadImageFormatException 0x80131124. The caller re-execs on the `true` return
        // below so the actual load always happens in a fresh process via cache HIT.)
        AtomicReplaceFrom(cachePath, binNclPath);
        Console.Error.WriteLine($"[Cecil] Copied rewritten Ncl to {binNclPath} ({modifiedBytes.Length} bytes)");
        PruneCacheFiles(cacheDir, cachePath, keepNewest: 8);
        return true;
    }

    /// <summary>
    /// #1871: the runner-identity component of this key used to be
    /// <c>File.GetLastWriteTimeUtc(typeof(NclCecilRewrite).Assembly.Location).Ticks</c> —
    /// the RUNNER assembly's own build-output mtime, which changes on every fresh
    /// `dotnet build`/`dotnet publish` (including every CI run) even when the runner's
    /// bytes, and therefore the Cecil rewrite it produces, are byte-for-byte identical to
    /// a prior run. A `ncl-cecil` entry persisted across CI runs would therefore MISS
    /// unconditionally — same defect family as #1815 (al-out) / #1820 (bc-symbols).
    /// Replaced with <see cref="RunnerFingerprint.ContentHash"/>: stable across rebuilds
    /// of unchanged source, still sensitive to any change in the runner's own
    /// Cecil-rewrite logic. No `bc:` line is needed here (unlike RunnerFingerprint's
    /// WriteKeyLines) — the rewrite only depends on the source Ncl bytes (already hashed
    /// below) and the runner's own rewrite logic, neither of which vary by BC version
    /// within one build.
    /// </summary>
    private static string ComputeCacheKey(string nclPath)
    {
        var nclBytes = File.ReadAllBytes(nclPath);
        return ComputeCacheKeyCore(nclBytes, RunnerFingerprint.ContentHash);
    }

    /// <summary>
    /// Testable core of <see cref="ComputeCacheKey(string)"/>: takes the Ncl bytes and
    /// runner content hash explicitly so a test can vary either independently without
    /// needing to swap out the actual running assembly on disk (mirrors
    /// <c>RunnerFingerprint.WriteKeyLines(Action{string}, string, Version)</c>'s
    /// explicit-parameter testable-core pattern).
    /// </summary>
    internal static string ComputeCacheKeyCore(byte[] nclBytes, string runnerContentHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(nclBytes);
        hash.AppendData(Encoding.UTF8.GetBytes(runnerContentHash));
        hash.AppendData(BitConverter.GetBytes(CACHE_VERSION));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void PruneCacheFiles(string cacheDir, string protectedPath, int keepNewest)
    {
        var protectedFullPath = Path.GetFullPath(protectedPath);
        var stale = Directory.EnumerateFiles(cacheDir, "*.dll")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(keepNewest)
            .Where(file => !string.Equals(file.FullName, protectedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in stale)
        {
            try { file.Delete(); }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[Cecil] WARN: failed to prune stale cache file {file.Name}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"[Cecil] WARN: failed to prune stale cache file {file.Name}: {ex.Message}");
            }
        }
    }

    public static void VerifyRewriteLanded()
    {
        var ncl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (ncl == null) { Console.Error.WriteLine("[Cecil] VERIFY: Ncl not loaded"); return; }
        var t = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject");
        if (t == null) { Console.Error.WriteLine("[Cecil] VERIFY: type not found"); return; }
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                            .Where(mi => mi.Name == "IsEventSubscribed"))
        {
            var body = m.GetMethodBody();
            var il = body?.GetILAsByteArray();
            var sig = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
            Console.Error.WriteLine($"[Cecil] VERIFY: {m.Name}({sig}) IL len={il?.Length} bytes={(il==null?"<null>":string.Join(" ", il.Take(20).Select(b => b.ToString("X2"))))}");
        }
    }
    /// <summary>
    /// Net evaluation-stack change of one instruction, for finding a statement boundary to
    /// truncate a method body at. Only as general as it needs to be — Varpop/Varpush are
    /// resolved from the call's own signature, and anything genuinely ambiguous throws
    /// rather than guessing, because a wrong answer here shows up as a bare
    /// InvalidProgramException at JIT time with nothing pointing back at this code.
    /// </summary>
    private static int StackDelta(Instruction ins)
    {
        var op = ins.OpCode;
        int pop = 0, push = 0;

        switch (op.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: pop = 0; break;
            case StackBehaviour.Pop1:
            case StackBehaviour.Popi:
            case StackBehaviour.Popref: pop = 1; break;
            case StackBehaviour.Pop1_pop1:
            case StackBehaviour.Popi_pop1:
            case StackBehaviour.Popi_popi:
            case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4:
            case StackBehaviour.Popi_popr8:
            case StackBehaviour.Popref_pop1:
            case StackBehaviour.Popref_popi: pop = 2; break;
            case StackBehaviour.Popi_popi_popi:
            case StackBehaviour.Popref_popi_popi:
            case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8:
            case StackBehaviour.Popref_popi_popref:
            case StackBehaviour.PopAll: pop = 0; break;   // only `leave`; not present in a kept prefix
            case StackBehaviour.Varpop:
                if (ins.Operand is MethodReference callee)
                {
                    pop = callee.Parameters.Count;
                    if (callee.HasThis && op != OpCodes.Newobj) pop++;
                }
                else if (op == OpCodes.Ret) pop = 0;
                else throw new InvalidOperationException($"StackDelta: unsupported Varpop on {op}");
                break;
            default: throw new InvalidOperationException($"StackDelta: unsupported pop behaviour {op.StackBehaviourPop} on {op}");
        }

        switch (op.StackBehaviourPush)
        {
            case StackBehaviour.Push0: push = 0; break;
            case StackBehaviour.Push1:
            case StackBehaviour.Pushi:
            case StackBehaviour.Pushi8:
            case StackBehaviour.Pushr4:
            case StackBehaviour.Pushr8:
            case StackBehaviour.Pushref: push = 1; break;
            case StackBehaviour.Push1_push1: push = 2; break;
            case StackBehaviour.Varpush:
                if (ins.Operand is MethodReference m2)
                    push = m2.ReturnType.FullName == "System.Void" ? 0 : 1;
                else throw new InvalidOperationException($"StackDelta: unsupported Varpush on {op}");
                break;
            default: throw new InvalidOperationException($"StackDelta: unsupported push behaviour {op.StackBehaviourPush} on {op}");
        }

        if (op == OpCodes.Newobj) push = 1;
        return push - pop;
    }

    /// <summary>
    /// Prepend `if (!RunnerFormInit.ShouldRunRealFormInit(this)) return default;` to an
    /// instance method, keeping its original body for the opted-in case. Used where the
    /// runner previously REPLACED a NavForm body with a default return: callers that were
    /// fine with the default still get exactly that, byte for byte, and only a form the
    /// runner is deliberately driving runs BC's real code.
    /// </summary>
    private static void GuardWithDefaultReturn(ModuleDefinition module, MethodDefinition method, MethodReference guardRef)
    {
        var body = method.Body;
        var il = body.GetILProcessor();
        var original = body.Instructions[0];
        var returnType = method.ReturnType;

        var prologue = new List<Instruction>
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Call, guardRef),
            il.Create(OpCodes.Brtrue, original),
        };

        if (returnType.FullName == "System.Void")
            prologue.Add(il.Create(OpCodes.Ret));
        else if (!returnType.IsValueType)
        {
            prologue.Add(il.Create(OpCodes.Ldnull));
            prologue.Add(il.Create(OpCodes.Ret));
        }
        else if (returnType.FullName is "System.Int32" or "System.Boolean" or "System.Byte"
                                     or "System.Int16" or "System.Int64" or "System.Char")
        {
            prologue.Add(il.Create(OpCodes.Ldc_I4_0));
            prologue.Add(il.Create(OpCodes.Ret));
        }
        else
        {
            // ValueTask<T> / ValueTuple<...> / other value types → default(T). The local is
            // appended to the ORIGINAL locals, so it must be addressed by reference rather
            // than by the index-0 assumption that a cleared body could get away with.
            var local = new VariableDefinition(module.ImportReference(returnType));
            body.Variables.Add(local);
            body.InitLocals = true;
            prologue.Add(il.Create(OpCodes.Ldloca_S, local));
            prologue.Add(il.Create(OpCodes.Initobj, module.ImportReference(returnType)));
            prologue.Add(il.Create(OpCodes.Ldloc_S, local));
            prologue.Add(il.Create(OpCodes.Ret));
        }

        foreach (var ins in prologue) il.InsertBefore(original, ins);
        body.MaxStackSize = Math.Max(body.MaxStackSize, 2);
    }


}
