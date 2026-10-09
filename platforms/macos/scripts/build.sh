#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Build this independent Swift application on macOS 13 or newer." >&2
  exit 1
fi
if ! command -v swift >/dev/null 2>&1; then
  echo "未找到 swift。请安装 Xcode 或 Swift toolchain（macOS 13+）：https://www.swift.org/install/" >&2
  exit 1
fi
if ! command -v xcodebuild >/dev/null 2>&1 && [[ -z "${SWIFT_FORCE:-}" ]]; then
  echo "警告：未检测到 xcodebuild；通用架构与 codesign 可能失败。已安装 Command Line Tools / Xcode 时可忽略 SWIFT_FORCE=1。" >&2
fi
swift test
swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)"
OUT="$PWD/dist/Boardcast.app"
mkdir -p "$OUT/Contents/MacOS"
cp "$BIN/Boardcast" "$OUT/Contents/MacOS/Boardcast"
cp Info.plist "$OUT/Contents/Info.plist"
codesign --force --options runtime --sign "${SIGN_IDENTITY:--}" "$OUT"
codesign --verify --strict "$OUT"
ditto -c -k --keepParent "$OUT" "$PWD/dist/Boardcast-macOS.zip"
echo "Created $PWD/dist/Boardcast-macOS.zip"
echo "Public distribution requires Developer ID signing and notarization."
