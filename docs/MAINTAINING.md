# Maintaining Shadepane

Notes for whoever builds, releases and publishes Shadepane (and for forks that want to do the same).
Users and administrators need only the [README](../README.md).

## Branches and CI

Development happens on `develop`; `main` holds released code. CI (`.github/workflows/ci.yml`, Windows runner)
builds and tests every push and pull request to both branches and uploads two artifacts:

- `Shadepane-win-x64` — installer, portable zip and `SHA256SUMS.txt`;
- `Shadepane-msix` — the Microsoft Store package (see [Microsoft Store](#microsoft-store)).

A `vX.Y.Z` tag additionally publishes a GitHub release with the installer, zip and checksums.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet test                                  # core tests (also run on Linux)
dotnet run --project src\Shadepane.App       # run the browser (Windows only)
```

Self-contained build and installer, as produced by CI (installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php),
e.g. `winget install --id JRSoftware.InnoSetup -e`). Run the commands one by one; the installer lands in `dist\`:

```powershell
dotnet publish src\Shadepane.App -c Release -r win-x64 --self-contained true -p:Version=0.2.0 -o publish\Shadepane
# Inno Setup is in Program Files, or in %LOCALAPPDATA%\Programs when installed without admin rights
$iscc = (Get-ChildItem "$env:ProgramFiles\Inno Setup*", "${env:ProgramFiles(x86)}\Inno Setup*", "$env:LOCALAPPDATA\Programs\Inno Setup*" -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
& $iscc /DAppVersion=0.2.0 "/DSourceDir=$PWD\publish\Shadepane" installer\Shadepane.iss
```

The UI, WebView2 behaviour and capture protection are not covered by automated tests; check them by hand on
Windows before a release: [`docs/TESTING.md`](TESTING.md).

The app icon is built from `design/app-icon.svg`: `python3 design/build-icon.py` (needs `rsvg-convert` and Pillow)
writes `src/Shadepane.App/Assets/Shadepane.ico` and the MSIX logos in `installer/msix/Assets/`.

## Releasing

Merge `develop` into `main` and push a `vX.Y.Z` tag; CI publishes the release:

```powershell
git checkout main
git merge --ff-only develop
git tag -a v0.3.0 -m "Shadepane 0.3.0"
git push origin main v0.3.0
```

Installed copies find the new release through the update check (see the README, section "Updates").

## Microsoft Store

The Store version is the MSIX package from the `Shadepane-msix` artifact of a tag build (product Shadepane,
publisher Quietglass). The Store signs and updates it; the GitHub update check is off in that version.
Packaging, local testing, screenshots, listing texts and certification notes: [`docs/STORE.md`](STORE.md).

## Code signing

Release builds are signed in CI with [Azure Artifact Signing](https://azure.microsoft.com/pricing/details/artifact-signing/)
once it is configured; until then the GitHub builds are unsigned. Since 2023 code-signing keys must live in hardware
or a cloud HSM, so a `.pfx` in a GitHub secret is no longer an option for new certificates. To enable signing:

1. Create an Artifact Signing account and a certificate profile (identity validation is done by Microsoft).
2. Create an app registration / service principal and give it the certificate-profile signer role on the profile.
3. In the repository settings add variables `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`,
   `ARTIFACT_SIGNING_PROFILE` and secrets `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`.

The next `vX.Y.Z` tag then signs `Shadepane.exe`, `Shadepane.dll`, `Shadepane.Core.dll` and the installer
before checksums are computed. A signed browser accepts only updates signed by the same publisher.
A free alternative for open-source projects is [SignPath Foundation](https://signpath.org/) (the publisher
shown is SignPath Foundation; its GitHub Actions integration has to be set up with SignPath).
