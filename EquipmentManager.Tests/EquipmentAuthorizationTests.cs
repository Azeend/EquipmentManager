using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EquipmentManager.Models;
using EquipmentManager.Services.Authentication;
using EquipmentManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет запрет анонимного доступа, серверные роли и криптографическую валидацию.</summary>
public sealed class EquipmentAuthorizationTests
{
    /// <summary>Все пять маршрутов требуют аутентификации.</summary>
    [Theory]
    [InlineData("GET", "/api/equipment")]
    [InlineData("GET", "/api/equipment/1")]
    [InlineData("POST", "/api/equipment")]
    [InlineData("PUT", "/api/equipment/1")]
    [InlineData("DELETE", "/api/equipment/1")]
    public async Task Anonymous_Returns401(string method, string path)
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp();
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app, false);
        // Act
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        { Content = JsonContent.Create(new EquipmentRequest()) });
        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    /// <summary>Руководитель и специалист не могут менять оборудование даже с engineer в JWT.</summary>
    [Theory]
    [InlineData("manager", "POST")]
    [InlineData("manager", "PUT")]
    [InlineData("manager", "DELETE")]
    [InlineData("maintenance_specialist", "POST")]
    [InlineData("maintenance_specialist", "PUT")]
    [InlineData("maintenance_specialist", "DELETE")]
    public async Task NonEngineer_Write_Returns403(string role, string method)
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp(roles: [role]);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        var path = method == "POST" ? "/api/equipment" : "/api/equipment/1";
        // Act
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        { Content = JsonContent.Create(new EquipmentRequest()) });
        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Все три серверные роли разрешают чтение.</summary>
    [Theory]
    [InlineData("engineer")]
    [InlineData("maintenance_specialist")]
    [InlineData("manager")]
    public async Task ActiveRole_Read_Returns200(string role)
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp(roles: [role]);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        // Act
        var response = await client.GetAsync("/api/equipment");
        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Неверные issuer, audience, срок, подпись или алгоритм не принимаются.</summary>
    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("algorithm")]
    [InlineData("missing_exp")]
    [InlineData("future")]
    public async Task InvalidJwt_Returns401(string scenario)
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp();
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app, false);
        using var foreignEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = scenario switch
        {
            "issuer" => ApiAuthFixture.CreateToken(issuer: "https://other.invalid/auth/v1"),
            "audience" => ApiAuthFixture.CreateToken(audience: "other-audience"),
            "expired" => ApiAuthFixture.CreateToken(expired: true),
            "signature" => ApiAuthFixture.CreateToken(key: new ECDsaSecurityKey(foreignEc) { KeyId = "test-key" }),
            "missing_exp" => ApiAuthFixture.CreateToken(omitExpiration: true),
            "future" => ApiAuthFixture.CreateToken(future: true),
            _ => ApiAuthFixture.CreateToken(key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)), algorithm: SecurityAlgorithms.HmacSha256)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Act
        var response = await client.GetAsync("/api/equipment");
        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Неактивный, отсутствующий профиль и пустые роли не допускают чтение.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task ProfileNotAuthorized_Returns403(bool active, bool missing, bool emptyRoles)
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp(active: active, missing: missing, roles: emptyRoles ? [] : ["engineer"]);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        // Act
        var response = await client.GetAsync("/api/equipment");
        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Отключённая конфигурация не принимает даже криптографически корректный тестовый токен.</summary>
    [Fact]
    public async Task UnconfirmedConfiguration_Returns401()
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp(enabled: false, realStore: true);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        // Act
        var response = await client.GetAsync("/api/equipment");
        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Недоступность доверенного источника не приводит к разрешению или ложному 403.</summary>
    [Fact]
    public async Task ProfileStoreUnavailable_Returns503()
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp(available: false);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        // Act
        var response = await client.GetAsync("/api/equipment");
        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>JWT работает без строки БД, но реальный provider не даёт полномочий и не берёт строку из общей конфигурации.</summary>
    [Fact]
    public async Task NoProtectedConnectionString_ValidJwt_Returns503()
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp(realStore: true);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        // Act
        var response = await client.GetAsync("/api/equipment");
        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Связанные заявки запрещают инженеру удаление с кодом 409.</summary>
    [Fact]
    public async Task LinkedEquipment_Delete_Returns409()
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp();
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        // Act
        var response = await client.DeleteAsync("/api/equipment/1");
        var stillExists = await client.GetAsync("/api/equipment/1");
        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillExists.StatusCode);
    }

    /// <summary>Завершённая заявка с историей также блокирует удаление.</summary>
    [Fact]
    public async Task EquipmentWithHistory_Delete_Returns409AndPreservesHistory()
    {
        // Arrange
        await using var app = ApiAuthFixture.CreateApp();
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        var service = app.Services.GetRequiredService<FaultDataService>();
        var fault = service.GetFaults()[0];
        Assert.Empty(service.ChangeStatus(fault.Id, fault.Version, "В работе"));
        fault = service.GetFaults().Single(item => item.Id == fault.Id);
        Assert.Empty(service.ChangeStatus(fault.Id, fault.Version, "Завершена",
            new FaultCompletion { WorkDescription = "Диагностика", Result = "Исправлено" }));
        // Act
        var response = await client.DeleteAsync($"/api/equipment/{fault.EquipmentId}");
        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(service.GetMaintenanceHistory(fault.EquipmentId));
    }

    /// <summary>Включение без подтверждения либо с HS256 блокируется до запуска приложения.</summary>
    [Theory]
    [InlineData(false, "RS256")]
    [InlineData(true, "HS256")]
    public void InvalidConfiguration_ThrowsBeforeActivation(bool confirmed, string algorithm)
    {
        // Arrange
        var settings = new SupabaseAuthSettings
        {
            Enabled = true,
            SettingsConfirmed = confirmed,
            Issuer = ApiAuthFixture.Issuer,
            Audience = ApiAuthFixture.Audience,
            SigningAlgorithm = algorithm
        };
        // Act
        var exception = Record.Exception(settings.Validate);
        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }
}
