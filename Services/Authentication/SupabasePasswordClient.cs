using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EquipmentManager.Services.Authentication;

/// <summary>Настройки серверного входа; ключ читается только из User Secrets или окружения.</summary>
public sealed class UiAuthSettings
{
    /// <summary>Адрес Auth того же проекта, который проверяет JwtBearer.</summary>
    public string Issuer { get; init; } = string.Empty;
    /// <summary>Разрешает вход только при включённой существующей JWT-конфигурации.</summary>
    public bool Enabled { get; init; }
    /// <summary>Доверенный HTTPS-адрес собственного Backend API, без вычисления из Host запроса.</summary>
    public Uri? BackendBaseUrl { get; init; }
    internal string PublishableKey { get; init; } = string.Empty;
    internal bool CanSignIn => Enabled && !string.IsNullOrWhiteSpace(PublishableKey);
}

/// <summary>Безопасная ошибка входа с фиксированным кодом без ответа Auth или учётных данных.</summary>
public sealed class UiSignInException(string code) : Exception("Вход не выполнен.")
{
    /// <summary>Код для выбора русского сообщения интерфейса.</summary>
    public string Code { get; } = code;
}

/// <summary>Результат криптографически проверенного входа; токен остаётся только на сервере.</summary>
public sealed class ValidatedUiLogin(ClaimsPrincipal principal, string accessToken, DateTimeOffset expiresAt)
{
    /// <summary>Пользователь из проверенного JWT.</summary>
    public ClaimsPrincipal Principal { get; } = principal;
    /// <summary>Срок access token.</summary>
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    internal string AccessToken { get; } = accessToken;
}

/// <summary>Обращается к Supabase Auth по HTTPS; не сохраняет пароль и refresh token.</summary>
public sealed class SupabasePasswordClient(IHttpClientFactory clients, UiAuthSettings settings,
    IOptionsMonitor<JwtBearerOptions> bearerOptions, TimeProvider clock, ILogger<SupabasePasswordClient> logger)
{
    /// <summary>Проверяет пароль через Auth и JWT через существующие настройки доверия Backend.</summary>
    public Task<ValidatedUiLogin> SignInAsync(string email, string password, CancellationToken cancellationToken) =>
        ExchangeAsync("password", new { email, password }, cancellationToken);

    /// <summary>Обменивает одноразовый код PKCE и проверяет JWT той же цепочкой доверия, что и пароль.</summary>
    public Task<ValidatedUiLogin> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken) =>
        ExchangeAsync("pkce", new { auth_code = code, code_verifier = verifier }, cancellationToken);

    private async Task<ValidatedUiLogin> ExchangeAsync(string grant, object payload, CancellationToken cancellationToken)
    {
        if (!settings.CanSignIn) { throw Reject("configuration", "configuration", "configuration", null); }
        var stage = "auth_request";
        int? authStatus = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Issuer + "/token?grant_type=" + grant);
            request.Headers.Add("apikey", settings.PublishableKey);
            request.Content = JsonContent.Create(payload);
            using var response = await clients.CreateClient(UiAuthentication.AuthClient).SendAsync(request, cancellationToken);
            authStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode switch
                {
                    HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "credentials",
                    HttpStatusCode.TooManyRequests => "rate_limit",
                    _ => "connection"
                };
                throw Reject(code, stage, code switch
                {
                    "credentials" => "auth_rejected",
                    "rate_limit" => "rate_limit",
                    _ => "auth_http_error"
                }, authStatus);
            }
            stage = "auth_response";
            // Refresh token даже не десериализуется в модель и не сохраняется.
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!body.RootElement.TryGetProperty("access_token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(tokenElement.GetString())) { throw Reject("connection", stage, "missing_access_token", authStatus); }
            var accessToken = tokenElement.GetString()!;
            var options = bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme);
            if (options.ConfigurationManager is null) { throw Reject("configuration", stage, "jwt_configuration", authStatus); }
            stage = "jwks";
            var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
            var parameters = options.TokenValidationParameters.Clone();
            parameters.IssuerSigningKeys = configuration.SigningKeys;
            stage = "jwt_validation";
            var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(accessToken, parameters);
            if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                // При ротации ключей запрашиваем обновление доверенного JWKS; неизвестному ключу не доверяем.
                options.ConfigurationManager.RequestRefresh();
                stage = "jwks_refresh";
                configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
                parameters.IssuerSigningKeys = configuration.SigningKeys;
                stage = "jwt_validation";
                result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(accessToken, parameters);
            }
            if (!result.IsValid || result.ClaimsIdentity is null) { throw Reject("credentials", stage, "jwt_validation", authStatus, result.Exception); }
            stage = "jwt_claims";
            var principal = new ClaimsPrincipal(result.ClaimsIdentity);
            var expiresAt = new DateTimeOffset(DateTime.SpecifyKind(result.SecurityToken.ValidTo, DateTimeKind.Utc));
            if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out _)
                || principal.FindFirst("role")?.Value != "authenticated"
                || principal.FindFirst("is_anonymous")?.Value == "true"
                || expiresAt <= clock.GetUtcNow()) { throw Reject("credentials", stage, "jwt_claims", authStatus); }
            return new ValidatedUiLogin(principal, accessToken, expiresAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Не включаем исходное исключение/ответ сервиса в отображаемую ошибку или логи.
            var category = exception switch
            {
                HttpRequestException => "http_transport",
                OperationCanceledException => "timeout",
                JsonException => "invalid_json",
                _ => "configuration_or_protocol"
            };
            throw Reject("connection", stage, category, authStatus, exception);
        }
    }

    /// <summary>Журналирует только фиксированные категории, HTTP-статус и тип исключения; ответ Auth и JWT не передаются.</summary>
    private UiSignInException Reject(string code, string stage, string category, int? authStatus, Exception? exception = null)
    {
        logger.LogWarning(new EventId(2001, "SupabaseSignInRejected"),
            "Событие: {Event}; отказ входа Supabase Auth. Этап: {Stage}; HTTP-статус Auth: {AuthStatus}; категория: {Category}; тип исключения: {ErrorType}",
            "SupabaseSignInRejected", stage, authStatus, category, exception?.GetType().Name ?? "none");
        return new UiSignInException(code);
    }

    /// <summary>Завершает только текущий Supabase-сеанс; отказ сети не восстанавливает локальный сеанс.</summary>
    public async Task<bool> SignOutAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Issuer + "/logout?scope=local");
            request.Headers.Add("apikey", settings.PublishableKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await clients.CreateClient(UiAuthentication.AuthClient).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false; // Вызывающий код сообщает, что удалённый выход не подтверждён.
        }
    }
}
