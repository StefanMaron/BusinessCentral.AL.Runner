#!/usr/bin/env python3
"""Tests for the telemetry triage aggregation and body-building logic."""

import sys
import os
import unittest

# Set required env vars before importing triage (module-level config reads them)
os.environ.setdefault("APPINSIGHTS_API_KEY", "test-key")
os.environ.setdefault("GITHUB_TOKEN", "test-token")
os.environ.setdefault("GITHUB_REPOSITORY", "test/repo")

sys.path.insert(0, os.path.dirname(__file__))
from triage import (
    _classify_compilation_gap,
    aggregate_by_root_cause,
    build_body,
)


class TestClassifyCompilationGap(unittest.TestCase):
    """Unit tests for _classify_compilation_gap()."""

    # ── CS0103: label-like names merge into one bucket ──

    def test_cs0103_lbl_suffix_groups(self):
        msg = "CS0103 on 'periodCalculationRequiredLbl': The name 'periodCalculationRequiredLbl' does not exist"
        self.assertEqual(_classify_compilation_gap(msg), "CS0103:label-vars")

    def test_cs0103_txt_suffix_groups(self):
        msg = "CS0103 on 'privacyBlockedTxt': The name 'privacyBlockedTxt' does not exist"
        self.assertEqual(_classify_compilation_gap(msg), "CS0103:label-vars")

    def test_cs0103_tok_suffix_groups(self):
        msg = "CS0103 on 'separatorTok': The name 'separatorTok' does not exist"
        self.assertEqual(_classify_compilation_gap(msg), "CS0103:label-vars")

    def test_cs0103_msg_suffix_groups(self):
        msg = "CS0103 on 'errorMsg': The name 'errorMsg' does not exist"
        self.assertEqual(_classify_compilation_gap(msg), "CS0103:label-vars")

    def test_cs0103_err_suffix_groups(self):
        msg = "CS0103 on 'fieldErr': The name 'fieldErr' does not exist"
        self.assertEqual(_classify_compilation_gap(msg), "CS0103:label-vars")

    def test_cs0103_caption_suffix_groups(self):
        msg = "CS0103 on 'myCaption': The name 'myCaption' does not exist"
        self.assertEqual(_classify_compilation_gap(msg), "CS0103:label-vars")

    # ── CS0103: non-label names stay individual ──

    def test_cs0103_non_label_stays_individual(self):
        msg = "CS0103 on 'myLocalVar': The name 'myLocalVar' does not exist"
        self.assertEqual(_classify_compilation_gap(msg), "CS0103 on 'myLocalVar'")

    # ── CS1061: generated types group by normalized target (numeric ID → <N>) ──

    def test_cs1061_report_groups_by_normalized_target(self):
        msg1 = "CS1061 on 'Report70400': 'Report70400' does not contain a definition for 'amountDue'"
        msg2 = "CS1061 on 'Report70400': 'Report70400' does not contain a definition for 'columnHead'"
        key1 = _classify_compilation_gap(msg1)
        key2 = _classify_compilation_gap(msg2)
        self.assertEqual(key1, key2)
        self.assertEqual(key1, "CS1061 on 'Report<N>'")

    def test_cs1061_different_report_ids_same_member_collapse(self):
        """CS1061 on Report70400 and Report72000 with same missing member → same group key."""
        msg1 = "CS1061 on 'Report70400': 'Report70400' does not contain a definition for 'RunModal'"
        msg2 = "CS1061 on 'Report72000': 'Report72000' does not contain a definition for 'RunModal'"
        self.assertEqual(_classify_compilation_gap(msg1), _classify_compilation_gap(msg2))
        self.assertEqual(_classify_compilation_gap(msg1), "CS1061 on 'Report<N>'")

    def test_cs1061_different_page_ids_collapse(self):
        """CS1061 on different page IDs with same missing member → one group key."""
        msg1 = "CS1061 on 'Page72336669': 'Page72336669' does not contain a definition for 'RunModal'"
        msg2 = "CS1061 on 'Page50100': 'Page50100' does not contain a definition for 'RunModal'"
        msg3 = "CS1061 on 'Page99999': 'Page99999' does not contain a definition for 'RunModal'"
        key1 = _classify_compilation_gap(msg1)
        key2 = _classify_compilation_gap(msg2)
        key3 = _classify_compilation_gap(msg3)
        self.assertEqual(key1, key2)
        self.assertEqual(key2, key3)
        self.assertEqual(key1, "CS1061 on 'Page<N>'")

    def test_cs1061_page_groups_by_normalized_target(self):
        msg = "CS1061 on 'Page50100': 'Page50100' does not contain a definition for 'SomeField'"
        self.assertEqual(_classify_compilation_gap(msg), "CS1061 on 'Page<N>'")

    def test_cs1061_report_extension_groups_by_normalized_base_target(self):
        msg = ("CS1061 on 'ReportExtension50506.DtldCustLedgEntries_a45_OnBeforeAfterGetRecord_Scope': "
               "'ReportExtension50506.DtldCustLedgEntries_a45_OnBeforeAfterGetRecord_Scope' "
               "does not contain a definition for 'Pa…")
        self.assertEqual(_classify_compilation_gap(msg), "CS1061 on 'ReportExtension<N>'")

    def test_cs1061_page_extension_groups_by_normalized_base_target(self):
        msg = "CS1061 on 'PageExtension50100': 'PageExtension50100' does not contain a definition for 'SomeField'"
        self.assertEqual(_classify_compilation_gap(msg), "CS1061 on 'PageExtension<N>'")

    def test_cs1061_table_extension_groups_by_normalized_base_target(self):
        msg = "CS1061 on 'TableExtension50200': 'TableExtension50200' does not contain a definition for 'X'"
        self.assertEqual(_classify_compilation_gap(msg), "CS1061 on 'TableExtension<N>'")

    # ── CS1061: mock types group by target + member ──

    def test_cs1061_mock_splits_by_member(self):
        msg1 = "CS1061 on 'MockRecordRef': 'MockRecordRef' does not contain a definition for 'ALFieldExists'"
        msg2 = "CS1061 on 'MockRecordRef': 'MockRecordRef' does not contain a definition for 'ALSetTable'"
        key1 = _classify_compilation_gap(msg1)
        key2 = _classify_compilation_gap(msg2)
        self.assertNotEqual(key1, key2)
        self.assertEqual(key1, "CS1061:'MockRecordRef'.ALFieldExists")
        self.assertEqual(key2, "CS1061:'MockRecordRef'.ALSetTable")

    def test_cs1061_mock_instream(self):
        msg = "CS1061 on 'MockInStream': 'MockInStream' does not contain a definition for 'ALRead'"
        self.assertEqual(_classify_compilation_gap(msg), "CS1061:'MockInStream'.ALRead")

    def test_cs1061_mock_truncated_member_falls_back_to_target(self):
        """When member name is truncated (no closing quote), fall back to target-only."""
        msg = "CS1061 on 'MockRecordRef': 'MockRecordRef' does not contain a definition for 'ALSomeVeryLongMethodNam…"
        key = _classify_compilation_gap(msg)
        # Should still extract the truncated member since _MEMBER_RE now handles …
        self.assertEqual(key, "CS1061:'MockRecordRef'.ALSomeVeryLongMethodNam")

    def test_cs1061_scope_qualified_report_strips_scope(self):
        """Report70400.SomeTrigger_Scope → normalizes to Report<N>."""
        msg = "CS1061 on 'Report70400.OnAfterGetRecord_Scope': 'Report70400.OnAfterGetRecord_Scope' does not contain a definition for 'foo'"
        self.assertEqual(_classify_compilation_gap(msg), "CS1061 on 'Report<N>'")

    # ── Other CS codes ──

    def test_other_cs_code_groups_by_code_and_target(self):
        msg = "CS0029 on 'NavText': Cannot implicitly convert type 'int' to 'NavText'"
        self.assertEqual(_classify_compilation_gap(msg), "CS0029 on 'NavText'")

    # ── Fallback for unparseable messages ──

    def test_unparseable_message_uses_prefix(self):
        msg = "Some completely unexpected error format that doesn't match"
        key = _classify_compilation_gap(msg)
        self.assertTrue(key.startswith("Some completely unexpected"))
        self.assertTrue(len(key) <= 120)


class TestAggregateByRootCause(unittest.TestCase):
    """Integration tests for aggregate_by_root_cause()."""

    def _make_gap(self, outer_message, occurrences=1):
        return {
            "type": "AlRunner.CompilationGap",
            "outerMessage": outer_message,
            "occurrences": occurrences,
            "first_seen": "2024-01-01T00:00:00Z",
            "last_seen": "2024-01-01T01:00:00Z",
            "versions": ["0.1.0"],
            "os_list": ["unix"],
            "sample_msg": outer_message,
            "sample_stack": "(stack)",
        }

    def test_label_vars_aggregate_into_one(self):
        """20 different *Lbl CS0103 errors → 1 aggregated row."""
        rows = [
            self._make_gap(f"CS0103 on '{name}Lbl': The name '{name}Lbl' does not exist")
            for name in ["aged", "current", "detail", "days", "over", "summary",
                         "noLimit", "upTo", "dueDateShort", "dueDateFull",
                         "transactionDateShort", "transactionDateFull",
                         "documentDateShort", "documentDateFull",
                         "agedCustomerBalances", "agedOverdueAmounts",
                         "showOnlyOverdueBy", "onlyForDueDate",
                         "amountsAreIn", "periodCalculationRequired"]
        ]
        result = aggregate_by_root_cause(rows)
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["_group_key"], "CS0103:label-vars")
        self.assertEqual(result[0]["distinct_errors"], 20)
        self.assertEqual(result[0]["occurrences"], 20)
        self.assertEqual(len(result[0]["_original_rows"]), 20)

    def test_report_members_aggregate_by_normalized_target(self):
        """Multiple CS1061 on Report70400 → 1 aggregated row with normalized key."""
        rows = [
            self._make_gap("CS1061 on 'Report70400': 'Report70400' does not contain a definition for 'amountDue'", 3),
            self._make_gap("CS1061 on 'Report70400': 'Report70400' does not contain a definition for 'columnHead'", 2),
            self._make_gap("CS1061 on 'Report70400': 'Report70400' does not contain a definition for 'Totals'", 1),
        ]
        result = aggregate_by_root_cause(rows)
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["_group_key"], "CS1061 on 'Report<N>'")
        self.assertEqual(result[0]["occurrences"], 6)
        self.assertEqual(result[0]["distinct_errors"], 3)

    def test_different_page_ids_same_missing_member_collapse_to_one(self):
        """CS1061 on different page IDs with same missing member → 1 aggregated row."""
        rows = [
            self._make_gap("CS1061 on 'Page72336669': 'Page72336669' does not contain a definition for 'RunModal'", 2),
            self._make_gap("CS1061 on 'Page50100': 'Page50100' does not contain a definition for 'RunModal'", 1),
            self._make_gap("CS1061 on 'Page99999': 'Page99999' does not contain a definition for 'RunModal'", 3),
            self._make_gap("CS1061 on 'Page11111': 'Page11111' does not contain a definition for 'RunModal'", 1),
        ]
        result = aggregate_by_root_cause(rows)
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["_group_key"], "CS1061 on 'Page<N>'")
        self.assertEqual(result[0]["occurrences"], 7)
        self.assertEqual(result[0]["distinct_errors"], 4)

    def test_mock_type_not_normalized(self):
        """CS1061 on MockRecordRef uses exact type name (no numeric suffix to normalize)."""
        rows = [
            self._make_gap("CS1061 on 'MockRecordRef': 'MockRecordRef' does not contain a definition for 'ALFieldExists'"),
        ]
        result = aggregate_by_root_cause(rows)
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["_group_key"], "CS1061:'MockRecordRef'.ALFieldExists")

    def test_mock_methods_stay_split(self):
        """Different missing methods on same mock type → separate rows."""
        rows = [
            self._make_gap("CS1061 on 'MockRecordRef': 'MockRecordRef' does not contain a definition for 'ALFieldExists'"),
            self._make_gap("CS1061 on 'MockRecordRef': 'MockRecordRef' does not contain a definition for 'ALSetTable'"),
        ]
        result = aggregate_by_root_cause(rows)
        self.assertEqual(len(result), 2)
        keys = {r["_group_key"] for r in result}
        self.assertIn("CS1061:'MockRecordRef'.ALFieldExists", keys)
        self.assertIn("CS1061:'MockRecordRef'.ALSetTable", keys)

    def test_non_gaps_pass_through(self):
        """Non-CompilationGap rows pass through unchanged."""
        rows = [
            {"type": "System.NullReferenceException", "outerMessage": "NRE in something",
             "occurrences": 5, "versions": [], "os_list": []},
        ]
        result = aggregate_by_root_cause(rows)
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["type"], "System.NullReferenceException")
        self.assertEqual(result[0]["_group_key"], "System.NullReferenceException")

    def test_mixed_rows(self):
        """Real-world mix: 20 labels + 3 Report members + 2 mock methods + 1 NRE → 5 rows."""
        rows = []
        for name in ["agedLbl", "currentLbl", "detailLbl", "daysLbl", "overLbl"]:
            rows.append(self._make_gap(f"CS0103 on '{name}': The name '{name}' does not exist"))
        rows.append(self._make_gap("CS0103 on 'privacyBlockedTxt': The name 'privacyBlockedTxt' does not exist"))
        rows.append(self._make_gap("CS1061 on 'Report70400': ... 'amountDue'"))
        rows.append(self._make_gap("CS1061 on 'Report70400': ... 'columnHead'"))
        rows.append(self._make_gap("CS1061 on 'MockRecordRef': 'MockRecordRef' does not contain a definition for 'ALFieldExists'"))
        rows.append(self._make_gap("CS1061 on 'MockFieldRef': 'MockFieldRef' does not contain a definition for 'ALSetTable'"))
        rows.append({"type": "System.NullReferenceException", "outerMessage": "NRE",
                      "occurrences": 1, "versions": [], "os_list": []})

        result = aggregate_by_root_cause(rows)
        keys = {r["_group_key"] for r in result}
        self.assertEqual(keys, {
            "CS0103:label-vars",
            "CS1061 on 'Report<N>'",
            "CS1061:'MockRecordRef'.ALFieldExists",
            "CS1061:'MockFieldRef'.ALSetTable",
            "System.NullReferenceException",
        })
        self.assertEqual(len(result), 5)

    def test_cs0103_non_label_stays_separate_from_labels(self):
        """CS0103 on a non-label variable doesn't merge with label bucket."""
        rows = [
            self._make_gap("CS0103 on 'currentLbl': The name 'currentLbl' does not exist"),
            self._make_gap("CS0103 on 'myLocalVar': The name 'myLocalVar' does not exist"),
        ]
        result = aggregate_by_root_cause(rows)
        self.assertEqual(len(result), 2)
        keys = {r["_group_key"] for r in result}
        self.assertIn("CS0103:label-vars", keys)
        self.assertIn("CS0103 on 'myLocalVar'", keys)


class TestBuildBody(unittest.TestCase):
    """Tests for build_body() with group-key matching."""

    def _make_aggregated_row(self, group_key, original_rows):
        return {
            "type": "AlRunner.CompilationGap",
            "_group_key": group_key,
            "_original_rows": original_rows,
        }

    def _make_raw_row(self, outer_message, occurrences=1):
        return {
            "type": "AlRunner.CompilationGap",
            "outerMessage": outer_message,
            "occurrences": occurrences,
            "first_seen": "2024-01-01T00:00:00Z",
            "last_seen": "2024-01-01T01:00:00Z",
            "versions": ["0.1.0"],
            "os_list": ["unix"],
            "sample_msg": outer_message,
            "sample_stack": "(stack)",
        }

    def test_matches_by_group_key(self):
        """build_body uses source_group_keys to match rows."""
        r1 = self._make_raw_row("CS0103 on 'agedLbl': ...")
        r2 = self._make_raw_row("CS0103 on 'currentLbl': ...")
        agg = self._make_aggregated_row("CS0103:label-vars", [r1, r2])
        other = self._make_aggregated_row("CS1061 on 'Report<N>'", [
            self._make_raw_row("CS1061 on 'Report70400': ... 'amountDue'"),
        ])

        problem = {"source_group_keys": ["CS0103:label-vars"]}
        body = build_body(problem, [agg, other], "2024-01-01T00:00:00Z")

        self.assertIn("agedLbl", body)
        self.assertIn("currentLbl", body)
        self.assertNotIn("amountDue", body)

    def test_expands_original_rows(self):
        """build_body shows each original row from an aggregated group."""
        originals = [
            self._make_raw_row(f"CS0103 on '{n}Lbl': ...")
            for n in ["aged", "current", "detail"]
        ]
        agg = self._make_aggregated_row("CS0103:label-vars", originals)

        problem = {"source_group_keys": ["CS0103:label-vars"]}
        body = build_body(problem, [agg], "2024-01-01T00:00:00Z")

        # Each original should produce its own section
        self.assertEqual(body.count("### `AlRunner.CompilationGap`"), 3)

    def test_fallback_to_source_exception_types(self):
        """Backward compat: falls back to source_exception_types if no group keys."""
        row = self._make_raw_row("some error")
        row["type"] = "System.NRE"
        row["_group_key"] = "System.NRE"
        row["_original_rows"] = [row]

        problem = {"source_exception_types": ["System.NRE"]}
        body = build_body(problem, [row], "2024-01-01T00:00:00Z")

        self.assertIn("some error", body)


if __name__ == "__main__":
    unittest.main()
