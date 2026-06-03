using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(args.Length == 0 ? ["http://0.0.0.0:5058"] : args);

var app = builder.Build();
var storage = new RelayStorage(
    Environment.GetEnvironmentVariable("STARBRIDGE_RELAY_DATA") ??
    Path.Combine(AppContext.BaseDirectory, "data", "relay-state.json"));
var serverKey = Environment.GetEnvironmentVariable("STARBRIDGE_RELAY_KEY");
var state = await storage.LoadAsync();
var players = new ConcurrentDictionary<string, NetworkPlayerSnapshot>(
    state.Players ?? [],
    StringComparer.OrdinalIgnoreCase);
var fleets = new ConcurrentDictionary<string, NetworkFleetSnapshot>(
    state.Fleets ?? [],
    StringComparer.OrdinalIgnoreCase);
var users = new ConcurrentDictionary<string, UserAccount>(
    state.Users ?? [],
    StringComparer.OrdinalIgnoreCase);
var verificationCodes = new ConcurrentDictionary<string, VerificationCodeRecord>(
    state.VerificationCodes ?? [],
    StringComparer.OrdinalIgnoreCase);
var smtpOptions = new SmtpOptions(
    Environment.GetEnvironmentVariable("STARBRIDGE_SMTP_HOST"),
    int.TryParse(Environment.GetEnvironmentVariable("STARBRIDGE_SMTP_PORT"), out var smtpPort) ? smtpPort : 587,
    Environment.GetEnvironmentVariable("STARBRIDGE_SMTP_USER"),
    Environment.GetEnvironmentVariable("STARBRIDGE_SMTP_PASS"),
    Environment.GetEnvironmentVariable("STARBRIDGE_SMTP_FROM"),
    !string.Equals(Environment.GetEnvironmentVariable("STARBRIDGE_SMTP_SSL"), "false", StringComparison.OrdinalIgnoreCase));

app.MapGet("/", () => Results.Ok(new
{
    app = "Star Bridge Relay Server",
    version = "0.3.0",
    mode = string.IsNullOrWhiteSpace(serverKey) ? "open-test" : "protected",
    accounts = users.Count,
    players = players.Count,
    fleets = fleets.Count,
    storage = storage.Path,
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    mode = string.IsNullOrWhiteSpace(serverKey) ? "open-test" : "protected",
    accounts = users.Count,
    players = players.Count,
    fleets = fleets.Count,
    time = DateTimeOffset.UtcNow
}));

app.MapPost("/api/auth/register", async (AuthRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Password) ||
        string.IsNullOrWhiteSpace(request.Email) ||
        string.IsNullOrWhiteSpace(request.VerificationCode) ||
        string.IsNullOrWhiteSpace(request.Callsign))
    {
        return Results.BadRequest(new { error = "Email, password, callsign and verification code are required." });
    }

    var email = request.Email.Trim();
    var callsign = request.Callsign.Trim();
    if (request.Password.Length < 6)
    {
        return Results.BadRequest(new { error = "Password must be 6+ chars." });
    }

    if (callsign.Length is < 1 or > 24)
    {
        return Results.BadRequest(new { error = "Callsign must be between 1 and 24 characters." });
    }

    if (!verificationCodes.TryGetValue(email, out var verification) ||
        verification.ExpiresAt < DateTimeOffset.UtcNow ||
        !verification.Code.Equals(request.VerificationCode.Trim(), StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "Verification code is invalid or expired." });
    }

    if (users.Values.Any(existing =>
            !string.IsNullOrWhiteSpace(existing.Email) &&
            existing.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.Conflict(new { error = "Email already registered." });
    }

    var account = CreateAccount(email, request.Password, request.GameName, email, callsign);
    if (!users.TryAdd(email, account))
    {
        return Results.Conflict(new { error = "Email already registered." });
    }

    verificationCodes.TryRemove(email, out _);
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new AuthResponse(account.UserName, account.Email, account.Callsign, account.GameName, account.AuthToken));
});

app.MapPost("/api/auth/send-code", async (EmailVerificationRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new { error = "Email is required." });
    }

    if (string.IsNullOrWhiteSpace(smtpOptions.Host) ||
        string.IsNullOrWhiteSpace(smtpOptions.UserName) ||
        string.IsNullOrWhiteSpace(smtpOptions.Password) ||
        string.IsNullOrWhiteSpace(smtpOptions.FromAddress))
    {
        return Results.BadRequest(new { error = "Email service is not configured on the server." });
    }

    var email = request.Email.Trim();
    var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");
    verificationCodes[email] = new VerificationCodeRecord(email, code, DateTimeOffset.UtcNow.AddMinutes(10));

    try
    {
        await SendVerificationCodeAsync(smtpOptions, email, code);
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(new { sent = true, expiresInMinutes = 10 });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to send verification code: {ex.Message}" });
    }
});

app.MapPost("/api/auth/login", async (AuthRequest request) =>
{
    var email = request.Email;
    if (string.IsNullOrWhiteSpace(email))
    {
        email = request.UserName;
    }

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { error = "Email and password are required." });
    }

    var name = email.Trim();
    if (!users.TryGetValue(name, out var account) || !VerifyPassword(request.Password, account))
    {
        return Results.Unauthorized();
    }

    var token = string.IsNullOrWhiteSpace(account.AuthToken)
        ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        : account.AuthToken;
    users[name] = account with { AuthToken = token, LastLogin = DateTimeOffset.UtcNow };
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new AuthResponse(name, account.Email, account.Callsign, account.GameName, token));
});

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

app.MapPost("/api/fleets", async (HttpRequest request, NetworkFleetSnapshot snapshot) =>
{
    if (!IsWriteAllowed(request, serverKey, users))
    {
        return Results.Unauthorized();
    }

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
        OwnerAccount = Normalize(snapshot.OwnerAccount, GetAuthorizedUserName(request, users) ?? ""),
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
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(merged);
});

app.MapPost("/api/players", async (HttpRequest request, NetworkPlayerSnapshot snapshot) =>
{
    if (!IsWriteAllowed(request, serverKey, users))
    {
        return Results.Unauthorized();
    }

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
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(normalized);
});

app.MapPost("/api/fleets/disband", async (HttpRequest request, FleetDisbandRequest disbandRequest) =>
{
    if (string.IsNullOrWhiteSpace(disbandRequest.FleetCode) || string.IsNullOrWhiteSpace(disbandRequest.Password))
    {
        return Results.BadRequest(new { error = "Fleet code and password are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    if (!VerifyPassword(disbandRequest.Password, account))
    {
        return Results.BadRequest(new { error = "Password is incorrect." });
    }

    var fleetCode = disbandRequest.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    var isOwner = !string.IsNullOrWhiteSpace(fleet.OwnerAccount) &&
                  fleet.OwnerAccount.Equals(account.UserName, StringComparison.OrdinalIgnoreCase);
    if (!isOwner)
    {
        return Results.BadRequest(new { error = "Only the fleet commander account can disband this fleet." });
    }

    fleets.TryRemove(fleetCode, out _);
    foreach (var pair in players.ToArray())
    {
        if (string.Equals(pair.Value.Fleet, fleet.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pair.Value.Fleet, fleet.Code, StringComparison.OrdinalIgnoreCase))
        {
            players[pair.Key] = pair.Value with
            {
                Fleet = "No Fleet",
                Squad = "Unassigned",
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
    }

    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new { disbanded = true, fleet = fleetCode });
});

app.MapPost("/api/clear", async (HttpRequest request) =>
{
    if (!IsWriteAllowed(request, serverKey, users))
    {
        return Results.Unauthorized();
    }

    players.Clear();
    fleets.Clear();
    users.Clear();
    verificationCodes.Clear();
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new { cleared = true });
});

app.Run();

static bool IsWriteAllowed(
    HttpRequest request,
    string? serverKey,
    ConcurrentDictionary<string, UserAccount> users)
{
    if (string.IsNullOrWhiteSpace(serverKey))
    {
        return true;
    }

    if (request.Headers.TryGetValue("X-StarBridge-Key", out var provided) &&
        provided.ToString().Equals(serverKey, StringComparison.Ordinal))
    {
        return true;
    }

    if (request.Headers.TryGetValue("Authorization", out var auth))
    {
        var value = auth.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
               users.Values.Any(user =>
                   !string.IsNullOrWhiteSpace(user.AuthToken) &&
                   user.AuthToken.Equals(value[7..].Trim(), StringComparison.Ordinal));
    }

    return false;
}

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

static UserAccount CreateAccount(string name, string password, string? gameName, string? email = null, string? callsign = null)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = HashPassword(password, salt);
    return new UserAccount(
        name,
        string.IsNullOrWhiteSpace(callsign) ? name : callsign.Trim(),
        Normalize(gameName, name),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Convert.ToBase64String(salt),
        Convert.ToBase64String(hash),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        string.IsNullOrWhiteSpace(email) ? null : email.Trim());
}

static bool VerifyPassword(string password, UserAccount account)
{
    try
    {
        var salt = Convert.FromBase64String(account.PasswordSalt);
        var expected = Convert.FromBase64String(account.PasswordHash);
        var actual = HashPassword(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
    catch
    {
        return false;
    }
}

static byte[] HashPassword(string password, byte[] salt)
{
    return Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(password),
        salt,
        100_000,
        HashAlgorithmName.SHA256,
        32);
}

static string? GetAuthorizedUserName(HttpRequest request, ConcurrentDictionary<string, UserAccount> users)
{
    if (!request.Headers.TryGetValue("Authorization", out var auth))
    {
        return null;
    }

    var value = auth.ToString();
    if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var token = value[7..].Trim();
    return users.Values.FirstOrDefault(user =>
        !string.IsNullOrWhiteSpace(user.AuthToken) &&
        user.AuthToken.Equals(token, StringComparison.Ordinal))?.UserName;
}

static async Task SendVerificationCodeAsync(SmtpOptions smtpOptions, string email, string code)
{
    using var client = new SmtpClient(smtpOptions.Host, smtpOptions.Port)
    {
        EnableSsl = smtpOptions.EnableSsl,
        Credentials = new NetworkCredential(smtpOptions.UserName, smtpOptions.Password)
    };
    using var message = new MailMessage(smtpOptions.FromAddress!, email, "StarBridge", code);
    await client.SendMailAsync(message);
}

public sealed class RelayStorage(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = path;

    public async Task<RelayState> LoadAsync()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new RelayState([], [], null, null);
            }

            await using var stream = File.OpenRead(Path);
            return await JsonSerializer.DeserializeAsync<RelayState>(stream, JsonOptions) ??
                   new RelayState([], [], null, null);
        }
        catch
        {
            return new RelayState([], [], null, null);
        }
    }

    public async Task SaveAsync(
        ConcurrentDictionary<string, NetworkPlayerSnapshot> players,
        ConcurrentDictionary<string, NetworkFleetSnapshot> fleets,
        ConcurrentDictionary<string, UserAccount> users,
        ConcurrentDictionary<string, VerificationCodeRecord> verificationCodes)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var state = new RelayState(
                players.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                fleets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                users.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                verificationCodes
                    .Where(pair => pair.Value.ExpiresAt > DateTimeOffset.UtcNow)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
            var tempPath = $"{Path}.tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
            }

            File.Move(tempPath, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed record RelayState(
    Dictionary<string, NetworkPlayerSnapshot>? Players,
    Dictionary<string, NetworkFleetSnapshot>? Fleets,
    Dictionary<string, UserAccount>? Users = null,
    Dictionary<string, VerificationCodeRecord>? VerificationCodes = null);

public sealed record AuthRequest(
    string UserName,
    string Password,
    string? GameName,
    string? Email = null,
    string? VerificationCode = null,
    string? Callsign = null);

public sealed record EmailVerificationRequest(
    string Email);

public sealed record AuthResponse(
    string UserName,
    string? Email,
    string? Callsign,
    string? GameName,
    string Token);

public sealed record UserAccount(
    string UserName,
    string? Callsign,
    string? GameName,
    string AuthToken,
    string PasswordSalt,
    string PasswordHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastLogin,
    string? Email = null);

public sealed record VerificationCodeRecord(
    string Email,
    string Code,
    DateTimeOffset ExpiresAt);

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
    DateTimeOffset LastUpdated,
    string? OwnerAccount = null);

public sealed record FleetDisbandRequest(
    string FleetCode,
    string Password);

public sealed record NetworkSquadSnapshot(
    string Name,
    string? Commander,
    string? Type,
    string? Description);

public sealed record SmtpOptions(
    string? Host,
    int Port,
    string? UserName,
    string? Password,
    string? FromAddress,
    bool EnableSsl);
