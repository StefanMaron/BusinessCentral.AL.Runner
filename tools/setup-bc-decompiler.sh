#!/usr/bin/env bash
# Install pardeike/DecompilerServer and register every cached BC version as a context.
#
# Why: settling "what does BC actually do" means reading Ncl.dll. The old way was a
# 16 MB decompile dumped to disk and grepped, which cannot answer "what calls this"
# across an async state machine and has to be regenerated per BC version. This server
# answers both in well under a second, and can diff a method between two BC versions --
# which is how a Cecil rewrite that silently stopped being reached gets caught.
#
# Requires: .NET 10 SDK (the runner itself stays on net8.0; this is a separate tool).
set -euo pipefail

DEST="${DECOMPILER_SERVER_DIR:-$HOME/Documents/Repos/tools/DecompilerServer}"

if ! dotnet --list-sdks | grep -q '^10\.'; then
  echo "ERROR: .NET 10 SDK not found. DecompilerServer needs it; the runner does not." >&2
  echo "Installed SDKs:" >&2; dotnet --list-sdks >&2
  exit 1
fi

if [ -d "$DEST/.git" ]; then
  echo "Updating $DEST ..."
  git -C "$DEST" pull --ff-only
else
  echo "Cloning into $DEST ..."
  mkdir -p "$(dirname "$DEST")"
  git clone -q https://github.com/pardeike/DecompilerServer.git "$DEST"
fi
echo "Publishing..."
dotnet publish "$DEST/DecompilerServer.csproj" -c Release -o "$DEST/publish" >/dev/null

echo
echo "Installed. Register it in .mcp.json (this file is gitignored, so it is per-machine):"
cat <<JSON

  "bc-decompiler": {
    "type": "stdio",
    "command": "dotnet",
    "args": ["$DEST/publish/DecompilerServer.dll"],
    "env": {}
  }

JSON
echo "Then load the BC versions you care about once — contexts persist in"
echo "~/.decompilerserver/ across restarts, so this is a one-time step per version:"
echo
echo "  load_assembly(assemblyPath: \"<artifacts>/<ver>/Microsoft.Dynamics.Nav.Ncl.dll\","
echo "                additionalSearchDirs: [\"<artifacts>/<ver>\"],"
echo "                contextAlias: \"bc281\")"
echo
echo "Aliases already in use on this machine follow the pattern bc260, bc270 ... bc284."
