// TestMethodOrderingContractTests — the order a codeunit's [Test] procedures run in must be a
// function of the METHODS, never of the array `Type.GetMethods()` happened to hand back (#3201).
//
// WHY THIS IS A CONTRACT TEST AND NOT AN END-TO-END ONE. `OrderTestMethodsBySourceDeclaration`
// sorts by the AL declaration line recovered from each procedure's `{Name}_Scope_<hash>` nested
// type (#1766). When every line resolves — which is what an AL fixture produces, every time —
// the result is fully determined and the fallback below is unreachable. The defect lives in the
// two paths where a line does NOT resolve, so the only way to observe it is to hand the
// comparator a line map that is empty or partial. `TestCodeunitExecutionOrderTests` covers the
// resolved path end to end through the AL compiler and stays the guard for that.
//
// WHY IT CANNOT PASS BY LUCK. `Type.GetMethods()` has no defined order — that is the whole
// defect — so a test that fed the methods in one fixed sequence and asserted one expected
// sequence would be asserting this box's reflection layout. Every case below instead runs ALL
// permutations of the input and requires a single answer across them: an implementation that
// carries any input-order dependence through to its output produces n! different results and
// fails, whatever `GetMethods()` returns on the day. That is the same property #2801 needed and
// the reason its fixture had to engineer a deterministic wrong answer — permutation invariance
// gets it without engineering anything.
using System.Reflection;
using System.Reflection.Emit;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestMethodOrderingContractTests : IDisposable
{
    /// <summary>
    /// `TestExecutor.IsTestMethod` matches on the attribute's NAME ("TestAttribute" /
    /// "NavTestAttribute"), so a local attribute of that name marks a probe procedure without
    /// dragging in BC's Ncl or the AL compiler.
    /// </summary>
    private sealed class TestAttribute : Attribute { }

    /// <summary>
    /// Declared deliberately in neither alphabetical nor any other meaningful order. Nothing in
    /// the assertions depends on that — the permutation sweeps make declaration order in this
    /// file irrelevant — but it keeps a reader from mistaking C# declaration order for the rule.
    /// </summary>
    private sealed class ProbeCodeunit
    {
        [Test] public void Zeta() { }
        [Test] public void Alpha() { }
        [Test] public void Mid() { }
        [Test] public void Dup(int a) { }
        [Test] public void Dup(string a) { }
    }

    private static MethodInfo M(string name) =>
        typeof(ProbeCodeunit).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    private static MethodInfo Dup(Type param) =>
        typeof(ProbeCodeunit).GetMethod("Dup", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: new[] { param }, modifiers: null)!;

    private static readonly MethodInfo Zeta = M("Zeta");
    private static readonly MethodInfo Alpha = M("Alpha");
    private static readonly MethodInfo MidM = M("Mid");

    public void Dispose() => TestExecutor.ResetSignatureSpanAttrTypeForTests();

    /// <summary>Every ordering of <paramref name="methods"/>, so no case depends on one input sequence.</summary>
    private static IEnumerable<MethodInfo[]> Permutations(MethodInfo[] methods)
    {
        if (methods.Length <= 1) { yield return methods; yield break; }
        for (var i = 0; i < methods.Length; i++)
        {
            var rest = methods.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
                yield return new[] { methods[i] }.Concat(tail).ToArray();
        }
    }

    /// <summary>
    /// Runs the comparator over every permutation of <paramref name="methods"/> and asserts all
    /// of them produced <paramref name="expected"/> — the single assertion that makes the output
    /// a function of the method set rather than of the input array.
    /// </summary>
    private static void AssertOrderIsPermutationInvariant(
        MethodInfo[] methods, IReadOnlyDictionary<MethodInfo, int> lines, string[] expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var permutationCount = 0;
        foreach (var perm in Permutations(methods))
        {
            permutationCount++;
            var ordered = TestExecutor.OrderMethodsByDeclarationLine(perm, lines);
            seen.Add(string.Join(",", ordered.Select(Describe)));

            // Ordering may never change WHAT runs — a comparator that dropped or duplicated a
            // method could still be "deterministic" and satisfy the sequence check alone.
            Assert.Equal(
                perm.Select(Describe).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                ordered.Select(Describe).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        Assert.True(permutationCount > 1, "the sweep must cover more than one input order");
        Assert.True(seen.Count == 1,
            $"method order must be a function of the methods, not of the Type.GetMethods() array "
            + $"they arrived in. {permutationCount} input permutations produced {seen.Count} "
            + $"different orders:\n  " + string.Join("\n  ", seen.OrderBy(x => x, StringComparer.Ordinal)));
        Assert.Equal(string.Join(",", expected), seen.Single());
    }

    private static string Describe(MethodInfo m) =>
        m.GetParameters().Length == 0
            ? m.Name
            : $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})";

    // ── the two fallback paths ────────────────────────────────────────────────────────────

    /// <summary>
    /// TOTAL fallback: not one line resolved. The old code returned the reflection array
    /// untouched ("keep original order"), so the answer was whatever the CLR handed over.
    /// </summary>
    [Fact]
    public void NoLineResolves_OrderIsStillAFunctionOfTheMethods()
    {
        AssertOrderIsPermutationInvariant(
            new[] { Zeta, Alpha, MidM },
            new Dictionary<MethodInfo, int>(),
            new[] { "Alpha", "Mid", "Zeta" });
    }

    /// <summary>
    /// PARTIAL fallback: one line resolved, two did not. The resolved one leads (it has a real
    /// declaration position); the rest may not be ordered by their index in an undefined array.
    /// </summary>
    [Fact]
    public void SomeLinesResolve_UnresolvedTailIsOrderedDeterministically()
    {
        AssertOrderIsPermutationInvariant(
            new[] { Zeta, Alpha, MidM },
            new Dictionary<MethodInfo, int> { [Zeta] = 10 },
            new[] { "Zeta", "Alpha", "Mid" });
    }

    /// <summary>
    /// The third instance of the same shape: two procedures that resolve to the SAME line tie,
    /// and the tie used to be broken by the undefined index too.
    /// </summary>
    [Fact]
    public void MethodsSharingADeclarationLine_TieBreakDeterministically()
    {
        AssertOrderIsPermutationInvariant(
            new[] { Zeta, Alpha, MidM },
            new Dictionary<MethodInfo, int> { [Alpha] = 5, [Zeta] = 5, [MidM] = 9 },
            new[] { "Alpha", "Zeta", "Mid" });
    }

    /// <summary>
    /// Overloads share a name, so the name alone is not a total key. Both are unresolved here,
    /// which is the case where the tiebreak is doing all the work.
    /// </summary>
    [Fact]
    public void UnresolvedOverloads_AreSeparatedByTheirSignature()
    {
        AssertOrderIsPermutationInvariant(
            new[] { Dup(typeof(string)), Dup(typeof(int)) },
            new Dictionary<MethodInfo, int>(),
            new[] { "Dup(Int32)", "Dup(String)" });
    }

    // ── the resolved path must not regress ────────────────────────────────────────────────

    /// <summary>
    /// NEGATIVE CONTROL, and the reason the cases above cannot be satisfied by deleting the
    /// line lookup and sorting by name. Declaration line beats name whenever it is known: these
    /// three resolve to lines whose order is the exact REVERSE of alphabetical, so a plain name
    /// sort inverts all three and fails. This case passes both before and after the fix, by
    /// design — it pins the #1766 rule the fix must leave alone.
    /// </summary>
    [Fact]
    public void ResolvedDeclarationLines_OutrankTheNameTieBreak()
    {
        AssertOrderIsPermutationInvariant(
            new[] { Zeta, Alpha, MidM },
            new Dictionary<MethodInfo, int> { [Alpha] = 30, [MidM] = 20, [Zeta] = 10 },
            new[] { "Zeta", "Mid", "Alpha" });
    }

    /// <summary>
    /// A resolved method always precedes an unresolved one, however the names compare. `Alpha`
    /// sorts first by name and last by line here, so a comparator that let the fallback key leak
    /// ahead of the line key would put it first.
    /// </summary>
    [Fact]
    public void AResolvedMethodAlwaysPrecedesAnUnresolvedOne()
    {
        AssertOrderIsPermutationInvariant(
            new[] { Zeta, Alpha, MidM },
            new Dictionary<MethodInfo, int> { [MidM] = 7, [Zeta] = 3 },
            new[] { "Zeta", "Mid", "Alpha" });
    }

    // ── the diagnostic ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Silence is the defect (.claude/rules/loud-failures.md): a run whose source order quietly
    /// degraded looked exactly like one that did not.
    /// </summary>
    [Fact]
    public void UnresolvedTestProcedures_ProduceADiagnostic()
    {
        var methods = new[] { Zeta, Alpha, MidM };
        var warning = TestExecutor.DescribeUnresolvedTestMethodOrder(
            typeof(ProbeCodeunit), methods,
            new Dictionary<MethodInfo, int> { [Zeta] = 3 },
            resolverAvailable: true);

        Assert.NotNull(warning);
        var text = warning!;
        Assert.Contains("ProbeCodeunit", text);
        Assert.Contains("2 of 3", text);
        Assert.Contains("Alpha", text);
        Assert.Contains("Mid", text);
        Assert.DoesNotContain("Zeta", text);
    }

    /// <summary>
    /// NEGATIVE, and the half that decides whether the diagnostic is usable at all.
    /// `GetMethods(Public|Instance)` also returns every inherited framework method — `ToString`,
    /// `Equals`, `GetHashCode`, `GetType` — none of which has an AL declaration line or ever
    /// will. Counting those would put a warning on every codeunit of every green run, and a
    /// warning that is always on is the same silence in a louder font.
    /// </summary>
    [Fact]
    public void FrameworkMethodsWithNoAlDeclarationLine_ProduceNoDiagnostic()
    {
        var all = typeof(ProbeCodeunit).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.Contains(all, m => m.Name == "ToString");   // the inherited methods really are here

        var lines = new Dictionary<MethodInfo, int>
        {
            [Zeta] = 3, [Alpha] = 6, [MidM] = 9,
            [Dup(typeof(int))] = 12, [Dup(typeof(string))] = 15,
        };
        Assert.Null(TestExecutor.DescribeUnresolvedTestMethodOrder(
            typeof(ProbeCodeunit), all, lines, resolverAvailable: true));
    }

    /// <summary>The resolver being absent entirely is its own message — every line is missing.</summary>
    [Fact]
    public void ResolverUnavailable_ProducesItsOwnDiagnostic()
    {
        var warning = TestExecutor.DescribeUnresolvedTestMethodOrder(
            typeof(ProbeCodeunit), new[] { Zeta, Alpha },
            new Dictionary<MethodInfo, int>(), resolverAvailable: false);

        Assert.NotNull(warning);
        Assert.Contains("SignatureSpanAttribute", warning!);
    }

    // ── the SignatureSpanAttribute latch ──────────────────────────────────────────────────

    /// <summary>
    /// An assembly carrying a type with the exact full name the resolver looks for, built at
    /// runtime so the test needs neither BC's Ncl nor a type squatting in Microsoft's namespace
    /// inside this test assembly.
    /// </summary>
    private static Assembly AssemblyDeclaringSignatureSpanAttribute()
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("SigSpanProbe_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var mb = ab.DefineDynamicModule("m");
        mb.DefineType(TestExecutor.SignatureSpanAttrTypeName, TypeAttributes.Public, typeof(Attribute))
          .CreateType();
        Assert.NotNull(ab.GetType(TestExecutor.SignatureSpanAttrTypeName));
        return ab;
    }

    /// <summary>
    /// The first candidate trigger #3201 names. The flag was set BEFORE the search, and stayed
    /// set when the search found nothing — so a single call landing before Ncl was loaded
    /// disabled source-order dispatch for the whole process, permanently and silently. A miss
    /// must leave the resolver willing to look again.
    /// </summary>
    [Fact]
    public void AFailedSearch_DoesNotPoisonTheResolverForTheRestOfTheProcess()
    {
        TestExecutor.ResetSignatureSpanAttrTypeForTests();
        var nothing = new Func<IEnumerable<Assembly>>(Array.Empty<Assembly>);

        Assert.Null(TestExecutor.ResolveSignatureSpanAttrType(nothing));
        Assert.Equal(1, TestExecutor.SignatureSpanAttrSearchCount);

        Assert.Null(TestExecutor.ResolveSignatureSpanAttrType(nothing));
        Assert.Equal(2, TestExecutor.SignatureSpanAttrSearchCount);

        // Ncl shows up late — as it does when the first ordering call beats the load.
        var withIt = AssemblyDeclaringSignatureSpanAttribute();
        Assert.NotNull(TestExecutor.ResolveSignatureSpanAttrType(() => new[] { withIt }));

        // Found once, then cached: a success DOES latch, so this is not a per-codeunit rescan.
        var searchesAfterSuccess = TestExecutor.SignatureSpanAttrSearchCount;
        Assert.NotNull(TestExecutor.ResolveSignatureSpanAttrType(nothing));
        Assert.Equal(searchesAfterSuccess, TestExecutor.SignatureSpanAttrSearchCount);
    }

    /// <summary>
    /// The second half of the same defect, and the one the issue calls "a concurrent second
    /// caller reading the field mid-search". Publishing `resolved = true` before the value meant
    /// a reader that arrived while the scan was still walking assemblies got `null` and silently
    /// fell back — and, because the caller memoises per type, kept the wrong order afterwards.
    ///
    /// Deterministic in both directions, not a timing probe: the reader is released only once
    /// the writer is provably inside the scan, so under the old code it always observed the
    /// latched-but-empty window, and under a correct one it can never observe a `null` it would
    /// act on.
    /// </summary>
    [Fact]
    public void AReaderArrivingMidSearch_NeverSeesTheEmptyWindow()
    {
        TestExecutor.ResetSignatureSpanAttrTypeForTests();
        var real = AssemblyDeclaringSignatureSpanAttribute();

        using var writerIsInsideTheScan = new ManualResetEventSlim(false);
        using var readerHasCalled = new ManualResetEventSlim(false);

        IEnumerable<Assembly> SlowScan()
        {
            // A decoy first, so the scan is genuinely mid-walk and has not yet found anything.
            yield return typeof(TestMethodOrderingContractTests).Assembly;
            writerIsInsideTheScan.Set();
            Assert.True(readerHasCalled.Wait(TimeSpan.FromSeconds(30)));
            yield return real;
        }

        Type? readerSaw = null;
        var reader = new Thread(() =>
        {
            Assert.True(writerIsInsideTheScan.Wait(TimeSpan.FromSeconds(30)));
            readerHasCalled.Set();
            readerSaw = TestExecutor.ResolveSignatureSpanAttrType(
                () => throw new InvalidOperationException(
                    "the reader must never start a second scan of its own"));
        });
        reader.Start();

        var writerSaw = TestExecutor.ResolveSignatureSpanAttrType(SlowScan);
        Assert.True(reader.Join(TimeSpan.FromSeconds(60)), "the reader thread did not finish");

        Assert.NotNull(writerSaw);
        Assert.NotNull(readerSaw);
        Assert.Same(writerSaw, readerSaw);
    }
}
