"""
Pulls the weights out of morseangel's default.model WITHOUT PyTorch installed.

The file is the old (pre-zip) torch.save format: five pickles one after another,
then the raw tensor bytes. Everything torch-specific is stubbed out, so all this
needs is the standard library.

Writes:
  weights.txt   - a listing of every tensor, its shape and a few of its numbers
  weights.bin   - the numbers themselves, little-endian float32, in a plain
                  format HolyLogger can read (see the header written below)
"""

import io
import pickle
import struct
import sys


class Storage:
    def __init__(self, key, numel, dtype):
        self.key = key
        self.numel = numel
        self.dtype = dtype
        self.data = None


class Tensor:
    def __init__(self, storage, offset, size, stride):
        self.storage = storage
        self.offset = offset
        self.size = size
        self.stride = stride

    def numbers(self):
        n = 1
        for d in self.size:
            n *= d
        return self.storage.data[self.offset:self.offset + n]


storages = {}


def rebuild_tensor_v2(storage, storage_offset, size, stride, requires_grad=False, backward_hooks=None, metadata=None):
    return Tensor(storage, storage_offset, tuple(size), tuple(stride))


class Stub:
    """Stands in for any torch class the pickle mentions but we do not need."""
    def __init__(self, *a, **k):
        pass

    def __call__(self, *a, **k):
        return None


DTYPE_SIZE = {"FloatStorage": 4, "DoubleStorage": 8, "LongStorage": 8, "IntStorage": 4}


class Reader(pickle.Unpickler):
    def find_class(self, module, name):
        if name == "_rebuild_tensor_v2":
            return rebuild_tensor_v2
        if module.startswith("torch"):
            return Stub
        return super().find_class(module, name)

    def persistent_load(self, pid):
        # ('storage', <storage type>, key, location, numel)
        tag = pid[0]
        if tag != "storage":
            raise RuntimeError("unexpected persistent id: %r" % (pid,))

        storage_type = pid[1]
        key = pid[2]
        numel = pid[4]

        name = getattr(storage_type, "__name__", str(storage_type))
        if name == "Stub":
            name = "FloatStorage"   # this model is all float32

        if key not in storages:
            storages[key] = Storage(key, numel, name)
        return storages[key]


def main(path, out_bin, out_txt):
    with open(path, "rb") as f:
        raw = f.read()

    buf = io.BytesIO(raw)

    magic = pickle.load(buf)
    protocol = pickle.load(buf)
    sysinfo = pickle.load(buf)

    reader = Reader(buf)
    state = reader.load()

    keys = pickle.load(buf)

    # The raw numbers follow, one block per storage: an 8-byte count, then the data.
    for key in keys:
        st = storages[key]
        count = struct.unpack("<q", buf.read(8))[0]
        size = DTYPE_SIZE[st.dtype]
        blob = buf.read(count * size)
        if st.dtype == "FloatStorage":
            st.data = list(struct.unpack("<%df" % count, blob))
        else:
            raise RuntimeError("unhandled storage type " + st.dtype)

    # state may be an OrderedDict of name -> Tensor, or a dict holding one.
    if not isinstance(state, dict):
        raise RuntimeError("expected a dict of tensors, got %r" % type(state))

    tensors = {k: v for k, v in state.items() if isinstance(v, Tensor)}
    if not tensors:
        raise RuntimeError("no tensors found; keys were %r" % list(state.keys()))

    lines = []
    lines.append("magic=%s protocol=%s" % (magic, protocol))
    lines.append("%d tensors" % len(tensors))
    total = 0
    for name, t in tensors.items():
        nums = t.numbers()
        total += len(nums)
        lines.append("%-40s %-16s %8d   first: %s" % (
            name, "x".join(str(d) for d in t.size), len(nums),
            ", ".join("%.6f" % v for v in nums[:4])))
    lines.append("total numbers: %d" % total)

    with open(out_txt, "w") as f:
        f.write("\n".join(lines) + "\n")

    # A plain file for C#: "MORSENN1", tensor count, then per tensor:
    # name length, name (ASCII), rank, dims, then the float32 numbers.
    with open(out_bin, "wb") as f:
        f.write(b"MORSENN1")
        f.write(struct.pack("<i", len(tensors)))
        for name, t in tensors.items():
            nb = name.encode("ascii")
            f.write(struct.pack("<i", len(nb)))
            f.write(nb)
            f.write(struct.pack("<i", len(t.size)))
            for d in t.size:
                f.write(struct.pack("<i", d))
            nums = t.numbers()
            f.write(struct.pack("<%df" % len(nums), *nums))

    print("\n".join(lines))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
