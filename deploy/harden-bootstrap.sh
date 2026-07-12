#!/usr/bin/env bash
set -euo pipefail

stamp="$(date +%Y%m%d%H%M%S)"
env_file=/etc/susurri/bootstrap.env
sshd_dropin=/etc/ssh/sshd_config.d/50-susurri-hardening.conf
sysctl_dropin=/etc/sysctl.d/90-susurri-hardening.conf
journald_dropin=/etc/systemd/journald.conf.d/50-susurri.conf
fail2ban_jail=/etc/fail2ban/jail.d/50-susurri-sshd.conf
autoupgrade_conf=/etc/apt/apt.conf.d/20auto-upgrades
fp_file=/var/lib/susurri/.config/Susurri/fingerprint.txt
attest_url=http://127.0.0.1:7071/attest
attest_copy=/etc/susurri/attestation.json
audit_file=/etc/susurri/hardening-audit.sha256

section() { printf '\n== %s\n' "$*"; }

backup_and_write() {
  local dest="$1" tmp
  tmp="$(mktemp)"
  cat > "$tmp"
  if [ -f "$dest" ] && cmp -s "$tmp" "$dest"; then
    rm -f "$tmp"
    echo "$dest unchanged"
    return 0
  fi
  if [ -f "$dest" ]; then
    cp -a "$dest" "${dest}.bak.${stamp}"
    echo "backed up $dest -> ${dest}.bak.${stamp}"
  fi
  install -m 644 "$tmp" "$dest"
  rm -f "$tmp"
  echo "wrote $dest"
}

ensure_trailing_newline() {
  [ -s "$env_file" ] && [ -n "$(tail -c1 "$env_file")" ] && echo >> "$env_file"
  return 0
}

ensure_env() {
  local key="$1" value="$2"
  ensure_trailing_newline
  if grep -q "^${key}=" "$env_file"; then
    echo "$key already present, keeping existing value"
  else
    printf '%s=%s\n' "$key" "$value" >> "$env_file"
    echo "appended $key"
  fi
}

unit_present() {
  systemctl list-unit-files "$1" 2>/dev/null | grep -q "^$1"
}

section "1/10 preconditions"
if [ "$(id -u)" -ne 0 ]; then
  echo "run as root" >&2
  exit 1
fi
if ! command -v apt-get >/dev/null 2>&1; then
  echo "apt-get not found; this script targets debian/ubuntu" >&2
  exit 1
fi
have_susurri_user=0
if id susurri >/dev/null 2>&1; then
  have_susurri_user=1
else
  echo "warning: susurri user missing — run deploy/setup-vps.sh first; continuing, but env-file group ownership will fall back to root"
fi
[ -f /etc/systemd/system/susurri-bootstrap.service ] || echo "warning: susurri-bootstrap.service not installed — fingerprint capture (step 8) will be skipped until setup-vps.sh and the first deploy have run"
install -d -m 755 /etc/susurri
if [ ! -f "$env_file" ]; then
  printf 'DHT__Bootstrap__PublicAddress=\n' > "$env_file"
  echo "created $env_file (set DHT__Bootstrap__PublicAddress=<public-ip>:7070 before going live)"
fi

section "2/10 os package hardening"
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends unattended-upgrades fail2ban openssl ca-certificates curl ufw
backup_and_write "$autoupgrade_conf" <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF
if unit_present unattended-upgrades.service; then
  systemctl enable --now unattended-upgrades
  echo "unattended-upgrades enabled (automatic security updates)"
fi
install -d -m 755 /etc/fail2ban/jail.d
backup_and_write "$fail2ban_jail" <<'EOF'
[sshd]
enabled = true
backend = systemd
maxretry = 4
findtime = 10m
bantime = 1h
EOF
if unit_present fail2ban.service; then
  systemctl enable fail2ban
  systemctl restart fail2ban
  echo "fail2ban sshd jail active (backend=systemd, works with volatile journald)"
fi
for unit in avahi-daemon.service avahi-daemon.socket cups.service cups.socket cups-browsed.service; do
  if systemctl list-unit-files | grep -q "^${unit}"; then
    systemctl disable --now "$unit" 2>/dev/null || true
    echo "disabled $unit"
  fi
done

section "3/10 ssh hardening"
echo "note: this disables password login — it assumes key-based auth is already working (setup-vps.sh seeds authorized_keys); verify you can log in with a key before closing this session"
install -d -m 755 /etc/ssh/sshd_config.d
if ! grep -Eq '^[Ii]nclude[[:space:]]+/etc/ssh/sshd_config\.d' /etc/ssh/sshd_config; then
  echo "warning: /etc/ssh/sshd_config has no Include for sshd_config.d — the drop-in below will NOT take effect; add 'Include /etc/ssh/sshd_config.d/*.conf' manually"
fi
for f in /etc/ssh/sshd_config.d/*.conf; do
  [ -e "$f" ] || continue
  [ "$f" = "$sshd_dropin" ] && continue
  if [[ "$(basename "$f")" < "$(basename "$sshd_dropin")" ]] && grep -Eqi '^[[:space:]]*PasswordAuthentication[[:space:]]+yes' "$f"; then
    echo "warning: $f sets PasswordAuthentication yes and sorts before our drop-in — sshd honours the FIRST match, so password auth stays ON until you remove that line/file"
  fi
done
sshd_prev=""
sshd_existed=0
if [ -f "$sshd_dropin" ]; then
  sshd_existed=1
  sshd_prev="$(mktemp)"
  cp -a "$sshd_dropin" "$sshd_prev"
fi
sshd_tmp="$(mktemp)"
cat > "$sshd_tmp" <<'EOF'
Protocol 2
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
X11Forwarding no
AllowAgentForwarding no
AllowTcpForwarding no
MaxAuthTries 3
LoginGraceTime 20
Ciphers chacha20-poly1305@openssh.com,aes256-gcm@openssh.com,aes128-gcm@openssh.com,aes256-ctr
KexAlgorithms curve25519-sha256,curve25519-sha256@libssh.org,diffie-hellman-group16-sha512,diffie-hellman-group18-sha512
MACs hmac-sha2-512-etm@openssh.com,hmac-sha2-256-etm@openssh.com
EOF
sshd_changed=1
if [ "$sshd_existed" = 1 ] && cmp -s "$sshd_tmp" "$sshd_dropin"; then
  sshd_changed=0
  echo "$sshd_dropin unchanged"
else
  [ "$sshd_existed" = 1 ] && cp -a "$sshd_dropin" "${sshd_dropin}.bak.${stamp}"
  install -m 644 "$sshd_tmp" "$sshd_dropin"
  echo "wrote $sshd_dropin"
fi
rm -f "$sshd_tmp"
sshd_bin="$(command -v sshd || echo /usr/sbin/sshd)"
if ! "$sshd_bin" -t; then
  if [ "$sshd_existed" = 1 ]; then
    cp -a "$sshd_prev" "$sshd_dropin"
  else
    rm -f "$sshd_dropin"
  fi
  [ -n "$sshd_prev" ] && rm -f "$sshd_prev"
  echo "sshd config validation FAILED — drop-in reverted, ssh untouched, aborting; inspect 'sshd -t' output above" >&2
  exit 1
fi
[ -n "$sshd_prev" ] && rm -f "$sshd_prev"
if [ "$sshd_changed" = 1 ]; then
  systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || systemctl restart ssh 2>/dev/null || systemctl restart sshd
  echo "sshd config validated and reloaded — keep this session open and confirm a fresh key login works"
fi

section "4/10 kernel/network sysctl hardening"
backup_and_write "$sysctl_dropin" <<'EOF'
net.ipv4.conf.all.rp_filter = 1
net.ipv4.conf.default.rp_filter = 1
net.ipv4.tcp_syncookies = 1
net.ipv4.conf.all.accept_redirects = 0
net.ipv4.conf.default.accept_redirects = 0
net.ipv4.conf.all.secure_redirects = 0
net.ipv4.conf.default.secure_redirects = 0
net.ipv4.conf.all.send_redirects = 0
net.ipv4.conf.default.send_redirects = 0
net.ipv4.conf.all.accept_source_route = 0
net.ipv4.conf.default.accept_source_route = 0
net.ipv4.conf.all.log_martians = 1
net.ipv4.conf.default.log_martians = 1
net.ipv6.conf.all.accept_redirects = 0
net.ipv6.conf.default.accept_redirects = 0
net.ipv6.conf.all.accept_source_route = 0
net.ipv6.conf.default.accept_source_route = 0
net.ipv6.conf.all.accept_ra = 0
net.ipv6.conf.default.accept_ra = 0
kernel.kptr_restrict = 2
kernel.dmesg_restrict = 1
kernel.unprivileged_bpf_disabled = 1
net.core.bpf_jit_harden = 2
kernel.yama.ptrace_scope = 1
EOF
if sysctl --system >/dev/null 2>&1; then
  echo "sysctl settings applied"
else
  echo "warning: some sysctl keys were rejected (older kernel?) — the rest applied; check 'sysctl --system' output manually"
fi

section "5/10 firewall (ufw)"
ufw default deny incoming
ufw default allow outgoing
ufw allow OpenSSH
ufw allow 7070/tcp
ufw allow 7070/udp
ufw --force enable
echo "inbound: ssh + 7070 (dht) only; health/attest port 7071 stays loopback-only and is NOT opened"

section "6/10 noLogs / privacy logging"
install -d -m 755 /etc/systemd/journald.conf.d
backup_and_write "$journald_dropin" <<'EOF'
[Journal]
Storage=volatile
ForwardToSyslog=no
MaxRetentionSec=0
EOF
systemctl restart systemd-journald 2>/dev/null || systemctl force-reload systemd-journald 2>/dev/null || true
echo "journald is RAM-only: peer ips that reach unit/kernel logs vanish on reboot and never touch disk"
if [ -d /var/log/journal ]; then
  rm -rf /var/log/journal
  echo "removed previously persisted journals under /var/log/journal"
fi
if unit_present rsyslog.service; then
  systemctl disable --now rsyslog 2>/dev/null || true
  echo "disabled rsyslog (would re-persist journal content to /var/log); pre-existing /var/log/{syslog,auth.log}* left for you to review and remove"
fi
ensure_env Logging__RedactNetworkIdentifiers true
ensure_env Logging__LogLevel__Default Warning
echo "susurri's own logs now scrub ip/endpoint strings and only emit warnings and above"

section "7/10 stable node identity"
if grep -q '^DHT__Bootstrap__IdentitySeed=' "$env_file"; then
  echo "DHT__Bootstrap__IdentitySeed already present, keeping it"
else
  ensure_trailing_newline
  seed="$(openssl rand -hex 32)"
  printf 'DHT__Bootstrap__IdentitySeed=%s\n' "$seed" >> "$env_file"
  unset seed
  echo "generated and appended DHT__Bootstrap__IdentitySeed (64 hex chars)"
fi
chmod 640 "$env_file"
if [ "$have_susurri_user" = 1 ]; then
  chown root:susurri "$env_file"
else
  chown root:root "$env_file"
fi
echo "WARNING: the seed in $env_file IS this node's identity — back it up offline and treat it like a private key; losing it or rotating it changes the node id and invalidates every client pin"

section "8/10 fingerprint capture & publish"
ensure_env Health__Enabled true
ensure_env Health__ListenAddress 127.0.0.1
have_curl=0; command -v curl >/dev/null 2>&1 && have_curl=1
have_jq=0; command -v jq >/dev/null 2>&1 && have_jq=1
if [ -f /etc/systemd/system/susurri-bootstrap.service ] && [ -x /opt/susurri/current/susurri-cli ]; then
  systemctl restart susurri-bootstrap
  echo "restarted susurri-bootstrap, waiting up to 30s for the node to publish its fingerprint"
  found=0
  for _ in $(seq 1 30); do
    if [ -s "$fp_file" ]; then found=1; break; fi
    if [ "$have_curl" = 1 ] && curl -fsS "$attest_url" >/dev/null 2>&1; then found=1; break; fi
    sleep 1
  done
  if [ "$found" = 1 ]; then
    fingerprint=""
    [ -s "$fp_file" ] && fingerprint="$(head -n1 "$fp_file" | tr -d '[:space:]')"
    signing_key="<signingPublicKeyHex>"
    if [ "$have_curl" = 1 ] && curl -fsS "$attest_url" -o "${attest_copy}.tmp" 2>/dev/null; then
      mv "${attest_copy}.tmp" "$attest_copy"
      chmod 644 "$attest_copy"
      echo "attestation json copied to $attest_copy"
      if [ "$have_jq" = 1 ]; then
        [ -z "$fingerprint" ] && fingerprint="$(jq -r '.fingerprint // empty' "$attest_copy")"
        signing_key="$(jq -r '.signingPublicKey // empty' "$attest_copy")"
        [ -z "$signing_key" ] && signing_key="<signingPublicKeyHex>"
        echo "node id:            $(jq -r '.nodeId // "n/a"' "$attest_copy")"
        echo "signing public key: $signing_key"
        echo "version:            $(jq -r '.version // "n/a"' "$attest_copy")"
      else
        [ -z "$fingerprint" ] && fingerprint="$(sed -n 's/.*"fingerprint"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$attest_copy" | head -n1)"
        sk="$(sed -n 's/.*"signingPublicKey"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$attest_copy" | head -n1)"
        [ -n "$sk" ] && signing_key="$sk"
        echo "jq not installed — raw attestation follows:"
        cat "$attest_copy"; echo
      fi
    else
      rm -f "${attest_copy}.tmp"
      echo "attest endpoint not reachable on $attest_url — using $fp_file only; $attest_copy not written"
    fi
    pub_addr="$(sed -n 's/^DHT__Bootstrap__PublicAddress=//p' "$env_file" | head -n1)"
    [ -n "$pub_addr" ] || pub_addr="<public-ip>:7070"
    echo
    echo "fingerprint (full):  ${fingerprint:-<not captured>}"
    echo "fingerprint (short): ${fingerprint:0:16}"
    echo "public address:      $pub_addr"
    echo
    echo "ACTION REQUIRED — publish this fingerprint out-of-band (website, signed release notes,"
    echo "keybase-style proofs — anywhere an attacker who owns this vps cannot also edit) and add"
    echo "it to the client's pinned registry:"
    echo "  file:  src/Bootstrapper/Susurri.CLI/Network/BootstrapRegistry.cs"
    echo "  entry: {\"$pub_addr\", \"${fingerprint:-<fingerprint>}\", \"$signing_key\"}"
  else
    echo "warning: fingerprint did not appear within 30s ($fp_file empty, $attest_url unreachable)"
    echo "check 'systemctl status susurri-bootstrap' and 'journalctl -u susurri-bootstrap', then re-run this script"
  fi
else
  echo "susurri-bootstrap not deployed yet (unit or /opt/susurri/current/susurri-cli missing) — skipping"
  echo "re-run this script after the first deploy to capture and publish the fingerprint"
fi

section "9/10 os-hardening audit hash"
{
  cat "$sshd_dropin" 2>/dev/null
  cat "$sysctl_dropin" 2>/dev/null
  cat "$journald_dropin" 2>/dev/null
  ufw status verbose 2>/dev/null
  sed 's/^DHT__Bootstrap__IdentitySeed=.*/DHT__Bootstrap__IdentitySeed=<redacted>/' "$env_file"
} | sha256sum | awk '{print $1}' > "$audit_file"
chmod 644 "$audit_file"
echo "audit hash: $(cat "$audit_file")"
echo "stored in $audit_file — record it somewhere off-box; re-run this script later and compare to detect drift in the os hardening config"
echo "note: this is drift detection for YOU the operator; it is not remote attestation and proves nothing to anyone else"

section "10/10 summary"
echo "changed on this host:"
echo "  - unattended security upgrades on, fail2ban sshd jail on, avahi/cups off if present"
echo "  - ssh: key-only, no root login, no forwarding, modern crypto ($sshd_dropin)"
echo "  - kernel/network sysctls hardened ($sysctl_dropin)"
echo "  - ufw: deny in, allow ssh + 7070 tcp/udp; 7071 loopback-only"
echo "  - journald volatile (no logs persisted to disk), rsyslog off, susurri log redaction + Warning level"
echo "  - stable identity seed + health/attest enabled in $env_file"
echo "  - audit hash in $audit_file"
echo
echo "you still must:"
echo "  1. back up the DHT__Bootstrap__IdentitySeed line from $env_file offline"
echo "  2. publish the fingerprint out-of-band and pin it in BootstrapRegistry.cs"
echo "  3. verify key-based ssh login from a second session BEFORE logging out"
echo "  4. record the audit hash off-box"
echo
echo "honest caveat: this hardens the box and stops logs hitting disk, but the kernel still"
echo "routes packets, your hosting provider still sees traffic flows, and a malicious operator"
echo "of this node could run modified code that reports the expected fingerprint — see"
echo "deploy/HARDENING.md and the threat model in README.md"
