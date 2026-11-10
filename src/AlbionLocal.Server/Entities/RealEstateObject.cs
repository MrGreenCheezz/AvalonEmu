using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// A real-estate plot of a city (NewRealEstate 191, client aus → cly; analysis/work/realestate/SPEC.md §2): the template's realestate tile,
/// bound to the landscape by its Guid and position (RealEstateObjectView.Initialize 0x181A764D0: an empty owner shows the free plot's sign,
/// RealEstatePlate.Unclaim; an owner shows its outline, RealEstateObjectView.Claim), with an AccessStatus after it as the official server sends
/// (owner / noaccess / noaccess). <see cref="Plot"/> null = free; <see cref="Reserved"/> = held by the city (a content/spawns station stands on
/// it: shown as owned by the city, never for sale). Landscape-bound like the exits: no Leave when it leaves the window (the captured plots were
/// sent again on re-entry without one).
/// </summary>
public sealed class RealEstateObject(long id, RealEstateSpawn spawn, string? reservedBy, Func<Guid, (Guid Id, string Name)?> guildOf) : WorldObject(id, spawn), IAccessStatusObject
{
    public new RealEstateSpawn Spawn { get; } = spawn;

    /// <summary>The plot's template Guid (key 1, the client's tile binding).</summary>
    public Guid Guid => Spawn.Guid;

    /// <summary>Its width and depth in metres (key 3: 20 × 20 or 40 × 40 in the royal cities).</summary>
    public Vector2 Size => Spawn.Size ?? new Vector2(20, 20);

    /// <summary>The owner's state; null = free (no row).</summary>
    public RealEstatePlot? Plot { get; set; }

    /// <summary>The name of the city holding it (a content/spawns station stands on it); null = a plot players can own.</summary>
    public string? ReservedBy { get; } = reservedBy;

    public bool Reserved => ReservedBy is not null;

    /// <summary>The owner Guid of a city-held plot (fixed, not a character).</summary>
    public static readonly Guid CityOwner = new("a1b10c17-0000-4000-8000-000000000001");

    public override string Kind => "real estate";
    public override string TypeName => $"{Size.X:0}x{Size.Y:0} {Spawn.Quality}";
    public override bool LeaveOnRange => false;

    /// <summary>Whether <paramref name="at"/> lies on the plot (its rectangle around the centre).</summary>
    public bool Contains(Vector2 at, float margin = 0) =>
        MathF.Abs(at.X - Position.X) <= Size.X / 2 - margin + 0.001f && MathF.Abs(at.Y - Position.Y) <= Size.Y / 2 - margin + 0.001f;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return NewRealEstate();
    }

    /// <summary>
    /// NewRealEstate (aus, read by cly.af(aus) 0x180B60A00): 0 id, 1 the plot Guid, 2 centre, 3 size, 4 the owner (empty = free), 5 its name,
    /// 6 its guild (left out without), 7 the outline byte (sent also at 0, as captured), 9 "" (as captured), 10 the asking price a7m (-10000 =
    /// not for sale; cly.arf compares with it, so it is always sent), 11 the last winning bid a7m, 12 the GameTimeStamp the owner got it. Key 8
    /// (a long the client keeps as a7m, 0 on every captured plot) is left out.
    /// </summary>
    public NewRealEstateEvent NewRealEstate()
    {
        var owner = Reserved ? CityOwner : Plot?.Owner ?? Guid.Empty;
        return new NewRealEstateEvent
        {
            c9h = Id,                                                  // [0]
            c9i = Guid,                                                // [1]
            c9j = Position,                                            // [2]
            c9k = Size,                                                // [3]
            c9l = owner,                                               // [4] left out when free
            c9m = Reserved ? ReservedBy : Plot?.OwnerName,             // [5]
            c9n = Plot is { } p && guildOf(p.Owner) is { } g ? g.Id : Guid.Empty, // [6]
            c9r = Plot?.SalePrice ?? RealEstatePlot.NotForSale,        // [10]
            c9s = Plot?.LastPrice ?? 0,                                // [11]
            c9t = Plot?.Since ?? 0,                                    // [12]
            Extra = new() { [7] = Plot?.Outline ?? (byte)0, [9] = "" }, // [7], [9] always, as captured
        };
    }

    /// <summary>AccessStatus {0 id, 1 preset 0 (left out), 4 the groups, 5 owner / noaccess / noaccess, 6 "", the guild's name, ""} as captured.</summary>
    public AccessStatusEvent AccessStatus() => new()
    {
        ddn = Id,
        bq8 = Building.AccessGroups,
        bq9 = ["owner", "noaccess", "noaccess"],
        bra = ["", Plot is { } p && guildOf(p.Owner) is { } g ? g.Name : "", ""],
    };
}
