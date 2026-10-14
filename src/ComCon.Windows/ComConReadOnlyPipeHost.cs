using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using ComCon.Core.Ipc;
using Microsoft.Win32.SafeHandles;

namespace ComCon.Windows;

/// <summary>Explicit-start, single-observer offline status host. Never opens a COM port.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComConReadOnlyPipeHost
{
    private readonly int profileId;
    private readonly string pipeName;
    private readonly Action<Exception> reportConnectionFailure;
    private readonly string serverInstanceId = Guid.NewGuid().ToString("N");
    private readonly SimulatedReceiveJournal? observations;
    private readonly PassiveTelemetryLifecycle? telemetry;
    private int running;

    public ComConReadOnlyPipeHost(int profileId, Action<Exception> reportConnectionFailure,
        string? testPipeName = null, SimulatedReceiveJournal? observations = null,
        PassiveTelemetryLifecycle? telemetry = null)
    {
        if (profileId is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(profileId));
        ArgumentNullException.ThrowIfNull(reportConnectionFailure);
        if (observations is not null && testPipeName is null)
            throw new ArgumentException("Simulated observations require an isolated test pipe.", nameof(testPipeName));
        if (telemetry is not null && (testPipeName is null || !telemetry.IsSimulation))
            throw new ArgumentException("Lifecycle IPC requires a simulation bridge and isolated test pipe.", nameof(telemetry));
        if (telemetry is not null && observations is not null)
            throw new ArgumentException("Lifecycle and legacy observation identities are not yet aligned.", nameof(observations));
        if (testPipeName is not null &&
            (!testPipeName.StartsWith("ComConTest_", StringComparison.Ordinal) ||
             testPipeName.Length > 100 ||
             testPipeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
            throw new ArgumentException("Test pipes must use a bounded ComConTest_ name.", nameof(testPipeName));
        this.profileId = profileId;
        this.reportConnectionFailure = reportConnectionFailure;
        this.observations = observations;
        this.telemetry = telemetry;
        pipeName = testPipeName ?? $"ComConPipe_Profile{profileId}";
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException("The IPC host is already running.");
        try
        {
            using var pipe = CreatePipe();
            while (!cancellationToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ComConReadOnlyConnection.ServeAsync(
                        pipe, profileId, serverInstanceId, cancellationToken, observations, telemetry).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException)
                {
                    reportConnectionFailure(exception);
                }
                finally
                {
                    // A broken pipe reports IsConnected=false but still needs Disconnect before reuse.
                    pipe.Disconnect();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    internal NamedPipeServerStream CreatePipe() => CreateProtectedPipe(pipeName);

    internal static NamedPipeServerStream CreateProtectedPipe(
        string pipeName, int maxInstances = 1, bool firstInstance = true)
    {
        if (maxInstances is < 1 or > 254) throw new ArgumentOutOfRangeException(nameof(maxInstances));
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ??
            throw new InvalidOperationException("Cannot establish the current-user pipe ACL.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
            $"D:P(A;;GA;;;{sid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the IPC security descriptor.");
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0
            };
            // Keep this first-instance handle for the whole accept loop; do not recreate between peers.
            var handle = CreateNamedPipe($@"\\.\pipe\{pipeName}",
                0x00000003 | 0x40000000 | (firstInstance ? 0x00080000u : 0u),
                0x00000008, // byte mode, blocking, PIPE_REJECT_REMOTE_CLIENTS
                (uint)maxInstances, ComConIpcProtocol.MaximumFrameBytes, ComConIpcProtocol.MaximumFrameBytes,
                0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Cannot create the protected IPC pipe (name may already be owned).");
            }
            try
            {
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string descriptor, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
