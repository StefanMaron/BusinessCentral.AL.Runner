// NavRecordRefPatches — replacements for NavRecordRef.get_Target and small siblings.
//
// NavRecordRef.get_Target tries to construct a SharedRecordRef using
//     base.Tree.Session.Company.SharedObjects
// which NREs on the skeleton because Session.Company.SharedObjects is null.
// Replacement constructs a SharedRecordRef using a process-wide skeleton
// TreeSharedObjectContainer parented to RootTreeStub, and stashes it via
// Tree.SetReferenceTarget so subsequent gets see the cached value.
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner;

public static partial class BcRuntime
{
    private static object? _skeletonSharedObjectContainer;
    private static ConstructorInfo? _ctorSharedRecordRef;
    private static MethodInfo? _mTreeGetReferenceTarget;
    private static MethodInfo? _mTreeSetReferenceTarget;
    private static PropertyInfo? _pNavRecordRefTree;

    // MEMORY LEAK FIX (see docs comment on RecordPatches.ResetPerTestState): every
    // SharedRecordRef / SharedNavStream / SharedHttpRequest / SharedHttpResponseMessage /
    // SharedNavHttpClient / SharedNavObjectDictionary constructed with
    // _skeletonSharedObjectContainer as its ITreeSharedObjectContainer parent becomes a
    // PERMANENT node in that container's TreeHandler child linked list — TreeObject's
    // ctor unconditionally calls TreeHandler.CreateTreeHandler(parent, this), which links
    // into parentHandler.firstChildHandler/nextSiblingHandler (Ncl TreeHandler ctor +
    // InternalAddChild) and nothing ever removed them. Because
    // _skeletonSharedObjectContainer is a single process-wide static (created once, reused
    // for the life of the process), that linked list — and everything each child
    // transitively holds (e.g. SharedRecordRef.record → the live NavRecord with its field
    // values/BLOBs) — grows without bound across the whole run. This is a completely
    // separate retention path from _dataAccessByTable (which IS cleared every
    // ResetPerTestState) and was NOT being cleared anywhere.
    //
    // Fix: sweep the container's children at the same per-test/per-codeunit boundary
    // _dataAccessByTable already resets at. TreeHandler.DisposeAllChildren() is BC's own
    // mechanism for this (atomically detaches the child chain and disposes each host
    // object) — nothing legitimately needs one of these wrapper objects to survive past
    // the test that created it, since a fresh one is always re-derived from
    // tree.GetReferenceTarget() the next time it's needed.
    public static void DisposeSkeletonSharedObjectContainerChildren()
    {
        if (_skeletonSharedObjectContainer is ITreeObject treeObject)
            treeObject.Tree?.DisposeAllChildren();
    }

    // TEMPORARY (memory-census diagnostic) — count the container's live child chain
    // WITHOUT disposing it, by walking TreeHandler's private child-linked-list fields
    // via reflection. Best-effort: returns -1 if the field shape can't be found.
    // See MemoryCensus.cs.
    private static FieldInfo? _fTreeHandlerFirstChild;
    private static FieldInfo? _fTreeHandlerNextSibling;
    internal static int CensusSharedObjectContainerChildCount()
    {
        if (_skeletonSharedObjectContainer is not ITreeObject treeObject || treeObject.Tree == null)
            return 0;
        var tree = treeObject.Tree;
        var treeType = tree.GetType();
        if (_fTreeHandlerFirstChild == null)
            _fTreeHandlerFirstChild = treeType.GetField("firstChildHandler",
                BindingFlags.NonPublic | BindingFlags.Instance);
        if (_fTreeHandlerFirstChild == null) return -1;
        if (_fTreeHandlerNextSibling == null)
            _fTreeHandlerNextSibling = _fTreeHandlerFirstChild.FieldType.GetField("nextSiblingHandler",
                BindingFlags.NonPublic | BindingFlags.Instance);
        if (_fTreeHandlerNextSibling == null) return -1;

        int n = 0;
        var cur = _fTreeHandlerFirstChild.GetValue(tree);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (cur != null && seen.Add(cur))
        {
            n++;
            cur = _fTreeHandlerNextSibling.GetValue(cur);
        }
        return n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavRecordRef_get_Target(object self)
    {
        // Reflection paths are cached after first call.
        if (_pNavRecordRefTree == null)
            _pNavRecordRefTree = self.GetType().GetProperty("Tree",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var tree = _pNavRecordRefTree!.GetValue(self)!;

        if (_mTreeGetReferenceTarget == null)
            _mTreeGetReferenceTarget = tree.GetType().GetMethod("GetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
        if (_mTreeSetReferenceTarget == null)
            _mTreeSetReferenceTarget = tree.GetType().GetMethod("SetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var existing = _mTreeGetReferenceTarget?.Invoke(tree, null);
        if (existing != null) return existing;

        // Construct SharedRecordRef using a skeleton TreeSharedObjectContainer.
        // Resolved through the memoised lookup: this method runs on every RecordRef
        // materialisation, and the AppDomain scan it replaces (which calls
        // RuntimeAssembly.GetName -> native GetCodeBase per loaded assembly) was a resolved
        // leaf frame in the #2304 profile. See BcRuntime LoadedRuntimeAssemblies.cs.
        var navNcl = RequireRuntimeAssembly("Microsoft.Dynamics.Nav.Ncl");
        if (_skeletonSharedObjectContainer == null)
        {
            var tContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeSharedObjectContainer")!;
            var tITree = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
            var ctor = tContainer.GetConstructor(new[] { tITree });
            _skeletonSharedObjectContainer = ctor!.Invoke(new object?[] { RootTreeStub });
        }
        if (_ctorSharedRecordRef == null)
        {
            var tShared = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedRecordRef")!;
            var tIContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
            _ctorSharedRecordRef = tShared.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null);
        }
        var srr = _ctorSharedRecordRef!.Invoke(new object?[] { _skeletonSharedObjectContainer });
        _mTreeSetReferenceTarget?.Invoke(tree, new object?[] { srr });
        return srr;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecordRef_ALOpen_Int(object self, int tableNo)
        => OpenRecordRefById(self, tableNo, isTemporary: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecordRef_ALOpen_IntBool(object self, int tableNo, bool isTemporary)
        => OpenRecordRefById(self, tableNo, isTemporary);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecordRef_ALOpen_IntBoolCompany(object self, int tableNo, bool isTemporary, string companyName)
        => OpenRecordRefById(self, tableNo, isTemporary);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecordRef_ALOpen_TargetInt(object self, CompilationTarget compilationTarget, int tableNo)
        => OpenRecordRefById(self, tableNo, isTemporary: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecordRef_ALOpen_TargetIntBool(object self, CompilationTarget compilationTarget, int tableNo, bool isTemporary)
        => OpenRecordRefById(self, tableNo, isTemporary);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecordRef_ALOpen_TargetIntBoolCompany(object self, CompilationTarget compilationTarget, int tableNo, bool isTemporary, string companyName)
        => OpenRecordRefById(self, tableNo, isTemporary);

    private static void OpenRecordRefById(object self, int tableNo, bool isTemporary)
    {
        var metaTable = RecordPatches.EnsureTableInMetadataCache(tableNo)
            ?? throw new InvalidOperationException($"RecordRef.Open: no NCLMetaTable for table {tableNo}");
        var recordType = RecordPatches.FindRecordType(tableNo)
            ?? throw new InvalidOperationException($"RecordRef.Open: no loaded type Record{tableNo} found");
        var ctor = recordType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 6)
            ?? throw new InvalidOperationException($"Record{tableNo} has no 6-arg constructor");
        var target = NavRecordRef_get_Target(self);
        var record = (NavRecord)ctor.Invoke(new object?[]
        {
            target, metaTable, isTemporary, null, null, SecurityFiltering.Ignored
        });
        // Register tableextensions so the record's extension triggers (incl. the field
        // OnBefore/OnAfterValidate handlers fired through FieldRef.Validate) dispatch to a
        // real extension instance instead of falling back to a cast of the base record.
        RecordPatches.RegisterParsedTableExtensions(record, tableNo);
        // Wire field OnValidate/OnLookup handlers + field-validate subscribers onto this table's
        // metatable (lazy wiring for on-demand-built tables — e.g. a precompiled BaseApp table).
        RecordPatches.WireFieldTriggerHandlersForTable(tableNo, metaTable);
        AlRunner.Patches.EventSubscriberPatches.InjectValidateSubsForTable(tableNo, metaTable);
        // Table-level trigger subscribers — see InjectTriggerSubsForTable for why this must
        // run after GetOrAdd returns, not from inside BuildNCLMetaTable.
        AlRunner.Patches.EventSubscriberPatches.InjectTriggerSubsForTable(tableNo, metaTable);
        // SharedRecordRef.Record is a non-public-accessor property on the headless
        // build, so include NonPublic in the lookup (Public-only returns null → NRE).
        var recordProp = target.GetType().GetProperty("Record",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"RecordRef.Open: SharedRecordRef has no 'Record' property on {target.GetType().FullName}");
        recordProp.SetValue(target, record);
    }

    // NavObjectList<T>.get_Target — same Option-C shape as NavRecordRef.get_Target.
    // Real body chains through base.Tree.Session.Company.SharedObjects on the
    // lazy-create path; on the headless skeleton, Session.Company is null → NRE.
    // Cecil rewrites get_Target to call this helper, which constructs
    // SharedNavObjectList<T> parented to the process-wide skeleton container.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ConstructorInfo> _ctorSharedNavObjectList = new();
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavObjectList_get_Target(object self)
    {
        var treeProp = self.GetType().GetProperty("Tree",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        var tree = treeProp!.GetValue(self)!;
        if (_mTreeGetReferenceTarget == null)
            _mTreeGetReferenceTarget = tree.GetType().GetMethod("GetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
        if (_mTreeSetReferenceTarget == null)
            _mTreeSetReferenceTarget = tree.GetType().GetMethod("SetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var existing = _mTreeGetReferenceTarget?.Invoke(tree, null);
        if (existing != null) return existing;

        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (_skeletonSharedObjectContainer == null)
        {
            var tContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeSharedObjectContainer")!;
            var tITree = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
            _skeletonSharedObjectContainer = tContainer.GetConstructor(new[] { tITree })!
                .Invoke(new object?[] { RootTreeStub });
        }

        // Construct SharedNavObjectList<T> for the same T as the receiver.
        var t = self.GetType().GetGenericArguments()[0];
        var ctor = _ctorSharedNavObjectList.GetOrAdd(t, tArg =>
        {
            var openShared = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavObjectList`1")!;
            var closedShared = openShared.MakeGenericType(tArg);
            var tIContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
            return closedShared.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { tIContainer }, null)!;
        });
        var shared = ctor.Invoke(new[] { _skeletonSharedObjectContainer });
        _mTreeSetReferenceTarget?.Invoke(tree, new[] { shared });
        return shared;
    }

    // RecordLink — in-memory link store keyed by RuntimeHelpers.GetHashCode(NavRecord).
    // Real impl writes to table 2000000068 (Record Link) which the runner has no SQL
    // backend for. Cecil rewrites RecordLink.{AddLinkAsync, HasLinks, DeleteLinksAsync,
    // DeleteLinkAsync, CopyLinksAsync, MoveLinksAsync, TableHasLinks} to the helpers
    // below. Both `Rec.AddLink(...)` and `RecRef.AddLink(...)` funnel through this one
    // store, so it must satisfy both AL surfaces — do not add a second one.
    //
    // Links carry an ID because that is BC's contract: AddLink returns the "Link ID"
    // primary key of the Record Link row it created (strictly positive — it is an
    // AutoIncrement field), and DeleteLink(ID) addresses that row. Returning 0 and
    // no-oping DeleteLink was a silent fake that made Rec.DeleteLink(Id) do nothing.
    private readonly record struct LinkEntry(int Id, string Url, string Description);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Generic.List<LinkEntry>> _recordLinks = new();

    /// <summary>Next Record Link "Link ID". Starts at 1: BC's AutoIncrement key is
    /// strictly positive, and AL code tests <c>LinkId &gt; 0</c> for success.</summary>
    private static int _nextLinkId;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<int> RecordLink_AddLinkAsync(object record, string url, string description)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (url == null) throw new ArgumentNullException(nameof(url));
        if (description == null) throw new ArgumentNullException(nameof(description));
        if (url.Length > 2048) throw new ArgumentException("RecordLink URL above max size");
        var key = RuntimeHelpers.GetHashCode(record);
        var list = _recordLinks.GetOrAdd(key, _ => new System.Collections.Generic.List<LinkEntry>());
        var id = System.Threading.Interlocked.Increment(ref _nextLinkId);
        lock (list) list.Add(new LinkEntry(id, url, description));
        return new System.Threading.Tasks.ValueTask<int>(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool RecordLink_HasLinks(object record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        return _recordLinks.TryGetValue(RuntimeHelpers.GetHashCode(record), out var list) && list.Count > 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask RecordLink_DeleteLinksAsync(object record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        _recordLinks.TryRemove(RuntimeHelpers.GetHashCode(record), out _);
        return System.Threading.Tasks.ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask RecordLink_DeleteLinkAsync(object record, int linkId)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (_recordLinks.TryGetValue(RuntimeHelpers.GetHashCode(record), out var list))
        {
            lock (list)
            {
                list.RemoveAll(e => e.Id == linkId);
                if (list.Count == 0) _recordLinks.TryRemove(RuntimeHelpers.GetHashCode(record), out _);
            }
        }
        // BC's DeleteLink on a non-existent Link ID is a no-op, not an error.
        return System.Threading.Tasks.ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask RecordLink_CopyLinksAsync(object src, object dst)
    {
        if (src == null || dst == null) return System.Threading.Tasks.ValueTask.CompletedTask;
        if (_recordLinks.TryGetValue(RuntimeHelpers.GetHashCode(src), out var srcList))
        {
            var dstList = _recordLinks.GetOrAdd(RuntimeHelpers.GetHashCode(dst), _ => new System.Collections.Generic.List<LinkEntry>());
            // Copy creates NEW Record Link rows, so each copy gets a fresh Link ID —
            // unlike MoveLinks below, which relocates the existing rows and keeps theirs.
            lock (srcList)
            {
                var snapshot = srcList.ToArray(); // src may == dst
                lock (dstList)
                    foreach (var e in snapshot)
                        dstList.Add(e with { Id = System.Threading.Interlocked.Increment(ref _nextLinkId) });
            }
        }
        return System.Threading.Tasks.ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask RecordLink_MoveLinksAsync(object src, object dst)
    {
        if (src == null || dst == null) return System.Threading.Tasks.ValueTask.CompletedTask;
        if (_recordLinks.TryRemove(RuntimeHelpers.GetHashCode(src), out var list))
            _recordLinks[RuntimeHelpers.GetHashCode(dst)] = list;
        return System.Threading.Tasks.ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool RecordLink_TableHasLinks(object parentTree, object table, string companyName)
    {
        // Conservative: only true if any record from any table has a link in our store.
        return _recordLinks.Count > 0;
    }

    // NavValue.CreateNavValueFromObject lacks a switch case for NavNclType.NavALErrorType
    // (introduced for ErrorInfo.ErrorType()) — when AL code reads a default ALErrorType
    // it ends up boxed as a CLR ALErrorType enum value, CalcMetadataFromDotNetObject
    // returns NavALErrorType metadata, and the switch falls through to the default
    // throw branch. NclCecilRewrite prepends a check that delegates to this helper
    // for the NavALErrorType case (returns `(NavValue)new NavALErrorType(int)`).
    private static ConstructorInfo? _ctorNavALErrorType;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object CreateNavALErrorType(object? value)
    {
        if (_ctorNavALErrorType == null)
        {
            var navNcl = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            var t = navNcl.GetType("Microsoft.Dynamics.Nav.Types.NavALErrorType")
                ?? navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavALErrorType")
                ?? navNcl.GetTypes().First(x => x.Name == "NavALErrorType" && !x.IsEnum);
            _ctorNavALErrorType = t.GetConstructors().First(c =>
                c.GetParameters().Length == 1 &&
                c.GetParameters()[0].ParameterType == typeof(int));
        }
        int iv = value switch
        {
            null => 0,
            int i => i,
            _ => Convert.ToInt32(value)
        };
        return _ctorNavALErrorType.Invoke(new object?[] { iv });
    }

    // NavStringValue.CompareTo(NavStringValue) — real impl reaches NavCurrentThread.Session.Culture
    // which is null on the skeleton. Fall back to ordinal comparison via the public Value property.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NavStringValue_CompareTo(object self, object? other)
    {
        if (other == null) return 1;
        if (ReferenceEquals(other, self)) return 0;
        var sv = GetNavStringValue(self);
        var ov = GetNavStringValue(other);
        return string.Compare(sv, ov, StringComparison.Ordinal);
    }

    private static string GetNavStringValue(object value)
    {
        var prop = value.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(value) as string ?? "";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool BitArrayHelpers_Equals(BitArray? left, BitArray? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Length != right.Length) return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    // NavStream.get_Target — same shape as NavRecordRef. Construct SharedNavStream parented
    // to the skeleton container. NavStream wraps AL InStream / OutStream variables; fixing
    // get_Target lets the NavStream ctor succeed and subsequent SharedStream assignment work.
    private static ConstructorInfo? _ctorSharedNavStream;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavStream_get_Target(object self)
    {
        var treeProp = self.GetType().GetProperty("Tree",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        var tree = treeProp!.GetValue(self)!;
        if (_mTreeGetReferenceTarget == null)
            _mTreeGetReferenceTarget = tree.GetType().GetMethod("GetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
        if (_mTreeSetReferenceTarget == null)
            _mTreeSetReferenceTarget = tree.GetType().GetMethod("SetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var existing = _mTreeGetReferenceTarget?.Invoke(tree, null);
        if (existing != null) return existing;

        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (_skeletonSharedObjectContainer == null)
        {
            var tContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeSharedObjectContainer")!;
            var tITree = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
            _skeletonSharedObjectContainer = tContainer.GetConstructor(new[] { tITree })!
                .Invoke(new object?[] { RootTreeStub });
        }
        if (_ctorSharedNavStream == null)
        {
            var tShared = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavStream")!;
            var tIContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
            _ctorSharedNavStream = tShared.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null)!;
        }
        var shared = _ctorSharedNavStream.Invoke(new object?[] { _skeletonSharedObjectContainer });
        _mTreeSetReferenceTarget?.Invoke(tree, new object?[] { shared });
        return shared;
    }

    // NavHttpRequestMessage.get_Target — same shape as NavRecordRef.Target. Construct
    // SharedNavHttpRequestMessage parented to the skeleton container.
    private static ConstructorInfo? _ctorSharedHttpReq;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavHttpRequestMessage_get_Target(object self)
    {
        if (_pNavRecordRefTree == null) // reuse Tree-property lookup logic per type below
            _pNavRecordRefTree = self.GetType().GetProperty("Tree",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        // Tree property is on TreeObject base — look up on the actual self type:
        var treeProp = self.GetType().GetProperty("Tree",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        var tree = treeProp!.GetValue(self)!;
        if (_mTreeGetReferenceTarget == null)
            _mTreeGetReferenceTarget = tree.GetType().GetMethod("GetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
        if (_mTreeSetReferenceTarget == null)
            _mTreeSetReferenceTarget = tree.GetType().GetMethod("SetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var existing = _mTreeGetReferenceTarget?.Invoke(tree, null);
        if (existing != null) return existing;

        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (_skeletonSharedObjectContainer == null)
        {
            var tContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeSharedObjectContainer")!;
            var tITree = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
            _skeletonSharedObjectContainer = tContainer.GetConstructor(new[] { tITree })!
                .Invoke(new object?[] { RootTreeStub });
        }
        if (_ctorSharedHttpReq == null)
        {
            var tShared = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavHttpRequestMessage")!;
            var tIContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
            _ctorSharedHttpReq = tShared.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null);
        }
        var shared = _ctorSharedHttpReq!.Invoke(new object?[] { _skeletonSharedObjectContainer });
        _mTreeSetReferenceTarget?.Invoke(tree, new object?[] { shared });
        return shared;
    }

    // NavHttpResponseMessageBase.get_Target — same shape. Construct SharedNavHttpResponseMessage
    // parented to skeleton container. SharedNavHttpResponseMessage(ITreeSharedObjectContainer) ctor
    // is safe — unlike HttpClient, it does NOT call InitializeToDefault/CreateClient.
    private static ConstructorInfo? _ctorSharedHttpResponseMsg;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavHttpResponseMessageBase_get_Target(object self)
    {
        var treeProp = self.GetType().GetProperty("Tree",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        var tree = treeProp!.GetValue(self)!;
        if (_mTreeGetReferenceTarget == null)
            _mTreeGetReferenceTarget = tree.GetType().GetMethod("GetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
        if (_mTreeSetReferenceTarget == null)
            _mTreeSetReferenceTarget = tree.GetType().GetMethod("SetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var existing = _mTreeGetReferenceTarget?.Invoke(tree, null);
        if (existing != null) return existing;

        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (_skeletonSharedObjectContainer == null)
        {
            var tContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeSharedObjectContainer")!;
            var tITree = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
            _skeletonSharedObjectContainer = tContainer.GetConstructor(new[] { tITree })!
                .Invoke(new object?[] { RootTreeStub });
        }
        if (_ctorSharedHttpResponseMsg == null)
        {
            var tShared = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavHttpResponseMessage")!;
            var tIContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
            _ctorSharedHttpResponseMsg = tShared.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null);
        }
        var shared = _ctorSharedHttpResponseMsg!.Invoke(new object?[] { _skeletonSharedObjectContainer });
        _mTreeSetReferenceTarget?.Invoke(tree, new object?[] { shared });
        return shared;
    }

    // NavHttpClient.get_Target — same Option-C shape. SharedNavHttpClient(ITreeSharedObjectContainer)
    // is safe: just calls base(sharedObjectContainer), no CreateClient or HTTP infrastructure.
    private static ConstructorInfo? _ctorSharedHttpClient;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavHttpClient_get_Target(object self)
    {
        var treeProp = self.GetType().GetProperty("Tree",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        var tree = treeProp!.GetValue(self)!;
        if (_mTreeGetReferenceTarget == null)
            _mTreeGetReferenceTarget = tree.GetType().GetMethod("GetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
        if (_mTreeSetReferenceTarget == null)
            _mTreeSetReferenceTarget = tree.GetType().GetMethod("SetReferenceTarget",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var existing = _mTreeGetReferenceTarget?.Invoke(tree, null);
        if (existing != null) return existing;

        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (_skeletonSharedObjectContainer == null)
        {
            var tContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeSharedObjectContainer")!;
            var tITree = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
            _skeletonSharedObjectContainer = tContainer.GetConstructor(new[] { tITree })!
                .Invoke(new object?[] { RootTreeStub });
        }
        if (_ctorSharedHttpClient == null)
        {
            var tShared = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavHttpClient")!;
            var tIContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
            _ctorSharedHttpClient = tShared.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null);
        }
        var shared = _ctorSharedHttpClient!.Invoke(new object?[] { _skeletonSharedObjectContainer });
        _mTreeSetReferenceTarget?.Invoke(tree, new object?[] { shared });
        return shared;
    }

    // ALSystemNumeric.ALRandomize / ALRandom — real impls hit NavCurrentThread.Session.Random
    // which is null on the skeleton. Back the statics with a process-static Random.
    private static System.Random _alRandom = new System.Random();
    private static readonly object _alRandomLock = new object();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALSystemNumeric_ALRandomize()
    {
        lock (_alRandomLock) _alRandom = new System.Random();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALSystemNumeric_ALRandomize_Seed(int seed)
    {
        lock (_alRandomLock) _alRandom = new System.Random(seed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ALSystemNumeric_ALRandom(int maxNumber)
    {
        if (maxNumber < 0) maxNumber = -maxNumber;
        if (maxNumber == 0) maxNumber = 1;
        lock (_alRandomLock) return _alRandom.Next(maxNumber) + 1;
    }

    // NavDialog.ALOpen — UI dialog open. Real impl reaches Tree.Session which is null.
    // No-op for skeleton tests; AL test code just needs the call to not throw.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavDialog_ALOpen(object self, Guid automationId, string message, object[] getters) { }

    // ALSystemString_ALLowercase / ALSystemString_ALUppercase used to live here, backing an
    // orphaned JmpHook registration in BcRuntime.cs (JmpHook disabled by default, so BC's real
    // ALSystemString.ALLowercase/ALUppercase bodies ran anyway). Deleted along with the
    // registration — see the comment in BcRuntime.cs's ApplyAllPatches for the empirical
    // evidence (#1883 follow-up).

    // RecordImplementation.GetActiveCompany — touched by NavRecord.CloneRecord.
    // Real impl: Session.Database.CompanyTokens.Get(tableState.CompanyNameToken). Both
    // Database and tableState are null on the skeleton; return empty string. AL code
    // that compares company names will see "" == "" which is fine for most tests.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string RecordImplementation_GetActiveCompany(object self) => "";

    // NavSession.GetPermissionSet — skeleton has no Permissions object, causing NREs on
    // permission checks during CalcFields, HasReadPermission, HasWritePermission, etc.
    // NCL already ships VirtualDataProvider.PermissionSet (a private singleton of
    // VirtualTablePermissionSet) whose HasPermissions returns true and VerifyPermissions
    // is a no-op. We return it for all GetPermissionSet calls on the skeleton.
    private static object? _allGrantedPermSet;

    private static object GetAllGrantedPermSet()
    {
        if (_allGrantedPermSet != null) return _allGrantedPermSet;
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        var tVdp = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.VirtualDataProvider")!;
        var fPermSet = tVdp.GetField("PermissionSet",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        _allGrantedPermSet = fPermSet.GetValue(null)!;
        return _allGrantedPermSet;
    }

    // Overload: GetPermissionSet(NavApplicationObjectBase, int, ApplicationObjectId)
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavSession_GetPermissionSet_ByObjectId(
        object self, object callingObject, int companyNameToken,
        Microsoft.Dynamics.Nav.Types.ApplicationObjectId applicationObjectId)
        => GetAllGrantedPermSet();

    // Overload: GetPermissionSet(NavApplicationObjectBase, int, IEnumerable<ApplicationObjectId>)
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavSession_GetPermissionSet_ByObjectIds(
        object self, object callingObject, int companyNameToken, object applicationObjects)
        => GetAllGrantedPermSet();
}
