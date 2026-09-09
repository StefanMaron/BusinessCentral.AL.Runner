using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #3608 — a query the runner COMPILED gets its <c>MetaQuery</c> design object from BC's own
/// emitted metadata document (captured by #3548) instead of from the SymbolReference sidecar
/// plus four hardcoded constants.
///
/// <para>Every value asserted here is one the runner used to supply from a constant, and
/// <c>QMD Divergent</c> declares a value DIFFERING from each — <c>ReadShared</c> against the
/// hardcoded <c>ReadUncommitted</c>, <c>API</c> against the defaulted <c>Normal</c>, top 5
/// against 0, <c>Lists</c> against empty. A query that happened to agree with a hardcode could
/// not distinguish a fix from a no-op, which is why <c>QMD Plain</c> is here as well: it
/// declares none of them, so no single constant satisfies both trace lines at once.</para>
///
/// <para>None of the four reaches AL. There is no AL surface that reads a query's
/// <c>ReadState</c>, its design-time <c>TopNumberOfRows</c>, its <c>QueryCategory</c> or a
/// dataitem's <c>Distinct</c> — <c>AllObjWithCaption</c>'s Object Subtype reports
/// <c>QueryType</c>, but from the parsed-query registry, a different consumer this change does
/// not touch. So this is a runner-mechanism test observing
/// <c>AL_RUNNER_TRACE_QUERY_METADATA_SOURCE=1</c>, the same shape
/// <see cref="TableMetadataFromBcDocumentTests"/> uses and for the same reason: the two
/// construction routes produce the same TYPE, so nothing downstream can be asked which one ran.
/// The AL-observable half of the change — an omitted <c>SqlJoinType</c> joining as
/// <c>LeftOuterJoin</c> — is adjudicated by a real service tier in corpus PR #297.</para>
///
/// <para>Warm assertions are identical to the cold ones and run against the same cache
/// directory (<c>.claude/rules/local-test-scope.md</c>): BC's Emit runs only on a compile-cache
/// MISS, so a warm run that lost the document would silently fall back to the derivation while
/// the run stayed green.</para>
///
/// <para>Spawns the real runner; needs the BC artifact cache. Skips when absent.</para>
/// </summary>
public class QueryMetadataFromBcDocumentTests
{
    private const int DivergentQueryId = 70720;
    private const int PlainQueryId = 70721;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "QueryMetadataFromBcDocument"));

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
    }

    private static (string output, int exit) RunRunner(string bundleDir, string alCacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        args.Append(" --verbose");
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
        psi.Environment["AL_RUNNER_TRACE_QUERY_METADATA_SOURCE"] = "1";
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps))
            psi.Arguments += $" --package-cache \"{platformApps}\"";
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

    /// <summary>The last trace line for one query whose text starts with the given suffix after
    /// the id — the last, because a query built more than once traces each build and the
    /// assertion is about the state the run ends up serving.</summary>
    private static string TraceLine(string output, int queryId, string discriminator)
    {
        var prefix = $"[query-metadata] {queryId} ";
        var hit = output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)
                                && l.Contains(discriminator, StringComparison.Ordinal));
        Assert.True(hit != null,
            $"no '{discriminator}' trace line for query {queryId}. "
            + $"AL_RUNNER_TRACE_QUERY_METADATA_SOURCE=1 emits one per built query.\n{output}");
        return hit!;
    }

    private static void AssertBothQueriesCameFromBcDocument(string output, string phase)
    {
        foreach (var id in new[] { DivergentQueryId, PlainQueryId })
            Assert.True(output.Contains($"[query-metadata] {id} source=bc-document"),
                $"{phase}: query {id} was not built from BC's metadata document. "
                + $"It is compiled by this bundle, so #3548 captures one for it.\n{output}");

        // Query 70720 declares a value differing from EVERY hardcode the derivation supplied.
        // A run still hardcoding them produces readState=ReadUncommitted and queryCategory=''
        // on this same line, so this single assertion cannot pass on the pre-fix build.
        var divergent = TraceLine(output, DivergentQueryId, "readState=");
        Assert.True(divergent.Contains("readState=ReadShared"),
            $"{phase}: query {DivergentQueryId} declares ReadState = ReadShared, not the "
            + "hardcoded ReadUncommitted.\n" + divergent);
        Assert.True(divergent.Contains("queryType=API"),
            $"{phase}: query {DivergentQueryId} declares QueryType = API, not the default Normal.\n"
            + divergent);
        Assert.True(divergent.Contains("top=5"),
            $"{phase}: query {DivergentQueryId} declares TopNumberOfRows = 5.\n" + divergent);
        Assert.True(divergent.Contains("queryCategory='Lists'"),
            $"{phase}: query {DivergentQueryId} declares QueryCategory = 'Lists'; the derivation "
            + "carried no QueryCategory at all.\n" + divergent);

        // Query 70721 declares NONE of them: the control. If it came back ReadShared or API,
        // the values above would be coming from somewhere other than each query's own
        // declaration, and no single constant could satisfy both lines.
        var plain = TraceLine(output, PlainQueryId, "readState=");
        Assert.True(plain.Contains("readState=ReadUncommitted"),
            $"{phase}: query {PlainQueryId} declares no ReadState, so it takes BC's default.\n"
            + plain);
        Assert.True(plain.Contains("queryType=Normal"),
            $"{phase}: query {PlainQueryId} declares QueryType = Normal.\n" + plain);
        Assert.True(plain.Contains("top=0"),
            $"{phase}: query {PlainQueryId} declares no TopNumberOfRows.\n" + plain);
        Assert.True(plain.Contains("queryCategory=''"),
            $"{phase}: query {PlainQueryId} declares no QueryCategory.\n" + plain);

        // The compiler-assigned columns. ColumnType is the one nothing but the document can
        // supply — the SymbolReference sidecar names the source FIELD, never the AL type the
        // compiler resolved it to, so the derivation left it at the design object's None.
        var totalAmount = TraceLine(output, DivergentQueryId, "column=TotalAmount");
        Assert.True(totalAmount.Contains("columnType=Decimal"),
            $"{phase}: column TotalAmount sums a Decimal field, so BC's document types it "
            + "Decimal; the derivation could only answer None.\n" + totalAmount);
        Assert.True(totalAmount.Contains("totalingMethod=Sum"),
            $"{phase}: column TotalAmount declares Method = Sum.\n" + totalAmount);
        Assert.True(totalAmount.Contains("index=1"),
            $"{phase}: TotalAmount is the second result column of the query.\n" + totalAmount);

        // A filter-only column takes QueryColumnIndex -1, not the design object's default 0 —
        // 0 is a real result slot, so the default would have it claim the first column's.
        var filterColumn = TraceLine(output, PlainQueryId, "column=HFilterNo");
        Assert.True(filterColumn.Contains("index=-1"),
            $"{phase}: HFilterNo is a filter(...) element, so it holds no result slot.\n"
            + filterColumn);
        Assert.True(filterColumn.Contains("filterOnly=True"),
            $"{phase}: HFilterNo is filter-only.\n" + filterColumn);

        // The nested dataitem of 70720 declares NO SqlJoinType. BC's document states
        // Left Outer Join for it; the derivation defaulted to InnerJoin, which dropped every
        // parent row with no matching child. Corpus PR #297 adjudicates that on real BC.
        var nested = TraceLine(output, DivergentQueryId, "dataItem=Line");
        Assert.True(nested.Contains("linkType=LeftOuterJoin"),
            $"{phase}: the nested dataitem declares no SqlJoinType, and AL defaults it to "
            + "LeftOuterJoin — not the InnerJoin the derivation assumed.\n" + nested);
    }

    [SkippableFact]
    public void CompiledQuery_IsBuiltFromBcsMetadataDocument_ColdAndOnAWarmCacheHit()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-query-metadata-from-bc");
        var bundle = Path.Combine(scratch, "bundle");
        var alCacheDir = Path.Combine(scratch, "al-out");
        CopyDir(FixtureRoot, bundle);

        var (cold, coldExit) = RunRunner(bundle, alCacheDir);
        Assert.True(coldExit == 0 && cold.Contains("1P/0F/0E"), $"cold run must pass:\n{cold}");
        AssertBothQueriesCameFromBcDocument(cold, "cold run");

        // Same sources, same cache directory: the AL-output cache HITs and Emit never runs, so
        // the document reaches this run only through the replayed sidecar. Without that replay
        // both queries fall back to the derivation and every assertion above flips.
        var (warm, warmExit) = RunRunner(bundle, alCacheDir);
        Assert.True(warmExit == 0 && warm.Contains("1P/0F/0E"), $"warm run must pass:\n{warm}");
        Assert.Contains("[cache] HIT", warm);
        AssertBothQueriesCameFromBcDocument(warm, "warm run (AL-output cache HIT)");
    }

    // The predicate half of #3608 lives in QueryMetadataDocumentPredicateTests.cs: it drives
    // AlObjectMetadataRegistry IN-PROCESS, so it has to sit in a serial collection, and this
    // class must not — its [SkippableFact] spawns the runner and would then serialize with
    // every other registry test for no benefit (#3613).
}
