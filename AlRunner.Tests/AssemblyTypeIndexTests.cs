// AssemblyTypeIndexTests — the proving tests for the metadata-backed type/subscriber lookup
// that replaced Assembly.GetTypes() across the runner's boot path.
//
// The claim being proven is NOT "the new scan finds some subscribers". It is "the new scan
// finds EXACTLY the same subscribers the old reflection scan found, on the same assemblies".
// So every test here diffs the new mechanism against the OLD one
// (EventSubscriberPatches.LegacyReflectionScan, kept verbatim for this purpose) rather than
// against a hand-written expectation that could quietly encode the new code's own bugs:
//
//   * In-process, on synthetic assemblies (this file) — covers both shapes of custom-attribute
//     constructor handle the metadata reader has to understand: a MethodDefinition (attribute
//     applied inside its own defining assembly) and a MemberReference (attribute applied in an
//     assembly that references the definer, which is the real BC shape —
//     NavEventSubscriberAttribute lives in Ncl and is applied in AL-emitted assemblies).
//     Also covers arity-disambiguated overloads, nested types, prefix gating, and the
//     negatives: a non-"Codeunit" type contributes nothing, an un-attributed method
//     contributes nothing, a missing name resolves to null.
//
//   * Out-of-process, on the REAL Base Application + System Application (see
//     EventSubscriberScanEquivalenceTests below) — the only place the ~3,400-subscriber claim
//     can honestly be made, since those assemblies only exist inside a real runner invocation.
//
// Would these still pass against a gutted implementation? No: an AssemblyTypeIndex that
// returned nothing fails the positive assertions AND the set-equality against the legacy scan;
// one that returned everything fails the negatives.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class AssemblyTypeIndexTests
{
    /// <summary>The attribute type + a few decoys, in their own assembly. Applying the
    /// attribute HERE makes its CustomAttribute constructor a MethodDefinition handle.</summary>
    private const string DefinerSource = """
        using System;
        namespace Fx.Defs
        {
            public sealed class NavEventSubscriberAttribute : Attribute
            {
                public NavEventSubscriberAttribute(int objectType, int objectId, string methodName) { }
            }

            public class Codeunit90001
            {
                [NavEventSubscriber(1, 18, "OnAfterInsertEvent")]
                public void HandleInsert() { }

                [NavEventSubscriber(1, 18, "OnAfterModifyEvent")]
                internal static void HandleModify(int a, string b) { }

                public void NotASubscriber() { }

                public class OnFooEvent_Scope
                {
                    public class Deeper { }
                }
                public class Decoy_Scope { }
            }

            public class Record90001 { }
            public class Record90002 { }
            public class RecordNotNumeric { }

            // Decoy: carries the attribute but is NOT a Codeunit* type, so the scan must skip it.
            public class Helper90003
            {
                [NavEventSubscriber(1, 18, "OnAfterInsertEvent")]
                public void Ignored() { }
            }
        }

        // Deliberately in the global namespace and carrying two attributed overloads with the
        // SAME name and DIFFERENT arity — the case the metadata scan disambiguates on the
        // signature's parameter count.
        public class Codeunit90002
        {
            [Fx.Defs.NavEventSubscriber(5, 42, "OnBar")]
            public void HandleBar() { }

            [Fx.Defs.NavEventSubscriber(5, 42, "OnBar")]
            public void HandleBar(string only) { }
        }
        """;

    /// <summary>A second assembly that REFERENCES the definer, so its attribute constructor is
    /// a MemberReference into a TypeReference — the shape every AL-emitted BC assembly has.</summary>
    private const string ConsumerSource = """
        using Fx.Defs;
        namespace Fx.Uses
        {
            public class Codeunit90101
            {
                [NavEventSubscriber(5, 99, "OnSomething")]
                public void Handle() { }

                public void Plain() { }
            }

            public class NotACodeunit90102
            {
                [NavEventSubscriber(5, 99, "OnSomething")]
                public void Handle() { }
            }
        }
        """;

    private static byte[] Compile(string assemblyName, string source, params byte[][] references)
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        };
        foreach (var r in references) refs.Add(MetadataReference.CreateFromImage(r));

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    /// <summary>
    /// The definer alone, loaded the way DependencyLoader loads BC's R2R chunks — from a
    /// byte[], with no file on disk and therefore an empty <c>Assembly.Location</c>. That is
    /// precisely the case <c>AssemblyExtensions.TryGetRawMetadata</c> exists to cover, so the
    /// tests below double as proof the index works on that shape.
    /// </summary>
    private static Assembly LoadDefinerFromBytes()
        => Assembly.Load(Compile($"al-runner-ti-def-{Guid.NewGuid():N}", DefinerSource));

    /// <summary>
    /// Definer + consumer, both written to a temp directory and loaded with
    /// <see cref="Assembly.LoadFrom(string)"/>.
    ///
    /// Deliberately NOT Assembly.Load(byte[]) for this pair: a byte-loaded assembly is not
    /// registered for simple-name binding in the default load context, so the consumer's
    /// [NavEventSubscriber] usage cannot materialise (FileNotFoundException inside
    /// GetCustomAttributes) and BOTH scans would report nothing — an equivalence test that
    /// compares empty to empty and proves nothing. LoadFrom probes the requesting assembly's
    /// own directory, so the cross-assembly attribute really resolves here, which is what the
    /// MemberReference-constructor case needs in order to be a real test.
    /// </summary>
    private static (Assembly Definer, Assembly Consumer) LoadPairFromDisk()
    {
        var tag = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-type-index", tag);
        Directory.CreateDirectory(dir);
        var definerBytes = Compile($"al-runner-ti-def-{tag}", DefinerSource);
        var consumerBytes = Compile($"al-runner-ti-use-{tag}", ConsumerSource, definerBytes);
        var definerPath = Path.Combine(dir, $"al-runner-ti-def-{tag}.dll");
        var consumerPath = Path.Combine(dir, $"al-runner-ti-use-{tag}.dll");
        File.WriteAllBytes(definerPath, definerBytes);
        File.WriteAllBytes(consumerPath, consumerBytes);
        return (Assembly.LoadFrom(definerPath), Assembly.LoadFrom(consumerPath));
    }

    private static HashSet<string> Describe(IEnumerable<MethodInfo> methods)
        => new(methods.Select(EventSubscriberPatches.DescribeSubscriberMethod), StringComparer.Ordinal);

    // ------------------------------------------------------------------
    // Attributed-method discovery
    // ------------------------------------------------------------------

    [Fact]
    public void FindAttributedMethods_MethodDefCtor_MatchesLegacyReflectionScanExactly()
    {
        var definer = LoadDefinerFromBytes();
        var index = AssemblyTypeIndex.For(definer);
        Assert.True(index.IsMetadataBacked,
            "an Assembly.Load(byte[]) assembly must still expose raw metadata via TryGetRawMetadata");

        var viaMetadata = Describe(index.FindAttributedMethods("Codeunit", "NavEventSubscriberAttribute"));
        var viaReflection = Describe(EventSubscriberPatches.LegacyReflectionScan(definer));

        // POSITIVE — the exact set, named. Both attributed overloads of HandleBar are present
        // and distinguished by arity; HandleModify is found although it is internal+static.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "Fx.Defs.Codeunit90001.HandleInsert/0",
            "Fx.Defs.Codeunit90001.HandleModify/2",
            "Codeunit90002.HandleBar/0",
            "Codeunit90002.HandleBar/1",
        };
        Assert.Equal(expected, viaMetadata);

        // EQUIVALENCE — and the pre-existing reflection scan agrees, member for member.
        Assert.Equal(viaReflection, viaMetadata);

        // NEGATIVES — an attributed method on a non-"Codeunit" type, and an un-attributed
        // method on a Codeunit type, contribute nothing to either scan.
        Assert.DoesNotContain("Fx.Defs.Helper90003.Ignored/0", viaMetadata);
        Assert.DoesNotContain("Fx.Defs.Codeunit90001.NotASubscriber/0", viaMetadata);
    }

    [Fact]
    public void FindAttributedMethods_MemberRefCtor_MatchesLegacyReflectionScanExactly()
    {
        // The cross-assembly shape: the attribute is defined in another assembly, so the
        // CustomAttribute row's Constructor is a MemberReference whose Parent is a TypeReference.
        // This is how EVERY [NavEventSubscriber] in a BC AL-emitted assembly is encoded.
        var (_, consumer) = LoadPairFromDisk();

        var viaMetadata = Describe(
            AssemblyTypeIndex.For(consumer).FindAttributedMethods("Codeunit", "NavEventSubscriberAttribute"));
        var viaReflection = Describe(EventSubscriberPatches.LegacyReflectionScan(consumer));

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "Fx.Uses.Codeunit90101.Handle/0" },
                     viaMetadata);
        Assert.Equal(viaReflection, viaMetadata);
        Assert.DoesNotContain("Fx.Uses.NotACodeunit90102.Handle/0", viaMetadata);
    }

    [Fact]
    public void FindAttributedMethods_UnknownAttributeName_FindsNothing()
    {
        var definer = LoadDefinerFromBytes();
        // Matching is on the attribute type's simple name, not "has any attribute at all".
        Assert.Empty(AssemblyTypeIndex.For(definer)
            .FindAttributedMethods("Codeunit", "SomeOtherAttribute"));
    }

    // ------------------------------------------------------------------
    // Type lookup by name
    // ------------------------------------------------------------------

    [Fact]
    public void FindFirst_ResolvesRealTypes_AndAnswersNullForNamesThatAreNotThere()
    {
        var definer = LoadDefinerFromBytes();
        var index = AssemblyTypeIndex.For(definer);

        // POSITIVE — the same Type object Array.Find(asm.GetTypes(), x => x.Name == n) returns.
        var expected = definer.GetTypes().First(t => t.Name == "Codeunit90001");
        Assert.Same(expected, index.FindFirst("Codeunit90001"));

        // Nested types are in the index too, addressed by their simple name, and resolve
        // through the Outer+Inner reflection name.
        var nested = definer.GetTypes().First(t => t.Name == "OnFooEvent_Scope");
        Assert.Same(nested, index.FindFirst("OnFooEvent_Scope"));
        var deeper = definer.GetTypes().First(t => t.Name == "Deeper");
        Assert.Same(deeper, index.FindFirst("Deeper"));

        // NEGATIVE — a name that does not exist, and a name that exists but fails the caller's
        // predicate, both answer null rather than "something close".
        Assert.Null(index.FindFirst("Codeunit99999"));
        Assert.Null(index.FindFirst("Codeunit90001", t => t.Name == "somethingElse"));
        Assert.NotNull(index.FindFirst("Codeunit90001", t => t.IsPublic));
    }

    [Fact]
    public void EnumerateWithPrefix_AndTypeNamesWithPrefix_AgreeWithGetTypes()
    {
        var definer = LoadDefinerFromBytes();
        var index = AssemblyTypeIndex.For(definer);

        var viaGetTypes = new HashSet<string>(
            definer.GetTypes().Where(t => t.Name.StartsWith("Record", StringComparison.Ordinal))
                              .Select(t => t.Name),
            StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
                     { "Record90001", "Record90002", "RecordNotNumeric" }, viaGetTypes);

        Assert.Equal(viaGetTypes,
            new HashSet<string>(index.EnumerateWithPrefix("Record").Select(t => t.Name), StringComparer.Ordinal));
        Assert.Equal(viaGetTypes,
            new HashSet<string>(index.TypeNamesWithPrefix("Record"), StringComparer.Ordinal));

        // NEGATIVE — a prefix nothing matches yields nothing (not "everything").
        Assert.Empty(index.EnumerateWithPrefix("NoSuchPrefix"));
        Assert.Empty(index.TypeNamesWithPrefix("NoSuchPrefix"));
    }

    [Fact]
    public void FindNestedType_ResolvesByNameAndAnswersNullWhenAbsent()
    {
        var definer = LoadDefinerFromBytes();
        var index = AssemblyTypeIndex.For(definer);
        var outer = definer.GetType("Fx.Defs.Codeunit90001")!;

        // POSITIVE — same answer as GetNestedTypes().FirstOrDefault(name), which is what this
        // replaced on the event-scope seeding path.
        var expected = outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                            .First(t => t.Name == "OnFooEvent_Scope");
        Assert.Same(expected, index.FindNestedType(outer, "OnFooEvent_Scope"));

        // NEGATIVE — a nested name that is not declared on THIS type answers null. "Deeper" is
        // nested inside OnFooEvent_Scope, not inside Codeunit90001, so the search must not
        // flatten the nesting chain.
        Assert.Null(index.FindNestedType(outer, "OnMissingEvent_Scope"));
        Assert.Null(index.FindNestedType(outer, "Deeper"));
        Assert.Same(definer.GetType("Fx.Defs.Codeunit90001+OnFooEvent_Scope+Deeper"),
                    index.FindNestedType(expected, "Deeper"));
    }
}

/// <summary>
/// The equivalence claim on the assemblies that actually matter: the real Base Application and
/// System Application R2R chunks, with their ~3,400 [NavEventSubscriber] methods.
///
/// Those assemblies only exist inside a real runner invocation (DependencyLoader loads them from
/// the .app packages via Assembly.Load(byte[])), so this drives the runner binary itself with
/// AL_RUNNER_SUBSCRIBER_SCAN_AUDIT=1, which makes EventSubscriberPatches run BOTH the metadata
/// scan and the pre-existing reflection scan over every assembly it discovers and print the
/// comparison. See EventSubscriberPatches.AuditAssemblyScan.
/// </summary>
public sealed class EventSubscriberScanEquivalenceTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    private static readonly Regex AuditLine = new(
        @"scan-audit (?<asm>\S+): metadata=(?<md>\d+) reflection=(?<rf>\d+) identical=(?<same>true|false)",
        RegexOptions.Compiled);

    [SkippableFact]
    public void MetadataScan_FindsExactlyTheSameSubscribersAsTheOldReflectionScan_OnRealBcAssemblies()
    {
        TestArtifacts.SkipIfMissing();

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        args.Append($" --quiet \"{Fixture}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_SUBSCRIBER_SCAN_AUDIT"] = "1";
        // The audit lines are `[Subscribers]`-tagged, which AlRunner/Log.cs drops at default
        // verbosity.
        psi.Environment["AL_RUNNER_VERBOSE"] = "1";

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output;
        lock (sb) output = sb.ToString();

        Assert.True(p.ExitCode == 0, $"runner exited {p.ExitCode}. Output:\n{output}");

        var rows = AuditLine.Matches(output)
            .Select(m => (
                Asm: m.Groups["asm"].Value,
                Md: int.Parse(m.Groups["md"].Value),
                Rf: int.Parse(m.Groups["rf"].Value),
                Same: m.Groups["same"].Value == "true"))
            .ToList();

        Assert.True(rows.Count > 0,
            $"the audit produced no scan-audit lines at all — the env var is not wired. Output:\n{output}");

        // THE CLAIM: every assembly the runner discovered subscribers in reports the identical
        // (declaring type, method, arity) SET from both scans.
        var mismatched = rows.Where(r => !r.Same).ToList();
        Assert.True(mismatched.Count == 0,
            "the metadata scan and the legacy reflection scan disagree on: " +
            string.Join(", ", mismatched.Select(r => $"{r.Asm} (metadata={r.Md} reflection={r.Rf})")) +
            "\n" + output);

        int totalMd = rows.Sum(r => r.Md);
        int totalRf = rows.Sum(r => r.Rf);
        Assert.Equal(totalRf, totalMd);

        // A run that scanned nothing but empty assemblies would satisfy "identical=true"
        // everywhere, so pin the magnitude too: Base Application + System Application carry
        // well over 3,000 [NavEventSubscriber] methods between them (3,447 on BC 28.1).
        Assert.True(totalMd > 3000,
            $"expected >3000 subscribers across the real BC assemblies, found {totalMd}. Output:\n{output}");

        // ...and that at least one SINGLE assembly is a genuinely large one, so the total
        // cannot be reached by summing many trivial assemblies.
        Assert.True(rows.Max(r => r.Md) > 500,
            $"no single assembly contributed >500 subscribers (max {rows.Max(r => r.Md)}); " +
            $"the Base Application chunks were not scanned. Output:\n{output}");
    }
}
