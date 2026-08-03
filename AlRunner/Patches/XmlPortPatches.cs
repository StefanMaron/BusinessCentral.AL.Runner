// XmlPortPatches — replacements for NavXmlPortHandle.CreateTarget and NavXmlPort
// instance methods (Export, Import, Run, SetTableView).
//
// NavXmlPortHandle.CreateTarget normally calls
//   NavGlobal.NCLMetadata.GetMetaXmlPortById(id, true).CreateObjectInstance(this)
// which NREs because our skeleton NCLMetaXmlPort has no ApplicationObjectConstructor
// delegate. We bypass it by finding XmlPort{ID} in the loaded test assembly and
// constructing directly via reflection — same pattern as NavFormHandle/NavReportHandle.
//
// The NavXmlPort instance methods (Export, Import, Run, SetTableView) all internally
// call Session.BeginTransaction / ApplicationObjectRootScope which NRE on our skeleton.
// We replace them with stubs that return the "success" value without side effects.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner;

public static partial class BcRuntime
{
    private static readonly ConcurrentDictionary<int, Type?> _xmlPortTypeCache = new();

    // Lazily resolved reflection handles for NavXmlPortNode base-class private list fields.
    private static System.Reflection.FieldInfo? _fXmlPortNodeAttrChildren;
    private static System.Reflection.FieldInfo? _fXmlPortNodeElemChildren;

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
                var t = Array.Find(_currentTestAssembly.GetTypes(),
                    x => x.Name == name && (xmlPortBase == null || xmlPortBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == _currentTestAssembly) continue;
            try
            {
                var t = Array.Find(asm.GetTypes(),
                    x => x.Name == name && (xmlPortBase == null || xmlPortBase.IsAssignableFrom(x)));
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
    // in AL compile to these static overloads. In standalone mode there is no
    // service tier and no interactive request page, so all four overloads are
    // safe no-ops. Without these hooks, BCruntime calls
    // NCLMetadata.GetMetaXmlPortById(id) → ThrowMetaApplicationObjectNotFound
    // for any XmlPort not registered in NCLMetadata (i.e. every test-assembly
    // XmlPort).
    // ──────────────────────────────────────────────────────────────────

    /// <summary>NavXmlPort.Run(int xmlPortId) — no-op; standalone mode has no request page or I/O target.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_StaticRun1(int xmlPortId)
    {
        Console.Error.WriteLine($"[BcRuntime] NavXmlPort.Run({xmlPortId}) → no-op (static Run hook)");
    }

    /// <summary>NavXmlPort.Run(int xmlPortId, bool requestWindow) — no-op.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_StaticRun2(int xmlPortId, bool requestWindow)
    {
        Console.Error.WriteLine($"[BcRuntime] NavXmlPort.Run({xmlPortId}, {requestWindow}) → no-op (static Run hook)");
    }

    /// <summary>NavXmlPort.Run(int xmlPortId, bool requestWindow, bool import) — no-op.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_StaticRun3(int xmlPortId, bool requestWindow, bool import)
    {
        Console.Error.WriteLine($"[BcRuntime] NavXmlPort.Run({xmlPortId}, {requestWindow}, {import}) → no-op (static Run hook)");
    }

    /// <summary>NavXmlPort.Run(int xmlPortId, bool requestWindow, bool import, NavRecord record) — no-op.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_StaticRun4(int xmlPortId, bool requestWindow, bool import, object record)
    {
        Console.Error.WriteLine($"[BcRuntime] NavXmlPort.Run({xmlPortId}, {requestWindow}, {import}, record) → no-op (static Run hook)");
    }

    // ──────────────────────────────────────────────────────────────────
    // NavXmlPort static Export/Import — XMLPORT.EXPORT(id, stream) and
    // XMLPORT.IMPORT(id, stream) in AL compile to these static overloads.
    // In-memory XmlPort serialization is in scope eventually (scope.md §4
    // TODO) but not yet implemented; throw loud failure so tests cannot
    // silently pass without real serialization.
    // ──────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavXmlPort_StaticExport(int errorLevel, int xmlPortId, object outStream, object record)
    {
        RunnerScope.ThrowNotYetImplemented(
            "NavXmlPort.StaticExport",
            "in-memory XmlPort serialization not yet implemented — see HANDOFF.md and SCOPE-AUDIT.md");
        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavXmlPort_StaticImport(int errorLevel, int xmlPortId, object inStream, object record)
    {
        RunnerScope.ThrowNotYetImplemented(
            "NavXmlPort.StaticImport",
            "in-memory XmlPort serialization not yet implemented — see HANDOFF.md and SCOPE-AUDIT.md");
        return default;
    }

    /// <summary>Export(DataError) — loud failure; in-memory XmlPort not yet implemented.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavXmlPort_Export(object self, int errorLevel)
    {
        RunnerScope.ThrowNotYetImplemented(
            "NavXmlPort.Export",
            "in-memory XmlPort serialization not yet implemented — see HANDOFF.md and SCOPE-AUDIT.md");
        return default;
    }

    /// <summary>Import(DataError) — loud failure; in-memory XmlPort not yet implemented.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavXmlPort_Import(object self, int errorLevel)
    {
        RunnerScope.ThrowNotYetImplemented(
            "NavXmlPort.Import",
            "in-memory XmlPort serialization not yet implemented — see HANDOFF.md and SCOPE-AUDIT.md");
        return default;
    }

    /// <summary>Run() — loud failure; in-memory XmlPort not yet implemented.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_Run(object self)
    {
        RunnerScope.ThrowNotYetImplemented(
            "NavXmlPort.Run",
            "in-memory XmlPort serialization not yet implemented — see HANDOFF.md and SCOPE-AUDIT.md");
    }

    /// <summary>RunXmlPort() (private) — loud failure; in-memory XmlPort not yet implemented.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_RunXmlPort(object self)
    {
        RunnerScope.ThrowNotYetImplemented(
            "NavXmlPort.RunXmlPort",
            "in-memory XmlPort serialization not yet implemented — see HANDOFF.md and SCOPE-AUDIT.md");
    }

    /// <summary>SetTableView(NavRecord) — loud failure; in-memory XmlPort not yet implemented.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_SetTableView(object self, object record)
    {
        RunnerScope.ThrowNotYetImplemented(
            "NavXmlPort.SetTableView",
            "in-memory XmlPort serialization not yet implemented — see HANDOFF.md and SCOPE-AUDIT.md");
    }

    /// <summary>BeginInitialization() — called from the BC-generated XmlPort{ID} ctor.
    /// Skeleton ctor-time scaffolding — required so XmlPort{ID} construction succeeds;
    /// no observable AL-test behavior to fake.
    /// Dereferences Session.MetadataProvider (null on skeleton) → NRE. Stub as no-op;
    /// fields it would populate (metadata, fieldDelimiter, …) are not needed for our
    /// Export/Import/Run/SetTableView loud-failure hooks.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_BeginInitialization(object self)
    {
    }

    /// <summary>EndInitialization() — called from the BC-generated XmlPort{ID} ctor after
    /// the node-building code. Skeleton ctor-time scaffolding — required so XmlPort{ID}
    /// construction succeeds; no observable AL-test behavior to fake.
    /// Accesses metadata.UseRequestForm and requestOptionsPage
    /// (both null on skeleton after BeginInitialization is no-op'd) → NRE. Stub as no-op.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_EndInitialization(object self)
    {
    }

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

    // Skeleton ctor-time scaffolding — required so XmlPort{ID} construction succeeds;
    // no observable AL-test behavior to fake.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_AddTableNode(object self, object node) { }

    // Skeleton ctor-time scaffolding — required so XmlPort{ID} construction succeeds;
    // no observable AL-test behavior to fake.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_AddFieldNode(object self, object node) { }

    // Skeleton ctor-time scaffolding — required so XmlPort{ID} construction succeeds;
    // no observable AL-test behavior to fake.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPort_AddTextNode(object self, object node) { }

    // Skeleton ctor-time scaffolding — required so XmlPort{ID} construction succeeds;
    // no observable AL-test behavior to fake. Initializes the attribute/element child
    // lists so node-traversal code does not NRE on an uninitialized collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavXmlPortTableNode_Ctor(object self, object record)
    {
        EnsureXmlPortNodeFields(self.GetType());
        if (_fXmlPortNodeAttrChildren != null)
        {
            _fXmlPortNodeAttrChildren.SetValue(self, Activator.CreateInstance(_xmlPortNodeListType!));
            _fXmlPortNodeElemChildren!.SetValue(self, Activator.CreateInstance(_xmlPortNodeListType!));
        }
    }

    private static System.Type? _xmlPortNodeListType;

    private static void EnsureXmlPortNodeFields(Type derivedType)
    {
        if (_fXmlPortNodeAttrChildren != null) return;
        var t = derivedType;
        while (t != null)
        {
            var attr = t.GetField("attributeChildren",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var elem = t.GetField("elementChildren",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (attr != null && elem != null)
            {
                var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(attr.FieldType.GetGenericArguments()[0]);
                System.Threading.Interlocked.CompareExchange(ref _xmlPortNodeListType, listType, null);
                System.Threading.Interlocked.CompareExchange(ref _fXmlPortNodeAttrChildren, attr, null);
                System.Threading.Interlocked.CompareExchange(ref _fXmlPortNodeElemChildren, elem, null);
                return;
            }
            t = t.BaseType;
        }
    }
}
