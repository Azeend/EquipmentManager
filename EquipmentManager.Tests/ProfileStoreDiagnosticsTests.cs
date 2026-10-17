using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using EquipmentManager.Services.Authentication;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет категории сбоев и отсутствие чувствительных данных в диагностике профилей.</summary>
public sealed class ProfileStoreDiagnosticsTests
{
    /// <summary>Цепочка InnerException классифицируется без вывода сообщений и экземпляра исключения.</summary>
    [Theory]
    [InlineData("tls", "tls_or_certificate", "AuthenticationException")]
    [InlineData("certificate", "tls_or_certificate", "CryptographicException")]
    [InlineData("dns", "dns", "SocketException")]
    [InlineData("timeout", "timeout", "TimeoutException")]
    [InlineData("socket_timeout", "timeout", "SocketException")]
    [InlineData("connection", "other_connection", "SocketException")]
    [InlineData("other", "other_connection", "IOException")]
    [InlineData("database", "database", "PostgresException")]
    public void Failure_LogsSafeCategoryAndTypes(string failure, string category, string rootType)
    {
        const string sensitive = "Password=secret-marker;JWT=private-token;user@example.invalid";
        Exception cause = failure switch
        {
            "tls" => new IOException(sensitive, new AuthenticationException(sensitive)),
            "certificate" => new CryptographicException(sensitive),
            "dns" => new SocketException((int)SocketError.HostNotFound),
            "timeout" => new TimeoutException(sensitive),
            "socket_timeout" => new SocketException((int)SocketError.TimedOut),
            "connection" => new SocketException((int)SocketError.ConnectionRefused),
            "database" => new PostgresException(sensitive, "ERROR", "ERROR", "28P01"),
            _ => new IOException(sensitive)
        };
        var exception = new NpgsqlException(sensitive, cause);
        var logger = new CapturedLogger();

        ProfileStoreDiagnostics.LogFailure(logger, "open_connection", exception);

        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.Null(logger.Exception);
        Assert.Equal("open_connection", logger.Values["Stage"]);
        Assert.Equal(category, logger.Values["Category"]);
        Assert.Equal("NpgsqlException", logger.Values["ErrorType"]);
        Assert.Equal(cause.GetType().Name, logger.Values["InnerErrorType"]);
        Assert.Equal(rootType, logger.Values["RootErrorType"]);
        Assert.DoesNotContain("secret-marker", logger.Message);
        Assert.DoesNotContain("private-token", logger.Message);
        Assert.DoesNotContain("user@example.invalid", logger.Message);
        Assert.DoesNotContain(logger.Values.Values, value => value?.ToString()?.Contains("secret-marker", StringComparison.Ordinal) == true);
    }

    /// <summary>Исключение без внутренней причины также журналируется безопасно.</summary>
    [Fact]
    public void Failure_WithoutInnerException_LogsNone()
    {
        var logger = new CapturedLogger();

        ProfileStoreDiagnostics.LogFailure(logger, "execute_query", new TimeoutException("secret-marker"));

        Assert.Equal("none", logger.Values["InnerErrorType"]);
        Assert.Equal("timeout", logger.Values["Category"]);
        Assert.Equal("execute_query", logger.Values["Stage"]);
        Assert.DoesNotContain("secret-marker", logger.Message);
    }

    private sealed class CapturedLogger : ILogger
    {
        internal string Message { get; private set; } = string.Empty;
        internal Dictionary<string, object?> Values { get; private set; } = [];
        internal Exception? Exception { get; private set; }
        internal LogLevel Level { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
            Exception = exception;
            Level = logLevel;
            Values = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }
}
