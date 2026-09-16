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

## recordings/w1aw/ - the big test

Three ten-minute recordings of W1AW's Code Bulletin of 16 September 2026, taken through public
KiwiSDR receivers in Virginia, Ontario and West Virginia, with `ARLP037.txt`: the text ARRL
publishes word for word. 624 words of reference against the 34 the other bench can be sure of.

Score a decoder on it with `BulletinText.exe <folder>` piped into `ScoreText.py ARLP037.txt`.
Standing score for the plain decoder on these three: 1001 words read, 335 invented.

MORE CAN BE RECORDED ANY DAY. `RecordW1aw.ps1 -StartUtc HH:MM -Minutes N -Name x` records several
receivers at once; W1AW sends code bulletins at 2100, 0000 and 0300 UTC (an hour earlier in UTC
during daylight saving - its schedule is fixed in Eastern time, which is what caught the first
attempt out). The text appears at arrl.org/w1aw-bulletins-archive. `PickKiwis.py` ranks public
receivers by distance from Newington; some cut every listener off after ten seconds, so test before
relying on one. `KiwiRecord.py` opens "kiwi/<stamp>/SND" - kiwiclient's own path is answered with
silence by firmware 1.902.

## NEVER TUNE ON ONE BENCH

Tuned on the bulletin alone, the two keying thresholds looked like a triumph: words read 2102 ->
2398, invented 1186 -> 690. The same change scored 188 of 256 on the generated bench instead of 247,
and 12 of 34 on the operator's own recordings instead of 32. The bulletin comes through internet
receivers with their own AGC and audio path; tuning to it alone fits the decoder to somebody else's
radio. `AllBenches.ps1` scores all three at once and is the only honest way to judge a change.

## tools/

Build with `tools\build.ps1` (uses the .NET Framework compiler in Windows; output goes to `build\`,
which is not committed). Every tool is compiled against the program's own decoder source, so it
measures what ships.

**The two numbers that decide everything:**

- `DecoderTest` - 256 generated cases across 8 noise seeds. Run with seeds 0,100..700 and add up.
- `RealBench <recordings>` - words found on the real recordings, minus a penalty for letters
  printed on the quiet ones. Standing score: **32 of 34**.

**All three at once:** `AllBenches.ps1` - the generated bench, his recordings and W1AW's bulletin
for one value of one constant. `BulletinSweep.ps1` sweeps the bulletin alone (fast, but see the
warning above).

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

The LETTER network (TrainLetters.py) - taught by naming each letter in the gap after it, after CTC
proved unable to learn this task at all - does read real CW: 29 to 33 of 34 on his recordings, and it
reads LY2PX, which the plain decoder never has. On W1AW's bulletin, where thousands of words are
known, it lost clearly: 1707 words read against the plain decoder's 2102, with the same amount
invented. Not in the program. Its weights are not committed (they are regenerated by training);
`MakeCtcAudio.cs` holds the strength calibration that matters - the practice audio must sit in the
0.9 to 1.5 depth window the real band occupies, not the 1.9 to 2.8 the first sets had.
