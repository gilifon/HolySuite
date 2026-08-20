using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HolyLogger
{
    // 588,000 CALLSIGNS WITHOUT 588,000 OBJECTS.
    //
    // This was a List<string>, one string per callsign, and building it froze the whole program for
    // seven and a half seconds - measured, by the window timing its own thread: "could not answer for
    // 7,819 ms, collections since the last tick: gen0=58 gen1=38 gen2=2". Ninety-six garbage
    // collections in one freeze. Every collection stops EVERY thread, whatever its priority, so moving
    // the work to a background thread changed nothing at all; that was tried first.
    //
    // The cause was never the reading - the file is 4.3 MB and the disk hands it over in well under a
    // second. It was the shape: 588,119 little objects, about 23 MB of them in a 32-bit program that is
    // already holding a large log, is more than the collector can take in its stride.
    //
    // So the callsigns are kept as ONE block of bytes exactly as they are in the file, with an array of
    // where each one starts. Three objects instead of 588,119, about 7 MB instead of 23, and not one
    // collection while it loads. A callsign is only turned into a string when something actually asks
    // for one - which is a handful at a time, for a dropdown.
    //
    // ASCII throughout: a callsign is letters, digits and strokes. A byte per character, compared as
    // bytes, which is the same ordering as the ordinal string comparison this replaces.
    internal sealed class CallsignIndex
    {
        private byte[] _text = new byte[0];      // every callsign, one after another
        private int[] _start = new int[0];       // where each one begins in _text
        private int[] _length = new int[0];      // how long each one is
        private int _count;

        // Callsigns added while the program is running (a new station worked). They are few - tens in a
        // long session - so they live in an ordinary list, searched after the big block. Keeping them
        // separate is what stops one new callsign from rewriting a 4 MB array.
        private readonly List<string> _added = new List<string>();

        public int Count { get { return _count + _added.Count; } }

        // How many are in the sorted block. Everything from here up to Count was added while the
        // program was running and is NOT in that order - a caller walking the index must stop breaking
        // out of its loop once it passes this point, or it will miss them.
        public int PackedCount { get { return _count; } }

        // Reads the file as bytes. Nothing here allocates per callsign.
        //
        // The file arrives sorted and without duplicates (checked: 588,119 callsigns, none out of
        // order, none repeated) - and that is VERIFIED here rather than trusted, because a future file
        // could break the promise and a binary search over unsorted data answers nonsense. If it is
        // ever broken, the caller is told and can fall back.
        public bool LoadFromFile(string path, out int version, out bool wasSorted)
        {
            version = 0;
            wasSorted = true;

            byte[] raw = File.ReadAllBytes(path);      // 4.3 MB, one object

            // Two passes: count the lines, then fill the arrays. Counting first means the arrays are
            // allocated once, at exactly the right size.
            // COUNTED EXACTLY, and counting only newlines was not exact. A file written on an old Mac
            // ends its lines with a lone carriage return and has no newlines at all - the arrays would
            // have been sized for two entries and the fill would have run off the end of them. Every
            // line ending is counted: a newline, or a carriage return that is not part of one.
            int lines = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == (byte)'\n') lines++;
                else if (raw[i] == (byte)'\r' && (i + 1 >= raw.Length || raw[i + 1] != (byte)'\n')) lines++;
            }
            lines += 2;   // a last line without any ending, and the version line

            var start = new int[lines];
            var length = new int[lines];
            int n = 0;
            bool firstDataLine = true;
            int prevStart = -1, prevLen = 0;

            int p = 0;
            while (p < raw.Length)
            {
                int lineStart = p;
                while (p < raw.Length && raw[p] != (byte)'\n' && raw[p] != (byte)'\r') p++;
                int lineEnd = p;
                while (p < raw.Length && (raw[p] == (byte)'\n' || raw[p] == (byte)'\r')) p++;

                // Trim, and stop at the first separator - the token is the callsign.
                while (lineStart < lineEnd && raw[lineStart] == (byte)' ') lineStart++;
                int tokenEnd = lineStart;
                while (tokenEnd < lineEnd && raw[tokenEnd] != (byte)' '
                       && raw[tokenEnd] != (byte)'\t' && raw[tokenEnd] != (byte)',') tokenEnd++;

                int len = tokenEnd - lineStart;
                if (len <= 0) continue;
                if (raw[lineStart] == (byte)'#' || raw[lineStart] == (byte)';') continue;

                // Upper case in place. The file is already upper case, so this almost never writes.
                for (int i = lineStart; i < tokenEnd; i++)
                    if (raw[i] >= (byte)'a' && raw[i] <= (byte)'z') raw[i] -= 32;

                if (firstDataLine)
                {
                    firstDataLine = false;
                    int parsed;
                    if (TryParseNumber(raw, lineStart, len, out parsed)) { version = parsed; continue; }
                }

                if (len > 15) continue;

                if (prevStart >= 0)
                {
                    int order = Compare(raw, prevStart, prevLen, raw, lineStart, len);
                    if (order > 0) wasSorted = false;
                    else if (order == 0) continue;          // a repeat
                }

                start[n] = lineStart;
                length[n] = len;
                n++;
                prevStart = lineStart; prevLen = len;
            }

            _text = raw;
            _start = start;
            _length = length;
            _count = n;
            _added.Clear();
            return n > 0;
        }

        // The same block, built from callsigns already in hand - the old path that reads them from a
        // SQLite file. They are sorted and packed here, so that path ends up with exactly the same
        // structure as the file path and nothing downstream needs to know which was used.
        public bool LoadFromStrings(IEnumerable<string> callsigns)
        {
            var list = new List<string>();
            foreach (string c in callsigns)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                string call = c.Trim().ToUpperInvariant();
                if (call.Length == 0 || call.Length > 15) continue;
                list.Add(call);
            }
            list.Sort(StringComparer.Ordinal);

            int bytes = 0;
            for (int i = 0; i < list.Count; i++) bytes += list[i].Length;

            var text = new byte[bytes];
            var start = new int[list.Count];
            var length = new int[list.Count];

            int at = 0, n = 0;
            string previous = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (previous != null && string.Equals(previous, list[i], StringComparison.Ordinal)) continue;
                previous = list[i];

                start[n] = at;
                length[n] = list[i].Length;
                for (int k = 0; k < list[i].Length; k++) text[at++] = (byte)list[i][k];
                n++;
            }

            _text = text; _start = start; _length = length; _count = n;
            _added.Clear();
            return n > 0;
        }

        // Where this callsign is, or the bitwise complement of where it would go - the same contract as
        // List.BinarySearch, so the calling code reads exactly as it did.
        public int BinarySearch(string call)
        {
            if (string.IsNullOrEmpty(call)) return ~0;
            byte[] needle = Encoding.ASCII.GetBytes(call);

            int lo = 0, hi = _count - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int cmp = Compare(_text, _start[mid], _length[mid], needle, 0, needle.Length);
                if (cmp == 0) return mid;
                if (cmp < 0) lo = mid + 1; else hi = mid - 1;
            }

            // Not in the block: it may be one added since the program started.
            for (int i = 0; i < _added.Count; i++)
                if (string.Equals(_added[i], call, StringComparison.Ordinal)) return _count + i;

            return ~lo;
        }

        public string this[int i]
        {
            get
            {
                if (i < 0) return null;
                if (i < _count) return Encoding.ASCII.GetString(_text, _start[i], _length[i]);
                int j = i - _count;
                return j < _added.Count ? _added[j] : null;
            }
        }

        // A callsign worked that the big list does not have. Kept beside the block rather than in it.
        public void Add(string call)
        {
            if (string.IsNullOrWhiteSpace(call)) return;
            if (BinarySearch(call) >= 0) return;
            _added.Add(call);
        }

        // Every callsign that begins with this prefix, in order, at most 'limit' of them. The dropdown
        // asks for a handful, and only those become strings.
        public List<string> StartingWith(string prefix, int limit)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(prefix)) return found;

            byte[] p = Encoding.ASCII.GetBytes(prefix);
            int at = BinarySearch(prefix);
            int i = at >= 0 ? at : ~at;

            for (; i < _count && found.Count < limit; i++)
            {
                if (!StartsWith(_text, _start[i], _length[i], p)) break;
                found.Add(Encoding.ASCII.GetString(_text, _start[i], _length[i]));
            }

            for (int k = 0; k < _added.Count && found.Count < limit; k++)
                if (_added[k].StartsWith(prefix, StringComparison.Ordinal)) found.Add(_added[k]);

            return found;
        }

        private static bool StartsWith(byte[] hay, int at, int len, byte[] prefix)
        {
            if (len < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (hay[at + i] != prefix[i]) return false;
            return true;
        }

        // Byte-by-byte, which for ASCII is the same order as StringComparer.Ordinal - the comparison
        // the List this replaces was searched with.
        private static int Compare(byte[] a, int aAt, int aLen, byte[] b, int bAt, int bLen)
        {
            int n = Math.Min(aLen, bLen);
            for (int i = 0; i < n; i++)
            {
                int d = a[aAt + i] - b[bAt + i];
                if (d != 0) return d;
            }
            return aLen - bLen;
        }

        private static bool TryParseNumber(byte[] raw, int at, int len, out int value)
        {
            value = 0;
            if (len <= 0 || len > 9) return false;
            for (int i = 0; i < len; i++)
            {
                byte c = raw[at + i];
                if (c < (byte)'0' || c > (byte)'9') { value = 0; return false; }
                value = value * 10 + (c - (byte)'0');
            }
            return true;
        }
    }
}
