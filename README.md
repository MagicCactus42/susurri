# Susurri

> *Most people don't appreciate anonymity before they lose it, and you only get to lose it once. Susurri will help you keep it as long as possible.*

![Logo](https://i.imgur.com/f3JmDdd.png)

**Secure Peer-to-Peer Chat with DHT-Based Discovery and Onion Routing**

Susurri is a decentralized, privacy-focused chat application that combines Kademlia DHT for peer discovery with Tor-like onion routing for anonymous communication. No central servers, no metadata leaks, complete privacy.

[Project Board (Whimsical)](https://whimsical.com/susurri-UVF3zJdmKYukMWMUPBJKg4)

## Features

- **Decentralized Architecture** - No central servers; peers discover each other via DHT
- **Kademlia DHT** - Distributed hash table for peer discovery and public key distribution
- **Onion Routing** - Three-layer encryption for sender anonymity (Tor-like)
- **End-to-End Encryption** - X25519 key exchange + ChaCha20-Poly1305 AEAD
- **Deterministic Identity** - BIP39 mnemonic-based key generation; same passphrase = same identity
- **Message Padding** - Fixed 16KB message blocks to resist traffic analysis attacks
- **Group Messaging** - Encrypted group chats with shared symmetric key distribution
- **Passphrase Generation** - Built-in BIP39 mnemonic generator (12-24 words)
- **Credential Caching** - Optional encrypted local storage for credentials
- **Offline Messages** - DHT stores messages for offline recipients
- **Cross-Platform CLI** - Interactive terminal interface for Linux/macOS/Windows
- **Desktop Integration** - Application menu entries and GUI launcher (Linux)

## Quick Start

### Installation (Arch Linux)

```bash
cd installers/arch
./install.sh
```

### Run

```bash
# Terminal interface
susurri

# GUI launcher
susurri-gui
```

### Basic Usage

```bash
# Generate a secure passphrase (first time setup)
susurri > generate
  === Generated Passphrase ===
  abandon ability able about above absent absorb abstract absurd abuse access accident

  [!] IMPORTANT: Write this down and store it securely offline!

# Login with your passphrase (6+ words required)
susurri > login alice
  Passphrase: ****************************************************
  Save credentials locally for future logins? [y/N]: y
  Enter a password to protect cached credentials (8+ chars): ********
  [+] Logged in as 'alice'.
  [+] Credentials saved locally (encrypted).

# Start DHT node
susurri > dht start
  [+] DHT node started on port 7070.

# Check status
susurri > status
  User: alice (logged in)
  DHT node: RUNNING

# Group chat commands
susurri > group create "Friends"
  [+] Group created. Group ID: a1b2c3d4-...

susurri > group list
  Your groups:
  - Friends (a1b2c3d4-...) - Owner
```

## Architecture

Susurri follows a **modular monolith architecture** with clear separation of concerns:

```
susurri/
├── src/
│   ├── Bootstrapper/           # Application entry points
│   │   ├── Susurri.CLI/        # Cross-platform CLI (Linux/macOS/Windows)
│   │   └── Susurri.Bootstrapper/ # WPF GUI (Windows only)
│   ├── Modules/
│   │   ├── DHT/                # Distributed Hash Table & Networking
│   │   ├── IAM/                # Identity & Access Management
│   │   └── Users/              # User persistence
│   └── Shared/                 # Common abstractions & infrastructure
├── tests/                      # Unit tests
└── installers/                 # Platform-specific installers
```

### Module Communication

```
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│  IAM Module │────▶│   Message   │────▶│Users Module │
│  (Identity) │     │   Broker    │     │(Persistence)│
└─────────────┘     └─────────────┘     └─────────────┘
                           │
                           ▼
                    ┌─────────────┐
                    │ DHT Module  │
                    │  (Network)  │
                    └─────────────┘
```

## Technology Stack

| Component | Technology |
|-----------|------------|
| Runtime | .NET 10.0 |
| Cryptography | NSec (libsodium wrapper) |
| Key Derivation | BIP39 + HKDF-SHA256 |
| Encryption | X25519 + ChaCha20-Poly1305 |
| Signing | Ed25519 |
| Database | PostgreSQL + Entity Framework Core |
| GUI (Windows) | WPF |
| GUI (Linux) | Zenity/YAD dialogs |
| Testing | xUnit + Shouldly + NSubstitute |

## How It Works

### 1. Identity (IAM Module)

Your identity is derived deterministically from a passphrase:

```
Passphrase
    │
    ▼ SHA256
32-byte entropy
    │
    ▼ BIP39
Mnemonic (24 words)
    │
    ▼ PBKDF2
64-byte seed
    │
    ├──▶ Bytes [0-32]  → Ed25519 Signing Key
    │
    └──▶ Bytes [32-64] → X25519 Encryption Key
```

**Same passphrase always generates the same keys** - your identity is portable and recoverable.

### 2. Peer Discovery (Kademlia DHT)

Susurri implements a full Kademlia DHT with:

- **256-bit Node IDs** - SHA256 hash of public key
- **XOR Distance Metric** - Determines routing proximity
- **K-Buckets** - 256 buckets, 20 nodes each, LRU eviction
- **Iterative Lookups** - α=3 parallel queries, k=20 results

**Protocol Messages:**

| Message | Purpose |
|---------|---------|
| PING/PONG | Node liveness |
| FIND_NODE | Iterative node lookup |
| FIND_VALUE | Key-value retrieval |
| STORE | DHT storage with TTL |

**DHT Storage:**
- **Public Key Distribution** - `username → publicKey` mapping
- **Offline Messages** - Encrypted messages for offline users (max 100/user)
- **Automatic Expiration** - TTL-based cleanup every 5 minutes

### 3. Onion Routing (Privacy)

Messages are wrapped in three encryption layers:

```
┌────────────────────────────────────────────────────────────┐
│ Layer 3 (Relay 1's key)                                    │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ Layer 2 (Relay 2's key)                              │  │
│  │  ┌────────────────────────────────────────────────┐  │  │
│  │  │ Layer 1 (Relay 3's key)                        │  │  │
│  │  │  ┌──────────────────────────────────────────┐  │  │  │
│  │  │  │ Message (Recipient's key)                │  │  │  │
│  │  │  │  - Sender Public Key                     │  │  │  │
│  │  │  │  - Content                               │  │  │  │
│  │  │  │  - Timestamp                             │  │  │  │
│  │  │  └──────────────────────────────────────────┘  │  │  │
│  │  └────────────────────────────────────────────────┘  │  │
│  └──────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────┘
```

**Path:** `Sender → Relay1 → Relay2 → Relay3 → Recipient`

Each relay only knows the previous and next hop. The recipient knows the sender (from the decrypted message), but relays cannot determine the communication endpoints.

**Encryption per layer:**
1. Generate ephemeral X25519 keypair
2. ECDH key agreement with relay's public key
3. HKDF-SHA256 key derivation
4. ChaCha20-Poly1305 authenticated encryption

### 4. Message Flow

```
┌────────┐    ┌────────┐    ┌────────┐    ┌────────┐    ┌────────┐
│ Sender │───▶│ Relay1 │───▶│ Relay2 │───▶│ Relay3 │───▶│Recipient│
└────────┘    └────────┘    └────────┘    └────────┘    └────────┘
     │             │             │             │             │
     │   Decrypt   │   Decrypt   │   Decrypt   │   Decrypt   │
     │   Layer 3   │   Layer 2   │   Layer 1   │   Message   │
     │             │             │             │             │
     ▼             ▼             ▼             ▼             ▼
 [Encrypted]  [Next Hop]    [Next Hop]    [Next Hop]   [Plaintext]
```

## Installation

### Arch Linux

```bash
# Quick install (builds from source)
cd installers/arch
./install.sh

# Or using PKGBUILD
makepkg -si -p PKGBUILD.local

# System-wide install
sudo ./install.sh --system
```

See [installers/arch/README.md](installers/arch/README.md) for details.

### Other Linux Distributions

```bash
# Ensure .NET 10.0+ is installed
# Then use the Arch installer (works on most distros)
cd installers/arch
./install.sh
```

### Building from Source

**Prerequisites:**
- .NET SDK 10.0+
- libsodium

```bash
# Clone repository
git clone https://github.com/susurri/susurri.git
cd susurri

# Build CLI
dotnet build src/Bootstrapper/Susurri.CLI/Susurri.CLI.csproj -c Release

# Run
dotnet run --project src/Bootstrapper/Susurri.CLI/Susurri.CLI.csproj
```

### Windows (WPF GUI)

```bash
dotnet build src/Bootstrapper/Susurri.Bootstrapper/Susurri.Bootstrapper.csproj -c Release
```

## Usage

### Terminal Interface

```bash
susurri
```

```
   ____                            _
  / ___| _   _ ___ _   _ _ __ _ __(_)
  \___ \| | | / __| | | | '__| '__| |
   ___) | |_| \__ \ |_| | |  | |  | |
  |____/ \__,_|___/\__,_|_|  |_|  |_|

  Secure P2P Chat with DHT & Onion Routing

  susurri > help

  Available Commands:

  login [username]     - Login with username and passphrase
  logout               - Logout current user
  status               - Show current status
  dht <command>        - DHT node management (see 'dht help')
  ping <host> <port>   - Ping a DHT node
  clear                - Clear screen
  version              - Show version info
  help                 - Show this help
  exit                 - Exit the application
```

### DHT Commands

```bash
susurri > dht help

  DHT Commands:

  dht start [port]     - Start DHT node (default port: 7070)
  dht stop             - Stop DHT node
  dht status           - Show DHT node status
```

### GUI Launcher (Linux)

```bash
susurri-gui
```

Or search for "Susurri" in your application menu.

## Configuration

Configuration file: `~/.config/susurri/appsettings.json`

```json
{
  "Messaging": {
    "UseBackgroundDispatcher": true
  },
  "DHT": {
    "DefaultPort": 7070,
    "BootstrapNodes": [
      { "Host": "bootstrap.susurri.io", "Port": 7070 }
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Susurri": "Information"
    }
  }
}
```

### DHT Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `DefaultPort` | 7070 | UDP/TCP port for DHT |
| `BootstrapNodes` | [] | Initial nodes for joining network |
| `Alpha` | 3 | Parallel lookup queries |
| `K` | 20 | Bucket size / lookup results |
| `RefreshInterval` | 60 min | Bucket refresh period |

## Security Model

### Cryptographic Primitives

| Purpose | Algorithm |
|---------|-----------|
| Key Exchange | X25519 (Curve25519 ECDH) |
| Symmetric Encryption | ChaCha20-Poly1305 (AEAD) |
| Key Encryption at Rest | AES-256-GCM |
| Digital Signatures | Ed25519 |
| Hashing | SHA-256 |
| Key Derivation | HKDF-SHA256, PBKDF2-SHA256 (600k iterations) |

### Privacy Properties

| Property | Mechanism |
|----------|-----------|
| **Sender Anonymity** | Onion routing (3-7 hops) |
| **Content Privacy** | E2E encryption with ephemeral keys |
| **Forward Secrecy** | New ephemeral X25519 keypair per message |
| **Metadata Protection** | DHT obfuscates lookup patterns |
| **Identity Portability** | Deterministic BIP39 derivation |

### Security Defenses Implemented

The following attack vectors are defended against:

#### Cryptographic Defenses

| Attack | Defense |
|--------|---------|
| **Key Theft (at rest)** | Private keys encrypted with AES-256-GCM using passphrase-derived keys (PBKDF2, 600k iterations) |
| **Brute Force (key derivation)** | High iteration count (600,000) makes offline attacks computationally expensive |
| **Timing Attacks** | Constant-time comparisons via `CryptographicOperations.FixedTimeEquals()` |
| **Nonce Reuse** | Fresh random nonce generated for every encryption operation |
| **Key Recovery** | Ephemeral keys ensure forward secrecy; compromised key doesn't reveal past messages |
| **Weak Entropy** | All random data from `RandomNumberGenerator` (CSPRNG) |

#### Input Validation Defenses

| Attack | Defense |
|--------|---------|
| **Buffer Overflow** | Strict size limits on all deserialized data (max 64KB messages, 32KB values) |
| **Memory Exhaustion (DoS)** | Maximum sizes enforced: values (32KB), messages (64KB), strings (1KB), nodes (20 per response) |
| **Malformed Data** | Length validation before reading; invalid data throws `InvalidDataException` |
| **Username Injection** | Usernames validated: 3-32 chars, alphanumeric + underscore/hyphen only |
| **Path Traversal** | No user-controlled file paths; fixed secure directories |

#### Network Defenses

| Attack | Defense |
|--------|---------|
| **Protocol Injection** | Binary protocol with typed messages; unknown types rejected |
| **Oversized Payloads** | Size limits on all protocol messages prevent memory exhaustion |
| **Invalid Public Keys** | Public key size validated (exactly 32 bytes) before cryptographic operations |
| **Malformed IP Addresses** | IP address length validated (max 16 bytes) during deserialization |

#### Memory Security

| Attack | Defense |
|--------|---------|
| **Memory Disclosure** | Sensitive data (keys, passphrases) zeroed with `CryptographicOperations.ZeroMemory()` |
| **Credential Leakage** | Credentials stored as byte arrays (clearable) instead of immutable strings |
| **Key Material in Memory** | Automatic disposal of cryptographic keys via `using` statements |
| **Secure File Deletion** | Key files overwritten with zeros before deletion |

#### File System Security

| Attack | Defense |
|--------|---------|
| **Unauthorized Access** | Unix file permissions set to user-only (700) on key directories |
| **Plaintext Credentials** | All stored credentials encrypted with AES-256-GCM |
| **Weak PIN Protection** | Minimum 8-character password required (not 4-digit PIN) |

#### Traffic Analysis Defenses

| Attack | Defense |
|--------|---------|
| **Message Size Correlation** | Fixed 16KB message padding - all messages appear identical in size |
| **Content Length Analysis** | Random padding bytes (not zeros) prevent compression-based inference |
| **Timing Correlation** | Onion routing with multiple hops obscures timing patterns |

#### Passphrase Security

| Attack | Defense |
|--------|---------|
| **Weak Passphrases** | Minimum 6 words required (enforced); recommended 12-24 BIP39 words |
| **Passphrase Guessing** | BIP39 wordlist provides 2048 words; 12 words = 128 bits entropy |
| **User-Generated Weakness** | Built-in cryptographic passphrase generator using CSPRNG |
| **Passphrase Reuse** | Deterministic keys mean same passphrase = same identity (feature, not bug) |

#### Group Messaging Security

| Attack | Defense |
|--------|---------|
| **Group Key Theft** | Group keys wrapped (encrypted) individually for each member |
| **Unauthorized Access** | Members must have valid wrapped key to decrypt group messages |
| **Key Compromise** | Key rotation support allows generating new group key |
| **Member Tracking** | Group messages padded to fixed size like direct messages |

### Security Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Application Layer                         │
├─────────────────────────────────────────────────────────────┤
│  Input Validation │ Username/Message validation, size limits │
├─────────────────────────────────────────────────────────────┤
│                    Crypto Layer                              │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────────────────┐│
│  │ X25519 ECDH │ │ ChaCha20    │ │ PBKDF2 Key Derivation   ││
│  │ Key Exchange│ │ Poly1305    │ │ (600k iterations)       ││
│  └─────────────┘ └─────────────┘ └─────────────────────────┘│
├─────────────────────────────────────────────────────────────┤
│                    Storage Layer                             │
│  ┌─────────────────────────────────────────────────────────┐│
│  │ AES-256-GCM encrypted keys │ Secure memory wiping       ││
│  └─────────────────────────────────────────────────────────┘│
├─────────────────────────────────────────────────────────────┤
│                    Network Layer                             │
│  ┌─────────────────────────────────────────────────────────┐│
│  │ Size-limited messages │ Validated deserialization       ││
│  └─────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────┘
```

### Remaining Considerations

| Risk | Mitigation Status |
|------|-------------------|
| Traffic Analysis | Partial - onion routing hides endpoints; no cover traffic yet |
| Global Adversary | Partial - timing correlation still possible |
| Bootstrap Trust | Initial nodes must be trusted; consider multiple bootstrap sources |
| Sybil Attacks | DHT vulnerable; consider proof-of-work or reputation systems |

## Development

### Project Structure

```
src/
├── Bootstrapper/
│   ├── Susurri.CLI/              # Cross-platform CLI
│   │   └── Program.cs            # Interactive command loop
│   └── Susurri.Bootstrapper/     # WPF GUI (Windows)
│
├── Modules/
│   ├── DHT/
│   │   └── Susurri.Modules.DHT.Core/
│   │       ├── Kademlia/         # DHT implementation
│   │       │   ├── KademliaId.cs
│   │       │   ├── KBucket.cs
│   │       │   ├── RoutingTable.cs
│   │       │   ├── KademliaNode.cs
│   │       │   ├── KademliaDhtNode.cs
│   │       │   ├── Storage/
│   │       │   └── Protocol/     # PING, FIND_NODE, STORE, etc.
│   │       ├── Network/          # Transport layer
│   │       │   ├── UdpTransport.cs
│   │       │   ├── RelayService.cs
│   │       │   └── ConnectionManager.cs
│   │       ├── Onion/            # Onion routing
│   │       │   ├── OnionBuilder.cs
│   │       │   ├── OnionRouter.cs
│   │       │   ├── OnionLayer.cs
│   │       │   └── ChatMessage.cs
│   │       └── Services/
│   │           └── ChatService.cs
│   │
│   ├── IAM/
│   │   └── Susurri.Modules.IAM.Core/
│   │       ├── Crypto/
│   │       │   ├── CryptoKeyGenerator.cs  # BIP39 key derivation
│   │       │   └── KeyPair.cs
│   │       └── Keys/
│   │           ├── KeyStorage.cs
│   │           └── CredentialsCache.cs
│   │
│   └── Users/
│       └── Susurri.Modules.Users.Core/
│           ├── DAL/
│           │   ├── UsersDbContext.cs
│           │   └── Repositories/
│           └── Entities/
│               └── User.cs
│
└── Shared/
    ├── Susurri.Shared.Abstractions/
    │   ├── Commands/             # ICommand, ICommandHandler
    │   ├── Events/               # IEvent, IEventHandler
    │   ├── Queries/              # IQuery, IQueryHandler
    │   └── Modules/              # IModule interface
    └── Susurri.Shared.Infrastructure/
        └── Messaging/
            └── InMemoryMessageBroker.cs
```

### Adding a New Module

1. Create project in `src/Modules/YourModule/`
2. Implement `IModule` interface:

```csharp
public class YourModule : IModule
{
    public string Name => "Your Module";

    public void Register(IServiceCollection services)
    {
        // Register services
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        // Post-registration setup
    }
}
```

3. Reference in bootstrapper project
4. Module will be auto-discovered and loaded

### Running Tests

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/Susurri.Tests.Unit/

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Test Coverage

| Component | Coverage |
|-----------|----------|
| Kademlia (DHT) | KademliaId, KBucket, RoutingTable, DhtStorage |
| Onion Routing | OnionBuilder, OnionLayer, OnionRouter |
| Cryptography | CryptoKeyGenerator, OnionCrypto |
| Network | UdpTransport, RelayProtocol, ConnectionManager |
| Services | ChatService |

## Roadmap

- [ ] **NAT Traversal** - STUN/TURN support for nodes behind NAT
- [ ] **Message Signatures** - Ed25519 signature verification
- [x] **Encrypted Key Storage** - Private keys protected with AES-256-GCM at rest
- [x] **Input Validation** - Comprehensive validation against injection and DoS
- [x] **Secure Memory Handling** - Sensitive data wiped from memory after use
- [ ] **Group Chat** - Multi-party encrypted conversations
- [ ] **File Transfer** - Encrypted file sharing over onion routes
- [ ] **Mobile Apps** - iOS and Android clients
- [ ] **Cover Traffic** - Dummy messages to prevent traffic analysis
- [ ] **Bridge Nodes** - Bypass network restrictions

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

Please ensure:
- All tests pass (`dotnet test`)
- Code follows existing style
- New features have tests
- Update documentation as needed

## License

MIT License - See [LICENSE](LICENSE) for details.

## Acknowledgments

- **Kademlia** - Petar Maymounkov and David Mazières
- **Tor Project** - Onion routing inspiration
- **BIP39** - Mnemonic code for deterministic keys
- **libsodium** - Cryptographic primitives
- **.NET Community** - Excellent tooling and libraries

---

**Susurri** - *Whisper in the network*
