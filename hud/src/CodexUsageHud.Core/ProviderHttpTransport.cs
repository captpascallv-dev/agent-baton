using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.IO;

namespace CodexUsageHud.Core;

public sealed record AllowlistedHttpRequest(
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body = null);

public sealed record AllowlistedHttpResponse(
    int StatusCode,
    string Body,
    Uri FinalUrl);

public interface IAllowlistedHttpSender
{
    Task<AllowlistedHttpResponse> SendAsync(AllowlistedHttpRequest request, CancellationToken cancellationToken);
}

public static class ProviderHttpAllowlist
{
    public const string CursorHost = "cursor.com";
    public const string GrokHost = "cli-chat-proxy.grok.com";
    public const string ChatgptHost = "chatgpt.com";
    public const string AnthropicHost = "api.anthropic.com";
    public static readonly Uri CursorUsageSummary = new("https://cursor.com/api/usage-summary");
    public static readonly Uri CursorSandUsage = new("https://cursor.com/api/dashboard/get-sand-usage-status");
    public static readonly Uri GrokBilling = new("https://cli-chat-proxy.grok.com/v1/billing?format=credits");
    public static readonly Uri GrokSettings = new("https://cli-chat-proxy.grok.com/v1/settings");
    public static readonly Uri PiCodexUsage = new("https://chatgpt.com/backend-api/wham/usage");
    public static readonly Uri ClaudeOAuthUsage = new("https://api.anthropic.com/api/oauth/usage");

    public static bool IsAllowed(Uri url)
    {
        if (!url.IsAbsoluteUri || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (url.Host.Equals(CursorHost, StringComparison.OrdinalIgnoreCase))
        {
            return url.AbsolutePath.Equals(CursorUsageSummary.AbsolutePath, StringComparison.Ordinal) ||
                   url.AbsolutePath.Equals(CursorSandUsage.AbsolutePath, StringComparison.Ordinal);
        }

        if (url.Host.Equals(GrokHost, StringComparison.OrdinalIgnoreCase))
        {
            return url.AbsolutePath.Equals(GrokBilling.AbsolutePath, StringComparison.Ordinal) ||
                   url.AbsolutePath.Equals(GrokSettings.AbsolutePath, StringComparison.Ordinal);
        }

        if (url.Host.Equals(ChatgptHost, StringComparison.OrdinalIgnoreCase))
            return url.AbsolutePath.Equals(PiCodexUsage.AbsolutePath, StringComparison.Ordinal);

        if (url.Host.Equals(AnthropicHost, StringComparison.OrdinalIgnoreCase))
        {
            return url.IsDefaultPort &&
                   string.IsNullOrEmpty(url.UserInfo) &&
                   string.IsNullOrEmpty(url.Query) &&
                   url.AbsolutePath.Equals(ClaudeOAuthUsage.AbsolutePath, StringComparison.Ordinal);
        }

        return false;
    }
}

public sealed class RedirectRefusingHandler : DelegatingHandler
{
    public RedirectRefusingHandler(HttpMessageHandler inner) : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !ProviderHttpAllowlist.IsAllowed(request.RequestUri))
            throw new InvalidOperationException("http_host_not_allowlisted");

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new InvalidOperationException("http_redirect_refused");
        }

        return response;
    }
}

public sealed class AllowlistedHttpsSender : IAllowlistedHttpSender, IDisposable
{
    private readonly HttpClient _client;

    public AllowlistedHttpsSender(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        HttpMessageHandler pipeline = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = timeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                      System.Security.Authentication.SslProtocols.Tls13,
            },
        };
        if (handler is null)
            pipeline = new RedirectRefusingHandler(pipeline);
        _client = new HttpClient(pipeline, disposeHandler: true)
        {
            Timeout = timeout,
        };
        _client.DefaultRequestHeaders.ExpectContinue = false;
    }

    public async Task<AllowlistedHttpResponse> SendAsync(AllowlistedHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!ProviderHttpAllowlist.IsAllowed(request.Url))
            throw new InvalidOperationException("http_host_not_allowlisted");

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        foreach (var header in request.Headers)
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.Headers.Location is not null && (int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("http_redirect_refused");
        if (response.RequestMessage?.RequestUri is { } final && !ProviderHttpAllowlist.IsAllowed(final))
            throw new InvalidOperationException("http_host_not_allowlisted");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = await ProviderHttpErrors.ReadBoundedUtf8Async(stream, ProviderHttpErrors.MaxBodyBytes,
            cancellationToken).ConfigureAwait(false);
        return new AllowlistedHttpResponse((int)response.StatusCode, body, request.Url);
    }

    public void Dispose() => _client.Dispose();
}

public sealed class ScriptedHttpSender : IAllowlistedHttpSender
{
    private readonly Func<AllowlistedHttpRequest, CancellationToken, Task<AllowlistedHttpResponse>> _handler;

    public ScriptedHttpSender(Func<AllowlistedHttpRequest, CancellationToken, Task<AllowlistedHttpResponse>> handler)
    {
        _handler = handler;
    }

    public Task<AllowlistedHttpResponse> SendAsync(AllowlistedHttpRequest request,
        CancellationToken cancellationToken) => _handler(request, cancellationToken);
}

public static class ProviderHttpErrors
{
    public const int MaxBodyBytes = 65536;

    public static readonly HashSet<string> StableCodes = new(StringComparer.Ordinal)
    {
        "http_host_not_allowlisted", "http_redirect_refused", "http_body_too_large", "http_timeout",
        "http_network", "http_io", "http_failed",
    };

    public static bool IsStableCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return false;
        if (StableCodes.Contains(code)) return true;
        foreach (var character in code)
        {
            if (character is not ('_' or '-') && !char.IsAsciiLetterOrDigit(character))
                return false;
        }

        return true;
    }

    public static string Sanitize(Exception exception)
    {
        if (exception is InvalidOperationException && IsStableCode(exception.Message))
            return exception.Message;
        if (exception is OperationCanceledException) return "http_timeout";
        if (exception is HttpRequestException) return "http_network";
        if (exception is IOException) return "http_io";
        return "http_failed";
    }

    public static async Task<string> ReadBoundedUtf8Async(Stream stream, int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(8192, Math.Max(1, maxBytes))];
        using var output = new MemoryStream(Math.Min(maxBytes, 4096));
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidOperationException("http_body_too_large");
            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }
}
