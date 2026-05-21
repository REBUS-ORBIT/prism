using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PRISM.Agent.Ws;
using PRISM.Contracts;

namespace PRISM.Agent.Pipeline;

/// <summary>
/// Discriminated union of work the agent slot pool can process.
/// Convert jobs carry the full <see cref="AssignData"/>; pollLayers jobs
/// carry only the file/job hints (see <see cref="PollLayersData"/>).
/// Both flow through the same channel/slot so Rhino access remains
/// serialised (the host is not re-entrant).
/// </summary>
public abstract record AgentSlotTask(string JobId);
public sealed record ConvertSlotTask(AssignData Assign)         : AgentSlotTask(Assign.JobId);
public sealed record PollLayersSlotTask(PollLayersData Poll)    : AgentSlotTask(Poll.JobId);

/// <summary>
/// Per-process pool of worker slots. The dispatcher hands incoming
/// <see cref="AssignData"/> / <see cref="PollLayersData"/> here; each slot
/// processes one task at a time in FIFO order. Concurrency is bound by
/// <c>AgentConfig.Slots</c>.
///
/// Rhino is not reentrant — even with N slots, the RhinoHost is shared
/// and jobs serialise on it. Slot count primarily controls queue depth
/// and lets the agent accept the next assignment while one is finishing
/// up download/upload around the Rhino work.
/// </summary>
public sealed class WorkerSlotPool : IAsyncDisposable
{
    readonly ILogger<WorkerSlotPool> _log;
    readonly Func<ConvertJob> _convertJobFactory;
    readonly Func<PollLayersJob> _pollLayersJobFactory;
    readonly WsClient _ws;
    readonly int _slotCount;
    readonly Channel<AgentSlotTask>[] _slotChannels;
    readonly Task[] _slotLoops;
    readonly CancellationTokenSource _cts = new();
    int _busy;

    public int BusyCount => Volatile.Read(ref _busy);

    public WorkerSlotPool(
        ILogger<WorkerSlotPool> log,
        Func<ConvertJob> convertJobFactory,
        Func<PollLayersJob> pollLayersJobFactory,
        WsClient ws,
        int slotCount)
    {
        _log = log;
        _convertJobFactory = convertJobFactory;
        _pollLayersJobFactory = pollLayersJobFactory;
        _ws = ws;
        _slotCount = Math.Max(1, slotCount);

        _slotChannels = new Channel<AgentSlotTask>[_slotCount];
        _slotLoops = new Task[_slotCount];
        for (int i = 0; i < _slotCount; i++)
        {
            int slot = i;
            // SingleReader = true would use SingleConsumerUnboundedChannel<T> which
            // throws NotSupportedException on .Count — keep multi-reader implementation
            // so the load-balancing probe in Enqueue() works.
            _slotChannels[i] = Channel.CreateUnbounded<AgentSlotTask>(
                new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
            _slotLoops[i] = Task.Run(() => SlotLoop(slot, _slotChannels[slot], _cts.Token));
        }
    }

    public void Enqueue(AssignData assign)            => EnqueueTask(new ConvertSlotTask(assign));
    public void EnqueuePollLayers(PollLayersData p)   => EnqueueTask(new PollLayersSlotTask(p));

    void EnqueueTask(AgentSlotTask task)
    {
        // Round-robin to the targeted slot; agent ignores AssignData.Slot
        // and instead routes to the least-loaded queue.
        int target = 0;
        int min = int.MaxValue;
        for (int i = 0; i < _slotCount; i++)
        {
            var pending = _slotChannels[i].Reader.Count;
            if (pending < min) { min = pending; target = i; }
        }
        _slotChannels[target].Writer.TryWrite(task);
    }

    async Task SlotLoop(int slotIndex, Channel<AgentSlotTask> chan, CancellationToken ct)
    {
        _log.LogInformation("slot {Slot} loop started", slotIndex);
        await foreach (var task in chan.Reader.ReadAllAsync(ct))
        {
            Interlocked.Increment(ref _busy);
            try
            {
                switch (task)
                {
                    case ConvertSlotTask c:
                        await _convertJobFactory().RunAsync(c.Assign, ct);
                        break;
                    case PollLayersSlotTask p:
                        await _pollLayersJobFactory().RunAsync(p.Poll, ct);
                        break;
                }
            }
            catch (Exception err)
            {
                _log.LogError(err, "slot {Slot} job {JobId} failed", slotIndex, task.JobId);
                try
                {
                    await _ws.SendAsync(MessageType.Fail, new FailData
                    {
                        JobId = task.JobId,
                        Error = err.Message,
                        Stack = err.StackTrace,
                        Retryable = false,
                    });
                }
                catch { /* WS may already be down; nothing to do here */ }
            }
            finally
            {
                Interlocked.Decrement(ref _busy);
            }
        }
        _log.LogInformation("slot {Slot} loop stopped", slotIndex);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        for (int i = 0; i < _slotChannels.Length; i++) _slotChannels[i].Writer.TryComplete();
        try { await Task.WhenAll(_slotLoops); } catch { /* swallow on shutdown */ }
        _cts.Dispose();
    }
}
