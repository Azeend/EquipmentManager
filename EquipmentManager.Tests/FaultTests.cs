using EquipmentManager.Models;
using EquipmentManager.Services;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет переходы заявок и целостность истории обслуживания.</summary>
public sealed class FaultTests
{
    /// <summary>Пропуск этапа обработки не меняет заявку и историю.</summary>
    [Fact]
    public void ChangeStatus_NewToCompleted_RejectsTransition()
    {
        // Arrange
        var service = new FaultDataService(new EquipmentDataService());
        var fault = service.GetFaults()[0];
        var completion = new FaultCompletion { WorkDescription = "Диагностика", Result = "Исправлено" };
        // Act
        var errors = service.ChangeStatus(fault.Id, fault.Version, "Завершена", completion);
        // Assert
        Assert.Contains("Недопустимый переход статуса", Assert.Single(errors));
        Assert.Equal(fault, service.GetFaults().Single(item => item.Id == fault.Id));
        Assert.Empty(service.GetMaintenanceHistory());
    }

    /// <summary>Успешное завершение создаёт одну связанную запись; повтор не создаёт дубль.</summary>
    [Fact]
    public void ChangeStatus_CompleteAndRepeat_CreatesExactlyOneHistoryRecord()
    {
        // Arrange
        var service = new FaultDataService(new EquipmentDataService());
        var fault = service.GetFaults()[0];
        Assert.Empty(service.ChangeStatus(fault.Id, fault.Version, "В работе"));
        fault = service.GetFaults().Single(item => item.Id == fault.Id);
        var completion = new FaultCompletion { WorkDescription = "Диагностика", Result = "Исправлено" };
        // Act
        var errors = service.ChangeStatus(fault.Id, fault.Version, "Завершена", completion);
        var completed = service.GetFaults().Single(item => item.Id == fault.Id);
        var repeatErrors = service.ChangeStatus(completed.Id, completed.Version, "Завершена", completion);
        // Assert
        Assert.Empty(errors);
        Assert.NotEmpty(repeatErrors);
        Assert.Equal("Завершена", completed.Status);
        var record = Assert.Single(service.GetMaintenanceHistory());
        Assert.Equal(fault.Id, record.FaultId);
        Assert.Equal(fault.EquipmentId, record.EquipmentId);
        Assert.Equal(fault.EquipmentLabel, record.EquipmentLabel);
        Assert.Equal(completion.WorkDescription, record.WorkDescription);
        Assert.Equal(completion.Result, record.Result);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(completed.CompletedAt.Value, record.PerformedAt);
    }
}
