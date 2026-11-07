"""Bounded offline PE/RIP-reference candidates, ordinary evidence (not REA)."""
import json
import pathlib
import struct

target = pathlib.Path(r"C:\Program Files\EZConnect\ezconnect_engine\ezconnect_engine.pyd")
data = target.read_bytes()
pe = struct.unpack_from("<I", data, 0x3C)[0]
count = struct.unpack_from("<H", data, pe + 6)[0]
optional = pe + 24
optional_size = struct.unpack_from("<H", data, pe + 20)[0]
base = struct.unpack_from("<Q", data, optional + 24)[0]
sections = []
for i in range(count):
    name, vsize, rva, rawsize, offset = struct.unpack_from("<8sIIII", data, optional + optional_size + 40 * i)
    sections.append(dict(name=name.rstrip(b"\0").decode(), vsize=vsize, rva=rva, rawsize=rawsize, offset=offset))
def file_to_rva(offset):
    section = next(s for s in sections if s["offset"] <= offset < s["offset"] + s["rawsize"])
    return section["rva"] + offset - section["offset"]
pdata = next(s for s in sections if s["name"] == ".pdata")
functions = [struct.unpack_from("<III", data, i) for i in range(pdata["offset"], pdata["offset"] + pdata["vsize"], 12)]
text = next(s for s in sections if s["name"] == ".text")
needles = [b"user key must look", b"PSK must be at least", b"server rejected our key", b"HELLO retries", b"key rejected by server", b"BadVersion", b"auth.rs", b"ezk1.\x08recv"]
findings = []
for needle in needles:
    at = data.find(needle)
    if at < 0:
        continue
    target_rva = file_to_rva(at)
    refs = []
    # LEA r64,[RIP+disp32]. Byte matches are candidates until corroborated by a disassembler.
    for pos in range(text["offset"], text["offset"] + text["vsize"] - 7):
        if data[pos] not in range(0x48, 0x50) or data[pos + 1] != 0x8D or data[pos + 2] & 0xC7 != 0x05:
            continue
        rva = file_to_rva(pos)
        destination = rva + 7 + struct.unpack_from("<i", data, pos + 3)[0]
        if not target_rva - 160 <= destination <= target_rva + len(needle):
            continue
        function = next(((start, end, unwind) for start, end, unwind in functions if start <= rva < end), None)
        refs.append(dict(instruction_file_offset=pos, instruction_address=hex(base+rva), referenced_address=hex(base+destination), delta_from_string=destination-target_rva, unwind_function_range=[hex(base+function[0]),hex(base+function[1])] if function else None))
    findings.append(dict(literal=needle.decode(), file_offset=at, address=hex(base+target_rva), rip_lea_candidates=refs))
result = dict(evidence_type="ordinary offline PE parsing and byte-pattern RIP LEA candidates, not REA or executed behavior", target=str(target), image_base=hex(base), sections=sections, findings=findings)
pathlib.Path("ezconnect-research/analysis/engine-static-rip-candidates.json").write_text(json.dumps(result, indent=2))
for finding in findings:
    print(json.dumps(finding))
