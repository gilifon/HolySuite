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

## recordings/4z5sl/ - his own transmission, text known

4Z5SL's IC-7610 at 30 W on 7028 kHz, keyed from HolyLogger's CW keyer (Ctrl+K) on 16 September 2026
at about 20:37 UTC, recorded at once on public KiwiSDRs in Austria (OE3AKB), Czechia (OK1DEK) and
Hungary (HG5ACZ). `SENT.txt` is exactly what went out: part 1 three times (at different speeds), then
part 3 - part 2 was never sent. Each file is cut to his transmission, so IK5WOB calling CQ before it
and G0BQV after it are not counted. Standing score (17 September): 88 of 337 words read, 233 invented.

## recordings/4z5sl2/ - the same text again, 17 September, on a clear frequency

The second session, and the better one: all three parts sent (part 2 included), 30 W on 7036 kHz at
about 20:31 UTC, heard in Greece (Nikea), Italy (Ischia, Trecastelli) and Hungary (HA2NA, HG5ACZ).
The frequency was chosen by listening: ten seconds recorded on each of eighteen candidates through a
Hungarian receiver and the quietest taken - 7036 and 7038 were busy 2% of the time, 7030 66% and
7022 85%. `SENT.txt` holds the three parts in the order sent.

Standing score: 282 of 584 words read, 275 invented - against 88 of 337 on the first session, on a
frequency where somebody else was working. Greece alone reads 87 of 121; the Hungarian copies, weak
and fading, read about a third. `AllBenches.ps1` counts both sessions together.

## recordings/4z5sl3/ - the third, on 30 metres in the morning

18 September at about 05:40 UTC, 30 W on 10106 kHz (found quiet by the same listening), heard in
Italy (Ischia, Trecastelli) and Hungary (HA2NA, HG5ACZ); the two German receivers tried heard
nothing of him. Part 3 dropped to 10 W for its last few words. Standing score: 139 of 335 words read,
192 invented. G4RCG began calling CQ on the frequency the moment he finished, so the files end with
his K.

To record another session: pick receivers from the public list by distance from Israel, test them
with a 15-second `KiwiRecord.py` first (about one in three refuse), find a quiet frequency the same
way, start them all recording, then he sends.

## NEVER TUNE ON ONE BENCH

Tuned on the bulletin alone, the two keying thresholds looked like a triumph: words read 2102 ->
2398, invented 1186 -> 690. The same change scored 188 of 256 on the generated bench instead of 247,
and 12 of 34 on the operator's own recordings instead of 32. The bulletin comes through internet
receivers with their own AGC and audio path; tuning to it alone fits the decoder to somebody else's
radio. `AllBenches.ps1` scores all four at once and is the only honest way to judge a change.

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
tracker, a lower threshold for elements inside a letter, a whole-character timing fit, a
strength-jump new-station test, joining every short dip in a mark, telling a fade from a gap by its
depth, reading on through a fade, and splitting an unspellable letter at its longest gap.

KEPT, 16-17 September 2026, each on all four benches: a lower start line in the shadow of a dah,
four marks before printing instead of eight, joining a dip when one piece is a fragment, and two
seconds' grace through a fade with nothing read. On his transmission the frequency was called empty
18-26% of the time and is now 13-14%, about the real pauses between his parts. What is left there is
elements never heard on a weak, fading signal.

The element-counting neural network (seven outputs, retrained on
this band) scored 0 of 34 despite 93.6% on clean CW: one miscounted element ruins a letter.

The LETTER network (TrainLetters.py) - taught by naming each letter in the gap after it, after CTC
proved unable to learn this task at all - does read real CW: 29 to 33 of 34 on his recordings, and it
reads LY2PX, which the plain decoder never has. On W1AW's bulletin, where thousands of words are
known, it lost clearly: 1707 words read against the plain decoder's 2102, with the same amount
invented. Not in the program.

Scored again on 17 September 2026 against the fourth bench, his own transmission over a fading path,
it is the one place a network wins: round2final.net read 92 words with 208 invented, against the
plain decoder's 88 and 233. Everywhere else it still loses - W1AW's three recordings 368 words against
402, his own recordings 27 of 34 against 32 (round3.net: 102 read but 348 invented on his
transmission, 28 of 34). So it does not replace the plain decoder; if it ever earns a place it will
be for fading signals only, and nothing yet tells the two cases apart. Its weights are not committed (they are regenerated by training);
`MakeCtcAudio.cs` holds the strength calibration that matters - the practice audio must sit in the
0.9 to 1.5 depth window the real band occupies, not the 1.9 to 2.8 the first sets had.

## Taught on real CW, 18 September 2026

The letter network had only ever been taught on CW this project generated. `LetterTimes.ps1` gives
the moment every letter the plain decoder spells ends; `LabelReal.py` lines those letters up with
ARRL's published bulletin, takes the best copy of each part as the clock, puts every other receiver
on that clock by the letters they share (all heard the same transmission at once, offsets agree to a
few ms) and cuts the text into twelve-second pieces labelled exactly as `MakeCtcAudio` labels:
1,254 pieces, about four hours, from the W1AW sessions of 15 and 16 September. Checked against the
decoder's own letter ends: within 25 ms, a dit being 67. W1AW sends a hyphen as the word DASH - the
reference is read that way, or four real letters went unlabelled.

Trained an hour on generated plus real, from round2final.net. `Exam.py` scores a network on his
three transmissions - texts it never trained on, so it cannot pass by knowing the words:

    plain decoder                          531 read   704 invented
    round2final (generated only)           473        715
    round4 (generated + real W1AW)         581        969
    round4, heard only where the plain
      decoder has proved Morse, +0.3 s     534        743      (GateMask.ps1)

Real audio made it much better - and it now only EQUALS the plain decoder. It reads more and hears
letters in the noise; gating it with the plain decoder's belief takes the noise out and most of the
gain with it. W1AW comes in strong to the American receivers; what it lacks is weak, fading copies,
which is where it would have to win. Not in the program.
