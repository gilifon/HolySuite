using System;
using System.Collections.Generic;
using System.Text;

namespace HolyLogger
{
    // THE WHOLE NEURAL READER: audio in at one end, letters out at the other.
    //
    // Three pieces, each in its own file. CwNeuralFrontEnd turns the sound into one number every
    // 7.69th of a dit - how loud the note is just then. CwNeuralNet runs those numbers through the
    // network. This file reads its answers.
    //
    // WHAT THE NETWORK ANSWERS. Seven numbers every step, and they are not letters - they are
    // something better. Two of them say "a character has ended here" and "a word has ended here".
    // The other five say WHICH ELEMENT of the character is being sent right now: the first, the
    // second, up to the fifth. So the network is not asked to spell; it is asked to say where it is
    // in the character, which is the part that needs an ear.
    //
    // THAT LEAVES ONLY COUNTING FOR US. Element three was lit for nine steps: nine is about one dit
    // (7.69 by construction), so element three is a dit. Lit for twenty-three, it is a dah. Then the
    // character separator lights and the run of dits and dahs is looked up. Nothing here has to
    // guess at the speed - the front end already stretched time so a dit is always 7.69 steps,
    // whoever is sending and however fast.
    //
    // AND NOTHING GUESSES AT WORDS. There is no dictionary here and there never will be one: what
    // was not heard is not printed. The network's advantage over the plain decoder is that it hears
    // through noise and fading better, not that it knows what a ham is likely to say.
    public class CwNeuralDecoder
    {
        // By construction of the front end - see NumbersPerDit there.
        const double StepsPerDit = CwNeuralFrontEnd.NumbersPerDit;

        // WHERE A DIT ENDS AND A DAH BEGINS, and this was got wrong first time by reasoning instead
        // of measuring. A dit is 7.69 steps of front end and a dah three times that, so the dividing
        // line looked like it should be halfway between them - about 13.
        //
        // Measured, it is nothing of the sort. The network does not drop an element the instant the
        // mark ends; it holds its answer about six steps longer. So a dit reads about 14 steps and a
        // dah about 30 - still far apart, but no longer three to one, and a line drawn at 13 puts
        // every dit on the dah side of it. That is exactly what happened: the first run of this
        // decoder read "CQ CQ DE 4Z5SL K" as "ON 19OP O", all dahs.
        const double DefaultBoundary = 21.0;

        // Better than any fixed number, though: watch the two lengths actually arriving and put the
        // line between them, the same way the plain decoder does. It costs nothing and it holds up
        // when the speed the front end was set to is not quite the speed being sent.
        const int ElementMemory = 16;

        // Nothing is believed until the network has been fed this long. Its first answers are
        // rubbish - it has no memory yet to reason from - and on PARIS the whole first character
        // came out scrambled while everything after it was perfect.
        const int WarmUpSteps = 10;

        // Below this an answer is not being given. The answers are raw scores, not chances: a stream
        // that is being asserted runs to 2 or 3 while the rest sit below zero, so the line between
        // them is broad and where exactly it falls hardly matters.
        const double Asserted = 0.5;

        // A FLICKER IS NOT AN ELEMENT. Coming off a mark the network often touches the NEXT element's
        // answer for a few steps before settling - so a bare E came out as I, and T as N, an extra
        // dit appended to everything that ended on one element. A real element is never much under a
        // dit's worth of steps, and a flicker is never much over a third of one, so the line between
        // them is broad. Taken as a fraction of the dit/dah line so it holds at any speed.
        const double FlickerFractionOfBoundary = 0.35;
        const int ShortestElementEver = 5;

        // How long a character separator must hold before the character is written out. Three steps
        // is deliberately short: the character separator is what carries the reading forward, and
        // making it wait only puts letters on screen late. Measured - 6 was the same, 9 lost letters
        // and 12 lost most of them.
        const int SeparatorSteps = 3;

        // A WORD SEPARATOR MUST HOLD FAR LONGER - about two and a half dits' worth.
        //
        // This one number was the whole of the fragmenting. The network asserts a word separator
        // whenever a gap runs long, and on the air gaps between letters wander: at six steps the
        // real band came out as "AU P L AI S SR E T BONN E S OIR" - every letter correct and a space
        // thrown in between most of them. Measured against a recording of a real station: six and
        // twelve steps both scattered, twenty gave "AU PLAISSR ET BONNE SOIR", and thirty began
        // swallowing the real spaces. Twenty it is.
        const int WordSeparatorSteps = 20;

        readonly CwNeuralFrontEnd _front;
        readonly CwNeuralNet _net;

        readonly int[] _elementSteps = new int[5];
        readonly double[] _recentElements = new double[ElementMemory];
        readonly double[] _sorted = new double[ElementMemory];
        int _elementCount, _elementNext;
        int _lastElement = -1;
        int _separatorRun;
        int _wordRun;
        int _stepsSeen;
        bool _characterOpen;
        bool _wordPending;

        public bool Ready { get { return _net.Loaded; } }

        /// <summary>Decoded characters as they are finished. Raised on the thread that feeds Process.</summary>
        public event Action<string> Text;

        public CwNeuralDecoder(int sampleRate, CwNeuralNet net)
        {
            _net = net;
            _front = new CwNeuralFrontEnd(sampleRate);
            _front.Envelope += OnEnvelope;
        }

        /// <summary>
        /// Points the reader at a note and a speed. Both come from the plain decoder, which is
        /// listening to the same audio and has already worked them out - the network needs to be
        /// told, because the spacing of what it is fed depends on the speed.
        /// </summary>
        public void Configure(double toneHz, double wpm)
        {
            _front.Configure(toneHz, wpm);
        }

        /// <summary>Empties everything: a new station, or a gap long enough to start again.</summary>
        public void Reset()
        {
            _front.Reset();
            _net.Forget();
            Array.Clear(_elementSteps, 0, _elementSteps.Length);
            Array.Clear(_recentElements, 0, _recentElements.Length);
            _elementCount = 0;
            _elementNext = 0;
            _lastElement = -1;
            _separatorRun = 0;
            _wordRun = 0;
            _stepsSeen = 0;
            _characterOpen = false;
            _wordPending = false;
        }

        /// <summary>Feeds audio in.</summary>
        public void Process(short[] samples, int count)
        {
            if (!_net.Loaded) return;
            _front.Process(samples, count);
        }

        void OnEnvelope(float loudness)
        {
            float[] answers;
            try { answers = _net.Step(loudness); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return; }

            // Fed, but not yet believed - see WarmUpSteps.
            if (_stepsSeen++ < WarmUpSteps) return;

            double charSeparator = answers[0];
            double wordSeparator = answers[1];

            // Which element is being sent, if any: the strongest of the five, provided it is being
            // asserted at all.
            int element = -1;
            double best = Asserted;
            for (int e = 0; e < 5; e++)
            {
                if (answers[2 + e] > best) { best = answers[2 + e]; element = e; }
            }

            if (element >= 0)
            {
                _elementSteps[element]++;
                _lastElement = element;
                _characterOpen = true;
                _separatorRun = 0;
                _wordRun = 0;
                return;
            }

            // No element. Is this the end of a character?
            if (charSeparator > Asserted)
            {
                _separatorRun++;
                if (_separatorRun == SeparatorSteps && _characterOpen)
                {
                    EmitCharacter();
                    _wordPending = true;
                }
            }
            else _separatorRun = 0;

            // AND THE END OF A WORD - which has to be asked far more strictly than the end of a
            // character. The network asserts this one whenever a gap is longer than usual, and on a
            // real signal that is often: a hand-sent gap between letters wanders, and every wander
            // put a space in. Whole words came out as scattered letters - "AU P L AI S SR E T B ON N
            // E S OI R" for "AU PLAISIR ET BONNE SOIR".
            //
            // So it must both be the STRONGEST thing the network is saying - stronger than the
            // character separator sitting beside it - and hold for twice as long.
            if (wordSeparator > Asserted && wordSeparator > charSeparator)
            {
                _wordRun++;
                if (_wordRun == WordSeparatorSteps && _wordPending && !_characterOpen)
                {
                    Raise(" ");
                    _wordPending = false;
                }
            }
            else _wordRun = 0;
        }

        void EmitCharacter()
        {
            var pattern = new StringBuilder(5);

            // The elements in the order the network numbered them. A gap in the middle - element one
            // and element three lit but not element two - means a mark was lost, and the character
            // cannot be trusted, so nothing is written.
            double boundary = Boundary();
            double flicker = Math.Max(ShortestElementEver, boundary * FlickerFractionOfBoundary);

            bool broken = false;
            for (int e = 0; e < 5; e++)
            {
                int steps = _elementSteps[e];

                if (steps == 0)
                {
                    // Everything after the first empty one must be empty too. A hole in the middle
                    // means an element was lost, and a character with a hole in it is not a
                    // character - so nothing is written rather than the wrong letter.
                    for (int rest = e + 1; rest < 5; rest++)
                        if (_elementSteps[rest] > 0) broken = true;
                    break;
                }

                // Too short to be real: the network touching the next answer on its way past.
                // Dropped, not treated as a fault - it is the commonest thing it does.
                if (steps < flicker) break;

                Remember(steps);
                pattern.Append(steps > boundary ? '-' : '.');
            }

            Array.Clear(_elementSteps, 0, _elementSteps.Length);
            _lastElement = -1;
            _characterOpen = false;

            if (broken || pattern.Length == 0) return;

            string letter;
            if (CwDecoder.FromMorseTable.TryGetValue(pattern.ToString(), out letter)) Raise(letter);
        }

        void Remember(int steps)
        {
            _recentElements[_elementNext] = steps;
            _elementNext = (_elementNext + 1) % ElementMemory;
            if (_elementCount < ElementMemory) _elementCount++;
        }

        // The line between a dit and a dah, taken from the lengths actually arriving. A fifth of the
        // way in from each end rather than the very shortest and longest, so one mangled element
        // cannot set the scale - the same guard the plain decoder uses.
        double Boundary()
        {
            if (_elementCount < 4) return DefaultBoundary;

            Array.Copy(_recentElements, _sorted, _elementCount);
            Array.Sort(_sorted, 0, _elementCount);

            double shortest = _sorted[_elementCount / 5];
            double longest = _sorted[_elementCount - 1 - _elementCount / 5];

            // Both kinds have to be in there before the two can be told apart at all. A run of dits
            // alone would otherwise have a line drawn through the middle of it.
            if (longest < shortest * 1.6) return DefaultBoundary;

            return Math.Sqrt(shortest * longest);
        }

        void Raise(string text)
        {
            var handler = Text;
            if (handler == null) return;
            try { handler(text); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
