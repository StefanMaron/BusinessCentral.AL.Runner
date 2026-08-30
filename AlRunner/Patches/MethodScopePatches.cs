// MethodScopePatches — replacements for NavMethodScope ctor + AssertError + ProcessException.
//
// NavMethodScope is the per-AL-frame execution unit. The real ctor body dereferences
// many session/scope properties that NRE on a skeleton; we replace it with a minimal
// version that only sets the fields the test harness actually depends on.
//
// AssertError mediates `asserterror` blocks. The real implementation rolls back the
// session transaction, which NREs on the skeleton; we replicate the pass/fail semantics
// without touching the (non-existent) transaction layer.
//
// Recursion guard: a [ThreadStatic] depth counter is incremented in the ctor and
// decremented in the Dispose(bool) hook. When depth exceeds MaxRecursionDepth,
// NavNCLDialogException is thrown so AL `asserterror` can trap it.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner;

public static partial class BcRuntime
{
    [ThreadStatic] private static int _navMethodScopeDepth;
    private const int MaxRecursionDepth = 500;
    /// <summary>
    /// Full replacement for NavMethodScope..ctor(NavApplicationObjectBase, MethodScopeFlags, bool).
    ///
    /// When a JMP-hook replaces a constructor, the ENTIRE ctor is replaced — including the
    /// base-chain call (: base(...)). That means TreeObject..ctor and NavScope..ctor do NOT run,
    /// so we must set up every field that any of those base ctors would have initialised.
    ///
    /// Fields initialised (all via FieldPoke to bypass readonly/private restrictions):
    ///
    ///   TreeObject.tree              — new child TreeHandler under _skeletonRootScope; sets up
    ///                                  the parent-child link in the tree so Dispose bookkeeping works
    ///   NavMethodScope.session       — _skeletonSession
    ///   NavMethodScope.parentScope   — the actual current scope at ctor entry (NOT always root),
    ///                                  so NavMethodScope_Dispose can restore CurrentMethodScope correctly
    ///   NavMethodScope.flags         — GetMethodScopeFlags() on the concrete subtype
    ///   NavMethodScope.StackDepth    — 2 (root=1, one level deeper)
    ///   NavMethodScope.TopLevelApplicationObject — applicationObject
    ///   NavSession.CurrentMethodScope (backing field) — self
    ///
    /// Recursion guard: increments _navMethodScopeDepth and throws NavNCLDialogException if
    /// MaxRecursionDepth is exceeded, so AL `asserterror` can trap recursive-trigger loops.
    ///
    /// TreeHandler.isDisposing is left false (default); all other TreeObject/NavMethodScope
    /// fields default to null/0/false which is safe for the thin test-harness usage.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavMethodScopeCtorReplacement(
        Microsoft.Dynamics.Nav.Runtime.NavMethodScope self,
        Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase applicationObject,
        object flags,   // MethodScopeFlags — superseded by GetMethodScopeFlags()
        bool eventSource)
    {
        // Capture the actual current scope (our parent) BEFORE we update CurrentMethodScope.
        object? actualParent = _fSessCurrentScope != null && _skeletonSession != null
            ? _fSessCurrentScope.GetValue(_skeletonSession)
            : null;
        actualParent ??= _skeletonRootScope;

        // Recursion guard: increment depth and throw if the limit is exceeded.
        // Decrement before throwing to keep the counter balanced.
        _navMethodScopeDepth++;
        if (_navMethodScopeDepth > MaxRecursionDepth)
        {
            _navMethodScopeDepth--;
            var msg = $"Maximum recursion depth ({MaxRecursionDepth}) exceeded";
            throw _navNCLDialogExceptionType != null
                ? (Exception)Activator.CreateInstance(_navNCLDialogExceptionType, msg)!
                : new InvalidOperationException(msg);
        }

        try
        {
            // 1. TreeObject.tree — CreateTreeHandler links self as a child of _skeletonRootScope.
            //    This is the equivalent of base(applicationObject.Session.CurrentMethodScope)
            //    → TreeObject..ctor(_skeletonRootScope) → tree = CreateTreeHandler(_skeletonRootScope, self).
            if (_mCreateTreeHandler != null && _fTreeObjTree != null && _skeletonRootScope != null)
            {
                var handler = _mCreateTreeHandler.Invoke(null, new object[] { _skeletonRootScope, self });
                FieldPoke.SetInstance(_fTreeObjTree, self, handler);
            }
            // 2. NavMethodScope.session
            if (_fMsSession != null)     FieldPoke.SetInstance(_fMsSession,     self, _skeletonSession);
            // 3. NavMethodScope.parentScope = actual parent scope at entry (enables correct
            //    CurrentMethodScope restoration in NavMethodScope_Dispose).
            if (_fMsParentScope != null) FieldPoke.SetInstance(_fMsParentScope, self, actualParent);
            // 4. NavMethodScope.flags — resolve via virtual GetMethodScopeFlags() on the concrete subtype.
            //    NavMethodScope<T> → IsStackFrame; TryMethodScope → IsInTryScope; etc.
            if (_fMsFlags != null)
            {
                try
                {
                    var getFlags = self.GetType().GetMethod("GetMethodScopeFlags",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var scopeFlags = getFlags != null ? getFlags.Invoke(self, null) : null;
                    FieldPoke.SetInstance(_fMsFlags, self, scopeFlags ?? Enum.ToObject(_fMsFlags.FieldType, 0));
                }
                catch { /* leave flags at default 0 on reflection error */ }
            }
            // 5. NavMethodScope.StackDepth = 2 (_skeletonRootScope.StackDepth=1)
            if (_fMsStackDepth != null)  FieldPoke.SetInstance(_fMsStackDepth,  self, 2);
            // 6. NavMethodScope.TopLevelApplicationObject = applicationObject
            if (_fMsTopLevelAppObj != null) FieldPoke.SetInstance(_fMsTopLevelAppObj, self, applicationObject);
            // 7. NavSession.CurrentMethodScope backing field = self  (mirrors real ctor's session.CurrentMethodScope = this)
            if (_fSessCurrentScope != null && _skeletonSession != null)
                FieldPoke.SetInstance(_fSessCurrentScope, _skeletonSession, self);
            // cancellationToken, sqlStatisticsAvailable, globalSql*AtStart all left at default
            // (zero-value structs / false) — safe for the test harness since no SQL paths run.
        }
        catch
        {
            // Unexpected failure during initialization: keep counter balanced.
            _navMethodScopeDepth--;
            throw;
        }
    }

    /// <summary>
    /// Replacement for NavMethodScope.Dispose(bool disposing).
    ///
    /// JmpHook on the virtual override intercepts the virtual dispatch from TreeObject.Dispose()
    /// (which calls `this.Dispose(true)` via callvirt). Our replacement:
    ///   1. Decrements the ThreadStatic recursion depth counter.
    ///   2. Restores session.CurrentMethodScope to parentScope (the actual parent captured at
    ///      ctor entry), mirroring what the original Dispose(bool) body does.
    ///   3. Detaches the TreeHandler node the ctor replacement linked as a child of
    ///      _skeletonRootScope (see MEMORY LEAK FIX note below) so it stops being a permanent
    ///      GC root.
    ///
    /// MEMORY LEAK FIX (dominant per-test leak; see docs comment on
    /// RecordPatches.ResetPerTestState / BcRuntime.DisposeSkeletonSharedObjectContainerChildren
    /// for the analogous, already-fixed leak on the OTHER skeleton container):
    /// NavMethodScopeCtorReplacement above links every constructed method-scope object (i.e.
    /// EVERY AL procedure/method call — top-level, local procedure, event, everything) as a
    /// permanent TreeHandler child of the single process-wide static _skeletonRootScope via
    /// TreeHandler.CreateTreeHandler(_skeletonRootScope, self). Because this Dispose
    /// replacement never unlinked that child handler, the handler (and everything it
    /// transitively retains — the scope object itself, its locals, and for local-procedure
    /// scopes the compiler-emitted `<Method>_Scope__...` frame objects) was NEVER removed from
    /// _skeletonRootScope's child chain. That chain lives for the entire process lifetime, so
    /// memory grew by one retained node per AL method/procedure call — not just per test or
    /// per codeunit. Confirmed via gcdump: a single 960KB-input pure-AL decompression test
    /// (~870K calls to two byte-at-a-time local procedures) alone retained ~1.2GB of
    /// TreeObjectHandler + scope-frame + ByRef-wrapper objects even after a forced blocking
    /// Gen2 GC, all rooted through _skeletonRootScope's child chain. This is the dominant leak
    /// (much larger than the SharedObjectContainer one above, since it fires on every call
    /// rather than every record/stream/link access).
    ///
    /// Fix approach — DETACH ONLY, not full Dispose(): TreeHandler's public Dispose() cascades
    /// into DisposeAllChildren(), which calls IDisposable.Dispose() on every descendant
    /// hostObject. That is too aggressive here: some values created "inside" a call (e.g. a
    /// dedup'd/shared image stream promoted into a longer-lived cache by our own patches, or a
    /// BLOB written by a deeply-nested render call and read back by an outer caller) are still
    /// legitimately reachable after the creating scope returns, and forcing their Dispose() at
    /// scope-exit corrupted that data (confirmed empirically: calling the full Dispose() here
    /// turned 3 pre-existing PageworksImageTest failures into 5 — the two new failures were
    /// image/BLOB content going missing — "no image XObject stream to compare" / "0 image
    /// draws found" — exactly what premature disposal of shared render buffers would cause).
    /// Real BC's compiler-generated code guarantees no such value is left ONLY reachable via a
    /// disposing scope's tree position (handle-typed values crossing a scope boundary are
    /// copied/re-parented by the emitted IL); our runner's polyfills don't fully replicate that
    /// guarantee, so a hard cascading Dispose() is unsafe here.
    ///
    /// Instead we just unlink this scope's TreeHandler node from its parent's child chain
    /// (mirroring TreeHandler's private InternalRemoveChild, resolved via reflection against
    /// the abstract base type — see _fTreeHandler* fields in BcRuntime.cs) WITHOUT touching its
    /// own children or calling hostObject.Dispose(). This breaks the artificial GC root that
    /// _skeletonRootScope's chain represents; anything only reachable through the now-detached
    /// subtree becomes ordinary collectible garbage on the next GC, while anything still
    /// legitimately referenced elsewhere (e.g. via a persistent cache field) survives via normal
    /// GC reachability, exactly as it would if the tree link had simply never been kept.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavMethodScope_Dispose(object? self, bool disposing)
    {
        if (!disposing) return;

        _navMethodScopeDepth = Math.Max(0, _navMethodScopeDepth - 1);

        // Restore CurrentMethodScope to the scope's parent (captured at ctor entry in parentScope).
        if (_fSessCurrentScope != null && _skeletonSession != null && _fMsParentScope != null)
        {
            var msScope = self as Microsoft.Dynamics.Nav.Runtime.NavMethodScope;
            var parent = msScope != null ? _fMsParentScope.GetValue(msScope) : _skeletonRootScope;
            FieldPoke.SetInstance(_fSessCurrentScope, _skeletonSession, parent ?? _skeletonRootScope);
        }

        DetachTreeHandlerFromParent(self);
    }

    /// <summary>
    /// Unlinks the TreeHandler stored in self's TreeObject.tree field from its parent's
    /// child chain, without disposing hostObjects (see NavMethodScope_Dispose doc above).
    /// Mirrors TreeHandler.InternalRemoveChild's doubly-linked-list surgery.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DetachTreeHandlerFromParent(object? self)
    {
        if (self == null || _fTreeObjTree == null) return;
        if (_fTreeHandlerParent == null || _fTreeHandlerFirstChildBase == null ||
            _fTreeHandlerPrevSibling == null || _fTreeHandlerNextSiblingBase == null)
            return;

        var handler = _fTreeObjTree.GetValue(self);
        if (handler == null) return;

        var parentHandler = _fTreeHandlerParent.GetValue(handler);
        if (parentHandler == null) return; // already detached, or a root handler

        var prev = _fTreeHandlerPrevSibling.GetValue(handler);
        var next = _fTreeHandlerNextSiblingBase.GetValue(handler);

        if (next != null) _fTreeHandlerPrevSibling.SetValue(next, prev);
        if (prev != null)
        {
            _fTreeHandlerNextSiblingBase.SetValue(prev, next);
        }
        else
        {
            // We were the parent's firstChildHandler.
            var parentFirstChild = _fTreeHandlerFirstChildBase.GetValue(parentHandler);
            if (ReferenceEquals(parentFirstChild, handler))
                _fTreeHandlerFirstChildBase.SetValue(parentHandler, next);
        }

        // Clear this handler's own links so it doesn't keep the (now-detached) sibling
        // chain or parent reachable either, and so it can't be mistaken for still-attached.
        _fTreeHandlerPrevSibling.SetValue(handler, null);
        _fTreeHandlerNextSiblingBase.SetValue(handler, null);
        _fTreeHandlerParent.SetValue(handler, null);
    }

    /// <summary>
    /// Replacement for NavMethodScope.AssertError(Action body). The real method calls
    /// session.Rollback() in its catch path, which NREs on the skeleton session. We invert the
    /// pass/fail semantics in headless mode: if the body throws, the asserterror succeeded;
    /// if the body completes normally, throw NavNCLAssertErrorException so the test driver
    /// sees an asserterror failure.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavMethodScope_AssertError(object self, Action body)
    {
        // AlRunner#2142: track Insert() attempts made by `body` — the compiled form of the
        // one statement `asserterror` wraps — separately from any made by earlier,
        // already-returned statements, so ForceDurableFailedInserts below only ever forces
        // durable an Insert() this SAME statement made. See
        // RecordPatches.BeginAssertErrorScope's doc and the file header on
        // RecordPatches.TransactionSnapshot.cs.
        AlRunner.Patches.RecordPatches.BeginAssertErrorScope();
        try
        {
            NavMethodScope_AssertErrorCore(self, body);
        }
        finally
        {
            AlRunner.Patches.RecordPatches.EndAssertErrorScope();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NavMethodScope_AssertErrorCore(object self, Action body)
    {
        try { body(); }
        catch (Exception ex)
        {
            // Real BC's own AssertError body (decompiled) is:
            //
            //     try { body(); }
            //     catch (Exception ex) when (RemapToALExceptionAndThrow(ex, out mapped)) { throw mapped; }
            //     ...
            //     catch (NavBaseException) { session.Rollback(); return; }
            //
            // i.e. it ALWAYS passes the caught exception through the real, unmodified
            // NavMethodScope.RemapToALExceptionAndThrow(Exception, out NavALException) before
            // deciding pass/fail. For most exception types that only rewraps the same
            // `.Message` text (DivideByZeroException, IndexOutOfRangeException,
            // FormatException, OverflowException) so skipping it was harmless. But for
            // NavMetadataNotFoundException it produces a DIFFERENT message — "You tried to
            // invoke the {type} object with the ID {id} from the object {caller}. ..."
            // (Lang.ObjectNotFoundError) — naming the calling AL object, instead of the raw
            // "The metadata object {type} {id} was not found" NavMetadataNotFoundException
            // carries. A static Page.RunModal(0, Record) on a table with no LookupPageId hits
            // exactly this: real BC's error names the page id and caller, ours (without this
            // remap) leaked the generic metadata-lookup message instead.
            //
            // RemapToALExceptionAndThrow is real BC's own method body (not Cecil-rewritten,
            // not a runner reimplementation) — call it via reflection on the real `self` so we
            // reuse it exactly rather than re-deriving the message text ourselves. Best-effort:
            // if invoking it throws (e.g. some scope-internal state the skeleton never
            // populates), fall back to the original exception unchanged, matching this
            // method's prior behaviour.
            var effectiveEx = TryRemapToALException(self, ex) ?? ex;
            // Store the (possibly remapped) exception in skeleton session.lastException so
            // that ALSystemErrorHandling.get_ALGetLastErrorText (and the patched override
            // in MiscPatches) can return its message — Assert.ExpectedError / Library
            // Assert depend on this round-trip.
            StoreLastExceptionOnSkeletonSession(effectiveEx);
            // BC's own AssertError ends its catch with session.Rollback(): an AL error
            // unwinds the database to the last COMMIT. This replacement exists because the
            // real body's rollback path NREs on the skeleton session, not because the
            // rollback itself is out of scope — see RecordPatches.TransactionSnapshot.
            AlRunner.Patches.RecordPatches.RollbackToCommitPoint(_skeletonSession);
            // AFTER the rollback above (AlRunner#2142): an Insert() this SAME statement
            // attempted may not have actually landed in the table yet (OnInsert runs before
            // the physical write completes, in this runner AND in real BC's own decompiled
            // NavRecord.InsertAsync — see RecordPatches.TransactionSnapshot.cs's file header
            // for what that does and does not explain), so there's nothing for the rollback
            // to have undone — but real BC keeps the row anyway. Force it durable now, once
            // any Modify/Delete rollback for this table has already settled.
            AlRunner.Patches.RecordPatches.ForceDurableFailedInserts();
            return; /* asserterror passed: body threw something */
        }
        throw new Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLAssertErrorException();
    }

    private static System.Reflection.MethodInfo? _mRemapToALExceptionAndThrow;

    /// <summary>
    /// Best-effort invocation of the real, unmodified NavMethodScope.RemapToALExceptionAndThrow
    /// (internal bool RemapToALExceptionAndThrow(Exception, out NavALException)) on the actual
    /// scope instance. Returns the mapped exception real BC would have thrown, or null if the
    /// method declined to remap (returned false) or the reflective call itself failed.
    /// </summary>
    private static Exception? TryRemapToALException(object self, Exception ex)
    {
        try
        {
            _mRemapToALExceptionAndThrow ??= self.GetType().GetMethod(
                "RemapToALExceptionAndThrow",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_mRemapToALExceptionAndThrow == null) return null;

            var args = new object?[] { ex, null };
            var remapped = (bool)_mRemapToALExceptionAndThrow.Invoke(self, args)!;
            return remapped ? args[1] as Exception : null;
        }
        catch
        {
            // Reflective call failed (e.g. some scope-internal state the skeleton doesn't
            // populate for this scope type) — fall back to the original exception, exactly
            // this method's behaviour before this fix existed.
            return null;
        }
    }

    private static System.Reflection.FieldInfo? _fSessLastException;
    private static void StoreLastExceptionOnSkeletonSession(Exception ex)
    {
        if (_skeletonSession == null) return;
        if (_fSessLastException == null)
            _fSessLastException = _skeletonSession.GetType().GetField("lastException",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        _fSessLastException?.SetValue(_skeletonSession, ex);
    }

    /// <summary>
    /// Replacement for NavMethodScope.ProcessException(Exception).
    /// The real body calls session.Diagnostics.SendExceptionTag(...) when the exception is an NRE,
    /// but session.Diagnostics is null on the skeleton session → secondary NRE that masks the original.
    /// Returning false immediately means "exception not handled here" so the original exception
    /// propagates cleanly through Run()'s outer catch clauses.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavMethodScope_ProcessException(object? self, Exception? exception) => false;

    /// <summary>
    /// Replacement for ALMethodScope.AssignScopeId(). Real body chains through
    /// `Session.NCLMetadata.CodeEnvironment.AssignScopeId(this)` — NCLMetadata is null
    /// on the skeleton session and NREs. No-op: scopeId stays null and ALMethodScope's
    /// `ScopeId` getter tolerates that (`value.HasValue ? value.Value : 0`).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALMethodScope_AssignScopeId(object? self) { }
}
