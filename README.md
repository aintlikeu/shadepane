# Shadepane

A Windows browser that keeps its window out of screenshots and screen recordings.

Shadepane is built on [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) (Chromium,
updated automatically by Microsoft) and excludes all of its windows — including dropdowns, menus and
dialogs — from screen capture (`WDA_EXCLUDEFROMCAPTURE`). It also closes the in-browser leak paths:
`getDisplayMedia`, printing, DevTools, downloads, save/print in the PDF viewer.

Unpacked Chrome extensions are supported. Every extension is checked against the policy before it is
loaded: capture permissions, access to all sites and access to protected hosts are refused.

Everyday browsing: tabs, search from the address bar (Startpage by default; DuckDuckGo, Brave Search,
Google, Bing or a custom template), home page, bookmarks and a bookmarks bar, history, downloads, find in
page, zoom and the usual keyboard shortcuts.

> Capture exclusion is a Windows feature, not DRM: it stops software, not a camera pointed at the screen.

## Download

From [Releases](https://github.com/aintlikeu/shadepane/releases):

- `Shadepane-<version>-win-x64-setup.exe` — installer. Installs for the current user without admin rights
  (`%LOCALAPPDATA%\Programs\Shadepane`); choose "for all users" (or run with `/ALLUSERS`) to install into
  Program Files. Start menu shortcut, optional desktop shortcut, uninstaller. Silent install: `/SILENT`.
- `Shadepane-<version>-win-x64.zip` — portable: unzip and run `Shadepane.exe`.
- `SHA256SUMS.txt` — checksums of both files.

Requirements: Windows 10 2004+ or Windows 11, and the
[WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on Windows 11).
.NET is bundled. Builds are not code-signed yet (see [Code signing](#code-signing)), so SmartScreen may warn
on first run (*More info* → *Run anyway*).

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet test                                  # core tests (also run on Linux)
dotnet run --project src\Shadepane.App       # run the browser (Windows only)
```

Self-contained build and installer, as produced by CI (installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php),
e.g. `winget install --id JRSoftware.InnoSetup -e`). Run the commands one by one; the installer lands in `dist\`:

```powershell
dotnet publish src\Shadepane.App -c Release -r win-x64 --self-contained true -p:Version=0.1.0 -o publish\Shadepane
# Inno Setup is in Program Files, or in %LOCALAPPDATA%\Programs when installed without admin rights
$iscc = (Get-ChildItem "$env:ProgramFiles\Inno Setup*", "${env:ProgramFiles(x86)}\Inno Setup*", "$env:LOCALAPPDATA\Programs\Inno Setup*" -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
& $iscc /DAppVersion=0.1.0 "/DSourceDir=$PWD\publish\Shadepane" installer\Shadepane.iss
```

The UI is checked by hand on Windows: [`docs/TESTING.md`](docs/TESTING.md).

Check an unpacked extension against a policy without starting the browser:

```powershell
dotnet run tools/check-extension.cs -- <extension-folder> [policy.json]
```

## Policy

Settings come from a JSON policy (see [`samples/policies/default.json`](samples/policies/default.json)).
Shadepane uses the first source that exists:

1. Registry `HKLM\Software\Policies\Shadepane`, value `Policy` (REG_SZ, JSON) — recommended.
2. `C:\ProgramData\Shadepane\policy.json` — accepted only if the file and the folder are writable by
   Administrators/SYSTEM only; otherwise the browser refuses to start.
3. Debug builds only: `policy.json` next to the executable.

Downloads and clipboard access for extensions are allowed by default and can be switched off by the user;
set `allowDownloads: false` / `allowExtensionClipboardRead: false` in the policy to enforce a block.

If no source exists, built-in defaults are used. An invalid or insecure source stops the browser instead
of falling back to defaults.

Set the policy from an elevated PowerShell:

```powershell
New-Item -Path 'HKLM:\Software\Policies\Shadepane' -Force | Out-Null
New-ItemProperty -Path 'HKLM:\Software\Policies\Shadepane' -Name Policy -PropertyType String `
  -Value (Get-Content -Raw samples\policies\default.json) -Force | Out-Null
```

Main options:

| Option | Default | Meaning |
|---|---|---|
| `homeUrl` | not set | Home page; when set, users cannot change it (default: the search engine home page) |
| `searchUrl` | not set | Search template with `{query}` (https); when set, users cannot change the search engine (default: Startpage) |
| `protectedHosts` | `[]` | Hosts extensions may not access and downloads from which are blocked unless `allowDownloads` is `true`, e.g. `"*.corp.example"` |
| `blockedPermissions` | capture, `activeTab`, `tabs`, `history`, `downloads`, … | Extension permissions that are refused |
| `allowedPermissions` | not set | Allowlist mode: only these extension permissions are accepted |
| `allowExtensionClipboardRead` | not set | Allow extensions with `clipboardRead`; when set, users cannot change it (default: allowed) |
| `allowBroadHostAccess` | `false` | Allow `<all_urls>` / `*://*/*` |
| `allowDownloads` | not set | Allow file downloads; when set, users cannot change it (default: allowed) |
| `checkForUpdates` | not set | Check GitHub Releases for updates; when set, users cannot change it (default: on) |
| `blockInRemoteSession` | `true` | Refuse to run in an RDP session |

User preferences (home page, search engine, downloads, clipboard access for extensions, history,
bookmarks bar) are set in the settings
window (⚙) and stored in `%LOCALAPPDATA%\Shadepane\settings.json`. A value set by the policy wins and is
shown as locked. Browser data and the audit log (`audit.log`) live in the same folder.

## Updates

When installed with the installer, Shadepane checks the latest GitHub release a few seconds after start
(and on "Check now" in settings) and offers an update button. The update is downloaded only from this
repository's release assets, verified against the release's `SHA256SUMS.txt` and, if the running browser
is code-signed, required to carry a valid signature from the same publisher; then it is installed silently
and the browser restarts. The portable zip build opens the release page instead. The check sends a request
to `api.github.com`; it can be switched off in settings or enforced with `checkForUpdates`.

## Code signing

Release builds are signed in CI with [Azure Artifact Signing](https://azure.microsoft.com/pricing/details/artifact-signing/)
once it is configured; until then they are unsigned. Since 2023 code-signing keys must live in hardware or a
cloud HSM, so a `.pfx` in a GitHub secret is no longer an option for new certificates. To enable signing:

1. Create an Artifact Signing account and a certificate profile (identity validation is done by Microsoft).
2. Create an app registration / service principal and give it the certificate-profile signer role on the profile.
3. In the repository settings add variables `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`,
   `ARTIFACT_SIGNING_PROFILE` and secrets `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`.

The next `vX.Y.Z` tag then signs `Shadepane.exe`, `Shadepane.dll`, `Shadepane.Core.dll` and the installer
before checksums are computed. A signed browser accepts only updates signed by the same publisher.
A free alternative for open-source projects is [SignPath Foundation](https://signpath.org/) (the publisher
shown is SignPath Foundation; its GitHub Actions integration has to be set up with SignPath).

## Releasing

Development happens on `develop`; `main` holds released code. CI builds and tests every push to both.
To release, merge `develop` into `main` and push a `vX.Y.Z` tag — CI publishes a release with the installer, zip and checksums:

```powershell
git checkout main
git merge --ff-only develop
git tag v0.1.0
git push origin main v0.1.0
```

## License

[MIT](LICENSE). Toolbar icons: [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT), see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
