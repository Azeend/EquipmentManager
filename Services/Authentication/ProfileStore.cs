using Npgsql;

namespace EquipmentManager.Services.Authentication;

/// <summary>Серверный профиль, прочитанный по проверенному UUID, а не из claims ролей.</summary>
public sealed record ServerProfile(bool IsActive, IReadOnlyList<string> Roles);

/// <summary>Отличает отсутствие профиля от недоступности источника.</summary>
public sealed record ProfileLookup(bool Available, ServerProfile? Profile);

/// <summary>Читает доверенные назначения ролей.</summary>
public interface IProfileStore
{
    /// <summary>Возвращает профиль проверенного пользователя либо отказ источника.</summary>
    Task<ProfileLookup> GetAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Читает только roles/is_active из PostgreSQL отдельным серверным подключением.</summary>
public sealed class PostgresProfileStore(NpgsqlDataSource? dataSource, ILogger<PostgresProfileStore> logger) : IProfileStore
{
    /// <inheritdoc />
    public async Task<ProfileLookup> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (dataSource is null)
        {
            logger.LogError("Не задано защищённое серверное подключение для чтения профилей.");
            return new ProfileLookup(false, null);
        }
        var stage = "open_connection";
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            stage = "prepare_query";
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT roles, is_active FROM public.profiles WHERE id = $1";
            command.Parameters.AddWithValue(userId);
            command.CommandTimeout = 5;
            stage = "execute_query";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            stage = "read_profile";
            if (!await reader.ReadAsync(cancellationToken)) { return new ProfileLookup(true, null); }
            var roles = reader.GetFieldValue<string[]>(0);
            return new ProfileLookup(true, new ServerProfile(reader.GetBoolean(1), roles));
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            ProfileStoreDiagnostics.LogFailure(logger, stage, exception);
            return new ProfileLookup(false, null);
        }
    }
}
