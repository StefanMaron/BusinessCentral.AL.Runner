// EnumSidecarOwnContributionTests — #3579: a source dependency's enum sidecar must carry what
// THAT dependency registered, including an enumextension of a base another app registered
// first, and nothing else. DependencyLoader used to diff enum ids before and after the emit; an
// extension lands under an id that already exists, so it never reached the sidecar and a warm
// run that served the dependency from cache lost the value.
//
// Two layers: the registry's own RegisteredSince/SaveSidecar, and the real runner on three
// sibling source apps (base enum, extension, tests), cold and then warm on one --cache root with
// the consuming app changed so only the dependency sidecars can supply the extension.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

[Collection(EnumMetadataRegistrySerialCollection.Name)]
public sealed class EnumSidecarOwnContributionTests
{
    private static (string[] Names, int?[] Extends) Contributions(AlEnumMetadataRegistry.RawMark mark)
    {
        var raw = AlEnumMetadataRegistry.RegisteredSince(mark);
        return (raw.Select(r => r.Entry.Name).ToArray(), raw.Select(r => r.ExtendsTargetId).ToArray());
    }

    [Fact]
    public void ExtensionOfABaseRegisteredEarlier_IsThisDependencysContribution_TheBaseIsNot()
    {
        const int target = 935791;
        AlEnumMetadataRegistry.Register(target, "OC Base A", new[] { "Base" }, new[] { 0 });
        var mark = AlEnumMetadataRegistry.Mark();

        AlEnumMetadataRegistry.RegisterExtension(target, "OC Ext B", new[] { "Added" }, new[] { 10 }, null, new string?[] { "Added caption" });

        var (names, extends) = Contributions(mark);
        Assert.Equal(new[] { "OC Ext B" }, names);
        Assert.Equal(new int?[] { target }, extends);
    }

    [Fact]
    public void BaseRegisteredAfterAnEarlierExtension_IsThisDependencysContribution_TheExtensionIsNot()
    {
        const int target = 935792;
        AlEnumMetadataRegistry.RegisterExtension(target, "OC Ext Earlier", new[] { "Added" }, new[] { 10 });
        var mark = AlEnumMetadataRegistry.Mark();

        AlEnumMetadataRegistry.Register(target, "OC Base Later", new[] { "Base" }, new[] { 0 });

        var (names, extends) = Contributions(mark);
        Assert.Equal(new[] { "OC Base Later" }, names);
        Assert.Equal(new int?[] { null }, extends);
    }

    [Fact]
    public void SecondExtensionOfOneTarget_OnlyTheNewOneIsContributed_AndItRoundTrips()
    {
        const int target = 935793;
        AlEnumMetadataRegistry.Register(target, "OC Base C", new[] { "Base" }, new[] { 0 });
        AlEnumMetadataRegistry.RegisterExtension(target, "OC Ext Other App", new[] { "Other" }, new[] { 5 });
        var mark = AlEnumMetadataRegistry.Mark();
        AlEnumMetadataRegistry.RegisterExtension(target, "OC Ext Mine", new[] { "Mine" }, new[] { 7 }, null, new string?[] { "Mine caption" });

        var path = TestScratch.FilePath("enum-own-contribution-3579", "mine.enum-registry.json");
        Assert.Equal(1, AlEnumMetadataRegistry.SaveSidecar(path, AlEnumMetadataRegistry.RegisteredSince(mark)));
        var json = File.ReadAllText(path);
        Assert.Contains("\"name\":\"OC Ext Mine\"", json);
        Assert.Contains("\"extends\":935793", json);
        Assert.Contains("\"captions\":[\"Mine caption\"]", json);
        Assert.DoesNotContain("OC Ext Other App", json);
        Assert.DoesNotContain("OC Base C", json);
    }
}

/// <summary>The same claim end to end, through the dependency sidecar a warm run replays.</summary>
public sealed class EnumSidecarOwnContributionEndToEndTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string BaseId = "3579a000-0000-4000-8000-000000003579";
    private const string MidId = "3579b000-0000-4000-8000-000000003579";

    private static string Dep(string id, string name) =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "publisher": "Gap3579", "version": "1.0.0.0" }""";

    private static void App(string dir, string id, string name, int from, string deps, string file, string al)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            { "id": "{{id}}", "name": "{{name}}", "publisher": "Gap3579", "version": "1.0.0.0",
              "dependencies": [ {{deps}} ], "platform": "1.0.0.0",
              "idRanges": [ { "from": {{from}}, "to": {{from + 9}} } ], "runtime": "14.0" }
            """);
        File.WriteAllText(Path.Combine(dir, file), al);
    }

    private static void WriteTests(string dir, string marker) =>
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
            codeunit 71470 "Sidecar Gap Tests"
            {
                Subtype = Test;

                [Test]
                procedure BaseAndExtensionSurviveReplay()
                var
                    Values: List of [Integer];
                    State: Enum "Sidecar Gap State";
                begin
                    // {{marker}}
                    Values := Enum::"Sidecar Gap State".Ordinals();
                    if (Values.Count() <> 2) or not Values.Contains(0) or not Values.Contains(10) then
                        Error('GAP3579 FAIL: %1 ordinal(s)', Values.Count());
                    State := Enum::"Sidecar Gap State".FromInteger(10);
                    if Format(State) <> 'Added caption' then
                        Error('GAP3579 FAIL: value 10 formats as [%1]', Format(State));
                    State := Enum::"Sidecar Gap State".FromInteger(0);
                    if Format(State) <> 'Base caption' then
                        Error('GAP3579 FAIL: value 0 formats as [%1]', Format(State));
                end;
            }
            """);

    private static (string Output, int Exit) RunRunner(string bundle, string cacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --cache \"{cacheDir}\" --verbose --show-pass");
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        args.Append($" \"{bundle}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void ExtensionInAMiddleDependency_SurvivesAWarmRunThatReplaysItsSidecar()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-enum-sidecar-3579");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var cache = Path.Combine(root, "cache");
        App(Path.Combine(root, "base"), BaseId, "Gap3579 Base", 71450, "", "Base.Enum.al", """
            enum 71450 "Sidecar Gap State"
            {
                Extensible = true;
                value(0; Base) { Caption = 'Base caption'; }
            }
            """);
        App(Path.Combine(root, "mid"), MidId, "Gap3579 Mid", 71460, Dep(BaseId, "Gap3579 Base"), "Ext.EnumExt.al", """
            enumextension 71460 "Sidecar Gap Extension" extends "Sidecar Gap State"
            {
                value(10; Added) { Caption = 'Added caption'; }
            }
            """);
        var tests = Path.Combine(root, "tests");
        App(tests, "3579c000-0000-4000-8000-000000003579", "Gap3579 Tests", 71470,
            Dep(BaseId, "Gap3579 Base") + ", " + Dep(MidId, "Gap3579 Mid"), "Tests.Codeunit.al", "");
        WriteTests(tests, "cold");

        var (cold, _) = RunRunner(tests, cache);
        Assert.Contains("[deps] source-cache WROTE: Gap3579 Mid", cold);
        Assert.Contains("[deps] source-cache WROTE: Gap3579 Mid v1.0.0.0", cold);
        Assert.Matches(@"source-cache WROTE: Gap3579 Mid v1\.0\.0\.0 .* 1 enum-registry entries", cold);
        Assert.Matches(@"(?m)^PASS\s+\S*BaseAndExtensionSurviveReplay", cold);

        // Warm: both dependencies come from the cache; the changed test source misses the
        // AL-output cache, so no bundle-level sidecar can supply the extension instead.
        WriteTests(tests, "warm");
        var (warm, _) = RunRunner(tests, cache);
        Assert.Contains("[deps] source-cache HIT: Gap3579 Base", warm);
        Assert.Contains("[deps] source-cache HIT: Gap3579 Mid", warm);
        Assert.DoesNotContain("GAP3579 FAIL", warm);
        Assert.Matches(@"(?m)^PASS\s+\S*BaseAndExtensionSurviveReplay", warm);
    }
}
