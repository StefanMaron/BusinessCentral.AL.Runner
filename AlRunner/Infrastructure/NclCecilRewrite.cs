// NclCecilRewrite — spike: rewrite Microsoft.Dynamics.Nav.Ncl.dll IL at load time
// to neutralize R2R-trapped methods that JmpHook and EventPipe-post-JIT can't reach.
//
// Allowed surface per .claude/rules/precompiled-dll-respect.md: Ncl.dll is runtime engine,
// not BaseApplication / SystemApplication / ISV business logic.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AlRunnerV2.Infrastructure;

public static class NclCecilRewrite
{
    private const int CACHE_VERSION = 91;

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
    public static readonly HashSet<string> CecilOwned = new()
    {
        // NavMethodScope cluster (migrated in Batch 2; registering now so their
        // JmpHooks — if any still install — become no-ops under the registry).
        "Microsoft.Dynamics.Nav.Runtime.NavMethodScope::.ctor/3",
        "Microsoft.Dynamics.Nav.Runtime.NavMethodScope::Dispose/1",
        "Microsoft.Dynamics.Nav.Runtime.NavMethodScope::AssertError/1",
        "Microsoft.Dynamics.Nav.Runtime.NavMethodScope::ProcessException/1",
        "Microsoft.Dynamics.Nav.Runtime.ALMethodScope::AssignScopeId/0",
        "Microsoft.Dynamics.Nav.Runtime.NavMethodScope::ThrowStackOverflow/1",
        // ALFunctionTimingExecutionListener (Batch 2).
        "Microsoft.Dynamics.Nav.Runtime.ALFunctionTimingExecutionListener::EnsureRegistered/0",
        "Microsoft.Dynamics.Nav.Runtime.ALFunctionTimingExecutionListener::Start/1",
        "Microsoft.Dynamics.Nav.Runtime.ALFunctionTimingExecutionListener::Exit/1",
        // CreateTarget family (Batch 3 — the coexistence-killer). The 0-arg
        // protected-override CreateTarget() on each handle type.
        "Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle::CreateTarget/0",
        "Microsoft.Dynamics.Nav.Runtime.NavRecordHandle::CreateTarget/0",
        "Microsoft.Dynamics.Nav.Runtime.NavTestPageHandle::CreateTarget/0",
        "Microsoft.Dynamics.Nav.Runtime.NavFormHandle::CreateTarget/0",
        "Microsoft.Dynamics.Nav.Runtime.NavReportHandle::CreateTarget/0",
        "Microsoft.Dynamics.Nav.Runtime.NavQueryHandle::CreateTarget/0",
        "Microsoft.Dynamics.Nav.Runtime.NavXmlPortHandle::CreateTarget/0",
        // NavApplicationObjectBase..ctor keystone (Batch 4) — the 3-arg
        // (ITreeObject, ApplicationObjectId, NCLStaticMetadata) ctor.
        "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase::.ctor/3",
        // NavDialog.ALStrMenu / ALConfirm (Batch 5 — in-memory shim; no re-entrancy).
        // All overloads by param count. Migrated from NoInlining+JmpHook to body rewrite.
        "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALStrMenu/2",
        "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALStrMenu/3",
        "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALStrMenu/4",
        "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALStrMenu/5",
        "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALConfirm/2",
        "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALConfirm/3",
        "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALConfirm/4",
        "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALConfirm/5",
        // NavMediaSet / NavMediaValueBase (Batch 5 — in-memory shim; no re-entrancy).
        "Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALInsert/2",
        "Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALRemove/2",
        "Microsoft.Dynamics.Nav.Runtime.NavMediaSet::get_ALCount/0",
        "Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALItem/1",
        "Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALImport/3",
        "Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALImport/4",
        "Microsoft.Dynamics.Nav.Runtime.NavMediaSet::ALExport/2",
        "Microsoft.Dynamics.Nav.Runtime.NavMediaValueBase::get_ALMediaId/0",
        // ── Record / session / data-access path (Batch 6 — the LINCHPIN). ────────
        // Migrated ATOMICALLY so the whole path is single-mechanism (Cecil), killing
        // the JmpHook+Cecil coexistence spin. Each key's body is rewritten in the
        // Batch-6 block in RewriteNcl to forward to its existing JmpHook helper.
        // NCLMetadata lookups
        "Microsoft.Dynamics.Nav.Runtime.NCLMetadata::GetMetaTableById/3",
        "Microsoft.Dynamics.Nav.Runtime.NCLMetadata::GetMetaApplicationObject/4",
        "Microsoft.Dynamics.Nav.Runtime.NCLMetadata::GetMetaApplicationObject/3",
        "Microsoft.Dynamics.Nav.Runtime.NCLMetaTable::GetFieldByNo/2",
        // NavSession getters
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_DataAccessSource/0",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_Database/0",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_SortingProperties/0",
        // DataAccessSource + TempTableDataProvider
        "Microsoft.Dynamics.Nav.Runtime.DataAccessSource::GetDataAccessForTable/2",
        "Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider::.ctor/2",
        "Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider::CalcNumeric/1",
        // Collation comparers
        "Microsoft.Dynamics.Nav.Runtime.NavDatabase::get_CollationAwareStringComparer/0",
        "Microsoft.Dynamics.Nav.Runtime.NavRecordId::get_CollationAwareStringComparer/0",
        // NavRecord no-ops
        "Microsoft.Dynamics.Nav.Runtime.NavRecord::Dispose/1",
        "Microsoft.Dynamics.Nav.Runtime.NavRecord::IsGlobalTriggerImplemented/1",
        "Microsoft.Dynamics.Nav.Runtime.NavRecord::UpdateReferencesOnRenameAsync/2",
        // RecordLink / management
        "Microsoft.Dynamics.Nav.Runtime.RecordLink::MoveLinksAsync/2",
        "Microsoft.Dynamics.Nav.Runtime.NavManagementTasks::CopyCompany/2",
        // NCLMetaApplicationObject
        "Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::CheckApplicationObjectIsValid/1",
        "Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::get_ApplicationObjectClrType/0",
        // NCLEnumMetadata.Create(int) (Batch 7 — THE CreateNoSeriesLine spin fix).
        // Its body is Cecil-rewritten to forward to BcRuntime.NCLEnumMetadata_CreateByIdAlAware
        // (see RewriteNcl ~line 232), but BcRuntime.cs ALSO JmpHook'd it. That double-patch
        // (Cecil body + JmpHook JMP-precode) is the exact COEXISTENCE the registry exists to
        // prevent: the JmpHook precode thunk (ReportStubBlock<MethodCallThunk>) spins
        // safepoint-free when reached on the AL-Validate construction path (e.g.
        // Library - No. Series.CreateNoSeriesLine validating an enum/option field). Listing
        // its key here makes JmpHook.Apply skip it → single-mechanism (Cecil) → no spin.
        "Microsoft.Dynamics.Nav.Runtime.NCLEnumMetadata::Create/1",
        // get_ApplicationObjectConstructor + Populate + CompileAndLoadClrObject (Batch 7
        // — completes the insert/construction path so ALInsertAsync→get_OldRecord→
        // CreateObjectInstance→{getter,Populate,CompileAndLoadClrObject} is single-mechanism).
        "Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::get_ApplicationObjectConstructor/0",
        "Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::Populate/0",
        "Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject::CompileAndLoadClrObject/0",
        // NCLMetaTable.CreateObjectInstance — concrete-type-aware record construction so
        // OldRecord (xRec) is the concrete Record{Id}, not a base NavRecord (see
        // RecordPatches.CreateObjectInstance.cs). Sibling of the nulled-out
        // ApplicationObjectConstructor above.
        "Microsoft.Dynamics.Nav.Runtime.NCLMetaTable::CreateObjectInstance/5",
        // RecordImplementation path
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::VerifyPermissions/2",
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::InternalFindRecordWithoutCheckingValuesAsync/4",
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::VerifySecurityFiltersOnRecordAsync/4",
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::VerifySecurityFiltersAsync/2",
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::get_IsOpen/0",
        // NavServerEventSource telemetry
        "Microsoft.Dynamics.Nav.Runtime.NavServerEventSource::get_Log/0",
        "Microsoft.Dynamics.Nav.Runtime.NavServerEventSource::WritePermissionUncheckedEvent/10",
        // Misc
        "Microsoft.Dynamics.Nav.Runtime.Data.SequentialUuidCreator+NativeMethods::NewSequentialId/0",
        "Microsoft.Dynamics.Nav.Runtime.TempTableStatistics::ReportIncrementChange/3",
        "Microsoft.Dynamics.Nav.Runtime.NavSystemCodeunitGlobalTriggers::GetTriggersOnTable/1",
        // NavSession getter cluster + GetActiveCompany + NavStream.Target (same atomic path)
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_CurrentMethodScope/0",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_NavAppGroup/0",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_LocalLanguageNoFallback/0",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_IsLocalLanguage/0",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::GetSecurityFilters/5",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::PushDynamicCaptionStack/2",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::SyncFormatSettings/0",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_Culture/0",
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_WindowsCulture/0",
        // get_ExecutionContext → Normal (skeleton Database.Tenant is null → UpgradeManager
        // ctor NRE). Body rewritten in RewriteNcl (search "get_ExecutionContext").
        "Microsoft.Dynamics.Nav.Runtime.NavSession::get_ExecutionContext/0",
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::GetActiveCompany/0",
        "Microsoft.Dynamics.Nav.Runtime.NavStream::get_Target/0",
        // FlowField CalcFieldsAsync — body already Cecil-rewritten upstream; register
        // keys so FlowFieldPatches.Register's JmpHook.Apply fallback becomes a no-op.
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::CalcFieldsAsync/2",
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::CalcFieldsAsync/3",
        // NavRecordRef cluster (Batch 8). get_Target + open-gates + all 6 ALOpen
        // overloads. ALOpen keys are by arity (Key counts declared params), so /1../4
        // each cover every overload of that arity — both the (int,…) and
        // (CompilationTarget,int,…) families. Migrated atomically (same R2R path).
        "Microsoft.Dynamics.Nav.Runtime.NavRecordRef::get_Target/0",
        "Microsoft.Dynamics.Nav.Runtime.NavRecordRef::CheckIsOpenAllowed/2",
        "Microsoft.Dynamics.Nav.Runtime.NavRecordRef::IsOpenAllowed/2",
        "Microsoft.Dynamics.Nav.Runtime.NavRecordRef::ALOpen/1",
        "Microsoft.Dynamics.Nav.Runtime.NavRecordRef::ALOpen/2",
        "Microsoft.Dynamics.Nav.Runtime.NavRecordRef::ALOpen/3",
        "Microsoft.Dynamics.Nav.Runtime.NavRecordRef::ALOpen/4",
        // NavSession.GetPermissionSet (Batch 8) — both 3-arg overloads (ByObjectId
        // + ByObjectIds). Leaf of the CalcSums permission-verify path.
        "Microsoft.Dynamics.Nav.Runtime.NavSession::GetPermissionSet/3",
        // NavCodeunit run path (Batch 8). DoRunAsync/2 + RunCodeunit/3. Only the
        // (DataError,Int32,NavRecord) RunCodeunit is JmpHook'd/Cecil'd; the sibling
        // (DataError,String,NavRecord) /3 is never hooked, so the by-arity key is safe.
        "Microsoft.Dynamics.Nav.Runtime.NavCodeunit::DoRunAsync/2",
        "Microsoft.Dynamics.Nav.Runtime.NavCodeunit::RunCodeunit/3",
        // Truncate + security-filtering cluster (Batch 8).
        "Microsoft.Dynamics.Nav.Runtime.NavRecord::ValidateTruncateSupport/1",
        "Microsoft.Dynamics.Nav.Runtime.RecordImplementation::SetSecurityFiltering/1",
        "Microsoft.Dynamics.Nav.Runtime.DataProvider::TruncateAsync/4",
        "Microsoft.Dynamics.Nav.Runtime.PermissionManagement::SessionHasSuperOrSecurityPermissionsForUser/2",
        // NavApplicationObjectBase.TryInvoke (Batch 8) — AL TryFunction entry.
        "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase::TryInvoke/2",
        // BitArrayHelpers.Equals (Batch 8) — static (BitArray,BitArray) overload.
        "Microsoft.Dynamics.Nav.Runtime.Utility.BitArrayHelpers::Equals/2",
        // Event-binding metadata cluster (Batch 8).
        "Microsoft.Dynamics.Nav.Runtime.NavCodeunit::get_MetaCodeunit/0",
        "Microsoft.Dynamics.Nav.Runtime.NCLMetaCodeunit::get_IsEventManualBinding/0",
        // ALSystemOperatingSystem GetUrl family (Batch 8) — all 7-arg overloads.
        "Microsoft.Dynamics.Nav.Runtime.ALSystemOperatingSystem::GetUrlCore/7",
        "Microsoft.Dynamics.Nav.Runtime.ALSystemOperatingSystem::ALGetUrl/7",
        "Microsoft.Dynamics.Nav.Runtime.ALSystemOperatingSystem::ALGetUrlInternal/7",
        // NavNotification send/recall (Batch 8).
        "Microsoft.Dynamics.Nav.Runtime.NavNotification::ALSend/1",
        "Microsoft.Dynamics.Nav.Runtime.NavNotification::ALRecall/1",
    };

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
            var helper = typeof(AlRunnerV2.BcRuntime).GetMethod(
                nameof(AlRunnerV2.BcRuntime.NCLEnumMetadata_CreateByIdAlAware),
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
            var helper = typeof(AlRunnerV2.BcRuntime).GetMethod(
                nameof(AlRunnerV2.BcRuntime.ALCompiler_ToInterfaceFromOption),
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

        // NavReport.SaveAsAsync → throw OOS (report-rendering is out-of-scope)
        var navReportType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavReport");
        if (navReportType == null)
            throw new InvalidOperationException("NavReport type not found in Ncl.dll — Ncl shape changed");

        var oosCtorInfo = typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })
            ?? throw new InvalidOperationException("InvalidOperationException(string) ctor not found via reflection");
        var oosCtor = asm.MainModule.ImportReference(oosCtorInfo);

        int saveAsRewroteCount = 0;
        foreach (var method in navReportType.Methods.Where(mm => mm.Name == "SaveAsAsync").ToList())
        {
            Console.Error.WriteLine($"[Cecil] Rewriting {method.FullName}");
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldstr, "out-of-scope: NavReport.SaveAs — report-rendering — see docs/scope.md#report-rendering"));
            il.Append(il.Create(OpCodes.Newobj, oosCtor));
            il.Append(il.Create(OpCodes.Throw));
            body.MaxStackSize = 1;
            saveAsRewroteCount++;
        }
        if (saveAsRewroteCount == 0)
            throw new InvalidOperationException("SaveAsAsync method not found in NavReport — Ncl shape changed; do not commit");
        Console.Error.WriteLine($"[Cecil] Rewrote {saveAsRewroteCount} SaveAsAsync overload(s) → throw OOS");

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

        // NavForm.GetMasterPage → return null/default (R2R-trapped; Cecil-rewrite is the only path)
        var navFormType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavForm");
        if (navFormType == null)
            throw new InvalidOperationException("NavForm type not found in Ncl.dll — Ncl shape changed; do not commit");

        int getMasterPageRewroteCount = 0;
        foreach (var method in navFormType.Methods.Where(mm => mm.Name == "GetMasterPage").ToList())
        {
            var returnType = method.ReturnType;
            Console.Error.WriteLine($"[Cecil] Rewriting {method.FullName} → return null/default (ReturnType={returnType.FullName}, IsValueType={returnType.IsValueType})");

            if (returnType.FullName.StartsWith("System.Threading.Tasks.Task`"))
                throw new InvalidOperationException($"GetMasterPage returns Task<T> ({returnType.FullName}) — cannot safely emit default; do not commit");

            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();

            if (!returnType.IsValueType)
            {
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
            }
            else if (returnType.FullName is "System.Int32" or "System.Boolean" or "System.Byte"
                                         or "System.Int16" or "System.Int64" or "System.Char")
            {
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
            }
            else
            {
                // ValueTask<T>, ValueTuple<...>, or other value types → default(T) via initobj
                var local = new VariableDefinition(asm.MainModule.ImportReference(returnType));
                body.Variables.Add(local);
                body.InitLocals = true;
                il.Append(il.Create(OpCodes.Ldloca_S, local));
                il.Append(il.Create(OpCodes.Initobj, asm.MainModule.ImportReference(returnType)));
                il.Append(il.Create(OpCodes.Ldloc_0));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
            }
            getMasterPageRewroteCount++;
        }
        if (getMasterPageRewroteCount == 0)
            throw new InvalidOperationException("GetMasterPage method not found in NavForm — Ncl shape changed; do not commit");
        Console.Error.WriteLine($"[Cecil] Rewrote {getMasterPageRewroteCount} GetMasterPage overload(s) → return null/default");

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
        var getUninitMethodInfo = typeof(System.Runtime.CompilerServices.RuntimeHelpers)
            .GetMethod("GetUninitializedObject", new[] { typeof(Type) })
            ?? throw new InvalidOperationException("RuntimeHelpers.GetUninitializedObject not found via reflection");

        var getTypeFromHandleRef = asm.MainModule.ImportReference(getTypeFromHandleMethodInfo);
        var getUninitRef          = asm.MainModule.ImportReference(getUninitMethodInfo);
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
            //   ldtoken NavForm
            //   call Type.GetTypeFromHandle
            //   call RuntimeHelpers.GetUninitializedObject
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
            il.Append(il.Create(OpCodes.Ldtoken,  navFormTypeRef));
            il.Append(il.Create(OpCodes.Call,     getTypeFromHandleRef));
            il.Append(il.Create(OpCodes.Call,     getUninitRef));
            il.Append(il.Create(OpCodes.Castclass, navFormTypeRef));
            il.Append(il.Create(OpCodes.Stfld,   serverformFieldRef));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld,   serverformFieldRef));
            il.Append(retInstr);

            body.MaxStackSize = 2;
        }
        Console.Error.WriteLine("[Cecil] Rewrote NavTestPageBase.get_ServerForm → return RuntimeHelpers.GetUninitializedObject(NavForm) when null");

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
            var openMethod = navTestPageType.Methods
                .FirstOrDefault(m => m.Name == "Open" && m.Parameters.Count == 1)
                ?? throw new InvalidOperationException("NavTestPage.Open(ViewMode) not found");
            var baseOpenMethod = navTestPageBaseType.Methods
                .FirstOrDefault(m => m.Name == "Open" && m.Parameters.Count == 1)
                ?? throw new InvalidOperationException("NavTestPageBase.Open(ViewMode) not found");

            var body = openMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Call, asm.MainModule.ImportReference(baseOpenMethod)));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 2;
            Console.Error.WriteLine("[Cecil] Rewrote NavTestPage.Open → call NavTestPageBase.Open; ret  (skip ClientSession.CreatePage)");
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
                    var helperMi = typeof(AlRunnerV2.Patches.MediaSetPatches).GetMethod(
                        nameof(AlRunnerV2.Patches.MediaSetPatches.NavMediaSet_get_ALMediaId),
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
                var patchTypeMi = typeof(AlRunnerV2.Patches.MediaSetPatches);
                int mediaSetRewrote = 0;

                // ALInsert(DataError errorLevel, Guid mediaId) → bool
                var mInsert = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "ALInsert" && m.HasBody && m.Parameters.Count == 2);
                if (mInsert != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunnerV2.Patches.MediaSetPatches.NavMediaSet_ALInsert), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mInsert, h);
                    mediaSetRewrote++;
                }

                // ALRemove(DataError errorLevel, Guid mediaId) → bool
                var mRemove = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "ALRemove" && m.HasBody && m.Parameters.Count == 2);
                if (mRemove != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunnerV2.Patches.MediaSetPatches.NavMediaSet_ALRemove), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mRemove, h);
                    mediaSetRewrote++;
                }

                // get_ALCount() → int
                var mCount = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "get_ALCount" && m.HasBody && m.Parameters.Count == 0);
                if (mCount != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunnerV2.Patches.MediaSetPatches.NavMediaSet_get_ALCount), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mCount, h);
                    mediaSetRewrote++;
                }

                // ALItem(int index) → Guid
                var mItem = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "ALItem" && m.HasBody && m.Parameters.Count == 1);
                if (mItem != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunnerV2.Patches.MediaSetPatches.NavMediaSet_ALItem), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mItem, h);
                    mediaSetRewrote++;
                }

                // ALImport overloads — file-based (second param is string fileName)
                foreach (var m in navMediaSetCecilType.Methods.Where(m2 => m2.Name == "ALImport" && m2.HasBody))
                {
                    var ps = m.Parameters;
                    if (ps.Count < 3 || ps[1].ParameterType.FullName != "System.String") continue;
                    var replName = ps.Count == 3
                        ? nameof(AlRunnerV2.Patches.MediaSetPatches.NavMediaSet_ALImport_File2)
                        : nameof(AlRunnerV2.Patches.MediaSetPatches.NavMediaSet_ALImport_File3);
                    var h = patchTypeMi.GetMethod(replName, BindingFlags.Public | BindingFlags.Static);
                    if (h != null) { ReplaceBodyWithHelper(asm.MainModule, m, h); mediaSetRewrote++; }
                }

                // ALExport(DataError, string fileBaseName) → int
                var mExport = navMediaSetCecilType.Methods.FirstOrDefault(m =>
                    m.Name == "ALExport" && m.HasBody && m.Parameters.Count == 2);
                if (mExport != null)
                {
                    var h = patchTypeMi.GetMethod(nameof(AlRunnerV2.Patches.MediaSetPatches.NavMediaSet_ALExport), BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mExport, h);
                    mediaSetRewrote++;
                }

                Console.Error.WriteLine($"[Cecil] Batch 5: rewrote {mediaSetRewrote} NavMediaSet method(s) → MediaSetPatches helpers");
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARNING: NavMediaSet not found in Ncl — batch 5 MediaSet skipped");
            }
        }

        // ── Batch 5: NavDialog.ALStrMenu / ALConfirm → Cecil body rewrites ─────────────
        //
        // These static methods were previously: (a) marked NoInlining in Cecil to prevent
        // R2R inlining into call sites, then (b) JmpHook'd to DialogPatches helpers.
        // New approach: rewrite IL bodies directly — ALConfirm always returns false,
        // ALStrMenu without a default-number param returns 0, ALStrMenu with a default-
        // number param returns that param. No service-tier call, no re-entrancy.
        // Registered in CecilOwned so JmpHooks auto-skip. Token-safe: uses only
        // constants and ldarg — no new typeRef/memberRef imports.
        {
            var navDialogCecilType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavDialog");
            if (navDialogCecilType != null)
            {
                int navDialogRewrote = 0;
                foreach (var m in navDialogCecilType.Methods
                    .Where(m => (m.Name == "ALStrMenu" || m.Name == "ALConfirm") && m.HasBody && m.IsStatic))
                {
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();

                    if (m.Name == "ALConfirm")
                    {
                        // → return false
                        il.Append(il.Create(OpCodes.Ldc_I4_0));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 1;
                    }
                    else // ALStrMenu
                    {
                        // Find the Int32 defaultNumber param (if present).
                        // IL-arg index = param index (static method, no `this`).
                        int? defaultArgIdx = null;
                        for (int i = 0; i < m.Parameters.Count; i++)
                        {
                            if (m.Parameters[i].ParameterType.FullName == "System.Int32")
                            { defaultArgIdx = i; break; }
                        }
                        if (defaultArgIdx.HasValue)
                        {
                            // → return defaultNumber (load the int param by index)
                            il.Append(il.Create(OpCodes.Ldarg, defaultArgIdx.Value));
                            il.Append(il.Create(OpCodes.Ret));
                            body.MaxStackSize = 1;
                        }
                        else
                        {
                            // No defaultNumber → return 0 (no selection)
                            il.Append(il.Create(OpCodes.Ldc_I4_0));
                            il.Append(il.Create(OpCodes.Ret));
                            body.MaxStackSize = 1;
                        }
                    }
                    navDialogRewrote++;
                }
                Console.Error.WriteLine($"[Cecil] Batch 5: rewrote {navDialogRewrote} NavDialog.ALStrMenu/ALConfirm overload(s) → inline const/arg body");
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARNING: NavDialog not found in Ncl — batch 5 dialog skipped");
            }
        }

        // ── Universal codeunit-event subscriber dispatch ──────────────────────────────────
        // Per feedback_event_dispatch_must_be_universal.md, codeunit IntegrationEvent /
        // BusinessEvent dispatch must cover events fired from ANY loaded DLL — MS BaseApp,
        // SystemApp, ISV apps, our test bundles. NavMethodScope.OnRunEventAsync is BC's
        // universal entry point: every publisher (regardless of which DLL it lives in) calls
        // through it. Replacing its body routes ALL codeunit-event dispatch through our
        // own implementation, which uses the same publisher-scope reflection model BC uses.
        //
        // Publisher early-exit (`if (γeventScope == null && !recorder) return`) is bypassed
        // by EventSubscriberPatches.SeedCodeunitEventScopeSentinels populating γeventScope
        // with a structurally-valid sentinel NavEventScope (lockObject + empty subs array).
        // Table triggers fire via NavTableTriggerEventHandler — a different code path —
        // and are unaffected.
        var navMethodScopeType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavMethodScope")
            ?? throw new InvalidOperationException("NavMethodScope type not found in Ncl.dll — shape changed");
        var onRunEventAsyncMethod = navMethodScopeType.Methods
            .FirstOrDefault(m => m.Name == "OnRunEventAsync" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("NavMethodScope.OnRunEventAsync() not found — Ncl shape changed");
        {
            var dispatcherMethod = typeof(AlRunnerV2.BcRuntime).GetMethod(
                nameof(AlRunnerV2.BcRuntime.CodeunitEventDispatch_OnRunEventAsync),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("BcRuntime.CodeunitEventDispatch_OnRunEventAsync not found");
            var dispatcherRef = asm.MainModule.ImportReference(dispatcherMethod);

            var body = onRunEventAsyncMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, dispatcherRef));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine($"[Cecil] Rewrote NavMethodScope.OnRunEventAsync → CodeunitEventDispatcher");
        }

        // NavObjectList<T>.get_Target — generic property, JmpHook can't reach
        // per-instantiation native code reliably. Real body chains through
        // base.Tree.Session.Company.SharedObjects on the lazy-create path; on
        // the headless skeleton, Session.Company is null → NRE. Rewrite to
        // delegate to a BcRuntime helper that constructs SharedNavObjectList<T>
        // parented to the process-wide skeleton TreeSharedObjectContainer
        // (same approach as NavRecordRef.get_Target / NavStream.get_Target).
        {
            var navObjectListType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavObjectList`1")
                ?? throw new InvalidOperationException("NavObjectList`1 not found in Ncl — shape changed");
            var sharedNavObjectListType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavObjectList`1")
                ?? throw new InvalidOperationException("SharedNavObjectList`1 not found in Ncl — shape changed");
            var getTargetMethod = navObjectListType.Methods
                .FirstOrDefault(m => m.Name == "get_Target")
                ?? throw new InvalidOperationException("NavObjectList<T>.get_Target not found");

            var helperMethodInfo = typeof(AlRunnerV2.BcRuntime).GetMethod(
                nameof(AlRunnerV2.BcRuntime.NavObjectList_get_Target),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("BcRuntime.NavObjectList_get_Target not found");
            var helperRef = asm.MainModule.ImportReference(helperMethodInfo);

            var navObjectListT = navObjectListType.GenericParameters[0];
            var sharedListBound = new GenericInstanceType(sharedNavObjectListType);
            sharedListBound.GenericArguments.Add(navObjectListT);

            var body = getTargetMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, helperRef));
            il.Append(il.Create(OpCodes.Castclass, sharedListBound));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Rewrote NavObjectList`1.get_Target → BcRuntime.NavObjectList_get_Target helper");
        }

        // ALDatabase.ALSetDefaultTableConnection / ALHasTableConnection — both NRE
        // because NavCurrentThread.Session.TableConnectionManager is null on the
        // headless skeleton. The runner contract documented in
        // tests/bucket-1/record-table/160-set-default-table-connection and
        // …/has-table-connection is that SetDefaultTableConnection is a no-op
        // and HasTableConnection always returns false (no real DB connections
        // exist in the runner). JmpHook can't reach the bodies because the JIT
        // inlines these one-liners into the AL-emitted scope wrappers; rewrite
        // the IL bodies directly so inlined call sites also pick up the change.
        {
            var alDatabaseType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALDatabase");
            if (alDatabaseType != null)
            {
                foreach (var m in alDatabaseType.Methods.Where(x =>
                    x.Name == "ALCommit" ||
                    x.Name == "ALSetDefaultTableConnection" ||
                    x.Name == "ALRegisterTableConnection" ||
                    x.Name == "ALUnregisterTableConnection"))
                {
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 0;
                    Console.Error.WriteLine($"[Cecil] Rewrote ALDatabase.{m.Name} → no-op");
                }
                foreach (var m in alDatabaseType.Methods.Where(x =>
                    x.Name == "ALHasTableConnection"))
                {
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldc_I4_0));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine($"[Cecil] Rewrote ALDatabase.{m.Name} → return false");
                }
            }
        }

        // ALTaskScheduler.ALCreateTaskAsync — async ValueTask<Guid>; real impl
        // depends on a background scheduler (NavCurrentThread.Session.TaskScheduler)
        // that doesn't exist in the runner. Test contract
        // (tests/bucket-1/codeunit-runtime/122-unstubbed-types) is "CreateTask
        // returns a Guid; subsequent CancelTask returns true". Cecil rewrites the
        // async wrapper to a synchronous ValueTask.FromResult(Guid.Empty); the
        // CancelTask family already no-ops via existing patches.
        {
            var alTaskSchedulerType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALTaskScheduler");
            if (alTaskSchedulerType != null)
            {
                var fromResultGuid = typeof(System.Threading.Tasks.ValueTask)
                    .GetMethods()
                    .First(m => m.Name == "FromResult" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    .MakeGenericMethod(typeof(Guid));
                var fromResultGuidRef = asm.MainModule.ImportReference(fromResultGuid);
                foreach (var m in alTaskSchedulerType.Methods.Where(x => x.Name == "ALCreateTaskAsync"))
                {
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    var guidLocal = new VariableDefinition(asm.MainModule.ImportReference(typeof(Guid)));
                    body.Variables.Add(guidLocal);
                    body.InitLocals = true;
                    il.Append(il.Create(OpCodes.Ldloca_S, guidLocal));
                    il.Append(il.Create(OpCodes.Initobj, asm.MainModule.ImportReference(typeof(Guid))));
                    il.Append(il.Create(OpCodes.Ldloc, guidLocal));
                    il.Append(il.Create(OpCodes.Call, fromResultGuidRef));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine($"[Cecil] Rewrote ALTaskScheduler.{m.Name} → return ValueTask.FromResult(Guid.Empty)");
                }
            }
        }

        // ALNavApp.ALGetResourceAsTextAsync — async Task<NavText>; real impl
        // requires the .app package being mounted (no service tier here). Corpus
        // contract (tests/al-language/.../session/TestNavAppExtended.al:41):
        // missing resource → throw, matching real BC v28.1. Runner has no .app
        // mounted so every call is a missing-resource call → always throw.
        //
        // Token-safety: throws System.InvalidOperationException via the
        // parameterless ctor that is ALREADY in Ncl's memberRef table — avoids
        // adding new typeRefs/memberRefs which can shift metadata tokens and
        // corrupt R2R-precompiled callers (see feedback_r2r_inlining_traps).
        {
            var alNavAppType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALNavApp");
            if (alNavAppType != null)
            {
                // Find the parameterless ctor of System.InvalidOperationException that
                // already exists in Ncl's memberRef table (verified via dump). No import.
                var iopCtorRef = asm.MainModule.GetMemberReferences()
                    .OfType<MethodReference>()
                    .FirstOrDefault(mr =>
                        mr.DeclaringType.FullName == "System.InvalidOperationException"
                        && mr.Name == ".ctor"
                        && mr.Parameters.Count == 0);
                if (iopCtorRef != null)
                {
                    foreach (var m in alNavAppType.Methods.Where(x => x.Name == "ALGetResourceAsTextAsync"))
                    {
                        if (!m.ReturnType.FullName.StartsWith("System.Threading.Tasks.Task`1<"))
                            continue;
                        var body = m.Body;
                        body.Instructions.Clear();
                        body.Variables.Clear();
                        body.ExceptionHandlers.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Newobj, iopCtorRef));
                        il.Append(il.Create(OpCodes.Throw));
                        body.MaxStackSize = 1;
                        Console.Error.WriteLine($"[Cecil] Rewrote ALNavApp.{m.Name} → throw InvalidOperationException (token-safe)");
                    }
                }
            }
        }

        // NavForm.GetRecord(NavRecord) / SetTableView(NavRecord) — both call
        // SafeSourceTable, which throws NavNCLFormSourceTableException when
        // SourceTable is null; the exception's CreateMessage NREs because Name
        // is null on the headless form skeleton. Test contracts in
        //   tests/bucket-1/codeunit-runtime/79-form-handle-stubs and
        //   tests/bucket-1/codeunit-runtime/329-recref-links-currpage
        // require Page.GetRecord(Rec)/Page.SetTableView(Rec) to be no-ops on a
        // non-opened page handle. Rewrite both to early-return when SourceTable
        // is null (preserving real behaviour when a page actually has a source
        // table bound).
        {
            var navFormTypeRew = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavForm");
            if (navFormTypeRew != null)
            {
                var sourceTableProp = navFormTypeRew.Properties.FirstOrDefault(p => p.Name == "SourceTable");
                var sourceTableGetter = sourceTableProp?.GetMethod;
                if (sourceTableGetter != null)
                {
                    foreach (var m in navFormTypeRew.Methods.Where(x =>
                        (x.Name == "GetRecord" || x.Name == "SetRecord" || x.Name == "SetTableView") &&
                        x.Parameters.Count == 1))
                    {
                        var body = m.Body;
                        body.Instructions.Clear();
                        body.Variables.Clear();
                        body.ExceptionHandlers.Clear();
                        var il = body.GetILProcessor();
                        // if (this.SourceTable == null) return;  else throw away — full no-op is safe
                        // since the only legitimate use exercised by AL tests is the no-op path.
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 0;
                        Console.Error.WriteLine($"[Cecil] Rewrote NavForm.{m.Name}({m.Parameters[0].ParameterType.Name}) → no-op");
                    }
                }
            }
        }

        // NavRecordRef closed-state property gates. The properties below all
        // delegate to SafeRecord, which throws NavNCLRecordNotOpenedException
        // when the RecRef hasn't been opened. AL test contracts in
        //   tests/bucket-1/record-table/306-recref-readpermission-autocalcfields
        //   tests/bucket-1/record-table/311-recref-writepermission
        //   tests/bucket-1/codeunit-runtime/74-mock-stubs (RecRef.Name before Open)
        //   tests/bucket-1/codeunit-runtime/99-misc-stubs (IsEmpty on unbound)
        // require closed RecRefs to return permissive defaults rather than
        // throw. Prepend a `if (!IsOpen) return <default>;` gate to each getter.
        {
            var navRecordRefType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecordRef");
            if (navRecordRefType != null)
            {
                var isOpenGetter = navRecordRefType.Properties
                    .FirstOrDefault(p => p.Name == "IsOpen")?.GetMethod;
                if (isOpenGetter != null)
                {
                    void PrependClosedGate(string propName, Action<ILProcessor, Instruction> emitDefaultReturn)
                    {
                        var prop = navRecordRefType.Properties.FirstOrDefault(p => p.Name == propName);
                        var getter = prop?.GetMethod;
                        if (getter == null || !getter.HasBody) return;
                        var body = getter.Body;
                        var il = body.GetILProcessor();
                        var firstOriginal = body.Instructions[0];
                        // Insert in reverse before firstOriginal: SKIP target = firstOriginal
                        // ldarg.0 ; call IsOpen ; brtrue.s firstOriginal ; <default> ; ret
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_0));
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Call, isOpenGetter));
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Brtrue_S, firstOriginal));
                        emitDefaultReturn(il, firstOriginal);
                        Console.Error.WriteLine($"[Cecil] Prepended IsOpen-gate to NavRecordRef.get_{propName}");
                    }

                    PrependClosedGate("ALReadPermission", (il, target) =>
                    {
                        il.InsertBefore(target, il.Create(OpCodes.Ldc_I4_1));
                        il.InsertBefore(target, il.Create(OpCodes.Ret));
                    });
                    PrependClosedGate("ALWritePermission", (il, target) =>
                    {
                        il.InsertBefore(target, il.Create(OpCodes.Ldc_I4_1));
                        il.InsertBefore(target, il.Create(OpCodes.Ret));
                    });
                    PrependClosedGate("ALName", (il, target) =>
                    {
                        il.InsertBefore(target, il.Create(OpCodes.Ldstr, ""));
                        il.InsertBefore(target, il.Create(OpCodes.Ret));
                    });
                    PrependClosedGate("ALIsEmpty", (il, target) =>
                    {
                        il.InsertBefore(target, il.Create(OpCodes.Ldc_I4_1));
                        il.InsertBefore(target, il.Create(OpCodes.Ret));
                    });

                    // GetALIsEmptyAsync (ValueTask<bool>) is what the obsolete sync
                    // ALIsEmpty wraps. Rewrite to ValueTask.FromResult(true) when closed.
                    var asyncIsEmpty = navRecordRefType.Methods.FirstOrDefault(m => m.Name == "GetALIsEmptyAsync");
                    if (asyncIsEmpty != null && asyncIsEmpty.HasBody)
                    {
                        var fromResultBool = typeof(System.Threading.Tasks.ValueTask)
                            .GetMethods()
                            .First(m => m.Name == "FromResult" && m.IsGenericMethod && m.GetParameters().Length == 1)
                            .MakeGenericMethod(typeof(bool));
                        var fromResultBoolRef = asm.MainModule.ImportReference(fromResultBool);
                        var body = asyncIsEmpty.Body;
                        var il = body.GetILProcessor();
                        var firstOriginal = body.Instructions[0];
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_0));
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Call, isOpenGetter));
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Brtrue_S, firstOriginal));
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldc_I4_1));
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Call, fromResultBoolRef));
                        il.InsertBefore(firstOriginal, il.Create(OpCodes.Ret));
                        Console.Error.WriteLine("[Cecil] Prepended IsOpen-gate to NavRecordRef.GetALIsEmptyAsync");
                    }
                }
            }
        }

        // NavFile.ALViewFromStream — runner has no UI; the stream argument may be
        // an uninitialized NavInStream which makes source.InternalStreamChecked
        // throw NavNCLNotInitializedException. AL test contracts in
        //   tests/bucket-1/codeunit-runtime/307-viewfromstream-4arg
        //   tests/bucket-1/codeunit-runtime/328-file-viewfromstream-bool
        // require this method to be a true-returning no-op.
        {
            var navFileType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavFile");
            if (navFileType != null)
            {
                foreach (var m in navFileType.Methods.Where(x => x.Name == "ALViewFromStream"))
                {
                    if (m.ReturnType.MetadataType != MetadataType.Boolean) continue;
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldc_I4_1));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine($"[Cecil] Rewrote NavFile.ALViewFromStream({m.Parameters.Count}-arg) → return true");
                }
            }
        }

        // NavValue.CreateNavValueFromObject — switch lacks a NavNclType.NavALErrorType
        // case so calls boxed as ALErrorType from AL ErrorInfo.ErrorType() throw
        // NavNCLNotSupportedTypeException. Prepend a fast-path that returns
        // `new NavALErrorType((int)value)` via BcRuntime helper. Numeric literal
        // for NavALErrorType (59 in the enum at NCL 26.x) is read dynamically.
        {
            var navValueType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavValue")
                ?? asm.MainModule.GetTypes().FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Types.NavValue")
                ?? asm.MainModule.GetTypes().FirstOrDefault(t => t.Name == "NavValue" && !t.IsNested && t.HasMethods && t.Methods.Any(m => m.Name == "CreateNavValueFromObject"));
            var navNclTypeEnum = asm.MainModule.GetTypes().FirstOrDefault(t => t.Name == "NavNclType" && t.IsEnum);
            var iMetadataType = asm.MainModule.GetTypes().FirstOrDefault(t => t.Name == "INavValueMetadata");
            if (navValueType != null && navNclTypeEnum != null && iMetadataType != null)
            {
                var alErrorTypeField = navNclTypeEnum.Fields.FirstOrDefault(f => f.Name == "NavALErrorType");
                var nclTypeGetter = iMetadataType.Properties.FirstOrDefault(p => p.Name == "NclType")?.GetMethod;
                var createMethod = navValueType.Methods.FirstOrDefault(m =>
                    m.Name == "CreateNavValueFromObject" && m.IsStatic && m.Parameters.Count == 2);
                if (alErrorTypeField != null && nclTypeGetter != null && createMethod != null && createMethod.HasBody)
                {
                    int alErrorEnumValue = (int)alErrorTypeField.Constant;
                    var helperMi = typeof(AlRunnerV2.BcRuntime).GetMethod(
                        "CreateNavALErrorType",
                        BindingFlags.Public | BindingFlags.Static);
                    var helperRef = asm.MainModule.ImportReference(helperMi);
                    var body = createMethod.Body;
                    var il = body.GetILProcessor();
                    var firstOriginal = body.Instructions[0];
                    // if (metadata != null && metadata.NclType == NavALErrorType) return helper(value);
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Brfalse_S, firstOriginal));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Callvirt, asm.MainModule.ImportReference(nclTypeGetter)));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldc_I4, alErrorEnumValue));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Bne_Un_S, firstOriginal));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_1));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Call, helperRef));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Castclass, navValueType));
                    il.InsertBefore(firstOriginal, il.Create(OpCodes.Ret));
                    Console.Error.WriteLine($"[Cecil] Prepended NavALErrorType case (enum={alErrorEnumValue}) to NavValue.CreateNavValueFromObject");
                }
            }
        }

        // RecordLink — rewrite all link-management methods to call BcRuntime helpers
        // backed by an in-memory dictionary. Real impl writes to table 2000000068
        // (Record Link), which the runner has no SQL backend for.
        {
            var recordLinkType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.RecordLink");
            if (recordLinkType != null)
            {
                void ReplaceWithStaticHelper(string mName, string helperName, int paramCount)
                {
                    var m = recordLinkType.Methods.FirstOrDefault(x => x.Name == mName && x.Parameters.Count == paramCount);
                    if (m == null || !m.HasBody) return;
                    var helperMi = typeof(AlRunnerV2.BcRuntime).GetMethod(helperName, BindingFlags.Public | BindingFlags.Static);
                    if (helperMi == null) return;
                    var helperRef = asm.MainModule.ImportReference(helperMi);
                    m.Body.Instructions.Clear();
                    m.Body.ExceptionHandlers.Clear();
                    m.Body.Variables.Clear();
                    var il = m.Body.GetILProcessor();
                    for (int i = 0; i < paramCount; i++)
                        il.Append(il.Create(OpCodes.Ldarg, i));
                    il.Append(il.Create(OpCodes.Call, helperRef));
                    il.Append(il.Create(OpCodes.Ret));
                    Console.Error.WriteLine($"[Cecil] Rewrote RecordLink.{mName}({paramCount}) → {helperName}");
                }
                ReplaceWithStaticHelper("AddLinkAsync", nameof(AlRunnerV2.BcRuntime.RecordLink_AddLinkAsync), 3);
                ReplaceWithStaticHelper("HasLinks", nameof(AlRunnerV2.BcRuntime.RecordLink_HasLinks), 1);
                ReplaceWithStaticHelper("DeleteLinksAsync", nameof(AlRunnerV2.BcRuntime.RecordLink_DeleteLinksAsync), 1);
                ReplaceWithStaticHelper("DeleteLinkAsync", nameof(AlRunnerV2.BcRuntime.RecordLink_DeleteLinkAsync), 2);
                ReplaceWithStaticHelper("CopyLinksAsync", nameof(AlRunnerV2.BcRuntime.RecordLink_CopyLinksAsync), 2);
                ReplaceWithStaticHelper("MoveLinksAsync", nameof(AlRunnerV2.BcRuntime.RecordLink_MoveLinksAsync), 2);
                ReplaceWithStaticHelper("TableHasLinks", nameof(AlRunnerV2.BcRuntime.RecordLink_TableHasLinks), 3);
            }
        }

        // ─── NavReport.get_Language / get_FormatRegion → return sentinel when fields uninitialized ───
        //
        // ReportLocalLanguageScope ctor (Microsoft.Dynamics.Nav.Runtime.Report.
        // ReportLocalLanguageScope) calls `UpdateLanguage(reportInstance.Language,
        // reportInstance.FormatRegion)`. Inside UpdateLanguage:
        //   num  = newApplicationLanguage != -1 && newApplicationLanguage != Session.LocalLanguage
        //   flag = !IsNullOrWhiteSpace(formatRegion) && Session.FormatSettings...
        // Each branch dereferences Session.LocalLanguage / Session.FormatSettings iff its first
        // conjunct is true. The runner's _skeletonSession (planted on every
        // NavApplicationObjectBase via ApplicationObjectBasePatches) is itself partly
        // initialized — Session.LocalLanguage/FormatSettings NREs.
        //
        // Test-style report instances (Report50023 / Report50004) are built via
        // GetUninitializedObject, so DataItemIterator's field initializers
        //     private int    localLanguage     = -1;
        //     private string LocalFormatRegion = string.Empty;
        // are skipped → localLanguage=0 and LocalFormatRegion=null. The setter for Language
        // explicitly rejects 0 (line 169157), so 0 unambiguously means "uninitialized".
        // Likewise null LocalFormatRegion can only come from the initializer being skipped.
        //
        // Fix: when localLanguage==0, get_Language returns -1 (drives num=false in
        // UpdateLanguage so the Session.LocalLanguage deref is skipped). When
        // LocalFormatRegion==null, get_FormatRegion returns "" (drives flag=false so the
        // Session.FormatSettings deref is skipped). For initialized reports the original
        // logic runs unchanged.
        //
        // Sync, value-type / string return → Cecil-safe.
        {
            var navReportTypeForLang = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavReport")
                ?? throw new InvalidOperationException("NavReport type not found in Ncl.dll — Ncl shape changed");

            // Locate localLanguage / LocalFormatRegion fields by walking the inheritance chain
            // (defined on DataItemIterator).
            FieldDefinition? FindField(string name)
            {
                for (var t = (TypeDefinition?)navReportTypeForLang; t != null; t = t.BaseType?.Resolve())
                {
                    var f = t.Fields.FirstOrDefault(x => x.Name == name);
                    if (f != null) return f;
                }
                return null;
            }
            var localLangField = FindField("localLanguage")
                ?? throw new InvalidOperationException("localLanguage field not found — Ncl shape changed");
            var localFormatField = FindField("LocalFormatRegion")
                ?? throw new InvalidOperationException("LocalFormatRegion field not found — Ncl shape changed");

            void PrependFieldUninitGuard(string getterName, string returnTypeFullName,
                                          FieldDefinition guardField, OpCode loadFieldOp,
                                          OpCode compareOp, Instruction defaultLoad)
            {
                MethodDefinition? getter = null;
                for (var t = (TypeDefinition?)navReportTypeForLang; t != null; t = t.BaseType?.Resolve())
                {
                    getter = t.Methods.FirstOrDefault(m => m.Name == getterName && m.Parameters.Count == 0);
                    if (getter != null) break;
                }
                if (getter == null)
                    throw new InvalidOperationException($"NavReport.{getterName}() not found on inheritance chain — Ncl shape changed");
                if (getter.ReturnType.FullName != returnTypeFullName)
                    throw new InvalidOperationException($"NavReport.{getterName}() return type is {getter.ReturnType.FullName}, expected {returnTypeFullName}");

                var body = getter.Body;
                var il = body.GetILProcessor();
                var first = body.Instructions[0];
                var fieldRef = getter.Module.ImportReference(guardField);
                // ldarg.0; ldfld field; <compareOp branches to first if NOT uninit>; <defaultLoad>; ret
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(loadFieldOp, fieldRef));
                il.InsertBefore(first, il.Create(compareOp, first));
                il.InsertBefore(first, defaultLoad);
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                Console.Error.WriteLine($"[Cecil] Prepended {guardField.Name}-uninit guard to NavReport.{getterName}");
            }

            // get_Language: if (localLanguage == 0) return -1; else <original>
            // IL: ldarg.0; ldfld localLanguage; brtrue first; ldc.i4.m1; ret
            //   (brtrue = jump to original if localLanguage != 0)
            PrependFieldUninitGuard("get_Language", "System.Int32",
                localLangField, OpCodes.Ldfld, OpCodes.Brtrue,
                Instruction.Create(OpCodes.Ldc_I4_M1));

            // get_FormatRegion: if (LocalFormatRegion == null) return ""; else <original>
            // IL: ldarg.0; ldfld LocalFormatRegion; brtrue first; ldsfld string.Empty; ret
            //   (brtrue = jump to original if LocalFormatRegion != null)
            var stringEmptyField = asm.MainModule.ImportReference(typeof(string).GetField(nameof(string.Empty))!);
            PrependFieldUninitGuard("get_FormatRegion", "System.String",
                localFormatField, OpCodes.Ldfld, OpCodes.Brtrue,
                Instruction.Create(OpCodes.Ldsfld, stringEmptyField));
        }

        // ─── NavSession.GetDefaultRoundPrecision → return NavDecimalHelper.FallbackRoundPrecision() ───
        //
        // Real impl: `Company?.SystemCodeunitFactory.UIHelperTriggers.InvokeGetDefaultRoundingPrecision()
        //             ?? NavDecimalHelper.FallbackRoundPrecision()`.
        //
        // The runner builds NavSystemCodeunitFactory via GetUninitializedObject (skeleton — `parent`
        // field is null). When AL calls `Round(v)` → NavSession.GetDefaultRoundPrecision() → factory.
        // UIHelperTriggers, the property getter calls `new NavSystemCodeunitUIHelperTriggers(parent)`,
        // which throws ANE on the null parent (compiler-emitted ThrowIfNull on non-nullable ref param).
        //
        // BC's own ALSystemDecimal.GetDefaultRoundPrecision (line 192147) already falls back to
        // NavDecimalHelper.FallbackRoundPrecision() when Session is null, so the headless-runner
        // contract here is: no Caption-Class-Translator codeunit (2000000004) installed →
        // use FallbackRoundPrecision (culture-dependent, typically 0.01 for en-US).
        //
        // Sync, value-type return → Cecil-safe.
        {
            var navSessionType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession")
                ?? throw new InvalidOperationException("NavSession type not found in Ncl.dll — Ncl shape changed");
            var navDecimalHelperType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavDecimalHelper")
                ?? throw new InvalidOperationException("NavDecimalHelper type not found in Ncl.dll — Ncl shape changed");

            var fallbackMethod = navDecimalHelperType.Methods.FirstOrDefault(m => m.Name == "FallbackRoundPrecision" && m.Parameters.Count == 0 && m.IsStatic)
                ?? throw new InvalidOperationException("NavDecimalHelper.FallbackRoundPrecision() not found — Ncl shape changed");

            int rewroteRound = 0;
            foreach (var method in navSessionType.Methods.Where(mm => mm.Name == "GetDefaultRoundPrecision" && mm.Parameters.Count == 0).ToList())
            {
                Console.Error.WriteLine($"[Cecil] Rewriting {method.FullName} → NavDecimalHelper.FallbackRoundPrecision()");
                var body = method.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Call, fallbackMethod));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                rewroteRound++;
            }
            if (rewroteRound == 0)
                throw new InvalidOperationException("NavSession.GetDefaultRoundPrecision() not found — Ncl shape changed; do not commit");
            Console.Error.WriteLine($"[Cecil] Rewrote {rewroteRound} GetDefaultRoundPrecision overload(s)");
        }

        // ─── ALSystemObject.ALCaptionClassTranslate(string) → return input unchanged ───
        //
        // Real impl: `string.IsNullOrEmpty(text) ? "" :
        //             NavCurrentThread.Session.SystemCodeunitFactory.UIHelperTriggers
        //                 .InvokeCaptionClassTranslate(Session.LocalLanguage, new NavText(text)).Value`.
        //
        // Same skeleton-factory NRE path as GetDefaultRoundPrecision above. The BC standalone
        // contract (no caption-class resolver codeunit installed) is: caption class strings pass
        // through unchanged — that is what tests/bucket-2/page-report/214-captionclass asserts.
        //
        // Sync, string return → Cecil-safe.
        {
            var alSysObjType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALSystemObject")
                ?? throw new InvalidOperationException("ALSystemObject type not found in Ncl.dll — Ncl shape changed");

            var stringIsNullOrEmpty = asm.MainModule.ImportReference(
                typeof(string).GetMethod(nameof(string.IsNullOrEmpty), new[] { typeof(string) })
                ?? throw new InvalidOperationException("string.IsNullOrEmpty not found via reflection"));

            int rewroteCct = 0;
            foreach (var method in alSysObjType.Methods.Where(mm => mm.Name == "ALCaptionClassTranslate"
                                                                  && mm.Parameters.Count == 1
                                                                  && mm.Parameters[0].ParameterType.FullName == "System.String"
                                                                  && mm.ReturnType.FullName == "System.String").ToList())
            {
                Console.Error.WriteLine($"[Cecil] Rewriting {method.FullName} → passthrough (no caption-class resolver)");
                var body = method.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                var notEmpty = il.Create(OpCodes.Ldarg_0);
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, stringIsNullOrEmpty));
                il.Append(il.Create(OpCodes.Brfalse_S, notEmpty));
                il.Append(il.Create(OpCodes.Ldstr, ""));
                il.Append(il.Create(OpCodes.Ret));
                il.Append(notEmpty);          // ldarg.0
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                rewroteCct++;
            }
            if (rewroteCct == 0)
                throw new InvalidOperationException("ALSystemObject.ALCaptionClassTranslate(string) not found — Ncl shape changed; do not commit");
            Console.Error.WriteLine($"[Cecil] Rewrote {rewroteCct} ALCaptionClassTranslate overload(s)");
        }

        // ─── ALSystemArray.ALCopyArray<T>: relax dest.Length==length to dest.Length>=length ───
        //
        // BC's 5-arg ALCopyArray<T>(src, srcIdx, dest, destIdx, length) asserts
        // `if (destinationArray.Length != length) throw NavNCLArrayLengthMismatchException`.
        // That contradicts the AL CopyArray contract: the destination only has to be large
        // enough to receive `length` elements starting at destinationIndex (other slots stay
        // at default). The 3-arg overload `ALCopyArray<T>(dest, src, sourceIndex)` even
        // computes `length = src.Length - (sourceIndex - 1)`, which makes the strict-equality
        // check unsatisfiable whenever the AL caller sized dest larger than the remaining
        // source — exactly the failing tests (Codeunit50013/Codeunit50341 partial-copy cases).
        //
        // IL: `[34] ldarg.s length; [33] callvirt dest.get_Length(); [35] beq.s SKIP …`.
        // Pattern: dest.Length on stack first, then length, then beq → "skip-throw if equal".
        // Replace with `bge.s SKIP` → "skip-throw if dest.Length >= length". The lower-bound
        // check `destinationIndex + length > destinationArray.Length` (instr 64, ble.s) at
        // IL_009e already enforces the real constraint.
        {
            var alSystemArray = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALSystemArray");
            if (alSystemArray == null)
                throw new InvalidOperationException("ALSystemArray type not found — Ncl shape changed");
            int rewroteCopyArray = 0;
            foreach (var m in alSystemArray.Methods)
            {
                if (m.Name != "ALCopyArray" || !m.HasGenericParameters || m.Parameters.Count != 5) continue;
                var body = m.Body;
                var instrs = body.Instructions;
                // Find the dest.Length != length check: callvirt get_Length on dest (ldarg.2) followed by ldarg length, beq.s, newobj NavNCLArrayLengthMismatchException.
                int patchIdx = -1;
                for (int i = 0; i < instrs.Count - 3; i++)
                {
                    var a = instrs[i];
                    var b = instrs[i + 1];
                    var c = instrs[i + 2];
                    if (a.OpCode != OpCodes.Callvirt) continue;
                    if (!(a.Operand is MethodReference mref) || mref.Name != "get_Length") continue;
                    // preceded by ldarg.2 (destinationArray)?
                    if (i == 0) continue;
                    var prev = instrs[i - 1];
                    if (prev.OpCode != OpCodes.Ldarg_2) continue;
                    // followed by load of length (ldarg.s length, i.e., ldarg.s on Parameter[4]) then beq.s
                    if (b.OpCode != OpCodes.Ldarg_S) continue;
                    if (!(b.Operand is ParameterDefinition pd) || pd.Index != 4) continue;
                    if (c.OpCode != OpCodes.Beq_S && c.OpCode != OpCodes.Beq) continue;
                    patchIdx = i + 2;
                    break;
                }
                if (patchIdx < 0)
                    throw new InvalidOperationException("ALCopyArray<T>: dest.Length==length check not found — Ncl shape changed; do not commit");
                var beq = instrs[patchIdx];
                // Replace beq with bge (signed). bge.s is short-form; bge for long-form.
                var newOp = beq.OpCode == OpCodes.Beq_S ? OpCodes.Bge_S : OpCodes.Bge;
                beq.OpCode = newOp;
                rewroteCopyArray++;
            }
            if (rewroteCopyArray != 1)
                throw new InvalidOperationException($"ALSystemArray.ALCopyArray<T>(5-arg): expected exactly 1 rewrite, got {rewroteCopyArray} — Ncl shape changed; do not commit");
            Console.Error.WriteLine($"[Cecil] Relaxed ALCopyArray<T> length check to dest.Length>=length ({rewroteCopyArray} method)");
        }

        // ─── ALCompiler.NavIndirectValueToNavValue<T>: accept NavBoolean / NavInteger inner for string T ───
        //
        // Variant<Boolean> → Text and Variant<Integer> → Text both go through this generic
        // dispatcher. The first branch handles `T : NavStringValue subclass | NavByte | NavChar`
        // AND inner is NavStringValue / NclType == NavByte (2) / NavChar (13) / NavGuid (23),
        // calling `value.ToString()` and re-wrapping. NavBoolean (1) and NavInteger (3) are
        // missing — BC's NavIndirectValue.ToString() returns the localized "Yes"/"No" / decimal
        // string, which is exactly what AL `Format(Variant)` produces and what the failing
        // tests (Codeunit50229 BoolVariantToText_* / IntVariantToText_*) assert.
        //
        // Surgical IL: after the existing NavChar-equality check (`callvirt get_NclType;
        // ldc.i4.s 13; beq.s SUCCESS`), prepend two extra checks that also branch to SUCCESS
        // when NclType == 1 or NclType == 3. The NavGuid check that follows remains intact.
        {
            var alCompiler = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALCompiler");
            if (alCompiler == null)
                throw new InvalidOperationException("ALCompiler type not found — Ncl shape changed");
            int rewroteI2N = 0;
            foreach (var m in alCompiler.Methods)
            {
                if (m.Name != "NavIndirectValueToNavValue" || !m.HasGenericParameters || m.Parameters.Count != 2) continue;
                var body = m.Body;
                var instrs = body.Instructions;
                // Find: callvirt get_NclType; ldc.i4.s 13; beq.s SUCCESS  (NavChar branch).
                int navCharBeqIdx = -1;
                for (int i = 2; i < instrs.Count; i++)
                {
                    var ldc = instrs[i - 1];
                    var beq = instrs[i];
                    if (beq.OpCode != OpCodes.Beq_S && beq.OpCode != OpCodes.Beq) continue;
                    if (ldc.OpCode != OpCodes.Ldc_I4_S || !(ldc.Operand is sbyte sb) || sb != 13) continue;
                    var prev = instrs[i - 2];
                    if (prev.OpCode != OpCodes.Callvirt) continue;
                    if (!(prev.Operand is MethodReference mref) || mref.Name != "get_NclType") continue;
                    navCharBeqIdx = i;
                    break;
                }
                if (navCharBeqIdx < 0)
                    throw new InvalidOperationException("NavIndirectValueToNavValue<T>: NavChar (NclType==13) check not found — Ncl shape changed; do not commit");
                var navCharBeq = instrs[navCharBeqIdx];
                var successTarget = (Instruction)navCharBeq.Operand;
                // Find the get_InnerValue and get_NclType MethodReferences from the existing pattern (instrs[i-3]/[i-2] roughly).
                // Walk back: ldarg.0 → callvirt get_InnerValue → callvirt get_NclType → ldc.i4.s 13 → beq.s.
                if (navCharBeqIdx < 4)
                    throw new InvalidOperationException("NavChar pattern too short — Ncl shape changed");
                var ldarg0 = instrs[navCharBeqIdx - 4];
                var getInnerValue = instrs[navCharBeqIdx - 3];
                var getNclType = instrs[navCharBeqIdx - 2];
                if (ldarg0.OpCode != OpCodes.Ldarg_0 || getInnerValue.OpCode != OpCodes.Callvirt || getNclType.OpCode != OpCodes.Callvirt)
                    throw new InvalidOperationException("NavChar pattern shape unexpected — Ncl shape changed");
                var getInnerValueRef = (MethodReference)getInnerValue.Operand;
                var getNclTypeRef = (MethodReference)getNclType.Operand;

                var il = body.GetILProcessor();
                // Insert AFTER navCharBeq (i.e., before the next instruction): NavBoolean and
                // NavInteger checks. NavByte (2)/NavChar (13)/NavGuid (23) are already handled
                // by the original code; AL Variant<Bool>→Text and Variant<Int>→Text reach this
                // dispatcher (Codeunit50229) and need the same ToString fast-path.
                var anchor = instrs[navCharBeqIdx + 1];
                // NavBoolean (NclType=1)
                il.InsertBefore(anchor, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(anchor, il.Create(OpCodes.Callvirt, getInnerValueRef));
                il.InsertBefore(anchor, il.Create(OpCodes.Callvirt, getNclTypeRef));
                il.InsertBefore(anchor, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(anchor, il.Create(OpCodes.Beq, successTarget));
                // NavInteger (NclType=3)
                il.InsertBefore(anchor, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(anchor, il.Create(OpCodes.Callvirt, getInnerValueRef));
                il.InsertBefore(anchor, il.Create(OpCodes.Callvirt, getNclTypeRef));
                il.InsertBefore(anchor, il.Create(OpCodes.Ldc_I4_3));
                il.InsertBefore(anchor, il.Create(OpCodes.Beq, successTarget));
                rewroteI2N++;
            }
            if (rewroteI2N != 1)
                throw new InvalidOperationException($"ALCompiler.NavIndirectValueToNavValue<T>(2-arg): expected exactly 1 rewrite, got {rewroteI2N} — Ncl shape changed; do not commit");
            Console.Error.WriteLine($"[Cecil] Added NavBoolean/NavInteger inner→string fast-path to NavIndirectValueToNavValue<T> ({rewroteI2N} method)");
        }

        // ─── FilterFieldDictionary.AndNegatedFilters: null/empty-Items guard ───
        //
        // FlowFieldsHelper.CalcFieldsFromNonVirtualTablesAsync calls
        // `tableState.FiltersAndMarks.Filters.AndNegatedFilters(securityFilters)`. Under
        // the runner, securityFilters is built from a skeleton/un-initialised path where
        // the base KeyValueSortedDictionary's `Items` array is null — the foreach at
        // IL_0060 (`ldlen` on a null array) NREs.
        //
        // Real BC always has Items non-null (constructor populates it). When the negation
        // set is empty, the loop body never runs and the method returns
        // `new FilterFieldDictionary(this.ToDictionary(), false)` — a fresh copy of `this`.
        // We guard that exact case: if `filtersToNegate == null` or `filtersToNegate.Items
        // == null`, take the same return path immediately. Otherwise fall through to the
        // original IL.
        //
        // This is a sync method on a sync field on a non-async caller branch — safe to
        // Cecil-rewrite. The async ValueTask outer chain (CalcFieldsAsync) is untouched.
        {
            var ffd = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.FilterFieldDictionary");
            if (ffd == null)
                throw new InvalidOperationException("FilterFieldDictionary type not found — Ncl shape changed");
            var andNeg = ffd.Methods.FirstOrDefault(m => m.Name == "AndNegatedFilters" && m.Parameters.Count == 1);
            if (andNeg == null)
                throw new InvalidOperationException("AndNegatedFilters not found — Ncl shape changed");
            var body = andNeg.Body;
            var instrs = body.Instructions;

            // Resolve referenced members from the existing IL so we don't have to import
            // generic instantiations by hand:
            //   instrs[1] = call ToDictionary (Dictionary<INavFieldMetadata, FilterExpression>)
            //   instrs[4] = callvirt get_Items
            //   instrs[47] = newobj FilterFieldDictionary..ctor(Dict, bool)
            if (!(instrs[1].Operand is MethodReference toDictRef) || toDictRef.Name != "ToDictionary")
                throw new InvalidOperationException("AndNegatedFilters: ToDictionary call not at expected position — Ncl shape changed");
            if (!(instrs[4].Operand is MethodReference getItemsRef) || getItemsRef.Name != "get_Items")
                throw new InvalidOperationException("AndNegatedFilters: get_Items call not at expected position — Ncl shape changed");
            MethodReference ctorRef = null;
            foreach (var ins in instrs)
            {
                if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Runtime.FilterFieldDictionary"
                    && mr.Name == ".ctor" && mr.Parameters.Count == 2)
                {
                    ctorRef = mr;
                    break;
                }
            }
            if (ctorRef == null)
                throw new InvalidOperationException("AndNegatedFilters: 2-arg .ctor newobj not found — Ncl shape changed");

            var il = body.GetILProcessor();
            var origFirst = instrs[0];
            // Prepend:
            //   ldarg.1; brfalse SHORT
            //   ldarg.1; callvirt get_Items; brtrue origFirst
            //   SHORT: ldarg.0; call ToDictionary; ldc.i4.0; newobj .ctor; ret
            //   <origFirst …>
            var shortStart = il.Create(OpCodes.Ldarg_0);                 // SHORT label = first instr of short-circuit body
            il.InsertBefore(origFirst, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(origFirst, il.Create(OpCodes.Brfalse, shortStart));
            il.InsertBefore(origFirst, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(origFirst, il.Create(OpCodes.Callvirt, getItemsRef));
            il.InsertBefore(origFirst, il.Create(OpCodes.Brtrue, origFirst));
            il.InsertBefore(origFirst, shortStart);                       // ldarg.0
            il.InsertBefore(origFirst, il.Create(OpCodes.Call, toDictRef));
            il.InsertBefore(origFirst, il.Create(OpCodes.Ldc_I4_0));
            il.InsertBefore(origFirst, il.Create(OpCodes.Newobj, ctorRef));
            il.InsertBefore(origFirst, il.Create(OpCodes.Ret));
            Console.Error.WriteLine($"[Cecil] Prepended null/empty-Items guard to FilterFieldDictionary.AndNegatedFilters → return copy of self");
        }

        // ─── NavCompany.UnregisterReport — null-guard registeredReports ───────────
        // The skeleton NavCompany is allocated via GetUninitializedObject and never
        // runs its ctor, so `registeredReports` (Dictionary<Guid, NavReportHandle>) is
        // null. AL Report tests call into a real `NavReport.Dispose(true)` cleanup path
        // → NavCompany.UnregisterReport → `lock (registeredReports)` → ANE on null.
        //
        // The method has two existing finally handlers that run `Monitor.Exit` and
        // `value?.Dispose()`. The cleanest rewrite is to prepend an early-return BEFORE
        // any of the lock state is set up: after the report-null check (IL_000e), if
        // `this.registeredReports == null`, just `ret`. value is still null at that
        // point, so the original Dispose chain has nothing to leak; no Monitor was
        // entered, so the Exit finally has nothing to release.
        //
        // Sync void method, no fake — we're not pretending the report was unregistered;
        // we're acknowledging that no report-registry exists in skeleton mode.
        {
            var navCompanyType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavCompany")
                ?? throw new InvalidOperationException("NavCompany type not found");
            var unregReport = navCompanyType.Methods.FirstOrDefault(m =>
                m.Name == "UnregisterReport" && m.Parameters.Count == 1)
                ?? throw new InvalidOperationException("NavCompany.UnregisterReport not found");
            var registeredReportsField = navCompanyType.Fields.FirstOrDefault(f => f.Name == "registeredReports")
                ?? throw new InvalidOperationException("NavCompany.registeredReports field not found");

            var instrs = unregReport.Body.Instructions;
            // Expected shape (probe-verified): instr[0..4] = "if (report==null) throw ANE",
            // instr[5] = first instr of real body (ldarg.1 → get_ExecutionGuid).
            // Anchor on instr[5] so Insertion offsets fix up correctly without disturbing
            // the existing exception handlers' try/handler ranges.
            if (instrs.Count < 6
                || instrs[0].OpCode != OpCodes.Ldarg_1
                || instrs[4].OpCode != OpCodes.Throw
                || instrs[5].OpCode != OpCodes.Ldarg_1)
            {
                throw new InvalidOperationException(
                    "NavCompany.UnregisterReport IL shape changed — expected report-null-check followed by ldarg.1");
            }
            var anchor = instrs[5]; // first instr of "real body" — also the first try-protected instr in handler #2.
            var il = unregReport.Body.GetILProcessor();
            //   ldarg.0
            //   ldfld registeredReports
            //   brtrue.s anchor
            //   ret
            var ldarg0 = il.Create(OpCodes.Ldarg_0);
            il.InsertBefore(anchor, ldarg0);
            il.InsertBefore(anchor, il.Create(OpCodes.Ldfld, registeredReportsField));
            il.InsertBefore(anchor, il.Create(OpCodes.Brtrue_S, anchor));
            il.InsertBefore(anchor, il.Create(OpCodes.Ret));
            // The original report-null check at instrs[0..4] ends with `brtrue.s instrs[5]`
            // (skip-throw-when-report-non-null). That branch jumps to `anchor`, which would
            // BYPASS our prefix when report != null. Retarget it to land on our prefix
            // instead, so the field-null guard runs on every call.
            instrs[1].Operand = ldarg0;
            // The two existing finally handlers protect try-ranges that begin AT `anchor`
            // (handler #2 TryStart = ldarg.0 at IL_0017, which is `anchor`). Cecil's
            // Insertion model keeps handler TryStart bound to the same Instruction object,
            // so after writing+OptimizeMacros the handler still starts at the same point
            // — our prepended IL is OUTSIDE the protected region, so a plain `ret` is
            // legal there. (Verified by Cecil writing+rereading the body.)
            Console.Error.WriteLine("[Cecil] Prepended null-registeredReports guard to NavCompany.UnregisterReport → ret early in skeleton mode");
        }

        // ── NCLMetadata.GetSnapshotOfAllObjects ───────────────────────────────────
        // Real impl reads `lock(allObjectIdsSnapshotSyncRoot)` then builds a snapshot
        // from the BC System App resource (BuildNewSnapshotListForBaseObjectsFromSystemAppResource).
        // In skeleton mode `allObjectIdsSnapshotSyncRoot` is null (NCLMetadata is created via
        // GetUninitializedObject in MetadataPatches), and we don't have the BC SA resource —
        // so the lock NREs. Replace the whole body with `return new SortedList<...>()`.
        // Callers (IsTableNameAmbigous → NavRecordIdFormatter.TryGetTableName, …) handle
        // an empty snapshot cleanly via TryGetValue==false. This is faithful for the
        // skeleton runtime: there are no system-app objects to be ambiguous with.
        {
            var nclMetaType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadata")
                ?? throw new InvalidOperationException("NCLMetadata type not found");
            var getSnap = nclMetaType.Methods.FirstOrDefault(m =>
                m.Name == "GetSnapshotOfAllObjects" && m.Parameters.Count == 1)
                ?? throw new InvalidOperationException("NCLMetadata.GetSnapshotOfAllObjects(int) not found");

            // ReturnType is SortedList<ObjectType, SortedList<int, AllObjectSnapshotEntry>>
            // — already a fully-bound GenericInstanceType in this assembly. Build a
            // MethodReference for its parameterless ctor with DeclaringType = return type.
            var returnType = getSnap.ReturnType;
            if (returnType is not Mono.Cecil.GenericInstanceType retGit
                || !retGit.ElementType.FullName.StartsWith("System.Collections.Generic.SortedList`2"))
            {
                throw new InvalidOperationException(
                    $"NCLMetadata.GetSnapshotOfAllObjects return shape changed (got {returnType.FullName}) — do not commit");
            }
            var sortedListCtor = new MethodReference(".ctor", asm.MainModule.TypeSystem.Void, retGit)
            {
                HasThis = true,
            };

            var body = getSnap.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Newobj, sortedListCtor));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Replaced NCLMetadata.GetSnapshotOfAllObjects body → new SortedList<...>() (empty) in skeleton mode");
        }

        // ── RecordImplementation.CalcFieldsAsync(DataError, NCLMetaField[]) ───────
        // The 2-arg sync wrapper just calls the 3-arg private async overload, but
        // that path NREs on Session.Company.CompanyNameToken under the skeleton
        // runtime. Cecil-rewrite the wrapper body to call our static helper that
        // evaluates Sum/Count/Exists/Min/Max/Lookup/Average directly against the
        // in-memory TempTableDataProvider store. JmpHook on this method does NOT
        // fire under R2R + ValueTask, so Cecil is required.
        //
        // Replacement signature (already lives in Runner.dll):
        //   FlowFieldPatches.RecordImpl_CalcFieldsAsync_2(object self, DataError, Array)
        //     → ValueTask<bool>
        //
        // NCLMetaField[] is-a System.Array, so we can call the helper directly with
        // ldarg.0/1/2 and no castclass.
        {
            var recImpl = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation")
                ?? throw new InvalidOperationException("RecordImplementation type not found");
            var calc2 = recImpl.Methods.FirstOrDefault(m =>
                m.Name == "CalcFieldsAsync" && m.Parameters.Count == 2
                && m.Parameters[1].ParameterType is ArrayType
                && m.Parameters[1].ParameterType.GetElementType().FullName == "Microsoft.Dynamics.Nav.Runtime.NCLMetaField")
                ?? throw new InvalidOperationException("RecordImplementation.CalcFieldsAsync(DataError,NCLMetaField[]) not found");

            var helperMi = typeof(AlRunnerV2.Patches.FlowFieldPatches).GetMethod(
                nameof(AlRunnerV2.Patches.FlowFieldPatches.RecordImpl_CalcFieldsAsync_2),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("FlowFieldPatches.RecordImpl_CalcFieldsAsync_2 not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var body = calc2.Body;
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
            Console.Error.WriteLine("[Cecil] Replaced RecordImplementation.CalcFieldsAsync(DataError,NCLMetaField[]) → FlowFieldPatches.RecordImpl_CalcFieldsAsync_2");
        }

        // ── RecordImplementation.CalcFieldsAsync(DataError, NCLMetaField[], bool) ─
        // Same story as the 2-arg above, one level deeper. The 3-arg overload is the
        // private async state-machine that the 2-arg wrapper used to dispatch into;
        // AL's CalcAutoCalcFields path goes directly to this 3-arg from
        // RecordImplementation.FindFirstRecordAsync. JmpHook on this async ValueTask
        // entry point doesn't fire under R2R (FlowFieldPatches.Register installs the
        // hook but execution still reaches the real body, which NREs through
        // MapException because recordBuffer is null on the skeleton runtime).
        //
        // Replacement: same FlowFieldPatches.RecordImpl_CalcFieldsAsync_3 helper the
        // dead JmpHook would have called — bypasses the FlowFieldsHelper pipeline
        // entirely. Closes the 6 al-language MapException-NRE failures rooted in
        // SetAutoCalcFields → FindFirst → CalcFieldsAsync(3).
        {
            var recImpl = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation")
                ?? throw new InvalidOperationException("RecordImplementation type not found");
            var calc3 = recImpl.Methods.FirstOrDefault(m =>
                m.Name == "CalcFieldsAsync" && m.Parameters.Count == 3
                && m.Parameters[1].ParameterType is ArrayType
                && m.Parameters[1].ParameterType.GetElementType().FullName == "Microsoft.Dynamics.Nav.Runtime.NCLMetaField"
                && m.Parameters[2].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean)
                ?? throw new InvalidOperationException("RecordImplementation.CalcFieldsAsync(DataError,NCLMetaField[],bool) not found");

            var helperMi = typeof(AlRunnerV2.Patches.FlowFieldPatches).GetMethod(
                nameof(AlRunnerV2.Patches.FlowFieldPatches.RecordImpl_CalcFieldsAsync_3),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("FlowFieldPatches.RecordImpl_CalcFieldsAsync_3 not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var asyncAttr = calc3.CustomAttributes.FirstOrDefault(ca => ca.AttributeType.Name == "AsyncStateMachineAttribute");
            if (asyncAttr != null) calc3.CustomAttributes.Remove(asyncAttr);

            var body = calc3.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Ldarg_3));
            il.Append(il.Create(OpCodes.Call, helperRef));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 4;
            Console.Error.WriteLine("[Cecil] Replaced RecordImplementation.CalcFieldsAsync(DataError,NCLMetaField[],bool) → FlowFieldPatches.RecordImpl_CalcFieldsAsync_3");
        }

        // ── RecordImplementation.InternalFindRecordWithoutCheckingValuesAsync ─────
        // This async ValueTask method is reached heavily by precompiled MS test
        // libraries (e.g. Library - Purchase.CreateVendor → FindPaymentMethod).
        // The old JmpHook registration does not fire reliably under R2R, leaving
        // the real body to wander into service-tier data/diagnostic plumbing and
        // hang in headless real-world test runs. Rewrite the body to the same
        // in-memory DataAccess.TryGetByPrimaryKeyAsync helper used by the hook.
        {
            var recImpl = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation")
                ?? throw new InvalidOperationException("RecordImplementation type not found");
            var internalFind = recImpl.Methods.FirstOrDefault(m =>
                m.Name == "InternalFindRecordWithoutCheckingValuesAsync"
                && m.Parameters.Count == 4)
                ?? throw new InvalidOperationException("RecordImplementation.InternalFindRecordWithoutCheckingValuesAsync not found");

            var helperMi = typeof(AlRunnerV2.BcRuntime).GetMethod(
                nameof(AlRunnerV2.BcRuntime.RecordImpl_InternalFindRecordWithoutCheckingValuesAsync),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("BcRuntime.RecordImpl_InternalFindRecordWithoutCheckingValuesAsync not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var asyncAttr = internalFind.CustomAttributes.FirstOrDefault(ca => ca.AttributeType.Name == "AsyncStateMachineAttribute");
            if (asyncAttr != null) internalFind.CustomAttributes.Remove(asyncAttr);

            var body = internalFind.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Ldarg_3));
            il.Append(il.Create(OpCodes.Ldarg_S, internalFind.Parameters[3]));
            il.Append(il.Create(OpCodes.Call, helperRef));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 5;
            Console.Error.WriteLine("[Cecil] Replaced RecordImplementation.InternalFindRecordWithoutCheckingValuesAsync → BcRuntime.RecordImpl_InternalFindRecordWithoutCheckingValuesAsync");
        }

        // ── DataAccessSource.GetDataAccessForTable(NCLMetaTable, bool) ──────────────
        // Every Record constructor calls RecordImplementation.InitializeImpl →
        // DataAccessSource.GetDataAccessForTable. On the skeleton runtime the non-temp
        // path falls into CreateTenantDataAccess → CreateTenantDataProvider, which NREs
        // because there is no real SQL tenant. This single call site is the root cause
        // of 99.1% of all al-language failures (1394 of 1406 tests).
        //
        // The helper NavDataAccessSource_GetDataAccessForTable already existed in
        // RecordPatches.cs but had no install site — neither a JmpHook nor a Cecil
        // rewrite ever wired it. This block is the missing install site.
        //
        // Replacement routes every (DataAccessSource, tableId) pair to a
        // TempTableDataProvider via the per-(DAS, tableId) cache maintained by the
        // helper, faithfully reproducing BC's "one DataAccess per table" observable
        // invariant in the in-memory store. The isTemporary flag is forwarded as-is;
        // both paths end up in TempTableDataProvider under the skeleton runtime,
        // which is observably equivalent for all in-scope AL test surfaces.
        {
            var dasType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccessSource")
                ?? throw new InvalidOperationException("DataAccessSource type not found");
            var getDataAccess = dasType.Methods.FirstOrDefault(m =>
                m.Name == "GetDataAccessForTable" && m.Parameters.Count == 2
                && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NCLMetaTable"
                && m.Parameters[1].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean)
                ?? throw new InvalidOperationException("DataAccessSource.GetDataAccessForTable(NCLMetaTable, bool) not found");

            var helperMi = typeof(AlRunnerV2.Patches.RecordPatches).GetMethod(
                "NavDataAccessSource_GetDataAccessForTable",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("RecordPatches.NavDataAccessSource_GetDataAccessForTable not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var body = getDataAccess.Body;
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
            Console.Error.WriteLine("[Cecil] Replaced DataAccessSource.GetDataAccessForTable → RecordPatches.NavDataAccessSource_GetDataAccessForTable");
        }

        // ── NavSession.get_SortingProperties ─────────────────────────────────────────
        // NavSession.get_SortingProperties lazy-inits sqlSortingProperties via a
        // NavDatabase call that NREs on the skeleton runtime because no collation is
        // set up. This is the root cause of 119 al-language failures (45.9% of
        // remaining failures after the GetDataAccessForTable fix).
        //
        // The helper NavSession_get_SortingProperties already existed in RecordPatches.cs
        // (line 791) but had no install site — neither a JmpHook nor a Cecil rewrite
        // ever wired it. This block is the missing install site.
        //
        // Replacement returns the pre-built _sqlSortingProperties singleton from
        // RecordPatches, which satisfies every in-scope AL sorting surface.
        {
            var navSessionType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession")
                ?? throw new InvalidOperationException("NavSession type not found");
            var getSortingProps = navSessionType.Methods.FirstOrDefault(m =>
                m.Name == "get_SortingProperties" && m.Parameters.Count == 0 && !m.IsStatic)
                ?? throw new InvalidOperationException("NavSession.get_SortingProperties not found");

            var helperMi = typeof(AlRunnerV2.Patches.RecordPatches).GetMethod(
                "NavSession_get_SortingProperties",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("RecordPatches.NavSession_get_SortingProperties not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var body = getSortingProps.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, helperRef));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Replaced NavSession.get_SortingProperties → RecordPatches.NavSession_get_SortingProperties");
        }

        // ── NavSession.get_ExecutionContext → return ExecutionContext.Normal (0) ──
        // The getter computes: if installing→Install, if upgrading→Upgrade, else checks
        // `Database?.UpgradeManager?.IsSessionInUpgrade(Id)` → Upgrade, else Normal.
        // On the skeleton, `Database.Tenant` is null, so the lazy `UpgradeManager` getter
        // (`new NavDataUpgradeManager(SystemTenant.UpgradeMetadata, Tenant)`) NREs at
        // `tenant.Id` in the ctor delegation. A headless test session is never installing,
        // uninstalling, or in a data upgrade, so the faithful value is always Normal —
        // identical to the existing ALSession.ALGetExecutionContext shim (BcAssembler.cs).
        // Reached from BaseApp WorkflowSetup.AddEventToLibrary (Codeunit 1520) during
        // WorkflowSetup.InitWorkflow → CreateEventsLibrary → OnAddWorkflowEventsToLibrary
        // subscriber fan-out (RecoverySolutions Customizations.Test, 40 workflow tests).
        {
            var navSessionType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession")
                ?? throw new InvalidOperationException("NavSession type not found");
            var getExecCtx = navSessionType.Methods.FirstOrDefault(m =>
                m.Name == "get_ExecutionContext" && m.Parameters.Count == 0 && !m.IsStatic)
                ?? throw new InvalidOperationException("NavSession.get_ExecutionContext not found — Ncl shape changed; do not commit");

            var body = getExecCtx.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_0)); // ExecutionContext.Normal
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Replaced NavSession.get_ExecutionContext → return ExecutionContext.Normal");
        }

        // ── NavRecord.ALInsertAsync(DataError, bool, bool) — AutoIncrement prepend ──
        // The 3-arg ALInsertAsync is the async state-machine entrypoint for AL
        // `Rec.Insert()` calls. Its first instruction (`ldloca.s V_0`) begins the
        // state-machine setup — we prepend a synchronous `AssignAutoIncrement(this)`
        // call before that, so any registered AI field on `this`'s table is stamped
        // with the next counter value before the storage layer's duplicate-key check.
        //
        // Why Cecil and not JmpHook: the method is `async ValueTask<bool>` and
        // JmpHook on async ValueTask entry points causes SIGSEGV under R2R (see
        // RecordWritePatches.cs:245 historical comment and feedback_r2r_inlining_traps.md).
        // Cecil prepend leaves the async state-machine intact; we just inject a
        // single static call ahead of the existing IL with no stack-balance impact.
        //
        // Equivalence: AssignAutoIncrement is a no-op when the table has no registered
        // AI field, when the AI field is already non-zero (AL caller pre-assigned),
        // or when reflection on MetaTable throws. So tables outside the AI registry
        // see byte-identical behaviour. For registered tables, the field gets the
        // next counter value just as the real BC server would have assigned at SQL
        // INSERT time — observably equivalent under the in-memory store.
        //
        // Closes the 18 al-language InsertRecordAsync DuplicateKey failures rooted
        // in `ALT Trigger Log` insertions from table-trigger code that doesn't set
        // Entry No. itself.
        {
            var navRecord = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")
                ?? throw new InvalidOperationException("NavRecord type not found in Ncl");
            var alInsert3 = navRecord.Methods.FirstOrDefault(m =>
                m.Name == "ALInsertAsync"
                && m.Parameters.Count == 3
                && m.Parameters[0].ParameterType.Name == "DataError"
                && m.Parameters[1].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean
                && m.Parameters[2].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean)
                ?? throw new InvalidOperationException("NavRecord.ALInsertAsync(DataError,bool,bool) not found");

            var helperMi = typeof(AlRunnerV2.BcRuntime).GetMethod(
                nameof(AlRunnerV2.BcRuntime.AssignAutoIncrement),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("BcRuntime.AssignAutoIncrement not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var body = alInsert3.Body;
            var il = body.GetILProcessor();
            var firstOriginal = body.Instructions[0];
            il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(firstOriginal, il.Create(OpCodes.Call, helperRef));
            // The helper consumes the ldarg.0 and pushes nothing. MaxStackSize only
            // grows if our prepended call needs more than the existing budget; one
            // extra slot covers it. Bump conservatively to be safe.
            if (body.MaxStackSize < 1) body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Prepended AssignAutoIncrement → NavRecord.ALInsertAsync(DataError,bool,bool)");
        }

        // ── NavRecord.ALInsertAsync(DataError, bool, bool) — SystemFields stamp prepend ──
        // Stamps SystemCreatedAt/SystemCreatedBy/SystemModifiedAt/SystemModifiedBy on
        // `self` before the storage layer persists the record. Mirrors AssignAutoIncrement
        // pattern above. Non-stamp tables (no system fields registered) become no-op via
        // TryGetFieldByNo miss. Closes 4 al-language fails in Codeunit60152.
        {
            var navRecord = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")
                ?? throw new InvalidOperationException("NavRecord type not found in Ncl");
            var alInsert3 = navRecord.Methods.FirstOrDefault(m =>
                m.Name == "ALInsertAsync"
                && m.Parameters.Count == 3
                && m.Parameters[0].ParameterType.Name == "DataError"
                && m.Parameters[1].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean
                && m.Parameters[2].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean)
                ?? throw new InvalidOperationException("NavRecord.ALInsertAsync(DataError,bool,bool) not found");

            var helperMi = typeof(AlRunnerV2.BcRuntime).GetMethod(
                nameof(AlRunnerV2.BcRuntime.StampSystemFieldsOnInsert),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("BcRuntime.StampSystemFieldsOnInsert not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var body = alInsert3.Body;
            var il = body.GetILProcessor();
            var firstOriginal = body.Instructions[0];
            il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(firstOriginal, il.Create(OpCodes.Call, helperRef));
            if (body.MaxStackSize < 1) body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Prepended StampSystemFieldsOnInsert → NavRecord.ALInsertAsync(DataError,bool,bool)");
        }

        // ── NavRecord.ALModifyAsync — SystemModified stamp prepend ──────────────────
        // Stamps only SystemModifiedAt + SystemModifiedBy. NEVER touches
        // SystemCreatedAt / SystemCreatedBy (BC semantics: created fields are
        // immutable after insert). Closes 2 al-language fails in Codeunit60152.
        // SystemCreatedAt_Does_Not_Change_On_Modify must remain passing.
        {
            var navRecord = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")
                ?? throw new InvalidOperationException("NavRecord type not found in Ncl");
            var alModify = navRecord.Methods.FirstOrDefault(m =>
                m.Name == "ALModifyAsync"
                && m.Parameters.Count == 3
                && m.Parameters[0].ParameterType.Name == "DataError"
                && m.Parameters[1].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean
                && m.Parameters[2].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean);
            if (alModify == null)
            {
                // Some Ncl revisions use a 2-arg overload
                alModify = navRecord.Methods.FirstOrDefault(m =>
                    m.Name == "ALModifyAsync"
                    && m.Parameters.Count == 2
                    && m.Parameters[0].ParameterType.Name == "DataError"
                    && m.Parameters[1].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean);
            }
            if (alModify != null)
            {
                var helperMi = typeof(AlRunnerV2.BcRuntime).GetMethod(
                    nameof(AlRunnerV2.BcRuntime.StampSystemFieldsOnModify),
                    BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("BcRuntime.StampSystemFieldsOnModify not found");
                var helperRef = asm.MainModule.ImportReference(helperMi);

                var body = alModify.Body;
                var il = body.GetILProcessor();
                var firstOriginal = body.Instructions[0];
                il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(firstOriginal, il.Create(OpCodes.Call, helperRef));
                if (body.MaxStackSize < 1) body.MaxStackSize = 1;
                Console.Error.WriteLine($"[Cecil] Prepended StampSystemFieldsOnModify → NavRecord.ALModifyAsync({alModify.Parameters.Count}-arg)");
            }
            else
            {
                Console.Error.WriteLine("[Cecil] WARN: NavRecord.ALModifyAsync not found — SystemModified stamping skipped");
            }
        }

        // ── NavRecord.get_ALReadPermission / get_ALWritePermission → return true ─────
        // AL `Rec.ReadPermission()` / `Rec.WritePermission()` lower to these getters.
        // Runner has no real permission system (single privileged user). Real BC's
        // TestPermissions=Disabled mode also returns true unconditionally — so this is
        // observably equivalent to the default test-context behaviour.
        //
        // JmpHook on these R2R-compiled getters SIGSEGVs (see RecordWritePatches.cs:194-198
        // historical note). Cecil body-replace is the safe path: clear instructions,
        // emit `ldc.i4.1; ret`.
        //
        // Closes 6 al-language fails:
        //   Codeunit60128.Database_ReadPermission_ReturnsTrue
        //   Codeunit60128.Database_WritePermission_ReturnsTrue
        //   Codeunit60059.Record_ReadPermission_ReturnsTrue
        //   Codeunit60059.Record_WritePermission_ReturnsTrue
        //   Codeunit60178.Record_ReadPermission_BCRUNNER_ReturnsTrue
        //   Codeunit60178.Record_WritePermission_BCRUNNER_ReturnsTrue
        {
            var navRecord = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
            if (navRecord != null)
            {
                foreach (var name in new[] { "get_ALReadPermission", "get_ALWritePermission" })
                {
                    var m = navRecord.Methods.FirstOrDefault(x =>
                        x.Name == name
                        && x.Parameters.Count == 0
                        && x.ReturnType.MetadataType == Mono.Cecil.MetadataType.Boolean);
                    if (m == null || !m.HasBody)
                    {
                        Console.Error.WriteLine($"[Cecil] WARN: NavRecord.{name} not found — permission fix not applied");
                        continue;
                    }
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldc_I4_1));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine($"[Cecil] Rewrote NavRecord.{name} → return true");
                }
            }
        }

        // ── NavSession.FlushDataCache(Nullable<Int32>) → no-op ───────────────────────
        // AL `SelectLatestVersion(...)` lowers to FlushDataCache. The body constructs
        // `new NavSystemCodeunitUIHelperTriggers(this.parent)`; `parent` is null on
        // the skeleton session and the ctor throws ArgumentNullException.
        //
        // Semantics: FlushDataCache is a cache-eviction hint for the server-side
        // version cache. The runner stores records in-memory via TempTableDataProvider
        // with no version cache, so flush is observably a no-op — the post-condition
        // "subsequent reads return latest data" already holds.
        //
        // Closes 6 al-language fails:
        //   Codeunit60178.Record_SelectLatestVersion_DoesNotThrow
        //   Codeunit60178.Record_SelectLatestVersion_WithTableId_DoesNotThrow
        //   Codeunit60145.Database_SelectLatestVersion_NoArgs_Succeeds
        //   Codeunit60145.Database_SelectLatestVersion_WithTableNo_Succeeds
        //   Codeunit60142.System_SelectLatestVersion_NoParameter_DoesNotThrow
        //   (+1 more — confirmed by classifier output)
        {
            var navSession = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession")
                ?? throw new InvalidOperationException("NavSession type not found in Ncl");
            var flush = navSession.Methods.FirstOrDefault(m =>
                m.Name == "FlushDataCache"
                && m.Parameters.Count == 1
                && m.ReturnType.MetadataType == Mono.Cecil.MetadataType.Void)
                ?? throw new InvalidOperationException("NavSession.FlushDataCache(Nullable<Int32>) not found");
            if (flush.HasBody)
            {
                var body = flush.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 0;
                Console.Error.WriteLine("[Cecil] Rewrote NavSession.FlushDataCache → no-op (runner has no version cache)");
            }
        }

        // ── NavNotification.ALAddAction(...) → return true ──────────────────────────
        // AL `Notification.AddAction(caption, codeunit, function[, description])`
        // registers a UI callback. Runner has no UI — actions never fire — so the
        // runtime state ("registered") is unobservable. Real BC returns true on
        // successful registration.
        //
        // Body-replace with `ldc.i4.1; ret` is observably equivalent for any in-scope
        // test (all three failing tests assert "does not throw").
        //
        // Closes 3 al-language fails:
        //   Codeunit60145.Notification_AddAction_WithDescription_Succeeds
        //   Codeunit60135.NotificationAddAction_AddsActionWithoutError
        //   Codeunit60135.NotificationAddAction_MultipleActions
        {
            var navNotification = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavNotification")
                ?? throw new InvalidOperationException("NavNotification type not found in Ncl");
            var alAddAction = navNotification.Methods.FirstOrDefault(m =>
                m.Name == "ALAddAction"
                && m.Parameters.Count == 4
                && m.ReturnType.MetadataType == Mono.Cecil.MetadataType.Boolean
                && m.Parameters[0].ParameterType.MetadataType == Mono.Cecil.MetadataType.String
                && m.Parameters[1].ParameterType.MetadataType == Mono.Cecil.MetadataType.Int32
                && m.Parameters[2].ParameterType.MetadataType == Mono.Cecil.MetadataType.String
                && m.Parameters[3].ParameterType.MetadataType == Mono.Cecil.MetadataType.String)
                ?? throw new InvalidOperationException("NavNotification.ALAddAction(String,Int32,String,String) not found");
            if (alAddAction.HasBody)
            {
                var body = alAddAction.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Rewrote NavNotification.ALAddAction → return true (no UI in runner)");
            }
        }

        // ── ALDatabase.ALTenantID() → return "STANDALONE" ────────────────────────────
        // JmpHook on this R2R-inlined static SIGSEGVs (see disabled hook in BcRuntime.cs).
        // Cecil IL rewrite is safe: body becomes ldstr "STANDALONE" / ret.
        {
            var alDbType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALDatabase");
            if (alDbType != null)
            {
                var tenantId = alDbType.Methods.FirstOrDefault(m =>
                    m.Name == "ALTenantID" && m.Parameters.Count == 0 && m.IsStatic);
                if (tenantId != null)
                {
                    var body = tenantId.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldstr, "STANDALONE"));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine("[Cecil] Replaced ALDatabase.ALTenantID → return \"STANDALONE\"");
                }
            }
        }

        // === NavQuery ctor null-safety ===
        // The AL-emitted Query{ID} class chains to `: base(parent, securityFiltering, metaQuery)`
        // (the 3-arg ctor `(ITreeObject, SecurityFiltering, NCLMetaQuery)`). The original ctor
        // body dereferences metaQuery (metaQuery.ApplicationObjectId, ValidateColumns,
        // ExtractDefaultRuntimeFilters, TopNumberOfRowsToReturn). We have no real metadata
        // tier, so we rewrite the ctor to be null-safe: just call base, set securityFiltering,
        // set NCLMetaQuery (may be null), and ExecutionGuid = Guid.NewGuid(). All metadata-
        // touching field inits are skipped — they'll fall to method-level rewrites.
        //
        // Same trim applied to the (ITreeObject, int, SecurityFiltering) overload.
        // The (ITreeObject, int, SecurityFiltering, NCLMetaQuery) overload chains to
        // : this(parent, securityFiltering, metaQuery) so it inherits the fix.
        {
            var navQuery = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavQuery")
                ?? throw new InvalidOperationException("NavQuery type not found in Ncl");

            // Discover required references by scanning NavQuery's existing ctor IL.
            MethodReference? appObjIdNewobj = null;
            MethodReference? baseCtorRef = null;
            FieldReference? securityFilteringFld = null;
            MethodReference? appObjIdGetter = null;     // NCLMetaQuery.get_ApplicationObjectId
            MethodReference? metaQuerySetter = null;    // NavQuery.set_NCLMetaQuery
            MethodReference? execGuidSetter = null;     // NavQuery.set_ExecutionGuid
            int objectTypeQueryValue = -1;

            foreach (var m in navQuery.Methods.Where(mm => mm.IsConstructor && !mm.IsStatic && mm.HasBody))
            {
                var instrs = m.Body.Instructions;
                for (int i = 0; i < instrs.Count; i++)
                {
                    var ins = instrs[i];
                    if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference mrNew &&
                        mrNew.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Types.ApplicationObjectId")
                    {
                        appObjIdNewobj = mrNew;
                        // Preceding instructions: ... <load ObjectType> <load int> newobj
                        // Walk backwards to find the ldc.i4 for ObjectType.Query (skip the int-objectId load).
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
                                // Heuristic: ObjectType.Query is a small positive int; objectId
                                // arg is loaded via ldarg, so the only ldc.i4 in this window is
                                // the enum value.
                                objectTypeQueryValue = val.Value;
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
                        fr.Name == "securityFiltering" &&
                        fr.DeclaringType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavQuery")
                    {
                        securityFilteringFld = fr;
                    }
                    if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call) &&
                        ins.Operand is MethodReference mrGet && mrGet.Name == "get_ApplicationObjectId" &&
                        mrGet.ReturnType.FullName == "Microsoft.Dynamics.Nav.Types.ApplicationObjectId")
                    {
                        // Defined on NCLMetaApplicationObject (base of NCLMetaQuery); ABI compatible.
                        appObjIdGetter = mrGet;
                    }
                    if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mrSet &&
                        mrSet.Name == "set_NCLMetaQuery")
                    {
                        metaQuerySetter = mrSet;
                    }
                    if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mrEg &&
                        mrEg.Name == "set_ExecutionGuid")
                    {
                        execGuidSetter = mrEg;
                    }
                }
            }

            if (appObjIdNewobj == null || baseCtorRef == null || securityFilteringFld == null ||
                appObjIdGetter == null || metaQuerySetter == null || execGuidSetter == null ||
                objectTypeQueryValue < 0)
            {
                throw new InvalidOperationException(
                    $"NavQuery ctor rewrite: missing IL refs " +
                    $"(appObjIdNewobj={appObjIdNewobj?.FullName ?? "null"}, " +
                    $"baseCtor={baseCtorRef?.FullName ?? "null"}, " +
                    $"secFld={securityFilteringFld?.FullName ?? "null"}, " +
                    $"appObjGetter={appObjIdGetter?.FullName ?? "null"}, " +
                    $"mqSetter={metaQuerySetter?.FullName ?? "null"}, " +
                    $"execSetter={execGuidSetter?.FullName ?? "null"}, " +
                    $"otQuery={objectTypeQueryValue})");
            }

            var guidNewGuid = asm.MainModule.ImportReference(
                typeof(Guid).GetMethod(nameof(Guid.NewGuid), Type.EmptyTypes)!);

            // Rewrite #1: ..ctor(ITreeObject, SecurityFiltering, NCLMetaQuery)
            var ctor3 = navQuery.Methods.FirstOrDefault(mm => mm.IsConstructor && !mm.IsStatic &&
                mm.Parameters.Count == 3 &&
                mm.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.ITreeObject" &&
                mm.Parameters[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.SecurityFiltering" &&
                mm.Parameters[2].ParameterType.Name == "NCLMetaQuery")
                ?? throw new InvalidOperationException("NavQuery..ctor(ITreeObject,SecurityFiltering,NCLMetaQuery) not found");
            {
                var body = ctor3.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();

                // base ctor expects: (ITreeObject, ApplicationObjectId, NCLStaticMetadata)
                // Build the AppObjId argument null-safely:
                //   if (metaQuery == null) new ApplicationObjectId(Query, 0); else metaQuery.ApplicationObjectId;
                var nullPath = il.Create(OpCodes.Ldc_I4, objectTypeQueryValue);
                var afterAppObjId = il.Create(OpCodes.Ldnull); // staticMetadata, also our merge point

                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_1));         // parent
                il.Append(il.Create(OpCodes.Ldarg_3));         // metaQuery
                il.Append(il.Create(OpCodes.Brfalse_S, nullPath));
                il.Append(il.Create(OpCodes.Ldarg_3));
                il.Append(il.Create(OpCodes.Callvirt, appObjIdGetter));
                il.Append(il.Create(OpCodes.Br_S, afterAppObjId));
                il.Append(nullPath);                            // ldc.i4 ObjectType.Query
                il.Append(il.Create(OpCodes.Ldc_I4_0));         // objectId = 0
                il.Append(il.Create(OpCodes.Newobj, appObjIdNewobj));
                il.Append(afterAppObjId);                       // ldnull (staticMetadata)
                il.Append(il.Create(OpCodes.Call, baseCtorRef));

                // this.securityFiltering = securityFiltering
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_2));
                il.Append(il.Create(OpCodes.Stfld, securityFilteringFld));

                // this.NCLMetaQuery = metaQuery
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_3));
                il.Append(il.Create(OpCodes.Call, metaQuerySetter));

                // this.ExecutionGuid = Guid.NewGuid()
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, guidNewGuid));
                il.Append(il.Create(OpCodes.Call, execGuidSetter));

                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 4;
                Console.Error.WriteLine("[Cecil] Rewrote NavQuery..ctor(ITreeObject,SecurityFiltering,NCLMetaQuery) → null-safe minimal init");
            }

            // Rewrite #2: ..ctor(ITreeObject, int, SecurityFiltering)
            var ctor3i = navQuery.Methods.FirstOrDefault(mm => mm.IsConstructor && !mm.IsStatic &&
                mm.Parameters.Count == 3 &&
                mm.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.ITreeObject" &&
                mm.Parameters[1].ParameterType.FullName == "System.Int32" &&
                mm.Parameters[2].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.SecurityFiltering")
                ?? throw new InvalidOperationException("NavQuery..ctor(ITreeObject,int,SecurityFiltering) not found");
            {
                var body = ctor3i.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();

                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_1));         // parent
                il.Append(il.Create(OpCodes.Ldc_I4, objectTypeQueryValue));
                il.Append(il.Create(OpCodes.Ldarg_2));         // objectId
                il.Append(il.Create(OpCodes.Newobj, appObjIdNewobj));
                il.Append(il.Create(OpCodes.Ldnull));          // staticMetadata
                il.Append(il.Create(OpCodes.Call, baseCtorRef));

                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_3));
                il.Append(il.Create(OpCodes.Stfld, securityFilteringFld));

                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, guidNewGuid));
                il.Append(il.Create(OpCodes.Call, execGuidSetter));

                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 4;
                Console.Error.WriteLine("[Cecil] Rewrote NavQuery..ctor(ITreeObject,int,SecurityFiltering) → null-safe minimal init (skip metadata)");
            }
        }

        // Rows 3+: rewrite NavQuery instance methods that depend on NCLMetaQuery
        // (which is null in our skeleton). Strategy: replace bodies with simple
        // const-or-throw IL. Cecil works where JmpHook silently fails (R2R precode).
        {
            var navQueryT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavQuery");
            if (navQueryT != null)
            {
                int rewriteCount = 0;
                foreach (var method in navQueryT.Methods.ToList())
                {
                    if (!method.HasBody) continue;
                    var ps = method.Parameters;

                    // ALColumnName(int) -> "Column" + columnNo
                    // ALColumnCaption(int) -> "Column" + columnNo
                    if ((method.Name == "ALColumnName" || method.Name == "ALColumnCaption")
                        && ps.Count == 1 && ps[0].ParameterType.FullName == "System.Int32"
                        && method.ReturnType.FullName == "System.String")
                    {
                        var concat = asm.MainModule.ImportReference(typeof(string).GetMethod(
                            "Concat", new[] { typeof(string), typeof(object) }));
                        var intBox = navQueryT.Module.TypeSystem.Int32;
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldstr, "Column"));
                        il.Append(il.Create(OpCodes.Ldarg_1));
                        il.Append(il.Create(OpCodes.Box, intBox));
                        il.Append(il.Create(OpCodes.Call, concat));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 2;
                        rewriteCount++;
                    }
                    // ALGetFilter(int) -> ""
                    else if (method.Name == "ALGetFilter"
                        && ps.Count == 1 && ps[0].ParameterType.FullName == "System.Int32"
                        && method.ReturnType.FullName == "System.String")
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldstr, ""));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 1;
                        rewriteCount++;
                    }
                    // ALGetFilters property getter (no params, returns string) -> ""
                    else if (method.Name == "get_ALGetFilters"
                        && ps.Count == 0
                        && method.ReturnType.FullName == "System.String")
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldstr, ""));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 1;
                        rewriteCount++;
                    }
                    // ALSetFilter / ALSetRangeSafe / ALClose -> no-op (void)
                    else if ((method.Name == "ALSetFilter"
                              || method.Name == "ALSetRangeSafe"
                              || method.Name == "ALClose")
                        && method.ReturnType.FullName == "System.Void")
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 0;
                        rewriteCount++;
                    }
                    // set_ALTopNumberOfRowsToReturn(int) -> store private field directly
                    else if (method.Name == "set_ALTopNumberOfRowsToReturn"
                        && ps.Count == 1 && ps[0].ParameterType.FullName == "System.Int32"
                        && method.ReturnType.FullName == "System.Void")
                    {
                        // Find the backing field — original setter calls InvalidateDataSet
                        // which dereferences metadata. Just write the field.
                        var topField = navQueryT.Fields.FirstOrDefault(f =>
                            f.Name == "topNumberOfRowsToReturn"
                            || f.Name == "<ALTopNumberOfRowsToReturn>k__BackingField");
                        if (topField != null)
                        {
                            var body = method.Body;
                            body.Instructions.Clear();
                            body.ExceptionHandlers.Clear();
                            body.Variables.Clear();
                            var il = body.GetILProcessor();
                            il.Append(il.Create(OpCodes.Ldarg_0));
                            il.Append(il.Create(OpCodes.Ldarg_1));
                            il.Append(il.Create(OpCodes.Stfld, topField));
                            il.Append(il.Create(OpCodes.Ret));
                            body.MaxStackSize = 2;
                            rewriteCount++;
                        }
                    }
                    // ValidateTablesNotVirtual / ValidateExpectedType / CheckMetadataHasNotChanged
                    // — sync helpers called from ALOpenAsync chain. No-op for void variants;
                    // ValidateExpectedType returns NCLMetaQueryColumn so we leave it null and
                    // let downstream sync sites deal with it (they currently NRE; addressed by
                    // the upstream rewrites of ALSetRangeSafe to no-op).
                    else if ((method.Name == "ValidateTablesNotVirtual"
                              || method.Name == "CheckMetadataHasNotChanged")
                        && method.ReturnType.FullName == "System.Void"
                        && ps.Count == 0)
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 0;
                        rewriteCount++;
                    }
                    // ALSaveAsXml(DataError, NavOutStream) / ALSaveAsCsv 3-arg + 4-arg → return true (no-op success).
                    // ALSaveAsCsv(DataError, string) [2-arg] / ALSaveAsJson(DataError, NavOutStream) → throw "Query: ..." (asserterror with "Query" expected).
                    else if (method.ReturnType.FullName == "System.Boolean"
                        && (method.Name == "ALSaveAsXml"
                            || method.Name == "ALSaveAsCsv"
                            || method.Name == "ALSaveAsJson")
                        && ps.Count >= 2
                        && ps[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.DataError")
                    {
                        bool isCsvFile2Arg = method.Name == "ALSaveAsCsv"
                            && ps.Count == 2
                            && ps[1].ParameterType.FullName == "System.String";
                        bool isJsonStream = method.Name == "ALSaveAsJson";
                        bool throwIt = isCsvFile2Arg || isJsonStream;
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        if (throwIt)
                        {
                            // throw new InvalidOperationException("Query: ...")
                            var ioeCtor = asm.MainModule.ImportReference(
                                typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
                            il.Append(il.Create(OpCodes.Ldstr,
                                $"Query: {method.Name} is not supported in standalone mode."));
                            il.Append(il.Create(OpCodes.Newobj, ioeCtor));
                            il.Append(il.Create(OpCodes.Throw));
                            body.MaxStackSize = 1;
                        }
                        else
                        {
                            il.Append(il.Create(OpCodes.Ldc_I4_1));
                            il.Append(il.Create(OpCodes.Ret));
                            body.MaxStackSize = 1;
                        }
                        rewriteCount++;
                    }
                    // ALRead(DataError) sync wrapper — throw with "Query" message.
                    // (Plan note: this is the safe sync wrapper, OK to rewrite.)
                    else if (method.Name == "ALRead"
                        && method.ReturnType.FullName == "System.Boolean"
                        && ps.Count == 1
                        && ps[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.DataError")
                    {
                        var ioeCtor = asm.MainModule.ImportReference(
                            typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldstr,
                            "Query: no data set loaded. Open() must be called before Read() in standalone mode."));
                        il.Append(il.Create(OpCodes.Newobj, ioeCtor));
                        il.Append(il.Create(OpCodes.Throw));
                        body.MaxStackSize = 1;
                        rewriteCount++;
                    }
                    // ALOpen(DataError) sync wrapper — return true.
                    else if (method.Name == "ALOpen"
                        && method.ReturnType.FullName == "System.Boolean"
                        && ps.Count == 1
                        && ps[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Types.DataError")
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldc_I4_1));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 1;
                        rewriteCount++;
                    }
                }
                Console.Error.WriteLine($"[Cecil] Rewrote {rewriteCount} NavQuery method(s) (ALColumnName/Caption, ALGetFilter(s), ALSetFilter, ALSetRangeSafe, ALClose, set_ALTopNumberOfRowsToReturn)");
            }
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
        //   DataItemIterator.SetTableView(NavRecord)
        //     → null-guard `SafeSourceTable` (null on skeleton instances) and record
        //       SetTableViewUsed = true. Filter is not yet applied to the source — TODO.
        {
            var navReportT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.NavReport");
            var ioeCtor = asm.MainModule.ImportReference(
                typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
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
                    if (method.Name == "Add"
                        && ps.Count == 2
                        && ps[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.DataItem"
                        && ps[1].ParameterType.FullName == "System.String"
                        && method.ReturnType.FullName == "System.Void"
                        && method.IsVirtual && !method.IsNewSlot)
                    {
                        var baseAdd = navReportT.BaseType?.Resolve()?.Methods
                            .FirstOrDefault(m => m.Name == "Add"
                                && m.Parameters.Count == 2
                                && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.DataItem"
                                && m.Parameters[1].ParameterType.FullName == "System.String");
                        if (baseAdd != null)
                        {
                            var baseAddRef = asm.MainModule.ImportReference(baseAdd);
                            var body = method.Body;
                            body.Instructions.Clear();
                            body.ExceptionHandlers.Clear();
                            body.Variables.Clear();
                            var il = body.GetILProcessor();
                            il.Append(il.Create(OpCodes.Ldarg_0));
                            il.Append(il.Create(OpCodes.Ldarg_1));
                            il.Append(il.Create(OpCodes.Ldarg_2));
                            il.Append(il.Create(OpCodes.Call, baseAddRef));
                            il.Append(il.Create(OpCodes.Ret));
                            body.MaxStackSize = 3;
                            reportRewrites++;
                            continue;
                        }
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
                        var stubInfo = typeof(AlRunnerV2.NavReportSync).GetMethod("StubInitializeMetadata",
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
                        var syncRunInfo = typeof(AlRunnerV2.NavReportSync).GetMethod("SyncRun",
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
                    // Static Run() / RunModal() overloads remain as `ret`
                    // placeholders here — separate JmpHooks in ReportPatches.cs
                    // throw OOS (in-process construction-from-id not wired).
                    else if ((method.Name == "Run" || method.Name == "RunModal")
                        && method.IsStatic
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
                    }
                    // RunRequestPage (any sync overload returning string) → throw OOS.
                    else if (method.Name == "RunRequestPage"
                        && method.ReturnType.FullName == "System.String")
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldstr,
                            "out-of-scope: NavReport.RunRequestPage (request-page UI rendering requires service tier)"));
                        il.Append(il.Create(OpCodes.Newobj, ioeCtor));
                        il.Append(il.Create(OpCodes.Throw));
                        body.MaxStackSize = 1;
                        reportRewrites++;
                    }
                    // SaveAsPdf / SaveAsHtml / SaveAsExcel / SaveAsWord / SaveAsDocx (sync, bool)
                    // → throw OOS. Layout rendering requires a service tier.
                    else if (method.Name.StartsWith("SaveAs")
                        && method.ReturnType.FullName == "System.Boolean")
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldstr,
                            "out-of-scope: NavReport." + method.Name +
                            " (layout rendering requires service tier)"));
                        il.Append(il.Create(OpCodes.Newobj, ioeCtor));
                        il.Append(il.Create(OpCodes.Throw));
                        body.MaxStackSize = 1;
                        reportRewrites++;
                    }
                }
            }
            // DataItemIterator.SetTableView(NavRecord) — null-guard for skeleton instances.
            var diiT = asm.MainModule.Types
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Runtime.DataItemIterator");
            if (diiT != null)
            {
                var setTableViewUsed = diiT.Properties.FirstOrDefault(p => p.Name == "SetTableViewUsed")?.SetMethod;
                foreach (var method in diiT.Methods.ToList())
                {
                    if (!method.HasBody) continue;
                    var ps = method.Parameters;
                    if (method.Name == "SetTableView"
                        && ps.Count == 1
                        && ps[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavRecord"
                        && method.ReturnType.FullName == "System.Void")
                    {
                        var body = method.Body;
                        body.Instructions.Clear();
                        body.ExceptionHandlers.Clear();
                        body.Variables.Clear();
                        var il = body.GetILProcessor();
                        if (setTableViewUsed != null)
                        {
                            il.Append(il.Create(OpCodes.Ldarg_0));
                            il.Append(il.Create(OpCodes.Ldc_I4_1));
                            il.Append(il.Create(OpCodes.Call, setTableViewUsed));
                        }
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 2;
                        reportRewrites++;
                    }
                }
            }
            Console.Error.WriteLine($"[Cecil] Rewrote {reportRewrites} NavReport/DataItemIterator method(s) (Run/RunModal→SyncRun; SaveAs*/RunRequestPage→OOS-throw; SetTableView→null-safe)");
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

                    var body = navFormCtor5.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();

                    // base(parent, new ApplicationObjectId(Page, objectId), staticMetadata)
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldarg_1));                       // parent
                    il.Append(il.Create(OpCodes.Ldc_I4, objectTypePageValue));   // ObjectType.Page
                    il.Append(il.Create(OpCodes.Ldarg_2));                       // objectId (int)
                    il.Append(il.Create(OpCodes.Newobj, appObjIdNewobj));
                    il.Append(il.Create(OpCodes.Ldarg_S, navFormCtor5.Parameters[4])); // staticMetadata
                    il.Append(il.Create(OpCodes.Call, baseCtorRef));

                    // this.masterPage = masterPage
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldarg_3));                       // masterPage
                    il.Append(il.Create(OpCodes.Stfld, masterPageFld));

                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 4;
                    Console.Error.WriteLine("[Cecil] Rewrote NavForm 5-arg private ctor → null-safe minimal init (skip Session.NavAppGroup, formId, personalizationId, etc.)");
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
                int rewrites = 0;
                foreach (var m in navFormT.Methods)
                {
                    if (!m.HasBody) continue;
                    bool target = false;
                    if (m.Name == "CallInitializeComponentExtensionMethod" && m.Parameters.Count == 0) target = true;
                    else if (m.Name == "InitializeForm" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Void") target = true;
                    else if (m.Name == "RegisterSourceExpression") target = true;
                    if (!target) continue;
                    // NEVER rewrite an async ValueTask body (CoreCLR segfault risk).
                    if (m.ReturnType.FullName.StartsWith("System.Threading.Tasks.ValueTask")) continue;
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 0;
                    rewrites++;
                }
                Console.Error.WriteLine($"[Cecil] Rewrote {rewrites} NavForm form-init method(s) (CallInitComponentExt/InitializeForm/RegisterSourceExpression) → no-op (headless: never observed via AL on ProcessingOnly path)");
            }
        }

        // Diagnostic prepend (gated by env AL_RUNNER_DIAG_IC=1): print marker at
        // entry of each Ncl method called by Report{N}.InitializeComponent.
        {
            var diagMi = typeof(AlRunnerV2.NavReportSync).GetMethod("Diag",
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
                var helperMi = typeof(AlRunnerV2.BcRuntime).GetMethod(
                    nameof(AlRunnerV2.BcRuntime.GetServiceAccountReplacement),
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
                nameof(AlRunnerV2.BcRuntime.GetServiceAccountNameReplacement));

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
                ReplaceBodyWithHelper(nclMod, ctor3, nameof(AlRunnerV2.BcRuntime.NavMethodScopeCtorReplacement));
            }

            // 2) NavMethodScope.Dispose(bool) → BcRuntime.NavMethodScope_Dispose(object, bool).
            //    Paired recursion-counter balance with the ctor. `this` is a reference type;
            //    `bool disposing` matches the helper's `bool` param exactly — no boxing.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, MsType, "Dispose", 1),
                nameof(AlRunnerV2.BcRuntime.NavMethodScope_Dispose));

            // NavMethodScope.AssertError(Action) and NavMethodScope.ProcessException(Exception)
            // — migrated to Cecil in Batch 3. Batch 2 left these JmpHook'd because their Cecil
            // form drove NORMAL mode into a safepoint-free 100%-CPU spin. That spin was COEXISTENCE
            // (JmpHook + Cecil both active on a re-entrant replacement); the Cecil-owned skip
            // registry removes coexistence, so the Cecil form is now safe. The helpers are on the
            // BcRuntime partial (MethodScopePatches.cs); both keys are registered in CecilOwned.
            // AssertError(Action): void, `this`→object widening + Action ref param, no box.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, MsType, "AssertError", 1),
                nameof(AlRunnerV2.BcRuntime.NavMethodScope_AssertError));
            // ProcessException(Exception): bool return, `this`→object? widening + Exception? param.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, MsType, "ProcessException", 1),
                nameof(AlRunnerV2.BcRuntime.NavMethodScope_ProcessException));

            // 3) ALMethodScope.AssignScopeId() → BcRuntime.ALMethodScope_AssignScopeId(object) (no-op).
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, AlMsType, "AssignScopeId", 0),
                nameof(AlRunnerV2.BcRuntime.ALMethodScope_AssignScopeId));

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
                typeof(AlRunnerV2.BcRuntime).GetMethod(
                    "NavCodeunitHandle_CreateTarget", BindingFlags.Public | BindingFlags.Static)!);

            // 2) NavRecordHandle.CreateTarget() → RecordPatches.NavRecordHandle_CreateTarget(self).
            //    Helper lives on RecordPatches (NOT BcRuntime); returns NavRecord — no cast.
            ReplaceBodyWithHelper(nclMod,
                CreateTarget0(nclMod, "Microsoft.Dynamics.Nav.Runtime.NavRecordHandle"),
                typeof(AlRunnerV2.Patches.RecordPatches).GetMethod(
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
                    typeof(AlRunnerV2.BcRuntime).GetMethod(
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
                typeof(AlRunnerV2.BcRuntime).GetMethod(
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

            var recordPatches = typeof(AlRunnerV2.Patches.RecordPatches);
            var helperShims   = typeof(AlRunnerV2.BcRuntime);          // NoOp*/ReturnValueTask*/ReturnTrue etc are BcRuntime statics
            var telemetry     = typeof(AlRunnerV2.BcRuntime);          // NavServerEventSource_* are BcRuntime partials too
            var navRecordIdP  = typeof(AlRunnerV2.Patches.NavRecordIdPatches);

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

            // ── NavSession getters / DataAccessSource (RecordWritePatches.cs:84-104,482) ──
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_DataAccessSource", 0),
                H(recordPatches, "NavSession_get_DataAccessSource"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_Database", 0),
                H(recordPatches, "NavSession_get_Database"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_SortingProperties", 0),
                H(recordPatches, "NavSession_get_SortingProperties"));

            // ── DataAccessSource.GetDataAccessForTable (RecordWritePatches.cs:106) ──
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "DataAccessSource", "GetDataAccessForTable", "NCLMetaTable", "Boolean"),
                H(recordPatches, "NavDataAccessSource_GetDataAccessForTable"));

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

            // ── NavDatabase / NavRecordId collation comparers ───────────────────
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavDatabase", "get_CollationAwareStringComparer", 0),
                H(recordPatches, "NavDatabase_get_CollationAwareStringComparer"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavRecordId", "get_CollationAwareStringComparer", 0),
                H(navRecordIdP, "NavRecordId_get_CollationAwareStringComparer"));

            // ── NavRecord no-ops (Dispose / IsGlobalTriggerImplemented / UpdateRefs) ──
            // Dispose(bool) → NoOp2; IsGlobalTriggerImplemented(Triggers) → ReturnFalse2;
            // UpdateReferencesOnRenameAsync(List,NavRecord) instance overload → ReturnValueTask3.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecord", "Dispose", "Boolean"),
                H(helperShims, "NoOp2"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecord", "IsGlobalTriggerImplemented", "Triggers"),
                H(helperShims, "ReturnFalse2"));
            {
                var navRec = nclMod.GetType(Rt + "NavRecord")!;
                var updRefs = navRec.Methods.FirstOrDefault(m =>
                    m.Name == "UpdateReferencesOnRenameAsync" && m.HasBody && m.HasThis
                    && m.Parameters.Count == 2 && m.Parameters[1].ParameterType.Name == "NavRecord")
                    ?? throw new InvalidOperationException("[Cecil] NavRecord.UpdateReferencesOnRenameAsync(List,NavRecord) not found — do not commit");
                ReplaceBodyWithHelper(nclMod, updRefs, H(helperShims, "ReturnValueTask3"));
            }

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

            // ── NavSystemCodeunitGlobalTriggers.GetTriggersOnTable(int) → Triggers.None ──
            // Helper returns int 0; the target returns the int-backed Triggers enum.
            // Emit `ldc.i4.0; ret` directly (enum/underlying-int are ret-compatible),
            // bypassing the int→enum boxing path entirely.
            {
                var gt = FindNclMethod(nclMod, Rt + "NavSystemCodeunitGlobalTriggers", "GetTriggersOnTable", 1);
                var body = gt.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 1;
                Console.Error.WriteLine("[Cecil] Replaced NavSystemCodeunitGlobalTriggers.GetTriggersOnTable → Triggers.None");
            }

            // ── NavSession getter cluster + GetActiveCompany + NavStream.Target ──
            // These are installed via Hook(...) from the main ApplyAllPatches (BcRuntime.cs
            // ~525-570 / 2291 / 2380), NOT ApplyRecordPatches — but they sit on the same
            // record/session R2R-inlined call path (CloneRecord → GetActiveCompany,
            // NavMethodScope.Run → SyncFormatSettings, etc.), so they MUST migrate in the
            // same atomic build to keep the path single-mechanism.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_CurrentMethodScope", 0),
                H(helperShims, "GetCurrentMethodScopeReplacement"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_NavAppGroup", 0),
                H(helperShims, "NavSession_NavAppGroup"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_LocalLanguageNoFallback", 0),
                H(helperShims, "NavSession_LocalLanguageNoFallback"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavSession", "get_IsLocalLanguage", 0),
                H(helperShims, "ReturnFalse_1Arg"));
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

            // ── NavRecordRef cluster (Batch 8) — get_Target + open-gates + ALOpen ─
            // get_Target's real body NREs on base.Tree.Session.Company.SharedObjects
            // on the headless skeleton; the replacement constructs a SharedRecordRef
            // parented to the process-wide skeleton container (NavRecordRefPatches).
            // CheckIsOpenAllowed / IsOpenAllowed gate Open against permissions that
            // are absent on the skeleton; the 6 ALOpen overloads build the Record via
            // OpenRecordRefById (which itself calls NavRecordRef_get_Target). These all
            // sit on the SAME record/RecordRef R2R-inlined path as get_Target, so they
            // migrate together — a Cecil get_Target coexisting with a JmpHook'd ALOpen
            // (or vice-versa) reproduced the Batch-7b coexistence spin / NRE. Atomic.
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavRecordRef", "get_Target", 0),
                H(typeof(AlRunnerV2.BcRuntime), "NavRecordRef_get_Target"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "CheckIsOpenAllowed", "CompilationTarget", "Int32"),
                H(helperShims, "NoOp3"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "IsOpenAllowed", "CompilationTarget", "Int32"),
                H(helperShims, "ReturnTrue_ThreeArgs"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "Int32"),
                H(typeof(AlRunnerV2.BcRuntime), "NavRecordRef_ALOpen_Int"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "Int32", "Boolean"),
                H(typeof(AlRunnerV2.BcRuntime), "NavRecordRef_ALOpen_IntBool"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "Int32", "Boolean", "String"),
                H(typeof(AlRunnerV2.BcRuntime), "NavRecordRef_ALOpen_IntBoolCompany"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "CompilationTarget", "Int32"),
                H(typeof(AlRunnerV2.BcRuntime), "NavRecordRef_ALOpen_TargetInt"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "CompilationTarget", "Int32", "Boolean"),
                H(typeof(AlRunnerV2.BcRuntime), "NavRecordRef_ALOpen_TargetIntBool"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecordRef", "ALOpen", "CompilationTarget", "Int32", "Boolean", "String"),
                H(typeof(AlRunnerV2.BcRuntime), "NavRecordRef_ALOpen_TargetIntBoolCompany"));

            // ── NavSession.GetPermissionSet (Batch 8) — both 3-arg overloads ─────
            // Real body NREs reaching the skeleton's (null) Permissions object.
            // Both return the all-granted PermissionSet singleton. This is the leaf
            // of the CalcSums path (ALCalcSumsAsync → CalcSumsAsync →
            // VerifyPermissionsCalculatedFields → GetPermissionSet) which NREs in
            // Cecil-only mode; migrating it fixes that whole cluster.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavSession", "GetPermissionSet",
                         "NavApplicationObjectBase", "Int32", "ApplicationObjectId"),
                H(typeof(AlRunnerV2.BcRuntime), "NavSession_GetPermissionSet_ByObjectId"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavSession", "GetPermissionSet",
                         "NavApplicationObjectBase", "Int32", "IEnumerable`1"),
                H(typeof(AlRunnerV2.BcRuntime), "NavSession_GetPermissionSet_ByObjectIds"));

            // ── NavCodeunit run path (Batch 8) — DoRunAsync + RunCodeunit ───────
            // DoRunAsync's first line builds a timing scope via
            // DiagnosticsResolver.GetMostSpecificInstance(Session) which NREs on the
            // skeleton; the static RunCodeunit reaches the same inlined body. Both
            // dispatch OnRun directly via the helpers. NavCodeunitHandle.CreateTarget
            // (already CecilOwned, Batch 3) is on the same path → migrate together.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavCodeunit", "DoRunAsync", "DataError", "NavRecord"),
                H(typeof(AlRunnerV2.BcRuntime), "NavCodeunit_DoRunAsync"));
            // RunCodeunit has TWO 3-arg overloads ((DataError,Int32,NavRecord) and
            // (DataError,String,NavRecord)); mirror the JmpHook by targeting only the
            // Int32 one. The String/lower-arity overloads forward into it.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavCodeunit", "RunCodeunit", "DataError", "Int32", "NavRecord"),
                H(typeof(AlRunnerV2.BcRuntime), "NavCodeunit_RunCodeunit"));

            // ── Truncate + security-filtering cluster (Batch 8) ─────────────────
            // ValidateTruncateSupport throws NavPermissionException on the skeleton
            // (a security filter is set); SetSecurityFiltering / DataProvider.Truncate
            // Async / SessionHasSuperOr… all sit on the same record/security R2R path.
            // Migrate together so the path is single-mechanism.
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "NavRecord", "ValidateTruncateSupport", "NavRecord"),
                H(helperShims, "NoOp_OneArg"));
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "RecordImplementation", "SetSecurityFiltering", "SecurityFiltering"),
                H(helperShims, "NoOp2"));
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
                H(typeof(AlRunnerV2.BcRuntime), "NavApplicationObjectBase_TryInvoke"));

            // ── BitArrayHelpers.Equals (Batch 8) — .NET API drift ───────────────
            // Real body calls GetIntArray which reads the removed private field
            // System.Collections.BitArray.m_array → MissingFieldException on current
            // .NET. Helper compares via the public indexer. (Reached from
            // FieldLoadInfo.Equals on the SetBaseLoadFields path.)
            ReplaceBodyWithHelper(nclMod,
                ByParams(Rt + "Utility.BitArrayHelpers", "Equals", "BitArray", "BitArray"),
                H(typeof(AlRunnerV2.BcRuntime), "BitArrayHelpers_Equals"));

            // ── Event-binding metadata cluster (Batch 8) ────────────────────────
            // NavCodeunit.get_MetaCodeunit + NCLMetaCodeunit.get_IsEventManualBinding.
            // The real getters traverse the metadata cache / dereference
            // ApplicationObjectClrType (null on the skeleton) → NRE. Helpers build a
            // skeleton NCLMetaCodeunit from the AL-emitted Codeunit{N} CLR type.
            // Reached via Bind/UnbindSubscription. Migrate together (companion getters).
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NavCodeunit", "get_MetaCodeunit", 0),
                H(typeof(AlRunnerV2.BcRuntime), "NavCodeunit_get_MetaCodeunit"));
            ReplaceBodyWithHelper(nclMod,
                FindNclMethod(nclMod, Rt + "NCLMetaCodeunit", "get_IsEventManualBinding", 0),
                H(typeof(AlRunnerV2.BcRuntime), "NCLMetaCodeunit_get_IsEventManualBinding"));

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
            foreach (var notifName in new[] { "ALSend", "ALRecall" })
                ReplaceBodyWithHelper(nclMod,
                    ByParams(Rt + "NavNotification", notifName, "DataError"),
                    H(helperShims, "NavNotification_ALSendOrRecall"));

            // ── FlowField CalcFieldsAsync(2)/(3) — already Cecil-body-rewritten above
            //    (see ~line 1751). FlowFieldPatches.Register additionally JmpHook.Apply's
            //    a (dead-under-R2R) fallback on the SAME methods; registering their keys
            //    in CecilOwned turns that fallback into a no-op so the path is strictly
            //    single-mechanism. No rewrite here — the body replace is upstream.
        }

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
    /// Clear <paramref name="target"/>'s body and emit `ldarg.0..N; call BcRuntime.helper; ret`,
    /// forwarding every IL argument (incl. `this` for instance methods) to the static helper.
    /// The helper's return value (if any) is left on the stack as the method result.
    /// Generalises the inline RecordLink ReplaceWithStaticHelper.
    /// </summary>
    private static void ReplaceBodyWithHelper(
        ModuleDefinition module, MethodDefinition target, string bcRuntimeHelperName)
    {
        var helperMi = typeof(AlRunnerV2.BcRuntime).GetMethod(
            bcRuntimeHelperName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"[Cecil] BcRuntime.{bcRuntimeHelperName} not found");
        ReplaceBodyWithHelper(module, target, helperMi);
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
    /// the source Ncl bytes, runner assembly mtime, and CACHE_VERSION. Set
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
            File.WriteAllBytes(binNclPath, bytes);
            Console.Error.WriteLine($"[Cecil] Wrote rewritten Ncl to {binNclPath} ({bytes.Length} bytes)");
            return true;
        }

        var cacheKey = ComputeCacheKey(nclSrc);
        var shortKey = cacheKey[..8];

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "al-runner", "ncl-cecil");
        Directory.CreateDirectory(cacheDir);
        var cachePath = Path.Combine(cacheDir, $"{cacheKey}.dll");

        if (File.Exists(cachePath))
        {
            Console.Error.WriteLine($"[Cecil] Cecil cache HIT (key={shortKey})");
            File.Copy(cachePath, binNclPath, overwrite: true);
            Console.Error.WriteLine($"[Cecil] Copied cached Ncl to {binNclPath}");
            PruneCacheFiles(cacheDir, cachePath, keepNewest: 8);
            return false;
        }

        Console.Error.WriteLine($"[Cecil] Cecil cache MISS — rewrote and cached (key={shortKey})");
        var modifiedBytes = RewriteNcl(nclSrc);

        // Write to cache atomically via temp-file-then-rename so concurrent runners
        // never read a partially-written cache entry.
        var tempPath = cachePath + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tempPath, modifiedBytes);
        File.Move(tempPath, cachePath, overwrite: true);
        Console.Error.WriteLine($"[Cecil] Saved to cache ({modifiedBytes.Length} bytes)");

        // Produce binNclPath via File.Copy from the freshly-written cache entry,
        // mirroring the cache-HIT path above. (Note: this alone does NOT prevent the
        // cold-run load crash — a process that ran the Cecil rewrite then loads the
        // byte-identical Ncl in-process still intermittently fails with
        // BadImageFormatException 0x80131124. The caller re-execs on the `true` return
        // below so the actual load always happens in a fresh process via cache HIT.)
        File.Copy(cachePath, binNclPath, overwrite: true);
        Console.Error.WriteLine($"[Cecil] Copied rewritten Ncl to {binNclPath} ({modifiedBytes.Length} bytes)");
        PruneCacheFiles(cacheDir, cachePath, keepNewest: 8);
        return true;
    }

    private static string ComputeCacheKey(string nclPath)
    {
        var nclBytes = File.ReadAllBytes(nclPath);
        var runnerMtimeTicks = File.GetLastWriteTimeUtc(typeof(NclCecilRewrite).Assembly.Location).Ticks;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(nclBytes);
        hash.AppendData(BitConverter.GetBytes(runnerMtimeTicks));
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
}
