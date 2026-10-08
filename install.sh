#!/usr/bin/env bash
# Agent Orchestrator installer for Linux and macOS. Run from a clone of the repository:
#
#   ./install.sh                     build, install, then run the guided `orch setup`
#   ./install.sh --no-setup          install only
#   ./install.sh --agents claude,qoder --yes     non-interactive setup (other flags go to `orch setup`)
#   ./install.sh --uninstall [--purge]           disconnect agents, remove autostart and the app (--purge: also task data/settings)
#
# Re-run it after `git pull` to upgrade; your settings and tasks are kept.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
setup=1 uninstall=0 purge=0
setup_args=()
for arg in "$@"; do
  case "$arg" in
    --no-setup) setup=0 ;;
    --uninstall) uninstall=1 ;;
    --purge) purge=1 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) setup_args+=("$arg") ;;
  esac
done

# Same folder the app computes (.NET LocalApplicationData + agent-orchestrator).
if [[ -n "${ORCHESTRATOR_HOME:-}" ]]; then
  root="$ORCHESTRATOR_HOME"
elif [[ "$(uname -s)" == "Darwin" ]]; then
  root="$HOME/Library/Application Support/agent-orchestrator"
else
  root="${XDG_DATA_HOME:-$HOME/.local/share}/agent-orchestrator"
fi
app="$root/app"
bindir="$HOME/.local/bin"
link="$bindir/orch"

say() { printf '\033[1m%s\033[0m\n' "$*"; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

if [[ $uninstall == 1 ]]; then
  if [[ -x "$app/orch" ]]; then "$app/orch" uninstall || true; fi
  rm -rf "$app"
  [[ -L "$link" ]] && rm -f "$link"
  if [[ $purge == 1 ]]; then rm -rf "$root"; say "Removed $root (tasks, logs, settings)."; else say "App removed. Tasks and settings kept in $root (use --purge to delete)."; fi
  exit 0
fi

# ---- prerequisites
command -v git >/dev/null || fail "git is required."
command -v dotnet >/dev/null || fail ".NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0"
dotnet --list-sdks | grep -q '^10\.' || fail ".NET 10 SDK is required (found: $(dotnet --list-sdks | tr '\n' ' ')). https://dotnet.microsoft.com/download/dotnet/10.0"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) rid=linux-x64 ;;
  Linux-aarch64|Linux-arm64) rid=linux-arm64 ;;
  Darwin-x86_64) rid=osx-x64 ;;
  Darwin-arm64) rid=osx-arm64 ;;
  *) fail "Unsupported platform $(uname -s) $(uname -m)." ;;
esac

# ---- build (self-contained: works under systemd/launchd without a DOTNET_ROOT)
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
say "Building Agent Orchestrator ($rid)…"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet publish "$repo/src/Orchestrator.Api" -c Release -r "$rid" --self-contained -o "$stage/app" -v quiet -nologo
dotnet publish "$repo/src/Orchestrator.Cli" -c Release -r "$rid" --self-contained -o "$stage/app" -v quiet -nologo

# ---- stop the old version, then swap in the new one
if [[ -x "$app/orch" ]]; then "$app/orch" stop >/dev/null 2>&1 || true; fi
mkdir -p "$root"
rm -rf "$app"
mv "$stage/app" "$app"
say "Installed to $app"

# ---- put orch on PATH
mkdir -p "$bindir"
ln -sf "$app/orch" "$link"
case ":$PATH:" in
  *":$bindir:"*) ;;
  *) echo "Note: $bindir is not on your PATH. Add this to your shell profile:"
     echo "  export PATH=\"\$HOME/.local/bin:\$PATH\""
     export PATH="$bindir:$PATH" ;;
esac

# ---- restart autostart (systemd) if it was enabled, otherwise setup starts the server
if [[ $setup == 1 ]]; then
  "$app/orch" setup ${setup_args[@]+"${setup_args[@]}"}
else
  "$app/orch" start
fi
echo
say "Manage platforms any time: orch setup, or open the Setup page shown above."
