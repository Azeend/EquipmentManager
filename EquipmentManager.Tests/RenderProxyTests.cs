using System.Net;
using EquipmentManager.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет восстановление HTTPS перед redirect и cookie без внешних подключений.</summary>
public sealed class RenderProxyTests
{
    /// <summary>HTTPS от ingress не перенаправляется повторно; HTTP по-прежнему требует HTTPS.</summary>
    [Theory]
    [InlineData("https", 200, false)]
    [InlineData("http", 307, false)]
    [InlineData("https", 200, true)]
    [InlineData("http", 307, true)]
    public async Task RenderIngress_PreservesHttpsAndIgnoresForwardedHost(string scheme, int expectedStatus, bool renderFree)
    {
        using var services = CreateServices(enabled: !renderFree, trustedProxy: renderFree ? null : "10.20.30.40",
            render: renderFree, renderFree: renderFree);
        var options = services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.30.40");
        context.Request.Host = new HostString("equipment.example.invalid");
        context.Request.Path = "/login";
        context.Request.Headers["X-Forwarded-Proto"] = scheme;
        context.Request.Headers["X-Forwarded-Host"] = "attacker.invalid";
        context.Request.Headers["X-Forwarded-For"] = "192.0.2.99";
        var redirect = new HttpsRedirectionMiddleware(http =>
        {
            http.Response.Cookies.Append("__Host-Test", "fixture", new CookieOptions
            {
                Secure = true,
                HttpOnly = true,
                Path = "/",
                SameSite = SameSiteMode.Lax
            });
            return Task.CompletedTask;
        }, Options.Create(new HttpsRedirectionOptions { HttpsPort = 443 }),
            new ConfigurationBuilder().Build(), NullLoggerFactory.Instance);
        var forwarded = new ForwardedHeadersMiddleware(redirect.Invoke, NullLoggerFactory.Instance, options);

        await forwarded.Invoke(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(scheme, context.Request.Scheme);
        Assert.Equal("equipment.example.invalid", context.Request.Host.Host);
        Assert.Equal(IPAddress.Parse("10.20.30.40"), context.Connection.RemoteIpAddress);
        if (scheme == "https")
        {
            Assert.Contains("secure", context.Response.Headers.SetCookie.ToString());
            Assert.Contains("httponly", context.Response.Headers.SetCookie.ToString());
        }
        else { Assert.Equal("https://equipment.example.invalid/login", context.Response.Headers.Location.ToString()); }
    }

    /// <summary>Недоверенный клиент не подменяет схему даже при включённой обработке заголовков.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrustedSource_DoesNotTrustForwardedScheme(bool enabled)
    {
        using var services = CreateServices(enabled, trustedProxy: enabled ? "10.20.30.40" : null);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "10.20.30.40";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
            services.GetRequiredService<IOptions<ForwardedHeadersOptions>>());

        await middleware.Invoke(context);

        Assert.Equal("http", context.Request.Scheme);
    }

    /// <summary>RENDER=true без доверенных IP не разрешает запуск с обработкой заголовков.</summary>
    [Fact]
    public void EnabledProxy_RequiresExplicitTrustedAddresses()
    {
        Assert.Throws<InvalidOperationException>(() => CreateServices(enabled: true));
    }

    /// <summary>Явный Render Free доверяет схеме любого непосредственного отправителя; риск ограничен этим режимом.</summary>
    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    public async Task RenderFree_ExplicitlyTrustsIngressSchemeWithoutKnownIp(string source)
    {
        using var services = CreateServices(enabled: false, renderFree: true);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse(source);
        context.Request.Headers["X-Forwarded-Proto"] = "http,https";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
            services.GetRequiredService<IOptions<ForwardedHeadersOptions>>());

        await middleware.Invoke(context);

        Assert.Equal("https", context.Request.Scheme);
        Assert.Equal("http", context.Request.Headers["X-Forwarded-Proto"].ToString());
    }

    /// <summary>Render Free не включается без окружения платформы либо вместе со строгим режимом.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RenderFree_InvalidModeSelection_StopsStartup(bool strictMode, bool render)
    {
        Assert.Throws<InvalidOperationException>(() => CreateServices(strictMode, render: render, renderFree: true));
    }

    /// <summary>Неопределённые адреса и ошибочные настройки не превращаются в неограниченное доверие.</summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("*")]
    [InlineData("10.0.0.0/8")]
    [InlineData("proxy.invalid")]
    [InlineData("")]
    public void InvalidTrustedAddress_StopsStartup(string trustedProxy)
    {
        Assert.Throws<InvalidOperationException>(() => CreateServices(enabled: true, trustedProxy));
    }

    /// <summary>Session Pooler допускает суффикс проекта и сохраняет обязательную проверку TLS.</summary>
    [Fact]
    public void SessionPooler_AcceptsRestrictedCrudLogin()
    {
        PostgresStoreExecutor.ValidateConnectionString(
            "Host=aws-0-example.pooler.supabase.com;Port=5432;Database=postgres;Username=equipment_api.example;SSL Mode=VerifyFull");
    }

    private static ServiceProvider CreateServices(bool enabled, string? trustedProxy = null, bool render = true, bool renderFree = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Hosting:RenderProxy"] = enabled.ToString(),
            ["Hosting:RenderFreeProxy"] = renderFree.ToString(),
            ["RENDER"] = render.ToString()
        };
        if (trustedProxy is not null) { settings["Hosting:KnownProxies:0"] = trustedProxy; }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddRenderProxy(configuration);
        return services.BuildServiceProvider();
    }
}
