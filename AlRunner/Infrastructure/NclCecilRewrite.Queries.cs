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
    private static void RewriteNcl_Queries(AssemblyDefinition asm)
    {
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

                    // Query-execution path: ALOpen/ALRead sync wrappers drive the REAL
                    // async engine (ALOpenAsync/ALReadAsync → FindDataImplAsync →
                    // GetDataAccessForQuery → in-memory GetDataAccessForTable). Now that
                    // NavQuery instances carry a real NCLMetaQuery (built via
                    // RecordPatches.BuildRealNCLMetaQuery), keep their ORIGINAL bodies so
                    // queries actually execute against in-memory data instead of being stubbed.
                    if (method.Name == "ALOpen" || method.Name == "ALRead")
                        continue;

                    // Query metadata / filter-state AL wrappers — their ORIGINAL bodies
                    // dereference NCLMetaQuery.QueryDefinition.GetColumnByNo(...) (column
                    // name/caption/no, SetRange/SetFilter, GetFilter) or just clear the
                    // open dataset (TopNumberOfRowsToReturn setter, ValidateTablesNotVirtual,
                    // CheckMetadataHasNotChanged). They were stubbed when NCLMetaQuery was
                    // null; now that NavQuery instances carry a REAL NCLMetaQuery (built via
                    // RecordPatches.BuildRealNCLMetaQuery) those bodies work, so keep them —
                    // exactly the ALOpen/ALRead un-stub above. This restores real column
                    // metadata (ColumnName/ColumnCaption/ColumnNo) and query filter state
                    // (SetRange/SetFilter set the FilterFieldDictionary keyed by the query
                    // column; GetFilter reads it back). Filter *evaluation* against the
                    // in-memory store is handled in RecordPatches.QueryProjection (table-
                    // field-keyed translation), so leaving these as real is faithful.
                    if (method.Name == "ALColumnName" || method.Name == "ALColumnCaption"
                        || method.Name == "ALColumnNo" || method.Name == "ALGetFilter"
                        || method.Name == "get_ALGetFilters"
                        || method.Name == "ALSetFilter" || method.Name == "ALSetRangeSafe"
                        || method.Name == "set_ALTopNumberOfRowsToReturn"
                        || method.Name == "ValidateTablesNotVirtual"
                        || method.Name == "CheckMetadataHasNotChanged")
                        continue;

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
                    // NOTE: ALSaveAsXml / ALSaveAsCsv / ALSaveAsJson are deliberately NOT
                    // rewritten. They used to be: Xml and Csv were replaced with a bare
                    // `return true` that wrote nothing at all, and Json with a throw. The
                    // `return true` pair is exactly the silent-fake shape loud-failures.md
                    // forbids — AL asked for a dataset export, got success, and read back an
                    // empty stream. BC's own implementations run instead, against the real
                    // NCLMetaQuery the query-symbol sidecar now supplies.
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

        // NCLMetadata.EnsureAppGroupOwnedObjectsInitialized(NavAppGroup, string) → no-op.
        // Building a real NCLMetaQuery (RecordPatches.BuildRealNCLMetaQuery) drives BC's
        // CreateQueryDefinition, whose ResolveAppGroupForTableMetadataResolution calls this
        // to lazily initialise an app group's owned objects. On the skeleton that recurses
        // into InitializeBaseAppGroup, which locks on a null `appObjectInitializationChangeOrRemovalSyncRoot`
        // (ArgumentNullException) and would otherwise build empty metadata for every system
        // object. We resolve table metadata via the GetMetaTableById hook (which ignores app
        // group), so this lazy group-object init is unnecessary work — no-op it. (Tables never
        // reach here because their lookups are hooked before the app-group machinery; only the
        // query-definition build hits it.)

    }

    private static void AddQueriesOwned(HashSet<string> set)
    {
        // NavApplicationObjectBase..ctor keystone (Batch 4) — the 3-arg
        // (ITreeObject, ApplicationObjectId, NCLStaticMetadata) ctor.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase::.ctor/3");
        // NavApplicationObjectBase.TryInvoke (Batch 8) — AL TryFunction entry.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase::TryInvoke/2");
        // NavApplicationObjectBase.TryInvokeAsync — async TryFunction entry (same skeleton
        // issue: session.CurrentMethodScope NRE). Once patched, CU3801.InitializeFromCurrentApp
        // can invoke its body; the Azure KV SDK load then hits the NavDotNet.CreateNavServerHandle
        // catch block → RunnerOutOfScopeException instead of a silent NRE.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase::TryInvokeAsync/2");
    }

}
