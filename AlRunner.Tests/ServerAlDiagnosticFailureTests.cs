// Issue #2152 — the AL-diagnostic compile-failure guard #2150/#2154 added only
// covered the default bundled CLI path. --server's per-request compile path
// (RunBundleForServer, shared by both `runTests` and `execute`) has the identical
// BC ContinueBuildOnError gap: `sources` can come back non-empty (a broken query
// column's metadata still emits) at the same time `alDiagnostics` also carries the
// AL0353 BC reported for it, and before this fix nothing in the server path checked
// that combination — the request could report a clean run for AL that can never
// build against a real service tier.
//
// This one matters more than the other two follow-up paths: --server is what an
// editor integration drives on every save. A false green here reaches a developer's
// inner loop with nothing telling them their AL would not build against BC.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerAlDiagnosticFailureTests
{
    private static string WriteBundle(string suffix, string queryBody)
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-server-al0353-" + suffix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "e4444444-4444-4444-4444-444444444444",
          "name": "Server AL0353 Diagnostic Test",
          "publisher": "Repro2152",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62220, "to": 62229 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Order.Table.al"), """
        table 62220 "AL0353 Srv Order"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Amount; Decimal) { }
            }
            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);
        File.WriteAllText(Path.Combine(root, "OrderSum.Query.al"), queryBody);
        return root;
    }

    private static string Req(string bundle) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
    });

    [SkippableFact]
    public async Task RunTests_ColumnDeclaresDataSourceAndMethodCount_ReportsCompilationErrorAndNonZeroExit()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = WriteBundle("bad", """
        query 62221 "AL0353 Srv Order Sum"
        {
            QueryType = Normal;

            elements
            {
                dataitem(Order; "AL0353 Srv Order")
                {
                    column(TheAmount; Amount) { }
                    column(CountAmount; Amount) { Method = Count; }
                }
            }
        }
        """);
        try
        {
            await using var server = await CliServer.StartAsync();
            var lines = await server.SendRequestStreamingAsync(Req(bundle), TimeSpan.FromSeconds(180));
            var (_, d) = ProtocolV2Streaming.Split(lines);

            // Would still pass if the server always returned a default/no-op success —
            // assert the SPECIFIC BC diagnostic surfaced over the wire, not just "some
            // non-zero exit code".
            Assert.NotEqual(0, d.GetProperty("exitCode").GetInt32());
            Assert.Equal(0, d.GetProperty("total").GetInt32());
            Assert.True(d.TryGetProperty("compilationErrors", out var compileErrors),
                $"expected compilationErrors on an AL-diagnostic compile failure: {string.Join(" | ", lines)}");
            var allErrorText = string.Join(" | ", compileErrors.EnumerateArray()
                .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())));
            Assert.Contains("AL0353", allErrorText);
            Assert.Contains("A Column must have a valid data source or have the 'Method' property set to 'Count'", allErrorText);
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task RunTests_ColumnMethodCountWithNoDataSource_RunsCleanly()
    {
        TestArtifacts.SkipIfMissing();
        // The corrected form real BC accepts — proves the server-mode gate does not
        // also reject valid AL. Required alongside the negative test above: a guard
        // that always failed compilation would pass that test too.
        var bundle = WriteBundle("good", """
        query 62222 "AL0353 Srv Order Sum"
        {
            QueryType = Normal;

            elements
            {
                dataitem(Order; "AL0353 Srv Order")
                {
                    column(TheAmount; Amount) { }
                    column(CountAmount) { Method = Count; }
                }
            }
        }
        """);
        try
        {
            await using var server = await CliServer.StartAsync();
            var lines = await server.SendRequestStreamingAsync(Req(bundle), TimeSpan.FromSeconds(180));
            var (_, d) = ProtocolV2Streaming.Split(lines);

            Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
            Assert.False(d.TryGetProperty("compilationErrors", out _),
                $"unexpected compile error: {string.Join(" | ", lines)}");
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }
}
