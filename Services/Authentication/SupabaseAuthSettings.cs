namespace EquipmentManager.Services.Authentication;

/// <summary>Доверенные серверные настройки проверки Supabase JWT; активация требует подтверждения.</summary>
public sealed class SupabaseAuthSettings
{
    /// <summary>Включает проверку реального проекта только после подтверждения настроек.</summary>
    public bool Enabled { get; set; }
    /// <summary>Подтверждение issuer, audience и алгоритма администратором.</summary>
    public bool SettingsConfirmed { get; set; }
    /// <summary>Точный issuer проекта, оканчивающийся на /auth/v1.</summary>
    public string Issuer { get; set; } = string.Empty;
    /// <summary>Фактический audience access-токенов проекта.</summary>
    public string Audience { get; set; } = string.Empty;
    /// <summary>Подтверждённый асимметричный алгоритм ES256 либо RS256.</summary>
    public string SigningAlgorithm { get; set; } = string.Empty;

    /// <summary>Проверяет полноту доверенных настроек; не пытается угадать конфигурацию проекта.</summary>
    public void Validate()
    {
        if (!Enabled) { return; }
        if (!SettingsConfirmed || !Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer)
            || issuer.Scheme != "https" || !string.IsNullOrEmpty(issuer.UserInfo)
            || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment)
            || !issuer.AbsolutePath.EndsWith("/auth/v1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(Audience) || SigningAlgorithm is not ("ES256" or "RS256"))
        {
            throw new InvalidOperationException("Подтвердите HTTPS issuer, audience и ES256/RS256 для Supabase Auth перед включением API.");
        }
    }
}
