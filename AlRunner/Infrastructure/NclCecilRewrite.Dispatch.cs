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
    private static void RewriteNcl_Dispatch(AssemblyDefinition asm, MethodReference oosCtor)
    {
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
            var dispatcherMethod = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.CodeunitEventDispatch_OnRunEventAsync),
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

            var helperMethodInfo = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NavObjectList_get_Target),
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

        // NavObjectDictionary<TKey,TValue>.get_Target — the sibling of the NavObjectList
        // case immediately above, and the last of the get_Target family still on the old
        // per-closed-instantiation JmpHook path (AsyncStateMachineSpike.
        // ApplyNavObjectDictionaryGetTargetHooks). That path has two structural holes:
        //   1. it goes through JmpHook, which is disabled by default (Cecil-only), so it
        //      no-ops entirely unless AL_RUNNER_ENABLE_JMPHOOK=1;
        //   2. it installs one hook per CLOSED instantiation, discovered by scanning the
        //      fields and properties of a SINGLE assembly (the app under test) — so a
        //      dictionary that only ever appears as a method local, or that lives in a
        //      dependency assembly (DependencyLoader never calls SetTestAssembly), is
        //      never covered. Reference-type instantiations can still be covered by
        //      accident because the CLR shares one __Canon body across all ref/ref
        //      instantiations; value-type ones get their own body and cannot be.
        // Rewriting the OPEN generic's body closes both holes at once and is exactly what
        // the NavObjectList block above already does — same problem, same shape, same
        // helper contract. The helper resolves <TKey,TValue> reflectively from the
        // receiver, so one rewrite serves every instantiation.
        {
            var navObjectDictType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavObjectDictionary`2")
                ?? throw new InvalidOperationException("NavObjectDictionary`2 not found in Ncl — shape changed");
            var sharedNavObjectDictType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavObjectDictionary`2")
                ?? throw new InvalidOperationException("SharedNavObjectDictionary`2 not found in Ncl — shape changed");
            var getTargetMethod = navObjectDictType.Methods
                .FirstOrDefault(m => m.Name == "get_Target" && m.Parameters.Count == 0)
                ?? throw new InvalidOperationException("NavObjectDictionary<TKey,TValue>.get_Target not found");

            var helperMethodInfo = typeof(AlRunner.BcRuntime).GetMethod(
                nameof(AlRunner.BcRuntime.NavObjectDictionary_get_Target),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("BcRuntime.NavObjectDictionary_get_Target not found");
            var helperRef = asm.MainModule.ImportReference(helperMethodInfo);

            var sharedDictBound = new GenericInstanceType(sharedNavObjectDictType);
            sharedDictBound.GenericArguments.Add(navObjectDictType.GenericParameters[0]);
            sharedDictBound.GenericArguments.Add(navObjectDictType.GenericParameters[1]);

            var body = getTargetMethod.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, helperRef));
            il.Append(il.Create(OpCodes.Castclass, sharedDictBound));
            il.Append(il.Create(OpCodes.Ret));
            body.MaxStackSize = 1;
            Console.Error.WriteLine("[Cecil] Rewrote NavObjectDictionary`2.get_Target → BcRuntime.NavObjectDictionary_get_Target helper");
        }

        // ALCompiler.DotNetToNavOutStream — marshals a NavDotNet-wrapped .NET Stream into
        // a NavOutStream. Real body wraps the stream in a NavStreamProvider parented to
        //     parentOfResult.Tree.Session.Company.SharedObjects
        // which is null on the headless skeleton → NRE (or ArgumentNullException from the
        // TreeObject base ctor). Hit by System Application CU 1279 "Cryptography Management
        // Impl." GenerateHash(InStream, HashAlgorithmType) — Pageworks cluster #1. Rewrite
        // to delegate to BcRuntime.ALCompiler_DotNetToNavOutStream, which replicates the
        // real branches exactly and parents the provider to the real session container when
        // present, else the process-wide skeleton TreeSharedObjectContainer (same approach
        // as the get_Target family above). RED→GREEN: tests/runner-extras/crypto-hash-instream.
        {
            var alCompilerType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALCompiler")
                ?? throw new InvalidOperationException("ALCompiler not found in Ncl — shape changed");
            var dotNetToNavOutStream = alCompilerType.Methods.FirstOrDefault(m =>
                m.Name == "DotNetToNavOutStream" && m.Parameters.Count == 2)
                ?? throw new InvalidOperationException("ALCompiler.DotNetToNavOutStream/2 not found — shape changed");
            ReplaceBodyWithHelper(asm.MainModule, dotNetToNavOutStream,
                nameof(AlRunner.BcRuntime.ALCompiler_DotNetToNavOutStream));
            Console.Error.WriteLine("[Cecil] Rewrote ALCompiler.DotNetToNavOutStream → BcRuntime helper (skeleton SharedObjects fallback)");
        }

        // ALCompiler.DotNetToNavInStream (#2576) — same fix as DotNetToNavOutStream just
        // above, InStream direction. Decompiling both methods off the same cached Ncl
        // (bc281, Microsoft.Dynamics.Nav.Runtime.ALCompiler.DotNetToNavInStream /
        // .DotNetToNavOutStream) shows they are STRUCTURALLY IDENTICAL — same three
        // branches (null → Default, Stream → NavStreamProvider-backed instance, else →
        // NavNCLConversionException), only the NavInStream/NavOutStream type differs —
        // so this is the same skeleton-SharedObjects gap the OutStream side already
        // fixed, not a new investigation. RED→GREEN:
        // tests/runner-extras/standalone-suites/dotnet-instream (added in this PR); the
        // upstream BC-behaviour claim is pinned in
        // StefanMaron/BusinessCentral.AL.Language.Tests#137
        // (tests/al-language/streams/TestDotNetInStream.al).
        {
            var alCompilerType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALCompiler")
                ?? throw new InvalidOperationException("ALCompiler not found in Ncl — shape changed");
            var dotNetToNavInStream = alCompilerType.Methods.FirstOrDefault(m =>
                m.Name == "DotNetToNavInStream" && m.Parameters.Count == 2)
                ?? throw new InvalidOperationException("ALCompiler.DotNetToNavInStream/2 not found — shape changed");
            ReplaceBodyWithHelper(asm.MainModule, dotNetToNavInStream,
                nameof(AlRunner.BcRuntime.ALCompiler_DotNetToNavInStream));
            Console.Error.WriteLine("[Cecil] Rewrote ALCompiler.DotNetToNavInStream → BcRuntime helper (skeleton SharedObjects fallback)");
        }

        // NavHttpClient egress — external HTTP is permanently out of scope (docs/scope.md §3.2,
        // anchor external-http), but AL's HTTP MOCKING is not: a test that declares
        // [HandlerFunctions('X')] and an [HttpClientHandler] procedure X gets its request served
        // by that AL handler and no socket is ever opened, which is squarely in scope.
        //
        // #2547: this used to rewrite the VERBS — ALGet/ALPost/ALPut/ALDelete/ALPatch/ALSend and
        // their *Async twins — to throw unconditionally. That is one call frame too early. Every
        // verb funnels into NavHttpClient.Send / SendAsync(NavHttpRequestMessage), whose FIRST
        // line is the mock dispatcher:
        //
        //     if (Tree.Session.TestExecution.TestHandleHttpClientRequest(this, request, out var mocked))
        //     { response.Value.Assign(mocked); return mocked.ALIsSuccessfulRequest; }   // mocked
        //     LogSecretHeaderNames(request);
        //     return SendAsync(errorLevel, request.RequestMessage, response);           // real egress
        //
        // Throwing at the verb meant that dispatcher never ran and every mocked-HTTP test was
        // refused as external-http, with no way to reach a first-class BC test feature.
        //
        // So the refusal moves down to the ONE method that actually opens a socket: the private
        // SendAsync overload taking a System.Net.Http.HttpRequestMessage. Both callers above
        // reach it only after TestHandleHttpClientRequest declined, so it is exactly the egress
        // boundary and nothing in scope passes through it. Header / base-address configuration
        // methods (ALSetBaseAddress, ALDefaultRequestHeaders, …) remain untouched, as before.
        //
        // Note for anyone measuring this: inside a test under the default TestHttpRequestPolicy
        // this throw is UNREACHABLE — BC's own dispatcher raises
        // NavNCLTestCodeunitUnhandledHttpRequestException (no handler) or
        // NavNCLNotAllowedHttpClientHandlerFallThroughException (handler returned true) first,
        // which is real BC behaviour and better left alone. It is reachable with
        // TestHttpRequestPolicy = AllowAllOutboundRequests, and for any HTTP outside a test
        // method (TestHandleHttpClientRequest's own `if (!InTest) return false`) — which is
        // precisely where the runner would otherwise open a real socket in silence.
        {
            var httpType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavHttpClient");
            if (httpType != null)
            {
                // Reuse the InvalidOperationException(string) `oosCtor` imported above (same
                // pattern as NavReport.SaveAs). The "out-of-scope: <api> — <reason> — see
                // docs/scope.md#<anchor>" message format is the loud-failures contract that AL
                // `asserterror`/`Assert.ExpectedError('out-of-scope:')` matches. Using the
                // already-imported memberRef avoids adding a new typeRef/memberRef to Ncl
                // (R2R token-shift safety — see feedback_r2r_cecil_token_shift).
                // The single egress method: private SendAsync(DataError, HttpRequestMessage, ByRef).
                // Matched on the parameter type, not the name — NavHttpClient has a SECOND private
                // SendAsync with the same arity whose second parameter is a NavHttpRequestMessage,
                // and THAT one is the mock-dispatching wrapper that must keep its body.
                int httpRewritten = 0;
                foreach (var method in httpType.Methods)
                {
                    if (!method.HasBody || method.Name != "SendAsync") continue;
                    if (method.Parameters.Count != 3) continue;
                    if (method.Parameters[1].ParameterType.FullName != "System.Net.Http.HttpRequestMessage") continue;

                    var body = method.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    // `call <helper returning Exception>; throw`, NOT the inline
                    // `ldstr + newobj InvalidOperationException` the verb rewrite used.
                    // Measured: from this deeper frame BC's error machinery replaces an
                    // unrecognised CLR exception with NavNCLInvalidOperationException ("The
                    // requested operation cannot be performed in this context.") and discards
                    // the original — the expectation classifier then reports "no out-of-scope
                    // signal". A BC-native AL error survives. See
                    // BcRuntime.MakeHttpEgressOutOfScopeException.
                    // ldarg.2 = the HttpRequestMessage (0 = this, 1 = errorLevel, 2 = requestMessage,
                    // 3 = response). The helper reads the HTTP method off it so the refusal names
                    // the verb the AL author wrote — HttpClient.Get, not a collapsed
                    // HttpClient.Send. AlRunner.Tests' ExpectationManifestWiringTests asserts that
                    // exact spelling, which is how the first version of this change (which
                    // collapsed the label) got caught.
                    il.Append(il.Create(OpCodes.Ldarg_2));
                    il.Append(il.Create(OpCodes.Call, asm.MainModule.ImportReference(
                        typeof(AlRunner.BcRuntime).GetMethod(
                            nameof(AlRunner.BcRuntime.MakeHttpEgressOutOfScopeException))!)));
                    il.Append(il.Create(OpCodes.Throw));
                    body.MaxStackSize = 1;
                    httpRewritten++;
                }
                // Loud when the shape moves. This method is the ONLY thing standing between an
                // unmocked AL HttpClient call and a real socket, so "we could not find it" must
                // never degrade into "we silently allowed egress" — that is the difference
                // between a missing feature and a broken promise (loud-failures.md).
                if (httpRewritten == 1)
                    Console.Error.WriteLine("[Cecil] Rewrote NavHttpClient.SendAsync(HttpRequestMessage) → throw OOS (external-http)");
                else
                    throw new InvalidOperationException(
                        $"[Cecil] Expected exactly ONE NavHttpClient.SendAsync(DataError, System.Net.Http.HttpRequestMessage, ByRef) "
                        + $"to rewrite as the HTTP egress boundary, found {httpRewritten}. BC's NavHttpClient shape has changed; "
                        + "external HTTP would otherwise escape unrefused. See AlRunner/Infrastructure/NclCecilRewrite.Dispatch.cs "
                        + "and issue #2547.");

                // NavHttpClient.get_Target — must NOT throw: the AL `HttpClient` value type
                // lazily materialises its backing SharedNavHttpClient via get_Target during
                // FIELD SETUP (scope ctor), before any verb call. The real body NREs on the
                // headless skeleton (base.Tree.Session.Company.SharedObjects is null). Delegate
                // to the existing helper that constructs a SharedNavHttpClient parented to the
                // skeleton container (no HTTP infra in that ctor) — same Cecil-delegation shape
                // as NavObjectList`1.get_Target. With construction succeeding, the egress verbs
                // above are what throw OOS, so `asserterror HttpClient.Get(...)` observes the
                // named failure at the call site. (The prior JmpHook for this getter no longer
                // fires under Cecil-only mode, which is why the raw NRE was surfacing.)
                var getTarget = httpType.Methods.FirstOrDefault(mm => mm.Name == "get_Target");
                if (getTarget != null && getTarget.HasBody)
                {
                    var sharedHttpType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavHttpClient");
                    var helperMI = typeof(AlRunner.BcRuntime).GetMethod(
                        nameof(AlRunner.BcRuntime.NavHttpClient_get_Target),
                        BindingFlags.Public | BindingFlags.Static);
                    if (sharedHttpType != null && helperMI != null)
                    {
                        var helperRef = asm.MainModule.ImportReference(helperMI);
                        var body = getTarget.Body;
                        body.Instructions.Clear();
                        body.Variables.Clear();
                        body.ExceptionHandlers.Clear();
                        var il = body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ldarg_0));
                        il.Append(il.Create(OpCodes.Call, helperRef));
                        il.Append(il.Create(OpCodes.Castclass, sharedHttpType));
                        il.Append(il.Create(OpCodes.Ret));
                        body.MaxStackSize = 1;
                        Console.Error.WriteLine("[Cecil] Rewrote NavHttpClient.get_Target → BcRuntime helper (skeleton-parented)");
                    }
                }
            }

            // NavHttpResponseMessageBase.get_Target — same skeleton-parented delegation as
            // NavHttpClient.get_Target above. The AL `HttpResponseMessage` value type also
            // materialises its backing SharedNavHttpResponseMessage during scope-ctor field
            // setup and NREs on the headless skeleton; its JmpHook no longer fires under
            // Cecil-only mode. Delegate to the existing BcRuntime helper so the response value
            // constructs cleanly (the HTTP egress that would populate it throws OOS first).
            var httpRespType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavHttpResponseMessageBase");
            if (httpRespType != null)
            {
                var getTarget = httpRespType.Methods.FirstOrDefault(mm => mm.Name == "get_Target");
                var sharedRespType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavHttpResponseMessage");
                var helperMI = typeof(AlRunner.BcRuntime).GetMethod(
                    nameof(AlRunner.BcRuntime.NavHttpResponseMessageBase_get_Target),
                    BindingFlags.Public | BindingFlags.Static);
                if (getTarget != null && getTarget.HasBody && sharedRespType != null && helperMI != null)
                {
                    var helperRef = asm.MainModule.ImportReference(helperMI);
                    var body = getTarget.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Call, helperRef));
                    il.Append(il.Create(OpCodes.Castclass, sharedRespType));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine("[Cecil] Rewrote NavHttpResponseMessageBase.get_Target → BcRuntime helper (skeleton-parented)");
                }
            }

            // NavHttpRequestMessage.get_Target — same skeleton-parented delegation as
            // NavHttpClient.get_Target / NavHttpResponseMessageBase.get_Target above, but this
            // one was MISSING here (#1883 follow-up): its BcRuntime.cs JmpHook registration
            // (BcRuntime.NavHttpRequestMessage_get_Target, unchanged, already correct) never
            // fires under Cecil-only mode, so BC's real body ran instead and genuinely NREs —
            // confirmed empirically: `var Req: HttpRequestMessage;` alone throws
            // ArgumentNullException(parent) out of TreeObject..ctor, because
            // NavHttpRequestMessage's own ctor eagerly calls Target.SetMessage(...) during
            // scope-ctor field setup, and the real get_Target body constructs
            // `new SharedNavHttpRequestMessage(base.Tree.Session.Company.SharedObjects)` with a
            // null container on the headless skeleton. Unlike the sibling two, this is not a
            // "same shape, already safe" case — it is the one-in-eight genuine bug #1883 asks
            // each cluster to check for. Delegate to the existing BcRuntime helper (identical
            // shape to the sibling two, just never wired into Cecil).
            var httpReqType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavHttpRequestMessage");
            if (httpReqType != null)
            {
                var getTarget = httpReqType.Methods.FirstOrDefault(mm => mm.Name == "get_Target");
                var sharedReqType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavHttpRequestMessage");
                var helperMI = typeof(AlRunner.BcRuntime).GetMethod(
                    nameof(AlRunner.BcRuntime.NavHttpRequestMessage_get_Target),
                    BindingFlags.Public | BindingFlags.Static);
                if (getTarget != null && getTarget.HasBody && sharedReqType != null && helperMI != null)
                {
                    var helperRef = asm.MainModule.ImportReference(helperMI);
                    var body = getTarget.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Call, helperRef));
                    il.Append(il.Create(OpCodes.Castclass, sharedReqType));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine("[Cecil] Rewrote NavHttpRequestMessage.get_Target → BcRuntime helper (skeleton-parented)");
                }
            }
        }

        // ALDatabase.ALCommit — and, formerly, the table-connection one-liners. The
        // ALSetDefaultTableConnection / ALRegisterTableConnection / ALUnregisterTableConnection
        // no-ops and the ALHasTableConnection `return false` that used to live here were
        // silent fakes standing in for a null NavSession.TableConnectionManager; the skeleton
        // session now carries BC's real manager (TableConnectionPatches, #2725) and those
        // bodies run unmodified. The one runtime-layer neutralisation they need is
        // TableConnectionSettingsStorage.Get, further down in this block.
        {
            var alDatabaseType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALDatabase");
            if (alDatabaseType != null)
            {
                // ALCommit is no longer a bare no-op: there is nothing to flush (the
                // in-memory store is written through) but the write transaction ENDS here,
                // which is what AL observes through Database.IsInWriteTransaction().
                foreach (var m in alDatabaseType.Methods.Where(x => x.Name == "ALCommit" && x.Parameters.Count == 0 && x.HasBody))
                {
                    ReplaceBodyWithHelper(asm.MainModule, m,
                        typeof(AlRunner.Patches.ALDatabasePatches).GetMethod(
                            nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_ALCommit),
                            BindingFlags.Public | BindingFlags.Static)!);
                    Console.Error.WriteLine("[Cecil] Rewrote ALDatabase.ALCommit → end write transaction");
                }

                // TableConnectionSettingsStorage.Get (#2725) — the one SQL touch on the
                // table-connection path: TableConnectionManager.RegisterTableConnection and
                // GetCurrentTableConnection ask NavGlobal.AppDatabase.TableConnectionSettingsStorage
                // whether a connection of that name was PERSISTED (a SELECT against
                // $ndo$tableconnections). The runner persists none, so `null` — "no such
                // stored connection" — is exactly what an empty table answers; anything else
                // would open a NavSqlConnectionScope on the skeleton. Body → `ldnull; ret`, no
                // new token references. TableConnectionPatches.PlantTableConnectionSettingsStorage
                // gives the skeleton NavDatabase the instance this method is invoked on.
                var storageType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.TableConnectionSettingsStorage");
                var storageGet = storageType?.Methods.FirstOrDefault(x =>
                    x.Name == "Get" && x.Parameters.Count == 2 && x.HasBody);
                if (storageGet == null)
                    throw new InvalidOperationException(
                        "[Cecil] TableConnectionSettingsStorage.Get(TableConnectionType, string) not found — "
                        + "Ncl shape changed; RegisterTableConnection would open a SQL connection. Do not commit.");
                {
                    var body = storageGet.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var il = body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldnull));
                    il.Append(il.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine("[Cecil] Rewrote TableConnectionSettingsStorage.Get → null (no persisted table connections)");
                }

                // ALDatabase.get_ALSerialNumber (#1883 follow-up) — genuinely NREs standalone,
                // confirmed empirically (not just from the pre-existing comment, per #2004/#2014's
                // discipline): NavSession.get_License() reads Database.SecurityAndLicense.License,
                // which chains into skeleton-null service-tier state and throws
                // NullReferenceException at Microsoft.Dynamics.Nav.Runtime.NavSession.get_License()
                // before ALSerialNumber's own body even runs. The prior JmpHook registration for
                // this (BcRuntime.cs, deleted in this same change) targeted this exact method and
                // is provably orphaned now that JmpHook is off by default — Cecil must own it
                // instead. Reuses the existing ReturnStandalone_0Args "STANDALONE" sentinel
                // (same one Database.TenantId/318-navtext-string-rewrite already return) rather
                // than inventing a new placeholder.
                foreach (var m in alDatabaseType.Methods.Where(x =>
                    x.Name == "get_ALSerialNumber" && x.Parameters.Count == 0 && x.HasBody))
                {
                    ReplaceBodyWithHelper(asm.MainModule, m, nameof(AlRunner.BcRuntime.ReturnStandalone_0Args));
                    Console.Error.WriteLine("[Cecil] Rewrote ALDatabase.get_ALSerialNumber → STANDALONE sentinel");
                }

                // ALDatabase.ALSid(string) — AL's Sid(). BC's real body calls
                // NTAccount(userName).Translate(SecurityIdentifier), which on Linux throws
                // PlatformNotSupportedException out of the IdentityReference constructor
                // and surfaces to AL as NavUserNotFoundException naming the .NET platform
                // rather than answering the AL author's question. The runner's host has no
                // Windows identity store, so the correct answer for any account name is
                // BC's own not-mapped result — the empty string. Full faithfulness argument,
                // including why this is not the fabricated-SID anti-pattern and what no
                // service tier has been able to adjudicate, is on the helper.
                var alSidHelper = typeof(AlRunner.Patches.ALDatabasePatches).GetMethod(
                    nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_ALSidForAccountName),
                    BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "[Cecil] ALDatabasePatches.ALDatabase_ALSidForAccountName not found");
                foreach (var m in alDatabaseType.Methods.Where(x =>
                    x.Name == "ALSid" && x.Parameters.Count == 1 && x.HasBody))
                {
                    ReplaceBodyWithHelper(asm.MainModule, m, alSidHelper);
                    Console.Error.WriteLine("[Cecil] Rewrote ALDatabase.ALSid(string) → host has no Windows identity store");
                }
            }
        }

        // ALTaskScheduler — background-job scheduling (scope.md §3.6).
        //
        // ALCreateTaskAsync is LEFT UNMODIFIED on purpose: its real BC body already
        // throws BC's own NavCreateScheduledTasksNotAllowedException when
        // CanCreateTask(session) is false (which we make so below). Letting the real
        // body run is the faithful behaviour — guarded AL (`if CanCreateTask then …`)
        // skips creation cleanly; unguarded AL that calls CreateTask directly gets
        // BC's own loud "scheduled tasks not allowed" exception instead of a silent
        // Guid.Empty. (Earlier this method was rewritten to return Guid.Empty to satisfy
        // an archived bucket-1 test; that was a silent fake suppressing BC's guard.)
        {
            var alTaskSchedulerType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALTaskScheduler");
            if (alTaskSchedulerType != null)
            {

                // ALTaskScheduler.ALCanCreateTask + private CanCreateTask — both access
                // session.Authenticator which NREs on the skeleton.  The runner has no real
                // task scheduler, so the faithful return value is false (no tasks can be
                // created).  InsertJobQueueData in WorkflowSetup.InitWorkflow gates its
                // job-queue row insertion on this, so returning false makes it skip the
                // Insert cleanly rather than NRE-ing through the authenticator.
                // Both methods return plain bool — ldc.i4.0; ret is all we need.
                // No new typeRefs/memberRefs added (avoids R2R token-shift risk).
                foreach (var m in alTaskSchedulerType.Methods
                    .Where(x => (x.Name == "ALCanCreateTask" || x.Name == "CanCreateTask")
                                && x.ReturnType.FullName == "System.Boolean"
                                && x.HasBody))
                {
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var ilc = body.GetILProcessor();
                    ilc.Append(ilc.Create(OpCodes.Ldc_I4_0));   // false
                    ilc.Append(ilc.Create(OpCodes.Ret));
                    body.MaxStackSize = 1;
                    Console.Error.WriteLine($"[Cecil] Rewrote ALTaskScheduler.{m.Name} → return false");
                }

                // ALTaskScheduler.CheckCodeUnit(NavSession, int) — the ALCreateTaskAsync
                // state machine calls this TWICE (codeunitId, then failureCodeunitId) BEFORE
                // it ever reaches the CanCreateTask gate above (#1733). Its real body calls
                // NCLMetadata.GetMetaCodeunitById, which does not know about a freshly
                // compiled test bundle's own codeunits, and throws a codeunit-resolution
                // NavALException naming the calling test codeunit itself — CanCreateTask is
                // never reached, so the documented scope.md §3.6 contract (unguarded CreateTask
                // hits BC's own NavCreateScheduledTasksNotAllowedException) never manifests.
                // The runner resolves codeunits via assembly-scan elsewhere (CreateTarget), so
                // this metadata check is redundant here; no-op lets execution fall through to
                // the CanCreateTask gate. A JmpHook no-op for this used to live in BcRuntime.cs
                // but JmpHook is off by default (Cecil-only) — that registration was silently
                // dead, which is exactly how this bug presented.
                foreach (var m in alTaskSchedulerType.Methods
                    .Where(x => x.Name == "CheckCodeUnit"
                                && x.ReturnType.FullName == "System.Void"
                                && x.HasBody))
                {
                    var body = m.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    var ilc = body.GetILProcessor();
                    ilc.Append(ilc.Create(OpCodes.Ret));
                    body.MaxStackSize = 0;
                    Console.Error.WriteLine($"[Cecil] Rewrote ALTaskScheduler.{m.Name} → no-op");
                }

                // ALTaskExistsAsync / ALCancelTaskAsync — the two members whose real bodies
                // have NO CanCreateTask guard and go straight to the scheduled-task store
                // (#2866). Measured on BC 28.1 before this rewrite: TaskExists died with
                // NullReferenceException in NavSqlConnectionScope.TryOpenConnection (reached
                // via NavTaskScheduler.SqlDml.RetrySqlAsync), CancelTask died with one inside
                // ALCancelTaskAsync itself. Neither names an API or cites a doc, which is the
                // silent-ish failure loud-failures.md exists to stop — so both refuse by name
                // against docs/scope.md#jobs instead. See AlRunner/Patches/TaskSchedulerPatches.cs.
                //
                // The *Async overloads are the choke points: the sync ALTaskExists(Guid) /
                // ALCancelTask(Guid) wrappers are `…Async(NavCurrentThread.Session, task)
                // .AsTask().GetAwaiter().GetResult()`, so rewriting the async pair covers both
                // AL call shapes and leaves one place to maintain.
                //
                // The replacements stand in for the WHOLE async body and carry its
                // ValueTask<bool> return type, so ALCancelTaskAsync can keep BC's one
                // pre-scheduler answer — `if (task == Guid.Empty) return false;`, the first
                // line of its real body — instead of refusing an id BC settles without any
                // scheduler at all. ALTaskExistsAsync has no such line and refuses every id.
                //
                // NOT touched here, deliberately: ALCanCreateTask / CanCreateTask (must keep
                // answering false — the documented guard is built on them), ALCreateTaskAsync
                // and ALSetTaskReadyAsync (their real bodies already raise BC's own
                // NavCreateScheduledTasksNotAllowedException through that guard, which #1739
                // decided to keep and tests/expectations/divergence-session.json classifies;
                // ALSetTaskReadyAsync also keeps its own identical empty-id short-circuit,
                // which is why leaving it alone and special-casing cancel agree rather than
                // conflict).
                foreach (var (name, helperName) in new[]
                {
                    ("ALTaskExistsAsync", nameof(AlRunner.Patches.TaskSchedulerPatches.ALTaskExistsAsync_Replacement)),
                    ("ALCancelTaskAsync", nameof(AlRunner.Patches.TaskSchedulerPatches.ALCancelTaskAsync_Replacement)),
                })
                {
                    var helperMi = typeof(AlRunner.Patches.TaskSchedulerPatches).GetMethod(
                        helperName, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException(
                            $"[Cecil] TaskSchedulerPatches.{helperName} not found");

                    int rewritten = 0;
                    foreach (var m in alTaskSchedulerType.Methods
                        .Where(x => x.Name == name && x.HasBody))
                    {
                        // Return type is load-bearing: ReplaceBodyWithHelper emits
                        // `ldarg…; call helper; ret`, so a helper whose return type stopped
                        // matching BC's would produce IL that only fails when the method is
                        // first JITted — long after the rewrite, with nothing pointing back
                        // here. Check it while we still have a useful message.
                        const string ExpectedRet = "System.Threading.Tasks.ValueTask`1<System.Boolean>";
                        if (m.ReturnType.FullName != ExpectedRet)
                            throw new InvalidOperationException(
                                $"[Cecil] ALTaskScheduler.{name} returns {m.ReturnType.FullName}, expected "
                                + $"{ExpectedRet}. BC's ALTaskScheduler shape has changed; see "
                                + "AlRunner/Patches/TaskSchedulerPatches.cs and issue #2866.");

                        ReplaceBodyWithHelper(asm.MainModule, m, helperMi);
                        rewritten++;
                    }

                    // Loud when the shape moves. "We could not find it" must never degrade
                    // back into the NRE this fix removed — that would read as a regression on
                    // a new BC build with nothing in the log to explain it.
                    if (rewritten == 1)
                        Console.Error.WriteLine(
                            $"[Cecil] Rewrote ALTaskScheduler.{name} → throw OOS (task-scheduler)");
                    else
                        throw new InvalidOperationException(
                            $"[Cecil] Expected exactly ONE ALTaskScheduler.{name} to rewrite as the "
                            + $"task-scheduler refusal, found {rewritten}. BC's ALTaskScheduler shape has "
                            + "changed; AL would otherwise hit a NullReferenceException out of BC's "
                            + "scheduler data layer again. See AlRunner/Patches/TaskSchedulerPatches.cs "
                            + "and issue #2866.");
                }
            }
        }

        // ALNavApp resource retrieval — NavApp.GetResource / GetResourceAsText are IN
        // SCOPE: the runner knows every loaded AL assembly's owning app and where its
        // resource bytes live (bundle source dir resourceFolders / .app "/resources/"
        // part). The real bodies NRE on the skeleton's null
        // CurrentMethodScope→…→OwningApp / NavAppMetadataRetriever chain (the exact
        // abort Pageworks's install trigger RegisterBaselineFonts hit), so:
        //   • the private GetPackagedResource(NavSession, string) — the single choke
        //     point every ALGetResource* overload awaits — is rewritten to
        //     NavAppResourcePatches.ALNavApp_GetPackagedResource, which serves a
        //     completed Task<Stream> over the owning app's resource bytes and throws
        //     BC's own NavNclResourceNotFoundException on a miss (faithful, incl. the
        //     corpus TestNavAppExtended missing-resource contract);
        //   • ALGetResourceAsTextAsync is rewritten too because its REAL body reads
        //     session.Tenant.DefaultEncoding before the resource fetch — null Tenant
        //     on the skeleton → NRE; the helper replicates the encoding switch
        //     skeleton-safely. RED→GREEN: tests/runner-extras/navapp-getresource.
        {
            var alNavAppType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALNavApp");
            if (alNavAppType != null)
            {
                var getPackaged = alNavAppType.Methods.FirstOrDefault(x =>
                    x.Name == "GetPackagedResource" && x.Parameters.Count == 2);
                if (getPackaged != null)
                {
                    var h = typeof(AlRunner.Patches.NavAppResourcePatches).GetMethod(
                        nameof(AlRunner.Patches.NavAppResourcePatches.ALNavApp_GetPackagedResource),
                        BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, getPackaged, h);
                    Console.Error.WriteLine("[Cecil] Rewrote ALNavApp.GetPackagedResource → NavAppResourcePatches (owning-app resource lookup)");
                }
                foreach (var m in alNavAppType.Methods.Where(x => x.Name == "ALGetResourceAsTextAsync"))
                {
                    if (!m.ReturnType.FullName.StartsWith("System.Threading.Tasks.Task`1<"))
                        continue;
                    var h = typeof(AlRunner.Patches.NavAppResourcePatches).GetMethod(
                        nameof(AlRunner.Patches.NavAppResourcePatches.ALNavApp_ALGetResourceAsTextAsync),
                        BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, m, h);
                    Console.Error.WriteLine($"[Cecil] Rewrote ALNavApp.{m.Name} → NavAppResourcePatches (skeleton-safe encoding + resource lookup)");
                }
            }
        }

        // ── ALNavApp.ALGetCurrentModuleInfo / ALGetCallerModuleInfo ───────────────────
        // Both NRE on the skeleton (NavTenant.get_Database chain). Precompiled deps
        // (SystemApp, ISV DLLs) call the real Ncl.dll methods directly; source-compiled
        // deps are already safe via the BcAssembler polyfill redirect. Cecil-patch both
        // here so precompiled facades like CopilotCapability.RegisterCapability return
        // the correct module identity from the stack-walk registry.
        // RED→GREEN: Pageworks Codeunit50364 tests 2-7 ("Capability has already been registered").
        {
            var alNavAppModuleType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALNavApp");
            if (alNavAppModuleType != null)
            {
                var mCurrent = alNavAppModuleType.Methods.FirstOrDefault(x =>
                    x.Name == "ALGetCurrentModuleInfo" && x.Parameters.Count == 2 && x.IsStatic);
                if (mCurrent != null)
                {
                    var h = typeof(AlRunner.Patches.NavAppModuleInfoPatches).GetMethod(
                        nameof(AlRunner.Patches.NavAppModuleInfoPatches.ALNavApp_GetCurrentModuleInfo),
                        BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mCurrent, h);
                }

                // The BY-ID overload. Unpatched it raises "No installed extension was found
                // with ID '<guid>'" for EVERY id, because BC resolves it against an app group
                // the runner does not have — so NavApp.VersionInstalled answered for nothing,
                // including the System Application's own id (#2961). The helper answers from
                // the loaded-app closure and keeps BC's not-found arms intact.
                var mById = alNavAppModuleType.Methods.FirstOrDefault(x =>
                    x.Name == "ALGetModuleInfo" && x.Parameters.Count == 3 && x.IsStatic);
                if (mById != null)
                {
                    var h = typeof(AlRunner.Patches.NavAppModuleInfoPatches).GetMethod(
                        nameof(AlRunner.Patches.NavAppModuleInfoPatches.ALNavApp_GetModuleInfo),
                        BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mById, h);
                }

                var mCaller = alNavAppModuleType.Methods.FirstOrDefault(x =>
                    x.Name == "ALGetCallerModuleInfo" && x.Parameters.Count == 2 && x.IsStatic);
                if (mCaller != null)
                {
                    var h = typeof(AlRunner.Patches.NavAppModuleInfoPatches).GetMethod(
                        nameof(AlRunner.Patches.NavAppModuleInfoPatches.ALNavApp_GetCallerModuleInfo),
                        BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mCaller, h);
                }
            }
        }

        // ── ALSession.ALStartSessionAsyncImpl → BcRuntime.ALSession_ALStartSessionAsyncImpl ──
        //
        // The single seam every ALStartSession / ALStartSessionAsync overload in Ncl forwards
        // into. Source-compiled AL already reaches the runner's StartSession model through
        // BcAssembler's polyfill redirect; precompiled AL (Base App, System App, ISV DLLs)
        // called Ncl's real body, which opens a second NavSession and asks SQL for the
        // database version — "Value cannot be null. (Parameter 'database')" on the skeleton.
        // Patching the impl rather than the seven public overloads keeps ONE model of
        // StartSession, so a precompiled and a source-compiled caller cannot diverge.
        // See BcRuntime.ALSession_ALStartSessionAsyncImpl for the measurement (#2960).
        {
            var alSessionType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.ALSession");
            if (alSessionType != null)
            {
                var mImpl = alSessionType.Methods.FirstOrDefault(x =>
                    x.Name == "ALStartSessionAsyncImpl" && x.Parameters.Count == 8 && x.IsStatic);
                if (mImpl != null)
                {
                    var h = typeof(AlRunner.BcRuntime).GetMethod(
                        nameof(AlRunner.BcRuntime.ALSession_ALStartSessionAsyncImpl),
                        BindingFlags.Public | BindingFlags.Static)!;
                    ReplaceBodyWithHelper(asm.MainModule, mImpl, h);
                    Console.Error.WriteLine("[Cecil] Rewrote ALSession.ALStartSessionAsyncImpl → BcRuntime.AlRunnerStartSession (inline-synchronous session model, precompiled callers included)");
                }
            }
        }

        // ── NavDotNet.CreateNavServerHandle: route object creation through the shims ──
        //
        // Redirects the single `call NavAutomationHelper.CreateDotNetObject` inside
        // CreateNavServerHandle to DotNetInteropShims.CreateDotNetObject, which has the
        // IDENTICAL signature (string, string, object[]) → object, so the call site's stack
        // shape is untouched. Everything it does not handle is forwarded to BC's original
        // method, exceptions and all — this intercepts, it does not reimplement.
        //
        // Needed because the type that actually has to be substituted
        // (System.Security.Principal.SecurityIdentifier, which throws
        // PlatformNotSupportedException for its pure-string-parsing constructor on Linux)
        // is created inside NavAutomationHelper in Types.dll, and the Cecil pass rewrites
        // only Ncl.dll. The CALL to it lives in Ncl, so the call site is the seam.
        {
            var navDotNetTypeForShim = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavDotNet");
            var mHandle = navDotNetTypeForShim?.Methods.FirstOrDefault(x =>
                x.Name == "CreateNavServerHandle" && x.Parameters.Count == 9);
            if (mHandle?.Body != null)
            {
                var calls = mHandle.Body.Instructions
                    .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                && i.Operand is MethodReference mr && mr.Name == "CreateDotNetObject")
                    .ToList();
                if (calls.Count != 1)
                    throw new InvalidOperationException(
                        $"NavDotNet.CreateNavServerHandle: expected 1 CreateDotNetObject call, found " +
                        $"{calls.Count} — Ncl shape changed; do not commit");
                var shimMi = typeof(AlRunner.Patches.DotNetInteropShims).GetMethod(
                        nameof(AlRunner.Patches.DotNetInteropShims.CreateDotNetObject),
                        BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "DotNetInteropShims.CreateDotNetObject not found — do not commit");
                calls[0].OpCode = OpCodes.Call;
                calls[0].Operand = asm.MainModule.ImportReference(shimMi);
                Console.Error.WriteLine(
                    "[Cecil] NavDotNet.CreateNavServerHandle → DotNetInteropShims.CreateDotNetObject");
            }
        }

        // ── NavDotNet.CreateNavServerHandle catch block → OOS ────────────────────────
        // The try block (NavAutomationHelper.CreateDotNetObject — succeeds for
        // in-process types like MemoryStream, crypto) is UNTOUCHED. Only the catch
        // block (NavNCLDotNetCreateException → add-in table fallback that NREs on
        // NavGlobal.SystemTenant = null on the runner skeleton) is replaced with a call
        // to ThrowServerInteropOOS, making absent-assembly accesses loud-and-named.
        {
            var navDotNetType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavDotNet");
            var mCsnh = navDotNetType?.Methods.FirstOrDefault(x =>
                x.Name == "CreateNavServerHandle" && x.Parameters.Count == 9);
            var asmField = navDotNetType?.Fields.FirstOrDefault(f => f.Name == "assemblyFullName");
            var ehCatch = mCsnh?.Body.ExceptionHandlers.FirstOrDefault(
                h => h.HandlerType == ExceptionHandlerType.Catch);

            if (mCsnh != null && asmField != null && ehCatch != null)
            {
                var helperMi = typeof(AlRunner.Patches.NavDotNetPatches).GetMethod(
                    nameof(AlRunner.Patches.NavDotNetPatches.ThrowServerInteropOOS),
                    BindingFlags.Public | BindingFlags.Static)!;
                var helperRef = asm.MainModule.ImportReference(helperMi);
                var il = mCsnh.Body.GetILProcessor();

                // Collect all catch-block instructions (HandlerStart inclusive to HandlerEnd exclusive).
                var toRemove = new List<Instruction>();
                for (var cur = ehCatch.HandlerStart; cur != ehCatch.HandlerEnd && cur != null; cur = cur.Next)
                    toRemove.Add(cur);

                // Replacement body: pop caught exception, load assemblyFullName, call helper,
                // throw (dead code — ThrowServerInteropOOS always throws; the throw opcode is
                // required to make IL valid: puts an Exception-typed value on the stack).
                var i0 = il.Create(OpCodes.Pop);       // discard NavNCLDotNetCreateException
                var i1 = il.Create(OpCodes.Ldarg_0);   // this (NavDotNet)
                var i2 = il.Create(OpCodes.Ldfld, asmField);
                var i3 = il.Create(OpCodes.Call, helperRef);   // → Exception (always throws)
                var i4 = il.Create(OpCodes.Throw);     // dead code; makes IL verifier happy

                // Insert before the first original instruction (so they'll precede it in the list).
                il.InsertBefore(toRemove[0], i0);
                il.InsertBefore(toRemove[0], i1);
                il.InsertBefore(toRemove[0], i2);
                il.InsertBefore(toRemove[0], i3);
                il.InsertBefore(toRemove[0], i4);

                // Redirect exception handler: TryEnd and HandlerStart both originally
                // pointed to the old pop (IL_007A). Update both to our new i0.
                ehCatch.TryEnd = i0;
                ehCatch.HandlerStart = i0;

                // Remove original catch block instructions.
                foreach (var instr in toRemove)
                    il.Remove(instr);

                Console.Error.WriteLine("[Cecil] Patched NavDotNet.CreateNavServerHandle catch block → ThrowServerInteropOOS");
            }
        }

        // ── NavDotNet.CreateDotNet catch-all → rethrow RunnerOutOfScopeException ─────
        // CreateDotNet has a catch (Exception) block that runs diagnostics then wraps
        // any non-NavBaseException in a fresh NavNCLDotNetCreateException (trappable).
        // Our RunnerOutOfScopeException (plain System.Exception) would be swallowed by
        // this: it IS caught, it is NOT a NavBaseException → gets wrapped and then
        // silently caught by TryInvokeAsync (returning false with no OOS signal).
        // Surgical fix: insert "ldloc V_5; call RethrowIfRunnerOOS" at the very START
        // of the catch block (right after the stloc that saves the caught exception into
        // V_5) so OOS propagates before the wrapping code runs.  For every other
        // exception the no-op helper returns and the original block continues normally.
        {
            var navDotNetType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavDotNet");
            var mCd = navDotNetType?.Methods.FirstOrDefault(x =>
                x.Name == "CreateDotNet" && x.Parameters.Count == 1);
            var ehAll = mCd?.Body.ExceptionHandlers.FirstOrDefault(
                h => h.HandlerType == ExceptionHandlerType.Catch
                     && h.CatchType?.FullName == "System.Exception");

            if (mCd != null && ehAll != null)
            {
                var rethrowMi = typeof(AlRunner.Patches.NavDotNetPatches).GetMethod(
                    nameof(AlRunner.Patches.NavDotNetPatches.RethrowIfRunnerOOS),
                    BindingFlags.Public | BindingFlags.Static)!;
                var rethrowRef = asm.MainModule.ImportReference(rethrowMi);
                var il = mCd.Body.GetILProcessor();

                // HandlerStart is "stloc.s V_5". We insert AFTER it so the variable
                // is populated before we load it.
                var stlocInstr = ehAll.HandlerStart; // stloc.s V_5
                var localVar = (VariableDefinition)stlocInstr.Operand;

                var ldloc = il.Create(OpCodes.Ldloc_S, localVar);
                var call  = il.Create(OpCodes.Call, rethrowRef);
                il.InsertAfter(stlocInstr, ldloc);
                il.InsertAfter(ldloc, call);

                Console.Error.WriteLine("[Cecil] Patched NavDotNet.CreateDotNet catch-all → RethrowIfRunnerOOS guard");
            }
        }

        // ALNumberSequence — runner-emitted AL calls the synchronous entry points, while
        // precompiled Microsoft apps call the async entry points directly. Rewrite both
        // public surfaces so neither can reach SQL and both observe the same store.
        {
            const string sequenceTypeName = "Microsoft.Dynamics.Nav.Runtime.ALNumberSequence";
            var sequenceType = asm.MainModule.GetType(sequenceTypeName)
                ?? throw new InvalidOperationException(
                    $"[Cecil] type {sequenceTypeName} not found — Ncl shape changed; do not commit (#2049)");
            var patchType = typeof(AlRunner.Patches.NumberSequencePatches);

            void RewriteSequence(
                string methodName,
                string returnType,
                Type[] helperParameterTypes,
                params string[] nclParameterTypes)
            {
                var target = ResolveNumberSequenceEntryPoint(
                    sequenceType, methodName, returnType, nclParameterTypes);
                var helper = patchType.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: helperParameterTypes,
                    modifiers: null)
                    ?? throw new InvalidOperationException(
                        $"[Cecil] helper {patchType.Name}.{methodName}({string.Join(", ", helperParameterTypes.Select(type => type.Name))}) not found");
                var helperReturnType = asm.MainModule.ImportReference(helper.ReturnType).FullName;
                var helperParameters = helper.GetParameters()
                    .Select(parameter => asm.MainModule.ImportReference(parameter.ParameterType).FullName)
                    .ToArray();
                if (helperReturnType != returnType ||
                    !helperParameters.SequenceEqual(nclParameterTypes, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"[Cecil] helper {helper} does not exactly match {target.FullName}");
                var asyncAttribute = target.CustomAttributes.FirstOrDefault(attribute =>
                    attribute.AttributeType.Name == "AsyncStateMachineAttribute");
                if (asyncAttribute != null)
                    target.CustomAttributes.Remove(asyncAttribute);
                ReplaceBodyWithHelper(asm.MainModule, target, helper);
            }

            RewriteSequence("ALInsert", "System.Void",
                new[] { typeof(string), typeof(long), typeof(long), typeof(bool) },
                "System.String", "System.Int64", "System.Int64", "System.Boolean");
            RewriteSequence("ALRestart", "System.Void",
                new[] { typeof(string), typeof(long), typeof(bool) },
                "System.String", "System.Int64", "System.Boolean");
            RewriteSequence("ALExists", "System.Boolean",
                new[] { typeof(string), typeof(bool) },
                "System.String", "System.Boolean");
            RewriteSequence("ALDelete", "System.Void",
                new[] { typeof(string), typeof(bool) },
                "System.String", "System.Boolean");
            RewriteSequence("ALNext", "System.Int64",
                new[] { typeof(string), typeof(bool) },
                "System.String", "System.Boolean");
            RewriteSequence("ALCurrent", "System.Int64",
                new[] { typeof(string), typeof(bool) },
                "System.String", "System.Boolean");
            RewriteSequence("ALRange", "System.Int64",
                new[] { typeof(string), typeof(int), typeof(bool) },
                "System.String", "System.Int32", "System.Boolean");
            RewriteSequence("ALRange", "System.Int64",
                new[] { typeof(string), typeof(int), typeof(Microsoft.Dynamics.Nav.Runtime.ByRef<long>), typeof(bool) },
                "System.String", "System.Int32",
                "Microsoft.Dynamics.Nav.Runtime.ByRef`1<System.Int64>", "System.Boolean");

            RewriteSequence("ALInsertAsync", "System.Threading.Tasks.ValueTask",
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(string), typeof(long), typeof(long), typeof(bool) },
                "Microsoft.Dynamics.Nav.Runtime.NavSession", "System.String", "System.Int64", "System.Int64", "System.Boolean");
            RewriteSequence("ALRestartAsync", "System.Threading.Tasks.ValueTask",
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(string), typeof(long), typeof(bool) },
                "Microsoft.Dynamics.Nav.Runtime.NavSession", "System.String", "System.Int64", "System.Boolean");
            RewriteSequence("ALExistsAsync", "System.Threading.Tasks.ValueTask`1<System.Boolean>",
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(string), typeof(bool) },
                "Microsoft.Dynamics.Nav.Runtime.NavSession", "System.String", "System.Boolean");
            RewriteSequence("ALDeleteAsync", "System.Threading.Tasks.ValueTask",
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(string), typeof(bool) },
                "Microsoft.Dynamics.Nav.Runtime.NavSession", "System.String", "System.Boolean");
            RewriteSequence("ALNextAsync", "System.Threading.Tasks.ValueTask`1<System.Int64>",
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(string), typeof(bool) },
                "Microsoft.Dynamics.Nav.Runtime.NavSession", "System.String", "System.Boolean");
            RewriteSequence("ALCurrentAsync", "System.Threading.Tasks.ValueTask`1<System.Int64>",
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(string), typeof(bool) },
                "Microsoft.Dynamics.Nav.Runtime.NavSession", "System.String", "System.Boolean");
            RewriteSequence("ALRangeAsync", "System.Threading.Tasks.ValueTask`1<System.Int64>",
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(string), typeof(int), typeof(bool) },
                "Microsoft.Dynamics.Nav.Runtime.NavSession", "System.String", "System.Int32", "System.Boolean");
            RewriteSequence("ALRangeAsync", "System.Threading.Tasks.ValueTask`1<System.Int64>",
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.NavSession), typeof(string), typeof(int), typeof(Microsoft.Dynamics.Nav.Runtime.ByRef<long>), typeof(bool) },
                "Microsoft.Dynamics.Nav.Runtime.NavSession", "System.String", "System.Int32",
                "Microsoft.Dynamics.Nav.Runtime.ByRef`1<System.Int64>", "System.Boolean");
            Console.Error.WriteLine(
                "[Cecil] Rewrote ALNumberSequence sync+async {Insert,Restart,Exists,Delete,Next,Current,Range×2} → in-memory store");
        }

        // IsolatedStorageRepository — Cecil migration of the TenantStoragePatches
        // lowest layer. The AL-facing ALIsolatedStorage bodies delegate here, and AL
        // output also lands here directly for Contains/Delete. The real bodies open
        // tenant-scoped NavRecord 2000000107 via NavCurrentThread.Session state that
        // the skeleton lacks → NRE (the exact crash SPBLIC's Extension Setup
        // SetAppValue hit inside Pageworks's OnInstallAppPerDatabase). The legacy
        // JmpHook replacements in TenantStoragePatches never install under the
        // Cecil-only default, so rewrite the five statics onto those SAME in-memory
        // helpers (scope-honouring store, AES for encrypted entries) and let every
        // higher ALIsolatedStorage entry run its REAL body into them.
        // Keys registered in CecilOwned so the legacy Hook(...) installs auto-no-op.
        // RED→GREEN: tests/runner-extras/isolated-storage.

    }

    private static void AddDispatchOwned(HashSet<string> set)
    {
        // AL NumberSequence's public synchronous entry points. Their real bodies
        // delegate to SQL-backed async methods and NRE on the standalone skeleton.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALInsert/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALRestart/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALExists/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALDelete/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALNext/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALCurrent/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALRange/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALRange/4");
        // Precompiled Microsoft apps call these async entry points directly instead of
        // passing through the synchronous AL wrappers, so both surfaces must share state.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALInsertAsync/5");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALRestartAsync/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALExistsAsync/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALDeleteAsync/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALNextAsync/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALCurrentAsync/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALRangeAsync/4");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::ALRangeAsync/5");
        // NavMethodScope cluster (migrated in Batch 2; registering now so their
        // JmpHooks — if any still install — become no-ops under the registry).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMethodScope::.ctor/3");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMethodScope::Dispose/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMethodScope::AssertError/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMethodScope::ProcessException/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALMethodScope::AssignScopeId/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMethodScope::ThrowStackOverflow/1");
        // StmtHit/CStmtHit — --coverage hook (issue #1922). Not previously JmpHook'd
        // (grep for StmtHit across AlRunner/ was empty before this), listed for symmetry
        // with the rest of the NavMethodScope cluster and to guard against a future
        // JmpHook targeting them by name. CStmtHit is the inline-expression form BC uses
        // for if/while/repeat CONDITIONS (`if (CStmtHit(1) & (this.flag))`) — confirmed
        // by decompiling generated C# for a scratch if/else fixture; without hooking it
        // too, every conditional's own line would read permanently 0 regardless of
        // whether it ran.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMethodScope::StmtHit/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMethodScope::CStmtHit/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavMethodScope::CStmtHit/2");
        // NavHttpClient/NavHttpResponseMessageBase/NavHttpRequestMessage.get_Target — same
        // skeleton-parented delegation shape as NavRecordRef/NavStream.get_Target above (see
        // RewriteNcl, "NavHttpClient egress" block). NavHttpClient and NavHttpResponseMessageBase
        // were already Cecil-rewritten there but missing from this list (#1883 follow-up) — their
        // BcRuntime.cs JmpHook registrations were kept as defense-in-depth (same precedent as
        // NavXmlPort::Run below) but the audit misclassified them as "orphaned" instead of
        // "redundant" for want of this key. NavHttpRequestMessage.get_Target was a genuine gap
        // (no Cecil rewrite existed at all) — added alongside this key in the same commit.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavHttpClient::get_Target/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavHttpResponseMessageBase::get_Target/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavHttpRequestMessage::get_Target/0");
        // NavDotNet.CreateNavServerHandle — catch block replaced with OOS throw so
        // absent server add-in assemblies (Azure KV SDK, etc.) fail loud instead of
        // NRE-ing on NavGlobal.SystemTenant (null on skeleton). Happy-path try block
        // (NavAutomationHelper.CreateDotNetObject for in-process types) is unchanged.
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavDotNet::CreateNavServerHandle/9");
        // NavDotNet.CreateDotNet — surgical RethrowIfRunnerOOS guard inserted at the
        // start of the catch-all block so RunnerOutOfScopeException (thrown by the
        // patched CreateNavServerHandle) propagates out instead of being wrapped in
        // NavNCLDotNetCreateException (which is trappable and would be silently swallowed
        // by TryInvokeAsync → TryInitializeFromCurrentApp returns false with no OOS signal).
        set.Add("Microsoft.Dynamics.Nav.Runtime.NavDotNet::CreateDotNet/1");
        // ALTaskScheduler cluster (scope.md §3.6, #1733) — CanCreateTask/ALCanCreateTask
        // rewritten to return false (no scheduler headlessly) and CheckCodeUnit no-op'd so
        // ALCreateTaskAsync's real body reaches that CanCreateTask gate instead of throwing
        // a codeunit-resolution error first. See the RewriteNcl block below for detail.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALTaskScheduler::ALCanCreateTask/0");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALTaskScheduler::ALCanCreateTask/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALTaskScheduler::CanCreateTask/1");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALTaskScheduler::CheckCodeUnit/2");
        // …and the two members with no CanCreateTask guard of their own (#2866), rewritten
        // to throw the task-scheduler out-of-scope refusal instead of NRE-ing out of BC's
        // scheduler data layer. The sync ALTaskExists/ALCancelTask wrappers are not listed
        // because they are not rewritten — they funnel into these.
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALTaskScheduler::ALTaskExistsAsync/2");
        set.Add("Microsoft.Dynamics.Nav.Runtime.ALTaskScheduler::ALCancelTaskAsync/2");
    }

}
