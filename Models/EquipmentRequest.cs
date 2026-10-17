namespace EquipmentManager.Models;

/// <summary>Входные поля REST API; идентификатор назначается сервером или берётся из маршрута.</summary>
public sealed class EquipmentRequest
{
    /// <summary>Наименование оборудования.</summary>
    public string? Name { get; set; }
    /// <summary>Инвентарный номер.</summary>
    public string? InventoryNumber { get; set; }
    /// <summary>Место установки.</summary>
    public string? Location { get; set; }
    /// <summary>Одно из допустимых технических состояний.</summary>
    public string? Status { get; set; }

    /// <summary>Преобразует отсутствующие значения в пустые для общего валидатора.</summary>
    public Equipment ToEquipment(int id = 0) => new()
    {
        Id = id, Name = Name ?? string.Empty, InventoryNumber = InventoryNumber ?? string.Empty,
        Location = Location ?? string.Empty, Status = Status ?? string.Empty
    };
}
