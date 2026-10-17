using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using EquipmentManager.Models;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет ограничения входящей формы и защитные заголовки без внешних сервисов.</summary>
public sealed class SecurityAuditTests
{
    /// <summary>Форма свыше 16 КиБ отклоняется до Auth, в том числе при chunked-передаче без Content-Length.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedLoginForm_IsRejectedBeforeAuth(bool unknownLength)
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        using var encoded = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "fixture@example.invalid",
            ["password"] = fixture.Auth.Password,
            ["__RequestVerificationToken"] = form.Token,
            ["padding"] = new string('x', 20000)
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/login");
        request.Headers.Add("Cookie", form.Cookie);
        request.Content = unknownLength
            ? new UnknownLengthContent(await encoded.ReadAsByteArrayAsync())
            : new ByteArrayContent(await encoded.ReadAsByteArrayAsync());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Auth.LoginCalls);
        Assert.DoesNotContain("__Host-EquipmentManager.Session", UiAuthFixture.Cookie(response));
    }

    /// <summary>Корректная небольшая chunked-форма по-прежнему проходит antiforgery и создаёт сеанс.</summary>
    [Fact]
    public async Task SmallChunkedLoginForm_StillCreatesSession()
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        var form = await fixture.FormAsync();
        using var encoded = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "fixture@example.invalid",
            ["password"] = fixture.Auth.Password,
            ["__RequestVerificationToken"] = form.Token
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/login");
        request.Headers.Add("Cookie", form.Cookie);
        request.Content = new UnknownLengthContent(await encoded.ReadAsByteArrayAsync());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, fixture.Auth.LoginCalls);
        Assert.Contains("__Host-EquipmentManager.Session", UiAuthFixture.Cookie(response));
    }

    /// <summary>Заголовки добавлены также к отказу Bearer; успешный вход и API продолжают работать.</summary>
    [Fact]
    public async Task SecurityHeaders_ProtectApiAndAuthenticatedHtml_WithoutChangingLogin()
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        using var unauthorized = await fixture.Client.GetAsync("/api/equipment");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("nosniff", Assert.Single(unauthorized.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(unauthorized.Headers.GetValues("Referrer-Policy")));
        Assert.True(unauthorized.Headers.CacheControl?.NoStore);

        var form = await fixture.FormAsync();
        using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, "/account/session");
        pageRequest.Headers.Add("Cookie", UiAuthFixture.Cookie(login));
        using var page = await fixture.Client.SendAsync(pageRequest);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl?.NoStore);

        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/equipment");
        apiRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Auth.AccessToken);
        using var api = await fixture.Client.SendAsync(apiRequest);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.True(api.Headers.CacheControl?.NoStore);
    }

    /// <summary>Пользовательский HTML сохраняется как текст и экранируется на SSR-странице оборудования.</summary>
    [Fact]
    public async Task EquipmentName_HtmlPayload_IsEncodedInRenderedPage()
    {
        await using var fixture = new UiAuthFixture();
        await fixture.StartAsync();
        const string payload = "<script>alert('audit')</script>";
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/equipment");
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Auth.AccessToken);
        create.Content = JsonContent.Create(new EquipmentRequest
        {
            Name = payload,
            InventoryNumber = "AUDIT-" + Guid.NewGuid().ToString("N"),
            Location = "Изолированный тест",
            Status = "Исправно"
        });
        using var created = await fixture.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var form = await fixture.FormAsync();
        using var login = await fixture.PostAsync("/account/login", form.Cookie, form.Token);
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, "/equipment");
        pageRequest.Headers.Add("Cookie", UiAuthFixture.Cookie(login));
        using var page = await fixture.Client.SendAsync(pageRequest);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain(payload, html);
        Assert.Contains(HtmlEncoder.Default.Encode(payload), html);
    }

    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
