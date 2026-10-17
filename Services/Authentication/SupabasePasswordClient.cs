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
    IOptionsMonitor<JwtBearerOptions> bearerOptions, TimeProvider clock)
{
    /// <summary>Проверяет пароль через Auth и JWT через существующие настройки доверия Backend.</summary>
    public async Task<ValidatedUiLogin> SignInAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (!settings.CanSignIn) { throw new UiSignInException("configuration"); }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Issuer + "/token?grant_type=password");
            request.Headers.Add("apikey", settings.PublishableKey);
            request.Content = JsonContent.Create(new { email, password });
            using var response = await clients.CreateClient(UiAuthentication.AuthClient).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new UiSignInException(response.StatusCode switch
                {
                    HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "credentials",
                    HttpStatusCode.TooManyRequests => "rate_limit",
                    _ => "connection"
                });
            }
            // Refresh token даже не десериализуется в модель и не сохраняется.
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!body.RootElement.TryGetProperty("access_token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(tokenElement.GetString())) { throw new UiSignInException("connection"); }
            var accessToken = tokenElement.GetString()!;
            var options = bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme);
            if (options.ConfigurationManager is null) { throw new UiSignInException("configuration"); }
            var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
            var parameters = options.TokenValidationParameters.Clone();
            parameters.IssuerSigningKeys = configuration.SigningKeys;
            var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(accessToken, parameters);
            if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                // При ротации ключей запрашиваем обновление доверенного JWKS; неизвестному ключу не доверяем.
                options.ConfigurationManager.RequestRefresh();
                configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
                parameters.IssuerSigningKeys = configuration.SigningKeys;
                result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(accessToken, parameters);
            }
            if (!result.IsValid || result.ClaimsIdentity is null) { throw new UiSignInException("credentials"); }
            var principal = new ClaimsPrincipal(result.ClaimsIdentity);
            var expiresAt = new DateTimeOffset(DateTime.SpecifyKind(result.SecurityToken.ValidTo, DateTimeKind.Utc));
            if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out _)
                || principal.FindFirst("role")?.Value != "authenticated"
                || principal.FindFirst("is_anonymous")?.Value == "true"
                || expiresAt <= clock.GetUtcNow()) { throw new UiSignInException("credentials"); }
            return new ValidatedUiLogin(principal, accessToken, expiresAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Не включаем исходное исключение/ответ сервиса в отображаемую ошибку или логи.
            throw new UiSignInException("connection");
        }
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
