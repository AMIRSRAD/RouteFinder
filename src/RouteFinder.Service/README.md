# EZConnect service adapter (experimental)

This library reconstructs the service contract vocabulary recovered from EZConnect 6.8.1's decoded constants. It is buildable and covered by mocked HTTP checks. **No live account, service response, or service tunnel has been tested. It is not a verified replacement for the original service client.**

The default client only targets the two decoded HTTPS origins. TLS validation remains enabled; redirects are disabled. No private key is sent to the service. HTTP refusals retain status/error code without printing response bodies. Tokens and keypair records redact their textual representations. Tokens are held in memory by the caller, not saved by this library.

## Contract and source boundary

Evidence: `ezconnect-research/analysis/decoded-constants/parsed/ezconnect.infrastructure.http.{auth_gateway,bootstrap_gateway,session_gateway,api_client}.json`, and `ezconnect.config.loader.json`. These typed records are data extracted from the shipped executable; method execution was not observed. Request-field associations, response nesting and alias precedence are reconstruction inferences until native flow or real responses corroborate them.

- Login: POST login origin `/api/desktop/auth/login`, email/password/device, response access/refresh token pair. A `mismatch=true` response fails and is never bypassed.
- Bootstrap: GET login origin `/api/desktop/bootstrap` with bearer token. Catalog/route data and raw JSON are retained. The original game and route policy vocabulary includes additional rules not automatically applied here.
- Session provisioning: POST pool origin `/api/desktop/sessions/connect`, route_name/region_code/client_public_key. A supplied private key must match the public key. The response must identify an allocated session and usable configuration. Failure to parse a response with a session ID triggers a disconnect cleanup attempt.
- Session cleanup: POST pool origin `/api/desktop/sessions/disconnect` with session_id and bearer token.
- API version header is the recovered `X-EzConnect-Version: 6.8.1`. User-Agent identifies this client as RouteFinder rather than claiming to be the original executable.

`DeviceDescriptor` requires the caller's real device hash and values. Later offline native inspection verified the original hash composition as SHA256 over UTF-8 `|`-joined, ordinal-sorted `key:value` fields for cpu_model, motherboard_model, ram_model, and system_uuid. `DeviceFingerprint` implements this verified composition. Hardware reading and normalization remain a separate injected responsibility. The auth field `gpu_model` is an empty string in the inspected original function, and defaults to empty here. This library does not copy, spoof, or bypass another device's registration identity. Optional unspecified hardware values are sent as empty strings.

Provisioning currently supports the recovered config/Interface/Peer object and route-array vocabulary. Interface CIDRs, peer keys, endpoint/port and scripts are strictly validated by RouteFinder.Core. MTU 1380 and `0.0.0.0/0` default AllowedIPs are exact recovered defaults. Amnezia defaults Jc=3, Jmin=50, Jmax=1000, Jd=0 are exact recovered object values; enabled-state mapping is inferred. A missing peer key or endpoint is never invented. Unsupported server response layouts fail rather than connecting to a guessed target.

Refresh/logout, EZwire key grants, anchors, custom route-check sessions, full original catalog policy and retry behavior are not implemented. The caller owns session lifecycle; dispose of the HTTP client does not disconnect provisioned service sessions. Disconnect those explicitly.

## WireGuard keys

`WireGuardKeys.Generate()` uses secure random 32-byte private material, X25519 clamping, and RFC 7748 base-point multiplication. Two published base-point test vectors pass. This independent implementation uses managed BigInteger arithmetic and **is not constant-time**; it is intended only for local keypair generation, not an exposed key-agreement oracle. Production hardening should replace it with an audited constant-time X25519 provider.

Primary specification: [RFC 7748](https://www.rfc-editor.org/rfc/rfc7748), sections 5 and 6.1.

## Checks

```
dotnet run --project tests/RouteFinder.ServiceChecks/RouteFinder.ServiceChecks.csproj -c Release
```

All HTTP exchanges use a fake handler and documentation-reserved IPs. Checks cover key derivation vectors, secret redaction, expected request hosts/headers/body shape, device mismatch, bearer handling, provisioning validation, cleanup after malformed config, pre-request keypair rejection, safe refusal handling, redirect rejection, and missing endpoint rejection. They establish implementation behavior, not live service compatibility.
