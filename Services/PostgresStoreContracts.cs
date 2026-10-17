using System.Data.Common;
using System.Security.Claims;
using EquipmentManager.Models;

namespace EquipmentManager.Services;

/// <summary>Параметр SQL с явным PostgreSQL-типом, включая NULL.</summary>
public sealed record StoreParameter(object? Value, NpgsqlTypes.NpgsqlDbType Type);

/// <summary>Команды одной транзакции с уже установленным проверенным пользователем.</summary>
public interface IStoreTransaction
{
    /// <summary>UUID из проверенного principal, а не из DTO.</summary>
    Guid UserId { get; }
    /// <summary>Исполняет параметризованный запрос и полностью считывает результат до следующей команды.</summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyList<StoreParameter> parameters,
        Func<DbDataReader, T> map, CancellationToken cancellationToken);
}

/// <summary>Одна транзакция на операцию: контекст, права, команды, COMMIT либо откат.</summary>
public interface IStoreExecutor
{
    /// <summary>Передаёт в команды только UUID аутентифицированного пользователя и проверяет актуальные роли.</summary>
    Task<T> RunAsync<T>(ClaimsPrincipal user, IReadOnlyList<string> allowedRoles,
        Func<IStoreTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
}

/// <summary>Причина отказа постоянного хранилища без раскрытия SQL, значений и строки подключения.</summary>
public enum StoreFailure
{
    /// <summary>Нет разрешения активного профиля.</summary>
    Forbidden,
    /// <summary>Ошибка ограничения PostgreSQL; SqlState/ConstraintName доступны для точной классификации.</summary>
    Database,
    /// <summary>Источник недоступен, бизнес-результат не возвращается как пустая коллекция.</summary>
    Unavailable,
    /// <summary>Соединение/отмена во время COMMIT: исход нужно сверить, автоматически повторять запись нельзя.</summary>
    OutcomeUnknown
}

/// <summary>Безопасное исключение хранилища; исходное исключение с данными БД не переносится наружу.</summary>
public sealed class StoreException(StoreFailure failure, string? sqlState = null, string? constraintName = null)
    : Exception("Операция постоянного хранилища не выполнена; требуется обработка причины отказа.")
{
    /// <summary>Тип отказа.</summary>
    public StoreFailure Failure { get; } = failure;
    /// <summary>Машинный SQLSTATE без текста PostgreSQL-ошибки.</summary>
    public string? SqlState { get; } = sqlState;
    /// <summary>Имя ограничения для точного различения бизнес-конфликта.</summary>
    public string? ConstraintName { get; } = constraintName;
}

/// <summary>Результат регистрации или смены статуса заявки.</summary>
public enum FaultWriteStatus
{
    /// <summary>Изменение сохранено.</summary>
    Saved,
    /// <summary>Некорректные обязательные поля.</summary>
    Invalid,
    /// <summary>Объект отсутствует или недоступен текущему пользователю.</summary>
    NotFound,
    /// <summary>Ожидаемая версия устарела.</summary>
    Conflict,
    /// <summary>Переход статуса запрещён.</summary>
    InvalidTransition
}

/// <summary>Сохранённая заявка либо причина бизнес-отказа.</summary>
public sealed record FaultWriteResult(FaultWriteStatus Status, Fault? Fault, IReadOnlyList<string> Errors);
