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

## Optional — the `login` / user-registration flow (needs Postgres)

Running a node does not require this. It's only needed if you want to register a
username in the local Users database.

```bash
cp .env.example .env          # set POSTGRES_USER / POSTGRES_PASSWORD
docker compose up -d db       # starts Postgres on :5432

# tell the CLI where the DB is (must match .env):
export ConnectionStrings__UsersDb="Host=localhost;Port=5432;Database=susurri;Username=susurri;Password=<yours>"

./dist/susurri-cli
# > generate            (creates a BIP39 passphrase — your identity)
# > login <username>    (enter the passphrase)
```

Your identity is derived from the passphrase (PBKDF2-SHA256, 600k iterations →
Ed25519 signing + X25519 encryption keys). Keep the passphrase safe: it *is*
your account. There is no password reset in a decentralized system.

---

## Do I need a VPS?

- **For a real, always-reachable network:** yes, run **1–2 bootstrap/relay nodes
  on public IPs** (a cheap VPS each). They are entry points and onion relays, not
  servers that see your messages. Nodes behind home NAT can reach public nodes
  outbound; onion circuits are built from nodes that have public reachability.
- **For local testing or friends who know each other's IPs:** no VPS needed —
  any node with a reachable address can be the seed for the others.

See the "Architecture" notes in the project docs for the full delivery path.
