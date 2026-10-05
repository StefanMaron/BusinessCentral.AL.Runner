// SqlConnectionPatches — the named refusal behind NavSqlConnectionScope.TryOpenConnection (#5190).
//
// The runner has no SQL Server. Its skeleton NavDatabase is `GetUninitializedObject(NavDatabase)`
// (RecordPatches.cs) and nothing ever gives it a DatabaseServer, so BC's
// `database.DatabaseServer.SqlConnectionProvider.GetConnection(...)` — the first statement of
// NavSqlConnectionScope.TryOpenConnection — always dereferences null. That NRE names neither the AL
// surface nor a reason; loud-failures.md requires a refusal that does.
//
// Two surfaces were fixed one by one before this existed: TaskScheduler.TaskExists/CancelTask (#2866,
// TaskSchedulerPatches) and raising an [ExternalBusinessEvent] (#5149, which stops the event path
// before it opens a connection). This is the BACKSTOP for every Ncl path nobody has reported yet. It
// does not replace a per-surface fix: a surface the runner can serve faithfully is still fixed where
// it is, and runs before this is ever reached.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static class SqlConnectionPatches
{
    // Reason and anchor stay separate: RunnerOutOfScopeException appends " — see docs/scope.md#<anchor>"
    // itself, so a reason that spelled the doc out would render it twice (#2766).
    private const string Anchor = "sql-connection";

    private const string BcNamespace = "Microsoft.Dynamics.Nav.Runtime";

    /// <summary>
    /// Prepended (Cecil) to <c>NavSqlConnectionScope.TryOpenConnection(NavDatabase, TransactionType,
    /// NavSqlConnection)</c>; returns, leaving BC's body to run, whenever the database has a SQL
    /// connection provider.
    ///
    /// <para>Observably equivalent to what it replaces: the two conditions tested are exactly the two
    /// dereferences that open BC's body (<c>get_DatabaseServer</c>, then
    /// <c>get_SqlConnectionProvider</c>), so it fires only where BC would already have thrown a
    /// NullReferenceException — it changes the exception, never an outcome. A database that does have a
    /// provider is untouched. Citation: that IL hashes identically in every Ncl build cached on the
    /// measuring box (27.0 through 28.5), and the corpus and runner-extras change no result (PR for
    /// #5190). Trap: re-check both if a BC build moves <c>TryOpenConnection</c>; the Cecil shape
    /// assertion refuses first.</para>
    /// </summary>
    public static void RefuseWithoutDatabaseServer(NavDatabase database)
    {
        if (database?.DatabaseServer?.SqlConnectionProvider != null)
            return;
        // skipFrames 1: this helper's own frame names nothing.
        var frames = new StackTrace(skipFrames: 1, fNeedFileInfo: false).GetFrames().Select(f => f.GetMethod());
        throw Refusal(frames);
    }

    /// <summary>
    /// The refusal for a connection request made from <paramref name="frames"/> (innermost first).
    /// Separate from <see cref="RefuseWithoutDatabaseServer"/> so the naming can be tested with chosen frames.
    /// </summary>
    internal static RunnerOutOfScopeException Refusal(IEnumerable<MethodBase?> frames)
    {
        string? requester = null;
        string? alEntry = null;
        foreach (var frame in frames)
        {
            var id = Identify(frame);
            if (id is null || IsConnectionPlumbing(id.Value)) continue;
            requester ??= $"{id.Value.Type}.{id.Value.Method}";
            if (alEntry is null && IsAlEntryPoint(id.Value))
                alEntry = $"{id.Value.Type}.{id.Value.Method}";
            if (alEntry is not null) break;
        }

        var asker = requester ?? "an unidentified frame";
        // Reason starts with "not-yet-implemented" ON PURPOSE: an AL [TryFunction] traps a PERMANENT
        // refusal into `false` (ApplicationObjectBasePatches.IsPermanentOutOfScope), and this backstop
        // cannot know that every surface behind it is permanent. Trapped, it would turn the NRE that
        // used to tear through the try into a quiet `false`. Not trapped, it keeps tearing through,
        // now with a name. (A guarded Codeunit.Run swallows every exception regardless: #5342.)
        var reason =
            "not-yet-implemented — the runner has no SQL Server: the skeleton database carries no database "
            + "server and no SQL connection provider, and this surface has no answer of its own that works "
            + "without one"
            + (alEntry is null || alEntry == requester ? "" : $"; reached from the AL entry point {alEntry}");
        return new RunnerOutOfScopeException($"SQL connection (requested by {asker})", reason, Anchor);
    }

    private readonly record struct FrameId(string? Namespace, string Type, string Method);

    /// <summary>
    /// The BC type and method a frame belongs to, with compiler-generated holders folded back into the
    /// method that was written: an async state machine <c>&lt;Foo&gt;d__12.MoveNext</c>, a closure's
    /// <c>&lt;Foo&gt;b__0</c> and a local function <c>&lt;Foo&gt;g__Bar|3_0</c> all name <c>Foo</c> on
    /// the type that declares it. The namespace is returned separately and left out of the message.
    /// </summary>
    private static FrameId? Identify(MethodBase? method)
    {
        var type = method?.DeclaringType;
        if (method is null || type is null) return null;

        var name = method.Name;
        while (type.IsNested && type.DeclaringType is { } outer && type.Name.StartsWith('<'))
        {
            var fromType = GeneratedMethodName(type.Name);
            if (fromType is not null) name = fromType;
            else if (GeneratedMethodName(name) is { } fromMethod) name = fromMethod;
            type = outer;
        }
        if (GeneratedMethodName(name) is { } folded) name = folded;

        var chain = new List<string>();
        for (var t = type; t is not null; t = t.DeclaringType)
            chain.Insert(0, StripArity(t.Name));
        return new FrameId(type.Namespace, string.Join('.', chain), name);
    }

    /// <summary><c>&lt;Foo&gt;d__12</c> → <c>Foo</c>; <c>&lt;&gt;c__DisplayClass1_0</c> and plain names → null.</summary>
    private static string? GeneratedMethodName(string name)
    {
        if (name.Length < 3 || name[0] != '<') return null;
        var end = name.IndexOf('>');
        // A lambda inside an async method is `<<Foo>b__20_4>d`: the first '>' closes the inner name.
        return end > 1 ? name[1..end].TrimStart('<') : null;
    }

    private static string StripArity(string typeName)
    {
        var tick = typeName.IndexOf('`');
        return tick < 0 ? typeName : typeName[..tick];
    }

    /// <summary>The scope types themselves and this runner's own frames ask for nothing.</summary>
    private static bool IsConnectionPlumbing(FrameId id)
        => id.Type.StartsWith("NavSqlConnection", StringComparison.Ordinal)
           || (id.Namespace?.StartsWith("AlRunner", StringComparison.Ordinal) ?? false);

    /// <summary>An <c>AL*</c> static on an <c>AL*</c> BC runtime type — the seam AL statements enter BC through.</summary>
    private static bool IsAlEntryPoint(FrameId id)
        => id.Namespace == BcNamespace
           && id.Type.StartsWith("AL", StringComparison.Ordinal)
           && id.Method.StartsWith("AL", StringComparison.Ordinal);
}
