using EquipmentManager.Components;
using EquipmentManager.Services;
using EquipmentManager.Services.Authentication;
using EquipmentManager.Services.Analytics;
using EquipmentManager.Services.Monitoring;
using EquipmentManager.Services.Observability;

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.AddEquipmentJsonConsole(builder.Environment.IsProduction());
    builder.Services.AddRenderProxy(builder.Configuration);
    builder.Services.AddEquipmentAnalytics(builder.Configuration);

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    // Строка подключения не читается из appsettings или аргументов командной строки.
    var profileCredentials = new ConfigurationBuilder();
    if (builder.Environment.IsDevelopment())
    {
        profileCredentials.AddUserSecrets(typeof(Program).Assembly, optional: true);
    }
    profileCredentials.AddEnvironmentVariables();
    var protectedCredentials = profileCredentials.Build();
    builder.Services.AddEquipmentStorage(builder.Configuration, protectedCredentials);
    builder.Services.AddEquipmentAuthentication(builder.Configuration, protectedCredentials);
    builder.Services.AddEquipmentUiAuthentication(builder.Configuration, protectedCredentials);
    builder.Services.AddEquipmentHealthChecks(builder.Configuration, protectedCredentials);

    var app = builder.Build();
    // Схема запроса восстанавливается до HSTS, HTTPS redirect, cookie и antiforgery.
    app.UseForwardedHeaders();
    app.UseEquipmentSecurityHeaders();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    // Сохраняем существующий обработчик ошибок; безопасная запись выполняется до повторного выброса исключения.
    app.UseMiddleware<SafeRequestLogging>();


    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapEquipmentApi();
    app.MapEquipmentUiAccount();
    app.MapEquipmentHealthChecks();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode()
        .RequireAuthorization(UiAuthentication.Policy);

    app.LogEquipmentStartup();
    await app.RunAsync();
}
catch (Exception exception)
{
    // Исключение запуска не должно попасть в стандартный вывод runtime с паролем/строкой подключения.
    using var logging = LoggerFactory.Create(options => options.AddEquipmentJsonConsole(production: true));
    ApplicationLogEvents.Fatal(logging.CreateLogger("EquipmentManager.Startup"), exception);
    Environment.ExitCode = 1;
}
