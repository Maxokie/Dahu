using System.Net.Sockets;

namespace Dahu.Core;

public static class Net
{
    public static async Task<bool> PortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    public static async Task<T> WithTimeout<T>(Func<CancellationToken, T> work, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(() => work(cts.Token), cts.Token);
        var delay = Task.Delay(timeout, cts.Token);
        var done = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (done != task)
        {
            cts.Cancel();
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"Délai dépassé ({timeout.TotalSeconds:0}s)");
        }
        cts.Cancel();
        return await task.ConfigureAwait(false);
    }

    public static Task WithTimeout(Action<CancellationToken> work, TimeSpan timeout, CancellationToken ct = default)
        => WithTimeout<object?>(c => { work(c); return null; }, timeout, ct);

    public static string ShortName(string host)
    {
        var i = host.IndexOf('.');
        return i > 0 ? host[..i] : host;
    }

    public static bool SameHost(string? a, string? b)
        => a != null && b != null && string.Equals(ShortName(a), ShortName(b), StringComparison.OrdinalIgnoreCase);
}
