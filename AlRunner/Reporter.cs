// Reporter — aggregates per-test results into per-bucket and overall summaries,
// and writes a JSON failure-classification file for follow-up parallel work.
using System.Text.Json;

namespace AlRunner;

public enum BucketStage { CompileFailed, ExecuteFailed, Ran }

public sealed record BucketResult(string BucketPath, BucketStage Stage,
                                   IReadOnlyList<string> CompileErrors,
                                   string? ProcessError,
                                   IReadOnlyList<TestResult> Tests,
                                   TimeSpan EmitTime, TimeSpan CompileTime, TimeSpan RunTime,
                                   // Number of app groups (bundled mode) / suites (--per-suite) that
                                   // actually executed to completion — i.e. reached the point of
                                   // contributing their tests to `Tests`, not merely discovered on
                                   // disk. Optional/trailing so existing call sites (tests included)
                                   // that don't care about it keep compiling unchanged. Consumed by
                                   // Infrastructure.CountBaselineCheck (#1880) as a second, coarser
                                   // signal alongside the test-count baseline — see that file's header
                                   // for why a whole-group-vanished bug can hide behind an unchanged
                                   // test count.
                                   int RanGroupCount = 0);

public static class Reporter
{
    public static void PrintSummary(IReadOnlyList<BucketResult> buckets, TextWriter w)
    {
        int totalTests = 0, pass = 0, fail = 0, err = 0, skipped = 0;
        int passOos = 0, passKnownGap = 0, passDivergence = 0;
        int compileFailed = 0, execFailed = 0;
        TimeSpan emit = TimeSpan.Zero, comp = TimeSpan.Zero, run = TimeSpan.Zero;
        foreach (var b in buckets)
        {
            emit += b.EmitTime; comp += b.CompileTime; run += b.RunTime;
            if (b.Stage == BucketStage.CompileFailed) { compileFailed++; continue; }
            if (b.Stage == BucketStage.ExecuteFailed) { execFailed++; continue; }
            foreach (var t in b.Tests)
            {
                totalTests++;
                if (t.Outcome == TestOutcome.Pass)
                {
                    pass++;
                    if (t.Expectation == Infrastructure.ExpectationResult.PassOos) passOos++;
                    else if (t.Expectation == Infrastructure.ExpectationResult.PassKnownGap) passKnownGap++;
                    else if (t.Expectation == Infrastructure.ExpectationResult.PassDivergence) passDivergence++;
                }
                else if (t.Outcome == TestOutcome.Fail) fail++;
                else if (t.Outcome == TestOutcome.Skipped) skipped++;
                else err++;
            }
        }
        w.WriteLine();
        w.WriteLine("=================================================================");
        w.WriteLine("al-runner — test run summary");
        w.WriteLine("=================================================================");
        w.WriteLine($"Buckets:       {buckets.Count} total");
        w.WriteLine($"  ran:         {buckets.Count - compileFailed - execFailed}");
        w.WriteLine($"  compile-fail:{compileFailed}");
        w.WriteLine($"  exec-fail:   {execFailed}");
        w.WriteLine($"Tests:         {totalTests} total");
        w.WriteLine($"  pass:        {pass}");
        // Manifest reclassifications (docs/expectations.md) are surfaced DISTINCTLY so
        // a green run that got there via quarantined tests does not read as an
        // unqualified green. Zero-count lines are omitted: no manifest, no noise.
        if (passOos > 0)
            w.WriteLine($"    pass-oos:        {passOos}");
        if (passKnownGap > 0)
            w.WriteLine($"    pass-known-gap:  {passKnownGap}");
        if (passDivergence > 0)
            w.WriteLine($"    pass-divergence: {passDivergence}");
        w.WriteLine($"  fail:        {fail}");
        w.WriteLine($"  error:       {err}");
        if (skipped > 0)
            w.WriteLine($"  skipped:     {skipped}");
        w.WriteLine($"Time:");
        w.WriteLine($"  AL emit:     {emit.TotalSeconds:F1}s");
        w.WriteLine($"  C# compile:  {comp.TotalSeconds:F1}s");
        w.WriteLine($"  test run:    {run.TotalSeconds:F1}s");
        w.WriteLine($"  total:       {(emit + comp + run).TotalSeconds:F1}s");
        w.WriteLine("=================================================================");
    }

    // V1-style per-test output. By default prints only FAIL and ERROR entries (PASS is too
    // noisy at thousands of tests). `showPass=true` adds PASS lines for parity with V1's
    // `PASS  Codeunit.Method (Nms)` format.
    public static void PrintPerTest(IReadOnlyList<BucketResult> buckets, TextWriter w, bool showPass)
    {
        foreach (var b in buckets)
        {
            if (b.Stage == BucketStage.CompileFailed)
            {
                w.WriteLine();
                w.WriteLine($"=== {Path.GetFileName(b.BucketPath)} — COMPILE FAIL ===");
                foreach (var e in b.CompileErrors.Take(20)) w.WriteLine($"  {e}");
                if (b.CompileErrors.Count > 20)
                    w.WriteLine($"  ... and {b.CompileErrors.Count - 20} more compile errors");
                continue;
            }
            if (b.Stage == BucketStage.ExecuteFailed)
            {
                w.WriteLine();
                w.WriteLine($"=== {Path.GetFileName(b.BucketPath)} — EXEC FAIL ===");
                if (b.ProcessError != null) w.WriteLine($"  {b.ProcessError}");
                continue;
            }
            // Skip silent buckets — only emit a per-bucket header if there's anything to show.
            var visible = b.Tests.Where(t => showPass || t.Outcome != TestOutcome.Pass).ToList();
            if (visible.Count == 0) continue;
            w.WriteLine();
            w.WriteLine($"=== {Path.GetFileName(b.BucketPath)} ===");
            foreach (var t in visible)
            {
                var label = t.Outcome switch
                {
                    TestOutcome.Pass when t.Expectation == Infrastructure.ExpectationResult.PassOos => "PASS (oos)",
                    TestOutcome.Pass when t.Expectation == Infrastructure.ExpectationResult.PassKnownGap => "PASS (known-gap)",
                    TestOutcome.Pass when t.Expectation == Infrastructure.ExpectationResult.PassDivergence => "PASS (divergence)",
                    TestOutcome.Pass => "PASS ",
                    TestOutcome.Fail => "FAIL ",
                    TestOutcome.Error => "ERROR",
                    TestOutcome.Skipped => "SKIP ",
                    _ => "?    "
                };
                long ms = (long)t.Duration.TotalMilliseconds;
                w.WriteLine($"{label} {t.Codeunit}.{t.Method} ({ms}ms)");
                if (t.Outcome != TestOutcome.Pass)
                {
                    if (!string.IsNullOrEmpty(t.Message))
                        w.WriteLine($"      {t.Message}");
                    if (!string.IsNullOrEmpty(t.AlCallStack))
                    {
                        // Show the AL call stack (BC service-tier format), not the C# trace.
                        foreach (var frame in t.AlCallStack.Split('\n'))
                            w.WriteLine($"      {frame.TrimEnd('\r')}");
                    }
                    else if (!string.IsNullOrEmpty(t.FullException))
                    {
                        foreach (var line in FilteredStack(t.FullException, max: 8))
                            w.WriteLine($"      {line}");
                    }
                }
            }
        }
    }

    // Failure-classification summary — groups all failing tests by ClassifyTest output and
    // ranks descending by count. This is the "where to attack next" view: each line is a
    // cluster of failures that one runtime/API fix could potentially resolve in bulk.
    public static void PrintFailureClassification(IReadOnlyList<BucketResult> buckets,
        TextWriter w, int topN = 25)
    {
        var groups = buckets
            .Where(b => b.Stage == BucketStage.Ran)
            .SelectMany(b => b.Tests.Where(t => t.Outcome is TestOutcome.Fail or TestOutcome.Error))
            .GroupBy(t => ClassifyTest(t.Message ?? "", t.FullException ?? ""))
            .Select(g => (Classification: g.Key, Count: g.Count(),
                          Sample: g.First()))
            .OrderByDescending(g => g.Count)
            .ToList();
        if (groups.Count == 0) return;
        w.WriteLine();
        w.WriteLine($"=== Failures by classification (top {Math.Min(topN, groups.Count)} of {groups.Count}) ===");
        int totalFail = groups.Sum(g => g.Count);
        foreach (var g in groups.Take(topN))
        {
            double pct = 100.0 * g.Count / totalFail;
            w.WriteLine($"  {g.Count,6:N0}  {pct,5:F1}%  {g.Classification}");
        }
        if (groups.Count > topN)
        {
            int tailCount = groups.Skip(topN).Sum(g => g.Count);
            double tailPct = 100.0 * tailCount / totalFail;
            w.WriteLine($"  {tailCount,6:N0}  {tailPct,5:F1}%  ... and {groups.Count - topN} more classifications");
        }
    }

    // Strips noise from a .NET exception's stack trace — keeps frames that mention
    // user/MS BC types, drops async/await internals, runtime infra, and our own patch
    // dispatch frames. Returns up to `max` lines.
    private static IEnumerable<string> FilteredStack(string fullException, int max)
    {
        var noise = new[] {
            "System.Runtime.CompilerServices",
            "System.Threading.Tasks.Task",
            "MoveNext()",
            "System.Runtime.ExceptionServices",
            "AlRunner.Patches.AsyncStateMachineSpike",
            "AlRunner.Infrastructure.JmpHook",
        };
        int kept = 0;
        foreach (var raw in fullException.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("at ") == false) continue;
            if (noise.Any(n => line.Contains(n))) continue;
            yield return line.TrimStart();
            if (++kept >= max) yield break;
        }
    }

    // v1-shaped per-test JSON to stdout (--output-json), distinct from
    // WriteClassification's failure-classification file (--out). capturedValues/
    // iterations fields from v1 are intentionally omitted — those need a shared
    // Cecil-instrumentation prerequisite that doesn't exist yet, tracked separately.
    public static string SerializeJsonOutput(IReadOnlyList<BucketResult> buckets, int exitCode)
    {
        var tests = buckets
            .Where(b => b.Stage == BucketStage.Ran)
            .SelectMany(b => b.Tests)
            .ToList();

        // One entry per compile-failed bucket, identified by its FULL path. This used to
        // build a Dictionary keyed on Path.GetFileName(BucketPath), which threw an
        // unhandled ArgumentException ("An item with the same key has already been added")
        // whenever two bundles shared a last path segment — `al-runner ./appA/src
        // ./appB/src`, or the same directory passed twice. That fired here, during report
        // serialisation and AFTER the tests had already run, so a completed run's results
        // were discarded with a raw stack trace. The dictionary bought nothing: the result
        // is projected straight back into an array. Full paths also keep same-named
        // bundles distinguishable in the output — same field name as WriteClassification's
        // `bucket`. See #1692.
        var compileErrors = buckets
            .Where(b => b.Stage == BucketStage.CompileFailed)
            .Select(b => new { file = b.BucketPath, errors = b.CompileErrors.ToList() })
            .ToList();

        var output = new
        {
            tests = tests.Select(t => new
            {
                name = $"{t.Codeunit}.{t.Method}",
                status = t.Outcome.ToString().ToLowerInvariant(),
                durationMs = (long)t.Duration.TotalMilliseconds,
                message = t.Message,
                stackTrace = (t.AlCallStack ?? t.FullException)?.TrimEnd(),
                // Manifest reclassification (docs/expectations.md): "pass-oos",
                // "pass-known-gap", "pass-divergence", "skipped" or
                // "fail-manifest-drift". Omitted (null) for results the manifest
                // did not touch.
                expectation = t.Expectation switch
                {
                    Infrastructure.ExpectationResult.PassOos => "pass-oos",
                    Infrastructure.ExpectationResult.PassKnownGap => "pass-known-gap",
                    Infrastructure.ExpectationResult.PassDivergence => "pass-divergence",
                    Infrastructure.ExpectationResult.Skipped => "skipped",
                    Infrastructure.ExpectationResult.FailManifestDrift => "fail-manifest-drift",
                    _ => null,
                },
            }),
            passed = tests.Count(t => t.Outcome == TestOutcome.Pass),
            failed = tests.Count(t => t.Outcome == TestOutcome.Fail),
            errors = tests.Count(t => t.Outcome == TestOutcome.Error),
            skipped = tests.Count(t => t.Outcome == TestOutcome.Skipped),
            total = tests.Count,
            exitCode,
            compilationErrors = compileErrors.Count > 0 ? compileErrors : null,
        };

        return JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    public static void WriteClassification(IReadOnlyList<BucketResult> buckets, string path)
    {
        var failures = new List<object>();
        foreach (var b in buckets)
        {
            if (b.Stage == BucketStage.CompileFailed)
            {
                failures.Add(new
                {
                    bucket = b.BucketPath,
                    kind = "compile",
                    errors = b.CompileErrors.Take(10).ToList(),
                    classification = ClassifyCompile(b.CompileErrors),
                });
            }
            else if (b.Stage == BucketStage.ExecuteFailed)
            {
                failures.Add(new
                {
                    bucket = b.BucketPath,
                    kind = "execute",
                    error = b.ProcessError,
                    classification = "process-error",
                });
            }
            else
            {
                foreach (var t in b.Tests.Where(t => t.Outcome is TestOutcome.Fail or TestOutcome.Error))
                {
                    failures.Add(new
                    {
                        bucket = b.BucketPath,
                        kind = t.Outcome.ToString().ToLowerInvariant(),
                        codeunit = t.Codeunit,
                        method = t.Method,
                        message = t.Message,
                        // First few stack frames (after the test method) — enough to identify
                        // which BC API the failure hit, but not so many that the JSON explodes.
                        stack_top = StackTop(t.FullException, 6),
                        classification = ClassifyTest(t.Message ?? "", t.FullException ?? ""),
                    });
                }
            }
        }
        var grouped = failures
            .GroupBy(f => f.GetType().GetProperty("classification")!.GetValue(f) as string ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Select(g => new { classification = g.Key, count = g.Count(), examples = g.Take(3).ToList() })
            .ToList();
        var doc = new
        {
            generated = DateTime.UtcNow.ToString("o"),
            total_failures = failures.Count,
            classifications = grouped,
            all_failures = failures,
        };
        File.WriteAllText(path,
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IReadOnlyList<string> StackTop(string? full, int max)
    {
        if (string.IsNullOrEmpty(full)) return Array.Empty<string>();
        return full.Split('\n')
            .Where(l => l.TrimStart().StartsWith("at "))
            .Take(15)  // more frames for diagnosis
            .Select(l => l.Trim())
            .ToArray();
    }

    // Hand-tuned classification heuristics — purely descriptive, not authoritative.
    private static string ClassifyCompile(IReadOnlyList<string> errors)
    {
        var first = errors.FirstOrDefault() ?? "";
        if (first.Contains("CS0246") || first.Contains("CS0103")) return "compile/missing-type-or-name";
        if (first.Contains("CS0117")) return "compile/missing-member";
        if (first.Contains("CS1503") || first.Contains("CS1501")) return "compile/signature-mismatch";
        if (first.Contains("CS0029") || first.Contains("CS0030")) return "compile/conversion";
        if (first.Contains("CS0234")) return "compile/missing-namespace";
        return "compile/other";
    }

    private static string ClassifyTest(string message, string full)
    {
        // Out-of-scope failures (loud-failures.md / docs/scope.md) are classified
        // by API name, not by stack frame — contract surface, not an NRE.
        // The convention is parsed in exactly one place, Infrastructure.
        // OutOfScopeMessage, which the expectations manifest reads too (#1743).
        if (Infrastructure.OutOfScopeMessage.TryParse(message, out var oosSignal)
            || Infrastructure.OutOfScopeMessage.TryParse(full, out oosSignal))
        {
            return oosSignal.Reason.Length == 0
                ? "out-of-scope/unknown"
                : $"out-of-scope/{oosSignal.Api}";
        }

        // Classify by the FIRST (innermost) BC stack frame — that's where the actual NRE
        // originates. Looking anywhere in the stack mis-buckets every AL test as
        // NavMethodScope because every AL method body wraps in a NavMethodScope.
        var first = full.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("at Microsoft.Dynamics.Nav."));
        if (first != null)
        {
            // Strip "at Microsoft.Dynamics.Nav.<Group>." prefix and the parameter list.
            var cleaned = first;
            int paren = cleaned.IndexOf('(');
            if (paren > 0) cleaned = cleaned[..paren];
            cleaned = cleaned.Replace("at Microsoft.Dynamics.Nav.", "");
            return $"runtime/{cleaned}";
        }
        // Fallbacks for non-BC frames or empty stacks.
        if (message.Contains("MissingMethodException")) return "runtime/missing-method";
        if (message.Contains("MissingFieldException")) return "runtime/missing-field";
        if (message.Contains("TypeInitializationException")) return "runtime/cctor";
        if (message.Contains("InvalidCastException")) return "runtime/cast";
        if (message.Contains("PlatformNotSupportedException")) return "runtime/platform-not-supported";
        if (message.Contains("NullReferenceException")) return "runtime/null-deref";
        return "runtime/other";
    }
}
