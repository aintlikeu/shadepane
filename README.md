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
page, zoom and the usual keyboard shortcuts. The interface is in English or Russian: it follows the Windows display language
and can be switched in settings.

> Capture exclusion is a Windows feature, not DRM: it stops software, not a camera pointed at the screen.

## Download

From [the latest release](https://github.com/aintlikeu/shadepane/releases/latest):

- `Shadepane-<version>-win-x64-setup.exe` — installer. Installs for the current user without admin rights
  (`%LOCALAPPDATA%\Programs\Shadepane`); choose "for all users" (or run with `/ALLUSERS`) to install into
  Program Files. Start menu shortcut, optional desktop shortcut, uninstaller. Silent install: `/SILENT`.
- `Shadepane-<version>-win-x64.zip` — portable: unzip and run `Shadepane.exe`.
- `SHA256SUMS.txt` — checksums of both files.

Requirements: Windows 10 2004+ or Windows 11, and the
[WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on Windows 11).
.NET is bundled. The builds are not code-signed yet, so Windows SmartScreen may warn on first run
(*More info* → *Run anyway*).

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

Check an unpacked extension against a policy without starting the browser (needs the .NET 10 SDK and a clone of
this repository):

```powershell
dotnet run tools/check-extension.cs -- <extension-folder> [policy.json]
```

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
| `restoreSession` | not set | Reopen the previous tabs on start; this keeps the addresses of open tabs in `session.json`. `false` forbids it (nothing is stored), `true` turns it on; when set, users cannot change it (default: off) |
| `blockInRemoteSession` | `true` | Refuse to run in an RDP session |

User preferences (home page, search engine, downloads, clipboard access for extensions, history,
bookmarks bar) are set in the settings
tab (⋯ → Settings) and stored in `%LOCALAPPDATA%\Shadepane\settings.json`. A value set by the policy wins and is
shown as locked. Browser data and the audit log (`audit.log`) live in the same folder.

## Updates

When installed with the installer, Shadepane checks the latest GitHub release a few seconds after start
(and on "Check now" in settings) and offers an update button. The update is downloaded only from this
repository's release assets, verified against the release's `SHA256SUMS.txt` and, if the running browser
is code-signed, required to carry a valid signature from the same publisher; then it is installed silently
and the browser restarts. The portable zip build opens the release page instead. The check sends a request
to `api.github.com`; it can be switched off in settings or enforced with `checkForUpdates`.

## Privacy

No accounts, telemetry or analytics. See [`docs/PRIVACY.md`](docs/PRIVACY.md).

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```powershell
dotnet test                                  # core tests (also run on Linux)
dotnet run --project src\Shadepane.App       # run the browser (Windows only)
```

Installer, releases, the Microsoft Store package and code signing: [`docs/MAINTAINING.md`](docs/MAINTAINING.md).

## License

[MIT](LICENSE). Toolbar icons: [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT), see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
