using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace OpenDesk;

internal sealed record NetworkChoice(IPAddress Address, int PrefixLength, string Label)
{
    internal static NetworkChoice[] GetAvailable()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(adapter => (adapter.Name, Properties: adapter.GetIPProperties()))
            .OrderByDescending(adapter => adapter.Properties.GatewayAddresses.Any(
                gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(adapter => adapter.Properties.UnicastAddresses
                .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork &&
                    item.PrefixLength is > 0 and < 32 &&
                    !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(item => new NetworkChoice(item.Address, item.PrefixLength, adapter.Name)))
            .Append(new NetworkChoice(IPAddress.Loopback, 32, "This PC only"))
            .ToArray();
    }

    internal bool Includes(IPAddress? peer)
    {
        if (peer?.AddressFamily != AddressFamily.InterNetwork)
            return false;

        byte[] local = Address.GetAddressBytes();
        byte[] remote = peer.GetAddressBytes();
        for (int bit = 0; bit < PrefixLength; bit += 8)
        {
            int mask = 255 << Math.Max(0, 8 - (PrefixLength - bit));
            if (((local[bit / 8] ^ remote[bit / 8]) & mask) != 0)
                return false;
        }

        return true;
    }

    public override string ToString() => $"{Label}  ·  {Address}";
}

internal sealed class ShareSession : IAsyncDisposable
{
    internal const int Port = 47824;
    private const int MaxConnections = 8;
    private static readonly byte[] Ready = "ready"u8.ToArray();

    private readonly NetworkChoice network;
    private readonly DisplayChoice display;
    private readonly StreamProfile profile;
    private readonly Func<StreamProfile>? quality;
    private readonly int port;
    private readonly IPAddress? expectedViewer;
    private readonly VirtualDesktop.Target? target;
    private readonly Func<bool>? allowTouch;
    private readonly TouchInput? touch;
    private volatile DisplayChoice latestDisplay;
    private readonly string authority;
    private readonly string origin;
    private readonly string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly CancellationTokenSource ending = new();
    private readonly object frameLock = new();
    private TaskCompletionSource nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Frame? latest;
    private WebApplication? app;
    private Task? captureTask;
    private int connections;
    private int viewers;
    private volatile string? captureError;

    internal ShareSession(NetworkChoice network, DisplayChoice display, StreamProfile profile,
        int port = Port, IPAddress? expectedViewer = null, VirtualDesktop.Target? target = null,
        Func<bool>? allowTouch = null, Func<StreamProfile>? quality = null)
    {
        this.network = network;
        this.display = display;
        this.profile = profile;
        this.quality = quality;
        this.port = port;
        this.expectedViewer = expectedViewer;
        this.target = target;
        this.allowTouch = allowTouch;
        touch = allowTouch is null ? null : new TouchInput();
        latestDisplay = display;
        authority = $"{network.Address}:{port}";
        origin = $"https://{authority}";
    }

    internal string Invite => $"{origin}/#{key}";
    internal int Viewers => Volatile.Read(ref viewers);
    internal string DeviceName => latestDisplay.DeviceName;
    internal string? CaptureError => captureError;

    internal bool MatchesKey(string candidate) => candidate.Length == key.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(key));

    internal async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            server.Listen(network.Address, port, listener =>
            {
                listener.Protocols = HttpProtocols.Http1;
                listener.UseHttps(HostCertificate.Certificate);
            });
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!network.Includes(context.Connection.RemoteIpAddress) ||
                (expectedViewer is not null && !expectedViewer.Equals(context.Connection.RemoteIpAddress)) ||
                !string.Equals(context.Request.Host.Value, authority, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 403;
                return;
            }

            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] =
                $"default-src 'none'; script-src 'self'; style-src 'self'; img-src blob:; " +
                $"connect-src 'self' wss://{authority}; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            await next(context);
        });
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            AllowedOrigins = { origin }
        });
        MapAsset("/", "index.html", "text/html; charset=utf-8");
        MapAsset("/viewer.css", "viewer.css", "text/css; charset=utf-8");
        MapAsset("/viewer.js", "viewer.js", "text/javascript; charset=utf-8");
        app.MapGet("/stream", StreamAsync);
        if (touch is not null)
            app.MapPost("/touch", TouchAsync);

        await app.StartAsync();
        captureTask = Task.Run(CaptureAsync);
    }

    private async Task TouchAsync(HttpContext context)
    {
        var request = context.Request;
        if (Viewers == 0 || ending.IsCancellationRequested || allowTouch?.Invoke() != true ||
            request.Headers.Origin != origin || !MatchesKey(request.Headers["X-OpenDesk-Key"].ToString()))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (request.ContentLength is not (>= 7 and <= 25))
        {
            context.Response.StatusCode = 400;
            return;
        }
        using var reader = new StreamReader(request.Body);
        string[] tokens = (await reader.ReadToEndAsync(context.RequestAborted)).Split(' ');
        if (tokens.Length != 4 || tokens[0] is not ("d" or "m" or "u" or "c") ||
            !int.TryParse(tokens[1], out int id) || id < 0 ||
            !ushort.TryParse(tokens[2], out ushort x) || !ushort.TryParse(tokens[3], out ushort y))
        {
            context.Response.StatusCode = 400;
            return;
        }
        if (allowTouch?.Invoke() != true || ending.IsCancellationRequested)
        {
            context.Response.StatusCode = 403;
            return;
        }
        var current = latestDisplay;
        var bounds = current.Bounds;
        Point point = new(bounds.Left + x * (bounds.Width - 1) / ushort.MaxValue,
            bounds.Top + y * (bounds.Height - 1) / ushort.MaxValue);
        if (tokens[0] != "c" && Screen.FromPoint(point).DeviceName != current.DeviceName)
        {
            touch!.Cancel();
            context.Response.StatusCode = 204;
        }
        else
        {
            context.Response.StatusCode = touch!.Send(tokens[0][0], id, point) ? 204 : 409;
        }
    }

    private void MapAsset(string path, string name, string contentType)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OpenDesk.Viewer.{name}")
            ?? throw new InvalidOperationException($"Viewer asset missing: {name}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] content = buffer.ToArray();
        app!.MapGet(path, () => Results.Bytes(content, contentType));
    }

    private async Task StreamAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest ||
            !string.Equals(context.Request.Headers.Origin, origin, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 403;
            return;
        }

        if (Interlocked.Increment(ref connections) > MaxConnections)
        {
            Interlocked.Decrement(ref connections);
            context.Response.StatusCode = 503;
            return;
        }

        try
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                ending.Token, context.RequestAborted);
            try
            {
                byte[] challenge = Encoding.ASCII.GetBytes(key);
                var message = new byte[challenge.Length];
                using (var authentication = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    authentication.CancelAfter(TimeSpan.FromSeconds(5));
                    var received = await socket.ReceiveAsync(message.AsMemory(), authentication.Token);
                    if (received.MessageType != WebSocketMessageType.Text || !received.EndOfMessage ||
                        received.Count != challenge.Length ||
                        !CryptographicOperations.FixedTimeEquals(message, challenge))
                    {
                        if (socket.State == WebSocketState.Open)
                            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Invalid invite",
                                authentication.Token);
                        return;
                    }
                }

                Interlocked.Increment(ref viewers);
                try
                {
                    await socket.SendAsync(Ready.AsMemory(), WebSocketMessageType.Text, true, lifetime.Token);
                    Frame? previous = null;
                    while (!lifetime.IsCancellationRequested)
                    {
                        Frame frame = await WaitForFrameAsync(previous, lifetime.Token);
                        using var delivery = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        delivery.CancelAfter(TimeSpan.FromSeconds(8));
                        await socket.SendAsync(frame.Bytes.AsMemory(), WebSocketMessageType.Binary, true, delivery.Token);
                        var ack = await socket.ReceiveAsync(message.AsMemory(), delivery.Token);
                        if (ack.MessageType != WebSocketMessageType.Text || !ack.EndOfMessage ||
                            ack.Count != 1 || message[0] != (byte)'1')
                            break;
                        previous = frame;
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref viewers) == 0)
                        touch?.Cancel();
                }
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException)
            {
                // Closing a tab or ending sharing can interrupt either direction.
            }
        }
        finally
        {
            Interlocked.Decrement(ref connections);
        }
    }

    private sealed record Frame(byte[] Bytes);

    private async Task<Frame> WaitForFrameAsync(Frame? previous, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Task changed;
            lock (frameLock)
            {
                if (latest is not null && !ReferenceEquals(latest, previous))
                    return latest;
                changed = nextFrame.Task;
            }
            await changed.WaitAsync(cancellation);
        }
    }

    private async Task CaptureAsync()
    {
        DesktopCapture? capture = null;
        StreamProfile activeProfile = quality?.Invoke() ?? profile;
        Rectangle captureBounds = Rectangle.Empty;
        long? unavailableSince = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / activeProfile.Fps));
            Stopwatch topology = Stopwatch.StartNew();
            Stopwatch published = Stopwatch.StartNew();
            while (await timer.WaitForNextTickAsync(ending.Token))
            {
                StreamProfile selectedProfile = quality?.Invoke() ?? profile;
                bool newProfile = activeProfile != selectedProfile;
                if (capture is null || newProfile || topology.ElapsedMilliseconds >= 1000)
                {
                    var current = target is { } monitor ? VirtualDesktop.GetDisplay(monitor) :
                        DisplayChoice.GetActive().FirstOrDefault(item =>
                            item.DeviceName == display.DeviceName && item.Identity == display.Identity);
                    if (current is null && target is not null)
                    {
                        touch?.Cancel();
                        unavailableSince ??= Environment.TickCount64;
                        if (Environment.TickCount64 - unavailableSince < 3500)
                            continue;
                    }
                    if (current is null || (expectedViewer is null && current.Bounds != display.Bounds))
                        throw new InvalidOperationException("The selected display changed. Choose it again to resume.");
                    unavailableSince = null;
                    bool newDisplay = captureBounds != current.Bounds || current.DeviceName != latestDisplay.DeviceName;
                    if (capture is null || newDisplay || newProfile)
                    {
                        if (newDisplay) touch?.Cancel();
                        capture?.Dispose();
                        capture = new DesktopCapture(current.Bounds, selectedProfile);
                        activeProfile = selectedProfile;
                        timer.Period = TimeSpan.FromSeconds(1.0 / activeProfile.Fps);
                        captureBounds = current.Bounds;
                        lock (frameLock)
                            latest = null;
                    }
                    latestDisplay = current;
                    topology.Restart();
                }

                if (allowTouch?.Invoke() == false)
                    touch?.Cancel();
                else
                    touch?.CancelIfIdle();

                if (Viewers == 0)
                {
                    lock (frameLock)
                        latest = null;
                    continue;
                }

                byte[] frame = capture.Grab();
                lock (frameLock)
                {
                    if (ReferenceEquals(frame, latest?.Bytes) && published.ElapsedMilliseconds < 1500)
                        continue;
                    latest = new Frame(frame);
                    published.Restart();
                    nextFrame.TrySetResult();
                    nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested) { }
        catch (Exception error)
        {
            captureError = error.Message;
            await ending.CancelAsync();
        }
        finally
        {
            capture?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ending.CancelAsync();
        touch?.Dispose();
        try
        {
            if (captureTask is not null)
                await captureTask;
            if (app is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await app.StopAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                await app.DisposeAsync();
            }
        }
        finally
        {
            ending.Dispose();
        }
    }
}
