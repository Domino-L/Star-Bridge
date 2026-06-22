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
var allowOpenTest = string.Equals(
    Environment.GetEnvironmentVariable("STARBRIDGE_ALLOW_OPEN_TEST"),
    "true",
    StringComparison.OrdinalIgnoreCase);
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
var fleetWriteLocks = new ConcurrentDictionary<string, SemaphoreSlim>(
    StringComparer.OrdinalIgnoreCase);
var fleetMembershipMoveLock = new SemaphoreSlim(1, 1);
var feedbackContactLastSentAt = new ConcurrentDictionary<string, DateTimeOffset>(
    StringComparer.OrdinalIgnoreCase);
var feedbackIpWindows = new ConcurrentDictionary<string, VerificationRateWindow>(
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
    version = "0.3.16",
    mode = GetRelayMode(serverKey, allowOpenTest),
    accounts = users.Count,
    players = players.Count,
    fleets = fleets.Count,
    storage = storage.Path,
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    mode = GetRelayMode(serverKey, allowOpenTest),
    accounts = users.Count,
    players = players.Count,
    fleets = fleets.Count,
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/api/updates/latest", () => Results.Ok(new UpdateManifest(
    Environment.GetEnvironmentVariable("STARBRIDGE_LATEST_VERSION") ?? "0.3.16",
    Environment.GetEnvironmentVariable("STARBRIDGE_DOWNLOAD_URL"),
    Environment.GetEnvironmentVariable("STARBRIDGE_PACKAGE_URL"),
    DecodeEnvironmentBase64("STARBRIDGE_RELEASE_NOTES_B64") ??
        NormalizeEnvironmentText(Environment.GetEnvironmentVariable("STARBRIDGE_RELEASE_NOTES")) ??
        "当前服务器未配置新版安装包。",
    string.Equals(Environment.GetEnvironmentVariable("STARBRIDGE_UPDATE_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase),
    DateTimeOffset.UtcNow,
    Environment.GetEnvironmentVariable("STARBRIDGE_DOWNLOAD_SHA256"),
    Environment.GetEnvironmentVariable("STARBRIDGE_PACKAGE_SHA256"))));

app.MapPost("/api/auth/register", async (AuthRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Password) ||
        string.IsNullOrWhiteSpace(request.Email) ||
        string.IsNullOrWhiteSpace(request.VerificationCode) ||
        string.IsNullOrWhiteSpace(request.Callsign))
    {
        return Results.BadRequest(new { error = "注册失败：登录邮箱、密码、呼号和验证码都是必填项。" });
    }

    var email = request.Email.Trim();
    var callsign = request.Callsign.Trim();
    if (request.Password.Length < 6)
    {
        return Results.BadRequest(new { error = "注册失败：密码至少需要 6 个字符。" });
    }

    if (GetCallsignWeight(callsign) is < 1 or > 10)
    {
        return Results.BadRequest(new { error = "注册失败：呼号过长。" });
    }

    if (!verificationCodes.TryGetValue(email, out var verification) ||
        verification.ExpiresAt < DateTimeOffset.UtcNow ||
        !verification.Code.Equals(request.VerificationCode.Trim(), StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "注册失败：验证码错误或已过期。" });
    }

    if (users.Values.Any(existing =>
            !string.IsNullOrWhiteSpace(existing.Email) &&
            existing.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.Conflict(new { error = "注册失败：该邮箱已注册，请直接登录。" });
    }

    var account = CreateAccount(email, request.Password, request.GameName, email, callsign, out var authToken);
    if (!users.TryAdd(email, account))
    {
        return Results.Conflict(new { error = "注册失败：该邮箱已注册，请直接登录。" });
    }

    verificationCodes.TryRemove(email, out _);
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(ToAuthResponse(account, authToken));
});

app.MapPost("/api/auth/send-code", async (HttpContext context, EmailVerificationRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new { error = "请输入邮箱地址。" });
    }

    if (!IsSmtpConfigured(smtpOptions))
    {
        return Results.BadRequest(new { error = "验证码发送失败：服务器邮件服务未配置。" });
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
        Console.Error.WriteLine($"Verification email failed for {RedactEmail(email)}: {ex.GetType().Name}: {ex.Message}");
        return Results.BadRequest(new { error = "验证码邮件发送失败：服务器 SMTP 投递失败，请稍后重试或联系管理员。" });
    }
});

app.MapPost("/api/feedback", async (HttpContext context, FeedbackRequest feedback) =>
{
    var request = context.Request;
    var message = feedback.Message?.Trim();
    if (string.IsNullOrWhiteSpace(message))
    {
        return Results.BadRequest(new { error = "反馈发送失败：请填写反馈内容。" });
    }

    if (message.Length > 4000)
    {
        return Results.BadRequest(new { error = "反馈内容过长，请控制在 4000 个字符以内。" });
    }

    var contact = feedback.Contact?.Trim();
    if (!string.IsNullOrWhiteSpace(contact) && contact.Length > 200)
    {
        return Results.BadRequest(new { error = "联系方式过长，请控制在 200 个字符以内。" });
    }

    if (!IsSmtpConfigured(smtpOptions))
    {
        return Results.BadRequest(new { error = "反馈发送失败：服务器邮件服务未配置。" });
    }

    var userName = GetAuthorizedUserName(request, users);
    users.TryGetValue(userName ?? "", out var account);
    var sender = account?.Email ?? contact ?? "Guest";
    var rateLimitError = ValidateFeedbackRateLimit(
        feedbackContactLastSentAt,
        feedbackIpWindows,
        sender,
        GetClientIp(context),
        DateTimeOffset.UtcNow);
    if (rateLimitError is not null)
    {
        return Results.BadRequest(new { error = rateLimitError });
    }

    var gameName = account?.GameName ?? feedback.GameName ?? "Unknown";
    var callsign = account?.Callsign ?? feedback.Callsign ?? "Unknown";
    var body = $"""
StarBridge Feedback

Time: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
Account: {sender}
GameName: {gameName}
Callsign: {callsign}
Contact: {Normalize(contact, "Not provided")}

Message:
{message}
""";

    try
    {
        await SendEmailAsync(smtpOptions, "ruiyanglyu0217@gmail.com", "StarBridge Feedback", body);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Feedback email failed from {RedactEmail(sender)}: {ex.GetType().Name}: {ex.Message}");
        return Results.BadRequest(new { error = "反馈发送失败：服务器 SMTP 投递失败，请稍后重试或联系管理员。" });
    }
});

app.MapPost("/api/fleets/notify", async (HttpRequest request, FleetNotificationRequest notification) =>
{
    if (!IsWriteAllowed(request, serverKey, allowOpenTest, users))
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
        return Results.BadRequest(new { error = "邮件通知发送失败：服务器邮件服务未配置。" });
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

    var token = CreateSessionToken();
    users[name] = account with
    {
        AuthToken = "",
        AuthTokenHash = HashAuthToken(token),
        AuthTokenExpiresAt = GetAuthTokenExpiresAt(),
        LastLogin = DateTimeOffset.UtcNow
    };
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(ToAuthResponse(users[name], token));
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
    return Results.Ok(ToAuthResponse(updated, GetBearerToken(request)));
});

app.MapPost("/api/auth/logout", async (HttpRequest request) =>
{
    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Ok(new { loggedOut = true });
    }

    users[userName] = account with
    {
        AuthToken = "",
        AuthTokenHash = "",
        AuthTokenExpiresAt = null
    };
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new { loggedOut = true });
});

app.MapGet("/api/players", (HttpRequest request) =>
{
    if (!IsReadAllowed(request, serverKey, allowOpenTest, users))
    {
        return Results.Ok(Array.Empty<NetworkPlayerSnapshot>());
    }

    return Results.Ok(players.Values
        .Select(player => ApplyPlayerOnlineTimeout(player, DateTimeOffset.UtcNow, playerOnlineTimeout))
        .OrderByDescending(player => player.Online)
        .ThenBy(player => player.Name)
        .ToArray());
});

app.MapGet("/api/fleets", (HttpRequest request) =>
{
    var canReadFullState = IsReadAllowed(request, serverKey, allowOpenTest, users);
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

    return Results.Ok(canReadFullState
        ? fleetArray
        : fleetArray.Select(ToPublicFleetSnapshot).ToArray());
});

app.MapPost("/api/fleets", async (HttpRequest request, NetworkFleetSnapshot snapshot) =>
{
    if (!IsWriteAllowed(request, serverKey, allowOpenTest, users))
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
        LogoImageData = NormalizeImageData(snapshot.LogoImageData, 512 * 1024),
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
        EventLog = [],
        Ships = NormalizeFleetShips(snapshot.Ships),
        TaskHistory = NormalizeFleetTaskHistory(snapshot.TaskHistory),
        Applications = NormalizeFleetApplications(snapshot.Applications),
        OwnerAccount = Normalize(snapshot.OwnerAccount, GetAuthorizedUserName(request, users) ?? ""),
        Squads = MergeSquads([], snapshot.Squads),
        LastUpdated = DateTimeOffset.UtcNow
    };

    var authorizedUser = GetAuthorizedUserName(request, users) ?? "";
    users.TryGetValue(authorizedUser, out var authorizedAccount);
    return await WithFleetWriteLockAsync(fleetWriteLocks, normalized.Code, async () =>
    {
        var normalizedCreate = normalized with
        {
            EventLog = AddFleetLog(
                [],
                "舰队",
                "创建舰队",
                $"{normalized.Name} ({normalized.Code}) 已创建")
        };
        var merged = fleets.AddOrUpdate(
            normalized.Code,
            normalizedCreate,
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
                var canJoinActionPlans = IsFleetMember(existing, authorizedAccount);
                var mergedMembers = MergeFleetMembers(
                    existing.Members,
                    FilterFleetMemberUpdatesForAccount(
                        normalized.Members,
                        existing.Members,
                        authorizedAccount,
                        canUpdateAllMembers: false));
                var mergedSquads = MergeAuthorizedSquads(
                    existing.Squads,
                    normalized.Squads,
                    existing,
                    authorizedAccount,
                    canManageFleetInfo);

                return normalized with
                {
                    Name = canManageFleetInfo ? normalized.Name : existing.Name,
                    Commander = canOwnFleet ? normalized.Commander : existing.Commander,
                    Description = canManageFleetInfo ? normalized.Description : existing.Description,
                    Type = canManageFleetInfo ? normalized.Type : existing.Type,
                    ActiveTime = canManageFleetInfo ? normalized.ActiveTime : existing.ActiveTime,
                    JoinPolicy = canManageFleetInfo ? normalized.JoinPolicy : existing.JoinPolicy,
                    LogoText = canManageFleetInfo ? normalized.LogoText : existing.LogoText,
                    LogoImageData = canManageFleetInfo
                        ? normalized.LogoImageData ?? existing.LogoImageData
                        : existing.LogoImageData,
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
                    Members = mergedMembers,
                    Ships = MergeFleetShips(existing.Ships, normalized.Ships, authorizedAccount),
                    TaskHistory = canPublishTasks
                        ? MergeFleetTaskHistory(existing.TaskHistory, normalized.TaskHistory)
                        : existing.TaskHistory,
                    Applications = canManageFleetInfo
                        ? NormalizeFleetApplications(normalized.Applications)
                        : existing.Applications,
                    EventLog = existing.EventLog,
                    OwnerAccount = canOwnFleet ? normalized.OwnerAccount : existing.OwnerAccount,
                    Squads = PruneEmptySquads(mergedSquads, mergedMembers, TimeSpan.FromMinutes(2))
                };
            });
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(merged);
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (!IsFleetOwner(fleet, account) &&
            !HasFleetPermission(fleet, account, permission => permission.CanManageFleetInfo))
        {
            return Results.Unauthorized();
        }

        var noticeTitle = Normalize(update.Title, "");
        var noticeContent = Normalize(update.Content, "");
        var eventLog = fleet.EventLog;
        if (!TextEquals(fleet.NoticeTitle, noticeTitle) ||
            !TextEquals(fleet.NoticeContent, noticeContent))
        {
            eventLog = AddFleetLog(
                eventLog,
                "公告",
                string.IsNullOrWhiteSpace(noticeTitle) && string.IsNullOrWhiteSpace(noticeContent)
                    ? "清空舰队公告"
                    : "更新舰队公告",
                string.IsNullOrWhiteSpace(noticeTitle) ? "舰队公告已更新" : noticeTitle);
        }

        var updated = fleet with
        {
            NoticeTitle = noticeTitle,
            NoticeContent = noticeContent,
            EventLog = eventLog,
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (!IsFleetOwner(fleet, account) &&
            !HasFleetPermission(fleet, account, permission => permission.CanPublishTasks))
        {
            return Results.Unauthorized();
        }

        var taskTitle = Normalize(update.Title, "");
        var taskBrief = Normalize(update.Brief, "");
        var taskParticipants = Normalize(update.Participants, "");
        var taskRally = Normalize(update.Rally, "");
        var taskShip = Normalize(update.Ship, "");
        var noticeRevision = Math.Max(
            Math.Max(0, update.NoticeRevision),
            Math.Max(0, fleet.CurrentTaskNoticeRevision));
        var taskHistory = MergeFleetTaskHistory(fleet.TaskHistory, update.TaskHistory);
        var eventLog = AddFleetTaskHistoryChangeLogs(fleet.EventLog, fleet.TaskHistory, taskHistory);
        var oldTaskActive = IsTaskActive(
            fleet.CurrentTaskTitle,
            fleet.CurrentTaskBrief,
            fleet.CurrentTaskRally,
            fleet.CurrentTaskShip);
        var newTaskActive = IsTaskActive(taskTitle, taskBrief, taskRally, taskShip);
        var taskChanged = !TaskEquals(
            fleet.CurrentTaskTitle,
            fleet.CurrentTaskBrief,
            fleet.CurrentTaskParticipants,
            fleet.CurrentTaskRally,
            fleet.CurrentTaskShip,
            fleet.CurrentTaskTime,
            taskTitle,
            taskBrief,
            taskParticipants,
            taskRally,
            taskShip,
            update.Time);
        if (newTaskActive && !oldTaskActive)
        {
            eventLog = AddFleetLog(eventLog, "任务", "发布任务", FormatTaskLogDetail(taskTitle, taskParticipants, taskRally, taskShip, update.Time));
        }
        else if (newTaskActive && taskChanged)
        {
            eventLog = AddFleetLog(eventLog, "任务", "更新任务", FormatTaskLogDetail(taskTitle, taskParticipants, taskRally, taskShip, update.Time));
        }
        else if (!newTaskActive && oldTaskActive)
        {
            eventLog = AddFleetLog(eventLog, "任务", "清除当前任务", "当前任务已清除");
        }
        else if (newTaskActive && update.NoticeRevision > fleet.CurrentTaskNoticeRevision)
        {
            eventLog = AddFleetLog(eventLog, "任务", "再次通知任务", FormatTaskLogDetail(taskTitle, taskParticipants, taskRally, taskShip, update.Time));
        }

        var updated = fleet with
        {
            CurrentTaskTitle = taskTitle,
            CurrentTaskBrief = taskBrief,
            CurrentTaskParticipants = taskParticipants,
            CurrentTaskRally = taskRally,
            CurrentTaskShip = taskShip,
            CurrentTaskTime = update.Time,
            CurrentTaskNoticeRevision = noticeRevision,
            TaskHistory = taskHistory,
            EventLog = eventLog,
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (!IsFleetOwner(fleet, account) &&
            !HasFleetPermission(fleet, account, permission => permission.CanPublishPlans))
        {
            return Results.Unauthorized();
        }

        var actionPlans = NormalizeActionPlans(update.ActionPlans);
        var eventLog = AddActionPlanChangeLogs(fleet.EventLog, fleet.ActionPlans, actionPlans);
        var updated = fleet with
        {
            ActionPlans = actionPlans,
            EventLog = eventLog,
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
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
        var planTitle = ResolveActionPlanTitle(fleet.ActionPlans, join.PlanId.Trim());
        var participantDisplay = FormatActionPlanParticipant(
            join.Participant,
            account,
            FindPlayerForAccount(players, account));
        var updated = fleet with
        {
            ActionPlans = MergeActionPlanParticipants(fleet.ActionPlans, [incoming]),
            EventLog = AddFleetLog(
                fleet.EventLog,
                "计划",
                "预约行动",
                $"{participantDisplay} 预约 {planTitle}"),
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (!IsFleetMember(fleet, account))
        {
            return Results.Unauthorized();
        }

        var aliases = BuildAccountAliases(account);
        var planTitle = ResolveActionPlanTitle(fleet.ActionPlans, leave.PlanId.Trim());
        var participantDisplay = FormatActionPlanParticipant(
            null,
            account,
            FindPlayerForAccount(players, account));
        var updated = fleet with
        {
            ActionPlans = RemoveActionPlanParticipants(fleet.ActionPlans, leave.PlanId.Trim(), aliases),
            EventLog = AddFleetLog(
                fleet.EventLog,
                "计划",
                "取消预约",
                $"{participantDisplay} 取消预约 {planTitle}"),
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    });
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
    await fleetMembershipMoveLock.WaitAsync();
    try
    {
        return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
        {
            if (!fleets.TryGetValue(fleetCode, out var fleet))
            {
                return Results.NotFound(new { error = "Fleet not found." });
            }

            if (IsFleetMember(fleet, account))
            {
                return Results.Ok(fleet with { Applications = NormalizeFleetApplications(fleet.Applications) });
            }

            if (FindOwnedFleetForAccount(fleets, fleetCode, account) is { } ownedFleet)
            {
                return Results.Conflict(new
                {
                    error = $"你是 {ownedFleet.Name} 的舰队指挥官。请先转移指挥权或解散舰队，再加入其他舰队。"
                });
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

            await RemoveAccountFromOtherFleetsAsync(fleets, players, fleetWriteLocks, fleetCode, account);
            var now = DateTimeOffset.UtcNow;
            var member = BuildFleetMemberFromAccount(account, player) with
            {
                SquadName = "Unassigned",
                LastUpdated = now
            };
            foreach (var pair in players.ToArray())
            {
                if (MatchesAccountIdentity(pair.Value.Name, account) ||
                    MatchesAccountIdentity(pair.Value.Callsign, account))
                {
                    players[pair.Key] = pair.Value with
                    {
                        Fleet = fleet.Name,
                        Squad = "Unassigned",
                        LastUpdated = now
                    };
                }
            }

            var aliases = BuildAccountAliases(account);
            var joined = fleet with
            {
                Members = UpsertFleetMember(fleet.Members, member),
                MemberPermissions = ResetFleetPermissionForMember(fleet.MemberPermissions, member),
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
    }
    finally
    {
        fleetMembershipMoveLock.Release();
    }
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
    await fleetMembershipMoveLock.WaitAsync();
    try
    {
        return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
        {
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
                    if (FindOwnedFleetForAccount(fleets, fleetCode, applicantAccount) is { } ownedFleet)
                    {
                        return Results.Conflict(new
                        {
                            error = $"{FormatApplicationIdentity(application)} 当前是 {ownedFleet.Name} 的舰队指挥官，需要先转移指挥权或解散原舰队。"
                        });
                    }

                    await RemoveAccountFromOtherFleetsAsync(fleets, players, fleetWriteLocks, fleetCode, applicantAccount);
                }

                var now = DateTimeOffset.UtcNow;
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
                        Squad = "Unassigned",
                        LastUpdated = now
                    };
                }

                var member = applicantAccount is null
                    ? BuildFleetMemberFromApplication(application, applicantPlayer)
                    : BuildFleetMemberFromAccount(applicantAccount, applicantPlayer);
                member = member with
                {
                    SquadName = "Unassigned",
                    LastUpdated = now
                };
                updated = updated with
                {
                    Members = UpsertFleetMember(updated.Members, member),
                    MemberPermissions = ResetFleetPermissionForMember(updated.MemberPermissions, member),
                    Ships = MergeFleetShips(updated.Ships, applicantPlayer is null ? [] : BuildFleetShipsFromPlayer(applicantPlayer), null)
                };
            }

            fleets[fleetCode] = updated;
            await storage.SaveAsync(players, fleets, users, verificationCodes);
            return Results.Ok(updated);
        });
    }
    finally
    {
        fleetMembershipMoveLock.Release();
    }
});

app.MapPost("/api/fleets/leave", async Task<IResult> (HttpRequest request, FleetLeaveRequest leave) =>
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async Task<IResult> () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (IsFleetOwner(fleet, account))
        {
            var ownerAliases = BuildAccountAliases(account);
            var remainingMembers = NormalizeFleetMembers(fleet.Members, null)
                .Where(member => !FleetMemberContainsAny(member, ownerAliases))
                .ToArray();
            var accountLabel = FormatAccountIdentity(account, FindPlayerForAccount(players, account));

            if (remainingMembers.Length == 0)
            {
                if (!leave.ConfirmDisbandIfOwnerAlone)
                {
                    return Results.BadRequest(new { error = "舰队指挥官离开前需要确认解散舰队。" });
                }

                foreach (var pair in players.ToArray())
                {
                    if (PlayerMatchesAccountOrAliases(pair.Key, pair.Value, account, ownerAliases) ||
                        PlayerBelongsToFleet(pair.Value, fleet, fleetCode))
                    {
                        players[pair.Key] = pair.Value with
                        {
                            Fleet = "No Fleet",
                            Squad = "Unassigned",
                            LastUpdated = DateTimeOffset.UtcNow
                        };
                    }
                }

                fleets.TryRemove(fleetCode, out _);
                await storage.SaveAsync(players, fleets, users, verificationCodes);
                return Results.Ok(new { status = "disbanded", fleetCode });
            }

            if (string.IsNullOrWhiteSpace(leave.TransferCommanderTo))
            {
                return Results.BadRequest(new { error = "离开前需要指定新的舰队指挥官。" });
            }

            var successorAliases = ExpandIdentityAliases(leave.TransferCommanderTo)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var successor = remainingMembers.FirstOrDefault(member => FleetMemberContainsAny(member, successorAliases));
            if (successor is null)
            {
                return Results.BadRequest(new { error = "未找到接任者。" });
            }

            var targetAccount = users.Values.FirstOrDefault(user =>
                MatchesAccountIdentity(successor.GameName, user) ||
                MatchesAccountIdentity(successor.Callsign, user) ||
                MatchesAccountIdentity(DisplayMember(successor), user) ||
                MatchesAccountIdentity(leave.TransferCommanderTo, user));
            if (targetAccount is null)
            {
                return Results.BadRequest(new { error = "接任者需要先注册并登录星海舰桥账号。" });
            }

            var ownerDisbandedSquads = FindCommandedSquadNames(fleet, ownerAliases);
            foreach (var pair in players.ToArray())
            {
                if (PlayerMatchesAccountOrAliases(pair.Key, pair.Value, account, ownerAliases))
                {
                    players[pair.Key] = pair.Value with
                    {
                        Fleet = "No Fleet",
                        Squad = "Unassigned",
                        LastUpdated = DateTimeOffset.UtcNow
                    };
                    continue;
                }

                if (ownerDisbandedSquads.Count > 0 &&
                    PlayerBelongsToFleet(pair.Value, fleet, fleetCode) &&
                    ownerDisbandedSquads.Contains(Normalize(pair.Value.Squad, "Unassigned")))
                {
                    players[pair.Key] = pair.Value with
                    {
                        Squad = "Unassigned",
                        LastUpdated = DateTimeOffset.UtcNow
                    };
                }
            }

            var ownerUpdated = RemoveFleetIdentity(fleet, ownerAliases, disbandCommandedSquads: true);
            var targetGameName = Normalize(targetAccount.GameName, successor.GameName);
            var targetCallsign = Normalize(targetAccount.Callsign, Normalize(successor.Callsign, ""));
            var targetDisplayName = string.IsNullOrWhiteSpace(targetCallsign)
                ? targetGameName
                : $"{targetCallsign} ({targetGameName})";
            var targetMember = NormalizeFleetMembers(ownerUpdated.Members, null)
                .FirstOrDefault(member =>
                    MatchesAccountIdentity(member.GameName, targetAccount) ||
                    MatchesAccountIdentity(member.Callsign, targetAccount)) ??
                successor;
            targetMember = targetMember with
            {
                GameName = targetGameName,
                Callsign = targetCallsign,
                RoleTitle = "舰队指挥官",
                LastUpdated = DateTimeOffset.UtcNow
            };

            var ownerEventLog = ownerUpdated.EventLog;
            foreach (var squadName in ownerDisbandedSquads.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                ownerEventLog = AddFleetLog(ownerEventLog, "小队", "小队解散", $"舰队指挥官离开舰队，{squadName} 已解散");
            }

            ownerEventLog = AddFleetLog(ownerEventLog, "权限", "转移舰队指挥官", $"{targetDisplayName} 成为新的舰队指挥官");
            ownerEventLog = AddFleetLog(ownerEventLog, "成员", "玩家离开", $"{accountLabel} 离开舰队");

            ownerUpdated = ownerUpdated with
            {
                Commander = targetDisplayName,
                OwnerAccount = targetAccount.UserName,
                Members = UpsertFleetMember(ownerUpdated.Members, targetMember),
                MemberPermissions = (ownerUpdated.MemberPermissions ?? [])
                    .Where(permission =>
                        !MatchesAccountIdentity(permission.GameName, targetAccount) &&
                        !MatchesAccountIdentity(permission.Callsign, targetAccount))
                    .Append(new NetworkFleetMemberPermissionSnapshot(
                        targetGameName,
                        targetCallsign,
                        "舰队指挥官",
                        true,
                        true,
                        true,
                        true,
                        true,
                        DateTimeOffset.UtcNow))
                    .ToArray(),
                EventLog = ownerEventLog,
                LastUpdated = DateTimeOffset.UtcNow
            };
            fleets[fleetCode] = ownerUpdated;
            await storage.SaveAsync(players, fleets, users, verificationCodes);
            return Results.Ok(ownerUpdated);
        }

        if (!IsFleetMember(fleet, account))
        {
            return Results.Ok(fleet);
        }

        var memberAliases = BuildAccountAliases(account);
        foreach (var pair in players.ToArray())
        {
            if (PlayerMatchesAccountOrAliases(pair.Key, pair.Value, account, memberAliases))
            {
                players[pair.Key] = pair.Value with
                {
                    Fleet = "No Fleet",
                    Squad = "Unassigned",
                    LastUpdated = DateTimeOffset.UtcNow
                };
            }
        }

        var disbandedSquads = FindCommandedSquadNames(fleet, memberAliases);
        var eventLog = fleet.EventLog;
        foreach (var squadName in disbandedSquads.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            eventLog = AddFleetLog(eventLog, "小队", "小队解散", $"小队指挥官离开舰队，{squadName} 已解散");
        }

        ClearDisbandedSquadsFromPlayers(players, fleet, fleetCode, disbandedSquads);

        var memberUpdated = RemoveFleetIdentity(fleet, memberAliases, disbandCommandedSquads: true) with
        {
            EventLog = AddFleetLog(
                eventLog,
                "成员",
                "玩家离开",
                $"{FormatAccountIdentity(account, FindPlayerForAccount(players, account))} 离开舰队"),
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = memberUpdated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(memberUpdated);
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (!IsFleetOwner(fleet, account))
        {
            return Results.Unauthorized();
        }

        var permission = NormalizeFleetMemberPermissions([update.Permission]).FirstOrDefault();
        if (permission is null)
        {
            return Results.BadRequest(new { error = "Member permission is invalid." });
        }

        var permissions = MergeFleetMemberPermissions(fleet.MemberPermissions, [permission]);
        var previousPermission = FindPermissionForIdentity(fleet.MemberPermissions, permission);
        var effectivePermission = FindPermissionForIdentity(permissions, permission) ?? permission;
        var eventLog = fleet.EventLog;
        if (previousPermission is null || !PermissionEquals(previousPermission, effectivePermission))
        {
            eventLog = AddFleetLog(
                eventLog,
                "权限",
                effectivePermission.PermissionEnabled ? "更新成员权限" : "撤销成员权限",
                FormatPermissionLogDetail(effectivePermission));
        }

        var updated = fleet with
        {
            MemberPermissions = permissions,
            EventLog = eventLog,
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (!IsFleetOwner(fleet, account) &&
            !HasFleetPermission(fleet, account, permission => permission.CanManageFleetInfo))
        {
            return Results.Unauthorized();
        }

        var description = Normalize(update.Description, "No fleet description.");
        var type = Normalize(update.Type, "Combat");
        var activeTime = Normalize(update.ActiveTime, "20:00 - 23:59 UTC+8");
        var joinPolicy = Normalize(update.JoinPolicy, "Open");
        var logoText = Normalize(update.LogoText, Normalize(fleet.LogoText, "LOGO"));
        var logoImageData = NormalizeImageData(update.LogoImageData, 512 * 1024);
        var fleetInfoChanges = BuildFleetInfoChangeList(
            fleet,
            description,
            type,
            activeTime,
            joinPolicy,
            logoText,
            logoImageData);
        var eventLog = fleet.EventLog;
        if (fleetInfoChanges.Length > 0)
        {
            eventLog = AddFleetLog(
                eventLog,
                "舰队",
                "更新舰队资料",
                string.Join("、", fleetInfoChanges));
        }

        var updated = fleet with
        {
            Description = description,
            Type = type,
            ActiveTime = activeTime,
            JoinPolicy = joinPolicy,
            LogoText = logoText,
            LogoImageData = logoImageData ?? fleet.LogoImageData,
            EventLog = eventLog,
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (!IsFleetOwner(fleet, account) && !IsFleetMember(fleet, account))
        {
            return Results.Unauthorized();
        }

        var canManageAllSquads = IsFleetOwner(fleet, account) ||
                                 HasFleetPermission(fleet, account, permission => permission.CanManageFleetInfo);
        var canAppendSquadLog = canManageAllSquads ||
                                HasAuthorizedSquadWrite(fleet.Squads, update.Squads, fleet, account);
        var mergedSquads = MergeAuthorizedSquads(
            fleet.Squads,
            update.Squads,
            fleet,
            account,
            canManageAllSquads);
        var prunedSquads = PruneEmptySquads(mergedSquads, fleet.Members, TimeSpan.FromMinutes(2));
        var eventLog = canAppendSquadLog
            ? AddSquadChangeLogs(fleet.EventLog, fleet.Squads, prunedSquads)
            : fleet.EventLog;
        var updated = fleet with
        {
            Squads = prunedSquads,
            EventLog = eventLog,
            LastUpdated = DateTimeOffset.UtcNow
        };
        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(updated);
    });
});

app.MapPost("/api/fleets/squads/leave", async (HttpRequest request, FleetSquadLeaveRequest leave) =>
{
    if (string.IsNullOrWhiteSpace(leave.FleetCode) ||
        string.IsNullOrWhiteSpace(leave.SquadName))
    {
        return Results.BadRequest(new { error = "Fleet code and squad name are required." });
    }

    var userName = GetAuthorizedUserName(request, users);
    if (string.IsNullOrWhiteSpace(userName) || !users.TryGetValue(userName, out var account))
    {
        return Results.Unauthorized();
    }

    var fleetCode = leave.FleetCode.Trim();
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
        if (!fleets.TryGetValue(fleetCode, out var fleet))
        {
            return Results.NotFound(new { error = "Fleet not found." });
        }

        if (!IsFleetMember(fleet, account))
        {
            return Results.Unauthorized();
        }

        var squadName = leave.SquadName.Trim();
        var squads = (fleet.Squads ?? [])
            .Where(squad => !string.IsNullOrWhiteSpace(squad.Name))
            .Select(NormalizeSquad)
            .ToArray();
        var squad = squads.FirstOrDefault(item =>
            item.Name.Equals(squadName, StringComparison.OrdinalIgnoreCase));
        if (squad is null)
        {
            return Results.NotFound(new { error = "Squad not found." });
        }

        var requesterAliases = BuildAccountAliases(account);
        var fleetMembers = NormalizeFleetMembers(fleet.Members, fleet.MemberPermissions);
        var squadMembers = fleetMembers
            .Where(member => Normalize(member.SquadName, "Unassigned")
                .Equals(squadName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var requesterMember = squadMembers.FirstOrDefault(member =>
            IdentityContainsAny(member.GameName, requesterAliases) ||
            IdentityContainsAny(member.Callsign, requesterAliases));

        if (requesterMember is null)
        {
            return Results.BadRequest(new { error = "You are not in this squad." });
        }

        var now = DateTimeOffset.UtcNow;
        var requesterDisplay = DisplayMember(requesterMember);
        var isSquadCommander = IdentityContainsAny(squad.Commander, requesterAliases);
        var remainingMembers = squadMembers
            .Where(member => !IdentityContainsAny(member.GameName, requesterAliases) &&
                             !IdentityContainsAny(member.Callsign, requesterAliases))
            .ToArray();

        var updatedMembers = fleetMembers
            .Select(member =>
                (IdentityContainsAny(member.GameName, requesterAliases) ||
                 IdentityContainsAny(member.Callsign, requesterAliases)) &&
                Normalize(member.SquadName, "Unassigned").Equals(squadName, StringComparison.OrdinalIgnoreCase)
                    ? member with { SquadName = "Unassigned", LastUpdated = now }
                    : member)
            .ToArray();

        var updatedShips = NormalizeFleetShips(fleet.Ships)
            .Select(ship =>
                (IdentityContainsAny(ship.OwnerGameName, requesterAliases) ||
                 IdentityContainsAny(ship.OwnerCallsign, requesterAliases)) &&
                Normalize(ship.OwnerSquad, "未加入小队").Equals(squadName, StringComparison.OrdinalIgnoreCase)
                    ? ship with { OwnerSquad = "未加入小队" }
                    : ship)
            .ToArray();

        foreach (var pair in players.ToArray())
        {
            var playerAliases = ExpandIdentityAliases(pair.Value.Name)
                .Concat(ExpandIdentityAliases(pair.Value.Callsign))
                .Concat(ExpandIdentityAliases(pair.Key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (playerAliases.Overlaps(requesterAliases) &&
                Normalize(pair.Value.Squad, "Unassigned").Equals(squadName, StringComparison.OrdinalIgnoreCase))
            {
                players[pair.Key] = pair.Value with
                {
                    Squad = "Unassigned",
                    LastUpdated = now
                };
            }
        }

        var eventLog = fleet.EventLog;
        var disbanded = false;
        string? successorDisplay = null;
        NetworkSquadSnapshot[] updatedSquads;

        if (!isSquadCommander)
        {
            updatedSquads = squads;
            eventLog = AddFleetLog(eventLog, "成员", "离开小队", $"{requesterDisplay} 离开 {squadName}");
        }
        else if (remainingMembers.Length == 0)
        {
            disbanded = true;
            updatedSquads = squads
                .Where(item => !item.Name.Equals(squadName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            eventLog = AddFleetLog(eventLog, "成员", "解散小队", $"{requesterDisplay} 离开并解散 {squadName}");
        }
        else
        {
            var successorAliases = ExpandIdentityAliases(leave.SuccessorGameName)
                .Concat(ExpandIdentityAliases(leave.SuccessorCallsign))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var successor = successorAliases.Count == 0
                ? PickRecommendedSquadSuccessor(remainingMembers)
                : remainingMembers.FirstOrDefault(member =>
                    IdentityContainsAny(member.GameName, successorAliases) ||
                    IdentityContainsAny(member.Callsign, successorAliases));

            if (successor is null)
            {
                return Results.BadRequest(new { error = "A valid successor is required before the squad commander can leave." });
            }

            successorDisplay = DisplayMember(successor);
            updatedSquads = squads
                .Select(item => item.Name.Equals(squadName, StringComparison.OrdinalIgnoreCase)
                    ? item with { Commander = successorDisplay }
                    : item)
                .ToArray();
            eventLog = AddFleetLog(
                eventLog,
                "成员",
                "移交小队指挥权",
                $"{requesterDisplay} 将 {squadName} 移交给 {successorDisplay} 并离开");
        }

        var updated = fleet with
        {
            Squads = PruneEmptySquads(updatedSquads, updatedMembers),
            Members = updatedMembers,
            Ships = updatedShips,
            EventLog = eventLog,
            LastUpdated = now
        };

        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(new { left = true, squad = squadName, disbanded, successor = successorDisplay });
    });
});

app.MapPost("/api/players", async (HttpRequest request, NetworkPlayerSnapshot snapshot) =>
{
    if (!IsWriteAllowed(request, serverKey, allowOpenTest, users))
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

    UserAccount? authorizedAccount = null;
    var authorizedUser = GetAuthorizedUserName(request, users);
    if (!string.IsNullOrWhiteSpace(authorizedUser) &&
        users.TryGetValue(authorizedUser, out var account))
    {
        authorizedAccount = account;
        if (IsUnboundGameName(account.GameName, account))
        {
            authorizedAccount = account with
            {
                GameName = normalized.Name,
                LastLogin = DateTimeOffset.UtcNow
            };
            users[authorizedUser] = authorizedAccount;
        }
        else if (!string.IsNullOrWhiteSpace(account.GameName) &&
                 !account.GameName.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }
    }

    var storedPlayer = players.AddOrUpdate(
        normalized.Name,
        normalized,
        (_, existing) => normalized with
        {
            AvatarImageData = normalized.AvatarImageData ?? existing.AvatarImageData
        });

    if (authorizedAccount is not null)
    {
        var matchingFleetCodes = fleets.Values
            .Where(fleet =>
                normalized.Fleet.Equals(fleet.Name, StringComparison.OrdinalIgnoreCase) ||
                normalized.Fleet.Equals(fleet.Code, StringComparison.OrdinalIgnoreCase))
            .Select(fleet => fleet.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var fleetCode in matchingFleetCodes)
        {
            await WithFleetWriteLockActionAsync(fleetWriteLocks, fleetCode, () =>
            {
                if (!fleets.TryGetValue(fleetCode, out var fleet) ||
                    (!IsFleetOwner(fleet, authorizedAccount) && !IsFleetMember(fleet, authorizedAccount)))
                {
                    return Task.CompletedTask;
                }

                var fleetPlayers = players.Values
                    .Where(player =>
                        player.Fleet?.Equals(fleet.Name, StringComparison.OrdinalIgnoreCase) == true ||
                        player.Fleet?.Equals(fleet.Code, StringComparison.OrdinalIgnoreCase) == true)
                    .ToArray();
                var updatedMembers = BuildFleetMembers(fleet, fleetPlayers);
                var updatedShips = NormalizeFleetShips((fleet.Ships ?? [])
                    .Concat(fleetPlayers.SelectMany(BuildFleetShipsFromPlayer))
                    .ToArray());
                fleets[fleetCode] = fleet with
                {
                    Members = updatedMembers,
                    Squads = PruneEmptySquads(fleet.Squads, updatedMembers, TimeSpan.FromMinutes(2)),
                    Ships = updatedShips,
                    LastUpdated = DateTimeOffset.UtcNow
                };

                return Task.CompletedTask;
            });
        }
    }

    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(storedPlayer);
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
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

        var updatedMembers = RemoveFleetMembersForAliases(fleet.Members, targetAliases);
        fleets[fleetCode] = fleet with
        {
            MemberPermissions = RemoveFleetPermissionsForAliases(fleet.MemberPermissions, targetAliases),
            Members = updatedMembers,
            Squads = PruneEmptySquads(fleet.Squads, updatedMembers),
            Ships = RemoveFleetShipsForAliases(fleet.Ships, targetAliases),
            ActionPlans = RemoveActionPlanParticipants(fleet.ActionPlans, null, targetAliases),
            Applications = RemoveFleetApplicationsForAliases(fleet.Applications, targetAliases),
            EventLog = AddFleetLog(fleet.EventLog, "成员", "移除成员", $"{targetName} 被移出舰队"),
            LastUpdated = DateTimeOffset.UtcNow
        };

        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(new { removed, target = targetName });
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
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
            Squads = PruneEmptySquads(fleet.Squads, members),
            Ships = ships,
            EventLog = AddFleetLog(fleet.EventLog, "成员", "移除小队成员", $"{targetDisplay} 被移出 {squadName}"),
            LastUpdated = now
        };

        fleets[fleetCode] = updated;
        await storage.SaveAsync(players, fleets, users, verificationCodes);
        return Results.Ok(new { removed, target = targetName, squad = squadName });
    });
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
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
    return await WithFleetWriteLockAsync(fleetWriteLocks, fleetCode, async () =>
    {
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
    fleetWriteLocks.Clear();
    await storage.SaveAsync(players, fleets, users, verificationCodes);
    return Results.Ok(new { cleared = true });
});

app.Run();

static bool IsWriteAllowed(
    HttpRequest request,
    string? serverKey,
    bool allowOpenTest,
    ConcurrentDictionary<string, UserAccount> users)
{
    if (IsRelayKeyAllowed(request, serverKey))
    {
        return true;
    }

    if (allowOpenTest && string.IsNullOrWhiteSpace(serverKey))
    {
        return true;
    }

    return !string.IsNullOrWhiteSpace(GetAuthorizedUserName(request, users));
}

static bool IsReadAllowed(
    HttpRequest request,
    string? serverKey,
    bool allowOpenTest,
    ConcurrentDictionary<string, UserAccount> users)
{
    return IsRelayKeyAllowed(request, serverKey) ||
           allowOpenTest ||
           !string.IsNullOrWhiteSpace(GetAuthorizedUserName(request, users));
}

static string GetRelayMode(string? serverKey, bool allowOpenTest)
{
    if (!string.IsNullOrWhiteSpace(serverKey))
    {
        return "protected";
    }

    return allowOpenTest ? "open-test" : "locked-missing-key";
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
        return "验证码发送过于频繁，请 60 秒后再试。";
    }

    var ipWindow = ipWindows.AddOrUpdate(
        clientIp,
        _ => new VerificationRateWindow(now, 1),
        (_, existing) => now - existing.StartedAt >= TimeSpan.FromMinutes(1)
            ? new VerificationRateWindow(now, 1)
            : existing with { Count = existing.Count + 1 });

    if (ipWindow.Count > 5)
    {
        return "当前网络验证码请求过多，请稍后再试。";
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

static string? ValidateFeedbackRateLimit(
    ConcurrentDictionary<string, DateTimeOffset> contactLastSentAt,
    ConcurrentDictionary<string, VerificationRateWindow> ipWindows,
    string contact,
    string clientIp,
    DateTimeOffset now)
{
    var contactKey = Normalize(contact, "guest").Trim().ToLowerInvariant();
    if (contactLastSentAt.TryGetValue(contactKey, out var lastSentAt) &&
        now - lastSentAt < TimeSpan.FromSeconds(60))
    {
        return "反馈发送过于频繁，请 60 秒后再试。";
    }

    var ipWindow = ipWindows.AddOrUpdate(
        clientIp,
        _ => new VerificationRateWindow(now, 1),
        (_, existing) => now - existing.StartedAt >= TimeSpan.FromMinutes(1)
            ? new VerificationRateWindow(now, 1)
            : existing with { Count = existing.Count + 1 });

    if (ipWindow.Count > 8)
    {
        return "当前网络反馈请求过多，请稍后再试。";
    }

    contactLastSentAt[contactKey] = now;
    return null;
}

static string RedactEmail(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "unknown";
    }

    var trimmed = value.Trim();
    var at = trimmed.IndexOf('@');
    if (at <= 0)
    {
        return trimmed.Length <= 2 ? "***" : $"{trimmed[..2]}***";
    }

    var prefix = trimmed[..at];
    var domain = trimmed[(at + 1)..];
    var visible = prefix.Length <= 2 ? prefix[..1] : prefix[..2];
    return $"{visible}***@{domain}";
}

static string? NormalizeEnvironmentText(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    // Some Linux service env edits can turn UTF-8 release notes into Latin-1 mojibake.
    // Repair only when the decoded text clearly contains more CJK characters.
    if (!value.Contains('Ã') &&
        !value.Contains('Â') &&
        !value.Contains('ä') &&
        !value.Contains('å') &&
        !value.Contains('æ') &&
        !value.Contains('è') &&
        !value.Contains('ç'))
    {
        return value;
    }

    try
    {
        var decoded = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value));
        return decoded.Count(IsCjk) > value.Count(IsCjk) ? decoded : value;
    }
    catch
    {
        return value;
    }
}

static string? DecodeEnvironmentBase64(string key)
{
    var value = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    try
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
    }
    catch
    {
        return null;
    }
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
            if (squads.TryGetValue(normalized.Name, out var current))
            {
                if (IsSquadSnapshotOlder(current, normalized))
                {
                    continue;
                }

                normalized = normalized with
                {
                    EmblemImageData = MergeSquadEmblemImage(current, normalized)
                };
            }

            squads[normalized.Name] = normalized;
        }
    }

    return squads.Values.OrderBy(squad => squad.Name).ToArray();
}

static NetworkSquadSnapshot[] MergeAuthorizedSquads(
    NetworkSquadSnapshot[]? existing,
    NetworkSquadSnapshot[]? incoming,
    NetworkFleetSnapshot fleet,
    UserAccount? account,
    bool canManageAllSquads)
{
    if (canManageAllSquads)
    {
        return MergeSquads(existing, incoming);
    }

    var squads = NormalizeSquadRows(existing)
        .ToDictionary(squad => squad.Name, StringComparer.OrdinalIgnoreCase);
    if (account is null || !IsFleetMember(fleet, account))
    {
        return squads.Values.OrderBy(squad => squad.Name).ToArray();
    }

    var accountAliases = BuildAccountAliases(account);
    var alreadyCommandsSquad = squads.Values.Any(squad =>
        IdentityContainsAny(squad.Commander, accountAliases));

    foreach (var incomingSquad in NormalizeSquadRows(incoming))
    {
        if (squads.TryGetValue(incomingSquad.Name, out var current))
        {
            if (!IdentityContainsAny(current.Commander, accountAliases))
            {
                continue;
            }

            squads[current.Name] = MergeSquadForCommander(current, incomingSquad);
            continue;
        }

        if (alreadyCommandsSquad || !IdentityContainsAny(incomingSquad.Commander, accountAliases))
        {
            continue;
        }

        squads[incomingSquad.Name] = incomingSquad;
        alreadyCommandsSquad = true;
    }

    return squads.Values.OrderBy(squad => squad.Name).ToArray();
}

static bool HasAuthorizedSquadWrite(
    NetworkSquadSnapshot[]? existing,
    NetworkSquadSnapshot[]? incoming,
    NetworkFleetSnapshot fleet,
    UserAccount account)
{
    var merged = MergeAuthorizedSquads(existing, incoming, fleet, account, canManageAllSquads: false);
    var current = NormalizeSquadRows(existing).OrderBy(squad => squad.Name).ToArray();
    if (merged.Length != current.Length)
    {
        return true;
    }

    for (var index = 0; index < merged.Length; index++)
    {
        if (!SquadEquals(merged[index], current[index]))
        {
            return true;
        }
    }

    return false;
}

static NetworkSquadSnapshot[] NormalizeSquadRows(NetworkSquadSnapshot[]? squads)
{
    return (squads ?? [])
        .Where(squad => !string.IsNullOrWhiteSpace(squad.Name))
        .Select(NormalizeSquad)
        .GroupBy(squad => squad.Name, StringComparer.OrdinalIgnoreCase)
        .Select(SelectLatestSquadSnapshot)
        .ToArray();
}

static NetworkSquadSnapshot SelectLatestSquadSnapshot(IEnumerable<NetworkSquadSnapshot> snapshots)
{
    NetworkSquadSnapshot? selected = null;
    foreach (var snapshot in snapshots)
    {
        if (selected is null || IsSquadSnapshotOlder(selected, snapshot))
        {
            selected = snapshot;
        }
    }

    return selected ?? new NetworkSquadSnapshot("Unnamed", null, null, null);
}

static NetworkSquadSnapshot MergeSquadForCommander(
    NetworkSquadSnapshot current,
    NetworkSquadSnapshot incoming)
{
    if (IsSquadSnapshotOlder(current, incoming))
    {
        return current;
    }

    return incoming with
    {
        Name = current.Name,
        Commander = current.Commander,
        EmblemImageData = MergeSquadEmblemImage(current, incoming)
    };
}

static string? MergeSquadEmblemImage(NetworkSquadSnapshot current, NetworkSquadSnapshot incoming)
{
    if (string.IsNullOrWhiteSpace(incoming.EmblemImageData))
    {
        return current.EmblemImageData;
    }

    if (string.IsNullOrWhiteSpace(current.EmblemImageData))
    {
        return incoming.EmblemImageData;
    }

    if (current.EmblemImageData.Equals(incoming.EmblemImageData, StringComparison.Ordinal))
    {
        return incoming.EmblemImageData;
    }

    if (current.UpdatedAt != default &&
        (incoming.UpdatedAt == default || incoming.UpdatedAt <= current.UpdatedAt))
    {
        return current.EmblemImageData;
    }

    return incoming.EmblemImageData;
}

static bool SquadEquals(NetworkSquadSnapshot left, NetworkSquadSnapshot right)
{
    return left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
           Normalize(left.Commander, "").Equals(Normalize(right.Commander, ""), StringComparison.OrdinalIgnoreCase) &&
           Normalize(left.Type, "").Equals(Normalize(right.Type, ""), StringComparison.OrdinalIgnoreCase) &&
           Normalize(left.Description, "").Equals(Normalize(right.Description, ""), StringComparison.Ordinal) &&
           Normalize(left.Mission, "").Equals(Normalize(right.Mission, ""), StringComparison.Ordinal) &&
           Normalize(left.RallyPoint, "").Equals(Normalize(right.RallyPoint, ""), StringComparison.Ordinal) &&
           Normalize(left.EmblemImageData, "").Equals(Normalize(right.EmblemImageData, ""), StringComparison.Ordinal) &&
           left.UpdatedAt.Equals(right.UpdatedAt);
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

static bool IsSquadSnapshotOlder(NetworkSquadSnapshot current, NetworkSquadSnapshot incoming)
{
    if (current.UpdatedAt == default)
    {
        return false;
    }

    return incoming.UpdatedAt == default || incoming.UpdatedAt < current.UpdatedAt;
}

static NetworkSquadSnapshot[] PruneEmptySquads(
    NetworkSquadSnapshot[]? squads,
    NetworkFleetMemberSnapshot[]? members,
    TimeSpan? emptySquadGracePeriod = null)
{
    var normalizedSquads = NormalizeSquadRows(squads);
    if (normalizedSquads.Length == 0 || members is null)
    {
        return normalizedSquads;
    }

    var now = DateTimeOffset.UtcNow;
    var membersBySquad = NormalizeFleetMembers(members, null)
        .Where(member => !IsUnassignedSquadName(member.SquadName))
        .GroupBy(member => Normalize(member.SquadName, "Unassigned"), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

    var repairedSquads = new List<NetworkSquadSnapshot>();
    foreach (var squad in normalizedSquads)
    {
        if (!membersBySquad.TryGetValue(squad.Name, out var squadMembers) || squadMembers.Length == 0)
        {
            if (emptySquadGracePeriod is { } gracePeriod &&
                squad.UpdatedAt != default &&
                now - squad.UpdatedAt <= gracePeriod)
            {
                repairedSquads.Add(squad);
            }

            continue;
        }

        if (!squadMembers.Any(member => SquadCommanderMatchesMember(squad, member)) &&
            PickRecommendedSquadSuccessor(squadMembers) is { } successor)
        {
            repairedSquads.Add(squad with
            {
                Commander = DisplayMember(successor),
                UpdatedAt = now
            });
            continue;
        }

        repairedSquads.Add(squad);
    }

    return repairedSquads
        .OrderBy(squad => squad.Name)
        .ToArray();
}

static bool IsUnassignedSquadName(string? squadName)
{
    var normalized = Normalize(squadName, "Unassigned");
    return string.IsNullOrWhiteSpace(normalized) ||
           normalized.Equals("Unassigned", StringComparison.OrdinalIgnoreCase) ||
           normalized.Equals("未加入小队", StringComparison.OrdinalIgnoreCase);
}

static bool SquadCommanderMatchesMember(NetworkSquadSnapshot squad, NetworkFleetMemberSnapshot member)
{
    var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    AddIdentityAliases(aliases, member.GameName);
    AddIdentityAliases(aliases, member.Callsign);
    AddIdentityAliases(aliases, DisplayMember(member));

    return IdentityContainsAny(squad.Commander, aliases);
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

static string ResolveActionPlanTitle(NetworkActionPlanSnapshot[]? actionPlans, string planId)
{
    var plan = (actionPlans ?? [])
        .FirstOrDefault(item => item.Id.Equals(planId, StringComparison.OrdinalIgnoreCase));
    return string.IsNullOrWhiteSpace(plan?.Title)
        ? "行动计划"
        : plan.Title.Trim();
}

static string FormatActionPlanParticipant(
    NetworkActionPlanParticipantSnapshot? participant,
    UserAccount account,
    NetworkPlayerSnapshot? player)
{
    var gameName = Normalize(participant?.GameName, Normalize(player?.Name, Normalize(account.GameName, account.UserName)));
    var callsign = Normalize(participant?.Callsign, Normalize(player?.Callsign, Normalize(account.Callsign, "")));
    return string.IsNullOrWhiteSpace(callsign) || callsign.Equals(gameName, StringComparison.OrdinalIgnoreCase)
        ? gameName
        : $"{callsign} ({gameName})";
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
        player?.AvatarImageData,
        Normalize(player?.LocationConfidence, "None"));
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
        NormalizeImageData(application.AvatarImageData, 512 * 1024),
        Normalize(player?.LocationConfidence, "None"));
}

static NetworkFleetSnapshot? FindOwnedFleetForAccount(
    ConcurrentDictionary<string, NetworkFleetSnapshot> fleets,
    string exceptFleetCode,
    UserAccount account)
{
    foreach (var fleetCode in fleets.Keys.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
    {
        if (fleetCode.Equals(exceptFleetCode, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (fleets.TryGetValue(fleetCode, out var fleet) && IsFleetOwner(fleet, account))
        {
            return fleet;
        }
    }

    return null;
}

static async Task RemoveAccountFromOtherFleetsAsync(
    ConcurrentDictionary<string, NetworkFleetSnapshot> fleets,
    ConcurrentDictionary<string, NetworkPlayerSnapshot> players,
    ConcurrentDictionary<string, SemaphoreSlim> fleetWriteLocks,
    string keepFleetCode,
    UserAccount account)
{
    var aliases = BuildAccountAliases(account);
    var fleetCodes = fleets.Keys
        .Where(code => !code.Equals(keepFleetCode, StringComparison.OrdinalIgnoreCase))
        .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    foreach (var fleetCode in fleetCodes)
    {
        await WithFleetWriteLockActionAsync(fleetWriteLocks, fleetCode, () =>
        {
            if (!fleets.TryGetValue(fleetCode, out var fleet) ||
                IsFleetOwner(fleet, account) ||
                !FleetContainsAnyIdentity(fleet, aliases))
            {
                return Task.CompletedTask;
            }

            var disbandedSquads = FindCommandedSquadNames(fleet, aliases);
            var eventLog = fleet.EventLog;
            foreach (var squadName in disbandedSquads.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                eventLog = AddFleetLog(eventLog, "小队", "小队解散", $"小队指挥官离开舰队，{squadName} 已解散");
            }

            ClearDisbandedSquadsFromPlayers(players, fleet, fleetCode, disbandedSquads);

            fleets[fleetCode] = RemoveFleetIdentity(fleet, aliases, disbandCommandedSquads: true) with
            {
                EventLog = AddFleetLog(
                    eventLog,
                    "成员",
                    "玩家离开",
                    $"{FormatAccountIdentity(account, FindPlayerForAccount(players, account))} 离开舰队"),
                LastUpdated = DateTimeOffset.UtcNow
            };

            return Task.CompletedTask;
        });
    }
}

static void ClearDisbandedSquadsFromPlayers(
    ConcurrentDictionary<string, NetworkPlayerSnapshot> players,
    NetworkFleetSnapshot fleet,
    string fleetCode,
    HashSet<string> disbandedSquads)
{
    if (disbandedSquads.Count == 0)
    {
        return;
    }

    foreach (var pair in players.ToArray())
    {
        if (!PlayerBelongsToFleet(pair.Value, fleet, fleetCode) ||
            !disbandedSquads.Contains(Normalize(pair.Value.Squad, "Unassigned")))
        {
            continue;
        }

        players[pair.Key] = pair.Value with
        {
            Squad = "Unassigned",
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

static NetworkFleetSnapshot RemoveFleetIdentity(
    NetworkFleetSnapshot fleet,
    HashSet<string> aliases,
    bool disbandCommandedSquads = false)
{
    var disbandedSquads = disbandCommandedSquads
        ? FindCommandedSquadNames(fleet, aliases)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var members = RemoveFleetMembersForAliases(fleet.Members, aliases);
    if (disbandedSquads.Count > 0)
    {
        members = members
            .Select(member => disbandedSquads.Contains(Normalize(member.SquadName, "Unassigned"))
                ? member with
                {
                    SquadName = "Unassigned",
                    LastUpdated = DateTimeOffset.UtcNow
                }
                : member)
            .ToArray();
    }

    var squads = NormalizeSquadRows(fleet.Squads)
        .Where(squad => !disbandedSquads.Contains(squad.Name))
        .ToArray();

    return fleet with
    {
        Members = members,
        Squads = PruneEmptySquads(squads, members),
        MemberPermissions = RemoveFleetPermissionsForAliases(fleet.MemberPermissions, aliases),
        Ships = RemoveFleetShipsForAliases(fleet.Ships, aliases),
        Applications = RemoveFleetApplicationsForAliases(fleet.Applications, aliases),
        ActionPlans = RemoveActionPlanParticipants(fleet.ActionPlans, null, aliases),
        LastUpdated = DateTimeOffset.UtcNow
    };
}

static HashSet<string> FindCommandedSquadNames(NetworkFleetSnapshot fleet, HashSet<string> aliases)
{
    return NormalizeSquadRows(fleet.Squads)
        .Where(squad => IdentityContainsAny(squad.Commander, aliases))
        .Select(squad => squad.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static bool FleetMemberContainsAny(NetworkFleetMemberSnapshot member, HashSet<string> aliases)
{
    return IdentityContainsAny(member.GameName, aliases) ||
           IdentityContainsAny(member.Callsign, aliases) ||
           IdentityContainsAny(DisplayMember(member), aliases);
}

static bool PlayerMatchesAccountOrAliases(
    string playerKey,
    NetworkPlayerSnapshot player,
    UserAccount account,
    HashSet<string> aliases)
{
    return IdentityContainsAny(playerKey, aliases) ||
           MatchesAccountIdentity(player.Name, account) ||
           MatchesAccountIdentity(player.Callsign, account);
}

static bool PlayerBelongsToFleet(NetworkPlayerSnapshot player, NetworkFleetSnapshot fleet, string fleetCode)
{
    var playerFleet = Normalize(player.Fleet, "");
    return playerFleet.Equals(fleetCode, StringComparison.OrdinalIgnoreCase) ||
           playerFleet.Equals(Normalize(fleet.Code, fleetCode), StringComparison.OrdinalIgnoreCase) ||
           playerFleet.Equals(Normalize(fleet.Name, ""), StringComparison.OrdinalIgnoreCase);
}

static NetworkFleetSnapshot ToPublicFleetSnapshot(NetworkFleetSnapshot fleet)
{
    return fleet with
    {
        NoticeTitle = null,
        NoticeContent = null,
        CurrentTaskTitle = null,
        CurrentTaskBrief = null,
        CurrentTaskParticipants = null,
        CurrentTaskRally = null,
        CurrentTaskShip = null,
        CurrentTaskTime = null,
        ActionPlans = [],
        OwnerAccount = null,
        MemberPermissions = [],
        Members = [],
        EventLog = [],
        Ships = [],
        TaskHistory = [],
        Applications = [],
        Squads = [],
        CurrentTaskNoticeRevision = 0
    };
}

static UserAccount CreateAccount(
    string name,
    string password,
    string? gameName,
    string? email,
    string? callsign,
    out string authToken)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = HashPassword(password, salt);
    authToken = CreateSessionToken();
    return new UserAccount(
        name,
        string.IsNullOrWhiteSpace(callsign) ? name : callsign.Trim(),
        Normalize(gameName, name),
        "",
        Convert.ToBase64String(salt),
        Convert.ToBase64String(hash),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
        true,
        HashAuthToken(authToken),
        GetAuthTokenExpiresAt());
}

static AuthResponse ToAuthResponse(UserAccount account, string? tokenOverride = null)
{
    return new AuthResponse(
        account.UserName,
        account.Email,
        account.Callsign,
        account.GameName,
        tokenOverride ?? account.AuthToken,
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
            player.AvatarImageData,
            Normalize(player.LocationConfidence, "None"));
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
            LocationConfidence = Normalize(member.LocationConfidence, "None"),
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
            var mergedMember = existing.LastUpdated > member.LastUpdated
                ? existing with
                {
                    Callsign = string.IsNullOrWhiteSpace(existing.Callsign)
                        ? Normalize(member.Callsign, "")
                        : existing.Callsign,
                    AvatarImageData = string.IsNullOrWhiteSpace(existing.AvatarImageData)
                        ? NormalizeImageData(member.AvatarImageData, 512 * 1024)
                        : existing.AvatarImageData
                }
                : member with
                {
                    AvatarImageData = string.IsNullOrWhiteSpace(member.AvatarImageData)
                        ? existing.AvatarImageData
                        : member.AvatarImageData
                };
            rows[member.GameName] = mergedMember;
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
    var existingByKey = existing.ToDictionary(
        ship => BuildFleetShipKey(ship.OwnerGameName, ship.Code),
        StringComparer.OrdinalIgnoreCase);
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
        var key = BuildFleetShipKey(ship.OwnerGameName, ship.Code);
        rows[key] = existingByKey.TryGetValue(key, out var existingShip) &&
                    string.IsNullOrWhiteSpace(ship.OwnerAvatarImageData)
            ? ship with { OwnerAvatarImageData = existingShip.OwnerAvatarImageData }
            : ship;
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
    var existingMembers = NormalizeFleetMembers(members);
    var existing = existingMembers.FirstOrDefault(item =>
        IdentityContainsAny(item.GameName, memberAliases) ||
        IdentityContainsAny(item.Callsign, memberAliases));
    var normalizedMember = string.IsNullOrWhiteSpace(member.AvatarImageData) &&
                           !string.IsNullOrWhiteSpace(existing?.AvatarImageData)
        ? member with { AvatarImageData = existing.AvatarImageData }
        : member;
    var rows = existingMembers
        .Where(item => !IdentityContainsAny(item.GameName, memberAliases) &&
                       !IdentityContainsAny(item.Callsign, memberAliases))
        .Append(normalizedMember)
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

static NetworkFleetMemberPermissionSnapshot[] ResetFleetPermissionForMember(
    NetworkFleetMemberPermissionSnapshot[]? permissions,
    NetworkFleetMemberSnapshot member)
{
    var memberAliases = ExpandIdentityAliases(member.GameName)
        .Concat(ExpandIdentityAliases(member.Callsign))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var retainedPermissions = (permissions ?? [])
        .Where(permission =>
            !IdentityContainsAny(permission.GameName, memberAliases) &&
            !IdentityContainsAny(permission.Callsign, memberAliases))
        .ToArray();

    return retainedPermissions
        .Append(new NetworkFleetMemberPermissionSnapshot(
            member.GameName,
            member.Callsign,
            "成员",
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
    var normalizedLogs = (logs ?? [])
        .Where(log => !string.IsNullOrWhiteSpace(log.Id) ||
                      !string.IsNullOrWhiteSpace(log.Title) ||
                      !string.IsNullOrWhiteSpace(log.Detail))
        .Select(log => log with
        {
            Id = string.IsNullOrWhiteSpace(log.Id) ? "" : log.Id.Trim(),
            Timestamp = log.Timestamp == default ? DateTimeOffset.UtcNow : log.Timestamp,
            Type = Normalize(log.Type, "舰队"),
            Title = Normalize(log.Title, ""),
            Detail = Normalize(log.Detail, "")
        })
        .ToArray();

    var rows = new Dictionary<string, NetworkFleetEventLogSnapshot>(StringComparer.OrdinalIgnoreCase);
    foreach (var log in normalizedLogs.OrderBy(log => log.Timestamp))
    {
        var key = BuildFleetEventLogSemanticKey(log);
        var row = string.IsNullOrWhiteSpace(log.Id)
            ? log with { Id = Guid.NewGuid().ToString("N") }
            : log;

        if (!rows.TryGetValue(key, out var existing) || row.Timestamp >= existing.Timestamp)
        {
            rows[key] = row;
        }
    }

    return rows.Values
        .OrderByDescending(log => log.Timestamp)
        .Take(500)
        .ToArray();
}

static string BuildFleetEventLogSemanticKey(NetworkFleetEventLogSnapshot log)
{
    var minuteBucket = log.Timestamp.ToUniversalTime().Ticks / TimeSpan.TicksPerMinute;
    var type = Normalize(log.Type, "舰队").Trim().ToUpperInvariant();
    var title = Normalize(log.Title, "").Trim().ToUpperInvariant();
    var detail = Normalize(log.Detail, "").Trim().ToUpperInvariant();
    return $"{minuteBucket}|{type}|{title}|{detail}";
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

static bool TextEquals(string? left, string? right)
{
    return Normalize(left, "").Equals(Normalize(right, ""), StringComparison.Ordinal);
}

static bool IsTaskActive(string? title, string? brief, string? rally, string? ship)
{
    return !string.IsNullOrWhiteSpace(Normalize(title, "")) ||
           !string.IsNullOrWhiteSpace(Normalize(brief, "")) ||
           !string.IsNullOrWhiteSpace(Normalize(rally, "")) ||
           !string.IsNullOrWhiteSpace(Normalize(ship, ""));
}

static bool TaskEquals(
    string? leftTitle,
    string? leftBrief,
    string? leftParticipants,
    string? leftRally,
    string? leftShip,
    DateTime? leftTime,
    string? rightTitle,
    string? rightBrief,
    string? rightParticipants,
    string? rightRally,
    string? rightShip,
    DateTime? rightTime)
{
    return TextEquals(leftTitle, rightTitle) &&
           TextEquals(leftBrief, rightBrief) &&
           TextEquals(leftParticipants, rightParticipants) &&
           TextEquals(leftRally, rightRally) &&
           TextEquals(leftShip, rightShip) &&
           Nullable.Equals(leftTime, rightTime);
}

static string FormatTaskLogDetail(
    string? title,
    string? participants,
    string? rally,
    string? ship,
    DateTime? time)
{
    var rows = new List<string>
    {
        Normalize(title, "未命名任务")
    };
    if (!string.IsNullOrWhiteSpace(participants))
    {
        rows.Add($"参与范围：{participants}");
    }

    if (!string.IsNullOrWhiteSpace(rally))
    {
        rows.Add($"集结点：{rally}");
    }

    if (!string.IsNullOrWhiteSpace(ship))
    {
        rows.Add($"指定舰船：{ship}");
    }

    if (time is not null)
    {
        rows.Add($"时间：{time:yyyy-MM-dd HH:mm}");
    }

    return string.Join(" / ", rows);
}

static NetworkFleetEventLogSnapshot[] AddFleetTaskHistoryChangeLogs(
    NetworkFleetEventLogSnapshot[]? logs,
    NetworkFleetTaskHistorySnapshot[]? existingHistory,
    NetworkFleetTaskHistorySnapshot[]? incomingHistory)
{
    var eventLog = logs ?? [];
    var existingRows = NormalizeFleetTaskHistory(existingHistory)
        .ToDictionary(task => task.Key, StringComparer.OrdinalIgnoreCase);
    foreach (var task in NormalizeFleetTaskHistory(incomingHistory))
    {
        if (!existingRows.TryGetValue(task.Key, out var existing))
        {
            eventLog = AddFleetLog(eventLog, "任务", "记录任务", FormatTaskHistoryLogDetail(task));
            continue;
        }

        if (!TextEquals(existing.Status, task.Status))
        {
            eventLog = AddFleetLog(eventLog, "任务", "更新任务状态", FormatTaskHistoryLogDetail(task));
        }
    }

    return eventLog;
}

static string FormatTaskHistoryLogDetail(NetworkFleetTaskHistorySnapshot task)
{
    var title = Normalize(task.Title, "未命名任务");
    var status = Normalize(task.Status, "未知状态");
    return $"{title} / {status}";
}

static NetworkFleetEventLogSnapshot[] AddActionPlanChangeLogs(
    NetworkFleetEventLogSnapshot[]? logs,
    NetworkActionPlanSnapshot[]? existingPlans,
    NetworkActionPlanSnapshot[]? incomingPlans)
{
    var eventLog = logs ?? [];
    var existingRows = NormalizeActionPlans(existingPlans)
        .ToDictionary(plan => plan.Id, StringComparer.OrdinalIgnoreCase);
    var incomingRows = NormalizeActionPlans(incomingPlans)
        .ToDictionary(plan => plan.Id, StringComparer.OrdinalIgnoreCase);

    foreach (var plan in incomingRows.Values.OrderBy(plan => plan.StartTime))
    {
        if (!existingRows.TryGetValue(plan.Id, out var existing))
        {
            eventLog = AddFleetLog(eventLog, "计划", "发布行动计划", FormatActionPlanLogDetail(plan));
            continue;
        }

        if (!ActionPlanEquals(existing, plan))
        {
            eventLog = AddFleetLog(eventLog, "计划", "更新行动计划", FormatActionPlanLogDetail(plan));
        }
    }

    foreach (var plan in existingRows.Values.OrderBy(plan => plan.StartTime))
    {
        if (!incomingRows.ContainsKey(plan.Id))
        {
            eventLog = AddFleetLog(eventLog, "计划", "取消行动计划", FormatActionPlanLogDetail(plan));
        }
    }

    return eventLog;
}

static bool ActionPlanEquals(NetworkActionPlanSnapshot left, NetworkActionPlanSnapshot right)
{
    return TextEquals(left.Title, right.Title) &&
           TextEquals(left.Content, right.Content) &&
           left.StartTime.Equals(right.StartTime) &&
           left.NotifyMembers == right.NotifyMembers;
}

static string FormatActionPlanLogDetail(NetworkActionPlanSnapshot plan)
{
    var time = plan.StartTime == default
        ? "时间未定"
        : plan.StartTime.ToString("yyyy-MM-dd HH:mm");
    return $"{Normalize(plan.Title, "未命名行动")} / {time}";
}

static NetworkFleetMemberPermissionSnapshot? FindPermissionForIdentity(
    NetworkFleetMemberPermissionSnapshot[]? permissions,
    NetworkFleetMemberPermissionSnapshot target)
{
    var aliases = ExpandIdentityAliases(target.GameName)
        .Concat(ExpandIdentityAliases(target.Callsign))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return NormalizeFleetMemberPermissions(permissions)
        .FirstOrDefault(permission =>
            IdentityContainsAny(permission.GameName, aliases) ||
            IdentityContainsAny(permission.Callsign, aliases));
}

static bool PermissionEquals(
    NetworkFleetMemberPermissionSnapshot left,
    NetworkFleetMemberPermissionSnapshot right)
{
    return TextEquals(left.GameName, right.GameName) &&
           TextEquals(left.Callsign, right.Callsign) &&
           TextEquals(left.RoleTitle, right.RoleTitle) &&
           left.PermissionEnabled == right.PermissionEnabled &&
           left.CanRemoveMembers == right.CanRemoveMembers &&
           left.CanPublishTasks == right.CanPublishTasks &&
           left.CanPublishPlans == right.CanPublishPlans &&
           left.CanManageFleetInfo == right.CanManageFleetInfo;
}

static string FormatPermissionLogDetail(NetworkFleetMemberPermissionSnapshot permission)
{
    var display = string.IsNullOrWhiteSpace(permission.Callsign)
        ? permission.GameName
        : $"{permission.Callsign} ({permission.GameName})";
    var enabled = permission.PermissionEnabled ? "已启用" : "已关闭";
    return $"{display} / {Normalize(permission.RoleTitle, "成员")} / {enabled}";
}

static string[] BuildFleetInfoChangeList(
    NetworkFleetSnapshot fleet,
    string description,
    string type,
    string activeTime,
    string joinPolicy,
    string logoText,
    string? logoImageData)
{
    var changes = new List<string>();
    if (!TextEquals(fleet.Description, description))
    {
        changes.Add("简介");
    }

    if (!TextEquals(fleet.Type, type))
    {
        changes.Add("类型");
    }

    if (!TextEquals(fleet.ActiveTime, activeTime))
    {
        changes.Add("活动时间");
    }

    if (!TextEquals(fleet.JoinPolicy, joinPolicy))
    {
        changes.Add("加入方式");
    }

    if (!TextEquals(fleet.LogoText, logoText))
    {
        changes.Add("队标文字");
    }

    if (!string.IsNullOrWhiteSpace(logoImageData) && !TextEquals(fleet.LogoImageData, logoImageData))
    {
        changes.Add("舰队队标");
    }

    return changes.ToArray();
}

static NetworkFleetEventLogSnapshot[] AddSquadChangeLogs(
    NetworkFleetEventLogSnapshot[]? logs,
    NetworkSquadSnapshot[]? existingSquads,
    NetworkSquadSnapshot[]? incomingSquads)
{
    var eventLog = logs ?? [];
    var existingRows = NormalizeSquadRows(existingSquads)
        .ToDictionary(squad => squad.Name, StringComparer.OrdinalIgnoreCase);
    var incomingRows = NormalizeSquadRows(incomingSquads)
        .ToDictionary(squad => squad.Name, StringComparer.OrdinalIgnoreCase);

    foreach (var squad in incomingRows.Values.OrderBy(squad => squad.Name))
    {
        if (!existingRows.TryGetValue(squad.Name, out var existing))
        {
            eventLog = AddFleetLog(eventLog, "小队", "创建小队", FormatSquadLogDetail(squad));
            continue;
        }

        if (!SquadCoreEquals(existing, squad))
        {
            eventLog = AddFleetLog(eventLog, "小队", "更新小队", FormatSquadLogDetail(squad));
        }
    }

    foreach (var squad in existingRows.Values.OrderBy(squad => squad.Name))
    {
        if (!incomingRows.ContainsKey(squad.Name))
        {
            eventLog = AddFleetLog(eventLog, "小队", "解散小队", FormatSquadLogDetail(squad));
        }
    }

    return eventLog;
}

static bool SquadCoreEquals(NetworkSquadSnapshot left, NetworkSquadSnapshot right)
{
    return TextEquals(left.Name, right.Name) &&
           TextEquals(left.Commander, right.Commander) &&
           TextEquals(left.Type, right.Type) &&
           TextEquals(left.Description, right.Description) &&
           TextEquals(left.Mission, right.Mission) &&
           TextEquals(left.RallyPoint, right.RallyPoint) &&
           TextEquals(left.EmblemImageData, right.EmblemImageData);
}

static string FormatSquadLogDetail(NetworkSquadSnapshot squad)
{
    return $"{Normalize(squad.Name, "未命名小队")} / 指挥官：{Normalize(squad.Commander, "Unassigned")}";
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

static NetworkFleetMemberSnapshot? PickRecommendedSquadSuccessor(IEnumerable<NetworkFleetMemberSnapshot> members)
{
    return members
        .OrderByDescending(member => member.Online)
        .ThenByDescending(member => member.LastUpdated)
        .ThenBy(member => Normalize(member.Callsign, member.GameName), StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
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

static async Task<IResult> WithFleetWriteLockAsync(
    ConcurrentDictionary<string, SemaphoreSlim> locks,
    string? fleetCode,
    Func<Task<IResult>> action)
{
    var key = string.IsNullOrWhiteSpace(fleetCode) ? "__unknown_fleet__" : fleetCode.Trim();
    var gate = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync();
    try
    {
        return await action();
    }
    finally
    {
        gate.Release();
    }
}

static async Task WithFleetWriteLockActionAsync(
    ConcurrentDictionary<string, SemaphoreSlim> locks,
    string? fleetCode,
    Func<Task> action)
{
    var key = string.IsNullOrWhiteSpace(fleetCode) ? "__unknown_fleet__" : fleetCode.Trim();
    var gate = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync();
    try
    {
        await action();
    }
    finally
    {
        gate.Release();
    }
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

static string CreateSessionToken()
{
    return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

static DateTimeOffset GetAuthTokenExpiresAt()
{
    return DateTimeOffset.UtcNow.AddDays(30);
}

static string HashAuthToken(string token)
{
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
}

static bool AuthTokenMatches(UserAccount user, string token)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return false;
    }

    if (user.AuthTokenExpiresAt is { } expiresAt && expiresAt < DateTimeOffset.UtcNow)
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(user.AuthTokenHash))
    {
        return user.AuthTokenHash.Equals(HashAuthToken(token), StringComparison.OrdinalIgnoreCase);
    }

    return !string.IsNullOrWhiteSpace(user.AuthToken) &&
           user.AuthToken.Equals(token, StringComparison.Ordinal);
}

static string? GetBearerToken(HttpRequest request)
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

    return value[7..].Trim();
}

static string? GetAuthorizedUserName(HttpRequest request, ConcurrentDictionary<string, UserAccount> users)
{
    var token = GetBearerToken(request);
    return string.IsNullOrWhiteSpace(token)
        ? null
        : users.Values.FirstOrDefault(user => AuthTokenMatches(user, token))?.UserName;
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
        if (!File.Exists(Path))
        {
            return new RelayState([], [], null, null);
        }

        try
        {
            await using var stream = File.OpenRead(Path);
            return await JsonSerializer.DeserializeAsync<RelayState>(stream, JsonOptions) ??
                   throw new InvalidDataException($"Relay state file is empty or invalid: {Path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load relay state from {Path}: {ex.GetType().Name}: {ex.Message}");
            throw;
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

            if (File.Exists(Path))
            {
                File.Copy(Path, $"{Path}.bak", overwrite: true);
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
    DateTimeOffset? PublishedAt = null,
    string? DownloadSha256 = null,
    string? PackageSha256 = null);

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
    string FleetCode,
    string? TransferCommanderTo = null,
    bool ConfirmDisbandIfOwnerAlone = false);

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
    bool AllowEmailNotifications = true,
    string? AuthTokenHash = null,
    DateTimeOffset? AuthTokenExpiresAt = null);

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
    string? AvatarImageData = null,
    string? LocationConfidence = null);

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

public sealed record FleetSquadLeaveRequest(
    string FleetCode,
    string SquadName,
    string? SuccessorGameName = null,
    string? SuccessorCallsign = null);

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
    string? EmblemImageData = null,
    DateTimeOffset UpdatedAt = default);

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
