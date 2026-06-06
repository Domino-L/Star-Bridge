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
var verificationEmailLastSentAt = new ConcurrentDictionary<string, DateTimeOffset>(
    StringComparer.OrdinalIgnoreCase);
var verificationIpWindows = new ConcurrentDictionary<string, VerificationRateWindow>(
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
    version = "0.3.5",
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

app.MapGet("/api/updates/latest", () => Results.Ok(new UpdateManifest(
    Environment.GetEnvironmentVariable("STARBRIDGE_LATEST_VERSION") ?? "0.3.5",
    Environment.GetEnvironmentVariable("STARBRIDGE_DOWNLOAD_URL"),
    Environment.GetEnvironmentVariable("STARBRIDGE_RELEASE_NOTES") ?? "当前服务器未配置新版安装包。",
    string.Equals(Environment.GetEnvironmentVariable("STARBRIDGE_UPDATE_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase),
    DateTimeOffset.UtcNow)));

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

    if (GetCallsignWeight(callsign) is < 1 or > 10)
    {
        return Results.BadRequest(new { error = "Callsign is too long." });
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

app.MapPost("/api/auth/send-code", async (HttpContext context, EmailVerificationRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new { error = "Email is required." });
    }

    if (!IsSmtpConfigured(smtpOptions))
    {
        return Results.BadRequest(new { error = "Email service is not configured on the server." });
    }

    var email = request.Email.Trim();
    var rateLimitError = ValidateVerificationRateLimit(
        verificationEmailLastSentAt,
        verificationIpWindows,
        email,
        GetClientIp(context),
        DateTimeOffset.UtcNow);
    if (rateLimitError is not null)
    {
        return Results.BadRequest(new { error = rateLimitError });
    }

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

app.MapPost("/api/feedback", async (HttpRequest request, FeedbackRequest feedback) =>
{
    if (string.IsNullOrWhiteSpace(feedback.Message))
    {
        return Results.BadRequest(new { error = "Feedback message is required." });
    }

    if (!IsSmtpConfigured(smtpOptions))
    {
        return Results.BadRequest(new { error = "Email service is not configured on the server." });
    }

    var userName = GetAuthorizedUserName(request, users);
    users.TryGetValue(userName ?? "", out var account);
    var sender = account?.Email ?? feedback.Contact ?? "Guest";
    var gameName = account?.GameName ?? feedback.GameName ?? "Unknown";
    var callsign = account?.Callsign ?? feedback.Callsign ?? "Unknown";
    var body = $"""
StarBridge Feedback

Time: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
Account: {sender}
GameName: {gameName}
Callsign: {callsign}
Contact: {Normalize(feedback.Contact, "Not provided")}

Message:
{feedback.Message.Trim()}
""";

    try
    {
        await SendEmailAsync(smtpOptions, "ruiyanglyu0217@gmail.com", "StarBridge Feedback", body);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to send feedback: {ex.Message}" });
    }
});

app.MapPost("/api/fleets/notify", async (HttpRequest request, FleetNotificationRequest notification) =>
{
    if (!IsWriteAllowed(request, serverKey, users))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(notification.FleetCode) ||
        string.IsNullOrWhiteSpace(notification.Subject) ||
        string.IsNullOrWhiteSpace(notification.Body))
    {
        return Results.BadRequest(new { error = "Fleet code, subject and body are required." });
    }

    if (!IsSmtpConfigured(smtpOptions))
    {
        return Results.BadRequest(new { error = "Email service is not configured on the server." });
    }

    if (!fleets.TryGetValue(notification.FleetCode.Trim(), out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    var authorizedUser = GetAuthorizedUserName(request, users);
    if (!string.IsNullOrWhiteSpace(fleet.OwnerAccount) &&
        !string.IsNullOrWhiteSpace(authorizedUser) &&
        !fleet.OwnerAccount.Equals(authorizedUser, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Unauthorized();
    }

    var memberNames = players.Values
        .Where(player =>
            string.Equals(player.Fleet, fleet.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(player.Fleet, fleet.Code, StringComparison.OrdinalIgnoreCase))
        .Select(player => player.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    if (!string.IsNullOrWhiteSpace(fleet.Commander))
    {
        memberNames.Add(fleet.Commander);
    }

    var recipients = users.Values
        .Where(user =>
            !string.IsNullOrWhiteSpace(user.Email) &&
            (!string.IsNullOrWhiteSpace(user.GameName) && memberNames.Contains(user.GameName) ||
             !string.IsNullOrWhiteSpace(user.Callsign) && memberNames.Contains(user.Callsign)))
        .Select(user => user.Email!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var sent = 0;
    foreach (var recipient in recipients)
    {
        await SendEmailAsync(smtpOptions, recipient, notification.Subject.Trim(), notification.Body.Trim());
        sent++;
    }

    return Results.Ok(new { sent });
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

app.MapPost("/api/auth/profile", async (HttpRequest request, ProfileUpdateRequest profile) =>
{
    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var callsign = string.IsNullOrWhiteSpace(profile.Callsign) ? null : profile.Callsign.Trim();
    if (!string.IsNullOrWhiteSpace(callsign) && GetCallsignWeight(callsign) > 10)
    {
        return Results.BadRequest(new { error = "Callsign is too long." });
    }

    var updated = account with { Callsign = callsign };
    users[userName] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new AuthResponse(updated.UserName, updated.Email, updated.Callsign, updated.GameName, updated.AuthToken));
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
                LogoImageData = Normalize(fleet.LogoImageData, ""),
                NoticeTitle = Normalize(fleet.NoticeTitle, ""),
                NoticeContent = Normalize(fleet.NoticeContent, ""),
                CurrentTaskTitle = Normalize(fleet.CurrentTaskTitle, ""),
                CurrentTaskBrief = Normalize(fleet.CurrentTaskBrief, ""),
                CurrentTaskParticipants = Normalize(fleet.CurrentTaskParticipants, ""),
                CurrentTaskRally = Normalize(fleet.CurrentTaskRally, ""),
                CurrentTaskShip = Normalize(fleet.CurrentTaskShip, ""),
                ActionPlans = fleet.ActionPlans ?? [],
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
        LogoImageData = Normalize(snapshot.LogoImageData, ""),
        NoticeTitle = Normalize(snapshot.NoticeTitle, ""),
        NoticeContent = Normalize(snapshot.NoticeContent, ""),
        CurrentTaskTitle = Normalize(snapshot.CurrentTaskTitle, ""),
        CurrentTaskBrief = Normalize(snapshot.CurrentTaskBrief, ""),
        CurrentTaskParticipants = Normalize(snapshot.CurrentTaskParticipants, ""),
        CurrentTaskRally = Normalize(snapshot.CurrentTaskRally, ""),
        CurrentTaskShip = Normalize(snapshot.CurrentTaskShip, ""),
        CurrentTaskTime = snapshot.CurrentTaskTime,
        ActionPlans = snapshot.ActionPlans ?? [],
        OwnerAccount = Normalize(snapshot.OwnerAccount, GetAuthorizedUserName(request, users) ?? ""),
        Squads = snapshot.Squads ?? [],
        LastUpdated = DateTimeOffset.UtcNow
    };

    var authorizedUser = GetAuthorizedUserName(request, users) ?? "";
    var merged = fleets.AddOrUpdate(
        normalized.Code,
        normalized,
        (_, existing) =>
        {
            var canOverwriteManagedState =
                string.IsNullOrWhiteSpace(existing.OwnerAccount) ||
                existing.OwnerAccount.Equals(authorizedUser, StringComparison.OrdinalIgnoreCase);

            return normalized with
            {
                Description = canOverwriteManagedState ? normalized.Description : existing.Description,
                Type = canOverwriteManagedState ? normalized.Type : existing.Type,
                ActiveTime = canOverwriteManagedState ? normalized.ActiveTime : existing.ActiveTime,
                JoinPolicy = canOverwriteManagedState ? normalized.JoinPolicy : existing.JoinPolicy,
                LogoText = canOverwriteManagedState ? normalized.LogoText : existing.LogoText,
                LogoImageData = canOverwriteManagedState ? normalized.LogoImageData : existing.LogoImageData,
                NoticeTitle = canOverwriteManagedState ? normalized.NoticeTitle : existing.NoticeTitle,
                NoticeContent = canOverwriteManagedState ? normalized.NoticeContent : existing.NoticeContent,
                CurrentTaskTitle = canOverwriteManagedState ? normalized.CurrentTaskTitle : existing.CurrentTaskTitle,
                CurrentTaskBrief = canOverwriteManagedState ? normalized.CurrentTaskBrief : existing.CurrentTaskBrief,
                CurrentTaskParticipants = canOverwriteManagedState ? normalized.CurrentTaskParticipants : existing.CurrentTaskParticipants,
                CurrentTaskRally = canOverwriteManagedState ? normalized.CurrentTaskRally : existing.CurrentTaskRally,
                CurrentTaskShip = canOverwriteManagedState ? normalized.CurrentTaskShip : existing.CurrentTaskShip,
                CurrentTaskTime = canOverwriteManagedState ? normalized.CurrentTaskTime : existing.CurrentTaskTime,
                ActionPlans = canOverwriteManagedState ? normalized.ActionPlans : existing.ActionPlans,
                OwnerAccount = canOverwriteManagedState ? normalized.OwnerAccount : existing.OwnerAccount,
                Squads = MergeSquads(existing.Squads, normalized.Squads)
            };
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

static string? ValidateVerificationRateLimit(
    ConcurrentDictionary<string, DateTimeOffset> emailLastSentAt,
    ConcurrentDictionary<string, VerificationRateWindow> ipWindows,
    string email,
    string clientIp,
    DateTimeOffset now)
{
    var emailKey = email.Trim().ToLowerInvariant();
    if (emailLastSentAt.TryGetValue(emailKey, out var lastSentAt) &&
        now - lastSentAt < TimeSpan.FromSeconds(60))
    {
        return "Please wait before requesting another verification code.";
    }

    var ipWindow = ipWindows.AddOrUpdate(
        clientIp,
        _ => new VerificationRateWindow(now, 1),
        (_, existing) => now - existing.StartedAt >= TimeSpan.FromMinutes(1)
            ? new VerificationRateWindow(now, 1)
            : existing with { Count = existing.Count + 1 });

    if (ipWindow.Count > 5)
    {
        return "Too many verification requests. Please try again later.";
    }

    emailLastSentAt[emailKey] = now;
    return null;
}

static string GetClientIp(HttpContext context)
{
    if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
    {
        var first = forwardedFor.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }
    }

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static string Normalize(string? value, string fallback)
{
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

static int GetCallsignWeight(string value)
{
    var total = 0;
    foreach (var character in value)
    {
        total += IsCjk(character) ? 2 : 1;
    }

    return total;
}

static bool IsCjk(char character)
{
    return (character >= '\u4E00' && character <= '\u9FFF') ||
           (character >= '\u3400' && character <= '\u4DBF') ||
           (character >= '\uF900' && character <= '\uFAFF');
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

static bool IsSmtpConfigured(SmtpOptions smtpOptions)
{
    return !string.IsNullOrWhiteSpace(smtpOptions.Host) &&
           !string.IsNullOrWhiteSpace(smtpOptions.UserName) &&
           !string.IsNullOrWhiteSpace(smtpOptions.Password) &&
           !string.IsNullOrWhiteSpace(smtpOptions.FromAddress);
}

static async Task SendVerificationCodeAsync(SmtpOptions smtpOptions, string email, string code)
{
    await SendEmailAsync(smtpOptions, email, "StarBridge", code);
}

static async Task SendEmailAsync(SmtpOptions smtpOptions, string to, string subject, string body)
{
    using var client = new SmtpClient(smtpOptions.Host, smtpOptions.Port)
    {
        EnableSsl = smtpOptions.EnableSsl,
        Credentials = new NetworkCredential(smtpOptions.UserName, smtpOptions.Password)
    };
    using var message = new MailMessage(smtpOptions.FromAddress!, to, subject, body)
    {
        BodyEncoding = Encoding.UTF8,
        SubjectEncoding = Encoding.UTF8
    };
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

public sealed record ProfileUpdateRequest(
    string? Callsign);

public sealed record FeedbackRequest(
    string? Contact,
    string? GameName,
    string? Callsign,
    string Message);

public sealed record UpdateManifest(
    string Version,
    string? DownloadUrl,
    string? Notes,
    bool Required = false,
    DateTimeOffset? PublishedAt = null);

public sealed record FleetNotificationRequest(
    string FleetCode,
    string Subject,
    string Body);

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

public sealed record VerificationRateWindow(
    DateTimeOffset StartedAt,
    int Count);

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
    string? LogoImageData,
    NetworkSquadSnapshot[]? Squads,
    int OnlineMembers,
    int TotalMembers,
    string? NoticeTitle,
    string? NoticeContent,
    string? CurrentTaskTitle,
    string? CurrentTaskBrief,
    string? CurrentTaskParticipants,
    string? CurrentTaskRally,
    string? CurrentTaskShip,
    DateTime? CurrentTaskTime,
    NetworkActionPlanSnapshot[]? ActionPlans,
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

public sealed record NetworkActionPlanSnapshot(
    string Id,
    string Title,
    string Content,
    DateTime StartTime,
    bool NotifyMembers,
    NetworkActionPlanParticipantSnapshot[]? Participants);

public sealed record NetworkActionPlanParticipantSnapshot(
    string Callsign,
    string GameName,
    string? AvatarPath,
    string Initials);

public sealed record SmtpOptions(
    string? Host,
    int Port,
    string? UserName,
    string? Password,
    string? FromAddress,
    bool EnableSsl);
