using System.Security.Claims;
using EquipmentManager.Models;
using NpgsqlTypes;

namespace EquipmentManager.Services;

/// <summary>Читает неизменяемую историю; публичных методов её создания, изменения и удаления нет.</summary>
public sealed class PostgresMaintenanceStore(IStoreExecutor executor)
{
    /// <summary>Возвращает историю объекта или всего парка от новых записей к старым.</summary>
    public Task<IReadOnlyList<MaintenanceRecord>> GetAsync(ClaimsPrincipal user, int? equipmentId = null,
        CancellationToken cancellationToken = default) =>
        executor.RunAsync(user, ["engineer", "maintenance_specialist", "manager"], (transaction, token) =>
            transaction.QueryAsync("SELECT fault_id, equipment_id, equipment_label, performed_at, work_description, result FROM public.maintenance_records WHERE ($1::integer IS NULL OR equipment_id = $1) ORDER BY performed_at DESC, fault_id DESC",
                [new(equipmentId, NpgsqlDbType.Integer)], reader => new MaintenanceRecord
                {
                    FaultId = reader.GetInt32(0), EquipmentId = reader.GetInt32(1), EquipmentLabel = reader.GetString(2),
                    PerformedAt = reader.GetFieldValue<DateTimeOffset>(3), WorkDescription = reader.GetString(4), Result = reader.GetString(5)
                }, token), cancellationToken);
}
