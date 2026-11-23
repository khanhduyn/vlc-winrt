#!/usr/bin/env bash
set -euo pipefail

XBOX_IP="${1:-192.168.1.68}"
PORT="11443"
BASE_URL="https://${XBOX_IP}:${PORT}"
PACKAGES_DIR="${2:-./AppPackages}"

# Optional credentials: export XBOX_USER=... XBOX_PASS=...
AUTH_ARGS=()
if [[ -n "${XBOX_USER:-}" && -n "${XBOX_PASS:-}" ]]; then
  AUTH_ARGS=(-u "${XBOX_USER}:${XBOX_PASS}")
fi

BUNDLE=$(find "$PACKAGES_DIR" -maxdepth 3 \( -name "*.appxbundle" -o -name "*.msixbundle" \) | head -n 1)
if [[ -z "$BUNDLE" ]]; then
  echo "Error: No .appxbundle or .msixbundle found in $PACKAGES_DIR" >&2
  exit 1
fi

BUNDLE_NAME=$(basename "$BUNDLE")
BUNDLE_DIR=$(dirname "$BUNDLE")
echo "Deploying $BUNDLE_NAME to $BASE_URL..."

FORM_ARGS=(-F "${BUNDLE_NAME}=@${BUNDLE}")

DEPS_DIR="${BUNDLE_DIR}/Dependencies/x64"
if [[ -d "$DEPS_DIR" ]]; then
  while IFS= read -r -d '' dep; do
    dep_name=$(basename "$dep")
    FORM_ARGS+=(-F "${dep_name}=@${dep}")
  done < <(find "$DEPS_DIR" -maxdepth 1 -type f \( -name "*.appx" -o -name "*.msix" \) -print0)
fi

curl -k -sS "${AUTH_ARGS[@]}" -X POST \
  "${BASE_URL}/api/app/packagemanager/package?package=${BUNDLE_NAME}" \
  "${FORM_ARGS[@]}"

echo ""
echo "Polling deployment state..."
for _ in $(seq 1 30); do
  STATE=$(curl -k -sS "${AUTH_ARGS[@]}" "${BASE_URL}/api/app/packagemanager/state")
  echo "$STATE"
  if echo "$STATE" | grep -qE '"Success"\s*:\s*true'; then
    echo "Deployment succeeded."
    exit 0
  fi
  if echo "$STATE" | grep -qE '"Success"\s*:\s*false'; then
    echo "Deployment failed." >&2
    exit 1
  fi
  sleep 2
done

echo "Timed out waiting for deployment state." >&2
exit 1
