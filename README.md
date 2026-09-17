# COD Downgrader

Download any build of a Call of Duty you own on Steam, into your installed game or into a folder of its own.

By [Xep](https://github.com/Xeptix).

When a Call of Duty update breaks a community client or a mod, the build from before the update is
still on Steam's servers. COD Downgrader finds out which build that was, and either puts it into your
installed game, downloading only the files that differ, or downloads the whole game at that build into a
folder of its own, which Steam never touches and never updates. It runs as a window, as menus in a
console, or one command at a time for scripts and other programs.

> **For Steam owners only.** COD Downgrader downloads through your own Steam account, and Steam only
> sends a game's files to an account that owns the game. Every Call of Duty you download with it has
> to be owned by the Steam account you sign in with. Copies from Battle.net, the Microsoft Store or
> Game Pass cannot be downloaded with it.

## Download

Get `COD-DG-v<version>-win-x64.zip` from the [latest release](https://github.com/Xeptix/COD-DG/releases/latest),
extract it anywhere and run `CODDowngrader.exe`. Keep `CODDowngrader.com` beside it: it is what runs when you
type `CODDowngrader` in a terminal.

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

1. **Pick a game** on the left. Installed Call of Duty games come first; every other Call of Duty on
   Steam is under *Not installed*, and those have to be owned by the account too. **Find a game** above the
   list narrows it by name or app ID.
2. **Pick a version.**
   - **Before the update of *date*** is a build Steam replaced, from the built-in list or put back
     together from what Steam left on this PC.
   - **Installed now** is the build on disk. It is worth taking when Steam has an update queued and
     the files are still the old build.
   - **Latest on Steam** is the current build, shown when it is newer than what is installed.
   - **Newest build on the built-in list** stands in for it when Steam on this PC has no product info
     for the game.
   - **Go back to a date**: pick a day and **Find that build**. See
     [Going back to a date](#going-back-to-a-date).
   - **Enter manifest IDs** gets any other build from SteamDB, and **Open a build** takes one someone
     sent you. Both are under *Not in the list?*, with going back to a date.
3. **Choose what to do with it.**
   - **Put it into the game** downloads only the files that differ from what is installed, and swaps
     them in. See [Into the installed game](#into-the-installed-game).
   - **Download into a folder of its own** leaves the installed game as it is. The first download goes into
     `COD Downgrader` inside the game's Steam library, beside `steamapps`, and later ones default to wherever
     the last one went; anywhere outside `steamapps` works. A download can be in any language Steam has for the
     game. See [What ends up in the folder](#what-ends-up-in-the-folder) and [Languages](#languages).
   - **Save a patch folder** downloads only the files that differ from another build, to put into the
     game later. See [Patch folders](#patch-folders).
4. COD Downgrader works out **what changes** and shows it before anything happens: how many files, how
   big, and anything worth knowing, such as files a mod has replaced. Choose which files go in, and the
   rest of the options, then press the button that says what happens: **Put it into the game**,
   **Download** or **Save the patch folder**.
5. **Sign in** to Steam with the account that owns the game, the first time. See
   [Signing in](#signing-in).
6. When it has finished, **Open the folder** shows where the files went: the download or patch folder, or
   the game's own folder, and **Share** hands the build to someone else. See
   [Sharing a build](#sharing-a-build).

The game's page shows a build written into it, with **Undo the downgrade**, and **Downgrade again** once
Steam has put its own files back. Putting a build into the game, applying a folder and undoing all start on a
page that shows what the game has now, what it will have after, and each depot that changes, before anything
does.

A download or a change to a game carries on while you look at other games, and several games can have one going
at once. The game in the list shows how far its job has got, and choosing it again shows the job; closing the window
while one is going asks first. Two games that share a folder, such as Black Ops II's Multiplayer and Zombies, are
changed one at a time.

**Your builds**, below the games, lists every downgrade, download and patch folder made on this PC, newest
first, with where each one is and whether it is still there, and **Share** on each. A download or a patch folder
that is still there has **Put it into the game**, and the downgrade in a game now has **Undo it**, each with
that same page first. **Find builds in a folder** adds the downloads and patch folders COD Downgrader made in a
folder you choose and the folders under it, such as ones made before the list existed or on another PC, and
changes nothing in them. **Open a build** is for
builds that are not in a game's list: a shared build someone sent you, and a patch folder or a downloaded build
you already have. Choose the folder and it says which game and build it holds, with **Put it into the game**
and **Share it**. Esc, or the mouse's back button, goes back a page.

### Three ways to run it

- **The window**: `CODDowngrader.exe`.
- **The menus in a console**: `CODDowngrader cli`. The same steps as questions, answered with the arrow
  keys.
- **One command at a time**: `CODDowngrader <command>`, for scripts and other programs. See
  [Command line](#command-line).

## Going back to a date

**Go back to a date** takes a day and finds the build the game had at the end of it. When the builds known
here reach that far back, that build is chosen, named as the list names it.

When they do not, because the date is older than every update known here or some depots' manifests from
then are not known, COD Downgrader shows each depot it needs, with its SteamDB page:

1. **Open on SteamDB** opens `steamdb.info/depot/<depot>/manifests/`.
2. Select the rows of the manifests table there, or just the manifest ID you want, and copy them.
3. Paste into that depot's box. From rows with dates, COD Downgrader takes the newest manifest SteamDB
   first saw on or before your date, and says which one it took.

**Copy all links** puts every page on the clipboard at once. **Continue** makes that build the chosen
version. [SteamDB](https://steamdb.info) does not allow automated access, so COD Downgrader never reads it:
the pages open in your browser, and what you paste is all it sees.

## Versions from SteamDB

[SteamDB](https://steamdb.info) records every manifest of every depot, going back years. It is where to
get a build the built-in list does not have, or a depot it does not cover, such as a language.

**Enter manifest IDs** lists every depot of the game with its SteamDB page. Paste into the
depots you want on another build; each depot left empty keeps the build it is on, so only the depots that
changed need an ID. A box takes the rows of SteamDB's table, a manifest ID alone, or the depot and manifest
in either of these forms:

```
311211 7651791086710252932
download_depot 311210 311211 7651791086710252932
```

In the menus, **Paste manifest IDs** takes the same lines, one per line, and **Pick a known manifest** picks
one for a single depot from the built-in list, what is remembered, the manifest cache and the content log.

## Remembered manifests

COD Downgrader keeps every manifest it learns on this PC: the ones Steam has installed, cached, logged and
lists as latest, and every one you paste or name. Steam deletes its manifest cache and rotates its log, and
the builds those told of stay known here. Manifests pasted from SteamDB's table keep SteamDB's dates, so
they build history the way the built-in list does, which is how Infinite Warfare, Modern Warfare
Remastered and WWII get builds before their updates.

They are in `remembered.txt` in COD Downgrader's folder (see [Files](#files)). **Settings** shows how many
there are and forgets them. Started with `--no-remember`, COD Downgrader remembers nothing new that run,
whichever way it runs: `CODDowngrader --no-remember`, `CODDowngrader cli --no-remember`, or on any
command. What is remembered already is still used.

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

COD Downgrader puts these, and what it remembers, together into whole builds. It works back from the
installed build one update at a time, and each step only reverts the depots that update changed, so a
version never mixes a depot from one build with a depot from another. A build that both this PC and the
built-in list name is shown once, with the list's date.

## Into the installed game

**Put it into the game** turns the Steam install itself into the chosen build, downloading only what
differs between the two:

1. Every file in a Steam manifest carries a SHA-1, so COD Downgrader compares the installed build's file
   list with the chosen build's and knows exactly which files differ. When Steam on this PC no longer
   has a file list, DepotDownloader fetches just that list first.
2. It reads those files in the game. A file that is not the installed build's version, as when a mod or
   a client has replaced it, is listed before anything changes.
3. DepotDownloader downloads only the files that differ, into `COD Downgrader\Staging` in the game's
   Steam library, and every one is checked against the build.
4. With the game closed, they are swapped in, and files the chosen build does not have are removed.
   The files this replaces can be kept in `COD Downgrader\Backups`, so **Undo the downgrade** puts them
   back without downloading. The staging folder is then deleted.

Undo puts each file back from the backup. Any file the backup does not hold, because none was kept or it has
gone, is downloaded from Steam at the build Steam has installed: the patch run the other way. Files the build
added are deleted, and files Steam has put back since stay as they are. The page before it says which files come
from where.

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
Where the chosen build has the same exe as the installed one, you choose whether to keep your
personalized copy or put Steam's original in its place.

You also choose which of the files that differ go in:

- **Everything that differs**: the whole build.
- **Only the content**: maps, fastfiles and every other file, keeping your installed exes and DLLs.
- **Only the exes and DLLs**, keeping your installed content.
- **Choose folders and files**: a checklist of every file that differs, grouped by folder, where a whole
  folder is picked at once.

The game's page names the part written after the build, and Undo takes it out the same way.

This is how a map pack or other DLC goes back to an earlier version while the game keeps its current exe.
Most map packs changed after release: Black Ops II's and Black Ops III's DLC went through many versions,
and Modern Warfare 2's Stimulus and Resurgence packs, Modern Warfare 3's Collections, and the map packs of
Ghosts and Advanced Warfare changed again in August and September 2026. Modern Warfare 2's and Modern
Warfare 3's content from before 3 Sep 2026, map packs included, is in the same fastfile format as the
current builds.

Steam still lists its latest build for the game. **Verify integrity of game files**, or Steam's next
update of the game, brings latest files back, and a game with an update queued in Steam gets them when
Steam installs it. The game's page shows when Steam has changed the files since, and offers **Downgrade
again**.

## Patch folders

**Save a patch folder** makes a minimal build: only the files that differ from another build, with
`COD Downgrader patch.json` naming both builds, every file's SHA-1 and the files the patch removes. You
choose the build the patch starts from: the latest on Steam, the build installed in the game, or any
other known build. The chosen version can be newer than that one, so a patch from an older build to the
latest takes a downgraded game back up without Steam. With one kept, the game can be downgraded again
without downloading after Verify integrity of game files has put its latest files back. A patch can hold
only part of a build, the same way as putting a build into the game.

**Open a build** puts a patch folder into its installed game the same way, after
checking every file in the folder. A patch made from a different build than the game is on is flagged
first. It takes a folder a whole build was downloaded into as well, lets you choose which of its files go
in, and can delete that folder afterwards.

## Languages

A download into a folder of its own can be in any language Steam has for the game. **Language** on the download
page lists them with their flags, starting on the one Steam downloads for you: the language the game is installed
in, or else the Steam client's language when the game has it, or else English. Only the language depots change
(the game's text and speech); the rest of the build is the same. The folder's name says which language it holds.

Steam's product info names the manifest each language's depots have today. In a build where the game's own
language depots have today's manifest, the other language's depots take today's manifest too. For an older build
they come from SteamDB: the page says so and **Get them from SteamDB** opens the depots to paste, each with the
moment to take its manifest at, the newest first seen before the update that replaced the build. What is pasted
is remembered.

A build in another language stays in its folder: **Put it into the game** and **Save a patch folder** work in the
language the game is installed in, and applying a download in another language to the game is refused. To play
the game in another language in place, change its language in Steam.

On the command line, `download <game> --language german` downloads a build in German, and
`builds <game> --language german` lists the builds with what each needs from SteamDB. `builds --json` lists each
game's languages, the one it is in, and the one Steam downloads for you.

## Sharing a build

A downgrade, a patch or a download you made can go to anyone else who owns the game. **Share** at the end of
one or on it in **Your builds**, **Share this downgrade** on the game's page, **Share this build** for the chosen
version, and **Share it** for a patch folder or a downloaded build in **Open a build** each give a few lines of
text to copy into a message or save as a file:

```
COD Downgrader shared build
game 202990 Call of Duty: Black Ops II - Multiplayer
title Before the update of 10 Feb 2015
manifest 202991=8255716060272897409
manifest 202992=6516382015411692772
only binaries
```

It names the game, the manifest of each of its depots (one line each), the files that were chosen, and
whether the game sharing the folder went along, and a `language` line for a download in another language. It holds no game files. Whoever you send it to chooses **Open a
build**, pastes it or opens the file, and has that build chosen on the game's page with the same files ticked. From there it is theirs to put into the game, download or save as a patch, and every file comes from
Steam to their own account, so it only works for someone who owns the game.

A build their PC already knows keeps the name it has there. Depots their copy of the game does not have are
left out, and depots the shared build does not name keep the build they are on; the page says when either
happens. In the menus, **Paste manifest IDs** takes a shared build too.

## Signing in

DepotDownloader signs in to Steam itself, in a console of its own:

- **QR code**: scan it with the Steam mobile app and approve.
- **Account name and password**: the password and the Steam Guard code are typed into DepotDownloader's
  own prompt.

From the window, and from a command, the sign-in opens in a window of its own the
first time it is needed, and again if Steam stops accepting the saved one, and COD Downgrader carries on once
it is done. Downloads themselves run out of sight. In the menus it happens in the same
console. **Settings** signs in, or signs in with another account, whenever you like.

COD Downgrader never sees a password, and neither does any program that runs COD Downgrader. DepotDownloader
keeps the login, so this happens once. If Steam drops DepotDownloader's connection while the QR code is on
screen and the sign-in is lost after you approve it, COD Downgrader starts the download again with the
saved login.

## Starting from your installed copy

For an installed game, a download into a folder of its own can start from your installed copy: the files
the chosen build shares with your install are copied into the new folder first. When Steam still has both
builds' manifests, only files whose contents are identical are copied. DepotDownloader then checks every
file already in the folder piece by piece and downloads only the pieces that differ. When only the
installed build's file list is known, its files are copied, and the ones the chosen build does not have
are removed once the download has finished.

Exes Steam personalizes for your account are never copied: DepotDownloader downloads Steam's originals.
Where your installed game has a personalized copy of the same exe, you choose whether the folder gets that
copy or keeps Steam's original. Applying the folder to the game later accepts either.

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
where it stopped.

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
WWII, and it covers the Black Ops III Mod Tools. For those three, the versions come from this PC, from
what is remembered, and from SteamDB.

Modern Warfare (2019), Black Ops Cold War, Vanguard, Modern Warfare II, Modern Warfare III, Black Ops 6
and the Call of Duty app (Black Ops 7, Warzone) are listed but not downloadable. They need Activision's
servers, and those only accept the current version.

## Command line

Give COD Downgrader a command and it asks nothing, does that one thing and stops, which is what a script
or another program uses. On Windows, type `CODDowngrader`, not `CODDowngrader.exe`: that runs
`CODDowngrader.com`, which the terminal waits for.

```
CODDowngrader list                          Steam, the games, and every build known for them
CODDowngrader builds 202990                 the builds of one game, and what to call each one
CODDowngrader builds 202990 --at 2015-03-12 which build the game had on that day
CODDowngrader status                        what is installed, and any build written into it
CODDowngrader download 311210 --build 2026-09-10 --to "X:\BO3 old"
CODDowngrader ingame 202990 --build 2026-09-10 --only content
CODDowngrader ingame 202990 --at 2015-03-12 --plan
CODDowngrader patch 202990 --build 2026-09-10 --from latest --to "X:\BO2 patch"
CODDowngrader apply 202990 --from "X:\BO2 patch"
CODDowngrader undo 202990
CODDowngrader download 311210 --build latest --language french
CODDowngrader history                       every download, downgrade and patch folder made here
CODDowngrader history --find "B:\COD"      and first, the ones in a folder that are not on the list yet
CODDowngrader share 202990 --build 2015-02-10 --only binaries --to "X:\shared"
CODDowngrader ingame --shared "X:\shared\COD Downgrader build - Call of Duty - Black Ops II - Multiplayer - Before the update of 10 Feb 2015.txt"
CODDowngrader login
CODDowngrader export "X:\exports"
```

`CODDowngrader help` lists them all.

**Naming a game**: its Steam app ID, or part of its name, so `202990` and `"black ops ii - multi"` are the
same game. **Naming a build**: `--build 2026-09-10` is the build before that day's update, the same way the
window and the menus name it; `--build latest` and `--build installed` are what Steam has now and what is in
the game folder now. `--at 2015-03-12` is the build the game had at the end of that day, or at a time with
`--at 2015-03-12T18:30`. `CODDowngrader builds <game>` prints the name to use for every build it knows:

```
Call of Duty: Black Ops II - Multiplayer [202990]
  latest           Latest on Steam (build 24784266), queued but not installed
  installed        Installed now (build 515837)
  2015-02-10       Before the update of 10 Feb 2015 · 1 depot differs
```

For a build the list does not have, name its depots instead: `--manifest 311211=9084453472036406216`,
once per depot. Every depot not named keeps the build it is on. When `--at` names a day the builds known
here do not reach, the command lists each depot it needs with its SteamDB page, and `--manifest` fills them
in. `CODDowngrader builds <game> --json` lists every manifest known of each depot, with when it was built,
when Steam fetched it, when SteamDB first saw it and whether it is only remembered.

| Option | What it does |
|---|---|
| `--to <folder>` | Where a download, a patch, an export or a shared build goes. Left out, a download or a patch goes where the last one went |
| `--from <build\|folder>` | `patch`: the build it starts from. `apply`: the folder to take |
| `--language <language>` | `download`, `builds`: the build in another language Steam has for the game, by Steam's name for it: `english`, `french`, `german`, `spanish`, `italian`, `russian`, `polish`, `japanese`, `brazilian`, `schinese` and so on. `builds --json` lists each game's |
| `--find <folder>` | `history`: first add the downloads and patch folders COD Downgrader made in that folder and the folders under it |
| `--only <what>` | `all` (the default), `content` (keep your exes and DLLs), or `binaries` |
| `--files <name,name>` | Only these files of the build, instead of `--only` |
| `--shared <file>` | `download`, `ingame`, `patch`: the build a shared build names, with the files and options it came with. `-` reads it from standard input. `--only` and `--files` still choose the part |
| `--exe <steam\|installed>` | Which copy of an exe Steam personalizes for your account: `steam` puts Steam's original in, `installed` takes the copy from your installed game. Left out, a download keeps Steam's original and the installed game keeps its own copy |
| `--backup <yes\|no>` | Keep the files a downgrade replaces, so `undo` can put them back. `yes` unless told otherwise |
| `--plan` | Work out and print what would change, file by file with `--json`, and change nothing. `undo` too |
| `--again` | `ingame`, `apply`: take the build already written in out first, then put this one in |
| `--siblings` | `ingame`: take a game sharing the folder back to its build from the same time as well |
| `--no-seed` | `download`: fetch everything instead of copying what the installed game already has |
| `--delete` | `apply`: delete the folder once the game has what it needs from it |
| `--yes` | Take the usual answer to anything that would be a question, including going ahead when the drive looks too full |
| `--json` | One JSON object of what happened, instead of the human report |
| `--login <auto\|saved\|window\|never>` | How to sign in, see below |
| `--username <account>` | Sign in as this account instead of the last one used |
| `--no-remember` | Remember nothing new this run: no manifests, and nothing added to `history` |
| `--steam <folder>` | Use this Steam folder instead of looking for one |
| `--depotdownloader <path>` | Use this DepotDownloader instead of fetching one |
| `--no-color` | Plain output |

A command works on the game it is given. Where two Call of Duty apps share a folder, as Black Ops II's
Multiplayer and Zombies do, `ingame --siblings` takes each of them back to its own build from the same time;
without it the other app is left alone and the command says so.

Steam puts its own files back when it verifies or updates a game. `ingame --again` writes the build in again
over that, taking the one recorded in the folder out first.

`share` prints a shared build, or saves it with `--to`: the build `--build`, `--at` or `--manifest` names, with
`--only`, `--files` and `--siblings`; a patch folder or a downloaded build with `--from`; and with none of those,
the downgrade written into the game. `history` lists what has been made, newest first, with where each build is
now; with `--json`, each carries its shared build too. `CODDowngrader --list` and `--export` still do
what they always did.

### Signing in from a command

Steam will not send a game's files to nobody, so anything that downloads needs an account. DepotDownloader
signs in and keeps the sign-in, so this only happens once.

**`CODDowngrader login`** signs in, in the terminal it runs in. When a command has to download and no account
is signed in, it opens a sign-in window itself, waits for it, and carries on. A program that runs COD
Downgrader therefore never has to handle a sign-in: the only thing the person sees is DepotDownloader's own
prompt, and the password or Steam Guard code goes into DepotDownloader and nothing else. COD Downgrader has
no `--password` option and refuses one.

`--login` decides what a command may do about it: `auto` uses the account that is signed in and opens a
window when there is none, `saved` never opens a window, `window` always opens one, and `never` fails
instead of signing in.

### What a program reads

`--json` prints one object, whether the command worked or not:

```json
{
  "tool": "COD Downgrader 1.1.0",
  "command": "ingame",
  "ok": true,
  "exitCode": 0,
  "game": { "app": 202990, "name": "Call of Duty: Black Ops II - Multiplayer" },
  "build": { "key": "2015-02-10", "title": "Before the update of 10 Feb 2015", "part": "content only" },
  "folder": "X:\\Steam\\steamapps\\common\\Call of Duty Black Ops II",
  "written": 3,
  "removed": 0,
  "backup": "X:\\Steam\\COD Downgrader\\Backups\\Call of Duty Black Ops II 2026-09-16 011333",
  "shared": "COD Downgrader shared build\ngame 202990 Call of Duty: Black Ops II - Multiplayer\n..."
}
```

`download`, `ingame`, `patch` and `apply` carry `shared`, the shared build of what they made.

With `--plan`, the object carries a `plan`: every file that would be written with its depot, size and
whether it is already in place or an exe Steam personalizes, what would be removed, files a mod has
replaced, files in use, and the games sharing the folder. For `undo` the plan lists the files coming back from
the backup, from Steam, deleted, kept because Steam has put them back, and any that cannot come back.
`ingame`, `apply` and `undo` carry `change` as well: what the game holds now, what it will hold, and each depot
whose manifest changes. A command that did not work says why in the same
shape: `"ok": false` and `"error": { "code": "signin", "message": "..." }`. The codes are words to switch
on, such as `signin`, `locked`, `not-owned`, `no-build`, `needs-manifests`, `already-applied` and
`incomplete`; `needs-manifests` comes with a `needs` list of the depots and their SteamDB pages, and
`neededBefore`, the moment each manifest is taken at.

| Exit code | Meaning |
|---|---|
| 0 | Done |
| 1 | Something went wrong |
| 2 | The command line itself: an unknown command, a missing option, a build that names nothing |
| 3 | A sign-in is needed |
| 4 | A file the command has to write is in use, usually because the game is running |
| 5 | The signed-in account does not own something the command needs |

## Files

COD Downgrader's own folder is `%LOCALAPPDATA%\COD Downgrader`.

| Path | Holds |
|---|---|
| `tools/` | DepotDownloader, fetched on first use |
| `settings.json` | the Steam account name and the last folder used |
| `remembered.txt` | every manifest remembered on this PC |
| `builds.json` | every download, downgrade and patch folder made on this PC, for Your builds |
| `logs/` | DepotDownloader's full output for every download |
| `manifests/` | file lists DepotDownloader fetched, so none is fetched twice |
| `applied/` | what was put into each installed game, for Undo |
| `COD Downgrader/Staging/` in a Steam library | files on their way into a game |
| `COD Downgrader/Backups/` in a Steam library | the files they replaced, when kept |

## DepotDownloader

Downloads are done by [DepotDownloader](https://github.com/SteamRE/DepotDownloader) by SteamRE. COD
Downgrader fetches version 3.4.0 from its GitHub release the first time a download needs it, and
extracts it only if the zip's SHA-256 matches the hash published for that release in
[winget-pkgs](https://github.com/microsoft/winget-pkgs).

## Building from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Python 3 for the packaging
script, and Visual Studio's C++ build tools, which `CODDowngrader.com` is compiled with.

```
dotnet test tests/CODDowngrader.Tests
python tools/build.py
```

`tools/build.py` runs the tests, publishes the program as one self-contained file for win-x64 beside
`CODDowngrader.com`, and writes the release zip and its `SHA256SUMS` to `dist/v<version>/`.

- `src/CODDowngrader.Core` is everything but the window: Steam, the builds, downloading, patching, the menus
  and the command line.
- `src/CODDowngrader` is the window, and the program's entry point.
- `src/CODDowngrader.Stub` is `CODDowngrader.com`.
- `tools/GuiShots` draws every page of the window to a PNG from this PC's Steam data:
  `dotnet run --project tools/GuiShots -- <folder>`.
- `tools/make_icon.py` and `tools/make_flags.py` draw the icon and the language flags (Pillow).

### Updating the built-in list

The list is `src/CODDowngrader.Core/Catalog/manifests.txt`, built into the program. After an update, copy the
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

### v1.1.3

- Downloads in any language Steam has for the game, chosen from a list with flags that starts on the language Steam
  downloads for you. Language depots an older build needs from SteamDB are asked for on the download page. The
  command line has it as `--language`, and a shared build carries the language.
- **Find builds in a folder** in Your builds adds the downloads and patch folders COD Downgrader made in a folder and
  the folders under it; `history --find <folder>` on the command line.

### v1.1.2

- Downloads and downgrades keep going while you look at other games, several games can have one going at once, and the
  list of games shows each one's progress. Choosing a game shows its job again, and closing the window while one is
  going asks first.

### v1.1.1

- The window and the command line suggest the folder the last download or patch went into, as the menus always have.

### v1.1.0

- **A window.** Pick a game, pick a version, see what changes before anything does, choose, and start, with
  progress, Cancel, and the full log a click away. Undo, Downgrade again, applying a folder, part of a
  build, the personalized exe, games sharing a folder and signing in are all there. The menus are
  `CODDowngrader cli`, and every command works as before.
- **Going back to a date**: the build a game had on a day you pick. Where that is not known here, COD
  Downgrader shows each depot's SteamDB page, takes the rows you paste, and picks the manifest for that day.
  The command line has it as `--at`.
- **Remembered manifests**: every manifest COD Downgrader learns on this PC is kept, so builds stay known
  after Steam deletes its own records, and pasted SteamDB rows give Infinite Warfare, Modern Warfare
  Remastered and WWII their history. `--no-remember` turns that off for a run.
- **Sharing a build**: a downgrade, a patch or a download becomes a few lines of text to send to someone who
  owns the game, and **Open a build** gets them the same build, with the same files chosen, from Steam.
  The command line has `share` and `--shared`.
- **Your builds** lists every downgrade, download and patch folder made on this PC, to find again or share;
  the command line has it as `history`.
- **Undo without a backup**: files the backup does not hold come back from Steam, at the build Steam has
  installed, and every change to a game starts on a page of what it has now and what it will have.
- **`--plan`** works out what a command would change and changes nothing.
- `CODDowngrader.com` sits beside `CODDowngrader.exe`: typing `CODDowngrader` in a terminal runs it, so the
  terminal waits and gets the exit code, and a double-click on the exe opens only the window.

### v1.0.3

- Commands for everything, so a script or another program can drive it: `list`, `builds`, `status`,
  `download`, `ingame`, `patch`, `apply`, `undo`, `login` and `export`. `CODDowngrader help` lists them.
- `--json` gives a program one object per command, and every command ends with an exit code that says what
  happened.
- `CODDowngrader login` signs in on its own, and a command that needs an account opens that window itself.

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

MIT, see [LICENSE](LICENSE). The program contains the .NET runtime, Spectre.Console, Avalonia, SkiaSharp and
HarfBuzzSharp, all MIT, and on Windows ANGLE, BSD; their notices are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). DepotDownloader is GPL-2.0 and is not part of this download.

COD Downgrader is not affiliated with or endorsed by Activision, Valve or SteamDB. Call of Duty is a
trademark of Activision Publishing, Inc.; Steam is a trademark of Valve Corporation. COD Downgrader
contains no game files: every file it downloads comes from Steam, to an account that owns the game.
