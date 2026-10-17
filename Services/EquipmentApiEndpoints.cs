using EquipmentManager.Models;
using EquipmentManager.Services.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace EquipmentManager.Services;

/// <summary>REST API оборудования, неисправностей и истории с прежней Bearer-аутентификацией.</summary>
public static class EquipmentApiEndpoints
{
    /// <summary>Регистрирует API выбранного DI-источника во всех средах приложения.</summary>
    public static void MapEquipmentApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(SupabaseAuthentication.ReadPolicy)
            .AddEndpointFilter<StorageErrorFilter>();
        var equipment = api.MapGroup("/equipment");
        equipment.MapGet("", async (ApplicationDataService data, HttpContext context, string? query) =>
            Results.Ok(await data.GetEquipmentAsync(context.User, query, context.RequestAborted)));
        equipment.MapGet("/{id:int}", async (int id, ApplicationDataService data, HttpContext context) =>
        {
            var item = await data.GetEquipmentAsync(context.User, id, context.RequestAborted);
            return item is null ? Error(context, 404, "equipment_not_found", ["Оборудование не найдено."]) : Results.Ok(item);
        });
        equipment.MapPost("", async (EquipmentRequest request, ApplicationDataService data, HttpContext context) =>
            Save(await data.SaveEquipmentAsync(context.User, request.ToEquipment(), context.RequestAborted), context, true))
            .RequireAuthorization(SupabaseAuthentication.WritePolicy);
        equipment.MapPut("/{id:int}", async (int id, EquipmentRequest request, ApplicationDataService data, HttpContext context) =>
            id <= 0 ? Error(context, 404, "equipment_not_found", ["Оборудование не найдено."]) :
            Save(await data.SaveEquipmentAsync(context.User, request.ToEquipment(id), context.RequestAborted), context, false))
            .RequireAuthorization(SupabaseAuthentication.WritePolicy);
        equipment.MapDelete("/{id:int}", async (int id, ApplicationDataService data, HttpContext context) =>
            await data.DeleteEquipmentAsync(context.User, id, context.RequestAborted) switch
            {
                EquipmentDeleteStatus.Deleted => Results.NoContent(),
                EquipmentDeleteStatus.Linked => Error(context, 409, "equipment_has_links", ["Удаление запрещено: у оборудования есть заявки или история обслуживания."]),
                _ => Error(context, 404, "equipment_not_found", ["Оборудование не найдено."])
            }).RequireAuthorization(SupabaseAuthentication.WritePolicy);

        var faults = api.MapGroup("/faults");
        faults.MapGet("", async (ApplicationDataService data, HttpContext context) =>
            Results.Ok(await data.GetFaultsAsync(context.User, context.RequestAborted)));
        faults.MapGet("/{id:int}", async (int id, ApplicationDataService data, HttpContext context) =>
        {
            var fault = (await data.GetFaultsAsync(context.User, context.RequestAborted)).FirstOrDefault(item => item.Id == id);
            return fault is null ? Error(context, 404, "fault_not_found", ["Заявка не найдена."]) : Results.Ok(fault);
        });
        faults.MapPost("", async (FaultRegistration request, ApplicationDataService data, HttpContext context) =>
            FaultResult(await data.RegisterFaultAsync(context.User, request, context.RequestAborted), context, true))
            .RequireAuthorization(SupabaseAuthentication.FaultRegisterPolicy);
        faults.MapPost("/{id:int}/start", async (int id, FaultTransitionRequest request, ApplicationDataService data, HttpContext context) =>
            FaultResult(await data.ChangeFaultAsync(context.User, id, request.ExpectedVersion, "В работе", null, context.RequestAborted), context))
            .RequireAuthorization(SupabaseAuthentication.FaultProcessPolicy);
        faults.MapPost("/{id:int}/complete", async (int id, FaultTransitionRequest request, ApplicationDataService data, HttpContext context) =>
            FaultResult(await data.ChangeFaultAsync(context.User, id, request.ExpectedVersion, "Завершена",
                new FaultCompletion { WorkDescription = request.WorkDescription ?? "", Result = request.Result ?? "" },
                context.RequestAborted), context)).RequireAuthorization(SupabaseAuthentication.FaultProcessPolicy);
        api.MapGet("/maintenance-records", async (int? equipmentId, ApplicationDataService data, HttpContext context) =>
            Results.Ok(await data.GetHistoryAsync(context.User, equipmentId, context.RequestAborted)));

        api.MapGet("/session/permissions", async (IAuthorizationService authorization, HttpContext context) =>
            Results.Ok(new ApplicationPermissions(
                (await authorization.AuthorizeAsync(context.User, context, SupabaseAuthentication.WritePolicy)).Succeeded,
                (await authorization.AuthorizeAsync(context.User, context, SupabaseAuthentication.FaultRegisterPolicy)).Succeeded,
                (await authorization.AuthorizeAsync(context.User, context, SupabaseAuthentication.FaultProcessPolicy)).Succeeded)));
    }

    /// <summary>Возвращает сохранённое оборудование либо понятный бизнес-отказ.</summary>
    private static IResult Save(EquipmentSaveResult result, HttpContext context, bool created) => result.Status switch
    {
        EquipmentSaveStatus.Saved when result.Equipment is not null => created
            ? Results.Created($"/api/equipment/{result.Equipment.Id}", result.Equipment) : Results.Ok(result.Equipment),
        EquipmentSaveStatus.Invalid => Error(context, 400, "validation_failed", result.Errors),
        EquipmentSaveStatus.Conflict => Error(context, 409, "inventory_number_conflict", result.Errors),
        EquipmentSaveStatus.NotFound => Error(context, 404, "equipment_not_found", result.Errors),
        _ => throw new InvalidOperationException("Неизвестный результат сохранения оборудования.")
    };

    /// <summary>Преобразует бизнес-результат заявки в HTTP, сохраняя конфликт версии отдельным кодом.</summary>
    private static IResult FaultResult(FaultWriteResult result, HttpContext context, bool created = false) => result.Status switch
    {
        FaultWriteStatus.Saved when result.Fault is not null => created
            ? Results.Created($"/api/faults/{result.Fault.Id}", result.Fault) : Results.Ok(result.Fault),
        FaultWriteStatus.NotFound => Error(context, 404, "fault_not_found", result.Errors),
        FaultWriteStatus.Conflict => Error(context, 409, "fault_conflict", result.Errors),
        FaultWriteStatus.InvalidTransition => Error(context, 400, "invalid_transition", result.Errors),
        FaultWriteStatus.Invalid => Error(context, 400, "validation_failed", result.Errors),
        _ => throw new InvalidOperationException("Неизвестный результат обработки заявки.")
    };

    /// <summary>Формирует общий ProblemDetails без SQL и секретов.</summary>
    private static IResult Error(HttpContext context, int status, string code, IReadOnlyList<string> errors) =>
        Results.Problem(statusCode: status, title: "Операция не выполнена.", extensions: new Dictionary<string, object?>
        {
            ["code"] = code, ["traceId"] = context.TraceIdentifier,
            ["errors"] = new Dictionary<string, IReadOnlyList<string>> { ["operation"] = errors }
        });

    private sealed class StorageErrorFilter(ILogger<StorageErrorFilter> logger) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try { return await next(context); }
            catch (StoreException exception)
            {
                var (status, code, message) = exception.Failure switch
                {
                    StoreFailure.Forbidden => (403, "forbidden", "Недостаточно прав для выполнения операции."),
                    StoreFailure.OutcomeUnknown => (503, "outcome_unknown", "Подтверждение сохранения не получено. Обновите данные перед повторной отправкой."),
                    StoreFailure.Unavailable => (503, "storage_unavailable", "Хранилище недоступно. Повторите попытку позже."),
                    _ => (500, "storage_error", "Не удалось выполнить операцию с данными.")
                };
                logger.LogWarning("Отказ API. HTTP: {Status}; код: {Code}; SQLSTATE: {SqlState}; TraceId: {TraceId}",
                    status, code, exception.SqlState, context.HttpContext.TraceIdentifier);
                return Error(context.HttpContext, status, code, [message]);
            }
            catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Текст и объект исключения могут содержать данные запроса или подключения.
                logger.LogError("Внутренняя ошибка API. Тип: {ErrorType}; TraceId: {TraceId}",
                    exception.GetType().Name, context.HttpContext.TraceIdentifier);
                return Error(context.HttpContext, 500, "internal_error", ["Внутренняя ошибка сервера. Повторите попытку позже."]);
            }
        }
    }
}
