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
                Id = nextId++, EquipmentId = equipment.Id,
                EquipmentLabel = $"{equipment.Name} ({equipment.InventoryNumber})",
                Description = "Тестовая заявка: повышенная вибрация, требуется диагностика.",
                Author = "Тестовые данные", CreatedAt = DateTimeOffset.UtcNow
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
    {
        ArgumentNullException.ThrowIfNull(registration);
        var errors = Validate(registration);
        lock (syncRoot)
        {
            var equipment = equipmentDataService.GetEquipment().FirstOrDefault(item => item.Id == registration.EquipmentId);
            if (equipment is null) { errors.Add("Выбранное оборудование недоступно. Обновите список оборудования."); }
            if (errors.Count > 0 || equipment is null) { return errors; }
            faults.Add(new Fault
            {
                Id = nextId++, EquipmentId = equipment.Id,
                EquipmentLabel = $"{equipment.Name} ({equipment.InventoryNumber})",
                Description = registration.Description.Trim(), CreatedAt = DateTimeOffset.UtcNow
            });
            return [];
        }
    }

    /// <summary>Разрешает только последовательные переходы с проверкой версии и полей завершения.</summary>
    public IReadOnlyList<string> ChangeStatus(int id, int expectedVersion, string targetStatus, FaultCompletion? completion = null)
    {
        lock (syncRoot)
        {
            var index = faults.FindIndex(item => item.Id == id);
            if (index < 0) { return ["Заявка больше не существует. Обновите список."]; }
            var fault = faults[index];
            if (fault.Version != expectedVersion) { return ["Заявка изменена в другом сеансе. Обновите данные; черновик работ сохранён в форме."]; }
            if (!((fault.Status == "Новая" && targetStatus == "В работе") || (fault.Status == "В работе" && targetStatus == "Завершена")))
            { return ["Недопустимый переход статуса. Разрешено: Новая → В работе → Завершена."]; }
            if (targetStatus == "Завершена")
            {
                if (completion is null) { return ["Укажите описание работ и результат."]; }
                var errors = Validate(completion);
                if (errors.Count > 0) { return errors; }
                var completedFault = fault with
                {
                    Status = targetStatus, Version = fault.Version + 1, CompletedAt = DateTimeOffset.UtcNow,
                    WorkDescription = completion.WorkDescription.Trim(), Result = completion.Result.Trim()
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
                    return ["Для заявки уже существует запись истории. Обновите данные."];
                }
                faults[index] = completedFault;
            }
            else
            {
                faults[index] = fault with { Status = targetStatus, Version = fault.Version + 1, StartedAt = DateTimeOffset.UtcNow };
            }
            return [];
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
