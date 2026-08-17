using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cineclub.App.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Cineclub.App.Server;

/// <summary>
/// Runs Kestrel inside the Windows application. It exposes one SignalR hub and
/// the original movie through an HTTP endpoint with native byte-range support.
/// </summary>
public sealed class EmbeddedRoomServer : IAsyncDisposable
{
    private readonly int _requestedPort;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _application;
    private bool _disposed;

    public EmbeddedRoomServer(string mediaPath, string? movieTitle = null, int port = 0)
    {
        if (port is < 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _requestedPort = port;
        Room = new RoomCoordinator(mediaPath, movieTitle);
    }

    public RoomCoordinator Room { get; }

    public Uri? LocalBaseUri { get; private set; }

    public bool IsRunning => _application is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_application is not null)
            {
                throw new InvalidOperationException("Le serveur Cineclub est déjà démarré.");
            }

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(EmbeddedRoomServer).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });

            builder.Logging.ClearProviders();
            builder.Services
                .AddSignalR(options =>
                {
                    options.EnableDetailedErrors = false;
                    options.MaximumReceiveMessageSize = 64 * 1024;
                    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                });
            builder.Services.AddSingleton(Room);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 64 * 1024;
                options.Listen(IPAddress.Loopback, _requestedPort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                });
            });

            var app = builder.Build();
            app.MapGet("/health", () => Results.Json(new
            {
                status = "ok",
                roomId = Room.RoomId,
                movieTitle = Room.MovieTitle,
                participants = Room.GetSnapshot().Participants.Count,
                serverTimeUtc = DateTimeOffset.UtcNow
            }));

            app.MapMethods(
                "/media/{token}",
                [HttpMethods.Get, HttpMethods.Head],
                (HttpContext context, string token) => CreateMediaResult(context, token));
            app.MapHub<RoomHub>("/room");

            try
            {
                await app.StartAsync(cancellationToken);
                var addresses = app.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>()?
                    .Addresses;
                var address = addresses?.FirstOrDefault(value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
                if (address is null)
                {
                    throw new InvalidOperationException("Kestrel n'a publié aucune adresse locale.");
                }

                LocalBaseUri = new Uri(address.EndsWith('/') ? address : address + "/");
                _application = app;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Uri BuildMediaUri(Uri? baseUri = null)
    {
        var origin = baseUri ?? LocalBaseUri ??
            throw new InvalidOperationException("Le serveur n'est pas encore démarré.");
        return new Uri(origin, Room.MediaEndpoint.TrimStart('/'));
    }

    public Uri BuildHubUri(Uri? baseUri = null)
    {
        var origin = baseUri ?? LocalBaseUri ??
            throw new InvalidOperationException("Le serveur n'est pas encore démarré.");
        return new Uri(origin, "room");
    }

    /// <summary>
    /// Produces the text copied to friends. It contains the guest token only;
    /// the host key never leaves this process.
    /// </summary>
    public Uri BuildInviteUri(Uri? publicBaseUri = null)
    {
        var origin = publicBaseUri ?? LocalBaseUri ??
            throw new InvalidOperationException("Le serveur n'est pas encore démarré.");
        var builder = new UriBuilder(origin)
        {
            Path = "/join",
            Query = $"token={Uri.EscapeDataString(Room.AccessToken)}"
        };
        return builder.Uri;
    }

    public static RoomInvitation ParseInvitation(Uri invitationUri)
    {
        ArgumentNullException.ThrowIfNull(invitationUri);
        if (!invitationUri.IsAbsoluteUri ||
            (invitationUri.Scheme != Uri.UriSchemeHttp && invitationUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("L'invitation doit être une adresse HTTP ou HTTPS absolue.", nameof(invitationUri));
        }

        var query = QueryHelpers.ParseQuery(invitationUri.Query);
        var token = query.TryGetValue("token", out var values)
            ? values.FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("L'invitation ne contient aucun jeton de salle.", nameof(invitationUri));
        }

        var origin = new UriBuilder(invitationUri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
        return new RoomInvitation(origin, token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
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
            await StopCoreAsync(CancellationToken.None);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var app = _application;
        _application = null;
        LocalBaseUri = null;
        if (app is null)
        {
            return;
        }

        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private IResult CreateMediaResult(HttpContext context, string token)
    {
        if (!Room.IsValidAccessToken(token))
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers[HeaderNames.AcceptRanges] = "bytes";
        context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";

        var etagBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Room.MediaSizeBytes}:{Room.MediaLastWriteUtc.UtcTicks}"));
        var etag = new EntityTagHeaderValue($"\"{Convert.ToHexString(etagBytes.AsSpan(0, 12))}\"");

        return Results.File(
            Room.MediaPath,
            contentType: Room.MediaContentType,
            lastModified: Room.MediaLastWriteUtc,
            entityTag: etag,
            enableRangeProcessing: true);
    }
}
