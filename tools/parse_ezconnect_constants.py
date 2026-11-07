"""Parse the target's observed randomized Nuitka constants tags, without execution.

Container/number/string grammar follows official Nuitka HelpersConstantsBlob.c;
tag bytes and code flags are source-bound to EZConnect RVA 0x179bd00.
"""
import hashlib
import json
import math
import pathlib
import struct

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASE = ROOT / "ezconnect-research" / "analysis" / "decoded-constants"


class Parser:
    def __init__(self, data, name):
        self.data, self.name, self.pos, self.blobs = data, name, 0, []

    def take(self, n):
        if n < 0 or self.pos + n > len(self.data):
            raise ValueError(f"overrun at {self.pos} length {n}")
        result = self.data[self.pos : self.pos + n]
        self.pos += n
        return result

    def byte(self):
        return self.take(1)[0]

    def varint(self):
        result = 0
        for shift in range(0, 128, 7):
            byte = self.byte()
            result |= (byte & 127) << shift
            if byte < 128:
                return result
        raise ValueError("excessive varint")

    def cstring(self):
        end = self.data.index(0, self.pos)
        return self.take(end - self.pos + 1)[:-1]

    def many(self, count):
        values = []
        for _ in range(count):
            values.append(self.value(values[-1] if values else None))
        return values

    def value(self, previous=None):
        offset = self.pos
        tag = self.byte()
        if tag == 0xE2:
            return previous
        if tag == 0x96:
            return None
        if tag == 0x0B:
            return True
        if tag == 0xDF:
            return False
        if tag == 0xAF:
            return ""
        if tag in (0xF1, 0x97, 0x59, 0x9F):
            kind = {0xF1: "tuple", 0x97: "list", 0x59: "set", 0x9F: "frozenset"}[tag]
            return {"type": kind, "items": self.many(self.varint())}
        if tag == 0x8D:
            count = self.varint()
            keys, values = self.many(count), self.many(count)
            return {"type": "dict", "items": list(zip(keys, values))}
        if tag in (0x75, 0x41):
            return self.varint() * (1 if tag == 0x75 else -1)
        if tag in (0x20, 0xCB):
            value = 0
            for _ in range(self.varint()):
                value = (value << 31) + self.varint()
            return value * (-1 if tag == 0x20 else 1)
        if tag == 0x47:
            return struct.unpack("<d", self.take(8))[0]
        if tag == 0x32:
            real, imag = struct.unpack("<dd", self.take(16))
            return {"type": "complex", "real": real, "imag": imag}
        if tag == 0x10:
            real, imag = self.many(2)
            return {"type": "complex", "real": real, "imag": imag}
        if tag in (0x09, 0x49):
            return self.cstring().decode("utf8", "surrogatepass")
        if tag == 0x98:
            return self.take(1).decode("utf8", "surrogatepass")
        if tag == 0xB7:
            return self.take(self.varint()).decode("utf8", "surrogatepass")
        if tag in (0x24, 0xC7, 0xFA, 0xBB):
            value = self.cstring() if tag == 0x24 else self.take(1 if tag == 0xFA else self.varint())
            return {"type": "bytearray" if tag == 0xBB else "bytes", "hex": value.hex()}
        if tag in (0x33, 0xA9):
            return {"type": "slice" if tag == 0x33 else "range", "items": self.many(3)}
        if tag in (0x86, 0xA4):
            return {"type": "builtin_anonymous" if tag == 0x86 else "builtin_special", "index": self.byte()}
        if tag in (0x28, 0xE7):
            return {"type": "builtin" if tag == 0x28 else "exception", "name": self.cstring().decode("ascii")}
        if tag == 0xA1:
            selector = self.byte()
            # Target's nested switch at RVA 0x179c85c and source-bound doubles.
            values = {0x3D: "+0.0", 0xFF: "-0.0", 0xBD: "+inf",
                      0xD3: "-inf", 0xE5: "-nan", 0xF9: "+nan"}
            return {"type": "special_float", "value": values[selector],
                    "target_encoded_selector": selector}
        if tag == 0x1D:
            raw = self.take(self.varint())
            blob_path = BASE / "blobs" / f"{self.name}-{offset:08x}.bin"
            blob_path.parent.mkdir(exist_ok=True)
            blob_path.write_bytes(raw)
            self.blobs.append(str(blob_path))
            return {"type": "blob", "length": len(raw), "sha256": hashlib.sha256(raw).hexdigest(), "path": str(blob_path)}
        if tag == 0xC4:
            origin, args = self.many(2)
            return {"type": "generic_alias", "origin": origin, "args": args}
        if tag == 0xC1:
            return {"type": "union", "args": self.value()}
        if tag == 0x64:
            flags = self.varint()
            name = self.value()
            line = self.varint() + 1
            args = self.value()
            arg_count = self.varint()
            qualname = self.value() if flags & (1 << 23) else name
            freevars = self.value() if flags & (1 << 20) else None
            kwonly = self.varint() + 1 if flags & 4 else 0
            posonly = self.varint() + 1 if flags & (1 << 10) else 0
            return {"type": "compiled_code_descriptor", "name": name, "qualname": qualname,
                    "line": line, "varnames": args, "arg_count": arg_count,
                    "kwonly_count": kwonly, "posonly_count": posonly,
                    "freevars": freevars, "encoded_flags": flags}
        raise ValueError(f"unhandled tag {tag:#x} at {offset:#x}")

    def parse(self):
        count = struct.unpack("<H", self.take(2))[0]
        records = []
        previous = None
        for index in range(count):
            offset = self.pos
            previous = self.value(previous)
            records.append({"index": index, "payload_offset": offset, "value": previous})
        tail = self.data[self.pos :]
        if tail not in (b"", b"\x7e"):
            raise ValueError(f"unexpected trailing bytes at {self.pos:#x}: {tail[:20].hex()}")
        return {"module": self.name, "constant_count": count, "parsed_bytes": self.pos,
                "payload_bytes": len(self.data), "trailer_hex": tail.hex(), "constants": records}


def main():
    data = (BASE / "constants.bin").read_bytes()
    frames = json.loads((BASE / "module-framing.json").read_text())["frames"]
    output = BASE / "parsed"
    output.mkdir(exist_ok=True)
    summary = []
    for frame in frames:
        name = frame["name"] or "_global"
        payload = data[frame["payload_offset"] : frame["payload_offset"] + frame["payload_bytes"]]
        parser = Parser(payload, name)
        try:
            doc = parser.parse()
            doc["decoded_payload_offset"] = frame["payload_offset"]
            (output / (name + ".json")).write_text(json.dumps(doc, indent=2, ensure_ascii=True), encoding="utf8")
            summary.append({"module": name, "status": "complete", "count": doc["constant_count"]})
        except Exception as exc:
            summary.append({"module": name, "status": "failed", "position": parser.pos, "error": str(exc)})
    (output / "_parse-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf8")
    complete = sum(row["status"] == "complete" for row in summary)
    print(json.dumps({"complete": complete, "total": len(summary), "failures": [r for r in summary if r["status"] != "complete"][:12]}))


if __name__ == "__main__":
    main()
