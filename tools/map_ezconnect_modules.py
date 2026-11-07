"""Offline map: native module loaders to decoded constants tables.

Matches source-bound LEA instructions and decoded module names, no target execution.
"""
import bisect
import json
import pathlib
import re
import struct

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASE = ROOT / "ezconnect-research" / "analysis" / "decoded-constants"


def main():
    data = pathlib.Path(r"C:\Program Files\EZConnect\EZConnect.exe").read_bytes()
    table = bytes.fromhex(json.loads((BASE / "module-name-table.json").read_text())["table_hex"])
    frames = json.loads((BASE / "module-framing.json").read_text())["frames"]
    names = {r["name"] for r in frames}
    def fileoff(rva):
        for va, size, raw in [(0x1000, 0x17A5400, 0x400), (0x17A7000, 0x57200, 0x17A5800),
                              (0x17FF000, 0x75A600, 0x17FCA00), (0x2011000, 0x46400, 0x1F57000)]:
            if va <= rva < va + size:
                return raw + rva - va
        raise ValueError(rva)
    def string(rva):
        try:
            start = fileoff(rva)
            raw = data[start : data.index(0, start, start + 250)]
            return raw
        except (ValueError, IndexError):
            return b"\xff"
    functions = [struct.unpack_from("<III", data, off)[:2] for off in range(0x1F57000, 0x1F57000 + 0x46218, 12)]
    begins = [r[0] for r in functions]
    text = data[0x400 : 0x17A5800]
    mappings = []
    for match in re.finditer(b"\xe8", text):
        i = match.start()
        call = 0x1000 + i
        if i + 5 > len(text) or call + 5 + struct.unpack_from("<i", text, i + 1)[0] != 0x17A0500:
            continue
        start = max(0, i - 160)
        leas = []
        for candidate in re.finditer(rb"[\x48-\x4f]\x8d", text[start:i]):
            pos = start + candidate.start()
            if text[pos + 2] & 0xC7 != 5:
                continue
            reg = ((text[pos + 2] >> 3) & 7) + (8 if text[pos] & 4 else 0)
            target = 0x1000 + pos + 7 + struct.unpack_from("<i", text, pos + 3)[0]
            leas.append({"instruction_rva": 0x1000 + pos, "register_number": reg, "target_rva": target})
        candidates = []
        for lea in leas:
            raw = string(lea["target_rva"])
            for transformed, encoding in [(raw, "plain"), (raw.translate(table), "substitution_at_1762ff0")]:
                try:
                    name = transformed.decode("ascii")
                except UnicodeDecodeError:
                    continue
                if name in names and lea["register_number"] in (1, 8):
                    candidates.append((name, lea, encoding))
        if candidates:
            # Direct R8 or the most recent RCX string-loader argument.
            name, lea, encoding = candidates[-1]
        else:
            name, lea, encoding = None, None, None
        direct = [r for r in leas if r["register_number"] == 2]
        table_lea = direct[-1] if direct else None
        if table_lea is None:
            fallback = [r for r in leas if r["register_number"] in (7, 6) and r["target_rva"] >= 0x1F50000]
            table_lea = fallback[-1] if fallback else None
        index = bisect.bisect_right(begins, call) - 1
        func = functions[index] if index >= 0 and call < functions[index][1] else None
        mappings.append({"module": name, "load_call_rva": hex(call),
                         "enclosing_function_rva": hex(func[0]) if func else None,
                         "enclosing_function_end_rva": hex(func[1]) if func else None,
                         "constants_table_rva": hex(table_lea["target_rva"]) if table_lea else None,
                         "table_pointer_evidence": table_lea,
                         "name_pointer_evidence": lea, "name_encoding": encoding})
    output = {"evidence_type": "ordinary static byte/PE inspection, not REA",
              "loader_rva": "0x17a0500", "name_decoder_rva": "0x1762ff0",
              "limitations": "Table pointers are candidate LEA arguments; verify register flow at each loader call before relying on table identity. Module names match exact verified frame names.",
              "mappings": mappings}
    (BASE / "native-module-map.json").write_text(json.dumps(output, indent=2), encoding="utf8")
    print(json.dumps({"load_calls": len(mappings), "mapped_names": sum(r["module"] is not None for r in mappings),
                      "targets": [r for r in mappings if r["module"] in ["ezconnect.config.loader", "ezconnect.infrastructure.http.auth_gateway", "ezconnect.infrastructure.device.fingerprint", "ezconnect.infrastructure.device.windows_device_probe"]]}, indent=2))


if __name__ == "__main__":
    main()
