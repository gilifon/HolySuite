using System;
using System.Collections.Generic;
using System.Text;

namespace HolyLogger
{
    // READS CW OUT OF THE AUDIO, with arithmetic only - no model, no library, nothing shipped
    // alongside. This is the decoder every program of this kind has had since the 1980s, and on a
    // clean signal it is very good. On a crowded band, or a signal that fades, it is not - which is
    // what the neural network is for later. Both need this same front end, so it is written first.
    //
    // ONE SIGNAL, THE ONE HE IS TUNED TO. The window follows the note in the receiver's passband
    // rather than reading the whole band at once the way a skimmer does. That matches how the
    // operator works a station: he tunes somebody in and wants to know what that somebody is
    // sending. Reading several at once is a different program and can come later if he wants it.
    //
    // How it works, in four steps:
    //   1. Every 10 ms, measure how much energy sits at each of a row of frequencies across the
    //      passband (a Goertzel filter each - a one-frequency Fourier transform, a few lines long).
    //   2. The frequency that has held the most energy over the last second or so IS the note being
    //      sent. Nobody has to type in the pitch, and it follows him when he tunes.
    //   3. That one frequency's energy over time is the key going up and down. A threshold that
    //      moves with the noise turns it into on and off.
    //   4. Lengths of the on and off stretches become dits, dahs, letter gaps and word gaps. The
    //      speed is learned from what arrives, so nothing has to be set for a fast or slow operator.
    public class CwDecoder
    {
        // ----- the shape of the analysis -----

        // A reading every 5 ms. A dit at 40 WPM is 30 ms, so even the fastest operator gets six
        // readings to a dit and the measurement is never what limits the accuracy.
        const double FrameMilliseconds = 5.0;

        // BUT EACH READING LOOKS AT 15 ms OF SOUND, the last three hops, overlapping. The width of
        // the filter is set by how much sound it looks at and nothing else: 5 ms of audio can only
        // be measured to about 200 Hz, which lets in a great deal of band noise and most of the
        // station next door, while 15 ms narrows that to about 65 Hz. Overlapping is what allows
        // both - a narrow filter AND timing measured to 5 ms - where a plain run of 15 ms blocks
        // would have to give one up for the other.
        const int HopsPerWindow = 3;

        // The band of notes we look in. Nobody listens to CW below 300 Hz or above 1200; a wider
        // search would only offer more chances to lock onto the wrong thing.
        const double LowestTone = 300.0;
        const double HighestTone = 1200.0;
        const double ToneStep = 25.0;

        // Speeds we are willing to believe. Outside this the timing has gone wrong, not the operator.
        const double SlowestDitMs = 200.0;   // 6 WPM
        const double FastestDitMs = 24.0;    // 50 WPM

        // A mark shorter than this is a click or a burst of noise, never a dit - and it must never be
        // allowed into the speed estimate. THIS IS THE FAULT THAT SPOILED THE FIRST VERSION ON THE
        // AIR: noise crossing the threshold for two readings counted as a very short dit, dragged the
        // learned speed down to the fastest it would believe, and from there every real gap looked
        // like the end of a letter - so the window filled with lone Es. A mark also has to be a
        // reasonable fraction of the dit length already being heard, which is the second guard.
        // Kept gentle on purpose. An earlier attempt threw away anything under 25 ms, or under half
        // the dit length already learned, and that turned out to be a trap of its own: at 40 WPM a
        // dit IS 30 ms, so the dits were thrown away, only the dahs survived, the learned speed
        // followed them upwards, and the rule then rejected everything real. Keeping noise out is
        // the job of the signal test above, which can tell noise from a station properly.
        const double ShortestRealMarkMs = 15.0;
        const double ShortestFractionOfDit = 0.25;

        // How far above the rest of the band a note has to stand before it is believed to be a
        // station. Measured in strength, so 6 is a little under 8 dB - low enough for a signal only
        // just readable by ear, high enough that noise never reaches it.
        const double SignalOverNoise = 6.0;

        // How many dits and dahs must be heard before anything is written on screen at all. Eight is
        // two or three characters - a moment's delay when a station starts, and far more structure
        // than noise ever manages to keep up.
        const int MarksNeededBeforeBelieving = 8;

        // The most held-back text kept while waiting to be sure. Eight marks is two or three
        // characters; this is generous, and it stops a long carrier quietly filling memory.
        const int MostHeldBack = 40;

        // The running score that decides whether this is Morse at all - see Score below.
        const int MorseScoreCeiling = 12;
        const int MorseScoreToOpen = 8;
        const int MorseScoreToKeep = 3;

        // How much louder the note must be with the key down than with it up before the keying is
        // believed. Twice is modest for a real signal - a key takes the tone away entirely - and far
        // beyond anything a steady carrier with noise on it can reach.
        const double KeyingDepth = 2.0;

        // How many recent dits and dahs are kept to decide which is which. Two dozen is three or
        // four characters - enough to be sure both kinds are in there, short enough to follow an
        // operator who changes speed, or a new station taking over the frequency.
        const int MarkMemory = 24;

        readonly int _sampleRate;
        readonly int _hopSamples;
        readonly int _windowSamples;
        readonly double[] _toneFrequencies;
        readonly double[] _coefficients;
        readonly double[] _binAverage;      // slow average per frequency: which note is really there
        readonly double[] _binPower;        // this reading's strength per frequency
        readonly double[] _binSorted;       // scratch for finding the middle of the band
        readonly double[] _window;          // the last _windowSamples of audio, oldest first
        readonly double[] _taper;           // smooths the ends of the window
        readonly double[] _recentLevels = new double[3];
        int _windowFill;
        int _levelIndex;

        int _bestBin;
        double _noiseFloor;
        double _peak;
        bool _levelsSeeded;

        bool _keyDown;
        double _stateMs;                    // how long the current on/off stretch has lasted
        double _ditMs = 60.0;               // 20 WPM until the sending says otherwise
        readonly double[] _marks = new double[MarkMemory];
        readonly double[] _sorted = new double[MarkMemory];
        int _markCount, _markNext;
        readonly StringBuilder _symbols = new StringBuilder();
        readonly List<double> _letterMarks = new List<double>(12);
        double _boundaryMs = 104.0;         // the dit/dah dividing line, until sending sets it
        bool _looksLikeMorse;               // is what we are hearing built of dits and dahs at all?
        int _morseScore;
        double _onLevel, _offLevel;         // how loud the note is with the key down, and up
        readonly StringBuilder _held = new StringBuilder();
        bool _letterPending;
        bool _wordPending;

        /// <summary>The note being decoded, in Hz. 0 before anything has been heard.</summary>
        public double ToneHz { get; private set; }

        /// <summary>Sending speed as measured from the air, in words a minute.</summary>
        public double Wpm { get { return 1200.0 / _ditMs; } }

        /// <summary>True while the decoder can see a signal above the noise.</summary>
        public bool SignalPresent { get; private set; }

        /// <summary>
        /// Decoded characters as they are finished, one or a few at a time. Raised on whichever
        /// thread feeds Process - the capture thread - so a screen handler must marshal.
        /// </summary>
        public event Action<string> Text;

        public CwDecoder(int sampleRate)
        {
            _sampleRate = sampleRate < 4000 ? 8000 : sampleRate;
            _hopSamples = (int)Math.Round(_sampleRate * FrameMilliseconds / 1000.0);
            _windowSamples = _hopSamples * HopsPerWindow;
            _window = new double[_windowSamples];

            // A raised cosine over the window. Without it the sharp ends of each window spray energy
            // across the whole search, and a strong station a few hundred Hz away shows up in the bin
            // we are listening to. This is most of what "narrower" buys.
            _taper = new double[_windowSamples];
            for (int i = 0; i < _windowSamples; i++)
                _taper[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (_windowSamples - 1));

            var freqs = new List<double>();
            for (double f = LowestTone; f <= HighestTone; f += ToneStep) freqs.Add(f);
            _toneFrequencies = freqs.ToArray();

            _coefficients = new double[_toneFrequencies.Length];
            for (int i = 0; i < _toneFrequencies.Length; i++)
                _coefficients[i] = 2.0 * Math.Cos(2.0 * Math.PI * _toneFrequencies[i] / _sampleRate);

            _binAverage = new double[_toneFrequencies.Length];
            _binPower = new double[_toneFrequencies.Length];
            _binSorted = new double[_toneFrequencies.Length];
            _bestBin = _toneFrequencies.Length / 2;
        }

        /// <summary>Forget the speed, the note and the half-finished letter. Used by Clear.</summary>
        public void Reset()
        {
            Array.Clear(_binAverage, 0, _binAverage.Length);
            Array.Clear(_window, 0, _window.Length);
            Array.Clear(_recentLevels, 0, _recentLevels.Length);
            _windowFill = 0; _levelIndex = 0;
            _noiseFloor = 0; _peak = 0; _levelsSeeded = false;
            _keyDown = false; _stateMs = 0;
            _ditMs = 60.0;
            _markCount = 0; _markNext = 0;
            Array.Clear(_marks, 0, _marks.Length);
            _symbols.Clear();
            _letterMarks.Clear();
            _boundaryMs = 104.0;
            _looksLikeMorse = false;
            _morseScore = 0;
            _onLevel = 0; _offLevel = 0;
            _held.Clear();
            _letterPending = false; _wordPending = false;
            SignalPresent = false;
            ToneHz = 0;
        }

        /// <summary>Feeds one block of samples from the recorder. Call it with every block.</summary>
        public void Process(short[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _window[_windowFill++] = samples[i] / 32768.0;
                if (_windowFill < _windowSamples) continue;

                AnalyseWindow();

                // Slide on by one hop and keep the tail, so the next reading overlaps this one.
                Array.Copy(_window, _hopSamples, _window, 0, _windowSamples - _hopSamples);
                _windowFill = _windowSamples - _hopSamples;
            }
        }

        void AnalyseWindow()
        {
            // Step 1: how strong each candidate note is in the last 20 ms.
            double best = 0;
            int bestIndex = _bestBin;

            for (int b = 0; b < _toneFrequencies.Length; b++)
            {
                _binPower[b] = Goertzel(_coefficients[b]);

                // A slow average per note. The tone is only present half the time - it is CW - so a
                // single reading proves nothing, but over a second the sent note stands out clearly.
                _binAverage[b] = _binAverage[b] * 0.99 + _binPower[b] * 0.01;

                if (_binAverage[b] > best) { best = _binAverage[b]; bestIndex = b; }
            }

            // Step 2: change note only when another is clearly stronger. Without the margin the
            // choice would flutter between neighbouring bins on every fade.
            if (bestIndex != _bestBin && best > _binAverage[_bestBin] * 1.3)
                _bestBin = bestIndex;

            ToneHz = _toneFrequencies[_bestBin];

            // Step 3: that note's LOUDNESS is the key, up or down.
            //
            // Loudness, not energy. Energy is loudness squared, and squaring stretches the random
            // ups and downs of band noise into something that looks a lot like a signal - which is
            // how noise came to be decoded as letters. Taking the square root puts noise back into a
            // narrow band around its own average, where a threshold can be set above it honestly.
            double level = Math.Sqrt(_binPower[_bestBin]);

            // Averaged over the last three readings as well. A single reading of noise can be twice
            // its neighbours; three in a row cannot, so this alone removes most of the false dits.
            _recentLevels[_levelIndex] = level;
            _levelIndex = (_levelIndex + 1) % _recentLevels.Length;
            double smoothed = (_recentLevels[0] + _recentLevels[1] + _recentLevels[2]) / 3.0;

            if (!_levelsSeeded) { _noiseFloor = smoothed; _peak = smoothed; _levelsSeeded = true; }

            // The floor drops quickly to anything quieter and creeps up slowly, so it settles on the
            // band noise; the peak does the opposite and settles on the sent tone. Between them they
            // give a threshold that follows a fading signal instead of losing it.
            // Both settle within a couple of seconds, which is roughly how fast a signal fades up and
            // down on HF - so the threshold rides the fade instead of losing the weak half of it.
            _noiseFloor += (smoothed < _noiseFloor) ? (smoothed - _noiseFloor) * 0.05 : (smoothed - _noiseFloor) * 0.005;
            _peak += (smoothed > _peak) ? (smoothed - _peak) * 0.05 : (smoothed - _peak) * 0.005;

            double span = _peak - _noiseFloor;

            // IS THERE A SIGNAL AT ALL? Asking whether this one note is loud compared with its own
            // quiet moments is not enough - band noise has loud and quiet moments too, and answering
            // that question wrongly is exactly what filled the window with lone Es on the air.
            //
            // The honest question is whether this note stands out from THE NOTES EITHER SIDE OF IT.
            // Noise is much the same at one frequency as at the next, so a hiss is no louder here
            // than it is a hundred Hz away; only a real signal is. Comparing across frequencies costs
            // nothing - the strength at every note has already been measured - and it cannot be
            // fooled by a burst of static, because static is loud everywhere at once.
            // Harder to gain than to keep, like the key threshold and for the same reason. A signal
            // that is only just strong enough would otherwise be declared present and absent over
            // and over, and every time it went absent the letter being spelled out was thrown away -
            // so a station next to another one lost letters it had actually decoded correctly.
            double standsOutBy = _binAverage[_bestBin] / NoiseBesideTheNote();
            SignalPresent = (SignalPresent
                                ? standsOutBy > SignalOverNoise * 0.5
                                : standsOutBy > SignalOverNoise)
                            && span > 1e-5;
            if (!SignalPresent)
            {
                FinishAnythingPending();
                _keyDown = false;
                _stateMs = 0;
                return;
            }

            // Two thresholds, not one: a signal hovering on a single threshold would chatter on and
            // off many times inside one dit. They sit well apart for the same reason.
            //
            // A tracker that learned the key-down and key-up loudness separately was tried here, on
            // the reasoning that it would follow a fade faster than this long-term range can. It was
            // measurably worse - 86 of 104 against 94 - and it destroyed fast sending completely,
            // because at 40 WPM a mark is 30 ms and there is not enough of it to learn from. Deep
            // fading is left as a known weakness rather than paid for with everything else.
            double onThreshold = _noiseFloor + span * 0.55;
            double offThreshold = _noiseFloor + span * 0.35;

            bool nowDown = _keyDown ? smoothed > offThreshold : smoothed > onThreshold;

            // HOW DEEPLY THE NOTE ACTUALLY SWITCHES OFF. Kept only to judge what we are hearing -
            // never to set the threshold above, which was tried and made fast sending far worse.
            //
            // A real key takes the tone away completely: while it is up, all that is left is the
            // noise floor, so the note is several times louder down than up. A steady carrier, or a
            // birdie, is always there; the threshold still finds edges in the noise riding on it,
            // but the loud parts are barely louder than the quiet ones. That ratio is the difference
            // between a station and a whistle, and no amount of studying the LENGTHS can see it.
            if (nowDown) _onLevel = _onLevel > 0 ? _onLevel * 0.9 + smoothed * 0.1 : smoothed;
            else _offLevel = _offLevel > 0 ? _offLevel * 0.9 + smoothed * 0.1 : smoothed;

            if (nowDown == _keyDown)
            {
                _stateMs += FrameMilliseconds;
                if (!_keyDown) CheckGaps();
                return;
            }

            // Step 4: the state changed, so the stretch that just ended has a length worth reading.
            double lasted = _stateMs;
            _keyDown = nowDown;
            _stateMs = FrameMilliseconds;

            // A space that has just ended needs nothing done to it: CheckGaps has been writing the
            // letter and the word gap out frame by frame as they became long enough.
            if (!nowDown) EndOfMark(lasted);
        }

        // How loud it is JUST BESIDE the note we are listening to - a hundred or two Hz either side,
        // which is near enough to be the same noise and far enough not to be the note itself.
        //
        // THIS MUST BE MEASURED CLOSE BY, and getting that wrong is what put lone Es on the screen a
        // second time. The first attempt compared the note against the middle of the whole search,
        // 300 to 1200 Hz. But a receiver has a CW filter: inside it there is a hump of noise, and
        // outside it there is next to nothing. So the whole search is not one band of noise at all,
        // and any note inside the filter towered over a "middle" that was really the silence outside
        // it. Every hiss in the passband passed the test.
        //
        // Beside the note, inside the same filter, noise is noise: a hiss is as loud there as it is
        // here, and only a real signal is louder here than beside itself.
        double NoiseBesideTheNote()
        {
            const int Guard = 3;      // 75 Hz - close enough that the note itself still spills in
            const int Reach = 8;      // 200 Hz - as far as we go before it may be another filter

            double left = SideMedian(_bestBin, -1, Guard, Reach);
            double right = SideMedian(_bestBin, +1, Guard, Reach);

            // The louder side. A note sitting at the edge of the receiver's filter has the filter's
            // skirt on one side of it, which is quiet for reasons that have nothing to do with
            // whether anybody is sending.
            double beside = Math.Max(left, right);
            return beside < 1e-12 ? 1e-12 : beside;
        }

        double SideMedian(int centre, int direction, int guard, int reach)
        {
            int n = 0;
            for (int step = guard; step <= reach; step++)
            {
                int b = centre + direction * step;
                if (b < 0 || b >= _binAverage.Length) continue;
                _binSorted[n++] = _binAverage[b];
            }
            if (n == 0) return 0;
            Array.Sort(_binSorted, 0, n);
            return _binSorted[n / 2];
        }

        // A one-frequency Fourier transform over the window, tapered at both ends.
        double Goertzel(double coefficient)
        {
            double s1 = 0, s2 = 0;
            for (int i = 0; i < _windowSamples; i++)
            {
                double s0 = _window[i] * _taper[i] + coefficient * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
            return power < 0 ? 0 : power / _windowSamples;
        }

        void EndOfMark(double lengthMs)
        {
            // Too short to be anything a person sent - a click, a crash of static, or the edge of
            // somebody else's signal. Thrown away WITHOUT being remembered: letting it into the
            // speed estimate is what ruined the first version on the air.
            if (lengthMs < ShortestRealMarkMs) return;
            if (lengthMs < _ditMs * ShortestFractionOfDit) return;

            _marks[_markNext] = lengthMs;
            _markNext = (_markNext + 1) % MarkMemory;
            if (_markCount < MarkMemory) _markCount++;

            // DITS AND DAHS ARE TOLD APART BY LOOKING AT THE LAST TWO DOZEN OF THEM, not by
            // measuring each one against a running guess. A single guess that starts too slow can
            // never recover: every dah falls just under its own threshold, gets counted as a dit,
            // and drags the guess back up again. Two dozen marks nearly always contain both kinds,
            // and then the two lengths stand a clear 3:1 apart and the line between them is obvious.
            //
            // NOT the shortest and longest of the two dozen, though: one mangled mark at either end
            // would set the whole scale. A fifth of the way in from each end gives the same answer
            // on clean sending and ignores the freaks on bad sending.
            Array.Copy(_marks, _sorted, _markCount);
            Array.Sort(_sorted, 0, _markCount);
            double shortest = _sorted[_markCount / 5];
            double longest = _sorted[_markCount - 1 - _markCount / 5];

            // The dividing line is the GEOMETRIC middle, not the plain average. These are lengths in
            // a ratio to each other, so the fair midpoint between 40 and 120 is 69, not 80 - and
            // that difference is exactly what decides a borderline character.
            bool canTellThemApart = longest >= shortest * 2.2;
            double boundary = canTellThemApart ? Math.Sqrt(shortest * longest) : _ditMs * 1.732;
            _boundaryMs = boundary;

            bool isDah = lengthMs > boundary;

            // THE LENGTHS ARE KEPT, NOT THE DOTS AND DASHES, and the letter is only spelled out when
            // the gap after it says it is finished. By then a mark or two more has been heard, so the
            // first letter off a station whose speed is not known yet gets read with a dividing line
            // that has already learned something - which is the difference between C and F when
            // somebody starts sending at 40 WPM.
            _letterMarks.Add(lengthMs);

            // The speed comes from the short cluster - the dits - averaged. Until both kinds have
            // been seen, fall back on what this one mark implies about the speed.
            double impliedDit;
            if (canTellThemApart)
            {
                double sum = 0; int n = 0;
                for (int i = 0; i < _markCount; i++)
                    if (_marks[i] <= boundary) { sum += _marks[i]; n++; }
                impliedDit = n > 0 ? sum / n : shortest;
            }
            else impliedDit = isDah ? lengthMs / 3.0 : lengthMs;

            _ditMs = _ditMs * 0.5 + impliedDit * 0.5;
            if (_ditMs < FastestDitMs) _ditMs = FastestDitMs;
            if (_ditMs > SlowestDitMs) _ditMs = SlowestDitMs;

            JudgeWhetherThisIsMorse(boundary);

            _letterPending = true;
            _wordPending = true;
        }

        // DOES WHAT WE ARE HEARING ACTUALLY LOOK LIKE MORSE? Nothing else asked this, and it is the
        // question that finally silences an empty frequency.
        //
        // Being loud enough is not the same as being a station. A steady carrier, a birdie, a noisy
        // receiver, a burst of atmospherics - any of them can stand above the noise beside them, and
        // the moment they do, the on-and-off threshold starts finding edges in them and letters come
        // out. But their edges fall where they like.
        //
        // Morse cannot. It is built out of exactly two lengths, one three times the other, and every
        // mark is close to one of them. So we measure that directly: the two lengths must be there,
        // one must be about three times the other, and most marks must sit close to whichever they
        // belong to. Noise cannot keep that up, and a station cannot help it.
        void JudgeWhetherThisIsMorse(double boundary)
        {
            if (_markCount < MarksNeededBeforeBelieving) { Score(false); return; }

            double shortSum = 0, longSum = 0;
            int shortCount = 0, longCount = 0;
            for (int i = 0; i < _markCount; i++)
            {
                if (_marks[i] <= boundary) { shortSum += _marks[i]; shortCount++; }
                else { longSum += _marks[i]; longCount++; }
            }

            // Only one length heard so far - a run of dits, or a run of dahs. Nothing to judge yet.
            if (shortCount == 0 || longCount == 0) { Score(false); return; }

            double shortMean = shortSum / shortCount;
            double longMean = longSum / longCount;
            double ratio = longMean / shortMean;

            // A dah is three dits. Real operators are loose, so anything from twice to four and a
            // half times is allowed - but noise lands outside this far more often than inside it.
            if (ratio < 2.0 || ratio > 4.5) { Score(false); return; }

            int nearItsOwnLength = 0;
            for (int i = 0; i < _markCount; i++)
            {
                double belongsTo = _marks[i] <= boundary ? shortMean : longMean;
                if (Math.Abs(_marks[i] - belongsTo) <= belongsTo * 0.4) nearItsOwnLength++;
            }

            bool lengthsLookRight = nearItsOwnLength >= _markCount * 0.7;

            // ...and the key must really be lifting the tone away, not just rippling it.
            bool keyingIsReal = _offLevel > 0 && _onLevel > _offLevel * KeyingDepth;

            Score(lengthsLookRight && keyingIsReal);
        }

        // ONCE IS NOT ENOUGH. A carrier with noise on top of it, chopped into marks by the
        // threshold, will now and then throw up a run of lengths that happens to look like Morse -
        // and a single instant of agreement was enough to open the gate and let a burst of rubbish
        // out. So agreement has to be sustained: each mark that fits scores one, each mark that does
        // not costs two, and the gate opens only well up the scale. Real sending climbs it in a
        // couple of characters and stays there; noise and carriers rattle around the bottom.
        void Score(bool fitsMorse)
        {
            _morseScore += fitsMorse ? 1 : -2;
            if (_morseScore < 0) _morseScore = 0;
            if (_morseScore > MorseScoreCeiling) _morseScore = MorseScoreCeiling;

            bool wasOpen = _looksLikeMorse;

            // Harder to open than to keep open, so a fade or one bad character does not shut the
            // decoder up in the middle of a callsign.
            _looksLikeMorse = wasOpen ? _morseScore >= MorseScoreToKeep : _morseScore >= MorseScoreToOpen;

            if (_looksLikeMorse && !wasOpen) ReleaseHeldText();
        }

        // Called on every frame of silence: as soon as the gap is long enough to BE a letter gap the
        // letter is written out. Waiting for the next mark would put the whole decode one letter
        // behind the operator, which is exactly when it stops being useful.
        void CheckGaps()
        {
            if (_letterPending && _stateMs >= _ditMs * 2.0)
            {
                EmitLetter();
                _letterPending = false;
            }

            // Five dits, not seven. Seven is what a machine sends between words; a person's word gap
            // wanders either side of it, and the only thing that must not happen is mistaking one for
            // the three-dit gap between letters. Halfway between three and seven is the safe place.
            if (_wordPending && !_letterPending && _stateMs >= _ditMs * 5.0)
            {
                Output(" ");
                _wordPending = false;
            }
        }

        // The signal has gone - the station stopped, or it faded into the noise. Whatever marks were
        // half way through a letter are THROWN AWAY, not written out.
        //
        // Writing them out was the last place a lone E could still escape onto the screen: a single
        // blip of noise makes one short mark, the signal test then decides there is no station after
        // all, and the half letter that blip started got printed as an E. A letter cut short by the
        // signal disappearing was never trustworthy anyway. When a station simply stops sending, the
        // ordinary gap has already written its last letter out long before this runs.
        void FinishAnythingPending()
        {
            _letterMarks.Clear();
            _symbols.Clear();
            _letterPending = false;
            _wordPending = false;
            _looksLikeMorse = false;
            _morseScore = 0;
            _onLevel = 0; _offLevel = 0;
            _held.Clear();
        }

        void EmitLetter()
        {
            if (_letterMarks.Count == 0) return;


            _symbols.Clear();
            for (int i = 0; i < _letterMarks.Count; i++)
                _symbols.Append(_letterMarks[i] > _boundaryMs ? '-' : '.');
            _letterMarks.Clear();

            string pattern = _symbols.ToString();

            // A run of dits and dahs that spells nothing is DROPPED, not shown. It was printed as
            // <..-.> at first, on the reasoning that an operator could often read it himself. On the
            // air that was wrong: what it really means is that the signal broke up, so the brackets
            // arrive exactly when the text is already hard to follow and make it harder still. The
            // operator asked for silence instead, and silence is also the honest answer - we did not
            // hear a letter.
            string letter;
            if (FromMorse.TryGetValue(pattern, out letter)) Output(letter);
        }

        // EVERY LETTER GOES THROUGH HERE, and until the sending has been recognised as Morse it is
        // held back rather than shown - or thrown away.
        //
        // Holding rather than throwing away matters: the proof that this is Morse takes eight marks,
        // two or three characters, and those characters are the beginning of a callsign. Dropping
        // them would mean the decoder was always right and always started with "Q DE" instead of
        // "CQ DE". Held, they arrive a moment late and whole. If the signal turns out to be noise
        // after all, they are never shown at all.
        void Output(string text)
        {
            if (_looksLikeMorse) { Raise(text); return; }

            _held.Append(text);
            if (_held.Length > MostHeldBack) _held.Remove(0, _held.Length - MostHeldBack);
        }

        void ReleaseHeldText()
        {
            if (_held.Length == 0) return;
            string text = _held.ToString();
            _held.Clear();
            Raise(text);
        }

        void Raise(string text)
        {
            var handler = Text;
            if (handler == null) return;
            try { handler(text); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Pattern to character. The letters, figures and the punctuation that actually turns up in a
        // QSO, plus the prosigns sent as one run of dits and dahs.
        static readonly Dictionary<string, string> FromMorse = new Dictionary<string, string>
        {
            {".-","A"},   {"-...","B"}, {"-.-.","C"}, {"-..","D"},  {".","E"},    {"..-.","F"},
            {"--.","G"},  {"....","H"}, {"..","I"},   {".---","J"}, {"-.-","K"},  {".-..","L"},
            {"--","M"},   {"-.","N"},   {"---","O"},  {".--.","P"}, {"--.-","Q"}, {".-.","R"},
            {"...","S"},  {"-","T"},    {"..-","U"},  {"...-","V"}, {".--","W"},  {"-..-","X"},
            {"-.--","Y"}, {"--..","Z"},
            {"-----","0"},{".----","1"},{"..---","2"},{"...--","3"},{"....-","4"},
            {".....","5"},{"-....","6"},{"--...","7"},{"---..","8"},{"----.","9"},
            {".-.-.-","."},  {"--..--",","}, {"..--..","?"},  {"-..-.","/"},
            {".--.-.","@"},  {"-...-","="},  {"-....-","-"},
            {"-.--.-",")"},  {".----.","'"}, {"---...",":"},  {"-.-.-.",";"},
            {"..--.-","_"},  {"...-..-","$"},{"-.-.--","!"},  {".-..-.","\""},
            // Prosigns, written the way an operator writes them down. KN and the opening bracket are
            // the same run of dits and dahs; on the air it is always KN, so that is what it says.
            {".-.-","<AA>"}, {".-...","<AS>"}, {".-.-.","<AR>"}, {"...-.-","<SK>"},
            {"-.--.","<KN>"}, {"........","<HH>"}, {"...-.","<SN>"},
        };
    }
}
