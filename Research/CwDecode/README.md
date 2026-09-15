# CW decoder research

Test material and tools for the CW decoder in `HolyLogger/CwDecoder.cs` and the neural reader
beside it. **Nothing here is part of the program or its installer.** It exists so every change to
the decoder can be measured instead of judged by eye.

## recordings/

Real audio off the IC-7610's USB codec, 8000 Hz, 16-bit mono.

| file | what is in it |
|---|---|
| quiet.wav | V4TQ working IZ5CMG and SP9ADG; a fast station hands over to a slow one at 38 s |
| radio.wav | French QSO, 675 Hz, about 30 WPM |
| radio2.wav | about 19 WPM ragchew |
| narrow.wav | LB2WD, radio filter narrowed |
| bad.wav | the same QSO with four stations in a 500 Hz passband |
| ly2px.wav | LY2PX calling CQ - the callsign the operator confirmed by ear |
| ly2px2.wav | LY2PX, fast and weak |
| slow.wav | IU5RDL calling CQ at 15 WPM |
| slow2.wav | the same station later, much harder - not scored |
| weak.wav | weak F4A.. working VK6, 24 WPM, 496 Hz |
| active.wav | R1LN on a busy frequency |
| real2.wav | a marginal station that stops two minutes in - not scored |
| beacons.wav | 14.100 MHz, three NCDXF beacons NEITHER decoder can read - must stay quiet |
| session1.wav | eight minutes of a band that emptied - must stay quiet |

The expected words in the benches are NOT guesses. Each is a word this decoder and ggmorse (a
separate decoder sharing no code) both produced independently, except LY2PX, which the operator
heard himself.

## tools/

Build with `tools\build.ps1` (uses the .NET Framework compiler in Windows; output goes to `build\`,
which is not committed). Every tool is compiled against the program's own decoder source, so it
measures what ships.

**The two numbers that decide everything:**

- `DecoderTest` - 256 generated cases across 8 noise seeds. Run with seeds 0,100..700 and add up.
- `RealBench <recordings>` - words found on the real recordings, minus a penalty for letters
  printed on the quiet ones. Standing score: **32 of 34**.

**Others:** `Judge` (plain decoder against a neural weights file, same scoring), `Sweep.ps1` (one
constant across both benches plus the wait before the first letter), `Delay` (that wait),
`Quiet` (text and signal state per tenth of a second), `Compare` (both decoders on one file),
`Recorder` (records the radio).

**Neural network work:** `MakeTraining` (known CW keyed into the real band noise in
session1/beacons), `MakeLabels`, `Train.py`, `EvalWeights.py`, `CleanConvention.py`,
`FindOffset.py`, `RunLengths.py`, `CsAnswers`, `LabelProbe`, `FrontDelay`, `netcheck.py`,
`reference.py`, `extract.py`. Python needs numpy, scipy and torch (CPU).

`Letters.cs` needs a decoder patched to expose its last marks and is not in the build.

`ggmorse/` holds the two small Windows shims needed to build ggmorse-from-file with a portable
MinGW (w64devkit); ggmorse itself is not copied here.

## What has been measured and rejected

Recorded in the decoder source with the numbers, so none of it is tried again: a learned letter-gap
line (twice), a lower key threshold, a separate key-down/key-up level tracker, a percentile level
tracker, a lower threshold for elements inside a letter, a whole-character timing fit, and a
strength-jump new-station test. The element-counting neural network (seven outputs, retrained on
this band) scored 0 of 34 despite 93.6% on clean CW: one miscounted element ruins a letter.
