// TestExclusionFilterTests — --exclude-test, the mechanism that lets a run resume past a hang
// (issue #2280 / the watchdog containment work).
//
// Why this exists: when a test's watchdog fires, TestExecutor abandons the rest of the codeunit
// AND every later codeunit in that bundle. That is the correct call in-process — the hung thread
// is never killed and keeps mutating shared BC state, so continuing would produce results that
// lie. The only safe way to reach the abandoned tests is a FRESH process that skips the hung
// one, and nothing could express "skip this one" until now: --test is inclusive only.
//
// Measured cost of having no such flag, on Microsoft's BaseApp buckets with --test-data:
// Tests-ERM ran 2 of 9,500 tests because one abort in ERM Close Income Statement took the whole
// bucket. Eleven aborts across the run cost more than every other failure cause combined.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestExclusionFilterTests
{
    /// <summary>The exact qualified name a SUITE ABORTED line names is what gets skipped.</summary>
    [Fact]
    public void Excludes_AnExactQualifiedName()
    {
        var f = new TestExclusionFilter(new[] { "Codeunit134228.CloseIncomeStatementTwice" });

        Assert.True(f.IsExcluded("Codeunit134228", "CloseIncomeStatementTwice"));
        Assert.False(f.IsExcluded("Codeunit134228", "SomeOtherTest"));
    }

    /// <summary>A whole codeunit can be excluded — the practical resume step when one codeunit
    /// hangs repeatedly and retrying it method-by-method would cost a process per method.</summary>
    [Fact]
    public void Excludes_AWholeCodeunit()
    {
        var f = new TestExclusionFilter(new[] { "Codeunit134228" });

        Assert.True(f.IsExcluded("Codeunit134228", "CloseIncomeStatementTwice"));
        Assert.True(f.IsExcluded("Codeunit134228", "AnythingElse"));
        Assert.False(f.IsExcluded("Codeunit134229", "CloseIncomeStatementTwice"));
    }

    /// <summary>Case-insensitive, matching --test's own behaviour: an abort line's casing must
    /// not decide whether the resume actually skips anything.</summary>
    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        var f = new TestExclusionFilter(new[] { "codeunit134228.closeincomestatementtwice" });

        Assert.True(f.IsExcluded("Codeunit134228", "CloseIncomeStatementTwice"));
    }

    /// <summary>Several exclusions accumulate — a resume loop adds one per hang it hits.</summary>
    [Fact]
    public void Accumulates_SeveralPatterns()
    {
        var f = new TestExclusionFilter(new[] { "Codeunit134228.A", "Codeunit137309.B" });

        Assert.True(f.IsExcluded("Codeunit134228", "A"));
        Assert.True(f.IsExcluded("Codeunit137309", "B"));
        Assert.False(f.IsExcluded("Codeunit134228", "B"));
    }

    /// <summary>Negative: an empty filter excludes NOTHING. A resume that accidentally excluded
    /// everything would report a green run over zero tests, which is worse than the hang.</summary>
    [Fact]
    public void Empty_ExcludesNothing()
    {
        var f = new TestExclusionFilter(Array.Empty<string>());

        Assert.False(f.IsExcluded("Codeunit134228", "CloseIncomeStatementTwice"));
        Assert.False(f.IsEmpty == false);
    }

    /// <summary>Negative: a pattern must not match a codeunit it merely prefixes. Codeunit1342
    /// excluding Codeunit134228 would silently drop thousands of unrelated tests.</summary>
    [Fact]
    public void DoesNotMatch_AMerePrefixOfAnotherCodeunit()
    {
        var f = new TestExclusionFilter(new[] { "Codeunit1342" });

        Assert.False(f.IsExcluded("Codeunit134228", "Anything"));
        Assert.True(f.IsExcluded("Codeunit1342", "Anything"));
    }
}
