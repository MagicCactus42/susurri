# Bug & Hardening Roadmap

> **INTERNAL / SENSITIVE — do not publish while HIGH items are open.**
> This file enumerates unpatched vulnerabilities with exact file:line locations
> and exploitation notes. The repository is public; keep this out of git (or in a
> private tracker) until at least the HIGH items below are fixed, then it can be
> folded into `KNOWN-LIMITATIONS.md` / issues.

Findings from the 2026-07-11 maturity audit, ranked highest → lowest severity.

**Legend**
- **Verified ✅** — read directly in the current source during the audit.
- **Reported ⚠️** — surfaced by an audit pass with file:line evidence; not independently re-read.

Template per item: what / where / impact / fix.

---

## CRITICAL / HIGH

### BUG-H1 — DHT node IDs are not bound to their public key (eclipse / Sybil enabler)
- **Verified ✅**
- **Where:** `src/Modules/DHT/Susurri.Modules.DHT.Core/Kademlia/KademliaDhtNode.cs:199` (UDP insert), `:255` (bootstrap PONG insert), `:607` (TCP insert). ID/key helper: `KademliaId.FromPublicKey` = `SHA256(pubkey)` (`Kademlia/KademliaId.cs:44`); the node computes its own ID this way at `:76`.
- **What:** peers are inserted into the routing table using the attacker-supplied `message.SenderId` / `pong.SenderId` verbatim. There is no check that `SenderId == KademliaId.FromPublicKey(SenderPublicKey)`. The TCP path (`:603-608`) gates on `SenderPort > 0 && SenderPublicKey.Length > 0` but never validates the ID against the key.
- **Impact:** an attacker can claim **any** node ID independent of their key and position themselves arbitrarily close to a victim's key/username hash, intercepting that victim's `FIND_VALUE` / `STORE` / offline-message routing — a targeted eclipse attack. Undermines the "genuinely decentralized" guarantee for any targeted user.
- **Fix:** at every insertion site, reject (or recompute) the entry unless `node.SenderId == KademliaId.FromPublicKey(node.SenderPublicKey)`. Centralize the check in `RoutingTable.TryAddNode` or a single validated-construction path so all three sites are covered.

### BUG-H2 — UDP relay frames are unauthenticated and unrate-limited (open reflector + registration hijack + unbounded memory)
- **Verified ✅**
- **Where:** `src/Modules/DHT/Susurri.Modules.DHT.Core/Network/UdpEndpoint.cs` — `DispatchDatagramAsync:346` (dispatches `SURG/SURF/SURD` **before** any rate limiter; the limiter only guards `HandleUdpMessageAsync`, which relay frames never reach), `HandleRelayRegister:385`, `HandleRelayForwardAsync:393`, `HandleRelayDeliverAsync:411`, `_registrations` dictionary `:44` (no size cap, 90 s TTL only).
- **What:** three unauthenticated primitives on the shared UDP socket:
  1. **Registration hijack** — `HandleRelayRegister` stores `nodeId → sender` with no proof the sender owns that nodeId and no cap on `_registrations`. An attacker registers a victim's nodeId to black-hole/redirect the victim's relayed inbound, or floods registrations to exhaust memory.
  2. **Open reflector** — `HandleRelayForwardAsync` forwards arbitrary inner payloads to any registered target with no auth and no rate limit; the relay becomes a source-hiding UDP reflector aimed at a chosen victim, abusing relay bandwidth.
  3. **Spoofable origin** — `HandleRelayDeliverAsync` trusts the `origin` nodeId from the datagram and synthesizes a peer endpoint from it.
- **Impact:** DoS amplification/reflection off any relay node, targeted denial of a peer's relay path, and memory exhaustion of a relay. Introduced with the recent symmetric-NAT relay-fallback feature.
- **Fix:** (a) rate-limit `DispatchDatagramAsync` per source IP (reuse the existing per-IP limiter); (b) require the registrant to prove nodeId ownership (sign the registration payload with the identity key, verify against `FromPublicKey`); (c) cap `_registrations` (bounded dictionary with LRU/oldest-eviction). Ties into BUG-H1's key↔ID binding.

---

## MEDIUM

### BUG-M1 — onion path selection and mixing delay use non-cryptographic `Random.Shared`
- **Verified ✅**
- **Where:** path selection `src/Modules/DHT/Susurri.Modules.DHT.Core/Kademlia/RoutingTable.cs:115,117` (`GetRandomNode`) and `:128` (`GetRandomNodes` Fisher–Yates); mixing delay `src/Modules/DHT/Susurri.Modules.DHT.Core/Onion/OnionRouter.cs:314` and `:651` (`Random.Shared.Next(50, 501)`).
- **What:** relay-path hop selection and the per-hop 50–500 ms mixing delay both draw from the predictable `Random.Shared` PRNG. Path predictability is the more security-relevant of the two.
- **Impact:** a predictable relay-selection RNG is a deanonymization aid; the README advertises the mixing delay as a traffic-analysis countermeasure but it is not crypto-random.
- **Fix:** use `System.Security.Cryptography.RandomNumberGenerator.GetInt32` for both the delay and the Fisher–Yates shuffle / bucket-and-node picks.

### BUG-M2 — no Sybil cost, no IP-diversity bucket limits, no liveness check on k-bucket insert
- **Reported ⚠️**
- **Where:** `KBucket.TryAdd:37`, `RoutingTable.TryAddNode:26`, `KademliaDhtNode.cs:959`.
- **What:** nodes are admitted to k-buckets on any received message or `FIND_NODE` response with no proof-of-work, no per-`/16` or per-IP diversity cap, and no pre-insertion PING. Node IDs are free to mint (keypair generation).
- **Impact:** combined with BUG-H1 this makes routing-table poisoning cheap and scalable.
- **Fix:** cap entries per IP-prefix within a bucket; verify liveness (PING) before admitting to a full/near-full bucket; consider a modest PoW on node ID.

### BUG-M3 — received files are written world-readable
- **Reported ⚠️**
- **Where:** `src/Bootstrapper/Susurri.CLI/Downloads.cs:15` (`Directory.CreateDirectory`, no `RestrictDirectory`), `:26` (`File.WriteAllBytes`, default umask).
- **What:** unlike every other local store (history/contacts/groups/keys chmod 0700), the downloads directory and saved files use default permissions (~0644 / 0755).
- **Impact:** decrypted, potentially sensitive received files are readable by other local users on a shared machine.
- **Fix:** call `LocalEncryption.RestrictDirectory(target)` on the directory and `File.SetUnixFileMode(path, UserRead | UserWrite)` (0600) on each saved file.

### BUG-M4 — group state persisted in plaintext when no store key is present
- **Reported ⚠️**
- **Where:** `src/Modules/DHT/Susurri.Modules.DHT.Core/.../GroupManager.cs:279-282` (`SaveGroup` writes unencrypted `.grp` when `_storageKey == null`; `LoadGroups` will read such files).
- **What:** the group symmetric key and roster are written to disk in the clear whenever `_storageKey` is null, and `_storageKey` is null-defaulted through the `ChatService` constructor chain — a latent footgun.
- **Impact:** group keys can hit disk unencrypted, defeating the at-rest guarantee for group secrecy.
- **Fix:** refuse to persist (or require an explicit ephemeral key) when no storage key is available, rather than silently writing plaintext.

### BUG-M5 — `send` reports success even when delivery fails
- **Reported ⚠️**
- **Where:** `src/Bootstrapper/Susurri.CLI/Commands/SendCommand.cs:41-47` — always takes the `Conversations` path and prints "Sent (see 'chats')"; the real send result is discarded. The `_session.Conversations == null` fallback branch that would report failure is unreachable (`Conversations` is always set at login).
- **What:** the CLI gives positive delivery feedback unconditionally; the actual status/ACK state machine exists but is surfaced only in the TUI.
- **Impact:** users believe a message was delivered when it silently failed (unreachable recipient, dropped onion path) — a correctness/trust bug for a messenger.
- **Fix:** surface the send/ACK result inline in `send` (Sending → Sent → Acknowledged → Failed); remove the dead fallback branch.

### BUG-M6 — deploy bypasses the NuGet lockfile
- **Reported ⚠️**
- **Where:** `.github/workflows/deploy-bootstrap.yml:40` publishes with `/p:RestoreLockedMode=false`.
- **What:** CI validates against committed `packages.lock.json` (locked mode), but the deploy job disables it, so the **deployed** bootstrap binary can resolve a different package set than the one audited.
- **Impact:** the running seed can drift from the reviewed/audited dependency graph — a supply-chain integrity gap on the most trust-sensitive node.
- **Fix:** restore in locked mode in the deploy job too; fix the underlying RID-restore issue that motivated the override instead of disabling the guard.

### BUG-M7 — a routing test is permanently skipped, masking a possible real defect
- **Reported ⚠️** (note: prior audit memory claims the underlying XOR-distance sort bug was fixed; the exclusion may be stale — either way it is a defect to resolve)
- **Where:** `RoutingTableTests.FindClosestNodes_ReturnsNodesOrderedByDistance`, `--filter`-excluded in `.github/workflows/build.yml:54`, `scripts/check-coverage.sh:68`, and the non-Linux test step.
- **What:** the test is excluded from every CI path. It exercises XOR-distance ordering in the routing table — core DHT correctness.
- **Impact:** either a real ordering bug is being hidden, or a fixed bug's exclusion was never removed (dead config that also drops coverage of a critical path).
- **Fix:** un-skip, run it; if green, delete all three exclusions; if red, fix the ordering defect.

---

## LOW

### BUG-L1 — file-transfer Accept/Reject not bound to the counterparty identity
- **Reported ⚠️**
- **Where:** `src/Modules/DHT/Susurri.Modules.DHT.Core/Services/FileTransferService.cs:449` (`HandleTransferAcceptAsync`), `:464` (`HandleTransferReject`).
- **What:** these act on any signed message matching the `TransferId` without checking the sender equals the transfer's counterparty. A third party who learns a `TransferId` could cancel a transfer or trigger chunk emission. Mitigated only by the 128-bit random GUID.
- **Fix:** verify `accept/reject.SenderPublicKey` equals the transfer's counterparty key before acting.

### BUG-L2 — TCP relay and UDP reassembly lack per-peer limits
- **Reported ⚠️**
- **Where:** `.../Network/RelayService.cs:280-328` (no per-IP request rate limit; forwards to any routing-table node), `.../Network/UdpEndpoint.cs:38,475` (`_inbound` reassembly keyed by `sender:messageId`, only a 15 s sweep, no per-sender cap).
- **What:** transient memory growth from spoofed sources; TCP relay has circuit caps but no per-IP rate limit.
- **Fix:** per-source rate limiting and a cap on concurrent in-flight reassemblies per sender.

### BUG-L3 — unbounded local collections not covered by `SecurityLimits`
- **Reported ⚠️**
- **Where:** `.../Contacts/ContactBook.cs:75` (`Add`, no size cap); `SecurityLimits` bounds messages/values/usernames/paths/offline-per-user but not routing-table size, contact-book size, or group member count (group roster only de-facto bounded to 1024 via wire `MaxRosterSize`).
- **What:** local/user-driven unbounded growth. Low impact (not remote-triggered) but inconsistent with the rest of the bounding discipline.
- **Fix:** add explicit caps to `SecurityLimits` and enforce in `ContactBook` / `GroupInfo`.

### BUG-L4 — silent broad `catch` blocks mask local-store corruption
- **Reported ⚠️**
- **Where:** `HistoryStore.cs:56`, `ContactBook.cs:160`, `GroupManager.cs:92,265`, `ConversationStore.cs:115,243` — swallow all exceptions and return empty.
- **What:** a corrupted or tampered local store silently degrades to blank state instead of alerting; "missing" and "corrupt/decrypt-failed" are indistinguishable.
- **Impact:** stealthy local data loss; a decrypt failure (possible tampering signal) is hidden.
- **Fix:** distinguish "missing" from "corrupt/decrypt-failed" and surface the latter.

### BUG-L5 — incoming message output corrupts the in-progress input line
- **Reported ⚠️**
- **Where:** `ConsoleUi.PrintIncoming` writes directly to stdout while the user is mid-line in `ReadLineAsync` (interactive REPL).
- **What:** an inbound message printed during typing garbles the line being composed.
- **Impact:** UX/robustness defect in the primary messaging loop.
- **Fix:** redraw the readline buffer after async output, or route incoming messages to a live pane / dedicated region.

---

## DESIGN-LEVEL (track, not a quick fix)

### DESIGN-1 — no traffic-analysis resistance (no cover traffic / mixing / batching)
- **Verified ✅** (grep for cover/dummy/decoy/batch/mix is empty)
- **What:** timing-correlation resistance rests solely on the per-hop 50–500 ms delay (weak RNG — see BUG-M1) plus 16 KB size padding. File chunks are emitted sequentially with no source-side inter-chunk jitter, so a global observer can count/size-correlate a transfer end-to-end.
- **Action:** add to `KNOWN-LIMITATIONS.md` (currently absent); evaluate cover traffic / batching / per-chunk jitter as a future phase. This is a stated-scope limitation, not a code defect — but it should be documented honestly like the other threat-model gaps.

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
- `SendCommand` fallback branch (see BUG-M5) unreachable.

**Feature backlog (enhancements, not bugs — ranked by user value)**
1. Inline delivery feedback in `send` (overlaps BUG-M5).
2. Non-interleaving input / live pane (overlaps BUG-L5).
3. Reconnect / offline→online resend queue after network loss.
4. Identity + history backup/restore (encrypted export).
5. Tor/SOCKS5 transport option (hide entry-hop IP).
6. Contact exchange by link/QR/safety-number.
7. Block/mute/allowlist (spam controls).
8. Offline / resumable file transfer (DHT-stored offers).
9. Message search + date-aware pagination.
10. Desktop/sound notifications; read receipts / presence.
