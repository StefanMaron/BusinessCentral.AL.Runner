// ReflectionDrivenHelperLivenessTests — issue #3100.
//
// THE FAILURE MODE THIS EXISTS TO CATCH
//   A test that reaches a private production helper through reflection cannot tell whether
//   production code reaches it too. PermissionMetadataShapeGapTests drives four helpers on
//   RecordPatches by name — real production code, no BC install required, which is exactly why
//   the idiom is used — and two of them (SetBackingField, SetEmptyListBackingField) had no
//   production caller at all. They were superseded inside PR #2921 itself: commit 793ab838 poked
//   NCLMetaPermissionSet's backing fields by hand, commit 75630e98 replaced that with BC's own
//   AssignFromMetaPermissionSet and removed the call sites but not the helpers. Squash-merge hid
//   the intermediate state, so `git log -S` showed one hit and the pair read as freshly landed.
//   Their four arms passed the whole time, because a reflection call site IS a call site as far
//   as the test is concerned.
//
// HOW LIVENESS IS MEASURED
//   From IL, not from grep. Every method declared on RecordPatches (and its nested types, which
//   is where lambdas compile to) is decoded with the real opcode table, and every call /
//   callvirt / newobj / ldftn / ldvirtftn / jmp token is resolved back to a method. Reachability
//   is then a BFS from the methods production code outside this partial class can actually enter
//   through — anything not `private` — plus every nested-type method, which is the conservative
//   direction: it can only ever call a dead helper ALIVE, never a live one dead.
//
//   A grep would have counted the declaration line, the doc comment, and the dead helper's own
//   call to SetBackingField as evidence of life. All three are exactly what was wrong.
//
//   IL alone is not enough in the other direction either: production enters some members BY NAME
//   rather than by a call. RecordPatches.QueryJoin.cs binds twelve adapters through
//   Delegate.CreateDelegate over nameof(...), which compiles to a ldstr and NOT a ldftn, so a
//   pure call graph calls Join_OutOfScope dead when it is live on every query-join refusal —
//   measured, it was this guard's only false positive across the whole test suite. A member
//   PRODUCTION names as a literal is therefore a root too. That is the same blind spot
//   CLAUDE.md records for hooks: an orphaned registration and a live one look identical
//   statically, so the analysis has to be conservative wherever a name is the entry point.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class ReflectionDrivenHelperLivenessTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));


    [Fact]
    public void EveryRecordPatchesHelperDrivenByReflectionFromTests_IsReachableFromProductionCode()
    {
        var target = typeof(RecordPatches);
        var byName = DeclaredMethods(target)
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Every test in the suite, not just the file #3100 came from: a member driven by name
        // is invisible to the compiler wherever it is driven from.
        var testDir = Path.Combine(RepoRoot, "AlRunner.Tests");
        Assert.True(Directory.Exists(testDir), $"AlRunner.Tests not found at {testDir}.");

        var exercised = new SortedSet<string>(StringComparer.Ordinal);
        var whereNamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(testDir, "*.cs", SearchOption.AllDirectories))
            foreach (var name in MemberNamesQuotedIn(file, byName.Keys))
                if (exercised.Add(name)) whereNamed[name] = Path.GetFileName(file);

        // Anti-vacuity: an extraction that found nothing would make the offender list empty for
        // the wrong reason.
        Assert.True(exercised.Count >= 5,
            "no RecordPatches member names were found quoted anywhere in AlRunner.Tests — the "
            + "scan stopped working, it did not find a clean suite. Found: "
            + (exercised.Count == 0 ? "(none)" : string.Join(", ", exercised)));

        var reachable = ReachableFromOutsideThePartialClass(target, NamedByProductionSource(byName.Keys));

        var dead = exercised
            .Where(n => byName[n].All(m => !reachable.Contains(m.MetadataToken)))
            .ToList();

        Assert.True(dead.Count == 0,
            "these RecordPatches members are driven by a test through reflection but are not "
            + "reachable from any production entry point, so their arms prove nothing about a "
            + "live path (#3100): "
            + string.Join(", ", dead.Select(n => $"{n} (named in {whereNamed[n]})")) + Environment.NewLine
            + "Either wire the member — the fix is then the missing call site, proved by a test "
            + "of the behaviour that call site produces — or delete it together with its arms.");

        // Anti-vacuity, the other direction: the analysis must be able to say ALIVE, or an
        // implementation that marked everything reachable would pass this test unchanged.
        Assert.Contains("SetProperty", exercised);
        Assert.True(byName["SetProperty"].Any(m => reachable.Contains(m.MetadataToken)),
            "SetProperty is called from BuildMetaPermissionSet on the live populate path, so a "
            + "liveness analysis that cannot see it is broken rather than strict.");
    }

    // ── liveness ─────────────────────────────────────────────────────────────────────────

    private static IEnumerable<MethodInfo> DeclaredMethods(Type t) => t.GetMethods(
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly);

    private static IEnumerable<MethodBase> DeclaredMethodsAndCtors(Type t)
    {
        const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public
                                 | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var m in t.GetMethods(All)) yield return m;
        foreach (var c in t.GetConstructors(All)) yield return c;
    }

    private static HashSet<int> ReachableFromOutsideThePartialClass(Type target, ISet<string> namedByProduction)
    {
        var universe = new Dictionary<int, MethodBase>();
        foreach (var m in DeclaredMethodsAndCtors(target)) universe[m.MetadataToken] = m;
        foreach (var nested in target.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var m in DeclaredMethodsAndCtors(nested)) universe[m.MetadataToken] = m;

        var seen = new HashSet<int>();
        var queue = new Queue<MethodBase>();
        foreach (var m in universe.Values)
        {
            // Roots: anything outside this partial class could call — plus every nested-type
            // method, since a lambda body's caller is the compiler, not a name we can follow —
            // plus anything production names as a literal, which is how the QueryJoin adapters
            // and the Cecil-installed patches are entered.
            var isRoot = m.DeclaringType != target || !m.IsPrivate || namedByProduction.Contains(m.Name);
            if (isRoot && seen.Add(m.MetadataToken)) queue.Enqueue(m);
        }

        while (queue.Count > 0)
            foreach (var callee in CalleeTokensOf(queue.Dequeue()))
                if (universe.TryGetValue(callee, out var next) && seen.Add(callee)) queue.Enqueue(next);

        return seen;
    }

    private static IEnumerable<int> CalleeTokensOf(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (Exception) { il = null; }
        if (il == null) yield break;

        var module = method.Module;
        var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        var pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE)
            {
                if (pos >= il.Length) yield break;
                value = (short)(0xFE00 | il[pos++]);
            }
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break;   // refuse to guess

            var operandSize = OperandSize(op.OperandType, il, pos);
            if (operandSize < 0 || pos + operandSize > il.Length) yield break;

            if (IsCallLike(op) && operandSize == 4)
            {
                var token = BitConverter.ToInt32(il, pos);
                MethodBase? callee = null;
                try { callee = module.ResolveMethod(token, typeArgs, methodArgs); }
                catch (Exception) { /* not a methoddef/ref we can follow */ }
                if (callee != null)
                {
                    var def = callee is MethodInfo { IsGenericMethod: true } mi
                        ? mi.GetGenericMethodDefinition()
                        : callee;
                    yield return def.MetadataToken;
                }
            }

            pos += operandSize;
        }
    }

    private static bool IsCallLike(OpCode op) =>
        op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj
        || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn || op == OpCodes.Jmp;

    private static int OperandSize(OperandType t, byte[] il, int pos) => t switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => pos + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, pos) : -1,
        _ => -1,
    };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g => g.First());

    // ── what a test file names ───────────────────────────────────────────────────────────

    /// <summary>
    /// The source of a file with whole-line comments removed. A doc comment is prose, and prose
    /// must not vote: <c>&lt;see cref="SetProperty"/&gt;</c> looks exactly like a quoted member
    /// name to a regex, and counting it would have made this guard's liveness root set — and its
    /// own anti-vacuity control — satisfiable by a sentence about the code.
    /// </summary>
    private static string CodeLinesOf(string path) => string.Join(Environment.NewLine,
        File.ReadAllLines(path).Where(l =>
        {
            var t = l.TrimStart();
            return !t.StartsWith("//", StringComparison.Ordinal)
                   && !t.StartsWith("/*", StringComparison.Ordinal)
                   && !t.StartsWith("*", StringComparison.Ordinal);
        }));

    /// <summary>
    /// Member names production itself spells out — <c>nameof(X)</c> (which compiles to a plain
    /// string, leaving no call edge) or a quoted <c>"X"</c> handed to a reflection lookup. Every
    /// one is an entry point a call graph cannot see, so every one is a root.
    /// </summary>
    private static HashSet<string> NamedByProductionSource(IEnumerable<string> candidates)
    {
        var known = new HashSet<string>(candidates, StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.Combine(RepoRoot, "AlRunner");
        Assert.True(Directory.Exists(root), $"AlRunner sources not found at {root}.");

        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var src = CodeLinesOf(file);
            foreach (Match m in Regex.Matches(src, "\"([A-Za-z_][A-Za-z0-9_]*)\""))
                if (known.Contains(m.Groups[1].Value)) named.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(src, @"\bnameof\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)"))
                if (known.Contains(m.Groups[1].Value)) named.Add(m.Groups[1].Value);
        }
        return named;
    }

    /// <summary>
    /// Member names of <paramref name="candidates"/> that appear as string LITERALS in the test
    /// source — the only way a test can name a private member. Prose in a comment does not
    /// count, which matters: the file's own banner lists all four helpers by name.
    /// </summary>
    private static IEnumerable<string> MemberNamesQuotedIn(string path, IEnumerable<string> candidates)
    {
        var known = new HashSet<string>(candidates, StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(CodeLinesOf(path), "\"([A-Za-z_][A-Za-z0-9_]*)\""))
            if (known.Contains(m.Groups[1].Value)) yield return m.Groups[1].Value;
    }
}
