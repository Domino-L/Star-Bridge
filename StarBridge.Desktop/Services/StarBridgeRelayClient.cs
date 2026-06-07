using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace StarBridge.Desktop;

public sealed class StarBridgeRelayClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<string> _baseUrlProvider;
    private readonly Func<string?> _relayKeyProvider;
    private readonly Func<string?> _authTokenProvider;

    public StarBridgeRelayClient(
        HttpClient httpClient,
        Func<string> baseUrlProvider,
        Func<string?> relayKeyProvider,
        Func<string?> authTokenProvider)
    {
        _httpClient = httpClient;
        _baseUrlProvider = baseUrlProvider;
        _relayKeyProvider = relayKeyProvider;
        _authTokenProvider = authTokenProvider;
    }

    public Uri BuildUri(string path)
    {
        var baseUrl = _baseUrlProvider().Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.scstarbridge.com";
        }

        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl += "/";
        }

        return new Uri(new Uri(baseUrl), path);
    }

    public async Task<T?> GetFromJsonAsync<T>(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path));
        AddAuthHeaders(request);
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public Task<HttpResponseMessage> GetAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path));
        AddAuthHeaders(request);
        return _httpClient.SendAsync(request);
    }

    public Task<HttpResponseMessage> PostJsonAsync<T>(string path, T payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(path))
        {
            Content = JsonContent.Create(payload)
        };
        AddAuthHeaders(request);
        return _httpClient.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        AddAuthHeaders(request);
        return _httpClient.SendAsync(request, cancellationToken);
    }

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        var key = _relayKeyProvider()?.Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Add("X-StarBridge-Key", key);
        }

        var authToken = _authTokenProvider()?.Trim();
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        }
    }
}
