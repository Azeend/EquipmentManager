using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace EquipmentManager.Services;

/// <summary>Отдельный Npgsql-пул equipment_api; не регистрируется вместо работающих сервисов автоматически.</summary>
public sealed class PostgresStoreExecutor : IStoreExecutor, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly ILogger<PostgresStoreExecutor> logger;

    /// <summary>Создаёт ленивый пул из явно переданной защищённой строки; секреты из конфигурации сам не читает.</summary>
    public PostgresStoreExecutor(string connectionString, ILogger<PostgresStoreExecutor> logger)
    {
        ValidateConnectionString(connectionString);
        this.logger = logger;
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false };
        dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>Допускает только equipment_api с проверкой сертификата и сбросом пула.</summary>
    public static void ValidateConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) { throw new InvalidOperationException("Нужно отдельное подключение CRUD equipment_api."); }
        NpgsqlConnectionStringBuilder settings;
        try { settings = new NpgsqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("Некорректные параметры отдельного подключения CRUD."); }
        if (settings.Username?.Split('.')[0] != "equipment_api" || settings.SslMode != SslMode.VerifyFull
            || settings.Database != "postgres" || settings.NoResetOnClose || settings.Multiplexing
            || settings.IncludeErrorDetail || settings.PersistSecurityInfo)
        {
            throw new InvalidOperationException("Требуется equipment_api, база postgres, SSL Mode=VerifyFull и безопасные настройки пула.");
        }
    }

    /// <summary>Извлекает единственный непустой sub из аутентифицированной сервером identity.</summary>
    public static Guid GetVerifiedUserId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var subjects = user.Identities.Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.FindAll("sub")).Select(claim => claim.Value).Distinct().ToArray();
        if (subjects.Length != 1 || !Guid.TryParse(subjects[0], out var userId) || userId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Требуется проверенный пользовательский сеанс.");
        }
        return userId;
    }

    /// <inheritdoc />
    public async Task<T> RunAsync<T>(ClaimsPrincipal user, IReadOnlyList<string> allowedRoles,
        Func<IStoreTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        var userId = GetVerifiedUserId(user);
        var commitStarted = false;
        var stage = "open_connection";
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            stage = "begin_transaction";
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var commands = new TransactionCommands(connection, transaction, userId);
            // Проверяется действительная login-роль, а не только Username строки (включая Supavisor).
            stage = "verify_login";
            var login = await commands.QueryAsync("SELECT session_user::text, current_user::text, r.rolsuper, r.rolbypassrls FROM pg_catalog.pg_roles r WHERE r.rolname = current_user",
                [], reader => (Login: reader.GetString(0), Current: reader.GetString(1), Super: reader.GetBoolean(2), Bypass: reader.GetBoolean(3)), cancellationToken);
            if (login.Count != 1 || login[0].Login != "equipment_api" || login[0].Current != "equipment_api"
                || login[0].Super || login[0].Bypass) { throw new StoreException(StoreFailure.Forbidden); }
            stage = "set_user_context";
            await commands.QueryAsync("SELECT pg_catalog.set_config('equipment.user_id', $1, true)",
                [new(userId.ToString(), NpgsqlDbType.Text)], reader => reader.GetString(0), cancellationToken);
            stage = "check_permissions";
            var permissions = await commands.QueryAsync("SELECT equipment_api_security.has_any_role($1)",
                [new(allowedRoles.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)], reader => reader.GetBoolean(0), cancellationToken);
            if (permissions.Count != 1 || !permissions[0]) { throw new StoreException(StoreFailure.Forbidden); }
            stage = "execute_operation";
            var result = await operation(commands, cancellationToken);
            stage = "commit";
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken);
            return result;
            // DisposeAsync откатывает незавершённую транзакцию; ошибки не подавляются.
        }
        catch (PostgresException exception)
        {
            logger.LogWarning("PostgreSQL отклонил операцию хранилища. Этап: {Stage}; SQLSTATE: {SqlState}", stage, exception.SqlState);
            throw new StoreException(exception.SqlState == PostgresErrorCodes.InsufficientPrivilege
                ? StoreFailure.Forbidden : StoreFailure.Database, exception.SqlState, exception.ConstraintName);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException
            || exception is OperationCanceledException && commitStarted)
        {
            logger.LogWarning("Сбой хранилища. Этап: {Stage}; тип: {ErrorType}; COMMIT начат: {CommitStarted}", stage, exception.GetType().Name, commitStarted);
            throw new StoreException(commitStarted ? StoreFailure.OutcomeUnknown : StoreFailure.Unavailable);
        }
    }

    /// <summary>Освобождает отдельный пул при завершении будущего PostgreSQL-режима.</summary>
    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private sealed class TransactionCommands(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId) : IStoreTransaction
    {
        public Guid UserId { get; } = userId;

        public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyList<StoreParameter> parameters,
            Func<DbDataReader, T> map, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = parameter.Type, Value = parameter.Value ?? DBNull.Value });
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<T>();
            while (await reader.ReadAsync(cancellationToken)) { result.Add(map(reader)); }
            return result;
        }
    }
}
