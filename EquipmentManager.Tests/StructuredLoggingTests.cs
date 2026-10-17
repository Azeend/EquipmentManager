using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Claims;
using System.Text.Json;
using EquipmentManager.Services.Authentication;
using EquipmentManager.Services.Monitoring;
using EquipmentManager.Services.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Захват stdout/stderr выполняется последовательно, не затрагивая консоль параллельных тестов.</summary>
[CollectionDefinition("JSON console", DisableParallelization = true)]
public sealed class JsonConsoleCollection;

/// <summary>Проверяет настоящий встроенный JSON Console Formatter и безопасные события Production.</summary>
[Collection("JSON console")]
public sealed class StructuredLoggingTests
{
    private const string Sensitive = "Password=secret-marker;Bearer private-token;Cookie=private-cookie;private@example.invalid;code=private-code";

    /// <summary>Все четыре уровня сохранены, scopes и непроверенные категории исключены даже при небезопасной конфигурации.</summary>
    [Fact]
    public async Task JsonConsole_PreservesLevelsAndStructure_WithoutScopesOrFrameworkSecrets()
    {
        var output = await CaptureAsync(() =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LogLevel:Default"] = "Trace",
                ["Console:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Console:FormatterName"] = "simple",
                ["Console:FormatterOptions:IncludeScopes"] = "true",
                ["Console:FormatterOptions:JsonWriterOptions:Indented"] = "true"
            }).Build();
            using var factory = LoggerFactory.Create(logging =>
            {
                logging.AddConfiguration(configuration);
                logging.AddEquipmentJsonConsole(production: true);
            });
            var logger = factory.CreateLogger<HealthStateTransitions>();
            using (logger.BeginScope(new Dictionary<string, object> { ["Cookie"] = Sensitive }))
            {
                foreach (var level in new[] { LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })
                {
                    logger.Log(level, new EventId(9000, "SafeTest"), "Событие: {Event}; HTTP: {Status}", "SafeTest", 503);
                }
                logger.LogDebug("Не выводится");
            }
            foreach (var category in new[] { "Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware",
                "Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost", "Npgsql.Command", "System.Net.Http.HttpClient", "EquipmentManager.Unreviewed" })
            {
                factory.CreateLogger(category).LogCritical(new Exception(Sensitive), "Чувствительные данные {Raw}", Sensitive);
            }
            return Task.CompletedTask;
        });
        Assert.Equal(2, Lines(output.Out).Length);
        Assert.Equal(2, Lines(output.Error).Length);
        var entries = Entries(output);
        Assert.Equal(4, entries.Length);
        Assert.Equal(new[] { "Information", "Warning", "Error", "Critical" }, entries.Select(entry => entry.GetProperty("LogLevel").GetString()));
        Assert.All(entries, entry =>
        {
            Assert.Equal(9000, entry.GetProperty("EventId").GetInt32());
            Assert.Equal(503, entry.GetProperty("State").GetProperty("Status").GetInt32());
            Assert.Equal("SafeTest", entry.GetProperty("State").GetProperty("Event").GetString());
            Assert.EndsWith("Z", entry.GetProperty("Timestamp").GetString());
            Assert.False(entry.TryGetProperty("Scopes", out _));
            Assert.False(entry.TryGetProperty("Exception", out _));
        });
        AssertSafe(output);
    }

    /// <summary>Фатальный сбой имеет Critical, а сообщение PostgreSQL/TLS не раскрывает исходную причину с секретом.</summary>
    [Fact]
    public async Task JsonConsole_RecordsSanitizedPostgresAndFatalEvents()
    {
        var output = await CaptureAsync(() =>
        {
            using var factory = LoggerFactory.Create(logging => logging.AddEquipmentJsonConsole(production: true));
            ProfileStoreDiagnostics.LogFailure(factory.CreateLogger<PostgresProfileStore>(), "open_connection",
                new NpgsqlException(Sensitive, new AuthenticationException(Sensitive)));
            ApplicationLogEvents.Fatal(factory.CreateLogger("EquipmentManager.Startup"), new InvalidOperationException(Sensitive));
            return Task.CompletedTask;
        });
        var entries = Entries(output);
        var postgres = Assert.Single(entries, entry => entry.GetProperty("EventId").GetInt32() == 3001);
        Assert.Equal("tls_or_certificate", postgres.GetProperty("State").GetProperty("Category").GetString());
        Assert.Equal("open_connection", postgres.GetProperty("State").GetProperty("Stage").GetString());
        var fatal = Assert.Single(entries, entry => entry.GetProperty("EventId").GetInt32() == 1002);
        Assert.Equal("Critical", fatal.GetProperty("LogLevel").GetString());
        Assert.All(entries, entry => Assert.False(entry.TryGetProperty("Exception", out _)));
        AssertSafe(output);
    }

    /// <summary>Событие запуска включает только нормализованное окружение, без произвольного значения из настроек.</summary>
    [Theory]
    [InlineData("Production", "Production")]
    [InlineData(Sensitive, "Other")]
    public async Task Startup_UsesFixedSafeFields(string environment, string expected)
    {
        var output = await CaptureAsync(() =>
        {
            using var factory = LoggerFactory.Create(logging => logging.AddEquipmentJsonConsole(production: true));
            ApplicationLogEvents.Started(factory.CreateLogger("EquipmentManager.Startup"), environment);
            return Task.CompletedTask;
        });
        var entry = Assert.Single(Entries(output));
        Assert.Equal(1000, entry.GetProperty("EventId").GetInt32());
        Assert.Equal(expected, entry.GetProperty("State").GetProperty("Environment").GetString());
        AssertSafe(output);
    }

    /// <summary>HTTP-сбой повторно выбрасывается без изменения поведения и без query, пользователя, заголовков или стека в журнале.</summary>
    [Fact]
    public async Task RequestFailure_IsSanitized_AndOriginalExceptionIsRethrown()
    {
        var output = await CaptureAsync(async () =>
        {
            using var factory = LoggerFactory.Create(logging => logging.AddEquipmentJsonConsole(production: true));
            var context = new DefaultHttpContext { TraceIdentifier = "server-diagnostic-trace" };
            context.Request.Path = "/account/oauth/callback";
            context.Request.QueryString = new QueryString("?code=private-code&email=private@example.invalid");
            context.Request.Headers.Authorization = "Bearer private-token";
            context.Request.Headers.Cookie = "private-cookie";
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", "11111111-1111-1111-1111-111111111111"), new Claim(ClaimTypes.Email, "private@example.invalid")], "test"));
            var original = new InvalidOperationException(Sensitive);
            var middleware = new SafeRequestLogging(_ => Task.FromException(original), factory.CreateLogger<SafeRequestLogging>());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
            Assert.Same(original, error);
        });
        var entry = Assert.Single(Entries(output));
        Assert.Equal(1001, entry.GetProperty("EventId").GetInt32());
        Assert.Equal("oauth_callback", entry.GetProperty("State").GetProperty("RequestKind").GetString());
        Assert.False(entry.TryGetProperty("Exception", out _));
        Assert.DoesNotContain("/account/oauth/callback", output.Out + output.Error);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", output.Out + output.Error);
        AssertSafe(output);
    }

    /// <summary>Обычный обрыв запроса не создаёт лишнее событие ошибки.</summary>
    [Fact]
    public async Task RequestCancellation_DoesNotCreateErrorEvent()
    {
        var output = await CaptureAsync(async () =>
        {
            using var factory = LoggerFactory.Create(logging => logging.AddEquipmentJsonConsole(production: true));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var context = new DefaultHttpContext { RequestAborted = cancelled.Token };
            var middleware = new SafeRequestLogging(_ => Task.FromCanceled(cancelled.Token), factory.CreateLogger<SafeRequestLogging>());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
        });
        Assert.Empty(Entries(output));
    }

    /// <summary>Реальный HTTP-конвейер сохраняет 200/401/302; повторные healthy-проверки не создают новые записи.</summary>
    [Fact]
    public async Task ProductionJson_RecordsAuthAndHealthTransitions_WithoutSuccessfulRequestNoise()
    {
        string password = "", token = "";
        var output = await CaptureAsync(async () =>
        {
            await using var fixture = new UiAuthFixture(configureServices: services =>
            {
                services.AddLogging(logging => logging.AddEquipmentJsonConsole(production: true));
                services.AddSingleton<IPostgresReadinessProbe>(new HealthyProbe());
            });
            password = fixture.Auth.Password;
            token = fixture.Auth.AccessToken;
            fixture.App.LogEquipmentStartup();
            await fixture.StartAsync();
            for (var index = 0; index < 3; index++)
            {
                using var health = await fixture.Client.GetAsync("/health/ready");
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                fixture.Clock.Now = fixture.Clock.Now.AddSeconds(6);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/equipment?token=private-token&email=private@example.invalid");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "private-token");
            using var denied = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            fixture.Auth.Status = HttpStatusCode.BadRequest;
            fixture.Auth.RawResponse = Sensitive;
            var form = await fixture.FormAsync();
            using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.Contains("error=credentials", login.Headers.Location!.OriginalString);
        });
        var entries = Entries(output);
        Assert.Single(entries, entry => entry.GetProperty("EventId").GetInt32() == 1000);
        Assert.Single(entries, entry => entry.GetProperty("EventId").GetInt32() == 2001);
        Assert.Single(entries, entry => entry.GetProperty("EventId").GetInt32() == 2002);
        Assert.Equal(2, entries.Count(entry => entry.GetProperty("EventId").GetInt32() == 6001));
        Assert.DoesNotContain("/health/ready", output.Out + output.Error);
        Assert.DoesNotContain(password, output.Out + output.Error);
        Assert.DoesNotContain(token, output.Out + output.Error);
        AssertSafe(output);
    }

    private static async Task<(string Out, string Error)> CaptureAsync(Func<Task> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            await action(); // Каждый владелец LoggerFactory/хоста освобождается до восстановления потоков.
            return (output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    private static JsonElement[] Entries((string Out, string Error) output) => Lines(output.Out).Concat(Lines(output.Error)).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray();

    private static void AssertSafe((string Out, string Error) output)
    {
        foreach (var marker in new[] { "secret-marker", "private-token", "private-cookie", "private@example.invalid", "private-code" })
        { Assert.DoesNotContain(marker, output.Out + output.Error); }
    }

    private sealed class HealthyProbe : IPostgresReadinessProbe
    {
        public Task CheckAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
