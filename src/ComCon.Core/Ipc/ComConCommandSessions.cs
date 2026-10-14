using System.Text.Json;
using System.Text.Json.Serialization;
using ComCon.Core.Configuration;
using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Ipc;

public sealed record ComConRigProfileMapping(string RigProfileId, int ProfileId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RigModelName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CivAddress { get; init; }
}

public enum ComConCommandSessionState
{
    AwaitingCatAcceptance,
    AwaitingControlBind,
    AwaitingControlAcceptance,
    AwaitingCatReady,
    Active,
    Ended
}

public sealed class ComConCommandSession
{
    private readonly object stateGate = new();
    private ComConCommandSessionState state;
    private string? endReason;

    internal ComConCommandSession(string rigProfileId, int profileId, long setupDeadlineMs)
    {
        RigProfileId = rigProfileId;
        ProfileId = profileId;
        SessionId = Guid.NewGuid().ToString("D");
        SetupDeadlineMs = setupDeadlineMs;
    }

    public string RigProfileId { get; }
    public int ProfileId { get; }
    public string SessionId { get; }
    public long SetupDeadlineMs { get; }
    public ComConCommandSessionState State { get { lock (stateGate) return state; } }
    public string? EndReason { get { lock (stateGate) return endReason; } }

    internal void Transition(ComConCommandSessionState next, string? reason = null)
    {
        lock (stateGate)
        {
            endReason = reason;
            state = next;
        }
    }
}

public sealed record ComConCommandSetupResult(
    bool Accepted, JsonElement Response, ComConCommandSession? Session);

/// <summary>Offline ownership/setup state only; transport owns response writes and endpoint closure.</summary>
public sealed class ComConCommandSessions
{
    private readonly object gate = new();
    private readonly IMonotonicClock clock;
    private readonly long setupTimeoutMs;
    private readonly Dictionary<int, ComConCommandSession> owners = [];
    private readonly HashSet<ComConCommandSession> transportOwners = [];
    private readonly Dictionary<ComConCommandSession, TaskCompletionSource> releases = [];
    private HashSet<int>? admittedProfiles;
    private ComConRigProfileMapping[] mappings;

    public ComConCommandSessions(
        IMonotonicClock clock, long setupTimeoutMs, IEnumerable<ComConRigProfileMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (setupTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(setupTimeoutMs));
        this.clock = clock;
        this.setupTimeoutMs = setupTimeoutMs;
        this.mappings = SnapshotMappings(mappings);
    }

    public void ReplaceMappings(IEnumerable<ComConRigProfileMapping> replacement)
    {
        var snapshot = SnapshotMappings(replacement);
        lock (gate) mappings = snapshot;
    }

    public void SetProfileAdmission(int profileId, bool available)
    {
        if (profileId is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(profileId));
        lock (gate)
        {
            admittedProfiles ??= [1, 2, 3, 4];
            if (available) admittedProfiles.Add(profileId);
            else admittedProfiles.Remove(profileId);
        }
    }

    public Task WaitForProfileReleaseAsync(int profileId)
    {
        lock (gate)
            return owners.TryGetValue(profileId, out var session) && releases.TryGetValue(session, out var release)
                ? release.Task : Task.CompletedTask;
    }

    public void ReleaseTransportOwnership(ComConCommandSession session)
    {
        lock (gate)
        {
            transportOwners.Remove(session);
            if (IsOwned(session)) EndOwnedSession(session, session.EndReason ?? "Transport detached.");
            if (releases.Remove(session, out var release)) release.TrySetResult();
        }
    }

    public IReadOnlyList<ComConRigProfileMapping> Mappings
    {
        get
        {
            lock (gate) return Array.AsReadOnly(mappings.ToArray());
        }
    }

    public IReadOnlyList<ComConCommandSession> ActiveSessions
    {
        get
        {
            lock (gate) return Array.AsReadOnly(owners.Values.ToArray());
        }
    }

    public static ComConCommandSessions FromSettings(
        IMonotonicClock clock, long setupTimeoutMs, AppSettings settings) =>
        new(clock, setupTimeoutMs, ValidatedMappings(settings));

    public void ReplaceMappingsFromSettings(AppSettings settings) =>
        ReplaceMappings(ValidatedMappings(settings));

    private static IReadOnlyList<ComConRigProfileMapping> ValidatedMappings(AppSettings settings)
    {
        var errors = ConfigurationValidator.Validate(settings);
        if (errors.Count > 0) throw new ConfigurationValidationException(errors);
        return settings.RigProfileMappings;
    }

    public ComConCommandSetupResult AcceptCatHandshake(
        ReadOnlySpan<byte> payload, int? endpointProfileId = null, bool retainTransportOwnership = false)
    {
        if (endpointProfileId is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(endpointProfileId));
        JsonElement envelope;
        try
        {
            envelope = ComConCommandProtocol.ParseEnvelope(payload);
        }
        catch (ComConCommandProtocolException exception)
        {
            return Reject("cat.rejected", exception.ErrorCode, exception.Message);
        }
        if (envelope.GetProperty("messageType").GetString() != "cat.handshake")
            return Reject("cat.rejected", "INVALID_MESSAGE_TYPE", "Expected cat.handshake on the CAT setup channel.");

        var rigProfileId = envelope.GetProperty("rigProfileId").GetString()!;
        lock (gate)
        {
            var matches = mappings.Where(mapping => mapping.RigProfileId == rigProfileId).ToArray();
            if (matches.Length == 0)
                return Reject("cat.rejected", "RIG_PROFILE_NOT_MAPPED", "No ComCon profile maps this rig identity.");
            if (matches.Length != 1)
                return Reject("cat.rejected", "AMBIGUOUS_RIG_MAPPING", "The rig identity has multiple mappings.");
            var profileId = matches[0].ProfileId;
            if (endpointProfileId is not null && profileId != endpointProfileId)
                return Reject("cat.rejected", "PROFILE_SESSION_MISMATCH", "The rig identity maps to a different profile endpoint.");
            if (admittedProfiles is not null && !admittedProfiles.Contains(profileId))
                return Reject("cat.rejected", "SETUP_NOT_READY", "The profile is not accepting IPC sessions.");
            if (owners.ContainsKey(profileId))
                return Reject("cat.rejected", "PROFILE_BUSY", "The profile already has an IpcClient owner.");

            var now = clock.NowMs;
            if (now < 0 || now > long.MaxValue - setupTimeoutMs)
                throw new InvalidOperationException("The monotonic clock cannot represent the setup deadline.");
            var session = new ComConCommandSession(rigProfileId, profileId, now + setupTimeoutMs);
            owners.Add(profileId, session);
            if (retainTransportOwnership)
            {
                transportOwners.Add(session);
                releases.Add(session, new(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            return Accept("cat.accepted", session);
        }
    }

    public void ConfirmCatAcceptanceSent(ComConCommandSession session)
    {
        lock (gate)
        {
            RequireLiveSetup(session);
            if (session.State != ComConCommandSessionState.AwaitingCatAcceptance)
                throw new InvalidOperationException("CAT acceptance is not awaiting transport completion.");
            session.Transition(ComConCommandSessionState.AwaitingControlBind);
        }
    }

    public ComConCommandSetupResult BindControl(ReadOnlySpan<byte> payload, int controlProfileId)
    {
        if (controlProfileId is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(controlProfileId));
        JsonElement envelope;
        try
        {
            envelope = ComConCommandProtocol.ParseEnvelope(payload);
        }
        catch (ComConCommandProtocolException exception)
        {
            return Reject("control.rejected", exception.ErrorCode, exception.Message);
        }
        if (envelope.GetProperty("messageType").GetString() != "control.bind")
            return Reject("control.rejected", "INVALID_MESSAGE_TYPE", "Expected control.bind on the control setup channel.");

        var profileId = envelope.GetProperty("profileId").GetInt32();
        var sessionId = envelope.GetProperty("sessionId").GetString()!;
        lock (gate)
        {
            var session = owners.Values.SingleOrDefault(owner => owner.SessionId == sessionId);
            if (session is null)
                return Reject("control.rejected", "UNKNOWN_SESSION", "The session is not active.");
            if (session.ProfileId != profileId || profileId != controlProfileId)
                return Reject("control.rejected", "PROFILE_SESSION_MISMATCH", "The profile, session and control endpoint do not match.");
            if (session.State == ComConCommandSessionState.Ended ||
                (admittedProfiles is not null && !admittedProfiles.Contains(profileId)))
            {
                EndOwnedSession(session, "Profile admission closed during setup.");
                return Reject("control.rejected", "SETUP_NOT_READY", session.EndReason!, session);
            }
            if (session.State != ComConCommandSessionState.Active && clock.NowMs >= session.SetupDeadlineMs)
            {
                var code = SetupTimeoutCode(session);
                EndOwnedSession(session, SetupTimeoutReason(session));
                return Reject("control.rejected", code, session.EndReason!, session);
            }
            if (session.State is ComConCommandSessionState.Active or
                ComConCommandSessionState.AwaitingControlAcceptance or ComConCommandSessionState.AwaitingCatReady)
                return Reject("control.rejected", "CONTROL_ALREADY_BOUND", "A control connection already owns this session.");
            if (session.State != ComConCommandSessionState.AwaitingControlBind)
                return Reject("control.rejected", "SETUP_NOT_READY", "CAT acceptance has not completed.");

            session.Transition(ComConCommandSessionState.AwaitingControlAcceptance);
            return Accept("control.bound", session);
        }
    }

    public void ConfirmControlAcceptanceSent(ComConCommandSession session)
    {
        lock (gate)
        {
            RequireLiveSetup(session);
            if (session.State != ComConCommandSessionState.AwaitingControlAcceptance)
                throw new InvalidOperationException("Control acceptance is not awaiting transport completion.");
            session.Transition(ComConCommandSessionState.AwaitingCatReady);
        }
    }

    public void AcceptCatReady(ComConCommandSession session, ReadOnlySpan<byte> payload)
    {
        lock (gate)
        {
            RequireLiveSetup(session);
            try
            {
                var envelope = ComConCommandProtocol.ParseEnvelope(payload);
                if (envelope.GetProperty("messageType").GetString() != "cat.ready")
                    throw new ComConCommandProtocolException("INVALID_MESSAGE_TYPE", "Expected cat.ready before raw CAT.");
                if (envelope.GetProperty("sessionId").GetString() != session.SessionId)
                    throw new ComConCommandProtocolException("UNKNOWN_SESSION", "CAT readiness does not belong to this session.");
                if (envelope.GetProperty("profileId").GetInt32() != session.ProfileId)
                    throw new ComConCommandProtocolException("PROFILE_SESSION_MISMATCH", "CAT readiness profile does not match.");
                if (session.State != ComConCommandSessionState.AwaitingCatReady)
                    throw new ComConCommandProtocolException("SETUP_NOT_READY", "CAT readiness is not awaiting validation.");
                session.Transition(ComConCommandSessionState.Active);
            }
            catch (ComConCommandProtocolException exception)
            {
                EndOwnedSession(session, $"CAT readiness failed ({exception.ErrorCode}): {exception.Message}");
                throw;
            }
        }
    }

    public bool CanRelayCat(ComConCommandSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (gate)
            return IsOwned(session) && session.State == ComConCommandSessionState.Active;
    }

    public IReadOnlyList<ComConCommandSession> ExpireSetups()
    {
        lock (gate)
        {
            var now = clock.NowMs;
            var expired = owners.Values.Where(session =>
                session.State is not (ComConCommandSessionState.Active or ComConCommandSessionState.Ended) &&
                now >= session.SetupDeadlineMs).ToArray();
            foreach (var session in expired)
                EndOwnedSession(session, SetupTimeoutReason(session));
            return expired;
        }
    }

    public void EndSession(ComConCommandSession session, string reason)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            if (IsOwned(session))
                EndOwnedSession(session, reason);
            else if (session.State != ComConCommandSessionState.Ended)
                throw new InvalidOperationException("This manager does not own the supplied session.");
        }
    }

    private bool IsOwned(ComConCommandSession session) =>
        owners.TryGetValue(session.ProfileId, out var owner) && ReferenceEquals(owner, session);

    private void RequireLiveSetup(ComConCommandSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsOwned(session))
            throw new InvalidOperationException("This manager does not own the supplied session.");
        if (session.State == ComConCommandSessionState.Ended ||
            (admittedProfiles is not null && !admittedProfiles.Contains(session.ProfileId)))
        {
            EndOwnedSession(session, "Profile admission closed during setup.");
            throw new ComConCommandProtocolException("SETUP_NOT_READY", session.EndReason!);
        }
        if (session.State != ComConCommandSessionState.Active && clock.NowMs >= session.SetupDeadlineMs)
        {
            var code = SetupTimeoutCode(session);
            EndOwnedSession(session, SetupTimeoutReason(session));
            throw new ComConCommandProtocolException(code, session.EndReason!);
        }
    }

    private static string SetupTimeoutCode(ComConCommandSession session) =>
        session.State == ComConCommandSessionState.AwaitingCatReady ? "CAT_READY_TIMEOUT" : "CONTROL_BIND_TIMEOUT";

    private static string SetupTimeoutReason(ComConCommandSession session) =>
        session.State == ComConCommandSessionState.AwaitingCatReady
            ? "CAT readiness exceeded the setup deadline."
            : "Control binding exceeded the setup deadline.";

    private void EndOwnedSession(ComConCommandSession session, string reason)
    {
        if (!transportOwners.Contains(session)) owners.Remove(session.ProfileId);
        if (session.State != ComConCommandSessionState.Ended)
            session.Transition(ComConCommandSessionState.Ended, reason);
    }

    private static ComConCommandSetupResult Accept(string type, ComConCommandSession session) =>
        new(true, Response(type, session), session);

    private static ComConCommandSetupResult Reject(
        string type, string code, string reason, ComConCommandSession? session = null) =>
        new(false, Response(type, session, code, reason), session);

    private static JsonElement Response(
        string type, ComConCommandSession? session, string? code = null, string? reason = null)
    {
        var fields = new Dictionary<string, object>
        {
            ["messageType"] = type,
            ["contractVersion"] = ComConCommandProtocol.ContractVersion
        };
        if (session is not null)
        {
            fields["profileId"] = session.ProfileId;
            fields["sessionId"] = session.SessionId;
        }
        if (code is not null)
        {
            fields["errorCode"] = code;
            fields["reason"] = reason!;
        }
        return ComConCommandProtocol.ParseEnvelope(JsonSerializer.SerializeToUtf8Bytes(fields));
    }

    private static ComConRigProfileMapping[] SnapshotMappings(IEnumerable<ComConRigProfileMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var snapshot = mappings.ToArray();
        foreach (var mapping in snapshot)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            if (!Guid.TryParseExact(mapping.RigProfileId, "D", out var uuid) ||
                uuid.ToString("D") != mapping.RigProfileId)
                throw new ArgumentException("Rig mappings require canonical lowercase UUIDs.", nameof(mappings));
            if (mapping.ProfileId is < 1 or > 4)
                throw new ArgumentException("Rig mappings require profile IDs from 1 through 4.", nameof(mappings));
        }
        return snapshot;
    }
}
