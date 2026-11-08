# Direct Windows routing test

AdapterProbe measures TCP and UDP egress from its own Windows sockets. It is a development tool for RouteFinder’s direct per-app routing work. Run it with the VPN disconnected when measuring the physical connections.

Build with `dotnet build tools/AdapterProbe -c Release`. The tool requires .NET 8. The probe client runs on Windows. The listener can run on Windows or Linux.

## Two internet connections

1. On a test server you control and can reach through both connections, run `AdapterProbe serve <server-bind-IP> 49372`. Allow TCP and UDP on that port for the duration of the test. The listener replies with the source IP it sees, and UDP replies are the same size as requests.
2. On the Windows test PC, connect both internet adapters. Run `AdapterProbe probe default <server-IP> 49372`, then repeat with each Windows adapter name in quotes in place of `default`. Run the IPv4 and IPv6 tests separately when both are available.
3. Compare the selected local source and server-observed source for both protocols. With different public IPs, the explicit Ethernet and Wi-Fi probes should arrive from their respective connections. If both links share a public IP, capture traffic on each Windows adapter to confirm the path. A timeout establishes only that the probe did not complete on that path.
4. Stop the listener and close the test port.

## Test RouteFinder's direct Windows rules

Keep Wi-Fi as the Windows default. Make two copies of the probe executable under different names in its build output directory. In an elevated terminal, run `AdapterProbe policy Ethernet <first-copy-exe>` and leave that process open. In another terminal, run both copies with `probe default <server-IP> 49372`. This leaves their sockets unbound so the first copy depends on its Windows rule. Compare the server-observed source and adapter captures for both TCP and UDP. Press Enter in the policy terminal to remove the policy and repeat with fresh sockets.

Repeat from RouteFinder’s **Windows routing** screen with a game and Discord, including voice traffic. Exercise link loss and recovery before relying on the behavior. VPN uplink selection is a separate setting.
