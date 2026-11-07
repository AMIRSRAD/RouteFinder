"""Offline, source-bound translation of EZConnect's observed constants decoder.

Does not load or execute the target. Requires the scoped tools/python-libs capstone.
"""
import hashlib
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools" / "python-libs"))
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_OP_REG, X86_REG_RIP, X86_REG_AL

SOURCE = pathlib.Path(r"C:\Program Files\EZConnect\EZConnect.exe")
OUTPUT = ROOT / "ezconnect-research" / "analysis" / "decoded-constants"
IMAGE_BASE = 0x140000000
RAW_DELTA = 0xC00
TABLE_RVA = 0x200C5F0
TABLE_INIT_RVA = 0x17630C0
DECODER_RVA = 0x16DC850
BLOB_RAW = 0x17FCA00
BLOB_RVA = 0x17FF000
ITERATIONS = 0x740245


def main():
    data = SOURCE.read_bytes()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    table = [None] * 256
    eax = 0
    stack_value = 0
    listing = []
    raw = TABLE_INIT_RVA - RAW_DELTA
    for ins in md.disasm(data[raw : raw + 0xA00], IMAGE_BASE + TABLE_INIT_RVA):
        listing.append(f"{ins.address:016x} {ins.mnemonic} {ins.op_str}")
        ops = ins.operands
        if ins.mnemonic == "ret":
            break
        if ins.mnemonic != "mov" or len(ops) != 2:
            continue
        dst, src = ops
        # The initializer includes volatile stack noise. Only AL can feed a table write.
        if dst.type == X86_OP_MEM and dst.mem.base != X86_REG_RIP and src.type == X86_OP_IMM:
            stack_value = src.imm
        elif dst.type == X86_OP_REG and ins.reg_name(dst.reg) == "eax" and src.type == X86_OP_MEM:
            eax = stack_value
        elif dst.type == X86_OP_MEM and dst.mem.base == X86_REG_RIP:
            address = ins.address + ins.size + dst.mem.disp
            index = address - IMAGE_BASE - TABLE_RVA
            if 0 <= index < 256:
                if src.type == X86_OP_IMM:
                    value = src.imm
                elif src.type == X86_OP_REG and src.reg == X86_REG_AL:
                    value = eax & 255
                else:
                    raise RuntimeError(f"Untranslated table write: {listing[-1]}")
                for byte_index in range(dst.size):
                    table[index + byte_index] = (value >> (byte_index * 8)) & 255
    if any(v is None for v in table) or len(set(table)) != 256:
        raise RuntimeError("Table extraction did not produce a 256-byte permutation")
    decoder_raw = DECODER_RVA - RAW_DELTA
    listing.extend(["", "DECODER"])
    for ins in md.disasm(data[decoder_raw : decoder_raw + 0xCA], IMAGE_BASE + DECODER_RVA):
        listing.append(f"{ins.address:016x} {ins.mnemonic} {ins.op_str}")
    (OUTPUT / "decoder-disassembly.txt").write_text("\n".join(listing), encoding="utf8")
    decoded = bytearray(ITERATIONS)
    previous = 0
    adjustments = (0x16, -0x5F, -0x29, 0x1A, -2, -0xB, 0x40, -0x23)
    for i in range(ITERATIONS):
        current = table[((previous + i) & 255) ^ data[BLOB_RAW + i]]
        decoded[i] = current
        previous = (current + adjustments[i % 8]) & 255
    # The observed native loop discards its first sixteen decoded bytes by writing
    # byte i to source[i - 16]. Preserve these separately for reproducibility.
    blob = bytes(decoded[16:])
    (OUTPUT / "prefix.bin").write_bytes(decoded[:16])
    (OUTPUT / "constants.bin").write_bytes(blob)
    strings = [{"decoded_offset": m.start(), "text": m.group().decode("ascii")}
               for m in re.finditer(rb"[ -~]{5,}", blob)]
    (OUTPUT / "strings.json").write_text(json.dumps(strings, indent=2), encoding="utf8")
    provenance = {
        "evidence_type": "ordinary offline Capstone disassembly and translated Python decoder, not REA",
        "source": str(SOURCE), "source_sha256": hashlib.sha256(data).hexdigest(),
        "image_base": hex(IMAGE_BASE), "decoder_rva": hex(DECODER_RVA),
        "table_initializer_rva": hex(TABLE_INIT_RVA), "table_rva": hex(TABLE_RVA),
        "source_blob_rva": hex(BLOB_RVA), "source_blob_file_offset": BLOB_RAW,
        "iterations": ITERATIONS, "discarded_prefix_bytes": 16,
        "output_bytes": len(blob), "output_sha256": hashlib.sha256(blob).hexdigest(),
        "table_hex": bytes(table).hex(), "adjustments": adjustments,
        "validation": "Table is a permutation; module framing validation pending",
        "limitations": "Recovered constants are data, not original Python source or verified runtime behavior",
    }
    (OUTPUT / "provenance.json").write_text(json.dumps(provenance, indent=2), encoding="utf8")
    print(json.dumps({"output_bytes": len(blob), "string_count": len(strings), "first64_hex": blob[:64].hex()}))


if __name__ == "__main__":
    main()
