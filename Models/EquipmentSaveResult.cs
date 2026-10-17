namespace EquipmentManager.Models;

/// <summary>Результат сохранения оборудования без зависимости от HTTP.</summary>
public enum EquipmentSaveStatus
{
    /// <summary>Запись сохранена.</summary>
    Saved,
    /// <summary>Входные данные некорректны.</summary>
    Invalid,
    /// <summary>Инвентарный номер занят.</summary>
    Conflict,
    /// <summary>Редактируемая запись отсутствует.</summary>
    NotFound
}

/// <summary>Снимок сохранённой записи либо ошибки и причина отказа.</summary>
public sealed record EquipmentSaveResult(EquipmentSaveStatus Status, Equipment? Equipment, IReadOnlyList<string> Errors);

/// <summary>Результат удаления с проверкой связей оборудования.</summary>
public enum EquipmentDeleteStatus
{
    /// <summary>Оборудование удалено.</summary>
    Deleted,
    /// <summary>Оборудование отсутствует.</summary>
    NotFound,
    /// <summary>Удаление запрещено связанными заявками или историей.</summary>
    Linked
}
