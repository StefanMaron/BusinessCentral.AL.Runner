// AlCompilerStreamPatches — replacement for ALCompiler.DotNetToNavOutStream.
//
// The real body (Ncl.dll, runtime engine — ours to patch) is:
//
//     public static NavOutStream DotNetToNavOutStream(ITreeObject parentOfResult, NavDotNet obj)
//     {
//         if (obj == null) return NavOutStream.Default(parentOfResult);
//         if (obj.Value is Stream stream)
//             return new NavOutStream(parentOfResult,
//                 new NavStreamProvider(stream, parentOfResult.Tree.Session.Company.SharedObjects));
//         throw new NavNCLConversionException(obj.GetType(), typeof(NavOutStream));
//     }
//
// On the headless skeleton `Session.Company` / `Company.SharedObjects` is null, so any
// AL code that marshals a .NET Stream into an OutStream dies with an NRE (or an
// ArgumentNullException from the TreeObject base ctor when Company exists but
// SharedObjects is null). The hottest consumer is System Application CU 1279
// "Cryptography Management Impl." GenerateHash(InStream, HashAlgorithmType), which wraps
// a .NET MemoryStream in a NavDotNet and calls this method before hashing.
//
// SCOPE AUDIT (loud-failures rule): this replacement is observably equivalent to the
// real BC behaviour for in-scope test code. It reproduces the real body's three branches
// exactly (null → Default, Stream → NavOutStream over a NavStreamProvider, anything else
// → NavNCLConversionException with the same argument types). The ONLY divergence is the
// shared-object container the NavStreamProvider is parented to: the real session
// container when present, otherwise the same process-wide skeleton
// TreeSharedObjectContainer every other stream/record Target patch in this runner uses
// (NavRecordRef.get_Target, NavStream.get_Target, ...). The container only governs
// tree-disposal bookkeeping, never stream content — the AL-observable bytes are the
// real .NET stream's bytes.
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    private static PropertyInfo? _pNavDotNetValue;
    private static MethodInfo? _mNavOutStreamDefault;
    private static ConstructorInfo? _ctorNavStreamProviderFromStream;
    private static ConstructorInfo? _ctorNavOutStream;
    private static ConstructorInfo? _ctorNavNclConversionException;

    private static Assembly NclAssembly()
        => AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object ALCompiler_DotNetToNavOutStream(object parentOfResult, object? obj)
    {
        var navNcl = NclAssembly();
        var tNavOutStream = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavOutStream")!;
        var tITreeObject = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;

        if (obj == null)
        {
            _mNavOutStreamDefault ??= tNavOutStream.GetMethod("Default",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { tITreeObject }, null)!;
            return _mNavOutStreamDefault.Invoke(null, new[] { parentOfResult })!;
        }

        _pNavDotNetValue ??= obj.GetType().GetProperty("Value",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var value = _pNavDotNetValue.GetValue(obj);
        if (value is System.IO.Stream stream)
        {
            var container = ResolveSharedObjectContainer(parentOfResult);
            if (_ctorNavStreamProviderFromStream == null)
            {
                var tProvider = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavStreamProvider")!;
                var tIContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
                _ctorNavStreamProviderFromStream = tProvider.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(System.IO.Stream), tIContainer }, null)!;
            }
            var provider = _ctorNavStreamProviderFromStream.Invoke(new object[] { stream, container });
            if (_ctorNavOutStream == null)
            {
                var tINavStreamProvider = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.INavStreamProvider")!;
                _ctorNavOutStream = tNavOutStream.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { tITreeObject, tINavStreamProvider }, null)!;
            }
            return _ctorNavOutStream.Invoke(new[] { parentOfResult, provider })!;
        }

        // Faithful to the real body: unsupported inner value → NavNCLConversionException.
        if (_ctorNavNclConversionException == null)
        {
            var tEx = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLConversionException"))
                .First(t => t != null)!;
            _ctorNavNclConversionException = tEx.GetConstructor(new[] { typeof(Type), typeof(Type) })!;
        }
        throw (Exception)_ctorNavNclConversionException.Invoke(new object[] { obj.GetType(), tNavOutStream });
    }

    // Resolve the ITreeSharedObjectContainer the real body reads from
    // parentOfResult.Tree.Session.Company.SharedObjects — preferring the REAL chain when
    // it is populated, falling back to the process-wide skeleton container (the same one
    // NavRecordRef.get_Target / NavStream.get_Target et al. use) when any link is null.
    private static object ResolveSharedObjectContainer(object parentOfResult)
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        var tree = parentOfResult.GetType().GetProperty("Tree", Flags)?.GetValue(parentOfResult);
        var session = tree?.GetType().GetProperty("Session", Flags)?.GetValue(tree);
        var company = session?.GetType().GetProperty("Company", Flags)?.GetValue(session);
        var shared = company?.GetType().GetProperty("SharedObjects", Flags)?.GetValue(company);
        if (shared != null) return shared;

        if (_skeletonSharedObjectContainer == null)
        {
            var navNcl = NclAssembly();
            var tContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeSharedObjectContainer")!;
            var tITree = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
            _skeletonSharedObjectContainer = tContainer.GetConstructor(new[] { tITree })!
                .Invoke(new object?[] { RootTreeStub });
        }
        return _skeletonSharedObjectContainer;
    }
}
