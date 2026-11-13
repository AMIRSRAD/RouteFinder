# RouteFinder

Windows x64 desktop application for routing selected applications through a WireGuard or supported Amnezia profile using the installed WireSock SDK.

[Download the latest Windows x64 installer](https://github.com/AMIRSRAD/RouteFinder/releases/latest/download/RouteFinderSetup.exe). Install WireSock Secure Connect separately and exit RouteFinder before upgrading.

## Updates

WinUI checks the latest published GitHub release in the background at startup. Open **Settings → Maintenance → Check for updates** to check immediately. When an update is available, **Download and install** downloads the official installer, verifies its SHA-256 checksum against the release manifest, closes RouteFinder, and opens Setup. Stop active routing before installing. Existing installations from before 1.1.9 can install this release using the download link above.

Under **Settings → Maintenance → Update feed**, leave the URL blank to disable checks or enter the GitHub manifest URL to re-enable them:

```text
https://github.com/AMIRSRAD/RouteFinder/releases/latest/download/manifest.json
```

To prepare a GitHub release, build with its final version and versioned asset URL:

```powershell
.\build.ps1 -Check -Installer -ReleaseVersion 1.1.9 `
    -ReleaseBaseUrl https://github.com/AMIRSRAD/RouteFinder/releases/download/v1.1.9/ `
    -OutputDirectory dist/release-1.1.9
```

Attach both `RouteFinderSetup.exe` and `manifest.json` before publishing the release.

## WinUI 3 desktop app

The app has seven pages: **Home** (connection status and one Connect button), **Apps** (every app and how it connects), **Routes** (route library and tests), **Providers** (EZConnect and imported configurations), **Network** (adapters, default adapter, DNS), **Activity** (log, diagnostics, routing check), and **Settings** (presets and connection options).

```powershell
.\build.ps1 -Check -Installer -OutputDirectory dist/release
.\dist\release\app\RouteFinder.exe --preview
```

Preview mode uses sample routes and simulates connecting and disconnecting. A normal launch requests Windows administrator access if needed. Launching again restores the existing window. Closing the window sends RouteFinder to the notification area and **Exit** in the tray menu quits it; turn this off with **Settings → Close to tray**. The tray menu follows the Windows light or dark setting.

## Run

Open RouteFinder with **Run as administrator** to enable WireSock tunnel tests and connections. On **Routes**, choose **Add route** to import a WireGuard or Amnezia configuration, or sign in to EZConnect on **Providers** to load its routes. Both appear in the same list. You can test a route before using **Connect**.

### Apps

The **Apps** page lists every app once. A tick box switches an app on or off without removing it. Each app has one menu for how it connects:

- **Through VPN (automatic)** or **Through VPN via Wi-Fi / Ethernet**: the app uses the selected route, and the VPN tunnel itself leaves through that adapter. One tunnel runs at a time, so this adapter applies to all Through VPN apps.
- **Direct · Wi-Fi / Ethernet**: the app skips the VPN and always uses that adapter.

A catalog game from EZConnect is added as one expandable group with a single switch and menu. Executables that were not found stay on the group by name and join when the app runs or its file is found. For Through VPN groups they are also handed to WireSock by name, as EZConnect does. Names used by many programs, such as `Update.exe`, only match inside the group's own folders and are never passed by name.

**Home → What goes through the route** chooses **Selected apps**, **Entire PC**, or **Selected apps only** (WireSock's network lock: other apps lose network access while connected). In those two modes Direct apps and the default adapter pause, but VPN traffic is still kept on the tunnel's adapter: WireSock's transparent mode only captures traffic on the adapter it reaches the VPN server through, so with Entire PC every app is sent there while connecting or connected.

Changes made while connected, such as apps, route, adapter, scope, or connection settings, are applied by reconnecting automatically after a short pause.

### Adapter rules

Direct apps and Through VPN pins are enforced with temporary Windows Filtering Platform connection policies. Rules apply automatically a moment after any change and when RouteFinder starts; there is no apply button. They are removed when RouteFinder exits, and replaced without turning Windows' route-policy setting off in between. Fully quit and reopen an app after pinning it so existing connections are replaced.

Rules cover internet addresses only (seven IPv4 ranges plus `2000::/3`, one rule each, since Windows allows one address condition per rule). Traffic inside the PC, the local network, link-local and multicast always follow Windows, so a pinned launcher such as Riot Client can still reach its own local service and LAN devices stay reachable.

RouteFinder pins itself (it sends the tunnel traffic) and the Through VPN apps to the tunnel's adapter whenever Windows would send them elsewhere: a default adapter is set, another VPN holds the route, or the route's adapter is not the one Windows prefers. This pinning is not possible in virtual adapter mode.

**Network → Default adapter** is set the Windows way: RouteFinder lowers that adapter's interface metric so Windows itself prefers it for every app and service, with no rule on other apps. When it is already Windows' own choice nothing changes. The original metric comes back on **Follow Windows**, on exit, and at the next start after a crash. Turn on **Keep as Windows default** to keep the adapter as Windows' preferred adapter after RouteFinder closes; choose **Follow Windows** to undo it. Only when another VPN holds the default route with split routes, which no metric can outrank, is a catch-all rule used instead.

If another VPN (for example F-Secure's, which uses a Wintun adapter) holds Windows' default route, RouteFinder keeps the tunnel and the Through VPN apps on a physical adapter. When the route's chosen adapter is offline, the tunnel and the default adapter fall back to a working physical adapter. **Network → Live routing inspector** shows each app's mode and connections; in WireSock's transparent mode Windows still reports your Wi-Fi or Ethernet address for tunneled traffic, so use **Activity → Verify routing** to confirm the exit IP.

### DNS

Windows resolves names for every app in one system service, so a Through VPN app's lookups do not enter the tunnel, and filtered networks answer them with a block page (for example `discord.com -> 10.10.34.36`). With **Settings → Secure DNS while connected** (on by default), RouteFinder runs a small DNS relay as SYSTEM while connected and points Windows' resolver at it with a name resolution policy. The relay answers over DNS over HTTPS (Cloudflare, then Google), rejects block-page answers, and sends local names (`.lan`, `.local`, single-label names, private reverse lookups) to the network's own DNS. RouteFinder checks a real lookup through Windows before relying on it. If the relay cannot be used, blocked names that apps actually hit are resolved securely and pinned in a marked block of the `hosts` file instead. The policy, the relay task, and the hosts block are removed on disconnect, on exit, and at the next start after a crash. The Windows DNS cache is flushed on connect and disconnect.

Security suites that filter DNS can take over all port-53 traffic. **Settings → Pause F-Secure while RouteFinder runs** (off by default) stops F-Secure's services (`fsvpnsvc`, `fsvpnextsvc`, `fsnethoster`, `fshoster`) and starts the ones it stopped again when RouteFinder exits or the option is turned off. F-Secure's tamper protection can refuse this; Activity says so.

### Start with Windows

**Settings → Start with Windows** (off by default), or the checkbox on the installer's last page, starts RouteFinder in the tray a few seconds after you sign in. RouteFinder needs administrator rights, so instead of a startup entry that would raise a UAC prompt at every sign-in, it registers a scheduled task (`RouteFinder startup`) that runs at logon with the highest privileges and the `--startup` switch. The task has no time limit and runs on battery. Updating RouteFinder keeps the option and points the task at the new files; uninstalling removes it. `RouteFinder.exe --set-startup on|off` changes it from a command line.

### Detailed logging

**Settings → Detailed logging** (off by default) writes a plain-text log to `%LOCALAPPDATA%\RouteFinder\logs`: routing decisions, adapter rules, tunnel steps and WireSock events, Windows adapter and route changes, DNS relay lookups, and every few seconds a snapshot of each program's connections with stuck connections and block-page DNS answers flagged. **Activity → Open logs** opens the folder and **Activity → Save report** includes the latest entries. Files roll over at 20 MB and the newest 15 are kept. Keys, tokens, and passwords are never written.

### Settings and presets

A **preset** (Settings → Presets) saves the whole setup: route and tunnel adapter, apps with their on/off state and route menu, groups, traffic scope, default adapter, and connection settings (kill switch, MTU, keepalive, DNS, bypass list, timeouts, tray options). SOCKS5 proxy credentials, language, and window size are not stored in presets. Number settings use limited number boxes; DNS uses a provider list with primary and secondary fields.

Profiles are encrypted for the current Windows user in `%LOCALAPPDATA%\RouteFinder\profiles.dat`. The optional remembered sign-in stores an encrypted refresh token. Disconnecting releases the allocated service session. Saved test results, route favorites, presets, and connection settings are encrypted for the current Windows user.

For direct WireGuard connections, RouteFinder adds a temporary Windows route to the VPN endpoint and removes its entry on disconnect. SOCKS5 profiles require **Automatic**. If Windows changes the selected endpoint path during a connection, RouteFinder disconnects when it detects the change.

## Direct Windows app routing

Direct routing uses temporary Windows Filtering Platform connection policies on supported Windows builds. Starting enables the Windows IPv4 and IPv6 route-policy settings for the current boot. RouteFinder's own rules apply to new connections and are removed when routing stops or RouteFinder exits. Use the [adapter probe](tools/AdapterProbe/README.md) to verify actual TCP and UDP egress on a two-link PC.

WireSock Secure Connect must remain installed at its normal location. The application binds its installed `sdk\wgbooster.dll`; no proprietary SDK binaries, drivers, EZConnect branding, or service credentials are bundled. Use of the SDK remains subject to its installed license.

RouteFinder 1.1.8 fixes multi-app routing with WireSock SDK 3.4.8.1 in both Transparent and Virtual adapter modes. This SDK parses comma-separated executable paths despite its bundled guide documenting pipes. RouteFinder uses commas for 3.4.8.1 and later and the documented pipe for older versions; `--wiresock-pipe-app-list` and `--wiresock-comma-app-list` force a format. If selected-app traffic stalls, use **Activity → Diagnostics → Active tunnel rules** to check the executable paths and **Activity → Save report** to capture the SDK version and tunnel counters.

`RouteFinder.exe --detect-apps <report> <title> <names> [related paths]` prints, read-only, which executables app detection finds for a game.

## Build and check

Requires the .NET 8 SDK or newer with Windows desktop targeting support and access to the Windows App SDK NuGet packages. The build bundles its Windows App SDK and .NET runtimes.

```powershell
.\build.ps1 -Check
```

Produces a self-contained Windows x64 app folder in `dist\app`. Keep its runtime files beside the executable, or use the installer. Checks cover configuration import, routing validation, SDK binding, preview rendering, launch activation, and normal startup from isolated encrypted test data. Checks do not change network state.

Run `.\build.ps1 -Installer` with NSIS 3 installed to also build `dist\RouteFinderSetup.exe`. Setup installs RouteFinder under Program Files, adds a Start menu shortcut, and registers it in Installed apps. WireSock is a separate prerequisite. Close RouteFinder before upgrading or uninstalling.

For a signed release, add `-SigningThumbprint <thumbprint>` and, if needed, `-SignTool <path-to-signtool.exe>`. The build uses the specified code-signing certificate from the current user's Personal certificate store, timestamps and verifies the application and installer signatures, and reports a build failure if signing or verification fails.

## Current coverage

RouteFinder includes account sign-in with optional Windows-user protected refresh-token saving, a searchable route library, sequential route comparison, executable selection, running game discovery, local profile import, and live connection status. Standard route tests use a temporary WireSock tunnel and report the peer handshake, the SDK's estimated packet loss and RTT when available, and a separately labeled TCP reachability check. Destination comparisons use a TCP probe scoped to RouteFinder and the destination IP. ICMP pings use the existing Windows route. These tests use local observations rather than EZwire's server-assisted route tests.

### Live per-app check

Use **Activity → Verify routing** to record and compare the readings. RouteFinder keeps those readings in the current window only.

With other VPN clients disconnected, run RouteFinder as administrator and open two different browsers. Record both public IPs before connecting. Select just one browser in RouteFinder and connect it to a known working route with a distinct exit IP. Open a fresh connection in each browser. The selected browser should show the route's exit IP and the unselected browser should still show its original IP. Disconnect and repeat the check to confirm both browsers return to the original path and RouteFinder shows no pending cleanup.

Save a setup under **Settings → Presets**. Applying a preset restores it for review before you connect. To test a shortlist, filter the route library by search, region, source, or favorites, then choose **Test filtered routes** from the route tools menu.

For EZConnect session JSON imports, include the original configuration object and supply the corresponding private key when prompted. For JSON with multiple routes, set `route_name` to the intended route's exact name. The relay must still authorize the profile.

To audit the route catalog with your own EZConnect account, run `./Audit-EzRoutes.ps1`. The default mode lists routes. `-ProbeCount 1` requests and releases one temporary WireGuard session. `-ProbeAll` processes the catalog one route at a time. Resume a rate-limited audit with `-ProbeAll -Resume -Output <existing-report.json>` after the reported cooldown. The audit records endpoint and routing metadata. It does not save client private keys or start local WireGuard tunnels.

For a live comparison, run `./Audit-EzRoutes.ps1 -VerifyTunnel` in an administrator PowerShell window with the original VPN disconnected. This mode starts WireSock for the audit executable only. It measures receive counters before releasing one temporary session and repeats the measurement on a fresh tunnel with the same key if the first tunnel receives traffic. Private keys stay in the process. A successful result after release would establish short-term reuse for that route, while long-term access and subscription requirements would need separate verification.

Imported configurations support one Interface and one Peer. Unknown options, script directives, duplicate fields, and multi-peer files are rejected rather than silently ignored. Selected app paths override `AllowedApps` in imported profiles. `DisallowedApps` profiles cannot be connected in selected-app mode.

See [the local EZConnect research archive](ezconnect-research/README.md) for the existing extraction, reconstruction notes, and audit reports. The release installer does not include the archive.
