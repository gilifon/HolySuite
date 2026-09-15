"""What a letter-network checkpoint reads off every real recording, next to the words expected."""

import os
import sys

import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainLetters import LetterNet, read, real_score  # noqa: E402
from LookAtAnswers import load  # noqa: E402


def main(real_file, weights):
    real, bins = read(real_file)
    model = LetterNet(bins)
    load(model, weights)
    score, found, wanted, noise, lines = real_score(model, real)
    print("\n".join(lines))
    print("REAL %d of %d words, -%d noise => %d   (plain decoder: 32)" % (found, wanted, noise, score))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
