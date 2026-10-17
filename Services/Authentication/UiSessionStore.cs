using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace EquipmentManager.Services.Authentication;

/// <summary>Данные входа, доступные только серверу; токен не включается в cookie.</summary>
public sealed class UiSession(string accessToken, Guid userId, DateTimeOffset expiresAt)
{
    /// <summary>Пользователь проверенного JWT.</summary>
    public Guid UserId { get; } = userId;
    /// <summary>Абсолютный срок сеанса, не превышающий срок JWT.</summary>
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    internal string AccessToken { get; } = accessToken;
}

/// <summary>Хранит сеансы одного процесса с абсолютным сроком; перезапуск требует повторного входа.</summary>
public sealed class UiSessionStore(IMemoryCache cache, TimeProvider clock)
{
    /// <summary>Название claim с непрозрачным идентификатором серверного сеанса.</summary>
    public const string SessionClaim = "em_session";

    /// <summary>Создаёт ограниченный сроком JWT серверный сеанс.</summary>
    public string Create(string accessToken, Guid userId, DateTimeOffset tokenExpiresAt)
    {
        var expiresAt = tokenExpiresAt < clock.GetUtcNow().AddHours(8)
            ? tokenExpiresAt : clock.GetUtcNow().AddHours(8);
        if (expiresAt <= clock.GetUtcNow()) { throw new InvalidOperationException("Срок токена истёк."); }
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        cache.Set("ui-session:" + sessionId, new UiSession(accessToken, userId, expiresAt), expiresAt);
        return sessionId;
    }

    /// <summary>Проверяет срок и соответствие сеанса пользователю cookie/circuit.</summary>
    public UiSession? Find(ClaimsPrincipal principal)
    {
        var sessionId = principal.FindFirst(SessionClaim)?.Value;
        if (principal.Identity?.IsAuthenticated != true || sessionId is null
            || !Guid.TryParse(principal.FindFirst("sub")?.Value, out var userId)) { return null; }
        if (!cache.TryGetValue<UiSession>("ui-session:" + sessionId, out var session)
            || session is null || session.UserId != userId) { return null; }
        if (session.ExpiresAt <= clock.GetUtcNow())
        {
            Remove(principal);
            return null;
        }
        return session;
    }

    /// <summary>Немедленно отзывает текущий локальный сеанс, включая его копии в других вкладках.</summary>
    public void Remove(ClaimsPrincipal principal)
    {
        var sessionId = principal.FindFirst(SessionClaim)?.Value;
        if (sessionId is not null) { cache.Remove("ui-session:" + sessionId); }
    }
}
