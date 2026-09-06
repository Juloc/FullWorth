using System.Net.Http.Headers;
using System.Text;

namespace FullWorth.FinTs;

public interface IFinTsTransport
{
    Task<byte[]> SendAsync(Uri endpoint, byte[] message, CancellationToken cancellationToken);
}

public sealed class FinTsHttpTransport(HttpClient httpClient) : IFinTsTransport
{
    public async Task<byte[]> SendAsync(Uri endpoint, byte[] message, CancellationToken cancellationToken)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            throw new FinTsException("FinTS endpoint must use HTTPS.", "transport_https");
        var encoded = Convert.ToBase64String(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(encoded, Encoding.ASCII, "text/plain")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new FinTsException($"FinTS HTTP request failed with status {(int)response.StatusCode}.", "transport_http");
        try
        {
            return Convert.FromBase64String(new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray()));
        }
        catch (FormatException exception)
        {
            throw new FinTsException("FinTS response was not valid base64.", "transport_base64", exception);
        }
    }
}
