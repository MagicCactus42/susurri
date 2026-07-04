# Running Susurri

Susurri is a fully peer-to-peer network: a Kademlia DHT for peer/key discovery,
onion routing for message delivery, and DHT-distributed storage for offline
messages. There is **no central server that relays your traffic**. The only
thing the network needs is at least one **bootstrap node** (a "seed") at a known
address so new nodes have an entry point to join — exactly like BitTorrent or
IPFS bootstrap nodes. A seed is just an ordinary DHT node; it does not see or
relay your messages.

Running a node needs **no database**. Postgres is only used by the optional
`login` (username registration) flow.

---

## Option A — prebuilt self-contained binary (recommended, no .NET needed)

The build under `dist/` bundles the .NET 10 runtime and libsodium, so on Arch you
need nothing installed (glibc is already present).

```bash
# from the repo root, after: dotnet publish ... -o ./dist   (see Option B to build)
./dist/susurri-cli version
```

### Run a bootstrap seed (headless, no login)

```bash
./dist/susurri-cli --bootstrap -p 7070
```

This starts a Kademlia node on `0.0.0.0:7070` and a local health endpoint on
`127.0.0.1:7071`. Check it:

```bash
curl -s http://127.0.0.1:7071/ready    # {"status":"ready","checks":{"dht-node":{"status":"healthy",...}}}
curl -s http://127.0.0.1:7071/health   # {"status":"alive"}
```

### Run a second node that joins the seed

The interactive CLI reads commands from stdin:

```bash
./dist/susurri-cli
# then at the > prompt:
dht start 7072 127.0.0.1:7070     # start on port 7072, bootstrap against the seed
status                            # shows "Peers: 1"
exit
```

Or non-interactively:

```bash
printf 'dht start 7072 127.0.0.1:7070\nstatus\nexit\n' | ./dist/susurri-cli
```

You can add several seeds for redundancy:
`dht start 7072 1.2.3.4:7070 5.6.7.8:7070`.

---

## Option B — build from source

Requires the **.NET 10 SDK** (currently a preview). On Arch, either:

- install the Microsoft dotnet-install script runtime you already have under
  `~/.dotnet`, or
- AUR: `dotnet-sdk-preview-bin`.

```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"

# run directly
dotnet run --project src/Bootstrapper/Susurri.CLI -- --bootstrap -p 7070

# or produce the self-contained ./dist used in Option A
dotnet publish src/Bootstrapper/Susurri.CLI/Susurri.CLI.csproj \
  -c Release -r linux-x64 --self-contained -o ./dist
```

> Do **not** build the whole solution on Linux: `Susurri.Bootstrapper` is a
> Windows-only WPF demo (`net10.0-windows`) and won't compile here. Build the
> CLI project (and, if you want, the test projects) directly.

---

## Using it as a messenger

Once at least one seed is reachable (set `DHT:BootstrapNodes` or
`DHT__BootstrapNodes__0=ip:port`), log in and chat:

```bash
DHT__BootstrapNodes__0=1.2.3.4:7070 ./dist/susurri-cli
# > generate                       (create a BIP39 passphrase — your identity)
# > login alice                    (enter the passphrase; derives your keys, goes online)
# > send bob hey there             (looks bob up in the DHT, sends over onion routing)
# > inbox                          (list received messages; incoming ones also print live)
# > status                         (identity, port, peers, inbox count)
# > logout
```

Your username → public-key mapping is published to the DHT on login, so peers
find you by name. Direct messages are onion-routed (3 hops by default) and
delivered to the recipient, or stored in the DHT if they're offline.

### Group chat

```bash
# > group create book-club                       (prints a group id)
# > group invite <group-id> bob                   (resolves bob's key, prints an invite code)
#   (send the invite code to bob out of band)
# bob> group join <invite-code>                   (joins with his private key)
# > group send <group-id> hi everyone             (encrypts once, fans out over onion)
# > group list / group msgs <group-id>
```

Group messages are encrypted with per-sender ratchet chains (forward secrecy);
the shared group key (wrapped per member on invite) proves membership and seals
invites. `group rotate <group-id>` (owner only) issues a new group key and
delivers it to every member in-band — owner-signed, sealed per member, applied
automatically (offline members get it on next login). `group kick <group-id>
<member>` removes a member and re-keys the remaining ones in the same way, so
the removed member is cut off immediately.

### Contacts

```bash
# > contacts add ala bob            (pins bob's current key under the petname "ala")
# > send ala hi                     (petnames work anywhere a username does)
# > contacts verify ala             (compare the safety number out of band, mark verified)
# > contacts check ala              (compare the pinned key against the live DHT record)
```

A pinned contact's key is used directly — DHT lookups can no longer redirect
messages for that contact — and their messages display under the petname.

### History

Conversations live in RAM by default: a restart forgets everything. `history on`
persists them to an encrypted local store (key derived from your passphrase, per
identity); `history off` shreds it; `history` shows status. The `chats` browser
picks stored conversations up automatically on the next login.

## Identity

Your identity is derived entirely from your passphrase and username
(PBKDF2-SHA256, 600k iterations, a stable per-username salt → Ed25519 signing +
X25519 encryption keys). The same passphrase + username reproduce the same
identity on any machine — nothing is stored server-side, and **no database is
required**. Keep the passphrase safe: it *is* your account, and there is no
password reset in a decentralized system.

> Postgres (`docker compose up -d db`, `.env`) is only used by the legacy
> username-registration handler and is **not needed** for the messenger.

---

## Transport & NAT traversal

Each node speaks its Kademlia + onion protocol over a **reliable UDP transport**
(fragmentation, retransmission, reassembly, dedup on one shared socket) and keeps
TCP as a fallback. UDP is preferred because it can traverse NAT via UDP
hole-punching; TCP is used when a peer only exposes its TCP listener.

When a node can't reach a peer directly, it **auto-coordinates a hole punch
through the DHT**: it asks an intermediary that can reach the target to relay a
punch request, learns the target's public endpoint from the reply, punches on
the shared socket, and retries — no manual signalling.

Config (`appsettings.json` → `DHT:Nat`, or env `DHT__Nat__Enabled`):

```json
"Nat": { "Enabled": true, "UseStun": false, "PublicEndpoint": "" }
```

- `Enabled` (default true): run the UDP transport + hole-punch path.
- `UseStun` (default false): discover this node's public `ip:port` via public
  STUN servers so it can be advertised for hole-punching. This reveals your IP
  to third-party STUN servers, so it's **opt-in**. Turn it on for nodes behind
  NAT that need to be reachable.
- `PublicEndpoint` (e.g. `"203.0.113.5:7070"`): set this on a node with a known
  public IP instead of using STUN — it's advertised as-is for hole-punch
  coordination. A node needs either this or `UseStun` on to be punch-reachable.

## Do I need a VPS?

- **Full P2P — no server sees your traffic.** Messages travel through onion
  circuits of random peers; offline messages are stored distributed in the DHT.
- **You still need ≥1 bootstrap seed at a known address** (entry point only, like
  BitTorrent/IPFS). For an always-on network, run **1–2 seeds on public IPs**
  (a cheap VPS each) — they also serve as onion relays and hole-punch
  rendezvous. With `UseStun` on, home nodes behind NAT can now become directly
  reachable to each other via hole-punching, so the network no longer depends
  solely on public relays.
- **For local testing or friends who know each other's IPs:** no VPS needed —
  any node with a reachable address is the seed for the others.
