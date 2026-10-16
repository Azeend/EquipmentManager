namespace EquipmentManager.Models;

/// <summary>
/// Представляет единицу промышленного оборудования в системе учёта.
/// </summary>
public class Equipment
{
    /// <summary>
    /// Идентификатор оборудования.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Наименование оборудования.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Инвентарный номер оборудования.
    /// </summary>
    public string InventoryNumber { get; set; } = string.Empty;

    /// <summary>
    /// Место установки оборудования.
    /// </summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>
    /// Техническое состояние оборудования.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
