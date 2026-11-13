using Microsoft.Extensions.Logging;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>
/// .NET drops the Authorization header whenever it follows a redirect, so a KTC base URL that redirects (http to https,
/// a missing or differently-cased virtual directory, a load balancer) turns every call into "401 Invalid token" even though
/// login works. Auto-redirect is switched off on the KTC clients and this handler follows redirects to the same host itself,
/// keeping the Bearer header, and logs the URL that Ktc:BaseUrl should be set to.
/// </summary>
public class KtcRedirectHandler : DelegatingHandler
{
    private readonly ILogger<KtcRedirectHandler> _log;
    private static int _warned;
    public KtcRedirectHandler(ILogger<KtcRedirectHandler> log) => _log = log;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var mediaType = request.Content?.Headers.ContentType;
        var res = await base.SendAsync(request, ct);
        for (var hop = 0; hop < 5 && IsRedirect((int)res.StatusCode) && res.Headers.Location is { } loc; hop++)
        {
            var from = request.RequestUri!;
            var to = loc.IsAbsoluteUri ? loc : new Uri(from, loc);
            if (!string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("KTC redirected {From} to another host {To}; not following it. Set Ktc:BaseUrl to the final KTC address.", from.GetLeftPart(UriPartial.Path), to.GetLeftPart(UriPartial.Path));
                return res;
            }
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                _log.LogWarning("KTC redirected {From} to {To} (HTTP {Status}). Following it, but set Ktc:BaseUrl to match the target to avoid the extra hop.", from.GetLeftPart(UriPartial.Path), to.GetLeftPart(UriPartial.Path), (int)res.StatusCode);

            var keepMethod = (int)res.StatusCode is 307 or 308 || request.Method == HttpMethod.Get;
            var next = new HttpRequestMessage(keepMethod ? request.Method : HttpMethod.Get, to) { Version = request.Version };
            foreach (var h in request.Headers) next.Headers.TryAddWithoutValidation(h.Key, h.Value);
            foreach (var o in request.Options) ((IDictionary<string, object?>)next.Options)[o.Key] = o.Value;
            if (keepMethod && body != null) { next.Content = new ByteArrayContent(body); if (mediaType != null) next.Content.Headers.ContentType = mediaType; }
            res.Dispose();
            request = next;
            res = await base.SendAsync(request, ct);
        }
        return res;
    }

    private static bool IsRedirect(int s) => s is 301 or 302 or 303 or 307 or 308;
}
