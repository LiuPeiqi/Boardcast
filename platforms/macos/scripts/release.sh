#!/bin/bash
# --prepare does not upload software; secure Developer ID timestamps may contact Apple.
set -euo pipefail
source "$(dirname "$0")/common.sh"
case "${1:-}" in
    --prepare|--notarize) mode="$1" ;;
    *) printf 'Usage: DEVELOPER_ID_APPLICATION="Developer ID Application: Name (TEAMID)" DEVELOPMENT_TEAM=TEAMID bash scripts/release.sh --prepare|--notarize\nNOTARYTOOL_PROFILE is required only for --notarize.\n' >&2; exit 1 ;;
esac
[[ $# == 1 ]] || fail "Provide exactly one release mode."
: "${DEVELOPER_ID_APPLICATION:?Set the exact Developer ID Application identity in your login keychain.}"
: "${DEVELOPMENT_TEAM:?Set your ten-character Apple Developer Team ID.}"
if [[ "$mode" == "--notarize" ]]; then
    : "${NOTARYTOOL_PROFILE:?Set a notarytool keychain profile; do not put passwords in source files.}"
fi
[[ "$DEVELOPMENT_TEAM" =~ ^[A-Z0-9]{10}$ ]] || fail "Invalid Apple Developer Team ID."
[[ "$DEVELOPER_ID_APPLICATION" == "Developer ID Application: "* && "$DEVELOPER_ID_APPLICATION" == *" ($DEVELOPMENT_TEAM)" ]] || fail "The identity must be Developer ID Application for the selected team."
preflight
security find-identity -v -p codesigning | grep -F "\"$DEVELOPER_ID_APPLICATION\"" >/dev/null || fail "The requested Developer ID identity is not valid in the keychain."
run="$(new_run_directory release)"
test_project "$run"
xcodebuild -project "$PROJECT" -scheme "$SCHEME" \
    -configuration Release -destination "generic/platform=macOS" \
    -derivedDataPath "$run/DerivedData" -archivePath "$run/Boardcast.xcarchive" \
    -disableAutomaticPackageResolution -skipPackageUpdates \
    "CODE_SIGN_IDENTITY=$DEVELOPER_ID_APPLICATION" "DEVELOPMENT_TEAM=$DEVELOPMENT_TEAM" \
    BOARDCAST_SIGNING_FLAGS=--timestamp CODE_SIGN_INJECT_BASE_ENTITLEMENTS=NO \
    ENABLE_HARDENED_RUNTIME=YES archive

version="$(config_value MARKETING_VERSION)"
build="$(config_value CURRENT_PROJECT_VERSION)"
bundle_id="$(config_value BOARDCAST_BUNDLE_IDENTIFIER)"
output="$MAC_ROOT/dist/release/Boardcast-$version-$build-$(basename "$run")"
app="$output/Boardcast.app"
mkdir -p "$output"
ditto "$run/Boardcast.xcarchive/Products/Applications/Boardcast.app" "$app"
verify_app "$app"
verify_developer_signature "$app" "$bundle_id" "$DEVELOPMENT_TEAM" YES
verify_developer_signature "$app/Contents/MacOS/BoardcastUpdater" "$bundle_id.updater" "$DEVELOPMENT_TEAM" YES

notarize_file() {
    local file="$1" label="$2" result status submission_id submit_status=0
    result="$run/notarization-$label.json"
    xcrun notarytool submit "$file" --keychain-profile "$NOTARYTOOL_PROFILE" \
        --wait --output-format json >"$result" || submit_status=$?
    [[ -s "$result" ]] || fail "Notarization failed ($submit_status); no response was returned."
    status="$(plutil -extract status raw -o - "$result")"
    submission_id="$(plutil -extract id raw -o - "$result")"
    [[ "$submission_id" =~ ^[a-fA-F0-9-]{36}$ ]] || fail "The notarization response has no valid submission ID."
    xcrun notarytool log "$submission_id" --keychain-profile "$NOTARYTOOL_PROFILE" "$run/notarization-$label-log.json"
    # Apple recommends inspecting the log even when the submission is accepted.
    plutil -p "$run/notarization-$label-log.json"
    [[ "$status" == "Accepted" ]] || fail "Apple returned '$status'. Read $result; no distributable release was produced."
}
if [[ "$mode" == "--prepare" ]]; then
    dmg="$output/Boardcast-macOS-universal-$version-$build-NOT-NOTARIZED.dmg"
    make_dmg "$app" "$dmg" "$run/Image"
    codesign --sign "$DEVELOPER_ID_APPLICATION" --timestamp --identifier "$bundle_id.dmg" "$dmg"
    verify_dmg_signature "$dmg" "$DEVELOPMENT_TEAM"
    printf 'Prepared signed app: %s\nPrepared signed DMG (not notarized): %s\nArchive: %s\n' "$app" "$dmg" "$run/Boardcast.xcarchive"
    printf 'No software was submitted to Apple. Use --notarize for a distributable release.\n'
    exit 0
fi

# The intermediate ZIP is not a release product. Stapling the app before building the
# DMG lets both the installed app and final disk image retain their offline tickets.
submission="$run/Boardcast-app-submission.zip"
ditto -c -k --sequesterRsrc --keepParent "$app" "$submission"
notarize_file "$submission" app
xcrun stapler staple "$app"
xcrun stapler validate "$app"
codesign --verify --deep --strict --all-architectures --verbose=2 "$app"
spctl --assess --type execute --verbose=2 "$app"

dmg="$output/Boardcast-macOS-universal-$version-$build.dmg"
make_dmg "$app" "$dmg" "$run/Image"
codesign --sign "$DEVELOPER_ID_APPLICATION" --timestamp --identifier "$bundle_id.dmg" "$dmg"
verify_dmg_signature "$dmg" "$DEVELOPMENT_TEAM"
notarize_file "$dmg" dmg
xcrun stapler staple "$dmg"
verify_notarized_dmg "$dmg" "$DEVELOPMENT_TEAM"
(
    cd "$output"
    shasum -a 256 "$(basename "$dmg")" >"$(basename "$dmg").sha256"
)
printf 'Notarized universal DMG: %s\nSHA-256: %s.sha256\nArchive, tests and both notarization logs: %s\n' "$dmg" "$dmg" "$run"
printf 'To copy this validated release into the Git distribution directory, explicitly run scripts/publish.sh.\n'
