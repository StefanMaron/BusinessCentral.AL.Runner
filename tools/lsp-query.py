#!/usr/bin/env python3
"""Ask the C# language server a question from the command line.

WHY THIS EXISTS
---------------
Claude Code's built-in `LSP` tool is not available inside subagents on this
Claude Code build (v2.1.252): a subagent calling it gets

    No such tool available: LSP. LSP is disabled for this session, in
    subagents as well as here.

Adding `LSP` to the agent's `tools:` frontmatter does not help, and neither
does `ENABLE_LSP_TOOL=1`. It DID work in subagents on v2.1.152 (see
anthropics/claude-code#62904), so this is a change in the harness, not a
property of language servers.

Subagents do have Bash. `csharp-ls` is a plain stdio process. So this script
gives them the same answers the LSP tool would, through a channel they have.

Cost: measured 0.5s to initialize and 4.9s to a first real answer on this
repo, cold, one process per query. That is cheap enough that no daemon is
needed -- and far cheaper than the grep-then-read-five-windows loop it
replaces.

FAILURE IS LOUD, ON PURPOSE
---------------------------
`.claude/rules/loud-failures.md` applies to tooling too. "No results" and
"the server never started" must never look the same, because an agent that
reads a failed lookup as "nothing calls this" draws exactly the wrong
conclusion and acts on it. Exit codes:

    0  the question was answered (results printed)
    1  the server answered and found nothing (a real negative)
    2  the server could not be started or did not answer (NOT a negative)

Usage:
    tools/lsp-query.py callers <SymbolName>        # what calls this (start here)
    tools/lsp-query.py symbol  <SymbolName>        # where is it defined
    tools/lsp-query.py refs    <file> <line> <col> # references at a position
    tools/lsp-query.py def     <file> <line> <col> # definition of what is here
"""
from __future__ import annotations
import json, os, subprocess, sys, time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOLUTION = os.path.join(REPO, "AlRunner.slnx")
READY_TIMEOUT = 120
CALL_TIMEOUT = 60


class Server:
    def __init__(self) -> None:
        try:
            self.p = subprocess.Popen(
                ["csharp-ls", "--solution", SOLUTION],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, cwd=REPO)
        except FileNotFoundError:
            die("csharp-ls is not installed or not on PATH. "
                "Install it with `mise use -g dotnet:csharp-ls` "
                "(see the README's tooling section). This is a SETUP failure, "
                "not an empty result -- do not read it as 'nothing found'.")

    def send(self, obj) -> None:
        b = json.dumps(obj).encode()
        self.p.stdin.write(b"Content-Length: %d\r\n\r\n" % len(b) + b)
        self.p.stdin.flush()

    def read(self):
        header = b""
        while b"\r\n\r\n" not in header:
            c = self.p.stdout.read(1)
            if not c:
                return None
            header += c
        length = int([l for l in header.decode().split("\r\n")
                      if l.lower().startswith("content-length")][0].split(":")[1])
        return json.loads(self.p.stdout.read(length))

    def call(self, method, params, req_id, timeout=CALL_TIMEOUT):
        self.send({"jsonrpc": "2.0", "id": req_id, "method": method, "params": params})
        deadline = time.time() + timeout
        while time.time() < deadline:
            m = self.read()
            if m is None:
                die(f"the language server exited while answering {method}.")
            if m.get("id") == req_id:
                if "error" in m:
                    die(f"{method} failed: {m['error']}")
                return m.get("result")
        die(f"{method} timed out after {timeout}s.")

    def start(self):
        self.call("initialize", {
            "processId": os.getpid(),
            "rootUri": uri(REPO),
            "capabilities": {},
            "workspaceFolders": [{"uri": uri(REPO), "name": "repo"}],
        }, 1)
        self.send({"jsonrpc": "2.0", "method": "initialized", "params": {}})
        return self

    def stop(self):
        try:
            self.p.terminate()
        except Exception:
            pass


def uri(path: str) -> str:
    return "file://" + os.path.abspath(path)


def rel(u: str) -> str:
    return (u or "").replace(uri(REPO) + "/", "").replace("file://", "")


def die(msg: str):
    print(f"lsp-query: {msg}", file=sys.stderr)
    sys.exit(2)


# A substring that matches something in any non-trivial C# solution. Used only
# to tell "the solution has finished loading" apart from "the symbol you asked
# for does not exist" -- without it, a genuine not-found costs the full
# READY_TIMEOUT (measured: 2 minutes) and teaches callers the tool is slow.
READINESS_PROBE = "Get"


def wait_for_symbol(srv: Server, query: str, req_id: int):
    """The solution loads in the background, so an early empty workspace/symbol
    result means 'not loaded yet', NOT 'no such symbol'. Poll until either the
    query answers, or a probe that must match proves the server is loaded --
    at which point an empty result for the real query is a true negative and
    is returned immediately."""
    deadline = time.time() + READY_TIMEOUT
    while time.time() < deadline:
        res = srv.call("workspace/symbol", {"query": query}, req_id) or []
        if res:
            return res
        req_id += 1
        if srv.call("workspace/symbol", {"query": READINESS_PROBE}, req_id):
            return []          # loaded, and the symbol genuinely is not there
        req_id += 1
        time.sleep(1)
    die(f"the language server did not finish loading within {READY_TIMEOUT}s, "
        f"so {query!r} could not be looked up. This is a TIMEOUT, not an empty "
        f"result -- do not read it as 'nothing found'.")


def loc_of(item):
    loc = item.get("location") or {}
    start = (loc.get("range") or {}).get("start") or {}
    return rel(loc.get("uri", "")), start.get("line", 0) + 1, start.get("character", 0) + 1


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    cmd = argv[1]
    srv = Server().start()
    try:
        if cmd in ("symbol", "callers"):
            if len(argv) < 3:
                die(f"usage: lsp-query.py {cmd} <SymbolName>")
            name = argv[2]
            hits = wait_for_symbol(srv, name, 10)
            if not hits:
                print(f"no symbol named {name!r} in the solution "
                      f"(server answered; this is a real negative)")
                return 1
            if cmd == "symbol":
                for h in hits:
                    f, ln, col = loc_of(h)
                    print(f"{f}:{ln}:{col}  {h.get('name')}")
                return 0
            # callers: references for every matching definition
            total = 0
            for h in hits:
                f, ln, col = loc_of(h)
                refs = srv.call("textDocument/references", {
                    "textDocument": {"uri": uri(os.path.join(REPO, f))},
                    "position": {"line": ln - 1, "character": col - 1},
                    "context": {"includeDeclaration": False},
                }, 20 + total) or []
                print(f"{h.get('name')}  [defined {f}:{ln}]")
                if not refs:
                    print("    (no callers found)")
                for r in refs:
                    start = (r.get("range") or {}).get("start") or {}
                    print(f"    {rel(r.get('uri',''))}:{start.get('line',0)+1}"
                          f":{start.get('character',0)+1}")
                total += len(refs) + 1
            return 0

        if cmd in ("refs", "def"):
            if len(argv) < 5:
                die(f"usage: lsp-query.py {cmd} <file> <line> <col>   (1-based)")
            path, line, col = argv[2], int(argv[3]), int(argv[4])
            if not os.path.exists(path):
                die(f"no such file: {path}")
            method = ("textDocument/references" if cmd == "refs"
                      else "textDocument/definition")
            params = {"textDocument": {"uri": uri(path)},
                      "position": {"line": line - 1, "character": col - 1}}
            if cmd == "refs":
                params["context"] = {"includeDeclaration": True}
            # the position query needs the file loaded; give the solution a moment
            wait_for_symbol(srv, os.path.splitext(os.path.basename(path))[0], 30)
            res = srv.call(method, params, 40) or []
            if isinstance(res, dict):
                res = [res]
            if not res:
                print(f"no {cmd} results at {path}:{line}:{col} "
                      f"(server answered; this is a real negative)")
                return 1
            for r in res:
                start = (r.get("range") or {}).get("start") or {}
                print(f"{rel(r.get('uri',''))}:{start.get('line',0)+1}"
                      f":{start.get('character',0)+1}")
            return 0

        die(f"unknown command {cmd!r}. Try: callers | symbol | refs | def")
    finally:
        srv.stop()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
