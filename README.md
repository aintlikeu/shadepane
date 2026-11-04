# Shadepane

A Windows browser that keeps its window out of screenshots and screen recordings.

Shadepane is built on [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) (Chromium,
updated automatically by Microsoft) and excludes all of its windows — including dropdowns, menus and
dialogs — from screen capture (`WDA_EXCLUDEFROMCAPTURE`). It also closes the in-browser leak paths:
`getDisplayMedia`, printing, DevTools, downloads, save/print in the PDF viewer.

Unpacked Chrome extensions are supported. Every extension is checked against the policy before it is
loaded: capture permissions, access to all sites and access to protected hosts are refused.

> Capture exclusion is a Windows feature, not DRM: it stops software, not a camera pointed at the screen.

## Download

Get `Shadepane-<version>-win-x64.zip` from [Releases](https://github.com/aintlikeu/shadepane/releases), unzip it and run `Shadepane.exe`.

Requirements: Windows 10 2004+ or Windows 11, and the
[WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on Windows 11).
.NET is bundled. Builds are not code-signed yet, so SmartScreen may warn on first run
(*More info* → *Run anyway*).

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet test                                  # core tests (also run on Linux)
dotnet run --project src\Shadepane.App       # run the browser (Windows only)
```

Self-contained build, as produced by CI:

```powershell
dotnet publish src\Shadepane.App -c Release -r win-x64 --self-contained true -o publish\Shadepane
```

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
| `searchUrl` | not set | Search template with `{query}` (https); when set, users cannot change the search engine (default: DuckDuckGo) |
| `protectedHosts` | `[]` | Hosts extensions may not access, e.g. `"*.corp.example"` |
| `blockedPermissions` | capture, `activeTab`, `tabs`, `history`, `downloads`, … | Extension permissions that are refused |
| `allowedPermissions` | not set | Allowlist mode: only these extension permissions are accepted |
| `allowExtensionClipboardRead` | not set | Allow extensions with `clipboardRead`; when set, users cannot change it (default: allowed) |
| `allowBroadHostAccess` | `false` | Allow `<all_urls>` / `*://*/*` |
| `allowDownloads` | not set | Allow file downloads; when set, users cannot change it (default: allowed) |
| `blockInRemoteSession` | `true` | Refuse to run in an RDP session |

User preferences (home page, search engine, downloads, clipboard access for extensions, history,
bookmarks bar) are set in the settings
window (⚙) and stored in `%LOCALAPPDATA%\Shadepane\settings.json`. A value set by the policy wins and is
shown as locked. Browser data and the audit log (`audit.log`) live in the same folder.

## Releasing

Development happens on `develop`; `main` holds released code. CI builds and tests every push to both.
To release, merge `develop` into `main` and push a `vX.Y.Z` tag — CI publishes a release with the zip:

```powershell
git checkout main
git merge --ff-only develop
git tag v0.1.0
git push origin main v0.1.0
```

## License

[MIT](LICENSE)
