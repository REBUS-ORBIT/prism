using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace PRISM.Visualiser.Orchestrator.PixelStreaming;

/// <summary>
/// Allocates loopback-bindable TCP and UDP ports for the local
/// PixelStreaming bring-up. The "bind to port 0" trick lets the kernel
/// hand us a free ephemeral port; we close the socket the moment we
/// have the number and hand it to the caller (Cirrus or the WebRTC
/// stack) which then re-binds it.
///
/// <para>
/// This is racy by definition — between our close and Cirrus's bind
/// some other process could grab the same port. In practice on a
/// single-tenant workstation that doesn't happen, and any clash is
/// caught downstream by Cirrus refusing to start (we'd surface
/// <c>signalling_start_timeout</c>). The alternative — holding the
/// socket open across the spawn — doesn't work because Cirrus itself
/// must bind to the port.
/// </para>
///
/// <para>
/// For test 11 the distinctness guarantee matters, so
/// <see cref="AllocateDistinctTcpPorts"/> opens all listeners
/// simultaneously, snapshots their <see cref="IPEndPoint.Port"/>s,
/// then releases them in one go. That sidesteps the "OS hands out
/// the same port twice in a tight loop" pitfall the per-call helper
/// can hit.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class PortAllocator
{
    /// <summary>
    /// Ephemeral port range Windows uses by default (per
    /// <c>netsh int ipv4 show dynamicport tcp</c>). We don't enforce
    /// it — the kernel does that for us — but tests assert against
    /// it so a regression to "well-known port hand-out" is caught.
    /// </summary>
    public const int EphemeralPortMin = 1024;

    /// <summary>Highest possible TCP / UDP port number.</summary>
    public const int EphemeralPortMax = 65535;

    /// <summary>
    /// Default size of the WebRTC UDP port range. PixelStreaming2's
    /// per-streamer WebRTC stack uses one port for ICE + a few for
    /// the SCTP / media streams; 5 is the documented PS2 minimum.
    /// </summary>
    public const int DefaultUdpRangeSize = 5;

    /// <summary>
    /// Snapshot a single free loopback TCP port. The listener is closed
    /// before this method returns; the caller is expected to re-bind
    /// it immediately, accepting the small race window.
    /// </summary>
    public static int AllocateTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Allocate <paramref name="count"/> distinct loopback TCP ports
    /// in one shot. All listeners are bound simultaneously so the
    /// kernel guarantees uniqueness, then released together. Used
    /// when a single Cirrus run wants several reserved ports up-front.
    /// </summary>
    public static IReadOnlyList<int> AllocateDistinctTcpPorts(int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var listeners = new List<TcpListener>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
            }
            return listeners
                .Select(l => ((IPEndPoint)l.LocalEndpoint).Port)
                .ToArray();
        }
        finally
        {
            foreach (var listener in listeners)
            {
                try { listener.Stop(); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Allocate a contiguous-ish range of distinct loopback UDP ports.
    /// Like the TCP variant, we bind <paramref name="count"/> sockets
    /// simultaneously to guarantee distinctness; the kernel does NOT
    /// promise the ports are contiguous (and WebRTC doesn't care).
    /// </summary>
    public static IReadOnlyList<int> AllocateUdpPortRange(int count = DefaultUdpRangeSize)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var clients = new List<UdpClient>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                clients.Add(client);
            }
            return clients
                .Select(c => ((IPEndPoint)c.Client.LocalEndPoint!).Port)
                .ToArray();
        }
        finally
        {
            foreach (var client in clients)
            {
                try { client.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Quick check that <paramref name="port"/> is currently bindable
    /// on loopback. Tests use this to assert the allocator handed out
    /// usable ports; the production path doesn't call this (it would
    /// only widen the race window above).
    /// </summary>
    public static bool IsTcpPortBindable(int port)
    {
        if (port is < EphemeralPortMin or > EphemeralPortMax) return false;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decide whether to honour the caller's hinted port or pick a
    /// fresh ephemeral port. Returns the hint when it's currently
    /// bindable on loopback; falls back to a kernel-picked port
    /// otherwise. Used by the Phase F stream command to honour
    /// <c>--signalling-port-hint</c> opportunistically without
    /// failing the whole run on a clash.
    /// </summary>
    public static int AllocateTcpPortHonouringHint(int hint)
    {
        if (hint is >= EphemeralPortMin and <= EphemeralPortMax
            && IsTcpPortBindable(hint))
        {
            return hint;
        }
        return AllocateTcpPort();
    }
}
