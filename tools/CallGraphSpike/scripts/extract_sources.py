#!/usr/bin/env python3
"""Extract the AL sources the #4979 part-2 spike needs, one directory per app, into OUT/<app>/.

usage: extract_sources.py <platform-apps dir> <sandbox Applications dir> <out dir> [Name=path/to/extra.app ...]

<platform-apps dir>   an al-runner artifact dir's platform-apps/ (the Microsoft .app files of one
                      exact build; their sources sit in the nested .app)
<sandbox Applications dir>  a sandbox artifact's platform/Applications/ (test framework, test
                      libraries and the BaseApp Tests-*.Source.zip buckets)
Name=path.app         any further .app whose source is embedded (e.g. a third-party dependency)
"""
import io, os, sys, zipfile


def write_al(z, out, app):
    n = 0
    for name in z.namelist():
        if not name.lower().endswith(".al"):
            continue
        dst = os.path.join(out, app, name.replace("\\", "/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        with open(dst, "wb") as f:
            f.write(z.read(name))
        n += 1
    print(f"{app}: {n} .al files")
    if n == 0:
        sys.exit(f"refusing: {app} yielded no .al files")


def app_zip(data):
    return zipfile.ZipFile(io.BytesIO(data[40:]))  # NAVX header


def main():
    pa, sb, out = sys.argv[1:4]
    for f in sorted(os.listdir(pa)):
        if not f.endswith(".app") or not f.startswith("Microsoft_"):
            continue
        app = f[len("Microsoft_"):].rsplit("_", 1)[0]
        z = app_zip(open(os.path.join(pa, f), "rb").read())
        inner = [n for n in z.namelist() if n.endswith(".app")]
        if inner:
            write_al(app_zip(z.read(inner[0])), out, app)
    if os.path.exists(os.path.join(pa, "System.app")):
        write_al(app_zip(open(os.path.join(pa, "System.app"), "rb").read()), out, "System")
    for rel in ["TestFramework/TestLibraries/Any/Any.Source.zip",
                "TestFramework/TestLibraries/Assert/Library Assert.Source.zip",
                "TestFramework/TestLibraries/permissions mock/Permissions Mock.Source.zip",
                "TestFramework/TestLibraries/Variable Storage/Library Variable Storage.Source.zip",
                "TestFramework/TestRunner/Test Runner.Source.zip",
                "System Application/Test/System Application Test Library.Source.zip",
                "BusinessFoundation/Test/Business Foundation Test Libraries.Source.zip"]:
        write_al(zipfile.ZipFile(os.path.join(sb, rel)), out, os.path.basename(rel)[:-len(".Source.zip")])
    tb = os.path.join(sb, "BaseApp/Test")
    for f in sorted(os.listdir(tb)):
        if f.endswith(".Source.zip") and f != "Tests-Local.Source.zip":
            write_al(zipfile.ZipFile(os.path.join(tb, f)), out, f[:-len(".Source.zip")])
    for extra in sys.argv[4:]:
        name, path = extra.split("=", 1)
        write_al(app_zip(open(path, "rb").read()), out, name)


main()
