// HostEnvironmentIsolationGuardTests — #5282, the general form of the guard #5280 wrote for one
// variable.
//
// A test that sets a variable in the test host's own environment owns it across a window, and
// xunit runs collections in parallel. Every runner another collection spawns in that window
// inherits the value (Process.Start copies the host's current environment), and every in-process
// reader sees it. #5201 was that, for one variable: exit 134 in a `--watch` child.
//
// THE RULE: a test class that mutates the host environment sits in a collection whose
// [CollectionDefinition] sets DisableParallelization = true — such a collection runs alone,
// after every parallel one (HostEnvironmentSerialCollection.cs). Not a list of variables: a
// list is what rots (the variable a runner reads is spelled as a literal, a constant, a
// computed name or a parameter in production), and a class setting a variable the runner does
// not read still changes every in-process reader of it. The one exemption is a
// [ModuleInitializer] method: it runs once at assembly load, before any test, and is how the
// suite pins variables on purpose (DefaultTestToolPin, SharedEngineCaches,
// SpawnedRunnerShowsPassLines).
//
// HOW IT READS: the IL of every method in the test assembly, nested types, lambdas and async
// state machines included, and not the source. The #5280 scan was a regex over source and its
// review found it saw one spelling at a time; the IL carries the callee whatever the spelling
// of the name argument (literal, constant, interpolation, parameter, three-argument overload),
// a method group (ldftn), or a call through a helper type. What it names:
//   * a call, ldftn or ldtoken of Environment.SetEnvironmentVariable,
//   * a string equal to that method's name (reflection),
//   * a P/Invoke of setenv / putenv / SetEnvironmentVariable,
// and a test class that REFERENCES a type doing any of those, transitively, is held to the same
// rule. A type that mutates and that no test class reaches (an xunit fixture, say) is refused
// too: the guard cannot say who runs it.
//
// NOT CAUGHT: a call through a function pointer (calli), a mutation inside AlRunner itself
// that a test triggers, an `Environment` change made by a spawned child (its own environment),
// and `Directory.SetCurrentDirectory`, which children inherit the same way and is not an
// environment variable.
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace AlRunner.Tests;

public sealed class HostEnvironmentIsolationGuardTests
{
    private const string FixtureNamespace = "AlRunner.Tests.HostEnvironmentGuardFixtures";

    // Assembled, so no single string in this file's IL equals the method name and the scan
    // does not flag its own detector.
    private static readonly string SetEnvName = string.Concat("SetEnvironment", "Variable");

    private static readonly string[] NativeSetters =
        { "setenv", "putenv", string.Concat("SetEnvironment", "VariableW"), SetEnvName };

    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    // ── the IL reader ─────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<short, OpCode> Ops = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static IEnumerable<(OpCode Op, int Operand)> Decode(byte[] il)
    {
        var i = 0;
        while (i < il.Length)
        {
            short code = il[i++];
            if (code == 0xFE) code = unchecked((short)(0xFE00 | il[i++]));
            if (!Ops.TryGetValue(code, out var op))
                throw new InvalidOperationException($"unknown opcode 0x{code:X} at byte {i - 1}");

            var operand = 0;
            int size;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: size = 8; break;
                case OperandType.InlineSwitch:
                    size = 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                default:
                    size = 4;
                    operand = BitConverter.ToInt32(il, i);
                    break;
            }
            yield return (op, operand);
            i += size;
        }
    }

    // ── the analysis, over a set of types so the proofs can run it on fixtures ────────────────

    internal sealed record Verdict(
        IReadOnlyDictionary<Type, IReadOnlyList<string>> DirectMutators,
        IReadOnlyList<Type> ExemptInitializerTypes,
        IReadOnlyList<string> Unreadable,
        int MethodsRead,
        IReadOnlyList<string> Violations);

    private static bool IsSetEnvironmentVariable(MethodBase m) =>
        m.DeclaringType == typeof(Environment) && m.Name == SetEnvName;

    private static bool IsNativeSetter(MethodBase m)
    {
        if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0) return false;
        var entry = m.GetCustomAttribute<DllImportAttribute>()?.EntryPoint;
        return NativeSetters.Contains(string.IsNullOrEmpty(entry) ? m.Name : entry,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The type a method's evidence is filed under: its outermost declaring type,
    /// except that a nested type which is itself a test class is its own node.</summary>
    private static Type NodeOf(Type t, Func<Type, bool> isTestClass)
    {
        while (t.DeclaringType is not null && !isTestClass(t)) t = t.DeclaringType;
        return t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t;
    }

    internal static Verdict Analyze(
        IEnumerable<Type> types,
        Func<Type, bool> isTestClass,
        Func<Type, string?> collectionOf,
        IReadOnlyDictionary<string, string> nonParallelCollections,
        Func<byte[], IEnumerable<(OpCode Op, int Operand)>>? decode = null)
    {
        decode ??= Decode;
        var universe = types.ToList();
        var nodes = universe.Select(t => NodeOf(t, isTestClass)).ToHashSet();
        var direct = new Dictionary<Type, List<string>>();
        var exempt = new HashSet<Type>();
        var unreadable = new List<string>();
        var edges = new Dictionary<Type, HashSet<Type>>();
        var methodsRead = 0;

        void Mutates(Type node, string evidence)
        {
            if (!direct.TryGetValue(node, out var list)) direct[node] = list = new List<string>();
            list.Add(evidence);
        }

        foreach (var type in universe)
        {
            var node = NodeOf(type, isTestClass);
            var methods = type.GetMethods(AllDeclared).Cast<MethodBase>()
                .Concat(type.GetConstructors(AllDeclared));
            foreach (var method in methods)
            {
                var where = $"{type.Name}.{method.Name}";
                if (IsNativeSetter(method)) Mutates(node, $"{where} is a P/Invoke of a native environment setter");

                var isInitializer = method.GetCustomAttributes(typeof(ModuleInitializerAttribute), false).Length > 0;
                byte[]? il;
                try { il = method.GetMethodBody()?.GetILAsByteArray(); }
                catch (Exception e)
                {
                    unreadable.Add($"{where}: {e.GetType().Name}: {e.Message}");
                    continue;
                }
                if (il is null) continue;
                methodsRead++;

                IEnumerable<(OpCode Op, int Operand)> instructions;
                try { instructions = decode(il).ToList(); }
                catch (Exception e)
                {
                    unreadable.Add($"{where}: {e.GetType().Name}: {e.Message}");
                    continue;
                }

                var typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;
                var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                foreach (var (op, operand) in instructions)
                {
                    if (op.OperandType == OperandType.InlineString)
                    {
                        string? text = null;
                        try { text = method.Module.ResolveString(operand); }
                        catch (Exception e) { unreadable.Add($"{where}: string token: {e.Message}"); }
                        if (text == SetEnvName) Mutates(node, $"{where} names the method as a string");
                        continue;
                    }
                    if (op.OperandType != OperandType.InlineMethod && op.OperandType != OperandType.InlineTok)
                        continue;

                    MemberInfo? target;
                    try { target = method.Module.ResolveMember(operand, typeArgs, methodArgs); }
                    catch (Exception e)
                    {
                        unreadable.Add($"{where}: token 0x{operand:X}: {e.GetType().Name}: {e.Message}");
                        continue;
                    }
                    if (target is not MethodBase called) continue;

                    if (IsSetEnvironmentVariable(called) || IsNativeSetter(called))
                    {
                        if (isInitializer) exempt.Add(node);
                        else Mutates(node, $"{where} calls {called.DeclaringType!.Name}.{called.Name}");
                    }
                    else if (called.DeclaringType is { } owner && owner.Assembly == type.Assembly)
                    {
                        var calleeNode = NodeOf(owner, isTestClass);
                        if (calleeNode != node && nodes.Contains(calleeNode))
                        {
                            if (!edges.TryGetValue(node, out var set)) edges[node] = set = new HashSet<Type>();
                            set.Add(calleeNode);
                        }
                    }
                }
            }
        }

        // A node reaches a mutator if it is one or references a node that does.
        List<Type> Reachers(Type mutator)
        {
            var seen = new HashSet<Type> { mutator };
            var queue = new Queue<Type>(new[] { mutator });
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var (from, to) in edges)
                    if (to.Contains(current) && seen.Add(from)) queue.Enqueue(from);
            }
            return seen.ToList();
        }

        var violations = new List<string>();
        var judged = new HashSet<Type>();
        foreach (var mutator in direct.Keys.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var reachers = Reachers(mutator);
            if (!isTestClass(mutator) && !reachers.Any(isTestClass))
            {
                violations.Add(
                    $"{mutator.Name} mutates the host process environment ({direct[mutator][0]}) " +
                    "but no test class reaches it, so nobody can say which collection runs it " +
                    "(an xunit fixture or a stray helper). Move the mutation into the test class " +
                    "that needs it, or hand the value to a child through its own " +
                    "ProcessStartInfo.Environment.");
            }
            foreach (var reacher in reachers.Where(isTestClass))
            {
                if (!judged.Add(reacher)) continue;
                var collection = collectionOf(reacher);
                if (collection is not null && nonParallelCollections.ContainsKey(collection)) continue;
                var how = reacher == mutator
                    ? direct[mutator][0]
                    : $"it references {mutator.Name}, which mutates the environment ({direct[mutator][0]})";
                violations.Add(
                    $"{reacher.Name} mutates the host process environment ({how}) but is " +
                    (collection is null
                        ? "in no xunit collection"
                        : $"in collection \"{collection}\", which does not set DisableParallelization = true") +
                    ". xunit runs collections in parallel, so every runner another class spawns " +
                    "meanwhile inherits the value, and every in-process reader of it sees it (#5201, #5282). " +
                    "Prefer not to set it: give the child the value through its own " +
                    "ProcessStartInfo.Environment, or give the runner's read a per-flow seam " +
                    "(Win32Stubs.OverrideSoForTests). If the subject is an in-process read of the real " +
                    "environment, add [Collection(HostEnvironmentSerialCollection.Name)] — or any " +
                    "collection whose [CollectionDefinition] disables parallelization.");
            }
        }

        return new Verdict(
            direct.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            exempt.ToList(), unreadable, methodsRead, violations);
    }

    // ── what xunit calls a test class, and what collection it runs in ─────────────────────────

    private static bool IsTestClass(Type t) =>
        t is { IsClass: true, IsAbstract: false } &&
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0);

    private static string? CollectionNameOf(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var attr in CustomAttributeData.GetCustomAttributes(t))
            {
                if (attr.AttributeType.FullName != "Xunit.CollectionAttribute") continue;
                if (attr.ConstructorArguments.Count > 0 && attr.ConstructorArguments[0].Value is string name)
                    return name;
            }
        }
        return null;
    }

    // The same derivation ConsoleSwapIsolationGuardTests uses: a collection is a fence because
    // its [CollectionDefinition] says so, read as data, never because of its name.
    private static IReadOnlyDictionary<string, string> NonParallelCollections() =>
        ConsoleSwapIsolationGuardTests.NonParallelCollections();

    private static IEnumerable<Type> WithNested(Type t)
    {
        yield return t;
        foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var x in WithNested(n)) yield return x;
    }

    private static Verdict AnalyzeFixtures(string? collection, params Type[] roots) =>
        Analyze(roots.SelectMany(WithNested), t => t == roots[0] && !t.IsNested, _ => collection,
            NonParallelCollections());

    private static Verdict AnalyzeRealAssembly() =>
        Analyze(
            typeof(HostEnvironmentIsolationGuardTests).Assembly.GetTypes()
                .Where(t => t.Namespace != FixtureNamespace),
            IsTestClass, CollectionNameOf, NonParallelCollections());

    // ── GREEN: the real assembly ──────────────────────────────────────────────────────────────

    [Fact]
    public void NoTestClassMutatesTheHostEnvironmentOutsideASerialCollection()
    {
        var verdict = AnalyzeRealAssembly();

        Assert.True(verdict.Unreadable.Count == 0,
            "the scan could not read part of the assembly, so it cannot vouch for it:\n" +
            string.Join("\n", verdict.Unreadable));
        Assert.True(verdict.MethodsRead > 1000,
            $"only {verdict.MethodsRead} method bodies were read from the whole test assembly, " +
            "so an empty violation list means nothing");
        Assert.True(verdict.Violations.Count == 0, string.Join("\n\n", verdict.Violations));
    }

    // The assertion above is also what an empty scan passes, so the scan has to be seen finding
    // the known mutators, one per spelling shape, and exempting the three module initializers.
    [Theory]
    [InlineData(nameof(HotPathHookCostTests))]              // string literal names
    [InlineData(nameof(BackupReaderServeSessionTests))]     // a constant and a literal
    [InlineData(nameof(DependencyMetadataProducerTests))]   // through a nested helper class
    [InlineData(nameof(CacheCompileLockTests))]             // a constant read from the production type
    public void TheScanFindsTheKnownMutators(string className)
    {
        var verdict = AnalyzeRealAssembly();

        Assert.True(verdict.MethodsRead > 1000,
            $"only {verdict.MethodsRead} method bodies were read from the whole test assembly");
        Assert.Contains(verdict.DirectMutators.Keys, t => t.Name == className);
    }

    [Theory]
    [InlineData(nameof(SharedEngineCaches))]
    [InlineData(nameof(DefaultTestToolPin))]
    [InlineData(nameof(SpawnedRunnerShowsPassLines))]
    public void TheModuleInitializerPinsAreSeenAndExempted(string typeName)
    {
        var verdict = AnalyzeRealAssembly();

        Assert.Contains(verdict.ExemptInitializerTypes, t => t.Name == typeName);
        Assert.DoesNotContain(verdict.DirectMutators.Keys, t => t.Name == typeName);
    }

    // ── RED: the detector on fixtures, one per spelling ───────────────────────────────────────

    public static IEnumerable<object[]> MutatingFixtures() => new[]
    {
        typeof(HostEnvironmentGuardFixtures.LiteralSetter),
        typeof(HostEnvironmentGuardFixtures.ConstantSetter),
        typeof(HostEnvironmentGuardFixtures.ComputedNameSetter),
        typeof(HostEnvironmentGuardFixtures.ThreeArgumentSetter),
        typeof(HostEnvironmentGuardFixtures.LambdaSetter),
        typeof(HostEnvironmentGuardFixtures.AsyncSetter),
        typeof(HostEnvironmentGuardFixtures.MethodGroupSetter),
        typeof(HostEnvironmentGuardFixtures.ReflectionSetter),
        typeof(HostEnvironmentGuardFixtures.NativeSetter),
        typeof(HostEnvironmentGuardFixtures.MixedInitializerSetter),
    }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(MutatingFixtures))]
    public void EverySpellingOfAnEnvironmentMutation_InNoCollection_IsReported(Type fixture)
    {
        var verdict = AnalyzeFixtures(null, fixture);

        Assert.Empty(verdict.Unreadable);
        var violation = Assert.Single(verdict.Violations);
        Assert.Contains(fixture.Name, violation);
        Assert.Contains("in no xunit collection", violation);
    }

    [Theory]
    [MemberData(nameof(MutatingFixtures))]
    public void EverySpellingOfAnEnvironmentMutation_InAParallelCollection_IsReported(Type fixture)
    {
        Assert.DoesNotContain("some-parallel-collection", NonParallelCollections().Keys);

        var violation = Assert.Single(AnalyzeFixtures("some-parallel-collection", fixture).Violations);
        Assert.Contains("some-parallel-collection", violation);
        Assert.Contains("DisableParallelization = true", violation);
    }

    [Theory]
    [MemberData(nameof(MutatingFixtures))]
    public void EverySpellingOfAnEnvironmentMutation_InASerialCollection_IsNotReported(Type fixture)
    {
        var verdict = AnalyzeFixtures(HostEnvironmentSerialCollection.Name, fixture);

        Assert.Empty(verdict.Violations);
        Assert.Contains(verdict.DirectMutators.Keys, t => t == fixture);
    }

    [Fact]
    public void ATestClassThatCallsAHelperWhichMutates_IsReported_AndTheHelperAloneIsRefused()
    {
        var viaHelper = AnalyzeFixtures(null,
            typeof(HostEnvironmentGuardFixtures.HelperCaller), typeof(HostEnvironmentGuardFixtures.EnvironmentHelper));
        var caller = Assert.Single(viaHelper.Violations);
        Assert.Contains("HelperCaller", caller);
        Assert.Contains("references EnvironmentHelper", caller);

        // No test class reaches the helper: the guard cannot say who runs it, which is a refusal
        // rather than a pass.
        var alone = Analyze(
            WithNested(typeof(HostEnvironmentGuardFixtures.EnvironmentHelper)),
            _ => false, _ => null, NonParallelCollections());
        var refusal = Assert.Single(alone.Violations);
        Assert.Contains("no test class reaches it", refusal);
    }

    [Fact]
    public void AModuleInitializerAlone_IsExempted_ButNotWhenAnotherMethodOfTheTypeMutates()
    {
        var onlyInit = AnalyzeFixtures(null, typeof(HostEnvironmentGuardFixtures.InitializerOnlySetter));
        Assert.Empty(onlyInit.Violations);
        Assert.Empty(onlyInit.DirectMutators);
        Assert.Contains(typeof(HostEnvironmentGuardFixtures.InitializerOnlySetter), onlyInit.ExemptInitializerTypes);

        var mixed = AnalyzeFixtures(null, typeof(HostEnvironmentGuardFixtures.MixedInitializerSetter));
        Assert.Single(mixed.Violations);
    }

    [Theory]
    [InlineData(typeof(HostEnvironmentGuardFixtures.PureReader))]
    [InlineData(typeof(HostEnvironmentGuardFixtures.ProseMentionsTheMethod))]
    public void ReadingTheEnvironment_OrNamingTheMethodInProse_IsNotReported(Type fixture)
    {
        var verdict = AnalyzeFixtures(null, fixture);

        Assert.Empty(verdict.Violations);
        Assert.Empty(verdict.DirectMutators);
        Assert.True(verdict.MethodsRead > 0, "the fixture's bodies were not read, so 'clean' means nothing");
    }

    [Fact]
    public void ABodyThatCannotBeDecoded_IsReportedAsUnreadable_NotSkipped()
    {
        var fixture = typeof(HostEnvironmentGuardFixtures.LiteralSetter);
        var verdict = Analyze(
            WithNested(fixture), t => t == fixture, _ => null, NonParallelCollections(),
            _ => throw new InvalidOperationException("synthetic decode failure"));

        Assert.Contains(verdict.Unreadable, u => u.Contains("synthetic decode failure"));
        // Nothing is claimed about a body that was not read; the sweep fails on any unreadable one.
        Assert.Empty(verdict.DirectMutators);
    }

    [Fact]
    public void TheDecoderRefusesAnOpcodeItDoesNotKnow()
    {
        // 0xFE 0xFF is no opcode; reading past it would misalign every later operand, so the
        // decoder throws rather than guessing.
        Assert.Throws<InvalidOperationException>(() => Decode(new byte[] { 0xFE, 0xFF }).ToList());
    }

    [Fact]
    public void TheFixtureNamespaceHoldsNoTest_SoExcludingItHidesNothing()
    {
        var inside = typeof(HostEnvironmentIsolationGuardTests).Assembly.GetTypes()
            .Where(t => t.Namespace == FixtureNamespace).ToList();

        Assert.True(inside.Count >= 10, $"only {inside.Count} fixture types found");
        foreach (var t in inside)
        {
            Assert.False(IsTestClass(t), $"{t.Name} is a test class hiding in the excluded namespace");
            Assert.Null(CollectionNameOf(t));
        }
    }
}
