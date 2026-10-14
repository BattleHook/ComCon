using System.Text;
using System.Text.Json;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ComConCommandSessionsTests
{
    private const string Rig = "12345678-abcd-4321-abcd-123456789abc";
    private const string OtherRig = "87654321-abcd-4321-abcd-123456789abc";

    [TestMethod]
    public void ProfileAdmissionRejectsStoppedAndLateSetupWithoutAffectingOtherProfile()
    {
        var manager = new ComConCommandSessions(new FakeClock(), 100, [new(Rig, 1), new(OtherRig, 2)]);
        manager.SetProfileAdmission(1, false);
        AssertReject(manager.AcceptCatHandshake(Handshake()), "SETUP_NOT_READY");
        var other = manager.AcceptCatHandshake(Handshake(OtherRig)).Session!;
        manager.SetProfileAdmission(1, true);
        var late = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(late);
        manager.SetProfileAdmission(1, false);
        var rejected = manager.BindControl(Bind(late), 1);
        Assert.IsFalse(rejected.Accepted);
        Assert.AreSame(late, rejected.Session);
        Assert.AreEqual("SETUP_NOT_READY", rejected.Response.GetProperty("errorCode").GetString());
        Assert.AreEqual(late.SessionId, rejected.Response.GetProperty("sessionId").GetString());
        Assert.AreEqual(1, rejected.Response.GetProperty("profileId").GetInt32());
        ValidateResponse(rejected);
        Assert.AreEqual(ComConCommandSessionState.Ended, late.State);
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatAcceptance, other.State);
        manager.SetProfileAdmission(1, true);
        var replacement = manager.AcceptCatHandshake(Handshake()).Session!;
        Assert.AreNotEqual(late.SessionId, replacement.SessionId);
        AssertReject(manager.BindControl(Bind(late), 1), "UNKNOWN_SESSION");
    }

    [TestMethod]
    public async Task EndedTransportOwnerCannotReattachUntilOldDetachCompletes()
    {
        var manager = Manager(new FakeClock());
        var old = manager.AcceptCatHandshake(Handshake(), retainTransportOwnership: true).Session!;
        manager.EndSession(old, "CAT/control closed before host detach.");
        manager.EndSession(old, "Later control-loop cleanup must not replace the original reason.");
        Assert.AreEqual("CAT/control closed before host detach.", old.EndReason);
        var release = manager.WaitForProfileReleaseAsync(1);
        Assert.IsFalse(release.IsCompleted);
        AssertReject(manager.AcceptCatHandshake(Handshake()), "PROFILE_BUSY");
        manager.ReleaseTransportOwnership(old);
        await release;
        Assert.AreEqual("CAT/control closed before host detach.", old.EndReason);
        var replacement = manager.AcceptCatHandshake(Handshake()).Session!;
        Assert.AreNotEqual(old.SessionId, replacement.SessionId);
        manager.ReleaseTransportOwnership(old);
        AssertReject(manager.AcceptCatHandshake(Handshake()), "PROFILE_BUSY");
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatAcceptance, replacement.State);
    }

    [TestMethod]
    public void AdmissionClosureRejectsCatReadyFromAnAlreadyBoundSetup()
    {
        var manager = Manager(new FakeClock());
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(session);
        Assert.IsTrue(manager.BindControl(Bind(session), 1).Accepted);
        manager.ConfirmControlAcceptanceSent(session);
        manager.SetProfileAdmission(1, false);
        var exception = Assert.ThrowsExactly<ComConCommandProtocolException>(() =>
            manager.AcceptCatReady(session, Ready(session)));
        Assert.AreEqual("SETUP_NOT_READY", exception.ErrorCode);
        Assert.IsFalse(manager.CanRelayCat(session));
        Assert.AreEqual(ComConCommandSessionState.Ended, session.State);
    }

    [TestMethod]
    public void SettingsMappingsValidateAtomicallyAndKeepExistingSessionsPinned()
    {
        var defaults = AppSettings.CreateDefault();
        var settings = defaults with
        {
            Profiles = defaults.Profiles.Select(profile => profile.Id == 1
                ? profile with { Framing = FramingMode.Civ }
                : profile).ToArray(),
            RigProfileMappings =
            [
                new ComConRigProfileMapping(Rig, 1)
                {
                    RigModelName = "Icom IC-7600",
                    CivAddress = "7A"
                }
            ]
        };
        var manager = ComConCommandSessions.FromSettings(new FakeClock(), 100, settings);
        var first = manager.AcceptCatHandshake(Handshake()).Session!;
        Assert.AreEqual(1, first.ProfileId);
        var invalid = settings with { RigProfileMappings = [new(Rig, 1), new(Rig, 4)] };
        Assert.ThrowsExactly<ConfigurationValidationException>(() => manager.ReplaceMappingsFromSettings(invalid));
        Assert.ThrowsExactly<ConfigurationValidationException>(() =>
            ComConCommandSessions.FromSettings(new FakeClock(), 100, invalid));
        AssertReject(manager.AcceptCatHandshake(Handshake()), "PROFILE_BUSY");
        manager.ReplaceMappingsFromSettings(settings with { RigProfileMappings = [new(Rig, 4), new(OtherRig, 1)] });
        Assert.AreEqual(1, first.ProfileId);
        var next = manager.AcceptCatHandshake(Handshake()).Session!;
        Assert.AreEqual(4, next.ProfileId);
        AssertReject(manager.AcceptCatHandshake(Handshake(OtherRig)), "PROFILE_BUSY");
        manager.ReplaceMappingsFromSettings(AppSettings.CreateDefault());
        AssertReject(manager.AcceptCatHandshake(Handshake()), "RIG_PROFILE_NOT_MAPPED");
        Assert.AreEqual(4, next.ProfileId);
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatAcceptance, first.State);
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatAcceptance, next.State);
        var invalidPorts = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile with { PhysicalPort = "COM3" }).ToArray()
        };
        Assert.ThrowsExactly<ConfigurationValidationException>(() => manager.ReplaceMappingsFromSettings(invalidPorts));
        AssertReject(manager.AcceptCatHandshake(Handshake()), "RIG_PROFILE_NOT_MAPPED");
    }

    [TestMethod]
    public void ProfilePinnedSetupRejectsBeforeAllocatingAndRetainsMappingAmbiguityChecks()
    {
        var manager = new ComConCommandSessions(new FakeClock(), 100, [new(Rig, 1), new(OtherRig, 4)]);
        AssertReject(manager.AcceptCatHandshake(Handshake(OtherRig), 1), "PROFILE_SESSION_MISMATCH");
        var other = manager.AcceptCatHandshake(Handshake(OtherRig), 4);
        Assert.IsTrue(other.Accepted);
        Assert.AreEqual(4, other.Session!.ProfileId);
        AssertReject(manager.AcceptCatHandshake(Handshake(OtherRig), 1), "PROFILE_SESSION_MISMATCH");
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatAcceptance, other.Session.State);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => manager.AcceptCatHandshake(Handshake(), 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => manager.AcceptCatHandshake(Handshake(), 5));
        manager.ReplaceMappings([new(Rig, 1), new(Rig, 4)]);
        AssertReject(manager.AcceptCatHandshake(Handshake(), 1), "AMBIGUOUS_RIG_MAPPING");
        AssertReject(manager.AcceptCatHandshake(Handshake(OtherRig), 4), "RIG_PROFILE_NOT_MAPPED");
    }

    [TestMethod]
    public async Task ClientCanReceiveControlBoundAndSendCatBeforeServerWriteConfirmation()
    {
        var manager = Manager(new FakeClock());
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(session);
        var bound = manager.BindControl(Bind(session), 1);
        using var control = new ComConCommandControlLoopTests.DuplexStream { HoldFlush = true };
        using var cat = new ComConCommandControlLoopTests.DuplexStream();
        using var writer = new SemaphoreSlim(1, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var write = ComConCommandProtocol.WriteEnvelopeAsync(
            control, Encoding.UTF8.GetBytes(bound.Response.GetRawText()), writer, timeout.Token).AsTask();
        try
        {
            var received = await control.Receive();
            Assert.AreEqual("control.bound", received.GetProperty("messageType").GetString());
            Assert.IsFalse(write.IsCompleted);
            byte[] command = [0xFE, 0xFE, 0x94, 0xE1, 1, 0xFD];
            using var clientOutput = new MemoryStream();
            using var clientWriter = new SemaphoreSlim(1, 1);
            await ComConCommandProtocol.WriteEnvelopeAsync(clientOutput, Ready(session), clientWriter, timeout.Token);
            await clientOutput.WriteAsync(command, timeout.Token);
            await cat.Send(clientOutput.ToArray());
            Assert.AreEqual(ComConCommandSessionState.AwaitingControlAcceptance, session.State);
            Assert.IsFalse(manager.CanRelayCat(session));
            control.ReleaseFlush();
            await write;
            manager.ConfirmControlAcceptanceSent(session);
            Assert.IsFalse(manager.CanRelayCat(session));
            var ready = await ComConCommandProtocol.ReadEnvelopeAsync(cat, timeout.Token);
            manager.AcceptCatReady(session, Encoding.UTF8.GetBytes(ready.GetRawText()));
            Assert.IsTrue(manager.CanRelayCat(session));
            var buffered = new byte[command.Length];
            await cat.ReadExactlyAsync(buffered, timeout.Token);
            CollectionAssert.AreEqual(command, buffered);
        }
        finally
        {
            control.ReleaseFlush();
            await write;
        }
    }

    [TestMethod]
    public void SetupRequiresBothSuccessfulAcceptanceWritesAndCatReadinessBeforeRelay()
    {
        var manager = Manager(new FakeClock());
        var accepted = manager.AcceptCatHandshake(Handshake());
        Assert.IsTrue(accepted.Accepted);
        var session = accepted.Session!;
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatAcceptance, session.State);
        Assert.IsFalse(manager.CanRelayCat(session));
        Assert.AreEqual(session.SessionId, accepted.Response.GetProperty("sessionId").GetString());
        Assert.AreEqual(Guid.Parse(session.SessionId).ToString("D"), session.SessionId);
        AssertReject(manager.BindControl(Bind(session), 1), "SETUP_NOT_READY");
        manager.ConfirmCatAcceptanceSent(session);
        Assert.IsFalse(manager.CanRelayCat(session));
        var bound = manager.BindControl(Bind(session), 1);
        Assert.IsTrue(bound.Accepted);
        Assert.AreSame(session, bound.Session);
        Assert.AreEqual(ComConCommandSessionState.AwaitingControlAcceptance, session.State);
        Assert.IsFalse(manager.CanRelayCat(session));
        manager.ConfirmControlAcceptanceSent(session);
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatReady, session.State);
        Assert.IsFalse(manager.CanRelayCat(session));
        AssertReject(manager.BindControl(Bind(session), 1), "CONTROL_ALREADY_BOUND");
        manager.AcceptCatReady(session, Ready(session));
        Assert.IsTrue(manager.CanRelayCat(session));
        Assert.AreEqual(ComConCommandSessionState.Active, session.State);
    }

    [TestMethod]
    [DataRow("profile", "PROFILE_SESSION_MISMATCH")]
    [DataRow("session", "UNKNOWN_SESSION")]
    [DataRow("type", "INVALID_MESSAGE_TYPE")]
    [DataRow("malformed", "INVALID_JSON")]
    public void InvalidReadinessEndsOnlyAssociatedOwner(string mutation, string code)
    {
        var manager = new ComConCommandSessions(new FakeClock(), 100, [new(Rig, 1), new(OtherRig, 4)]);
        var other = Activate(manager, OtherRig);
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(session);
        manager.BindControl(Bind(session), 1);
        manager.ConfirmControlAcceptanceSent(session);
        var payload = mutation switch
        {
            "profile" => Ready(session, profileId: 4),
            "session" => Ready(session, sessionId: other.SessionId),
            "type" => Bind(session),
            _ => "{"u8.ToArray()
        };
        var exception = Assert.ThrowsExactly<ComConCommandProtocolException>(() => manager.AcceptCatReady(session, payload));
        Assert.AreEqual(code, exception.ErrorCode);
        Assert.AreEqual(ComConCommandSessionState.Ended, session.State);
        Assert.IsFalse(manager.CanRelayCat(session));
        Assert.IsTrue(manager.CanRelayCat(other));
        Assert.IsTrue(manager.AcceptCatHandshake(Handshake()).Accepted);
    }

    [TestMethod]
    public void ReadinessDoesNotResetOriginalDeadlineAndExpiresAtExactBoundary()
    {
        var clock = new FakeClock();
        var manager = Manager(clock);
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        clock.NowMs = 90;
        manager.ConfirmCatAcceptanceSent(session);
        manager.BindControl(Bind(session), 1);
        manager.ConfirmControlAcceptanceSent(session);
        Assert.AreEqual(100L, session.SetupDeadlineMs);
        clock.NowMs = 100;
        var exception = Assert.ThrowsExactly<ComConCommandProtocolException>(() => manager.AcceptCatReady(session, Ready(session)));
        Assert.AreEqual("CAT_READY_TIMEOUT", exception.ErrorCode);
        Assert.IsFalse(manager.CanRelayCat(session));
        Assert.IsTrue(manager.AcceptCatHandshake(Handshake()).Accepted);
    }

    [TestMethod]
    public void EarlyReadinessCannotEnableUnconfirmedControlAndRepeatedReadinessEndsSession()
    {
        var manager = Manager(new FakeClock());
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(session);
        manager.BindControl(Bind(session), 1);
        var early = Assert.ThrowsExactly<ComConCommandProtocolException>(() => manager.AcceptCatReady(session, Ready(session)));
        Assert.AreEqual("SETUP_NOT_READY", early.ErrorCode);
        Assert.IsFalse(manager.CanRelayCat(session));
        var active = Activate(manager);
        var repeated = Assert.ThrowsExactly<ComConCommandProtocolException>(() => manager.AcceptCatReady(active, Ready(active)));
        Assert.AreEqual("SETUP_NOT_READY", repeated.ErrorCode);
        Assert.IsFalse(manager.CanRelayCat(active));
    }

    [TestMethod]
    public void CompetingRigMappingsShareOneProfileReservation()
    {
        var manager = new ComConCommandSessions(new FakeClock(), 100,
            [new(Rig, 1), new(OtherRig, 1)]);
        var owner = manager.AcceptCatHandshake(Handshake()).Session!;
        AssertReject(manager.AcceptCatHandshake(Handshake(OtherRig)), "PROFILE_BUSY");
        manager.EndSession(owner, "CAT pipe closed.");
        var next = manager.AcceptCatHandshake(Handshake(OtherRig)).Session!;
        Assert.AreNotEqual(owner.SessionId, next.SessionId);
        manager.EndSession(owner, "Duplicate close callback.");
        AssertReject(manager.AcceptCatHandshake(Handshake()), "PROFILE_BUSY");
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatAcceptance, next.State);
    }

    [TestMethod]
    public void SeparateProfilesCanSetUpAndRemainActiveConcurrently()
    {
        var manager = new ComConCommandSessions(new FakeClock(), 100,
            [new(Rig, 1), new(OtherRig, 4)]);
        var first = Activate(manager, Rig);
        var second = Activate(manager, OtherRig);
        Assert.IsTrue(manager.CanRelayCat(first));
        Assert.IsTrue(manager.CanRelayCat(second));
        manager.EndSession(first, "Control disconnected.");
        Assert.IsFalse(manager.CanRelayCat(first));
        Assert.IsTrue(manager.CanRelayCat(second));
    }

    [TestMethod]
    public void MappingReplacementDoesNotRedirectAcceptedSessions()
    {
        var manager = Manager(new FakeClock());
        var first = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ReplaceMappings([new(Rig, 2)]);
        manager.ConfirmCatAcceptanceSent(first);
        Assert.IsTrue(manager.BindControl(Bind(first), 1).Accepted);
        manager.ConfirmControlAcceptanceSent(first);
        manager.AcceptCatReady(first, Ready(first));
        var second = manager.AcceptCatHandshake(Handshake()).Session!;
        Assert.AreEqual(1, first.ProfileId);
        Assert.AreEqual(2, second.ProfileId);
    }

    [TestMethod]
    public void MappingInputIsSnapshottedAndInvalidReplacementIsAtomic()
    {
        var mappings = new List<ComConRigProfileMapping> { new(Rig, 1) };
        var manager = new ComConCommandSessions(new FakeClock(), 100, mappings);
        mappings.Clear();
        Assert.ThrowsExactly<ArgumentException>(() =>
            manager.ReplaceMappings([new(Rig.ToUpperInvariant(), 2)]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            manager.ReplaceMappings([new(Rig, 5)]));
        Assert.AreEqual(1, manager.AcceptCatHandshake(Handshake()).Session!.ProfileId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DuplicateOrAmbiguousMappingFailsClosed(bool sameProfile)
    {
        var manager = new ComConCommandSessions(new FakeClock(), 100,
            [new(Rig, 1), new(Rig, sameProfile ? 1 : 2), new(OtherRig, 3)]);
        AssertReject(manager.AcceptCatHandshake(Handshake()), "AMBIGUOUS_RIG_MAPPING");
        Assert.AreEqual(3, manager.AcceptCatHandshake(Handshake(OtherRig)).Session!.ProfileId);
    }

    [TestMethod]
    public void UnmappedAndMalformedHandshakeDoesNotAffectExistingOwner()
    {
        var manager = Manager(new FakeClock());
        var owner = Activate(manager);
        AssertReject(manager.AcceptCatHandshake(Handshake(OtherRig)), "RIG_PROFILE_NOT_MAPPED");
        AssertReject(manager.AcceptCatHandshake(Handshake(Rig.ToUpperInvariant())), "INVALID_RIG_PROFILE_ID");
        AssertReject(manager.AcceptCatHandshake(Encoding.UTF8.GetBytes("{}")), "MISSING_FIELD");
        AssertReject(manager.AcceptCatHandshake(Bind(owner)), "INVALID_MESSAGE_TYPE");
        Assert.IsTrue(manager.CanRelayCat(owner));
    }

    [TestMethod]
    public void InvalidOrMismatchedBindCannotReleaseProvisionalOrActiveOwner()
    {
        var manager = Manager(new FakeClock());
        var owner = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(owner);
        AssertReject(manager.BindControl(Bind(owner, profileId: 2), 1), "PROFILE_SESSION_MISMATCH");
        AssertReject(manager.BindControl(Bind(owner), 2), "PROFILE_SESSION_MISMATCH");
        AssertReject(manager.BindControl(Bind(owner, sessionId: Guid.NewGuid().ToString("D")), 1), "UNKNOWN_SESSION");
        AssertReject(manager.BindControl(Bind(owner, sessionId: "INVALID"), 1), "INVALID_SESSION_ID");
        var malformed = Encoding.UTF8.GetString(Bind(owner));
        AssertReject(manager.BindControl(Encoding.UTF8.GetBytes(malformed[..^1] + ",\"extra\":1}"), 1), "UNKNOWN_FIELD");
        AssertReject(manager.AcceptCatHandshake(Handshake()), "PROFILE_BUSY");
        Assert.IsTrue(manager.BindControl(Bind(owner), 1).Accepted);
        manager.ConfirmControlAcceptanceSent(owner);
        manager.AcceptCatReady(owner, Ready(owner));
        AssertReject(manager.BindControl(Bind(owner), 1), "CONTROL_ALREADY_BOUND");
        Assert.IsTrue(manager.CanRelayCat(owner));
    }

    [TestMethod]
    public void DuplicateBindWhileAcceptancePendingLeavesOriginalBindingIntact()
    {
        var manager = Manager(new FakeClock());
        var owner = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(owner);
        Assert.IsTrue(manager.BindControl(Bind(owner), 1).Accepted);
        AssertReject(manager.BindControl(Bind(owner), 1), "CONTROL_ALREADY_BOUND");
        manager.ConfirmControlAcceptanceSent(owner);
        manager.AcceptCatReady(owner, Ready(owner));
        Assert.IsTrue(manager.CanRelayCat(owner));
    }

    [TestMethod]
    public void DuplicateBindAtDeadlineExpiresPendingAcceptanceInsteadOfRetainingReservation()
    {
        var clock = new FakeClock();
        var manager = Manager(clock);
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(session);
        manager.BindControl(Bind(session), 1);
        clock.NowMs = 100;
        var result = manager.BindControl(Bind(session), 1);
        Assert.AreEqual("CONTROL_BIND_TIMEOUT", result.Response.GetProperty("errorCode").GetString());
        Assert.AreSame(session, result.Session);
        Assert.AreEqual(ComConCommandSessionState.Ended, session.State);
        Assert.IsTrue(manager.AcceptCatHandshake(Handshake()).Accepted);
    }

    [TestMethod]
    [DataRow(ComConCommandSessionState.AwaitingCatAcceptance)]
    [DataRow(ComConCommandSessionState.AwaitingControlBind)]
    [DataRow(ComConCommandSessionState.AwaitingControlAcceptance)]
    [DataRow(ComConCommandSessionState.AwaitingCatReady)]
    public void SetupDeadlineExpiresEveryProvisionalStateAtExactBoundary(ComConCommandSessionState state)
    {
        var clock = new FakeClock { NowMs = 20 };
        var manager = Manager(clock);
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        if (state != ComConCommandSessionState.AwaitingCatAcceptance)
            manager.ConfirmCatAcceptanceSent(session);
        if (state is ComConCommandSessionState.AwaitingControlAcceptance or ComConCommandSessionState.AwaitingCatReady)
            manager.BindControl(Bind(session), 1);
        if (state == ComConCommandSessionState.AwaitingCatReady)
            manager.ConfirmControlAcceptanceSent(session);
        clock.NowMs = 119;
        Assert.HasCount(0, manager.ExpireSetups());
        clock.NowMs = 120;
        var expired = manager.ExpireSetups();
        Assert.HasCount(1, expired);
        Assert.AreSame(session, expired[0]);
        Assert.AreEqual(ComConCommandSessionState.Ended, session.State);
        Assert.IsFalse(string.IsNullOrEmpty(session.EndReason));
        Assert.HasCount(0, manager.ExpireSetups());
        Assert.IsTrue(manager.AcceptCatHandshake(Handshake()).Accepted);
    }

    [TestMethod]
    [DataRow(4_999, 0)]
    [DataRow(5_000, 1)]
    [DataRow(5_001, 1)]
    public void SelectedSetupTimeoutUsesExactNMinusOneNAndNPlusOneBoundary(
        int elapsedMs, int expectedExpiredSessions)
    {
        var clock = new FakeClock();
        var manager = new ComConCommandSessions(
            clock, ComConCommandBounds.SetupTimeoutMs, [new(Rig, 1)]);
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        Assert.AreEqual(ComConCommandBounds.SetupTimeoutMs, session.SetupDeadlineMs);

        clock.NowMs = elapsedMs;
        var expired = manager.ExpireSetups();
        Assert.HasCount(expectedExpiredSessions, expired);
        Assert.AreEqual(
            expectedExpiredSessions == 0 ? ComConCommandSessionState.AwaitingCatAcceptance : ComConCommandSessionState.Ended,
            session.State);
    }

    [TestMethod]
    public void ValidLateBindReportsAssociatedTimeoutAndReleasesOnlyItsReservation()
    {
        var clock = new FakeClock();
        var manager = Manager(clock);
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        manager.ConfirmCatAcceptanceSent(session);
        clock.NowMs = 100;
        var rejected = manager.BindControl(Bind(session), 1);
        Assert.IsFalse(rejected.Accepted);
        Assert.AreSame(session, rejected.Session);
        Assert.AreEqual("CONTROL_BIND_TIMEOUT", rejected.Response.GetProperty("errorCode").GetString());
        Assert.AreEqual(session.SessionId, rejected.Response.GetProperty("sessionId").GetString());
        ValidateResponse(rejected);
        Assert.IsTrue(manager.AcceptCatHandshake(Handshake()).Accepted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SlowAcceptanceWriteCannotEnableExpiredSetup(bool control)
    {
        var clock = new FakeClock();
        var manager = Manager(clock);
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        if (control)
        {
            manager.ConfirmCatAcceptanceSent(session);
            manager.BindControl(Bind(session), 1);
        }
        clock.NowMs = 100;
        var exception = Assert.ThrowsExactly<ComConCommandProtocolException>(() =>
        {
            if (control) manager.ConfirmControlAcceptanceSent(session);
            else manager.ConfirmCatAcceptanceSent(session);
        });
        Assert.AreEqual("CONTROL_BIND_TIMEOUT", exception.ErrorCode);
        Assert.IsFalse(manager.CanRelayCat(session));
        Assert.IsTrue(manager.AcceptCatHandshake(Handshake()).Accepted);
    }

    [TestMethod]
    public void ActiveSessionHasNoSetupExpiryAndStaleSessionNeverBindsNewOwner()
    {
        var clock = new FakeClock();
        var manager = Manager(clock);
        var first = Activate(manager);
        clock.NowMs = 10_000;
        Assert.HasCount(0, manager.ExpireSetups());
        Assert.IsTrue(manager.CanRelayCat(first));
        manager.EndSession(first, "CAT disconnected.");
        var second = manager.AcceptCatHandshake(Handshake()).Session!;
        AssertReject(manager.BindControl(Bind(first), 1), "UNKNOWN_SESSION");
        Assert.AreEqual(ComConCommandSessionState.AwaitingCatAcceptance, second.State);
    }

    [TestMethod]
    public void FailedAcceptanceAndPrematureCatInputCanReleaseSessionWithoutReplay()
    {
        var manager = Manager(new FakeClock());
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        Assert.IsFalse(manager.CanRelayCat(session));
        manager.EndSession(session, "Premature CAT input; transport must discard buffered bytes.");
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.ConfirmCatAcceptanceSent(session));
        Assert.IsFalse(manager.CanRelayCat(session));
        Assert.IsTrue(manager.AcceptCatHandshake(Handshake()).Accepted);
    }

    [TestMethod]
    public void SessionHandlesCannotControlAnotherManager()
    {
        var first = Manager(new FakeClock());
        var second = Manager(new FakeClock());
        var session = first.AcceptCatHandshake(Handshake()).Session!;
        Assert.IsFalse(second.CanRelayCat(session));
        Assert.ThrowsExactly<InvalidOperationException>(() => second.EndSession(session, "Wrong owner."));
        Assert.ThrowsExactly<InvalidOperationException>(() => second.ConfirmCatAcceptanceSent(session));
        Assert.ThrowsExactly<InvalidOperationException>(() => second.AcceptCatReady(session, Ready(session)));
        AssertReject(first.AcceptCatHandshake(Handshake()), "PROFILE_BUSY");
    }

    [TestMethod]
    public async Task ConcurrentHandshakesReserveOnlyOneOwner()
    {
        var manager = Manager(new FakeClock());
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => manager.AcceptCatHandshake(Handshake()))));
        Assert.HasCount(1, results.Where(result => result.Accepted).ToArray());
        foreach (var rejected in results.Where(result => !result.Accepted))
            AssertReject(rejected, "PROFILE_BUSY");
    }

    [TestMethod]
    public void GuardsTimeoutOverflowAndOutOfOrderConfirmations()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ComConCommandSessions(new FakeClock(), 0, []));
        var manager = Manager(new FakeClock { NowMs = long.MaxValue });
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.AcceptCatHandshake(Handshake()));
        manager = Manager(new FakeClock());
        var session = manager.AcceptCatHandshake(Handshake()).Session!;
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.ConfirmControlAcceptanceSent(session));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => manager.BindControl(Bind(session), 5));
        manager.ConfirmCatAcceptanceSent(session);
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.ConfirmCatAcceptanceSent(session));
    }

    private static ComConCommandSessions Manager(FakeClock clock) => new(clock, 100, [new(Rig, 1)]);

    private static ComConCommandSession Activate(ComConCommandSessions manager, string rig = Rig)
    {
        var session = manager.AcceptCatHandshake(Handshake(rig)).Session!;
        manager.ConfirmCatAcceptanceSent(session);
        var bound = manager.BindControl(Bind(session), session.ProfileId);
        Assert.IsTrue(bound.Accepted);
        ValidateResponse(bound);
        manager.ConfirmControlAcceptanceSent(session);
        manager.AcceptCatReady(session, Ready(session));
        return session;
    }

    private static byte[] Handshake(string rig = Rig) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        messageType = "cat.handshake", contractVersion = ComConCommandProtocol.ContractVersion, rigProfileId = rig
    });

    private static byte[] Bind(ComConCommandSession session, int? profileId = null, string? sessionId = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = profileId ?? session.ProfileId, sessionId = sessionId ?? session.SessionId
        });

    private static byte[] Ready(ComConCommandSession session, int? profileId = null, string? sessionId = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = profileId ?? session.ProfileId, sessionId = sessionId ?? session.SessionId
        });

    private static void AssertReject(ComConCommandSetupResult result, string code)
    {
        Assert.IsFalse(result.Accepted);
        Assert.IsNull(result.Session);
        Assert.AreEqual(code, result.Response.GetProperty("errorCode").GetString());
        Assert.IsFalse(result.Response.TryGetProperty("sessionId", out _));
        Assert.IsFalse(result.Response.TryGetProperty("profileId", out _));
        ValidateResponse(result);
    }

    private static void ValidateResponse(ComConCommandSetupResult result) =>
        ComConCommandProtocol.ParseEnvelope(Encoding.UTF8.GetBytes(result.Response.GetRawText()));

    private sealed class FakeClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
