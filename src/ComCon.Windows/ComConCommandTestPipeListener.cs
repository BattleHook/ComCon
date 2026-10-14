using System.IO.Pipes;
using System.Runtime.Versioning;
using ComCon.Core.Ipc;

namespace ComCon.Windows;

/// <summary>Four profile sessions plus one bounded setup slot, retaining first-instance handles.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComConCommandTestPipeListener
{
    private const int CatInstances = 5;
    private sealed class Slot
    {
        public ComConCommandSession? Session;
        public CancellationTokenSource? Setup;
        public bool Serving;
    }

    private readonly ComConCommandTestPipeAcceptor setup;
    private readonly ComConCommandSessions sessions;
    private readonly TimeSpan stopTimeout;
    private readonly Action<Exception> reportConnectionFailure;
    private readonly object admissionGate = new();
    private readonly Slot[] slots = Enumerable.Range(0, CatInstances).Select(_ => new Slot()).ToArray();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? admission;
    private bool stopAccepting;
    private int started;

    public Task Ready => ready.Task;
    public string CatPipeName => setup.CatPipeName;
    public string ControlPipeName(int profileId) => setup.ControlPipeName(profileId);

    public void StopAccepting()
    {
        lock (admissionGate)
        {
            stopAccepting = true;
            admission?.Cancel();
        }
    }

    public void CancelProfileSetups(int profileId)
    {
        lock (admissionGate)
            foreach (var slot in slots)
                if (slot.Session?.ProfileId == profileId && !slot.Serving) slot.Setup?.Cancel();
    }

    public ComConCommandTestPipeListener(
        string testPipeName, ComConCommandSessions sessions, TimeSpan setupTimeout,
        TimeSpan stopTimeout, Action<Exception> reportConnectionFailure, int? endpointProfileId = null)
        : this(ComConCommandPipeNames.IsolatedTest(testPipeName), sessions, setupTimeout,
            stopTimeout, reportConnectionFailure, endpointProfileId) { }

    public ComConCommandTestPipeListener(
        ComConCommandPipeNames names, ComConCommandSessions sessions, TimeSpan setupTimeout,
        TimeSpan stopTimeout, Action<Exception> reportConnectionFailure, int? endpointProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(reportConnectionFailure);
        if (stopTimeout <= TimeSpan.Zero || stopTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        setup = new(names, sessions, setupTimeout, endpointProfileId);
        this.sessions = sessions;
        this.stopTimeout = stopTimeout;
        this.reportConnectionFailure = reportConnectionFailure;
    }

    public async Task RunAsync(
        Func<ComConAcceptedTestPipes, CancellationToken, Task> serveSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serveSession);
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("A command listener may run only once.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var accepting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        lock (admissionGate)
        {
            admission = accepting;
            if (stopAccepting) accepting.Cancel();
        }
        var cats = new List<NamedPipeServerStream>();
        var controls = new List<NamedPipeServerStream>();
        try
        {
            accepting.Token.ThrowIfCancellationRequested();
            for (var index = 0; index < CatInstances; index++)
                cats.Add(ComConReadOnlyPipeHost.CreateProtectedPipe(CatPipeName, CatInstances, index == 0));
            for (var profile = 1; profile <= 4; profile++)
                controls.Add(ComConReadOnlyPipeHost.CreateProtectedPipe(ControlPipeName(profile)));
            ready.TrySetResult();
            await Task.WhenAll(cats.Select((cat, index) => RunSlotAsync(
                cat, controls, slots[index], serveSession, accepting, lifetime))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (accepting.IsCancellationRequested)
        {
            ready.TrySetCanceled(accepting.Token);
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            _ = ready.Task.Exception;
            throw;
        }
        finally
        {
            lock (admissionGate) admission = null;
            foreach (var pipe in cats.Concat(controls)) pipe.Dispose();
        }
    }

    private async Task RunSlotAsync(
        NamedPipeServerStream cat, IReadOnlyList<NamedPipeServerStream> controls, Slot slot,
        Func<ComConAcceptedTestPipes, CancellationToken, Task> serveSession,
        CancellationTokenSource accepting, CancellationTokenSource lifetime)
    {
        try
        {
            while (!accepting.IsCancellationRequested)
            {
                await cat.WaitForConnectionAsync(accepting.Token).ConfigureAwait(false);
                Task? serving = null;
                using var setupStop = CancellationTokenSource.CreateLinkedTokenSource(accepting.Token);
                lock (admissionGate) slot.Setup = setupStop;
                try
                {
                    await using var accepted = await setup.AcceptConnectedAsync(
                        cat, controls, setupStop.Token, leaveOpen: true, reserveTransport: session =>
                        {
                            lock (admissionGate) slot.Session = session;
                        }).ConfigureAwait(false);
                    lock (admissionGate) slot.Serving = true;
                    using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    serving = serveSession(accepted, sessionStop.Token) ??
                        throw new InvalidOperationException("The session handler returned no task.");
                    try { await serving.WaitAsync(lifetime.Token).ConfigureAwait(false); }
                    finally
                    {
                        sessionStop.Cancel();
                        try { await serving.WaitAsync(stopTimeout).ConfigureAwait(false); }
                        catch (TimeoutException)
                        {
                            _ = serving.ContinueWith(task =>
                            {
                                if (task.IsFaulted) reportConnectionFailure(task.Exception!);
                            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            throw new TimeoutException("Session handler did not quiesce; the listener must stop.");
                        }
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested || setupStop.IsCancellationRequested)
                {
                    // Setup revocation and service shutdown are expected, not service faults.
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                {
                    reportConnectionFailure(exception);
                }
                finally
                {
                    ComConCommandSession? session;
                    lock (admissionGate)
                    {
                        session = slot.Session;
                        slot.Session = null;
                        slot.Setup = null;
                        slot.Serving = false;
                    }
                    Disconnect(cat);
                    if (session is not null)
                    {
                        Disconnect(controls[session.ProfileId - 1]);
                        if (serving is { IsCompleted: false })
                        {
                            // Keep the profile reserved until the old handler's detach really completes.
                            _ = serving.ContinueWith(_ => sessions.ReleaseTransportOwnership(session),
                                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                        else sessions.ReleaseTransportOwnership(session);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (accepting.IsCancellationRequested) { }
        catch
        {
            lifetime.Cancel();
            throw;
        }
    }

    private static void Disconnect(NamedPipeServerStream pipe)
    {
        try { pipe.Disconnect(); }
        catch (InvalidOperationException) when (!pipe.IsConnected) { }
    }
}
