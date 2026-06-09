using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(args.Length == 0 ? ["http://127.0.0.1:5058"] : args);

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
var playerOnlineTimeout = TimeSpan.FromSeconds(120);

app.MapGet("/", () => Results.Ok(new
{
    app = "Star Bridge Relay Server",
    version = "0.3.7",
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
    Environment.GetEnvironmentVariable("STARBRIDGE_LATEST_VERSION") ?? "0.3.7",
    Environment.GetEnvironmentVariable("STARBRIDGE_DOWNLOAD_URL"),
    Environment.GetEnvironmentVariable("STARBRIDGE_PACKAGE_URL"),
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
    return Results.Ok(ToAuthResponse(account));
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
        Console.Error.WriteLine($"Verification email failed for {email}: {ex}");
        return Results.BadRequest(new { error = "Verification email could not be sent. Please try again later." });
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
        Console.Error.WriteLine($"Feedback email failed from {sender}: {ex}");
        return Results.BadRequest(new { error = "Feedback could not be sent. Please try again later." });
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
    users.TryGetValue(authorizedUser ?? "", out var authorizedAccount);
    if (!CanSendFleetNotification(fleet, authorizedAccount))
    {
        return Results.Unauthorized();
    }

    var memberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var player in players.Values.Where(player =>
                 string.Equals(player.Fleet, fleet.Name, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(player.Fleet, fleet.Code, StringComparison.OrdinalIgnoreCase)))
    {
        AddIdentityAliases(memberNames, player.Name);
        AddIdentityAliases(memberNames, player.Callsign);
    }

    foreach (var member in fleet.MemberPermissions ?? [])
    {
        AddIdentityAliases(memberNames, member.GameName);
        AddIdentityAliases(memberNames, member.Callsign);
    }

    foreach (var member in fleet.Members ?? [])
    {
        AddIdentityAliases(memberNames, member.GameName);
        AddIdentityAliases(memberNames, member.Callsign);
    }

    AddIdentityAliases(memberNames, fleet.Commander);

    var recipients = users.Values
        .Where(user =>
            !string.IsNullOrWhiteSpace(user.Email) &&
            user.AllowEmailNotifications &&
            MatchesIdentitySet(user, memberNames))
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
    return Results.Ok(ToAuthResponse(users[name]));
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

    var updated = account with
    {
        Callsign = callsign,
        AllowEmailNotifications = profile.AllowEmailNotifications ?? account.AllowEmailNotifications
    };
    users[userName] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(ToAuthResponse(updated));
});

app.MapGet("/api/players", () => Results.Ok(players.Values
    .Select(player => ApplyPlayerOnlineTimeout(player, DateTimeOffset.UtcNow, playerOnlineTimeout))
    .OrderByDescending(player => player.Online)
    .ThenBy(player => player.Name)
    .ToArray()));

app.MapGet("/api/fleets", () =>
{
    var now = DateTimeOffset.UtcNow;
    var playerArray = players.Values
        .Select(player => ApplyPlayerOnlineTimeout(player, now, playerOnlineTimeout))
        .ToArray();
    var fleetArray = fleets.Values
        .Select(fleet =>
        {
            var members = playerArray
                .Where(player => player.Fleet?.Equals(fleet.Name, StringComparison.OrdinalIgnoreCase) == true ||
                                 player.Fleet?.Equals(fleet.Code, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            var fleetMembers = BuildFleetMembers(fleet, members);
            var fleetShips = NormalizeFleetShips((fleet.Ships ?? [])
                .Concat(members.SelectMany(BuildFleetShipsFromPlayer))
                .ToArray());

            return fleet with
            {
                OnlineMembers = fleetMembers.Count(member => member.Online),
                TotalMembers = fleetMembers.Length,
                LogoImageData = Normalize(fleet.LogoImageData, ""),
                NoticeTitle = Normalize(fleet.NoticeTitle, ""),
                NoticeContent = Normalize(fleet.NoticeContent, ""),
                CurrentTaskTitle = Normalize(fleet.CurrentTaskTitle, ""),
                CurrentTaskBrief = Normalize(fleet.CurrentTaskBrief, ""),
                CurrentTaskParticipants = Normalize(fleet.CurrentTaskParticipants, ""),
                CurrentTaskRally = Normalize(fleet.CurrentTaskRally, ""),
                CurrentTaskShip = Normalize(fleet.CurrentTaskShip, ""),
                ActionPlans = fleet.ActionPlans ?? [],
                MemberPermissions = fleet.MemberPermissions ?? [],
                Members = fleetMembers,
                EventLog = NormalizeFleetEventLogs(fleet.EventLog),
                Ships = fleetShips,
                TaskHistory = NormalizeFleetTaskHistory(fleet.TaskHistory),
                Applications = NormalizeFleetApplications(fleet.Applications),
                Squads = MergeSquads([], fleet.Squads),
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
        CurrentTaskNoticeRevision = Math.Max(0, snapshot.CurrentTaskNoticeRevision),
        ActionPlans = snapshot.ActionPlans ?? [],
        MemberPermissions = NormalizeFleetMemberPermissions(snapshot.MemberPermissions),
        Members = NormalizeFleetMembers(snapshot.Members, snapshot.MemberPermissions),
        EventLog = NormalizeFleetEventLogs(snapshot.EventLog),
        Ships = NormalizeFleetShips(snapshot.Ships),
        TaskHistory = NormalizeFleetTaskHistory(snapshot.TaskHistory),
        Applications = NormalizeFleetApplications(snapshot.Applications),
        OwnerAccount = Normalize(snapshot.OwnerAccount, GetAuthorizedUserName(request, users) ?? ""),
        Squads = MergeSquads([], snapshot.Squads),
        LastUpdated = DateTimeOffset.UtcNow
    };

    var authorizedUser = GetAuthorizedUserName(request, users) ?? "";
    users.TryGetValue(authorizedUser, out var authorizedAccount);
    var merged = fleets.AddOrUpdate(
        normalized.Code,
        normalized,
        (_, existing) =>
        {
            var canOwnFleet =
                string.IsNullOrWhiteSpace(existing.OwnerAccount) ||
                existing.OwnerAccount.Equals(authorizedUser, StringComparison.OrdinalIgnoreCase);
            var canManageFleetInfo = canOwnFleet ||
                                     HasFleetPermission(existing, authorizedAccount, permission => permission.CanManageFleetInfo);
            var canPublishTasks = canOwnFleet ||
                                  HasFleetPermission(existing, authorizedAccount, permission => permission.CanPublishTasks);
            var canPublishPlans = canOwnFleet ||
                                  HasFleetPermission(existing, authorizedAccount, permission => permission.CanPublishPlans);
            var canAppendFleetLogs = canOwnFleet || canManageFleetInfo || canPublishTasks || canPublishPlans;
            var canJoinActionPlans = IsFleetMember(existing, authorizedAccount);

            return normalized with
            {
                Name = canManageFleetInfo ? normalized.Name : existing.Name,
                Commander = canOwnFleet ? normalized.Commander : existing.Commander,
                Description = canManageFleetInfo ? normalized.Description : existing.Description,
                Type = canManageFleetInfo ? normalized.Type : existing.Type,
                ActiveTime = canManageFleetInfo ? normalized.ActiveTime : existing.ActiveTime,
                JoinPolicy = canManageFleetInfo ? normalized.JoinPolicy : existing.JoinPolicy,
                LogoText = canManageFleetInfo ? normalized.LogoText : existing.LogoText,
                LogoImageData = canManageFleetInfo ? normalized.LogoImageData : existing.LogoImageData,
                NoticeTitle = canManageFleetInfo ? normalized.NoticeTitle : existing.NoticeTitle,
                NoticeContent = canManageFleetInfo ? normalized.NoticeContent : existing.NoticeContent,
                CurrentTaskTitle = canPublishTasks ? normalized.CurrentTaskTitle : existing.CurrentTaskTitle,
                CurrentTaskBrief = canPublishTasks ? normalized.CurrentTaskBrief : existing.CurrentTaskBrief,
                CurrentTaskParticipants = canPublishTasks ? normalized.CurrentTaskParticipants : existing.CurrentTaskParticipants,
                CurrentTaskRally = canPublishTasks ? normalized.CurrentTaskRally : existing.CurrentTaskRally,
                CurrentTaskShip = canPublishTasks ? normalized.CurrentTaskShip : existing.CurrentTaskShip,
                CurrentTaskTime = canPublishTasks ? normalized.CurrentTaskTime : existing.CurrentTaskTime,
                CurrentTaskNoticeRevision = canPublishTasks
                    ? Math.Max(existing.CurrentTaskNoticeRevision, normalized.CurrentTaskNoticeRevision)
                    : existing.CurrentTaskNoticeRevision,
                ActionPlans = canPublishPlans
                    ? normalized.ActionPlans
                    : canJoinActionPlans
                        ? MergeActionPlanParticipants(existing.ActionPlans, normalized.ActionPlans)
                        : existing.ActionPlans,
                MemberPermissions = canOwnFleet
                    ? MergeFleetMemberPermissions(existing.MemberPermissions, normalized.MemberPermissions)
                    : existing.MemberPermissions,
                Members = MergeFleetMembers(
                    existing.Members,
                    FilterFleetMemberUpdatesForAccount(
                        normalized.Members,
                        existing.Members,
                        authorizedAccount,
                        canUpdateAllMembers: false)),
                Ships = MergeFleetShips(existing.Ships, normalized.Ships, authorizedAccount),
                TaskHistory = canPublishTasks
                    ? MergeFleetTaskHistory(existing.TaskHistory, normalized.TaskHistory)
                    : existing.TaskHistory,
                Applications = canManageFleetInfo
                    ? NormalizeFleetApplications(normalized.Applications)
                    : existing.Applications,
                EventLog = canAppendFleetLogs
                    ? MergeFleetEventLogs(existing.EventLog, normalized.EventLog)
                    : existing.EventLog,
                OwnerAccount = canOwnFleet ? normalized.OwnerAccount : existing.OwnerAccount,
                Squads = MergeSquads(existing.Squads, normalized.Squads)
            };
        });
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(merged);
});

app.MapPost("/api/fleets/notice", async (HttpRequest request, FleetNoticeUpdateRequest update) =>
{
    if (string.IsNullOrWhiteSpace(update.FleetCode))
    {
        return Results.BadRequest(new { error = "Fleet code is required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = update.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetOwner(fleet, account) &&
        !HasFleetPermission(fleet, account, permission => permission.CanManageFleetInfo))
    {
        return Results.Unauthorized();
    }

    var updated = fleet with
    {
        NoticeTitle = Normalize(update.Title, ""),
        NoticeContent = Normalize(update.Content, ""),
        EventLog = MergeFleetEventLogs(fleet.EventLog, update.EventLog),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/task", async (HttpRequest request, FleetTaskUpdateRequest update) =>
{
    if (string.IsNullOrWhiteSpace(update.FleetCode))
    {
        return Results.BadRequest(new { error = "Fleet code is required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = update.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetOwner(fleet, account) &&
        !HasFleetPermission(fleet, account, permission => permission.CanPublishTasks))
    {
        return Results.Unauthorized();
    }

    var updated = fleet with
    {
        CurrentTaskTitle = Normalize(update.Title, ""),
        CurrentTaskBrief = Normalize(update.Brief, ""),
        CurrentTaskParticipants = Normalize(update.Participants, ""),
        CurrentTaskRally = Normalize(update.Rally, ""),
        CurrentTaskShip = Normalize(update.Ship, ""),
        CurrentTaskTime = update.Time,
        CurrentTaskNoticeRevision = Math.Max(
            Math.Max(0, update.NoticeRevision),
            Math.Max(0, fleet.CurrentTaskNoticeRevision)),
        TaskHistory = MergeFleetTaskHistory(fleet.TaskHistory, update.TaskHistory),
        EventLog = MergeFleetEventLogs(fleet.EventLog, update.EventLog),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/action-plans", async (HttpRequest request, FleetActionPlansUpdateRequest update) =>
{
    if (string.IsNullOrWhiteSpace(update.FleetCode))
    {
        return Results.BadRequest(new { error = "Fleet code is required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = update.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetOwner(fleet, account) &&
        !HasFleetPermission(fleet, account, permission => permission.CanPublishPlans))
    {
        return Results.Unauthorized();
    }

    var updated = fleet with
    {
        ActionPlans = NormalizeActionPlans(update.ActionPlans),
        EventLog = MergeFleetEventLogs(fleet.EventLog, update.EventLog),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/action-plans/join", async (HttpRequest request, FleetActionPlanJoinRequest join) =>
{
    if (string.IsNullOrWhiteSpace(join.FleetCode) ||
        string.IsNullOrWhiteSpace(join.PlanId) ||
        join.Participant is null)
    {
        return Results.BadRequest(new { error = "Fleet code, action plan and participant are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = join.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetMember(fleet, account))
    {
        return Results.Unauthorized();
    }

    var incoming = new NetworkActionPlanSnapshot(
        join.PlanId.Trim(),
        "",
        "",
        DateTime.MinValue,
        false,
        [join.Participant]);
    var updated = fleet with
    {
        ActionPlans = MergeActionPlanParticipants(fleet.ActionPlans, [incoming]),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/action-plans/leave", async (HttpRequest request, FleetActionPlanLeaveRequest leave) =>
{
    if (string.IsNullOrWhiteSpace(leave.FleetCode) ||
        string.IsNullOrWhiteSpace(leave.PlanId))
    {
        return Results.BadRequest(new { error = "Fleet code and action plan are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = leave.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetMember(fleet, account))
    {
        return Results.Unauthorized();
    }

    var aliases = BuildAccountAliases(account);
    var updated = fleet with
    {
        ActionPlans = RemoveActionPlanParticipants(fleet.ActionPlans, leave.PlanId.Trim(), aliases),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/apply", async (HttpRequest request, FleetJoinApplicationRequest join) =>
{
    if (string.IsNullOrWhiteSpace(join.FleetCode))
    {
        return Results.BadRequest(new { error = "Fleet code is required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = join.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (IsFleetMember(fleet, account))
    {
        return Results.Ok(fleet with { Applications = NormalizeFleetApplications(fleet.Applications) });
    }

    var player = FindPlayerForAccount(players, account);
    if (RequiresFleetApplication(fleet.JoinPolicy))
    {
        var application = BuildFleetApplication(account, player, join.Message);
        var updated = fleet with
        {
            Applications = UpsertFleetApplication(fleet.Applications, application),
            EventLog = AddFleetLog(
                fleet.EventLog,
                "申请",
                "收到加入申请",
                $"{FormatAccountIdentity(account, player)} 申请加入舰队"),
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    }

    RemoveAccountFromOtherFleets(fleets, players, fleetCode, account);
    var member = BuildFleetMemberFromAccount(account, player);
    foreach (var pair in players.ToArray())
    {
        if (MatchesAccountIdentity(pair.Value.Name, account) ||
            MatchesAccountIdentity(pair.Value.Callsign, account))
        {
            players[pair.Key] = pair.Value with
            {
                Fleet = fleet.Name,
                Squad = Normalize(pair.Value.Squad, "Unassigned"),
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
    }

    var aliases = BuildAccountAliases(account);
    var joined = fleet with
    {
        Members = UpsertFleetMember(fleet.Members, member),
        MemberPermissions = EnsureFleetPermission(fleet.MemberPermissions, member),
        Ships = MergeFleetShips(fleet.Ships, player is null ? [] : BuildFleetShipsFromPlayer(player), null),
        Applications = RemoveFleetApplicationsForAliases(fleet.Applications, aliases),
        EventLog = AddFleetLog(
            fleet.EventLog,
            "成员",
            "玩家加入",
            $"{DisplayMember(member)} 加入舰队"),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = joined;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(joined);
});

app.MapPost("/api/fleets/applications/decide", async (HttpRequest request, FleetApplicationDecisionRequest decision) =>
{
    if (string.IsNullOrWhiteSpace(decision.FleetCode) ||
        string.IsNullOrWhiteSpace(decision.ApplicationId))
    {
        return Results.BadRequest(new { error = "Fleet code and application id are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = decision.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetOwner(fleet, account) &&
        !HasFleetPermission(fleet, account, permission => permission.CanManageFleetInfo))
    {
        return Results.Unauthorized();
    }

    var application = NormalizeFleetApplications(fleet.Applications)
        .FirstOrDefault(item => item.Id.Equals(decision.ApplicationId.Trim(), StringComparison.OrdinalIgnoreCase));
    if (application is null)
    {
        return Results.NotFound(new { error = "Application not found." });
    }

    var applicantAccount = users.Values.FirstOrDefault(user => ApplicationMatchesAccount(application, user));
    var applicantPlayer = applicantAccount is null
        ? players.Values.FirstOrDefault(player =>
            IdentityContainsAny(player.Name, ExpandIdentityAliases(application.ApplicantGameName).ToHashSet(StringComparer.OrdinalIgnoreCase)) ||
            IdentityContainsAny(player.Callsign, ExpandIdentityAliases(application.ApplicantCallsign).ToHashSet(StringComparer.OrdinalIgnoreCase)))
        : FindPlayerForAccount(players, applicantAccount);
    var appAliases = BuildApplicationAliases(application, applicantAccount);

    var applications = NormalizeFleetApplications(fleet.Applications)
        .Where(item => !item.Id.Equals(application.Id, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var updated = fleet with
    {
        Applications = applications,
        EventLog = AddFleetLog(
            fleet.EventLog,
            "申请",
            decision.Approve ? "通过加入申请" : "拒绝加入申请",
            $"{FormatApplicationIdentity(application)} {(decision.Approve ? "加入舰队" : "被拒绝加入舰队")}"),
        LastUpdated = DateTimeOffset.UtcNow
    };

    if (decision.Approve)
    {
        if (applicantAccount is not null)
        {
            RemoveAccountFromOtherFleets(fleets, players, fleetCode, applicantAccount);
        }

        foreach (var pair in players.ToArray())
        {
            var matches = applicantAccount is not null
                ? MatchesAccountIdentity(pair.Value.Name, applicantAccount) ||
                  MatchesAccountIdentity(pair.Value.Callsign, applicantAccount)
                : IdentityContainsAny(pair.Value.Name, appAliases) ||
                  IdentityContainsAny(pair.Value.Callsign, appAliases);
            if (!matches)
            {
                continue;
            }

            players[pair.Key] = pair.Value with
            {
                Fleet = fleet.Name,
                Squad = Normalize(pair.Value.Squad, "Unassigned"),
                LastUpdated = DateTimeOffset.UtcNow
            };
        }

        var member = applicantAccount is null
            ? BuildFleetMemberFromApplication(application, applicantPlayer)
            : BuildFleetMemberFromAccount(applicantAccount, applicantPlayer);
        updated = updated with
        {
            Members = UpsertFleetMember(updated.Members, member),
            MemberPermissions = EnsureFleetPermission(updated.MemberPermissions, member),
            Ships = MergeFleetShips(updated.Ships, applicantPlayer is null ? [] : BuildFleetShipsFromPlayer(applicantPlayer), null)
        };
    }

    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/leave", async (HttpRequest request, FleetLeaveRequest leave) =>
{
    if (string.IsNullOrWhiteSpace(leave.FleetCode))
    {
        return Results.BadRequest(new { error = "Fleet code is required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = leave.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (IsFleetOwner(fleet, account))
    {
        return Results.BadRequest(new { error = "Fleet commander must transfer command or disband the fleet first." });
    }

    if (!IsFleetMember(fleet, account))
    {
        return Results.Ok(fleet);
    }

    var aliases = BuildAccountAliases(account);
    foreach (var pair in players.ToArray())
    {
        if (MatchesAccountIdentity(pair.Value.Name, account) ||
            MatchesAccountIdentity(pair.Value.Callsign, account))
        {
            players[pair.Key] = pair.Value with
            {
                Fleet = "No Fleet",
                Squad = "Unassigned",
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
    }

    var updated = RemoveFleetIdentity(fleet, aliases) with
    {
        EventLog = AddFleetLog(
            fleet.EventLog,
            "成员",
            "玩家离开",
            $"{FormatAccountIdentity(account, FindPlayerForAccount(players, account))} 离开舰队"),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/permissions", async (HttpRequest request, FleetMemberPermissionUpdateRequest update) =>
{
    if (string.IsNullOrWhiteSpace(update.FleetCode) || update.Permission is null)
    {
        return Results.BadRequest(new { error = "Fleet code and member permission are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = update.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetOwner(fleet, account))
    {
        return Results.Unauthorized();
    }

    var updated = fleet with
    {
        MemberPermissions = MergeFleetMemberPermissions(fleet.MemberPermissions, [update.Permission]),
        EventLog = MergeFleetEventLogs(fleet.EventLog, update.EventLog),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/info", async (HttpRequest request, FleetInfoUpdateRequest update) =>
{
    if (string.IsNullOrWhiteSpace(update.FleetCode))
    {
        return Results.BadRequest(new { error = "Fleet code is required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = update.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetOwner(fleet, account) &&
        !HasFleetPermission(fleet, account, permission => permission.CanManageFleetInfo))
    {
        return Results.Unauthorized();
    }

    var logoImageData = NormalizeImageData(update.LogoImageData, 512 * 1024);
    var updated = fleet with
    {
        Description = Normalize(update.Description, "No fleet description."),
        Type = Normalize(update.Type, "Combat"),
        ActiveTime = Normalize(update.ActiveTime, "20:00 - 23:59 UTC+8"),
        JoinPolicy = Normalize(update.JoinPolicy, "Open"),
        LogoText = Normalize(update.LogoText, Normalize(fleet.LogoText, "LOGO")),
        LogoImageData = logoImageData ?? fleet.LogoImageData,
        EventLog = MergeFleetEventLogs(fleet.EventLog, update.EventLog),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
});

app.MapPost("/api/fleets/squads", async (HttpRequest request, FleetSquadsUpdateRequest update) =>
{
    if (string.IsNullOrWhiteSpace(update.FleetCode))
    {
        return Results.BadRequest(new { error = "Fleet code is required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = update.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetOwner(fleet, account) && !IsFleetMember(fleet, account))
    {
        return Results.Unauthorized();
    }

    var updated = fleet with
    {
        Squads = MergeSquads(fleet.Squads, update.Squads),
        EventLog = MergeFleetEventLogs(fleet.EventLog, update.EventLog),
        LastUpdated = DateTimeOffset.UtcNow
    };
    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
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
        AvatarImageData = NormalizeImageData(snapshot.AvatarImageData, 512 * 1024),
        OwnedShips = NormalizeOwnedShips(snapshot.OwnedShips),
        LastUpdated = DateTimeOffset.UtcNow
    };

    var authorizedUser = GetAuthorizedUserName(request, users);
    if (!string.IsNullOrWhiteSpace(authorizedUser) &&
        users.TryGetValue(authorizedUser, out var account))
    {
        if (IsUnboundGameName(account.GameName, account))
        {
            users[authorizedUser] = account with
            {
                GameName = normalized.Name,
                LastLogin = DateTimeOffset.UtcNow
            };
        }
        else if (!string.IsNullOrWhiteSpace(account.GameName) &&
                 !account.GameName.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }
    }

    players.AddOrUpdate(normalized.Name, normalized, (_, _) => normalized);
    RemovePlayerFromStaleFleets(fleets, normalized);
    UpsertFleetMemberFromPlayer(fleets, normalized);
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(normalized);
});

app.MapPost("/api/fleets/members/remove", async (HttpRequest request, FleetMemberMutationRequest mutation) =>
{
    if (string.IsNullOrWhiteSpace(mutation.FleetCode) || string.IsNullOrWhiteSpace(mutation.TargetGameName))
    {
        return Results.BadRequest(new { error = "Fleet code and target player are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = mutation.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    var isFleetOwner = IsFleetOwner(fleet, account);
    var canRemoveMembers = isFleetOwner ||
                           HasFleetPermission(fleet, account, permission => permission.CanRemoveMembers);
    if (!canRemoveMembers)
    {
        return Results.Unauthorized();
    }

    var targetName = mutation.TargetGameName.Trim();
    var targetAliases = ExpandIdentityAliases(targetName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (MatchesAccountIdentity(targetName, account))
    {
        return Results.BadRequest(new { error = "The fleet commander cannot remove themselves." });
    }

    if (!isFleetOwner && IsPrivilegedFleetMember(fleet, targetName))
    {
        return Results.Unauthorized();
    }

    var removed = false;
    foreach (var pair in players.ToArray())
    {
        if (MatchesPlayerIdentity(targetName, pair.Value) ||
            IdentityContainsAny(pair.Key, targetAliases))
        {
            players[pair.Key] = pair.Value with
            {
                Fleet = "No Fleet",
                Squad = "Unassigned",
                LastUpdated = DateTimeOffset.UtcNow
            };
            removed = true;
        }
    }

    fleets[fleetCode] = fleet with
    {
        MemberPermissions = RemoveFleetPermissionsForAliases(fleet.MemberPermissions, targetAliases),
        Members = RemoveFleetMembersForAliases(fleet.Members, targetAliases),
        Ships = RemoveFleetShipsForAliases(fleet.Ships, targetAliases),
        ActionPlans = RemoveActionPlanParticipants(fleet.ActionPlans, null, targetAliases),
        Applications = RemoveFleetApplicationsForAliases(fleet.Applications, targetAliases),
        EventLog = AddFleetLog(fleet.EventLog, "成员", "移除成员", $"{targetName} 被移出舰队"),
        LastUpdated = DateTimeOffset.UtcNow
    };

    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new { removed, target = targetName });
});

app.MapPost("/api/fleets/squads/members/remove", async (HttpRequest request, FleetSquadMemberMutationRequest mutation) =>
{
    if (string.IsNullOrWhiteSpace(mutation.FleetCode) ||
        string.IsNullOrWhiteSpace(mutation.SquadName) ||
        string.IsNullOrWhiteSpace(mutation.TargetGameName))
    {
        return Results.BadRequest(new { error = "Fleet code, squad name and target player are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = mutation.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    var squadName = mutation.SquadName.Trim();
    var squad = (fleet.Squads ?? []).FirstOrDefault(item =>
        item.Name.Equals(squadName, StringComparison.OrdinalIgnoreCase));
    if (squad is null)
    {
        return Results.NotFound(new { error = "Squad not found." });
    }

    var requesterAliases = BuildAccountAliases(account);
    var isFleetOwner = IsFleetOwner(fleet, account);
    var canRemoveMembers = HasFleetPermission(fleet, account, permission => permission.CanRemoveMembers);
    var isSquadCommander = IdentityContainsAny(squad.Commander, requesterAliases);
    if (!isFleetOwner && !canRemoveMembers && !isSquadCommander)
    {
        return Results.Unauthorized();
    }

    var targetName = mutation.TargetGameName.Trim();
    var targetAliases = ExpandIdentityAliases(targetName)
        .Concat(ExpandIdentityAliases(mutation.TargetCallsign))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (MatchesIdentitySet(account, targetAliases))
    {
        return Results.BadRequest(new { error = "You cannot remove yourself from the squad." });
    }

    if (IdentityContainsAny(squad.Commander, targetAliases))
    {
        return Results.BadRequest(new { error = "Squad commander cannot be removed from their own squad." });
    }

    if (!isFleetOwner && IsPrivilegedFleetMember(fleet, targetName))
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var removed = false;
    foreach (var pair in players.ToArray())
    {
        var playerAliases = ExpandIdentityAliases(pair.Value.Name)
            .Concat(ExpandIdentityAliases(pair.Value.Callsign))
            .Concat(ExpandIdentityAliases(pair.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (playerAliases.Overlaps(targetAliases) &&
            Normalize(pair.Value.Squad, "Unassigned").Equals(squadName, StringComparison.OrdinalIgnoreCase))
        {
            players[pair.Key] = pair.Value with
            {
                Squad = "Unassigned",
                LastUpdated = now
            };
            removed = true;
        }
    }

    var members = NormalizeFleetMembers(fleet.Members, fleet.MemberPermissions)
        .Select(member =>
            (IdentityContainsAny(member.GameName, targetAliases) ||
             IdentityContainsAny(member.Callsign, targetAliases)) &&
            Normalize(member.SquadName, "Unassigned").Equals(squadName, StringComparison.OrdinalIgnoreCase)
                ? member with { SquadName = "Unassigned", LastUpdated = now }
                : member)
        .ToArray();
    removed = removed || members.Any(member =>
        (IdentityContainsAny(member.GameName, targetAliases) ||
         IdentityContainsAny(member.Callsign, targetAliases)) &&
        Normalize(member.SquadName, "Unassigned").Equals("Unassigned", StringComparison.OrdinalIgnoreCase));

    var ships = NormalizeFleetShips(fleet.Ships)
        .Select(ship =>
            (IdentityContainsAny(ship.OwnerGameName, targetAliases) ||
             IdentityContainsAny(ship.OwnerCallsign, targetAliases)) &&
            Normalize(ship.OwnerSquad, "未加入小队").Equals(squadName, StringComparison.OrdinalIgnoreCase)
                ? ship with { OwnerSquad = "未加入小队" }
                : ship)
        .ToArray();

    var targetDisplay = string.IsNullOrWhiteSpace(mutation.TargetCallsign)
        ? targetName
        : $"{mutation.TargetCallsign} ({targetName})";
    var updated = fleet with
    {
        Members = members,
        Ships = ships,
        EventLog = AddFleetLog(fleet.EventLog, "成员", "移除小队成员", $"{targetDisplay} 被移出 {squadName}"),
        LastUpdated = now
    };

    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new { removed, target = targetName, squad = squadName });
});

app.MapPost("/api/fleets/transfer-commander", async (HttpRequest request, FleetCommanderTransferRequest transfer) =>
{
    if (string.IsNullOrWhiteSpace(transfer.FleetCode) || string.IsNullOrWhiteSpace(transfer.TargetGameName))
    {
        return Results.BadRequest(new { error = "Fleet code and target player are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = transfer.FleetCode.Trim();
    if (!fleets.TryGetValue(fleetCode, out var fleet))
    {
        return Results.NotFound(new { error = "Fleet not found." });
    }

    if (!IsFleetOwner(fleet, account))
    {
        return Results.Unauthorized();
    }

    var targetName = transfer.TargetGameName.Trim();
    var targetAccount = users.Values.FirstOrDefault(user => MatchesAccountIdentity(targetName, user));
    if (targetAccount is null)
    {
        return Results.BadRequest(new { error = "Target player must have a StarBridge account." });
    }

    var targetDisplayName = string.IsNullOrWhiteSpace(targetAccount.Callsign)
        ? Normalize(targetAccount.GameName, targetName)
        : $"{targetAccount.Callsign} ({Normalize(targetAccount.GameName, targetName)})";
    var updated = fleet with
    {
        Commander = targetDisplayName,
        OwnerAccount = targetAccount.UserName,
        MemberPermissions = (fleet.MemberPermissions ?? [])
            .Where(permission => !MatchesAccountIdentity(permission.GameName, targetAccount) &&
                                 !MatchesAccountIdentity(permission.Callsign, targetAccount))
            .Append(new NetworkFleetMemberPermissionSnapshot(
                Normalize(targetAccount.GameName, targetName),
                targetAccount.Callsign,
                "舰队指挥官",
                true,
                true,
                true,
                true,
                true,
                DateTimeOffset.UtcNow))
            .ToArray(),
        Members = UpsertFleetMember(
            fleet.Members,
            new NetworkFleetMemberSnapshot(
                Normalize(targetAccount.GameName, targetName),
                targetAccount.Callsign,
                "舰队指挥官",
                "Unassigned",
                false,
                null,
                null,
                DateTimeOffset.UtcNow)),
        EventLog = AddFleetLog(fleet.EventLog, "权限", "转移舰队指挥官", $"{targetDisplayName} 成为新的舰队指挥官"),
        LastUpdated = DateTimeOffset.UtcNow
    };

    fleets[fleetCode] = updated;
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(updated);
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
    if (!IsRelayKeyAllowed(request, serverKey))
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

static bool IsRelayKeyAllowed(HttpRequest request, string? serverKey)
{
    return !string.IsNullOrWhiteSpace(serverKey) &&
           request.Headers.TryGetValue("X-StarBridge-Key", out var provided) &&
           provided.ToString().Equals(serverKey, StringComparison.Ordinal);
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

static string? NormalizeImageData(string? value, int maxBytes)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var trimmed = value.Trim();
    var payload = trimmed;
    var commaIndex = payload.IndexOf(',');
    if (payload.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
    {
        payload = payload[(commaIndex + 1)..];
    }

    try
    {
        var bytes = Convert.FromBase64String(payload);
        if (bytes.Length == 0 || bytes.Length > maxBytes)
        {
            return null;
        }

        return trimmed;
    }
    catch
    {
        return null;
    }
}

static NetworkOwnedShipSnapshot[] NormalizeOwnedShips(NetworkOwnedShipSnapshot[]? ships)
{
    return (ships ?? [])
        .Where(ship => !string.IsNullOrWhiteSpace(ship.Code))
        .Select(ship => ship with
        {
            Code = ship.Code.Trim(),
            DisplayName = Normalize(ship.DisplayName, ship.Code.Trim()),
            Source = Normalize(ship.Source, "Unknown"),
            ImportedAt = ship.ImportedAt == default ? DateTimeOffset.UtcNow : ship.ImportedAt
        })
        .GroupBy(ship => ship.Code, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(ship => ship.ImportedAt).First())
        .OrderBy(ship => ship.ImportedAt)
        .ToArray();
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
            squads[squad.Name.Trim()] = NormalizeSquad(squad);
        }
    }

    foreach (var squad in incoming ?? [])
    {
        if (!string.IsNullOrWhiteSpace(squad.Name))
        {
            var normalized = NormalizeSquad(squad);
            if (squads.TryGetValue(normalized.Name, out var current) &&
                string.IsNullOrWhiteSpace(normalized.EmblemImageData) &&
                !string.IsNullOrWhiteSpace(current.EmblemImageData))
            {
                normalized = normalized with { EmblemImageData = current.EmblemImageData };
            }

            squads[normalized.Name] = normalized;
        }
    }

    return squads.Values.OrderBy(squad => squad.Name).ToArray();
}

static NetworkSquadSnapshot NormalizeSquad(NetworkSquadSnapshot squad)
{
    var name = Normalize(squad.Name, "Unnamed");
    return squad with
    {
        Name = name,
        Commander = Normalize(squad.Commander, "Unassigned"),
        Type = Normalize(squad.Type, "Assault"),
        Description = Normalize(squad.Description, "No squad briefing yet."),
        Mission = Normalize(squad.Mission, "Standby"),
        RallyPoint = Normalize(squad.RallyPoint, "Use Global"),
        EmblemImageData = NormalizeImageData(squad.EmblemImageData, 512 * 1024)
    };
}

static NetworkFleetTaskHistorySnapshot[] NormalizeFleetTaskHistory(NetworkFleetTaskHistorySnapshot[]? history)
{
    return (history ?? [])
        .Where(task => !string.IsNullOrWhiteSpace(task.Key) ||
                       !string.IsNullOrWhiteSpace(task.Title))
        .Select(task => task with
        {
            Key = string.IsNullOrWhiteSpace(task.Key) ? Guid.NewGuid().ToString("N") : task.Key.Trim(),
            Title = Normalize(task.Title, "未命名任务"),
            Brief = Normalize(task.Brief, "未指定"),
            Status = Normalize(task.Status, "进行中"),
            Participants = Normalize(task.Participants, "参与范围 / 未指定"),
            Rally = Normalize(task.Rally, "集结点 / 未发布"),
            RequiredShip = Normalize(task.RequiredShip, "指定舰船 / 无"),
            PublishedAtText = Normalize(task.PublishedAtText, DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
        })
        .GroupBy(task => task.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Last())
        .Take(200)
        .ToArray();
}

static NetworkFleetTaskHistorySnapshot[] MergeFleetTaskHistory(
    NetworkFleetTaskHistorySnapshot[]? existing,
    NetworkFleetTaskHistorySnapshot[]? incoming)
{
    return NormalizeFleetTaskHistory((existing ?? []).Concat(incoming ?? []).ToArray());
}

static NetworkFleetApplicationSnapshot[] NormalizeFleetApplications(NetworkFleetApplicationSnapshot[]? applications)
{
    return (applications ?? [])
        .Where(application => !string.IsNullOrWhiteSpace(application.ApplicantGameName) ||
                              !string.IsNullOrWhiteSpace(application.ApplicantAccount))
        .Select(application =>
        {
            var account = Normalize(application.ApplicantAccount, "");
            var gameName = Normalize(application.ApplicantGameName, Normalize(account, "Unknown"));
            return application with
            {
                Id = Normalize(application.Id, BuildFleetApplicationId(account, gameName)),
                ApplicantGameName = gameName,
                ApplicantCallsign = Normalize(application.ApplicantCallsign, ""),
                ApplicantAccount = account,
                Message = Normalize(application.Message, ""),
                Status = Normalize(application.Status, "Pending"),
                CreatedAt = application.CreatedAt == default ? DateTimeOffset.UtcNow : application.CreatedAt,
                AvatarImageData = NormalizeImageData(application.AvatarImageData, 512 * 1024)
            };
        })
        .GroupBy(BuildFleetApplicationKey, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(application => application.CreatedAt).First())
        .OrderByDescending(application => application.CreatedAt)
        .ToArray();
}

static string BuildFleetApplicationId(string? account, string? gameName)
{
    var source = Normalize(account, Normalize(gameName, Guid.NewGuid().ToString("N")));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..16];
}

static string BuildFleetApplicationKey(NetworkFleetApplicationSnapshot application)
{
    return Normalize(application.ApplicantAccount, Normalize(application.ApplicantGameName, application.Id));
}

static bool RequiresFleetApplication(string? joinPolicy)
{
    var value = Normalize(joinPolicy, "");
    return value.Contains("申请", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("审核", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("application", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("approval", StringComparison.OrdinalIgnoreCase);
}

static HashSet<string> BuildAccountAliases(UserAccount account)
{
    var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    AddIdentityAliases(aliases, account.UserName);
    AddIdentityAliases(aliases, account.Email);
    AddIdentityAliases(aliases, account.GameName);
    AddIdentityAliases(aliases, account.Callsign);
    return aliases;
}

static NetworkPlayerSnapshot? FindPlayerForAccount(
    ConcurrentDictionary<string, NetworkPlayerSnapshot> players,
    UserAccount account)
{
    return players.Values.FirstOrDefault(player =>
        MatchesAccountIdentity(player.Name, account) ||
        MatchesAccountIdentity(player.Callsign, account));
}

static NetworkFleetApplicationSnapshot BuildFleetApplication(
    UserAccount account,
    NetworkPlayerSnapshot? player,
    string? message)
{
    var gameName = Normalize(player?.Name, Normalize(account.GameName, account.UserName));
    var callsign = Normalize(player?.Callsign, Normalize(account.Callsign, ""));
    return new NetworkFleetApplicationSnapshot(
        BuildFleetApplicationId(account.UserName, gameName),
        gameName,
        callsign,
        account.UserName,
        Normalize(message, ""),
        "Pending",
        DateTimeOffset.UtcNow,
        player?.AvatarImageData);
}

static NetworkFleetApplicationSnapshot[] UpsertFleetApplication(
    NetworkFleetApplicationSnapshot[]? applications,
    NetworkFleetApplicationSnapshot application)
{
    var aliases = BuildApplicationAliases(application);
    return NormalizeFleetApplications(applications)
        .Where(item => !ApplicationContainsAny(item, aliases))
        .Append(application)
        .ToArray();
}

static HashSet<string> BuildApplicationAliases(NetworkFleetApplicationSnapshot application, UserAccount? account = null)
{
    var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    AddIdentityAliases(aliases, application.Id);
    AddIdentityAliases(aliases, application.ApplicantGameName);
    AddIdentityAliases(aliases, application.ApplicantCallsign);
    AddIdentityAliases(aliases, application.ApplicantAccount);
    if (account is not null)
    {
        aliases.UnionWith(BuildAccountAliases(account));
    }

    return aliases;
}

static bool ApplicationContainsAny(NetworkFleetApplicationSnapshot application, HashSet<string> aliases)
{
    return IdentityContainsAny(application.Id, aliases) ||
           IdentityContainsAny(application.ApplicantGameName, aliases) ||
           IdentityContainsAny(application.ApplicantCallsign, aliases) ||
           IdentityContainsAny(application.ApplicantAccount, aliases);
}

static bool ApplicationMatchesAccount(NetworkFleetApplicationSnapshot application, UserAccount account)
{
    return ApplicationContainsAny(application, BuildAccountAliases(account));
}

static NetworkFleetApplicationSnapshot[] RemoveFleetApplicationsForAliases(
    NetworkFleetApplicationSnapshot[]? applications,
    HashSet<string> aliases)
{
    return NormalizeFleetApplications(applications)
        .Where(application => !ApplicationContainsAny(application, aliases))
        .ToArray();
}

static string FormatAccountIdentity(UserAccount account, NetworkPlayerSnapshot? player)
{
    var gameName = Normalize(player?.Name, Normalize(account.GameName, account.UserName));
    var callsign = Normalize(player?.Callsign, Normalize(account.Callsign, ""));
    return string.IsNullOrWhiteSpace(callsign)
        ? gameName
        : $"{callsign} ({gameName})";
}

static string FormatApplicationIdentity(NetworkFleetApplicationSnapshot application)
{
    return string.IsNullOrWhiteSpace(application.ApplicantCallsign)
        ? application.ApplicantGameName
        : $"{application.ApplicantCallsign} ({application.ApplicantGameName})";
}

static NetworkFleetMemberSnapshot BuildFleetMemberFromAccount(UserAccount account, NetworkPlayerSnapshot? player)
{
    return new NetworkFleetMemberSnapshot(
        Normalize(player?.Name, Normalize(account.GameName, account.UserName)),
        Normalize(player?.Callsign, Normalize(account.Callsign, "")),
        "成员",
        Normalize(player?.Squad, "Unassigned"),
        player?.Online ?? false,
        Normalize(player?.Ship, "Unknown"),
        Normalize(player?.Location, "Unknown"),
        player?.LastUpdated == default ? DateTimeOffset.UtcNow : player?.LastUpdated ?? DateTimeOffset.UtcNow,
        player?.AvatarImageData);
}

static NetworkFleetMemberSnapshot BuildFleetMemberFromApplication(
    NetworkFleetApplicationSnapshot application,
    NetworkPlayerSnapshot? player)
{
    return new NetworkFleetMemberSnapshot(
        Normalize(player?.Name, application.ApplicantGameName),
        Normalize(player?.Callsign, Normalize(application.ApplicantCallsign, "")),
        "成员",
        Normalize(player?.Squad, "Unassigned"),
        player?.Online ?? false,
        Normalize(player?.Ship, "Unknown"),
        Normalize(player?.Location, "Unknown"),
        player?.LastUpdated == default ? DateTimeOffset.UtcNow : player?.LastUpdated ?? DateTimeOffset.UtcNow,
        NormalizeImageData(player?.AvatarImageData, 512 * 1024) ??
        NormalizeImageData(application.AvatarImageData, 512 * 1024));
}

static void RemoveAccountFromOtherFleets(
    ConcurrentDictionary<string, NetworkFleetSnapshot> fleets,
    ConcurrentDictionary<string, NetworkPlayerSnapshot> players,
    string keepFleetCode,
    UserAccount account)
{
    var aliases = BuildAccountAliases(account);
    foreach (var pair in fleets.ToArray())
    {
        if (pair.Key.Equals(keepFleetCode, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var fleet = pair.Value;
        if (IsFleetOwner(fleet, account) ||
            !FleetContainsAnyIdentity(fleet, aliases))
        {
            continue;
        }

        fleets[pair.Key] = RemoveFleetIdentity(fleet, aliases) with
        {
            EventLog = AddFleetLog(
                fleet.EventLog,
                "成员",
                "玩家离开",
                $"{FormatAccountIdentity(account, FindPlayerForAccount(players, account))} 离开舰队"),
            LastUpdated = DateTimeOffset.UtcNow
        };
    }
}

static bool FleetContainsAnyIdentity(NetworkFleetSnapshot fleet, HashSet<string> aliases)
{
    return (fleet.Members ?? []).Any(member =>
               IdentityContainsAny(member.GameName, aliases) ||
               IdentityContainsAny(member.Callsign, aliases)) ||
           (fleet.MemberPermissions ?? []).Any(permission =>
               IdentityContainsAny(permission.GameName, aliases) ||
               IdentityContainsAny(permission.Callsign, aliases)) ||
           (fleet.Ships ?? []).Any(ship =>
               IdentityContainsAny(ship.OwnerGameName, aliases) ||
               IdentityContainsAny(ship.OwnerCallsign, aliases)) ||
           (fleet.ActionPlans ?? []).Any(plan =>
               (plan.Participants ?? []).Any(participant =>
                   IdentityContainsAny(participant.GameName, aliases) ||
                   IdentityContainsAny(participant.Callsign, aliases))) ||
           (fleet.Applications ?? []).Any(application => ApplicationContainsAny(application, aliases));
}

static NetworkFleetSnapshot RemoveFleetIdentity(NetworkFleetSnapshot fleet, HashSet<string> aliases)
{
    return fleet with
    {
        Members = RemoveFleetMembersForAliases(fleet.Members, aliases),
        MemberPermissions = RemoveFleetPermissionsForAliases(fleet.MemberPermissions, aliases),
        Ships = RemoveFleetShipsForAliases(fleet.Ships, aliases),
        Applications = RemoveFleetApplicationsForAliases(fleet.Applications, aliases),
        ActionPlans = RemoveActionPlanParticipants(fleet.ActionPlans, null, aliases),
        LastUpdated = DateTimeOffset.UtcNow
    };
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
        string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
        true);
}

static AuthResponse ToAuthResponse(UserAccount account)
{
    return new AuthResponse(
        account.UserName,
        account.Email,
        account.Callsign,
        account.GameName,
        account.AuthToken,
        account.AllowEmailNotifications);
}

static NetworkFleetMemberSnapshot[] BuildFleetMembers(
    NetworkFleetSnapshot fleet,
    IEnumerable<NetworkPlayerSnapshot> fleetPlayers)
{
    var now = DateTimeOffset.UtcNow;
    var timeout = TimeSpan.FromSeconds(120);
    var members = new Dictionary<string, NetworkFleetMemberSnapshot>(StringComparer.OrdinalIgnoreCase);

    foreach (var member in NormalizeFleetMembers(fleet.Members, fleet.MemberPermissions))
    {
        members[member.GameName] = member;
    }

    foreach (var permission in fleet.MemberPermissions ?? [])
    {
        if (string.IsNullOrWhiteSpace(permission.GameName))
        {
            continue;
        }

        var gameName = permission.GameName.Trim();
        if (members.TryGetValue(gameName, out var existing))
        {
            members[gameName] = existing with
            {
                Callsign = Normalize(permission.Callsign, existing.Callsign ?? ""),
                RoleTitle = Normalize(permission.RoleTitle, existing.RoleTitle),
                LastUpdated = Max(existing.LastUpdated, permission.UpdatedAt)
            };
        }
        else
        {
            members[gameName] = new NetworkFleetMemberSnapshot(
                gameName,
                Normalize(permission.Callsign, ""),
                Normalize(permission.RoleTitle, "成员"),
                "Unassigned",
                false,
                null,
                null,
                permission.UpdatedAt,
                null);
        }
    }

    foreach (var player in fleetPlayers)
    {
        if (string.IsNullOrWhiteSpace(player.Name))
        {
            continue;
        }

        var gameName = player.Name.Trim();
        var roleTitle = members.TryGetValue(gameName, out var existing)
            ? existing.RoleTitle
            : "成员";
        members[gameName] = new NetworkFleetMemberSnapshot(
            gameName,
            Normalize(player.Callsign, members.TryGetValue(gameName, out existing) ? existing.Callsign ?? "" : ""),
            roleTitle,
            Normalize(player.Squad, "Unassigned"),
            player.Online,
            Normalize(player.Ship, "Unknown"),
            Normalize(player.Location, "Unknown"),
            player.LastUpdated,
            player.AvatarImageData);
    }

    return members.Values
        .Select(member => ApplyMemberOnlineTimeout(member, now, timeout))
        .OrderByDescending(member => member.Online)
        .ThenBy(member => member.SquadName)
        .ThenBy(member => member.Callsign ?? member.GameName)
        .ToArray();
}

static NetworkFleetMemberSnapshot[] NormalizeFleetMembers(
    NetworkFleetMemberSnapshot[]? members,
    NetworkFleetMemberPermissionSnapshot[]? permissions = null)
{
    var rows = new Dictionary<string, NetworkFleetMemberSnapshot>(StringComparer.OrdinalIgnoreCase);
    foreach (var member in members ?? [])
    {
        if (string.IsNullOrWhiteSpace(member.GameName))
        {
            continue;
        }

        var gameName = member.GameName.Trim();
        rows[gameName] = member with
        {
            GameName = gameName,
            Callsign = Normalize(member.Callsign, ""),
            RoleTitle = Normalize(member.RoleTitle, "成员"),
            SquadName = Normalize(member.SquadName, "Unassigned"),
            Ship = Normalize(member.Ship, "Unknown"),
            Location = Normalize(member.Location, "Unknown"),
            AvatarImageData = NormalizeImageData(member.AvatarImageData, 512 * 1024),
            LastUpdated = member.LastUpdated == default ? DateTimeOffset.UtcNow : member.LastUpdated
        };
    }

    foreach (var permission in permissions ?? [])
    {
        if (string.IsNullOrWhiteSpace(permission.GameName) ||
            rows.ContainsKey(permission.GameName.Trim()))
        {
            continue;
        }

        rows[permission.GameName.Trim()] = new NetworkFleetMemberSnapshot(
            permission.GameName.Trim(),
            Normalize(permission.Callsign, ""),
            Normalize(permission.RoleTitle, "成员"),
            "Unassigned",
            false,
            null,
            null,
            permission.UpdatedAt == default ? DateTimeOffset.UtcNow : permission.UpdatedAt,
            null);
    }

    return rows.Values.ToArray();
}

static NetworkFleetMemberSnapshot[] MergeFleetMembers(
    NetworkFleetMemberSnapshot[]? existingMembers,
    NetworkFleetMemberSnapshot[]? incomingMembers)
{
    var rows = NormalizeFleetMembers(existingMembers)
        .ToDictionary(member => member.GameName, StringComparer.OrdinalIgnoreCase);

    foreach (var member in NormalizeFleetMembers(incomingMembers))
    {
        var memberAliases = BuildFleetMemberAliases(member);
        var existingPair = rows.FirstOrDefault(pair =>
            IdentityContainsAny(pair.Value.GameName, memberAliases) ||
            IdentityContainsAny(pair.Value.Callsign, memberAliases));

        if (existingPair.Value is not null)
        {
            rows.Remove(existingPair.Key);
            var existing = existingPair.Value;
            rows[member.GameName] = existing.LastUpdated > member.LastUpdated
                ? existing with
                {
                    Callsign = string.IsNullOrWhiteSpace(existing.Callsign)
                        ? Normalize(member.Callsign, "")
                        : existing.Callsign,
                    AvatarImageData = string.IsNullOrWhiteSpace(existing.AvatarImageData)
                        ? NormalizeImageData(member.AvatarImageData, 512 * 1024)
                        : existing.AvatarImageData
                }
                : member;
        }
        else
        {
            rows[member.GameName] = member;
        }
    }

    return rows.Values.ToArray();
}

static NetworkFleetMemberSnapshot[] FilterFleetMemberUpdatesForAccount(
    NetworkFleetMemberSnapshot[]? members,
    NetworkFleetMemberSnapshot[]? existingMembers,
    UserAccount? account,
    bool canUpdateAllMembers)
{
    var normalized = NormalizeFleetMembers(members);
    if (canUpdateAllMembers)
    {
        return normalized;
    }

    if (account is null)
    {
        return [];
    }

    var existing = NormalizeFleetMembers(existingMembers);
    return normalized
        .Where(member =>
            MatchesAccountIdentity(member.GameName, account) ||
            MatchesAccountIdentity(member.Callsign, account))
        .Select(member =>
        {
            var aliases = BuildFleetMemberAliases(member);
            var existingMember = existing.FirstOrDefault(item =>
                IdentityContainsAny(item.GameName, aliases) ||
                IdentityContainsAny(item.Callsign, aliases));

            return member with
            {
                RoleTitle = existingMember is null ? "成员" : Normalize(existingMember.RoleTitle, "成员")
            };
        })
        .ToArray();
}

static HashSet<string> BuildFleetMemberAliases(NetworkFleetMemberSnapshot member)
{
    return ExpandIdentityAliases(member.GameName)
        .Concat(ExpandIdentityAliases(member.Callsign))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static NetworkFleetShipSnapshot[] NormalizeFleetShips(NetworkFleetShipSnapshot[]? ships)
{
    return (ships ?? [])
        .Where(ship => !string.IsNullOrWhiteSpace(ship.Code) &&
                       !string.IsNullOrWhiteSpace(ship.OwnerGameName))
        .Select(ship => ship with
        {
            Code = ship.Code.Trim(),
            DisplayName = Normalize(ship.DisplayName, ship.Code.Trim()),
            OwnerGameName = ship.OwnerGameName.Trim(),
            OwnerCallsign = Normalize(ship.OwnerCallsign, ""),
            OwnerSquad = Normalize(ship.OwnerSquad, "未加入小队"),
            OwnerAvatarImageData = NormalizeImageData(ship.OwnerAvatarImageData, 512 * 1024),
            ImportedAt = ship.ImportedAt == default ? DateTimeOffset.UtcNow : ship.ImportedAt
        })
        .GroupBy(ship => BuildFleetShipKey(ship.OwnerGameName, ship.Code), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(ship => ship.ImportedAt).First())
        .OrderBy(ship => ship.ImportedAt)
        .ToArray();
}

static NetworkFleetShipSnapshot[] MergeFleetShips(
    NetworkFleetShipSnapshot[]? existingShips,
    NetworkFleetShipSnapshot[]? incomingShips,
    UserAccount? account)
{
    var existing = NormalizeFleetShips(existingShips);
    var incoming = NormalizeFleetShips(incomingShips);
    var rows = existing.ToDictionary(ship => BuildFleetShipKey(ship.OwnerGameName, ship.Code), StringComparer.OrdinalIgnoreCase);

    if (account is not null)
    {
        foreach (var key in rows
                     .Where(pair => OwnerMatchesAccount(pair.Value, account))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            rows.Remove(key);
        }

        incoming = incoming
            .Where(ship => OwnerMatchesAccount(ship, account))
            .ToArray();
    }

    foreach (var ship in incoming)
    {
        rows[BuildFleetShipKey(ship.OwnerGameName, ship.Code)] = ship;
    }

    return rows.Values
        .OrderBy(ship => ship.ImportedAt)
        .ThenBy(ship => ship.DisplayName)
        .ToArray();
}

static bool OwnerMatchesAccount(NetworkFleetShipSnapshot ship, UserAccount account)
{
    return MatchesAccountIdentity(ship.OwnerGameName, account) ||
           MatchesAccountIdentity(ship.OwnerCallsign, account);
}

static NetworkFleetShipSnapshot[] BuildFleetShipsFromPlayer(NetworkPlayerSnapshot player)
{
    return (player.OwnedShips ?? [])
        .Where(ship => !string.IsNullOrWhiteSpace(ship.Code))
        .Select(ship => new NetworkFleetShipSnapshot(
            ship.Code,
            ship.DisplayName,
            player.Name,
            player.Callsign,
            player.Squad,
            player.AvatarImageData,
            ship.ImportedAt))
        .ToArray();
}

static string BuildFleetShipKey(string? ownerGameName, string? shipCode)
{
    return $"{Normalize(ownerGameName, "unknown")}::{Normalize(shipCode, "unknown")}";
}

static NetworkFleetMemberSnapshot[] UpsertFleetMember(
    NetworkFleetMemberSnapshot[]? members,
    NetworkFleetMemberSnapshot member)
{
    var memberAliases = ExpandIdentityAliases(member.GameName)
        .Concat(ExpandIdentityAliases(member.Callsign))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var rows = NormalizeFleetMembers(members)
        .Where(item => !IdentityContainsAny(item.GameName, memberAliases) &&
                       !IdentityContainsAny(item.Callsign, memberAliases))
        .Append(member)
        .ToArray();
    return rows;
}

static NetworkFleetMemberSnapshot[] RemoveFleetMembersForAliases(
    NetworkFleetMemberSnapshot[]? members,
    HashSet<string> aliases)
{
    return NormalizeFleetMembers(members)
        .Where(member => !IdentityContainsAny(member.GameName, aliases) &&
                         !IdentityContainsAny(member.Callsign, aliases))
        .ToArray();
}

static NetworkFleetMemberPermissionSnapshot[] RemoveFleetPermissionsForAliases(
    NetworkFleetMemberPermissionSnapshot[]? permissions,
    HashSet<string> aliases)
{
    return (permissions ?? [])
        .Where(permission => !IdentityContainsAny(permission.GameName, aliases) &&
                             !IdentityContainsAny(permission.Callsign, aliases))
        .ToArray();
}

static NetworkFleetShipSnapshot[] RemoveFleetShipsForAliases(
    NetworkFleetShipSnapshot[]? ships,
    HashSet<string> aliases)
{
    return NormalizeFleetShips(ships)
        .Where(ship => !IdentityContainsAny(ship.OwnerGameName, aliases) &&
                       !IdentityContainsAny(ship.OwnerCallsign, aliases))
        .ToArray();
}

static void UpsertFleetMemberFromPlayer(
    ConcurrentDictionary<string, NetworkFleetSnapshot> fleets,
    NetworkPlayerSnapshot player)
{
    if (string.IsNullOrWhiteSpace(player.Fleet) ||
        player.Fleet.Equals("No Fleet", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    foreach (var pair in fleets.ToArray())
    {
        var fleet = pair.Value;
        if (!player.Fleet.Equals(fleet.Name, StringComparison.OrdinalIgnoreCase) &&
            !player.Fleet.Equals(fleet.Code, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var existed = (fleet.Members ?? []).Any(member =>
            MatchesPlayerIdentity(member.GameName, player) ||
            MatchesPlayerIdentity(member.Callsign, player)) ||
            (fleet.MemberPermissions ?? []).Any(permission =>
                MatchesPlayerIdentity(permission.GameName, player) ||
                MatchesPlayerIdentity(permission.Callsign, player));

        var permission = (fleet.MemberPermissions ?? []).FirstOrDefault(item =>
            MatchesPlayerIdentity(item.GameName, player) ||
            MatchesPlayerIdentity(item.Callsign, player));
        var member = new NetworkFleetMemberSnapshot(
            player.Name,
            Normalize(player.Callsign, permission?.Callsign ?? ""),
            Normalize(permission?.RoleTitle, "成员"),
            Normalize(player.Squad, "Unassigned"),
            player.Online,
            Normalize(player.Ship, "Unknown"),
            Normalize(player.Location, "Unknown"),
            player.LastUpdated,
            player.AvatarImageData);

        var updated = fleet with
        {
            Members = UpsertFleetMember(fleet.Members, member),
            MemberPermissions = EnsureFleetPermission(fleet.MemberPermissions, member),
            Ships = MergeFleetShips(fleet.Ships, BuildFleetShipsFromPlayer(player), null),
            EventLog = existed
                ? fleet.EventLog
                : AddFleetLog(fleet.EventLog, "成员", "玩家加入", $"{DisplayMember(member)} 加入舰队"),
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[pair.Key] = updated;
        return;
    }
}

static void RemovePlayerFromStaleFleets(
    ConcurrentDictionary<string, NetworkFleetSnapshot> fleets,
    NetworkPlayerSnapshot player)
{
    if (string.IsNullOrWhiteSpace(player.Name))
    {
        return;
    }

    foreach (var pair in fleets.ToArray())
    {
        var fleet = pair.Value;
        if (PlayerBelongsToFleet(player, fleet) ||
            !FleetContainsPlayerIdentity(fleet, player) ||
            IsFleetCommanderIdentity(fleet, player))
        {
            continue;
        }

        var playerAliases = ExpandIdentityAliases(player.Name)
            .Concat(ExpandIdentityAliases(player.Callsign))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        fleets[pair.Key] = fleet with
        {
            Members = RemoveFleetMembersForAliases(fleet.Members, playerAliases),
            MemberPermissions = RemoveFleetPermissionsForAliases(fleet.MemberPermissions, playerAliases),
            Ships = RemoveFleetShipsForAliases(fleet.Ships, playerAliases),
            ActionPlans = RemoveActionPlanParticipants(fleet.ActionPlans, null, playerAliases),
            Applications = RemoveFleetApplicationsForAliases(fleet.Applications, playerAliases),
            EventLog = AddFleetLog(
                fleet.EventLog,
                "成员",
                "玩家离开",
                $"{FormatPlayerIdentity(player)} 离开舰队"),
            LastUpdated = DateTimeOffset.UtcNow
        };
    }
}

static bool PlayerBelongsToFleet(NetworkPlayerSnapshot player, NetworkFleetSnapshot fleet)
{
    return !string.IsNullOrWhiteSpace(player.Fleet) &&
           !player.Fleet.Equals("No Fleet", StringComparison.OrdinalIgnoreCase) &&
           (player.Fleet.Equals(fleet.Name, StringComparison.OrdinalIgnoreCase) ||
            player.Fleet.Equals(fleet.Code, StringComparison.OrdinalIgnoreCase));
}

static bool FleetContainsPlayerIdentity(NetworkFleetSnapshot fleet, NetworkPlayerSnapshot player)
{
    return (fleet.Members ?? []).Any(member =>
               MatchesPlayerIdentity(member.GameName, player) ||
               MatchesPlayerIdentity(member.Callsign, player)) ||
           (fleet.MemberPermissions ?? []).Any(permission =>
               MatchesPlayerIdentity(permission.GameName, player) ||
               MatchesPlayerIdentity(permission.Callsign, player)) ||
           (fleet.Ships ?? []).Any(ship =>
               MatchesPlayerIdentity(ship.OwnerGameName, player) ||
               MatchesPlayerIdentity(ship.OwnerCallsign, player));
}

static bool IsFleetCommanderIdentity(NetworkFleetSnapshot fleet, NetworkPlayerSnapshot player)
{
    if (MatchesPlayerIdentity(fleet.Commander, player))
    {
        return true;
    }

    return (fleet.MemberPermissions ?? []).Any(permission =>
        Normalize(permission.RoleTitle, "").Equals("舰队指挥官", StringComparison.OrdinalIgnoreCase) &&
        (MatchesPlayerIdentity(permission.GameName, player) ||
         MatchesPlayerIdentity(permission.Callsign, player)));
}

static bool MatchesPlayerIdentity(string? value, NetworkPlayerSnapshot player)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var aliases = ExpandIdentityAliases(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    return aliases.Contains(player.Name) ||
           (!string.IsNullOrWhiteSpace(player.Callsign) && aliases.Contains(player.Callsign));
}

static string FormatPlayerIdentity(NetworkPlayerSnapshot player)
{
    return string.IsNullOrWhiteSpace(player.Callsign)
        ? player.Name
        : $"{player.Callsign} ({player.Name})";
}

static NetworkFleetMemberPermissionSnapshot[] EnsureFleetPermission(
    NetworkFleetMemberPermissionSnapshot[]? permissions,
    NetworkFleetMemberSnapshot member)
{
    var memberAliases = ExpandIdentityAliases(member.GameName)
        .Concat(ExpandIdentityAliases(member.Callsign))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if ((permissions ?? []).Any(permission =>
        IdentityContainsAny(permission.GameName, memberAliases) ||
        IdentityContainsAny(permission.Callsign, memberAliases)))
    {
        return permissions ?? [];
    }

    return (permissions ?? [])
        .Append(new NetworkFleetMemberPermissionSnapshot(
            member.GameName,
            member.Callsign,
            member.RoleTitle,
            false,
            false,
            false,
            false,
            false,
            DateTimeOffset.UtcNow))
        .ToArray();
}

static NetworkFleetMemberPermissionSnapshot[] NormalizeFleetMemberPermissions(
    NetworkFleetMemberPermissionSnapshot[]? permissions)
{
    var rows = new Dictionary<string, NetworkFleetMemberPermissionSnapshot>(StringComparer.OrdinalIgnoreCase);
    foreach (var permission in permissions ?? [])
    {
        if (string.IsNullOrWhiteSpace(permission.GameName))
        {
            continue;
        }

        var normalized = NormalizeFleetMemberPermission(permission);
        rows[BuildPermissionKey(normalized)] = normalized;
    }

    return rows.Values
        .OrderBy(permission => Normalize(permission.RoleTitle, "成员"))
        .ThenBy(permission => Normalize(permission.Callsign, permission.GameName))
        .ToArray();
}

static NetworkFleetMemberPermissionSnapshot NormalizeFleetMemberPermission(
    NetworkFleetMemberPermissionSnapshot permission)
{
    return permission with
    {
        GameName = permission.GameName.Trim(),
        Callsign = Normalize(permission.Callsign, ""),
        RoleTitle = Normalize(permission.RoleTitle, "成员"),
        UpdatedAt = permission.UpdatedAt == default ? DateTimeOffset.UtcNow : permission.UpdatedAt
    };
}

static NetworkFleetMemberPermissionSnapshot[] MergeFleetMemberPermissions(
    NetworkFleetMemberPermissionSnapshot[]? existingPermissions,
    NetworkFleetMemberPermissionSnapshot[]? incomingPermissions)
{
    var rows = NormalizeFleetMemberPermissions(existingPermissions).ToList();
    foreach (var incoming in NormalizeFleetMemberPermissions(incomingPermissions))
    {
        var incomingAliases = ExpandIdentityAliases(incoming.GameName)
            .Concat(ExpandIdentityAliases(incoming.Callsign))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingIndex = rows.FindIndex(existing =>
            IdentityContainsAny(existing.GameName, incomingAliases) ||
            IdentityContainsAny(existing.Callsign, incomingAliases));

        if (existingIndex < 0)
        {
            rows.Add(incoming);
            continue;
        }

        var existing = rows[existingIndex];
        rows[existingIndex] = incoming.UpdatedAt >= existing.UpdatedAt ? incoming : existing;
    }

    return NormalizeFleetMemberPermissions(rows.ToArray());
}

static string BuildPermissionKey(NetworkFleetMemberPermissionSnapshot permission)
{
    return Normalize(permission.GameName, permission.Callsign ?? "unknown").Trim();
}

static NetworkFleetEventLogSnapshot[] NormalizeFleetEventLogs(NetworkFleetEventLogSnapshot[]? logs)
{
    return (logs ?? [])
        .Where(log => !string.IsNullOrWhiteSpace(log.Id) ||
                      !string.IsNullOrWhiteSpace(log.Title) ||
                      !string.IsNullOrWhiteSpace(log.Detail))
        .Select(log => log with
        {
            Id = string.IsNullOrWhiteSpace(log.Id) ? Guid.NewGuid().ToString("N") : log.Id.Trim(),
            Timestamp = log.Timestamp == default ? DateTimeOffset.UtcNow : log.Timestamp,
            Type = Normalize(log.Type, "舰队"),
            Title = Normalize(log.Title, ""),
            Detail = Normalize(log.Detail, "")
        })
        .GroupBy(log => log.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(log => log.Timestamp).First())
        .OrderByDescending(log => log.Timestamp)
        .Take(500)
        .ToArray();
}

static NetworkFleetEventLogSnapshot[] MergeFleetEventLogs(
    NetworkFleetEventLogSnapshot[]? existingLogs,
    NetworkFleetEventLogSnapshot[]? incomingLogs)
{
    return NormalizeFleetEventLogs((existingLogs ?? []).Concat(incomingLogs ?? []).ToArray());
}

static NetworkFleetEventLogSnapshot[] AddFleetLog(
    NetworkFleetEventLogSnapshot[]? existingLogs,
    string type,
    string title,
    string detail)
{
    var row = new NetworkFleetEventLogSnapshot(
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow,
        type,
        title,
        detail);
    return NormalizeFleetEventLogs((existingLogs ?? []).Prepend(row).ToArray());
}

static NetworkActionPlanSnapshot[] NormalizeActionPlans(NetworkActionPlanSnapshot[]? plans)
{
    return (plans ?? [])
        .Where(plan => !string.IsNullOrWhiteSpace(plan.Id) &&
                       !string.IsNullOrWhiteSpace(plan.Title))
        .Select(plan => plan with
        {
            Id = plan.Id.Trim(),
            Title = Normalize(plan.Title, "未命名行动"),
            Content = Normalize(plan.Content, ""),
            Participants = MergeActionPlanParticipantRows([], plan.Participants)
        })
        .GroupBy(plan => plan.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(plan => plan.StartTime).First())
        .OrderBy(plan => plan.StartTime)
        .ToArray();
}

static NetworkActionPlanSnapshot[] MergeActionPlanParticipants(
    NetworkActionPlanSnapshot[]? existingPlans,
    NetworkActionPlanSnapshot[]? incomingPlans)
{
    var rows = (existingPlans ?? [])
        .Where(plan => !string.IsNullOrWhiteSpace(plan.Id))
        .ToDictionary(plan => plan.Id, StringComparer.OrdinalIgnoreCase);

    foreach (var incoming in incomingPlans ?? [])
    {
        if (string.IsNullOrWhiteSpace(incoming.Id) ||
            !rows.TryGetValue(incoming.Id, out var existing))
        {
            continue;
        }

        rows[incoming.Id] = existing with
        {
            Participants = MergeActionPlanParticipantRows(existing.Participants, incoming.Participants)
        };
    }

    return rows.Values
        .OrderBy(plan => plan.StartTime)
        .ToArray();
}

static NetworkActionPlanSnapshot[] RemoveActionPlanParticipants(
    NetworkActionPlanSnapshot[]? existingPlans,
    string? planId,
    HashSet<string> aliases)
{
    return NormalizeActionPlans(existingPlans)
        .Select(plan =>
        {
            if (!string.IsNullOrWhiteSpace(planId) &&
                !plan.Id.Equals(planId, StringComparison.OrdinalIgnoreCase))
            {
                return plan;
            }

            return plan with
            {
                Participants = RemoveActionPlanParticipantRows(plan.Participants, aliases)
            };
        })
        .ToArray();
}

static NetworkActionPlanParticipantSnapshot[] RemoveActionPlanParticipantRows(
    NetworkActionPlanParticipantSnapshot[]? participants,
    HashSet<string> aliases)
{
    return MergeActionPlanParticipantRows(participants, [])
        .Where(participant => !IdentityContainsAny(participant.GameName, aliases) &&
                              !IdentityContainsAny(participant.Callsign, aliases))
        .ToArray();
}

static NetworkActionPlanParticipantSnapshot[] MergeActionPlanParticipantRows(
    NetworkActionPlanParticipantSnapshot[]? existingParticipants,
    NetworkActionPlanParticipantSnapshot[]? incomingParticipants)
{
    var rows = new Dictionary<string, NetworkActionPlanParticipantSnapshot>(StringComparer.OrdinalIgnoreCase);
    foreach (var participant in existingParticipants ?? [])
    {
        var key = NormalizeParticipantKey(participant);
        if (!string.IsNullOrWhiteSpace(key))
        {
            rows[key] = participant;
        }
    }

    foreach (var participant in incomingParticipants ?? [])
    {
        var key = NormalizeParticipantKey(participant);
        if (!string.IsNullOrWhiteSpace(key))
        {
            rows[key] = participant;
        }
    }

    return rows.Values
        .OrderBy(participant => Normalize(participant.Callsign, participant.GameName))
        .ToArray();
}

static string NormalizeParticipantKey(NetworkActionPlanParticipantSnapshot participant)
{
    return Normalize(participant.GameName, participant.Callsign).Trim();
}

static string DisplayMember(NetworkFleetMemberSnapshot member)
{
    return string.IsNullOrWhiteSpace(member.Callsign)
        ? member.GameName
        : $"{member.Callsign} ({member.GameName})";
}

static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b)
{
    return a >= b ? a : b;
}

static NetworkPlayerSnapshot ApplyPlayerOnlineTimeout(
    NetworkPlayerSnapshot player,
    DateTimeOffset now,
    TimeSpan timeout)
{
    if (!player.Online || player.LastUpdated == default || now - player.LastUpdated <= timeout)
    {
        return player;
    }

    return player with
    {
        Online = false,
        Ship = "Unknown",
        ShipConfidence = "None",
        Location = "Unknown",
        LocationConfidence = "None"
    };
}

static NetworkFleetMemberSnapshot ApplyMemberOnlineTimeout(
    NetworkFleetMemberSnapshot member,
    DateTimeOffset now,
    TimeSpan timeout)
{
    if (!member.Online || member.LastUpdated == default || now - member.LastUpdated <= timeout)
    {
        return member;
    }

    return member with
    {
        Online = false,
        Ship = "Unknown",
        Location = "Unknown"
    };
}

static bool HasFleetPermission(
    NetworkFleetSnapshot fleet,
    UserAccount? account,
    Func<NetworkFleetMemberPermissionSnapshot, bool> predicate)
{
    if (account is null)
    {
        return false;
    }

    var permission = FindFleetPermission(fleet, account);
    return permission is not null &&
           permission.PermissionEnabled &&
           predicate(permission);
}

static NetworkFleetMemberPermissionSnapshot? FindFleetPermission(NetworkFleetSnapshot fleet, UserAccount account)
{
    return (fleet.MemberPermissions ?? []).FirstOrDefault(permission =>
        MatchesAccountIdentity(permission.GameName, account) ||
        MatchesAccountIdentity(permission.Callsign, account));
}

static bool IsFleetMember(NetworkFleetSnapshot fleet, UserAccount? account)
{
    if (account is null)
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(fleet.OwnerAccount) &&
        fleet.OwnerAccount.Equals(account.UserName, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (FindFleetPermission(fleet, account) is not null)
    {
        return true;
    }

    return (fleet.Members ?? []).Any(member =>
        MatchesAccountIdentity(member.GameName, account) ||
        (!string.IsNullOrWhiteSpace(member.Callsign) &&
         MatchesAccountIdentity(member.Callsign, account)));
}

static bool MatchesAccountIdentity(string? value, UserAccount account)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var aliases = ExpandIdentityAliases(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    return MatchesIdentitySet(account, aliases);
}

static bool IsPrivilegedFleetMember(NetworkFleetSnapshot fleet, string targetName)
{
    var targetAliases = ExpandIdentityAliases(targetName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (IdentityContainsAny(fleet.Commander, targetAliases))
    {
        return true;
    }

    return (fleet.MemberPermissions ?? []).Any(permission =>
        permission.PermissionEnabled &&
        (IdentityContainsAny(permission.GameName, targetAliases) ||
         IdentityContainsAny(permission.Callsign, targetAliases)));
}

static bool CanSendFleetNotification(NetworkFleetSnapshot fleet, UserAccount? account)
{
    if (account is null)
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(fleet.OwnerAccount) &&
        fleet.OwnerAccount.Equals(account.UserName, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return MatchesAccountIdentity(fleet.Commander, account) ||
           HasFleetPermission(fleet, account, permission =>
               permission.CanPublishTasks ||
               permission.CanPublishPlans ||
               permission.CanManageFleetInfo);
}

static bool IsFleetOwner(NetworkFleetSnapshot fleet, UserAccount account)
{
    if (!string.IsNullOrWhiteSpace(fleet.OwnerAccount) &&
        fleet.OwnerAccount.Equals(account.UserName, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (string.IsNullOrWhiteSpace(fleet.Commander))
    {
        return false;
    }

    return MatchesAccountIdentity(fleet.Commander, account);
}

static void AddIdentityAliases(HashSet<string> identities, string? value)
{
    foreach (var alias in ExpandIdentityAliases(value))
    {
        identities.Add(alias);
    }
}

static IEnumerable<string> ExpandIdentityAliases(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        yield break;
    }

    var trimmed = value.Trim();
    yield return trimmed;

    var open = trimmed.LastIndexOf('(');
    var close = trimmed.LastIndexOf(')');
    if (open >= 0 && close > open)
    {
        var inside = trimmed[(open + 1)..close].Trim();
        if (!string.IsNullOrWhiteSpace(inside))
        {
            yield return inside;
        }

        var before = trimmed[..open].Trim();
        if (!string.IsNullOrWhiteSpace(before))
        {
            yield return before;
        }
    }
}

static bool MatchesIdentitySet(UserAccount account, HashSet<string> identities)
{
    return ExpandIdentityAliases(account.UserName).Any(identities.Contains) ||
           ExpandIdentityAliases(account.Email).Any(identities.Contains) ||
           ExpandIdentityAliases(account.GameName).Any(identities.Contains) ||
           ExpandIdentityAliases(account.Callsign).Any(identities.Contains);
}

static bool IdentityContainsAny(string? value, HashSet<string> identities)
{
    return ExpandIdentityAliases(value).Any(identities.Contains);
}

static bool IsUnboundGameName(string? gameName, UserAccount account)
{
    return string.IsNullOrWhiteSpace(gameName) ||
           gameName.Equals(account.UserName, StringComparison.OrdinalIgnoreCase) ||
           (!string.IsNullOrWhiteSpace(account.Email) &&
            gameName.Equals(account.Email, StringComparison.OrdinalIgnoreCase));
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
    string? Callsign,
    bool? AllowEmailNotifications = null);

public sealed record FeedbackRequest(
    string? Contact,
    string? GameName,
    string? Callsign,
    string Message);

public sealed record UpdateManifest(
    string Version,
    string? DownloadUrl,
    string? PackageUrl,
    string? Notes,
    bool Required = false,
    DateTimeOffset? PublishedAt = null);

public sealed record FleetNotificationRequest(
    string FleetCode,
    string Subject,
    string Body);

public sealed record FleetNoticeUpdateRequest(
    string FleetCode,
    string? Title,
    string? Content,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetTaskUpdateRequest(
    string FleetCode,
    string? Title,
    string? Brief,
    string? Participants,
    string? Rally,
    string? Ship,
    DateTime? Time,
    int NoticeRevision,
    NetworkFleetTaskHistorySnapshot[]? TaskHistory = null,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetActionPlansUpdateRequest(
    string FleetCode,
    NetworkActionPlanSnapshot[]? ActionPlans,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetActionPlanJoinRequest(
    string FleetCode,
    string PlanId,
    NetworkActionPlanParticipantSnapshot Participant);

public sealed record FleetActionPlanLeaveRequest(
    string FleetCode,
    string PlanId);

public sealed record FleetJoinApplicationRequest(
    string FleetCode,
    string? Message = null);

public sealed record FleetApplicationDecisionRequest(
    string FleetCode,
    string ApplicationId,
    bool Approve);

public sealed record FleetLeaveRequest(
    string FleetCode);

public sealed record FleetMemberPermissionUpdateRequest(
    string FleetCode,
    NetworkFleetMemberPermissionSnapshot Permission,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetInfoUpdateRequest(
    string FleetCode,
    string? Description,
    string? Type,
    string? ActiveTime,
    string? JoinPolicy,
    string? LogoText,
    string? LogoImageData,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetSquadsUpdateRequest(
    string FleetCode,
    NetworkSquadSnapshot[]? Squads,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record AuthResponse(
    string UserName,
    string? Email,
    string? Callsign,
    string? GameName,
    string Token,
    bool AllowEmailNotifications = true);

public sealed record UserAccount(
    string UserName,
    string? Callsign,
    string? GameName,
    string AuthToken,
    string PasswordSalt,
    string PasswordHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastLogin,
    string? Email = null,
    bool AllowEmailNotifications = true);

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
    DateTimeOffset LastUpdated,
    string? AvatarImageData = null,
    NetworkOwnedShipSnapshot[]? OwnedShips = null);

public sealed record NetworkOwnedShipSnapshot(
    string Code,
    string DisplayName,
    string Source,
    DateTimeOffset ImportedAt);

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
    string? OwnerAccount = null,
    NetworkFleetMemberPermissionSnapshot[]? MemberPermissions = null,
    NetworkFleetMemberSnapshot[]? Members = null,
    NetworkFleetEventLogSnapshot[]? EventLog = null,
    int CurrentTaskNoticeRevision = 0,
    NetworkFleetShipSnapshot[]? Ships = null,
    NetworkFleetTaskHistorySnapshot[]? TaskHistory = null,
    NetworkFleetApplicationSnapshot[]? Applications = null);

public sealed record NetworkFleetMemberPermissionSnapshot(
    string GameName,
    string? Callsign,
    string RoleTitle,
    bool PermissionEnabled,
    bool CanRemoveMembers,
    bool CanPublishTasks,
    bool CanPublishPlans,
    bool CanManageFleetInfo,
    DateTimeOffset UpdatedAt);

public sealed record NetworkFleetMemberSnapshot(
    string GameName,
    string? Callsign,
    string RoleTitle,
    string SquadName,
    bool Online,
    string? Ship,
    string? Location,
    DateTimeOffset LastUpdated,
    string? AvatarImageData = null);

public sealed record NetworkFleetShipSnapshot(
    string Code,
    string DisplayName,
    string OwnerGameName,
    string? OwnerCallsign,
    string? OwnerSquad,
    string? OwnerAvatarImageData,
    DateTimeOffset ImportedAt);

public sealed record NetworkFleetEventLogSnapshot(
    string Id,
    DateTimeOffset Timestamp,
    string Type,
    string Title,
    string Detail);

public sealed record NetworkFleetTaskHistorySnapshot(
    string Key,
    string Title,
    string Brief,
    string Status,
    string Participants,
    string Rally,
    string RequiredShip,
    string PublishedAtText);

public sealed record NetworkFleetApplicationSnapshot(
    string Id,
    string ApplicantGameName,
    string? ApplicantCallsign,
    string? ApplicantAccount,
    string? Message,
    string Status,
    DateTimeOffset CreatedAt,
    string? AvatarImageData = null);

public sealed record FleetDisbandRequest(
    string FleetCode,
    string Password);

public sealed record FleetMemberMutationRequest(
    string FleetCode,
    string TargetGameName);

public sealed record FleetSquadMemberMutationRequest(
    string FleetCode,
    string SquadName,
    string TargetGameName,
    string? TargetCallsign = null);

public sealed record FleetCommanderTransferRequest(
    string FleetCode,
    string TargetGameName);

public sealed record NetworkSquadSnapshot(
    string Name,
    string? Commander,
    string? Type,
    string? Description,
    string? Mission = null,
    string? RallyPoint = null,
    string? EmblemImageData = null);

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
    string Initials,
    string? AvatarImageData = null);

public sealed record SmtpOptions(
    string? Host,
    int Port,
    string? UserName,
    string? Password,
    string? FromAddress,
    bool EnableSsl);
