using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EquipmentManager.Services.Authentication;

/// <summary>Добавляет отдельный cookie-сеанс интерфейса, не заменяя Bearer-политики API.</summary>
public static class UiAuthentication
{
    /// <summary>Имя схемы cookie интерфейса.</summary>
    public const string Scheme = "Equipment.Ui";
    /// <summary>Политика входа в интерфейс без присвоения бизнес-ролей из cookie.</summary>
    public const string Policy = "Equipment.Ui.Session";
    internal const string AuthClient = "Supabase.Password";
    internal const string BackendClient = "Equipment.Backend";

    /// <summary>Регистрирует серверное хранилище, защищённую cookie и проверку SignalR-сеансов.</summary>
    public static IServiceCollection AddEquipmentUiAuthentication(this IServiceCollection services,
        IConfiguration configuration, IConfiguration credentials)
    {
        var auth = configuration.GetSection("SupabaseAuth").Get<SupabaseAuthSettings>() ?? new();
        var backendAddress = configuration["SupabaseUiAuth:BackendBaseUrl"];
        Uri? backendUri = null;
        if (!string.IsNullOrEmpty(backendAddress)
            && (!Uri.TryCreate(backendAddress, UriKind.Absolute, out backendUri) || backendUri.Scheme != "https"
                || backendUri.AbsolutePath != "/" || backendUri.UserInfo != "" || backendUri.Query != "" || backendUri.Fragment != ""))
        {
            throw new InvalidOperationException("BackendBaseUrl должен быть доверенным HTTPS origin с завершающим /.");
        }
        var key = credentials["SupabaseUiAuth:PublishableKey"] ?? string.Empty;
        if (key.Length > 0 && !IsPublicKey(key))
        {
            throw new InvalidOperationException("Для входа используйте только publishable или legacy anon key Supabase, не secret/service_role.");
        }
        services.AddSingleton(new UiAuthSettings
        {
            Enabled = auth.Enabled,
            Issuer = auth.Issuer,
            PublishableKey = key,
            BackendBaseUrl = backendUri
        });
        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<UiSessionStore>();
        services.AddScoped<SupabasePasswordClient>();
        services.AddScoped<UiCookieEvents>();
        services.AddScoped<AuthenticationStateProvider, UiAuthenticationStateProvider>();
        services.AddCascadingAuthenticationState();
        services.AddScoped<SessionBackendClient>();
        // Не логируем HTTP-запросы аутентификации или заголовки access token.
        foreach (var name in new[] { AuthClient, BackendClient })
        {
            services.AddHttpClient(name, client => client.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                .RemoveAllLoggers();
        }
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Scheme;
            options.DefaultChallengeScheme = Scheme;
            options.DefaultSignInScheme = Scheme;
        }).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = "__Host-EquipmentManager.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.LoginPath = "/login";
            options.SlidingExpiration = false;
            options.EventsType = typeof(UiCookieEvents);
        });
        services.AddAuthorization(options => options.AddPolicy(Policy, policy =>
            policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
        return services;
    }

    private static bool IsPublicKey(string key)
    {
        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal)) { return true; }
        if (!new JsonWebTokenHandler().CanReadToken(key)) { return false; }
        // Проверяется тип ключа, а не его доверие: подлинность всё равно проверит Supabase по HTTPS.
        try { return new JsonWebToken(key).TryGetClaim("role", out var role) && role.Value == "anon"; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Отвергает cookie после истечения токена, локального выхода или перезапуска процесса.</summary>
public sealed class UiCookieEvents(UiSessionStore sessions) : CookieAuthenticationEvents
{
    /// <inheritdoc />
    public override Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null || sessions.Find(context.Principal) is null) { context.RejectPrincipal(); }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.Redirect("/login?error=expired&returnUrl="
            + Uri.EscapeDataString(UiAccountEndpoints.LocalReturnUrl(context.Request.PathBase + context.Request.Path + context.Request.QueryString)));
        return Task.CompletedTask;
    }
}

/// <summary>Повторно проверяет серверный сеанс подключённого circuit, включая выход из другой вкладки.</summary>
public sealed class UiAuthenticationStateProvider(ILoggerFactory loggerFactory, UiSessionStore sessions)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    /// <inheritdoc />
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState,
        CancellationToken cancellationToken) => Task.FromResult(sessions.Find(authenticationState.User) is not null);
}
