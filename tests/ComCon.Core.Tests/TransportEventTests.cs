using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class TransportEventTests
{
    [TestMethod]
    public void EventFactoriesPreserveKindsMetadataAndDefensivelyCopyBytes()
    {
        var context = Context();
        byte[] source = [1, 2, 3];
        var observed = TransportEvent.Observe(context, TransportEventKind.BytesObserved, source);
        source[0] = 99;
        var copy = observed.CopyBytes();
        copy[1] = 88;

        Assert.AreEqual(TransportEventKind.BytesObserved, observed.Kind);
        Assert.AreEqual(3, observed.ByteCount);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, observed.CopyBytes());

        var identity = DxIdentity();
        var write = TransportEvent.Write(context, Guid.NewGuid(), identity, AdapterWriteCompletion.Succeeded);
        Assert.AreEqual(TransportEventKind.WriteSettled, write.Kind);
        Assert.AreEqual(identity, write.Identity);
        Assert.HasCount(0, write.CopyBytes());

        var deadline = TransportEvent.DeadlineExpired(context, TransportDeadline.Queue);
        Assert.AreEqual(TransportEventKind.DeadlineElapsed, deadline.Kind);
        Assert.AreEqual(TransportDeadline.Queue, deadline.Deadline);

        var fault = TransportEvent.Fault(context, TransportFaultCategory.Io);
        Assert.AreEqual(TransportEventKind.IoFault, fault.Kind);
        Assert.AreEqual(TransportFaultCategory.Io, fault.FaultCategory);
    }

    [TestMethod]
    [DataRow(TransportEventKind.BytesObserved, 1)]
    [DataRow(TransportEventKind.BytesObserved, 4096)]
    [DataRow(TransportEventKind.FramingProgress, 1)]
    [DataRow(TransportEventKind.FramingProgress, 256)]
    [DataRow(TransportEventKind.RecordObserved, 1)]
    [DataRow(TransportEventKind.RecordObserved, 256)]
    public void ObserveAcceptsInclusiveByteBounds(TransportEventKind kind, int length)
    {
        var observation = TransportEvent.Observe(Context(), kind, new byte[length]);

        Assert.AreEqual(length, observation.ByteCount);
        Assert.HasCount(length, observation.CopyBytes());
    }

    [TestMethod]
    public void FactoriesRejectNullContextsInvalidEnumsAndInvalidArguments()
    {
        var context = Context();
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            TransportEvent.Observe(null!, TransportEventKind.BytesObserved, [1]));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            TransportEvent.Write(null!, Guid.NewGuid(), DxIdentity(), AdapterWriteCompletion.Succeeded));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            TransportEvent.DeadlineExpired(null!, TransportDeadline.Queue));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            TransportEvent.Fault(null!, TransportFaultCategory.Io));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Observe(context, TransportEventKind.BytesObserved, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Observe(context, TransportEventKind.BytesObserved, new byte[4097]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Observe(context, TransportEventKind.RecordObserved, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Observe(context, TransportEventKind.RecordObserved, new byte[257]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Observe(context, TransportEventKind.WriteSettled, [1]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Observe(context, (TransportEventKind)int.MaxValue, [1]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.DeadlineExpired(context, (TransportDeadline)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Fault(context, (TransportFaultCategory)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Write(context, Guid.Empty, DxIdentity(), AdapterWriteCompletion.Succeeded));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            TransportEvent.Write(context, Guid.NewGuid(), null!, AdapterWriteCompletion.Succeeded));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TransportEvent.Write(context, Guid.NewGuid(), DxIdentity(), (AdapterWriteCompletion)int.MaxValue));
    }

    [TestMethod]
    public void WriteFactoryRejectsInvalidAuthenticatedIdentities()
    {
        var context = Context();
        var externalSession = Guid.NewGuid();
        var invalidIdentities = new[]
        {
            new RequestIdentity(RequestOrigin.DXLab, Guid.NewGuid(), 1),
            new RequestIdentity(RequestOrigin.External, Guid.Empty, 1),
            new RequestIdentity(RequestOrigin.DXLab, Guid.Empty, 0),
            new RequestIdentity(RequestOrigin.External, externalSession, -1),
            new RequestIdentity((RequestOrigin)int.MaxValue, externalSession, 1)
        };

        foreach (var identity in invalidIdentities)
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                TransportEvent.Write(context, Guid.NewGuid(), identity, AdapterWriteCompletion.Succeeded));
        }
    }

    [TestMethod]
    public void FaultFactoryMapsEveryCategoryToItsDistinctEventKind()
    {
        var mappings = new[]
        {
            (TransportFaultCategory.Framing, TransportEventKind.FramingAnomaly),
            (TransportFaultCategory.Overflow, TransportEventKind.Overflow),
            (TransportFaultCategory.Disconnected, TransportEventKind.EndpointDisconnected),
            (TransportFaultCategory.Io, TransportEventKind.IoFault),
            (TransportFaultCategory.Delivery, TransportEventKind.DeliveryFailed)
        };

        foreach (var (category, kind) in mappings)
        {
            var observation = TransportEvent.Fault(Context(), category);
            Assert.AreEqual(kind, observation.Kind);
            Assert.AreEqual(category, observation.FaultCategory);
        }
    }

    [TestMethod]
    public void ConstructorValidatesProfileGenerationsEpochClockAndSink()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewStream(profileId: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewStream(profileId: 5));
        Assert.IsNotNull(NewStream(profileId: 4));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewStream(physicalGeneration: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewStream(virtualGeneration: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewStream(ownershipEpoch: 0));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new TransportEventStream(1, 1, 1, 1, null!, _ => true));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new TransportEventStream(1, 1, 1, 1, new FakeClock(), null!));
    }

    [TestMethod]
    public void ProfileEndpointAndEpochAreFixedForStreamLifetime()
    {
        var stream = NewStream();
        Assert.AreEqual(7L, stream.OwnershipEpoch);

        Assert.AreEqual(TransportEventReceipt.WrongProfile,
            stream.Report(TransportEvent.Observe(Context(profileId: 2), TransportEventKind.BytesObserved, [1])));
        Assert.AreEqual(TransportEventReceipt.WrongEpoch,
            stream.Report(TransportEvent.Observe(Context(epoch: 8), TransportEventKind.BytesObserved, [1])));
        Assert.IsTrue(stream.IsFaulted);
    }

    [TestMethod]
    public void WrongProfileAndStaleGenerationReturnExplicitReceiptsWithoutChangingCurrentStream()
    {
        var stream = NewStream();

        Assert.AreEqual(TransportEventReceipt.WrongProfile,
            stream.Report(TransportEvent.Observe(Context(profileId: 2), TransportEventKind.BytesObserved, [1])));
        Assert.IsFalse(stream.IsFaulted);
        Assert.AreEqual(TransportEventReceipt.StaleGeneration,
            stream.Report(TransportEvent.Observe(Context(generation: 98), TransportEventKind.BytesObserved, [1])));
        Assert.IsFalse(stream.IsFaulted);
        Assert.AreEqual(TransportEventReceipt.Accepted,
            stream.Report(TransportEvent.Observe(Context(), TransportEventKind.BytesObserved, [2])));
    }

    [TestMethod]
    public void EndpointSequencesAreIndependentAndMustBeExactlyContiguousFromOne()
    {
        var startsOutOfOrder = NewStream();
        Assert.AreEqual(TransportEventReceipt.InvalidOrder,
            startsOutOfOrder.Report(TransportEvent.Observe(Context(sequence: 2),
                TransportEventKind.RecordObserved, [0])));
        Assert.IsTrue(startsOutOfOrder.IsFaulted);

        var stream = NewStream();

        Assert.AreEqual(TransportEventReceipt.Accepted,
            stream.Report(TransportEvent.Observe(Context(sequence: 1), TransportEventKind.RecordObserved, [1])));
        Assert.AreEqual(TransportEventReceipt.Accepted,
            stream.Report(TransportEvent.Observe(Context(endpoint: TransportEndpoint.VirtualClient,
                generation: 20, sequence: 1),
                TransportEventKind.RecordObserved, [2])));
        Assert.AreEqual(TransportEventReceipt.InvalidOrder,
            stream.Report(TransportEvent.Observe(Context(sequence: 3), TransportEventKind.RecordObserved, [3])));
        Assert.IsTrue(stream.IsFaulted);
        Assert.AreEqual(TransportEventReceipt.Faulted,
            stream.Report(TransportEvent.Observe(Context(endpoint: TransportEndpoint.VirtualClient,
                generation: 20, sequence: 2),
                TransportEventKind.RecordObserved, [4])));
    }

    [TestMethod]
    public void InvalidCurrentContextAndClockOrderLatchFault()
    {
        var invalidEndpoint = NewStream().Report(TransportEvent.Observe(
            Context(endpoint: (TransportEndpoint)int.MaxValue), TransportEventKind.BytesObserved, [1]));
        Assert.AreEqual(TransportEventReceipt.InvalidContext, invalidEndpoint);

        var negativeSequence = NewStream().Report(TransportEvent.Observe(
            Context(sequence: 0), TransportEventKind.BytesObserved, [1]));
        Assert.AreEqual(TransportEventReceipt.InvalidContext, negativeSequence);

        var clock = new FakeClock { NowMs = 5 };
        var stream = NewStream(clock: clock);
        Assert.AreEqual(TransportEventReceipt.InvalidContext,
            stream.Report(TransportEvent.Observe(Context(observedMs: 6), TransportEventKind.BytesObserved, [1])));
        Assert.IsTrue(stream.IsFaulted);

        clock = new FakeClock { NowMs = 2 };
        stream = NewStream(clock: clock);
        Assert.AreEqual(TransportEventReceipt.Accepted,
            stream.Report(TransportEvent.Observe(Context(observedMs: 2), TransportEventKind.BytesObserved, [1])));
        Assert.AreEqual(TransportEventReceipt.InvalidContext,
            stream.Report(TransportEvent.Observe(Context(sequence: 2, observedMs: 1),
                TransportEventKind.BytesObserved, [2])));
        Assert.IsTrue(stream.IsFaulted);
    }

    [TestMethod]
    public void MonotonicClockCannotMoveBackward()
    {
        var clock = new FakeClock { NowMs = 10 };
        var stream = NewStream(clock: clock);
        Assert.AreEqual(TransportEventReceipt.Accepted,
            stream.Report(TransportEvent.Observe(Context(observedMs: 10), TransportEventKind.BytesObserved, [1])));

        clock.NowMs = 9;
        Assert.AreEqual(TransportEventReceipt.InvalidContext,
            stream.Report(TransportEvent.Observe(Context(sequence: 2, observedMs: 9),
                TransportEventKind.BytesObserved, [2])));
        Assert.IsTrue(stream.IsFaulted);
    }

    [TestMethod]
    public void QueueDeadlineIsAnObservationButOtherDeadlinesLatchFault()
    {
        foreach (var (deadline, expected) in new[]
        {
            (TransportDeadline.Queue, TransportEventReceipt.Accepted),
            (TransportDeadline.Write, TransportEventReceipt.FaultRecorded),
            (TransportDeadline.Transaction, TransportEventReceipt.FaultRecorded),
            (TransportDeadline.Transition, TransportEventReceipt.FaultRecorded)
        })
        {
            var stream = NewStream();
            var result = stream.Report(TransportEvent.DeadlineExpired(Context(), deadline));
            Assert.AreEqual(expected, result, deadline.ToString());
            Assert.AreEqual(deadline != TransportDeadline.Queue, stream.IsFaulted, deadline.ToString());
        }
    }

    [TestMethod]
    public void ExplicitFaultEventsLatchAndAreDelivered()
    {
        foreach (var category in Enum.GetValues<TransportFaultCategory>())
        {
            var stream = NewStream();
            Assert.AreEqual(TransportEventReceipt.FaultRecorded,
                stream.Report(TransportEvent.Fault(Context(), category)));
            Assert.IsTrue(stream.IsFaulted);
        }
    }

    [TestMethod]
    public void SuccessfulWriteIsOnlyReportedAndDoesNotSettleDispatch()
    {
        var stream = NewStream();
        var action = Dispatch(stream);
        var write = TransportEvent.Write(Context(observedMs: action.DispatchedMs),
            action.TransactionId, action.Identity, AdapterWriteCompletion.Succeeded);

        Assert.AreEqual(TransportEventReceipt.Accepted, stream.Report(write));
        Assert.IsFalse(stream.IsFaulted);
        Assert.IsTrue(stream.HasUnresolvedDispatch);
        Assert.ThrowsExactly<InvalidOperationException>(() => stream.RegisterDispatch(action));
    }

    [TestMethod]
    [DataRow(AdapterWriteCompletion.ProvenZeroBytes)]
    [DataRow(AdapterWriteCompletion.Unknown)]
    public void UnsuccessfulWriteReportsLatchFault(AdapterWriteCompletion completion)
    {
        var stream = NewStream();
        var action = Dispatch(stream);

        Assert.AreEqual(TransportEventReceipt.FaultRecorded,
            stream.Report(TransportEvent.Write(Context(observedMs: action.DispatchedMs),
                action.TransactionId, action.Identity, completion)));
        Assert.IsTrue(stream.IsFaulted);
        Assert.IsTrue(stream.HasUnresolvedDispatch);
    }

    [TestMethod]
    public void WriteMustMatchPhysicalEndpointDispatchAndBeUnique()
    {
        var wrongEndpoint = NewStream();
        var endpointAction = Dispatch(wrongEndpoint);
        Assert.AreEqual(TransportEventReceipt.WrongEndpoint,
            wrongEndpoint.Report(TransportEvent.Write(
                Context(endpoint: TransportEndpoint.VirtualClient, generation: 20,
                    observedMs: endpointAction.DispatchedMs),
                endpointAction.TransactionId, endpointAction.Identity, AdapterWriteCompletion.Succeeded)));
        Assert.IsTrue(wrongEndpoint.IsFaulted);

        var wrongDispatch = NewStream();
        var dispatchAction = Dispatch(wrongDispatch);
        Assert.AreEqual(TransportEventReceipt.WrongDispatch,
            wrongDispatch.Report(TransportEvent.Write(Context(observedMs: dispatchAction.DispatchedMs),
                Guid.NewGuid(), dispatchAction.Identity, AdapterWriteCompletion.Succeeded)));
        Assert.IsTrue(wrongDispatch.IsFaulted);

        var duplicate = NewStream();
        var duplicateAction = Dispatch(duplicate);
        Assert.AreEqual(TransportEventReceipt.Accepted,
            duplicate.Report(TransportEvent.Write(Context(observedMs: duplicateAction.DispatchedMs),
                duplicateAction.TransactionId, duplicateAction.Identity, AdapterWriteCompletion.Succeeded)));
        Assert.AreEqual(TransportEventReceipt.DuplicateWrite,
            duplicate.Report(TransportEvent.Write(Context(sequence: 2, observedMs: duplicateAction.DispatchedMs),
                duplicateAction.TransactionId, duplicateAction.Identity, AdapterWriteCompletion.Succeeded)));
        Assert.IsTrue(duplicate.IsFaulted);
    }

    [TestMethod]
    public void WriteObservationBeforeDispatchTimeLatchesInvalidContext()
    {
        var stream = NewStream();
        var action = Dispatch(stream, dispatchedMs: 5);

        Assert.AreEqual(TransportEventReceipt.InvalidContext,
            stream.Report(TransportEvent.Write(Context(observedMs: 4), action.TransactionId,
                action.Identity, AdapterWriteCompletion.Succeeded)));
        Assert.IsTrue(stream.IsFaulted);
        Assert.IsTrue(stream.HasUnresolvedDispatch);
    }

    [TestMethod]
    public void RegisterDispatchRejectsInvalidEnvelopeAndDeadlineBudgets()
    {
        var identity = DxIdentity();
        var payload = BrokerPayload.Create([1]);
        var valid = ValidDispatch(identity, payload);
        var invalid = new[]
        {
            valid with { TransactionId = Guid.Empty },
            valid with { OwnershipEpoch = 0 },
            valid with { Envelope = null! },
            valid with { Envelope = valid.Envelope with { Identity = null! } },
            valid with { Envelope = valid.Envelope with { Payload = null! } },
            valid with { Envelope = valid.Envelope with { Identity = identity with { RequestId = 0 } } },
            valid with { Envelope = valid.Envelope with { Identity = identity with { Origin = RequestOrigin.External } } },
            valid with { Envelope = valid.Envelope with { Identity = identity with { Origin = (RequestOrigin)int.MaxValue } } },
            valid with { Envelope = valid.Envelope with { CompletionMode = (ClientCompletionMode)int.MaxValue } },
            valid with { DispatchedMs = -1 },
            valid with { WriteDeadlineMs = valid.DispatchedMs },
            valid with { TotalDeadlineMs = valid.WriteDeadlineMs - 1 }
        };

        foreach (var action in invalid)
        {
            var stream = NewStream();
            Assert.ThrowsExactly<ArgumentException>(() => stream.RegisterDispatch(action));
            Assert.IsFalse(stream.HasUnresolvedDispatch);
            Assert.IsFalse(stream.IsFaulted);
        }

        var invalidExternal = ValidDispatch(
            new RequestIdentity(RequestOrigin.External, Guid.Empty, 1), payload);
        Assert.ThrowsExactly<ArgumentException>(() => NewStream().RegisterDispatch(invalidExternal));
    }

    [TestMethod]
    public void RegisterDispatchRejectsNullAndSecondDispatchButKeepsFirstUnresolved()
    {
        var stream = NewStream();
        Assert.ThrowsExactly<ArgumentNullException>(() => stream.RegisterDispatch(null!));
        var action = Dispatch(stream);
        Assert.ThrowsExactly<InvalidOperationException>(() => stream.RegisterDispatch(action));
        Assert.IsTrue(stream.HasUnresolvedDispatch);
    }

    [TestMethod]
    public void SinkRejectionAndExceptionLatchFaultWithoutSettlingDispatch()
    {
        var rejected = NewStream(sink: _ => false);
        Dispatch(rejected);
        Assert.AreEqual(TransportEventReceipt.DeliveryFailed,
            rejected.Report(TransportEvent.Observe(Context(), TransportEventKind.RecordObserved, [1])));
        Assert.IsTrue(rejected.IsFaulted);
        Assert.IsTrue(rejected.HasUnresolvedDispatch);

        var throwing = NewStream(sink: _ => throw new IOException("sink failure"));
        Dispatch(throwing);
        Assert.ThrowsExactly<IOException>(() =>
            throwing.Report(TransportEvent.Observe(Context(), TransportEventKind.RecordObserved, [1])));
        Assert.IsTrue(throwing.IsFaulted);
        Assert.IsTrue(throwing.HasUnresolvedDispatch);
    }

    [TestMethod]
    public void StreamRejectsReentrantAndCrossThreadActorCalls()
    {
        TransportEventStream? stream = null;
        stream = NewStream(sink: _ =>
        {
            stream!.Report(TransportEvent.Observe(Context(sequence: 2), TransportEventKind.RecordObserved, [2]));
            return true;
        });
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            stream.Report(TransportEvent.Observe(Context(), TransportEventKind.RecordObserved, [1])));
        Assert.IsTrue(stream.IsFaulted);

        var actorConfined = NewStream();
        Exception? crossThreadError = null;
        var worker = new Thread(() =>
        {
            try
            {
                actorConfined.Report(TransportEvent.Observe(
                    Context(), TransportEventKind.RecordObserved, [1]));
            }
            catch (Exception exception)
            {
                crossThreadError = exception;
            }
        });
        worker.Start();
        worker.Join();
        Assert.IsInstanceOfType<InvalidOperationException>(crossThreadError);
        Assert.IsFalse(actorConfined.IsFaulted);
    }

    [TestMethod]
    public void ProfilesMaintainIndependentStreamState()
    {
        var first = NewStream(profileId: 1);
        var second = NewStream(profileId: 2);

        Assert.AreEqual(TransportEventReceipt.Accepted,
            first.Report(TransportEvent.Observe(Context(profileId: 1), TransportEventKind.RecordObserved, [1])));
        Assert.AreEqual(TransportEventReceipt.Accepted,
            second.Report(TransportEvent.Observe(Context(profileId: 2), TransportEventKind.RecordObserved, [2])));
        Assert.AreEqual(TransportEventReceipt.InvalidOrder,
            first.Report(TransportEvent.Observe(Context(profileId: 1, sequence: 3),
                TransportEventKind.RecordObserved, [3])));
        Assert.IsTrue(first.IsFaulted);
        Assert.IsFalse(second.IsFaulted);
        Assert.AreEqual(TransportEventReceipt.Accepted,
            second.Report(TransportEvent.Observe(Context(profileId: 2, sequence: 2),
                TransportEventKind.RecordObserved, [4])));
    }

    [TestMethod]
    public void OldGenerationStreamCannotAffectNewStreamEvenWithUnresolvedDispatch()
    {
        var oldStream = NewStream(physicalGeneration: 10);
        Dispatch(oldStream);
        var newStream = NewStream(physicalGeneration: 11);

        Assert.AreEqual(TransportEventReceipt.StaleGeneration,
            newStream.Report(TransportEvent.Observe(Context(generation: 10),
                TransportEventKind.RecordObserved, [1])));
        Assert.IsFalse(newStream.IsFaulted);
        Assert.IsTrue(oldStream.HasUnresolvedDispatch);
        Assert.IsFalse(oldStream.IsFaulted);
        Assert.AreEqual(TransportEventReceipt.Accepted,
            newStream.Report(TransportEvent.Observe(Context(generation: 11),
                TransportEventKind.RecordObserved, [2])));
    }

    [TestMethod]
    public void NoReplyCompletionAndReceiveActivityCannotSettleRouterTransaction()
    {
        var fixture = new RouterFixture();
        var session = fixture.Router.OpenSession();
        Assert.AreEqual(RouterCode.Pending, fixture.Router.Acquire(session, 1).Code);
        fixture.Clock.NowMs = 50;
        fixture.Router.Tick();
        var lease = fixture.Results.OfType<AcquisitionCompleted>().Single().Result.Lease!;
        Assert.AreEqual(RouterState.ExternalOwned, fixture.Router.State);

        var identity = new RequestIdentity(RequestOrigin.External, session, 2);
        var request = new RouterRequest(identity, BrokerPayload.Create([0x01, 0x02]),
            lease.OwnershipEpoch, ClientCompletionMode.NoReplyExpected, lease.LeaseId);
        Assert.AreEqual(RouterCode.Accepted, fixture.Router.Submit(request));
        var dispatch = fixture.Router.TryDispatch();
        Assert.IsNotNull(dispatch);

        var stream = NewStream(clock: fixture.Clock, ownershipEpoch: dispatch.OwnershipEpoch);
        stream.RegisterDispatch(dispatch);
        var host = new FakeHost(stream, fixture.Router, fixture.Clock);

        Assert.AreEqual(ClientSignalReceipt.AcceptedAdvisoryOnly,
            fixture.Router.ReportClientSignal(new(dispatch.TransactionId, identity, ClientTransactionSignalKind.Completed)));
        Assert.AreEqual(TransportEventReceipt.Accepted, host.DeliverPhysicalRecord([0xAA]));
        Assert.AreEqual(TransportEventReceipt.Accepted, host.DeliverPhysicalRecord([0xBB]));
        Assert.AreEqual(2, host.DeliveredRecordCount);
        Assert.AreEqual(0, host.ReplyReceivedCalls);
        Assert.IsTrue(stream.HasUnresolvedDispatch);
        Assert.IsTrue(fixture.Router.HasOutstandingTransaction);
        Assert.AreEqual(RouterState.ExternalOwned, fixture.Router.State);

        fixture.Clock.NowMs = dispatch.TotalDeadlineMs;
        fixture.Router.Tick();
        Assert.AreEqual(RouterState.Faulted, fixture.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, fixture.Results.OfType<CommandCompleted>().Single().Result.Outcome);
        Assert.AreEqual(0, host.ReplyReceivedCalls);
    }

    [TestMethod]
    public void FakeHostConsumesAndRejectsGatedVirtualInputWithoutQueueingReplay()
    {
        var fixture = new RouterFixture();
        var stream = NewStream(clock: fixture.Clock, ownershipEpoch: fixture.Router.OwnershipEpoch);
        var host = new FakeHost(stream, fixture.Router, fixture.Clock) { VirtualInputGated = true };

        host.ReceiveVirtualInput([0x10, 0x20]);

        Assert.AreEqual(1, host.VirtualInputsConsumed);
        Assert.AreEqual(1, host.VirtualInputsRejected);
        Assert.AreEqual(0, host.VirtualInputsForwarded);
        Assert.AreEqual(0, fixture.Router.QueuedCount);
        Assert.IsEmpty(host.ForwardedVirtualInput);
    }

    [TestMethod]
    public void CloseSessionRemainsAnExplicitRouterActionSeparateFromTransportEvents()
    {
        var fixture = new RouterFixture();
        var session = fixture.Router.OpenSession();
        fixture.Router.Acquire(session, 1);
        fixture.Clock.NowMs = 50;
        fixture.Router.Tick();
        Assert.AreEqual(RouterState.ExternalOwned, fixture.Router.State);
        var delivered = new List<TransportEvent>();
        var stream = NewStream(clock: fixture.Clock, ownershipEpoch: fixture.Router.OwnershipEpoch,
            sink: observation =>
            {
                delivered.Add(observation);
                return true;
            });

        fixture.Router.CloseSession(session);

        Assert.AreEqual(RouterState.Transitioning, fixture.Router.State);
        Assert.IsEmpty(delivered);
        Assert.IsFalse(stream.IsFaulted);
    }

    private static RequestIdentity DxIdentity() => new(RequestOrigin.DXLab, Guid.Empty, 1);

    private static TransportEventContext Context(
        int profileId = 1,
        TransportEndpoint endpoint = TransportEndpoint.Physical,
        long generation = 10,
        long sequence = 1,
        long epoch = 7,
        long observedMs = 0) =>
        new(profileId, endpoint, generation, sequence, epoch, observedMs);

    private static TransportEventStream NewStream(
        int profileId = 1,
        long physicalGeneration = 10,
        long virtualGeneration = 20,
        long ownershipEpoch = 7,
        FakeClock? clock = null,
        Func<TransportEvent, bool>? sink = null) =>
        new(profileId, physicalGeneration, virtualGeneration, ownershipEpoch,
            clock ?? new FakeClock(), sink ?? (_ => true));

    private static DispatchAction Dispatch(
        TransportEventStream stream,
        long dispatchedMs = 0,
        RequestIdentity? identity = null)
    {
        var action = ValidDispatch(identity ?? DxIdentity(), BrokerPayload.Create([1]), dispatchedMs);
        stream.RegisterDispatch(action);
        return action;
    }

    private static DispatchAction ValidDispatch(
        RequestIdentity identity,
        BrokerPayload payload,
        long dispatchedMs = 0) =>
        new(Guid.NewGuid(),
            new ClientTransactionEnvelope(identity, payload, ClientCompletionMode.NoReplyExpected),
            7, dispatchedMs, dispatchedMs + 500, dispatchedMs + 250);

    private sealed class FakeClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }

    private sealed class FakeHost(TransportEventStream stream, ProfileRouter router, FakeClock clock)
    {
        public bool VirtualInputGated { get; init; }
        public int DeliveredRecordCount { get; private set; }
        public int ReplyReceivedCalls { get; private set; }
        public int VirtualInputsConsumed { get; private set; }
        public int VirtualInputsRejected { get; private set; }
        public int VirtualInputsForwarded { get; private set; }
        public List<byte[]> ForwardedVirtualInput { get; } = [];

        public TransportEventReceipt DeliverPhysicalRecord(byte[] record)
        {
            clock.NowMs++;
            var observation = TransportEvent.Observe(
                new TransportEventContext(1, TransportEndpoint.Physical, 10, DeliveredRecordCount + 1,
                    stream.OwnershipEpoch, clock.NowMs),
                TransportEventKind.RecordObserved, record);
            var receipt = stream.Report(observation);
            if (receipt == TransportEventReceipt.Accepted)
            {
                DeliveredRecordCount++;
                router.ReceiveActivity(hasPartialFrame: false);
            }
            return receipt;
        }

        public void ReceiveVirtualInput(byte[] input)
        {
            VirtualInputsConsumed++;
            if (VirtualInputGated)
            {
                VirtualInputsRejected++;
                return;
            }
            VirtualInputsForwarded++;
            ForwardedVirtualInput.Add(input.ToArray());
        }
    }

    private sealed class RouterFixture
    {
        private readonly List<RouterNotification> notifications = [];

        public FakeClock Clock { get; } = new();
        public ProfileRouter Router { get; }
        public IReadOnlyList<RouterNotification> Results => notifications;

        public RouterFixture()
        {
            Router = new ProfileRouter(Clock, new(), notification =>
            {
                notifications.Add(notification);
                return true;
            });
            Assert.AreEqual(RouterCode.Accepted, Router.Start(adapterVerifiedReady: true).Code);
        }
    }
}
