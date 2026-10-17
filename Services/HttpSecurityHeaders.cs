namespace EquipmentManager.Services;

/// <summary>Добавляет совместимые с Blazor защитные заголовки без изменения CSP и WebSocket.</summary>
public static class HttpSecurityHeaders
{
    /// <summary>Запрещает MIME-sniffing, передачу Referer другим сайтам и кеширование API/личных HTML-страниц.</summary>
    public static IApplicationBuilder UseEquipmentSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = context.Request.Path == "/account/oauth/callback" ? "no-referrer" : "same-origin";
            if (context.Request.Path.StartsWithSegments("/api")
                || context.User.Identity?.IsAuthenticated == true
                && context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Response.Headers.CacheControl = "no-store";
            }
            return Task.CompletedTask;
        });
        await next(context);
    });
}
