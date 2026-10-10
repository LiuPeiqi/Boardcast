#!/bin/bash
# Explicit local export only: no git add, git push or hosting upload.
set -euo pipefail
source "$(dirname "$0")/common.sh"
[[ $# == 1 ]] || fail "Usage: DEVELOPMENT_TEAM=TEAMID bash scripts/publish.sh /absolute/path/Boardcast-macOS-universal-version-build.dmg"
: "${DEVELOPMENT_TEAM:?Set the expected Apple Developer Team ID.}"
[[ "$DEVELOPMENT_TEAM" =~ ^[A-Z0-9]{10}$ ]] || fail "Invalid Apple Developer Team ID."
preflight
[[ -f "$1" && ! -L "$1" ]] || fail "Select a real notarized release DMG."
source_dmg="$(cd "$(dirname "$1")" && pwd -P)/$(basename "$1")"
dmg="$source_dmg"
name="$(basename "$dmg")"
[[ "$name" =~ ^Boardcast-macOS-universal-[0-9]+\.[0-9]+\.[0-9]+-[0-9]+\.dmg$ ]] || fail "Select a final universal DMG, not a prepare artifact."
[[ -f "$dmg.sha256" && ! -L "$dmg.sha256" ]] || fail "The final release SHA-256 sidecar is missing."
expected_hash="$(awk -v name="$name" '$2 == name { print $1 }' "$dmg.sha256")"
[[ "$expected_hash" =~ ^[a-fA-F0-9]{64}$ ]] || fail "The SHA-256 sidecar must contain exactly one matching checksum."
expected_hash="$(printf '%s' "$expected_hash" | tr 'A-F' 'a-f')"
# Validate a private copy; the selected input may be on a mutable volume.
run="$(new_run_directory publish)"
dmg="$run/$name"
ditto "$source_dmg" "$dmg"
actual_hash="$(shasum -a 256 "$dmg" | awk '{ print $1 }')"
[[ "$actual_hash" == "$expected_hash" ]] || fail "The DMG checksum does not match."
verify_notarized_dmg "$dmg" "$DEVELOPMENT_TEAM"
mountpoint="$run/Mounted"
mkdir -p "$mountpoint"
mounted=0
dmg_temp=""
hash_temp=""
destination=""
published_dmg=0
published_pair=0
cleanup() {
    if [[ "$mounted" == 1 ]]; then
        hdiutil detach "$mountpoint" >/dev/null || true
    fi
    # Only remove our own new hard link if the checksum link could not be created.
    if [[ "$published_dmg" == 1 && "$published_pair" == 0 && -f "$dmg_temp" && "$destination" -ef "$dmg_temp" ]]; then
        rm -f "$destination"
    fi
    [[ -z "$dmg_temp" ]] || rm -f "$dmg_temp"
    [[ -z "$hash_temp" ]] || rm -f "$hash_temp"
}
trap cleanup EXIT
hdiutil attach -readonly -nobrowse -noautoopen -mountpoint "$mountpoint" "$dmg"
mounted=1
app="$mountpoint/Boardcast.app"
verify_app "$app"
bundle_id="$(config_value BOARDCAST_BUNDLE_IDENTIFIER)"
verify_developer_signature "$app" "$bundle_id" "$DEVELOPMENT_TEAM" YES
verify_developer_signature "$app/Contents/MacOS/BoardcastUpdater" "$bundle_id.updater" "$DEVELOPMENT_TEAM" YES
xcrun stapler validate "$app"
spctl --assess --type execute --verbose=2 "$app"
version="$(plutil -extract CFBundleShortVersionString raw -o - "$app/Contents/Info.plist")"
build="$(plutil -extract CFBundleVersion raw -o - "$app/Contents/Info.plist")"
[[ "$name" == "Boardcast-macOS-universal-$version-$build.dmg" ]] || fail "The DMG filename does not match the embedded app version."
hdiutil detach "$mountpoint"
mounted=0

repo_root="$(cd "$MAC_ROOT/../.." && pwd -P)"
destination_dir="$repo_root/dist/macos"
[[ ! -L "$repo_root/dist" && ! -L "$destination_dir" ]] || fail "The Git distribution directories must not be symlinks."
mkdir -p "$destination_dir"
destination="$destination_dir/$name"
[[ ! -e "$destination" && ! -L "$destination" && ! -e "$destination.sha256" && ! -L "$destination.sha256" ]] || fail "Refusing to overwrite an existing release or checksum."
# Prepare a complete verified copy on the destination filesystem before publishing.
dmg_temp="$(mktemp "$destination_dir/.boardcast-dmg.XXXXXXXX")"
hash_temp="$(mktemp "$destination_dir/.boardcast-sha256.XXXXXXXX")"
ditto "$dmg" "$dmg_temp"
[[ "$(shasum -a 256 "$dmg_temp" | awk '{ print $1 }')" == "$actual_hash" ]] || fail "The exported copy failed its checksum check."
printf '%s  %s\n' "$actual_hash" "$name" >"$hash_temp"
chmod 644 "$dmg_temp" "$hash_temp"
# Hard links publish complete files atomically and refuse an existing destination.
ln "$dmg_temp" "$destination"
published_dmg=1
ln "$hash_temp" "$destination.sha256"
published_pair=1
printf 'Exported validated release: %s\nChecksum: %s.sha256\n' "$destination" "$destination"
printf 'Review and publish through Git LFS explicitly. This script does not commit or upload files.\n'
