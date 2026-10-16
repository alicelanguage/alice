#!/bin/sh
# Alice installer for Linux and macOS.
#
#   curl -fsSL https://github.com/alicelanguage/alice/releases/latest/download/install.sh | sh
#   curl -fsSL .../install.sh | sh -s -- --nightly
#
# Installs a verified release into $ALICE_HOME/toolchains/VERSION (default ~/.alice), makes it the default,
# links ~/.alice/bin/alice, adds that to PATH, and installs the .NET 10 SDK into ~/.dotnet when it is missing.
# Afterwards manage versions with `alice toolchain` and update with `alice self update`.
#
# Options (or env): --version VER|stable|nightly (ALICE_VERSION)   --nightly   --home DIR (ALICE_HOME)
#   --archive FILE (offline install of a downloaded release archive)   --no-modify-path   --no-dotnet   -y/--yes   -h/--help
set -eu

REPO="${ALICE_RELEASES_REPO:-alicelanguage/alice}"
SPEC="${ALICE_VERSION:-stable}"
ALICE_HOME="${ALICE_HOME:-$HOME/.alice}"
ARCHIVE=""
MODIFY_PATH=1
INSTALL_DOTNET=1

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  B="$(printf '\033[1m')"; M="$(printf '\033[1;35m')"; G="$(printf '\033[1;32m')"; Y="$(printf '\033[1;33m')"
  R="$(printf '\033[1;31m')"; C="$(printf '\033[36m')"; D="$(printf '\033[90m')"; N="$(printf '\033[0m')"
else B=""; M=""; G=""; Y=""; R=""; C=""; D=""; N=""; fi

say()  { printf '%s%12s%s %s\n' "$G" "$1" "$N" "$2"; }
note() { printf '%s%12s%s %s\n' "$C" "$1" "$N" "$2"; }
warn() { printf '%s%12s%s %s\n' "$Y" "warning" "$N" "$1" >&2; }
die()  { printf '%serror%s: %s\n' "$R" "$N" "$1" >&2; [ $# -gt 1 ] && printf '  %shint%s: %s\n' "$C" "$N" "$2" >&2; exit 1; }

usage() {
  cat <<EOF
Alice installer (Linux, macOS)

usage: install.sh [options]
  --version VER|stable|nightly   release to install (default: stable; env ALICE_VERSION)
  --nightly                      same as --version nightly
  --home DIR                     install root (default: ~/.alice; env ALICE_HOME)
  --archive FILE                 install a downloaded release archive (verifies FILE.sha256 if present)
  --no-modify-path               do not edit shell profiles
  --no-dotnet                    do not install the .NET 10 SDK when it is missing
  -h, --help                     show this help
EOF
  exit 0
}

while [ $# -gt 0 ]; do
  case "$1" in
    --version|-v) [ $# -ge 2 ] || die "--version needs a value"; SPEC="$2"; shift ;;
    --version=*) SPEC="${1#*=}" ;;
    --nightly) SPEC="nightly" ;;
    --home) [ $# -ge 2 ] || die "--home needs a value"; ALICE_HOME="$2"; shift ;;
    --archive) [ $# -ge 2 ] || die "--archive needs a value"; ARCHIVE="$2"; shift ;;
    --no-modify-path) MODIFY_PATH=0 ;;
    --no-dotnet) INSTALL_DOTNET=0 ;;
    -y|--yes) ;;
    -h|--help) usage ;;
    *) die "unknown option $1" "install.sh --help" ;;
  esac
  shift
done

need() { command -v "$1" >/dev/null 2>&1; }

fetch() { # fetch URL [OUTFILE]
  if need curl; then
    if [ $# -ge 2 ]; then curl -fL --proto '=https' --tlsv1.2 --retry 3 --progress-bar -o "$2" "$1"
    else curl -fsSL --proto '=https' --tlsv1.2 --retry 3 ${GITHUB_TOKEN:+-H "Authorization: Bearer $GITHUB_TOKEN"} "$1"; fi
  elif need wget; then
    if [ $# -ge 2 ]; then wget -q --show-progress -O "$2" "$1"; else wget -qO- "$1"; fi
  else die "curl or wget is required"; fi
}

sha256() {
  if need sha256sum; then sha256sum "$1" | cut -d' ' -f1
  elif need shasum; then shasum -a 256 "$1" | cut -d' ' -f1
  else die "sha256sum or shasum is required to verify downloads"; fi
}

# ---- platform ----
case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  MINGW*|MSYS*|CYGWIN*) die "use install.ps1 on Windows" "irm https://github.com/$REPO/releases/latest/download/install.ps1 | iex" ;;
  *) die "unsupported OS $(uname -s)" ;;
esac
case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) die "unsupported architecture $(uname -m)" ;;
esac
# Apple silicon under Rosetta reports x86_64: prefer the native build
if [ "$os" = osx ] && [ "$arch" = x64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = 1 ]; then arch=arm64; fi
RID="$os-$arch"
case "$RID" in
  linux-x64|osx-x64|osx-arm64) ;;
  *) die "no prebuilt Alice for $RID yet" "build from source: https://github.com/$REPO#building" ;;
esac
if [ "$os" = linux ] && ldd --version 2>&1 | grep -qi musl; then die "Alpine/musl is not supported by the prebuilt archives"; fi

printf '\n  %s╱|%s   %sAlice installer%s %s(%s, %s)%s\n\n' "$M" "$N" "$B" "$N" "$D" "$RID" "$SPEC" "$N"

TMP="$(mktemp -d 2>/dev/null || mktemp -d -t alice)"
trap 'rm -rf "$TMP"' EXIT INT TERM

# ---- .NET 10 SDK ----
has_sdk() { need dotnet && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; }
if ! has_sdk && [ -x "$HOME/.dotnet/dotnet" ] && "$HOME/.dotnet/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
  export DOTNET_ROOT="$HOME/.dotnet"; PATH="$DOTNET_ROOT:$PATH"; export PATH
fi
DOTNET_ADDED=0
if has_sdk; then
  say "Found" ".NET SDK $(dotnet --list-sdks | grep '^10\.' | tail -1 | cut -d' ' -f1)"
elif [ "$INSTALL_DOTNET" = 1 ]; then
  note "Installing" ".NET 10 SDK into ~/.dotnet (Microsoft dotnet-install script)"
  fetch "https://dot.net/v1/dotnet-install.sh" "$TMP/dotnet-install.sh"
  sh "$TMP/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet" --no-path >/dev/null \
    || die ".NET SDK installation failed" "install it manually: https://dot.net/download"
  export DOTNET_ROOT="$HOME/.dotnet"; PATH="$DOTNET_ROOT:$PATH"; export PATH
  DOTNET_ADDED=1
  say "Installed" ".NET SDK $("$DOTNET_ROOT/dotnet" --list-sdks | tail -1 | cut -d' ' -f1)"
else
  warn ".NET 10 SDK not found; Alice needs it to build (https://dot.net/download)"
fi

# ---- release ----
if [ -n "$ARCHIVE" ]; then
  [ -f "$ARCHIVE" ] || die "archive not found: $ARCHIVE"
  NAME="$(basename "$ARCHIVE")"
  cp "$ARCHIVE" "$TMP/$NAME"
  [ -f "$ARCHIVE.sha256" ] && cp "$ARCHIVE.sha256" "$TMP/$NAME.sha256"
else
  case "$SPEC" in
    stable|latest) API="https://api.github.com/repos/$REPO/releases/latest" ;;
    nightly) API="https://api.github.com/repos/$REPO/releases/tags/nightly" ;;
    *) API="https://api.github.com/repos/$REPO/releases/tags/v${SPEC#v}" ;;
  esac
  note "Resolving" "$SPEC from github.com/$REPO"
  JSON="$(fetch "$API")" || die "could not find release '$SPEC'" "see https://github.com/$REPO/releases"
  URL="$(printf '%s' "$JSON" | tr ',' '\n' | grep '"browser_download_url"' | sed 's/.*"\(https[^"]*\)".*/\1/' | grep -- "-$RID\.tar\.gz$" | head -1)"
  [ -n "$URL" ] || die "release '$SPEC' has no $RID archive"
  NAME="$(basename "$URL")"
  say "Downloading" "$NAME"
  fetch "$URL" "$TMP/$NAME"
  fetch "$URL.sha256" "$TMP/$NAME.sha256" >/dev/null 2>&1 || die "missing checksum for $NAME"
fi

VERSION="${NAME#alice-}"; VERSION="${VERSION%-"$RID".tar.gz}"
if [ -f "$TMP/$NAME.sha256" ]; then
  expected="$(cut -d' ' -f1 < "$TMP/$NAME.sha256" | tr 'A-F' 'a-f')"
  actual="$(sha256 "$TMP/$NAME")"
  [ "$expected" = "$actual" ] || die "checksum mismatch for $NAME" "the download was corrupted or tampered with; retry"
  say "Verified" "sha256 $actual"
else
  warn "no .sha256 next to $ARCHIVE; skipping verification"
fi

DEST="$ALICE_HOME/toolchains/$VERSION"
mkdir -p "$ALICE_HOME/toolchains" "$ALICE_HOME/bin"
STAGE="$ALICE_HOME/toolchains/.tmp-$VERSION-$$"
rm -rf "$STAGE"; mkdir -p "$STAGE"
tar -xzf "$TMP/$NAME" -C "$STAGE" || die "could not extract $NAME"
[ -f "$STAGE/alice" ] || die "$NAME does not contain the alice executable"
chmod +x "$STAGE/alice"
rm -rf "$DEST"; mv "$STAGE" "$DEST"
if [ "$os" = osx ]; then xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true; fi

printf '{\n  "default": "%s"\n}\n' "$VERSION" > "$ALICE_HOME/settings.json"
cat > "$ALICE_HOME/bin/alice" <<'SHIM'
#!/bin/sh
# Alice shim: the apphost only searches default .NET locations, so point it at the dotnet on PATH.
if [ -z "${DOTNET_ROOT:-}" ]; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET_ROOT="$HOME/.dotnet"
  elif d="$(command -v dotnet 2>/dev/null)"; then
    while [ -L "$d" ]; do l="$(readlink "$d")"; case "$l" in /*) d="$l" ;; *) d="$(dirname "$d")/$l" ;; esac; done
    DOTNET_ROOT="$(cd "$(dirname "$d")" && pwd)"
  fi
  [ -n "${DOTNET_ROOT:-}" ] && export DOTNET_ROOT
fi
exec "__ALICE_EXE__" "$@"
SHIM
sed "s#__ALICE_EXE__#$DEST/alice#" "$ALICE_HOME/bin/alice" > "$ALICE_HOME/bin/alice.tmp" && mv "$ALICE_HOME/bin/alice.tmp" "$ALICE_HOME/bin/alice"
chmod +x "$ALICE_HOME/bin/alice"
say "Installed" "alice $VERSION → $DEST"

# ---- PATH ----
add_line() { # file line
  [ -f "$1" ] && grep -Fq "$2" "$1" 2>/dev/null && return 0
  [ -f "$1" ] || [ "$3" = create ] || return 0
  mkdir -p "$(dirname "$1")"; printf '\n# Alice\n%s\n' "$2" >> "$1"; note "Updated" "$1"
}
if [ "$MODIFY_PATH" = 1 ]; then
  line="export PATH=\"$ALICE_HOME/bin:\$PATH\""
  [ "$DOTNET_ADDED" = 1 ] && line="export DOTNET_ROOT=\"\$HOME/.dotnet\"; export PATH=\"$ALICE_HOME/bin:\$HOME/.dotnet:\$PATH\""
  add_line "$HOME/.profile" "$line" create
  add_line "$HOME/.bashrc" "$line" keep
  add_line "$HOME/.zshrc" "$line" "$( [ "$os" = osx ] && echo create || echo keep )"
  if need fish || [ -d "$HOME/.config/fish" ]; then
    fl="fish_add_path -g $ALICE_HOME/bin"
    [ "$DOTNET_ADDED" = 1 ] && fl="set -gx DOTNET_ROOT \$HOME/.dotnet; fish_add_path -g $ALICE_HOME/bin \$HOME/.dotnet"
    add_line "$HOME/.config/fish/conf.d/alice.fish" "$fl" create
  fi
fi

"$ALICE_HOME/bin/alice" --no-color version >/dev/null 2>&1 || warn "alice did not start; run \`$DEST/alice doctor\` for details"

printf '\n  %sAlice %s is installed.%s\n\n' "$G" "$VERSION" "$N"
case ":$PATH:" in
  *":$ALICE_HOME/bin:"*) ;;
  *) printf '  Restart your shell, or run:  %sexport PATH="%s/bin:$PATH"%s\n' "$B" "$ALICE_HOME" "$N" ;;
esac
printf '  Start the REPL:               %salice%s\n' "$B" "$N"
printf '  Create a project:             %salice new hello && cd hello && alice run%s\n' "$B" "$N"
printf '  Check the setup:              %salice doctor%s\n' "$B" "$N"
printf '  Manage versions:              %salice toolchain list | install nightly | default VER%s\n' "$B" "$N"
printf '  Update later:                 %salice self update%s\n' "$B" "$N"
printf '  Shell completions:            %seval "$(alice completions bash)"%s %s(zsh, fish, pwsh too)%s\n\n' "$B" "$N" "$D" "$N"
