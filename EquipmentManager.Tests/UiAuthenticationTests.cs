using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using EquipmentManager.Services.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет реальный cookie/antiforgery-конвейер, границу Bearer и отзыв circuit-сеанса без Supabase.</summary>
public sealed class UiAuthenticationTests
{
    /// <summary>Вход выдаёт Secure/HttpOnly cookie; cookie не подменяет Bearer-аутентификацию API.</summary>
    [Fact]
    public async Task Login_SetsProtectedCookie_AndApiStillRequiresBearer()
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        Assert.NotEmpty(form.Token);
        Assert.Contains("type=\"password\"", form.Html);
        Assert.DoesNotContain("_blazor", form.Html); // Вход — static SSR, не circuit.
        using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token, "https://attacker.invalid/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/account/session", login.Headers.Location!.OriginalString);
        var setCookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-EquipmentManager.Session="));
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Auth.AccessToken, setCookie);
        var cookie = UiAuthFixture.Cookie(login);
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, "/account/session");
        pageRequest.Headers.Add("Cookie", cookie);
        using var page = await fixture.Client.SendAsync(pageRequest);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain(fixture.Auth.AccessToken, html);
        Assert.DoesNotContain(fixture.Auth.Password, html);
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/equipment");
        apiRequest.Headers.Add("Cookie", cookie);
        using var cookieOnly = await fixture.Client.SendAsync(apiRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/equipment");
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Auth.AccessToken);
        using var bearer = await fixture.Client.SendAsync(bearerRequest);
        Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
    }

    /// <summary>Неверный пароль, сеть, некорректный JWT и сбой профилей не создают cookie.</summary>
    [Theory]
    [InlineData("password", "credentials")]
    [InlineData("network", "connection")]
    [InlineData("jwt", "credentials")]
    [InlineData("expired", "credentials")]
    [InlineData("profiles", "profiles")]
    [InlineData("inactive", "forbidden")]
    public async Task LoginFailure_DoesNotCreateSession(string scenario, string error)
    {
        await using var fixture = new UiAuthFixture(profileAvailable: scenario != "profiles", profileActive: scenario != "inactive");
        if (scenario == "password") { fixture.Auth.Status = HttpStatusCode.BadRequest; }
        if (scenario == "network") { fixture.Auth.NetworkFailure = true; }
        if (scenario == "jwt") { fixture.Auth.AccessToken = ApiAuthFixture.CreateToken(audience: "foreign"); }
        if (scenario == "expired") { fixture.Auth.AccessToken = ApiAuthFixture.CreateToken(expired: true); }
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        using var response = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=" + error, response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("__Host-EquipmentManager.Session", UiAuthFixture.Cookie(response));
    }

    /// <summary>Логин и выход без antiforgery запрещены до вызова Auth.</summary>
    [Theory]
    [InlineData("/account/login")]
    [InlineData("/account/logout")]
    public async Task MissingAntiforgery_Returns400(string path)
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        using var response = await fixture.PostAsync(path, "", "");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Auth.LoginCalls);
        Assert.Equal(0, fixture.Auth.LogoutCalls);
    }

    /// <summary>Выход отзывает серверный сеанс, даже если удалённый logout недоступен.</summary>
    [Theory]
    [InlineData(false, "signed_out")]
    [InlineData(true, "local_signout")]
    public async Task Logout_RevokesCopiedCookie(bool remoteFailure, string status)
    {
        await using var fixture = new UiAuthFixture();
        fixture.Auth.LogoutFailure = remoteFailure;
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
        var cookie = UiAuthFixture.Cookie(login);
        var logoutForm = await fixture.FormAsync("/account/session", cookie);
        Assert.NotEmpty(logoutForm.Token);
        using var logout = await fixture.PostAsync("/account/logout", cookie + "; " + logoutForm.Cookie, logoutForm.Token);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Contains("status=" + status, logout.Headers.Location!.OriginalString);
        Assert.Equal(1, fixture.Auth.LogoutCalls);
        using var replay = new HttpRequestMessage(HttpMethod.Get, "/account/session");
        replay.Headers.Add("Cookie", cookie);
        using var response = await fixture.Client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login?error=expired", response.Headers.Location!.OriginalString);
    }

    /// <summary>Срок cookie не позволяет использовать серверный сеанс после истечения JWT.</summary>
    [Fact]
    public async Task ExpiredToken_RejectsExistingCookie()
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/session");
        request.Headers.Add("Cookie", UiAuthFixture.Cookie(login));
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>Серверный клиент передаёт JWT в настоящий API; после отзыва запрос больше не отправляется.</summary>
    [Fact]
    public async Task BackendClient_UsesCurrentToken_AndRejectsRevokedSession()
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<UiSessionStore>();
        var sessionId = sessions.Create(ApiAuthFixture.CreateToken(), ApiAuthFixture.UserId, fixture.Clock.Now.AddMinutes(5));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", ApiAuthFixture.UserId.ToString()), new Claim(UiSessionStore.SessionClaim, sessionId)
        }, UiAuthentication.Scheme));
        var provider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
        var backend = scope.ServiceProvider.GetRequiredService<SessionBackendClient>();
        Assert.NotEmpty(await backend.GetEquipmentAsync());
        sessions.Remove(user);
        var exception = await Assert.ThrowsAsync<UiSignInException>(() => backend.GetEquipmentAsync());
        Assert.Equal("expired", exception.Code);
    }

    /// <summary>Отказ Bearer-проверки отзывает локальный сеанс и требует повторного входа.</summary>
    [Fact]
    public async Task BackendUnauthorized_RevokesLocalSession()
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<UiSessionStore>();
        var sessionId = sessions.Create(ApiAuthFixture.CreateToken(audience: "foreign"), ApiAuthFixture.UserId, fixture.Clock.Now.AddMinutes(5));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", ApiAuthFixture.UserId.ToString()), new Claim(UiSessionStore.SessionClaim, sessionId)
        }, UiAuthentication.Scheme));
        ((IHostEnvironmentAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
        var exception = await Assert.ThrowsAsync<UiSignInException>(() => scope.ServiceProvider.GetRequiredService<SessionBackendClient>().GetEquipmentAsync());
        Assert.Equal("expired", exception.Code);
        Assert.Null(sessions.Find(user));
    }

    /// <summary>Связанный circuit теряет аутентификацию после отзыва общего серверного сеанса.</summary>
    [Fact]
    public async Task Circuit_RevalidatesRevokedSession()
    {
        await using var fixture = new UiAuthFixture();
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<UiSessionStore>();
        var sessionId = sessions.Create(ApiAuthFixture.CreateToken(), ApiAuthFixture.UserId, fixture.Clock.Now.AddMinutes(5));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", ApiAuthFixture.UserId.ToString()), new Claim(UiSessionStore.SessionClaim, sessionId)
        }, UiAuthentication.Scheme));
        var provider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        var revoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AuthenticationStateChanged += async state =>
        {
            if ((await state).User.Identity?.IsAuthenticated != true) { revoked.TrySetResult(true); }
        };
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
        sessions.Remove(user);
        Assert.True(await revoked.Task.WaitAsync(TimeSpan.FromSeconds(25)));
    }

    /// <summary>Адрес возврата не позволяет перейти на внешний сайт.</summary>
    [Theory]
    [InlineData("//attacker.invalid")]
    [InlineData("/\\attacker.invalid")]
    [InlineData("https://attacker.invalid")]
    [InlineData("/\r\nLocation:evil")]
    public void ReturnUrl_RejectsExternalOrMalformed(string value) =>
        Assert.Equal("/account/session", UiAccountEndpoints.LocalReturnUrl(value));
}
