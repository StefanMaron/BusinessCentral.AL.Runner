"""
Generate a CHANGELOG.md section from git commit messages and inject it into the file.

Environment variables (all required):
  VERSION  - the release version, e.g. 1.0.22
  DATE     - ISO date string, e.g. 2026-04-24
  COMMITS  - newline-separated squash-commit subjects since the previous tag
  CHANGELOG_PATH - path to CHANGELOG.md (default: CHANGELOG.md)
"""

import os
import re

version     = os.environ["VERSION"]
date        = os.environ["DATE"]
commits_raw = os.environ.get("COMMITS", "")
changelog   = os.environ.get("CHANGELOG_PATH", "CHANGELOG.md")

added, fixed, docs, changed = [], [], [], []
for line in commits_raw.splitlines():
    line = line.strip()
    if not line:
        continue
    line = re.sub(r'\s+\(#\d+\)$', '', line)   # strip PR number suffix
    lower = line.lower()
    if re.match(r'chore:\s*(release|update changelog)', lower):
        continue                                 # skip release-housekeeping
    if lower.startswith('feat:'):
        added.append('- ' + line[5:].strip())
    elif lower.startswith('fix:'):
        fixed.append('- ' + line[4:].strip())
    elif lower.startswith('docs:'):
        docs.append('- ' + line[5:].strip())
    elif lower.startswith('chore:'):
        pass                                     # skip internal chores
    else:
        changed.append('- ' + line)

section = [f'## [{version}] - {date}', '']
if added:
    section += ['### Added'] + added + ['']
if fixed:
    section += ['### Fixed'] + fixed + ['']
if docs:
    section += ['### Documentation'] + docs + ['']
if changed:
    section += ['### Changed'] + changed + ['']

section_text = '\n'.join(section)

# Print section for capture by the shell
print(section_text)

# Inject into CHANGELOG.md after the [Unreleased] heading
with open(changelog, 'r') as f:
    content = f.read()

updated = re.sub(
    r'(## \[Unreleased\]\n+)',
    r'\1' + section_text.rstrip() + '\n\n',
    content,
    count=1
)

with open(changelog, 'w') as f:
    f.write(updated)
