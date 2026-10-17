using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
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
using Microsoft.IdentityModel.Tokens;

namespace EquipmentManager.Tests;

/// <summary>Изолирует сеть и БД в тестах, сохраняя настоящий JwtBearer и криптографическую проверку JWT.</summary>
internal static class ApiAuthFixture
{
    internal const string Issuer = "https://cyzanurmyswswbicjiaf.supabase.co/auth/v1";
    internal const string Audience = "authenticated";
    internal static readonly Guid UserId = Guid.NewGuid();
    internal static readonly ECDsaSecurityKey SigningKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "test-key" };

    /// <summary>Создаёт независимое приложение; ключи и профиль заменены только тестовым DI.</summary>
    internal static WebApplication CreateApp(string environmentName = "Development", string[]? roles = null,
        bool active = true, bool available = true, bool enabled = true, bool missing = false, bool realStore = false,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SupabaseAuth:Enabled"] = enabled.ToString(),
            ["SupabaseAuth:SettingsConfirmed"] = "true",
            ["SupabaseAuth:Issuer"] = Issuer,
            ["SupabaseAuth:Audience"] = Audience,
            ["SupabaseAuth:SigningAlgorithm"] = "ES256",
            // Не секрет и не действующее подключение: тестовый IProfileStore заменяется ниже.
            ["ConnectionStrings:Profiles"] = enabled ? "Host=localhost;Username=equipment_profile_reader;SSL Mode=VerifyFull" : null
        });
        var credentials = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Profiles"] = enabled && !realStore ? "Host=localhost;Username=equipment_profile_reader;SSL Mode=VerifyFull" : null
        }).Build();
        builder.Services.AddEquipmentAuthentication(builder.Configuration, credentials);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                Issuer + "/.well-known/jwks.json", new SupabaseJwksRetriever("ES256"),
                new TestJwksDocument());
        });
        if (!realStore)
        {
            builder.Services.RemoveAll<IProfileStore>();
            builder.Services.AddSingleton<IProfileStore>(new TestProfiles(roles ?? ["engineer"], active, available, missing));
        }
        builder.Services.AddEquipmentStorage(new ConfigurationBuilder().Build(), credentials);
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEquipmentApi();
        return app;
    }

    /// <summary>Создаёт клиент со свежим подписанным токеном тестового пользователя.</summary>
    internal static HttpClient CreateClient(WebApplication app, bool authenticated = true)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        if (authenticated) { client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken()); }
        return client;
    }

    /// <summary>Выпускает токен только для локальных тестов; реальный API не содержит такого издателя.</summary>
    internal static string CreateToken(string? issuer = null, string? audience = null, bool expired = false,
        SecurityKey? key = null, string algorithm = SecurityAlgorithms.EcdsaSha256,
        bool omitExpiration = false, bool future = false)
    {
        var claims = new[] { new Claim("sub", UserId.ToString()), new Claim("role", "authenticated"),
            new Claim("roles", "engineer"), new Claim("user_metadata", "{\"roles\":[\"engineer\"]}") };
        var token = new JwtSecurityToken(issuer ?? Issuer, audience ?? Audience, claims,
            DateTime.UtcNow.AddMinutes(future ? 2 : -10),
            omitExpiration ? null : expired ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key ?? SigningKey, algorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Возвращает только публичные координаты тестового P-256 вместо внешней сети.</summary>
    internal sealed class TestJwksDocument : IDocumentRetriever
    {
        /// <inheritdoc />
        public Task<string> GetDocumentAsync(string address, CancellationToken cancellationToken)
        {
            var parameters = SigningKey.ECDsa.ExportParameters(false);
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                keys = new[] { new
            {
                kty = "EC", crv = "P-256", alg = "ES256", use = "sig", kid = SigningKey.KeyId,
                x = Base64UrlEncoder.Encode(parameters.Q.X!), y = Base64UrlEncoder.Encode(parameters.Q.Y!)
            } }
            }));
        }
    }

    /// <summary>Подменяет только доверенный источник профиля, но не проверку токена или политики.</summary>
    private sealed class TestProfiles(string[] roles, bool active, bool available, bool missing) : IProfileStore
    {
        /// <inheritdoc />
        public Task<ProfileLookup> GetAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(
            new ProfileLookup(available, missing || userId != UserId ? null : new ServerProfile(active, roles)));
    }
}
