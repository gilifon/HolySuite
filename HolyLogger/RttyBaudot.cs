using System;
using System.Collections.Generic;

namespace HolyLogger
{
    /// <summary>
    /// What both RTTY senders have in common: FSK on a COM port line (RttyFskKeyer) and AFSK through
    /// the sound card (RttyAfskSender). The main window holds one of them and does not care which.
    /// </summary>
    public interface IRttySender : IDisposable
    {
        /// <summary>Hands text over to go after whatever is already waiting. False once closed.</summary>
        bool Send(string text);

        /// <summary>Stops now; BusyChanged(false) follows.</summary>
        void Stop();

        bool Busy { get; }

        /// <summary>
        /// True when sending starts (switch the radio to transmit now - a lead of mark is already
        /// going out), false once the tail is over (switch it back). On the sender's own thread.
        /// </summary>
        event Action<bool> BusyChanged;

        /// <summary>A character as it goes out whole, on the sender's own thread.</summary>
        event Action<char> CharacterSent;

        /// <summary>Where it sends, in a few words for the log: "COM10 RTS" or the sound card's name.</summary>
        string Describe { get; }
    }

    /// <summary>
    /// TEXT TO BAUDOT AND BACK, for sending (5-bit ITA2, the tables the decoder reads with).
    ///
    /// Every transmission starts with two LTRS so the other station's decoder is in step and in
    /// letters; figures are announced with FIGS, and announced again after a space, because nearly
    /// every receiver drops back to letters at a space (unshift on space). Characters Baudot cannot
    /// carry are left out.
    /// </summary>
    public static class RttyBaudot
    {
        public const int LTRS = 0x1F, FIGS = 0x1B, SPACE = 0x04, CR = 0x02, LF = 0x08;

        /// <summary>The shift as the far end sees it: letters, figures, or not known (just after a space in figures).</summary>
        public const int Letters = 0, Figures = 1, Unknown = 2;

        /// <summary>Adds one character's codes, announcing a change of shift first when it needs one.</summary>
        public static void Encode(char raw, ref int shift, Queue<int> codes)
        {
            char c = char.ToUpperInvariant(raw);
            if (c == '\r') { codes.Enqueue(CR); return; }
            if (c == '\n') { codes.Enqueue(LF); return; }
            if (c == ' ')
            {
                codes.Enqueue(SPACE);
                if (shift == Figures) shift = Unknown;      // he may have dropped back to letters on the space
                return;
            }
            if (c == '\0' || c == '#' || c == '\a') return;
            int code = Array.IndexOf(RttyDecoder.Letters, c);
            int want = Letters;
            if (code < 0) { code = Array.IndexOf(RttyDecoder.Figures, c); want = Figures; }
            if (code < 0) return;                           // nothing Baudot can carry
            if (want != shift)
            {
                codes.Enqueue(want == Figures ? FIGS : LTRS);
                shift = want;
            }
            codes.Enqueue(code);
        }

        /// <summary>What the far end prints for a code it has just been sent; '\0' for a shift code.</summary>
        public static char Print(int code, ref int shift)
        {
            if (code == LTRS) { shift = Letters; return '\0'; }
            if (code == FIGS) { shift = Figures; return '\0'; }
            char c = shift == Figures ? RttyDecoder.Figures[code] : RttyDecoder.Letters[code];
            if (c == ' ' && shift == Figures) shift = Letters;
            return c;
        }
    }
}
