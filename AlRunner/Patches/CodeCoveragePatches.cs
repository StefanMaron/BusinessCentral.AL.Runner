// CodeCoveragePatches — gives the skeleton NCLMetadata the ALCodeEnvironment BC's code-coverage
// recorder is built over, so AL's CODECOVERAGELOG(TRUE) starts BC's own recorder (#4468).
//
// OBSERVABLY EQUIVALENT: the environment is BC's own ALCodeEnvironment, built through its public
// constructor with the same two delegates NCLMetadata's constructor passes (the only FieldWrite of
// NCLMetadata.codeEnvironment in Ncl.dll): a meta-object getter that calls this NCLMetadata's
// GetMetaApplicationObject(id, requireCompiled: true) and maps NavNCLMetadataCSharpErrorException
// to null, and NavGlobal.NavAppGroupResolver.GetExistingGroupById. Every recorder, listener and
// scope-id step after that is BC's unmodified code. Settled by corpus codeunit 60339
// ("Test Code Coverage Start Stop"), whose four arms pass on a real service tier.
//
// TRAP: the constructor is picked by its first two parameter types, not by arity alone — BC
// defaults the remaining three and a future overload could reorder them.

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static class CodeCoveragePatches
{
    private const string DocLink = "docs/limitations.md#code-coverage-virtual-tables";

    private static object? _nclMetadata;
    private static MethodInfo? _getMetaApplicationObject;
    private static object? _emitVersionDefault;

    /// <summary>
    /// Assign a real <c>ALCodeEnvironment</c> to <paramref name="nclMetadata"/>'s
    /// <c>codeEnvironment</c> field. Throws when BC's shape does not match: a skeleton left with a
    /// null environment is the state that made CODECOVERAGELOG(TRUE) unreachable, and it must not
    /// come back silently on a new BC build.
    /// </summary>
    internal static void SeedCodeEnvironment(object nclMetadata, Assembly navNcl)
    {
        var nclMetadataType = nclMetadata.GetType();
        var envType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALCodeEnvironment")
            ?? throw ShapeGap("type Microsoft.Dynamics.Nav.Runtime.ALCodeEnvironment not found");
        var field = nclMetadataType.GetField("codeEnvironment", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw ShapeGap("field NCLMetadata.codeEnvironment not found");

        var getMeta = nclMetadataType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == "GetMetaApplicationObject"
                && m.GetParameters() is { Length: 3 } ps
                && ps[0].ParameterType.Name == "ApplicationObjectId"
                && ps[1].ParameterType == typeof(bool)
                && ps[2].ParameterType == typeof(int))
            ?? throw ShapeGap("NCLMetadata.GetMetaApplicationObject(ApplicationObjectId, bool, int) not found");
        var emitVersion = getMeta.GetParameters()[2];

        var ctor = envType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SingleOrDefault(c => c.GetParameters() is { Length: >= 2 } ps
                && IsFunc(ps[0].ParameterType, "ApplicationObjectId", "NCLMetaApplicationObject")
                && IsFunc(ps[1].ParameterType, "Int32", "NavAppGroup")
                && ps.Skip(2).All(p => p.IsOptional))
            ?? throw ShapeGap("ALCodeEnvironment(Func<ApplicationObjectId, NCLMetaApplicationObject>, "
                + "Func<int, NavAppGroup>, ...) constructor not found");

        _nclMetadata = nclMetadata;
        _getMetaApplicationObject = getMeta;
        _emitVersionDefault = emitVersion.HasDefaultValue ? emitVersion.DefaultValue : 0;

        var ctorParams = ctor.GetParameters();
        var args = new object?[ctorParams.Length];
        args[0] = MakeFunc(ctorParams[0].ParameterType, nameof(GetMetaApplicationObjectForCodeEnvironment));
        args[1] = MakeFunc(ctorParams[1].ParameterType, nameof(GetExistingAppGroupById));
        for (int i = 2; i < ctorParams.Length; i++)
            args[i] = ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : null;

        FieldPoke.SetInstance(field, nclMetadata, ctor.Invoke(args));
    }

    /// <summary>BC's getter: <c>GetMetaApplicationObject(id, requireCompiled: true)</c>, with a
    /// <c>NavNCLMetadataCSharpErrorException</c> answered as null.</summary>
    public static object? GetMetaApplicationObjectForCodeEnvironment(object applicationObjectId)
    {
        object? meta;
        try
        {
            meta = _getMetaApplicationObject!.Invoke(_nclMetadata,
                new[] { applicationObjectId, true, _emitVersionDefault });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            if (tie.InnerException.GetType().Name == "NavNCLMetadataCSharpErrorException")
                return null;
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
        if (meta != null)
            EnsureMethodNumberCounter(meta);
        return meta;
    }

    /// <summary>
    /// BC's NCLMetaApplicationObject.Populate ends with <c>methodNumberCounter ??= new
    /// MethodNumberCounter()</c> on first metadata load; the runner's Populate is a Cecil no-op
    /// (NclCecilRewrite.Runtime.cs, Batch 7), so its metas arrive without one and
    /// ALCodeEnvironment.AssignScopeId dereferences the null for every AL method scope. The
    /// counter is per-object lifetime state: it is created once and never replaced, as in BC.
    /// </summary>
    private static void EnsureMethodNumberCounter(object meta)
    {
        var baseType = meta.GetType();
        while (baseType != null && baseType.Name != "NCLMetaApplicationObject")
            baseType = baseType.BaseType;
        var field = baseType?.GetField("methodNumberCounter", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw ShapeGap("field NCLMetaApplicationObject.methodNumberCounter not found");
        lock (meta)
        {
            if (field.GetValue(meta) == null)
                FieldPoke.SetInstance(field, meta, Activator.CreateInstance(field.FieldType, nonPublic: true));
        }
    }

    /// <summary>BC's getter: <c>NavGlobal.NavAppGroupResolver.GetExistingGroupById</c>, read at
    /// call time. A runner with no resolver refuses rather than answering a null group, which BC's
    /// callers would read as "the base group".</summary>
    public static object? GetExistingAppGroupById(object groupId)
    {
        var navGlobal = _getMetaApplicationObject!.DeclaringType!.Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NavGlobal");
        var resolver = navGlobal?.GetProperty("NavAppGroupResolver",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (resolver == null)
            throw new RunnerOutOfScopeException("ALCodeEnvironment.GetExtensionMetadataForObject",
                "not-yet-implemented — the runner has no NavGlobal.NavAppGroupResolver to resolve an "
                + "app group through", DocLink);
        var get = resolver.GetType().GetMethod("GetExistingGroupById",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw ShapeGap("NavAppGroupResolver.GetExistingGroupById not found");
        try
        {
            return get.Invoke(resolver, new[] { groupId });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Prepended to CodeCoverageDataProvider.ComputeRecordsForObject: the Code Coverage
    /// (2000000049) line rows are BC's projection of the object's AL SOURCE TEXT, which BC reads
    /// from the application database and the runner has no store for. Recording, the hit counts,
    /// and every other read of the coverage tables stay BC's own code.
    /// </summary>
    public static void RefuseCodeCoverageLineRows()
        => throw new RunnerOutOfScopeException("Record \"Code Coverage\" (2000000049) line rows",
            "not-yet-implemented — the rows are built from each covered object's AL source text, "
            + "which BC reads from the application database (ALCodeEnvironment.GetSourceCodeLines) "
            + "and the runner has no store for. Recording itself works: CODECOVERAGELOG(TRUE) "
            + "starts BC's own recorder and CODECOVERAGELOG() reports it", DocLink);

    private static bool IsFunc(Type t, string argName, string resultName)
        => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Func<,>)
           && t.GetGenericArguments()[0].Name == argName
           && t.GetGenericArguments()[1].Name == resultName;

    private static Delegate MakeFunc(Type funcType, string helperName)
    {
        var helper = typeof(CodeCoveragePatches).GetMethod(helperName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"[CodeCoverage] CodeCoveragePatches.{helperName} not found");
        var types = funcType.GetGenericArguments();
        var x = Expression.Parameter(types[0], "x");
        var call = Expression.Call(helper, Expression.Convert(x, typeof(object)));
        return Expression.Lambda(funcType, Expression.Convert(call, types[1]), x).Compile();
    }

    private static InvalidOperationException ShapeGap(string what)
        => new($"[CodeCoverage] {what}. BC's ALCodeEnvironment/NCLMetadata shape has changed; the "
            + "skeleton NCLMetadata would keep a null code environment and AL's CODECOVERAGELOG(TRUE) "
            + "would fail inside BC's recorder constructor. See AlRunner/Patches/CodeCoveragePatches.cs "
            + "and issue #4468.");
}
