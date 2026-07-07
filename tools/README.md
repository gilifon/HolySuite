# Maintainer tools

Utilities for whoever maintains HolyLogger. **The ham operator never runs these** —
they are not part of the app and are not shipped to users.

## verify_contest_cabnames.py

Checks the `cabrillo_name` of every contest in `HolyLogger/contests.json` against the
authoritative WA7BNM Cabrillo-names list (<https://www.contestcalendar.com/cabnames.php>)
and reports any that don't match. It **only reports** — it never edits `contests.json`.

**Run it occasionally** (needs internet), e.g. before contest season:

```
python tools/verify_contest_cabnames.py
```

or just double-click `verify_contest_cabnames.bat`.

Read the output; for anything marked `MISMATCH`, open `contests.json`, fix the
`cabrillo_name` by hand, and ship an app update. Expect a couple of intentional
non-matches:

- `POTA` / `SOTA` — `N/A` on purpose (they don't use Cabrillo). Reported as `SKIP`.
- `HOLYLAND` — HolyLogger submits to the **IARC** robot, which expects `HOLYLAND`;
  WA7BNM catalogs it differently. Keep `HOLYLAND`.
- `SKCC` — submits via its own system, not a Cabrillo robot, so it isn't on WA7BNM.

### What it does NOT check

Only the Cabrillo **name** (the `CONTEST:` header) is machine-checkable this way.
The **exchange fields** — which boxes open in contest mode and what goes in each
QSO line — are not published in machine-readable form anywhere, so those still have
to be verified by hand against each sponsor's official rules.
