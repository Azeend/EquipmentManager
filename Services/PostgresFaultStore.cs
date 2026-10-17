using System.Data.Common;
using System.Security.Claims;
using EquipmentManager.Models;
using Npgsql;
using NpgsqlTypes;

namespace EquipmentManager.Services;

/// <summary>Хранит заявки; история создаётся только существующим AFTER-триггером PostgreSQL.</summary>
public sealed class PostgresFaultStore(IStoreExecutor executor)
{
    // Политика profiles разрешает только собственный профиль: чужие display_name не запрашиваются.
    private const string Columns = "id, equipment_id, equipment_label, description, status, created_by::text AS author, created_at, started_at, completed_at, work_description, result, version";

    /// <summary>Возвращает очередь от новых заявок к старым.</summary>
    public Task<IReadOnlyList<Fault>> GetAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        executor.RunAsync(user, ["engineer", "maintenance_specialist", "manager"], (transaction, token) =>
            transaction.QueryAsync($"SELECT {Columns} FROM public.faults ORDER BY id DESC", [], Map, token), cancellationToken);

    /// <summary>Регистрирует неисправность существующего оборудования с фактическим автором серверного контекста.</summary>
    public async Task<FaultWriteResult> RegisterAsync(ClaimsPrincipal user, FaultRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        PostgresStoreExecutor.GetVerifiedUserId(user);
        if (registration.EquipmentId <= 0 || string.IsNullOrWhiteSpace(registration.Description))
        {
            return new(FaultWriteStatus.Invalid, null, ["Выберите оборудование и введите описание неисправности."]);
        }
        var equipmentId = registration.EquipmentId;
        var description = registration.Description.Trim();
        try
        {
            return await executor.RunAsync(user, ["engineer", "maintenance_specialist"], async (transaction, token) =>
            {
                var rows = await transaction.QueryAsync($"INSERT INTO public.faults (equipment_id, description, created_by) SELECT id, $2, $3 FROM public.equipment WHERE id = $1 RETURNING {Columns}",
                    [new(equipmentId, NpgsqlDbType.Integer), new(description, NpgsqlDbType.Text), new(transaction.UserId, NpgsqlDbType.Uuid)], Map, token);
                return rows.Count == 0 ? new FaultWriteResult(FaultWriteStatus.NotFound, null, ["Выбранное оборудование недоступно."])
                    : new FaultWriteResult(FaultWriteStatus.Saved, rows.Single(), []);
            }, cancellationToken);
        }
        catch (StoreException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
            && exception.ConstraintName == "faults_equipment_id_fkey")
        {
            return new(FaultWriteStatus.NotFound, null, ["Выбранное оборудование недоступно."]);
        }
    }

    /// <summary>Проверяет версию и переход; UPDATE и INSERT истории триггером коммитятся одной транзакцией.</summary>
    public Task<FaultWriteResult> ChangeStatusAsync(ClaimsPrincipal user, int id, int expectedVersion,
        string targetStatus, FaultCompletion? completion = null, CancellationToken cancellationToken = default)
    {
        PostgresStoreExecutor.GetVerifiedUserId(user);
        if (id <= 0 || expectedVersion <= 0)
        {
            return Task.FromResult(new FaultWriteResult(FaultWriteStatus.Invalid, null, ["Некорректный ID или версия заявки."]));
        }
        if (targetStatus is not ("В работе" or "Завершена"))
        {
            return Task.FromResult(new FaultWriteResult(FaultWriteStatus.InvalidTransition, null, ["Недопустимый переход статуса."]));
        }
        // Снимок DTO до await исключает изменение формы во время сетевого запроса.
        var work = completion?.WorkDescription?.Trim() ?? string.Empty;
        var result = completion?.Result?.Trim() ?? string.Empty;
        if (targetStatus == "Завершена" && (work.Length == 0 || result.Length == 0))
        {
            return Task.FromResult(new FaultWriteResult(FaultWriteStatus.Invalid, null, ["Укажите описание выполненных работ и результат."]));
        }
        return executor.RunAsync(user, ["maintenance_specialist"], async (transaction, token) =>
        {
            var current = (await transaction.QueryAsync($"SELECT {Columns} FROM public.faults WHERE id = $1",
                [new(id, NpgsqlDbType.Integer)], Map, token)).SingleOrDefault();
            if (current is null) { return new FaultWriteResult(FaultWriteStatus.NotFound, null, ["Заявка недоступна."]); }
            if (current.Version != expectedVersion)
            {
                return new FaultWriteResult(FaultWriteStatus.Conflict, null, ["Заявка изменена в другом сеансе. Обновите данные."]);
            }
            var expectedStatus = targetStatus == "В работе" ? "Новая" : "В работе";
            if (current.Status != expectedStatus)
            {
                return new FaultWriteResult(FaultWriteStatus.InvalidTransition, null, ["Разрешено: Новая → В работе → Завершена."]);
            }
            var parameters = new List<StoreParameter>
            {
                new(id, NpgsqlDbType.Integer), new(expectedVersion, NpgsqlDbType.Integer), new(transaction.UserId, NpgsqlDbType.Uuid)
            };
            var sql = $"UPDATE public.faults SET status = 'В работе', started_by = $3 WHERE id = $1 AND version = $2 AND status = 'Новая' RETURNING {Columns}";
            if (targetStatus == "Завершена")
            {
                parameters.Add(new(work, NpgsqlDbType.Text));
                parameters.Add(new(result, NpgsqlDbType.Text));
                sql = $"UPDATE public.faults SET status = 'Завершена', completed_by = $3, work_description = $4, result = $5 WHERE id = $1 AND version = $2 AND status = 'В работе' RETURNING {Columns}";
            }
            var updated = await transaction.QueryAsync(sql, parameters, Map, token);
            // Второй конкурентный UPDATE после ожидания блокировки перепроверяет version/status и не меняет строку.
            return updated.Count == 0 ? new FaultWriteResult(FaultWriteStatus.Conflict, null, ["Заявка уже изменена. Обновите данные."])
                : new FaultWriteResult(FaultWriteStatus.Saved, updated.Single(), []);
        }, cancellationToken);
    }

    private static Fault Map(DbDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        EquipmentId = reader.GetInt32(1),
        EquipmentLabel = reader.GetString(2),
        Description = reader.GetString(3),
        Status = reader.GetString(4),
        Author = reader.GetString(5),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        StartedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        CompletedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        WorkDescription = reader.GetString(9),
        Result = reader.GetString(10),
        Version = reader.GetInt32(11)
    };
}
