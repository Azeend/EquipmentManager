using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace EquipmentManager.Services.Monitoring;

/// <summary>Изолирует сетевую проверку PostgreSQL от Health Checks и тестовых подмен.</summary>
public interface IPostgresReadinessProbe
{
    /// <summary>Проверяет только доступность настроенных зависимостей, без чтения прикладных данных.</summary>
    Task CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Использует прежние защищённые строки подключения, сохраняя VerifyFull и доверенный CA.</summary>
public sealed class PostgresReadinessProbe(bool requireProfiles, bool requireCrud, string? profiles, string? crud) : IPostgresReadinessProbe
{
    /// <inheritdoc />
    public Task CheckAsync(CancellationToken cancellationToken)
    {
        var checks = new List<Task>();
        if (requireProfiles) { checks.Add(CheckConnectionAsync(profiles, cancellationToken)); }
        if (requireCrud) { checks.Add(CheckConnectionAsync(crud, cancellationToken)); }
        return Task.WhenAll(checks);
    }

    private static async Task CheckConnectionAsync(string? connectionString, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) { throw new InvalidOperationException("Не настроена зависимость готовности."); }
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 3,
            CommandTimeout = 2,
            CancellationTimeout = 1000,
            Enlist = false,
            Pooling = false
        };
        if (settings.SslMode != SslMode.VerifyFull) { throw new InvalidOperationException("Требуется проверяемое TLS-подключение."); }
        // Отдельные короткоживущие подключения не меняют рабочие пулы, роли или пользовательский контекст.
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = 2;
        if (await command.ExecuteScalarAsync(cancellationToken) is not int value || value != 1)
        { throw new InvalidOperationException("Зависимость не подтвердила готовность."); }
    }
}

/// <summary>Записывает только первое наблюдение и изменения состояния, без исключений и настроек подключения.</summary>
public sealed class HealthStateTransitions(ILogger<HealthStateTransitions> logger)
{
    private readonly Dictionary<string, HealthStatus> states = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>Атомарно публикует безопасное структурированное изменение состояния.</summary>
    public void Observe(string check, HealthStatus status, string category = "none", string errorType = "none")
    {
        lock (gate)
        {
            var previous = states.TryGetValue(check, out var old) ? old.ToString() : "Unknown";
            if (previous == status.ToString()) { return; }
            states[check] = status;
            logger.Log(status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning,
                new EventId(6001, "HealthStateChanged"),
                "Событие: {Event}; состояние проверки изменилось. Проверка: {Check}; раньше: {PreviousStatus}; сейчас: {Status}; категория: {Category}; тип: {ErrorType}",
                "HealthStateChanged", check, previous, status.ToString(), category, errorType);
        }
    }
}

/// <summary>Проверяет работающий процесс независимо от PostgreSQL; остановка делает его неготовым.</summary>
public sealed class ApplicationLivenessCheck(IHostApplicationLifetime lifetime, HealthStateTransitions transitions) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = lifetime.ApplicationStarted.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested
            ? HealthStatus.Healthy : HealthStatus.Unhealthy;
        transitions.Observe("application", status);
        return Task.FromResult(new HealthCheckResult(status));
    }
}

/// <summary>Ограничивает время сетевой проверки и объединяет параллельные запросы, кешируя результат на пять секунд.</summary>
public sealed class PostgresReadinessCheck(IPostgresReadinessProbe probe, HealthStateTransitions transitions, TimeProvider clock)
    : IHealthCheck, IDisposable
{
    /// <summary>Общий бюджет обеих PostgreSQL-зависимостей, включая подключение и запрос.</summary>
    public static TimeSpan ProbeTimeout => TimeSpan.FromSeconds(4);
    private readonly SemaphoreSlim gate = new(1, 1);
    private HealthCheckResult? cached;
    private DateTimeOffset validUntil;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is { } result && clock.GetUtcNow() < validUntil) { return result; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                // WaitAsync ограничивает ответ также для некорректной реализации, не соблюдающей отмену.
                await probe.CheckAsync(timeout.Token).WaitAsync(timeout.Token);
                cached = HealthCheckResult.Healthy();
                transitions.Observe("postgresql", HealthStatus.Healthy);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var category = exception switch
                {
                    OperationCanceledException or TimeoutException => "timeout",
                    NpgsqlException => "connection",
                    ArgumentException or InvalidOperationException => "configuration",
                    _ => "unexpected"
                };
                // Исходное исключение не передаётся результату Health Checks или ILogger.
                cached = HealthCheckResult.Unhealthy();
                transitions.Observe("postgresql", HealthStatus.Unhealthy, category, exception.GetType().Name);
            }
            validUntil = clock.GetUtcNow().AddSeconds(5);
            return cached!.Value;
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public void Dispose() => gate.Dispose();
}

/// <summary>Регистрирует встроенные Health Checks и минимальные публичные GET endpoints без сторонних пакетов.</summary>
public static class EquipmentHealthChecks
{
    /// <summary>Читает те же защищённые источники подключения, что и приложение; соединения при регистрации не открываются.</summary>
    public static IServiceCollection AddEquipmentHealthChecks(this IServiceCollection services, IConfiguration configuration, IConfiguration credentials)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IPostgresReadinessProbe>(new PostgresReadinessProbe(
            configuration.GetValue<bool>("SupabaseAuth:Enabled"),
            string.Equals(configuration["Storage:Provider"], "PostgreSQL", StringComparison.OrdinalIgnoreCase),
            credentials.GetConnectionString("Profiles"), credentials.GetConnectionString("EquipmentCrud")));
        services.AddSingleton<HealthStateTransitions>();
        services.AddSingleton<ApplicationLivenessCheck>();
        services.AddSingleton<PostgresReadinessCheck>();
        // Собственный журнал публикует только переходы; стандартный журнал не должен раскрывать произвольное исключение проверки.
        services.AddLogging(logging => logging.AddFilter("Microsoft.Extensions.Diagnostics.HealthChecks", LogLevel.None));
        services.AddHealthChecks()
            .AddCheck<ApplicationLivenessCheck>("application", tags: ["live", "ready"])
            .AddCheck<PostgresReadinessCheck>("postgresql", failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"], timeout: TimeSpan.FromSeconds(6));
        return services;
    }

    /// <summary>Публикует только общий статус; готовность не подтверждает права к прикладным таблицам или доступность OAuth.</summary>
    public static void MapEquipmentHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", Options("live")).AllowAnonymous()
            .WithMetadata(new Microsoft.AspNetCore.Routing.HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
        app.MapHealthChecks("/health/ready", Options("ready")).AllowAnonymous()
            .WithMetadata(new Microsoft.AspNetCore.Routing.HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
    }

    private static HealthCheckOptions Options(string tag) => new()
    {
        Predicate = registration => registration.Tags.Contains(tag),
        AllowCachingResponses = false,
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(
            new { status = report.Status == HealthStatus.Healthy ? "Healthy" : "Unhealthy" }, cancellationToken: context.RequestAborted)
    };
}
