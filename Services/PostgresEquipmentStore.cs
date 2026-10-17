using System.Data.Common;
using System.Security.Claims;
using EquipmentManager.Models;
using EquipmentManager.Validators;
using Npgsql;
using NpgsqlTypes;

namespace EquipmentManager.Services;

/// <summary>Асинхронное хранилище реестра; права пользователя проверяются в каждой транзакции и RLS.</summary>
public sealed class PostgresEquipmentStore(IStoreExecutor executor)
{
    private const string Columns = "id, name, inventory_number, location, status";

    /// <summary>Возвращает реестр с поиском по части имени или номера без учёта регистра.</summary>
    public Task<IReadOnlyList<Equipment>> GetAsync(ClaimsPrincipal user, string? query = null, CancellationToken cancellationToken = default) =>
        executor.RunAsync(user, ["engineer", "maintenance_specialist", "manager"], (transaction, token) =>
            transaction.QueryAsync($"SELECT {Columns} FROM public.equipment WHERE ($1::text = '' OR strpos(lower(name), lower($1)) > 0 OR strpos(normalized_inventory_number, lower($1)) > 0) ORDER BY id",
                [new(query?.Trim() ?? string.Empty, NpgsqlDbType.Text)], Map, token), cancellationToken);

    /// <summary>Возвращает запись либо null при отсутствии/невидимости согласно RLS.</summary>
    public Task<Equipment?> GetByIdAsync(ClaimsPrincipal user, int id, CancellationToken cancellationToken = default) =>
        executor.RunAsync(user, ["engineer", "maintenance_specialist", "manager"], async (transaction, token) =>
            (await transaction.QueryAsync($"SELECT {Columns} FROM public.equipment WHERE id = $1",
                [new(id, NpgsqlDbType.Integer)], Map, token)).SingleOrDefault(), cancellationToken);

    /// <summary>Создаёт/изменяет оборудование; конкурентный дубль окончательно отклоняет UNIQUE БД.</summary>
    public async Task<EquipmentSaveResult> SaveAsync(ClaimsPrincipal user, Equipment equipment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        PostgresStoreExecutor.GetVerifiedUserId(user);
        var candidate = new Equipment
        {
            Id = equipment.Id,
            Name = equipment.Name?.Trim() ?? string.Empty,
            InventoryNumber = equipment.InventoryNumber?.Trim() ?? string.Empty,
            Location = equipment.Location?.Trim() ?? string.Empty,
            Status = equipment.Status
        };
        var errors = new EquipmentValidator().Validate(candidate);
        if (candidate.Id < 0) { errors.Add("Некорректный идентификатор оборудования."); }
        if (errors.Count > 0) { return new(EquipmentSaveStatus.Invalid, null, errors); }
        try
        {
            return await executor.RunAsync(user, ["engineer"], async (transaction, token) =>
            {
                var parameters = new List<StoreParameter>
                {
                    new(candidate.Name, NpgsqlDbType.Text), new(candidate.InventoryNumber, NpgsqlDbType.Text),
                    new(candidate.Location, NpgsqlDbType.Text), new(candidate.Status, NpgsqlDbType.Text)
                };
                var sql = $"INSERT INTO public.equipment (name, inventory_number, location, status) VALUES ($1, $2, $3, $4) RETURNING {Columns}";
                if (candidate.Id != 0)
                {
                    parameters.Add(new(candidate.Id, NpgsqlDbType.Integer));
                    sql = $"UPDATE public.equipment SET name = $1, inventory_number = $2, location = $3, status = $4 WHERE id = $5 RETURNING {Columns}";
                }
                var rows = await transaction.QueryAsync(sql, parameters, Map, token);
                return rows.Count == 0 ? new EquipmentSaveResult(EquipmentSaveStatus.NotFound, null, ["Оборудование не найдено."])
                    : new EquipmentSaveResult(EquipmentSaveStatus.Saved, rows.Single(), []);
            }, cancellationToken);
        }
        catch (StoreException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation
            && exception.ConstraintName == "equipment_inventory_number_unique")
        {
            return new(EquipmentSaveStatus.Conflict, null, ["Оборудование с таким инвентарным номером уже существует."]);
        }
    }

    /// <summary>Удаляет только несвязанное оборудование; FK защищает и при конкурентной регистрации заявки.</summary>
    public async Task<EquipmentDeleteStatus> DeleteAsync(ClaimsPrincipal user, int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await executor.RunAsync(user, ["engineer"], async (transaction, token) =>
                (await transaction.QueryAsync("DELETE FROM public.equipment WHERE id = $1 RETURNING id",
                    [new(id, NpgsqlDbType.Integer)], reader => reader.GetInt32(0), token)).Count == 0
                        ? EquipmentDeleteStatus.NotFound : EquipmentDeleteStatus.Deleted, cancellationToken);
        }
        catch (StoreException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
            && exception.ConstraintName is "faults_equipment_id_fkey" or "maintenance_records_equipment_id_fkey")
        {
            return EquipmentDeleteStatus.Linked;
        }
    }

    private static Equipment Map(DbDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1),
        InventoryNumber = reader.GetString(2),
        Location = reader.GetString(3),
        Status = reader.GetString(4)
    };
}
