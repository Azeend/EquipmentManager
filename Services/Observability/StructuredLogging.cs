using System.Text.Json;
using Microsoft.Extensions.Logging.Console;

namespace EquipmentManager.Services.Observability;

/// <summary>Настраивает единственный встроенный JSON Console provider и строгую политику Production.</summary>
public static class StructuredLogging
{
    internal const string StartupCategory = "EquipmentManager.Startup";
    private static readonly HashSet<string> ProductionCategories = new(StringComparer.Ordinal)
    {
        StartupCategory,
        "EquipmentManager.Services.Observability.SafeRequestLogging",
        "EquipmentManager.Services.Authentication.PostgresProfileStore",
        "EquipmentManager.Services.Authentication.SupabasePasswordClient",
        "EquipmentManager.Services.Authentication.UiAccountEndpoints",
        "EquipmentManager.Services.Authentication.ProfileAuthorizationResultHandler",
        "EquipmentManager.Services.PostgresStoreExecutor",
        "EquipmentManager.Services.EquipmentApiEndpoints.StorageErrorFilter",
        "EquipmentManager.Services.Monitoring.HealthStateTransitions",
        "EquipmentManager.Components.MetrikaTracker",
        "EquipmentManager.Components.Pages.MaintenanceHistory",
        "Yandex.OAuth",
        "Yandex.Userinfo"
    };

    /// <summary>Пишет одну JSON-строку на событие; Error/Critical идут в stderr, Information/Warning — в stdout.</summary>
    public static ILoggingBuilder AddEquipmentJsonConsole(this ILoggingBuilder logging, bool production)
    {
        logging.ClearProviders();
        logging.AddJsonConsole();
        logging.SetMinimumLevel(LogLevel.Information);
        logging.Services.PostConfigure<ConsoleLoggerOptions>(options =>
        {
            options.FormatterName = ConsoleFormatterNames.Json;
            options.LogToStandardErrorThreshold = LogLevel.Error;
        });
        logging.Services.PostConfigure<JsonConsoleFormatterOptions>(options =>
        {
            options.IncludeScopes = false;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
        });
        if (production)
        {
            // Правила окружения не должны повторно включить HTTP/query, JWT, Npgsql или circuit exceptions.
            logging.Services.PostConfigure<LoggerFilterOptions>(options =>
            {
                options.Rules.Clear();
                options.MinLevel = LogLevel.Information;
                options.Rules.Add(new LoggerFilterRule(null, null, LogLevel.Information,
                    (_, category, level) => category is not null && ProductionCategories.Contains(category)
                        && level is >= LogLevel.Information and <= LogLevel.Critical));
            });
        }
        return logging;
    }

    /// <summary>Публикует одно событие после завершения запуска, без адресов, путей и значений конфигурации.</summary>
    public static void LogEquipmentStartup(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(StartupCategory);
        app.Lifetime.ApplicationStarted.Register(() => ApplicationLogEvents.Started(logger, app.Environment.EnvironmentName));
    }
}

/// <summary>Фиксированные события жизненного цикла без сериализации исходных исключений.</summary>
public static class ApplicationLogEvents
{
    /// <summary>Записывает успешный запуск и нормализованное имя окружения.</summary>
    public static void Started(ILogger logger, string environment) => logger.LogInformation(new EventId(1000, "ApplicationStarted"),
        "Событие: {Event}; приложение: {Application}; окружение: {Environment}", "ApplicationStarted", "EquipmentManager",
        environment is "Production" or "Development" or "Staging" ? environment : "Other");

    /// <summary>Фиксирует фатальный сбой без Message/StackTrace/InnerException, которые могут содержать секреты.</summary>
    public static void Fatal(ILogger logger, Exception exception) => logger.LogCritical(new EventId(1002, "ApplicationFailed"),
        "Событие: {Event}; тип: {ErrorType}", "ApplicationFailed", exception.GetType().Name);
}

/// <summary>Журналирует только необработанный HTTP-сбой и повторно выбрасывает исходное исключение без изменения ответа.</summary>
public sealed class SafeRequestLogging(RequestDelegate next, ILogger<SafeRequestLogging> logger)
{
    /// <summary>Не журналирует успешные запросы, заголовки, query, формы, пользователя и обычную отмену запроса.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(new EventId(1001, "UnhandledRequestError"),
                "Событие: {Event}; вид запроса: {RequestKind}; тип: {ErrorType}; TraceId: {TraceId}",
                "UnhandledRequestError", RequestKind(context.Request.Path), exception.GetType().Name, context.TraceIdentifier);
            throw;
        }
    }

    private static string RequestKind(PathString path) => path.StartsWithSegments("/health") ? "health"
        : path.StartsWithSegments("/api") ? "api"
        : path == "/account/oauth/callback" ? "oauth_callback"
        : path.StartsWithSegments("/account") || path == "/login" ? "authentication"
        : path.StartsWithSegments("/_blazor") ? "interactive" : "page";
}
