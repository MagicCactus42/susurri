# Susurri.Tests.Fuzz

Coverage-guided fuzzing harness for Susurri's protocol parsers.

The console app exposes one fuzz target per `Deserialize` entry point —
`KademliaMessage`, `OnionLayer`, `OnionLayerContent`, `ChatMessage`,
`UserPublicKeyRecord`, `FileTransferMessage`, `RecipientPayload`, `ReplyPath`,
`ReplyTokenContent`, `GroupKey`, `WrappedGroupKey`, `GroupMessage`,
`EncryptedGroupMessage`. List them with `susurri-fuzz list`.

Each target either parses the input or throws one of the "graceful rejection"
exception types (`InvalidDataException`, `EndOfStreamException`,
`OverflowException`, `ArgumentException`, `FormatException`, `IOException`).
Anything else escaping the target is recorded as a crash.

## Quick local smoke (no AFL needed)

```bash
# Run 10 000 random inputs against the Kademlia parser
dotnet run --project tests/Susurri.Tests.Fuzz -- kademlia --smoke

# 30-second time-bounded fuzz with a fixed seed for reproducibility
dotnet run --project tests/Susurri.Tests.Fuzz -- onion-layer --smoke \
    --seconds 30 --seed 42

# Save crash-triggering inputs to a custom directory
dotnet run --project tests/Susurri.Tests.Fuzz -- chat --smoke \
    --crashes /tmp/susurri-crashes
```

Smoke mode is **not** coverage-guided — it just generates uniformly random
bytes. It will catch regressions and obvious null-deref / OOB / OOM bugs but
won't explore parser branches strategically. For real fuzzing, use AFL below.

## Coverage-guided fuzzing with AFL

[SharpFuzz](https://github.com/Metalnem/sharpfuzz) is wired up in `Program.cs`
and the harness uses `Fuzzer.Run(Action<Stream>)` so it works under both
`afl-fuzz` and libFuzzer-style drivers.

### Prerequisites
1. Install AFL++ on the machine running the fuzzer:
   - Arch: `pacman -S aflplusplus`
   - Debian/Ubuntu: `apt install afl++` (or build from source)
   - Other: see https://github.com/AFLplusplus/AFLplusplus
2. Install the `sharpfuzz` instrumentation tool:
   ```bash
   dotnet tool install --global SharpFuzz.CommandLine
   ```

### Workflow

```bash
# 1. Build a release-mode binary
dotnet publish tests/Susurri.Tests.Fuzz -c Release -o publish-fuzz

# 2. Instrument the DHT.Core assembly so afl-fuzz sees coverage edges
sharpfuzz publish-fuzz/Susurri.Modules.DHT.Core.dll

# 3. Seed corpus — start with a couple of known-good message bytes
mkdir -p corpus/kademlia
echo -ne '\x01' > corpus/kademlia/empty-ping  # 1-byte type tag

# 4. Run afl-fuzz for 5 minutes (or longer)
afl-fuzz -i corpus/kademlia -o findings/ -V 300 -- \
    publish-fuzz/susurri-fuzz kademlia
```

Crashes appear under `findings/default/crashes/`. Each is the byte stream
that triggered the unexpected exception. Reproduce with:

```bash
publish-fuzz/susurri-fuzz kademlia < findings/default/crashes/id:000000,*
```

### CI cadence (Phase 5 work)

The roadmap calls for a nightly 5-minute fuzzing run per target with build
failure on any crash, allocation > limit, or hang > 30s. AFL provides the
crash + hang detection out of the box; allocation bounds need a separate
limit (e.g. running under a memory cgroup or using `ulimit -v`).

This is not yet wired into CI — Susurri has no CI today. When Phase 5 lands,
the workflow YAML will:
1. Install AFL and SharpFuzz
2. Run `sharpfuzz` against the published assemblies
3. Loop over each target, running `afl-fuzz -V 300`
4. Fail the build if `findings/*/crashes/` contains anything
5. Upload `findings/` as an artifact for triage

## Why both AFL and a smoke mode?

The smoke mode lets the harness be invoked from xUnit (see
`Susurri.Tests.Unit.Properties.FuzzSmokeTests`) and from `dotnet run`
without any native dependency. AFL is the real fuzzer — much higher coverage
per second and the only thing that finds the deep bugs — but it's a heavy
dependency that shouldn't be required for routine builds.
