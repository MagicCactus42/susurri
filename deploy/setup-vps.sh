#!/usr/bin/env bash
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo "run as root" >&2
  exit 1
fi

apt-get update
apt-get install -y --no-install-recommends ca-certificates curl ufw
apt-get install -y --no-install-recommends '^libicu[0-9]+$' || apt-get install -y --no-install-recommends libicu-dev

id susurri >/dev/null 2>&1 || useradd --system --create-home --shell /bin/bash susurri

install -d -m 755 -o susurri -g susurri /opt/susurri /opt/susurri/releases /opt/susurri/incoming
install -d -m 700 -o susurri -g susurri /home/susurri/.ssh
touch /home/susurri/.ssh/authorized_keys
chmod 600 /home/susurri/.ssh/authorized_keys
chown susurri:susurri /home/susurri/.ssh/authorized_keys

install -d -m 755 /etc/susurri
if [ ! -f /etc/susurri/bootstrap.env ]; then
  printf 'DHT__Bootstrap__PublicAddress=\n' > /etc/susurri/bootstrap.env
  chmod 640 /etc/susurri/bootstrap.env
  chown root:susurri /etc/susurri/bootstrap.env
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install -m 644 "$script_dir/susurri-bootstrap.service" /etc/systemd/system/susurri-bootstrap.service

printf 'susurri ALL=(root) NOPASSWD: /usr/bin/systemctl restart susurri-bootstrap\n' > /etc/sudoers.d/susurri-deploy
chmod 440 /etc/sudoers.d/susurri-deploy

systemctl daemon-reload
systemctl enable susurri-bootstrap

ufw allow OpenSSH
ufw allow 7070/tcp
ufw allow 7070/udp
ufw --force enable

echo "setup complete"
echo "1. append the github deploy public key to /home/susurri/.ssh/authorized_keys"
echo "2. set DHT__Bootstrap__PublicAddress=<public-ip>:7070 in /etc/susurri/bootstrap.env"
echo "3. push to main (or run the deploy-bootstrap workflow) to start the first release"
