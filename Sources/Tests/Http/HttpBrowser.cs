using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class HttpBrowser : IDisposable
{
    internal HttpClient Client { get; }
    internal CookieContainer Cookies { get; }
    internal string? CsrfToken { get; private set; }

    internal HttpBrowser(Uri address, CookieContainer? cookies = null)
    {
        Cookies = cookies ?? new();
        Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CookieContainer = Cookies }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
    }

    internal async Task<JsonObject> RefreshCsrfAsync()
    {
        using var response = await Client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("Missing session response.");
        CsrfToken = json["csrfToken"]!.GetValue<string>();
        return json;
    }

    internal async Task<JsonObject> LoginAsync(string email, string password)
    {
        await RefreshCsrfAsync();
        using var response = await SendAsync(HttpMethod.Post, "/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("Missing login response.");
        CsrfToken = json["csrfToken"]!.GetValue<string>();
        return json;
    }

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool includeCsrf = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (includeCsrf && CsrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", CsrfToken);
        }
        return await Client.SendAsync(request);
    }

    public void Dispose() => Client.Dispose();
}
