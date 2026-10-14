using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Core.Ipc;

/// <summary>Owns one IPC session; construction chooses whole-profile or attached-session lifetime.</summary>
public sealed class ComConCommandLifecycle : IAsyncDisposable
{
    private readonly SharedRadioHost host;
    private readonly ComConCommandEndpoint endpoint;
    private readonly ComConCommandNotifications notifications;
    private readonly ComConCommandControlLoop control;
    private readonly TimeSpan shutdownTimeout;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource abort = new();
    private Task? completion;
    private bool started;
    private bool stopped;
    private readonly bool attachedMode;
    private bool attached;

    public ComConCommandLifecycle(
        SharedRadioHost host, ComConCommandEndpoint endpoint,
        ComConCommandNotifications notifications, ComConCommandControlLoop control,
        TimeSpan shutdownTimeout)
        : this(host, endpoint, notifications, control, shutdownTimeout, false)
    {
    }

    public static ComConCommandLifecycle ForAttachedSession(
        SharedRadioHost host, ComConCommandEndpoint endpoint,
        ComConCommandNotifications notifications, ComConCommandControlLoop control,
        TimeSpan shutdownTimeout) =>
        new(host, endpoint, notifications, control, shutdownTimeout, true);

    private ComConCommandLifecycle(
        SharedRadioHost host, ComConCommandEndpoint endpoint,
        ComConCommandNotifications notifications, ComConCommandControlLoop control,
        TimeSpan shutdownTimeout, bool attachedMode)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(control);
        if (shutdownTimeout <= TimeSpan.Zero || shutdownTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        if (!endpoint.UsesTracker(notifications) || !control.UsesTracker(notifications))
            throw new ArgumentException("CAT and control components must share one notification tracker.");
        this.host = host;
        this.endpoint = endpoint;
        this.notifications = notifications;
        this.control = control;
        this.shutdownTimeout = shutdownTimeout;
        this.attachedMode = attachedMode;
        endpoint.SetCatEofHandler(control.RequestOrderlyClose);
    }

    public Task ControlCompletion => completion ??
        throw new InvalidOperationException("The lifecycle has not started.");

    public async Task StartAsync(
        ProfileConfig profile, SharedRadioCoordinatorOptions options,
        CancellationToken cancellationToken = default)
    {
        if (attachedMode)
            throw new InvalidOperationException("An attached-session lifecycle must use StartAttachedAsync.");
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (started || stopped)
                throw new InvalidOperationException("The lifecycle can start only once.");
            started = true;
            completion = control.RunAsync(abort.Token);
            try
            {
                await host.StartAsync(profile, options, endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                await AbortAfterFailureAsync("Shared profile startup failed.", failure).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task StopOrderlyAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started || stopped)
                throw new InvalidOperationException("Orderly shutdown requires a running lifecycle.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(shutdownTimeout);
            try
            {
                endpoint.PrepareOrderlyStop();
                if (attachedMode)
                {
                    await host.DetachIpcAsync(endpoint, deadline.Token).ConfigureAwait(false);
                    attached = false;
                }
                else
                    await host.StopGracefullyAsync(deadline.Token).ConfigureAwait(false);
                if (host.State == SharedRadioHostState.Faulted)
                    throw new IOException($"Shared host faulted during shutdown: {host.Fault}");
                if (completion!.IsCompleted)
                    await completion.ConfigureAwait(false);
                control.RequestOrderlyClose(reason);
                await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
                stopped = true;
                abort.Dispose();
            }
            catch (Exception failure)
            {
                await AbortAfterFailureAsync(
                    "Orderly shutdown failed; no CAT replay is permitted.", failure).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task StartAttachedAsync(CancellationToken cancellationToken = default)
    {
        if (!attachedMode)
            throw new InvalidOperationException("Use ForAttachedSession to retain profile-host ownership.");
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (started || stopped)
                throw new InvalidOperationException("The lifecycle can start only once.");
            started = true;
            completion = control.RunAsync(abort.Token);
            try
            {
                await host.AttachIpcAsync(endpoint, cancellationToken).ConfigureAwait(false);
                attached = true;
            }
            catch (Exception failure)
            {
                await AbortAfterFailureAsync("IPC attachment failed.", failure).ConfigureAwait(false);
                // A control-channel protocol fault can close the session mid-attach; report that root cause.
                if (completion?.Exception?.InnerException is ComConCommandProtocolException protocolFailure)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(protocolFailure);
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!stopped)
                await AbortCoreAsync("IPC lifecycle disposed.").ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task AbortCoreAsync(string reason)
    {
        abort.Cancel();
        completion ??= control.RunAsync(abort.Token);
        notifications.Close(reason);
        var failures = new List<Exception>();
        try
        {
            if (attachedMode)
            {
                if (attached)
                {
                    await host.DetachIpcAsync(endpoint).ConfigureAwait(false);
                    attached = false;
                }
            }
            else
                await host.StopAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        try
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or
            OperationCanceledException or InvalidOperationException)
        {
            // A failed cleanup must not replace the control fault already observed by the caller.
            if (failures.Count > 0 && failure is not OperationCanceledException)
                failures.Insert(0, failure);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        finally
        {
            stopped = true;
            abort.Dispose();
        }
        if (failures.Count > 0)
            throw new AggregateException("IPC lifecycle cleanup failed.", failures);
    }

    private async Task AbortAfterFailureAsync(string reason, Exception primaryFailure)
    {
        try
        {
            await AbortCoreAsync(reason).ConfigureAwait(false);
        }
        catch (AggregateException cleanupFailure)
        {
            var failures = cleanupFailure.Flatten().InnerExceptions.ToList();
            if (!failures.Contains(primaryFailure))
                failures.Insert(0, primaryFailure);
            throw new AggregateException("IPC operation and cleanup failed.", failures);
        }
    }
}
