using System.Security.Claims;
using EquipmentManager.Models;

namespace EquipmentManager.Services;

/// <summary>Единая точка выбора трёх источников для API; источник фиксируется DI при запуске.</summary>
public sealed class ApplicationDataService
{
    private readonly EquipmentDataService? memoryEquipment;
    private readonly FaultDataService? memoryFaults;
    private readonly PostgresEquipmentStore? postgresEquipment;
    private readonly PostgresFaultStore? postgresFaults;
    private readonly PostgresMaintenanceStore? postgresHistory;

    /// <summary>Использует только прежние сервисы памяти, общие для всего приложения.</summary>
    public ApplicationDataService(EquipmentDataService equipment, FaultDataService faults)
    {
        memoryEquipment = equipment;
        memoryFaults = faults;
    }

    /// <summary>Использует только постоянное хранение всех трёх сущностей.</summary>
    public ApplicationDataService(PostgresEquipmentStore equipment, PostgresFaultStore faults, PostgresMaintenanceStore history)
    {
        postgresEquipment = equipment;
        postgresFaults = faults;
        postgresHistory = history;
    }

    /// <summary>Читает реестр и применяет поиск.</summary>
    public Task<IReadOnlyList<Equipment>> GetEquipmentAsync(ClaimsPrincipal user, string? query, CancellationToken cancellationToken) =>
        postgresEquipment is not null ? postgresEquipment.GetAsync(user, query, cancellationToken)
            : Task.FromResult<IReadOnlyList<Equipment>>(memoryEquipment!.GetEquipment().Where(item =>
                string.IsNullOrWhiteSpace(query) || item.Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)
                    || item.InventoryNumber.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList());

    /// <summary>Читает одну единицу оборудования.</summary>
    public Task<Equipment?> GetEquipmentAsync(ClaimsPrincipal user, int id, CancellationToken cancellationToken) =>
        postgresEquipment is not null ? postgresEquipment.GetByIdAsync(user, id, cancellationToken)
            : Task.FromResult(memoryEquipment!.GetEquipment().FirstOrDefault(item => item.Id == id));

    /// <summary>Создаёт либо редактирует оборудование.</summary>
    public Task<EquipmentSaveResult> SaveEquipmentAsync(ClaimsPrincipal user, Equipment equipment, CancellationToken cancellationToken) =>
        postgresEquipment is not null ? postgresEquipment.SaveAsync(user, equipment, cancellationToken)
            : Task.FromResult(memoryEquipment!.SaveWithResult(equipment));

    /// <summary>Удаляет оборудование с проверкой связанных заявок и истории.</summary>
    public Task<EquipmentDeleteStatus> DeleteEquipmentAsync(ClaimsPrincipal user, int id, CancellationToken cancellationToken) =>
        postgresEquipment is not null ? postgresEquipment.DeleteAsync(user, id, cancellationToken)
            : Task.FromResult(memoryFaults!.DeleteEquipment(id));

    /// <summary>Читает очередь заявок.</summary>
    public Task<IReadOnlyList<Fault>> GetFaultsAsync(ClaimsPrincipal user, CancellationToken cancellationToken) =>
        postgresFaults is not null ? postgresFaults.GetAsync(user, cancellationToken) : Task.FromResult(memoryFaults!.GetFaults());

    /// <summary>Регистрирует заявку с фактическим автором.</summary>
    public Task<FaultWriteResult> RegisterFaultAsync(ClaimsPrincipal user, FaultRegistration registration, CancellationToken cancellationToken) =>
        postgresFaults is not null ? postgresFaults.RegisterAsync(user, registration, cancellationToken)
            : Task.FromResult(memoryFaults!.RegisterWithResult(registration, PostgresStoreExecutor.GetVerifiedUserId(user).ToString()));

    /// <summary>Изменяет статус с проверкой версии и без отдельного INSERT истории.</summary>
    public Task<FaultWriteResult> ChangeFaultAsync(ClaimsPrincipal user, int id, int expectedVersion,
        string status, FaultCompletion? completion, CancellationToken cancellationToken)
    {
        if (id <= 0 || expectedVersion <= 0)
        {
            return Task.FromResult(new FaultWriteResult(FaultWriteStatus.Invalid, null, ["Некорректный ID или версия заявки."]));
        }
        return postgresFaults is not null ? postgresFaults.ChangeStatusAsync(user, id, expectedVersion, status, completion, cancellationToken)
            : Task.FromResult(memoryFaults!.ChangeStatusWithResult(id, expectedVersion, status, completion));
    }

    /// <summary>Читает историю выбранного оборудования либо всего парка.</summary>
    public Task<IReadOnlyList<MaintenanceRecord>> GetHistoryAsync(ClaimsPrincipal user, int? equipmentId, CancellationToken cancellationToken) =>
        postgresHistory is not null ? postgresHistory.GetAsync(user, equipmentId, cancellationToken)
            : Task.FromResult(memoryFaults!.GetMaintenanceHistory(equipmentId));
}

/// <summary>Единый режим всех прикладных данных; профили авторизации используют прежний отдельный источник.</summary>
public enum StorageProvider
{
    /// <summary>Демонстрационные данные в памяти.</summary>
    InMemory,
    /// <summary>Постоянное хранение через ограниченный equipment_api.</summary>
    PostgreSQL
}

/// <summary>Выбранный при старте источник, доступный для пояснения режима в интерфейсе.</summary>
public sealed record StorageSettings(StorageProvider Provider);

/// <summary>Подключает один полный набор источников, не смешивая память и PostgreSQL.</summary>
public static class ApplicationStorageRegistration
{
    /// <summary>Настраивает DI без подключения к БД и без автоматического запуска миграций.</summary>
    public static IServiceCollection AddEquipmentStorage(this IServiceCollection services, IConfiguration configuration, IConfiguration credentials)
    {
        var value = configuration["Storage:Provider"] ?? nameof(StorageProvider.InMemory);
        if (!Enum.TryParse<StorageProvider>(value, true, out var mode) || !Enum.IsDefined(mode))
        {
            throw new InvalidOperationException("Storage:Provider должен быть InMemory или PostgreSQL.");
        }
        services.AddSingleton(new StorageSettings(mode));
        if (mode == StorageProvider.InMemory)
        {
            services.AddSingleton<EquipmentDataService>();
            services.AddSingleton<FaultDataService>();
            services.AddScoped(provider => new ApplicationDataService(provider.GetRequiredService<EquipmentDataService>(), provider.GetRequiredService<FaultDataService>()));
        }
        else
        {
            var connectionString = credentials.GetConnectionString("EquipmentCrud") ?? string.Empty;
            PostgresStoreExecutor.ValidateConnectionString(connectionString);
            services.AddSingleton<IStoreExecutor>(provider => new PostgresStoreExecutor(connectionString, provider.GetRequiredService<ILogger<PostgresStoreExecutor>>()));
            services.AddScoped<PostgresEquipmentStore>();
            services.AddScoped<PostgresFaultStore>();
            services.AddScoped<PostgresMaintenanceStore>();
            services.AddScoped(provider => new ApplicationDataService(provider.GetRequiredService<PostgresEquipmentStore>(),
                provider.GetRequiredService<PostgresFaultStore>(), provider.GetRequiredService<PostgresMaintenanceStore>()));
        }
        return services;
    }
}
