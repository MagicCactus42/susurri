# Bug & Hardening Roadmap

> **INTERNAL / SENSITIVE.** This file enumerates unpatched weaknesses with exact
> file:line locations and exploitation notes. Both original HIGH items are now
> fixed (BUG-H2 partially — see the residual below); the remaining open items are
> lower-severity residuals. Once BUG-H2's residual is closed this can be folded
> into `KNOWN-LIMITATIONS.md` / issues.

Findings from the 2026-07-11 maturity audit, re-verified against source on
**2026-08-22**. Fixed items are collapsed to one-liners with the enforcing code;
open items keep the full template (what / where / impact / fix).

---

## STILL OPEN

### BUG-H2 (residual) — UDP relay registration and delivery origin are unauthenticated
- **Fixed since the audit:** per-IP rate limiting now guards all three relay frames
  before handling (`Network/UdpEndpoint.cs` `DispatchDatagramAsync`, via
  `RateLimiter` keyed on source address), and `_registrations` is capped at
  `MaxRegistrations = 4096` with expired/oldest eviction.
- **Still open:** `HandleRelayRegister` accepts a bare `4 + 32`-byte frame with no
  signature — nothing proves the sender owns the nodeId it registers (only
  first-come TTL squatting protection). `HandleRelayDeliverAsync` still trusts the
  datagram-supplied `origin` nodeId and synthesizes a peer endpoint from it.
- **Impact:** an attacker can still race a victim's registration to black-hole or
  redirect the victim's relayed inbound, and spoof delivery origins. Reflection
  and memory-exhaustion angles are closed.
- **Fix:** require the registrant to sign the registration payload with the
  identity key and verify against `KademliaId.FromPublicKey`; authenticate or stop
  trusting the `origin` field on deliver. Builds on the (now enforced) key↔ID
  binding from BUG-H1.

### BUG-M2 (residual) — no liveness check on k-bucket insert; eviction path is dead code
- **Fixed since the audit:** per-IP-prefix diversity cap inside buckets
  (`Kademlia/KBucket.cs` `ExceedsPrefixDiversityLocked`, /24 for v4 and /48 for
  v6, `SecurityLimits.MaxBucketNodesPerPrefix = 6`).
- **Still open:** `KBucket.TryAdd` inserts without a pre-insertion PING, the
  `BucketFull` result is never acted on — all `TryAddNode` call sites discard the
  `AddNodeResult`, so `GetOldestNodeInBucket` / `ReplaceOldestInBucket`
  (`Kademlia/RoutingTable.cs`) have no callers.
- **Impact:** routing-table poisoning is costlier than at audit time (ID↔key
  binding + prefix caps) but still lacks the classic Kademlia liveness defense.
- **Fix:** on a full bucket, PING the oldest node and only replace it on timeout;
  wire up the existing eviction helpers. Consider a modest PoW on node IDs.

### BUG-L4 (residual) — store corruption is quarantined but never surfaced
- **Fixed since the audit:** corrupt vs missing is now distinguished — decrypt
  failures quarantine the file (`Security/LocalEncryption.cs` `QuarantineCorrupt`;
  used by `HistoryStore`, `ContactBook`, `GroupManager`).
- **Still open:** every caller ignores the quarantine path — nothing is shown to
  the user, so a tampering signal stays invisible. `Tui/ConversationStore.cs`
  still swallows all exceptions silently on history save and group send.
- **Fix:** surface "store was corrupt, moved to <path>" in the UI/log on load;
  report (or at least log) history-save and group-send failures.

---

## FIXED (re-verified in source, 2026-08-22)

- **BUG-H1 — DHT node IDs not bound to their public key.** Fixed: all insertion
  sites funnel through `RoutingTable.TryAddNode`, which rejects any node whose
  `Id != KademliaId.FromPublicKey(EncryptionPublicKey)` (`RoutingTable.cs`
  `IsIdBoundToKey`).
- **BUG-M1 — onion path selection / mixing delay used `Random.Shared`.** Fixed:
  `RandomNumberGenerator.GetInt32` everywhere (`RoutingTable.cs`,
  `OnionRouter.cs`); no `Random.Shared` left in `src`.
- **BUG-M3 — received files world-readable.** Fixed: `Downloads.cs` restricts the
  directory (`LocalEncryption.RestrictDirectory`) and chmods files 0600.
- **BUG-M4 — group state persisted in plaintext without a store key.** Fixed:
  `GroupManager.SaveGroup` refuses to write when `_storageKey == null`; only
  encrypted `.grpe` is written, legacy `.grp` migrated then shredded. Note: keyless
  sessions now silently skip persistence — acceptable, but worth a log line.
- **BUG-M5 — `send` reported success unconditionally.** Fixed: `SendCommand`
  surfaces failure and distinguishes Sent vs Acknowledged via `WaitForAckAsync`.
- **BUG-M6 — deploy bypassed the NuGet lockfile.** Fixed 2026-08-22: all publish
  RIDs (`linux-x64;win-x64;osx-x64;osx-arm64`) are declared in
  `Directory.Build.props` so lock files carry the full RID graph; lock files
  regenerated; every `/p:RestoreLockedMode=false` removed from
  `deploy-bootstrap.yml` and `release.yml`. Deploy and release now restore in
  locked mode against the audited graph.
- **BUG-M7 — routing test permanently skipped.** Fixed: no `--filter` exclusions
  remain in CI or `scripts/check-coverage.sh`; the test runs and passes.
- **BUG-L1 — file-transfer Accept/Reject not bound to counterparty.** Fixed:
  sender key checked against the transfer counterparty on accept, reject, and
  chunks (`FileTransferService.cs`).
- **BUG-L2 — TCP relay / UDP reassembly lacked per-peer limits.** Fixed: per-IP
  token bucket in `RelayService`, `MaxReassembliesPerSender = 64` in
  `UdpEndpoint`.
- **BUG-L3 — unbounded local collections.** Fixed: `SecurityLimits` adds
  `MaxBucketNodesPerPrefix`, `MaxContacts = 4096`, `MaxGroupMembers = 1024`,
  enforced in `ContactBook` and `GroupManager`. (Routing-table size remains
  implicitly bounded at k × 256 buckets.)
- **BUG-L5 — incoming output corrupted the input line.** Fixed: incoming prints
  are wrapped in `ConsoleLineReader.Shared.WriteInterrupting`, which erases and
  redraws prompt + buffer. Note: `ConsoleUi.PrintIncoming` itself is still a raw
  write — any future unwrapped caller would regress this.
- **DESIGN-1 — no traffic-analysis resistance.** Documented as a stated-scope
  limitation in `KNOWN-LIMITATIONS.md` ("No traffic-analysis resistance"), with
  rationale and deferral target. Cover traffic / batching / per-chunk jitter
  remain future work.

---

## Appendix — non-bug findings from the same audit (tracked elsewhere)

These are not defects but were surfaced alongside the bugs; recorded here so nothing
is lost. Detail lives in the maturity report; move to issues/roadmap as appropriate.

**Test-coverage gaps**
- `group kick` (remove-then-rekey cut-off) — zero tests.
- File-transfer finalize race-fix (`CompleteReceived`/`TryFinalizeAsync`/`Interlocked`) — no targeted test; also no accept/reject/timeout/100 MB-cap/hash-mismatch tests.
- `ContactBook` and `HistoryStore` — no tests (encrypted round-trip, shred, pin-overrides-DHT, `contacts check`).
- Relay fallback — protocol unit-tested, but no E2E of both-sides-symmetric-NAT delivery.
- No test project for the `Susurri.CLI` command/TUI layer.

**Release-engineering / ops gaps (maturity, not bugs)**
- Windows release pipeline exists (`release-windows.yml`: `v*` tags → Velopack setup + `SHA256SUMS.txt`), but still no signed binaries, no SBOM, no provenance — the site's GPG-verify story remains unimplemented.
- No versioning source of truth (no `<Version>`/MinVer/GitVersion; installers hardcode 1.0.0).
- No `global.json`; all workflows pull latest .NET 10 *preview* SDK → non-reproducible builds despite locked packages.
- Single bootstrap seed = SPOF; deploy targets one host.
- No runtime monitoring/alerting on the VPS (health checked only at deploy time).
- Fuzz / scheduled-security failures notify no one.
- VPS hardening gaps in `deploy/setup-vps.sh`: no sshd hardening, no fail2ban, no unattended-upgrades.
- Actions pinned to mutable tags (`@v5`) not commit SHAs; `osv-scanner` runs with `continue-on-error`.
- Windows now ships via Velopack (`release-windows.yml`), still unsigned (no Authenticode); Arch PKGBUILD remains a prototype (`sha256sums=SKIP`, nonexistent repo URL + `v1.0.0` tag).
- Dependabot ignores all semver-major NuGet bumps (EF Core stuck on 9.x, no signal).

**Dead / legacy code (code health)**
- Entire Users module (`src/Modules/Users/*`: EF Core + Npgsql) — loaded but `IUserRepository` never resolved; `ConnectionStrings:UsersDb` defaults empty. RUN.md calls it legacy.
- IAM CQRS login path + `NodeServer` line-protocol server — used only by the Windows WPF demo.
- `NodeServerRunningCheck` health check misnamed — actually probes the Kademlia node.
- ~~`SendCommand` fallback branch unreachable~~ — resolved with BUG-M5.

**Feature backlog (enhancements, not bugs — ranked by user value)**
1. ~~Inline delivery feedback in `send`~~ — done (BUG-M5).
2. ~~Non-interleaving input / live pane~~ — done (BUG-L5).
3. Reconnect / offline→online resend queue after network loss.
4. Identity + history backup/restore (encrypted export).
5. Tor/SOCKS5 transport option (hide entry-hop IP).
6. Contact exchange by link/QR/safety-number.
7. Block/mute/allowlist (spam controls).
8. Offline / resumable file transfer (DHT-stored offers).
9. Message search + date-aware pagination.
10. Desktop/sound notifications; read receipts / presence.
