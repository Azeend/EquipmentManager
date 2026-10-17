using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EquipmentManager.Services.Authentication;
using EquipmentManager.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Изолированные проверки адаптера, PKCE, корреляции и общей авторизации; облачные сервисы не вызываются.</summary>
public sealed class YandexOAuthTests
{
    private static UiAuthFixture Fixture(bool active = true, bool available = true, bool enabled = true, YandexHandler? handler = null, bool missing = false) =>
        new(profileAvailable: available, profileActive: active, configureServices: services =>
        {
            services.AddSingleton(new YandexOAuthSettings { Enabled = enabled, ClientId = "test-client" });
            services.AddHttpClient("Yandex.Userinfo").ConfigurePrimaryHttpMessageHandler(() => handler ?? new YandexHandler());
            if (missing) { services.AddSingleton<IProfileStore>(new MissingProfile()); }
        });

    private static async Task<(string Callback, string Cookie, string Challenge)> StartAsync(UiAuthFixture fixture)
    {
        var form = await fixture.FormAsync();
        Assert.Contains("Войти через Яндекс", WebUtility.HtmlDecode(form.Html));
        using var start = await fixture.PostAsync("/account/oauth/yandex", form.Cookie, form.Token, "https://evil.invalid/");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        Assert.Equal("custom:yandex", query["provider"]);
        Assert.Equal("s256", query["code_challenge_method"]);
        var callback = new Uri(query["redirect_to"].ToString());
        Assert.Equal("backend.invalid", callback.Host); // Только конфигурация, не Host входящего запроса.
        var cookie = start.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        return (callback.PathAndQuery, cookie.Split(';')[0], query["code_challenge"].ToString());
    }

    private static async Task<HttpResponseMessage> CallbackAsync(UiAuthFixture fixture, string url, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return await fixture.Client.SendAsync(request);
    }

    /// <summary>Код обменивается с правильным verifier; cookie не заменяет Bearer, повтор отклоняется.</summary>
    [Fact]
    public async Task OAuth_UsesPkce_CreatesExistingSession_AndRejectsReplay()
    {
        await using var fixture = Fixture();
        await fixture.StartAsync();
        var flow = await StartAsync(fixture);
        using var response = await CallbackAsync(fixture, flow.Callback + "&code=test-code", flow.Cookie);
        Assert.Equal("/account/session", response.Headers.Location!.OriginalString);
        Assert.Contains("__Host-EquipmentManager.Session=", UiAuthFixture.Cookie(response));
        Assert.DoesNotContain(fixture.Auth.AccessToken, UiAuthFixture.Cookie(response));
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("?grant_type=pkce", fixture.Auth.LastGrant);
        using var payload = JsonDocument.Parse(fixture.Auth.LastPayload!);
        Assert.Equal("test-code", payload.RootElement.GetProperty("auth_code").GetString());
        var verifier = payload.RootElement.GetProperty("code_verifier").GetString()!;
        Assert.Equal(flow.Challenge, WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/equipment");
        apiRequest.Headers.Add("Cookie", UiAuthFixture.Cookie(response));
        using var api = await fixture.Client.SendAsync(apiRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        using var replay = await CallbackAsync(fixture, flow.Callback + "&code=test-code", flow.Cookie);
        Assert.Contains("error=oauth", replay.Headers.Location!.OriginalString);
        Assert.Equal(1, fixture.Auth.LoginCalls);
    }

    /// <summary>Чужой браузер, неверный state, истечение и отказ провайдера не создают сеанс.</summary>
    [Theory]
    [InlineData("browser")]
    [InlineData("state")]
    [InlineData("expired")]
    [InlineData("denied")]
    [InlineData("duplicate")]
    public async Task Callback_RejectsInvalidCorrelation(string scenario)
    {
        await using var fixture = Fixture();
        await fixture.StartAsync();
        var flow = await StartAsync(fixture);
        if (scenario == "expired") { fixture.Clock.Now = fixture.Clock.Now.AddMinutes(6); }
        var url = flow.Callback + (scenario == "denied" ? "&error=access_denied" : "&code=test");
        if (scenario == "state") { url = "/account/oauth/callback?state=" + new string('x', 43) + "&code=test"; }
        if (scenario == "duplicate") { url += "&state=extra"; }
        using var response = await CallbackAsync(fixture, url, scenario == "browser" ? "unrelated=value" : flow.Cookie);
        Assert.Contains("error=oauth", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("__Host-EquipmentManager.Session=", UiAuthFixture.Cookie(response));
        Assert.Equal(0, fixture.Auth.LoginCalls);
    }

    /// <summary>OAuth использует прежние криптографические проверки JWT и активного профиля.</summary>
    [Theory]
    [InlineData("audience", "oauth")]
    [InlineData("issuer", "oauth")]
    [InlineData("expired", "oauth")]
    [InlineData("malformed", "oauth")]
    [InlineData("missing_profile", "forbidden")]
    [InlineData("inactive", "forbidden")]
    [InlineData("unavailable", "profiles")]
    [InlineData("transport", "connection")]
    public async Task OAuthFailure_DoesNotIssueCookie(string scenario, string error)
    {
        await using var fixture = Fixture(active: scenario != "inactive", available: scenario != "unavailable", missing: scenario == "missing_profile");
        fixture.Auth.AccessToken = scenario switch
        {
            "audience" => ApiAuthFixture.CreateToken(audience: "foreign"),
            "issuer" => ApiAuthFixture.CreateToken(issuer: "https://foreign.invalid"),
            "expired" => ApiAuthFixture.CreateToken(expired: true),
            "malformed" => "not-a-jwt",
            _ => fixture.Auth.AccessToken
        };
        fixture.Auth.NetworkFailure = scenario == "transport";
        await fixture.StartAsync();
        var flow = await StartAsync(fixture);
        using var response = await CallbackAsync(fixture, flow.Callback + "&code=test", flow.Cookie);
        Assert.Contains("error=" + error, response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("__Host-EquipmentManager.Session=", UiAuthFixture.Cookie(response));
    }

    /// <summary>Без antiforgery провайдер не вызывается; выключенный режим не публикует адаптер.</summary>
    [Fact]
    public async Task OAuth_RequiresAntiforgery_AndExplicitEnablement()
    {
        await using var fixture = Fixture(enabled: false);
        await fixture.StartAsync();
        using var start = await fixture.PostAsync("/account/oauth/yandex", "", "");
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        using var userinfo = await fixture.Client.GetAsync("/oauth/yandex/userinfo");
        Assert.Equal(HttpStatusCode.NotFound, userinfo.StatusCode);
        var form = await fixture.FormAsync();
        Assert.DoesNotContain("/account/oauth/yandex", form.Html);
    }

    /// <summary>Адаптер допускает только проверенный ответ своего клиента и не возвращает email или токен.</summary>
    [Theory]
    [InlineData("valid", 200)]
    [InlineData("foreign", 401)]
    [InlineData("missing_id", 401)]
    [InlineData("invalid_json", 502)]
    [InlineData("oversize", 502)]
    [InlineData("network", 502)]
    [InlineData("unauthorized", 401)]
    [InlineData("server_error", 502)]
    public async Task Userinfo_ValidatesYandexResponse(string scenario, int expected)
    {
        var handler = new YandexHandler { Scenario = scenario };
        await using var fixture = Fixture(handler: handler);
        await fixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/yandex/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-test-token");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-test-token", body);
        Assert.DoesNotContain("default_email", body);
        if (expected == 200) { Assert.Equal("{\"sub\":\"123456\"}", body); }
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    /// <summary>Токен из query string не принимается и не отправляется Яндексу.</summary>
    [Fact]
    public async Task Userinfo_DoesNotAcceptQueryToken()
    {
        var handler = new YandexHandler();
        await using var fixture = Fixture(handler: handler);
        await fixture.StartAsync();
        using var response = await fixture.Client.GetAsync("/oauth/yandex/userinfo?access_token=synthetic");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>Привязка требует исходный cookie-сеанс, сохраняет UUID и использует пользовательский токен.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("remote_failure")]
    [InlineData("session_lost")]
    [InlineData("wrong_uuid")]
    public async Task Link_RequiresOriginalSessionAndUuid(string scenario)
    {
        await using var fixture = Fixture();
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
        var sessionCookie = UiAuthFixture.Cookie(login);
        var linkForm = await fixture.FormAsync("/account/session", sessionCookie);
        Assert.NotEmpty(linkForm.Token);
        fixture.Auth.LinkFailure = scenario == "remote_failure";
        using var link = await fixture.PostAsync("/account/oauth/yandex/link", form.Cookie + "; " + linkForm.Cookie + "; " + sessionCookie, linkForm.Token);
        Assert.Equal(HttpStatusCode.Redirect, link.StatusCode);
        if (scenario == "remote_failure")
        {
            Assert.Contains("error=oauth", link.Headers.Location!.OriginalString);
            return;
        }
        Assert.Equal("oauth.yandex.ru", link.Headers.Location!.Host);
        var parameters = QueryHelpers.ParseQuery(fixture.Auth.LinkRequest!.Query);
        Assert.Equal("true", parameters["skip_http_redirect"]);
        Assert.Equal("custom:yandex", parameters["provider"]);
        var callback = new Uri(parameters["redirect_to"].ToString()).PathAndQuery;
        var callsBeforeCallback = fixture.Auth.LoginCalls;
        if (scenario == "wrong_uuid")
        {
            var token = new JwtSecurityToken(ApiAuthFixture.Issuer, ApiAuthFixture.Audience,
                [new Claim("sub", Guid.NewGuid().ToString()), new Claim("role", "authenticated")],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(ApiAuthFixture.SigningKey, SecurityAlgorithms.EcdsaSha256));
            fixture.Auth.AccessToken = new JwtSecurityTokenHandler().WriteToken(token);
        }
        using var response = await CallbackAsync(fixture, callback + "&code=test",
            UiAuthFixture.Cookie(link) + (scenario == "session_lost" ? "" : "; " + sessionCookie));
        if (scenario == "success")
        {
            Assert.Equal("/account/session", response.Headers.Location!.OriginalString);
            Assert.Contains("__Host-EquipmentManager.Session=", UiAuthFixture.Cookie(response));
        }
        else
        {
            Assert.Contains("error=oauth", response.Headers.Location!.OriginalString);
            Assert.DoesNotContain("__Host-EquipmentManager.Session=", UiAuthFixture.Cookie(response));
            if (scenario == "session_lost") { Assert.Equal(callsBeforeCallback, fixture.Auth.LoginCalls); }
        }
    }

    /// <summary>Обработка привязки защищена cookie-аутентификацией и antiforgery.</summary>
    [Fact]
    public async Task Link_RejectsAnonymousAndMissingAntiforgery()
    {
        await using var fixture = Fixture();
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        using var anonymous = await fixture.PostAsync("/account/oauth/yandex/link", form.Cookie, form.Token);
        Assert.Contains("/login", anonymous.Headers.Location!.OriginalString);
        using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
        using var noCsrf = await fixture.PostAsync("/account/oauth/yandex/link", UiAuthFixture.Cookie(login), "");
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Null(fixture.Auth.LinkRequest);
    }

    /// <summary>Параллельное потребление одной попытки имеет ровно одного победителя.</summary>
    [Fact]
    public async Task State_IsConsumedAtomically()
    {
        using var store = new OAuthAttemptStore(TimeProvider.System);
        var flow = store.Create("/account/session");
        Assert.Null(store.Consume(flow.State, new string('x', 43)));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.Consume(flow.State, flow.Browser))));
        Assert.Single(results, result => result is not null);
    }

    /// <summary>Диагностика не включает токен, тело ответа и исходное исключение транспорта.</summary>
    [Fact]
    public async Task Userinfo_LogsOnlySafeErrorType()
    {
        var logs = new SafeLogs();
        await using var fixture = new UiAuthFixture(configureServices: services =>
        {
            services.AddSingleton(new YandexOAuthSettings { Enabled = true, ClientId = "test-client" });
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddHttpClient("Yandex.Userinfo").ConfigurePrimaryHttpMessageHandler(() => new YandexHandler { Scenario = "network" });
        });
        await fixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/yandex/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-test-token");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(logs.Messages, message => message.Contains("HttpRequestException", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("synthetic-test-token", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("login.yandex.ru", StringComparison.Ordinal));
        Assert.Empty(logs.Exceptions);
    }

    private sealed class YandexHandler : HttpMessageHandler
    {
        internal string Scenario { get; init; } = "valid";
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https://login.yandex.ru/info?format=json", request.RequestUri!.AbsoluteUri);
            Assert.Equal("OAuth", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-test-token", request.Headers.Authorization.Parameter);
            if (Scenario == "network") { throw new HttpRequestException("synthetic-test-token must not enter logs"); }
            var status = Scenario switch { "unauthorized" => HttpStatusCode.Unauthorized, "server_error" => HttpStatusCode.ServiceUnavailable, _ => HttpStatusCode.OK };
            var body = Scenario switch
            {
                "foreign" => "{\"id\":\"123456\",\"client_id\":\"foreign\"}",
                "missing_id" => "{\"client_id\":\"test-client\"}",
                "invalid_json" => "broken-json",
                "oversize" => new string('x', 40000),
                _ => "{\"id\":\"123456\",\"client_id\":\"test-client\",\"default_email\":\"unused@example.invalid\"}"
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class MissingProfile : IProfileStore
    {
        public Task<ProfileLookup> GetAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(new ProfileLookup(true, null));
    }

    private sealed class SafeLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        internal ConcurrentQueue<Exception> Exceptions { get; } = new();
        public ILogger CreateLogger(string categoryName) => new SafeLogger(this);
        public void Dispose() { }
    }

    private sealed class SafeLogger(SafeLogs logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            logs.Messages.Enqueue(formatter(state, exception));
            if (exception is not null) { logs.Exceptions.Enqueue(exception); }
        }
    }
}
