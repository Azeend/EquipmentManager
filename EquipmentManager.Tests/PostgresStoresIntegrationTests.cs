using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using EquipmentManager.Models;
using EquipmentManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace EquipmentManager.Tests;

/// <summary>Ручной opt-in: без явного включения запуск тестов не подключается к Supabase.</summary>
public sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    /// <summary>Разрешает обнаруженный тест только при явном подтверждении тестового проекта.</summary>
    public PostgresIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EM_PG_TEST_RUN") != "EquipmentManager-Test")
        {
            Skip = "Ручной PostgreSQL-тест выключен. Настройка: database/006_3_integration_test.md";
        }
    }
}

/// <summary>Исключает параллельный запуск сквозного теста вместе с другими тестовыми коллекциями.</summary>
[CollectionDefinition("PostgresIntegration", DisableParallelization = true)]
public sealed class PostgresIntegrationCollection;

/// <summary>Один реальный сценарий хранилищ с отдельным административным подключением только для точной очистки.</summary>
[Collection("PostgresIntegration")]
public sealed class PostgresStoresIntegrationTests(ITestOutputHelper output)
{
    /// <summary>Проверяет коммиты, повторное подключение, RLS, роли и одну триггерную запись истории.</summary>
    [PostgresIntegrationFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task EquipmentFaultMaintenance_PersistsAcrossConnections_AndEnforcesRoles()
    {
        var settings = Settings.Load(); // Никакой сети до полной проверки всех настроек и разрешения очистки.
        var marker = "LAB5-E2E-" + Guid.NewGuid().ToString("N");
        var manifestPath = Path.Combine(Path.GetTempPath(), "EquipmentManager-PgIntegration", marker + ".json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = timeout.Token;
        var cleanupNeeded = false;
        string? failure = null;
        var stage = "предварительная проверка подключений и профилей";
        try
        {
            await VerifyConnectionsAsync(settings, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                Marker = marker, settings.ProjectRef, settings.Engineer, settings.Specialist, settings.Manager
            }), cancellationToken);
            output.WriteLine("Метка только этого запуска: " + marker);
            output.WriteLine("Журнал восстановления без секретов: " + manifestPath);
            cleanupNeeded = true; // В том числе если ответ на первый COMMIT потерян: очистка ищет метку, не только возвращённый ID.
            int equipmentId;
            int faultId;
            stage = "создание оборудования и заявки";
            await using (var executor = Executor(settings))
            {
                var equipment = new PostgresEquipmentStore(executor);
                var faults = new PostgresFaultStore(executor);
                var saved = await equipment.SaveAsync(Principal(settings.Engineer), new Equipment
                {
                    Name = marker, InventoryNumber = marker, Location = "EquipmentManager-Test E2E", Status = "Исправно"
                }, cancellationToken);
                Assert.Equal(EquipmentSaveStatus.Saved, saved.Status);
                equipmentId = Assert.IsType<Equipment>(saved.Equipment).Id;
                var duplicate = await equipment.SaveAsync(Principal(settings.Engineer), new Equipment
                {
                    Name = marker, InventoryNumber = " " + marker.ToLowerInvariant() + " ",
                    Location = "EquipmentManager-Test E2E", Status = "Исправно"
                }, cancellationToken);
                Assert.Equal(EquipmentSaveStatus.Conflict, duplicate.Status);
                var registered = await faults.RegisterAsync(Principal(settings.Engineer),
                    new FaultRegistration { EquipmentId = equipmentId, Description = marker }, cancellationToken);
                Assert.Equal(FaultWriteStatus.Saved, registered.Status);
                var fault = Assert.IsType<Fault>(registered.Fault);
                faultId = fault.Id;
                Assert.Equal("Новая", fault.Status);
                Assert.Equal(1, fault.Version);
                Assert.Equal(settings.Engineer.ToString(), fault.Author);

                stage = "запреты ролей в хранилищах и непосредственно в RLS";
                await ForbiddenAsync(() => equipment.SaveAsync(Principal(settings.Manager), saved.Equipment!, cancellationToken));
                await ForbiddenAsync(() => faults.RegisterAsync(Principal(settings.Manager),
                    new FaultRegistration { EquipmentId = equipmentId, Description = marker }, cancellationToken));
                await ForbiddenAsync(() => faults.ChangeStatusAsync(Principal(settings.Engineer), faultId, 1, "В работе", cancellationToken: cancellationToken));
                // Эти запросы обходят прикладную проверку ролей, чтобы проверить именно PostgreSQL RLS.
                await DeniedSqlAsync(settings, settings.Manager,
                    "INSERT INTO public.equipment(name, inventory_number, location, status) VALUES ($1, $1, 'Test', 'Исправно')", [marker + "-DENIED"], cancellationToken);
                await DeniedSqlAsync(settings, settings.Specialist,
                    "INSERT INTO public.equipment(name, inventory_number, location, status) VALUES ($1, $1, 'Test', 'Исправно')", [marker + "-DENIED"], cancellationToken);
                await DeniedSqlAsync(settings, settings.Manager,
                    "INSERT INTO public.faults(equipment_id, description, created_by) VALUES ($1, $2, $3)", [equipmentId, marker, settings.Manager], cancellationToken);
                await DeniedSqlAsync(settings, settings.Engineer,
                    "UPDATE public.faults SET status = 'В работе', started_by = $2 WHERE id = $1", [faultId, settings.Engineer], cancellationToken);
                await DeniedSqlAsync(settings, settings.Specialist,
                    "INSERT INTO public.faults(equipment_id, description, created_by) VALUES ($1, $2, $3)", [equipmentId, marker, settings.Engineer], cancellationToken);

                stage = "переходы статусов и триггерное завершение";
                var completion = new FaultCompletion { WorkDescription = marker + " work", Result = marker + " result" };
                Assert.Equal(FaultWriteStatus.InvalidTransition,
                    (await faults.ChangeStatusAsync(Principal(settings.Specialist), faultId, 1, "Завершена", completion, cancellationToken)).Status);
                var started = await faults.ChangeStatusAsync(Principal(settings.Specialist), faultId, 1, "В работе", cancellationToken: cancellationToken);
                Assert.Equal(FaultWriteStatus.Saved, started.Status);
                Assert.Equal(2, started.Fault!.Version);
                Assert.NotNull(started.Fault.StartedAt);
                Assert.Equal(FaultWriteStatus.Conflict,
                    (await faults.ChangeStatusAsync(Principal(settings.Specialist), faultId, 1, "Завершена", completion, cancellationToken)).Status);
                var completed = await faults.ChangeStatusAsync(Principal(settings.Specialist), faultId, 2, "Завершена", completion, cancellationToken);
                Assert.Equal(FaultWriteStatus.Saved, completed.Status);
                Assert.Equal(3, completed.Fault!.Version);
                Assert.NotNull(completed.Fault.CompletedAt);
                Assert.Equal(FaultWriteStatus.InvalidTransition,
                    (await faults.ChangeStatusAsync(Principal(settings.Specialist), faultId, 3, "Завершена", completion, cancellationToken)).Status);
                Assert.Equal(EquipmentDeleteStatus.Linked, await equipment.DeleteAsync(Principal(settings.Engineer), equipmentId, cancellationToken));
            } // Первый пул полностью уничтожен; Pooling=false также исключает повторное использование его соединения.

            stage = "повторное подключение и чтение сохранённых данных";
            await using (var executor = Executor(settings))
            {
                var equipment = await new PostgresEquipmentStore(executor).GetByIdAsync(Principal(settings.Manager), equipmentId, cancellationToken);
                Assert.Equal(marker, equipment!.InventoryNumber);
                var fault = Assert.Single(await new PostgresFaultStore(executor).GetAsync(Principal(settings.Manager), cancellationToken), item => item.Id == faultId);
                Assert.Equal("Завершена", fault.Status);
                Assert.Equal(3, fault.Version);
                var history = await new PostgresMaintenanceStore(executor).GetAsync(Principal(settings.Manager), equipmentId, cancellationToken);
                var record = Assert.Single(history);
                Assert.Equal(faultId, record.FaultId);
                Assert.Equal(equipmentId, record.EquipmentId);
                Assert.Equal(fault.EquipmentLabel, record.EquipmentLabel);
                Assert.Equal(fault.CompletedAt!.Value, record.PerformedAt);
                Assert.Equal(TimeSpan.Zero, record.PerformedAt.Offset);
                Assert.Equal(marker + " work", record.WorkDescription);
                Assert.Equal(marker + " result", record.Result);
            }
            stage = "отсутствие контекста и запрет прямой записи истории";
            await VerifyNoContextAsync(settings, equipmentId, cancellationToken);
            await DeniedSqlAsync(settings, null,
                "INSERT INTO public.equipment(name, inventory_number, location, status) VALUES ($1, $1, 'Test', 'Исправно')", [marker + "-DENIED"], cancellationToken);
            await DeniedSqlAsync(settings, settings.Specialist,
                "INSERT INTO public.maintenance_records(fault_id, equipment_id, equipment_label, performed_at, work_description, result, performed_by) SELECT id, equipment_id, equipment_label, completed_at, work_description, result, completed_by FROM public.faults WHERE id = $1", [faultId], cancellationToken);
            await DeniedSqlAsync(settings, settings.Specialist, "DELETE FROM public.faults WHERE id = $1", [faultId], cancellationToken);
        }
        catch (Exception exception) { failure = SafeFailure(stage, exception); }
        finally
        {
            if (cleanupNeeded)
            {
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await CleanupAsync(settings, marker, cleanupTimeout.Token);
                    File.Delete(manifestPath);
                    output.WriteLine("Записи только этого запуска удалены; триггер истории восстановлен.");
                }
                catch (Exception exception)
                {
                    failure = (failure is null ? "" : failure + " ") + SafeFailure("очистка", exception)
                        + " Сохранён журнал восстановления: " + manifestPath;
                }
            }
        }
        if (failure is not null) { throw new XunitException(failure); } // Без исходных exception/connection string/token.
    }

    private static PostgresStoreExecutor Executor(Settings settings) =>
        new(settings.CrudConnection, NullLogger<PostgresStoreExecutor>.Instance);

    // Тестирует границу хранилищ с выделенными UUID. Не подменяет и не проверяет JWT-аутентификацию приложения.
    private static ClaimsPrincipal Principal(Guid id) => new(new ClaimsIdentity([new Claim("sub", id.ToString())], "PostgresIntegration"));

    private static async Task ForbiddenAsync<T>(Func<Task<T>> operation)
    {
        var exception = await Assert.ThrowsAsync<StoreException>(async () => { await operation(); });
        Assert.Equal(StoreFailure.Forbidden, exception.Failure);
    }

    private static string SafeFailure(string stage, Exception exception) =>
        $"Этап «{stage}»: {exception.GetType().Name}"
        + (exception is PostgresException postgres ? "; SQLSTATE=" + postgres.SqlState : "")
        + ". Секреты и исходный текст исключения скрыты.";

    private static async Task VerifyConnectionsAsync(Settings settings, CancellationToken cancellationToken)
    {
        await using (var connection = new NpgsqlConnection(settings.CrudConnection))
        {
            await connection.OpenAsync(cancellationToken);
            await using var identity = new NpgsqlCommand("SELECT session_user::text, current_user::text, rolsuper, rolbypassrls FROM pg_catalog.pg_roles WHERE rolname = current_user", connection);
            await using (var reader = await identity.ExecuteReaderAsync(cancellationToken))
            {
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal("equipment_api", reader.GetString(0));
                Assert.Equal("equipment_api", reader.GetString(1));
                Assert.False(reader.GetBoolean(2));
                Assert.False(reader.GetBoolean(3));
            }
            foreach (var (id, role) in new[] { (settings.Engineer, "engineer"), (settings.Specialist, "maintenance_specialist"), (settings.Manager, "manager") })
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await SetContextAsync(connection, transaction, id, cancellationToken);
                await using var command = new NpgsqlCommand("SELECT roles, is_active FROM public.profiles WHERE id = $1", connection, transaction);
                command.Parameters.AddWithValue(id);
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    Assert.True(await reader.ReadAsync(cancellationToken));
                    Assert.Equal([role], reader.GetFieldValue<string[]>(0));
                    Assert.True(reader.GetBoolean(1));
                }
                await transaction.RollbackAsync(cancellationToken);
            }
        }
        await using var owner = new NpgsqlConnection(settings.CleanupConnection);
        await owner.OpenAsync(cancellationToken);
        await VerifyCleanupOwnerAsync(owner, null, cancellationToken);
    }

    private static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.set_config('equipment.user_id', $1, true)", connection, transaction);
        command.Parameters.AddWithValue(id.ToString());
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task DeniedSqlAsync(Settings settings, Guid? userId, string sql, object[] parameters, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.CrudConnection);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (userId.HasValue) { await SetContextAsync(connection, transaction, userId.Value, cancellationToken); }
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var parameter in parameters) { command.Parameters.Add(new NpgsqlParameter { Value = parameter }); }
            try { Assert.Equal(0, await command.ExecuteNonQueryAsync(cancellationToken)); }
            catch (PostgresException exception) { Assert.Equal("42501", exception.SqlState); }
        }
        finally { await transaction.RollbackAsync(CancellationToken.None); } // Даже неожиданный успех не коммитит отрицательную фикстуру.
    }

    private static async Task VerifyNoContextAsync(Settings settings, int equipmentId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.CrudConnection);
        await connection.OpenAsync(cancellationToken);
        await using var context = new NpgsqlCommand("SELECT nullif(current_setting('equipment.user_id', true), '') IS NULL", connection);
        Assert.Equal(true, await context.ExecuteScalarAsync(cancellationToken));
        foreach (var sql in new[]
        {
            "SELECT count(*) FROM public.equipment WHERE id = $1",
            "SELECT count(*) FROM public.faults WHERE equipment_id = $1",
            "SELECT count(*) FROM public.maintenance_records WHERE equipment_id = $1"
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(equipmentId);
            Assert.Equal(0L, await command.ExecuteScalarAsync(cancellationToken));
        }
    }

    private static async Task VerifyCleanupOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT session_user = 'postgres' AND current_user = 'postgres'
              AND (SELECT count(*) FROM pg_catalog.pg_class
                   WHERE oid IN ('public.equipment'::regclass, 'public.faults'::regclass, 'public.maintenance_records'::regclass)
                     AND relowner = current_user::regrole AND NOT relforcerowsecurity) = 3
              AND EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                   WHERE tgrelid = 'public.maintenance_records'::regclass AND tgname = 'maintenance_records_guard'
                     AND tgfoid = 'public.guard_maintenance_record()'::regprocedure AND tgenabled = 'O')
            """, connection, transaction);
        Assert.Equal(true, await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task CleanupAsync(Settings settings, string marker, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.CleanupConnection);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", [], connection, transaction, cancellationToken);
        // ACCESS EXCLUSIVE удерживается до COMMIT/ROLLBACK: другие сессии не видят временно отключённого guard.
        await ExecuteAsync("LOCK TABLE public.equipment, public.faults, public.maintenance_records IN ACCESS EXCLUSIVE MODE", [], connection, transaction, cancellationToken);
        await VerifyCleanupOwnerAsync(connection, transaction, cancellationToken);
        await using var find = new NpgsqlCommand("SELECT id, name, location FROM public.equipment WHERE inventory_number = $1", connection, transaction);
        find.Parameters.AddWithValue(marker);
        int equipmentId;
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) { await reader.DisposeAsync(); await transaction.RollbackAsync(cancellationToken); return; }
            equipmentId = reader.GetInt32(0);
            Assert.Equal(marker, reader.GetString(1));
            Assert.Equal("EquipmentManager-Test E2E", reader.GetString(2));
            Assert.False(await reader.ReadAsync(cancellationToken));
        }
        await using var validate = new NpgsqlCommand("""
            SELECT NOT EXISTS (SELECT 1 FROM public.faults WHERE equipment_id = $1
                AND (description <> $2 OR created_by <> $3 OR (started_by IS NOT NULL AND started_by <> $4)
                     OR (completed_by IS NOT NULL AND completed_by <> $4)))
              AND NOT EXISTS (SELECT 1 FROM public.maintenance_records WHERE equipment_id = $1 AND performed_by <> $4)
            """, connection, transaction);
        validate.Parameters.AddWithValue(equipmentId);
        validate.Parameters.AddWithValue(marker);
        validate.Parameters.AddWithValue(settings.Engineer);
        validate.Parameters.AddWithValue(settings.Specialist);
        Assert.Equal(true, await validate.ExecuteScalarAsync(cancellationToken)); // При чужих данных прекращаем очистку до изменения trigger.
        await ExecuteAsync("ALTER TABLE public.maintenance_records DISABLE TRIGGER maintenance_records_guard", [], connection, transaction, cancellationToken);
        await ExecuteAsync("DELETE FROM public.maintenance_records WHERE equipment_id = $1", [equipmentId], connection, transaction, cancellationToken);
        await ExecuteAsync("DELETE FROM public.faults WHERE equipment_id = $1 AND description = $2 AND created_by = $3", [equipmentId, marker, settings.Engineer], connection, transaction, cancellationToken);
        await ExecuteAsync("DELETE FROM public.equipment WHERE id = $1 AND inventory_number = $2", [equipmentId, marker], connection, transaction, cancellationToken);
        await ExecuteAsync("ALTER TABLE public.maintenance_records ENABLE TRIGGER maintenance_records_guard", [], connection, transaction, cancellationToken);
        await using var verifyEmpty = new NpgsqlCommand("""
            SELECT NOT EXISTS (SELECT 1 FROM public.equipment WHERE id = $1 OR inventory_number = $2)
              AND NOT EXISTS (SELECT 1 FROM public.faults WHERE equipment_id = $1)
              AND NOT EXISTS (SELECT 1 FROM public.maintenance_records WHERE equipment_id = $1)
            """, connection, transaction);
        verifyEmpty.Parameters.AddWithValue(equipmentId);
        verifyEmpty.Parameters.AddWithValue(marker);
        Assert.Equal(true, await verifyEmpty.ExecuteScalarAsync(cancellationToken));
        await VerifyCleanupOwnerAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(string sql, object[] parameters, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 20 };
        foreach (var parameter in parameters) { command.Parameters.Add(new NpgsqlParameter { Value = parameter }); }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class Settings
    {
        // Публичный идентификатор основного проекта из текущего appsettings.json: дополнительный запрет основной базы.
        private const string MainProjectRef = "cyzanurmyswswbicjiaf";
        internal string ProjectRef { get; private init; } = "";
        internal string CrudConnection { get; private init; } = "";
        internal string CleanupConnection { get; private init; } = "";
        internal Guid Engineer { get; private init; }
        internal Guid Specialist { get; private init; }
        internal Guid Manager { get; private init; }

        internal static Settings Load()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("EM_PG_TEST_RUN") != "EquipmentManager-Test")
                {
                    throw new InvalidOperationException("Ручной запуск не подтверждён.");
                }
                string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                    ? value : throw new InvalidOperationException("Отсутствует обязательная настройка теста.");
                var projectRef = Required("EM_PG_TEST_PROJECT_REF");
                if (!Regex.IsMatch(projectRef, "^[a-z0-9]{20}$") || projectRef == MainProjectRef
                    || Required("EM_PG_TEST_ALLOW_CLEANUP") != "EquipmentManager-Test only")
                {
                    throw new InvalidOperationException("Тестовый проект или разрешение точной очистки не подтверждены.");
                }
                var settings = new Settings
                {
                    ProjectRef = projectRef,
                    CrudConnection = Connection(Required("EM_PG_TEST_CRUD_CONNECTION"), projectRef, "equipment_api"),
                    CleanupConnection = Connection(Required("EM_PG_TEST_CLEANUP_CONNECTION"), projectRef, "postgres"),
                    Engineer = Guid.Parse(Required("EM_PG_TEST_ENGINEER_ID")),
                    Specialist = Guid.Parse(Required("EM_PG_TEST_SPECIALIST_ID")),
                    Manager = Guid.Parse(Required("EM_PG_TEST_MANAGER_ID"))
                };
                if (new[] { settings.Engineer, settings.Specialist, settings.Manager }.Contains(Guid.Empty)
                    || new[] { settings.Engineer, settings.Specialist, settings.Manager }.Distinct().Count() != 3)
                {
                    throw new InvalidOperationException("Требуются три разных тестовых пользователя.");
                }
                PostgresStoreExecutor.ValidateConnectionString(settings.CrudConnection);
                return settings;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
            {
                throw new XunitException("Настройки интеграционного теста не приняты. Сверьте database/006_3_integration_test.md. Значения скрыты.");
            }
        }

        private static string Connection(string value, string projectRef, string role)
        {
            var builder = new NpgsqlConnectionStringBuilder(value);
            var direct = string.Equals(builder.Host, $"db.{projectRef}.supabase.co", StringComparison.OrdinalIgnoreCase) && builder.Username == role;
            var sessionPooler = builder.Host?.EndsWith(".pooler.supabase.com", StringComparison.OrdinalIgnoreCase) == true && builder.Username == role + "." + projectRef;
            if ((!direct && !sessionPooler) || builder.Port != 5432 || builder.Database != "postgres"
                || builder.SslMode != SslMode.VerifyFull || builder.IncludeErrorDetail || builder.PersistSecurityInfo
                || builder.NoResetOnClose || builder.Multiplexing)
            {
                throw new InvalidOperationException("Разрешён только подтверждённый Test project: TLS VerifyFull, порт 5432, отдельные login.");
            }
            builder.Pooling = false;
            builder.Enlist = false;
            builder.Timeout = 10;
            builder.CommandTimeout = 15;
            return builder.ConnectionString;
        }
    }
}
