namespace EquipmentManager.Services.Analytics;

/// <summary>Публичные настройки аналитики; не содержат ключей авторизации или данных посетителя.</summary>
public sealed class YandexMetrikaSettings
{
    /// <summary>Явно разрешает аналитику; компонент дополнительно требует Production.</summary>
    public bool Enabled { get; set; }
    /// <summary>Публичный номер счётчика, изменяемый через окружение.</summary>
    public long CounterId { get; set; }
    /// <summary>Единственный HTTPS origin опубликованного приложения, без пути и параметров.</summary>
    public string PublicOrigin { get; set; } = string.Empty;

    /// <summary>Некорректная конфигурация отключает только аналитику, не вход и API.</summary>
    public bool IsConfigured => Enabled && CounterId is > 0 and <= 9007199254740991L
        && Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) && origin.Scheme == "https"
        && origin.AbsolutePath == "/" && origin.UserInfo == "" && origin.Query == "" && origin.Fragment == "";

    /// <summary>Возвращает только фиксированный разрешённый путь; чувствительные URL полностью пропускаются.</summary>
    public string? GetPagePath(string location)
    {
        if (!IsConfigured || !Uri.TryCreate(location, UriKind.Absolute, out var page)
            || page.GetLeftPart(UriPartial.Authority) != new Uri(PublicOrigin).GetLeftPart(UriPartial.Authority)
            || page.Query != "" || page.Fragment != "") { return null; }
        return page.AbsolutePath switch
        {
            "/" or "/equipment" or "/faults" or "/maintenance-history" or "/counter" or "/weather" => page.AbsolutePath,
            _ => null
        };
    }
}

/// <summary>Регистрирует отдельные настройки, не затрагивая конфигурацию авторизации.</summary>
public static class AnalyticsConfiguration
{
    /// <summary>Читает публичные параметры из стандартной конфигурации .NET.</summary>
    public static IServiceCollection AddEquipmentAnalytics(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Analytics:YandexMetrika");
        // Ошибка ввода публичного номера должна отключить аналитику, а не запуск приложения.
        return services.AddSingleton(new YandexMetrikaSettings
        {
            Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled,
            CounterId = long.TryParse(section["CounterId"], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var counterId) ? counterId : 0,
            PublicOrigin = section["PublicOrigin"] ?? string.Empty
        });
    }
}
