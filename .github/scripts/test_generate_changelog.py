"""
Tests for generate_changelog.py -- #2109.

The classifier is exercised with REAL commit subjects from this repo's own
history (`git log --pretty=format:%s`), not synthetic examples, because the
synthetic/unscoped case was never broken -- scoped commits are what #2109 is
about, and this repo writes those almost exclusively.

Run directly: python3 .github/scripts/test_generate_changelog.py
"""

import importlib.util
import os
import unittest

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
SCRIPT_PATH = os.path.join(SCRIPT_DIR, 'generate_changelog.py')

spec = importlib.util.spec_from_file_location('generate_changelog', SCRIPT_PATH)
gc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gc)


class ClassifyScopedCommitsTests(unittest.TestCase):
    """Real subjects, taken verbatim from `git log --pretty=format:%s` on this
    repo, before #2109's fix -- this is the case that was broken."""

    def test_scoped_fix_is_classified_as_fixed_not_dumped_into_changed(self):
        added, fixed, docs, changed = gc.classify_commits(
            'fix(startup): report the true final package-cache search set (#2108)'
        )
        self.assertEqual(fixed, ['- **startup:** report the true final package-cache search set'])
        self.assertEqual(changed, [])

    def test_scoped_feat_is_classified_as_added_not_dumped_into_changed(self):
        added, fixed, docs, changed = gc.classify_commits(
            'feat(server): per-statement hit counts + position table (coverage:true) (#2069)'
        )
        self.assertEqual(
            added,
            ['- **server:** per-statement hit counts + position table (coverage:true)'],
        )
        self.assertEqual(changed, [])

    def test_scoped_docs_is_classified_as_documentation_not_dumped_into_changed(self):
        added, fixed, docs, changed = gc.classify_commits(
            "docs(agents): say how to wait for CI, not just how to read it (#2099)"
        )
        self.assertEqual(docs, ['- **agents:** say how to wait for CI, not just how to read it'])
        self.assertEqual(changed, [])

    def test_scoped_chore_is_skipped_exactly_as_bare_chore_is(self):
        added, fixed, docs, changed = gc.classify_commits(
            'chore(corpus): bump al-language pin to ab43ec0 for #66/#67, bump count-baseline to 2140 (#2083)'
            '\nchore(agent-docs): cleanup pass on the agent instruction surface (#2084)'
            '\nchore: release v2.7.0'
        )
        self.assertEqual(added, [])
        self.assertEqual(fixed, [])
        self.assertEqual(docs, [])
        self.assertEqual(changed, [])

    def test_measured_release_batch_from_the_issue_classifies_ten_of_eleven(self):
        # The exact 11-commit batch #2109 measured: "1 classified, 10 dumped
        # into Changed with their prefixes visible" under the old classifier.
        commits = "\n".join([
            "fix(tests): make SiblingSourceDep_CompilesWithZeroPackageCacheDirs hermetic (#2106)",
            "fix(startup): defer the remaining per-generation startup lines past re-exec (#2105)",
            "fix(provisioning): derive transitive no-fallback platform-app need via a closure walk, not a hand-maintained list (#2101)",
            "docs(agents): say how to wait for CI, not just how to read it (#2099)",
            "fix(deps): report missing/too-old third-party deps as provisioning gaps, not COMPILE-FAIL (#2100)",
            "fix(startup): defer startup trio across every re-exec generation, not just the shadow hop (#2096)",
            "docs(rules): fold pr-ci-monitoring.md into ci-verdicts.md (#2094)",
            "fix(provision): expose platform-apps/test-apps/service-tier download from the shipped binary (#2091)",
            "fix(cli): -v/-V/version alias --version, --help prints version, --guide tells agents where and how to report gaps (#2092)",
            "fix(provision): detect transitive Application Test Library need, provision the selected BC version not the cache's (#2086)",
            "fix: declare _BCVersion default once in Directory.Build.props (#2102) (#2104)",
        ])

        added, fixed, docs, changed = gc.classify_commits(commits)

        # Every one of the 11 lands in a real section; NONE fall through to
        # Changed with a raw prefix (that was the defect: 10 of 11 did).
        self.assertEqual(changed, [])
        self.assertEqual(len(fixed), 9)
        self.assertEqual(len(docs), 2)
        self.assertEqual(len(added), 0)

        # Spot-check one bullet has its scope preserved and prefix stripped.
        self.assertIn(
            '- **startup:** defer the remaining per-generation startup lines past re-exec',
            fixed,
        )
        for bullet in fixed + docs:
            self.assertNotRegex(bullet, r'^\-\s*(fix|feat|docs|chore)\(')

    def test_unscoped_forms_still_classify_exactly_as_before(self):
        # The case that was NEVER broken -- must keep working unchanged.
        added, fixed, docs, changed = gc.classify_commits(
            'feat: restore --dap breakpoint debugging on v2 (slice 1 of #1642) (#2048)'
            '\nfix: print startup reporting once per invocation, not once per re-exec generation (#2044)'
            '\ndocs: correct something'
            '\nchore: internal cleanup only'
        )
        self.assertEqual(added, ['- restore --dap breakpoint debugging on v2 (slice 1 of #1642)'])
        self.assertEqual(fixed, ['- print startup reporting once per invocation, not once per re-exec generation'])
        self.assertEqual(docs, ['- correct something'])
        self.assertEqual(changed, [])

    def test_unrecognized_conventional_type_is_stripped_not_left_raw(self):
        # "dap:" shipped raw into the published 2.7.0 changelog under the old
        # classifier (visible in CHANGELOG.md as "- dap: add a stdio
        # transport..."). Not one of feat/fix/docs/chore, but still
        # conventional-commit-shaped -- the type prefix must not leak either.
        added, fixed, docs, changed = gc.classify_commits(
            'dap: add a stdio transport so VS Code can launch the adapter directly (#2068)'
        )
        self.assertEqual(changed, ['- add a stdio transport so VS Code can launch the adapter directly'])
        for section in (added, fixed, docs):
            self.assertEqual(section, [])

    def test_scoped_unrecognized_type_preserves_scope_and_drops_type(self):
        added, fixed, docs, changed = gc.classify_commits(
            'perf(startup): shave 200ms off cold boot'
        )
        self.assertEqual(changed, ['- **startup:** shave 200ms off cold boot'])

    def test_non_conventional_free_form_message_is_left_completely_alone(self):
        added, fixed, docs, changed = gc.classify_commits('Merge pull request #123 from foo/bar')
        self.assertEqual(changed, ['- Merge pull request #123 from foo/bar'])


class RepeatedPrNumberStrippingTests(unittest.TestCase):

    def test_single_trailing_pr_number_is_stripped(self):
        self.assertEqual(
            gc.strip_pr_numbers('fix: do the thing (#123)'),
            'fix: do the thing',
        )

    def test_double_trailing_pr_number_is_stripped_repeatedly(self):
        # The exact real subject from #2109: a squash of a PR whose title
        # already carried a trailing (#N) leaves the squash-merge's own (#N)
        # appended after it.
        self.assertEqual(
            gc.strip_pr_numbers(
                'fix: declare _BCVersion default once in Directory.Build.props (#2102) (#2104)'
            ),
            'fix: declare _BCVersion default once in Directory.Build.props',
        )

    def test_no_trailing_pr_number_is_left_unchanged(self):
        self.assertEqual(
            gc.strip_pr_numbers('fix: something with no PR number'),
            'fix: something with no PR number',
        )

    def test_end_to_end_double_pr_number_via_classify_commits(self):
        added, fixed, docs, changed = gc.classify_commits(
            'fix: declare _BCVersion default once in Directory.Build.props (#2102) (#2104)'
        )
        self.assertEqual(fixed, ['- declare _BCVersion default once in Directory.Build.props'])


class UpdateUnreleasedTests(unittest.TestCase):

    def setUp(self):
        self.tmp_path = os.path.join(
            SCRIPT_DIR, '.test_changelog_scratch_2109.md'
        )

    def tearDown(self):
        if os.path.exists(self.tmp_path):
            os.remove(self.tmp_path)

    def write(self, content):
        with open(self.tmp_path, 'w') as f:
            f.write(content)

    def read(self):
        with open(self.tmp_path) as f:
            return f.read()

    def test_populates_empty_unreleased_from_supplied_commits(self):
        self.write('# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-01-01\n\nold stuff\n')

        changed = gc.update_unreleased(
            self.tmp_path,
            commits_raw='feat(dap): new debugger feature\nfix(startup): faster boot',
        )

        self.assertTrue(changed)
        text = self.read()
        self.assertIn('## [Unreleased]', text)
        self.assertIn('### Added', text)
        self.assertIn('- **dap:** new debugger feature', text)
        self.assertIn('### Fixed', text)
        self.assertIn('- **startup:** faster boot', text)
        # The old [1.0.0] section is untouched.
        self.assertIn('## [1.0.0] - 2026-01-01\n\nold stuff', text)

    def test_replaces_stale_unreleased_content_rather_than_appending(self):
        self.write(
            '# Changelog\n\n## [Unreleased]\n\n### Fixed\n- stale entry\n\n'
            '## [1.0.0] - 2026-01-01\n'
        )

        gc.update_unreleased(self.tmp_path, commits_raw='feat(x): brand new thing')

        text = self.read()
        self.assertNotIn('stale entry', text)
        self.assertIn('- **x:** brand new thing', text)

    def test_no_commits_since_last_tag_clears_unreleased_to_empty(self):
        self.write(
            '# Changelog\n\n## [Unreleased]\n\n### Fixed\n- stale entry\n\n'
            '## [1.0.0] - 2026-01-01\n'
        )

        changed = gc.update_unreleased(self.tmp_path, commits_raw='')

        self.assertTrue(changed)
        text = self.read()
        self.assertNotIn('stale entry', text)
        self.assertIn('## [Unreleased]\n\n## [1.0.0]', text)

    def test_idempotent_rerun_reports_no_change(self):
        self.write('# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-01-01\n')

        first = gc.update_unreleased(self.tmp_path, commits_raw='fix(a): thing one')
        self.assertTrue(first)

        second = gc.update_unreleased(self.tmp_path, commits_raw='fix(a): thing one')
        self.assertFalse(second)

    def test_missing_unreleased_heading_raises(self):
        self.write('# Changelog\n\n## [1.0.0] - 2026-01-01\n')
        with self.assertRaises(SystemExit):
            gc.update_unreleased(self.tmp_path, commits_raw='fix: x')


if __name__ == '__main__':
    unittest.main(verbosity=2)
