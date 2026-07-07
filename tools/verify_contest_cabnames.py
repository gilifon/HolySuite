#!/usr/bin/env python3
"""
Maintainer tool for HolyLogger -- NOT run by the ham operator.

Verifies the `cabrillo_name` of every contest in HolyLogger/contests.json against
the authoritative WA7BNM Cabrillo-names list:
    https://www.contestcalendar.com/cabnames.php

It only REPORTS drift; it never edits contests.json. Run it occasionally (needs
internet), e.g. before contest season, then hand-edit contests.json for anything
it flags and ship an app update.

IMPORTANT LIMIT: only the Cabrillo NAME (the CONTEST: header) is machine-checkable
here -- that page does not publish the exchange-field structure. The per-contest
exchange fields (which boxes open in contest mode) still have to be verified by a
human against each sponsor's official rules.

Usage:
    python verify_contest_cabnames.py [path\\to\\contests.json]

Exit code: 0 = all names valid, 1 = mismatches found, 2 = could not fetch the site.
"""
import sys, os, re, json, urllib.request, difflib

URL = "https://www.contestcalendar.com/cabnames.php"


def fetch_site_names():
    """Return {cabrillo_name: [display contest names]} scraped from WA7BNM."""
    req = urllib.request.Request(URL, headers={"User-Agent": "Mozilla/5.0"})
    html = urllib.request.urlopen(req, timeout=30).read().decode("utf-8", "replace")
    # Each row ends with: <td>Contest Name &nbsp;</td><td>CABRILLO-NAME &nbsp;</td></tr>
    pairs = re.findall(
        r"<td[^>]*>([^<]*?)\s*&nbsp;</td>\s*<td[^>]*>([^<]*?)\s*&nbsp;</td>\s*</tr>",
        html, re.S)
    site = {}
    for name, cab in pairs:
        name = name.strip()
        cab = cab.strip()
        if cab and cab.upper() not in ("CABRILLO NAME", "NAME"):
            site.setdefault(cab, []).append(name)
    return site


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "..", "HolyLogger", "contests.json")
    data = json.load(open(path, encoding="utf-8"))
    contests = data.get("contests", [])

    try:
        site = fetch_site_names()
    except Exception as e:
        print("ERROR: could not fetch %s\n  %s" % (URL, e))
        sys.exit(2)

    valid = set(site.keys())
    by_name = {}   # UPPER display name -> cabrillo name (for suggestions)
    for cab, names in site.items():
        for n in names:
            by_name[n.upper()] = cab

    ok = skipped = 0
    problems = []
    for c in contests:
        cid = c.get("id", "")
        cab = (c.get("cabrillo_name") or "").strip()
        nm = c.get("name", "")
        if not cab or cab.upper() == "N/A":
            skipped += 1
            print("SKIP      %-26s cabrillo_name = '%s'" % (cid, cab))
            continue
        if cab in valid:
            ok += 1
        else:
            match = difflib.get_close_matches(nm.upper(), list(by_name.keys()), n=1, cutoff=0.55)
            suggest = by_name[match[0]] if match else "(no close match on site)"
            problems.append((cid, cab, suggest, nm))

    print()
    for cid, cab, suggest, nm in problems:
        print("MISMATCH  %-26s ours='%s'  site suggests='%s'   (%s)" % (cid, cab, suggest, nm))

    print()
    print("Summary: %d valid, %d MISMATCH, %d skipped (N/A).  WA7BNM lists %d Cabrillo names."
          % (ok, len(problems), skipped, len(valid)))
    print("NOTE: Cabrillo NAMES checked only. Exchange fields must still be verified by hand")
    print("      against each sponsor's official rules -- the site does not publish those.")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
