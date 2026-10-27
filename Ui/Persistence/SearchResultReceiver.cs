using System.Threading.Channels;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Runtime;

namespace RolltheSpire2.Ui.Persistence;

// Owns the session's only candidate reader. The worker only transfers immutable
// candidates into a bounded buffer; Godot, persistence and UI remain main-thread
// responsibilities. Cancellation of the search does not cancel its result tail.
internal sealed class SearchResultReceiver
{
    internal const int MaximumUiCapacity = 1000;
    internal const int MaximumCapacity = 65536;
    internal const int UiDrainBatchSize = 64;
    private readonly IProductionSearchSession _session;
    private readonly Channel<SearchCandidate> _buffer;
    private Exception? _failure;
    private long _received, _delivered;
    private int _peakBuffered;
    private long _transferVersion;
    private int _transferInFlight;

    internal SearchResultReceiver(IProductionSearchSession session, int capacity)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (capacity is < 1 or > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        _session = session; Capacity = capacity;
        _buffer = Channel.CreateBounded<SearchCandidate>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false
        });
        ReceptionCompletion = Task.Run(ReceiveAsync);
    }

    internal int Capacity { get; }
    internal int BufferedCount => _buffer.Reader.Count;
    internal int PeakBufferedCount => Volatile.Read(ref _peakBuffered);
    internal long ReceivedCount => Interlocked.Read(ref _received);
    internal long DeliveredCount => Interlocked.Read(ref _delivered);
    internal long TransferVersion => Interlocked.Read(ref _transferVersion);
    internal bool TransferInFlight => Volatile.Read(ref _transferInFlight) != 0;
    internal Task SourceCompletion => _session.Completion;
    internal Task ReceptionCompletion { get; }
    internal Exception? Failure => Volatile.Read(ref _failure);
    internal bool IsFullyDrained => SourceCompletion.IsCompleted && ReceptionCompletion.IsCompleted &&
        _buffer.Reader.Completion.IsCompleted && BufferedCount == 0;

    internal bool TryReadCandidate(out SearchCandidate? candidate)
    {
        if (!_buffer.Reader.TryRead(out candidate)) return false;
        Interlocked.Increment(ref _delivered);
        return true;
    }
    internal ValueTask<bool> WaitToReadCandidateAsync(CancellationToken cancellationToken = default) =>
        _buffer.Reader.WaitToReadAsync(cancellationToken);

    private async Task ReceiveAsync()
    {
        try
        {
            // The source lifetime closes its channel after cancellation/failure.
            // Do not cancel this wait or the buffer write on Pause/Detached:
            // successfully published candidates still belong to the player.
            while (await _session.WaitToReadCandidateAsync().ConfigureAwait(false))
            {
                while (await TransferNextAsync().ConfigureAwait(false)) { }
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _failure, ex);
            _session.Cancel();
            // A readiness failure must not abandon already published items.
            // Cancellation releases blocked publishers; then drain their tail.
            try { await _session.Completion.ConfigureAwait(false); } catch { }
            try
            {
                while (await TransferNextAsync().ConfigureAwait(false)) { }
            }
            catch (Exception tailFailure) { Volatile.Write(ref _failure, new AggregateException(ex, tailFailure)); }
        }
        finally { _buffer.Writer.TryComplete(); }
    }

    private async ValueTask<bool> TransferNextAsync()
    {
        // Seqlock-style observation: a checkpoint may only accept an unchanged
        // even generation with both queues empty, never a moving candidate.
        Interlocked.Increment(ref _transferVersion);
        Volatile.Write(ref _transferInFlight, 1);
        try
        {
            if (!_session.TryReadCandidate(out SearchCandidate? candidate)) return false;
            if (candidate is null) return true;
            await _buffer.Writer.WriteAsync(candidate).ConfigureAwait(false);
            Interlocked.Increment(ref _received); ObservePeak(BufferedCount);
            return true;
        }
        finally
        {
            Volatile.Write(ref _transferInFlight, 0);
            Interlocked.Increment(ref _transferVersion);
        }
    }

    private void ObservePeak(int count)
    {
        int previous;
        while (count > (previous = Volatile.Read(ref _peakBuffered)) &&
            Interlocked.CompareExchange(ref _peakBuffered, count, previous) != previous) { }
    }
}
