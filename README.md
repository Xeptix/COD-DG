# COD Downgrader

Download any build of a Call of Duty you own on Steam, into your installed game or into a folder of its own.

By [Xep](https://github.com/Xeptix).

When a Call of Duty update breaks a community client or a mod, the build from before the update is
still on Steam's servers. COD Downgrader finds out which build that was, and either puts it into your
installed game, downloading only the files that differ, or downloads the whole game at that build into a
folder of its own, which Steam never touches and never updates.

> **For Steam owners only.** COD Downgrader downloads through your own Steam account, and Steam only
> sends a game's files to an account that owns the game. Every Call of Duty you download with it has
> to be owned by the Steam account you sign in with. Copies from Battle.net, the Microsoft Store or
> Game Pass cannot be downloaded with it.

## Download

Get `COD-DG-v<version>-win-x64.zip` from the [latest release](https://github.com/Xeptix/COD-DG/releases/latest),
extract it anywhere and run `CODDowngrader.exe`.

You need:

- a Steam account that owns the Call of Duty games you want to download
- Steam installed, on Windows 10 or 11, 64-bit
- the Steam mobile app, or the account's name and password, to sign in

The exe is not code-signed, so Windows SmartScreen asks before running it for the first time: choose
**More info**, then **Run anyway**.

Every release has a `SHA256SUMS` file beside the zip. To check a download, run
`Get-FileHash .\COD-DG-v<version>-win-x64.zip` in PowerShell and compare the hash with the zip's line
in `SHA256SUMS`.

## Using it

1. **Pick a game.** Installed Call of Duty games come first, each with how many older builds are
   known. Every other Call of Duty on Steam is under *Other Call of Duty games*; those have to be
   owned by the account too.
2. **Pick a version.**
   - **Before the update of *date*** is a build Steam replaced, from the built-in list or put back
     together from what Steam left on this PC.
   - **Installed now** is the build on disk. It is worth taking when Steam has an update queued and
     the files are still the old build.
   - **Latest on Steam** is the current build, shown when it is newer than what is installed.
   - **Newest build on the built-in list** stands in for it when Steam on this PC has no product info
     for the game.
   - **Enter manifest IDs from SteamDB** gets any other build.
3. **Choose how.**
   - **Put it into the installed game** downloads only the files that differ from what is installed,
     and swaps them in. See [Into the installed game](#into-the-installed-game).
   - **Download the whole build into a folder of its own** leaves the installed game as it is. The
     first download defaults to `COD Downgrader` inside the game's Steam library, beside `steamapps`;
     later ones default to wherever the last one went. Anywhere outside `steamapps` works.
   - **Save a patch folder** downloads only the files that differ from another build, the latest on
     Steam unless you pick one, to put into the game later. See [Patch folders](#patch-folders).
4. For a whole build of an installed game, **start from your installed copy**. See below.
5. **Sign in** to Steam with the account that owns the game.
6. DepotDownloader downloads the build, or only the files that differ.

## Where the versions come from

**The built-in list.** COD Downgrader carries the manifest history of the Call of Duty depots, copied by
hand from [SteamDB](https://steamdb.info)'s manifest lists: every manifest ID, with the date SteamDB
first saw it. Manifests that appeared within minutes of each other are one update, and each update gives
the build from before it, including for a game this PC has never installed. Updates from late June 2014
onwards are used, and each game goes back as far as the history of every one of its depots reaches.

Steam itself only tells a client about a game's current build, but a machine that has downloaded a game
keeps records of the builds it had:

- **The manifest cache.** After an update, Steam keeps the previous build's manifests in its
  `depotcache` folder. Each manifest records the depot, the manifest ID and when that build was made.
- **The content log.** Steam logs every manifest it fetches, with the time. The log still names a
  build after Steam has deleted its manifests.
- **Steam's product info.** The client's `appinfo.vdf` names the latest build of every game.

COD Downgrader puts these together into whole builds. It works back from the installed build one
update at a time, and each step only reverts the depots that update changed, so a version never mixes
a depot from one build with a depot from another. A build that both this PC and the built-in list name
is shown once, with the list's date.

## Versions from SteamDB

[SteamDB](https://steamdb.info) records every manifest of every depot, going back years. It is where to
get a build the built-in list does not have, or a depot it does not cover, such as a language. SteamDB
does not allow automated access, so COD Downgrader does not read it: it opens the page in your browser,
and you paste the ID.

1. Choose **Enter manifest IDs from SteamDB**.
2. **Open a depot's manifest list on SteamDB** opens `steamdb.info/depot/<depot>/manifests/`.
3. Find the date you want, copy the manifest ID, and choose **Paste manifest IDs**. One per line,
   either as a depot and a manifest, or as the `download_depot` command:

   ```
   311211 7651791086710252932
   download_depot 311210 311211 7651791086710252932
   ```

Only the depots that changed need an ID. Every other depot stays on the manifest that is installed.
**Pick a known manifest** does the same for a single depot, from the built-in list, the manifest cache
and the content log.

## Into the installed game

**Put it into the installed game** turns the Steam install itself into the chosen build, downloading only
what differs between the two:

1. Every file in a Steam manifest carries a SHA-1, so COD Downgrader compares the installed build's file
   list with the chosen build's and knows exactly which files differ. When Steam on this PC no longer
   has a file list, DepotDownloader fetches just that list first.
2. It reads those files in the game. A file that is not the installed build's version, as when a mod or
   a client has replaced it, is listed before anything changes.
3. DepotDownloader downloads only the files that differ, into `COD Downgrader\Staging` in the game's
   Steam library, and every one is checked against the build.
4. With the game closed, they are swapped in, and files the chosen build does not have are removed.
   COD Downgrader asks whether to keep the files this replaces in `COD Downgrader\Backups`, so **Undo the
   downgrade** can put them back without downloading. The staging folder is then deleted.

The update that broke a client is often small. Black Ops III before its update of 10 Sep 2026 differs
from the build after it by `BlackOps3.exe` alone, 101 MB, and Black Ops II by its three exes, 36 MB.
Modern Warfare 3's update of 3 Sep 2026 repacked nearly all of its content, so there it is most of the
game.

Apps that share a folder share content, so when a campaign and its Multiplayer are installed together,
COD Downgrader offers to take the other app back to its build from the same time as well. Only files in
the game's depots are ever touched: a client's or a mod's own files stay where they are.

Steam personalizes some exes for your account when it installs a game, rewriting part of each and signing
it: Black Ops's, Black Ops II's, and the Modern Warfare 2 and 3 campaign exes from before 3 Sep 2026.
Steam only does this for the build it installs, so a build with a different exe gets Steam's original.
Where the chosen build has the same exe as the installed one, COD Downgrader asks whether to keep your
personalized copy or put Steam's original in its place.

Before anything is downloaded, COD Downgrader asks which of the files that differ go in:

- **Everything that differs**: the whole build.
- **Only the content**: maps, fastfiles and every other file, keeping your installed exes and DLLs.
- **Only the exes and DLLs**, keeping your installed content.
- **Choose folders and files**: a checklist of every file that differs, grouped by folder, where a whole
  folder is picked at once.

The game's menu names the part written after the build, and Undo takes it out the same way.

This is how a map pack or other DLC goes back to an earlier version while the game keeps its current exe.
Most map packs changed after release: Black Ops II's and Black Ops III's DLC went through many versions,
and Modern Warfare 2's Stimulus and Resurgence packs, Modern Warfare 3's Collections, and the map packs of
Ghosts and Advanced Warfare changed again in August and September 2026. Modern Warfare 2's and Modern
Warfare 3's content from before 3 Sep 2026, map packs included, is in the same fastfile format as the
current builds.

Steam still lists its latest build for the game. **Verify integrity of game files**, or Steam's next
update of the game, brings latest files back, and a game with an update queued in Steam gets them when
Steam installs it. The game's menu shows when Steam has changed the files since, and offers **Downgrade
again**.

## Patch folders

**Save a patch folder** makes a minimal build: only the files that differ from another build, with
`COD Downgrader patch.json` naming both builds, every file's SHA-1 and the files the patch removes. It
asks which build the patch starts from: the latest on Steam, the build installed in the game, or any
other known build. The chosen version can be newer than that one, so a patch from an older build to the
latest takes a downgraded game back up without Steam. With one kept, the game can be downgraded again without downloading after Verify integrity of
game files has put its latest files back. It asks the same question as putting a build into the game, and
the patch holds only the part chosen.

**Apply a patch or a downloaded build to this game**, in the game's menu, puts a patch folder into the
installed game the same way, after checking every file in the folder. A patch made from a different build
than the game is on is flagged first. It takes a folder a whole build was downloaded into as well, asks
which of its files go in, and afterwards offers to delete that folder.

## Signing in

DepotDownloader signs in to Steam itself, in the same window:

- **QR code**: scan it with the Steam mobile app and approve.
- **Account name and password**: COD Downgrader asks for the account name; the password and the Steam
  Guard code are typed into DepotDownloader's own prompt.

COD Downgrader never sees a password. DepotDownloader keeps the login, so the next download only needs
the account name, which COD Downgrader remembers and offers first. If Steam drops DepotDownloader's
connection while the QR code is on screen and the sign-in is lost after you approve it, COD Downgrader
starts the download again with the saved login.

## Starting from your installed copy

For an installed game, COD Downgrader offers to copy the files the chosen build shares with your
install into the new folder before downloading. When Steam still has both builds' manifests, only files
whose contents are identical are copied. DepotDownloader then checks every file already in the folder
piece by piece and downloads only the pieces that differ. When only the installed build's file list is
known, its files are copied, and the ones the chosen build does not have are removed once the download
has finished.

Exes Steam personalizes for your account are never copied: DepotDownloader downloads Steam's originals.
When your installed game has a personalized copy of the same exe, COD Downgrader asks once the download
has finished whether the folder gets that copy or keeps Steam's original. Applying the folder to the game
later accepts either.

## What ends up in the folder

The whole game at that build: every depot that is installed (the game, its language and your DLC), or
for a game that is not installed, its Windows English depots. That includes the content a Multiplayer
or Zombies app shares with its campaign app, such as Black Ops II's `202972`, so the folder holds a
complete game. DepotDownloader runs once for each app that owns part of the download.

A download counts as finished only when DepotDownloader has recorded every depot as complete. DLC
depots the account does not own are skipped and listed at the end.

Beside the game files:

- `.DepotDownloader` is DepotDownloader's record of what it downloaded.
- `COD Downgrader.json` names each game in the folder, its build and every depot's manifest. It is
  written before anything is downloaded, so choosing the same version and folder again carries on from
  where a download stopped, and a different build is never written over a folder without asking.

When a download does not finish, COD Downgrader shows why and offers to try again, carrying on from
where it stopped. **Ctrl+C** stops a download.

## Games

| Game | Steam app |
|---|---|
| Call of Duty (2003) | 2620 |
| Call of Duty: United Offensive | 2640 |
| Call of Duty 2 | 2630 |
| Call of Duty 4: Modern Warfare (2007) | 7940 |
| Call of Duty: World at War | 10090 |
| Call of Duty: Modern Warfare 2 (2009) | 10180, Multiplayer 10190 |
| Call of Duty: Modern Warfare 3 (2011) | 42680, Multiplayer 42690 |
| Call of Duty: Black Ops | 42700, Multiplayer 42710 |
| Call of Duty: Black Ops II | 202970, Multiplayer 202990, Zombies 212910 |
| Call of Duty: Ghosts | 209160, Multiplayer 209170 |
| Call of Duty: Advanced Warfare | 209650, Multiplayer 209660 |
| Call of Duty: Black Ops III | 311210 |
| Call of Duty: Infinite Warfare | 292730 |
| Call of Duty: Modern Warfare Remastered (2017) | 393080, Multiplayer 393100 |
| Call of Duty: WWII | 476600, Multiplayer 476620 |

Any other installed Steam app with *Call of Duty* in its name is listed as well, such as the Black
Ops III Mod Tools.

The built-in list covers every game in the table except Infinite Warfare, Modern Warfare Remastered and
WWII, and it covers the Black Ops III Mod Tools. For those three, the versions come from this PC and
from SteamDB.

Modern Warfare (2019), Black Ops Cold War, Vanguard, Modern Warfare II, Modern Warfare III, Black Ops 6
and the Call of Duty app (Black Ops 7, Warzone) are listed but not downloadable. They need Activision's
servers, and those only accept the current version.

## Options

```
CODDowngrader --list                     everything it can see, changing nothing
CODDowngrader --game 311210              open one game straight away
CODDowngrader --steam "D:\Steam"         use this Steam folder
CODDowngrader --depotdownloader <path>   use this DepotDownloader instead of fetching one
CODDowngrader --no-color                 plain output
```

`--list` prints Steam's folder, its libraries, every installed Call of Duty with any build put into it,
and every build known for it, with each manifest that differs from the install and where it comes from.
It is the thing to paste when asking for help.

## Files

| Path | Holds |
|---|---|
| `%LOCALAPPDATA%\COD Downgrader\tools\` | DepotDownloader, fetched on first use |
| `%LOCALAPPDATA%\COD Downgrader\settings.json` | the Steam account name and the last folder used |
| `%LOCALAPPDATA%\COD Downgrader\logs\` | DepotDownloader's full output for every download |
| `%LOCALAPPDATA%\COD Downgrader\manifests\` | file lists DepotDownloader fetched, so none is fetched twice |
| `%LOCALAPPDATA%\COD Downgrader\applied\` | what was put into each installed game, for Undo |
| `COD Downgrader\Staging\` in a Steam library | files on their way into a game |
| `COD Downgrader\Backups\` in a Steam library | the files they replaced, when kept |

## DepotDownloader

Downloads are done by [DepotDownloader](https://github.com/SteamRE/DepotDownloader) by SteamRE. COD
Downgrader fetches version 3.4.0 from its GitHub release the first time a download needs it, and
extracts it only if the zip's SHA-256 matches the hash published for that release in
[winget-pkgs](https://github.com/microsoft/winget-pkgs).

## Building from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and Python 3 for the
packaging script.

```
dotnet test tests/CODDowngrader.Tests
python tools/build.py
```

`tools/build.py` runs the tests, publishes `CODDowngrader.exe` as one self-contained file for win-x64,
and writes the release zip and its `SHA256SUMS` to `dist/v<version>/`.

### Updating the built-in list

The list is `src/CODDowngrader/Catalog/manifests.txt`, built into the exe. After an update, copy the
rows of each changed depot from `steamdb.info/depot/<depot>/manifests/` into a text file, laid out as
`tools/catalog.py` describes, and merge them:

```
python tools/catalog.py add rows.txt
```

Pages saved from the browser work as well. Save an app's Depots page, or a depot's Manifests page, into a
folder and run:

```
python tools/catalog.py pages <folder> --write
```

It takes the manifest rows, each depot's name and owning app, and the depots a download of a game leaves
out, such as low-violence content.

## Changelog

### v1.0.2

- Patch folders between any two builds: saving one asks which build it starts from, the latest on Steam,
  the build installed, or any other known build, and a patch can go up to the latest build as well.
- Part of a build: putting a build into the game, saving a patch folder and applying a folder ask which of
  the files that differ go in. Everything, only the content with your exes and DLLs kept, only the exes and
  DLLs, or folders and files picked from a list. Map packs and other DLC can go back to an earlier version
  while the game keeps its current exe.
- Where the chosen build has the same exe Steam personalized for your account in the installed game, COD
  Downgrader asks whether to use that copy or Steam's original: when putting the build into the game, when
  applying a downloaded build, and at the end of a download into a folder.
- A download of a game that is not installed leaves out content Steam does not install with the game: the
  low-violence versions of Call of Duty, Call of Duty 2, World at War, Modern Warfare 2 and Black Ops, and
  Call of Duty 4's German depots. Modern Warfare 2's alone is 7.25 GB.
- Depots are shown by name.
- The built-in list's depots match SteamDB's: United Offensive takes its English content and the Call of Duty
  content it runs on instead of a Mac depot, Advanced Warfare Multiplayer takes its four DLC maps' English
  content, and Ghosts' and Advanced Warfare's shared content belongs to their Multiplayer apps.

### v1.0.1

- Modern Warfare 2's builds from before 3 Sep 2026 download complete. Their English content names its
  files the old way, `.\main\...`, and a download that started from the installed copy deleted 120 of
  those files, 6.8 GB, once DepotDownloader had finished. Putting that build into the installed game, or
  applying a patch of it, removed the same files straight after writing them. Choosing the same version
  and folder again fetches what is missing.
- Exes Steam personalizes for each account when it installs a game, such as Black Ops II's, are no longer
  reported as replaced by a mod or a client.
- A patch folder no longer keeps DepotDownloader's file list beside the patch.

### v1.0.0

First release.

## License

MIT, see [LICENSE](LICENSE). `CODDowngrader.exe` contains the .NET runtime and Spectre.Console, both
MIT; their notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). DepotDownloader is GPL-2.0
and is not part of this download.

COD Downgrader is not affiliated with or endorsed by Activision, Valve or SteamDB. Call of Duty is a
trademark of Activision Publishing, Inc.; Steam is a trademark of Valve Corporation. COD Downgrader
contains no game files: every file it downloads comes from Steam, to an account that owns the game.
