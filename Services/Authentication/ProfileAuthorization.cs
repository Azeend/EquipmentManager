using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace EquipmentManager.Services.Authentication;

/// <summary>Требует одну из ролей активного серверного профиля.</summary>
public sealed record ProfileRolesRequirement(params string[] Roles) : IAuthorizationRequirement;

/// <summary>Проверяет профиль на каждый запрос, игнорируя роли из JWT и user_metadata.</summary>
public sealed class ProfileAuthorizationHandler(IServiceProvider services)
    : AuthorizationHandler<ProfileRolesRequirement>
{
    internal const string StoreUnavailable = "EquipmentManager.ProfileStoreUnavailable";
    private Task<ProfileLookup>? lookup;

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ProfileRolesRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId)) { return; }
        var httpContext = context.Resource as HttpContext;
        var result = await (lookup ??= services.GetRequiredService<IProfileStore>()
            .GetAsync(userId, httpContext?.RequestAborted ?? CancellationToken.None));
        if (!result.Available)
        {
            if (httpContext is not null) { httpContext.Items[StoreUnavailable] = true; }
            context.Fail();
            return;
        }
        if (result.Profile is { IsActive: true } profile
            && profile.Roles.Any(role => requirement.Roles.Contains(role, StringComparer.Ordinal)))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Не выдаёт недоступность БД за недостаточные права и не разрешает операцию при сбое.</summary>
public sealed class ProfileAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler defaultHandler = new();

    /// <inheritdoc />
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (context.Items.ContainsKey(ProfileAuthorizationHandler.StoreUnavailable))
        {
            await Results.Problem(statusCode: 503, title: "Не удалось проверить права доступа.",
                extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier })
                .ExecuteAsync(context);
            return;
        }
        await defaultHandler.HandleAsync(next, context, policy, result);
    }
}
