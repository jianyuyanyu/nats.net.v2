using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NATS.Client.Core.Commands;
using NATS.Client.Core.Internal;

namespace NATS.Client.Core;

public enum NatsSubEndReason
{
    None,
    NoMsgs,
    MaxMsgs,
    MaxBytes,
    Timeout,
    IdleTimeout,
    EmptyMsg,
    IdleHeartbeatTimeout,
    StartUpTimeout,
    Cancelled,
    Exception,
    JetStreamError,
}

/// <summary>
/// The base class for NATS subscriptions.
/// </summary>
public abstract class NatsSubBase
{
    private static readonly byte[] NoRespondersHeaderSequence = { (byte)' ', (byte)'5', (byte)'0', (byte)'3' };
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly bool _debug;
    private readonly INatsSubscriptionManager _manager;
    private readonly Timer? _timeoutTimer;
    private readonly Timer? _idleTimeoutTimer;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _startUpTimeout;
    private readonly TimeSpan _timeout;
    private readonly bool _countPendingMsgs;
    private readonly CancellationTokenRegistration _tokenRegistration;
    private volatile Timer? _startUpTimeoutTimer;
    private bool _disposed;
    private bool _unsubscribed;
    private bool _endSubscription;
    private int _endReasonRaw;
    private int _pendingMsgs;
    private Exception? _exception;

    /// <summary>
    /// Creates a new instance of <see cref="NatsSubBase"/>.
    /// </summary>
    /// <param name="connection">NATS connection.</param>
    /// <param name="manager">Subscription manager.</param>
    /// <param name="subject">Subject to subscribe to.</param>
    /// <param name="queueGroup">Queue group name.</param>
    /// <param name="opts">Subscription options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected NatsSubBase(
        INatsConnection connection,
        INatsSubscriptionManager manager,
        string subject,
        string? queueGroup,
        NatsSubOpts? opts,
        CancellationToken cancellationToken = default)
    {
        _logger = connection.Opts.LoggerFactory.CreateLogger<NatsSubBase>();
        _debug = _logger.IsEnabled(LogLevel.Debug);
        _manager = manager;
        _pendingMsgs = opts is { MaxMsgs: > 0 } ? opts.MaxMsgs ?? -1 : -1;
        _countPendingMsgs = _pendingMsgs > 0;
        _idleTimeout = opts?.IdleTimeout ?? default;
        _startUpTimeout = opts?.StartUpTimeout ?? default;
        _timeout = opts?.Timeout ?? default;

        Connection = connection;
        Subject = subject;
        QueueGroup = queueGroup;
        Opts = opts;

        // If cancellation token is already cancelled we don't need to register however there is still
        // a chance that cancellation token is cancelled after this check but before we register or
        // the derived class constructor is completed. In that case we might be calling subclass
        // methods through EndSubscription() on potentially not a fully initialized instance which
        // might be a problem. This should reduce the impact of that problem.
        cancellationToken.ThrowIfCancellationRequested();

#if NETSTANDARD
        _tokenRegistration = cancellationToken.Register(
            state =>
            {
                var self = (NatsSubBase)state!;
                self.EndSubscription(NatsSubEndReason.Cancelled);
            },
            this);
#else
        _tokenRegistration = cancellationToken.UnsafeRegister(
            state =>
            {
                var self = (NatsSubBase)state!;
                self.EndSubscription(NatsSubEndReason.Cancelled);
            },
            this);
#endif

        // Only allocate timers if necessary to reduce GC pressure
        if (_idleTimeout != default)
        {
            // Instead of Timers what we could've used here is a cancellation token source based loop
            // i.e. CancellationTokenSource.CancelAfter(TimeSpan) within a Task.Run(async delegate)
            // They both seem to use internal TimerQueue. The difference is that Timer seem to
            // lead to a relatively simpler implementation but the downside is callback is not
            // async and unsubscribe call has to be fire-and-forget. On the other hand running
            // CancellationTokenSource.CancelAfter in Task.Run(async delegate) gives us the
            // chance to await the unsubscribe call but leaves us to deal with the created task.
            // Since awaiting unsubscribe isn't crucial Timer approach is currently acceptable.
            // If we need an async loop in the future cancellation token source approach can be used.
            _idleTimeoutTimer = new Timer(_ => EndSubscription(NatsSubEndReason.IdleTimeout));
        }

        if (_startUpTimeout != default)
        {
            _startUpTimeoutTimer = new Timer(_ => EndSubscription(NatsSubEndReason.StartUpTimeout));
        }

        if (_timeout != default)
        {
            _timeoutTimer = new Timer(_ => EndSubscription(NatsSubEndReason.Timeout));
        }
    }

    /// <summary>
    /// The subject name to subscribe to.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// If specified, the subscriber will join this queue group. Subscribers with the same queue group name,
    /// become a queue group, and only one randomly chosen subscriber of the queue group will
    /// consume a message each time a message is received by the queue group.
    /// </summary>
    public string? QueueGroup { get; }

    /// <summary>
    /// Represents an exception that occurs during the execution of a NATS subscription.
    /// </summary>
    public Exception? Exception => Volatile.Read(ref _exception);

    // Hide from public API using explicit interface implementations
    // since INatsSub is marked as internal.
    public int? PendingMsgs => _pendingMsgs == -1 ? null : Volatile.Read(ref _pendingMsgs);

    /// <summary>
    /// The reason for the subscription ending.
    /// </summary>
    public NatsSubEndReason EndReason => (NatsSubEndReason)Volatile.Read(ref _endReasonRaw);

    internal NatsSubOpts? Opts { get; private set; }

    /// <summary>
    /// Represents a connection to the NATS server.
    /// </summary>
    protected INatsConnection Connection { get; }

    /// <summary>
    /// Signals that the subscription is ready to receive messages.
    /// Override this method to perform any initialization logic.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that represents the asynchronous operation.</returns>
    public virtual ValueTask ReadyAsync()
    {
        // Let idle timer start with the first message, in case
        // we're allowed to wait longer for the first message.
        if (_startUpTimeoutTimer == null)
            _idleTimeoutTimer?.Change(_idleTimeout, Timeout.InfiniteTimeSpan);

        _startUpTimeoutTimer?.Change(_startUpTimeout, Timeout.InfiniteTimeSpan);
        _timeoutTimer?.Change(dueTime: _timeout, period: Timeout.InfiniteTimeSpan);

        return default;
    }

    /// <summary>
    /// Complete the message channel, stop timers if they were used and send an unsubscribe
    /// message to the server.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that represents the asynchronous server UNSUB operation.</returns>
    public ValueTask UnsubscribeAsync()
    {
        lock (_gate)
        {
            if (_unsubscribed)
                return default;
            _unsubscribed = true;
        }

        _timeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _idleTimeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _startUpTimeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        try
        {
            TryComplete();
        }
        catch (Exception e)
        {
            // Ignore any exceptions thrown by the derived class since we're already in the process of unsubscribing.
            // We don't want to throw here and prevent the unsubscribe call from completing.
            // This is also required to workaround a potential race condition between the derived class and the base
            // class when cancellation token is cancelled before the instance creation is fully finished in its
            // constructor hence TryComplete() method might be dealing with uninitialized state.
            _logger.LogWarning(NatsLogEvents.Subscription, e, "Error while completing subscription");
        }

        return _manager.RemoveAsync(this);
    }

    /// <summary>
    /// Disposes the instance asynchronously.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous disposal operation.</returns>
    /// <exception cref="Exception">Thrown when an exception occurs during disposal.</exception>
    public virtual ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return default;
            _disposed = true;
        }

        GC.SuppressFinalize(this);

        var unsubscribeAsync = UnsubscribeAsync();

        _timeoutTimer?.Dispose();
        _idleTimeoutTimer?.Dispose();
        _startUpTimeoutTimer?.Dispose();

        _tokenRegistration.Dispose();

        if (Exception != null)
        {
            if (Exception is NatsSubException { Exception: not null } nse)
            {
                nse.Exception.Throw();
            }

            throw Exception;
        }

        return unsubscribeAsync;
    }

    /// <summary>
    /// Called when a message is received for the subscription.
    /// Calls <see cref="ReceiveInternalAsync"/> to process the message handling any exceptions.
    /// </summary>
    /// <param name="subject">Subject received for this subscription.</param>
    /// <param name="replyTo">Reply subject received for this subscription.</param>
    /// <param name="headersBuffer">Headers buffer received for this subscription.</param>
    /// <param name="payloadBuffer">Payload buffer received for this subscription.</param>
    public virtual async ValueTask ReceiveAsync(string subject, string? replyTo, ReadOnlySequence<byte>? headersBuffer, ReadOnlySequence<byte> payloadBuffer)
    {
        ResetIdleTimeout();

        // check for empty payload conditions
        if (payloadBuffer.Length == 0)
        {
            switch (Opts)
            {
            case { ThrowIfNoResponders: true } when IsHeader503(headersBuffer):
                SetException(new NatsNoRespondersException());
                return;
            case { StopOnEmptyMsg: true }:
                EndSubscription(NatsSubEndReason.EmptyMsg);
                return;
            }
        }

        try
        {
            // Need to await to handle any exceptions
            await ReceiveInternalAsync(subject, replyTo, headersBuffer, payloadBuffer).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // When user disposes or unsubscribes there maybe be messages still coming in
            // (e.g. JetStream consumer batch might not be finished) even though we're not
            // interested in the messages anymore. Hence we ignore any messages being
            // fed into the channel and rejected.
        }
        catch (Exception e)
        {
            _logger.LogError(NatsLogEvents.Subscription, e, "Error while processing message");

            var payload = new Memory<byte>(new byte[payloadBuffer.Length]);
            payloadBuffer.CopyTo(payload.Span);

            Memory<byte> headers = default;
            if (headersBuffer != null)
            {
                headers = new Memory<byte>(new byte[headersBuffer.Value.Length]);
            }

            SetException(new NatsSubException($"Message error: {e.Message}", ExceptionDispatchInfo.Capture(e), payload, headers));

            Telemetry.SetException(Activity.Current, e);
        }
    }

    internal static bool IsHeader503(ReadOnlySequence<byte>? headersBuffer) =>
        headersBuffer is { Length: >= 12 }
        && headersBuffer.Value.Slice(8, 4).ToSpan().SequenceEqual(NoRespondersHeaderSequence);

    internal void ClearException() => Interlocked.Exchange(ref _exception, null);

    /// <summary>
    /// Write commands when reconnecting.
    /// </summary>
    /// <remarks>
    /// By default this will write the required subscription command.
    /// When overriden base must be called to write the re-subscription command.
    /// Additional command (e.g. publishing pull requests in case of JetStream consumers) can be written as part of the reconnect routine.
    /// </remarks>
    /// <param name="commandWriter">command writer used to write reconnect commands</param>
    /// <param name="sid">SID which might be required to create subscription commands</param>
    /// <returns>ValueTask</returns>
    internal virtual ValueTask WriteReconnectCommandsAsync(CommandWriter commandWriter, int sid) => commandWriter.SubscribeAsync(sid, Subject, QueueGroup, PendingMsgs, CancellationToken.None);

    /// <summary>
    /// Invoked when a MSG or HMSG arrives for the subscription.
    /// <remarks>
    /// This method is invoked while reading from the socket. Buffers belong to the socket reader and you should process them as quickly as possible or create a copy before you return from this method.
    /// </remarks>
    /// </summary>
    /// <param name="subject">Subject received for this subscription. This might not be the subject you subscribed to especially when using wildcards. For example, if you subscribed to events.* you may receive events.open.</param>
    /// <param name="replyTo">Subject the sender wants you to send messages back to it.</param>
    /// <param name="headersBuffer">Raw headers bytes. You can use <see cref="NatsConnection"/> <see cref="NatsHeaderParser"/> to decode them.</param>
    /// <param name="payloadBuffer">Raw payload bytes.</param>
    /// <returns></returns>
    protected abstract ValueTask ReceiveInternalAsync(string subject, string? replyTo, ReadOnlySequence<byte>? headersBuffer, ReadOnlySequence<byte> payloadBuffer);

    /// <summary>
    /// Sets the exception that caused the subscription to end.
    /// </summary>
    /// <param name="exception">Exception that caused the subscription to end.</param>
    protected void SetException(Exception exception)
    {
        Interlocked.Exchange(ref _exception, exception);
        EndSubscription(NatsSubEndReason.Exception);
    }

    /// <summary>
    /// Resets the idle timeout timer.
    /// </summary>
    protected void ResetIdleTimeout()
    {
        _idleTimeoutTimer?.Change(dueTime: _idleTimeout, period: Timeout.InfiniteTimeSpan);

        // Once the first message is received we don't need to keep resetting the start-up timer
        if (_startUpTimeoutTimer != null)
        {
            _startUpTimeoutTimer.Change(dueTime: Timeout.InfiniteTimeSpan, period: Timeout.InfiniteTimeSpan);
            _startUpTimeoutTimer.Dispose();
        }
    }

    /// <summary>
    /// Decrements the maximum number of messages.
    /// </summary>
    protected void DecrementMaxMsgs()
    {
        if (!_countPendingMsgs)
            return;
        var maxMsgs = Interlocked.Decrement(ref _pendingMsgs);
        if (maxMsgs == 0)
            EndSubscription(NatsSubEndReason.MaxMsgs);
    }

    /// <summary>
    /// Invoked to signal end of the subscription.
    /// </summary>
    /// <remarks>
    /// Do not implement complex logic in this method. It should only be used to complete the channel writers.
    /// The reason is that this method might be invoked while instance is being created in constructors and
    /// the cancellation token might be cancelled before the members are fully initialized.
    /// </remarks>
    protected abstract void TryComplete();

    /// <summary>
    /// Ends the subscription with the specified reason.
    /// </summary>
    /// <param name="reason">Reason for ending the subscription.</param>
    protected void EndSubscription(NatsSubEndReason reason)
    {
        if (_debug)
            _logger.LogDebug(NatsLogEvents.Subscription, "End subscription {Reason}", reason);

        lock (_gate)
        {
            if (_endSubscription)
                return;
            _endSubscription = true;
        }

        Interlocked.Exchange(ref _endReasonRaw, (int)reason);

        // Stops timers and completes channel writer to exit any message iterators
        // synchronously, which is fine, however, we're not able to wait for
        // UNSUB message to be sent to the server. If any message arrives after this point
        // channel writer will ignore the message and we would effectively drop it.
#pragma warning disable CA2012
#pragma warning disable VSTHRD110
        UnsubscribeAsync();
#pragma warning restore VSTHRD110
#pragma warning restore CA2012
    }
}
