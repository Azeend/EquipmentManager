namespace EquipmentManager.Models;

/// <summary>Неизменяемая запись обслуживания, связанная с завершённой заявкой.</summary>
public sealed record MaintenanceRecord
{
    /// <summary>Номер исходной заявки; уникальный ключ записи истории.</summary>
    public int FaultId { get; init; }
    /// <summary>Идентификатор обслуженного оборудования.</summary>
    public int EquipmentId { get; init; }
    /// <summary>Название и инвентарный номер из исходной заявки.</summary>
    public string EquipmentLabel { get; init; } = string.Empty;
    /// <summary>Дата завершения обслуживания в UTC.</summary>
    public DateTimeOffset PerformedAt { get; init; }
    /// <summary>Описание выполненных работ.</summary>
    public string WorkDescription { get; init; } = string.Empty;
    /// <summary>Результат выполненных работ.</summary>
    public string Result { get; init; } = string.Empty;
}
