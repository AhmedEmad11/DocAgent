using System.Net;
using System.Text.RegularExpressions;
using DocAgent.Core.Entities;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// The only HTTP layer between the provider SDKs and the network. It (1) allows requests to the provider's fixed host only
/// (KEY-8) and never follows redirects, (2) turns every error response into an <see cref="LlmProviderException"/> with a
/// fixed message, so no response body or header (which can echo a key fragment) travels further (KEY-7, SEC-2).
/// There is deliberately no logging handler and no resilience handler here: the HttpClient is created directly, so
/// IHttpClientFactory header logging cannot see the Authorization / x-api-key / x-goog-api-key headers.
/// </summary>
public sealed partial class ProviderHttpHandler(LlmProvider provider, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaxBodyChars = 4096;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, LlmProviders.Host(provider), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Requests to this host are not allowed for this provider.");
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > MaxBodyChars)
            {
                body = body[..MaxBodyChars];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            body = "";
        }

        var status = (int)response.StatusCode;
        var retryAfter = ParseRetryAfter(response);
        response.Dispose();
        throw new LlmProviderException(Classify(status, body), status, retryAfter);
    }

    /// <summary>Maps a failed response to what the user can do about it. Provider quirks are listed in the SRS notes (PROGRESS.md).</summary>
    public static LlmFailureKind Classify(int status, string body)
    {
        switch (status)
        {
            case 401:
            case 403:
                return LlmFailureKind.InvalidKey;
            case 402:
                return LlmFailureKind.NoCredit;
            case 404:
                return LlmFailureKind.UnknownModel;
            case 429:
                return NoCreditText().IsMatch(body) ? LlmFailureKind.NoCredit : LlmFailureKind.RateLimited;
            case 400:
                // Gemini answers a wrong key with 400 API_KEY_INVALID; Anthropic answers an empty balance with 400.
                if (InvalidKeyText().IsMatch(body))
                {
                    return LlmFailureKind.InvalidKey;
                }

                if (NoCreditText().IsMatch(body))
                {
                    return LlmFailureKind.NoCredit;
                }

                return UnknownModelText().IsMatch(body) ? LlmFailureKind.UnknownModel : LlmFailureKind.Rejected;
            case 408:
            case >= 500:
                return LlmFailureKind.Unavailable; // includes Anthropic 529 overloaded
            default:
                return LlmFailureKind.Rejected;
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? value = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (value is null)
        {
            return null;
        }

        // A hostile or broken header must not stall a client for long.
        return value.Value < TimeSpan.Zero ? TimeSpan.Zero : value.Value > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : value.Value;
    }

    [GeneratedRegex("insufficient_quota|insufficient credit|credit balance|billing|exceeded your current quota|payment required|out of credits", RegexOptions.IgnoreCase)]
    private static partial Regex NoCreditText();

    [GeneratedRegex("API_KEY_INVALID|API key not valid|invalid api key|invalid x-api-key|incorrect api key|authentication", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidKeyText();

    [GeneratedRegex("model.{0,80}(not found|does not exist|not a valid|invalid|unknown|not supported|is not available)|(unknown|invalid|unsupported|not a valid) model|no endpoints found", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UnknownModelText();
}
