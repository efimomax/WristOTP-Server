using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using WristOTP.Server.Models;
using WristOTP.Server.Services;
#if DEBUG
using System.Text;
using WristOTP.Server.Diagnostics;
#endif

// WristOTP relay: a purely ephemeral PUT/GET /api/v1/tokens/{id} store.
// No accounts, no profiles, no persistent storage — see RelayStore.
// The server never sees plaintext: the phone encrypts, the watch decrypts.
//
// Релей WristOTP: чисто эфемерное хранилище PUT/GET /api/v1/tokens/{id}.
// Никаких аккаунтов, профилей и постоянного хранения — см. RelayStore.
// Сервер никогда не видит открытых данных: шифрует телефон, расшифровывают часы.

// Max encrypted payload size — plenty for dozens of accounts in one transfer.
// Максимальный размер зашифрованного пакета — с запасом на десятки аккаунтов.
const int MaxPayloadBytes = 10 * 1024;
const string TokenReadRateLimiterPolicy = "token-read";

// Tells the public page (via /api/v1/stats) whether to show the "Debug"
// button. In Release the /debug page does not exist at all (#if DEBUG below).
//
// Сообщает публичной странице (через /api/v1/stats), показывать ли кнопку
// "Debug". В Release страницы /debug физически нет (см. #if DEBUG ниже).
#if DEBUG
const bool IsDebugBuild = true;
#else
const bool IsDebugBuild = false;
#endif

// Same JSON conventions as the clients: camelCase, case-insensitive property
// matching. Set explicitly instead of relying on ASP.NET Core defaults.
//
// Те же правила JSON, что у клиентов: camelCase, сопоставление свойств без
// учёта регистра. Задано явно, а не через дефолты ASP.NET Core.
var tokenPayloadJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var builder = WebApplication.CreateBuilder(args);

// The only place where the address/port is configured. DEBUG listens on all
// interfaces (handy for testing with a phone/watch on the local network).
// Release listens on 127.0.0.1 only — put a reverse proxy (nginx, Caddy, a
// tunnel) with TLS in front of it for external access.
//
// Единственное место настройки адреса/порта. DEBUG слушает все интерфейсы
// (удобно тестировать с телефона/часов в локальной сети). Release — только
// 127.0.0.1: для доступа снаружи перед ним ставится reverse proxy (nginx,
// Caddy, туннель) с TLS.
builder.WebHost.ConfigureKestrel(options =>
{
#if DEBUG
    options.Listen(IPAddress.Any, 8443);
#else
    options.Listen(IPAddress.Loopback, 8443);
#endif
});

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<RelayStats>();
builder.Services.AddSingleton<RelayStore>();
#if DEBUG
builder.Services.AddSingleton<RelayDiagnostics>();
#endif

// Rate limit for GET (one-time read): 5 requests/min/IP in Release (sliding
// 60 s window of six 10 s segments, so attempts come back gradually), 60/min
// in Development. Enough to retype a mistyped code; useless for brute force:
// the LookupID depends on both the specific watch's ODID and the code, and an
// entry lives for one minute. PUT is not limited this way — one-time GET
// already removes any benefit of guessing LookupIDs.
//
// Ограничение частоты GET (одноразовое чтение): 5 запросов/мин/IP в Release
// (скользящее окно 60 с из шести сегментов по 10 с — попытки возвращаются
// постепенно), 60/мин в Development. Хватает, чтобы перенабрать код с
// опечаткой, и бесполезно для перебора: LookupID зависит и от ODID
// конкретных часов, и от кода, а запись живёт минуту. PUT так не
// ограничивается — одноразовость GET и так лишает перебор смысла.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(TokenReadRateLimiterPolicy, httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var permitLimit = builder.Environment.IsDevelopment() ? 60 : 5;

        return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(60),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        });
    });

#if DEBUG
    // Rejections are part of the diagnostics picture. OnRejected runs after
    // routing (the limiter is attached to a specific endpoint), so the "id"
    // route value is already available.
    //
    // Отказы лимитера — тоже часть диагностики. OnRejected срабатывает после
    // роутинга (лимитер навешан на конкретный endpoint), поэтому параметр
    // маршрута "id" уже доступен.
    options.OnRejected = (context, _) =>
    {
        var lookupId = context.HttpContext.Request.RouteValues.TryGetValue("id", out var idValue)
            ? idValue?.ToString() ?? string.Empty
            : string.Empty;
        context.HttpContext.RequestServices
            .GetRequiredService<RelayDiagnostics>()
            .RecordRateLimited(lookupId);
        return ValueTask.CompletedTask;
    };
#endif
});

var app = builder.Build();

// Link to the source repository shown on the public page. Configured in
// appsettings.json (Links:GithubRepoUrl), not hard-coded in index.html, so a
// self-hosted instance can point to its own fork without editing the page.
//
// Ссылка на репозиторий с исходниками для публичной страницы. Задаётся в
// appsettings.json (Links:GithubRepoUrl), а не в index.html — свой экземпляр
// сервера может указать на свой форк, не трогая страницу.
var githubRepoUrl = app.Configuration["Links:GithubRepoUrl"] ?? "";

// Privacy contact shown on /privacy. Empty in the repository — each relay
// operator sets their own (appsettings.json or the Links__ContactEmail
// environment variable); the page then falls back to the repository link.
//
// Контакт по вопросам конфиденциальности для /privacy. В репозитории пустой —
// каждый владелец релея задаёт свой (appsettings.json или переменная
// окружения Links__ContactEmail); иначе страница показывает ссылку на репозиторий.
var contactEmail = app.Configuration["Links:ContactEmail"] ?? "";

// Behind a reverse proxy every request would come from 127.0.0.1, and the
// rate limiter above would treat all clients as a single IP. The client IP is
// taken from X-Forwarded-For, accepted only from loopback (default
// KnownProxies/KnownNetworks), so it cannot be spoofed from outside.
//
// За reverse proxy все запросы приходили бы с 127.0.0.1, и лимитер выше
// считал бы всех клиентов одним IP. IP клиента берётся из X-Forwarded-For,
// который принимается только от loopback (дефолтные KnownProxies/
// KnownNetworks), — подделать его снаружи нельзя.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseRateLimiter();

// Public landing page (wwwroot/index.html) with live, public-safe statistics:
// how many unclaimed entries are stored right now, totals, uptime. Not a
// single LookupID or payload byte is exposed — see RelayStats.
//
// Публичная стартовая страница (wwwroot/index.html) с живой статистикой,
// безопасной для показа: сколько сейчас невостребованных записей, итоги,
// аптайм. Ни одного LookupID и ни байта payload — см. RelayStats.
app.UseDefaultFiles();
app.UseStaticFiles();

// Privacy policy (wwwroot/privacy.html) at a clean /privacy URL — the address
// app stores ask for when publishing the watch and phone apps.
//
// Политика конфиденциальности (wwwroot/privacy.html) по короткому адресу
// /privacy — его требуют магазины приложений при публикации приложений.
app.MapGet("/privacy", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "privacy.html"), "text/html; charset=utf-8"));

// Page settings that do not change while the server runs — separate from
// /api/v1/stats so the page fetches them once instead of polling.
//
// Настройки страницы, которые не меняются во время работы, — отдельно от
// /api/v1/stats, чтобы страница брала их один раз, а не опрашивала.
app.MapGet("/api/v1/config", () => Results.Ok(new { githubRepoUrl, contactEmail }));

app.MapGet("/api/v1/stats", (RelayStore store, RelayStats stats) =>
{
    var recentBuckets = stats.GetRecentBuckets(out var bucketIndex, out var bucketElapsedMs);
    return Results.Ok(new
    {
        activeCount = store.ActiveCount,
        totalPut = stats.TotalPut,
        totalClaimed = stats.TotalClaimed,
        startedAtUtc = stats.StartedAtUtc,
        recentBuckets,
        bucketIndex,
        bucketElapsedMs,
        isDebugBuild = IsDebugBuild
    });
});

app.MapPut("/api/v1/tokens/{id}", async (string id, HttpRequest request, RelayStore store) =>
{
    if (string.IsNullOrWhiteSpace(id))
    {
        return Results.BadRequest();
    }

    // The body is read manually and limited by the bytes actually read, not
    // by the Content-Length header: HttpClient.PutAsJsonAsync sends chunked
    // bodies without Content-Length.
    //
    // Тело читается вручную с ограничением по фактически прочитанным байтам,
    // а не по заголовку Content-Length: HttpClient.PutAsJsonAsync шлёт тело
    // chunked, без Content-Length.
    byte[] body;
    var buffer = new byte[4096];
    using (var ms = new MemoryStream())
    {
        int read;
        while ((read = await request.Body.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + read > MaxPayloadBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            ms.Write(buffer, 0, read);
        }
        body = ms.ToArray();
    }

    if (body.Length == 0)
    {
        return Results.BadRequest();
    }

    TokenPayload? payload;
    try
    {
        payload = JsonSerializer.Deserialize<TokenPayload>(body, tokenPayloadJsonOptions);
    }
    catch (JsonException)
    {
        return Results.BadRequest();
    }

    if (payload is null
        || string.IsNullOrWhiteSpace(payload.Iv)
        || string.IsNullOrWhiteSpace(payload.AuthTag)
        || string.IsNullOrWhiteSpace(payload.Ciphertext))
    {
        return Results.BadRequest();
    }

    store.Put(id, payload);

#if DEBUG
    app.Services.GetRequiredService<RelayDiagnostics>()
        .RecordPut(id, body.Length, DateTimeOffset.UtcNow);
#endif

    return Results.NoContent();
});

app.MapGet("/api/v1/tokens/{id}", (string id, RelayStore store) =>
{
    if (string.IsNullOrWhiteSpace(id))
    {
        return Results.BadRequest();
    }

    var found = store.TryTakeOnce(id, out var payload);

#if DEBUG
    app.Services.GetRequiredService<RelayDiagnostics>().RecordGet(id, found);
#endif

    return found ? Results.Ok(payload) : Results.NotFound();
}).RequireRateLimiting(TokenReadRateLimiterPolicy);

#if DEBUG
// Bump on every change to Program.cs/RelayStore/RelayDiagnostics — shown on
// /debug so you can tell whether the running process has picked up the code.
//
// Увеличивать при каждом изменении Program.cs/RelayStore/RelayDiagnostics —
// показывается на /debug, чтобы было видно, подхватил ли процесс новый код.
const int DebugServerVersion = 3;

// Diagnostics page: live snapshot of active cache entries (LookupID, payload
// size) and a log of recent requests, including rate-limit rejections. Debug
// build only (RelayDiagnostics.cs is excluded from Release). Never shows the
// payload itself — only its size in bytes.
//
// Диагностическая страница: живой снимок активных записей (LookupID, размер
// payload) и лог последних запросов, включая отказы лимитера. Только Debug
// (RelayDiagnostics.cs в Release не компилируется). Само содержимое payload
// никогда не показывается — только размер в байтах.
app.MapGet("/debug", (RelayDiagnostics diagnostics) => Results.Content(RenderDebugPage(diagnostics), "text/html; charset=utf-8"));

// Manual deletion (Delete button on /debug). POST, not GET, so that a crawler
// or an accidental link click cannot wipe entries.
//
// Ручное удаление (кнопка Delete на /debug). POST, а не GET, — чтобы краулер
// или случайный переход по ссылке не стирали записи.
app.MapPost("/debug/cache/{id}/delete", (string id, RelayStore store, RelayDiagnostics diagnostics) =>
{
    store.Delete(id);
    diagnostics.RecordDelete(id);
    return Results.Redirect("/debug");
});

string RenderDebugPage(RelayDiagnostics diagnostics)
{
    var now = DateTimeOffset.UtcNow;
    var sb = new StringBuilder();

    sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
    sb.Append("<meta http-equiv=\"refresh\" content=\"2\">");
    sb.Append("<title>WristOTP relay — debug</title>");
    sb.Append("<style>");
    sb.Append("body{font-family:system-ui,sans-serif;background:#111;color:#eee;padding:16px}");
    sb.Append("h1{font-size:18px}h2{font-size:14px;color:#9c9}");
    sb.Append("table{border-collapse:collapse;width:100%;margin-bottom:24px;font-size:13px}");
    sb.Append("th,td{border:1px solid #333;padding:4px 8px;text-align:left}");
    sb.Append("th{background:#1a1a1a}tr:nth-child(even){background:#181818}");
    sb.Append(".empty{color:#888;font-style:italic}");
    sb.Append(".version{color:#888;font-size:12px;margin:-8px 0 16px}");
    sb.Append("td form{margin:0}");
    sb.Append("td form button{background:#3a1a1a;color:#f88;border:1px solid #663;border-radius:3px;padding:2px 8px;cursor:pointer}");
    sb.Append("</style>");
    sb.Append("</head><body>");
    sb.Append("<h1>WristOTP relay — diagnostics (Debug only)</h1>");
    sb.Append("<div class=\"version\">Server version: ").Append(DebugServerVersion).Append("</div>");

    sb.Append("<h2>Active cache entries</h2>");
    var entries = diagnostics.GetActiveEntries();
    if (entries.Count == 0)
    {
        sb.Append("<p class=\"empty\">No active entries.</p>");
    }
    else
    {
        sb.Append("<table><tr><th>LookupID</th><th>Payload size</th><th>Stored (UTC)</th><th></th></tr>");
        foreach (var e in entries)
        {
            var encodedId = WebUtility.HtmlEncode(e.LookupId);
            sb.Append("<tr><td>").Append(encodedId).Append("</td>");
            sb.Append("<td>").Append(e.PayloadSizeBytes).Append(" bytes</td>");
            sb.Append("<td>").Append(e.StoredAtUtc.ToString("HH:mm:ss")).Append("</td>");
            sb.Append("<td><form method=\"post\" action=\"/debug/cache/").Append(Uri.EscapeDataString(e.LookupId)).Append("/delete\">");
            sb.Append("<button type=\"submit\">Delete</button></form></td></tr>");
        }
        sb.Append("</table>");
    }

    sb.Append("<h2>Recent requests</h2>");
    var log = diagnostics.GetRecentLog();
    if (log.Count == 0)
    {
        sb.Append("<p class=\"empty\">No requests yet.</p>");
    }
    else
    {
        sb.Append("<table><tr><th>Time (UTC)</th><th>Method</th><th>LookupID</th><th>Status</th><th>Detail</th></tr>");
        foreach (var e in log)
        {
            sb.Append("<tr><td>").Append(e.TimestampUtc.ToString("HH:mm:ss.fff")).Append("</td>");
            sb.Append("<td>").Append(e.Method).Append("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(e.LookupId)).Append("</td>");
            sb.Append("<td>").Append(e.StatusCode).Append("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(e.Detail)).Append("</td></tr>");
        }
        sb.Append("</table>");
    }

    sb.Append("</body></html>");
    return sb.ToString();
}
#endif

app.Run();
