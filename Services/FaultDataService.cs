using System.ComponentModel.DataAnnotations;
using EquipmentManager.Models;

namespace EquipmentManager.Services;

/// <summary>Хранит заявки в памяти и проверяет команды на сервере.</summary>
public sealed class FaultDataService
{
    private readonly object syncRoot = new();
    private readonly EquipmentDataService equipmentDataService;
    private readonly List<Fault> faults = [];
    private readonly Dictionary<int, MaintenanceRecord> maintenanceRecords = [];
    private int nextId = 1;

    /// <summary>Создаёт тестовые заявки для существующего оборудования.</summary>
    public FaultDataService(EquipmentDataService equipmentDataService)
    {
        this.equipmentDataService = equipmentDataService;
        foreach (var equipment in equipmentDataService.GetEquipment().Take(2))
        {
            faults.Add(new Fault
            {
                Id = nextId++,
                EquipmentId = equipment.Id,
                EquipmentLabel = $"{equipment.Name} ({equipment.InventoryNumber})",
                Description = "Тестовая заявка: повышенная вибрация, требуется диагностика.",
                Author = "Тестовые данные",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>Возвращает независимую очередь от новых заявок к старым.</summary>
    public IReadOnlyList<Fault> GetFaults()
    {
        lock (syncRoot) { return faults.OrderByDescending(item => item.Id).ToList(); }
    }

    /// <summary>Возвращает историю выбранного оборудования или всего парка от новых записей к старым.</summary>
    /// <param name="equipmentId">Идентификатор оборудования или null для всего парка.</param>
    public IReadOnlyList<MaintenanceRecord> GetMaintenanceHistory(int? equipmentId = null)
    {
        lock (syncRoot)
        {
            return maintenanceRecords.Values
                .Where(item => equipmentId is null || item.EquipmentId == equipmentId)
                .OrderByDescending(item => item.PerformedAt)
                .ThenByDescending(item => item.FaultId)
                .ToList();
        }
    }

    /// <summary>Регистрирует заявку после проверки описания и оборудования.</summary>
    public IReadOnlyList<string> Register(FaultRegistration registration)
        => RegisterWithResult(registration).Errors;

    /// <summary>Регистрирует заявку и атомарно возвращает назначенный ID и тип отказа для API.</summary>
    public FaultWriteResult RegisterWithResult(FaultRegistration registration, string? author = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var errors = Validate(registration);
        lock (syncRoot)
        {
            var equipment = equipmentDataService.GetEquipment().FirstOrDefault(item => item.Id == registration.EquipmentId);
            if (errors.Count > 0) { return new(FaultWriteStatus.Invalid, null, errors); }
            if (equipment is null) { return new(FaultWriteStatus.NotFound, null, ["Выбранное оборудование недоступно. Обновите список оборудования."]); }
            var fault = new Fault
            {
                Id = nextId++,
                EquipmentId = equipment.Id,
                EquipmentLabel = $"{equipment.Name} ({equipment.InventoryNumber})",
                Description = registration.Description.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
                Author = author ?? "Не установлен (без авторизации)"
            };
            faults.Add(fault);
            return new(FaultWriteStatus.Saved, fault, []);
        }
    }

    /// <summary>Удаляет через API только несвязанное оборудование; блокировка общая с регистрацией заявки.</summary>
    public EquipmentDeleteStatus DeleteEquipment(int equipmentId)
    {
        lock (syncRoot)
        {
            if (!equipmentDataService.GetEquipment().Any(item => item.Id == equipmentId))
            {
                return EquipmentDeleteStatus.NotFound;
            }
            if (faults.Any(item => item.EquipmentId == equipmentId)
                || maintenanceRecords.Values.Any(item => item.EquipmentId == equipmentId))
            {
                return EquipmentDeleteStatus.Linked;
            }
            return equipmentDataService.Delete(equipmentId) ? EquipmentDeleteStatus.Deleted : EquipmentDeleteStatus.NotFound;
        }
    }

    /// <summary>Разрешает только последовательные переходы с проверкой версии и полей завершения.</summary>
    public IReadOnlyList<string> ChangeStatus(int id, int expectedVersion, string targetStatus, FaultCompletion? completion = null)
        => ChangeStatusWithResult(id, expectedVersion, targetStatus, completion).Errors;

    /// <summary>Возвращает типизированный результат прежней атомарной смены статуса в памяти.</summary>
    public FaultWriteResult ChangeStatusWithResult(int id, int expectedVersion, string targetStatus, FaultCompletion? completion = null)
    {
        lock (syncRoot)
        {
            var index = faults.FindIndex(item => item.Id == id);
            if (index < 0) { return new(FaultWriteStatus.NotFound, null, ["Заявка больше не существует. Обновите список."]); }
            var fault = faults[index];
            if (fault.Version != expectedVersion) { return new(FaultWriteStatus.Conflict, null, ["Заявка изменена в другом сеансе. Обновите данные; черновик работ сохранён в форме."]); }
            if (!((fault.Status == "Новая" && targetStatus == "В работе") || (fault.Status == "В работе" && targetStatus == "Завершена")))
            { return new(FaultWriteStatus.InvalidTransition, null, ["Недопустимый переход статуса. Разрешено: Новая → В работе → Завершена."]); }
            if (targetStatus == "Завершена")
            {
                if (completion is null) { return new(FaultWriteStatus.Invalid, null, ["Укажите описание работ и результат."]); }
                var errors = Validate(completion);
                if (errors.Count > 0) { return new(FaultWriteStatus.Invalid, null, errors); }
                var completedFault = fault with
                {
                    Status = targetStatus,
                    Version = fault.Version + 1,
                    CompletedAt = DateTimeOffset.UtcNow,
                    WorkDescription = completion.WorkDescription.Trim(),
                    Result = completion.Result.Trim()
                };
                var record = new MaintenanceRecord
                {
                    FaultId = completedFault.Id,
                    EquipmentId = completedFault.EquipmentId,
                    EquipmentLabel = completedFault.EquipmentLabel,
                    PerformedAt = completedFault.CompletedAt!.Value,
                    WorkDescription = completedFault.WorkDescription,
                    Result = completedFault.Result
                };
                // До добавления истории статус не меняется; оба изменения скрыты общей блокировкой.
                if (!maintenanceRecords.TryAdd(fault.Id, record))
                {
                    return new(FaultWriteStatus.Conflict, null, ["Для заявки уже существует запись истории. Обновите данные."]);
                }
                faults[index] = completedFault;
            }
            else
            {
                faults[index] = fault with { Status = targetStatus, Version = fault.Version + 1, StartedAt = DateTimeOffset.UtcNow };
            }
            return new(FaultWriteStatus.Saved, faults[index], []);
        }
    }

    /// <summary>Повторяет проверку обязательных полей на стороне сервиса.</summary>
    private static List<string> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, true);
        return results.Select(item => item.ErrorMessage ?? "Некорректные данные.").ToList();
    }
}
