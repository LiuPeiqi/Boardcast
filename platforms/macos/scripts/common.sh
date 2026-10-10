#!/bin/bash
# Local Apple tools only; compatible with the macOS Bash 3.2 installation.
set -euo pipefail
MAC_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
PROJECT="$MAC_ROOT/Boardcast.xcodeproj"
SCHEME="Boardcast"
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

preflight() {
    [[ "$(uname -s)" == "Darwin" ]] || fail "Use a Mac with full Xcode 16 or newer."
    local command_name xcode_version selected
    for command_name in xcodebuild xcrun codesign ditto plutil shasum lipo security hdiutil spctl; do
        command -v "$command_name" >/dev/null || fail "Missing Apple tool: $command_name."
    done
    selected="$(xcode-select -p)"
    [[ "$selected" == */Contents/Developer ]] || fail "Select full Xcode, not standalone Command Line Tools, with xcode-select."
    xcode_version="$(xcodebuild -version | awk '/^Xcode / { print $2; exit }')"
    [[ "${xcode_version%%.*}" =~ ^[0-9]+$ ]] || fail "Cannot determine the selected Xcode version."
    (( ${xcode_version%%.*} >= 16 )) || fail "Xcode 16 or newer is required."
    xcrun --sdk macosx --show-sdk-path >/dev/null
    plutil -lint "$PROJECT/project.pbxproj" "$MAC_ROOT/Info.plist"
    [[ -d "$MAC_ROOT/Sources/Boardcast/Resources/Icons" ]] || fail "The complete resource tree is required."
    [[ -f "$MAC_ROOT/Sources/BoardcastUpdater/main.swift" && -f "$MAC_ROOT/Sources/Shared/MacRelease.swift" ]] || fail "The native updater sources are missing."
    [[ -f "$MAC_ROOT/Resources/Assets.xcassets/AppIcon.appiconset/Contents.json" ]] || fail "The app icon catalog is missing."
    printf 'Xcode: %s\nDeveloper directory: %s\n' "$xcode_version" "$selected"
    xcrun swift --version
}

config_value() {
    local value
    value="$(awk -F= -v name="$1" '$1 ~ "^[[:space:]]*" name "[[:space:]]*$" { gsub(/^[[:space:]]+|[[:space:]]+$/, "", $2); print $2 }' "$MAC_ROOT/Config/Base.xcconfig")"
    [[ -n "$value" && "$value" != *$'\n'* ]] || fail "Expected exactly one $1 in Base.xcconfig."
    printf '%s' "$value"
}
new_run_directory() {
    mkdir -p "$MAC_ROOT/.build/xcode"
    mktemp -d "$MAC_ROOT/.build/xcode/$1.XXXXXXXX"
}
test_project() {
    local run="$1"
    xcodebuild -project "$PROJECT" -scheme "$SCHEME" \
        -configuration Debug -destination "platform=macOS" \
        -derivedDataPath "$run/DerivedData" -resultBundlePath "$run/Tests.xcresult" \
        -disableAutomaticPackageResolution -skipPackageUpdates CODE_SIGN_IDENTITY=- test
}
verify_app() {
    local app="$1" architectures locale executable identifier helper_signature
    [[ -d "$app" ]] || fail "Build did not produce the app: $app."
    plutil -lint "$app/Contents/Info.plist"
    identifier="$(plutil -extract CFBundleIdentifier raw -o - "$app/Contents/Info.plist")"
    [[ "$identifier" == "$(config_value BOARDCAST_BUNDLE_IDENTIFIER)" ]] || fail "The app has an unexpected bundle identifier."
    [[ -d "$app/Contents/Resources/Icons" ]] || fail "The app is missing shared icons."
    [[ -d "$app/Contents/Resources/ThirdParty" ]] || fail "The app is missing resource licenses."
    for locale in en zh-Hans; do
        [[ -f "$app/Contents/Resources/$locale.lproj/Localizable.strings" ]] || fail "The app is missing $locale UI strings."
        [[ -f "$app/Contents/Resources/$locale.lproj/Localizable.stringsdict" ]] || fail "The app is missing $locale plural rules."
        [[ -f "$app/Contents/Resources/$locale.lproj/InfoPlist.strings" ]] || fail "The app is missing $locale privacy descriptions."
    done
    for executable in Boardcast BoardcastUpdater; do
        [[ -x "$app/Contents/MacOS/$executable" ]] || fail "The app is missing executable $executable."
        architectures="$(lipo -archs "$app/Contents/MacOS/$executable")"
        [[ " $architectures " == *" arm64 "* && " $architectures " == *" x86_64 "* ]] || fail "$executable is not universal: $architectures."
        codesign --verify --strict --all-architectures --verbose=2 "$app/Contents/MacOS/$executable"
    done
    helper_signature="$(codesign --display --verbose=2 "$app/Contents/MacOS/BoardcastUpdater" 2>&1)"
    printf '%s\n' "$helper_signature" | grep -Fx "Identifier=$identifier.updater" >/dev/null || fail "The helper has an unexpected signing identifier."
    codesign --verify --deep --strict --all-architectures --verbose=2 "$app"
}
verify_developer_signature() {
    local target="$1" identifier="$2" team="$3" runtime="$4" architecture signature entitlements entitlement_file task_allow
    for architecture in arm64 x86_64; do
        signature="$(codesign --display --architecture "$architecture" --verbose=4 "$target" 2>&1)"
        printf '%s\n' "$signature" | grep -Fx "Identifier=$identifier" >/dev/null || fail "Unexpected $architecture signing identifier for $target."
        printf '%s\n' "$signature" | grep -Fx "TeamIdentifier=$team" >/dev/null || fail "Unexpected $architecture signing team for $target."
        printf '%s\n' "$signature" | grep -F "Authority=Developer ID Application:" >/dev/null || fail "Missing Developer ID signature for $target."
        printf '%s\n' "$signature" | grep -E '^Timestamp=' >/dev/null || fail "Missing secure timestamp for $target."
        if [[ "$runtime" == "YES" ]]; then
            printf '%s\n' "$signature" | grep -E 'flags=0x[0-9a-fA-F]+\(.*runtime.*\)' >/dev/null || fail "Missing Hardened Runtime for $target."
        fi
        entitlements="$(codesign --display --architecture "$architecture" --entitlements :- "$target" 2>/dev/null)"
        if [[ -n "$entitlements" ]]; then
            entitlement_file="$(mktemp "${TMPDIR:-/tmp}/boardcast-entitlements.XXXXXXXX")"
            printf '%s\n' "$entitlements" >"$entitlement_file"
            if ! plutil -lint "$entitlement_file" >/dev/null; then
                rm -f "$entitlement_file"
                fail "Cannot parse release entitlements for $target."
            fi
            task_allow="$(/usr/libexec/PlistBuddy -c 'Print :com.apple.security.get-task-allow' "$entitlement_file" 2>/dev/null || true)"
            rm -f "$entitlement_file"
            [[ "$task_allow" != "true" ]] || fail "A public release must not carry get-task-allow."
        fi
    done
}
verify_dmg_signature() {
    local dmg="$1" team="$2" signature
    hdiutil verify "$dmg"
    codesign --verify --strict --verbose=2 "$dmg"
    signature="$(codesign --display --verbose=4 "$dmg" 2>&1)"
    printf '%s\n' "$signature" | grep -Fx "Identifier=$(config_value BOARDCAST_BUNDLE_IDENTIFIER).dmg" >/dev/null || fail "Unexpected DMG signing identifier."
    printf '%s\n' "$signature" | grep -Fx "TeamIdentifier=$team" >/dev/null || fail "Unexpected DMG signing team."
    printf '%s\n' "$signature" | grep -F "Authority=Developer ID Application:" >/dev/null || fail "Missing DMG Developer ID signature."
    printf '%s\n' "$signature" | grep -E '^Timestamp=' >/dev/null || fail "Missing DMG secure timestamp."
}
verify_notarized_dmg() {
    verify_dmg_signature "$1" "$2"
    xcrun stapler validate "$1"
    spctl --assess --type open --context context:primary-signature --verbose=2 "$1"
}
make_dmg() {
    local app="$1" dmg="$2" staging="$3"
    [[ ! -e "$dmg" && ! -L "$dmg" ]] || fail "Refusing to replace an existing DMG."
    [[ ! -e "$staging" && ! -L "$staging" ]] || fail "The image staging directory must be new."
    mkdir -p "$staging"
    ditto "$app" "$staging/Boardcast.app"
    cat >"$staging/Install.txt" <<'INSTALL'
Boardcast for macOS (Apple Silicon + Intel)
Drag Boardcast.app into your own ~/Applications directory in Finder.
Create that directory if needed, then launch the copied app there.
You may use /Applications when your account can write there.
Do not run the app from this disk image or Downloads; a read-only/translocated app cannot update itself.
This image contains one app, including its signed BoardcastUpdater helper.

请使用 Finder 将 Boardcast.app 拖入当前用户的 ~/Applications。
目录不存在时可先创建；复制完成后从该目录启动。
仅在当前账户可写时选用 /Applications。
不要从磁盘镜像或下载目录直接运行，否则只读/路径转移状态可能阻止更新。
镜像仅包含一个应用，内含签名的 BoardcastUpdater 更新程序。
INSTALL
    hdiutil create -volname Boardcast -srcfolder "$staging" -format UDZO \
        -imagekey zlib-level=9 "$dmg"
}
