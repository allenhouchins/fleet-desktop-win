using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FleetDesktop;

/// <summary>
/// Enforces a single running instance per user.
///
/// First-instance path: acquires a named mutex and starts a named-pipe server.
/// Later-instance path: connects to the pipe, sends the incoming fleet:// URL,
/// then exits — the running app handles the URL.
///
/// Names are scoped per-user (Local\) so two different users on the same machine
/// each get their own instance.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly string _name;
    private Mutex? _mutex;
    private bool _mutexOwned;
    private CancellationTokenSource? _serverCts;

    public event Action<string>? OnUrlReceived;

    public SingleInstance(string name)
    {
        _name = name;
    }

    /// <summary>
    /// Returns true if this is the first instance (we own the mutex and the pipe server is running).
    /// Returns false if another instance is running; in that case <paramref name="urlToForward"/>
    /// (if non-null) has been sent to the running instance.
    /// </summary>
    public bool AcquireOrForward(string? urlToForward)
    {
        _mutex = new Mutex(initiallyOwned: false, name: $"Local\\{_name}.mutex");
        try
        {
            _mutexOwned = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner crashed — we now own it.
            _mutexOwned = true;
        }

        if (!_mutexOwned)
        {
            if (!string.IsNullOrEmpty(urlToForward))
            {
                TryForwardUrl(urlToForward);
            }
            return false;
        }

        StartPipeServer();
        return true;
    }

    private void StartPipeServer()
    {
        _serverCts = new CancellationTokenSource();
        var ct = _serverCts.Token;
        _ = Task.Run(() => RunPipeServerLoop(ct), ct);
    }

    private async Task RunPipeServerLoop(CancellationToken ct)
    {
        var pipeName = PipeName(_name);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var url = await reader.ReadToEndAsync().ConfigureAwait(false);
                url = url?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(url))
                {
                    OnUrlReceived?.Invoke(url);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Swallow and continue; the next iteration will reopen the pipe.
            }
        }
    }

    private void TryForwardUrl(string url)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(_name), PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(url);
            writer.Flush();
        }
        catch
        {
            // Best-effort — if the other instance can't be reached, the URL just won't be handled.
        }
    }

    private static string PipeName(string name) => $"{name}.pipe";

    public void Dispose()
    {
        _serverCts?.Cancel();
        _serverCts?.Dispose();
        _serverCts = null;

        if (_mutex != null)
        {
            if (_mutexOwned)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutexOwned = false;
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
