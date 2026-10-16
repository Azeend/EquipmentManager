using System.ComponentModel.DataAnnotations;

namespace EquipmentManager.Models;

/// <summary>Неизменяемый снимок заявки на устранение неисправности.</summary>
public sealed record Fault
{
    /// <summary>Номер заявки.</summary>
    public int Id { get; init; }
    /// <summary>Идентификатор оборудования.</summary>
    public int EquipmentId { get; init; }
    /// <summary>Название оборудования при регистрации.</summary>
    public string EquipmentLabel { get; init; } = string.Empty;
    /// <summary>Описание проблемы.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Состояние заявки.</summary>
    public string Status { get; init; } = "Новая";
    /// <summary>Автор; без авторизации личность не определяется.</summary>
    public string Author { get; init; } = "Не установлен (без авторизации)";
    /// <summary>Время регистрации в UTC.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Время начала работ в UTC.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>Время завершения в UTC.</summary>
    public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>Выполненные работы.</summary>
    public string WorkDescription { get; init; } = string.Empty;
    /// <summary>Результат работ.</summary>
    public string Result { get; init; } = string.Empty;
    /// <summary>Версия записи для проверки конкурентных изменений.</summary>
    public int Version { get; init; } = 1;
}

/// <summary>Поля регистрации неисправности.</summary>
public sealed class FaultRegistration
{
    /// <summary>Выбранное оборудование.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Выберите оборудование.")]
    public int EquipmentId { get; set; }
    /// <summary>Описание проблемы.</summary>
    [Required(ErrorMessage = "Введите описание неисправности.")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>Сведения для завершения заявки.</summary>
public sealed class FaultCompletion
{
    /// <summary>Обязательное описание работ.</summary>
    [Required(ErrorMessage = "Введите описание выполненных работ.")]
    public string WorkDescription { get; set; } = string.Empty;
    /// <summary>Обязательный результат.</summary>
    [Required(ErrorMessage = "Введите результат работ.")]
    public string Result { get; set; } = string.Empty;
}
