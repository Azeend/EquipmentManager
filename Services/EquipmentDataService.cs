using EquipmentManager.Models;
using EquipmentManager.Validators;

namespace EquipmentManager.Services;

/// <summary>
/// Хранит оборудование в памяти на время работы приложения.
/// </summary>
public class EquipmentDataService
{
    private readonly object syncRoot = new();
    private readonly EquipmentValidator validator = new();
    private readonly List<Equipment> equipmentItems = CreateInitialEquipment();
    private int nextId = 6;

    /// <summary>
    /// Возвращает независимый снимок текущего списка оборудования.
    /// </summary>
    /// <returns>Коллекция объектов <see cref="Equipment"/>.</returns>
    public IReadOnlyList<Equipment> GetEquipment()
    {
        lock (syncRoot)
        {
            return equipmentItems.Select(Clone).ToList();
        }
    }

    /// <summary>
    /// Проверяет и сохраняет копию записи в памяти; возвращает ошибки без изменения данных.
    /// </summary>
    public IReadOnlyList<string> Save(Equipment equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        var candidate = Clone(equipment);
        candidate.Name = candidate.Name.Trim();
        candidate.InventoryNumber = candidate.InventoryNumber.Trim();
        candidate.Location = candidate.Location.Trim();

        lock (syncRoot)
        {
            var errors = validator.Validate(candidate);
            if (equipmentItems.Any(item => item.Id != candidate.Id
                && string.Equals(item.InventoryNumber, candidate.InventoryNumber, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("Оборудование с таким инвентарным номером уже существует.");
            }

            var index = equipmentItems.FindIndex(item => item.Id == candidate.Id);
            if (candidate.Id != 0 && index < 0)
            {
                errors.Add("Оборудование не найдено. Обновите список.");
            }

            if (errors.Count > 0)
            {
                return errors;
            }

            if (candidate.Id == 0)
            {
                candidate.Id = nextId++;
                equipmentItems.Add(candidate);
            }
            else
            {
                equipmentItems[index] = candidate;
            }

            return [];
        }
    }

    /// <summary>
    /// Удаляет оборудование по идентификатору из хранилища в памяти.
    /// </summary>
    /// <param name="id">Идентификатор удаляемого оборудования.</param>
    /// <returns>Истина, если запись удалена; ложь, если она уже отсутствует.</returns>
    public bool Delete(int id)
    {
        lock (syncRoot)
        {
            var index = equipmentItems.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return false;
            }

            equipmentItems.RemoveAt(index);
            return true;
        }
    }

    /// <summary>
    /// Создаёт независимую копию оборудования.
    /// </summary>
    private static Equipment Clone(Equipment equipment) => new()
    {
        Id = equipment.Id,
        Name = equipment.Name,
        InventoryNumber = equipment.InventoryNumber,
        Location = equipment.Location,
        Status = equipment.Status
    };

    /// <summary>
    /// Создаёт начальный набор оборудования.
    /// </summary>
    private static List<Equipment> CreateInitialEquipment() =>
        [
            new Equipment
            {
                Id = 1,
                Name = "Центробежный насос ЦНС 180-85",
                InventoryNumber = "INV-1001",
                Location = "Насосная станция №1, цех водоснабжения",
                Status = "Исправно"
            },
            new Equipment
            {
                Id = 2,
                Name = "Асинхронный электродвигатель АИР 160S4",
                InventoryNumber = "INV-1002",
                Location = "Компрессорная, линия №2",
                Status = "Исправно"
            },
            new Equipment
            {
                Id = 3,
                Name = "Кожухотрубный теплообменник ТТ-25",
                InventoryNumber = "INV-1003",
                Location = "Участок теплообмена, корпус Б",
                Status = "Требует обслуживания"
            },
            new Equipment
            {
                Id = 4,
                Name = "Винтовой компрессор Atlas Copco GA 37",
                InventoryNumber = "INV-1004",
                Location = "Компрессорная станция, зал А",
                Status = "Исправно"
            },
            new Equipment
            {
                Id = 5,
                Name = "Датчик температуры Pt100 WIKA",
                InventoryNumber = "INV-1005",
                Location = "Реакторный блок, контур охлаждения",
                Status = "Исправно"
            }
        ];

}
