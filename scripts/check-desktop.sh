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

# tauri-build wants the sidecar for the host triple; the real one is the Windows exe from publish-sidecar.ps1.
bin="$tauri/binaries/ofnr-server-$(rustc -vV | sed -n 's/^host: //p')"
[ -e "$bin" ] || { mkdir -p "$(dirname "$bin")"; printf '#!/bin/sh\n' > "$bin"; chmod +x "$bin"; }

cargo check --manifest-path "$tauri/Cargo.toml"
cargo test --manifest-path "$tauri/Cargo.toml" --lib
