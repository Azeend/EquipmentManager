using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using EquipmentManager.Models;
using EquipmentManager.Services;
using EquipmentManager.Services.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет интеграцию DI, API и страниц локально, без Supabase и без запуска облачного теста.</summary>
public sealed class ApplicationIntegrationTests
{
    /// <summary>Оборудование, заявка и история проходят единый REST-процесс.</summary>
    [Fact]
    public async Task RestApi_EquipmentFaultHistory_RoundTrip()
    {
        await using var app = ApiAuthFixture.CreateApp(roles: ["engineer", "maintenance_specialist"]);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        var created = await client.PostAsJsonAsync("/api/equipment", new EquipmentRequest
        {
            Name = "API насос",
            InventoryNumber = "API-ROUNDTRIP",
            Location = "Цех",
            Status = "Исправно"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var equipment = (await created.Content.ReadFromJsonAsync<Equipment>())!;
        var registered = await client.PostAsJsonAsync("/api/faults", new { EquipmentId = equipment.Id, Description = "Вибрация", CreatedBy = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var fault = (await registered.Content.ReadFromJsonAsync<Fault>())!;
        Assert.Equal(ApiAuthFixture.UserId.ToString(), fault.Author);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/faults/{fault.Id}")).StatusCode);
        var request = new FaultTransitionRequest { ExpectedVersion = fault.Version, WorkDescription = "Диагностика", Result = "Исправно" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/faults/{fault.Id}/complete", request)).StatusCode);
        var started = await client.PostAsJsonAsync($"/api/faults/{fault.Id}/start", request);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/faults/{fault.Id}/start", request)).StatusCode);
        request.ExpectedVersion = (await started.Content.ReadFromJsonAsync<Fault>())!.Version;
        var completed = await client.PostAsJsonAsync($"/api/faults/{fault.Id}/complete", request);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var history = await client.GetFromJsonAsync<List<MaintenanceRecord>>($"/api/maintenance-records?equipmentId={equipment.Id}");
        Assert.Equal(fault.Id, Assert.Single(history!).FaultId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/equipment/{equipment.Id}")).StatusCode);
    }

    /// <summary>Новые команды проверяют прежнюю матрицу ролей на сервере.</summary>
    [Theory]
    [InlineData("manager", "/api/faults")]
    [InlineData("manager", "/api/faults/1/start")]
    [InlineData("manager", "/api/faults/1/complete")]
    [InlineData("engineer", "/api/faults/1/start")]
    [InlineData("engineer", "/api/faults/1/complete")]
    public async Task FaultApi_RejectsForbiddenRole(string role, string path)
    {
        await using var app = ApiAuthFixture.CreateApp(roles: [role]);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        var response = await client.PostAsJsonAsync(path, new { EquipmentId = 1, Description = "Проблема", ExpectedVersion = 1, WorkDescription = "Работы", Result = "Готово" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Разрешения интерфейса получаются из серверного профиля, а не claims бизнес-ролей JWT.</summary>
    [Theory]
    [InlineData("engineer", true, true, false)]
    [InlineData("maintenance_specialist", false, true, true)]
    [InlineData("manager", false, false, false)]
    public async Task Api_ReturnsProfilePermissions(string role, bool equipment, bool register, bool process)
    {
        await using var app = ApiAuthFixture.CreateApp(roles: [role]);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        Assert.Equal(new ApplicationPermissions(equipment, register, process),
            await client.GetFromJsonAsync<ApplicationPermissions>("/api/session/permissions"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/faults")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/maintenance-records")).StatusCode);
    }

    /// <summary>Новые маршруты не принимают анонимные запросы.</summary>
    [Theory]
    [InlineData("/api/faults")]
    [InlineData("/api/maintenance-records")]
    [InlineData("/api/session/permissions")]
    public async Task Api_AnonymousRead_Returns401(string path)
    {
        await using var app = ApiAuthFixture.CreateApp();
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app, authenticated: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    /// <summary>Новый HTTP-клиент отправляет команды и все три страницы читают API при SSR.</summary>
    [Fact]
    public async Task FrontendClient_AndPages_UseBackendApi()
    {
        await using var fixture = new UiAuthFixture(roles: ["engineer", "maintenance_specialist"]);
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
        var cookie = UiAuthFixture.Cookie(login);
        Assert.Contains("__Host-EquipmentManager.Session", cookie);
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<UiSessionStore>();
        var sessionId = sessions.Create(fixture.Auth.AccessToken, ApiAuthFixture.UserId, fixture.Clock.Now.AddMinutes(5));
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", ApiAuthFixture.UserId.ToString()), new Claim(UiSessionStore.SessionClaim, sessionId)], UiAuthentication.Scheme));
        ((IHostEnvironmentAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
        var backend = scope.ServiceProvider.GetRequiredService<SessionBackendClient>();
        var marker = "UI-API-" + Guid.NewGuid().ToString("N");
        var equipment = await backend.SaveEquipmentAsync(new Equipment
        {
            Name = marker,
            InventoryNumber = marker,
            Location = "Цех",
            Status = "Исправно"
        });
        await backend.RegisterFaultAsync(new FaultRegistration { EquipmentId = equipment.Id, Description = marker });
        var fault = Assert.Single(await backend.GetFaultsAsync(), item => item.Description == marker);
        await backend.StartFaultAsync(fault);
        var conflict = await Assert.ThrowsAsync<BackendApiException>(() => backend.StartFaultAsync(fault));
        Assert.Equal(409, conflict.StatusCode);
        fault = Assert.Single(await backend.GetFaultsAsync(), item => item.Description == marker);
        await backend.CompleteFaultAsync(fault, new FaultCompletion { WorkDescription = marker, Result = "Готово" });
        Assert.Single(await backend.GetHistoryAsync(), item => item.EquipmentId == equipment.Id);
        foreach (var path in new[] { "/equipment", "/faults", "/maintenance-history" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", cookie);
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains(marker, html);
            Assert.DoesNotContain(fixture.Auth.AccessToken, html);
        }
    }

    /// <summary>Выбор источника не регистрирует второй набор сервисов и не открывает соединения.</summary>
    [Theory]
    [InlineData("InMemory")]
    [InlineData("PostgreSQL")]
    public async Task StorageRegistration_SelectsOneCompleteBackend(string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = mode }).Build();
        var credentials = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EquipmentCrud"] = "Host=localhost;Database=postgres;Username=equipment_api;SSL Mode=VerifyFull"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEquipmentStorage(configuration, credentials);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ApplicationDataService>());
        Assert.Equal(mode == "InMemory", scope.ServiceProvider.GetService<EquipmentDataService>() is not null);
        Assert.Equal(mode == "InMemory", scope.ServiceProvider.GetService<FaultDataService>() is not null);
        Assert.Equal(mode == "PostgreSQL", scope.ServiceProvider.GetService<PostgresEquipmentStore>() is not null);
        Assert.Equal(mode == "PostgreSQL", scope.ServiceProvider.GetService<PostgresFaultStore>() is not null);
        Assert.Equal(mode == "PostgreSQL", scope.ServiceProvider.GetService<PostgresMaintenanceStore>() is not null);
    }

    /// <summary>Неверный режим или отсутствие защищённого CRUD-подключения не включает PostgreSQL молча.</summary>
    [Theory]
    [InlineData("Mixed")]
    [InlineData("PostgreSQL")]
    public void StorageRegistration_InvalidConfiguration_Fails(string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = mode }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddEquipmentStorage(configuration, new ConfigurationBuilder().Build()));
    }
}
