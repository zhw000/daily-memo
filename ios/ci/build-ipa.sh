#!/bin/bash
# 编译真机版本并打包成未签名 IPA：build/DailyMemo-unsigned.ipa
# 拿到 IPA 后用 Sideloadly / AltStore / 爱思助手 / 自己的证书签名安装即可
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build/logs
rm -rf build/device build/ipa build/DailyMemo-unsigned.ipa

echo "== 编译 Release (iphoneos) =="
if ! xcodebuild -project DailyMemo.xcodeproj -scheme DailyMemo -configuration Release \
     -sdk iphoneos -destination 'generic/platform=iOS' -derivedDataPath build/device \
     CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" \
     build > build/logs/device.log 2>&1; then
  grep -E "error:" build/logs/device.log | head -120 || true
  tail -60 build/logs/device.log
  exit 1
fi

echo "== 编译警告（去重）=="
grep -E "\.swift:[0-9]+:[0-9]+: warning:" build/logs/device.log | sed -E 's#^.*/ios/##' | sort -u | head -80 || true

APP=build/device/Build/Products/Release-iphoneos/DailyMemo.app
test -d "$APP/PlugIns/DailyMemoWidget.appex" || { echo "小组件扩展没有打进 App"; exit 1; }

# 用临时签名写入 entitlements（App Group），重新签名的工具据此保留 App Group，小组件才能共享数据
codesign --force --sign - --timestamp=none --entitlements Widget/DailyMemoWidget.entitlements "$APP/PlugIns/DailyMemoWidget.appex"
codesign --force --sign - --timestamp=none --entitlements App/DailyMemo.entitlements "$APP"
codesign -d --entitlements - "$APP" 2>/dev/null | head -20 || true

mkdir -p build/ipa/Payload
cp -R "$APP" build/ipa/Payload/
(cd build/ipa && zip -qry ../DailyMemo-unsigned.ipa Payload)
ls -lh build/DailyMemo-unsigned.ipa
