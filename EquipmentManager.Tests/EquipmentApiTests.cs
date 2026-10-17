using System.Net;
using System.Net.Http.Json;
using EquipmentManager.Models;
using EquipmentManager.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет реальные HTTP-ответы учебного API на локальном Kestrel.</summary>
public sealed class EquipmentApiTests
{
    /// <summary>Защищённый CRUD работает во всех средах приложения.</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Crud_ReturnsExpectedResponses(string environmentName)
    {
        // Arrange
        await using var app = CreateApp(environmentName);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        var request = CreateRequest();

        // Act
        var createResponse = await client.PostAsJsonAsync("/api/equipment", request);
        var created = await createResponse.Content.ReadFromJsonAsync<Equipment>();
        Assert.NotNull(created);
        var readResponse = await client.GetAsync($"/api/equipment/{created.Id}");
        request.Name = "Обновлённый насос";
        var updateResponse = await client.PutAsJsonAsync($"/api/equipment/{created.Id}", request);
        var updated = await updateResponse.Content.ReadFromJsonAsync<Equipment>();
        var deleteResponse = await client.DeleteAsync($"/api/equipment/{created.Id}");
        var missingResponse = await client.GetAsync($"/api/equipment/{created.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal($"/api/equipment/{created.Id}", createResponse.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal("Обновлённый насос", updated?.Name);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(string.Empty, await deleteResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    /// <summary>Некорректные значения и дубликаты не изменяют общее хранилище.</summary>
    [Fact]
    public async Task Development_InvalidAndDuplicateRequests_Return400And409()
    {
        // Arrange
        await using var app = CreateApp("Development");
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        var duplicate = CreateRequest();
        duplicate.InventoryNumber = " inv-1001 ";
        var invalid = CreateRequest();
        invalid.Name = " ";
        invalid.Location = null;

        // Act
        var invalidResponse = await client.PostAsJsonAsync("/api/equipment", invalid);
        var duplicateResponse = await client.PostAsJsonAsync("/api/equipment", duplicate);
        var updateConflict = await client.PutAsJsonAsync("/api/equipment/2", duplicate);
        var items = await client.GetFromJsonAsync<List<Equipment>>("/api/equipment");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, updateConflict.StatusCode);
        Assert.Contains("inventory_number_conflict", await duplicateResponse.Content.ReadAsStringAsync());
        Assert.NotNull(items);
        Assert.Equal(5, items.Count);
        Assert.Equal("INV-1002", items.Single(item => item.Id == 2).InventoryNumber);
    }

    /// <summary>Отсутствующая запись не создаётся через PUT; повторный DELETE возвращает 404.</summary>
    [Fact]
    public async Task Development_MissingEquipment_Returns404()
    {
        // Arrange
        await using var app = CreateApp("Development");
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);

        // Act
        var read = await client.GetAsync("/api/equipment/999");
        var update = await client.PutAsJsonAsync("/api/equipment/999", CreateRequest());
        var delete = await client.DeleteAsync("/api/equipment/999");
        var zero = await client.PutAsJsonAsync("/api/equipment/0", CreateRequest());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, zero.StatusCode);
        Assert.Equal(5, app.Services.GetRequiredService<EquipmentDataService>().GetEquipment().Count);
    }

    /// <summary>В Production и Staging все пять маршрутов сохраняют запрет анонимного доступа.</summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopment_EquipmentRoutes_RequireAuthentication(string environmentName)
    {
        // Arrange
        await using var app = CreateApp(environmentName);
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app, authenticated: false);

        // Act
        var list = await client.GetAsync("/api/equipment");
        var read = await client.GetAsync("/api/equipment/1");
        var create = await client.PostAsJsonAsync("/api/equipment", CreateRequest());
        var update = await client.PutAsJsonAsync("/api/equipment/1", CreateRequest());
        var delete = await client.DeleteAsync("/api/equipment/1");

        // Assert
        foreach (var response in new[] { list, read, create, update, delete })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(5, app.Services.GetRequiredService<EquipmentDataService>().GetEquipment().Count);
    }

    /// <summary>Создаёт изолированный хост с тем же регистратором маршрутов, что и Program.</summary>
    private static WebApplication CreateApp(string environmentName)
    {
        return ApiAuthFixture.CreateApp(environmentName);
    }

    /// <summary>Возвращает корректный запрос с уникальным тестовым номером.</summary>
    private static EquipmentRequest CreateRequest() => new()
    {
        Name = "Насос API",
        InventoryNumber = "API-TEST-001",
        Location = "Цех API",
        Status = "Исправно"
    };
}
