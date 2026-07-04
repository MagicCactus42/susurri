#!/usr/bin/env bash
set -euo pipefail

sha="${1:?usage: activate-release.sh <sha>}"
base=/opt/susurri
release="$base/releases/$sha"
archive="$base/incoming/susurri-bootstrap-$sha.tar.gz"

previous="$(readlink -f "$base/current" 2>/dev/null || true)"

rm -rf "$release"
mkdir -p "$release"
tar -xzf "$archive" -C "$release"
rm -f "$archive"
chmod +x "$release/susurri-cli"

ln -sfn "$release" "$base/current"
sudo systemctl restart susurri-bootstrap

for _ in $(seq 1 30); do
  sleep 2
  if curl -fsS http://127.0.0.1:7071/ready >/dev/null 2>&1; then
    ls -1dt "$base"/releases/*/ 2>/dev/null | tail -n +6 | xargs -r rm -rf
    echo "deploy ok: $sha"
    exit 0
  fi
done

echo "health check failed for $sha" >&2
if [ -n "$previous" ] && [ -d "$previous" ] && [ "$previous" != "$release" ]; then
  ln -sfn "$previous" "$base/current"
  sudo systemctl restart susurri-bootstrap
  echo "rolled back to $previous" >&2
fi
exit 1
