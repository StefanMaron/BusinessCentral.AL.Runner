// TestDataCompanyHelpTextTests — the proving tests for issue #2290 (the --test-data-company
// help text promised a default the runner refuses to apply) and for the help/guide entries
// #2730 adds alongside it.
//
// WHY THESE ARE TEXT ASSERTIONS AND NOT A SUBPROCESS SPAWN
//   CliDocumentationTests already proves every recognised flag APPEARS in --help, by scraping
//   Program.cs — a NAME-level check. #2290 is the class that check cannot see: the flag was
//   present and its description was wrong. So this file asserts on the description, and it
//   calls ProgramSupport.PrintHelp/PrintGuide directly rather than spawning al-runner, because the claim is about the text
//   this repository ships and ProgramSupport (AlRunner/ProgramSupport/CliText.cs) is where that text lives. A subprocess would prove the
//   same sentence at ~6s per test.
//
// WHAT #2290 ACTUALLY WAS
//   --help said "Default: the first company the backup reports". The runner refuses to choose —
//   picking silently would mean every hydrated row came from a company nobody selected — and
//   the shipped W1 backup holds TWO companies, so anyone following the help text on the default
//   artifact got an EXEC-FAIL. The runtime behaviour is the intended one; the help was
//   documenting an earlier design.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataCompanyHelpTextTests
{
    private static string Help()
    {
        var w = new StringWriter();
        ProgramSupport.PrintHelp(w);
        return w.ToString();
    }

    private static string Guide()
    {
        var w = new StringWriter();
        ProgramSupport.PrintGuide(w);
        return w.ToString();
    }

    /// <summary>
    /// The exact sentence #2290 is about. It has to be gone, not merely reworded around: a
    /// reader who takes it at face value on the shipped W1 backup gets an EXEC-FAIL.
    /// </summary>
    [Fact]
    public void Help_DoesNotPromiseADefaultCompanyTheRunnerRefusesToPick()
    {
        var help = Help();

        Assert.DoesNotContain("Default: the first", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the first company the backup reports", help, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The positive half. Deleting the wrong sentence is not enough — the help now has to say
    /// what the runner actually does, or a reader is worse off than before.
    /// </summary>
    [Fact]
    public void Help_SaysTheCompanyIsRequiredWhenTheBackupHoldsMoreThanOne()
    {
        var help = Help();

        Assert.Contains("--test-data-company NAME", help, StringComparison.Ordinal);
        Assert.Contains("REQUIRED when the", help, StringComparison.Ordinal);
        Assert.Contains("more than one company", help, StringComparison.Ordinal);
        Assert.Contains("refuses to", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// The help must not have swung to the opposite error either: a single-company backup IS
    /// hydrated without the flag, so "always pass it" would be wrong in the other direction.
    /// </summary>
    [Fact]
    public void Help_StillSaysASingleCompanyBackupNeedsNoFlag()
    {
        Assert.Contains("exactly one company is hydrated without this flag", Help(), StringComparison.Ordinal);
    }

    // ────────────────────────────────────── #2730's own flag ──

    /// <summary>
    /// --test-data-normalize-company changes which VALUES a run executes against, so its help
    /// entry has to carry the two facts a reader cannot recover on their own: that it is off by
    /// default, and that its numbers are not comparable with a run without it.
    /// </summary>
    [Fact]
    public void Help_DocumentsTheNormalizationFlagAsOffByDefaultAndNotComparable()
    {
        var help = Help();

        Assert.Contains("--test-data-normalize-company", help, StringComparison.Ordinal);
        Assert.Contains("OFF BY DEFAULT", help, StringComparison.Ordinal);
        Assert.Contains("NOT comparable", help, StringComparison.Ordinal);
        Assert.Contains("Additional Reporting", help, StringComparison.Ordinal);
    }

    /// <summary>--guide is where an agent starts (CLAUDE.md / the al-runner-workflow skill), so
    /// the same two facts have to survive there as well.</summary>
    [Fact]
    public void Guide_DocumentsTheNormalizationFlagAndItsIncomparability()
    {
        var guide = Guide();

        Assert.Contains("--test-data-normalize-company", guide, StringComparison.Ordinal);
        Assert.Contains("OFF by default", guide, StringComparison.Ordinal);
        Assert.Contains("NOT comparable", guide, StringComparison.Ordinal);
    }
}
