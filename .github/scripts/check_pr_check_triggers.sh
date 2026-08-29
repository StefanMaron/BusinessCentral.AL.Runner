#!/usr/bin/env bash
# Fails when a workflow's `pull_request` trigger is missing a required event
# type. Written for pr-check.yml, whose reject-ci-skip-directives and
# reject-bad-closing-references jobs both read the PR title/body -- see
# #2159.
#
# The gap this guards: `pull_request` only reruns a workflow on the event
# types listed under `on.pull_request.types`. If `edited` is not one of
# them, editing a PR's title or body after those two jobs already passed
# retriggers nothing. The squash-merge commit then carries whatever title/
# body existed at merge time, which may never have been checked at all.
# Real incidents this exact mechanism produced: #2116 (a CI-skip directive
# added after the check passed), #2128 (an unintended closing reference
# added after the check passed).
#
# Extracted into its own script (out of pr-check.yml's inline `run:` block)
# so this is unit-tested directly, the same pattern as
# check_ci_skip_directives.sh and check_closing_reference.sh.
#
# This is a line-oriented extraction, not a real YAML parser -- deliberately
# so, to avoid taking a yq/python-yaml dependency for a one-line sanity
# check on a workflow file with a flat, single-line trigger declaration. It
# reads the FIRST `types: [...]` line in the file, which is fine as long as
# the workflow keeps that trigger on one line; a workflow that reformats it
# across multiple lines needs a corresponding update here.
#
# Usage: check_pr_check_triggers.sh [path-to-workflow-yaml]
# Defaults to .github/workflows/pr-check.yml relative to this script.
# Exits 0 and prints a confirmation when all required types are present.
# Exits 1 with an ::error:: line naming what's missing otherwise.
# Exits 2 if the file or the types line can't be found at all (a distinct
# code from "check failed" -- this means the check couldn't even run).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKFLOW="${1:-$SCRIPT_DIR/../workflows/pr-check.yml}"

if [ ! -f "$WORKFLOW" ]; then
  echo "::error::workflow file not found: $WORKFLOW" >&2
  exit 2
fi

TYPES_LINE=$(grep -E '^[[:space:]]*types:[[:space:]]*\[' "$WORKFLOW" | head -n1)

if [ -z "$TYPES_LINE" ]; then
  echo "::error::could not find a 'types: [...]' line under pull_request in $WORKFLOW" >&2
  exit 2
fi

# 'edited' is required so a title/body edit after opened/synchronize/
# reopened/labeled/unlabeled gets re-validated by the two jobs that read
# PR_TITLE/PR_BODY (#2159). The rest are pre-existing and load-bearing for
# reasons documented inline in pr-check.yml itself; this check also catches
# one of them being dropped by accident while editing the trigger line for
# something unrelated.
REQUIRED_TYPES=(opened synchronize reopened labeled unlabeled edited)

MISSING=()
for t in "${REQUIRED_TYPES[@]}"; do
  if ! echo "$TYPES_LINE" | grep -qE "(\[|,)[[:space:]]*${t}[[:space:]]*(,|\])"; then
    MISSING+=("$t")
  fi
done

if [ "${#MISSING[@]}" -ne 0 ]; then
  echo "::error::pull_request trigger in $WORKFLOW is missing required type(s): ${MISSING[*]} (found line: ${TYPES_LINE# })" >&2
  exit 1
fi

echo "pull_request trigger in $WORKFLOW includes all required types: ${REQUIRED_TYPES[*]}"
