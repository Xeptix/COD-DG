using CODDowngrader.Catalog;
using CODDowngrader.Steam;

namespace CODDowngrader.Builds;

public enum BuildKind
{
    /// <summary>What Steam has published, when that is newer than what is installed.</summary>
    Latest,
    Installed,
    Previous,
}

/// <summary>What the time of an update is.</summary>
public enum UpdateSource
{
    /// <summary>Steam's content log: when this PC fetched the update.</summary>
    Log,

    /// <summary>The manifest cache alone: when the newest manifest of the update was built.</summary>
    Cache,

    /// <summary>The built-in manifest list: when SteamDB first saw the update.</summary>
    List,
}

public sealed record DepotChange(uint DepotId, ulong From, ulong To);

/// <summary>A set of depot changes that arrived together.</summary>
public sealed record UpdateEvent(DateTimeOffset? Time, UpdateSource Source, IReadOnlyList<DepotChange> Changes)
{
    public bool FromLog => Source == UpdateSource.Log;
}

/// <summary>
/// A manifest of a depot, with what is known of it: when this PC fetched it, when it was built (cached),
/// and when SteamDB first saw it (<paramref name="Listed"/>, from the built-in list).
/// </summary>
public sealed record KnownManifest(uint DepotId, ulong ManifestId, DateTimeOffset? FirstSeen, DateTimeOffset? Created, bool Installed, bool Latest, DateTimeOffset? Listed = null);

/// <param name="ListOnly">Nothing on this PC names this build; every manifest in it is from the built-in list.</param>
public sealed record Build(
    BuildKind Kind,
    IReadOnlyDictionary<uint, ulong> Manifests,
    IReadOnlyList<uint> Unknown,
    IReadOnlyList<uint> DifferentFromInstalled,
    UpdateEvent? ReplacedBy,
    DateTimeOffset? Built,
    bool ListOnly = false);

public sealed class BuildInput
{
    public required IReadOnlyCollection<uint> DepotIds { get; init; }
    public IReadOnlyDictionary<uint, ulong>? Installed { get; init; }
    public IReadOnlyDictionary<uint, ulong>? Latest { get; init; }
    public IReadOnlyList<CachedManifest> Cached { get; init; } = Array.Empty<CachedManifest>();

    /// <summary>Manifest fetches in the order Steam logged them.</summary>
    public IReadOnlyList<ManifestSeen> Seen { get; init; } = Array.Empty<ManifestSeen>();

    /// <summary>The built-in list's manifests of each depot, oldest first. Depots not in <see cref="DepotIds"/> are ignored.</summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<ListedManifest>>? Listed { get; init; }

    /// <summary>Updates in the list first seen before this are not used.</summary>
    public DateTimeOffset? ListedUpdatesFrom { get; init; }
}

public sealed record BuildHistoryResult(IReadOnlyList<Build> Builds, IReadOnlyDictionary<uint, IReadOnlyList<KnownManifest>> Depots);

/// <summary>
/// Works out which whole builds can be put back together from what Steam left on this machine and from
/// the built-in manifest list.
///
/// Each depot gets its own ordered history of manifests, consecutive entries are changes, and changes
/// that arrived together are one update. Starting from the newest state and undoing one update at a
/// time only ever reverts the depots that update touched, which is what keeps a depot from one build
/// out of a different one.
///
/// Ordering a depot's manifests on this machine: what Steam has published is newest, then what is
/// installed, then everything else. Among the rest, build times decide when both manifests are cached.
/// Otherwise the log decides, with one exception: while updating, Steam fetches the new manifest and
/// then, if the old one is not cached, the old one to compare against, so of two fetches moments apart
/// the second is the older manifest.
///
/// The built-in list is walked back the same way, its updates being the changes SteamDB first saw within
/// minutes of each other. The walk stops at the first build holding a manifest SteamDB only began
/// listing after the update being undone, because what that depot held before is not known. A build both
/// sources name is shown once, with the list's date; the list's other builds are placed around the ones
/// this machine names.
/// </summary>
public static class BuildHistory
{
    static readonly TimeSpan LogBurst = TimeSpan.FromMinutes(10);
    static readonly TimeSpan BuildWindow = TimeSpan.FromHours(12);
    static readonly TimeSpan ComparisonFetch = TimeSpan.FromMinutes(2);
    static readonly TimeSpan ListBurst = TimeSpan.FromMinutes(10);

    enum Source
    {
        /// <summary>Arrived before the log begins: known from the manifest cache or as the install.</summary>
        BeforeLog = 1,
        Logged = 2,
        /// <summary>Published but never fetched on this machine.</summary>
        Unfetched = 3,
    }

    sealed class Entry
    {
        public ulong Id;
        public DateTimeOffset? FirstSeen;
        public int SeenIndex = int.MaxValue;
        public DateTimeOffset? Created;
        public bool Installed;
        public bool Latest;
        public bool FetchedToCompare;

        public int Rank => Latest && !Installed ? 2 : Installed ? 1 : 0;

        public Source Source =>
            FirstSeen is not null && !FetchedToCompare ? Source.Logged
            : Latest && !Installed && Created is null && FirstSeen is null ? Source.Unfetched
            : Source.BeforeLog;

        /// <summary>When this manifest replaced the one before it, as far as this machine can tell.</summary>
        public DateTimeOffset? ArrivedAt => Source switch
        {
            Source.Logged => FirstSeen,
            Source.BeforeLog => Created,
            _ => null,
        };
    }

    sealed record ListedBuild(Dictionary<uint, ulong> Manifests, UpdateEvent? ReplacedBy);

    public static BuildHistoryResult Reconstruct(BuildInput input)
    {
        var depotIds = input.DepotIds.Distinct().ToList();
        var histories = depotIds.ToDictionary(d => d, d => LocalHistory(input, d));

        var created = new Dictionary<(uint, ulong), DateTimeOffset>();
        foreach (var cached in input.Cached) created.TryAdd((cached.DepotId, cached.ManifestId), cached.Created);

        var updates = GroupIntoUpdates(histories);
        var state = histories.Where(h => h.Value.Count > 0).ToDictionary(h => h.Key, h => h.Value[^1].Id);
        var builds = new List<Build>();

        void Emit(UpdateEvent? replacedBy)
        {
            if (state.Count == 0) return;
            var manifests = new Dictionary<uint, ulong>(state);
            if (builds.Any(b => Same(b.Manifests, manifests))) return;
            builds.Add(Make(input, depotIds, created, manifests, replacedBy, listOnly: false));
        }

        Emit(null);
        for (var i = updates.Count - 1; i >= 0; i--)
        {
            foreach (var change in updates[i].Changes) state[change.DepotId] = change.From;
            Emit(updates[i]);
        }

        var listed = new Dictionary<uint, IReadOnlyList<ListedManifest>>();
        foreach (var depot in depotIds)
            if (input.Listed is not null && input.Listed.TryGetValue(depot, out var rows) && rows.Count > 0) listed[depot] = rows;
        if (listed.Count > 0) builds = Merge(input, depotIds, created, builds, FromList(listed, input.ListedUpdatesFrom));

        return new BuildHistoryResult(builds, Known(histories, listed));
    }

    static List<Entry> LocalHistory(BuildInput input, uint depot)
    {
        var known = new Dictionary<ulong, Entry>();
        Entry Get(ulong id)
        {
            if (!known.TryGetValue(id, out var e)) known[id] = e = new Entry { Id = id };
            return e;
        }

        for (var i = 0; i < input.Seen.Count; i++)
        {
            var seen = input.Seen[i];
            if (seen.DepotId != depot || seen.ManifestId == 0) continue;
            var entry = Get(seen.ManifestId);
            if (entry.FirstSeen is null)
            {
                entry.FirstSeen = seen.Time;
                entry.SeenIndex = i;
            }
        }
        foreach (var cached in input.Cached)
        {
            if (cached.DepotId == depot && cached.ManifestId != 0) Get(cached.ManifestId).Created = cached.Created;
        }
        if (input.Installed is not null && input.Installed.TryGetValue(depot, out var installed) && installed != 0)
            Get(installed).Installed = true;
        if (input.Latest is not null && input.Latest.TryGetValue(depot, out var latest) && latest != 0)
            Get(latest).Latest = true;

        var history = Order(known.Values);
        for (var i = 0; i + 1 < history.Count; i++)
        {
            var older = history[i];
            var newer = history[i + 1];
            if (older.FirstSeen is { } a && newer.FirstSeen is { } b && older.SeenIndex > newer.SeenIndex && (a - b).Duration() <= ComparisonFetch)
                older.FetchedToCompare = true;
        }
        return history;
    }

    static Build Make(BuildInput input, IReadOnlyList<uint> depotIds, IReadOnlyDictionary<(uint, ulong), DateTimeOffset> created,
        Dictionary<uint, ulong> manifests, UpdateEvent? replacedBy, bool listOnly)
    {
        var isInstalled = input.Installed is { Count: > 0 } && Covers(manifests, input.Installed);
        var isLatest = replacedBy is null && input.Latest is { Count: > 0 } && Covers(manifests, input.Latest);
        var kind = isInstalled ? BuildKind.Installed : isLatest ? BuildKind.Latest : BuildKind.Previous;

        var unknown = depotIds.Where(d => !manifests.ContainsKey(d)).OrderBy(d => d).ToList();
        var different = input.Installed is null
            ? new List<uint>()
            : manifests.Where(m => input.Installed.TryGetValue(m.Key, out var i) && i != m.Value).Select(m => m.Key).OrderBy(d => d).ToList();

        // A build is as new as its newest manifest, so a date is only given when every manifest's is known.
        var dates = manifests.Select(m => created.TryGetValue((m.Key, m.Value), out var c) ? c : (DateTimeOffset?)null).ToList();
        var built = dates.All(d => d is not null) ? dates.Max() : null;

        return new Build(kind, manifests, unknown, different, replacedBy, built, listOnly);
    }

    /// <summary>Oldest first. An insertion sort, because the comparison is only meaningful pairwise and lists are a handful long.</summary>
    static List<Entry> Order(IEnumerable<Entry> entries)
    {
        var list = new List<Entry>();
        foreach (var entry in entries)
        {
            var at = list.Count;
            while (at > 0 && Compare(list[at - 1], entry) > 0) at--;
            list.Insert(at, entry);
        }
        return list;
    }

    static int Compare(Entry a, Entry b)
    {
        if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);

        if (a.Created is { } createdA && b.Created is { } createdB && createdA != createdB)
            return createdA.CompareTo(createdB);

        if (a.FirstSeen is { } seenA && b.FirstSeen is { } seenB)
        {
            // Fetched moments apart: the second fetch was the comparison, so it is the older manifest.
            if ((seenA - seenB).Duration() <= ComparisonFetch) return b.SeenIndex.CompareTo(a.SeenIndex);
            return seenA.CompareTo(seenB);
        }

        // Cached but never logged: fetched before the log began, unless it was built after the other was seen.
        if (a.Created is { } onlyCreatedA && b.FirstSeen is { } onlySeenB) return onlyCreatedA > onlySeenB ? 1 : -1;
        if (a.FirstSeen is { } onlySeenA && b.Created is { } onlyCreatedB) return onlyCreatedB > onlySeenA ? -1 : 1;

        return a.Id.CompareTo(b.Id);
    }

    static List<UpdateEvent> GroupIntoUpdates(Dictionary<uint, List<Entry>> histories)
    {
        var changes = new List<(Source Source, DateTimeOffset? Time, DepotChange Change)>();
        foreach (var (depot, history) in histories)
        {
            for (var i = 1; i < history.Count; i++)
            {
                var to = history[i];
                changes.Add((to.Source, to.ArrivedAt, new DepotChange(depot, history[i - 1].Id, to.Id)));
            }
        }

        var updates = new List<UpdateEvent>();
        foreach (var group in changes.GroupBy(c => c.Source).OrderBy(g => g.Key))
        {
            var logged = group.Key == Source.Logged;
            var window = logged ? LogBurst : BuildWindow;
            var source = logged ? UpdateSource.Log : UpdateSource.Cache;
            var current = new List<DepotChange>();
            DateTimeOffset? last = null;

            foreach (var (_, time, change) in group.OrderBy(c => c.Time ?? DateTimeOffset.MaxValue))
            {
                var split = current.Count > 0 && (
                    (time is null) != (last is null)
                    || (time is not null && last is not null && time.Value - last.Value > window)
                    || current.Any(c => c.DepotId == change.DepotId));

                if (split)
                {
                    updates.Add(new UpdateEvent(last, source, current));
                    current = new List<DepotChange>();
                }
                current.Add(change);
                last = time;
            }

            if (current.Count > 0) updates.Add(new UpdateEvent(last, source, current));
        }
        return updates;
    }

    /// <summary>
    /// The list's builds, newest first: the newest state, then the state before each update for as long as
    /// every depot's manifest is known. An update is every row first seen within minutes of the one before,
    /// a depot's first row included: a depot that first appears in an update was not part of the build before it.
    /// </summary>
    static List<ListedBuild> FromList(Dictionary<uint, IReadOnlyList<ListedManifest>> listed, DateTimeOffset? updatesFrom)
    {
        var firstSeen = new Dictionary<(uint, ulong), DateTimeOffset>();
        var rows = new List<(DateTimeOffset Time, uint Depot, int Index)>();
        var state = new Dictionary<uint, ulong>();
        foreach (var (depot, manifests) in listed)
        {
            for (var i = 0; i < manifests.Count; i++)
            {
                firstSeen[(depot, manifests[i].ManifestId)] = manifests[i].FirstSeen;
                rows.Add((manifests[i].FirstSeen, depot, i));
            }
            state[depot] = manifests[^1].ManifestId;
        }

        // A depot that changed twice within one update changed once: the state in between was never a build.
        var updates = new List<(DateTimeOffset Start, List<DepotChange> Changes)>();
        DateTimeOffset? last = null;
        foreach (var (time, depot, index) in rows.OrderBy(r => r.Time))
        {
            if (last is null || time - last.Value > ListBurst) updates.Add((time, new List<DepotChange>()));
            last = time;
            if (index == 0) continue;

            var row = listed[depot];
            var change = new DepotChange(depot, row[index - 1].ManifestId, row[index].ManifestId);
            var current = updates[^1].Changes;
            var same = current.FindIndex(c => c.DepotId == depot);
            if (same < 0) current.Add(change);
            else current[same] = current[same] with { To = change.To };
        }

        var builds = new List<ListedBuild> { new(new Dictionary<uint, ulong>(state), null) };
        for (var i = updates.Count - 1; i >= 0; i--)
        {
            var (start, changes) = updates[i];
            if (changes.Count == 0) continue;
            if (start < updatesFrom) break;
            foreach (var change in changes) state[change.DepotId] = change.From;
            if (state.Any(kv => firstSeen[(kv.Key, kv.Value)] >= start)) break;
            builds.Add(new ListedBuild(new Dictionary<uint, ulong>(state), new UpdateEvent(start, UpdateSource.List, changes)));
        }
        return builds;
    }

    /// <summary>
    /// One list of builds from both sources, newest first. A build of this machine's that agrees with a listed
    /// build on every depot both name is that build, with the list's date. The listed builds this machine
    /// never had go just before the first of its builds that is older.
    /// </summary>
    static List<Build> Merge(BuildInput input, IReadOnlyList<uint> depotIds, IReadOnlyDictionary<(uint, ulong), DateTimeOffset> created,
        List<Build> local, List<ListedBuild> listed)
    {
        var result = new List<Build>();
        var used = new bool[listed.Count];
        var next = 0;

        bool Matches(Build build, ListedBuild candidate)
        {
            var shared = 0;
            foreach (var (depot, manifest) in candidate.Manifests)
            {
                if (!build.Manifests.TryGetValue(depot, out var have)) continue;
                if (have != manifest) return false;
                shared++;
            }
            return shared > 0;
        }

        void Add(Dictionary<uint, ulong> manifests, UpdateEvent? replacedBy, bool listOnly)
        {
            if (!result.Any(b => Same(b.Manifests, manifests))) result.Add(Make(input, depotIds, created, manifests, replacedBy, listOnly));
        }

        void AddListedBefore(int end)
        {
            for (; next < end; next++)
            {
                if (used[next]) continue;
                used[next] = true;
                Add(new Dictionary<uint, ulong>(listed[next].Manifests), listed[next].ReplacedBy, listOnly: true);
            }
        }

        foreach (var build in local)
        {
            var match = Enumerable.Range(next, listed.Count - next).Concat(Enumerable.Range(0, next))
                .FirstOrDefault(j => !used[j] && Matches(build, listed[j]), -1);
            if (match < 0)
            {
                if (!result.Any(b => Same(b.Manifests, build.Manifests))) result.Add(build);
                continue;
            }

            AddListedBefore(match);
            next = Math.Max(next, match + 1);
            used[match] = true;

            var manifests = new Dictionary<uint, ulong>(build.Manifests);
            foreach (var (depot, manifest) in listed[match].Manifests) manifests.TryAdd(depot, manifest);
            Add(manifests, listed[match].ReplacedBy ?? build.ReplacedBy, listOnly: false);
        }
        AddListedBefore(listed.Count);
        return result;
    }

    /// <summary>Every manifest of each depot: the list's oldest first, then those only this machine knows, in its order.</summary>
    static Dictionary<uint, IReadOnlyList<KnownManifest>> Known(Dictionary<uint, List<Entry>> histories, IReadOnlyDictionary<uint, IReadOnlyList<ListedManifest>> listed)
    {
        var result = new Dictionary<uint, IReadOnlyList<KnownManifest>>();
        foreach (var (depot, history) in histories)
        {
            var rows = listed.GetValueOrDefault(depot) ?? Array.Empty<ListedManifest>();
            var list = new List<KnownManifest>();
            foreach (var row in rows)
            {
                var e = history.FirstOrDefault(x => x.Id == row.ManifestId);
                list.Add(new KnownManifest(depot, row.ManifestId, e?.FirstSeen, e?.Created, e?.Installed == true, e?.Latest == true, row.FirstSeen));
            }
            list.AddRange(history
                .Where(e => rows.All(r => r.ManifestId != e.Id))
                .Select(e => new KnownManifest(depot, e.Id, e.FirstSeen, e.Created, e.Installed, e.Latest)));
            result[depot] = list;
        }
        return result;
    }

    static bool Same(IReadOnlyDictionary<uint, ulong> a, IReadOnlyDictionary<uint, ulong> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>True when every depot in <paramref name="subset"/> is on the same manifest in <paramref name="state"/>.</summary>
    static bool Covers(IReadOnlyDictionary<uint, ulong> state, IReadOnlyDictionary<uint, ulong> subset) =>
        subset.All(kv => state.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
