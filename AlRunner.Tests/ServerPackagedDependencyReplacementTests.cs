// ServerPackagedDependencyReplacementTests — issue #3974.
//
// One --server process, one source bundle (the tests), and a dependency supplied as a packaged
// `.app` with embedded source that is REWRITTEN AT THE SAME PATH between requests. The dependency
// answers Twice(21); the test asserts 42 and names the value it saw, so a PASS after the package
// changed to `Value * 3` means a previous request's module executed.
//
// Distinct from ServerCrossAppStaleGenerationTests (#1901), which passes the dependency as a
// second SOURCE directory: here the dependency arrives through DependencyLoader's Tier-3 package
// path, whose module name carries the app version.
//
// Two branches, one fact each, because they are two different reuse decisions:
//   * version bumped with the package  -> the old `Dep_..._<old version>` module stayed eligible
//     for AL object lookup beside the new one;
//   * version unchanged, content changed -> LoadAll reused the cached module without asking
//     whether the package at that path still held the bytes it was compiled from.
//
// Each fact runs its sequence on a cold server, then again on a fresh server sharing the same
// --cache root (compiled-deps HITs), per .claude/rules/local-test-scope.md. The fresh server's
// first request is also the "changed package in a new process" control.

using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerPackagedDependencyReplacementTests
{
    private sealed record Fixture(string SubjectDir, string PackageDir, string TestsDir, string CacheDir, string AppPath);

    private static Fixture Create(string tag, string subjectAppId, string testsAppId, int idBase)
    {
        // The dependency's source lives under a different scratch root than the tests, so sibling
        // source discovery cannot supply it: the package is the only route.
        var subjectDir = TestScratch.Dir($"al-runner-3974-{tag}-subject-src");
        var root = TestScratch.Dir($"al-runner-3974-{tag}");
        var packageDir = Path.Combine(root, "packages");
        var testsDir = Path.Combine(root, "tests");
        var cacheDir = Path.Combine(root, ".cache");
        Directory.CreateDirectory(subjectDir);
        Directory.CreateDirectory(packageDir);
        Directory.CreateDirectory(testsDir);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsAppId}}",
          "name": "Repro3974 Tests {{tag}}",
          "publisher": "Repro3974",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{subjectAppId}}", "name": "Repro3974 Subject {{tag}}", "publisher": "Repro3974", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idBase + 5}}, "to": {{idBase + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testsDir, "Tests.Codeunit.al"), $$"""
        codeunit {{idBase + 5}} "Repro3974 Tests {{tag}}"
        {
            Subtype = Test;

            [Test]
            procedure DependencyLogic()
            var
                Logic: Codeunit "Repro3974 Logic {{tag}}";
                Actual: Integer;
            begin
                Actual := Logic.Twice(21);
                if Actual <> 42 then
                    Error('dependency result: expected 42, actual %1', Actual);
            end;
        }
        """);

        var appPath = Path.Combine(packageDir, $"Repro3974_Subject_{tag}.app");
        return new Fixture(subjectDir, packageDir, testsDir, cacheDir, appPath);
    }

    /// <summary>
    /// The dependency's compile-time symbols, in the shape BC's own compiler writes into a
    /// package. The method Id is BC's: copied from the SymbolReference.json `al compile` produced
    /// for this exact signature, `Twice(Value: Integer): Integer`; the dependent's compiled call
    /// dispatches by that id, so it must match what the Tier-3 source compile emits.
    /// </summary>
    private static byte[] SymbolReference(string appId, string tag, int idBase, string version) =>
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            RuntimeVersion = "14.0",
            Codeunits = new object[]
            {
                new
                {
                    Methods = new object[]
                    {
                        new
                        {
                            ReturnTypeDefinition = new { Name = "Integer" },
                            Parameters = new object[] { new { Name = "Value", TypeDefinition = new { Name = "Integer" } } },
                            Id = 1516892452,
                            Name = "Twice",
                        },
                    },
                    Id = idBase,
                    Name = $"Repro3974 Logic {tag}",
                },
            },
            AppId = appId,
            Name = $"Repro3974 Subject {tag}",
            Publisher = "Repro3974",
            Version = version,
        }));

    /// <summary>Rewrite the dependency's source and re-package it at the SAME .app path.</summary>
    private static void PublishSubject(Fixture f, string subjectAppId, string tag, int idBase, string version, int multiplier)
    {
        File.WriteAllText(Path.Combine(f.SubjectDir, "app.json"), $$"""
        {
          "id": "{{subjectAppId}}",
          "name": "Repro3974 Subject {{tag}}",
          "publisher": "Repro3974",
          "version": "{{version}}",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idBase}}, "to": {{idBase + 4}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(f.SubjectDir, "Logic.Codeunit.al"), $$"""
        codeunit {{idBase}} "Repro3974 Logic {{tag}}"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * {{multiplier}});
            end;
        }
        """);
        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(f.SubjectDir, "app.json"))
            ?? throw new InvalidOperationException("fixture app.json did not parse");
        var before = File.Exists(f.AppPath) ? File.GetLastWriteTimeUtc(f.AppPath) : DateTime.MinValue;
        InProcessAppPackager.EmitAppPackageToFile(
            f.SubjectDir, identity, f.AppPath, SymbolReference(subjectAppId, tag, idBase, version));
        // `* 2` and `* 3` package to the same byte length; move mtime forward explicitly so no
        // (inode, size, mtime) file identity can mistake the rewrite for the previous file.
        var after = before == DateTime.MinValue ? DateTime.UtcNow : before.AddSeconds(5);
        File.SetLastWriteTimeUtc(f.AppPath, after);
    }

    private static string Req(Fixture f) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { f.TestsDir },
        packagePaths = new[] { f.PackageDir },
        coverage = false,
    });

    /// <summary>
    /// Run one request and assert its single test's verdict. <paramref name="expectedActual"/>
    /// null means PASS (the dependency answered 42); otherwise the failure must name that value,
    /// which is what says WHICH compile of the dependency executed.
    /// </summary>
    private static async Task AssertRequest(CliServer server, Fixture f, string label, int? expectedActual)
    {
        var lines = await server.SendRequestStreamingAsync(Req(f), TimeSpan.FromSeconds(240));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var joined = string.Join("\n", lines);
        var ev = events.SingleOrDefault(e => e.GetProperty("name").GetString()!.EndsWith("DependencyLogic"));
        Assert.True(ev.ValueKind == JsonValueKind.Object,
            $"{label}: DependencyLogic did not run.\n{joined}\n--- stderr ---\n{server.StdErr}");
        var status = ev.GetProperty("status").GetString();
        var message = ev.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (expectedActual is null)
        {
            Assert.True(status == "pass", $"{label}: expected PASS (answer 42), got {status}: {message}");
            Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        }
        else
        {
            Assert.True(status == "fail",
                $"{label}: expected FAIL naming actual {expectedActual} — a PASS means a previous " +
                $"request's dependency module executed. Got {status}: {message}");
            Assert.Contains($"expected 42, actual {expectedActual}", message);
        }
    }

    [SkippableFact]
    public async Task PackageReplacedWithANewVersion_ExecutesTheNewCode_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        const string tag = "V";
        const string subjectId = "3974a000-0000-4000-8000-00000000a001";
        const string testsId = "3974a000-0000-4000-8000-00000000a002";
        const int idBase = 63970;
        var f = Create(tag, subjectId, testsId, idBase);
        var args = new[] { "--cache", f.CacheDir, "--package-cache", f.PackageDir };
        try
        {
            await using (var cold = await CliServer.StartAsync(args))
            {
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.0", multiplier: 2);
                await AssertRequest(cold, f, "cold 1 (v1.0.0.0, *2)", null);
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.1", multiplier: 3);
                await AssertRequest(cold, f, "cold 2 (v1.0.0.1, *3)", 63);
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.2", multiplier: 2);
                await AssertRequest(cold, f, "cold 3 (v1.0.0.2, *2)", null);
            }

            // Fresh process, same --cache root: every dependency compile below is a HIT.
            await using (var warm = await CliServer.StartAsync(args))
            {
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.1", multiplier: 3);
                await AssertRequest(warm, f, "warm 1 / fresh-server control (v1.0.0.1, *3)", 63);
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.2", multiplier: 2);
                await AssertRequest(warm, f, "warm 2 (v1.0.0.2, *2)", null);
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.1", multiplier: 3);
                await AssertRequest(warm, f, "warm 3 (v1.0.0.1 again, *3)", 63);
            }
        }
        finally
        {
            try { Directory.Delete(f.SubjectDir, recursive: true); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(f.PackageDir)!, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task PackageReplacedAtTheSameVersion_ExecutesTheNewCode_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        const string tag = "S";
        const string subjectId = "3974b000-0000-4000-8000-00000000b001";
        const string testsId = "3974b000-0000-4000-8000-00000000b002";
        const int idBase = 63980;
        var f = Create(tag, subjectId, testsId, idBase);
        var args = new[] { "--cache", f.CacheDir, "--package-cache", f.PackageDir };
        try
        {
            await using (var cold = await CliServer.StartAsync(args))
            {
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.0", multiplier: 2);
                await AssertRequest(cold, f, "cold 1 (*2)", null);
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.0", multiplier: 3);
                await AssertRequest(cold, f, "cold 2 (same version, *3)", 63);
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.0", multiplier: 2);
                await AssertRequest(cold, f, "cold 3 (same version, *2)", null);
            }

            await using (var warm = await CliServer.StartAsync(args))
            {
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.0", multiplier: 3);
                await AssertRequest(warm, f, "warm 1 / fresh-server control (*3)", 63);
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.0", multiplier: 2);
                await AssertRequest(warm, f, "warm 2 (same version, *2)", null);
                PublishSubject(f, subjectId, tag, idBase, "1.0.0.0", multiplier: 3);
                await AssertRequest(warm, f, "warm 3 (same version, *3)", 63);
            }
        }
        finally
        {
            try { Directory.Delete(f.SubjectDir, recursive: true); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(f.PackageDir)!, recursive: true); } catch { }
        }
    }
}
