using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Npgsql;

namespace EquipmentManager.Services.Authentication;

/// <summary>Классифицирует ошибки профилей без записи сообщений исключений и данных подключения.</summary>
public static class ProfileStoreDiagnostics
{
    /// <summary>Записывает только этап, типы исключений и безопасную категорию сбоя.</summary>
    public static void LogFailure(ILogger logger, string stage, Exception exception)
    {
        var causes = new List<Exception>();
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException) { causes.Add(cause); }
        var category = causes.Any(cause => cause is AuthenticationException or CryptographicException)
            ? "tls_or_certificate"
            : causes.Any(cause => cause is SocketException socket && socket.SocketErrorCode is
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.NoRecovery)
                ? "dns"
                : causes.Any(cause => cause is TimeoutException
                    || cause is SocketException socket && socket.SocketErrorCode == SocketError.TimedOut)
                    ? "timeout"
                    : causes.Any(cause => cause is PostgresException) ? "database" : "other_connection";
        // Исключение намеренно не передаётся ILogger: Message/ToString могут содержать секреты и данные профиля.
        logger.LogError(new EventId(3001, "ProfileStoreUnavailable"),
            "Событие: {Event}; источник ролей недоступен. Этап: {Stage}; категория: {Category}; тип: {ErrorType}; внутренний тип: {InnerErrorType}; корневой тип: {RootErrorType}",
            "ProfileStoreUnavailable", stage, category, exception.GetType().Name, exception.InnerException?.GetType().Name ?? "none", causes[^1].GetType().Name);
    }
}
