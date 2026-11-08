# RouteFinder

Windows x64 desktop application for routing selected applications through a WireGuard or supported Amnezia profile using the installed WireSock SDK.

[Download the latest Windows x64 installer](https://github.com/AMIRSRAD/RouteFinder/releases/latest/download/RouteFinderSetup.exe). Install WireSock Secure Connect separately and exit RouteFinder before upgrading.

## Updates

WinUI checks the latest published GitHub release in the background at startup. Open **Advanced tools → Check for RouteFinder updates** to check immediately. When an update is available, **Download and install** downloads the official installer, verifies its SHA-256 checksum against the release manifest, closes RouteFinder, and opens Setup. Stop active routing before installing. Existing installations from before 1.1.9 can install this release using the download link above.

Under **Advanced tools → Configure update feed**, leave the URL blank to disable checks or enter the GitHub manifest URL to re-enable them:

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

The WinUI app uses the existing routing engine, saved configurations, app selections, and remembered EZConnect sign-in. The connection screen supports selected apps, Entire PC, imported routes, route and destination comparisons, and live status. The **Add app** menu opens the EZConnect game catalog. **Connection options** provides reconnect, app-launch connections, and console routing. **Settings** controls the tunnel and tray behavior, **Activity** provides diagnostics, and **Advanced tools** includes Windows and WireSock maintenance.

```powershell
.\build.ps1 -Check -Installer -OutputDirectory dist/release
.\dist\release\app\RouteFinder.exe --preview
```

Preview mode uses sample routes and simulates connecting and disconnecting. A normal launch requests Windows administrator access if needed. Launching again restores the existing WinUI window for the current Windows user and session. Close any active legacy RouteFinder tunnel before connecting in WinUI. The legacy Windows Forms build is available through `.\build.ps1 -Classic -OutputDirectory dist/classic`.

## Run

Open RouteFinder with **Run as administrator** to enable WireSock tunnel tests and connections. In **Where to connect**, choose **Add route** to import a WireGuard or Amnezia configuration, or sign in to EZConnect to load routes from that provider. Both sources appear in the same route list. Use **Add route → Manage EZConnect account** to refresh the provider's routes or sign out. In **What to route**, select apps or switch to **Entire PC**. You can test a route before using **Connect**. Entire PC mode applies to all apps, subject to app exclusions and the destinations allowed by the route profile. EZConnect IPv6 destinations require an IPv6 tunnel address. The connection card distinguishes a started tunnel from a completed peer handshake and shows traffic counters.

Choose **Selected apps only** in **What to route** to turn on WireSock’s network lock for that connection. Checked apps use the tunnel and other apps lose network access while connected. This mode disables direct LAN and destination bypasses. Disconnect to restore normal access. Routing presets can save this mode.

**Settings** includes ICMP-based Auto MTU optimization, a WireSock kill switch, the virtual adapter, an optional DNS override, and an option to bypass local IPv4 networks. **Advanced tools** contains the IPv6 adapter binding control, which requires administrator access and may briefly reconnect active adapters. Auto MTU keeps the profile's MTU if no probe target responds. In selected-app mode, the kill switch blocks traffic from other apps while connected. Saved test results, route favorites, and connection settings are encrypted for the current Windows user.

Imported routes are available without signing in. Use **Route options** to edit, export, or remove an imported profile. Profiles are encrypted for the current Windows user in `%LOCALAPPDATA%\RouteFinder\profiles.dat`. App selections are stored separately. The optional remembered sign-in stores an encrypted refresh token. Disconnecting releases the allocated service session.

Choose the apps under **What to route**, then select the route and **Internet adapter** under **Where to connect**. **Automatic** uses Windows routing. The adapter choice is saved per route and applies to connections and route tests. Use **Presets → Save current setup** to save the apps, route, and adapter together. RouteFinder runs one setup at a time.

For direct WireGuard connections, RouteFinder adds a temporary Windows route to the VPN endpoint and removes its entry on disconnect. SOCKS5 profiles require **Automatic**. If Windows changes the selected endpoint path during a connection, RouteFinder disconnects when it detects the change.

## Direct Windows app routing

In the App Router, choose **Direct / no VPN** for an app that should use its assigned interface directly, or select a VPN route and its uplink. VPN apps in a single active profile share one VPN route and uplink. When using VPN rules, set the default for unassigned apps to **Follow Windows**.

**Routing profiles** saves or loads the current app rules and default adapter. Loading a profile lets you review it before applying. **Live routing inspector** compares configured rules with established TCP connections and their Windows source interfaces. The source interface alone does not verify VPN egress. **Interfaces and DNS** shows addresses, gateways, and DNS servers for Windows interfaces and lets you set manual DNS or restore automatic DNS. DNS changes are stored by Windows and persist after RouteFinder exits.

Open **Windows routing** from the main toolbar. Checked apps appear automatically as suggestions. Removing or unchecking an app removes its suggestion. Manage apps that already have an adapter assignment in **Windows routing**. **Browse for apps** adds other executables. Choose an adapter beside each app, then select **Save & apply** and fully quit and reopen the assigned apps. Use **Default adapter for unassigned apps** to pick a connected adapter for your other apps while routing is active. Explicit app assignments take priority. Choose **Follow Windows** to leave unassigned apps on normal Windows routing. Stopping routing removes the temporary fallback. You can assign an app to a connected adapter without an internet gateway to keep its new connections on that adapter. The page also shows the current Windows IPv4 default. Use **Back to VPN routes** to return to the VPN workspace.

Direct routing uses temporary Windows Filtering Platform connection policies on supported Windows builds. Starting enables the Windows IPv4 and IPv6 route-policy settings for the current boot. RouteFinder’s own rules apply to new connections and are removed when routing stops or RouteFinder exits. To switch modes, stop Windows routing before starting a VPN tunnel. Use the [adapter probe](tools/AdapterProbe/README.md) to verify actual TCP and UDP egress on a two-link PC.

WireSock Secure Connect must remain installed at its normal location. The application binds its installed `sdk\wgbooster.dll`; no proprietary SDK binaries, drivers, EZConnect branding, or service credentials are bundled. Use of the SDK remains subject to its installed license.

RouteFinder 1.1.8 fixes multi-app routing with WireSock SDK 3.4.8.1 in both Transparent and Virtual adapter modes. This SDK parses comma-separated executable paths despite its bundled guide documenting pipes. RouteFinder selects the correct format for that version automatically. If selected-app traffic stalls, use **Activity → Diagnostics → Active tunnel rules** to check the executable paths and **Activity → Save report** to capture the SDK version and tunnel counters.

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

Use **Verify routing** in the live connection panel to record and compare the readings. RouteFinder keeps those readings in the current window only.

With other VPN clients disconnected, run RouteFinder as administrator and open two different browsers. Record both public IPs before connecting. Select just one browser in RouteFinder and connect it to a known working route with a distinct exit IP. Open a fresh connection in each browser. The selected browser should show the route's exit IP and the unselected browser should still show its original IP. Disconnect and repeat the check to confirm both browsers return to the original path and RouteFinder shows no pending cleanup.

Save a route and traffic scope under **Presets**. Applying a preset restores the selection for review before you connect. To test a shortlist, filter the route library by search, region, source, or favorites, then choose **Test filtered routes** from the route tools menu.

For EZConnect session JSON imports, include the original configuration object and supply the corresponding private key when prompted. For JSON with multiple routes, set `route_name` to the intended route's exact name. The relay must still authorize the profile.

To audit the route catalog with your own EZConnect account, run `./Audit-EzRoutes.ps1`. The default mode lists routes. `-ProbeCount 1` requests and releases one temporary WireGuard session. `-ProbeAll` processes the catalog one route at a time. Resume a rate-limited audit with `-ProbeAll -Resume -Output <existing-report.json>` after the reported cooldown. The audit records endpoint and routing metadata. It does not save client private keys or start local WireGuard tunnels.

For a live comparison, run `./Audit-EzRoutes.ps1 -VerifyTunnel` in an administrator PowerShell window with the original VPN disconnected. This mode starts WireSock for the audit executable only. It measures receive counters before releasing one temporary session and repeats the measurement on a fresh tunnel with the same key if the first tunnel receives traffic. Private keys stay in the process. A successful result after release would establish short-term reuse for that route, while long-term access and subscription requirements would need separate verification.

Imported configurations support one Interface and one Peer. Unknown options, script directives, duplicate fields, and multi-peer files are rejected rather than silently ignored. Selected app paths override `AllowedApps` in imported profiles. `DisallowedApps` profiles cannot be connected in selected-app mode.

See [the local EZConnect research archive](ezconnect-research/README.md) for the existing extraction, reconstruction notes, and audit reports. The release installer does not include the archive.
