using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace EquipmentManager.Services.Authentication;

/// <summary>Регистрирует JWT-проверку и серверные политики без самостоятельного хранения паролей.</summary>
public static class SupabaseAuthentication
{
    /// <summary>Политика чтения оборудования.</summary>
    public const string ReadPolicy = "Equipment.Read";
    /// <summary>Политика изменения оборудования.</summary>
    public const string WritePolicy = "Equipment.Write";
    /// <summary>Регистрация неисправности инженером или специалистом.</summary>
    public const string FaultRegisterPolicy = "Faults.Register";
    /// <summary>Обработка неисправности специалистом.</summary>
    public const string FaultProcessPolicy = "Faults.Process";

    /// <summary>Настраивает доверенный JWKS и читает строку БД только из отдельного защищённого источника.</summary>
    public static IServiceCollection AddEquipmentAuthentication(this IServiceCollection services, IConfiguration configuration,
        IConfiguration profileCredentials)
    {
        var settings = configuration.GetSection("SupabaseAuth").Get<SupabaseAuthSettings>() ?? new();
        settings.Validate();
        var connectionString = profileCredentials.GetConnectionString("Profiles");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var databaseSettings = new NpgsqlConnectionStringBuilder(connectionString);
            var loginName = databaseSettings.Username?.Split('.')[0];
            if (databaseSettings.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(loginName)
                || loginName is "postgres" or "supabase_admin" or "service_role"
                || databaseSettings.IncludeErrorDetail || databaseSettings.PersistSecurityInfo)
            {
                throw new InvalidOperationException("Используйте отдельную ограниченную роль профилей и SSL Mode=VerifyFull без расширенного раскрытия ошибок.");
            }
        }
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.RequireHttpsMetadata = true;
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (!settings.Enabled) { context.Fail("Supabase Auth ещё не настроен и не подтверждён."); }
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    if (!Guid.TryParse(context.Principal?.FindFirst("sub")?.Value, out _)
                        || context.Principal?.FindFirst("role")?.Value != "authenticated"
                        || context.Principal?.FindFirst("is_anonymous")?.Value == "true")
                    {
                        context.Fail("Требуется пользовательский access-токен Supabase.");
                    }
                    return Task.CompletedTask;
                }
            };
            if (!settings.Enabled) { return; }
            // JWKS-адрес определяется только доверенной конфигурацией, никогда не из jku/x5u токена.
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                settings.Issuer + "/.well-known/jwks.json", new SupabaseJwksRetriever(settings.SigningAlgorithm),
                new HttpDocumentRetriever { RequireHttps = true });
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidateIssuer = true, ValidIssuer = settings.Issuer,
                ValidateAudience = true, ValidAudience = settings.Audience,
                ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [settings.SigningAlgorithm]
            };
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReadPolicy, policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().AddRequirements(new ProfileRolesRequirement("engineer", "maintenance_specialist", "manager")));
            options.AddPolicy(WritePolicy, policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().AddRequirements(new ProfileRolesRequirement("engineer")));
            options.AddPolicy(FaultRegisterPolicy, policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().AddRequirements(new ProfileRolesRequirement("engineer", "maintenance_specialist")));
            options.AddPolicy(FaultProcessPolicy, policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().AddRequirements(new ProfileRolesRequirement("maintenance_specialist")));
        });
        // Создание пула ленивое; при Enabled=false нет подключений к PostgreSQL.
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString
            ?? throw new InvalidOperationException("Не настроен источник ролей.")));
        services.AddScoped<IProfileStore>(provider => new PostgresProfileStore(
            string.IsNullOrWhiteSpace(connectionString) ? null : provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<ILogger<PostgresProfileStore>>()));
        services.AddScoped<IAuthorizationHandler, ProfileAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProfileAuthorizationResultHandler>();
        return services;
    }

}
