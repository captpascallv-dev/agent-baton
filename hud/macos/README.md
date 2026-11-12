# Mac menu-bar HUD

SwiftUI presentation of the Core CLI's schema-1 `quota-snapshot.json`. No credential access, provider parsing, account login, or CLI invocation happens in this app. With no arguments it uses clearly marked demo data. Other providers are never queried.

Build (serial, through repository guard):

```sh
python3 guards/capped_run.py --mem-gb 1 --timeout 900 --log /tmp/hud-swift-build.log -- sh hud/macos/build.sh
open hud/macos/build/CodexUsageHUD.app --args --demo
open hud/macos/build/CodexUsageHUD.app --args --snapshot "$HOME/Library/Application Support/CodexUsageHUD/quota-snapshot.json"
```

The CLI must independently export its snapshot. The app reads it every five seconds. Unavailable, schema-mismatched, and older-than-five-minute data is not shown as live menu-bar percentage. Last successful details remain visibly stale. Null percentages stay unknown. Countdown reaching zero says refresh is due, never assumes renewed quota.

`--appearance light` or `--appearance dark` changes only this application's appearance, not macOS settings. Without that argument the system appearance is followed. For screenshots, click the menu item to expand its panel; capture only this app, avoiding other personal desktop content. Screen Recording permission may be necessary and must be approved through the normal flow. Demo screenshots must be labeled as demo, not live account validation.

macOS 13+; official SwiftUI, AppKit, and Foundation only. No startup/login item is installed. Quit from the panel's power button.

For a deterministic application-only screenshot, launch the executable with `--demo --panel --appearance light` (or `dark`). It opens a preview window and prints `HUD_WINDOW_ID=<number>` to stdout. Use `screencapture -l <number> <file.png>` only after the normal screen-capture permission is available. This preview uses the same panel view as the menu-bar popover; it does not prove the menu-bar placement, which should be checked separately.

### Actual status-item popover (public APIs)

Launch with `--popover --appearance light` (or `dark`) to open the real menu-bar
popover using the app's own `NSStatusItem` and `NSPopover`. The contents remain
SwiftUI. No Accessibility or other-app control is used. The app prints
`HUD_POPOVER_WINDOW_ID` for exact-window capture and `HUD_STATUS_RECT_CAPTURE`
(`x,y,width,height`, screen points, primary-display top-left coordinates) for an
own-button-only `screencapture -R` crop. `HUD_STATUS_RECT_APPKIT` is also printed
for diagnostics. Capture permission is still required; do not bypass denial.
Capture the two regions separately; do not take a full-screen shot or a bounding
rectangle that includes unrelated menu-bar items or other app windows.
`--panel` remains available as an independent own-window preview fallback.

界面中文文案与 Windows 版本保持一致：剩余额度、距离重置、重置时间未提供、不可用、已过期、未知。窗口名称仅做展示翻译，不改变快照或重新推断重置周期。
