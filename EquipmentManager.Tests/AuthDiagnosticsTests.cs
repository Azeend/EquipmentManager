using System.Net;
using EquipmentManager.Services.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет диагностику реального клиента Auth с локальной подменой транспорта и без секретов в логах.</summary>
public sealed class AuthDiagnosticsTests
{
    /// <summary>Отказы HTTP, JWT, транспорта и конфигурации различаются без изменения кода ошибки интерфейса.</summary>
    [Theory]
    [InlineData("400", "credentials", "auth_rejected", 400)]
    [InlineData("401", "credentials", "auth_rejected", 401)]
    [InlineData("403", "credentials", "auth_rejected", 403)]
    [InlineData("429", "rate_limit", "rate_limit", 429)]
    [InlineData("404", "connection", "auth_http_error", 404)]
    [InlineData("500", "connection", "auth_http_error", 500)]
    [InlineData("jwt", "credentials", "jwt_validation", 200)]
    [InlineData("expired", "credentials", "jwt_validation", 200)]
    [InlineData("network", "connection", "http_transport", null)]
    [InlineData("timeout", "connection", "timeout", null)]
    [InlineData("json", "connection", "invalid_json", 200)]
    [InlineData("missing", "connection", "missing_access_token", 200)]
    [InlineData("configuration", "configuration", "configuration", null)]
    public async Task LoginFailure_LogsSafeStatusCategoryAndType(string scenario, string error, string category, int? status)
    {
        var logs = new CapturedLogs();
        await using var fixture = new UiAuthFixture(configureServices: services =>
        {
            services.AddLogging(builder => builder.AddProvider(logs));
            if (scenario == "configuration")
            {
                services.RemoveAll<UiAuthSettings>();
                services.AddSingleton(new UiAuthSettings());
            }
        });
        const string secret = "secret-marker";
        if (int.TryParse(scenario, out var responseStatus)) { fixture.Auth.Status = (HttpStatusCode)responseStatus; }
        if (scenario == "jwt") { fixture.Auth.AccessToken = ApiAuthFixture.CreateToken(audience: "foreign"); }
        if (scenario == "expired") { fixture.Auth.AccessToken = ApiAuthFixture.CreateToken(expired: true); }
        if (scenario == "network") { fixture.Auth.LoginException = new HttpRequestException(secret); }
        if (scenario == "timeout") { fixture.Auth.LoginException = new TaskCanceledException(secret); }
        if (scenario == "json") { fixture.Auth.RawResponse = "<html>" + secret + "</html>"; }
        if (scenario == "missing") { fixture.Auth.RawResponse = "{\"message\":\"" + secret + "\"}"; }
        using var scope = fixture.App.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<SupabasePasswordClient>();

        var exception = await Assert.ThrowsAsync<UiSignInException>(() =>
            client.SignInAsync("private-email@example.invalid", secret, CancellationToken.None));

        Assert.Equal(error, exception.Code);
        var entry = Assert.Single(logs.Entries);
        Assert.Null(entry.Exception);
        Assert.Equal(category, entry.Values["Category"]);
        Assert.Equal(status, entry.Values["AuthStatus"]);
        Assert.NotNull(entry.Values["Stage"]);
        Assert.NotNull(entry.Values["ErrorType"]);
        if (scenario == "network") { Assert.Equal("HttpRequestException", entry.Values["ErrorType"]); }
        if (scenario == "timeout") { Assert.Equal("TaskCanceledException", entry.Values["ErrorType"]); }
        Assert.DoesNotContain(secret, entry.Message);
        Assert.DoesNotContain("private-email", entry.Message);
        Assert.DoesNotContain(fixture.Auth.AccessToken, entry.Message);
        Assert.DoesNotContain("https://", entry.Message);
        Assert.DoesNotContain(entry.Values.Values, value => value?.ToString()?.Contains(secret, StringComparison.Ordinal) == true);
    }

    private sealed record LogEntry(string Message, Exception? Exception, Dictionary<string, object?> Values);

    private sealed class CapturedLogs : ILoggerProvider
    {
        internal List<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(this,
            categoryName == typeof(SupabasePasswordClient).FullName);
        public void Dispose() { }
    }

    private sealed class CapturedLogger(CapturedLogs logs, bool capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => capture;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (capture)
            {
                logs.Entries.Add(new LogEntry(formatter(state, exception), exception,
                    ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
        }
    }
}
