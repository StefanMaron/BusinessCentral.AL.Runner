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

    # A real, unrelated commit subject -- so these tests exercise the normal
    # path deterministically rather than depending on whatever HEAD's message
    # happens to be in whatever repo the test suite is run inside (which would
    # otherwise make these tests hit real git AND be non-hermetic: they'd behave
    # differently if ever run checked out exactly at a real release commit).
    NOT_A_RELEASE_COMMIT = 'fix: unrelated commit, not a release'

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
            head_message=self.NOT_A_RELEASE_COMMIT,
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

        gc.update_unreleased(
            self.tmp_path,
            commits_raw='feat(x): brand new thing',
            head_message=self.NOT_A_RELEASE_COMMIT,
        )

        text = self.read()
        self.assertNotIn('stale entry', text)
        self.assertIn('- **x:** brand new thing', text)

    def test_no_commits_since_last_tag_clears_unreleased_to_empty(self):
        self.write(
            '# Changelog\n\n## [Unreleased]\n\n### Fixed\n- stale entry\n\n'
            '## [1.0.0] - 2026-01-01\n'
        )

        changed = gc.update_unreleased(
            self.tmp_path, commits_raw='', head_message=self.NOT_A_RELEASE_COMMIT,
        )

        self.assertTrue(changed)
        text = self.read()
        self.assertNotIn('stale entry', text)
        self.assertIn('## [Unreleased]\n\n## [1.0.0]', text)

    def test_idempotent_rerun_reports_no_change(self):
        self.write('# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-01-01\n')

        first = gc.update_unreleased(
            self.tmp_path, commits_raw='fix(a): thing one', head_message=self.NOT_A_RELEASE_COMMIT,
        )
        self.assertTrue(first)

        second = gc.update_unreleased(
            self.tmp_path, commits_raw='fix(a): thing one', head_message=self.NOT_A_RELEASE_COMMIT,
        )
        self.assertFalse(second)

    def test_missing_unreleased_heading_raises(self):
        self.write('# Changelog\n\n## [1.0.0] - 2026-01-01\n')
        with self.assertRaises(SystemExit):
            gc.update_unreleased(
                self.tmp_path, commits_raw='fix: x', head_message=self.NOT_A_RELEASE_COMMIT,
            )

    # ---- race with publish.yml's own release commit (#2109 PR discussion) --------

    def test_skips_entirely_when_head_is_a_release_commit(self):
        # A release's CHANGELOG commit lands on main and pushes BEFORE
        # publish.yml's very next command creates and pushes the release tag.
        # If sync-changelog-unreleased.yml's run on that same push fell through
        # to git_commits_since_last_tag(), `git describe` could still resolve
        # the PREVIOUS tag (this release's own tag isn't visible on origin
        # yet), wrongly re-including everything just shipped. The commit's own
        # message is checked first and short-circuits before any of that.
        self.write(
            '# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-01-01\n'
        )

        changed = gc.update_unreleased(
            self.tmp_path,
            # Deliberately non-empty -- proves the skip happens BEFORE this is
            # ever consulted, not that it coincidentally produced no diff.
            commits_raw='feat(x): this must never be written',
            head_message='chore: release v1.1.0',
        )

        self.assertFalse(changed)
        text = self.read()
        self.assertNotIn('this must never be written', text)
        self.assertEqual(text, '# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-01-01\n')

    def test_release_commit_recognized_case_insensitively_and_with_whitespace(self):
        changed = gc.update_unreleased(
            self._scratch_with_unreleased(),
            commits_raw='feat(x): must not land',
            head_message='  Chore: Release v2.0.0  ',
        )
        self.assertFalse(changed)

    def _scratch_with_unreleased(self):
        self.write('# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-01-01\n')
        return self.tmp_path

    def test_non_release_commit_that_merely_mentions_release_still_runs_normally(self):
        # Must not be a substring match -- "release" appearing anywhere in an
        # unrelated commit message must not be mistaken for the release commit
        # itself, or a real change would silently vanish.
        changed = gc.update_unreleased(
            self._scratch_with_unreleased(),
            commits_raw='feat(x): document the release process',
            head_message='docs: explain how to release a version',
        )
        self.assertTrue(changed)
        self.assertIn('document the release process', self.read())


class GenerateReleaseSectionTests(unittest.TestCase):

    def setUp(self):
        self.tmp_path = os.path.join(
            SCRIPT_DIR, '.test_changelog_scratch_2109_release.md'
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

    def test_wipes_stale_unreleased_content_left_by_the_sync_workflow(self):
        # Simulates the exact race #2109's PR review raised: sync-changelog-
        # unreleased.yml already wrote something into [Unreleased] before this
        # release ran. commits_raw covers the SAME "since last tag" range, so
        # the stale text must be replaced, not left dangling under the new
        # version heading.
        self.write(
            '# Changelog\n\n## [Unreleased]\n\n### Fixed\n'
            '- **startup:** stale entry the sync workflow already wrote\n\n'
            '## [1.0.0] - 2026-01-01\n'
        )

        section = gc.generate_release_section(
            '1.1.0', '2026-03-01', 'fix(startup): stale entry the sync workflow already wrote',
            self.tmp_path,
        )

        text = self.read()
        # The stale line appears exactly once -- inside the new release
        # section -- not a second time left over under [Unreleased].
        self.assertEqual(text.count('stale entry the sync workflow already wrote'), 1)
        self.assertIn('## [1.1.0] - 2026-03-01', text)
        self.assertIn(
            '## [1.1.0] - 2026-03-01\n\n### Fixed\n- **startup:** stale entry the sync workflow already wrote',
            text,
        )
        # [Unreleased] itself is empty again -- nothing sits between its
        # heading and the new release heading.
        self.assertIn('## [Unreleased]\n\n## [1.1.0]', text)
        self.assertEqual(section, '## [1.1.0] - 2026-03-01\n\n### Fixed\n- **startup:** stale entry the sync workflow already wrote')

    def test_old_sections_below_are_untouched(self):
        self.write(
            '# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-01-01\n\nold notes here\n'
        )
        gc.generate_release_section('1.1.0', '2026-03-01', 'feat(x): new thing', self.tmp_path)
        text = self.read()
        self.assertIn('## [1.0.0] - 2026-01-01\n\nold notes here', text)


if __name__ == '__main__':
    unittest.main(verbosity=2)
