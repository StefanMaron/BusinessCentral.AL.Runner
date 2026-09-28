// #4931: `al-runner <A> <B> <C>` loads every bundle before any of them runs tests, as a run over
// one root holding the same folders does, so a later bundle's subscriber sees an earlier bundle's
// event. Each deferred run first restores the state its own load left (BundleRunState).
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public class CliMultiPathBundleRunTests
{
    private static readonly Guid RsDep = new("3b0e6f52-0000-4c38-9e25-0000000000a1");
    private static readonly Guid RsP = new("3b0e6f52-0000-4c38-9e25-0000000000a2");
    private static readonly Guid RsQ = new("3b0e6f52-0000-4c38-9e25-0000000000a3");

    private static string Quoted(IEnumerable<string> dirs) => string.Join(" ", dirs.Select(d => $"\"{d}\""));

    /// <summary>The #4931 reproducer: the dupX/dupY/depZ fixture as separate paths. Z depends on X and
    /// subscribes to X's events, so X's tests fail unless Z is loaded before X runs.</summary>
    [SkippableFact]
    public void EdgeFixturesAsSeparateBundlePaths_PassAsTheSingleRootDoes()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-multipath-edges");
        Directory.CreateDirectory(root);
        var dirs = AppGroupObjectVisibilityTests.WriteOwnershipEdgeFixtures(root);

        AssertAllPass(AppGroupObjectVisibilityTests.RunCli($" --no-cache {Quoted(dirs)}"), 40);
    }

    /// <summary>The same paths cold, then warm on one cache root, where every module is a HIT.</summary>
    [SkippableFact]
    public void EdgeFixturesAsSeparateBundlePaths_ColdThenWarmOnOneCacheRoot()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-multipath-edges-warm");
        Directory.CreateDirectory(root);
        var dirs = AppGroupObjectVisibilityTests.WriteOwnershipEdgeFixtures(root);
        var cache = TestScratch.Dir("al-runner-multipath-edges-warm-cache");

        AssertAllPass(AppGroupObjectVisibilityTests.RunCli($" --cache \"{cache}\" {Quoted(dirs)}"), 40);
        AssertAllPass(AppGroupObjectVisibilityTests.RunCli($" --cache \"{cache}\" {Quoted(dirs)}"), 40);
    }

    /// <summary>
    /// Two bundles, each run after both loaded, in both orders. P's run needs its own dependency's
    /// Install trigger, which only P's install-dependency set carries: Q loads after P in [P,Q], so
    /// a run reading what the last load left fires Q's (empty) set and P's seed row is missing.
    /// Module info and resources are each bundle's own in both orders.
    /// </summary>
    [SkippableTheory]
    [InlineData("rsP", "rsQ")]
    [InlineData("rsQ", "rsP")]
    public void TwoBundlePaths_EachRunReadsItsOwnInstallDependenciesModuleInfoAndResources(string first, string second)
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir($"al-runner-multipath-runstate-{first}");
        Directory.CreateDirectory(root);
        WriteRunStateFixture(root);
        var cache = TestScratch.Dir($"al-runner-multipath-runstate-{first}-cache");

        for (var pass = 0; pass < 2; pass++)
        {
            var (output, exitCode) = AppGroupObjectVisibilityTests.RunCli(
                $" --cache \"{cache}\" --show-pass {Quoted(new[] { Path.Combine(root, first), Path.Combine(root, second) })}");
            AssertAllPass((output, exitCode), 5, $"pass {pass}: ");
            foreach (var t in new[]
                     {
                         "Codeunit62840.ItsDependencysInstallTriggerRan", "Codeunit62840.ModuleInfoIsItsOwn",
                         "Codeunit62840.ResourceIsItsOwn", "Codeunit62850.ModuleInfoIsItsOwn", "Codeunit62850.ResourceIsItsOwn",
                     })
                Assert.Matches(new Regex($@"PASS +{Regex.Escape(t)}\b"), output);
        }
    }

    /// <summary>A run over several paths prints the run-wide `Tests:` line, not one bundle's.</summary>
    private static void AssertAllPass((string Output, int ExitCode) run, int tests, string label = "")
    {
        var (output, exitCode) = run;
        Assert.True(Regex.IsMatch(output, $@"Tests: {tests} +passed {tests} +failed 0 +errors 0"), label + output);
        Assert.DoesNotContain("MISSING:", output);
        Assert.DoesNotContain("WRONG:", output);
        Assert.Equal(0, exitCode);
    }

    private static void WriteRunStateFixture(string root)
    {
        WriteApp(Path.Combine(root, "rsdep"), RsDep, "RS Dep", 62830, 62839, resource: null);
        File.WriteAllText(Path.Combine(root, "rsdep", "Dep.al"), """
        table 62830 "RS Seed" { fields { field(1; "Code"; Code[20]) { } } keys { key(PK; "Code") { Clustered = true; } } }
        codeunit 62831 "RS Dep Install"
        {
            Subtype = Install;
            trigger OnInstallAppPerCompany()
            var
                Seed: Record "RS Seed";
            begin
                Seed.Code := 'P-DEP';
                if Seed.Insert() then;
            end;
        }
        """);

        foreach (var (letter, appId, cu, from) in new[] { ("P", RsP, 62840, 62840), ("Q", RsQ, 62850, 62850) })
        {
            var dir = Path.Combine(root, "rs" + letter);
            WriteApp(dir, appId, "RS " + letter, from, from + 9, resource: letter,
                dependsOn: letter == "P" ? new[] { (RsDep, "RS Dep") } : Array.Empty<(Guid, string)>());
            var installTest = letter != "P" ? "" : """
                [Test]
                procedure ItsDependencysInstallTriggerRan()
                var
                    Seed: Record "RS Seed";
                begin
                    if not Seed.Get('P-DEP') then Error('WRONG: the Install trigger of P''s own dependency did not run for P');
                end;

            """;
            File.WriteAllText(Path.Combine(dir, "Tests.al"), $$"""
            codeunit {{cu}} "RS {{letter}} Tests"
            {
                Subtype = Test;
            {{installTest}}
                [Test]
                procedure ModuleInfoIsItsOwn()
                var
                    Info: ModuleInfo;
                begin
                    NavApp.GetCurrentModuleInfo(Info);
                    if Info.Name <> 'RS {{letter}}' then Error('WRONG: module info in {{letter}} names %1', Info.Name);
                end;

                [Test]
                procedure ResourceIsItsOwn()
                var
                    InS: InStream;
                    Got: Text;
                begin
                    NavApp.GetResource('rs.txt', InS);
                    InS.ReadText(Got);
                    if Got <> '{{letter}}' then Error('WRONG: resource rs.txt in {{letter}} reads %1', Got);
                end;
            }
            """);
        }
    }

    private static void WriteApp(string dir, Guid appId, string name, int from, int to, string? resource,
        (Guid Id, string Name)[]? dependsOn = null)
    {
        Directory.CreateDirectory(dir);
        var deps = "[" + string.Join(", ", (dependsOn ?? Array.Empty<(Guid, string)>()).Select(d =>
            $$"""{ "id": "{{d.Id}}", "name": "{{d.Name}}", "publisher": "AL Runner", "version": "1.0.0.0" }""")) + "]";
        var resources = resource == null ? "" : """ "resourceFolders": [ "res" ],""";
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        { "id": "{{appId}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0",{{resources}}
          "dependencies": {{deps}}, "platform": "1.0.0.0", "idRanges": [ { "from": {{from}}, "to": {{to}} } ], "runtime": "14.0" }
        """);
        if (resource == null) return;
        Directory.CreateDirectory(Path.Combine(dir, "res"));
        File.WriteAllText(Path.Combine(dir, "res", "rs.txt"), resource);
    }
}
