#!/usr/bin/env bash
set -euo pipefail

KEYSTORE="${1:-susurri-release.keystore}"
ALIAS="${2:-susurri}"

if [ -e "$KEYSTORE" ]; then
  echo "refusing to overwrite existing $KEYSTORE" >&2
  exit 1
fi

read -r -s -p "keystore password (8+ chars, used for both store and key): " PASS
echo

keytool -genkeypair -v \
  -keystore "$KEYSTORE" \
  -storetype PKCS12 \
  -alias "$ALIAS" \
  -keyalg RSA -keysize 4096 \
  -validity 10950 \
  -storepass "$PASS" \
  -dname "CN=Susurri"

echo
echo "keystore written to $KEYSTORE — back it up offline; losing it means users must uninstall to update."
echo
echo "set the GitHub secrets (Settings → Secrets and variables → Actions), then shred the local file:"
echo
echo "  gh secret set ANDROID_KEYSTORE_BASE64 --body \"\$(base64 -w0 $KEYSTORE)\""
echo "  gh secret set ANDROID_KEYSTORE_PASS   --body '<the password>'"
echo "  gh secret set ANDROID_KEY_PASS        --body '<the password>'"
echo "  gh secret set ANDROID_KEY_ALIAS       --body '$ALIAS'"
echo
echo "  shred -u $KEYSTORE"
