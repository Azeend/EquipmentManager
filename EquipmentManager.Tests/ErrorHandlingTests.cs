using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using EquipmentManager.Services;
using EquipmentManager.Services.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет безопасные ответы и диагностику API без обращения к PostgreSQL.</summary>
public sealed class ErrorHandlingTests
{
    /// <summary>Некорректное тело ошибки Backend не ломает обработчик страницы вторичным исключением.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("<html>secret-marker</html>")]
    public async Task BackendFailure_MalformedProblem_ReturnsSafeMessage(string body)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new UiSessionStore(cache, TimeProvider.System);
        var userId = Guid.NewGuid();
        var sessionId = sessions.Create("test-token", userId, DateTimeOffset.UtcNow.AddMinutes(5));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", userId.ToString()), new Claim(UiSessionStore.SessionClaim, sessionId)
        ], UiAuthentication.Scheme));
        using var client = new HttpClient(new ErrorResponseHandler(body));
        var backend = new SessionBackendClient(new FixedClientFactory(client),
            new UiAuthSettings { BackendBaseUrl = new Uri("https://backend.invalid/") },
            new FixedAuthenticationState(principal), sessions);
        var exception = await Assert.ThrowsAsync<BackendApiException>(() => backend.GetEquipmentAsync());
        Assert.Equal(500, exception.StatusCode);
        Assert.DoesNotContain("secret-marker", exception.Message);
        Assert.NotNull(sessions.Find(principal));
    }

    /// <summary>Сбой хранилища получает корректный статус, TraceId и журнал без исходных секретных данных.</summary>
    [Theory]
    [InlineData("database", 500, "storage_error")]
    [InlineData("unavailable", 503, "storage_unavailable")]
    [InlineData("unknown", 503, "outcome_unknown")]
    [InlineData("forbidden", 403, "forbidden")]
    [InlineData("unexpected", 500, "internal_error")]
    public async Task ApiFailure_IsSafeAndCorrelated(string failure, int expectedStatus, string expectedCode)
    {
        var logs = new CapturedLogs();
        await using var app = ApiAuthFixture.CreateApp(configureServices: services =>
        {
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddSingleton<IStoreExecutor>(new FailingExecutor(failure));
            services.AddScoped<PostgresEquipmentStore>();
            services.AddScoped<PostgresFaultStore>();
            services.AddScoped<PostgresMaintenanceStore>();
            services.AddScoped(provider => new ApplicationDataService(provider.GetRequiredService<PostgresEquipmentStore>(),
                provider.GetRequiredService<PostgresFaultStore>(), provider.GetRequiredService<PostgresMaintenanceStore>()));
        });
        await app.StartAsync();
        using var client = ApiAuthFixture.CreateClient(app);
        using var response = await client.GetAsync("/api/equipment");
        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
        var traceId = document.RootElement.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.Contains(logs.Messages, message => message.Contains(traceId!, StringComparison.Ordinal));
        Assert.DoesNotContain("secret-marker", body);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("secret-marker", StringComparison.Ordinal));
        if (failure == "database") { Assert.Contains(logs.Messages, message => message.Contains("42P01", StringComparison.Ordinal)); }
    }

    private sealed class FailingExecutor(string failure) : IStoreExecutor
    {
        public Task<T> RunAsync<T>(ClaimsPrincipal user, IReadOnlyList<string> allowedRoles,
            Func<IStoreTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) =>
            throw failure switch
            {
                "database" => new StoreException(StoreFailure.Database, "42P01"),
                "unavailable" => new StoreException(StoreFailure.Unavailable),
                "unknown" => new StoreException(StoreFailure.OutcomeUnknown),
                "forbidden" => new StoreException(StoreFailure.Forbidden),
                _ => new InvalidOperationException("secret-marker")
            };
    }

    private sealed class FixedAuthenticationState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ErrorResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(body) });
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        public void Dispose() { }
    }

    private sealed class CapturedLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}
