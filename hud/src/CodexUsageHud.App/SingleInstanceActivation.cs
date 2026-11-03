using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace CodexUsageHud.App;

public sealed class SingleInstanceActivationServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<Task> _onActivateAsync;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listenTask;
    private bool _disposed;

    public SingleInstanceActivationServer(string appDataDirectory, Func<Task> onActivateAsync)
    {
        ArgumentNullException.ThrowIfNull(onActivateAsync);
        _pipeName = GetPipeName(appDataDirectory);
        _onActivateAsync = onActivateAsync;
        _listenTask = Task.Run(ListenLoopAsync);
    }

    public static string GetPipeName(string appDataDirectory)
    {
        var canonical = Path.GetFullPath(appDataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"CodexUsageHud.Activate.{Convert.ToHexString(digest.AsSpan(0, 12))}";
    }

    public static async Task<bool> TrySignalAsync(string appDataDirectory, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return false;
        var pipeName = GetPipeName(appDataDirectory);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var remaining = timeout - stopwatch.Elapsed;
            using var attempt = new CancellationTokenSource(
                remaining < TimeSpan.FromMilliseconds(350) ? remaining : TimeSpan.FromMilliseconds(350));
            try
            {
                await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(attempt.Token);
                await client.WriteAsync(new byte[] { 1 }, attempt.Token);
                await client.FlushAsync(attempt.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            var retryDelay = timeout - stopwatch.Elapsed;
            if (retryDelay <= TimeSpan.Zero) break;
            await Task.Delay(retryDelay < TimeSpan.FromMilliseconds(80)
                ? retryDelay : TimeSpan.FromMilliseconds(80));
        }
        return false;
    }

    private async Task ListenLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token);
                var signal = new byte[1];
                if (await server.ReadAsync(signal, _shutdown.Token) == 1 && signal[0] == 1)
                {
                    try { await _onActivateAsync(); }
                    catch { }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(80, _shutdown.Token);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        try { _listenTask.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        _shutdown.Dispose();
    }
}
