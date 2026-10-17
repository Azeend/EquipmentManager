using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;

namespace EquipmentManager.Services.Authentication;

/// <summary>Выполняет вход/выход обычными POST, чтобы cookie устанавливалась до запуска SignalR.</summary>
public static class UiAccountEndpoints
{
    /// <summary>Публикует обработчики входа/выхода и отдельный защищённый OAuth-конвейер.</summary>
    public static void MapEquipmentUiAccount(this WebApplication app)
    {
        app.MapPost("/account/login", SignInAsync).AllowAnonymous();
        app.MapPost("/account/logout", SignOutAsync).AllowAnonymous();
        app.MapYandexOAuth();
    }

    /// <summary>Оставляет только локальный адрес возврата без схемы, host и обратных слешей.</summary>
    public static string LocalReturnUrl(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] == '/'
        && (value.Length == 1 || value[1] is not ('/' or '\\'))
        && !value.Any(character => char.IsControl(character) || character == '\\') ? value : "/account/session";

    private static async Task<IResult> SignInAsync(HttpContext context, IAntiforgery antiforgery,
        SupabasePasswordClient auth, UiSessionStore sessions, IProfileStore profiles, ILoggerFactory loggerFactory)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps) { return Results.BadRequest("Для входа требуется HTTPS."); }
        if (!await ValidateFormAsync(context, antiforgery)) { return Results.BadRequest("Форма устарела. Перезагрузите страницу входа."); }
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var returnUrl = LocalReturnUrl(form["returnUrl"]);
        IResult Error(string code) => Results.LocalRedirect("/login?error=" + code + "&returnUrl=" + Uri.EscapeDataString(returnUrl));
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        if (email.Length is 0 or > 320 || password.Length is 0 or > 4096)
        {
            loggerFactory.CreateLogger("EquipmentManager.Services.Authentication.UiAccountEndpoints")
                .LogWarning(new EventId(2003, "InvalidSignInForm"),
                    "Событие: {Event}; отказ входа до Supabase Auth. Категория: invalid_form; HTTP-ответ Auth отсутствует.", "InvalidSignInForm");
            return Error("credentials");
        }
        try
        {
            var login = await auth.SignInAsync(email, password, context.RequestAborted);
            return await CompleteSignInAsync(context, login, sessions, profiles, returnUrl, email);
        }
        catch (UiSignInException exception) { return Error(exception.Code); }
    }

    internal static async Task<IResult> CompleteSignInAsync(HttpContext context, ValidatedUiLogin login,
        UiSessionStore sessions, IProfileStore profiles, string returnUrl, string fallbackName = "Пользователь")
    {
        IResult Error(string code) => Results.LocalRedirect("/login?error=" + code + "&returnUrl=" + Uri.EscapeDataString(returnUrl));
        var userId = Guid.Parse(login.Principal.FindFirst("sub")!.Value);
        var lookup = await profiles.GetAsync(userId, context.RequestAborted);
        if (!lookup.Available) { return Error("profiles"); }
        if (lookup.Profile is not { IsActive: true } profile
            || !profile.Roles.Any(role => role is "engineer" or "maintenance_specialist" or "manager"))
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("EquipmentManager.Services.Authentication.UiAccountEndpoints")
                .LogWarning(new EventId(2004, "UiProfileAccessDenied"),
                    "Событие: {Event}; причина: {Reason}", "UiProfileAccessDenied", "forbidden");
            return Error("forbidden");
        }
        // При повторном входе в том же браузере предыдущий локальный сеанс отзывается.
        sessions.Remove(context.User);
        var sessionId = sessions.Create(login.AccessToken, userId, login.ExpiresAt);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()), new Claim(UiSessionStore.SessionClaim, sessionId),
            new Claim(ClaimTypes.Name, login.Principal.FindFirst("email")?.Value ?? fallbackName)
        }, UiAuthentication.Scheme));
        try
        {
            await context.SignInAsync(UiAuthentication.Scheme, principal, new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                ExpiresUtc = sessions.Find(principal)!.ExpiresAt
            });
        }
        catch
        {
            sessions.Remove(principal);
            throw;
        }
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, IAntiforgery antiforgery,
        SupabasePasswordClient auth, UiSessionStore sessions)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps) { return Results.BadRequest("Для выхода требуется HTTPS."); }
        if (!await ValidateFormAsync(context, antiforgery)) { return Results.BadRequest("Форма устарела. Перезагрузите страницу."); }
        var session = sessions.Find(context.User);
        sessions.Remove(context.User);
        await context.SignOutAsync(UiAuthentication.Scheme);
        var confirmed = session is null || await auth.SignOutAsync(session.AccessToken, context.RequestAborted);
        return Results.LocalRedirect(confirmed ? "/login?status=signed_out" : "/login?status=local_signout");
    }

    internal static async Task<bool> ValidateFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!context.Request.HasFormContentType || context.Request.ContentLength > 16384) { return false; }
        // Ограничиваем поток до разбора antiforgery: Content-Length может отсутствовать при chunked/HTTP/2.
        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = Math.Min(bodySize.MaxRequestBodySize ?? long.MaxValue, 16384);
        }
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException) { return false; }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge) { return false; }
        catch (InvalidDataException) { return false; }
    }
}
