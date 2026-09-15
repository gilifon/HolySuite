"""
The same network again, written independently in numpy, to check the C# one.

Nothing here is translated from CwNeuralNet.cs; it is written from PyTorch's own
definition of nn.LSTM - gates in the order i, f, g, o, both bias vectors added,
the hidden state carried from one step to the next - so if the two agree on the
same weights and the same input, the C# forward pass is right.

Reads the envelope numbers on standard input, one per line, and prints the seven
outputs for every step.
"""

import struct
import sys

import numpy as np


def read_weights(path):
    with open(path, "rb") as f:
        raw = f.read()

    assert raw[:8] == b"MORSENN1", "not the weights file"
    at = 8
    (count,) = struct.unpack_from("<i", raw, at); at += 4

    out = {}
    for _ in range(count):
        (n,) = struct.unpack_from("<i", raw, at); at += 4
        name = raw[at:at + n].decode(); at += n
        (dims,) = struct.unpack_from("<i", raw, at); at += 4
        shape = []
        for _ in range(dims):
            (d,) = struct.unpack_from("<i", raw, at); at += 4
            shape.append(d)
        total = 1
        for d in shape:
            total *= d
        nums = np.frombuffer(raw, dtype="<f4", count=total, offset=at).astype(np.float64)
        at += total * 4
        out[name] = nums.reshape(shape)
    return out


def sigmoid(x):
    return 1.0 / (1.0 + np.exp(-x))


def main(weights_path):
    w = read_weights(weights_path)

    hidden = w["lstm.bias_ih_l0"].shape[0] // 4
    layers = 2

    h = [np.zeros(hidden) for _ in range(layers)]
    c = [np.zeros(hidden) for _ in range(layers)]

    for line in open(sys.argv[2], 'r', encoding='utf-8-sig'):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        x = np.array([float(line)])

        for layer in range(layers):
            wi = w["lstm.weight_ih_l%d" % layer]
            wh = w["lstm.weight_hh_l%d" % layer]
            bi = w["lstm.bias_ih_l%d" % layer]
            bh = w["lstm.bias_hh_l%d" % layer]

            gates = wi.dot(x) + bi + wh.dot(h[layer]) + bh

            i = sigmoid(gates[0:hidden])
            f = sigmoid(gates[hidden:2 * hidden])
            g = np.tanh(gates[2 * hidden:3 * hidden])
            o = sigmoid(gates[3 * hidden:4 * hidden])

            c[layer] = f * c[layer] + i * g
            h[layer] = o * np.tanh(c[layer])
            x = h[layer]

        y = w["linear.weight"].dot(x) + w["linear.bias"]
        print(" ".join("%.6f" % v for v in y))


if __name__ == "__main__":
    main(sys.argv[1])
