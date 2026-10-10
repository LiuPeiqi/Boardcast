#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
[[ $# == 0 ]] || fail "Usage: bash scripts/build.sh"
preflight
run="$(new_run_directory local)"
test_project "$run"
xcodebuild -project "$PROJECT" -scheme "$SCHEME" \
    -configuration Release -destination "generic/platform=macOS" \
    -derivedDataPath "$run/DerivedData" \
    -disableAutomaticPackageResolution -skipPackageUpdates \
    CODE_SIGN_IDENTITY=- build

version="$(config_value MARKETING_VERSION)"
build="$(config_value CURRENT_PROJECT_VERSION)"
output="$MAC_ROOT/dist/local/Boardcast-$version-$build-$(basename "$run")"
mkdir -p "$output"
ditto "$run/DerivedData/Build/Products/Release/Boardcast.app" "$output/Boardcast.app"
verify_app "$output/Boardcast.app"
ditto -c -k --sequesterRsrc --keepParent "$output/Boardcast.app" "$output/Boardcast-macOS-local.zip"
printf 'Local ad-hoc signed app: %s\nTest results: %s\n' "$output/Boardcast.app" "$run/Tests.xcresult"
printf 'This local build has not been notarized. Developer ID distribution uses release.sh.\n'
