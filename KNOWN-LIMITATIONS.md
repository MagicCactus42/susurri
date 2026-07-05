# Known Limitations

Inventory of deliberate deferrals, work-in-progress items, and known shortcomings
of the current Susurri codebase. Each entry should answer **what's missing**,
**why it was deferred**, and **what phase will close it** (per the
production-readiness roadmap at `~/.claude/plans/audit-this-project-check-pure-babbage.md`).

This file is the single source of truth for "we know about this — it's intentional."
If something is missing here, it's either an oversight (file an issue) or it's
already done.

---

## Phase 1 deferrals

### 1.6 File transfer: in-memory accumulation
- **What:** `FileTransferService` keeps the entire outgoing file in `byte[]` memory
  and accumulates incoming chunks in `ConcurrentDictionary<int, byte[]>` until
  reassembly. With the 100 MB cap (Phase 1) this is bounded but inefficient.
- **Why deferred:** the 100 MB cap closes the OOM-DoS vector that mattered for
  Phase 1 security; switching to a temp-file streaming model is a substantial
  refactor with its own correctness surface.
- **Target:** **Phase 2 follow-up** — stream chunks to a `FileStream` opened
  with `FileOptions.DeleteOnClose` instead of holding chunks in memory.

### 1.8 Forward secrecy ratchet
- **What:** Direct messages run a per-peer double ratchet
  (`RatchetSessionManager` + `DoubleRatchet`); group messages run per-sender
  HKDF hash chains with sealed sender-key distribution (`GroupRatchetManager`).
  Group re-keying is automatic and in-band: sender chains re-seed on an epoch
  (256 messages / 24 h), and the group key rotates on `group rotate` /
  `group kick` via owner-signed, per-member-sealed `GroupRekeyMessage`s with
  monotonic versions and roster sync. What remains: rotation is owner-driven
  (an offline owner cannot re-key; a member's voluntary `group leave` sends no
  notification, so it does not trigger one), and distribution is O(n) rewrap
  per rotation rather than TreeKEM's O(log n).
- **Why deferred:** owner-driven O(n) re-key is sound and simple at the group
  sizes the CLI targets; decentralized rotation authority and TreeKEM-style
  efficiency are a full MLS-shaped protocol effort.
- **Target:** **Phase 6 follow-up** — leave-notification message so departures
  trigger rotation, and evaluate MLS/TreeKEM if groups need to scale.

### 1.9 Onion relay selection can pick non-relay peers
- **What:** `GetRandomNodesForPath` draws relays from the whole routing table,
  which also contains **DHT-only nodes** — bootstrap seeds (`--bootstrap`) and
  headless `dht start` nodes never log in, so they have no `OnionRouter` and
  silently drop any onion frame they're asked to relay. When such a node lands
  in a 3-hop path, that message is black-holed (delivery is fire-and-forget;
  there's no relay-failure retry). The odds shrink as the online-user count
  grows relative to the number of seeds, but on a tiny network (e.g. one seed +
  two users) it bites often.
- **Why deferred:** the protocol has no capability bit advertising "I relay
  onion traffic," so a node can't currently tell relay-capable peers from
  DHT-only ones at path-selection time.
- **Target:** **Phase 6** — advertise an onion-relay capability flag in the
  DHT record / PONG and filter `GetRandomNodesForPath` to capable peers; short
  term, exclude configured bootstrap endpoints from relay selection.
- **Local-test impact:** to test delivery on one machine you need the seed
  **plus ≥3 logged-in peers**, and even then a path may occasionally include the
  seed and drop — resend, or run more logged-in peers. The deterministic proof
  lives in `LocalChatDeliveryTests` (5 full nodes, no seed in the mesh).

---

## Phase 2 deferrals

### 2.1 KademliaDhtNode still 791 LOC
- **What:** Phase 2.1 brought `KademliaDhtNode.cs` from 1209 → 791 LOC by
  extracting `OfflineMessageService`, `HolePunchCoordinator`, and
  `UserPublicKeyRecord`. The plan's acceptance bar is "no class > 400 LOC."
- **Why deferred:** further extraction (e.g. `KademliaLookup` for the
  iterative `FindClosestNodesAsync`/`FindValueAsync`, `PublicKeyDirectory` for
  `PublishPublicKeyAsync`/`LookupPublicKeyAsync`) is achievable but the
  remaining file is now coherent — listener + dispatcher + transport. Splitting
  more risks fragmenting the request/response correlation logic across files.
- **Target:** **Phase 2 follow-up** — extract `KademliaLookup` (~150 LOC) and
  `PublicKeyDirectory` (~200 LOC) when test scaffolding for the iterative
  lookup logic exists.

### 2.2 ConfigureAwait residuals
- **What:** 206 `.ConfigureAwait(false)` calls cover ~95 % of awaits in library
  projects. A handful of multi-line awaits in lambdas (e.g. progress event
  invocations inside loops) still don't have it.
- **Why deferred:** the residuals are inside lambdas where the awaited
  expression has already-bound semantics; verifying each by hand was
  diminishing returns. Modern .NET console hosts have no `SynchronizationContext`,
  so the correctness impact is only in WPF/Avalonia consumers — and those
  hand off to UI dispatch separately.
- **Target:** **Phase 4** — adopt `ConfigureAwaitChecker.Analyzer` as part of
  the `AnalysisLevel=latest-recommended` pass below; the analyzer surfaces
  the residuals automatically and turns them into build errors.

### 2.4 AnalysisLevel=latest-recommended NOT enabled
- **What:** the plan prescribed
  `<AnalysisLevel>latest-recommended</AnalysisLevel>` alongside
  `TreatWarningsAsErrors=true`; only the latter is on.
- **Why deferred:** `latest-recommended` enables hundreds of CA/IDE
  diagnostics (CA1001, CA1002, IDE0xxx, etc.) and would surface a large
  cleanup pass — primarily stylistic — that has its own scope and merit
  but is orthogonal to the correctness bar. Explained in the comment block
  inside `Directory.Build.props`.
- **Target:** **separate cleanup pass** between Phase 2 and Phase 3.

### 2.4 Tmds.DBus.Protocol 0.20.0 vulnerability (transitive)
- **What:** Avalonia.Desktop transitively brings in
  `Tmds.DBus.Protocol 0.20.0`, which has a published high-severity advisory
  (`GHSA-xrw6-gwf8-vvr9`).
- **Why deferred:** this is upstream's choice — we can't fix Avalonia's
  package graph without forking. `<NuGetAuditMode>direct</NuGetAuditMode>`
  in `Directory.Build.props` quiets the build error while still surfacing
  the audit info in restore logs. We retain visibility, we just don't
  block on a transitive third-party vulnerability we can't fix.
- **Target:** **track upstream Avalonia releases** — re-enable transitive
  audit when Avalonia 11.3.x or later ships a Tmds bump.

---

## Phases not yet started

These are entire phases of the production-readiness roadmap that have not been
attempted. They are documented in
`~/.claude/plans/audit-this-project-check-pure-babbage.md`.

- **Phase 4** — Observability:
  - 4.1 (Serilog + LogRedaction.KeyFingerprint + Activity correlation) **complete.**
  - 4.2 OpenTelemetry metrics (counters + 1 histogram + conditional OTLP exporter) **complete.** Observable gauges and outbound-DHT counter deferred to a later sub-phase.
  - 4.3 `/health` + `/ready` endpoints via HttpListener **complete.** Bootstrap-mode only; bind 127.0.0.1:7071 by default. The bootstrap node now runs a real `KademliaDhtNode`; the readiness check reports listening state and the known-peer count.
  - 4.4 `IFatalErrorHandler` + redacted local crash dumps + optional OTLP-style POST **complete (code).** Wired to `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`. Writes JSON to `~/.config/Susurri/crashes/` by default. Remote POST off unless `CrashReporting:Endpoint` set. Build/test verification deferred to a user-driven session (constraint: no Bash in autonomous loop).
- **Phase 5** — CI/CD:
  - 5.1 GitHub Actions matrix build + security scans + nightly fuzz **complete.** `build.yml` (ubuntu/windows/macos × net10.0, strict warnings, coverage gate on linux), `security.yml` (CodeQL csharp, `dotnet list package --vulnerable`, `dotnet format` informational), `fuzz.yml` (nightly 5-min SharpFuzz smoke). First real run will land when these are pushed and a PR opens.
  - 5.2 Dependabot + pinned NuGet sources + lock-file mode **complete (config).** `.github/dependabot.yml` opens weekly grouped PRs for nuget + github-actions, ignoring semver-major bumps. `nuget.config` clears inherited sources and pins everything to nuget.org. `Directory.Build.props` sets `RestorePackagesWithLockFile=true` always and `RestoreLockedMode=true` when `$(ContinuousIntegrationBuild)` is set (CI workflows export it). **Action required:** before pushing, run `dotnet restore` locally to generate `packages.lock.json` for every csproj, then commit them — first CI run will otherwise fail with a "lock file is missing" error.
  - 5.3 Versioning + signed releases with SBOM — pending (signing requires GPG/cosign keys the user must provision; loop will stop and surface the blocker).
  - 5.4 Reproducible builds (`<DeterministicSourcePaths>`, attestations) — pending.
  - 5.5 Cross-platform installers (deb / Flatpak / AppImage / WiX MSI / macOS pkg) — pending; macOS pkg needs Apple Developer ID.
- **Phase 6** — Operations: bootstrap-node IaC, systemd units, multi-environment
  configs (Dev/Staging/Production), DB migration runner, secret-management
  documentation.
- **Phase 7** — Docs & external review: SECURITY.md, CONTRIBUTING.md,
  CODE_OF_CONDUCT.md, THREAT-MODEL.md, PROTOCOL.md, DEPLOYMENT.md, third-party
  licenses, paid external security review.
- **Phase 8** — Pre-launch validation: load + chaos testing on 200-node DHT,
  operational runbook drills, soft launch.

---

## Coverage gate (Phase 3.5)

### Per-assembly thresholds are below the 75% target
- **What:** `scripts/check-coverage.sh` runs `dotnet test --collect:"XPlat Code Coverage"`
  on every test project, parses the cobertura output per-assembly, and checks
  each Modules/* / Shared/* assembly against a minimum threshold. The script
  passes today, but the thresholds are deliberately set to **current
  reality** rather than the roadmap goal of 75% line coverage:

  | Assembly | Today | Active threshold | Goal |
  |---|---|---|---|
  | `Susurri.Modules.DHT.Core` | 55% | 50% | 75% |
  | `Susurri.Modules.IAM.Core` | 42% | 35% | 75% |
  | `Susurri.Modules.DHT.Application` | 0% | 0% | 60% |
  | `Susurri.Modules.IAM.Application` | 0% | 0% | 60% |
  | `Susurri.Modules.Users.Core` | 0% | 0% | 60% |
  | `Susurri.Modules.Users.Application` | 0% | 0% | 60% |
  | `Susurri.Shared.Abstractions` | 32% | 25% | 60% |
  | `Susurri.Shared.Infrastructure` | 0% | 0% | 60% |

- **Why deferred:** raising the gate to 75% today would force ~30 percentage
  points of new tests across the heaviest assemblies before any other change
  can land. That work is real but should be done deliberately, not as a
  rushed precondition for this phase.
- **Path forward:** the threshold values in `scripts/check-coverage.sh` are
  the ratchet. Each PR that adds tests for a previously-uncovered area
  should bump the corresponding threshold by ~5 pp, so the gate steadily
  pulls coverage upward. Application/Users.Core need the most attention —
  they're 0% because no integration tests exercise their EF/DI paths.
- **Target:** **Phase 3.x sustained work** — incremental ratchet rather than
  a single-PR push. Tracked separately from the Phase 5 CI work that will
  actually invoke `scripts/check-coverage.sh` in pipeline.

---

## Fuzzing infrastructure (Phase 3.4)

### AFL coverage-guided fuzzing not yet wired into CI
- **What:** `tests/Susurri.Tests.Fuzz` provides 13 fuzz targets and a
  `susurri-fuzz` console executable that supports both AFL persistent mode
  and a standalone smoke mode. The AFL setup is documented in
  `tests/Susurri.Tests.Fuzz/README.md` (install AFL++, instrument via
  `SharpFuzz.CommandLine`, run `afl-fuzz -V 300`). Today the harness has
  only been exercised in standalone smoke (65 000 random iterations × 0
  crashes on first run) and the in-process xUnit guard (1000 iterations
  per target on every PR).
- **Why deferred:** AFL needs native infrastructure (`afl-fuzz` binary)
  and a CI pipeline to run nightly with crash detection. Susurri has no
  CI today (Phase 5). Local AFL runs would also work but aren't yet part
  of the developer workflow.
- **Target:** **Phase 5** — when the CI pipeline lands, add a workflow
  step that installs AFL, runs `sharpfuzz` against the published assembly,
  loops `afl-fuzz -V 300` over each target, fails on any output in
  `findings/*/crashes/`, and uploads `findings/` as an artifact.

---

## Maintenance

When closing one of these items, **delete the corresponding row/section** in
this file and reference the closing change in your commit/PR. This file
should shrink, not grow, as we make progress. New deferrals get appended
with a phase reference and a why-deferred line.
