using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ktc.Infrastructure.KtcApi;

public class ApiHealthTracker : IApiHealthTracker
{
    private record Sample(DateTime Utc, int Status, long Ms, bool Timeout, string? Error);
    private class Bucket { public string Method = ""; public string Url = ""; public readonly LinkedList<Sample> Samples = new(); public int Consecutive; public readonly object Lock = new(); }
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private const int Window = 100;

    private static readonly Regex Num = new(@"/\d+(?=/|$)", RegexOptions.Compiled);
    private static readonly Regex Guid = new(@"/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?=/|$)", RegexOptions.Compiled);

    public static string Normalize(string url)
    {
        var path = "/" + url.Split('?')[0].TrimStart('/');
        return Num.Replace(Guid.Replace(path, "/{id}"), "/{id}");
    }

    public void Record(string method, string url, int statusCode, long elapsedMs, bool timeout, string? error)
    {
        var path = Normalize(url);
        var b = _buckets.GetOrAdd(method + " " + path, _ => new Bucket { Method = method, Url = path });
        var failed = IsFailure(statusCode, timeout, error);
        lock (b.Lock)
        {
            b.Samples.AddLast(new Sample(DateTime.UtcNow, statusCode, elapsedMs, timeout, error));
            while (b.Samples.Count > Window) b.Samples.RemoveFirst();
            b.Consecutive = failed ? b.Consecutive + 1 : 0;
        }
    }

    private static bool IsFailure(int status, bool timeout, string? error) => timeout || status == 0 || status >= 500 || status == 408 || status == 429 || error != null && status == 0;

    public IReadOnlyList<ApiHealthRow> Snapshot()
    {
        var rows = new List<ApiHealthRow>();
        foreach (var b in _buckets.Values)
        {
            lock (b.Lock)
            {
                if (b.Samples.Count == 0) continue;
                var list = b.Samples.ToList();
                var fails = list.Count(s => IsFailure(s.Status, s.Timeout, s.Error));
                var ok = list.Where(s => !IsFailure(s.Status, s.Timeout, s.Error)).Select(s => (double)s.Ms).ToList();
                var recent = list.TakeLast(20).Where(s => !IsFailure(s.Status, s.Timeout, s.Error)).Select(s => (double)s.Ms).ToList();
                var avg = recent.Count > 0 ? recent.Average() : 0;
                var sorted = ok.OrderBy(x => x).ToList();
                var p95 = sorted.Count > 0 ? sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.95))] : 0;
                var last = list[^1];
                var color = b.Consecutive > 3 || avg > 5000 ? "red" : avg >= 2000 ? "yellow" : "green";
                rows.Add(new ApiHealthRow(b.Url, b.Method, list.Count, fails, list.Count(s => s.Timeout),
                    Math.Round(100.0 * (list.Count - fails) / list.Count, 1), Math.Round(avg), Math.Round(p95), last.Ms, b.Consecutive, last.Utc, list.LastOrDefault(s => s.Error != null)?.Error, color));
            }
        }
        return rows.OrderByDescending(r => r.Color == "red").ThenByDescending(r => r.Color == "yellow").ThenByDescending(r => r.Calls).ToList();
    }
}

/// <summary>Measures every logical KTC call (outside the Polly pipeline, so retries are included in the elapsed time).</summary>
public class ApiHealthHandler : DelegatingHandler
{
    private readonly IApiHealthTracker _tracker;
    public ApiHealthHandler(IApiHealthTracker tracker) => _tracker = tracker;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var url = request.RequestUri?.PathAndQuery ?? "";
        try
        {
            var resp = await base.SendAsync(request, ct);
            _tracker.Record(request.Method.Method, url, (int)resp.StatusCode, sw.ElapsedMilliseconds, false, null);
            return resp;
        }
        catch (Exception ex) when (ex is TimeoutRejectedException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            _tracker.Record(request.Method.Method, url, 0, sw.ElapsedMilliseconds, true, "Timeout");
            throw;
        }
        catch (BrokenCircuitException ex)
        {
            _tracker.Record(request.Method.Method, url, 0, sw.ElapsedMilliseconds, false, "Circuit open: " + ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _tracker.Record(request.Method.Method, url, 0, sw.ElapsedMilliseconds, false, ex.Message);
            throw;
        }
    }
}
