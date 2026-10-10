#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
preflight
xcodebuild -project "$PROJECT" -list
printf 'Preflight complete. No app was built or uploaded.\n'
