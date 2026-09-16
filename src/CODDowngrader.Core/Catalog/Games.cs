namespace CODDowngrader.Catalog;

public sealed record Game(uint AppId, string Name, string? NotDowngradable = null)
{
    public bool Downgradable => NotDowngradable is null;
}

/// <summary>
/// Every Call of Duty with its own Steam app. Multiplayer and Zombies are separate apps for the
/// older titles, exactly as Steam installs them. Anything else installed with "Call of Duty" in
/// its name is picked up at runtime, so a title missing here still works.
/// </summary>
public static class Games
{
    const string OnlineOnly = "Online-only: it needs Activision's servers, and they only accept the current version.";

    public static readonly IReadOnlyList<Game> All = new Game[]
    {
        new(2620, "Call of Duty (2003)"),
        new(2640, "Call of Duty: United Offensive"),
        new(2630, "Call of Duty 2"),
        new(7940, "Call of Duty 4: Modern Warfare (2007)"),
        new(10090, "Call of Duty: World at War"),
        new(10180, "Call of Duty: Modern Warfare 2 (2009)"),
        new(10190, "Call of Duty: Modern Warfare 2 (2009) - Multiplayer"),
        new(42680, "Call of Duty: Modern Warfare 3 (2011)"),
        new(42690, "Call of Duty: Modern Warfare 3 (2011) - Multiplayer"),
        new(42700, "Call of Duty: Black Ops"),
        new(42710, "Call of Duty: Black Ops - Multiplayer"),
        new(202970, "Call of Duty: Black Ops II"),
        new(202990, "Call of Duty: Black Ops II - Multiplayer"),
        new(212910, "Call of Duty: Black Ops II - Zombies"),
        new(209160, "Call of Duty: Ghosts"),
        new(209170, "Call of Duty: Ghosts - Multiplayer"),
        new(209650, "Call of Duty: Advanced Warfare"),
        new(209660, "Call of Duty: Advanced Warfare - Multiplayer"),
        new(311210, "Call of Duty: Black Ops III"),
        new(292730, "Call of Duty: Infinite Warfare"),
        new(393080, "Call of Duty: Modern Warfare Remastered (2017)"),
        new(393100, "Call of Duty: Modern Warfare Remastered - Multiplayer"),
        new(476600, "Call of Duty: WWII"),
        new(476620, "Call of Duty: WWII - Multiplayer"),
        new(2000950, "Call of Duty: Modern Warfare (2019)", OnlineOnly),
        new(1985810, "Call of Duty: Black Ops Cold War", OnlineOnly),
        new(1985820, "Call of Duty: Vanguard", OnlineOnly),
        new(3595230, "Call of Duty: Modern Warfare II", OnlineOnly),
        new(3595270, "Call of Duty: Modern Warfare III", OnlineOnly),
        new(4384550, "Call of Duty: Black Ops 6", OnlineOnly),
        new(1938090, "Call of Duty (Black Ops 7, Warzone)", OnlineOnly),
    };

    public static Game? Find(uint appId) => All.FirstOrDefault(g => g.AppId == appId);
}
