"""Keep COD Downgrader's built-in manifest list up to date.

    python tools/catalog.py add rows.txt                          merge rows copied from SteamDB into the list
    python tools/catalog.py import a.zip [b.zip ...] [--write]    compare exports from PCs with the list
    python tools/catalog.py check                                 check the list and say what it holds

The list is src/CODDowngrader/Catalog/manifests.txt, compiled into the exe. SteamDB does not allow
automated access, so its rows are copied by hand, in this layout:

    APP: 10180
    DEPOT: 10181
    11 September 2026 – 16:00:08 UTC	3 days ago	3303535216987755639
    3 September 2026 – 16:01:41 UTC	11 days ago	5551013591116506205

APP starts an app's depots and DEPOT starts a depot's rows, copied from
steamdb.info/depot/<depot>/manifests/ as the page shows them. A depot two apps share only needs its
rows once. A depot new to an app is added to that app; when another app owns it, write @<owner> after
it on the app's line by hand.

import reads the zips `CODDowngrader --export <file>` writes: what a PC's Steam has installed, cached,
downloaded and names as the latest build. A PC does not know when a build came out, so every date in the
list stays SteamDB's. import reports what the exports show that the list lacks or contradicts, with the
SteamDB pages to copy rows from. With --write it also fills in what needs no date: the app that owns each
depot, app lines' depots that already have rows, and manifests' sizes.
"""
import argparse
import calendar
import html
import json
import re
import sys
import zipfile
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOG = ROOT / "src" / "CODDowngrader" / "Catalog" / "manifests.txt"
MONTHS = {name.lower(): number for number, name in enumerate(calendar.month_name) if name}
ROW = re.compile(r"^(\d{1,2}) ([A-Za-z]+) (\d{4})\s+[–-]\s+(\d{2}):(\d{2}):(\d{2}) UTC\b.*?\b(\d{1,20})\s*$")
NEAR = timedelta(minutes=10)
EXPORT_FORMAT = 1

HEADER = """\
# COD Downgrader's built-in manifest list: every manifest of every depot below, with when SteamDB
# first saw it. Maintained with tools/catalog.py, which says how to copy new rows from SteamDB.
#
#   updated <date>                         when rows were last added
#   history-from <date>                    updates SteamDB first saw before this are not used: its rows
#                                          from before then include manifests that were never the public
#                                          build, and first-seen dates long after a build went up
#   app <app> <depot> <depot>@<owner> ...  the depots a download of the app is made of; @<owner> marks
#                                          a depot another app owns
#   not-public <depot> <manifest>          listed on SteamDB, but not what Steam installed
#   not-default <depot>                    never part of a download of a game Steam has not installed, such as
#                                          low-violence content product info does not mark as such
#   name <depot> <name>                    the depot's name on SteamDB
#   <depot> <manifest> <first seen, UTC> [<size>]
#                                          one manifest of a depot, newest first, with its size on disk
#                                          in bytes once a PC's export has shown it
"""


class Catalog:
    def __init__(self):
        self.updated = None
        self.history_from = None
        self.apps = {}        # app -> [(depot, owner or None)]
        self.notes = {}       # ("app", app) or ("not-public", depot, manifest) -> comment
        self.not_public = []  # [(depot, manifest)]
        self.not_default = [] # [depot]
        self.names = {}       # depot -> name
        self.rows = {}        # depot -> {manifest: first seen}
        self.sizes = {}       # depot -> {manifest: size in bytes}

    @staticmethod
    def read(path: Path) -> "Catalog":
        catalog = Catalog()
        if not path.exists():
            return catalog
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            text, _, comment = line.partition("#")
            words = text.split()
            comment = comment.strip()
            if not words:
                continue
            try:
                if words[0] == "updated":
                    catalog.updated = words[1]
                elif words[0] == "history-from":
                    catalog.history_from = words[1]
                elif words[0] == "app":
                    app = int(words[1])
                    catalog.apps[app] = [(int(d), int(o) if o else None) for d, _, o in (w.partition("@") for w in words[2:])]
                    if comment:
                        catalog.notes[("app", app)] = comment
                elif words[0] == "not-public":
                    key = (int(words[1]), int(words[2]))
                    catalog.not_public.append(key)
                    if comment:
                        catalog.notes[("not-public", *key)] = comment
                elif words[0] == "not-default":
                    depot = int(words[1])
                    catalog.not_default.append(depot)
                    if comment:
                        catalog.notes[("not-default", depot)] = comment
                elif words[0] == "name":
                    catalog.names[int(words[1])] = text.split(None, 2)[2].strip()
                else:
                    depot, manifest = int(words[0]), int(words[1])
                    seen = datetime.strptime(words[2], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
                    catalog.rows.setdefault(depot, {})[manifest] = seen
                    if len(words) > 3:
                        catalog.sizes.setdefault(depot, {})[manifest] = int(words[3])
            except (IndexError, ValueError) as error:
                raise SystemExit(f"{path}:{number}: {error}: {line}")
        return catalog

    def depot_order(self):
        seen = []
        for depots in self.apps.values():
            seen.extend(d for d, _ in depots if d not in seen)
        return seen + sorted(d for d in self.rows if d not in seen)

    def write(self, path: Path) -> None:
        out = [HEADER, f"updated {self.updated}"]
        if self.history_from:
            out.append(f"history-from {self.history_from}")
        out.append("")
        for app, depots in self.apps.items():
            line = f"app {app} " + " ".join(f"{d}@{o}" if o else str(d) for d, o in depots)
            note = self.notes.get(("app", app))
            out.append(f"{line}  # {note}" if note else line)
        out.append("")
        for depot, manifest in self.not_public:
            note = self.notes.get(("not-public", depot, manifest))
            out.append(f"not-public {depot} {manifest}" + (f"  # {note}" if note else ""))
        for depot in self.not_default:
            note = self.notes.get(("not-default", depot))
            out.append(f"not-default {depot}" + (f"  # {note}" if note else ""))
        if self.names:
            out.append("")
            order = {depot: i for i, depot in enumerate(self.depot_order())}
            for depot in sorted(self.names, key=lambda d: (order.get(d, len(order)), d)):
                out.append(f"name {depot} {self.names[depot]}")
        for depot in self.depot_order():
            out.append("")
            sizes = self.sizes.get(depot, {})
            for manifest, seen in sorted(self.rows.get(depot, {}).items(), key=lambda kv: kv[1], reverse=True):
                size = sizes.get(manifest)
                out.append(f"{depot} {manifest} {seen.strftime('%Y-%m-%dT%H:%M:%SZ')}" + (f" {size}" if size else ""))
        path.write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")


def read_paste(text: str):
    """Rows as SteamDB shows them: {app: [depots]} and {depot: [(first seen, manifest)]}."""
    apps, rows = {}, {}
    app = depot = None
    for number, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if match := re.fullmatch(r"APP:\s*(\d+)", stripped):
            app = int(match[1])
            apps.setdefault(app, [])
        elif match := re.fullmatch(r"DEPOT:\s*(\d+)", stripped):
            if app is None:
                raise SystemExit(f"line {number}: DEPOT before any APP")
            depot = int(match[1])
            if depot not in apps[app]:
                apps[app].append(depot)
            rows.setdefault(depot, [])
        elif match := ROW.match(stripped):
            if depot is None:
                raise SystemExit(f"line {number}: a row before any DEPOT")
            day, month, year, hour, minute, second, manifest = match.groups()
            if month.lower() not in MONTHS:
                raise SystemExit(f"line {number}: unknown month {month}")
            seen = datetime(int(year), MONTHS[month.lower()], int(day), int(hour), int(minute), int(second), tzinfo=timezone.utc)
            rows[depot].append((seen, int(manifest)))
        elif stripped:
            print(f"skipped line {number}: {stripped[:80]}")
    return apps, rows


def add(paste: Path) -> None:
    catalog = Catalog.read(CATALOG)
    apps, rows = read_paste(paste.read_text(encoding="utf-8"))
    added, conflicts = 0, []
    for app, depots in apps.items():
        known = catalog.apps.setdefault(app, [])
        for depot in depots:
            if all(d != depot for d, _ in known):
                known.append((depot, None))
                print(f"app {app}: new depot {depot}")
    for depot, listed in rows.items():
        have = catalog.rows.setdefault(depot, {})
        for seen, manifest in listed:
            if manifest not in have:
                have[manifest] = seen
                added += 1
            elif have[manifest] != seen:
                conflicts.append(f"{depot} {manifest}: listed {seen:%Y-%m-%d %H:%M:%S}, kept {have[manifest]:%Y-%m-%d %H:%M:%S}")
    for conflict in conflicts:
        print(f"different time, kept the first: {conflict}")
    if added or catalog.updated is None:
        catalog.updated = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    catalog.write(CATALOG)
    print(f"{added} new manifests; {CATALOG.relative_to(ROOT)} now lists {sum(map(len, catalog.rows.values()))} in {len(catalog.rows)} depots")
    check(catalog)


def check(catalog: Catalog) -> bool:
    ok = True
    for app, depots in catalog.apps.items():
        for depot, owner in depots:
            if not catalog.rows.get(depot):
                print(f"app {app}: depot {depot} has no rows")
                ok = False
            if owner is not None and owner not in catalog.apps:
                print(f"app {app}: depot {depot} is owned by app {owner}, which has no app line")
                ok = False
    for depot, manifest in catalog.not_public:
        if manifest not in catalog.rows.get(depot, {}):
            print(f"not-public {depot} {manifest} is not a row")
            ok = False
    hidden = set(catalog.not_public)
    for depot, have in catalog.rows.items():
        listed = sorted((seen, manifest) for manifest, seen in have.items() if (depot, manifest) not in hidden)
        for (older, a), (newer, b) in zip(listed, listed[1:]):
            if newer - older < NEAR:
                print(f"depot {depot}: {a} and {b} were first seen {int((newer - older).total_seconds())} s apart; "
                      "only one of them can have been the public build (see not-public)")
    sized = sum(map(len, catalog.sizes.values()))
    print(f"{len(catalog.apps)} apps, {len(catalog.rows)} depots, {sum(map(len, catalog.rows.values()))} manifests "
          f"({sized} with a size), updated {catalog.updated}")
    return ok


# --- exports ---------------------------------------------------------------------------------------


class Evidence:
    """What the exports show, merged: every manifest each depot is known to have had, and where that is known from."""

    def __init__(self):
        self.exports = []                                  # [(label, export)]
        self.seen = defaultdict(lambda: defaultdict(set))  # depot -> manifest -> {"installed on laptop", ...}
        self.sizes = defaultdict(set)                      # (depot, manifest) -> {size in bytes}
        self.product = {}                                  # app -> (exported, product info)
        self.names = {}                                    # app -> name
        self.installed = defaultdict(set)                  # app -> {label}

    def strong(self, depot, manifest):
        """A PC had the manifest installed, or Steam cached it: more than a download in the log, which can be a comparison."""
        return any(source.startswith(("installed", "cached")) for source in self.seen[depot].get(manifest, ()))

    def read(self, path: Path) -> None:
        label = path.stem
        try:
            with zipfile.ZipFile(path) as archive:
                data = json.loads(archive.read("export.json").decode("utf-8"))
        except (OSError, KeyError, zipfile.BadZipFile, json.JSONDecodeError) as error:
            raise SystemExit(f"{path}: not an export ({error})")
        if data.get("format") != EXPORT_FORMAT:
            raise SystemExit(f"{path}: export format {data.get('format')}; this script reads {EXPORT_FORMAT}")
        self.exports.append((label, data))
        exported = data.get("exported", "")

        def saw(depot, manifest, source, size=None):
            manifest = int(manifest)
            if manifest:
                self.seen[int(depot)][manifest].add(f"{source} on {label}")
                if size:
                    self.sizes[(int(depot), manifest)].add(int(size))

        for app in data.get("apps", []):
            app_id = app["app"]
            self.names[app_id] = app.get("name") or f"app {app_id}"
            if info := app.get("productInfo"):
                if app_id not in self.product or exported > self.product[app_id][0]:
                    self.product[app_id] = (exported, info)
                for depot in info.get("depots", []):
                    if depot.get("manifest"):
                        saw(depot["depot"], depot["manifest"], "Steam's latest", depot.get("size"))
            if installed := app.get("installed"):
                self.installed[app_id].add(label)
                for depot in installed.get("depots", []):
                    saw(depot["depot"], depot["manifest"], "installed", depot.get("size"))
        for cached in data.get("cached", []):
            saw(cached["depot"], cached["manifest"], "cached", cached.get("size"))
        for fetched in data.get("fetched", []):
            saw(fetched["depot"], fetched["manifest"], "downloaded")
        for name in data.get("downloadedLists", []):
            if match := re.search(r"(\d+)_(\d+)\.(?:manifest|txt)$", name):
                saw(match[1], match[2], "file list")


def downloads(depot: dict) -> bool:
    """Whether COD Downgrader downloads the depot for a game Steam has not installed: Windows, English or no language."""
    if depot.get("sharedInstall") == "1" or depot.get("lowViolence"):
        return False
    os_list = (depot.get("os") or "").lower()
    language = (depot.get("language") or "").lower()
    if (os_list and "windows" not in os_list) or (language and language != "english"):
        return False
    return bool(depot.get("fromApp") or depot.get("manifest"))


def describe(depot: dict) -> str:
    parts = [depot["name"]] if depot.get("name") else []
    if depot.get("dlcApp"):
        parts.append(f"DLC {depot['dlcApp']}")
    if depot.get("fromApp"):
        parts.append(f"from app {depot['fromApp']}")
    return f" ({', '.join(parts)})" if parts else ""


def import_exports(paths, write: bool, report) -> None:
    catalog = Catalog.read(CATALOG)
    evidence = Evidence()
    for path in paths:
        evidence.read(path)

    hidden = set(catalog.not_public)
    depot_apps = defaultdict(set)
    for app, depots in catalog.apps.items():
        for depot, _ in depots:
            depot_apps[depot].add(app)
    for app, (_, info) in evidence.product.items():
        for depot in info.get("depots", []):
            depot_apps[depot["depot"]].add(app)

    def name(app):
        return evidence.names.get(app, f"app {app}")

    def game(depot):
        return " / ".join(sorted({name(a) for a in depot_apps.get(depot, ())})) or "no known app"

    def sources(depot, manifest):
        return ", ".join(sorted(evidence.seen[depot].get(manifest, ())))

    def steamdb(depot):
        return f"[manifests](https://steamdb.info/depot/{depot}/manifests/)"

    out = ["# Exports compared with the built-in list", ""]
    for label, data in evidence.exports:
        apps = data.get("apps", [])
        out.append(f"- **{label}**: exported {data.get('exported', '?')}, {sum(1 for a in apps if a.get('productInfo'))} games with "
                   f"product info, {sum(1 for a in apps if a.get('installed'))} installed, {len(data.get('cached', []))} cached "
                   f"manifests, {len(data.get('fetched', []))} manifest downloads in Steam's log")

    def section(title, lines, empty):
        out.extend(["", f"## {title}", ""])
        out.extend(lines if lines else [empty])

    lines = [f"- {game(d)}, depot {d}: `{m}` is marked not-public, but it was {sources(d, m)}"
             for d, m in catalog.not_public if m in evidence.seen[d]]
    section("Marked not-public, but a PC had it", lines, "None.")

    lines = []
    for depot, rows in catalog.rows.items():
        listed = sorted((seen, m) for m, seen in rows.items() if (depot, m) not in hidden)
        for (t1, m1), (t2, m2) in zip(listed, listed[1:]):
            if t2 - t1 < NEAR and evidence.strong(depot, m1) and m2 not in evidence.seen[depot]:
                lines.append(f"- {game(depot)}, depot {depot}: `{m2}` was first seen {int((t2 - t1).total_seconds())} s after `{m1}`, "
                             f"which was {sources(depot, m1)}. If it was never public: `not-public {depot} {m2}`")
    section("Maybe never public", lines, "None.")

    lines = []
    for depot in sorted(evidence.seen):
        rows = catalog.rows.get(depot)
        if rows:
            for manifest in sorted(set(evidence.seen[depot]) - set(rows)):
                lines.append(f"- {game(depot)}, depot {depot}: `{manifest}`, {sources(depot, manifest)} · {steamdb(depot)}")
    section("Manifests the list does not have", lines, "None: every manifest of a listed depot in the exports is in the list.")

    lines = []
    for depot in sorted(evidence.seen):
        rows = {m: seen for m, seen in catalog.rows.get(depot, {}).items() if (depot, m) not in hidden}
        if not rows:
            continue
        newest_seen, newest = max((seen, m) for m, seen in rows.items())
        for manifest, found in sorted(evidence.seen[depot].items()):
            latest = sorted(s for s in found if s.startswith("Steam's latest"))
            if latest and manifest in rows and manifest != newest:
                lines.append(f"- {game(depot)}, depot {depot}: {', '.join(latest)} is `{manifest}` (first seen "
                             f"{rows[manifest]:%Y-%m-%d %H:%M}), but the list's newest is `{newest}` (first seen {newest_seen:%Y-%m-%d %H:%M})")
    section("Steam's latest is not the list's newest", lines, "None.")

    lines, languages = [], defaultdict(set)
    for app, (_, info) in sorted(evidence.product.items()):
        depots = info.get("depots", [])
        if app not in catalog.apps:
            wanted = [d["depot"] for d in depots if downloads(d)]
            lines.append(f"- {name(app)} ({app}) is not in the list · [depots](https://steamdb.info/app/{app}/depots/) · "
                         f"[patch notes](https://steamdb.info/app/{app}/patchnotes/) · manifests of "
                         + ", ".join(f"[{d}](https://steamdb.info/depot/{d}/manifests/)" for d in wanted))
            continue
        for depot in depots:
            if depot["depot"] in catalog.rows:
                continue
            if downloads(depot):
                lines.append(f"- {name(app)}, depot {depot['depot']}{describe(depot)} · {steamdb(depot['depot'])}")
            elif depot.get("language") and not depot.get("fromApp") and "windows" in (depot.get("os") or "windows").lower():
                languages[depot["language"]].add(app)
    for app in sorted(evidence.installed):
        if app not in catalog.apps and app not in evidence.product:
            lines.append(f"- {name(app)} ({app}) is installed but not in the list, and there is no product info for it · "
                         f"[depots](https://steamdb.info/app/{app}/depots/)")
    section("Depots the list does not have", lines, "None.")
    section("Other languages", [f"- {language}: {len(apps)} games" for language, apps in sorted(languages.items())], "None.")

    lines = []
    for app, depots in catalog.apps.items():
        if app not in evidence.product:
            continue
        info = {d["depot"]: d for d in evidence.product[app][1].get("depots", [])}
        updated = []
        for depot, owner in depots:
            d = info.get(depot)
            if d is None:
                lines.append(f"- {name(app)} ({app}): depot {depot} is on the app's line, but not in Steam's product info")
                updated.append((depot, owner))
                continue
            should = d["fromApp"] if d.get("fromApp") and d.get("sharedInstall") != "1" and d["fromApp"] != app else None
            if should != owner:
                lines.append(f"- {name(app)} ({app}): depot {depot} is owned by {f'app {should}' if should else 'the app itself'}, "
                             f"not {f'app {owner}' if owner else 'the app itself'}")
            updated.append((depot, should))
        on_line = {depot for depot, _ in updated}
        for d in evidence.product[app][1].get("depots", []):
            if downloads(d) and d["depot"] not in on_line and d["depot"] in catalog.rows:
                updated.append((d["depot"], d["fromApp"] if d.get("fromApp") and d["fromApp"] != app else None))
                lines.append(f"- {name(app)} ({app}): depot {d['depot']}{describe(d)} has rows and joins the app's line")
        if write:
            catalog.apps[app] = updated
    section("App lines" + ("" if write else " (--write applies these)"), lines, "Every app line matches Steam's product info.")

    lines, added = [], 0
    for (depot, manifest), sizes in sorted(evidence.sizes.items()):
        if manifest not in catalog.rows.get(depot, {}):
            continue
        known = catalog.sizes.get(depot, {}).get(manifest)
        every = set(sizes) | ({known} if known else set())
        if len(every) > 1:
            lines.append(f"- depot {depot} `{manifest}` has different sizes: {', '.join(map(str, sorted(every)))}")
        elif not known:
            added += 1
            if write:
                catalog.sizes.setdefault(depot, {})[manifest] = next(iter(sizes))
    section("Sizes", [f"- {added} manifests {'got' if write else 'get'} a size{'' if write else ' with --write'}."] + lines, "")

    text = "\n".join(out) + "\n"
    print(text)
    if report:
        report = Path(report)
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(text, encoding="utf-8", newline="\n")
        print(f"report written to {report}")
    if write:
        catalog.write(CATALOG)
        print(f"{CATALOG.relative_to(ROOT)} updated")
    if not check(catalog):
        sys.exit(1)


# --- pages saved from SteamDB ---------------------------------------------------------------------

REDISTRIBUTABLES = 228980
# A depot with no language tag whose name says it is another language's or the low-violence version's content.
NOT_DEFAULT_NAME = re.compile(
    r"\blow violence\b|\bLV\b|\b(?:german|germany|french|italian|spanish|russian|polish|japanese|chinese|korean|czech|brazilian|portuguese|arabic)\b",
    re.IGNORECASE)


def _text(fragment: str) -> str:
    return html.unescape(" ".join(re.sub(r"<[^>]+>", " ", fragment).split())).replace("#", "no.")


def read_page(path: Path):
    """An app's depots page or a depot's manifests page, as saved from the browser. None for any other page."""
    source = path.read_text(encoding="utf-8", errors="replace")
    canonical = re.search(r'<link rel="canonical" href="https://steamdb\.info/(app|depot)/(\d+)/', source)
    if not canonical:
        return None
    if canonical[1] == "app":
        depots = {}
        for depot, packages, body in re.findall(r'<tr class="depot" data-depotid="(\d+)" data-packages="([^"]*)">(.*?)</tr>', source, flags=re.S):
            cell = re.search(r'<td class="depot-config">(.*?)</td>', body, flags=re.S)
            config = cell[1] if cell else ""
            language = re.search(r'<span class="depot-language">(.*?)</span>', config, flags=re.S)
            name = re.search(r'<span class="i muted">(.*?)</span>', config, flags=re.S)
            owner = re.search(r'data-appid="(\d+)">Depot from', config)
            dlc = re.search(r">DLC (\d+)<", config)
            disk = re.search(r'depot-size-disk[^"]*" data-sort="(\d+)"', body)
            depots[int(depot)] = {
                "os": [_text(s).lower() for s in re.findall(r'<span class="depot-os">(.*?)</span>', config, flags=re.S)],
                "language": _text(language[1]) if language else None,
                "optional": "<span>Optional</span>" in config,
                "from": int(owner[1]) if owner else None,
                "dlc": int(dlc[1]) if dlc else None,
                "name": _text(name[1]) if name else None,
                "size": int(disk[1]) if disk and int(disk[1]) > 0 else None,
                "packages": {p for p in packages.split(",") if p.strip() and p != "0"},
            }
        branches = []
        for row in re.findall(r"<tr[^>]*>(?:(?!</tr>).)*\?branch=(?:(?!</tr>).)*</tr>", source, flags=re.S):
            branch = re.search(r"\?branch=([^\"&]+)", row)
            build = re.search(r"/patchnotes/(\d+)/", row)
            branches.append((branch[1], int(build[1]) if build else None, re.findall(r'datetime="([^"]+)"', row)))
        title = re.search(r"<h1[^>]*>(.*?)</h1>", source, flags=re.S)
        return {"kind": "app", "app": int(canonical[2]), "name": _text(title[1]) if title else "", "depots": depots, "branches": branches}

    heading = re.search(r'Depot <b>(\d+)</b> for <a href="/app/(\d+)/', source)
    title = re.search(r"<title>Depot \d+ \((.*)\) · ", source)
    rows = [(datetime.fromisoformat(when).astimezone(timezone.utc), int(manifest)) for when, manifest in re.findall(
        r'data-time="([^"]+)"></td>\s*<td class="tabular-nums">\s*<a href="/depot/\d+/history/\?changeid=M:(\d+)"', source)]
    return {"kind": "depot", "depot": int(canonical[2]), "app": int(heading[2]) if heading else None,
            "name": _text(title[1]) if title else None, "rows": rows,
            "empty": "have any manifests in history for this depot" in source}


def download_depot(info: dict) -> bool:
    """Whether a depot on an app's page is one COD Downgrader downloads for that app: Windows, English or no language, not a redistributable."""
    if info["from"] == REDISTRIBUTABLES or (info["os"] and "windows" not in info["os"]):
        return False
    return info["language"] in (None, "English")


def pages(folder: Path, write: bool, report) -> None:
    catalog = Catalog.read(CATALOG)
    apps, depots, skipped = {}, {}, []
    for path in sorted(folder.iterdir()):
        if not path.is_file() or path.suffix.lower() not in (".html", ".htm"):
            continue
        try:
            page = read_page(path)
        except OSError as error:
            skipped.append(f"{path.name} ({error.strerror})")
            continue
        if page is None:
            skipped.append(f"{path.name} (not a depots or manifests page)")
        elif page["kind"] == "app":
            apps[page["app"]] = page
        else:
            depots.setdefault(page["depot"], page)["rows"] = sorted(set(depots.get(page["depot"], page)["rows"]) | set(page["rows"]))

    out = [f"# SteamDB pages in {folder}", "", f"- {len(apps)} depots pages, {len(depots)} depots with manifest pages"]
    out += [f"- could not read: {name}" for name in skipped]

    def section(title, lines, empty="None."):
        out.extend(["", f"## {title}", ""])
        out.extend(lines if lines else [empty])

    lines, added = [], 0
    for depot, page in sorted(depots.items()):
        have = catalog.rows.setdefault(depot, {})
        new = [(seen, m) for seen, m in page["rows"] if m not in have]
        moved = [f"`{m}` {seen:%Y-%m-%d %H:%M:%S} (list: {have[m]:%Y-%m-%d %H:%M:%S})" for seen, m in page["rows"] if m in have and have[m] != seen]
        for seen, manifest in new:
            if write:
                have[manifest] = seen
            added += 1
        if not have:
            del catalog.rows[depot]
        if new or moved:
            lines.append(f"- depot {depot} ({page['name']}): {len(new)} new" + (f"; different times: {', '.join(moved)}" if moved else ""))
    section(f"Manifest rows ({added} new)", lines, "Every row on the pages is already in the list, at the same time.")

    def wanted(info):
        """A depot a download of the app takes: a download depot whose name does not make it another language's or low-violence content."""
        return download_depot(info) and not NOT_DEFAULT_NAME.search(info["name"] or "")

    def described(depot, info):
        extra = [info["name"] or "no name"]
        extra += [f"DLC {info['dlc']}"] if info["dlc"] else []
        extra += [f"from {info['from']}"] if info["from"] else []
        return f"depot {depot} ({', '.join(extra)})"

    def has_rows(depot):
        return bool(catalog.rows.get(depot)) or bool(depots.get(depot, {}).get("rows"))

    def no_manifests(depot):
        """SteamDB has never seen a manifest for the depot, so there is nothing to download and nothing to wait for."""
        return depots.get(depot, {}).get("empty", False) and not has_rows(depot)

    lines, owners, adds, leaves, not_default, names = [], [], [], [], {}, {}
    for app, page in sorted(apps.items()):
        table = page["depots"]
        public = next((b for b in page["branches"] if b[0] == "public"), None)
        lines.append(f"- {page['name']} ({app}): {len(table)} depots" + (f", public build {public[1]}, built {public[2][0][:16] if public[2] else '?'}" + (f", updated {public[2][1][:16]}" if len(public[2]) > 1 else "") if public else ""))
        for depot, info in table.items():
            if info["name"] and (download_depot(info) or depot in dict(catalog.apps.get(app, []))):
                names[depot] = info["name"]
            if download_depot(info) and not wanted(info) and depot not in catalog.not_default:
                not_default.setdefault(depot, f"{info['name']}{', Optional' if info['optional'] else ''} (app {app} on SteamDB)")
        if app not in catalog.apps:
            continue
        updated = []
        for depot, owner in catalog.apps[app]:
            info = table.get(depot)
            if info is None:
                leaves.append(f"- {page['name']} ({app}): depot {depot} is on the app's line but not on its SteamDB page, and stays")
                updated.append((depot, owner))
                continue
            should = info["from"] if info["from"] not in (None, app) else None
            if should != owner:
                owners.append(f"- {page['name']} ({app}): depot {depot} is from {f'app {should}' if should else 'the app itself'}, not {f'app {owner}' if owner else 'the app itself'}")
            if not wanted(info):
                leaves.append(f"- {page['name']} ({app}): {described(depot, info)}, {', '.join(info['os']) or 'every OS'}, {info['language'] or 'no language'}")
                continue
            updated.append((depot, should))
        on_line = {depot for depot, _ in updated}
        for depot, info in sorted(table.items()):
            if not wanted(info) or depot in on_line:
                continue
            if has_rows(depot):
                updated.append((depot, info["from"] if info["from"] not in (None, app) else None))
                adds.append(f"- {page['name']} ({app}): {described(depot, info)} has rows")
            elif no_manifests(depot):
                adds.append(f"- {page['name']} ({app}): {described(depot, info)} has no manifests on SteamDB at all, so it stays off")
            else:
                adds.append(f"- {page['name']} ({app}): {described(depot, info)} has no rows yet, so it cannot join · [manifests](https://steamdb.info/depot/{depot}/manifests/)")
        if write:
            catalog.apps[app] = updated
    section("Pages", lines)
    section("Owners" + ("" if write else " (--write applies these)"), owners, "Every owner on the app lines matches SteamDB.")
    section("Not part of a default download" + ("" if write else " (--write marks these not-default)"), [f"- depot {d}: {note}" for d, note in sorted(not_default.items())])
    section("App lines: depots that join" + ("" if write else " (--write)"), adds)
    section("App lines: depots that leave" + ("" if write else " (--write)"), leaves)
    # An app the list lacks joins once every depot a download of it takes has rows, and the apps it borrows from can join too.
    proposed = {app: [(d, i["from"] if i["from"] not in (None, app) else None) for d, i in sorted(p["depots"].items()) if wanted(i) and not no_manifests(d)]
                for app, p in sorted(apps.items()) if app not in catalog.apps}
    ready = {app for app, line in proposed.items() if line and all(has_rows(d) for d, _ in line)}
    while blocked := {app for app in ready if any(o is not None and o not in catalog.apps and o not in ready for _, o in proposed[app])}:
        ready -= blocked
    unlisted = []
    for app, line in proposed.items():
        listed = ", ".join(f"{d}@{o}" if o else str(d) for d, o in line)
        lacking = [str(d) for d, _ in line if not has_rows(d)]
        if app in ready:
            unlisted.append(f"- {apps[app]['name']} ({app}) joins the list: {listed}")
            if write:
                catalog.apps[app] = line
        else:
            unlisted.append(f"- {apps[app]['name']} ({app}): {listed}; " + (f"no rows yet for {', '.join(lacking)}" if lacking else "waits for the app it borrows from"))
    section("Apps not in the list", unlisted)
    new_names = {d: n for d, n in names.items() if catalog.names.get(d) != n}
    section("Names", [f"- {len(new_names)} depot names {'written' if write else 'to write with --write'}"])

    if write:
        for depot, note in sorted(not_default.items()):
            catalog.not_default.append(depot)
            catalog.notes[("not-default", depot)] = note
        catalog.names.update(new_names)
        if added:
            catalog.updated = datetime.now(timezone.utc).strftime("%Y-%m-%d")

    text = "\n".join(out) + "\n"
    print(text)
    if report:
        Path(report).write_text(text, encoding="utf-8", newline="\n")
        print(f"report written to {report}")
    if write:
        catalog.write(CATALOG)
        print(f"{CATALOG.relative_to(ROOT)} updated")
    if not check(catalog):
        sys.exit(1)


def main() -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("add", help="merge rows copied from SteamDB").add_argument("rows", type=Path)
    compare = commands.add_parser("import", help="compare exports from PCs with the list")
    compare.add_argument("exports", type=Path, nargs="+")
    compare.add_argument("--write", action="store_true", help="fill in owners, app lines' depots and sizes")
    compare.add_argument("--report", type=Path, help="also write the report to this Markdown file")
    saved = commands.add_parser("pages", help="read depots and manifests pages saved from SteamDB")
    saved.add_argument("folder", type=Path)
    saved.add_argument("--write", action="store_true", help="add rows, owners, names and low-violence marks")
    saved.add_argument("--report", type=Path, help="also write the report to this Markdown file")
    commands.add_parser("check", help="check the list")
    args = parser.parse_args()
    if args.command == "add":
        add(args.rows)
    elif args.command == "import":
        import_exports(args.exports, args.write, args.report)
    elif args.command == "pages":
        pages(args.folder, args.write, args.report)
    elif not check(Catalog.read(CATALOG)):
        sys.exit(1)


if __name__ == "__main__":
    main()
