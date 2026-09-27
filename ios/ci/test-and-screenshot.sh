#!/bin/bash
# 在模拟器上跑单元测试，并用演示数据给各个界面截图（build/screenshots）
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build/logs build/screenshots

UDID=$(xcrun simctl list devices available -j | python3 -c '
import json, re, sys
devices = json.load(sys.stdin)["devices"]
best = None
for runtime, devs in devices.items():
    m = re.search(r"iOS-(\d+)-(\d+)", runtime)
    if not m:
        continue
    version = (int(m.group(1)), int(m.group(2)))
    for d in devs:
        name = d.get("name", "")
        if not d.get("isAvailable") or not name.startswith("iPhone"):
            continue
        # 优先 6.1 寸的 Pro 机型，截图比例最常见
        score = (version, 2 if ("Pro" in name and "Max" not in name) else 1, name)
        if best is None or score > best[0]:
            best = (score, d["udid"], name)
if best:
    sys.stderr.write("simulator: %s %s\n" % (best[2], best[0][0]))
    print(best[1])
')
if [ -z "$UDID" ]; then
  echo "没有找到可用的 iPhone 模拟器"; exit 1
fi

xcrun simctl boot "$UDID" >/dev/null 2>&1 || true
xcrun simctl bootstatus "$UDID" -b >/dev/null

echo "== 单元测试 =="
if ! xcodebuild test -project DailyMemo.xcodeproj -scheme DailyMemo -destination "id=$UDID" \
     -derivedDataPath build/sim CODE_SIGNING_ALLOWED=NO > build/logs/test.log 2>&1; then
  grep -E "error:|failed|XCTAssert|Fatal" build/logs/test.log | head -120 || true
  tail -60 build/logs/test.log
  exit 1
fi
grep -E "Executed [0-9]+ test" build/logs/test.log | tail -1 || true

echo "== 截图 =="
APP=build/sim/Build/Products/Debug-iphonesimulator/DailyMemo.app
BUNDLE_ID=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$APP/Info.plist")
xcrun simctl install "$UDID" "$APP"
xcrun simctl status_bar "$UDID" override --time "9:41" --batteryState charged --batteryLevel 100 --wifiBars 3 --cellularBars 4 >/dev/null 2>&1 || true

for mode in light dark; do
  xcrun simctl ui "$UDID" appearance "$mode" || true
  for screen in today upcoming lists settings editor widgets onboarding; do
    xcrun simctl terminate "$UDID" "$BUNDLE_ID" >/dev/null 2>&1 || true
    xcrun simctl launch "$UDID" "$BUNDLE_ID" -demo -screen "$screen" -AppleLanguages "(zh-Hans)" -AppleLocale zh_CN >/dev/null
    sleep 6
    xcrun simctl io "$UDID" screenshot "build/screenshots/${screen}-${mode}.png" >/dev/null 2>&1
    echo "  ${screen}-${mode}.png"
  done
done
