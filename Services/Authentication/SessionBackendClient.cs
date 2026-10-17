using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EquipmentManager.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace EquipmentManager.Services.Authentication;

/// <summary>Ошибка Backend API с безопасными сообщениями валидации и бизнес-отказа.</summary>
public sealed class BackendApiException(int statusCode, IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    /// <summary>HTTP-код отказа.</summary>
    public int StatusCode { get; } = statusCode;
    /// <summary>Ошибки для отображения без потери ввода формы.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Передаёт токен текущего серверного сеанса только в доверенный Backend API.</summary>
public sealed class SessionBackendClient(IHttpClientFactory clients, UiAuthSettings settings,
    AuthenticationStateProvider authenticationState, UiSessionStore sessions)
{
    /// <summary>Загружает реестр через API.</summary>
    public async Task<IReadOnlyList<Equipment>> GetEquipmentAsync(CancellationToken cancellationToken = default) =>
        await RequestAsync<List<Equipment>>(HttpMethod.Get, "api/equipment", null, cancellationToken) ?? [];

    /// <summary>Получает разрешения из существующих серверных политик.</summary>
    public async Task<ApplicationPermissions> GetPermissionsAsync(CancellationToken cancellationToken = default) =>
        await RequestAsync<ApplicationPermissions>(HttpMethod.Get, "api/session/permissions", null, cancellationToken)
            ?? throw new BackendApiException(502, ["Не удалось получить права пользователя."]);

    /// <summary>Создаёт или редактирует оборудование.</summary>
    public async Task<Equipment> SaveEquipmentAsync(Equipment equipment, CancellationToken cancellationToken = default) =>
        await RequestAsync<Equipment>(equipment.Id == 0 ? HttpMethod.Post : HttpMethod.Put,
            equipment.Id == 0 ? "api/equipment" : $"api/equipment/{equipment.Id}",
            new EquipmentRequest { Name = equipment.Name, InventoryNumber = equipment.InventoryNumber, Location = equipment.Location, Status = equipment.Status },
            cancellationToken) ?? throw new BackendApiException(502, ["Сервер не вернул сохранённую запись."]);

    /// <summary>Удаляет только несвязанное оборудование.</summary>
    public async Task DeleteEquipmentAsync(int id, CancellationToken cancellationToken = default) =>
        _ = await RequestAsync<object>(HttpMethod.Delete, $"api/equipment/{id}", null, cancellationToken);

    /// <summary>Загружает очередь неисправностей.</summary>
    public async Task<IReadOnlyList<Fault>> GetFaultsAsync(CancellationToken cancellationToken = default) =>
        await RequestAsync<List<Fault>>(HttpMethod.Get, "api/faults", null, cancellationToken) ?? [];

    /// <summary>Регистрирует неисправность; автор определяется только Backend.</summary>
    public async Task RegisterFaultAsync(FaultRegistration registration, CancellationToken cancellationToken = default) =>
        _ = await RequestAsync<Fault>(HttpMethod.Post, "api/faults", registration, cancellationToken);

    /// <summary>Берёт заявку в работу с проверкой показанной версии.</summary>
    public async Task StartFaultAsync(Fault fault, CancellationToken cancellationToken = default) =>
        _ = await RequestAsync<Fault>(HttpMethod.Post, $"api/faults/{fault.Id}/start",
            new FaultTransitionRequest { ExpectedVersion = fault.Version }, cancellationToken);

    /// <summary>Завершает заявку одной командой; отдельной отправки истории нет.</summary>
    public async Task CompleteFaultAsync(Fault fault, FaultCompletion completion, CancellationToken cancellationToken = default) =>
        _ = await RequestAsync<Fault>(HttpMethod.Post, $"api/faults/{fault.Id}/complete",
            new FaultTransitionRequest { ExpectedVersion = fault.Version, WorkDescription = completion.WorkDescription, Result = completion.Result }, cancellationToken);

    /// <summary>Загружает историю обслуживания.</summary>
    public async Task<IReadOnlyList<MaintenanceRecord>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        await RequestAsync<List<MaintenanceRecord>>(HttpMethod.Get, "api/maintenance-records", null, cancellationToken) ?? [];

    private async Task<T?> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var principal = (await authenticationState.GetAuthenticationStateAsync()).User;
        var session = sessions.Find(principal) ?? throw new UiSignInException("expired");
        if (settings.BackendBaseUrl is null) { throw new UiSignInException("backend_configuration"); }
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(settings.BackendBaseUrl, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            if (body is not null) { request.Content = JsonContent.Create(body, body.GetType()); }
            using var response = await clients.CreateClient(UiAuthentication.BackendClient).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                sessions.Remove(principal);
                throw new UiSignInException("expired");
            }
            if (response.StatusCode == HttpStatusCode.Forbidden) { throw new UiSignInException("forbidden"); }
            if (!response.IsSuccessStatusCode) { throw new BackendApiException((int)response.StatusCode, await ReadErrorsAsync(response, cancellationToken)); }
            if (response.StatusCode == HttpStatusCode.NoContent) { return default; }
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new BackendApiException(502, ["Backend API вернул некорректный ответ."]);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new BackendApiException(503, ["Не удалось подключиться к Backend API. Если запрос изменял данные, обновите список перед повтором."]);
        }
        catch (JsonException)
        {
            throw new BackendApiException(502, ["Не удалось прочитать ответ Backend API."]);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadErrorsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ["Backend API вернул ошибку. Повторите попытку позже."];
            }
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject().Where(field => field.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(field => field.Value.EnumerateArray()).Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!).ToArray();
                if (messages.Length > 0) { return messages; }
            }
            if (document.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                return [title.GetString()!];
            }
        }
        catch (JsonException) { return ["Backend API вернул ошибку. Повторите попытку позже."]; }
        return [$"Backend API недоступен или отклонил операцию (HTTP {(int)response.StatusCode})."];
    }
}
