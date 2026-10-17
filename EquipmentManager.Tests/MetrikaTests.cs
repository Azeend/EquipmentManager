using System.Net;
using EquipmentManager.Services.Analytics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет границу публичных маршрутов и независимость аналитики от пользовательского сеанса.</summary>
public sealed class MetrikaTests
{
    private const string Origin = "https://equipment-manager-demo.onrender.com";
    private static YandexMetrikaSettings Settings() => new() { Enabled = true, CounterId = 113657931, PublicOrigin = Origin };

    /// <summary>В аналитику допускаются только фиксированные пути без произвольных сегментов.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/equipment")]
    [InlineData("/faults")]
    [InlineData("/maintenance-history")]
    [InlineData("/counter")]
    [InlineData("/weather")]
    public void PagePath_AllowsOnlyPublicRoutes(string path) => Assert.Equal(path, Settings().GetPagePath(Origin + path));

    /// <summary>Параметры, fragment, UUID, API и авторизация полностью исключены, а не передаются в JS.</summary>
    [Theory]
    [InlineData("/login?email=private%40example.invalid")]
    [InlineData("/login")]
    [InlineData("/account/login")]
    [InlineData("/account/logout")]
    [InlineData("/account/session")]
    [InlineData("/account/oauth/callback?code=synthetic&state=synthetic")]
    [InlineData("/oauth/yandex/userinfo")]
    [InlineData("/api/equipment")]
    [InlineData("/equipment?access_token=synthetic")]
    [InlineData("/equipment?search=private")]
    [InlineData("/equipment#access_token=synthetic")]
    [InlineData("/equipment/11111111-1111-1111-1111-111111111111")]
    [InlineData("/equipment/private@example.invalid")]
    [InlineData("/equipment%3Femail=private")]
    [InlineData("/Error")]
    public void PagePath_RejectsSensitiveAndUnknownRoutes(string path) => Assert.Null(Settings().GetPagePath(Origin + path));

    /// <summary>Локальные и внешние origin не отправляются в опубликованный счётчик.</summary>
    [Theory]
    [InlineData("https://localhost:7128/equipment")]
    [InlineData("http://equipment-manager-demo.onrender.com/equipment")]
    [InlineData("https://foreign.invalid/equipment")]
    [InlineData("/equipment")]
    public void PagePath_RejectsUntrustedOrigin(string location) => Assert.Null(Settings().GetPagePath(location));

    /// <summary>Отключение и некорректный номер счётчика не разрешают отправку данных.</summary>
    [Theory]
    [InlineData(false, 113657931)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 9007199254740992L)]
    public void Settings_DisableInvalidOrUnselectedCounter(bool enabled, long counterId)
    {
        var settings = Settings();
        settings.Enabled = enabled;
        settings.CounterId = counterId;
        Assert.False(settings.IsConfigured);
        Assert.Null(settings.GetPagePath(Origin + "/equipment"));
    }

    /// <summary>Адрес аналитики должен быть HTTPS origin без пользовательской информации и параметров.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("http://example.invalid")]
    [InlineData("https://example.invalid/private")]
    [InlineData("https://user@example.invalid")]
    [InlineData("https://example.invalid/?token=synthetic")]
    [InlineData("https://example.invalid/#private")]
    public void Settings_RejectInvalidPublicOrigin(string origin)
    {
        var settings = Settings();
        settings.PublicOrigin = origin;
        Assert.False(settings.IsConfigured);
    }

    /// <summary>Идентификатор читается из конфигурации, а не зашит в JS или компонент.</summary>
    [Fact]
    public void Settings_BindConfigurableCounterId()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Analytics:YandexMetrika:Enabled"] = "true",
            ["Analytics:YandexMetrika:CounterId"] = "123456",
            ["Analytics:YandexMetrika:PublicOrigin"] = Origin
        }).Build();
        using var services = new ServiceCollection().AddEquipmentAnalytics(configuration).BuildServiceProvider();
        var settings = services.GetRequiredService<YandexMetrikaSettings>();
        Assert.Equal(123456, settings.CounterId);
        Assert.True(settings.IsConfigured);
    }

    /// <summary>Опечатка в переменной окружения не останавливает регистрацию приложения и вход.</summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("999999999999999999999999999")]
    public void Settings_InvalidNumberDisablesOnlyAnalytics(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Analytics:YandexMetrika:Enabled"] = "true",
            ["Analytics:YandexMetrika:CounterId"] = value,
            ["Analytics:YandexMetrika:PublicOrigin"] = Origin
        }).Build();
        using var services = new ServiceCollection().AddEquipmentAnalytics(configuration).BuildServiceProvider();
        Assert.False(services.GetRequiredService<YandexMetrikaSettings>().IsConfigured);
    }

    /// <summary>SSR не вставляет сторонний скрипт в login или защищённый HTML и не нарушает вход/выход.</summary>
    [Fact]
    public async Task Analytics_DoesNotChangePasswordSessionOrRenderTrackingOnLogin()
    {
        await using var fixture = new UiAuthFixture(configureServices: services => services.AddSingleton(Settings()));
        await fixture.StartAsync();
        var loginForm = await fixture.FormAsync();
        Assert.DoesNotContain("mc.yandex.ru", loginForm.Html);
        Assert.DoesNotContain("metrika.js", loginForm.Html);
        using var login = await fixture.PostAsync("/account/login", loginForm.Cookie, loginForm.Token);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var cookie = UiAuthFixture.Cookie(login);
        var page = await fixture.FormAsync("/equipment", cookie);
        Assert.DoesNotContain("mc.yandex.ru", page.Html);
        Assert.DoesNotContain("113657931", page.Html);
        var session = await fixture.FormAsync("/account/session", cookie);
        using var logout = await fixture.PostAsync("/account/logout", cookie + "; " + session.Cookie, session.Token);
        Assert.Contains("status=signed_out", logout.Headers.Location!.OriginalString);
    }
}
