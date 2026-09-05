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
    private static void RewriteNcl_Records(AssemblyDefinition asm)
    {
        {
            var repoType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.IsolatedStorageRepository");
            if (repoType != null)
            {
                var tsp = typeof(AlRunner.Patches.TenantStoragePatches);
                void RewriteRepo(string name, int paramCount, string helperName)
                {
                    var m = repoType.Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == paramCount)
                        ?? throw new InvalidOperationException(
                            $"IsolatedStorageRepository.{name}/{paramCount} not found — Ncl shape changed");
                    var h = tsp.GetMethod(helperName, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException($"TenantStoragePatches.{helperName} not found");
                    ReplaceBodyWithHelper(asm.MainModule, m, h);
                }
                RewriteRepo("Set", 9, nameof(AlRunner.Patches.TenantStoragePatches.Repo_Set));
                RewriteRepo("Get", 8, nameof(AlRunner.Patches.TenantStoragePatches.Repo_Get));
                RewriteRepo("Contains", 6, nameof(AlRunner.Patches.TenantStoragePatches.Repo_Contains_6));
                RewriteRepo("Contains", 5, nameof(AlRunner.Patches.TenantStoragePatches.Repo_Contains_5));
                RewriteRepo("Delete", 6, nameof(AlRunner.Patches.TenantStoragePatches.Repo_Delete));
                Console.Error.WriteLine("[Cecil] Rewrote IsolatedStorageRepository.{Set,Get,Contains×2,Delete} → TenantStoragePatches in-memory store");
            }

            // ALSystemEncryption — same dead-JmpHook migration. The real bodies resolve a
            // tenant RSA/KeyVault encryption provider (NavTenant.GetEncryptionKeyFileName →
            // "The given database is not a tenant database" on the skeleton), hit by
            // BaseApp CU1266/1279 IsEncryptionEnabled from SPBLIC's SetAppValue during
            // the Pageworks install. Rewrite the four AL-facing statics onto the
            // in-process AES envelope (real crypto — encrypted ≠ plaintext; key exists /
            // encryption enabled are TRUE, matching an encryption-enabled BC tenant).
            var sysEncType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALSystemEncryption");
            if (sysEncType != null)
            {
                var tsp2 = typeof(AlRunner.Patches.TenantStoragePatches);
                void RewriteEnc(string name, int paramCount, string helperName)
                {
                    var m = sysEncType.Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == paramCount)
                        ?? throw new InvalidOperationException(
                            $"ALSystemEncryption.{name}/{paramCount} not found — Ncl shape changed");
                    var h = tsp2.GetMethod(helperName, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException($"TenantStoragePatches.{helperName} not found");
                    ReplaceBodyWithHelper(asm.MainModule, m, h);
                }
                RewriteEnc("ALEncrypt", 1, nameof(AlRunner.Patches.TenantStoragePatches.SysEnc_ALEncrypt));
                RewriteEnc("ALDecrypt", 1, nameof(AlRunner.Patches.TenantStoragePatches.SysEnc_ALDecrypt));
                RewriteEnc("ALKeyExists", 0, nameof(AlRunner.Patches.TenantStoragePatches.SysEnc_ALKeyExists));
                RewriteEnc("ALEncryptionEnabled", 0, nameof(AlRunner.Patches.TenantStoragePatches.SysEnc_ALEncryptionEnabled));
                Console.Error.WriteLine("[Cecil] Rewrote ALSystemEncryption.{ALEncrypt,ALDecrypt,ALKeyExists,ALEncryptionEnabled} → in-process AES envelope");
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
                    // Guard the real body; do NOT replace it. This used to be an unconditional
                    // `ret` justified by "the only legitimate use exercised by AL tests is the
                    // no-op path" — true of the bucket-1 contracts above, and false as soon as
                    // real AL arrived: `Card.SetRecord(Rec); Card.RunModal();` is how AL hands a
                    // page the row it must show, and silently dropping it opened the page on
                    // whatever the source table yielded first. A no-op that is only correct for
                    // the callers you happened to know about announces nothing when a new one
                    // appears — see .claude/rules/loud-failures.md.
                    foreach (var m in navFormTypeRew.Methods.Where(x =>
                        (x.Name == "GetRecord" || x.Name == "SetRecord" || x.Name == "SetTableView") &&
                        x.Parameters.Count == 1))
                    {
                        var body = m.Body;
                        if (body.Instructions.Count == 0) continue;
                        var il = body.GetILProcessor();
                        var original = body.Instructions[0];

                        // if (this.SourceTable == null) return;  — then fall through to BC's own
                        // body, which is what a page with a real source table needs.
                        var ret = il.Create(OpCodes.Ret);
                        il.InsertBefore(original, il.Create(OpCodes.Ldarg_0));
                        il.InsertBefore(original, il.Create(OpCodes.Call,
                            asm.MainModule.ImportReference(sourceTableGetter)));
                        il.InsertBefore(original, il.Create(OpCodes.Brtrue, original));
                        il.InsertBefore(original, ret);

                        body.MaxStackSize = Math.Max(body.MaxStackSize, 1);
                        Console.Error.WriteLine(
                            $"[Cecil] Guarded NavForm.{m.Name}({m.Parameters[0].ParameterType.Name}) "
                            + "→ no-op only when SourceTable is null");
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
                    var helperMi = typeof(AlRunner.BcRuntime).GetMethod(
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
                    var helperMi = typeof(AlRunner.BcRuntime).GetMethod(helperName, BindingFlags.Public | BindingFlags.Static);
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
                ReplaceWithStaticHelper("AddLinkAsync", nameof(AlRunner.BcRuntime.RecordLink_AddLinkAsync), 3);
                ReplaceWithStaticHelper("HasLinks", nameof(AlRunner.BcRuntime.RecordLink_HasLinks), 1);
                ReplaceWithStaticHelper("DeleteLinksAsync", nameof(AlRunner.BcRuntime.RecordLink_DeleteLinksAsync), 1);
                ReplaceWithStaticHelper("DeleteLinkAsync", nameof(AlRunner.BcRuntime.RecordLink_DeleteLinkAsync), 2);
                ReplaceWithStaticHelper("CopyLinksAsync", nameof(AlRunner.BcRuntime.RecordLink_CopyLinksAsync), 2);
                ReplaceWithStaticHelper("MoveLinksAsync", nameof(AlRunner.BcRuntime.RecordLink_MoveLinksAsync), 2);
                ReplaceWithStaticHelper("TableHasLinks", nameof(AlRunner.BcRuntime.RecordLink_TableHasLinks), 3);
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

            var helperMi = typeof(AlRunner.Patches.FlowFieldPatches).GetMethod(
                nameof(AlRunner.Patches.FlowFieldPatches.RecordImpl_CalcFieldsAsync_2),
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

            var helperMi = typeof(AlRunner.Patches.FlowFieldPatches).GetMethod(
                nameof(AlRunner.Patches.FlowFieldPatches.RecordImpl_CalcFieldsAsync_3),
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

        // ── FlowFieldsHelper.FieldsAndFormulaAreSelfReferencing(NCLMetaField[]) ──────
        // The real BC body iterates `f.CalculationFormula.Filters` and NREs when the
        // formula is the shared `NCLMetaCalculationFormula.EmptyFormula` singleton,
        // whose `Filters` collection is constructed as null. That happens for skeleton
        // FlowFields whose CalculationFormula could not be materialised (observed on
        // Purchase Line "Matched Order Lines" 39/2701 during Purch.-Post via the
        // TempTableDataProvider filter visitor). Rewrite the body to a null-safe helper
        // that returns the same value BC would compute (false for an empty/null filter
        // set — no field-filter ⇒ no self-reference). Runtime-engine layer; no AL body
        // is touched. Reuses the helper already shipped in Runner.dll.
        {
            var ffh = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.FlowFieldsHelper")
                ?? throw new InvalidOperationException("FlowFieldsHelper type not found");
            var m = ffh.Methods.FirstOrDefault(x =>
                x.Name == "FieldsAndFormulaAreSelfReferencing" && x.Parameters.Count == 1
                && x.Parameters[0].ParameterType is ArrayType)
                ?? throw new InvalidOperationException("FlowFieldsHelper.FieldsAndFormulaAreSelfReferencing not found");

            var helperMi = typeof(AlRunner.Patches.FlowFieldPatches).GetMethod(
                nameof(AlRunner.Patches.FlowFieldPatches.FieldsAndFormulaAreSelfReferencing),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("FlowFieldPatches.FieldsAndFormulaAreSelfReferencing not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var body = m.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, helperRef));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Replaced FlowFieldsHelper.FieldsAndFormulaAreSelfReferencing → FlowFieldPatches (null-safe)");
        }

        // ── FlowFieldsHelper.CalcFieldsAsync (the 9-arg static) ─────────────────────
        // #1757. Rewriting RecordImplementation.CalcFieldsAsync (above) keeps AL's own
        // CalcFields off the broken async pipeline, but BC re-enters this STATIC from
        // inside its own code and the record-level hooks never see those calls:
        //
        //   GetFilterFromMetaFilterCollection, case FieldClass.FlowField:
        //       NavValue value = CalcFieldsAsync(session, companyToken, currentRecord,
        //                            filtersAndMarks, new[] { nCLMetaField }, false,
        //                            securityFiltering, alIsolationLevel, recursionLevel)
        //                        .AsTask().GetAwaiter().GetResult()[filter.ValueField];
        //
        // — i.e. a `where(X = field("<a FlowField>"))` condition is resolved by RECURSIVELY
        // calculating the referenced FlowField. RecordIsWithinFilteredFlowFieldsAsync reaches
        // the same static. Both used to land in the async body that NREs on the skeleton
        // session, which is why #1716 had to refuse the whole formula.
        //
        // Hooking the static (rather than pre-computing values into the parent buffer and
        // presenting the value field as Normal) leaves BC's dispatch in charge: BC decides
        // when to recurse, in what order the conditions resolve, and BC's own recursion
        // guards still run — FlowFieldPatches reproduces both of them (recursionLevel > 50
        // and FieldsAndFormulaAreSelfReferencing → NavNCLStackOverflowException) inside the
        // shared core. Every other BC caller of this method is served for free.
        //
        // Body shape emitted:
        //   FieldDictionary<NavValue> fd = (FieldDictionary<NavValue>)
        //       FlowFieldPatches.FlowFieldsHelper_CalcFieldsAsync(
        //           session, companyToken, recordBuffer, filtersAndMarks, fieldsToCalc,
        //           onlyFieldsSourcedFromVirtualTables, (object)securityFiltering,
        //           (object)alIsolationLevel, recursionLevel);
        //   return new ValueTask<FieldDictionary<NavValue>>(fd);
        //
        // The helper takes `object` for every Ncl-internal parameter type (NavSession,
        // IRecordBuffer, FiltersAndMarks) and returns `object`, because FieldDictionary<>,
        // FiltersAndMarks and IRecordBuffer are all INTERNAL to Ncl and cannot be named from
        // Runner.dll. The two enum arguments are boxed at the call site; the returned
        // dictionary is castclass'd back here, where the real type IS nameable.
        {
            var ffh = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.FlowFieldsHelper")
                ?? throw new InvalidOperationException("FlowFieldsHelper type not found");
            var calcStatic = ffh.Methods.FirstOrDefault(x =>
                x.Name == "CalcFieldsAsync" && x.IsStatic && x.Parameters.Count == 9)
                ?? throw new InvalidOperationException(
                    "FlowFieldsHelper.CalcFieldsAsync(9 args) not found — Ncl shape changed");

            // ValueTask`1<FieldDictionary`1<NavValue>> — taken from the method's own signature
            // so the generic instantiation never has to be rebuilt by hand.
            if (calcStatic.ReturnType is not GenericInstanceType retType
                || retType.GenericArguments.Count != 1)
                throw new InvalidOperationException(
                    "FlowFieldsHelper.CalcFieldsAsync return type is not ValueTask<T> — Ncl shape changed");
            var fieldDictionaryType = retType.GenericArguments[0];

            var helperMi = typeof(AlRunner.Patches.FlowFieldPatches).GetMethod(
                nameof(AlRunner.Patches.FlowFieldPatches.FlowFieldsHelper_CalcFieldsAsync),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("FlowFieldPatches.FlowFieldsHelper_CalcFieldsAsync not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            // ValueTask<TResult>(TResult result) — the single-arg ctor whose parameter is the
            // type's own generic parameter (the other single-arg ctor takes Task<TResult>).
            var vtCtorOpen = typeof(System.Threading.Tasks.ValueTask<>).GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 1
                                     && c.GetParameters()[0].ParameterType.IsGenericParameter)
                ?? throw new InvalidOperationException("ValueTask<TResult>(TResult) ctor not found");
            var vtCtorRef = asm.MainModule.ImportReference(vtCtorOpen);
            var boundCtor = new MethodReference(vtCtorRef.Name, vtCtorRef.ReturnType, retType)
            {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = vtCtorRef.CallingConvention,
            };
            foreach (var p in vtCtorRef.Parameters)
                boundCtor.Parameters.Add(new ParameterDefinition(p.ParameterType));

            var asyncAttr = calcStatic.CustomAttributes
                .FirstOrDefault(ca => ca.AttributeType.Name == "AsyncStateMachineAttribute");
            if (asyncAttr != null) calcStatic.CustomAttributes.Remove(asyncAttr);

            var body = calcStatic.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));                                  // NavSession       → object
            il.Append(il.Create(OpCodes.Ldarg_1));                                  // int companyToken
            il.Append(il.Create(OpCodes.Ldarg_2));                                  // IRecordBuffer    → object
            il.Append(il.Create(OpCodes.Ldarg_3));                                  // FiltersAndMarks  → object
            il.Append(il.Create(OpCodes.Ldarg_S, calcStatic.Parameters[4]));        // NCLMetaField[]   → Array
            il.Append(il.Create(OpCodes.Ldarg_S, calcStatic.Parameters[5]));        // bool
            il.Append(il.Create(OpCodes.Ldarg_S, calcStatic.Parameters[6]));        // SecurityFiltering
            il.Append(il.Create(OpCodes.Box, calcStatic.Parameters[6].ParameterType));
            il.Append(il.Create(OpCodes.Ldarg_S, calcStatic.Parameters[7]));        // ALIsolationLevel
            il.Append(il.Create(OpCodes.Box, calcStatic.Parameters[7].ParameterType));
            il.Append(il.Create(OpCodes.Ldarg_S, calcStatic.Parameters[8]));        // int recursionLevel
            il.Append(il.Create(OpCodes.Call, helperRef));
            il.Append(il.Create(OpCodes.Castclass, fieldDictionaryType));
            il.Append(il.Create(OpCodes.Newobj, boundCtor));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 9;
            Console.Error.WriteLine(
                "[Cecil] Replaced FlowFieldsHelper.CalcFieldsAsync(9) → FlowFieldPatches.FlowFieldsHelper_CalcFieldsAsync");
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

            var helperMi = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.RecordImpl_InternalFindRecordWithoutCheckingValuesAsync),
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

            var helperMi = typeof(AlRunner.Patches.RecordPatches).GetMethod(
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

            var helperMi = typeof(AlRunner.Patches.RecordPatches).GetMethod(
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

        // NavSession.get_ExecutionContext used to be replaced here with `return
        // ExecutionContext.Normal`, because the getter's third branch reads
        // `Database?.UpgradeManager?.IsSessionInUpgrade(Id)` and the lazy UpgradeManager getter
        // NRE'd on `tenant.Id` — NavDatabase.Tenant was null on the skeleton. That cause is fixed
        // at the source (RecordPatches.Register wires the skeleton NavDatabase's tenant field,
        // MetadataPatches seeds NavSystemTenant.upgradeMetadata), so BC's own getter runs:
        // Install / Uninstall from session.AppInstallationContext, Upgrade from
        // session.AppUpgradeContext, and Normal otherwise — which the hardcoded 0 got wrong
        // inside an install trigger, where the runner does populate AppInstallationContext.
        // See AlRunner#2353.

        // ── ALNavApp.GetDataVersionForUpgrade(NavAppRuntimeMetadata) → return null ──
        // The method probes whether an app data-upgrade is in progress, via
        // `NavCurrentThread.Session?.Database?.UpgradeManager…`. Under R2R the
        // `Session.Database` access is inlined to `Session.Tenant.Database`, bypassing
        // our NavSession.get_Database redirect, and `NavTenant.Database` throws
        // ArgumentNullException("NavDatabase") because the skeleton tenant has no
        // database LazyEx. A headless test session is NEVER in a data upgrade, so the
        // faithful result is the same `null` the real body returns when
        // navAppUpgradeContext == null (the normal, non-upgrade case). Returning null
        // makes the `GetDataVersionForInstall(app) ?? GetDataVersionForUpgrade(app) ??
        // app.Version` chain fall through to app.Version, exactly as on a live tier with
        // no upgrade running. Reached from FeatureTelemetry.LogUsage → ALGetModuleInfo
        // during Purch.-Post (RecoverySolutions CU74486 posting tests). Runtime-engine
        // layer; no AL business-logic body is touched.
        {
            var alNavApp = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALNavApp")
                ?? throw new InvalidOperationException("ALNavApp type not found");
            var m = alNavApp.Methods.FirstOrDefault(x =>
                x.Name == "GetDataVersionForUpgrade" && x.Parameters.Count == 1 && x.IsStatic)
                ?? throw new InvalidOperationException("ALNavApp.GetDataVersionForUpgrade not found — Ncl shape changed; do not commit");

            var body = m.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldnull)); // no upgrade in progress → null data version
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Replaced ALNavApp.GetDataVersionForUpgrade → return null (no skeleton upgrade)");
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

            var helperMi = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.AssignAutoIncrement),
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

            var helperMi = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.StampSystemFieldsOnInsert),
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

        // ── NavRecord.ALInsertAsync(DataError, bool, bool) — User Property companion row ──
        // On a real tier, SystemTableTriggers.OnBeforeInsertAsync's `case 2000000120:` arm
        // inserts the matching User Property (2000000121) row for every User it accepts, and
        // BC's own UserManagement.DirectSetUserFieldValue then Gets that row with the RAISING
        // error level. The runner bypasses BC's trigger dispatch on insert, so the row was
        // never created. Same prepend shape as AssignAutoIncrement / StampSystemFieldsOnInsert
        // above; a no-op for every table but User. See AlRunner/Patches/UserTableTriggerPatches.cs
        // and issue #2355.
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

            var helperMi = typeof(AlRunner.Patches.UserTableTriggerPatches).GetMethod(
                nameof(AlRunner.Patches.UserTableTriggerPatches.CreateUserPropertyOnUserInsert),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("UserTableTriggerPatches.CreateUserPropertyOnUserInsert not found");
            var helperRef = asm.MainModule.ImportReference(helperMi);

            var body = alInsert3.Body;
            var il = body.GetILProcessor();
            var firstOriginal = body.Instructions[0];
            il.InsertBefore(firstOriginal, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(firstOriginal, il.Create(OpCodes.Call, helperRef));
            if (body.MaxStackSize < 1) body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Prepended CreateUserPropertyOnUserInsert → NavRecord.ALInsertAsync(DataError,bool,bool)");
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
                var helperMi = typeof(AlRunner.BcRuntime).GetMethod(
                    nameof(AlRunner.BcRuntime.StampSystemFieldsOnModify),
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

                // ── ALDatabase.ALLastUsedRowVersion / ALMinimumActiveRowVersion ──────
                // Both real bodies call NavSqlRowVersionCommand, which opens a SQL
                // connection and NREs in NavSqlConnectionScope.TryOpenConnection on the
                // headless session. The JmpHook registrations for these two have been
                // orphaned since the JmpHook layer was disabled by default (BcRuntime's
                // Hook(...) call sites became silent no-ops), so BC's unpatched SQL body
                // ran and 5 al-language tests NRE'd. Migrate to Cecil, backed by the real
                // monotonic counter in ALDatabasePatches — see the faithfulness note there.
                foreach (var (name, helper) in new[]
                         {
                             ("ALLastUsedRowVersion",
                              nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_ALLastUsedRowVersion)),
                             ("ALMinimumActiveRowVersion",
                              nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_ALMinimumActiveRowVersion)),
                         })
                {
                    var m = alDbType.Methods.FirstOrDefault(
                        x => x.Name == name && x.Parameters.Count == 0 && x.IsStatic)
                        ?? throw new InvalidOperationException($"ALDatabase.{name}() not found in Ncl");
                    var helperMi = typeof(AlRunner.Patches.ALDatabasePatches).GetMethod(
                        helper, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException($"ALDatabasePatches.{helper} not found");
                    ReplaceBodyWithHelper(asm.MainModule, m, helperMi);
                    Console.Error.WriteLine($"[Cecil] Replaced ALDatabase.{name} → {helper}");
                }
            }
        }

        // ── Row-version clock: advance on every AL write entry point ─────────────────
        // The counter above is only faithful if it actually moves when a row is written.
        // Prepend a no-arg bump to each AL write entry on NavRecord, mirroring the
        // AssignAutoIncrement / StampSystemFields prepends below. Insert/Modify/Delete/
        // Rename are the four AL-visible row writes; every overload of each is covered
        // because AL binds different overloads depending on the call form.
        {
            var navRecord = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")
                ?? throw new InvalidOperationException("NavRecord type not found in Ncl");
            var bumpMi = typeof(AlRunner.Patches.ALDatabasePatches).GetMethod(
                nameof(AlRunner.Patches.ALDatabasePatches.NoteRecordWrite),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("ALDatabasePatches.NoteRecordWrite not found");
            var bumpRef = asm.MainModule.ImportReference(bumpMi);
            // ALInsertAsync gets its OWN prepend target (AlRunner#2142) — as of AlRunner#2431
            // it is behaviourally identical to NoteRecordWrite; see
            // ALDatabasePatches.NoteRecordInsertWrite's doc for why the separate method still
            // exists (a distinct Cecil prepend target).
            var insertBumpMi = typeof(AlRunner.Patches.ALDatabasePatches).GetMethod(
                nameof(AlRunner.Patches.ALDatabasePatches.NoteRecordInsertWrite),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("ALDatabasePatches.NoteRecordInsertWrite not found");
            var insertBumpRef = asm.MainModule.ImportReference(insertBumpMi);

            // The bulk forms count too: BC's DeleteAll/ModifyAll write rows, so they advance
            // @@DBTS and open a write transaction exactly like the single-row forms — and the
            // rollback snapshot hangs off this same note, so leaving them out meant a
            // DeleteAll before the first single-row write was never captured and therefore
            // never rolled back.
            //
            // DeleteAllAsync/ModifyAllAsync (no "AL" prefix) — NOT ALDeleteAllAsync/
            // ALModifyAllAsync — deliberately (AlRunner#1791). The single-row forms
            // (ALInsert/ALModify/ALDelete/ALRename) all fire their prepend correctly via
            // either overload because BC's own sync entry point (e.g. `ALInsert(bool)`)
            // itself calls the "AL"-prefixed async sibling (`ALInsertAsync(...)`), so
            // hooking the async name catches both call surfaces. The bulk forms break that
            // pattern: decompiling the shipped Ncl.dll shows `ALDeleteAll(bool)` — what AL's
            // compiler actually binds `Record.DeleteAll(RunTrigger)` to, confirmed by
            // decompiling this project's own AL-compiled test output — calls the PROTECTED
            // `DeleteAllAsync(bool)` directly, bypassing `ALDeleteAllAsync` entirely (same
            // for `ALModifyAll` → `ModifyAllAsync`). Hooking `ALDeleteAllAsync` therefore
            // never fired for any DeleteAll()/ModifyAll() statement the AL compiler emits —
            // confirmed by decompiling this project's OWN compiled test corpus: zero
            // `.ALDeleteAllAsync(`/`.ALModifyAllAsync(` call sites anywhere in it, only
            // `.ALDeleteAll(` (125 occurrences). Most visibly this left
            // IsInWriteTransaction() silently reading false after a DeleteAll() that matched
            // zero rows (AlRunner#1791's reproduction), but the miss is unconditional — the
            // hooked method is simply never reached, whether or not the call matches any
            // rows. `DeleteAllAsync`/`ModifyAllAsync` are each a single, non-overloaded,
            // protected `virtual` method that every entry surface (0-arg/1-arg, sync/async)
            // funnels through exactly once, so hooking them fires exactly once per AL
            // DeleteAll()/ModifyAll() statement regardless of which surface form the AL
            // compiler chose — no double-count risk from a forwarding overload also being
            // in this list.
            var insertEntries = new[] { "ALInsertAsync" };
            var writeEntries = new[]
            {
                "ALModifyAsync", "ALDeleteAsync", "ALRenameAsync",
                "DeleteAllAsync", "ModifyAllAsync",
            };
            int bumped = 0;
            foreach (var m in navRecord.Methods.Where(
                         x => (insertEntries.Contains(x.Name) || writeEntries.Contains(x.Name))
                              && x.HasBody && x.Body.Instructions.Count > 0))
            {
                var il = m.Body.GetILProcessor();
                var first = m.Body.Instructions[0];
                // Pass `this` so the helper can exclude temporary records, which touch no
                // database and therefore neither advance @@DBTS nor open a write transaction.
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, insertEntries.Contains(m.Name) ? insertBumpRef : bumpRef));
                bumped++;
            }
            if (bumped == 0)
                throw new InvalidOperationException(
                    "[Cecil] no NavRecord AL write entry points found for the write-note prepend — " +
                    "Database.LastUsedRowVersion would stop advancing silently.");
            Console.Error.WriteLine(
                $"[Cecil] Prepended NoteRecordWrite/NoteRecordInsertWrite → {bumped} NavRecord AL write entry point(s)");
        }


        // -- All Profile (2000000178) write rules ------------------------------------
        // The in-memory store behind All Profile accepts any write, but a real tier does
        // not: AllProfileDataProvider routes Insert/Modify/Delete through
        // TenantProfileTableDataHandler, which refuses Insert/Delete/Rename for any profile
        // an installed app declares. Prepend a guard to those three AL write entry points,
        // exactly like the rowversion clock above; it is a no-op for every table but
        // 2000000178. Modify is deliberately NOT guarded -- a non-key modify of an app-owned
        // profile is legal on a real tier (it writes the per-tenant profile settings), and
        // Microsoft's own AllProfile V2 Test.Cleanup() relies on that.
        // See AlRunner/Patches/AllProfileWritePatches.cs.
        {
            var navRecordForProfileGuard = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")
                ?? throw new InvalidOperationException("NavRecord type not found in Ncl");

            MethodReference ProfileGuardRef(string helper)
            {
                var mi = typeof(AlRunner.Patches.AllProfileWritePatches).GetMethod(
                    helper, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"AllProfileWritePatches.{helper} not found");
                return asm.MainModule.ImportReference(mi);
            }

            var profileGuards = new (string Entry, MethodReference Helper)[]
            {
                ("ALInsertAsync", ProfileGuardRef(nameof(AlRunner.Patches.AllProfileWritePatches.GuardAllProfileInsert))),
                ("ALDeleteAsync", ProfileGuardRef(nameof(AlRunner.Patches.AllProfileWritePatches.GuardAllProfileDelete))),
                ("ALRenameAsync", ProfileGuardRef(nameof(AlRunner.Patches.AllProfileWritePatches.GuardAllProfileRename))),
            };

            int profileGuarded = 0;
            foreach (var (entry, helper) in profileGuards)
                foreach (var m in navRecordForProfileGuard.Methods.Where(
                             x => x.Name == entry && x.HasBody && x.Body.Instructions.Count > 0))
                {
                    var il = m.Body.GetILProcessor();
                    var first = m.Body.Instructions[0];
                    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(first, il.Create(OpCodes.Call, helper));
                    profileGuarded++;
                }
            if (profileGuarded == 0)
                throw new InvalidOperationException(
                    "[Cecil] no NavRecord ALInsert/ALDelete/ALRename entry points found for the All Profile "
                    + "write guard - an app-owned profile would silently be deletable.");
            Console.Error.WriteLine(
                $"[Cecil] Prepended All Profile write guards -> {profileGuarded} NavRecord AL write entry point(s)");
        }

        // -- Page background task write refusal (issue #2514) -----------------------------
        // A page background task's worker codeunit runs inline against the current session
        // (RunnerPageBackgroundTaskGap.cs), with NavSession.PageBackgroundTask set for the
        // duration — real BC refuses ANY write from that scope (measured against BC 27.5 and
        // 28.3, corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#135; see
        // PageBackgroundTaskWritePatches.cs for the full trail). Prepend a guard to all four
        // AL write entry points, exactly like the All Profile guard above and the rowversion
        // clock before it — a no-op for every write except one made from inside a page
        // background task worker.
        {
            var navRecordForPbtGuard = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")
                ?? throw new InvalidOperationException("NavRecord type not found in Ncl");

            MethodReference PbtGuardRef(string helper)
            {
                var mi = typeof(AlRunner.Patches.PageBackgroundTaskWritePatches).GetMethod(
                    helper, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"PageBackgroundTaskWritePatches.{helper} not found");
                return asm.MainModule.ImportReference(mi);
            }

            var pbtGuards = new (string Entry, MethodReference Helper)[]
            {
                ("ALInsertAsync", PbtGuardRef(nameof(AlRunner.Patches.PageBackgroundTaskWritePatches.GuardPageBackgroundTaskInsert))),
                ("ALModifyAsync", PbtGuardRef(nameof(AlRunner.Patches.PageBackgroundTaskWritePatches.GuardPageBackgroundTaskModify))),
                ("ALDeleteAsync", PbtGuardRef(nameof(AlRunner.Patches.PageBackgroundTaskWritePatches.GuardPageBackgroundTaskDelete))),
                ("ALRenameAsync", PbtGuardRef(nameof(AlRunner.Patches.PageBackgroundTaskWritePatches.GuardPageBackgroundTaskRename))),
            };

            int pbtGuarded = 0;
            foreach (var (entry, helper) in pbtGuards)
                foreach (var m in navRecordForPbtGuard.Methods.Where(
                             x => x.Name == entry && x.HasBody && x.Body.Instructions.Count > 0))
                {
                    var il = m.Body.GetILProcessor();
                    var first = m.Body.Instructions[0];
                    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(first, il.Create(OpCodes.Call, helper));
                    pbtGuarded++;
                }
            if (pbtGuarded == 0)
                throw new InvalidOperationException(
                    "[Cecil] no NavRecord ALInsert/ALModify/ALDelete/ALRename entry points found for the "
                    + "page background task write guard - a worker codeunit's write would silently succeed.");
            Console.Error.WriteLine(
                $"[Cecil] Prepended page background task write guards -> {pbtGuarded} NavRecord AL write entry point(s)");
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

    }

    private static void AddRecordsOwned(HashSet<string> set)
    {
        // IsolatedStorageRepository lowest layer (Cecil-migrated onto the
        // TenantStoragePatches in-memory store) — legacy JmpHooks must no-op.
        set.Add("Microsoft.Dynamics.Nav.Runtime.IsolatedStorageRepository::Set/9");
        set.Add("Microsoft.Dynamics.Nav.Runtime.IsolatedStorageRepository::Get/8");
        set.Add("Microsoft.Dynamics.Nav.Runtime.IsolatedStorageRepository::Contains/6");
        set.Add("Microsoft.Dynamics.Nav.Runtime.IsolatedStorageRepository::Contains/5");
        set.Add("Microsoft.Dynamics.Nav.Runtime.IsolatedStorageRepository::Delete/6");
        // ALSystemEncryption AL-facing statics (Cecil-migrated onto the in-process
        // AES envelope) — legacy JmpHooks must no-op.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALSystemEncryption::ALEncrypt/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALSystemEncryption::ALDecrypt/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALSystemEncryption::ALKeyExists/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALSystemEncryption::ALEncryptionEnabled/0");
        // ── Record / session / data-access path (Batch 6 — the LINCHPIN). ────────
        // Migrated ATOMICALLY so the whole path is single-mechanism (Cecil), killing
        // the JmpHook+Cecil coexistence spin. Each key's body is rewritten in the
        // Batch-6 block in RewriteNcl to forward to its existing JmpHook helper.
        // NCLMetadata lookups
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetadata::GetMetaTableById/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetadata::GetMetaApplicationObject/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetadata::GetMetaApplicationObject/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable::GetFieldByNo/2");
        // Referencing-relations reverse index (rename propagation, #1730) — Cecil-forwarded
        // to RecordPatches.NCLMetaTable_ComputeReferencingRelations over the runner's
        // metatable cache (BC's body reads the null ObjectLoader and NREs).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable::ComputeReferencingRelations/2");
        // DataAccessSource + TempTableDataProvider
        set.Add("Microsoft.Dynamics.Nav.Runtime.DataAccessSource::GetDataAccessForTable/2");
        // NavRecord::UpdateReferencesOnRenameAsync/2 is deliberately NOT here: BC's real
        // body runs (rename propagation to validated TableRelation fields, issue #1730).
        // RecordLink / management
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordLink::MoveLinksAsync/2");
        // RecordLink::HasLinks/1 is ALSO Cecil-rewritten (ReplaceWithStaticHelper below, "RecordLink
        // — rewrite all link-management methods") but was missing from this list (#1883 follow-up).
        // RecordLinkPatches.cs separately JmpHook's the same static with its own replacement
        // (RecordLink_HasLinks) — kept as defense-in-depth (same precedent as NavXmlPort::Run
        // below), but the missing key here meant the audit misclassified it as "orphaned" instead
        // of "redundant", and — more importantly — meant JmpHook.Apply would have actually
        // installed the native patch on top of the Cecil-rewritten body if AL_RUNNER_ENABLE_JMPHOOK=1
        // ever re-enabled the JmpHook layer: the exact JmpHook+Cecil COEXISTENCE double-dispatch
        // spin this registry exists to prevent (see NCLEnumMetadata::Create/1 above). Registering
        // the key here makes JmpHook.Apply skip installing the native patch entirely (see
        // JmpHook.Apply's CecilOwned check) — the redundant registration becomes provably inert
        // under BOTH JmpHook-enabled and JmpHook-disabled configurations, not just the default.
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordLink::HasLinks/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavManagementTasks::CopyCompany/2");
        // NCLMetaTable.CreateObjectInstance — concrete-type-aware record construction so
        // OldRecord (xRec) is the concrete Record{Id}, not a base NavRecord (see
        // RecordPatches.CreateObjectInstance.cs). Sibling of the nulled-out
        // ApplicationObjectConstructor above.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable::CreateObjectInstance/5");
        // RecordImplementation path
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::VerifyPermissions/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::InternalFindRecordWithoutCheckingValuesAsync/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::VerifySecurityFiltersOnRecordAsync/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::VerifySecurityFiltersAsync/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::get_IsOpen/0");
        // FlowField CalcFieldsAsync — body already Cecil-rewritten upstream; register
        // keys so FlowFieldPatches.Register's JmpHook.Apply fallback becomes a no-op.
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::CalcFieldsAsync/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::CalcFieldsAsync/3");
        // NavRecordRef cluster (Batch 8). get_Target + all 6 ALOpen overloads. ALOpen
        // keys are by arity (Key counts declared params), so /1../4 each cover every
        // overload of that arity — both the (int,…) and (CompilationTarget,int,…)
        // families. Migrated atomically (same R2R path).
        //
        // CheckIsOpenAllowed/2 and IsOpenAllowed/2 are deliberately NOT here: #2783
        // stopped replacing them so BC's own compilation-target scope gate runs. Their
        // bodies are BC's, nothing hooks them, so registering them as Cecil-owned would
        // be a false claim.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecordRef::get_Target/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecordRef::ALOpen/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecordRef::ALOpen/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecordRef::ALOpen/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavRecordRef::ALOpen/4");
        // NavNotification send/recall (Batch 8).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavNotification::ALSend/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavNotification::ALRecall/1");
    }

}
