using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace OpenDesk;

internal sealed record ConnectedDevice(Guid Id, string Name, IPAddress Address, string Display, bool Live)
{
    public override string ToString() => $"{Name}  ·  {Address}  ·  {Display}" + (Live ? "" : "  ·  Waiting for video");
}

internal sealed class DeviceConnections(NetworkChoice network, Func<IPAddress, Task<bool>> approve,
    Func<string?> manuallySharedDisplay, Func<bool> allowTouch, Func<StreamProfile> quality,
    ViewerPreferences preferences) : IAsyncDisposable
{
    internal const int Port = 47825;
    private static readonly int[] StreamPorts = [47826, 47827];
    private static readonly byte[] DiscoveryQuery = "OPENDESK/1 DISCOVER"u8.ToArray();
    private readonly ConcurrentDictionary<int, Connection> active = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim approval = new(1);
    private static readonly Regex DeviceKey = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex DeviceName = new(@"\A[A-Za-z0-9 ._()\-]{1,40}\z", RegexOptions.CultureInvariant);
    private WebApplication? server;
    private UdpClient? udp;
    private Task? discovery;
    private Task? housekeeping;
    private long lastRequest = -5000;
    private long layoutSettlesAt;
    private volatile bool ready;
    private volatile string? topologyError;

    internal bool IsUsing(string display) => active.Values.Any(item => item.Stream.DeviceName == display);
    internal string? TopologyError => topologyError;
    internal ConnectedDevice[] GetDevices() => active.OrderBy(entry => entry.Key)
        .Select(entry => entry.Value.Snapshot()).ToArray();

    internal async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 256;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Listen(network.Address, Port, address => address.Protocols = HttpProtocols.Http1);
        });
        server = builder.Build();
        server.MapGet("/identity", (HttpContext context) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            return IsLocal(context) ? Results.Text(preferences.HostId) : Results.StatusCode(403);
        });
        server.MapPost("/connect", async (HttpContext context) =>
            await (await ConnectAsync(context)).ExecuteAsync(context));
        server.MapDelete("/connect", DisconnectClientAsync);
        udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        await server.StartAsync();
        VirtualDesktop.ParkDisplays();
        ready = true;
        discovery = DiscoverAsync(udp);
        housekeeping = RemoveIdleAsync();
    }

    private async Task DiscoverAsync(UdpClient socket)
    {
        byte[] reply = Encoding.UTF8.GetBytes($"OPENDESK/1 {network.Address} {Environment.MachineName}");
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var incoming = await socket.ReceiveAsync(stopping.Token);
                if (network.Includes(incoming.RemoteEndPoint.Address) &&
                    incoming.Buffer.AsSpan().SequenceEqual(DiscoveryQuery))
                    await socket.SendAsync(reply, incoming.RemoteEndPoint, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (SocketException) when (!stopping.IsCancellationRequested)
            {
                await Task.Delay(250);
            }
        }
    }

    private bool IsDeviceRequest(HttpContext context, string deviceId)
        => IsLocal(context) && context.Request.Headers["X-OpenDesk-Viewer"] == "1" && DeviceKey.IsMatch(deviceId);

    private bool IsLocal(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        return ready && peer is not null && network.Includes(peer) && context.Request.Host.Value == $"{network.Address}:{Port}" &&
            !context.Request.Headers.ContainsKey("Origin");
    }

    private async Task<IResult> ConnectAsync(HttpContext context)
    {
        string deviceId = context.Request.Headers["X-OpenDesk-Device"].ToString();
        if (!IsDeviceRequest(context, deviceId))
            return Results.StatusCode(403);
        var peer = context.Connection.RemoteIpAddress!;
        bool automatic = context.Request.Headers["X-OpenDesk-Auto"] == "1";
        if (automatic && context.Request.Headers["X-OpenDesk-Host"] != preferences.HostId)
            return Results.StatusCode(403);
        context.Response.Headers["X-OpenDesk-Host"] = preferences.HostId;
        string name = context.Request.Headers["X-OpenDesk-Name"].ToString().Trim();
        if (!DeviceName.IsMatch(name))
            name = "Android viewer";

        if (!await approval.WaitAsync(0, context.RequestAborted))
            return Results.Text("Another connection is being approved. Try again shortly.", statusCode: 429);

        try
        {
            long now = Environment.TickCount64;
            if (now - lastRequest < 1200)
                return Results.Text("Try again shortly.", statusCode: 429);
            lastRequest = now;

            var saved = preferences.Find(deviceId);
            if (automatic && (saved is null || saved.Paused))
                return Results.Text("Select the PC again to resume automatic reconnect.", statusCode: 423);
            var ongoing = active.FirstOrDefault(item => item.Value.DeviceId == deviceId);
            if (ongoing.Value is not null && ongoing.Value.Address.Equals(peer) && ongoing.Value.Stream.CaptureError is null)
            {
                context.Response.Headers["Cache-Control"] = "no-store";
                return Results.Text(ongoing.Value.Stream.Invite);
            }
            RememberPositions();
            if (ongoing.Value is not null)
                await RemoveAsync(ongoing.Key, ongoing.Value);
            int port = StreamPorts.FirstOrDefault(candidate => !active.ContainsKey(candidate));
            if (port == 0)
                return Results.Text("No virtual display is available. Check the Windows host.", statusCode: 409);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(55));
            if (saved is null && !await approve(peer).WaitAsync(timeout.Token))
                return Results.Text("The Windows host declined the connection.", statusCode: 403);
            if (saved is null || saved.Paused)
                saved = preferences.Approve(deviceId);
            VirtualDesktop.Lease lease;
            try
            {
                lease = await VirtualDesktop.AttachAsync(GetSize(context),
                    active.Values.Select(item => item.Monitor).ToArray(), saved.Layout?.ToPoint(), timeout.Token);
                topologyError = null;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                topologyError = $"Could not attach a virtual display. {error.Message}";
                return Results.Text(topologyError, statusCode: 503);
            }
            DisplayChoice display = lease.Display;
            var stream = new ShareSession(network, display, quality(), port, peer, lease.Monitor, allowTouch, quality);
            var entry = new Connection(stream, deviceId, name, peer, lease.Monitor, saved);
            active[port] = entry;
            try
            {
                if (display.DeviceName == manuallySharedDisplay())
                {
                    await RemoveAsync(port, entry);
                    return Results.Text("That display is already being shared. Try again.", statusCode: 409);
                }
                TryRestorePositions();
                RefreshObservedPositions();
                await stream.StartAsync();
                context.Response.Headers["Cache-Control"] = "no-store";
                return Results.Text(stream.Invite);
            }
            catch
            {
                await RemoveAsync(port, entry);
                return Results.Text("The Windows host could not start the virtual display stream.", statusCode: 503);
            }
        }
        catch (OperationCanceledException)
        {
            return Results.Text("The connection request expired.", statusCode: 408);
        }
        finally
        {
            approval.Release();
        }
    }

    private void TryRestorePositions()
    {
        var positions = active.Values.Where(item => item.Preferences.Layout is not null)
            .ToDictionary(item => item.Monitor, item => item.Preferences.Layout!.ToPoint());
        try { VirtualDesktop.RestorePositions(positions); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            topologyError = "Windows could not restore the saved display arrangement.";
        }
    }

    private void RefreshObservedPositions()
    {
        layoutSettlesAt = Environment.TickCount64 + 1700;
        RememberPositions();
    }

    private void RememberPositions()
    {
        bool settling = Environment.TickCount64 < layoutSettlesAt;
        bool changed = false;
        foreach (var connection in active.Values)
        {
            Point? position;
            try { position = VirtualDesktop.GetDisplay(connection.Monitor)?.Bounds.Location; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { continue; }
            if (position is not { } current)
                continue;
            if (!settling && (connection.Preferences.Layout is null ||
                (connection.Observed is { } previous && previous != current)))
            {
                connection.Preferences.Layout = new ViewerPreferences.Position(current.X, current.Y);
                changed = true;
            }
            connection.Observed = current;
        }
        if (changed)
            preferences.Save();
    }

    private static Size GetSize(HttpContext context)
    {
        if (int.TryParse(context.Request.Headers["X-OpenDesk-Width"], out int width) &&
            int.TryParse(context.Request.Headers["X-OpenDesk-Height"], out int height) &&
            width is >= 480 and <= 3840 && height is >= 480 and <= 3840 &&
            (long)width * height <= 3840 * 2160)
            return new Size(width, height);
        return new Size(1920, 1080);
    }

    private Task DisconnectClientAsync(HttpContext context)
    {
        string deviceId = context.Request.Headers["X-OpenDesk-Device"].ToString();
        string sessionKey = context.Request.Headers["X-OpenDesk-Session"].ToString();
        if (!IsDeviceRequest(context, deviceId) || !DeviceKey.IsMatch(sessionKey))
        {
            context.Response.StatusCode = 403;
            return Task.CompletedTask;
        }
        context.Response.StatusCode = 204;
        return DisconnectMatchingAsync(item => item.DeviceId == deviceId &&
            item.Address.Equals(context.Connection.RemoteIpAddress) && item.Stream.MatchesKey(sessionKey));
    }

    internal Task DisconnectAsync(Guid id) => DisconnectMatchingAsync(item => item.Id == id, pause: true);

    private async Task DisconnectMatchingAsync(Func<Connection, bool> matches, bool pause = false)
    {
        try
        {
            await approval.WaitAsync(stopping.Token);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            foreach (var (port, connection) in active)
            {
                if (matches(connection))
                {
                    RememberPositions();
                    if (pause)
                    {
                        connection.Preferences.Paused = true;
                        preferences.Save();
                    }
                    await RemoveAsync(port, connection);
                    return;
                }
            }
        }
        finally
        {
            approval.Release();
        }
    }

    private async Task RemoveAsync(int port, Connection connection)
    {
        try { await connection.Stream.DisposeAsync(); }
        finally
        {
            try { VirtualDesktop.Detach(connection.Monitor); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                topologyError = $"Could not detach the virtual display. {error.Message}";
            }
            active.TryRemove(port, out _);
            RefreshObservedPositions();
        }
    }

    private async Task RemoveIdleAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stopping.Token))
            {
                if (!await approval.WaitAsync(0, stopping.Token))
                    continue;
                try
                {
                    RememberPositions();
                    foreach (var (port, connection) in active)
                    {
                        if (connection.Stream.Viewers > 0)
                            connection.LastActive = Environment.TickCount64;
                        if (connection.Stream.CaptureError is not null ||
                            Environment.TickCount64 - connection.LastActive > 18000)
                        {
                            await RemoveAsync(port, connection);
                        }
                    }
                }
                finally
                {
                    approval.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        ready = false;
        await stopping.CancelAsync();
        if (discovery is not null)
            await discovery;
        udp?.Dispose();
        if (housekeeping is not null)
            await housekeeping;
        if (server is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await server.StopAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            await server.DisposeAsync();
        }
        await approval.WaitAsync();
        try
        {
            RememberPositions();
            foreach (var (port, connection) in active)
                await RemoveAsync(port, connection);
        }
        finally
        {
            approval.Dispose();
            stopping.Dispose();
        }
    }

    private sealed class Connection(ShareSession stream, string deviceId, string name, IPAddress address,
        VirtualDesktop.Target monitor, ViewerPreferences.Viewer preferences)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal ShareSession Stream { get; } = stream;
        internal string DeviceId { get; } = deviceId;
        internal IPAddress Address { get; } = address;
        internal VirtualDesktop.Target Monitor { get; } = monitor;
        internal ViewerPreferences.Viewer Preferences { get; } = preferences;
        internal Point? Observed { get; set; }
        internal long LastActive { get; set; } = Environment.TickCount64;

        internal ConnectedDevice Snapshot() => new(Id, name, Address,
            Stream.DeviceName[(Stream.DeviceName.LastIndexOf('\\') + 1)..], Stream.Viewers > 0);
    }
}
