# Windows release pipeline

Windows builds ship through [Velopack](https://velopack.io). The hand-rolled WPF installer that used to live in this directory is gone; `.github/workflows/release-windows.yml` builds and publishes every artifact.

## Cutting a release

```
git tag v1.2.3
git push origin v1.2.3
```

Pushing a `v*` tag runs `release-windows.yml` on `windows-latest`. The version is the tag without the leading `v`. Triggering the workflow manually (`workflow_dispatch` with a `version` input) is a dry run: it builds and packs the same artifacts but attaches them to the workflow run instead of creating a GitHub release.

## What the workflow does

1. Publishes `src/Bootstrapper/Susurri.GUI/Susurri.GUI.csproj` self-contained for `win-x64` into a staging directory.
2. Publishes `src/Bootstrapper/Susurri.CLI/Susurri.CLI.csproj` (binary name `susurri-cli`) self-contained for `win-x64` into the same staging directory, so the CLI ships inside the same install.
3. Installs the Velopack CLI (`dotnet tool install -g vpk`) and pulls the previous GitHub release with `vpk download github`, which is what enables delta package generation.
4. Packs the staging directory:

   ```
   vpk pack --packId Susurri --packVersion <version> --packDir staging --mainExe Susurri.GUI.exe --packTitle Susurri --packAuthors Susurri --outputDir Releases
   ```

5. Copies `Susurri-win-Setup.exe` to `susurri-setup-x64.exe` (the filename the landing page advertises), writes `SHA256SUMS` over all assets, and creates the GitHub release for the tag with everything attached via `gh release create`.

## What vpk produces

- `Susurri-win-Setup.exe` — the installer; `susurri-setup-x64.exe` is a byte-identical copy.
- `Susurri-win-Portable.zip` — run-in-place build, no install.
- `Susurri-<version>-full.nupkg` — the complete package Velopack installs and updates from.
- `Susurri-<version>-delta.nupkg` — emitted when the previous release was available to diff against; contains only the changes.
- `releases.win.json` — the update feed manifest.

The update artifacts (`*.nupkg`, `releases.win.json`) must reach the GitHub release unmodified — renaming them breaks the update feed. Only the setup exe gets an extra renamed copy.

## In-app delta updates

Velopack apps self-update through `Velopack.UpdateManager` pointed at the GitHub releases feed (`GithubSource` over `https://github.com/MagicCactus42/susurri`). The manager reads the feed from the latest release, downloads the delta package (falling back to the full one), applies it in the background, and swaps the versioned install directory on restart. The GUI does not reference Velopack yet — wiring `VelopackApp.Build().Run()` into startup plus an `UpdateManager` check is the remaining integration step; the release side already produces everything it needs.

## Install layout

Velopack installs per-user under `%LocalAppData%\Susurri`. The Start Menu shortcut `Susurri` launches `Susurri.GUI.exe`. Application files live in `%LocalAppData%\Susurri\current\`, including `susurri-cli.exe` — call it from a terminal directly or put that folder on `PATH`.

## Testing a release build locally

On a Windows machine:

```
dotnet publish src/Bootstrapper/Susurri.GUI/Susurri.GUI.csproj -c Release -r win-x64 --self-contained -o staging
dotnet publish src/Bootstrapper/Susurri.CLI/Susurri.CLI.csproj -c Release -r win-x64 --self-contained -o staging
dotnet tool install -g vpk
vpk pack --packId Susurri --packVersion 0.0.1 --packDir staging --mainExe Susurri.GUI.exe --packTitle Susurri --packAuthors Susurri --outputDir Releases
Releases\Susurri-win-Setup.exe
```

To exercise an update, pack again with a higher `--packVersion` while the Releases directory still holds the earlier output — vpk will emit a delta, and a locally hosted feed (`vpk` docs: `UpdateManager` with a file path source) will apply it.
