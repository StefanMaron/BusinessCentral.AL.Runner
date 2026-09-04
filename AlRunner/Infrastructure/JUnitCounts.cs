// JUnitCounts — read the totals back out of a worker's JUnit XML (issue #2280).
//
// How --jobs aggregates: each worker writes JUnit, the parent adds the counts up. JUnit is used
// rather than a new IPC format because the runner already emits it (--output-junit) and its
// counts are the same ones the single-process summary prints.
//
// A missing or unreadable file reads as all zeros ON PURPOSE, and is NOT how a failed shard goes
// unnoticed: Run() takes the worst child exit code independently of these counts, so a worker
// that died before writing contributes nothing here and still fails the run.

using System.Xml.Linq;

namespace AlRunner.Infrastructure;

internal readonly record struct JUnitTotals(long Tests, long Failures, long Errors, long Skipped);

internal static class JUnitCounts
{
    /// <summary>
    /// Totals from a JUnit XML file. Sums the <c>testsuite</c> elements rather than trusting a
    /// top-level <c>testsuites</c> attribute, which is optional in the format and absent from
    /// some writers — and when a root total IS present, summing the suites agrees with it.
    /// </summary>
    public static JUnitTotals Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            var doc = XDocument.Load(path);
            long t = 0, f = 0, e = 0, s = 0;
            foreach (var suite in doc.Descendants("testsuite"))
            {
                t += Attr(suite, "tests");
                f += Attr(suite, "failures");
                e += Attr(suite, "errors");
                s += Attr(suite, "skipped");
            }
            return new JUnitTotals(t, f, e, s);
        }
        catch
        {
            // Unreadable output is not a verdict — the shard's exit code is.
            return default;
        }
    }

    private static long Attr(XElement el, string name)
        => long.TryParse(el.Attribute(name)?.Value, out var v) ? v : 0;
}
