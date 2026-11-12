#!/bin/sh
# Explicit config only; foreground lifetime and Ctrl-C own just this Node/bridge.
set -eu
if [ "$(uname -s)" != Darwin ]; then
  echo 'This launcher is for macOS; use Start-Studio.ps1 on Windows.' >&2; exit 2
fi
if [ "$#" -ne 2 ] || [ "$1" != --config ]; then
  echo 'Usage: sh studio/start-studio.sh --config /absolute/task-fixture/studio.json' >&2; exit 2
fi
case "$2" in /*) ;; *) echo 'Config must be an explicit absolute path.' >&2; exit 2;; esac
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
command -v node >/dev/null
[ -f "$ROOT/dist/client/index.html" ] || { echo 'Build studio assets first with npm run build.' >&2; exit 2; }
# No private default paths, browser launch, installs or background service.
node --input-type=module - "$2" "$ROOT" <<'JS'
import { pathToFileURL } from 'node:url';
import path from 'node:path';
const { loadConfig, validateMacConfig } = await import(pathToFileURL(path.join(process.argv[3], 'server/live-server.mjs')));
await validateMacConfig(await loadConfig(process.argv[2]), process.argv[2]);
JS
exec node "$ROOT/server/live-server.mjs" --config "$2"
