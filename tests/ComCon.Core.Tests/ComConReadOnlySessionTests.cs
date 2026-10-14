using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ComConReadOnlySessionTests
{
    [TestMethod]
    public async Task LifecycleStatusReadsCurrentOffObservingFaultAndStopEvenOnDuplicate()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new TestClock());
        var session = new ComConReadOnlySession(1, "test-instance", new TestClock(), telemetry: bridge.Telemetry);
        var hello = session.Process(Hello()).Message.GetProperty("result");
        Assert.AreEqual("offlinePassiveLifecycleSimulation", hello.GetProperty("implementation").GetString());
        CollectionAssert.AreEqual(new[] { "HELLO", "GET_STATUS", "GET_TELEMETRY_STATUS", "GET_OBSERVATIONS" },
            hello.GetProperty("operations").EnumerateArray().Select(value => value.GetString()).ToArray());
        var request = Request(2, "GET_TELEMETRY_STATUS", "{}", hello.GetProperty("sessionId").GetString());
        JsonElement Current() => session.Process(request).Message.GetProperty("result");
        var off = Current();
        Assert.IsFalse(off.GetProperty("hardwareAccess").GetBoolean());
        Assert.IsFalse(off.GetProperty("safetyConfirmation").GetBoolean());
        Assert.AreEqual("Off", off.GetProperty("telemetry").GetProperty("state").GetString());
        Assert.IsEmpty(endpoints.OpenedPorts);
        await bridge.StartAsync(CommanderByteBridgeHostTests.CreateProfile() with
        {
            Label = "Fake lifecycle IPC", Framing = ComCon.Core.Models.FramingMode.Civ
        });
        await bridge.Telemetry.EnableSimulationAsync();
        var observing = Current().GetProperty("telemetry");
        Assert.AreEqual("Observing", observing.GetProperty("state").GetString());
        Assert.IsTrue(observing.GetProperty("enabled").GetBoolean());
        Assert.AreEqual(bridge.Telemetry.Snapshot.ServerInstanceId, observing.GetProperty("serverInstanceId").GetString());
        Assert.AreNotEqual("test-instance", observing.GetProperty("serverInstanceId").GetString());
        await endpoints.Get("COM3").SendAsync([0xFC]);
        await endpoints.Get("COM31").ReadWrittenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (bridge.Telemetry.Snapshot.Fault is null) await Task.Delay(5, deadline.Token);
        var fault = Current().GetProperty("telemetry");
        Assert.AreEqual("Faulted", fault.GetProperty("state").GetString());
        Assert.IsFalse(fault.GetProperty("enabled").GetBoolean());
        Assert.AreEqual(bridge.Telemetry.Snapshot.Fault, fault.GetProperty("fault").GetString());
        Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        await bridge.StopAsync();
        Assert.AreEqual("Off", Current().GetProperty("telemetry").GetProperty("state").GetString());
        Assert.HasCount(2, endpoints.OpenedPorts);
    }

    [TestMethod]
    public async Task LifecycleAdmissionRejectsStationMixedFeedsArgumentsAndControl()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var station = new CommanderByteBridgeHost(endpoints);
        await using var simulation = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new TestClock());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ComConReadOnlySession(1, "test", new TestClock(), telemetry: station.Telemetry));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ComConReadOnlySession(1, "test", new TestClock(), new SimulatedReceiveJournal(new TestClock()),
                simulation.Telemetry));
        Assert.IsEmpty(endpoints.OpenedPorts);
        var session = new ComConReadOnlySession(1, "test", new TestClock(), telemetry: simulation.Telemetry);
        var id = SessionId(session);
        AssertError(session.Process(Request(2, "GET_TELEMETRY_STATUS", """{"enable":true}""", id)), "INVALID_ARGUMENTS");
        AssertError(session.Process(Request(3, "GET_OBSERVATIONS", """{"afterObservationId":"0"}""", id)), "INVALID_ARGUMENTS");
        AssertError(session.Process(Request(4, "CAT_REQUEST", "{}", id)), "OPERATION_DISABLED");
        AssertError(NewSession().Process(Request(1, "GET_TELEMETRY_STATUS", "{}")), "HELLO_REQUIRED");
        var plain = NewSession();
        AssertError(plain.Process(Request(2, "GET_TELEMETRY_STATUS", "{}", SessionId(plain))), "OPERATION_DISABLED");
    }

    [TestMethod]
    public async Task LifecycleRetriesStillEnforceConflictExpirationAndCachedErrors()
    {
        var clock = new TestClock();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(
            new CommanderByteBridgeHostTests.FakeEndpointFactory(), clock);
        var session = new ComConReadOnlySession(1, "test", clock, telemetry: bridge.Telemetry);
        var id = SessionId(session);
        var invalid = Request(2, "GET_TELEMETRY_STATUS", """{"unexpected":0}""", id);
        Assert.AreEqual(session.Process(invalid).Message.GetRawText(), session.Process(invalid).Message.GetRawText());
        var request = Request(3, "GET_TELEMETRY_STATUS", "{}", id);
        session.Process(request);
        AssertError(session.Process(Request(3, "GET_TELEMETRY_STATUS", """{"unexpected":0}""", id)), "ID_CONFLICT");
        clock.NowMs = 60_000;
        AssertError(session.Process(request), "RESULT_EXPIRED");
    }

    [TestMethod]
    [DataRow("""{"afterObservationId":"0","captureId":"","profileGeneration":"1","ownershipEpoch":"2"}""")]
    [DataRow("""{"afterObservationId":"01","captureId":"capture","profileGeneration":"1","ownershipEpoch":"2"}""")]
    [DataRow("""{"afterObservationId":"0","captureId":"capture","profileGeneration":"01","ownershipEpoch":"2"}""")]
    [DataRow("""{"afterObservationId":"0","captureId":"capture","profileGeneration":"1","ownershipEpoch":"2","enable":true}""")]
    public async Task LifecycleObservationArgumentsAreStrictAndCannotEnableCapture(string args)
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new TestClock());
        var session = new ComConReadOnlySession(1, "pipe", new TestClock(), telemetry: bridge.Telemetry);
        AssertError(session.Process(Request(2, "GET_OBSERVATIONS", args, SessionId(session))), "INVALID_ARGUMENTS");
        Assert.IsEmpty(endpoints.OpenedPorts);
        Assert.IsFalse(bridge.Telemetry.Snapshot.Enabled);
    }

    [TestMethod]
    public async Task LifecycleObservationsBindCaptureAndRejectOffFaultAndOldReplays()
    {
        var clock = new TestClock();
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, clock);
        var profile = CommanderByteBridgeHostTests.CreateProfile() with { Framing = ComCon.Core.Models.FramingMode.Civ };
        await bridge.StartAsync(profile);
        var session = new ComConReadOnlySession(1, "pipe-host", clock, telemetry: bridge.Telemetry);
        var id = SessionId(session);
        ComConIpcRequest Query(long number, PassiveTelemetrySnapshot capture, string cursor = "0") =>
            Request(number, "GET_OBSERVATIONS", JsonSerializer.Serialize(new
            {
                afterObservationId = cursor, captureId = capture.ServerInstanceId,
                profileGeneration = capture.ProfileGeneration, ownershipEpoch = capture.OwnershipEpoch
            }), id);
        AssertError(session.Process(Query(2, bridge.Telemetry.Snapshot)), "TELEMETRY_OFF");
        await bridge.Telemetry.EnableSimulationAsync();
        var first = bridge.Telemetry.Snapshot;
        var journal = bridge.Telemetry.SimulationJournal!;
        journal.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        var request = Query(3, first);
        var batch = session.Process(request).Message.GetProperty("result");
        Assert.AreEqual("pipe-host", batch.GetProperty("serverInstanceId").GetString());
        Assert.AreEqual(first.ServerInstanceId, batch.GetProperty("captureId").GetString());
        Assert.AreEqual(first.OwnershipEpoch, batch.GetProperty("ownershipEpoch").GetString());
        clock.NowMs = 50;
        Assert.AreEqual(50, session.Process(request).Message.GetProperty("result")
            .GetProperty("observations")[0].GetProperty("ageMs").GetInt64());
        AssertError(session.Process(Query(4, first with { OwnershipEpoch = "999" })), "IDENTITY_CHANGED");
        AssertError(session.Process(Query(5, first, "2")), "CURSOR_INVALID");
        await bridge.Telemetry.DisableAsync();
        AssertError(session.Process(request), "TELEMETRY_OFF");
        AssertError(session.Process(Query(6, first)), "TELEMETRY_OFF");
        await bridge.Telemetry.EnableSimulationAsync();
        var second = bridge.Telemetry.Snapshot;
        bridge.Telemetry.SimulationJournal!.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        AssertError(session.Process(request), "IDENTITY_CHANGED");
        AssertError(session.Process(Query(7, first, "1")), "IDENTITY_CHANGED");
        var next = Query(8, second);
        Assert.IsTrue(session.Process(next).Message.GetProperty("ok").GetBoolean());
        await endpoints.Get("COM3").SendAsync([0xFC]);
        await endpoints.Get("COM31").ReadWrittenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (bridge.Telemetry.Snapshot.Fault is null) await Task.Delay(5, deadline.Token);
        AssertError(session.Process(next), "RECEIVE_FAULT");
        AssertError(session.Process(Query(9, second)), "RECEIVE_FAULT");
        await bridge.StopAsync();
        AssertError(session.Process(next), "TELEMETRY_OFF");
        await bridge.StartAsync(profile with { Id = 2, VirtualPort = "COM32", CommanderPort = "COM22" });
        AssertError(session.Process(Query(10, bridge.Telemetry.Snapshot)), "IDENTITY_CHANGED");
    }

    [TestMethod]
    public void HelloEstablishesStatusOnlySessionWithNoHardwareReadiness()
    {
        var session = NewSession();
        var reply = session.Process(Hello());
        var result = reply.Message.GetProperty("result");

        Assert.IsTrue(session.HasSession);
        Assert.AreEqual(1, result.GetProperty("profileId").GetInt32());
        Assert.IsFalse(result.GetProperty("ready").GetBoolean());
        Assert.AreEqual("offlineStatusOnly", result.GetProperty("implementation").GetString());
        Assert.IsFalse(reply.Message.TryGetProperty("error", out _));
    }

    [TestMethod]
    public void StatusReturnsExplicitStoppedSnapshot()
    {
        var session = NewSession();
        var id = SessionId(session);
        var reply = session.Process(Request(2, "GET_STATUS", "{}", id));
        var result = reply.Message.GetProperty("result");

        Assert.AreEqual("Stopped", result.GetProperty("state").GetString());
        Assert.IsFalse(result.GetProperty("ready").GetBoolean());
        Assert.IsFalse(result.GetProperty("hardwareAccess").GetBoolean());
        Assert.AreEqual(0, result.GetProperty("cacheObservations").GetArrayLength());
        Assert.IsFalse(result.TryGetProperty("leaseId", out _));
    }

    [TestMethod]
    [DataRow("ACQUIRE")]
    [DataRow("RENEW")]
    [DataRow("RELEASE")]
    [DataRow("SET_OVERRIDE")]
    [DataRow("CAT_REQUEST")]
    [DataRow("UNKNOWN")]
    public void EveryNonStatusOperationIsRejectedWithoutDispatch(string operation)
    {
        var session = NewSession();
        var id = SessionId(session);
        var reply = session.Process(Request(2, operation, """{"mode":"live","hex":"FEFE7AE11C0001FD"}""", id));

        AssertError(reply, "OPERATION_DISABLED");
        Assert.AreEqual("notDispatched", reply.Message.GetProperty("error").GetProperty("dispatchState").GetString());
        Assert.IsFalse(reply.CloseConnection);
    }

    [TestMethod]
    public void HelloRetryIgnoresPropertyOrderAndReturnsSameIdentity()
    {
        var session = NewSession();
        var first = session.Process(Hello());
        var second = session.Process(Request(1, "HELLO",
            """{"supportedVersions":[1],"clientName":"Test"}"""));

        Assert.AreEqual(first.Message.GetRawText(), second.Message.GetRawText());
    }

    [TestMethod]
    public void DuplicateStatusReturnsOriginalResponse()
    {
        var session = NewSession();
        var id = SessionId(session);
        var request = Request(2, "GET_STATUS", "{}", id);
        var first = session.Process(request);
        var second = session.Process(request);

        Assert.AreEqual(first.Message.GetRawText(), second.Message.GetRawText());
    }

    [TestMethod]
    public void ChangedDuplicateRequiresConnectionClose()
    {
        var session = NewSession();
        session.Process(Hello());
        var reply = session.Process(Request(1, "HELLO", """{"clientName":"Changed","supportedVersions":[1]}"""));

        AssertError(reply, "ID_CONFLICT");
        Assert.IsTrue(reply.CloseConnection);
    }

    [TestMethod]
    public void RetentionExpiresAtExactlySixtySeconds()
    {
        var clock = new TestClock();
        var session = new ComConReadOnlySession(1, "test-instance", clock);
        var id = SessionId(session);
        var request = Request(2, "GET_STATUS", "{}", id);
        session.Process(request);
        clock.NowMs = 59_999;
        Assert.IsTrue(session.Process(request).Message.GetProperty("ok").GetBoolean());
        clock.NowMs = 60_000;
        AssertError(session.Process(request), "RESULT_EXPIRED");
    }

    [TestMethod]
    public void ResponseRetentionIsBoundedToOneHundredTwentyEight()
    {
        var session = NewSession();
        var id = SessionId(session);
        for (var number = 2; number <= 129; number++)
            session.Process(Request(number, "GET_STATUS", "{}", id));

        AssertError(session.Process(Hello()), "RESULT_EXPIRED");
        Assert.IsTrue(session.Process(Request(2, "GET_STATUS", "{}", id)).Message.GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public void RejectedArgumentsConsumeIdAndOlderGapsNeverExecute()
    {
        var session = NewSession();
        var id = SessionId(session);
        AssertError(session.Process(Request(10, "GET_STATUS", """{"extra":1}""", id)), "INVALID_ARGUMENTS");
        AssertError(session.Process(Request(10, "GET_STATUS", "{}", id)), "ID_CONFLICT");
        AssertError(session.Process(Request(9, "GET_STATUS", "{}", id)), "RESULT_EXPIRED");
    }

    [TestMethod]
    [DataRow("""{"clientName":"","supportedVersions":[1]}""", "INVALID_ARGUMENTS")]
    [DataRow("""{"clientName":"Test","supportedVersions":["1"]}""", "INVALID_ARGUMENTS")]
    [DataRow("""{"clientName":"Test","supportedVersions":[2]}""", "UNSUPPORTED_VERSION")]
    [DataRow("""{"clientName":"Test","supportedVersions":[1],"extra":true}""", "INVALID_ARGUMENTS")]
    public void InvalidHelloFailsClosed(string args, string code)
    {
        var session = NewSession();
        var reply = session.Process(Request(1, "HELLO", args));
        AssertError(reply, code);
        Assert.IsTrue(reply.CloseConnection);
        Assert.IsFalse(session.HasSession);
    }

    [TestMethod]
    public void SessionIdentityIsConnectionBoundAndHelloIsRequired()
    {
        AssertError(NewSession().Process(Request(1, "GET_STATUS", "{}")), "HELLO_REQUIRED");
        var first = NewSession();
        var second = NewSession();
        var firstId = SessionId(first);
        SessionId(second);
        var reply = second.Process(Request(2, "GET_STATUS", "{}", firstId));
        AssertError(reply, "SESSION_INVALID");
        Assert.IsTrue(reply.CloseConnection);
    }

    private static ComConReadOnlySession NewSession() => new(1, "test-instance", new TestClock());

    private static ComConIpcRequest Hello() =>
        Request(1, "HELLO", """{"clientName":"Test","supportedVersions":[1]}""");

    private static string SessionId(ComConReadOnlySession session) =>
        session.Process(Hello()).Message.GetProperty("result").GetProperty("sessionId").GetString()!;

    private static ComConIpcRequest Request(long id, string operation, string args, string? session = null)
    {
        using var arguments = JsonDocument.Parse(args);
        return new(id, operation, session, arguments.RootElement.Clone());
    }

    private static void AssertError(ComConIpcReply reply, string code)
    {
        Assert.IsFalse(reply.Message.GetProperty("ok").GetBoolean());
        Assert.AreEqual(code, reply.Message.GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(reply.Message.TryGetProperty("result", out _));
    }

    private sealed class TestClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
