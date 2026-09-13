"""Cut a test process's git off from the user's global and system config (#4001).

Test suites that build throwaway repositories call `isolate()` before their first
git command. Without it, a box with `commit.gpgsign=true` sends every scratch
commit through the user's signer, which fails or waits for a prompt; CI has no
such config, so only a local run shows it. `tools/test_scratch_repo_git_isolation.py`
reproduces that config on CI and runs every such suite under it.

Process-wide on purpose: a per-call `env=` misses every bare `subprocess.run(["git",
...])` and every tool under test that shells out to git. Suites set `user.name` /
`user.email` per scratch repository, so no identity is lost.
"""
from __future__ import annotations

import os

_INJECTED = ("GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT")


def isolate() -> None:
    os.environ["GIT_CONFIG_GLOBAL"] = os.devnull
    os.environ["GIT_CONFIG_SYSTEM"] = os.devnull
    os.environ["GIT_CONFIG_NOSYSTEM"] = "1"
    # `git -c` from a parent process travels in these; drop them with the files.
    for name in list(os.environ):
        if name in _INJECTED or name.startswith(("GIT_CONFIG_KEY_", "GIT_CONFIG_VALUE_")):
            del os.environ[name]
