using System.Reflection;
using EquipmentManager.Components.Pages;
using EquipmentManager.Models;
using EquipmentManager.Services;
using EquipmentManager.Validators;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет обязательные поля, уникальность номера и существующий поиск.</summary>
public sealed class EquipmentTests
{
    /// <summary>Пустое или пробельное обязательное поле отклоняется валидатором.</summary>
    [Theory]
    [InlineData(nameof(Equipment.Name), "", "Наименование оборудования обязательно для заполнения.")]
    [InlineData(nameof(Equipment.Name), "  ", "Наименование оборудования обязательно для заполнения.")]
    [InlineData(nameof(Equipment.InventoryNumber), "", "Инвентарный номер обязателен для заполнения.")]
    [InlineData(nameof(Equipment.InventoryNumber), "  ", "Инвентарный номер обязателен для заполнения.")]
    [InlineData(nameof(Equipment.Location), "", "Место установки обязательно для заполнения.")]
    [InlineData(nameof(Equipment.Location), "  ", "Место установки обязательно для заполнения.")]
    [InlineData(nameof(Equipment.Status), "", "Статус должен иметь значение «Исправно», «Требует обслуживания» или «Неисправно».")]
    [InlineData(nameof(Equipment.Status), "  ", "Статус должен иметь значение «Исправно», «Требует обслуживания» или «Неисправно».")]
    public void Validate_RequiredFieldIsBlank_ReturnsError(string propertyName, string value, string expectedError)
    {
        // Arrange
        var equipment = CreateEquipment();
        typeof(Equipment).GetProperty(propertyName)!.SetValue(equipment, value);
        var validator = new EquipmentValidator();

        // Act
        var errors = validator.Validate(equipment);

        // Assert
        Assert.Equal(expectedError, Assert.Single(errors));
    }

    /// <summary>Корректное оборудование проходит проверку обязательных полей.</summary>
    [Fact]
    public void Validate_ValidEquipment_ReturnsNoErrors()
    {
        // Arrange
        var validator = new EquipmentValidator();
        var equipment = CreateEquipment();
        // Act
        var errors = validator.Validate(equipment);
        // Assert
        Assert.Empty(errors);
    }

    /// <summary>Повторный номер с другим регистром и пробелами не создаёт запись.</summary>
    [Theory]
    [InlineData("INV-1001")]
    [InlineData(" inv-1001 ")]
    public void Save_DuplicateInventoryNumber_LeavesEquipmentUnchanged(string number)
    {
        // Arrange
        var service = new EquipmentDataService();
        var before = service.GetEquipment();
        var equipment = CreateEquipment();
        equipment.InventoryNumber = number;
        // Act
        var errors = service.Save(equipment);
        // Assert
        Assert.Contains("Оборудование с таким инвентарным номером уже существует.", errors);
        Assert.Equal(before.Select(item => item.InventoryNumber), service.GetEquipment().Select(item => item.InventoryNumber));
    }

    /// <summary>Настоящий фильтр компонента ищет имя без учёта регистра и крайних пробелов.</summary>
    [Theory]
    [InlineData("НАСОС")]
    [InlineData("  насос  ")]
    public void FilteredEquipment_NameQuery_FindsEquipmentIgnoringCase(string query)
    {
        // Arrange
        var component = new EquipmentList();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(EquipmentList).GetField("allEquipment", flags)!.SetValue(component, new EquipmentDataService().GetEquipment());
        typeof(EquipmentList).GetField("searchQuery", flags)!.SetValue(component, query);
        // Act
        var result = (IReadOnlyList<Equipment>)typeof(EquipmentList).GetProperty("FilteredEquipment", flags)!.GetValue(component)!;
        // Assert
        Assert.Equal("INV-1001", Assert.Single(result).InventoryNumber);
    }

    /// <summary>Создаёт независимые корректные данные для каждого теста.</summary>
    private static Equipment CreateEquipment() => new()
    {
        Name = "Насос",
        InventoryNumber = "TEST-001",
        Location = "Цех 1",
        Status = "Исправно"
    };
}
