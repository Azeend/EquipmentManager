namespace EquipmentManager.Models;

/// <summary>Команда обработки заявки; автор определяется сервером из проверенного JWT.</summary>
public sealed class FaultTransitionRequest
{
    /// <summary>Версия заявки, показанная пользователю.</summary>
    public int ExpectedVersion { get; set; }
    /// <summary>Работы для завершения заявки.</summary>
    public string? WorkDescription { get; set; }
    /// <summary>Результат для завершения заявки.</summary>
    public string? Result { get; set; }
}

/// <summary>Доступные действия интерфейса из существующих серверных политик.</summary>
public sealed record ApplicationPermissions(bool CanManageEquipment, bool CanRegisterFault, bool CanProcessFaults);
