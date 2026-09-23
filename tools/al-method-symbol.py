#!/usr/bin/env python3
"""Print a shipped AL method's ATTRIBUTES beside its return type, from a BC `.app`.

Why this exists: a method's SymbolReference entry can carry
`ReturnTypeDefinition: {"Name": "Boolean"}` because it is declared `: Boolean`, or
because an attribute SYNTHESIZED that return. `[TryFunction]` is the case that bites:
it declares no return type in AL source, and its synthesized Boolean means "the body
completed without raising" -- the opposite of AL's implicit default return for a plain
method whose body falls off the end.

Reading the return type without the attributes therefore gives exactly the wrong
conclusion, with nothing to flag it. Issue #3710 was filed that way: Codeunit 2000
"Time Series Management".GetMLForecastCredentials answering `true` was recorded as a
runner defect, when it is a [TryFunction] whose body completed and `true` is correct.

Two traps this tool exists to remove, both of which return a clean empty answer:

  * a BC `.app` is a 40-byte header followed by a zip, and the Base Application `.app`
    contains ANOTHER `.app` -- the sources and SymbolReference.json are in the INNER
    one, so a single-level reader finds nothing and reads as "the method is absent";
  * codeunits live in the `Namespaces` tree, NOT in the top-level `Codeunits` key,
    which is present and empty.

  tools/al-method-symbol.py <app-file> <codeunit-id> [method-name-substring]

Exit 0 printed at least one method, 1 nothing matched, 3 the package could not be read.
"""
import argparse, io, json, os, sys, zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    # Before any print: stdout is built from the console codec, and cp1252 cannot
    # encode the box-drawing and quote characters in this tool's output (#3589).
    _stdio.enable_utf8_stdio()


def unwrap(blob, what):
    i = blob.find(b'PK\x03\x04')
    if i < 0:
        print(f"refusing: {what} has no zip magic - not a BC .app?", file=sys.stderr)
        sys.exit(3)
    return zipfile.ZipFile(io.BytesIO(blob[i:]))


def walk_codeunits(node, want, out):
    for c in node.get("Codeunits") or []:
        if want is None or c.get("Id") == want:
            out.append(c)
    for ns in node.get("Namespaces") or []:
        walk_codeunits(ns, want, out)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("app")
    ap.add_argument("codeunit_id", type=int)
    ap.add_argument("method", nargs="?", default="")
    ap.add_argument("--source", action="store_true",
                    help="also print the shipped AL source of the matching method(s)")
    a = ap.parse_args()

    if not os.path.exists(a.app):
        print(f"refusing: no such package: {a.app}", file=sys.stderr)
        sys.exit(3)

    outer = unwrap(open(a.app, "rb").read(), a.app)
    inner_names = [n for n in outer.namelist() if n.lower().endswith(".app")]
    pkg, chain = outer, [os.path.basename(a.app)]
    if inner_names:                       # the nesting level that is easy to miss
        pkg = unwrap(outer.read(inner_names[0]), inner_names[0])
        chain.append(inner_names[0])
    print("package chain: " + " -> ".join(chain))

    try:
        raw = pkg.read("SymbolReference.json")
    except KeyError:
        print("refusing: no SymbolReference.json in the package", file=sys.stderr)
        sys.exit(3)

    cus = []
    walk_codeunits(json.loads(raw.decode("utf-8-sig")), a.codeunit_id, cus)
    if not cus:
        print(f"no codeunit {a.codeunit_id} in this package", file=sys.stderr)
        sys.exit(1)

    hits = 0
    for c in cus:
        for m in c.get("Methods") or []:
            if a.method and a.method.lower() not in (m.get("Name") or "").lower():
                continue
            hits += 1
            attrs = [x.get("Name") for x in (m.get("Attributes") or [])]
            rt = m.get("ReturnTypeDefinition")
            print(f"\nCodeunit {c.get('Id')} {c.get('Name')}.{m.get('Name')}")
            print(f"  ReturnTypeDefinition: {json.dumps(rt) if rt else 'null'}")
            print(f"  Attributes:           {json.dumps(attrs) if attrs else 'null'}")
            if rt and "TryFunction" in attrs:
                print("  NOTE: the Boolean is [TryFunction]'s SYNTHESIZED return -- it means")
                print("        'the body completed without raising', so falling off the end")
                print("        answers TRUE, not AL's implicit default false.")
            if a.source:
                for n in pkg.namelist():
                    if not n.endswith(".al"):
                        continue
                    txt = pkg.read(n).decode("utf-8-sig", errors="replace")
                    if f"procedure {m.get('Name')}(" not in txt:
                        continue
                    lines = txt.splitlines()
                    k = next(j for j, L in enumerate(lines)
                             if f"procedure {m.get('Name')}(" in L)
                    first = k
                    while first > 0 and lines[first - 1].strip().startswith("["):
                        first -= 1
                    print(f"  --- {n} ---")
                    for L in lines[first:k + 1]:
                        print("  " + L)
                    break

    if not hits:
        print(f"no method matching {a.method!r} on codeunit {a.codeunit_id}", file=sys.stderr)
        sys.exit(1)
    sys.exit(0)


if __name__ == "__main__":
    main()
