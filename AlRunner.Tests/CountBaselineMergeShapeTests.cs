// CountBaselineMergeShapeTests — issue #2485.
//
// --count-baseline is an EXACT, two-way guard and must stay one: a drop and a growth both
// exit 4. Nothing here relaxes that. What this file pins is the SHAPE of the checked-in
// baseline, which is a separate property from what it asserts.
//
// The problem it exists for, measured: `tests/expectations/count-baseline/` moved
// al-language 2464 -> 2496 -> 2500 -> 2523 and runner-extras 234 -> 237 -> 243 -> 250 ->
// 256 -> 260 inside ONE working session. Every one of those PRs had to edit the same one or
// two integers, plus append its rationale to the same single 40,178-character `_comment`
// line, so every count-changing PR conflicted with every other count-changing PR whatever
// it had actually changed. Of the last 25 commits touching the file, 11 also moved the
// tests/al-language gitlink (corpus pin bumps, which conflict with each other regardless)
// and 14 did not (runner-extras additions, which need not conflict with anything).
//
// CI does not run at all on a conflicted PR, so this did not merely cost a rebase — it hid
// whether the PR had ever been green.
//
// The tests below reproduce that as a real three-way merge (`git merge-file`) of two
// independent branches, each carrying out the full bump procedure a PR author must carry
// out. They are the RED for #2485 and they stay as the regression guard.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CountBaselineMergeShapeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string BaselineDir => Path.Combine(RepoRoot, "tests", "expectations", "count-baseline");

    private const string BaselineFile = "test-count-baseline.json";

    /// <summary>
    /// No line in the baseline may be long enough that two independent edits to it cannot be
    /// told apart. A line every count-changing PR must append to is a guaranteed conflict
    /// between all of them, and a 40,178-character one cannot even be reviewed as a diff —
    /// git reports it as one changed line whatever changed inside it.
    /// </summary>
    [Fact]
    public void BaselineJson_HasNoLineLongEnoughToBeAConflictMagnet()
    {
        var path = Path.Combine(BaselineDir, BaselineFile);
        var offenders = File.ReadAllLines(path)
            .Select((line, i) => (Number: i + 1, line.Length))
            .Where(l => l.Length > 300)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{BaselineFile} has {offenders.Count} line(s) over 300 chars: "
            + string.Join(", ", offenders.Select(o => $"line {o.Number} ({o.Length} chars)"))
            + ". Every count-changing PR edits this file; prose that grows on one shared line "
            + "makes all of them conflict. Per-bump rationale belongs in history.md (#2485).");
    }

    /// <summary>
    /// The exact case the issue was filed for: two PRs each adding a new runner-extras app
    /// group. They have nothing to do with each other, and after #2485 each one's numbers
    /// live on its own line, so git merges them without help.
    /// </summary>
    [Fact]
    public void TwoPrsAddingDifferentAppGroups_MergeCleanly()
    {
        var (base_, ours, theirs) = ThreeCopies();
        ApplyBump(ours, "runner-extras", newAppGroup: "aaa-probe-suite-one", addedTests: 2,
            note: "New app group aaa-probe-suite-one: 2 tests (PR one).");
        ApplyBump(theirs, "runner-extras", newAppGroup: "zzz-probe-suite-two", addedTests: 3,
            note: "New app group zzz-probe-suite-two: 3 tests (PR two).");

        AssertMergesCleanly(base_, ours, theirs);
    }

    /// <summary>
    /// Two PRs moving DIFFERENT suites — a corpus pin bump and a runner-extras addition —
    /// do not disagree about a single number, and must not collide. They did, because both
    /// had to append their rationale to the same `_comment` line.
    /// </summary>
    [Fact]
    public void PrBumpingTheCorpusAndPrAddingAnAppGroup_MergeCleanly()
    {
        var (base_, ours, theirs) = ThreeCopies();
        ApplyBump(ours, "al-language", newAppGroup: null, addedTests: 6,
            note: "Corpus pin bump: 6 tests from an upstream corpus PR.");
        ApplyBump(theirs, "runner-extras", newAppGroup: "zzz-probe-suite-two", addedTests: 3,
            note: "New app group zzz-probe-suite-two: 3 tests.");

        AssertMergesCleanly(base_, ours, theirs);
    }

    /// <summary>
    /// The per-group keys are app-group directories, and the guard is only exact if the two
    /// agree. Checked here rather than at runtime because the runner has no app-group NAMES
    /// to check against — <c>BucketResult.RanGroupCount</c> is a count — so a suite added
    /// without its baseline entry would otherwise surface as a bare "expected 50, actual 51"
    /// on eight CI legs, minutes apart, instead of here in milliseconds.
    /// </summary>
    [Fact]
    public void RunnerExtrasGroupKeys_AreExactlyTheAppGroupDirectories()
    {
        var extras = Path.Combine(RepoRoot, "tests", "runner-extras");
        var onDisk = Directory.GetDirectories(extras)
            .Where(d => File.Exists(Path.Combine(d, "app.json")))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(BaselineDir, BaselineFile)));
        var declared = doc.RootElement.GetProperty("suites").GetProperty("runner-extras")
            .GetProperty("groups").EnumerateObject().Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        var missing = onDisk.Except(declared).ToList();
        var extra = declared.Except(onDisk!).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"{BaselineFile} 'runner-extras'.groups does not match tests/runner-extras/ on disk.\n"
            + (missing.Count > 0 ? "  app groups with no baseline entry: " + string.Join(", ", missing) + "\n" : "")
            + (extra.Count > 0 ? "  baseline entries with no app group: " + string.Join(", ", extra) + "\n" : "")
            + "Add or remove the group's one line — the suite total and app-group count are derived from it.");
    }

    /// <summary>
    /// An <c>absentOn</c> entry naming a BC version the matrix does not run is dead weight
    /// that reads as a live exemption. The supported list is <c>.github/bc-versions.txt</c>,
    /// which both workflows already read.
    /// </summary>
    [Fact]
    public void AbsentOnVersions_AreVersionsTheMatrixActuallyRuns()
    {
        var supported = File.ReadAllLines(Path.Combine(RepoRoot, ".github", "bc-versions.txt"))
            .Where(l => !l.TrimStart().StartsWith("#") && l.Trim().Length > 0)
            .SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet();

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(BaselineDir, BaselineFile)));
        var unknown = new List<string>();
        foreach (var suite in doc.RootElement.GetProperty("suites").EnumerateObject())
        {
            if (!suite.Value.TryGetProperty("groups", out var groups)) continue;
            foreach (var g in groups.EnumerateObject())
            {
                if (!g.Value.TryGetProperty("absentOn", out var absent)) continue;
                foreach (var v in absent.EnumerateArray())
                    if (!supported.Contains(v.GetString()!))
                        unknown.Add($"{suite.Name}.{g.Name}: {v.GetString()}");
            }
        }

        Assert.True(unknown.Count == 0,
            "absentOn names BC versions the matrix does not run: " + string.Join(", ", unknown));
    }

    // ── The bump procedure, as a PR author performs it ──────────────────────────────────
    //
    // Deliberately TEXTUAL, not a JSON round-trip: a reserialised document would rewrite
    // lines nobody touched and the merge result would say nothing about what a human editing
    // the file causes. Deliberately schema-agnostic too — it performs whichever procedure the
    // checked-in file supports, so the same test measures the old shape and the new one.
    private static void ApplyBump(string dir, string suite, string? newAppGroup, int addedTests, string note)
    {
        var path = Path.Combine(dir, BaselineFile);
        var text = File.ReadAllText(path);
        var (start, end) = SuiteBlock(text, suite);
        var block = text[start..end];

        var groupsIdx = block.IndexOf("\"groups\"", StringComparison.Ordinal);
        // Where the rationale goes, per this directory's README: a per-group line names the
        // app group and its test count, so it IS the record and nothing is written to
        // history.md. Prose is only asked for where the file cannot carry the reason itself —
        // a corpus pin bump, whose count is one integer. That asymmetry is deliberate: prose
        // for two bumps of the SAME suite lands at the same insertion point and conflicts, so
        // the procedure asks for it only where the PRs conflict on the submodule gitlink
        // anyway.
        var recordsNote = groupsIdx < 0;

        if (groupsIdx >= 0 && newAppGroup != null)
        {
            // Per-group form: one new line, at its own sorted position.
            var entry = $"        \"{newAppGroup}\": {{ \"tests\": {addedTests} }},\n";
            block = InsertGroupEntry(block, newAppGroup, entry);
        }
        else if (groupsIdx >= 0)
        {
            // Per-group form, more tests in an EXISTING group: edit that group's own line.
            block = BumpFirstGroupTestCount(block, addedTests);
        }
        else
        {
            // Flat form: every count for the suite is a shared integer — the whole point of
            // the issue. `tests` moves by the added tests, `appGroups` by one per new group,
            // and every byBcVersion override under each moves with its default.
            block = BumpEveryNumberIn(block, "tests", addedTests);
            if (newAppGroup != null) block = BumpEveryNumberIn(block, "appGroups", 1);
        }

        text = text[..start] + block + text[end..];
        if (recordsNote) text = RecordNote(dir, text, suite, note);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// Where the rationale for a bump goes. history.md if the directory has one (one section
    /// per suite, so two suites never touch the same lines); otherwise the single `_comment`
    /// string in the JSON, which is what every bump in this file's history actually did.
    /// </summary>
    private static string RecordNote(string dir, string json, string suite, string note)
    {
        var history = Path.Combine(dir, "history.md");
        if (File.Exists(history))
        {
            var lines = File.ReadAllLines(history).ToList();
            var heading = lines.FindIndex(l => l.Trim().Equals("## " + suite, StringComparison.OrdinalIgnoreCase));
            Assert.True(heading >= 0, $"history.md has no '## {suite}' section to record a bump under.");
            var next = lines.FindIndex(heading + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
            var insertAt = next < 0 ? lines.Count : next;
            while (insertAt > heading + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
            lines.Insert(insertAt, "");
            lines.Insert(insertAt + 1, "- " + note);
            File.WriteAllLines(history, lines);
            return json;
        }

        var m = Regex.Match(json, "\"_comment\"\\s*:\\s*\"");
        Assert.True(m.Success, "no history.md and no _comment: nowhere to record why the count moved.");
        var closing = json.IndexOf('"', m.Index + m.Length);
        while (closing > 0 && json[closing - 1] == '\\') closing = json.IndexOf('"', closing + 1);
        return json[..closing] + " " + note.Replace("\"", "'") + json[closing..];
    }

    private static (int Start, int End) SuiteBlock(string text, string suite)
    {
        var key = text.IndexOf($"\"{suite}\"", StringComparison.Ordinal);
        Assert.True(key >= 0, $"suite '{suite}' not found in {BaselineFile}");
        var open = text.IndexOf('{', key);
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return (open, i + 1);
        }
        throw new Xunit.Sdk.XunitException($"unbalanced braces in suite '{suite}'");
    }

    private static string InsertGroupEntry(string block, string name, string entry)
    {
        var lines = block.Split('\n').ToList();
        var first = lines.FindIndex(l => Regex.IsMatch(l, "^\\s+\"[^\"]+\"\\s*:\\s*\\{\\s*\"tests\""));
        Assert.True(first >= 0, "per-group form has no group entries to insert next to.");
        var at = first;
        while (at < lines.Count
               && Regex.IsMatch(lines[at], "^\\s+\"[^\"]+\"\\s*:\\s*\\{\\s*\"tests\"")
               && string.CompareOrdinal(Regex.Match(lines[at], "\"([^\"]+)\"").Groups[1].Value, name) < 0)
            at++;
        lines.Insert(at, entry.TrimEnd('\n'));
        return string.Join('\n', lines);
    }

    private static string BumpFirstGroupTestCount(string block, int added)
    {
        var m = Regex.Match(block, "\"tests\"\\s*:\\s*(\\d+)");
        Assert.True(m.Success, "per-group form has no group to bump.");
        return block[..m.Groups[1].Index] + (int.Parse(m.Groups[1].Value) + added)
             + block[(m.Groups[1].Index + m.Groups[1].Length)..];
    }

    private static string BumpEveryNumberIn(string block, string metric, int added)
    {
        var (s, e) = SuiteBlock(block, metric);
        // Only numbers in VALUE position: "27.0" is a key, and bumping it would invent a BC
        // version rather than a count.
        var inner = Regex.Replace(block[s..e], ":(\\s*)(\\d+)",
            m => ":" + m.Groups[1].Value + (int.Parse(m.Groups[2].Value) + added));
        return block[..s] + inner + block[e..];
    }

    // ── Merge machinery ────────────────────────────────────────────────────────────────
    private (string Base, string Ours, string Theirs) ThreeCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-count-baseline-merge-" + Guid.NewGuid().ToString("N"));
        string Copy(string name)
        {
            var dst = Path.Combine(root, name);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(BaselineDir))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            return dst;
        }
        return (Copy("base"), Copy("ours"), Copy("theirs"));
    }

    private static void AssertMergesCleanly(string base_, string ours, string theirs)
    {
        var conflicted = new List<string>();
        foreach (var file in Directory.GetFiles(base_).Select(Path.GetFileName))
        {
            var (exit, output) = Git("merge-file", "-p",
                Path.Combine(ours, file!), Path.Combine(base_, file!), Path.Combine(theirs, file!));
            // git merge-file: 0 = clean, >0 = that many conflicts, <0 = error.
            if (exit != 0)
                conflicted.Add($"{file} ({exit} conflict(s)):\n"
                    + string.Join('\n', output.Split('\n').Where(l => l.StartsWith("<<<<<<<") || l.StartsWith("=======")
                        || l.StartsWith(">>>>>>>")).Take(6)));
        }

        Assert.True(conflicted.Count == 0,
            "two independent PRs, each doing nothing but its own bump, do not merge:\n"
            + string.Join("\n", conflicted));
    }

    private static (int Exit, string Output) Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout);
    }
}
