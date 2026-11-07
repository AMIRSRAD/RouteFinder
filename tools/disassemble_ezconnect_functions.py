"""Capstone static listing with imports and recovered constants-table annotations."""
import bisect
import json
import pathlib
import struct
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools" / "python-libs"))
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_MEM, X86_REG_RIP

BASE = ROOT / "ezconnect-research" / "analysis" / "decoded-constants"
data = pathlib.Path(r"C:\Program Files\EZConnect\EZConnect.exe").read_bytes()
imports = json.loads((BASE / "iat-symbols.json").read_text())
module_maps = json.loads((BASE / "native-module-map.json").read_text())["mappings"]
ranges = {}
for row in module_maps:
    if row["constants_table_rva"] is None:
        continue
    name = row["module"] or "_global"
    p = BASE / "parsed" / (name + ".json")
    if not p.exists():
        continue
    doc = json.loads(p.read_text())
    start = int(row["constants_table_rva"], 16)
    ranges[start] = (start + len(doc["constants"]) * 8, name, doc)
range_starts = sorted(ranges)
functions = [struct.unpack_from("<III", data, o)[:2] for o in range(0x1F57000, 0x1F57000 + 0x46218, 12)]
function_starts = [row[0] for row in functions]
md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
for arg in sys.argv[1:]:
    start = int(arg, 16)
    index = bisect.bisect_right(function_starts, start) - 1
    end = functions[index][1]
    lines = []
    for ins in md.disasm(data[start - 0xC00 : end - 0xC00], 0x140000000 + start):
        comments = []
        for op in ins.operands:
            if op.type != X86_OP_MEM or op.mem.base != X86_REG_RIP:
                continue
            target = ins.address + ins.size + op.mem.disp - 0x140000000
            if hex(target) in imports:
                comments.append(imports[hex(target)])
            i = bisect.bisect_right(range_starts, target) - 1
            if i >= 0:
                lo = range_starts[i]
                hi, name, doc = ranges[lo]
                if lo <= target < hi and (target - lo) % 8 == 0:
                    slot = (target - lo) // 8
                    comments.append(f"{name}#{slot}=" + json.dumps(doc["constants"][slot]["value"]))
        lines.append(f"{ins.address:016x} {ins.mnemonic:8} {ins.op_str}" + (" ; " + " / ".join(comments) if comments else ""))
    (BASE / f"annotated-{start:08x}.asm").write_text("\n".join(lines), encoding="utf8")
    print(f"{start:x}: {len(lines)} instructions")
