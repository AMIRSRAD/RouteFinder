"""Create reviewable settings/contracts from decoded, typed constants.

This intentionally separates exact values from untraced role associations.
"""
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
PARSED = ROOT / "ezconnect-research" / "analysis" / "decoded-constants" / "parsed"
CACHE = {}


def doc(module):
    if module not in CACHE:
        CACHE[module] = json.loads((PARSED / (module + ".json")).read_text())
    return CACHE[module]


def plain(value):
    if isinstance(value, dict) and value.get("type") in ("tuple", "list", "set", "frozenset"):
        return [plain(v) for v in value["items"]]
    if isinstance(value, dict) and value.get("type") == "dict":
        return {str(plain(k)): plain(v) for k, v in value["items"]}
    return value


def ref(module, index):
    d = doc(module)
    r = d["constants"][index]
    return {"module": module, "constant_index": index,
            "module_payload_offset": r["payload_offset"],
            "decoded_offset": d["decoded_payload_offset"] + r["payload_offset"]}


def setting(module, index, status="exact_value_with_inferred_role", note=None):
    row = {"value": plain(doc(module)["constants"][index]["value"]),
           "evidence_status": status, "source": ref(module, index)}
    if note:
        row["note"] = note
    return row


def refs_for_names(module, names):
    rows = doc(module)["constants"]
    return [{"name": name, "sources": [ref(module, r["index"]) for r in rows if r["value"] == name]} for name in names]


def main():
    loader = "ezconnect.config.loader"
    profile = "ezconnect.domain.value_objects.tunnel_profile"
    quality = "ezconnect.domain.value_objects.route_quality"
    scoring = "ezconnect.domain.services.route_scoring"
    engine = "ezconnect.infrastructure.ezwire.embedded_engine"
    service = "ezconnect.application.services.ezwire_engine_service"
    wire = "ezconnect.domain.value_objects.ezwire"
    check = "ezconnect.application.services.route_check_service"
    settings = {
        "evidence_type": "ordinary offline decoded typed constants; not REA",
        "value_validation": "All 519 module payloads parsed for their declared constant count with exact exhaustion except one-byte 0x7e terminators; randomized tag semantics traced at RVA 0x179bd00.",
        "interpretation": "Values, tuple order and dict key/value relations are verified serialization observations. Named global/field association is inferred unless explicitly marked; no native assignments or call flow are invented.",
        "api_origins": {"login": setting(loader, 206), "pool": setting(loader, 207),
                        "ezwire": setting(loader, 208), "anchors": setting(loader, 209)},
        "vpn": {"default_mtu": setting(profile, 44), "local_bypass_networks": setting(profile, 46),
                "default_allowed_ips": setting(profile, 85),
                "obfuscation_constructor_values": setting(profile, 70, note="Tuple pairs by position with junk_count,junk_minimum,junk_maximum,junk_delay tuple at index71; call-site association remains to be traced."),
                "obfuscation_constructor_field_names": setting(profile, 71, "verified_tuple_literal"),
                "config_field_names": setting(loader, 109, "verified_tuple_literal"),
                "unresolved": "Exact handshake timeout/poll/tunnel-name/keepalive assignment from ConfigLoader still needs native tracing; numeric constants are preserved below."},
        "metrics": {"config_field_names": setting(loader, 117, "verified_tuple_literal"),
                    "probe_target_candidate": setting(loader, 236),
                    "unresolved": "Numeric defaults for probe timeout, sampling interval, settling and history have not been matched to native assignments."},
        "mtu": {"config_field_names": setting(loader, 124, "verified_tuple_literal"),
                "probe_targets": setting(loader, 223), "cache_ttl_seconds_candidate": setting(loader, 224),
                "unresolved": "Probe timeout and overlay value assignments need native tracing."},
        "ezwire": {"engine_settings_literal": setting(engine, 175, "verified_dict_literal"),
                   "driver_service_name": setting(loader, 217), "status_poll_ms": setting(loader, 219),
                   "max_udp_routes": setting(wire, 147), "max_check_routes": setting(wire, 149),
                   "max_parallel_pings": setting(wire, 151), "full_retry_seconds": setting(wire, 90),
                   "check_probes": setting(service, 511), "check_interval_ms": setting(service, 513),
                   "native_ping_keyword_defaults": setting(engine, 218, "verified_dict_literal"),
                   "native_rule_keyword_defaults": setting(engine, 212, "verified_dict_literal")},
        "route_check": {"cache_ttl_seconds": setting(check, 183), "fallback_target": setting(check, 184),
                        "teardown_timeout_seconds": setting(check, 185), "teardown_poll_seconds": setting(check, 186)},
        "probe_quality": {"minimum_samples": setting(quality, 47),
                          "baseline_spacing_seconds": setting(quality, 49), "stream_spacing_seconds": setting(quality, 51),
                          "quick_plan_values": setting(quality, 84), "standard_plan_values": setting(quality, 86),
                          "deep_plan_values": setting(quality, 87), "plan_field_order": setting(quality, 85, "verified_tuple_literal"),
                          "r_factor_boundaries_descending": [setting(quality, i, "verified_numeric_literal") for i in range(95, 100)],
                          "ping_bands_ms": setting(quality, 106), "jitter_bands_ms": setting(quality, 107),
                          "loss_bands_pct": setting(quality, 108),
                          "note": "Plan/depth and threshold-name associations follow constants context; verify native assignment before claiming exact grading parity."},
        "scoring": {"starting_r_factor": setting(scoring, 71),
                    "ping_penalty_table": setting(scoring, 72), "ping_penalty_above": setting(scoring, 74),
                    "jitter_penalty_table": setting(scoring, 75), "jitter_penalty_above": setting(scoring, 76),
                    "loss_penalty_table": setting(scoring, 77), "spike_penalty_table": setting(scoring, 78),
                    "loss_penalty_above": setting(scoring, 79), "spike_penalty_above": setting(scoring, 80),
                    "spike_threshold_multiplier": setting(scoring, 81),
                    "note": "Tables and scalar values exact; name-to-value association inferred from module constants and declaration ordering. The score formula has not been reconstructed."},
        "config_numeric_literal_pool": [setting(loader, r["index"], "verified_numeric_literal_unassigned")
                                        for r in doc(loader)["constants"] if type(r["value"]) in (int, float)],
        "embedded_bytecode": {"blob_count": 402, "xdis_decoded": 401, "ezconnect_filename_matches": 0,
                              "unresolved_blob_indices": [14], "inventory": "ezconnect-research/analysis/decoded-constants/bytecode-inventory.json"},
    }
    (ROOT / "ezconnect-research" / "analysis" / "recovered-settings.json").write_text(json.dumps(settings, indent=2), encoding="utf8")

    bootstrap = "ezconnect.infrastructure.http.bootstrap_gateway"
    session = "ezconnect.infrastructure.http.session_gateway"
    sweep = "ezconnect.infrastructure.http.check_route_gateway"
    auth = "ezconnect.infrastructure.http.auth_gateway"
    ew = "ezconnect.infrastructure.http.ezwire_gateway"
    def contract(module, paths, request, response, note):
        return {"module": module, "paths": refs_for_names(module, paths),
                "request_field_candidates": refs_for_names(module, request),
                "response_field_candidates": refs_for_names(module, response),
                "verification": "Literal strings and structured tuples verified by parser. HTTP method, request/response classification and nesting are static-context inferences; instruction-level assignments and network behavior are not yet verified.",
                "note": note}
    contracts = {
        "evidence_type": "ordinary offline decoded typed constants; not REA; no API calls made",
        "auth": contract(auth, ["/api/desktop/auth/login", "/api/desktop/auth/refresh", "/api/desktop/auth/logout"],
                         ["email", "password", "device", "refresh_token"],
                         ["access_token", "refresh_token", "expires_in", "refresh_expires_in", "anchor_ticket", "mismatch"],
                         "Field presence does not establish token validity or service authorization."),
        "bootstrap": contract(bootstrap, ["/api/desktop/bootstrap"], ["access_token"],
                              ["account", "news", "app_about", "games", "routes", "ipv6_enabled", "wiresock_version",
                               "executables", "executable_name", "exe_name", "confirmation_text", "linked_app_names",
                               "incompatible_app_names", "best_region", "global_disallowed_ips", "global_disallowed_domains",
                               "region_disallowed_config", "disallowed_ips", "disallowed_domains",
                               "ezwire_destination_mode", "ezwire_forward_copies", "region_code", "city_name"],
                              "Authenticated bootstrap appears to supply app/game catalog, executable names, region-specific bypass rules and route metadata. Static constants establish schema vocabulary, not a recovered live catalog."),
        "session_connect": contract(session, ["/api/desktop/sessions/connect", "/api/desktop/sessions/disconnect"],
                                    ["route_name", "region_code", "client_public_key", "session_id", "access_token"],
                                    ["config", "Interface", "Peer", "routes", "address", "Address", "peer_public_key", "PublicKey",
                                     "peer_port", "Port", "mtu", "MTU", "route_endpoint", "route_ipv6_endpoint", "route_allowedips",
                                     "route_dns", "session_id", "server_ip", "proxy_ip", "proxy_username", "proxy_password",
                                     "amnezia_enabled", "junk_count", "junk_minimum", "junk_maximum", "junk_delay",
                                     "imitation_id", "imitation_ip", "imitation_ib"],
                                    "The parser vocabulary explicitly covers endpoint/key/interface configuration from service responses. These are dynamic settings; no live endpoint list was recovered offline."),
        "checkroute": contract(sweep, ["/api/desktop/sessions/checkroute", "/api/desktop/sessions/checkroute/cleanup"],
                               ["region", "client_public_key", "check_session_id", "access_token"],
                               ["servers", "check_session_id", "route_name", "region_code", "city_name", "router_id", "route_id", "failed"],
                               "Service provisions candidate sessions for a route sweep. Cleanup/session lifetime and field nesting require native tracing."),
        "ezwire": contract(ew, ["/api/desktop/ezwire/routes", "/api/desktop/ezwire/key", "/api/desktop/ezwire/key/release"],
                            ["device_hash", "hostname", "rotate", "access_token"],
                            ["routes", "name", "region_code", "ip", "ip_address", "port", "stun", "key", "key_id", "expires_in", "remaining_seconds", "revoked", "holder_hostname"],
                            "Route addresses and access grants appear to arrive from authenticated service replies. No key request, rotation, release or connection was performed."),
    }
    (ROOT / "ezconnect-research" / "analysis" / "parsed-api-contracts.json").write_text(json.dumps(contracts, indent=2), encoding="utf8")
    print("Wrote recovered-settings.json and parsed-api-contracts.json")


if __name__ == "__main__":
    main()
