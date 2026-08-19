// XmlPortPatches — replacements for NavXmlPortHandle.CreateTarget /
// NCLMetaXmlPort.CreateObjectInstance (construction) and NavXmlPort's static Run overloads.
//
// NavXmlPortHandle.CreateTarget normally calls
//   NavGlobal.NCLMetadata.GetMetaXmlPortById(id, true).CreateObjectInstance(this)
// which NREs because our skeleton NCLMetaXmlPort has no ApplicationObjectConstructor
// delegate. We bypass it by finding XmlPort{ID} in the loaded test assembly and
// constructing directly via reflection — same pattern as NavFormHandle/NavReportHandle.
//
// The NavXmlPort INSTANCE methods (Export, Import, Run, SetTableView,
// BeginInitialization/EndInitialization/Add) are NOT replaced here (or anywhere) — BC's real,
// unpatched bodies already handle well-formed AL usage correctly once construction succeeds
// (see the #1800 investigation below and tests/runner-extras/standalone-suites/
// xmlport-cluster-hooks-1800). Only the four STATIC Run(int[, bool[, bool[, NavRecord]]])
// overloads are replaced, and only because they are a genuine, permanent out-of-scope
// surface — see the block above NavXmlPort_StaticRun1..4 below.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner;

public static partial class BcRuntime
{
    private static readonly ConcurrentDictionary<int, Type?> _xmlPortTypeCache = new();

    // ──────────────────────────────────────────────────────────────────
    // NavXmlPortHandle.CreateTarget — bypass GetMetaXmlPortById +
    // CreateObjectInstance (which NREs on null delegate). Construct
    // XmlPort{ID} directly from the test assembly.
    // ──────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavXmlPortHandle_CreateTarget(object self)
    {
        var objIdProp = self.GetType().GetProperty("ObjectId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var objId = objIdProp!.GetValue(self)!;
        var idProp = objId.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        int id = (int)idProp!.GetValue(objId)!;

        return ConstructXmlPort(id, self);
    }

    /// <summary>
    /// Replacement for <c>NCLMetaXmlPort.CreateObjectInstance(ITreeObject)</c>.
    ///
    /// BC's body invokes <c>base.ApplicationObjectConstructor</c>, and the runner forces
    /// that delegate to null for every object type (see RecordPatches.CreateObjectInstance),
    /// substituting a per-type construction path instead. XmlPort had one only for the
    /// HANDLE path (<c>NavXmlPortHandle.CreateTarget</c>, above) — i.e. for an AL
    /// <c>XmlPort "Foo"</c> variable. The STATIC forms, <c>XmlPort.Import(id, …)</c> and
    /// <c>XmlPort.Export(id, …)</c>, reach the instance through
    /// <c>NCLMetaXmlPort.CreateObjectInstance</c> instead and so NREd on the null delegate.
    ///
    /// Both paths now construct the same way, from the same CLR type.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavXmlPort NCLMetaXmlPort_CreateObjectInstance(
        object self, Microsoft.Dynamics.Nav.Runtime.ITreeObject parent)
    {
        int id = ReadMetaObjectNumber(self);
        if (id == 0)
            throw new InvalidOperationException(
                "NCLMetaXmlPort.CreateObjectInstance: could not read the xmlport's object id " +
                "from its metadata — constructing the wrong xmlport would silently import or " +
                "export against the wrong schema.");

        return (Microsoft.Dynamics.Nav.Runtime.NavXmlPort)ConstructXmlPort(id, parent);
    }

    /// <summary>Read ObjectId.ObjectNumber off an NCLMetaApplicationObject.</summary>
    private static int ReadMetaObjectNumber(object meta)
    {
        const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var propName in new[] { "ApplicationObjectId", "ObjectId" })
        {
            var objId = meta.GetType().GetProperty(propName, Any)?.GetValue(meta);
            if (objId?.GetType().GetProperty("ObjectNumber", Any)?.GetValue(objId) is int n && n != 0)
                return n;
        }
        return 0;
    }

    /// <summary>
    /// Construct the AL-emitted <c>XmlPort{id}</c> instance parented to <paramref name="parent"/>.
    /// Shared by the handle path and the static-form path so the two can never diverge.
    /// </summary>
    private static object ConstructXmlPort(int id, object parent)
    {
        var xmlPortType = _xmlPortTypeCache.GetOrAdd(id, FindXmlPortType);
        if (xmlPortType == null)
            throw new InvalidOperationException(
                $"XmlPort{id} is not present in the test assembly or any loaded dependency.");

        // BC emits XmlPort ctors as either:
        //   (ITreeObject parent)                          — legacy
        //   (ITreeObject parent, NCLMetaXmlPort meta)    — modern (BC 27+)
        // Try 1-arg first; if missing try 2-arg with our skeleton meta from the cache.
        var ctors = xmlPortType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var oneArg = ctors.FirstOrDefault(c => c.GetParameters().Length == 1 &&
            typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (oneArg != null) return oneArg.Invoke(new object[] { parent })!;

        var twoArg = ctors.FirstOrDefault(c => c.GetParameters().Length == 2 &&
            typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (twoArg != null)
        {
            object? metaArg = LookupNclMetaForXmlPort(id);
            return twoArg.Invoke(new object?[] { parent, metaArg })!;
        }
        throw new InvalidOperationException(
            $"XmlPort{id} has no (ITreeObject) or (ITreeObject, NCLMetaXmlPort) constructor");
    }

    private static object? LookupNclMetaForXmlPort(int id)
    {
        var nclMeta = BcRuntime.SkeletonNCLMetadata;
        if (nclMeta == null) return null;
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        var getMeta = nclMeta.GetType().GetMethod("GetMetaXmlPortById",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(int), typeof(bool) }, null)
            ?? nclMeta.GetType().GetMethod("GetMetaXmlPortById",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(int) }, null);
        try { return getMeta?.Invoke(nclMeta, getMeta.GetParameters().Length == 2
            ? new object[] { id, false }
            : new object[] { id }); }
        catch { return null; }
    }

    private static Type? FindXmlPortType(int id)
    {
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        Type? xmlPortBase = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavXmlPort");
        var name = $"XmlPort{id}";
        if (_currentTestAssembly != null)
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(_currentTestAssembly)
                    .FindFirst(name, x => xmlPortBase == null || xmlPortBase.IsAssignableFrom(x));
                if (t != null) return t;
            }
            catch { }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == _currentTestAssembly) continue;
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm)
                    .FindFirst(name, x => xmlPortBase == null || xmlPortBase.IsAssignableFrom(x));
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    // ──────────────────────────────────────────────────────────────────
    // NavXmlPort instance method stubs — all paths through Export/Import/
    // Run/SetTableView call Session.BeginTransaction or
    // ApplicationObjectRootScope which NRE on skeleton session.
    // Return the "success / no-op" value for each.
    // ──────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────
    // NavXmlPort static Run — XMLPORT.RUN(id), XMLPORT.RUN(id, reqPage),
    // XMLPORT.RUN(id, reqPage, import), XMLPORT.RUN(id, reqPage, import, rec)
    // in AL compile to these static overloads.
    //
    // These are a genuine, permanent out-of-scope surface — §3.4 (file-storage)
    // of docs/scope.md, same bucket as NavFile.ALUpload/ALDownload's browser
    // round-trip overloads (see FilePatches.cs) — NOT "in scope, not yet
    // implemented" and NOT a safe no-op. Verified by decompiling BC's real,
    // unpatched Ncl.dll body (Microsoft.Dynamics.Nav.Runtime.NavXmlPort):
    //
    //   public static void Run(int xmlPortId, bool requestWindow, bool import, NavRecord record)
    //   {
    //       using NavXmlPort navXmlPort = NavGlobal.NCLMetadata.GetMetaXmlPortById(xmlPortId, ...)
    //           .CreateObjectInstance(NavCurrentThread.Session);
    //       navXmlPort.useRequestForm = requestWindow;
    //       if (record != null) navXmlPort.SetTableView(record);
    //       navXmlPort.ImportFile = import;
    //       navXmlPort.RunXmlPort();   // <-- always runs, regardless of the args above
    //   }
    //
    //   private void RunXmlPort()
    //   {
    //       ApplicationObjectRootScope.AddApplicationObjectRootScope(this, delegate {
    //           if (!CallRequestForm()) return;               // no-op when UseRequestPage=false
    //           if (importFile)
    //               fileBufferedStream = NavFile.InternalUpload(displayDialog: true, ..., Guid.NewGuid());
    //           else
    //               NavFile.InternalDownload(displayDialog: true, ..., Destination.InternalStream, Guid.NewGuid());
    //       });
    //   }
    //
    // `record` only ever feeds SetTableView (a row filter) — it never supplies an
    // I/O stream. There is no argument combination, including requestWindow=false,
    // that skips NavFile.InternalUpload/InternalDownload: both are called with
    // displayDialog:true hard-coded, and both resolve to
    // Session.ClientCallback.UploadFileAction/DownloadFileAction — the exact
    // "browser round-trip" surface docs/scope.md#file-storage already names for
    // NavFile.ALUpload/ALDownload. On the runner's non-interactive skeleton session,
    // Session.ClientCallback itself throws NavNCLCallbackNotAllowedException
    // ("Callback functions are not allowed") — confirmed empirically against a
    // pristine, unpatched build for every overload/argument combination tried
    // (unresolvable id still raises BC's own NavALException first, as expected).
    //
    // So a "real fix" resolving via ConstructXmlPort and calling the instance's
    // Export()/Import() directly (bypassing RunXmlPort()'s file-dialog step) would
    // NOT be faithful to XmlPort.Run(...) — it would silently answer a different,
    // easier question (this is already proven and covered by
    // InstanceExportImportRoundTrip_RealBcBody_NoThrow in the sibling suite) while
    // claiming to implement Run(), which is exactly the "pass for the wrong reason"
    // failure loud-failures.md exists to prevent. We instead throw our own typed
    // OOS exception uniformly, before BC's real (and, on the export path,
    // inconsistent — see PR #1884 discussion) body runs at all, mirroring
    // FilePatches.cs's policy for the same underlying surface.
    // ──────────────────────────────────────────────────────────────────

    /// <summary>NavXmlPort.Run(int xmlPortId) — always needs a client file-browse dialog; OOS.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_StaticRun1(int xmlPortId)
    {
        RunnerScope.ThrowOutOfScope("NavXmlPort.Run", "browser-roundtrip", "file-storage");
    }

    /// <summary>NavXmlPort.Run(int xmlPortId, bool requestWindow) — always needs a client file-browse dialog; OOS.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_StaticRun2(int xmlPortId, bool requestWindow)
    {
        RunnerScope.ThrowOutOfScope("NavXmlPort.Run", "browser-roundtrip", "file-storage");
    }

    /// <summary>NavXmlPort.Run(int xmlPortId, bool requestWindow, bool import) — always needs a client file-browse dialog; OOS.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_StaticRun3(int xmlPortId, bool requestWindow, bool import)
    {
        RunnerScope.ThrowOutOfScope("NavXmlPort.Run", "browser-roundtrip", "file-storage");
    }

    /// <summary>NavXmlPort.Run(int xmlPortId, bool requestWindow, bool import, NavRecord record) — record only ever
    /// feeds SetTableView, never the I/O stream; still always needs a client file-browse dialog; OOS.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_StaticRun4(int xmlPortId, bool requestWindow, bool import, object record)
    {
        RunnerScope.ThrowOutOfScope("NavXmlPort.Run", "browser-roundtrip", "file-storage");
    }

    // NavXmlPort static Export/Import — XMLPORT.EXPORT(id, stream[, record]) and
    // XMLPORT.IMPORT(id, stream[, record]) in AL compile to static overloads on NavXmlPort.
    // #1883: no runner replacement here (or anywhere) — see the big comment block below,
    // same conclusion as the #1800 instance-method cluster: BC's real, unpatched static
    // Export/Import bodies already handle well-formed AL usage correctly.

    // Export(DataError) / Import(DataError) / Run() / RunXmlPort() (private) /
    // SetTableView(NavRecord) / BeginInitialization() / EndInitialization() /
    // Add(TableNode|FieldNode|TextNode) used to live here as JmpHook.Hook(...) targets in
    // BcRuntime.cs — Export/Import/Run/RunXmlPort/SetTableView as loud "not-yet-implemented"
    // throw stubs, BeginInitialization/EndInitialization/Add as no-op ctor scaffolding. ALL of
    // them were dead: JmpHook is disabled by default, so none of these hooks ever fired and
    // BC's real, unpatched bodies ran instead.
    //
    // Investigated as part of #1800 (orphaned-hook audit). An earlier revision of this fix
    // Cecil-owned BeginInitialization to install stub metadata, on the belief that
    // Session.MetadataProvider is null on the skeleton and NREs the ctor — that turned out to
    // be a misdiagnosis, and an active regression: it broke 14 previously-passing al-language
    // corpus tests (Codeunit60206/60207). Root cause: Session.MetadataProvider is NOT null on
    // the skeleton — AlRunner/Patches/MetadataPatches.cs's InjectSkeletonSystemTenant already
    // seeds session.tenant/systemTenant for exactly this call path (its own comment names
    // NavXmlPort.BeginInitialization as the motivating case), so BC's real, unpatched
    // BeginInitialization/EndInitialization/Add bodies already construct correctly — proven
    // empirically against a pristine, unpatched build. And once construction succeeds, BC's
    // real Export/Import/Run/SetTableView bodies already handle well-formed AL usage correctly
    // too (nested-table export/import, text-variable triggers, auto-update/auto-replace,
    // SetTableView row filtering — all passing against the corpus). So none of these eight
    // methods need a runner replacement at all; their throw stubs / no-ops and the matching
    // (already-orphaned) Hook(...) call sites were deleted outright rather than left dead —
    // there is nothing correct to redirect them to, BC's real body already is the right
    // answer. See tests/runner-extras/standalone-suites/xmlport-cluster-hooks-1800 for the
    // proving tests and the #1800 PR body for the full orphan-hook inventory and the
    // misdiagnosis-and-correction record.
    //
    // NavXmlPort.Run(int[, bool[, bool[, NavRecord]]]) — the 4 static overloads — are the one
    // genuine, permanent out-of-scope surface in this cluster, NOT a case of "BC's real body
    // is already correct" like the eight methods above: see the block above
    // (NavXmlPort_StaticRun1..4) and the matching Cecil ownership in NclCecilRewrite.cs for the
    // decompiled-source evidence and the docs/scope.md#file-storage classification.
    //
    // XmlPort{ID}.InitializeComponent() (below) is a separate mechanism — JmpHook.Apply
    // against a method on the test assembly's own BC-generated type, not NCL — and was not
    // part of this investigation; left unchanged.
    //
    // #1883: NavXmlPortTableNode(NavRecordHandle)'s ctor used to be hooked here too
    // (NavXmlPortTableNode_Ctor, further down), on the belief that BC's real ctor NREs before
    // reaching Add(). Also orphaned, also a misdiagnosis of the same shape: every al-language
    // corpus xmlport test constructs a tableelement-bound XmlPort (see BcRuntime.cs's
    // NavXmlPortTableNode note near the Run-overload hook block) and all of them pass, so BC's
    // real, unpatched ctor already constructs correctly. Deleted outright along with
    // EnsureXmlPortNodeFields — there is nothing to redirect to.

    /// <summary>
    /// XmlPort{ID}.InitializeComponent() — the BC-generated override that calls
    /// BeginInitialization, constructs nodes, and calls EndInitialization.
    /// Skeleton ctor-time scaffolding — required so XmlPort{ID} construction succeeds;
    /// no observable AL-test behavior to fake.
    /// EndInitialization accesses metadata (null on skeleton) and may be JIT-inlined
    /// into the BC-generated InitializeComponent body, making the EndInitialization hook
    /// unreliable. We instead hook the concrete override directly (after the test assembly
    /// is loaded) so the JIT has not yet compiled the method and the hook is guaranteed
    /// to land.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_InitializeComponent(object self)
    {
    }
}
