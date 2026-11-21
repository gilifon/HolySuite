"""
WHERE PLAIN LIVE AND PLAIN REPLAYED DISAGREE (2026-10-27). He saw a clear CQ read badly live that Plain read
well from a recording. The window saved the exact sound Plain was given (HolyLogger_cw_input_8000.raw, every run
appended) and every letter Plain spelled live with the sample it ended at (HolyLogger_cw_letters.tsv; the count
restarts with each window). tools/PlainRaw.exe played the whole file through one Plain (build/live_replay.tsv).

Each live run is placed in the file by voting: every live letter against every replayed letter of the same kind
gives an offset, the commonest (to 0.05 s) wins. Then, per run, the live text and the replayed text over the same
stretch are printed side by side where they differ.

  LiveVsReplay.py <letters.tsv> <live_replay.tsv> [rate=8000]
"""
import bisect
import collections
import difflib
import sys


def main():
    rate = int(sys.argv[3]) if len(sys.argv) > 3 else 8000
    live = []
    for line in open(sys.argv[1], encoding='utf-8'):
        clock, letter, s = line.rstrip('\n').split('\t')
        live.append((clock, letter, int(s)))
    runs, cur, prev = [], [], -1
    for r in live:
        if r[2] < prev:
            runs.append(cur); cur = []
        cur.append(r); prev = r[2]
    runs.append(cur)

    rep = []          # (sample, letter); word gaps get the sample of the letter before
    last = 0
    for line in open(sys.argv[2], encoding='utf-8'):
        letter, s = line.rstrip('\n').split('\t')
        s = int(s)
        if s < 0:
            rep.append((last, ' '))
        else:
            rep.append((s, letter)); last = s
    rep_s = [s for s, _ in rep]
    by_letter = collections.defaultdict(list)
    for s, l in rep:
        if l != ' ':
            by_letter[l].append(s)

    floor = 0
    tol = int(0.15 * rate)
    total_live = total_match = 0
    for run in runs:
        if len(run) < 40:       # too few letters to place it surely
            continue
        votes = collections.Counter()
        for _, l, s in run:
            for r in by_letter.get(l, ()):
                o = r - s
                votes[o // (rate // 20)] += 1
        if not votes:
            continue
        o = votes.most_common(1)[0][0] * (rate // 20)
        # refine: the median of the exact offsets near the winner
        near = sorted(r - s for _, l, s in run for r in by_letter.get(l, ()) if abs(r - s - o) <= rate // 10)
        o = near[len(near) // 2] if near else o
        a = run[0][2] + o - rate
        b = run[-1][2] + o + rate
        i0, i1 = bisect.bisect_left(rep_s, a), bisect.bisect_right(rep_s, b)
        rtext = ''.join(l for _, l in rep[i0:i1]).strip()
        ltext = ''.join(l for _, l, _ in run)
        matched = 0
        for _, l, s in run:
            j = bisect.bisect_left(rep_s, s + o - tol)
            while j < len(rep) and rep_s[j] <= s + o + tol:
                if rep[j][1] == l:
                    matched += 1
                    break
                j += 1
        total_live += len(run); total_match += matched
        floor = run[-1][2] + o
        print('=== run %s-%s  %d live letters, %d at the same place in the replay, file %.1f-%.1f min' % (
            run[0][0], run[-1][0], len(run), matched, a / rate / 60, b / rate / 60))
        rnospace = rtext.replace(' ', '')
        if matched < len(run) or len(rnospace) != len(ltext):
            sm = difflib.SequenceMatcher(None, ltext, rnospace, autojunk=False)
            for tag, i1_, i2, j1, j2 in sm.get_opcodes():
                if tag != 'equal':
                    clock = run[min(i1_, len(run) - 1)][0]
                    print('   %s  live %-20r replay %-20r   ...%s...' % (
                        clock, ltext[i1_:i2], rnospace[j1:j2], ltext[max(0, i1_ - 12):i2 + 12]))
    print('ALL: %d live letters, %d at the same place in the replay' % (total_live, total_match))


if __name__ == '__main__':
    main()
