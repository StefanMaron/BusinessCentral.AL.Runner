// TestDataCompanyNormalizationTests — the proving tests for --test-data-normalize-company
// (issue #2730).
//
// WHAT IS PROVED HERE, AND WHY IT IS HERE
//   Every claim below is about the RUNNER: a flag that is off by default, a cache key that
//   changes when it is on, a rule set that rewrites the reader's rows, and a report that says
//   what it changed. None of them is a statement about what Business Central does — BC's
//   residual-entry rule given an additional reporting currency is CORRECT and is not what this
//   change touches. What changes is which COMPANY the tests run against. So
//   .claude/rules/bc-behavior-tests-go-upstream.md sends nothing upstream here, and there is no
//   corpus PR and no pin bump.
//
//   They are also not expressible as an AL bundle in tests/runner-extras/: CI runs that whole
//   directory WITHOUT --test-data and with no 900 MB backup on the machine, so an AL test
//   asserting on hydrated General Ledger Setup rows would fail there by construction.
//
// THE ONE THAT MUST NOT BE FAKEABLE
//   The dominant failure mode in this repository is a check that cannot fail. The claim being
//   made is "the field is blank AFTER a restore that presented it as EUR" — not "a setter was
//   called". So the tests below drive the SAME wire-shaped JSON row the backup reader emits for
//   General Ledger Setup through the SAME entry point the hydration path calls
//   (TestDataNormalization.Apply), then push the result through the SAME codec that turns it
//   into the NavValue that lands in the store (RecordPatches.ConvertTestDataValue), and assert
//   the resulting AL value is blank. Both halves of the chain that can go wrong are covered:
//   a rule that does not fire, and a rule that fires with the wrong value.
//
//   Verified by deliberately breaking the implementation twice — making Apply a no-op, and
//   making the rule write "USD" instead of blank. Both breaks turn these red. See the PR body.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataCompanyNormalizationTests : IDisposable
{
    public TestDataCompanyNormalizationTests() => TestDataNormalization.ResetForTests();
    public void Dispose() => TestDataNormalization.ResetForTests();

    private const int GeneralLedgerSetupTableId = 98;
    private const string AcyField = "Additional Reporting Currency";

    /// <summary>
    /// One General Ledger Setup row in the shape the backup reader emits, measured against
    /// sandbox/28.1.49838.50621's BusinessCentral-W1.bak company 'CRONUS International Ltd_'.
    /// Written as a JSON literal rather than read from the backup for the reason
    /// TestDataDateValueHydrationTests gives: CI has no backup and no reader binary, and demo
    /// values are not guaranteed identical across the eight BC versions in the matrix.
    /// EUR is the value #2730 is about; the other fields are here so the row is a row and not
    /// a one-field stub a rule could not tell apart from an empty one.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> RestoredGeneralLedgerSetup(
        string additionalReportingCurrency = "EUR")
        => ParseRows($$"""
            [ { "Primary Key": "",
                "LCY Code": "GBP",
                "Additional Reporting Currency": "{{additionalReportingCurrency}}",
                "Global Dimension 1 Code": "DEPARTMENT",
                "Global Dimension 2 Code": "CUSTOMERGROUP",
                "Enable Data Check": true } ]
            """);

    private static IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> ParseRows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => (IReadOnlyDictionary<string, JsonElement>)e.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal))
            .ToList();
    }

    private static string? Acy(IReadOnlyDictionary<string, JsonElement> row)
        => row[AcyField].GetString();

    // ───────────────────────────────────────────────────── default off ──

    /// <summary>
    /// The constraint that matters most: `--test-data` alone must keep behaving exactly as it
    /// does today, because every pass/fail number recorded in this repository — the skill's
    /// Tests-SMB figures, the corpus counts, the full-surface floor run — was measured against
    /// the un-normalized restore. If this test ever goes red, all of those silently stop
    /// meaning what they say.
    /// </summary>
    [Fact]
    public void WithoutTheFlag_TheRestoredEurIsLeftExactlyAsTheBackupPresentsIt()
    {
        var rows = RestoredGeneralLedgerSetup();

        var result = TestDataNormalization.Apply(GeneralLedgerSetupTableId, "General Ledger Setup", rows);

        Assert.Same(rows, result);
        Assert.Equal("EUR", Acy(result[0]));
        Assert.Null(TestDataNormalization.Describe());
    }

    [Fact]
    public void WithoutTheFlag_NothingIsParsedFromTheCommandLine()
    {
        Assert.False(TestDataNormalization.TryParseArg("--test-data"));
        Assert.False(TestDataNormalization.TryParseArg("--test-data-company"));
        Assert.False(TestDataNormalization.Enabled);
    }

    [Fact]
    public void TheFlagIsRecognizedAndTurnsTheRuleSetOn()
    {
        Assert.True(TestDataNormalization.TryParseArg("--test-data-normalize-company"));
        Assert.True(TestDataNormalization.Enabled);
    }

    // ────────────────────────────────────────── the rewrite itself ──

    /// <summary>
    /// The claim #2730 is about, at the level the defect lives at: a row the backup presented
    /// as EUR is handed to the hydration path as blank.
    /// </summary>
    [Fact]
    public void WithTheFlag_AdditionalReportingCurrencyIsBlankedOnARowThatArrivedAsEur()
    {
        TestDataNormalization.Enabled = true;
        var rows = RestoredGeneralLedgerSetup();
        Assert.Equal("EUR", Acy(rows[0]));   // the precondition, asserted rather than assumed

        var result = TestDataNormalization.Apply(GeneralLedgerSetupTableId, "General Ledger Setup", rows);

        Assert.Equal("", Acy(result[0]));
        // The input is left alone: HydrateOne's caller still holds it, and a rule that mutated
        // the reader's rows in place would make the "without the flag" claim above depend on
        // call order.
        Assert.Equal("EUR", Acy(rows[0]));
    }

    /// <summary>
    /// Negative direction, and the one a no-op implementation passes: no OTHER field of the
    /// same row may move. A rule set that blanked the row, or wrote the target into the wrong
    /// column, would be caught here and nowhere else.
    /// </summary>
    [Fact]
    public void WithTheFlag_NoOtherFieldOfTheSameRowIsTouched()
    {
        TestDataNormalization.Enabled = true;

        var result = TestDataNormalization.Apply(
            GeneralLedgerSetupTableId, "General Ledger Setup", RestoredGeneralLedgerSetup());

        Assert.Equal("GBP", result[0]["LCY Code"].GetString());
        Assert.Equal("DEPARTMENT", result[0]["Global Dimension 1 Code"].GetString());
        // #3429 names Global Dimension 2 as a NEXT candidate and explains why it cannot be a
        // field write on a company that already has posted entries dimensioned by CUSTOMERGROUP.
        // This asserts it was not attempted.
        Assert.Equal("CUSTOMERGROUP", result[0]["Global Dimension 2 Code"].GetString());
        Assert.True(result[0]["Enable Data Check"].GetBoolean());
    }

    /// <summary>
    /// A table no rule targets is returned untouched and unallocated. Without this, a rule set
    /// that rewrote something in every table would still pass every assertion above.
    /// </summary>
    [Fact]
    public void WithTheFlag_ATableNoRuleTargetsIsReturnedUnchanged()
    {
        TestDataNormalization.Enabled = true;
        var rows = ParseRows("""[ { "Code": "EUR", "Description": "Euro" } ]""");

        var result = TestDataNormalization.Apply(4, "Currency", rows);

        Assert.Same(rows, result);
        Assert.Equal("EUR", result[0]["Code"].GetString());
    }

    // ──────────────────────────── the value that reaches the store ──

    /// <summary>
    /// The chain closed end to end. Rewriting the reader's JSON only matters if the AL value
    /// built from it is blank, so this pushes the normalized cell through the SAME codec
    /// RecordPatches.HydrateTestDataTable uses to build the NavValue that lands in the store,
    /// and asserts the AL-observable result — not that a dictionary key changed.
    ///
    /// `Additional Reporting Currency` is Code[10], so the field metadata is NavCode.
    /// </summary>
    [Fact]
    public void WithTheFlag_TheAlValueBuiltFromTheNormalizedCellIsBlank()
    {
        TestDataNormalization.Enabled = true;

        var normalized = TestDataNormalization.Apply(
            GeneralLedgerSetupTableId, "General Ledger Setup", RestoredGeneralLedgerSetup());

        var value = BuildAlValue(normalized[0][AcyField]);
        Assert.Equal("", Assert.IsType<NavCode>(value).Value);
        // BC's own blank test, the one AL's `if X = '' then` resolves to. Asserting the string
        // alone would not distinguish a blank Code from one this codec failed to build.
        Assert.True(value.IsZeroOrEmpty, "a blanked Code field must read as AL's blank, not as a 3-character code");
    }

    /// <summary>The control for the test above: the SAME codec, the SAME field, fed the
    /// UN-normalized cell, produces EUR. Without this pair, "the value is blank" could be a
    /// property of the codec rather than of the normalization.</summary>
    [Fact]
    public void WithoutTheFlag_TheAlValueBuiltFromTheSameCellIsEur()
    {
        var untouched = TestDataNormalization.Apply(
            GeneralLedgerSetupTableId, "General Ledger Setup", RestoredGeneralLedgerSetup());

        var value = BuildAlValue(untouched[0][AcyField]);
        Assert.Equal("EUR", Assert.IsType<NavCode>(value).Value);
        Assert.False(value.IsZeroOrEmpty);
    }

    private sealed class CodeFieldMetadata : INavValueMetadata
    {
        public NavType NavType => NavType.Code;
        public NavNclType NclType => NavNclType.NavCode;
        public int NavDefinedLengthMetadata => 10;
        public NCLOptionMetadata NavOptionMetadata => null!;
    }

    private static NavValue BuildAlValue(JsonElement cell)
        => RecordPatches.ConvertTestDataValue(
            new RecordPatches.TestDataFieldFacts(
                new CodeFieldMetadata(),
                () => throw new InvalidOperationException("a non-null Code cell must not reach the empty value"),
                storedCompressed: false),
            cell, GeneralLedgerSetupTableId, "General Ledger Setup", 3, AcyField);

    // ────────────────────────────────────────────── loud reporting ──

    /// <summary>
    /// .claude/rules/loud-failures.md. The report must name the rule, the value it replaced and
    /// how many rows moved, so a pass/fail count from this run can never be read without knowing
    /// which company produced it.
    /// </summary>
    [Fact]
    public void WithTheFlag_TheReportNamesTheRuleTheOldValueAndTheRowCount()
    {
        TestDataNormalization.Enabled = true;
        TestDataNormalization.Apply(
            GeneralLedgerSetupTableId, "General Ledger Setup", RestoredGeneralLedgerSetup());

        var report = TestDataNormalization.Describe();

        Assert.NotNull(report);
        Assert.Contains("General Ledger Setup", report!, StringComparison.Ordinal);
        Assert.Contains(AcyField, report, StringComparison.Ordinal);
        Assert.Contains("'EUR'", report, StringComparison.Ordinal);
        Assert.Contains("1 row(s) changed", report, StringComparison.Ordinal);
        Assert.Contains("NOT comparable", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule fired and changed NOTHING, because the backup already matched. That is a real
    /// outcome and must not read like the rule having done work — otherwise a future backup
    /// that ships a blank ACY would look as though normalization were still buying something.
    /// </summary>
    [Fact]
    public void WithTheFlag_ARowThatAlreadyMatchesIsReportedAsChangingNothing()
    {
        TestDataNormalization.Enabled = true;
        TestDataNormalization.Apply(GeneralLedgerSetupTableId, "General Ledger Setup",
            RestoredGeneralLedgerSetup(additionalReportingCurrency: ""));

        var report = TestDataNormalization.Describe()!;

        Assert.Contains("0 row(s) changed", report, StringComparison.Ordinal);
        Assert.Contains("1 already matched", report, StringComparison.Ordinal);
        Assert.DoesNotContain("'EUR'", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flag was on and the rule never fired, because the run never touched table 98. Saying
    /// so is the whole point: a run that reports "normalization ON" while nothing was normalized
    /// is the measurement trap this rule set exists to avoid.
    /// </summary>
    [Fact]
    public void WithTheFlag_ARuleThatNeverFiredIsReportedAsNotApplied()
    {
        TestDataNormalization.Enabled = true;

        var report = TestDataNormalization.Describe()!;

        Assert.Contains("DID NOT APPLY", report, StringComparison.Ordinal);
        Assert.Contains("never touched that table", report, StringComparison.Ordinal);
        Assert.DoesNotContain("row(s) changed", report, StringComparison.Ordinal);
    }

    // ───────────────────────────────────────────── loud refusal ──

    /// <summary>
    /// A rule naming a field the loaded rows do not carry throws. The alternative — skipping it
    /// — is a run that reports a normalization it did not perform, which is the exact
    /// check-that-cannot-fail shape this file's header is about.
    /// </summary>
    [Fact]
    public void ARuleWhoseFieldIsAbsentFromEveryRow_Throws_RatherThanReportingSuccess()
    {
        TestDataNormalization.Enabled = true;
        var rows = ParseRows("""[ { "Primary Key": "", "LCY Code": "GBP" } ]""");

        var ex = Assert.Throws<TestDataNormalizationException>(
            () => TestDataNormalization.Apply(GeneralLedgerSetupTableId, "General Ledger Setup", rows));

        Assert.Contains(AcyField, ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot fire", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An EMPTY table is not a missing field: there is nothing to normalize and nothing
    /// is wrong. It must not throw, and it must not claim to have changed anything.</summary>
    [Fact]
    public void ATableWithNoRows_IsNotMistakenForAMisnamedField()
    {
        TestDataNormalization.Enabled = true;
        var rows = ParseRows("[]");

        var result = TestDataNormalization.Apply(GeneralLedgerSetupTableId, "General Ledger Setup", rows);

        Assert.Empty(result);
        Assert.Null(TestDataNormalization.OutcomeOf(TestDataNormalization.Rules[0]));
    }

    // ─────────────────────────────────────────────── cache key ──

    /// <summary>
    /// The silent-wrong-answer guard, and the same argument #2258 made for --test-data itself: a
    /// baseline captured WITHOUT normalization must not be restored into a run that asked FOR
    /// it. The two tiers of the install-baseline cache are keyed by this string and nothing
    /// else, so this is the level the defect would live at.
    /// </summary>
    [Fact]
    public void TheNormalizationFlagChangesTheInstallBaselineCacheKey()
    {
        const string bak = "/artifacts/28.1.49838.50621/w1/BusinessCentral-W1.bak";
        const string company = "CRONUS International Ltd_";
        const string reader = "readerhash0000ab";

        Assert.Equal("", TestDataNormalization.CacheIdentity());
        var withoutNormalization = TestDataOptions.BuildCacheIdentity(bak, company, reader, "");

        TestDataNormalization.Enabled = true;
        var identity = TestDataNormalization.CacheIdentity();
        Assert.NotEqual("", identity);

        var withNormalization = TestDataOptions.BuildCacheIdentity(bak, company, reader, identity);
        Assert.NotEqual(withoutNormalization, withNormalization);
    }

    /// <summary>The rule-set version is part of the key, so adding or changing a rule
    /// invalidates baselines captured under the old set. A cache cannot detect for itself that
    /// the VALUES it holds were produced by different rules.</summary>
    [Fact]
    public void TheCacheIdentityCarriesTheRuleSetVersionAndTheRuleCount()
    {
        TestDataNormalization.Enabled = true;
        var identity = TestDataNormalization.CacheIdentity();

        Assert.Contains($"v{TestDataNormalization.RuleSetVersion}", identity, StringComparison.Ordinal);
        Assert.Contains($"/{TestDataNormalization.Rules.Count}", identity, StringComparison.Ordinal);
    }

    // ───────────────────────────────────── the rule set itself ──

    /// <summary>#3429 names Global Dimension 2 and shortcut dimensions 3-6 as the next
    /// candidates and explains why they are master data rather than field writes — the target
    /// dimension PROJECT does not exist in the restored company at all. A rule for one of them
    /// would produce a company referencing a dimension that is not there, which is worse than
    /// the difference it closes.</summary>
    [Fact]
    public void TheRuleSetDoesNotAttemptTheMasterDataDifferences()
    {
        var fields = TestDataNormalization.Rules.Select(r => r.FieldName).ToList();

        Assert.DoesNotContain("Global Dimension 2 Code", fields);
        Assert.DoesNotContain("Shortcut Dimension 3 Code", fields);
        Assert.DoesNotContain("Shortcut Dimension 4 Code", fields);
        Assert.DoesNotContain("Shortcut Dimension 5 Code", fields);
        Assert.DoesNotContain("Shortcut Dimension 6 Code", fields);
    }

    /// <summary>Every rule has to be printable and explicable: the report prints the reason with
    /// every application, and a rule whose reason is not worth printing is not worth applying.</summary>
    [Fact]
    public void EveryRuleCarriesATargetThatParsesAndAReasonWorthPrinting()
    {
        Assert.NotEmpty(TestDataNormalization.Rules);
        foreach (var rule in TestDataNormalization.Rules)
        {
            Assert.True(rule.TableId > 0);
            Assert.False(string.IsNullOrWhiteSpace(rule.FieldName));
            Assert.True(rule.Why.Length > 40, $"rule for '{rule.FieldName}' needs a reason a reader can act on");
            TestDataNormalization.ParseTarget(rule);   // throws if the literal is not JSON
        }
    }

    // ──────────────────────── the No. Series setup fields (#3497) ──

    /// <summary>
    /// The four setup fields #3497 measured as blank in the restored company, each with the
    /// series code Microsoft's DemoTool leaves there. The table ids and field names were read
    /// from the SHIPPED Base Application AL source (Microsoft_Base Application_28.1.49838.53910),
    /// not from memory; the target values from BCApps src/Layers/W1/DemoTool. See the PR body.
    /// </summary>
    public static TheoryData<int, string, string, string> ExpectedNoSeriesRules() => new()
    {
        { 311,  "Sales & Receivables Setup",     "Posted Prepmt. Inv. Nos.",  "S-INV+"   },
        { 312,  "Purchases & Payables Setup",    "Posted Prepmt. Inv. Nos.",  "P-INV+"   },
        { 313,  "Inventory Setup",               "Internal Movement Nos.",    "INT-MOVE" },
        { 5911, "Service Mgt. Setup",            "Service Quote Nos.",        "SM-QUOTE" },
        // Found by scanning the run for EVERY "must have a value in <setup table>" failure
        // rather than only the shapes #3497 listed: same table, same mechanism, same evidence.
        { 311,  "Sales & Receivables Setup",     "Direct Debit Mandate Nos.", "DDM"        },
        { 5911, "Service Mgt. Setup",            "Contract Credit Memo Nos.", "SM-CR-CON"  },
    };

    /// <summary>
    /// Each rule exists, targets the right table, and carries the EXACT series code Microsoft's
    /// DemoTool company holds. The value is the load-bearing half: a rule that fired with a
    /// plausible-but-wrong code would leave BC refusing to draw a number just as it does now,
    /// and would look like a fix in the report while changing nothing observable.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExpectedNoSeriesRules))]
    public void TheRuleSetCarriesTheDemoToolSeriesCodeForEachBlankSetupField(
        int tableId, string tableName, string fieldName, string expectedSeries)
    {
        var rule = TestDataNormalization.Rules.SingleOrDefault(
            r => r.TableId == tableId && r.FieldName == fieldName);

        Assert.True(rule is not null,
            $"no rule for table {tableId} '{tableName}'.'{fieldName}' — #3497 measured it blank in the restore");
        Assert.Equal(tableName, rule!.TableName);
        Assert.Equal($"\"{expectedSeries}\"", rule.TargetJson);
    }

    /// <summary>
    /// The rewrite at the level the defect lives at, for the field that carries 316 of the 356
    /// "empty No. Series Code" failures: a Sales &amp; Receivables Setup row the backup presents
    /// with a BLANK "Posted Prepmt. Inv. Nos." reaches hydration carrying S-INV+.
    ///
    /// Why blank is the failure: Sales-Post Prepayments (codeunit 442, line 546 of the shipped
    /// SalesPostPrepayments.Codeunit.al) assigns this field into Sales Header."Prepayment No.
    /// Series", and Library - Sales.PostSalesPrepaymentInvoice then calls NoSeries.PeekNextNo on
    /// it with NO blank guard — which is the exact stack the run log shows.
    /// </summary>
    [Fact]
    public void WithTheFlag_ABlankSalesPostedPrepaymentSeriesIsFilledWithTheDemoToolCode()
    {
        TestDataNormalization.Enabled = true;
        var rows = ParseRows("""
            [ { "Primary Key": "",
                "Posted Prepmt. Inv. Nos.": "",
                "Posted Invoice Nos.": "S-INV+",
                "Direct Debit Mandate Nos.": "DDM" } ]
            """);
        Assert.Equal("", rows[0]["Posted Prepmt. Inv. Nos."].GetString());   // precondition asserted

        var result = TestDataNormalization.Apply(311, "Sales & Receivables Setup", rows);

        Assert.Equal("S-INV+", result[0]["Posted Prepmt. Inv. Nos."].GetString());
        // No other field of the same row moves. A rule set that wrote into the wrong column, or
        // rewrote a field that already carries the DemoTool value, is caught only here.
        Assert.Equal("S-INV+", result[0]["Posted Invoice Nos."].GetString());
        Assert.Equal("DDM", result[0]["Direct Debit Mandate Nos."].GetString());
        Assert.Equal("", rows[0]["Posted Prepmt. Inv. Nos."].GetString());   // input untouched
    }

    /// <summary>
    /// The AL-observable end of the chain for a filled field, through the SAME codec
    /// RecordPatches.HydrateTestDataTable uses. "Posted Prepmt. Inv. Nos." is Code[20], and the
    /// claim is that BC reads a drawable series code — not that a dictionary key changed.
    /// </summary>
    [Fact]
    public void WithTheFlag_TheAlValueBuiltFromAFilledSeriesCellIsTheSeriesCode()
    {
        TestDataNormalization.Enabled = true;
        var normalized = TestDataNormalization.Apply(
            311, "Sales & Receivables Setup",
            // Both of table 311's rule fields must be present: a rule naming a field the rows do
            // not carry THROWS by design, which is the guard ARuleWhoseFieldIsAbsent… pins.
            ParseRows("""
                [ { "Primary Key": "",
                    "Posted Prepmt. Inv. Nos.": "",
                    "Direct Debit Mandate Nos.": "" } ]
                """));

        var value = BuildAlValue(normalized[0]["Posted Prepmt. Inv. Nos."]);

        Assert.Equal("S-INV+", Assert.IsType<NavCode>(value).Value);
        // The blank test BC's own `if X = '' then` guard resolves to. This is the difference
        // between "a series was written" and "the field is still AL-blank".
        Assert.False(value.IsZeroOrEmpty, "a filled No. Series field must not read as AL's blank");
    }

    /// <summary>
    /// A restore that ALREADY carries the DemoTool code is reported as changing nothing. Without
    /// this, a future backup shipping these fields populated would look as though normalization
    /// were still buying something.
    /// </summary>
    [Fact]
    public void WithTheFlag_ASetupRowThatAlreadyCarriesTheSeriesIsReportedAsChangingNothing()
    {
        TestDataNormalization.Enabled = true;
        TestDataNormalization.Apply(313, "Inventory Setup",
            ParseRows("""[ { "Primary Key": "", "Internal Movement Nos.": "INT-MOVE" } ]"""));

        var report = TestDataNormalization.Describe()!;

        Assert.Contains("'INT-MOVE'", report, StringComparison.Ordinal);
        Assert.Contains("0 row(s) changed", report, StringComparison.Ordinal);
        Assert.Contains("1 already matched", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The residue, asserted rather than left to a reader of the PR body. #3497 grouped two
    /// different situations under one heading; only the blank-field half is a field write.
    ///
    /// "A-ORD" carries 174 failures and is NOT a blank field: the run log shows the series
    /// walking A00995 -> A01000 and exhausting ("The No. Series A-ORD is soon running out. The
    /// current number is A01000 and the last allowed number of the sequence is A01000"), against
    /// a DemoTool definition of exactly 1000 numbers. That is a No. Series Line ROW — a Last No.
    /// Used that the bucket itself consumed — not a field on a setup table, so no rule here can
    /// express it and none pretends to.
    /// </summary>
    [Fact]
    public void TheRuleSetDoesNotAttemptTheExhaustedSeriesOrAnyNoSeriesLineRow()
    {
        var tables = TestDataNormalization.Rules.Select(r => r.TableId).ToList();

        // 308/309 are "No. Series" / "No. Series Line". A rule targeting either would be trying
        // to express a row difference as a field write.
        Assert.DoesNotContain(308, tables);
        Assert.DoesNotContain(309, tables);
        // 905 is Assembly Setup, which holds "Assembly Order Nos." = A-ORD. The field is
        // correct in the restore; the series behind it is what runs out.
        Assert.DoesNotContain(905, tables);
        Assert.DoesNotContain("Last No. Used", TestDataNormalization.Rules.Select(r => r.FieldName));
    }
}
