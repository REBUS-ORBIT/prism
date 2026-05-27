using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

using Xunit;

using PRISM.Visualiser.Orchestrator.PixelStreaming;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 11 — <see cref="PortAllocator"/> hands out distinct,
/// ephemeral-range, bindable ports.
///
/// <para>
/// We deliberately exercise the "5 ports in a tight loop" path the
/// Phase F plan calls out: the orchestrator only ever needs one
/// signalling TCP port + a handful of UDP ports per run, but the
/// distinctness contract is the one that breaks if the OS hands out
/// the same ephemeral port twice in quick succession (it can, when
/// the previous binding hasn't drained from <c>TIME_WAIT</c> yet).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class PortAllocatorTests
{
    [Fact]
    public void AllocateDistinctTcpPorts_HandsOutFiveDistinctEphemeralPorts()
    {
        var ports = PortAllocator.AllocateDistinctTcpPorts(5);

        Assert.Equal(5, ports.Count);
        // Distinct.
        Assert.Equal(ports.Count, ports.Distinct().Count());
        foreach (var port in ports)
        {
            Assert.InRange(
                port,
                PortAllocator.EphemeralPortMin,
                PortAllocator.EphemeralPortMax);
        }
    }

    [Fact]
    public void AllocateDistinctTcpPorts_AllReturnedPortsAreImmediatelyBindable()
    {
        // After the allocator releases the listeners, every port it
        // returned must still be bindable on loopback — that's the
        // contract Cirrus relies on (its own bind happens microseconds
        // after we close the throwaway listener).
        var ports = PortAllocator.AllocateDistinctTcpPorts(5);

        foreach (var port in ports)
        {
            Assert.True(
                PortAllocator.IsTcpPortBindable(port),
                $"port {port} was returned by AllocateDistinctTcpPorts but is no longer bindable.");
        }
    }

    [Fact]
    public void AllocateTcpPort_ReturnsValueInEphemeralRange()
    {
        var port = PortAllocator.AllocateTcpPort();
        Assert.InRange(port, PortAllocator.EphemeralPortMin, PortAllocator.EphemeralPortMax);
    }

    [Fact]
    public void AllocateUdpPortRange_HandsOutFiveDistinctEphemeralUdpPorts()
    {
        var ports = PortAllocator.AllocateUdpPortRange();

        Assert.Equal(PortAllocator.DefaultUdpRangeSize, ports.Count);
        Assert.Equal(ports.Count, ports.Distinct().Count());
        foreach (var port in ports)
        {
            Assert.InRange(
                port,
                PortAllocator.EphemeralPortMin,
                PortAllocator.EphemeralPortMax);
        }
    }

    [Fact]
    public void IsTcpPortBindable_FalseForOutOfRangePort()
    {
        Assert.False(PortAllocator.IsTcpPortBindable(0));
        Assert.False(PortAllocator.IsTcpPortBindable(-1));
        Assert.False(PortAllocator.IsTcpPortBindable(70_000));
    }

    [Fact]
    public void AllocateTcpPortHonouringHint_PicksHintWhenBindable()
    {
        var hint = PortAllocator.AllocateTcpPort();
        var chosen = PortAllocator.AllocateTcpPortHonouringHint(hint);
        Assert.Equal(hint, chosen);
    }

    [Fact]
    public void AllocateTcpPortHonouringHint_FallsBackWhenHintIsTaken()
    {
        // Hold a real listener on the hinted port so the hint is
        // "not bindable", forcing the allocator to pick a fresh
        // ephemeral port.
        var hint = PortAllocator.AllocateTcpPort();
        using var blocker = new TcpListener(IPAddress.Loopback, hint);
        blocker.Start();
        try
        {
            var chosen = PortAllocator.AllocateTcpPortHonouringHint(hint);
            Assert.NotEqual(hint, chosen);
            Assert.InRange(chosen, PortAllocator.EphemeralPortMin, PortAllocator.EphemeralPortMax);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void AllocateDistinctTcpPorts_InvalidCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PortAllocator.AllocateDistinctTcpPorts(0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PortAllocator.AllocateDistinctTcpPorts(-1));
    }
}
