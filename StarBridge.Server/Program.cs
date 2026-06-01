using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(args.Length == 0 ? ["http://0.0.0.0:5058"] : args);

var app = builder.Build();
var players = new ConcurrentDictionary<string, NetworkPlayerSnapshot>(StringComparer.OrdinalIgnoreCase);
var fleets = new ConcurrentDictionary<string, NetworkFleetSnapshot>(StringComparer.OrdinalIgnoreCase);

app.MapGet("/", () => Results.Ok(new
{
    app = "Star Bridge Relay Server",
    version = "0.2.2",
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/api/players", () => Results.Ok(players.Values
    .OrderByDescending(player => player.Online)
    .ThenBy(player => player.Name)
    .ToArray()));

app.MapGet("/api/fleets", () =>
{
    var playerArray = players.Values.ToArray();
    var fleetArray = fleets.Values
        .Select(fleet =>
        {
            var members = playerArray
                .Where(player => player.Fleet?.Equals(fleet.Name, StringComparison.OrdinalIgnoreCase) == true ||
                                 player.Fleet?.Equals(fleet.Code, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            return fleet with
            {
                OnlineMembers = members.Count(member => member.Online),
                TotalMembers = members.Length,
                LastUpdated = DateTimeOffset.UtcNow
            };
        })
        .OrderByDescending(fleet => fleet.OnlineMembers)
        .ThenBy(fleet => fleet.Name)
        .ToArray();

    return Results.Ok(fleetArray);
});

app.MapPost("/api/fleets", (NetworkFleetSnapshot snapshot) =>
{
    if (string.IsNullOrWhiteSpace(snapshot.Name) || string.IsNullOrWhiteSpace(snapshot.Code))
    {
        return Results.BadRequest(new { error = "Fleet name and code are required." });
    }

    var normalized = snapshot with
    {
        Name = snapshot.Name.Trim(),
        Code = snapshot.Code.Trim(),
        Commander = Normalize(snapshot.Commander, "Unassigned"),
        Description = Normalize(snapshot.Description, "No fleet description."),
        Type = Normalize(snapshot.Type, "Combat"),
        ActiveTime = Normalize(snapshot.ActiveTime, "20:00 - 23:59 UTC+8"),
        JoinPolicy = Normalize(snapshot.JoinPolicy, "Open"),
        LogoText = Normalize(snapshot.LogoText, "LOGO"),
        Squads = snapshot.Squads ?? [],
        LastUpdated = DateTimeOffset.UtcNow
    };

    var merged = fleets.AddOrUpdate(
        normalized.Code,
        normalized,
        (_, existing) => normalized with
        {
            Squads = MergeSquads(existing.Squads, normalized.Squads)
        });
    return Results.Ok(merged);
});

app.MapPost("/api/players", (NetworkPlayerSnapshot snapshot) =>
{
    if (string.IsNullOrWhiteSpace(snapshot.Name))
    {
        return Results.BadRequest(new { error = "Player name is required." });
    }

    var normalized = snapshot with
    {
        Name = snapshot.Name.Trim(),
        Ship = Normalize(snapshot.Ship, "Unknown"),
        ShipConfidence = Normalize(snapshot.ShipConfidence, "None"),
        Location = Normalize(snapshot.Location, "Unknown"),
        LocationConfidence = Normalize(snapshot.LocationConfidence, "None"),
        Callsign = Normalize(snapshot.Callsign, ""),
        Squad = Normalize(snapshot.Squad, "Unassigned"),
        Fleet = Normalize(snapshot.Fleet, "No Fleet"),
        LastUpdated = DateTimeOffset.UtcNow
    };

    players.AddOrUpdate(normalized.Name, normalized, (_, _) => normalized);
    return Results.Ok(normalized);
});

app.MapPost("/api/clear", () =>
{
    players.Clear();
    return Results.Ok(new { cleared = true });
});

app.Run();

static string Normalize(string? value, string fallback)
{
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

static NetworkSquadSnapshot[] MergeSquads(
    NetworkSquadSnapshot[]? existing,
    NetworkSquadSnapshot[]? incoming)
{
    var squads = new Dictionary<string, NetworkSquadSnapshot>(StringComparer.OrdinalIgnoreCase);
    foreach (var squad in existing ?? [])
    {
        if (!string.IsNullOrWhiteSpace(squad.Name))
        {
            squads[squad.Name.Trim()] = squad;
        }
    }

    foreach (var squad in incoming ?? [])
    {
        if (!string.IsNullOrWhiteSpace(squad.Name))
        {
            squads[squad.Name.Trim()] = squad;
        }
    }

    return squads.Values.OrderBy(squad => squad.Name).ToArray();
}

public sealed record NetworkPlayerSnapshot(
    string Name,
    string? Callsign,
    string? Fleet,
    string? Squad,
    bool Online,
    string? Ship,
    string? ShipConfidence,
    string? Location,
    string? LocationConfidence,
    DateTimeOffset LastUpdated);

public sealed record NetworkFleetSnapshot(
    string Name,
    string Code,
    string? Commander,
    string? Description,
    string? Type,
    string? ActiveTime,
    string? JoinPolicy,
    string? LogoText,
    NetworkSquadSnapshot[]? Squads,
    int OnlineMembers,
    int TotalMembers,
    DateTimeOffset LastUpdated);

public sealed record NetworkSquadSnapshot(
    string Name,
    string? Commander,
    string? Type,
    string? Description);
