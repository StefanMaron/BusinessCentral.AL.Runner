// ServerSiblingSourceDependencyReloadTests — issue #4025.
//
// One --server process, one requested bundle (the tests), and a dependency that exists only as
// AL source in a sibling directory, so BuildSiblingSourceDeps is the only route that supplies it.
// The dependency answers Twice(21); the test asserts 42 and names the value it saw, so a PASS after
// the source changed to `Value * 3` means an earlier request's compile executed.
//
// Same AppId and version throughout: a version bump additionally needs the displaced module
// retired, which is #3974's fix (PR #4008), not this one.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerSiblingSourceDependencyReloadTests
{
    private sealed record Fixture(string Root, string SubjectDir, string TestsDir, string CacheDir);

    private static Fixture Create(string tag, string subjectAppId, string testsAppId, int idBase)
    {
        var root = TestScratch.Dir($"al-runner-4025-{tag}");
        var subjectDir = Path.Combine(root, "subject");
        var testsDir = Path.Combine(root, "tests");
        var cacheDir = TestScratch.Dir($"al-runner-4025-{tag}-cache");
        Directory.CreateDirectory(subjectDir);
        Directory.CreateDirectory(testsDir);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsAppId}}",
          "name": "Repro4025 Tests {{tag}}",
          "publisher": "Repro4025",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{subjectAppId}}", "name": "Repro4025 Subject {{tag}}", "publisher": "Repro4025", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idBase + 5}}, "to": {{idBase + 9}} } ],
          "runtime": "14.0"
        }
        """);
        var f = new Fixture(root, subjectDir, testsDir, cacheDir);
        WriteTests(f, tag, idBase, "request 1");
        return f;
    }

    /// <summary>The test bundle; <paramref name="marker"/> lets a request change only this bundle.</summary>
    private static void WriteTests(Fixture f, string tag, int idBase, string marker) =>
        File.WriteAllText(Path.Combine(f.TestsDir, "Tests.Codeunit.al"), $$"""
        // {{marker}}
        codeunit {{idBase + 5}} "Repro4025 Tests {{tag}}"
        {
            Subtype = Test;

            [Test]
            procedure DependencyLogic()
            var
                Logic: Codeunit "Repro4025 Logic {{tag}}";
                Actual: Integer;
            begin
                Actual := Logic.Twice(21);
                if Actual <> 42 then
                    Error('dependency result: expected 42, actual %1', Actual);
            end;
        }
        """);

    /// <summary>Write the (version, multiplier) variant of the sibling dependency's source.</summary>
    private static void WriteSubject(Fixture f, string subjectAppId, string tag, int idBase, string version, int multiplier)
    {
        File.WriteAllText(Path.Combine(f.SubjectDir, "app.json"), $$"""
        {
          "id": "{{subjectAppId}}",
          "name": "Repro4025 Subject {{tag}}",
          "publisher": "Repro4025",
          "version": "{{version}}",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idBase}}, "to": {{idBase + 4}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(f.SubjectDir, "Logic.Codeunit.al"), $$"""
        codeunit {{idBase}} "Repro4025 Logic {{tag}}"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * {{multiplier}});
            end;
        }
        """);
    }

    private static string Req(Fixture f) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { f.TestsDir },
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
            Assert.True(status == "pass", $"{label}: expected PASS (answer 42), got {status}: {message}\n--- stderr ---\n{server.StdErr}");
            Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        }
        else
        {
            Assert.True(status == "fail",
                $"{label}: expected FAIL naming actual {expectedActual} — a PASS means an earlier " +
                $"request's compile of the sibling dependency executed. Got {status}: {message}\n--- stderr ---\n{server.StdErr}");
            Assert.Contains($"expected 42, actual {expectedActual}", message);
        }
    }

    /// <summary>The second server must have served the dependency from the shared --cache root;
    /// otherwise its half of the fact is a second cold run, not a warm one.</summary>
    private static void AssertWarm(CliServer warm, string tag) =>
        Assert.True(warm.StdErr.Contains($"[deps] source-cache HIT: Repro4025 Subject {tag}"),
            $"the fresh server never hit the compiled-deps cache:\n{warm.StdErr}");

    private static async Task RunSequence(string tag, string subjectId, string testsId, int idBase,
        (string Version, int Multiplier)[] cold, (string Version, int Multiplier)[] warm)
    {
        TestArtifacts.SkipIfMissing();
        var f = Create(tag, subjectId, testsId, idBase);
        var args = new[] { "--cache", f.CacheDir, "--verbose" };
        static int? Expect(int multiplier) => multiplier == 2 ? null : 21 * multiplier;
        try
        {
            await using (var server = await CliServer.StartAsync(args))
            {
                for (int i = 0; i < cold.Length; i++)
                {
                    WriteSubject(f, subjectId, tag, idBase, cold[i].Version, cold[i].Multiplier);
                    await AssertRequest(server, f, $"cold {i + 1} (v{cold[i].Version}, *{cold[i].Multiplier})", Expect(cold[i].Multiplier));
                }

                // Negative arm: only the TEST bundle changes. Rebuilding from the base caches
                // every request must still serve the unchanged dependency from its content-keyed
                // workspace directory rather than re-synthesising it.
                var mark = server.StdErrMark;
                WriteTests(f, tag, idBase, "tests-only edit");
                await AssertRequest(server, f, "cold, tests-only edit", Expect(cold[^1].Multiplier));
                var slice = await server.StdErrSinceAsync(mark, $"→ Repro4025_Repro4025_Subject_{tag}_");
                Assert.Contains($"[source-dep] cache HIT Repro4025 Subject {tag} ", slice);
                Assert.DoesNotContain($"[source-dep] WROTE Repro4025 Subject {tag} ", slice);
            }

            // Fresh process, same --cache root.
            await using (var server = await CliServer.StartAsync(args))
            {
                for (int i = 0; i < warm.Length; i++)
                {
                    WriteSubject(f, subjectId, tag, idBase, warm[i].Version, warm[i].Multiplier);
                    await AssertRequest(server, f, $"warm {i + 1} (v{warm[i].Version}, *{warm[i].Multiplier})", Expect(warm[i].Multiplier));
                }
                AssertWarm(server, tag);
            }
        }
        finally
        {
            try { Directory.Delete(f.Root, recursive: true); } catch { }
            try { Directory.Delete(f.CacheDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public Task SiblingSourceChangedAtTheSameVersion_ExecutesTheNewCode_ColdAndWarm() =>
        RunSequence("S", "4025b000-0000-4000-8000-00000000b001", "4025b000-0000-4000-8000-00000000b002", 64040,
            cold: new[] { ("1.0.0.0", 2), ("1.0.0.0", 3), ("1.0.0.0", 2) },
            warm: new[] { ("1.0.0.0", 3), ("1.0.0.0", 2), ("1.0.0.0", 3) });
}
