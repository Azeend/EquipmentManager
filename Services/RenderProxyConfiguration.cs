using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace EquipmentManager.Services;

/// <summary>Настраивает строгий список прокси либо отдельное доверие ingress для учебного Render Free.</summary>
public static class RenderProxyConfiguration
{
    /// <summary>Разделяет строгую проверку IP и явный режим Render Free; без выбора заголовки не принимаются.</summary>
    public static void AddRenderProxy(this IServiceCollection services, IConfiguration configuration)
    {
        var strictMode = configuration.GetValue<bool>("Hosting:RenderProxy");
        var renderFreeMode = configuration.GetValue<bool>("Hosting:RenderFreeProxy");
        if (renderFreeMode)
        {
            if (strictMode || !configuration.GetValue<bool>("RENDER"))
            {
                throw new InvalidOperationException("Hosting:RenderFreeProxy требует отдельного выбора режима, Hosting:RenderProxy=false и окружения Render.");
            }
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                // Только явный учебный режим: доверие переносится с IP на ingress и изоляцию сети Render.
                // Любой источник с прямым доступом к Kestrel сможет подменить схему; host и IP не принимаются.
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });
            return;
        }
        if (!strictMode) { return; }
        var proxyAddresses = configuration.GetSection("Hosting:KnownProxies").Get<string[]>() ?? [];
        if (proxyAddresses.Length == 0)
        {
            throw new InvalidOperationException("Для Hosting:RenderProxy требуется явный список Hosting:KnownProxies с подтверждёнными IP прокси.");
        }
        var trustedProxies = proxyAddresses.Select(value =>
        {
            if (!IPAddress.TryParse(value, out var address)
                || address.MapToIPv6().Equals(IPAddress.Any.MapToIPv6())
                || address.Equals(IPAddress.IPv6Any))
            {
                throw new InvalidOperationException("Hosting:KnownProxies должен содержать конкретные IP прокси без wildcard, CIDR и DNS-имён.");
            }
            return address;
        }).ToArray();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            // Заменяем стандартное доверие loopback только проверенным непустым списком.
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var address in trustedProxies) { options.KnownProxies.Add(address); }
        });
    }
}
