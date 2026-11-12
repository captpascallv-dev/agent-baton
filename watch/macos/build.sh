#!/bin/sh
set -eu
cd "$(dirname "$0")"
swift build -c release --jobs 1
APP="$PWD/build/CliWatchMac.app"
mkdir -p "$APP/Contents/MacOS"
cp .build/release/CliWatchMac "$APP/Contents/MacOS/"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleExecutable</key><string>CliWatchMac</string><key>CFBundleIdentifier</key><string>dev.agent-baton.cli-watch</string><key>CFBundleName</key><string>CLI 执行窗</string><key>CFBundlePackageType</key><string>APPL</string><key>LSMinimumSystemVersion</key><string>13.0</string><key>NSHighResolutionCapable</key><true/></dict></plist>
PLIST
printf '%s\n' "$APP"
