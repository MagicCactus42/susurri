# Signing — releases, commits, and the site claim

Three independent things carry a signature in Susurri. Set all three up so the chain from "a commit you pushed" to "a binary a stranger downloads" is verifiable end to end.

1. **Release artifacts** — the `SHA256SUMS` manifest is GPG-signed in CI, so a download can be proven byte-for-byte what CI built.
2. **Git commits** — every commit is signed so GitHub shows "Verified" and history can't be silently forged.
3. **The site** — `docs/` claims a specific release key fingerprint; it must match the real one, and right now it is a placeholder.

Everything below is one-time setup you run locally, plus two GitHub secrets.

---

## 1. Release signing key (GPG)

CI signs `SHA256SUMS` with a dedicated key. Generate one that is used **only** for releases — never your personal identity key.

```bash
# generate an ed25519 signing key, no expiry prompt fuss
gpg --batch --quick-generate-key "Susurri releases <releases@YOURDOMAIN>" ed25519 sign 2y

# find the key id (the long hex after 'sec   ed25519/')
gpg --list-secret-keys --keyid-format long

# full fingerprint (the 40-hex value you publish everywhere)
gpg --fingerprint releases@YOURDOMAIN
```

Export the pieces CI needs:

```bash
# private key (goes into a GitHub secret — treat like a password)
gpg --armor --export-secret-keys releases@YOURDOMAIN > release-private.asc

# public key (publish it — repo + keyserver)
gpg --armor --export releases@YOURDOMAIN > deploy/release-pubkey.asc
gpg --keyserver keys.openpgp.org --send-keys <LONG_KEY_ID>
```

Add two repository secrets (Settings → Secrets and variables → Actions):

- `GPG_PRIVATE_KEY` — the entire contents of `release-private.asc`.
- `GPG_PASSPHRASE` — the passphrase you set on the key.

Then **delete `release-private.asc` from disk** (`shred -u release-private.asc`). Commit `deploy/release-pubkey.asc` so anyone can import the key from the repo, not only a keyserver.

### What CI does with it

`.github/workflows/release.yml` gathers the artifacts from every platform job (Windows setup, Linux AppImage, macOS pkgs, CLI tarballs), generates one combined `SHA256SUMS`, imports the key and runs:

```
gpg --armor --detach-sign --output Releases/SHA256SUMS.asc Releases/SHA256SUMS
gpg --verify Releases/SHA256SUMS.asc Releases/SHA256SUMS
```

`SHA256SUMS.asc` is uploaded alongside the release. If the `GPG_PRIVATE_KEY` secret is absent the step logs a warning and ships **unsigned** — so the pipeline never breaks, but you get an unsigned release until the secret exists.

### What a user runs to verify

```
gpg --keyserver keys.openpgp.org --recv-keys <LONG_KEY_ID>
gpg --verify SHA256SUMS.asc SHA256SUMS
sha256sum -c SHA256SUMS
```

---

## 2. Fix the site fingerprint (pre-launch blocker)

The landing page now lives in its own repo: **github.com/MagicCactus42/susurri-site**. It ships a **placeholder** fingerprint. Until it is replaced, the download page claims a key you do not control — do not launch the site until this is done.

In the **susurri-site** repo, replace these exact strings in **both** `docs/index.html` and `docs/site.js`:

| Placeholder | Replace with |
|---|---|
| `7C4A1E9B` (in `--recv-keys 7C4A1E9B`) | your long key id |
| `releases@susurri` | `releases@YOURDOMAIN` |
| `7C4A 1E9B 55D2 08F3 A6C1  9E44 B02D 7731 C58A 60ED` | your real 40-hex fingerprint, same spacing |

`docs/site.js` line ~96 and `docs/index.html` "verify your download" block both contain the same block — change both. The site's two-channel check tells users the README pins the same key, so also add the fingerprint to this repo's `README.md`.

Pushing to `susurri-site` triggers its `deploy-site.yml` and redeploys the site.

---

## 3. Sign your commits

You already push from your own terminal with an SSH key — SSH commit signing reuses it, no GPG needed. This is the least-friction path.

```bash
# use your existing SSH key as the commit signer
git config --global gpg.format ssh
git config --global user.signingkey ~/.ssh/id_ed25519.pub
git config --global commit.gpgsign true
git config --global tag.gpgsign true
```

Add the **same public key** to GitHub a second time as a *signing* key (Settings → SSH and GPG keys → New SSH key → key type: Signing) — an authentication key and a signing key are separate entries even for the same key. After that, your commits show "Verified" on GitHub.

Verify locally:

```bash
git commit --allow-empty -m "test: signing"
git log --show-signature -1
```

Prefer GPG commit signing instead? Use a **separate** key from the release key:

```bash
git config --global gpg.format openpgp
git config --global user.signingkey <YOUR_PERSONAL_GPG_KEYID>
git config --global commit.gpgsign true
```

To enforce it on the repo (optional), enable branch protection → "Require signed commits" on `main`.

---

## 4. Android APK signing

Every push to `main` builds an APK (`.github/workflows/android.yml`), and every tagged release attaches `susurri-android.apk` covered by the GPG-signed `SHA256SUMS` (`release.yml`, android job). Android requires every APK to be signed; without your keystore both workflows fall back to a **throwaway debug signature** and log a warning — installable, but every run signs with a different identity, so updates won't install over an old build and users can't pin a signer.

Generate a dedicated release keystore once:

```bash
bash scripts/generate-android-keystore.sh
```

The script creates `susurri-release.keystore` (RSA-4096, PKCS12, ~30 years) and prints the four `gh secret set` commands to run:

- `ANDROID_KEYSTORE_BASE64` — the keystore file, base64-encoded.
- `ANDROID_KEYSTORE_PASS` — the keystore password.
- `ANDROID_KEY_PASS` — the key password (same value for PKCS12).
- `ANDROID_KEY_ALIAS` — the key alias (defaults to `susurri` if unset).

Then **back the keystore up offline and `shred -u` the local copy**. Unlike the GPG key this one is unrecoverable-critical: Android identifies an app by its signing certificate, so a lost keystore means existing installs can never update.

### What CI does with it

`android.yml` decodes the keystore into the runner's temp dir and passes it to `dotnet publish` via `AndroidKeyStore`/`AndroidSigningKeyStore` properties (passwords travel as `env:` references, never on the command line), then proves the result with:

```
apksigner verify --print-certs com.susurri.app-Signed.apk
```

The signed APK is uploaded as the `susurri-android-apk` workflow artifact on every build.

### What a user runs to verify

```
apksigner verify --print-certs susurri.apk
```

and compares the certificate digest against the one you publish (add it to `README.md` next to the GPG fingerprint once the keystore exists).

---

## 5. Windows Authenticode (optional, later)

GPG proves the `SHA256SUMS` manifest; it does not stop Windows SmartScreen warning on an unsigned `.exe`. Authenticode signing of `susurri-setup-x64.exe` needs a code-signing certificate (OV or EV from a CA, or Azure Trusted Signing). When you have one, add a step in `release.yml` (windows job) after `vpk pack`:

```
# with a PFX in secrets (base64) + password:
signtool sign /fd SHA256 /f cert.pfx /p $env:CERT_PASSWORD /tr http://timestamp.digicert.com /td SHA256 Releases/*.exe
```

Velopack can also invoke a signing command during `vpk pack` via `--signParams`. Until then, the GPG-signed `SHA256SUMS` is the trust anchor and the site documents that flow.

---

## 6. macOS notarization (optional, later)

The `.pkg` installers ship unsigned, so Gatekeeper warns on first launch (right-click → Open works). Proper fixes need an Apple Developer ID ($99/yr): `vpk pack` accepts `--signAppIdentity`/`--signInstallIdentity` plus `--notaryProfile` on a macOS runner with the certificates imported into the keychain. Until then the GPG-signed `SHA256SUMS` is the trust anchor, same as on Windows.

---

## Summary — what you must do

- [ ] Generate the release GPG key, add `GPG_PRIVATE_KEY` + `GPG_PASSPHRASE` secrets, commit `deploy/release-pubkey.asc`, publish to a keyserver.
- [ ] Replace the three placeholder strings in `docs/index.html` and `docs/site.js`, and pin the fingerprint in `README.md`.
- [ ] Turn on commit signing (SSH is easiest) and add the signing key to GitHub.
- [ ] Generate the Android keystore (`scripts/generate-android-keystore.sh`), add the four `ANDROID_*` secrets, back the keystore up offline.
- [ ] (Later) Buy a code-signing cert and add Authenticode to the release workflow.
