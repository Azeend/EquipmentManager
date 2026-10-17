using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace EquipmentManager.Services.Authentication;

/// <summary>Явное включение Яндекс OAuth; секрет клиента хранится исключительно в Supabase.</summary>
public sealed class YandexOAuthSettings
{
    /// <summary>Включает маршруты OAuth после ручной настройки провайдера.</summary>
    public bool Enabled { get; set; }
    /// <summary>Публичный идентификатор приложения Яндекса для проверки полученного токена.</summary>
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>Ограниченное серверное хранилище одноразовых PKCE-попыток; перезапуск безопасно отменяет вход.</summary>
public sealed class OAuthAttemptStore(TimeProvider clock) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly object gate = new();

    /// <summary>Создаёт независимые случайные state, корреляцию браузера и verifier.</summary>
    public (string State, string Browser, string Challenge) Create(string returnUrl, Guid? linkUserId = null, string? sessionId = null)
    {
        var state = Random();
        var browser = Random();
        var verifier = Random();
        lock (gate)
        {
            cache.Set(state, new Attempt(browser, verifier, returnUrl, clock.GetUtcNow().AddMinutes(5), linkUserId, sessionId),
                new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromMinutes(5)));
        }
        return (state, browser, WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    /// <summary>Извлекает попытку ровно один раз только для исходного браузера.</summary>
    public (string Verifier, string ReturnUrl, Guid? LinkUserId, string? SessionId)? Consume(string state, string browser)
    {
        if (state.Length != 43 || browser.Length != 43) { return null; }
        lock (gate)
        {
            if (!cache.TryGetValue<Attempt>(state, out var attempt) || attempt is null
                || attempt.ExpiresAt <= clock.GetUtcNow()
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(browser), Encoding.UTF8.GetBytes(attempt.Browser)))
            { return null; }
            cache.Remove(state);
            return (attempt.Verifier, attempt.ReturnUrl, attempt.LinkUserId, attempt.SessionId);
        }
    }

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private sealed record Attempt(string Browser, string Verifier, string ReturnUrl, DateTimeOffset ExpiresAt, Guid? LinkUserId, string? SessionId);
    /// <inheritdoc />
    public void Dispose() => cache.Dispose();
}

/// <summary>Серверный OAuth через Supabase и адаптер Userinfo; токены не передаются в браузерное хранилище.</summary>
public static class YandexOAuth
{
    internal const string ClientName = "Yandex.Userinfo";
    private const string CookieName = "__Host-EquipmentManager.OAuth";

    /// <summary>Публикует отдельные маршруты, не изменяя Bearer-политики API.</summary>
    public static void MapYandexOAuth(this WebApplication app)
    {
        app.MapGet("/oauth/yandex/userinfo", UserinfoAsync).AllowAnonymous();
        app.MapPost("/account/oauth/yandex", StartAsync).AllowAnonymous();
        app.MapPost("/account/oauth/yandex/link", StartAsync).RequireAuthorization(UiAuthentication.Policy);
        app.MapGet("/account/oauth/callback", CallbackAsync).AllowAnonymous();
    }

    private static bool Ready(YandexOAuthSettings settings, UiAuthSettings auth) =>
        settings.Enabled && !string.IsNullOrWhiteSpace(settings.ClientId) && auth.CanSignIn && auth.BackendBaseUrl is not null;

    private static async Task<IResult> StartAsync(HttpContext context, IAntiforgery antiforgery,
        YandexOAuthSettings settings, UiAuthSettings auth, OAuthAttemptStore attempts, UiSessionStore sessions,
        IHttpClientFactory clients, ILoggerFactory loggers)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps || !await UiAccountEndpoints.ValidateFormAsync(context, antiforgery))
        { return Results.BadRequest("Форма устарела или отсутствует HTTPS."); }
        if (!Ready(settings, auth)) { return Error("configuration"); }
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var linking = context.Request.Path == "/account/oauth/yandex/link";
        var session = linking ? sessions.Find(context.User) : null;
        if (linking && session is null) { return Error("expired"); }
        var attempt = attempts.Create(UiAccountEndpoints.LocalReturnUrl(form["returnUrl"]), session?.UserId,
            linking ? context.User.FindFirst(UiSessionStore.SessionClaim)?.Value : null);
        context.Response.Cookies.Append(CookieName, attempt.Browser, CookieOptions());
        // Внешний state Supabase не переопределяется; наш state возвращается внутри разрешённого redirect_to.
        var callback = QueryHelpers.AddQueryString(new Uri(auth.BackendBaseUrl!, "/account/oauth/callback").AbsoluteUri, "state", attempt.State);
        var parameters = new Dictionary<string, string?>
        {
            ["provider"] = "custom:yandex",
            ["redirect_to"] = callback,
            ["code_challenge"] = attempt.Challenge,
            ["code_challenge_method"] = "s256"
        };
        if (!linking) { return Results.Redirect(QueryHelpers.AddQueryString(auth.Issuer + "/authorize", parameters)); }
        parameters["skip_http_redirect"] = "true";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString(auth.Issuer + "/user/identities/authorize", parameters));
            request.Headers.Add("apikey", auth.PublishableKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session!.AccessToken);
            using var response = await clients.CreateClient(UiAuthentication.AuthClient).SendAsync(request, context.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                loggers.CreateLogger("Yandex.OAuth").LogWarning("Отказ привязки Supabase; HTTP: {Status}", (int)response.StatusCode);
                return Error("oauth");
            }
            await response.Content.LoadIntoBufferAsync(32768);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.RequestAborted));
            if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("url", out var value)
                || value.ValueKind != JsonValueKind.String || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url)
                || url.Scheme != "https" || url.Host != "oauth.yandex.ru" || !url.IsDefaultPort
                || url.AbsolutePath != "/authorize" || url.UserInfo != "" || url.Fragment != "") { return Error("oauth"); }
            return Results.Redirect(url.AbsoluteUri);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            || exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            loggers.CreateLogger("Yandex.OAuth").LogWarning("Ошибка привязки; тип: {ErrorType}", exception.GetType().Name);
            return Error("connection");
        }
    }

    private static async Task<IResult> CallbackAsync(HttpContext context, YandexOAuthSettings settings, UiAuthSettings auth,
        OAuthAttemptStore attempts, SupabasePasswordClient client, UiSessionStore sessions, IProfileStore profiles)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!context.Request.IsHttps || !Ready(settings, auth)) { return Error("oauth"); }
        var query = context.Request.Query;
        if (query["state"].Count != 1) { return Error("oauth"); }
        var attempt = attempts.Consume(query["state"].ToString(), context.Request.Cookies[CookieName] ?? "");
        if (attempt is null) { return Error("oauth"); }
        context.Response.Cookies.Delete(CookieName, CookieOptions());
        if (query.ContainsKey("error") || query["code"].Count != 1 || query["code"].ToString().Length is 0 or > 2048)
        { return Error("oauth"); }
        try
        {
            if (attempt.Value.LinkUserId is { } expected
                && (sessions.Find(context.User)?.UserId != expected
                    || context.User.FindFirst(UiSessionStore.SessionClaim)?.Value != attempt.Value.SessionId)) { return Error("oauth"); }
            var login = await client.ExchangeCodeAsync(query["code"].ToString(), attempt.Value.Verifier, context.RequestAborted);
            if (attempt.Value.LinkUserId is { } userId && login.Principal.FindFirst("sub")?.Value != userId.ToString())
            { return Error("oauth"); }
            return await UiAccountEndpoints.CompleteSignInAsync(context, login, sessions, profiles, attempt.Value.ReturnUrl);
        }
        catch (UiSignInException exception) { return Error(exception.Code == "credentials" ? "oauth" : exception.Code); }
    }

    private static CookieOptions CookieOptions() => new()
    {
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = TimeSpan.FromMinutes(5),
        IsEssential = true
    };

    private static IResult Error(string code) => Results.LocalRedirect("/login?error=" + code);

    private static async Task<IResult> UserinfoAsync(HttpContext context, YandexOAuthSettings settings,
        IHttpClientFactory clients, ILoggerFactory loggers)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!settings.Enabled) { return Results.NotFound(); }
        if (!context.Request.IsHttps || string.IsNullOrWhiteSpace(settings.ClientId)) { return Results.StatusCode(503); }
        if (context.Request.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header)
            || !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter) || header.Parameter.Length > 4096
            || header.Parameter.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '~' or '+' or '/' or '=')))
        { return Results.Unauthorized(); }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://login.yandex.ru/info?format=json");
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", header.Parameter);
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                loggers.CreateLogger("Yandex.Userinfo").LogWarning("Отказ Userinfo Яндекса; HTTP: {Status}", (int)response.StatusCode);
                return Results.StatusCode(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? 401 : 502);
            }
            // Ограничиваем ответ даже при отсутствии Content-Length и используем общий срок на чтение тела.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[32769];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token);
                if (read == 0) { break; }
                length += read;
            }
            if (length == bytes.Length) { return Results.StatusCode(502); }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)
                || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 and <= 128 } subject || !subject.All(char.IsAsciiDigit)
                || !root.TryGetProperty("client_id", out var clientId) || clientId.ValueKind != JsonValueKind.String
                || clientId.GetString() != settings.ClientId)
            { return Results.Unauthorized(); }
            // Не возвращаем email: привязка по неподтверждённой почте недопустима.
            return Results.Json(new { sub = subject });
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException
            || exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            loggers.CreateLogger("Yandex.Userinfo").LogWarning("Ошибка Userinfo Яндекса; тип: {ErrorType}", exception.GetType().Name);
            return Results.StatusCode(502);
        }
    }
}
