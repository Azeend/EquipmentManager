using EquipmentManager.Models;

namespace EquipmentManager.Services;

/// <summary>
/// Предоставляет тестовый набор данных об оборудовании.
/// </summary>
public class EquipmentDataService
{
    /// <summary>
    /// Возвращает список из пяти единиц промышленного оборудования.
    /// </summary>
    /// <returns>Коллекция объектов <see cref="Equipment"/>.</returns>
    public IReadOnlyList<Equipment> GetEquipment()
    {
        List<Equipment> equipmentItems =
        [
            new Equipment
            {
                Id = 1,
                Name = "Центробежный насос ЦНС 180-85",
                InventoryNumber = "INV-1001",
                Location = "Насосная станция №1, цех водоснабжения",
                Status = "В работе"
            },
            new Equipment
            {
                Id = 2,
                Name = "Асинхронный электродвигатель АИР 160S4",
                InventoryNumber = "INV-1002",
                Location = "Компрессорная, линия №2",
                Status = "В резерве"
            },
            new Equipment
            {
                Id = 3,
                Name = "Кожухотрубный теплообменник ТТ-25",
                InventoryNumber = "INV-1003",
                Location = "Участок теплообмена, корпус Б",
                Status = "На обслуживании"
            },
            new Equipment
            {
                Id = 4,
                Name = "Винтовой компрессор Atlas Copco GA 37",
                InventoryNumber = "INV-1004",
                Location = "Компрессорная станция, зал А",
                Status = "В работе"
            },
            new Equipment
            {
                Id = 5,
                Name = "Датчик температуры Pt100 WIKA",
                InventoryNumber = "INV-1005",
                Location = "Реакторный блок, контур охлаждения",
                Status = "Исправен"
            }
        ];

        return equipmentItems;
    }
}
