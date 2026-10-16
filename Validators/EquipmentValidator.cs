using EquipmentManager.Models;

namespace EquipmentManager.Validators;

/// <summary>
/// Проверяет данные промышленного оборудования.
/// </summary>
public class EquipmentValidator
{
    /// <summary>
    /// Проверяет обязательные поля и допустимость технического состояния оборудования.
    /// </summary>
    /// <param name="equipment">Оборудование для проверки.</param>
    /// <returns>Список ошибок или пустой список, если данные корректны.</returns>
    /// <exception cref="ArgumentNullException">Оборудование не передано.</exception>
    public List<string> Validate(Equipment equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(equipment.Name))
        {
            errors.Add("Наименование оборудования обязательно для заполнения.");
        }

        if (string.IsNullOrWhiteSpace(equipment.InventoryNumber))
        {
            errors.Add("Инвентарный номер обязателен для заполнения.");
        }

        if (string.IsNullOrWhiteSpace(equipment.Location))
        {
            errors.Add("Место установки обязательно для заполнения.");
        }

        if (!Equipment.AllowedStatuses.Contains(equipment.Status))
        {
            errors.Add("Статус должен иметь значение «Исправно», «Требует обслуживания» или «Неисправно».");
        }

        return errors;
    }
}
