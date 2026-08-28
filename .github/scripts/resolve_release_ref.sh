#!/usr/bin/env bash
# Decide which commit a publish.yml dispatch tests and ships, and validate that
# the release commit can actually be pushed back somewhere real. See #2060.
#
# Extracted into its own script (out of publish.yml's inline `run:` block) so
# this logic -- previously provable only by dispatching the real workflow --
# can be unit-tested directly. publish.yml's plan job calls this exact script;
# there is no separate copy for tests to drift against.
#
# Before #2060, the release job unconditionally ran `git push origin HEAD:main`
# no matter what was dispatched. A release branch is by definition not a
# fast-forward of main -- that's the entire point of cutting one -- so that
# push could never succeed, and the failure only surfaced after the full
# ~40-minute test matrix had already run. This script fixes both problems:
#
#   1. The commit is pushed back to whatever ref was actually dispatched
#      (REF_NAME) instead of a hardcoded "main". Ordinary main releases are
#      unaffected -- REF_NAME is "main" for those, same push target as before.
#   2. It's called from the "Resolve the commit to test and ship" job, which
#      runs first and takes seconds, so a dispatch that CANNOT be released
#      (see below) fails immediately instead of after the matrix.
#
# A dispatch cannot be released when the ref dispatched against isn't a
# branch. Only a branch can receive the release-commit push; a tag is
# immutable by convention, and workflow_dispatch doesn't offer dispatching
# against a bare commit SHA in the first place.
#
# Inputs (environment variables, all required):
#   VERSION              - package version being released, e.g. "1.0.23"
#   REF_TYPE             - github.ref_type ("branch" or "tag")
#   REF_NAME             - github.ref_name (the dispatched branch/tag's short name)
#   SHA                  - github.sha (the dispatched ref's head commit)
#   TAG_EXISTS_REMOTELY  - "true"/"false". Precomputed by the caller via
#                          `git ls-remote --exit-code --tags origin refs/tags/vVERSION`
#                          rather than run here, so this script has no network
#                          dependency and is trivially testable offline.
#
# Output: GITHUB_OUTPUT-format `key=value` lines on stdout (ref=..., tag-exists=...,
# and, only when a fresh release is being planned, branch=...). Diagnostic
# messages go to stderr. Exits non-zero (no output on stdout) when the dispatch
# cannot be released.

set -euo pipefail

: "${VERSION:?VERSION is required}"
: "${REF_TYPE:?REF_TYPE is required}"
: "${REF_NAME:?REF_NAME is required}"
: "${SHA:?SHA is required}"
: "${TAG_EXISTS_REMOTELY:?TAG_EXISTS_REMOTELY is required}"

TAG="v${VERSION}"

if [ "$TAG_EXISTS_REMOTELY" = "true" ]; then
  # A retry of an attempt that already tagged -- which now means the release
  # job itself failed after tagging (NuGet rejected the push, the Release API
  # call failed), NOT that tests failed, because tests run first. Test and
  # ship the tag as it stands and let the release job skip re-committing, so
  # no push target is needed here either.
  echo "Tag $TAG already exists on origin — testing and shipping that tag." >&2
  echo "ref=$TAG"
  echo "tag-exists=true"
  exit 0
fi

if [ "$REF_TYPE" != "branch" ]; then
  echo "::error::This workflow was dispatched against a $REF_TYPE ($REF_NAME), not a branch. The release commit is pushed back to the ref that was dispatched, which only makes sense for a branch. Re-dispatch against the branch you want to release (usually 'main', or a release branch for a patch release)." >&2
  exit 1
fi

# SHA is the dispatched branch's head at dispatch time. Pinning it HERE, rather
# than letting the release job read origin/<branch> once the matrix finishes,
# is what makes the tag point at the commit that was actually tested: the
# branch can move during a 40-minute run, and whatever gets pushed meanwhile
# has not been through this matrix at this version.
echo "Releasing $SHA ($REF_NAME at dispatch)." >&2
echo "ref=$SHA"
echo "tag-exists=false"
echo "branch=$REF_NAME"
