using EquipmentManager.Services.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет загрузчик публичного JWKS и параметры подтверждённого проекта без внешней сети.</summary>
public sealed class SupabaseJwksTests
{
    /// <summary>Приняты только публичные ключи подписи ES256 на P-256.</summary>
    [Fact]
    public async Task ConfirmedSettings_UseIssuerAudienceLifetimeAndP256Jwks()
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp();
        var options = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");
        // Act
        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);
        // Assert
        Assert.Equal("https://cyzanurmyswswbicjiaf.supabase.co/auth/v1", options.TokenValidationParameters.ValidIssuer);
        Assert.Equal("authenticated", options.TokenValidationParameters.ValidAudience);
        Assert.Equal("ES256", Assert.Single(options.TokenValidationParameters.ValidAlgorithms));
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
        Assert.True(options.TokenValidationParameters.RequireExpirationTime);
        Assert.Single(configuration.SigningKeys);
    }

    /// <summary>Ключи неправильной кривой, назначения или алгоритма отвергаются загрузчиком.</summary>
    [Theory]
    [InlineData("P-384", "sig", "ES256")]
    [InlineData("P-256", "enc", "ES256")]
    [InlineData("P-256", "sig", "ES384")]
    public async Task IncompatibleJwks_IsRejected(string curve, string use, string algorithm)
    {
        // Arrange
        var document = await new ApiAuthFixture.TestJwksDocument().GetDocumentAsync("", CancellationToken.None);
        document = document.Replace("P-256", curve).Replace("\"sig\"", $"\"{use}\"").Replace("ES256", algorithm);
        var retriever = new SupabaseJwksRetriever("ES256");
        // Act
        var exception = await Record.ExceptionAsync(() => retriever.GetConfigurationAsync(
            ApiAuthFixture.Issuer + "/.well-known/jwks.json", new StaticDocument(document), CancellationToken.None));
        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Возвращает заданный публичный документ без сетевого запроса.</summary>
    private sealed class StaticDocument(string json) : IDocumentRetriever
    {
        /// <inheritdoc />
        public Task<string> GetDocumentAsync(string address, CancellationToken cancellationToken) => Task.FromResult(json);
    }
}
