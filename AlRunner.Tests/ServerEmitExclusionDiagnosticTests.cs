// Issue #2207 — server-mode sibling of EmitExclusionLoudnessTests.
//
// --server's EMIT-EXCLUDED compileErrors path already branched on "do we have the AL
// diagnostics that identified the excluded object(s)?" (Program.cs, RunBundleForServer),
// but the branch was dead code: it read `alDiagnostics`, which BcCompiler's emit-retry
// loop only ever populated from the FINAL (successfully recovered) compile round — by
// construction empty once the retry against the surviving objects succeeds. So every
// server-mode exclusion fell into the "Re-run with --verbose" fallback message, over a
// wire protocol that has no --verbose to re-run with at all.
//
// The fix threads BcEmitOutput's new ExcludedObjectDiagnostics field (populated
// unconditionally, not gated on --tdd) through instead — this proves the wire response
// actually carries the identifying AL diagnostic (AL0185), not just the generic count.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerEmitExclusionDiagnosticTests
{
    private static string WriteBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-server-emitexcl-2207", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "f5555555-5555-5555-5555-555555555555",
          "name": "Server Emit Exclusion Diagnostic Test",
          "publisher": "Repro2207",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 62230, "to": 62239 } ],
          "runtime": "14.0"
        }
        """);
        // Mirrors AlRunner.Tests/Fixtures/EmitExclusion/BrokenObject.Codeunit.al: a
        // reference to a codeunit that exists nowhere, so BC's Compilation.Emit crashes
        // on this object specifically and BcCompiler's retry loop excludes it.
        File.WriteAllText(Path.Combine(root, "Broken.Codeunit.al"), """
        codeunit 62230 "Srv Emit Excl Broken"
        {
            Subtype = Test;

            [Test]
            procedure Broken_NeverRuns()
            var
                Missing: Codeunit "This Server Codeunit Does Not Exist Either";
            begin
                Missing.DoSomething();
            end;
        }
        """);
        File.WriteAllText(Path.Combine(root, "Healthy.Codeunit.al"), """
        codeunit 62231 "Srv Emit Excl Healthy"
        {
            Subtype = Test;

            [Test]
            procedure Healthy_StillRuns()
            begin
            end;
        }
        """);
        return root;
    }

    private static string Req(string bundle) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
    });

    [SkippableFact]
    public async Task RunTests_ExcludedObject_CompilationErrorsCarryTheIdentifyingAlDiagnostic()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = WriteBundle();
        try
        {
            await using var server = await CliServer.StartAsync();
            var lines = await server.SendRequestStreamingAsync(Req(bundle), TimeSpan.FromSeconds(180));
            var (_, d) = ProtocolV2Streaming.Split(lines);

            Assert.NotEqual(0, d.GetProperty("exitCode").GetInt32());
            Assert.True(d.TryGetProperty("compilationErrors", out var compileErrors),
                $"expected compilationErrors on an EMIT-EXCLUDED failure: {string.Join(" | ", lines)}");
            var allErrorText = string.Join(" | ", compileErrors.EnumerateArray()
                .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())));

            Assert.Contains("EMIT-EXCLUDED", allErrorText);
            // Would still pass if the fix were a no-op that just always claimed success —
            // assert the SPECIFIC diagnostic that identifies WHY the object was excluded,
            // not merely that some compile error text is present.
            Assert.Contains("AL0185", allErrorText);
            Assert.Contains("This Server Codeunit Does Not Exist Either", allErrorText);
            // The dead "re-run with --verbose" fallback must not appear once the real
            // diagnostics are actually available — a server client has no CLI to re-run.
            Assert.DoesNotContain("Re-run with --verbose", allErrorText);
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }
}
