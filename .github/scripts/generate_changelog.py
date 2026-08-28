"""
Generate a CHANGELOG.md section from git commit messages and inject it into the file.

## Conventional-commit classification (#2109)

This repo writes scoped commits almost exclusively -- `fix(startup):`,
`feat(dap):`, `docs(agents):`, `chore(corpus):` -- but the classifier used to
test only unscoped prefixes (`feat:`, `fix:`, `docs:`, `chore:`). A scoped
commit matched none of those, fell to the catch-all branch, and was dumped
into `### Changed` with its raw prefix still attached: measured against the
11 commits queued for a release, 1 was classified and 10 landed in `Changed`
reading `fix(startup): ...`, prefix and all. Bare `chore:` was skipped but
`chore(corpus):` was not, so scoped chores -- meaningless to someone reading
a release changelog -- reached the published output (2.7.0 shipped two of
them).

`CONVENTIONAL_RE` now recognizes an OPTIONAL `(scope)` after any
conventional-commit type, not just the four this classifier sections
(`feat`/`fix`/`docs`) or drops (`chore`). Every other type this repo has
actually used in its history (`perf`, `test`, `refactor`, `ci`, and even
one-off outliers like `dap:` that were clearly meant to carry a real type but
never merged one -- see 2.7.0's shipped "`- dap: add a stdio transport...`")
gets the SAME treatment: the raw `type(scope):` token is never left in the
output. Recognized types (`feat`/`fix`/`docs`) go to their section with the
scope preserved as a bold prefix (`**startup:** ...`) or dropped if there was
none; `chore` (scoped or not) is skipped outright, matching #2109's
acceptance criterion directly; anything else still lands in `### Changed`,
but with the type token stripped either way -- the type-prefix leak this
issue is about isn't specific to feat/fix/docs/chore, it's specific to
"any recognized-looking commit type", so the fix generalizes to match.

A message that doesn't look like `word:` or `word(scope):` at all (rare,
free-form) is left completely untouched, same as before -- there's nothing
conventional-commit-shaped to strip.

## Repeated PR-number stripping

A squash of a PR whose title already carried a trailing `(#N)` leaves the
squash merge's OWN `(#N)` appended after it: `fix: ... (#2102) (#2104)`. The
old code stripped once; STRIP_PR_NUMBER_RE now loops until nothing more
matches.

## [Unreleased] (#2109)

`update_unreleased()` / `--unreleased` recomputes the `## [Unreleased]`
section from every commit since the last tag reachable from HEAD (an
ancestry walk via `git describe --tags --abbrev=0` -- unambiguous here since
#2060 declined releasing from any branch other than main) and rewrites just
that section in place. It's meant to be run on every push to main (see
.github/workflows/sync-changelog-unreleased.yml), so `[Unreleased]` is a live
answer to "what's about to ship", not permanently empty between releases.
Idempotent by construction: re-running it against the same tag/HEAD pair
recomputes byte-identical text, so the caller only needs to check whether the
file actually changed before committing.

Environment variables (all required unless noted):
  VERSION  - the release version, e.g. 1.0.22 (not read in --unreleased mode)
  DATE     - ISO date string, e.g. 2026-04-24 (not read in --unreleased mode)
  COMMITS  - newline-separated squash-commit subjects since the previous tag
             (not read in --unreleased mode, which computes this itself)
  CHANGELOG_PATH - path to CHANGELOG.md (default: CHANGELOG.md)
"""

import os
import re
import subprocess
import sys

# Matches any conventional-commit-shaped prefix: a lowercase-led type token,
# an optional (scope), then a colon. Deliberately not restricted to
# feat/fix/docs/chore -- see the module docstring for why: any OTHER type
# this repo has used (perf, test, refactor, ci, even a bare mis-typed one
# like "dap:") gets its prefix stripped too, just without a dedicated section.
CONVENTIONAL_RE = re.compile(r'^([A-Za-z][\w.+-]*)(\(([^)]*)\))?:\s*(.*)$')

# A commit subject can carry more than one trailing "(#N)" -- one from the PR
# title, one appended by the squash-merge itself. Applied repeatedly by
# strip_pr_numbers() below, not just once.
PR_NUMBER_RE = re.compile(r'\s+\(#\d+\)$')

UNRELEASED_SECTION_RE = re.compile(r'(## \[Unreleased\]\n)(.*?)\n+(?=## \[|\Z)', re.DOTALL)


def strip_pr_numbers(line):
    while True:
        stripped = PR_NUMBER_RE.sub('', line)
        if stripped == line:
            return line
        line = stripped


def classify_commits(commits_raw):
    """(commits_raw: str) -> (added, fixed, docs, changed), each a list of
    '- ...' bullet strings ready to drop under a '### Heading'."""
    added, fixed, docs, changed = [], [], [], []

    for line in commits_raw.splitlines():
        line = line.strip()
        if not line:
            continue
        line = strip_pr_numbers(line)

        m = CONVENTIONAL_RE.match(line)
        if not m:
            # Not conventional-commit-shaped at all -- nothing to strip.
            changed.append('- ' + line)
            continue

        ctype = m.group(1).lower()
        scope = m.group(3)
        rest = m.group(4).strip()

        if ctype == 'chore':
            continue  # a chore never belongs in a published changelog, scoped or not

        bullet = '- ' + (f'**{scope}:** ' if scope else '') + rest

        if ctype == 'feat':
            added.append(bullet)
        elif ctype == 'fix':
            fixed.append(bullet)
        elif ctype == 'docs':
            docs.append(bullet)
        else:
            # Any other conventional type (perf, test, refactor, ci, dap, ...)
            # -- no dedicated section, but the raw prefix still must not leak.
            changed.append(bullet)

    return added, fixed, docs, changed


def build_section_body(added, fixed, docs, changed):
    lines = []
    if added:
        lines += ['### Added'] + added + ['']
    if fixed:
        lines += ['### Fixed'] + fixed + ['']
    if docs:
        lines += ['### Documentation'] + docs + ['']
    if changed:
        lines += ['### Changed'] + changed + ['']
    return '\n'.join(lines).rstrip()


def git_commits_since_last_tag():
    described = subprocess.run(
        ['git', 'describe', '--tags', '--abbrev=0'],
        capture_output=True, text=True,
    )
    prev_tag = described.stdout.strip() if described.returncode == 0 else None
    if prev_tag:
        return run_git_log(f'{prev_tag}..HEAD')
    return run_git_log('HEAD')


def run_git_log(ref_range):
    return subprocess.run(
        ['git', 'log', ref_range, '--pretty=format:%s'],
        capture_output=True, text=True,
    ).stdout


def update_unreleased(changelog_path, commits_raw=None):
    """Recomputes '## [Unreleased]' from every commit since the last tag and
    rewrites it in place. Returns True iff the file's content actually
    changed (so the caller can skip a no-op commit)."""
    if commits_raw is None:
        commits_raw = git_commits_since_last_tag()

    body = build_section_body(*classify_commits(commits_raw))

    with open(changelog_path, 'r') as f:
        content = f.read()

    # UNRELEASED_SECTION_RE's trailing `\n+` consumes every blank line up to
    # (not including) the next heading or EOF, so the replacement supplies the
    # exact spacing itself rather than depending on what was already there.
    replacement_tail = ('\n' + body + '\n\n') if body else '\n'

    def repl(m):
        return m.group(1) + replacement_tail

    updated, count = UNRELEASED_SECTION_RE.subn(repl, content, count=1)
    if count == 0:
        raise SystemExit(f"{changelog_path} has no '## [Unreleased]' heading to update")

    if updated == content:
        return False

    with open(changelog_path, 'w') as f:
        f.write(updated)
    return True


def generate_release_section(version, date, commits_raw, changelog_path):
    body = build_section_body(*classify_commits(commits_raw))
    section_text = f'## [{version}] - {date}\n\n' + body if body else f'## [{version}] - {date}'

    with open(changelog_path, 'r') as f:
        content = f.read()

    updated = re.sub(
        r'(## \[Unreleased\]\n+)',
        r'\1' + section_text.rstrip() + '\n\n',
        content,
        count=1,
    )

    with open(changelog_path, 'w') as f:
        f.write(updated)

    return section_text


def main():
    changelog_path = os.environ.get('CHANGELOG_PATH', 'CHANGELOG.md')

    if len(sys.argv) > 1 and sys.argv[1] == '--unreleased':
        changed = update_unreleased(changelog_path)
        print('changed=true' if changed else 'changed=false')
        return

    version = os.environ['VERSION']
    date = os.environ['DATE']
    commits_raw = os.environ.get('COMMITS', '')

    section_text = generate_release_section(version, date, commits_raw, changelog_path)

    # Print section for capture by the shell
    print(section_text)


if __name__ == '__main__':
    main()
