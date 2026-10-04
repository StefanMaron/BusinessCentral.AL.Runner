// SqlConnectionBackstopTests — issue #5190.
//
// NavSqlConnectionScope.TryOpenConnection opens with `database.DatabaseServer.SqlConnectionProvider
// .GetConnection(...)`. The runner's skeleton database has no database server, so every connection
// request died with an unnamed NullReferenceException. The Cecil rewrite
// (NclCecilRewrite.SqlConnection.cs) prepends SqlConnectionPatches.RefuseWithoutDatabaseServer,
// which refuses by name when — and only when — that dereference would have failed.
//
// Every claim here is about OUR rewrite and OUR refusal; what BC does with a live SQL Server is not
// in question. The AL-visible half is tests/runner-extras/sql-connection-oos.
//
// Two things are pinned in both directions: the refusal fires on exactly the two null conditions
// (no server, no provider) and falls through to BC's own body otherwise, and the rewrite refuses at
// startup when BC's shape moves rather than leaving the NRE in place.
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

// Loads Ncl types in-process, and the IL test reads the image BcEngineBootstrap has already
// rewritten — so it shares the serial bc-engine collection.
[Collection(BcEngineCollection.Name)]
public class SqlConnectionBackstopTests
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Rt = "Microsoft.Dynamics.Nav.Runtime.";

    private readonly BcEngineFixture _engine;

    public SqlConnectionBackstopTests(BcEngineFixture engine) => _engine = engine;

    private void RequireEngine()
        => TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    // ── The database shapes the guard has to tell apart ─────────────────────────────────

    private static void SetField(object target, string name, object? value)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (f is null) continue;
            f.SetValue(target, value);
            return;
        }
        throw new InvalidOperationException($"{target.GetType().Name}.{name} not found — BC's layout moved");
    }

    private static NavDatabase DatabaseWith(bool server, bool provider)
    {
        var db = (NavDatabase)RuntimeHelpers.GetUninitializedObject(typeof(NavDatabase));
        if (!server) return db;
        var srv = RuntimeHelpers.GetUninitializedObject(typeof(NavDatabaseServer));
        if (provider)
            SetField(srv, "sqlConnectionProvider", RuntimeHelpers.GetUninitializedObject(typeof(NavSqlConnectionProvider)));
        SetField(db, "databaseServer", srv);
        return db;
    }

    private static MethodInfo TryOpenConnection()
        => typeof(NavSqlConnectionScope).GetMethod("TryOpenConnection", AnyInstance)
           ?? throw new InvalidOperationException("NavSqlConnectionScope.TryOpenConnection not found");

    /// <summary>Invoke BC's TryOpenConnection on an unconstructed scope; returns what it threw.</summary>
    private static Exception? InvokeTryOpen(NavDatabase db)
    {
        var scope = RuntimeHelpers.GetUninitializedObject(typeof(NavSqlConnectionScope));
        var ex = Record.Exception(() => TryOpenConnection().Invoke(scope, new object?[] { db, default(Microsoft.Dynamics.Nav.Types.TransactionType), null }));
        return (ex as TargetInvocationException)?.InnerException ?? ex;
    }

    // ── The guard: refuses on exactly the two null conditions ───────────────────────────

    [SkippableFact]
    public void Guard_RefusesAByNameNotYetImplementedWhenTheDatabaseHasNoServer()
    {
        RequireEngine();

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => SqlConnectionPatches.RefuseWithoutDatabaseServer(DatabaseWith(server: false, provider: false)));

        Assert.StartsWith("out-of-scope: SQL connection (requested by ", ex.Message);
        Assert.Equal("sql-connection", ex.DocAnchor);
        // "not-yet-implemented" is load-bearing: an AL [TryFunction] traps a PERMANENT refusal into
        // `false` (ApplicationObjectBasePatches.IsPermanentOutOfScope), and this backstop must keep
        // tearing through a try the way the NRE it replaces did.
        Assert.StartsWith("not-yet-implemented", ex.Reason);
        Assert.Contains("docs/scope.md#sql-connection", ex.Message);
    }

    [SkippableFact]
    public void Guard_RefusesWhenTheServerHasNoConnectionProvider()
    {
        RequireEngine();

        Assert.Throws<RunnerOutOfScopeException>(
            () => SqlConnectionPatches.RefuseWithoutDatabaseServer(DatabaseWith(server: true, provider: false)));
    }

    [SkippableFact]
    public void Guard_ReturnsWhenTheDatabaseHasAConnectionProvider()
    {
        RequireEngine();

        // The negative control: a database that CAN open a connection is none of the guard's business.
        SqlConnectionPatches.RefuseWithoutDatabaseServer(DatabaseWith(server: true, provider: true));
    }

    // ── The rewrite, observed through BC's own method ───────────────────────────────────

    [SkippableFact]
    public void TryOpenConnection_OnADatabaseWithNoServer_RefusesByNameInsteadOfAnNre()
    {
        RequireEngine();

        var ex = InvokeTryOpen(DatabaseWith(server: false, provider: false));

        var oos = Assert.IsType<RunnerOutOfScopeException>(ex);
        Assert.Contains("SQL connection", oos.Message);
        Assert.IsNotType<NullReferenceException>(ex);
    }

    [SkippableFact]
    public void TryOpenConnection_OnADatabaseWithAProvider_RunsBcsOwnBody()
    {
        RequireEngine();

        // The provider here is an unconstructed shell, so BC's own `GetConnection` fails inside it —
        // which is the point: the failure is BC's, not the guard's. A rewrite that refused every call
        // would turn this into a RunnerOutOfScopeException.
        var ex = InvokeTryOpen(DatabaseWith(server: true, provider: true));

        Assert.NotNull(ex);
        Assert.IsNotType<RunnerOutOfScopeException>(ex);
    }

    [SkippableFact]
    public void TryOpenConnection_StartsWithTheGuardCall_AndKeepsBcsBody()
    {
        RequireEngine();

        var nclPath = typeof(ITreeObject).Assembly.Location;
        var type = AssemblyDefinition.ReadAssembly(nclPath).MainModule.GetType(Rt + "NavSqlConnectionScope");
        var method = type.Methods.Single(m => m.Name == "TryOpenConnection");
        var il = method.Body.Instructions;

        // Slot 1 is `database` (slot 0 is `this`), pushed for the guard, then the guard is called.
        Assert.Contains(il[0].OpCode.Code, new[] { Code.Ldarg, Code.Ldarg_S, Code.Ldarg_1 });
        var call = Assert.IsAssignableFrom<MethodReference>(il[1].Operand);
        Assert.Equal(nameof(SqlConnectionPatches.RefuseWithoutDatabaseServer), call.Name);
        Assert.Equal(nameof(SqlConnectionPatches), call.DeclaringType.Name);

        // BC's own body is still behind it: the two dereferences the guard mirrors, then GetConnection.
        var called = il.Select(i => (i.Operand as MethodReference)?.Name).Where(n => n != null).ToList();
        Assert.Contains("get_DatabaseServer", called);
        Assert.Contains("get_SqlConnectionProvider", called);
        Assert.Contains("GetConnection", called);
    }

    // ── The shape check: BC moving the method must refuse at rewrite time, not re-open the NRE ──

    private sealed record Synthetic(AssemblyDefinition Asm, MethodDefinition? TryOpen);

    private static Synthetic BuildScope(bool withMethod = true, bool callsServer = true, bool callsProvider = true,
        bool twoOverloads = false)
    {
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("FakeNcl", new Version(1, 0)), "FakeNcl", ModuleKind.Dll);
        var m = asm.MainModule;

        TypeDefinition Add(string ns, string name)
        {
            var t = new TypeDefinition(ns, name, Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, m.TypeSystem.Object);
            m.Types.Add(t);
            return t;
        }

        MethodDefinition Getter(TypeDefinition on, string name, TypeReference returns)
        {
            var g = new MethodDefinition(name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, returns);
            var gil = g.Body.GetILProcessor();
            gil.Append(gil.Create(OpCodes.Ldnull));
            gil.Append(gil.Create(OpCodes.Ret));
            on.Methods.Add(g);
            return g;
        }

        var db = Add("Microsoft.Dynamics.Nav.Runtime", "NavDatabase");
        var server = Add("Microsoft.Dynamics.Nav.Runtime", "NavDatabaseServer");
        var provider = Add("Microsoft.Dynamics.Nav.Runtime", "NavSqlConnectionProvider");
        var conn = Add("Microsoft.Dynamics.Nav.Runtime", "NavSqlConnection");
        var txn = Add("Microsoft.Dynamics.Nav.Types", "TransactionType");
        var getServer = Getter(db, "get_DatabaseServer", server);
        var getProvider = Getter(server, "get_SqlConnectionProvider", provider);
        var scope = Add("Microsoft.Dynamics.Nav.Runtime", "NavSqlConnectionScope");

        MethodDefinition? tryOpen = null;
        for (var n = 0; n < (twoOverloads ? 2 : withMethod ? 1 : 0); n++)
        {
            tryOpen = new MethodDefinition("TryOpenConnection",
                Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.HideBySig, m.TypeSystem.Boolean);
            tryOpen.Parameters.Add(new ParameterDefinition("database", Mono.Cecil.ParameterAttributes.None, db));
            tryOpen.Parameters.Add(new ParameterDefinition("transactionType", Mono.Cecil.ParameterAttributes.None, txn));
            tryOpen.Parameters.Add(new ParameterDefinition("last", Mono.Cecil.ParameterAttributes.None, conn));
            var il = tryOpen.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_1));
            if (callsServer) il.Append(il.Create(OpCodes.Callvirt, getServer));
            if (callsProvider) il.Append(il.Create(OpCodes.Callvirt, getProvider));
            il.Append(il.Create(OpCodes.Pop));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ret));
            scope.Methods.Add(tryOpen);
        }
        return new Synthetic(asm, tryOpen);
    }

    [SkippableFact]
    public void Rewrite_PrependsTheGuardAndLeavesBcsInstructionsInPlace()
    {
        RequireEngine();
        var s = BuildScope();
        var before = s.TryOpen!.Body.Instructions.Select(i => i.OpCode.Code).ToList();

        NclCecilRewrite.RewriteNcl_SqlConnection(s.Asm);

        var after = s.TryOpen.Body.Instructions;
        Assert.Equal(before.Count + 2, after.Count);
        Assert.Equal(nameof(SqlConnectionPatches.RefuseWithoutDatabaseServer), ((MethodReference)after[1].Operand).Name);
        Assert.Equal(before, after.Skip(2).Select(i => i.OpCode.Code).ToList());
    }

    [SkippableFact]
    public void Rewrite_RefusesWhenTheScopeTypeIsGone()
    {
        RequireEngine();
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("FakeNcl", new Version(1, 0)), "FakeNcl", ModuleKind.Dll);

        var ex = Assert.Throws<InvalidOperationException>(() => NclCecilRewrite.RewriteNcl_SqlConnection(asm));
        Assert.Contains("NavSqlConnectionScope", ex.Message);
        Assert.Contains("#5190", ex.Message);
    }

    [SkippableFact]
    public void Rewrite_RefusesWhenTryOpenConnectionIsGone()
    {
        RequireEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => NclCecilRewrite.RewriteNcl_SqlConnection(BuildScope(withMethod: false).Asm));
        Assert.Contains("TryOpenConnection", ex.Message);
        Assert.Contains("found 0", ex.Message);
    }

    [SkippableFact]
    public void Rewrite_RefusesWhenTryOpenConnectionIsAmbiguous()
    {
        RequireEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => NclCecilRewrite.RewriteNcl_SqlConnection(BuildScope(twoOverloads: true).Asm));
        Assert.Contains("found 2", ex.Message);
    }

    [SkippableTheory]
    [InlineData(false, true, "get_DatabaseServer")]
    [InlineData(true, false, "get_SqlConnectionProvider")]
    public void Rewrite_RefusesWhenBcStopsMakingTheDereferenceTheGuardMirrors(bool callsServer, bool callsProvider, string missing)
    {
        RequireEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => NclCecilRewrite.RewriteNcl_SqlConnection(BuildScope(callsServer: callsServer, callsProvider: callsProvider).Asm));
        Assert.Contains(missing, ex.Message);
    }

    // ── The naming: who asked, and which AL entry point it came in through ──────────────

    private static MethodBase Ncl(string type, string method)
    {
        var t = typeof(ITreeObject).Assembly.GetType(Rt + type) ?? throw new InvalidOperationException(type);
        return t.GetMethods(AnyInstance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                   .FirstOrDefault(m => m.Name == method) is { } found
            ? found
            : throw new InvalidOperationException($"{type}.{method} not found");
    }

    private static MethodBase StateMachineMoveNext(Type owner, string method)
    {
        var sm = owner.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                     .FirstOrDefault(t => t.Name.StartsWith("<" + method + ">", StringComparison.Ordinal))
                 ?? throw new InvalidOperationException($"no async state machine for {owner.Name}.{method}");
        return sm.GetMethod("MoveNext", AnyInstance)!;
    }

    [SkippableFact]
    public void Refusal_NamesTheFirstFrameOutsideTheScopeAndTheAlEntryPointBehindIt()
    {
        RequireEngine();

        // innermost first: scope plumbing, the BC type that asked, then the AL entry point
        var scopeFrame = typeof(NavSqlConnectionScope).GetMethod("TryOpenConnection", AnyInstance)!;
        var asker = Ncl("NavTaskScheduler+SqlDml", "RetrySqlAsync");
        var alEntry = typeof(ALTaskScheduler).GetMethod(nameof(ALTaskScheduler.ALTaskExistsAsync))!;

        var oos = SqlConnectionPatches.Refusal(new[] { scopeFrame, asker, alEntry });

        Assert.Equal("SQL connection (requested by NavTaskScheduler.SqlDml.RetrySqlAsync)", oos.Api);
        Assert.Contains("reached from the AL entry point ALTaskScheduler.ALTaskExistsAsync", oos.Reason);
        // The two names are different frames; neither is the scope plumbing.
        Assert.DoesNotContain("NavSqlConnectionScope", oos.Api);
    }

    [SkippableFact]
    public void Refusal_FoldsAnAsyncStateMachineBackIntoTheMethodThatWasWritten()
    {
        RequireEngine();

        var frame = StateMachineMoveNext(typeof(ALTaskScheduler), "ALTaskExistsAsync");

        var oos = SqlConnectionPatches.Refusal(new[] { frame });

        // `<ALTaskExistsAsync>d__N.MoveNext` names a compiler-generated type, not anything an author wrote.
        Assert.Equal("SQL connection (requested by ALTaskScheduler.ALTaskExistsAsync)", oos.Api);
        Assert.DoesNotContain("MoveNext", oos.Message);
        Assert.DoesNotContain("<", oos.Api);
    }

    [SkippableFact]
    public void Refusal_FoldsALambdaInsideAnAsyncMethod()
    {
        RequireEngine();

        // `<>c/<<CancelScheduledTaskAsync>b__20_4>d` — the first '>' closes the INNER name.
        var sqlDml = typeof(ITreeObject).Assembly.GetType(Rt + "NavTaskScheduler+SqlDml")!;
        var closure = sqlDml.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .Single(t => t.Name == "<>c");
        var lambda = closure.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .First(t => t.Name.StartsWith("<<CancelScheduledTaskAsync>", StringComparison.Ordinal))
            .GetMethod("MoveNext", AnyInstance)!;

        var oos = SqlConnectionPatches.Refusal(new[] { lambda });

        Assert.Equal("SQL connection (requested by NavTaskScheduler.SqlDml.CancelScheduledTaskAsync)", oos.Api);
    }

    [SkippableFact]
    public void Refusal_SaysSoWhenNoFrameCanBeNamed()
    {
        RequireEngine();

        var scopeOnly = new MethodBase?[] { null, typeof(NavSqlConnectionScope).GetMethod("TryOpenConnection", AnyInstance) };

        var oos = SqlConnectionPatches.Refusal(scopeOnly);

        Assert.Contains("an unidentified frame", oos.Api);
        Assert.DoesNotContain("reached from", oos.Reason);
    }

    [SkippableFact]
    public void Refusal_SkipsTheRunnersOwnFrames()
    {
        RequireEngine();

        var ownFrame = typeof(SqlConnectionPatches).GetMethod(nameof(SqlConnectionPatches.RefuseWithoutDatabaseServer))!;
        var asker = Ncl("NavTaskScheduler+SqlDml", "RetrySqlAsync");

        var oos = SqlConnectionPatches.Refusal(new[] { ownFrame, asker });

        Assert.Contains("NavTaskScheduler.SqlDml.RetrySqlAsync", oos.Api);
    }
}
