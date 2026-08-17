using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Cineclub.App.Models;

namespace Cineclub.App.Server;

/// <summary>
/// Starts a bundled cloudflared Quick Tunnel and extracts its public HTTPS URL.
/// The process is always hidden and is killed with its child tree on shutdown.
/// </summary>
public sealed partial class CloudflareTunnelService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly string _executablePath;
    private Process? _process;
    private TunnelStatus _status = new(TunnelState.Stopped, null, null);
    private bool _disposed;

    public CloudflareTunnelService(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
    }

    public event EventHandler<TunnelStatus>? StatusChanged;

    public event EventHandler<string>? OutputReceived;

    public TunnelStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public async Task<Uri> StartAsync(
        Uri localBaseUri,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localBaseUri);
        if (!localBaseUri.IsLoopback || localBaseUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Le tunnel doit cibler un serveur HTTP local.", nameof(localBaseUri));
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(_executablePath))
            {
                var exception = new FileNotFoundException("cloudflared.exe est introuvable.", _executablePath);
                SetStatus(new TunnelStatus(TunnelState.Error, null, exception.Message));
                throw exception;
            }

            lock (_gate)
            {
                if (_process is not null)
                {
                    throw new InvalidOperationException("Le tunnel Cloudflare est déjà démarré.");
                }
            }

            SetStatus(new TunnelStatus(TunnelState.Starting, null, null));
            var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var discoveryGate = new object();
            Uri? discoveredPublicUri = null;
            var tunnelConnectionRegistered = false;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _executablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? AppContext.BaseDirectory
                },
                EnableRaisingEvents = true
            };
            process.StartInfo.ArgumentList.Add("tunnel");
            process.StartInfo.ArgumentList.Add("--url");
            process.StartInfo.ArgumentList.Add(localBaseUri.GetLeftPart(UriPartial.Authority));
            process.StartInfo.ArgumentList.Add("--no-autoupdate");

            void InspectLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                RaiseOutputReceived(line);
                var match = QuickTunnelUrlRegex().Match(line);
                lock (discoveryGate)
                {
                    if (match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var publicUri))
                    {
                        discoveredPublicUri = new Uri(publicUri.GetLeftPart(UriPartial.Authority) + "/");
                    }

                    if (line.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase))
                    {
                        tunnelConnectionRegistered = true;
                    }

                    if (discoveredPublicUri is not null && tunnelConnectionRegistered)
                    {
                        ready.TrySetResult(discoveredPublicUri);
                    }
                }
            }

            process.OutputDataReceived += (_, args) => InspectLine(args.Data);
            process.ErrorDataReceived += (_, args) => InspectLine(args.Data);
            process.Exited += (_, _) =>
            {
                var exitCode = TryGetExitCode(process);
                if (!ready.Task.IsCompleted)
                {
                    ready.TrySetException(new InvalidOperationException(
                        $"cloudflared s'est arrêté avant de fournir une adresse (code {exitCode})."));
                    return;
                }

                var wasActive = false;
                lock (_gate)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                        wasActive = true;
                    }
                }

                if (wasActive)
                {
                    SetStatus(new TunnelStatus(
                        TunnelState.Error,
                        null,
                        $"Le tunnel Cloudflare s'est arrêté (code {exitCode})."));
                    process.Dispose();
                }
            };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Impossible de démarrer cloudflared.");
                }

                lock (_gate)
                {
                    _process = process;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var publicBaseUri = await ready.Task.WaitAsync(
                    timeout ?? TimeSpan.FromSeconds(35),
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                TunnelStatus readyStatus;
                lock (_gate)
                {
                    if (!ReferenceEquals(_process, process) || process.HasExited)
                    {
                        throw new InvalidOperationException("cloudflared s'est arrêté pendant son démarrage.");
                    }

                    readyStatus = new TunnelStatus(TunnelState.Ready, publicBaseUri, null);
                    _status = readyStatus;
                }

                RaiseStatusChanged(readyStatus);
                return publicBaseUri;
            }
            catch (Exception exception)
            {
                ready.TrySetCanceled();
                await StopProcessAsync(process);
                lock (_gate)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                    }
                }

                process.Dispose();
                SetStatus(exception is OperationCanceledException
                    ? new TunnelStatus(TunnelState.Stopped, null, null)
                    : new TunnelStatus(TunnelState.Error, null, exception.Message));
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    private async Task StopCoreAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            await StopProcessAsync(process);
            process.Dispose();
        }

        SetStatus(new TunnelStatus(TunnelState.Stopped, null, null));
    }

    private void SetStatus(TunnelStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        RaiseStatusChanged(status);
    }

    private void RaiseStatusChanged(TunnelStatus status)
    {
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TunnelStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, status);
            }
            catch
            {
                // A presentation callback must never terminate the tunnel process.
            }
        }
    }

    private void RaiseOutputReceived(string line)
    {
        var handlers = OutputReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, line);
            }
            catch
            {
                // Log consumers are observational and cannot own process lifetime.
            }
        }
    }

    private static string TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch (InvalidOperationException)
        {
            return "inconnu";
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The OS already reclaimed the process.
        }
        catch (TimeoutException)
        {
            // Kill was already requested; do not block application shutdown.
        }
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex QuickTunnelUrlRegex();
}
