using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using EquipmentManager.Components;
using EquipmentManager.Services;
using EquipmentManager.Services.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EquipmentManager.Tests;

/// <summary>Запускает реальный локальный HTTP-конвейер с изолированными Supabase/JWKS/профилями.</summary>
internal sealed class UiAuthFixture : IAsyncDisposable
{
    internal WebApplication App { get; }
    internal FakeAuthHandler Auth { get; } = new();
    internal TestClock Clock { get; } = new();
    internal HttpClient Client { get; private set; } = null!;

    internal UiAuthFixture(bool profileAvailable = true, bool profileActive = true, string[]? roles = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SupabaseAuth:Enabled"] = "true",
            ["SupabaseAuth:SettingsConfirmed"] = "true",
            ["SupabaseAuth:Issuer"] = ApiAuthFixture.Issuer,
            ["SupabaseAuth:Audience"] = ApiAuthFixture.Audience,
            ["SupabaseAuth:SigningAlgorithm"] = "ES256",
            ["SupabaseUiAuth:BackendBaseUrl"] = "https://backend.invalid/"
        });
        var credentials = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SupabaseUiAuth:PublishableKey"] = "sb_publishable_test_fixture"
        }).Build();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddEquipmentAuthentication(builder.Configuration, credentials);
        builder.Services.AddEquipmentUiAuthentication(builder.Configuration, credentials);
        builder.Services.AddSingleton<TimeProvider>(Clock);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                ApiAuthFixture.Issuer + "/.well-known/jwks.json", new SupabaseJwksRetriever("ES256"), new ApiAuthFixture.TestJwksDocument()));
        builder.Services.RemoveAll<IProfileStore>();
        builder.Services.AddSingleton<IProfileStore>(new Profiles(profileAvailable, profileActive, roles ?? ["engineer"]));
        builder.Services.AddHttpClient("Supabase.Password").ConfigurePrimaryHttpMessageHandler(() => Auth);
        builder.Services.AddHttpClient("Equipment.Backend").ConfigurePrimaryHttpMessageHandler(() => new LocalBackendHandler(this));
        builder.Services.AddEquipmentStorage(new ConfigurationBuilder().Build(), credentials);
        configureServices?.Invoke(builder.Services);
        App = builder.Build();
        // Только в тестовом хосте: имитируем HTTPS termination, чтобы тестировать Secure cookie без сертификата.
        App.Use((context, next) => { context.Request.Scheme = "https"; return next(context); });
        App.UseEquipmentSecurityHeaders();
        App.UseAuthentication();
        App.UseAuthorization();
        App.UseAntiforgery();
        App.MapEquipmentUiAccount();
        App.MapEquipmentApi();
        App.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization(UiAuthentication.Policy);
    }

    internal async Task StartAsync()
    {
        await App.StartAsync();
        Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = new Uri(App.Urls.Single()) };
    }

    internal async Task<(string Cookie, string Token, string Html)> FormAsync(string path = "/login", string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) { request.Headers.Add("Cookie", cookie); }
        using var response = await Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        return (Cookie(response), WebUtility.HtmlDecode(token), html);
    }

    internal static string Cookie(HttpResponseMessage response) => response.Headers.TryGetValues("Set-Cookie", out var cookies)
        ? string.Join("; ", cookies.Select(cookie => cookie.Split(';')[0])) : string.Empty;

    internal async Task<HttpResponseMessage> PostAsync(string path, string cookie, string token,
        string returnUrl = "/account/session")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (!string.IsNullOrEmpty(cookie)) { request.Headers.Add("Cookie", cookie); }
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "fixture@example.invalid",
            ["password"] = Auth.Password,
            ["returnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = token
        });
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await App.DisposeAsync();
    }

    /// <summary>Часы позволяют проверить истечение без ожидания срока реального JWT.</summary>
    internal sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Подмена транспорта Auth: ни один тест не обращается к реальному проекту.</summary>
    internal sealed class FakeAuthHandler : HttpMessageHandler
    {
        internal string Password { get; } = Guid.NewGuid().ToString("N");
        internal string AccessToken { get; set; } = ApiAuthFixture.CreateToken();
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal bool NetworkFailure { get; set; }
        internal Exception? LoginException { get; set; }
        internal string? RawResponse { get; set; }
        internal bool LogoutFailure { get; set; }
        internal int LoginCalls { get; private set; }
        internal int LogoutCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/logout"))
            {
                LogoutCalls++;
                return Task.FromResult(new HttpResponseMessage(LogoutFailure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent));
            }
            LoginCalls++;
            if (LoginException is not null) { throw LoginException; }
            if (NetworkFailure) { throw new HttpRequestException("Изолированный отказ тестового транспорта."); }
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = RawResponse is null
                    ? JsonContent.Create(new { access_token = AccessToken, refresh_token = "discarded-test-value" })
                    : new StringContent(RawResponse)
            });
        }
    }

    /// <summary>Перенаправляет только тестовый Backend-запрос в локальный API с настоящим JwtBearer.</summary>
    private sealed class LocalBackendHandler(UiAuthFixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "backend.invalid" || !request.RequestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Тест допускает только фиксированный адрес Backend.");
            }
            using var forwarded = new HttpRequestMessage(request.Method, new Uri(new Uri(fixture.App.Urls.Single()), request.RequestUri.PathAndQuery));
            forwarded.Headers.Authorization = request.Headers.Authorization;
            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                forwarded.Content.Headers.ContentType = request.Content.Headers.ContentType;
            }
            return await fixture.Client.SendAsync(forwarded, cancellationToken);
        }
    }

    private sealed class Profiles(bool available, bool active, string[] roles) : IProfileStore
    {
        public Task<ProfileLookup> GetAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(
            new ProfileLookup(available, userId == ApiAuthFixture.UserId ? new ServerProfile(active, roles) : null));
    }
}
