# RouteFinder

Windows x64 desktop application for routing selected applications through a WireGuard or supported Amnezia profile using the installed WireSock SDK.

## WinUI 3 desktop app

The WinUI app uses the existing routing engine, saved configurations, app selections, and remembered EZConnect sign-in. The connection screen supports selected apps, Entire PC, imported routes, route and destination comparisons, and live status. The **Add app** menu opens the EZConnect game catalog. **Connection options** provides reconnect, app-launch connections, and console routing. **Settings** controls the tunnel and tray behavior, **Activity** provides diagnostics, and **Advanced tools** includes Windows and WireSock maintenance.

```powershell
.\build.ps1 -Check -Installer -OutputDirectory dist/release
.\dist\release\app\RouteFinder.exe --preview
```

Preview mode uses sample routes and simulates connecting and disconnecting. A normal launch requests Windows administrator access if needed. Launching again restores the existing WinUI window for the current Windows user and session. Close any active legacy RouteFinder tunnel before connecting in WinUI. The legacy Windows Forms build is available through `.\build.ps1 -Classic -OutputDirectory dist/classic`.

## Run

Open RouteFinder with **Run as administrator** to enable WireSock tunnel tests and connections. In **What to route**, select apps or switch to **Entire PC**. In **Where to connect**, import a local config or add an optional EZConnect account, then choose a route from the library. You can test routes before using **Connect**. Entire PC mode uses the profile's default route for all applications; service routes include IPv6 when the route advertises IPv6, and imported profiles must contain `0.0.0.0/0`. The connection card distinguishes a started tunnel from a completed peer handshake and shows traffic counters.

**Settings** includes ICMP-based Auto MTU optimization, a WireSock kill switch, the virtual adapter, an optional DNS override, and an option to bypass local IPv4 networks. **Advanced tools** contains the IPv6 adapter binding control, which requires administrator access and may briefly reconnect active adapters. Auto MTU keeps the profile's MTU if no probe target responds. In selected-app mode, the kill switch blocks traffic from other apps while connected. Saved test results, route favorites, and connection settings are encrypted for the current Windows user.

Use **Import config** to work with a local WireGuard or supported Amnezia file without signing in. Use **Route options** to edit, export, or remove an imported profile. Profiles are encrypted for the current Windows user in `%LOCALAPPDATA%\RouteFinder\profiles.dat`. App selections are stored separately. The optional remembered sign-in stores an encrypted refresh token. Disconnecting releases the allocated service session.

WireSock Secure Connect must remain installed at its normal location. The application binds its installed `sdk\wgbooster.dll`; no proprietary SDK binaries, drivers, EZConnect branding, or service credentials are bundled. Use of the SDK remains subject to its installed license.

## Build and check

Requires the .NET 8 SDK or newer with Windows desktop targeting support and access to the Windows App SDK NuGet packages. The build bundles its Windows App SDK and .NET runtimes.

```powershell
.\build.ps1 -Check
```

Produces a self-contained Windows x64 app folder in `dist\app`. Keep its runtime files beside the executable, or use the installer. Checks cover configuration import, routing validation, SDK binding, preview rendering, launch activation, and normal startup from isolated encrypted test data. Checks do not change network state.

Run `.\build.ps1 -Installer` with NSIS 3 installed to also build `dist\RouteFinderSetup.exe`. Setup installs RouteFinder under Program Files, adds a Start menu shortcut, and registers it in Installed apps. WireSock is a separate prerequisite. Close RouteFinder before upgrading or uninstalling.

For a signed release, add `-SigningThumbprint <thumbprint>` and, if needed, `-SignTool <path-to-signtool.exe>`. The build uses the specified code-signing certificate from the current user's Personal certificate store, timestamps and verifies the application and installer signatures, and reports a build failure if signing or verification fails.

## Current coverage

RouteFinder includes account sign-in with optional Windows-user protected refresh-token saving, a searchable route library, sequential route comparison, executable selection, running game discovery, local profile import, and live connection status. Tests use a temporary WireSock tunnel and check for a WireGuard handshake, estimated packet loss, RTT, average inter-sample jitter, RTT spread, and a separately labeled TCP reachability check. Those metrics come from local WireSock observations and do not implement the original EZwire server-assisted route tests.

For EZConnect session JSON imports, include the original configuration object and supply the corresponding private key when prompted. For JSON with multiple routes, set `route_name` to the intended route's exact name. The relay must still authorize the profile.

To audit the route catalog with your own EZConnect account, run `./Audit-EzRoutes.ps1`. The default mode lists routes. `-ProbeCount 1` requests and releases one temporary WireGuard session. `-ProbeAll` processes the catalog one route at a time. Resume a rate-limited audit with `-ProbeAll -Resume -Output <existing-report.json>` after the reported cooldown. The audit records endpoint and routing metadata. It does not save client private keys or start local WireGuard tunnels.

For a live comparison, run `./Audit-EzRoutes.ps1 -VerifyTunnel` in an administrator PowerShell window with the original VPN disconnected. This mode starts WireSock for the audit executable only. It measures receive counters before releasing one temporary session and repeats the measurement on a fresh tunnel with the same key if the first tunnel receives traffic. Private keys stay in the process. A successful result after release would establish short-term reuse for that route, while long-term access and subscription requirements would need separate verification.

Imported configurations support one Interface and one Peer. Unknown options, script directives, duplicate fields, and multi-peer files are rejected rather than silently ignored. Selected app paths override `AllowedApps` in imported profiles. `DisallowedApps` profiles cannot be connected in selected-app mode.

See [the local EZConnect research archive](ezconnect-research/README.md) for the existing extraction, reconstruction notes, and audit reports. The release installer does not include the archive.
