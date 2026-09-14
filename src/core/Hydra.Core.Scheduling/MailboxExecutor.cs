using System.Threading.Channels;

namespace Hydra.Core.Scheduling;

/// <summary>
/// A single-consumer async mailbox. Handlers posted via <see cref="PostAsync"/>
/// are executed one at a time, in order, on a dedicated consumer loop task —
/// the "serial executor" pattern used to give each resource (e.g. a worker or a
/// slot) a single logical writer.
///
/// <para>Handler exceptions never kill the loop: the exception faults the task
/// returned by <see cref="PostAsync"/>, increments <see cref="HandlerErrorCount"/>
/// and is passed to the optional <c>onHandlerError</c> callback; the loop then
/// continues with the next handler. <see cref="StopAsync"/> performs a graceful
/// drain — it completes the inbound channel and returns only after every
/// already-posted handler has run to completion.</para>
/// </summary>
public sealed class MailboxExecutor : IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly Channel<Func<Task>> _channel;
    private readonly Action<Exception>? _onHandlerError;
    private int _pendingCount; // written items not yet consumed (UnboundedChannel has no Count)
    private Task? _consumerTask;
    private int _started;
    private bool _stopped;
    private int _handlerErrors;

    /// <summary>Creates a stopped executor; call <see cref="Start"/> before posting.</summary>
    /// <param name="onHandlerError">Optional callback invoked (on the consumer
    /// loop) for every handler exception.</param>
    public MailboxExecutor(Action<Exception>? onHandlerError = null)
    {
        _onHandlerError = onHandlerError;
        _channel = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,          // exactly one consumer loop
            SingleWriter = false,         // many callers may post
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Number of handlers currently queued but not yet started.</summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>Number of handler exceptions captured since construction.</summary>
    public int HandlerErrorCount => Volatile.Read(ref _handlerErrors);

    /// <summary>True between <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _started != 0 && !_stopped;
            }
        }
    }

    /// <summary>Launch the consumer loop. Posts are not allowed before this.</summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        lock (_stateGate)
        {
            if (_started != 0)
                throw new InvalidOperationException("MailboxExecutor is already started.");
            _started = 1;
            _consumerTask = RunAsync();
        }
    }

    /// <summary>
    /// Enqueue <paramref name="handler"/> for serial execution. The returned task
    /// completes when the handler has run to completion (faulting if it threw).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not started, or already stopped.</exception>
    public Task PostAsync(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_stateGate)
        {
            if (_started == 0)
                throw new InvalidOperationException("MailboxExecutor is not started; call Start() first.");
            if (_stopped)
                throw new InvalidOperationException("MailboxExecutor has been stopped.");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new Func<Task>(async () =>
        {
            try
            {
                await handler();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _handlerErrors);
                _onHandlerError?.Invoke(ex);
                completion.TrySetException(ex);
            }
        });

        Interlocked.Increment(ref _pendingCount);
        if (!_channel.Writer.TryWrite(wrapper))
        {
            Interlocked.Decrement(ref _pendingCount);
            throw new InvalidOperationException("MailboxExecutor has been stopped.");
        }

        return completion.Task;
    }

    /// <summary>
    /// Gracefully stop the executor: no new posts are accepted, all pending
    /// handlers run to completion, then the consumer loop exits. Idempotent —
    /// concurrent/second callers await the same drain.
    /// </summary>
    /// <exception cref="InvalidOperationException">Never started.</exception>
    public async Task StopAsync()
    {
        Task consumer;
        lock (_stateGate)
        {
            if (_started == 0)
                throw new InvalidOperationException("MailboxExecutor is not started.");
            if (!_stopped)
            {
                _stopped = true;
                _channel.Writer.TryComplete();
            }

            consumer = _consumerTask!;
        }

        await consumer.ConfigureAwait(false);
    }

    /// <summary>Stops the executor (draining pending handlers) if it was started.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_started == 0)
                return;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        await foreach (var handler in reader.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _pendingCount);
            try
            {
                await handler().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Unreachable in practice — the posted wrapper captures handler
                // failures — but never let the mailbox loop die: report, continue.
                Interlocked.Increment(ref _handlerErrors);
                _onHandlerError?.Invoke(ex);
            }
        }
    }
}
