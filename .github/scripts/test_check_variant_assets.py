#!/usr/bin/env python3
"""Tests for check_variant_assets.py (#5461).

The shape under test is the one that shipped: a BC 29 variant whose al-runner.deps.json names
runtimes/win/lib/net10.0/System.Diagnostics.EventLog.dll while the package carried only the
install's runtimes/win/lib/net8.0/... assets. Every case builds a small nupkg-shaped zip and runs
the script as a subprocess, so the exit code is what is asserted.

Usage: python3 test_check_variant_assets.py
Exits 0 when every case passes, 1 otherwise.
"""

import json
import pathlib
import subprocess
import sys
import tempfile
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
SCRIPT = HERE / "check_variant_assets.py"
TOOL = "tools/net8.0/any"

failures = 0


def check(name, ok, detail=""):
    global failures
    if ok:
        print(f"PASS  {name}")
    else:
        failures += 1
        print(f"FAIL  {name}\n      {detail}")


def lib_entry(runtime=(), native=(), resources=None, runtime_targets=None):
    out = {}
    if runtime:
        out["runtime"] = {p: {} for p in runtime}
    if native:
        out["native"] = {p: {} for p in native}
    if resources:
        out["resources"] = {p: {"locale": loc} for p, loc in resources.items()}
    if runtime_targets:
        out["runtimeTargets"] = {p: {"rid": "win", "assetType": "runtime"} for p in runtime_targets}
    return out


def deps(libs):
    """libs: name -> (type, entry)."""
    return {
        "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0"},
        "targets": {".NETCoreApp,Version=v10.0": {n: e for n, (_, e) in libs.items()}},
        "libraries": {n: {"type": t} for n, (t, _) in libs.items()},
    }


EVENTLOG = deps({
    "System.Diagnostics.EventLog/10.0.0": ("package", lib_entry(
        runtime=["lib/net10.0/System.Diagnostics.EventLog.dll"],
        runtime_targets=["runtimes/win/lib/net10.0/System.Diagnostics.EventLog.dll",
                         "runtimes/win/lib/net10.0/System.Diagnostics.EventLog.Messages.dll"])),
})

WIN10 = ["runtimes/win/lib/net10.0/System.Diagnostics.EventLog.dll",
         "runtimes/win/lib/net10.0/System.Diagnostics.EventLog.Messages.dll"]
INSTALL = [f"{TOOL}/al-runner.dll", f"{TOOL}/System.Diagnostics.EventLog.dll",
           f"{TOOL}/runtimes/win/lib/net8.0/System.Diagnostics.EventLog.dll",
           f"{TOOL}/runtimes/win/lib/net8.0/System.Diagnostics.EventLog.Messages.dll"]


def variant(build, deps_doc, files):
    """Entries of one variant folder: its deps.json plus `files` (paths relative to the folder)."""
    base = f"{TOOL}/variants/{build}/"
    out = {base + "al-runner.deps.json": json.dumps(deps_doc)}
    out.update({base + f: "x" for f in files})
    return out


def run(entries):
    with tempfile.TemporaryDirectory() as tmp:
        pkg = pathlib.Path(tmp) / "t.nupkg"
        with zipfile.ZipFile(pkg, "w") as z:
            for name, body in entries.items():
                z.writestr(name, body)
        return subprocess.run([sys.executable, str(SCRIPT), str(pkg)], capture_output=True, text=True)


def install_entries(extra=()):
    return {n: "x" for n in [*INSTALL, *extra]}


# 1. The shipped defect: the variant's win-rid assets are in neither folder.
r = run({**install_entries(), **variant("29.0.1", EVENTLOG, ["System.Diagnostics.EventLog.dll"])})
check("a variant lacking the rid-specific assets its deps.json names exits 1", r.returncode == 1, r.stdout + r.stderr)
check("... and names both missing files", all(p in r.stdout for p in WIN10), r.stdout)
check("... and does not name the asset that IS present (root-level EventLog.dll)",
      "lib/net10.0/System.Diagnostics.EventLog.dll" in r.stdout and "\n    System.Diagnostics.EventLog.dll" not in r.stdout,
      r.stdout)

# 2. The fix: the same package with the variant carrying them.
r = run({**install_entries(), **variant("29.0.1", EVENTLOG, ["System.Diagnostics.EventLog.dll", *WIN10])})
check("the variant carrying its own rid-specific assets exits 0", r.returncode == 0, r.stdout + r.stderr)

# 3. The install folder supplies a file the variant leaves out (the shadow-dir mirror's union).
cecil = deps({"Mono.Cecil/0.11.6": ("package", lib_entry(runtime=["lib/netstandard2.0/Mono.Cecil.dll"]))})
r = run({**install_entries([f"{TOOL}/Mono.Cecil.dll"]), **variant("28.5.1", cecil, [])})
check("an asset the install carries and the variant omits is not a finding", r.returncode == 0, r.stdout + r.stderr)
r = run({**install_entries(), **variant("28.5.1", cecil, [])})
check("the same asset absent from BOTH folders is a finding", r.returncode == 1 and "Mono.Cecil.dll" in r.stdout,
      r.stdout + r.stderr)

# 4. BC service-tier assemblies are "reference" libraries, deliberately not shipped.
bc = deps({"Microsoft.Dynamics.Nav.Ncl/29.0.0.0": ("reference", lib_entry(runtime=["Microsoft.Dynamics.Nav.Ncl.dll"]))})
r = run({**install_entries(), **variant("29.0.1", bc, [])})
check("a reference library (BC service tier) missing from the package is not a finding", r.returncode == 0,
      r.stdout + r.stderr)

# 5. Satellite resources are expected at <locale>/<file name>.
res = deps({"Microsoft.CodeAnalysis.Common/5.3.0": ("package", lib_entry(
    resources={"lib/net10.0/de/Microsoft.CodeAnalysis.resources.dll": "de"}))})
r = run({**install_entries(), **variant("29.0.1", res, [])})
check("a satellite resource named by the deps.json and in neither folder is a finding",
      r.returncode == 1 and "de/Microsoft.CodeAnalysis.resources.dll" in r.stdout, r.stdout + r.stderr)
r = run({**install_entries(["%s/de/Microsoft.CodeAnalysis.resources.dll" % TOOL]), **variant("29.0.1", res, [])})
check("... and is satisfied by the install's copy", r.returncode == 0, r.stdout + r.stderr)

# 6. Several variants: only the broken one is reported, and the verdict is the worst one.
r = run({**install_entries(),
         **variant("28.5.1", EVENTLOG, ["System.Diagnostics.EventLog.dll", *WIN10]),
         **variant("29.0.1", EVENTLOG, ["System.Diagnostics.EventLog.dll"])})
check("one broken variant among whole ones exits 1", r.returncode == 1, r.stdout + r.stderr)
check("... naming the broken one and reporting the whole one as whole",
      "variant 29.0.1: 2 asset(s)" in r.stdout and "variant 28.5.1: every asset" in r.stdout, r.stdout)

# 7. The third state: nothing measured is never the success state.
r = run(install_entries())
check("a package with no variant carrying a deps.json exits 3, not 0", r.returncode == 3, r.stdout + r.stderr)
r = run({**install_entries(), f"{TOOL}/variants/29.0.1/al-runner.deps.json": "{not json"})
check("an unreadable variant deps.json exits 3", r.returncode == 3, r.stdout + r.stderr)
with tempfile.TemporaryDirectory() as tmp:
    bad = pathlib.Path(tmp) / "bad.nupkg"
    bad.write_text("not a zip")
    r = subprocess.run([sys.executable, str(SCRIPT), str(bad)], capture_output=True, text=True)
    check("a file that is not a zip exits 3", r.returncode == 3, r.stdout + r.stderr)
    r = subprocess.run([sys.executable, str(SCRIPT), str(pathlib.Path(tmp) / "absent.nupkg")], capture_output=True, text=True)
    check("a path that does not exist exits 3", r.returncode == 3, r.stdout + r.stderr)
r = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True)
check("no argument exits 3", r.returncode == 3, r.stdout + r.stderr)

print(f"\n{failures} failure(s)")
sys.exit(1 if failures else 0)
