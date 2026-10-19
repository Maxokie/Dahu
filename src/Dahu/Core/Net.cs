using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Dahu.Core;

public static class Net
{
    private static readonly Regex HostRx = new(@"^[A-Za-z0-9][A-Za-z0-9._:\-]*$", RegexOptions.Compiled);

    public static bool IsValidHost(string? host)
        => !string.IsNullOrWhiteSpace(host) && host.Length <= 255 && HostRx.IsMatch(host);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
            Observe(task);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(string.Format(Loc.T("Err.Timeout"), timeout.TotalSeconds.ToString("0")));
        }
        cts.Cancel();
        return await task.ConfigureAwait(false);
    }

    public static Task WithTimeout(Action<CancellationToken> work, TimeSpan timeout, CancellationToken ct = default)
        => WithTimeout<object?>(c => { work(c); return null; }, timeout, ct);

    public static void Observe(Task task)
        => _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    public static string ShortName(string host)
    {
        var i = host.IndexOf('.');
        return i > 0 ? host[..i] : host;
    }

    public static bool SameHost(string? a, string? b)
        => a != null && b != null && string.Equals(ShortName(a), ShortName(b), StringComparison.OrdinalIgnoreCase);
}
