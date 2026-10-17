using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using EquipmentManager.Services.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет настоящий HTTP Health Checks-конвейер без сети Supabase и подключений к основной БД.</summary>
public sealed class HealthCheckTests
{
    /// <summary>Liveness работает анонимно и независимо от неисправной базы; readiness имеет отдельный статус.</summary>
    [Theory]
    [InlineData(false, HttpStatusCode.OK, "Healthy")]
    [InlineData(true, HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    public async Task Endpoints_SeparateLivenessFromDatabase(bool unavailable, HttpStatusCode expected, string status)
    {
        var probe = new Probe { Failure = unavailable ? new NpgsqlException("Password=secret-marker;JWT=private-token") : null };
        await using var fixture = Fixture(probe);
        await fixture.StartAsync();
        using var live = await fixture.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await live.Content.ReadAsStringAsync());
        Assert.Equal(0, probe.Calls);
        using var ready = await fixture.Client.GetAsync("/health/ready");
        Assert.Equal(expected, ready.StatusCode);
        Assert.Equal("{\"status\":\"" + status + "\"}", await ready.Content.ReadAsStringAsync());
        Assert.True(ready.Headers.CacheControl?.NoStore);
        Assert.Equal(1, probe.Calls);
        Assert.DoesNotContain("__Host-EquipmentManager.Session", UiAuthFixture.Cookie(ready));
    }

    /// <summary>Детали произвольных исключений не передаются наружу или в журнал.</summary>
    [Theory]
    [InlineData("connection")]
    [InlineData("timeout")]
    [InlineData("configuration")]
    [InlineData("unexpected")]
    public async Task Readiness_SanitizesErrorsAndLogsTransitions(string failure)
    {
        const string sensitive = "Password=secret-marker;JWT=private-token;user@example.invalid;Host=private-host";
        var logs = new Logs();
        var probe = new Probe
        {
            Failure = failure switch
            {
                "connection" => new NpgsqlException(sensitive),
                "timeout" => new TimeoutException(sensitive),
                "configuration" => new InvalidOperationException(sensitive),
                _ => new Exception(sensitive)
            }
        };
        await using var fixture = Fixture(probe, logs);
        await fixture.StartAsync();
        for (var index = 0; index < 2; index++)
        {
            using var response = await fixture.Client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("{\"status\":\"Unhealthy\"}", await response.Content.ReadAsStringAsync());
        }
        var failed = Assert.Single(logs.Entries, entry => entry.Values.GetValueOrDefault("Check")?.ToString() == "postgresql");
        Assert.Equal(failure, failed.Values["Category"]);
        Assert.Equal("Unknown", failed.Values["PreviousStatus"]);
        Assert.Equal("Unhealthy", failed.Values["Status"]);
        Assert.Equal(6001, failed.EventId.Id);
        Assert.Equal(1, probe.Calls);
        probe.Failure = null;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(6);
        using var recovered = await fixture.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var changes = logs.Entries.Where(entry => entry.Values.GetValueOrDefault("Check")?.ToString() == "postgresql").ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal("Unhealthy", changes[1].Values["PreviousStatus"]);
        Assert.Equal("Healthy", changes[1].Values["Status"]);
        Assert.All(logs.Entries, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("secret-marker", entry.Message);
            Assert.DoesNotContain("private-token", entry.Message);
            Assert.DoesNotContain("user@example.invalid", entry.Message);
            Assert.DoesNotContain("private-host", entry.Message);
        });
    }

    /// <summary>Повторные и параллельные запросы не создают отдельные проверки PostgreSQL.</summary>
    [Fact]
    public async Task Readiness_CachesAndCoalescesConcurrentRequests()
    {
        var probe = new Probe();
        await using var fixture = Fixture(probe);
        await fixture.StartAsync();
        var requests = Enumerable.Range(0, 12).Select(async _ =>
        {
            using var response = await fixture.Client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
        await Task.WhenAll(requests);
        Assert.Equal(1, probe.Calls);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(6);
        using var repeated = await fixture.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(2, probe.Calls);
    }

    /// <summary>Общий таймаут возвращает 503 даже при пробе, игнорирующей CancellationToken.</summary>
    [Fact]
    public async Task Readiness_EnforcesDeadline()
    {
        var probe = new Probe { Hang = true };
        await using var fixture = Fixture(probe);
        await fixture.StartAsync();
        var elapsed = Stopwatch.StartNew();
        using var response = await fixture.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(9));
        Assert.True(probe.Token.IsCancellationRequested);
        using var live = await fixture.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    /// <summary>Отсутствие обязательной строки даёт 503, не открывая подключение по умолчанию.</summary>
    [Fact]
    public async Task Readiness_RejectsMissingRequiredConfiguration()
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        using var ready = await fixture.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("{\"status\":\"Unhealthy\"}", await ready.Content.ReadAsStringAsync());
        using var live = await fixture.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    /// <summary>Неполная и небезопасная конфигурация любой из двух БД отвергается до сети.</summary>
    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, false, "Host=unused.invalid;SSL Mode=Disable")]
    [InlineData(false, true, "Host=unused.invalid;SSL Mode=Prefer")]
    public async Task DatabaseProbe_RequiresConfiguredVerifyFull(bool profiles, bool crud, string? connectionString)
    {
        var probe = new PostgresReadinessProbe(profiles, crud, connectionString, connectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.CheckAsync(CancellationToken.None));
    }

    /// <summary>Демонстрационный режим с отключённым Auth не требует внешних зависимостей.</summary>
    [Fact]
    public async Task DatabaseProbe_DoesNotConnectForUnusedDependencies()
    {
        var probe = new PostgresReadinessProbe(false, false, "invalid unused string", "invalid unused string");
        await probe.CheckAsync(CancellationToken.None);
    }

    /// <summary>Пробы доступны только GET/HEAD; POST не запускает сетевую проверку.</summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Endpoints_RejectPost(string path)
    {
        var probe = new Probe();
        await using var fixture = Fixture(probe);
        await fixture.StartAsync();
        using var response = await fixture.Client.PostAsync(path, new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(0, probe.Calls);
    }

    /// <summary>Даже чужие HealthReport.Data/Description/Exception не включаются в публичный JSON.</summary>
    [Fact]
    public async Task ResponseWriter_DoesNotExposeReportDetails_AndTreatsDegradedAs503()
    {
        await using var fixture = new UiAuthFixture(configureServices: services =>
        {
            services.AddSingleton<IPostgresReadinessProbe>(new Probe());
            services.Configure<HealthCheckServiceOptions>(options => options.Registrations.Add(new HealthCheckRegistration(
                "sensitive", _ => new SensitiveCheck(), HealthStatus.Unhealthy, ["ready"])));
        });
        await fixture.StartAsync();
        using var response = await fixture.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"status\":\"Unhealthy\"}", await response.Content.ReadAsStringAsync());
    }

    private static UiAuthFixture Fixture(Probe probe, Logs? logs = null) => new(configureServices: services =>
    {
        services.AddSingleton<IPostgresReadinessProbe>(probe);
        if (logs is not null) { services.AddLogging(logging => logging.AddProvider(logs)); }
    });

    private sealed class Probe : IPostgresReadinessProbe
    {
        private int calls;
        internal int Calls => calls;
        internal Exception? Failure { get; set; }
        internal bool Hang { get; init; }
        internal CancellationToken Token { get; private set; }
        public Task CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Token = cancellationToken;
            if (Failure is not null) { return Task.FromException(Failure); }
            return Hang ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task : Task.CompletedTask;
        }
    }

    private sealed class SensitiveCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Degraded("secret-marker", new Exception("private-token"),
                new Dictionary<string, object> { ["connectionString"] = "Password=secret-marker" }));
    }

    private sealed record Entry(string Message, Exception? Exception, EventId EventId, Dictionary<string, object?> Values);

    private sealed class Logs : ILoggerProvider
    {
        internal ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }
    }

    private sealed class Capture(Logs logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Entries.Enqueue(new Entry(formatter(state, exception), exception, eventId,
                state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(pair => pair.Key, pair => pair.Value) : []));
    }
}
