# Bootstrap node hardening

`deploy/harden-bootstrap.sh` hardens a Debian/Ubuntu VPS running the Susurri
bootstrap seed. Run it as root **after** `deploy/setup-vps.sh` and the first
deploy; it is idempotent and safe to re-run (re-running also recomputes the
drift-detection hash, which is the point). Every file it overwrites is backed
up first as `<file>.bak.<timestamp>`.

## What the script does

| Step | Change |
|---|---|
| 1 | Preconditions: root, apt present, warns if `setup-vps.sh` hasn't run |
| 2 | Unattended security upgrades on; fail2ban sshd jail (journald backend); avahi/cups disabled if present |
| 3 | SSH drop-in: key-only auth, no root login, no agent/TCP/X11 forwarding, MaxAuthTries 3, modern ciphers/KEX/MACs — validated with `sshd -t` and reverted on failure so it can never lock you out |
| 4 | Kernel/network sysctls: rp_filter, syncookies, no redirects/source routing, no IPv6 RAs, kptr/dmesg restricted, unprivileged BPF off, ptrace scoped |
| 5 | ufw: deny incoming, allow SSH + 7070/tcp + 7070/udp; the health/attest port 7071 stays loopback-only |
| 6 | noLogs: journald RAM-only, rsyslog off, app-side IP redaction + Warning log level |
| 7 | Stable identity seed in `/etc/susurri/bootstrap.env` |
| 8 | Restarts the node, captures its attestation fingerprint, tells you where to pin it |
| 9 | SHA-256 audit hash over the hardening config for operator drift detection |

## noLogs: what it guarantees, and what it does not

The goal is that **no peer IP address is persisted to this machine's disk**.

What the script does toward that:

- `Storage=volatile` + `MaxRetentionSec=0` + `ForwardToSyslog=no` in journald:
  everything systemd or the kernel logs (unit stdout, martian-packet logs,
  fail2ban's view of sshd) lives in RAM only and is gone on reboot. Previously
  persisted journals under `/var/log/journal` are deleted.
- rsyslog is disabled so nothing re-materialises the journal into
  `/var/log/syslog` or `/var/log/auth.log`. Files written before hardening are
  left in place for you to review and remove.
- `Logging__RedactNetworkIdentifiers=true` makes Susurri's own log output scrub
  IP/endpoint strings, and `Logging__LogLevel__Default=Warning` keeps chatter
  down, so even the in-RAM journal carries little.

What it does **not** and cannot guarantee:

- The kernel still routes packets. Connection state exists in RAM
  (conntrack, socket tables) while the node runs, and anyone with root on the
  live box can observe traffic with tcpdump.
- Your **hosting provider** sees every packet at the hypervisor/network level
  and may keep flow logs. No configuration on the guest changes that.
- A **global passive adversary** watching links is explicitly out of scope —
  same as the client-side threat model ("Threat model, honestly" in
  [README.md](../README.md), details in
  [KNOWN-LIMITATIONS.md](../KNOWN-LIMITATIONS.md)): there is no cover traffic
  yet, so timing correlation is possible regardless of what this box logs.
- The volatile journal is readable while the machine is up; "no logs" means
  "no logs *at rest across reboots*", not "no observable state ever".
- If the VPS has swap, RAM contents can still hit disk. Most minimal VPS
  images ship without swap; if yours has it, disable or encrypt it.

## The stable identity seed

`DHT__Bootstrap__IdentitySeed` in `/etc/susurri/bootstrap.env` is 64 hex
characters (32 bytes) that deterministically derive the node's keypair and
therefore its DHT node ID and its attestation fingerprint. Without it the node
would mint a fresh identity on every restart and could never be pinned.

- **Back it up offline.** It is equivalent to a private key: anyone holding it
  can impersonate this bootstrap node from any IP.
- **Rotating it is an identity change.** The node ID, fingerprint, and signing
  key all change; every client pin in `BootstrapRegistry.cs` for this node
  becomes stale and must be republished. Rotate deliberately, never casually.
- The env file is `root:susurri 640`, and the script never prints the seed.
  The audit hash (below) redacts the seed line before hashing, so the hash
  file leaks nothing.

If the env var is absent the app self-generates and persists a seed under
`/var/lib/susurri`, but the script sets it explicitly so the operator controls
it and can back it up.

## Fingerprint pinning and the attestation model — read this honestly

On startup the bootstrap node computes a fingerprint over its identity and
configuration, writes it to `/var/lib/susurri/.config/Susurri/fingerprint.txt`,
and serves a signed JSON attestation at `http://127.0.0.1:7071/attest`
(loopback only; ufw never opens 7071). The script captures both, copies the
JSON to `/etc/susurri/attestation.json`, and prints the registry entry to add
to `src/Bootstrapper/Susurri.CLI/Network/BootstrapRegistry.cs`.

What pinning that fingerprint actually buys:

- **Impostor resistance.** A client that pins `<ip>:7070` to this fingerprint
  and signing key will reject an attacker who hijacks the IP, poisons DNS, or
  stands up a look-alike seed — the impostor cannot forge the node's Ed25519
  signature over the attestation.
- **Drift detection.** If the deployed binary or identity-relevant config
  changes (a bad deploy, a restored-from-wrong-snapshot box), the fingerprint
  changes and pinned clients notice.

What it does **not** buy — and this is the important part:

- **It is not remote attestation.** The fingerprint is computed and reported
  by the node's own software. An operator who controls the box can run
  modified code that simply reports the expected fingerprint while behaving
  differently. Pinning defends against an impostor *squatting the address*
  and against *accidental drift*; it does **not** defend against a malicious
  operator of the pinned node itself.
- Genuine remote attestation needs **reproducible builds** (so anyone can map
  a fingerprint to exact source) plus a **hardware root of trust**
  (TPM/TEE-measured boot) so the report can't be forged by the host OS. Both
  are future work — reproducible builds are tracked as Phase 5.4 in
  [KNOWN-LIMITATIONS.md](../KNOWN-LIMITATIONS.md).

This is consistent with the existing threat model: bootstrap seeds are a
trust-on-first-use entry point, they see no message content, and the standing
advice is to **run several, operated by different people**. Publish the
fingerprint out-of-band — somewhere an attacker who compromises the VPS cannot
also edit — so clients can pin before first contact.

## The audit hash

Step 9 hashes the concatenation of the sshd drop-in, the sysctl drop-in, the
journald drop-in, the live `ufw status verbose` output, and the env file with
the seed line redacted, and stores it in `/etc/susurri/hardening-audit.sha256`.

Record the hash off-box. Re-running the script recomputes it; a changed hash
means the OS hardening config drifted since you last looked. That is the
entire guarantee: it is a tripwire for **you, the operator**. It is not remote
attestation, it proves nothing to clients, and root on the box can obviously
recompute it after tampering — pair it with off-box records and, ideally,
comparison from a trusted machine.

## Operator checklist after running

1. From a **second** terminal, confirm key-based SSH login still works before
   closing the session you ran the script in.
2. Back up the `DHT__Bootstrap__IdentitySeed` line offline.
3. Publish the fingerprint out-of-band and add the printed entry to
   `BootstrapRegistry.cs`.
4. Record `/etc/susurri/hardening-audit.sha256` off-box.
5. Set `DHT__Bootstrap__PublicAddress=<public-ip>:7070` in
   `/etc/susurri/bootstrap.env` if you haven't already (setup-vps.sh step 2).
