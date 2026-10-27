#!/usr/bin/env bash
# Checks the Tauri shell on the Linux dev box: config shape, then cargo check and the Rust tests.
set -euo pipefail
cd "$(dirname "$0")/.."
tauri=desktop/src-tauri

node -e '
const c = require("./desktop/src-tauri/tauri.conf.json");
const fail = (m) => { console.error("tauri.conf.json: " + m); process.exit(1); };
if (!c.bundle?.externalBin?.includes("binaries/ofnr-server")) fail("bundle.externalBin needs binaries/ofnr-server");
if (!c.plugins?.updater?.endpoints?.[0]?.endsWith("latest.json")) fail("plugins.updater.endpoints[0] must end with latest.json");
if (c.app?.windows?.[0]?.dragDropEnabled !== false) fail("app.windows[0].dragDropEnabled must be false");
console.log("tauri.conf.json ok");
'

# Tauri gives an inline <style> a CSP nonce, and a nonce turns off 'unsafe-inline' for Fluent UI's runtime styles.
if grep -q '<style' web/index.html; then echo "web/index.html: move <style> into web/src/index.css" >&2; exit 1; fi

# web/src is covered by the No_em_dash_in_source test; this covers the docs and skills.
if LC_ALL=C grep -rlF -e $'\xe2\x80\x93' -e $'\xe2\x80\x94' README.md CLAUDE.md claude-plugin; then echo "em dash or en dash in the files above" >&2; exit 1; fi

# tauri-build wants the sidecar for the host triple; the real one is the Windows exe from publish-sidecar.ps1.
bin="$tauri/binaries/ofnr-server-$(rustc -vV | sed -n 's/^host: //p')"
[ -e "$bin" ] || { mkdir -p "$(dirname "$bin")"; printf '#!/bin/sh\n' > "$bin"; chmod +x "$bin"; }

cargo check --manifest-path "$tauri/Cargo.toml"
cargo test --manifest-path "$tauri/Cargo.toml" --lib
