// TestDataNormalization — the opt-in `--test-data-normalize-company` rule set (issue #2730).
//
// WHAT IT IS FOR
//   `--test-data` restores the demo backup as shipped; Microsoft generates the company its
//   BaseApp tests run against with the legacy DemoTool instead. This file narrows named,
//   measured differences between the two. Which fields differ, and why the ACY one costs
//   tests: docs/limitations.md § "Which company --test-data presents", and #3429 for the
//   derivation.
//
// WHY OPT-IN, AND WHY IT MUST STAY OPT-IN
//   Every pass/fail number recorded in this repository was measured against the un-normalized
//   restore. If normalization became the default, all of them would silently stop meaning what
//   they say, and a normalized run compared against a recorded un-normalized one would read as
//   the runner having improved. So: separate flag, default off, and `--test-data` alone behaves
//   exactly as it did.
//
//   The same argument is why the flag is folded into the install-baseline cache key
//   (TestDataOptions.BuildCacheIdentity). A baseline captured WITHOUT normalization restored
//   into a run that asked FOR it would proceed against un-normalized rows with no error
//   anywhere — the silent-wrong-answer class .claude/rules/loud-failures.md exists to prevent,
//   and the identical argument #2258 made for --test-data itself.
//
// WHY IT IS LOUD (.claude/rules/loud-failures.md)
//   A normalization that happens quietly is a measurement trap. Every rule reports what it
//   changed, from which value, in how many rows — and a rule that did NOT fire reports why
//   (the table was never touched, or the value already matched). See Describe().
//
//   A rule naming a field the loaded rows do not carry THROWS. That is a bug in the rule set,
//   and the alternative is a run that reports "normalized" while nothing was normalized —
//   exactly the check-that-cannot-fail shape this repository keeps producing.
//
// WHERE IT IS APPLIED
//   On the reader's parsed JSON rows, in TestDataProvisioner.HydrateOne, BEFORE
//   RecordPatches.HydrateTestDataTable turns them into NavValues. That one point covers both
//   the rows that land in the live store and the `pristineRows` handed to AppendBaselineTable,
//   so the value cannot be normalized in the store and un-normalized in the snapshot that
//   replaces it at the next test boundary.
//
// ADDING A RULE
//   Append to Rules. A rule is (AL table id, field name, target JSON literal, why). What does
//   NOT belong here: anything that is not a field write on an existing row. #3429 names Global
//   Dimension 2 and shortcut dimensions 3-6 as the next candidates, and they are master data —
//   changing Global Dimension 2 on a company that already has posted entries dimensioned by
//   CUSTOMERGROUP is not a field write in BC, and the target value PROJECT does not exist as a
//   Dimension in the restored company at all. Do not add them as field writes.
using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>Thrown when a normalization rule cannot be applied as written. Loud on purpose:
/// the alternative is a run reporting normalization it did not perform.</summary>
public sealed class TestDataNormalizationException : Exception
{
    public TestDataNormalizationException(string message) : base(message) { }
}

/// <summary>One field write against one table's restored rows.</summary>
/// <param name="TableId">AL table id, not the backup's table name — the name varies by
/// country layer, the id does not.</param>
/// <param name="TableName">For diagnostics only.</param>
/// <param name="FieldName">AL field name, as the backup reader emits it.</param>
/// <param name="TargetJson">The value to write, as a JSON literal, so a future rule can set a
/// number or a boolean without changing this type.</param>
/// <param name="Why">Printed with every application. A rule whose reason is not worth printing
/// is not worth applying.</param>
internal sealed record CompanyNormalizationRule(
    int TableId, string TableName, string FieldName, string TargetJson, string Why);

internal static class TestDataNormalization
{
    /// <summary>Off unless --test-data-normalize-company was passed. Absent the flag, Apply()
    /// returns its input unchanged and nothing is printed.</summary>
    internal static bool Enabled { get; set; }

    /// <summary>Bump when a rule is added, removed or changed. Folded into the install-baseline
    /// cache key so a baseline captured under one rule set is never restored into a run using
    /// another — the rules change which VALUES are in the store, which is precisely what a
    /// baseline holds.
    ///
    /// 1 — #2730, the first rule: General Ledger Setup."Additional Reporting Currency" := ''.</summary>
    internal const int RuleSetVersion = 1;

    internal static readonly IReadOnlyList<CompanyNormalizationRule> Rules = new[]
    {
        new CompanyNormalizationRule(
            TableId: 98,
            TableName: "General Ledger Setup",
            FieldName: "Additional Reporting Currency",
            TargetJson: "\"\"",
            Why: "Microsoft's DemoTool-built test company has no additional reporting currency "
               + "(<AdditionalCurrency/> is empty in all 25 DemoDataConfig.xml files); the restored "
               + "backup's EUR makes BC correctly write an extra residual G/L Entry, so every "
               + "Microsoft test counting G/L Entries after a posting sees one more than it expects"),
    };

    /// <summary>What one rule did over the whole run. Accumulated rather than reported per
    /// application so the run-end summary can also name the rules that never fired.</summary>
    internal sealed record RuleOutcome(
        CompanyNormalizationRule Rule, int Applications, int RowsChanged, int RowsAlreadyMatching,
        IReadOnlyList<string> ReplacedValues);

    private static readonly object _gate = new();
    private static readonly Dictionary<CompanyNormalizationRule, RuleOutcome> _outcomes = new();

    internal static void ResetForTests()
    {
        Enabled = false;
        lock (_gate) _outcomes.Clear();
    }

    /// <summary>Parse one argument. False when it is not this flag, so the caller's flag loop
    /// is unchanged for everything else.</summary>
    internal static bool TryParseArg(string arg)
    {
        if (arg != "--test-data-normalize-company") return false;
        Enabled = true;
        return true;
    }

    /// <summary>The part of the install-baseline cache key this flag owns. Empty when off, so
    /// the key of every run that does not opt in is byte-identical to what it was before.</summary>
    internal static string CacheIdentity()
        => Enabled ? $"normalize-company/v{RuleSetVersion}/{Rules.Count}" : "";

    /// <summary>
    /// Apply every rule that targets <paramref name="tableId"/> to <paramref name="rows"/>,
    /// returning the rows to hydrate. A no-op returning the input when the flag is off or no
    /// rule targets the table, so the hot path costs one dictionary miss.
    /// </summary>
    /// <exception cref="TestDataNormalizationException">A rule names a field the rows do not
    /// carry. See the file header for why that is fatal rather than skipped.</exception>
    internal static IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Apply(
        int tableId, string tableNameForDiagnostics,
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> rows)
    {
        if (!Enabled) return rows;
        var applicable = Rules.Where(r => r.TableId == tableId).ToList();
        if (applicable.Count == 0) return rows;
        if (rows.Count == 0) return rows;

        var working = rows.Select(r => new Dictionary<string, JsonElement>(r, StringComparer.Ordinal)).ToList();

        foreach (var rule in applicable)
        {
            var carriers = working.Count(r => r.ContainsKey(rule.FieldName));
            if (carriers == 0)
                throw new TestDataNormalizationException(
                    $"--test-data-normalize-company: rule for table {rule.TableId} '{rule.TableName}' names "
                    + $"field '{rule.FieldName}', which none of the {working.Count} restored row(s) of "
                    + $"'{tableNameForDiagnostics}' carries — the rule cannot fire, so the run would report a "
                    + "normalization it did not perform. Fix the rule (the column set the reader emitted was: "
                    + string.Join(", ", working[0].Keys.OrderBy(k => k, StringComparer.Ordinal).Take(12)) + "…).");

            var target = ParseTarget(rule);
            var targetText = Render(target);
            var changed = 0;
            var alreadyMatching = 0;
            var replaced = new List<string>();

            foreach (var row in working)
            {
                if (!row.TryGetValue(rule.FieldName, out var current)) continue;
                var currentText = Render(current);
                if (string.Equals(currentText, targetText, StringComparison.Ordinal)) { alreadyMatching++; continue; }
                replaced.Add(currentText);
                row[rule.FieldName] = target;
                changed++;
            }

            Console.Error.WriteLine(
                $"[test-data] NORMALIZED table {rule.TableId} '{rule.TableName}'.'{rule.FieldName}' := {targetText} — "
                + $"{changed} of {working.Count} row(s) changed"
                + (replaced.Count > 0 ? $" (was {string.Join(", ", replaced.Distinct().Take(4))})" : "")
                + $", {alreadyMatching} already matched. Why: {rule.Why}.");

            Record(rule, changed, alreadyMatching, replaced);
        }

        return working;
    }

    private static void Record(CompanyNormalizationRule rule, int changed, int alreadyMatching, List<string> replaced)
    {
        lock (_gate)
        {
            _outcomes.TryGetValue(rule, out var prior);
            var values = prior == null
                ? replaced.Distinct().ToList()
                : prior.ReplacedValues.Concat(replaced).Distinct().ToList();
            _outcomes[rule] = new RuleOutcome(
                rule,
                (prior?.Applications ?? 0) + 1,
                (prior?.RowsChanged ?? 0) + changed,
                (prior?.RowsAlreadyMatching ?? 0) + alreadyMatching,
                values);
        }
    }

    /// <summary>The outcome of one rule so far, or null if it never fired. Exists so a test can
    /// assert on what a rule did without parsing stderr.</summary>
    internal static RuleOutcome? OutcomeOf(CompanyNormalizationRule rule)
    {
        lock (_gate) return _outcomes.TryGetValue(rule, out var o) ? o : null;
    }

    /// <summary>
    /// The run-end report. Null when the flag is off, so a default run's output is unchanged.
    /// Otherwise EVERY rule appears — including the ones that never fired, with the reason —
    /// because the whole point is that a pass/fail number from this run can never be read
    /// without knowing which company produced it.
    /// </summary>
    internal static string? Describe()
    {
        if (!Enabled) return null;
        var lines = new List<string>
        {
            $"[test-data] company normalization ON (--test-data-normalize-company, rule set v{RuleSetVersion}, "
            + $"{Rules.Count} rule(s)). These numbers are NOT comparable with a run without it:",
        };
        foreach (var rule in Rules)
        {
            var outcome = OutcomeOf(rule);
            if (outcome == null)
            {
                lines.Add(
                    $"[test-data]   table {rule.TableId} '{rule.TableName}'.'{rule.FieldName}': DID NOT APPLY — "
                    + "the run never touched that table, so its rows were never loaded and nothing was changed.");
                continue;
            }
            var was = outcome.ReplacedValues.Count > 0
                ? $" (was {string.Join(", ", outcome.ReplacedValues.Take(4))})"
                : "";
            lines.Add(
                $"[test-data]   table {rule.TableId} '{rule.TableName}'.'{rule.FieldName}' := "
                + $"{Render(ParseTarget(rule))}: {outcome.RowsChanged} row(s) changed{was}, "
                + $"{outcome.RowsAlreadyMatching} already matched, over {outcome.Applications} load(s).");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The rule's target as a detached JsonElement. Clone() is what makes it outlive
    /// the JsonDocument it was parsed from.</summary>
    internal static JsonElement ParseTarget(CompanyNormalizationRule rule)
    {
        try
        {
            using var doc = JsonDocument.Parse(rule.TargetJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new TestDataNormalizationException(
                $"--test-data-normalize-company: rule for table {rule.TableId} '{rule.TableName}'.'{rule.FieldName}' "
                + $"has an unparseable target '{rule.TargetJson}' ({ex.Message}).");
        }
    }

    /// <summary>A JSON value rendered the way the report prints it. A blank string reads as
    /// <c>''</c> rather than as nothing at all, which is the difference between "we set it to
    /// blank" and "we printed no value".</summary>
    internal static string Render(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => $"'{value.GetString()}'",
            JsonValueKind.Null => "(null)",
            _ => value.GetRawText(),
        };
}
