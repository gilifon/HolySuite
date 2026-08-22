# HolyLogger callsign list — two endpoints we need on the server

**For:** the server side of `tools.iarc.org/holyland/server/`
**Written:** 22 August 2026
**Status:** the client side is already written, built and waiting. Nothing is needed from HolyLogger.

---

## Why

HolyLogger suggests callsigns as the operator types. The suggestions come from one file that ships
with the installer:

```
Data\callsigns_merged_big.txt      ~11 MB, 1,510,080 callsigns
```

First line is a version number, then one callsign per line, sorted:

```
8
4X1AA
4X1AB
4X4ABC
...
```

At every start the program asks `getcallsign.php?version=N` what has changed since the version in
its file, and merges the answer. **That conversation can only ever add what is new.** Nothing in it
says how many callsigns there are supposed to be, so a file that is missing callsigns stays missing
them for ever — the program never asks for them again, and nothing notices.

This is not hypothetical. On **1 August 2026 the file lost 921,961 of its 1.5 million callsigns**.
Nothing in the program was able to tell; it had to be put back by hand from the repository.

So we need the program to be able to ask a second question — *how many should there be?* — and, when
the answer is more than it is holding, to fetch the whole list and start again from that.

`getcallsign.php` does not change. It stays exactly as it is.

---

## Endpoint 1 — how many callsigns are there?

```
GET https://tools.iarc.org/holyland/server/getcallsigncount.php
```

Reply, `application/json`:

```json
{"success":true,"total":1510342,"latestVersion":102}
```

| field | meaning |
|---|---|
| `success` | `true` when the number below is real. `false` (or a missing field) makes the client do nothing at all. |
| `total` | How many callsigns the server's full list holds. **This is the whole point of the endpoint.** |
| `latestVersion` | The current version number. Optional — the client ignores it here. |

Notes:

* This is asked at **every start of the program**, by every operator. Keep it cheap — a
  `SELECT COUNT(*)`, or a cached number refreshed when the list changes.
* `total` must be counted from the **same list** endpoint 2 serves. If the two disagree, the client
  throws away the download (see "What the client does with it" below).
* If this endpoint does not exist yet, the client logs one line and carries on as it does today.
  Nothing breaks. It is safe to add endpoint 1 first and endpoint 2 later.

---

## Endpoint 2 — the whole list

```
GET https://tools.iarc.org/holyland/server/getcallsignlist.php
```

Reply, `text/plain`:

```
102
4X1AA
4X1AB
4X4ABC
...
```

Rules, all of them enforced by the client:

1. **First line is the version number**, on its own. If the first line is not a number the client
   keeps the version it already had and treats that line as a callsign.
2. **One callsign per line.** Nothing else on the line.
3. **Upper case.** Letters, digits and strokes only. Longer than 15 characters is skipped.
4. **Sorted, byte order, ascending** — plain `ORDER BY BINARY callsign ASC` in MySQL (`COLLATE
   utf8mb4_bin`), *not* a case-insensitive or locale collation. The program binary-searches this
   file; a list in the wrong order makes the suggestions answer nonsense, so **a list that arrives
   out of order is refused outright**.
5. **No duplicates.**
6. Plain ASCII/UTF-8, no BOM. Line endings either way.
7. **gzip is very welcome** — the list is ~11 MB and compresses to about 3 MB. Ordinary
   `Content-Encoding: gzip` is fine; the client handles it.

In short: serve exactly the file that ships in the installer. Same shape, because it replaces
exactly that file.

**The list has to exist on the server first.** Today the server holds only the change log —
version 1 is `K3L`, version 102 is `DL2026T`, two rows to a page. The 1.5 million base callsigns
are only in the installer. They need seeding into a table (the file is in the repository at
`HolyLogger\Data\callsigns_merged_big.txt`), and `getcallsign.php`'s change log should go on
recording changes to that same table.

---

## What the client does with it

At every start, in the background, after the usual delta update:

1. Asks endpoint 1 for `total`.
2. Counts what its own file holds.
3. **If the file holds as many or more — it stops.** Nothing is downloaded. This is the normal case,
   every start, for every operator: one small JSON request and no more. A file holding *more* than
   the server is not a fault to be repaired — it is a station that has worked callsigns the server
   was never told about, and they are not thrown away.
4. If the file is short, it fetches endpoint 2, writing it straight to
   `callsigns_merged_big.txt.new` — never into memory — while counting the lines and checking the
   order as they go past.
5. It then **refuses the download, and leaves the file on disk untouched**, if any of these is true:
   * the list did not arrive in sorted order;
   * it holds fewer callsigns than the file already here;
   * it holds fewer than the `total` endpoint 1 promised.
6. Otherwise the new file takes the place of the old one, the index is rebuilt, and the before and
   after are written to the log.

Every step, including every refusal, is logged to
`%LOCALAPPDATA%\HolyLogger\Logs\callsign_update.log`.

---

## Testing it

```bash
curl -s https://tools.iarc.org/holyland/server/getcallsigncount.php
# {"success":true,"total":1510342,"latestVersion":102}

curl -s https://tools.iarc.org/holyland/server/getcallsignlist.php | head -5
# 102
# 4X1AA
# ...

# the two must agree: this count, minus the version line, must equal "total"
curl -s https://tools.iarc.org/holyland/server/getcallsignlist.php | wc -l

# and it must really be sorted - this must print nothing
curl -s https://tools.iarc.org/holyland/server/getcallsignlist.php | tail -n +2 | LC_ALL=C sort -c
```

If those four commands are right, the client end will do the rest.

---

## Questions

Ask Dan (4Z5SL). The client side is in `MainWindow.xaml.cs`, in
`ReconcileCallsignListWithServer` — the two URLs are constants at the top of it, so if the names or
the paths should be different, say so and they will be changed to whatever suits the server.
