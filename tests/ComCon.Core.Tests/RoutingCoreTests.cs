using ComCon.Core.Configuration;
using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class RoutingCoreTests
{
    [TestMethod]
    public void DefaultsAndIncoherentLimitsAreValidated()
    {
        var limits = new RoutingLimits();
        Assert.AreEqual(500, limits.CatTransactionTimeoutMs);
        Assert.AreEqual(250, limits.CatWriteTimeoutMs);
        Assert.AreEqual(250, limits.CatReplyTimeoutMs);
        Assert.AreEqual(50, limits.QuietGuardMs);
        Assert.AreEqual(1000, limits.TransitionTimeoutMs);
        Assert.AreEqual(5000, limits.LeaseDurationMs);
        Assert.ThrowsExactly<ConfigurationValidationException>(() => new Fixture(limits with { QuietGuardMs = 0 }));
        Assert.ThrowsExactly<ConfigurationValidationException>(() => new Fixture(limits with { CatTransactionTimeoutMs = 499 }));
        Assert.ThrowsExactly<ArgumentException>(() => new Fixture(limits with { TransitionTimeoutMs = 549 }));
        Assert.ThrowsExactly<ArgumentException>(() => new Fixture(limits with { CatTransactionTimeoutMs = 501 }));
        Assert.ThrowsExactly<ArgumentException>(() => new Fixture(limits with { NormalQueueCapacityPerProfile = 2 }));
    }

    [TestMethod]
    public void StartRequiresExplicitAdapterReadinessAndEmitsNoWrite()
    {
        var clock = new FakeClock();
        var router = new ProfileRouter(clock, new(), _ => true);
        Assert.AreEqual(RouterCode.NotReady, router.Start(false).Code);
        Assert.AreEqual(RouterState.Stopped, router.State);
        Assert.AreEqual(RouterCode.Accepted, router.Start(true).Code);
        Assert.IsNull(router.TryDispatch());
        Assert.AreEqual(RouterCode.Busy, router.Start(true).Code);
    }

    [TestMethod]
    public void OpaquePayloadAndDispatchCopiesCannotAliasCallerBuffers()
    {
        byte[] input = [1, 2, 3];
        var payload = BrokerPayload.Create(input);
        input[0] = 99;
        Assert.AreEqual((byte)1, payload.CopyBytes()[0]);
        var copy = payload.CopyBytes();
        copy[0] = 77;
        var fixture = new Fixture();
        fixture.Router.Submit(fixture.Dx(payload));
        Assert.AreEqual((byte)1, fixture.Adapter.Dispatch().Payload.CopyBytes()[0]);
        Assert.AreEqual(RouterCode.Accepted, fixture.Router.Submit(fixture.Dx(
            BrokerPayload.Create([0xFE, 0x00, 0xFF]))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BrokerPayload.Create([]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BrokerPayload.Create(new byte[257]));
    }

    [TestMethod]
    public void AcquireGrantsOnlyAtFiftyMillisecondsAndCompetingAcquireIsBusy()
    {
        var f = new Fixture();
        var session = f.Router.OpenSession();
        var competitor = f.Router.OpenSession();
        Assert.AreEqual(RouterCode.Pending, f.Router.Acquire(session, 7).Code);
        Assert.AreEqual(RouterCode.Busy, f.Router.Acquire(competitor, 8).Code);
        f.At(49);
        Assert.IsEmpty(f.Acquisitions);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(50);
        Assert.HasCount(1, f.Acquisitions);
        var granted = f.Acquisitions[0];
        Assert.AreEqual(7L, granted.RequestId);
        Assert.AreEqual(session, granted.Result.Lease!.SessionId);
        Assert.AreNotEqual(Guid.Empty, granted.Result.Lease.LeaseId);
        Assert.AreEqual(RouterState.ExternalOwned, f.Router.State);
        Assert.AreEqual(5050L, granted.Result.Lease.ExpiresMs);
    }

    [TestMethod]
    public void PendingAcquireDisconnectAtGuardCannotGrantOrphan()
    {
        var f = new Fixture();
        var session = f.Router.OpenSession();
        f.Router.Acquire(session, 1);
        f.Clock.NowMs = 50;
        f.Router.CloseSession(session);
        Assert.HasCount(1, f.Acquisitions);
        Assert.AreEqual(RouterCode.SessionClosed, f.Acquisitions[0].Result.Code);
        f.Router.Tick();
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Acquire(session, 2).Code);
    }

    [TestMethod]
    public void ReceiveAtGuardRestartsQuietBeforeGrant()
    {
        var f = new Fixture();
        var session = f.Router.OpenSession();
        f.Router.Acquire(session, 1);
        f.Clock.NowMs = 50;
        f.Router.ReceiveActivity(false);
        f.At(99);
        Assert.IsEmpty(f.Acquisitions);
        f.At(100);
        Assert.HasCount(1, f.Acquisitions);
    }

    [TestMethod]
    public void PartialReceiveBlocksHandoverUntilExplicitlyResolvedAndQuiet()
    {
        var f = new Fixture();
        f.Router.ReceiveActivity(true);
        f.Router.Acquire(f.Router.OpenSession(), 1);
        f.At(500);
        Assert.IsEmpty(f.Acquisitions);
        f.Router.ReceiveActivity(false);
        f.At(549);
        Assert.IsEmpty(f.Acquisitions);
        f.At(550);
        Assert.AreEqual(RouterState.ExternalOwned, f.Router.State);
    }

    [TestMethod]
    public void PartialReceiveAtTransitionDeadlineFaultsAndNeverRecoversBySilence()
    {
        var f = new Fixture();
        f.Router.ReceiveActivity(true);
        f.Router.Acquire(f.Router.OpenSession(), 1);
        f.At(999);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(1000);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.AreEqual(RouterCode.Faulted, f.Acquisitions.Single().Result.Code);
        f.Router.ReceiveActivity(false);
        f.At(10_000);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
    }

    [TestMethod]
    public void RetargetingTransitionDoesNotExtendItsClosureDeadline()
    {
        var f = new Fixture();
        f.Router.ReceiveActivity(true);
        f.Router.Acquire(f.Router.OpenSession(), 1);
        f.Clock.NowMs = 500;
        f.Router.SetOverride(true, true);
        f.At(999);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(1000);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
    }

    [TestMethod]
    public void RenewJustBeforeExpiryExtendsFromAcceptance()
    {
        var f = new Fixture();
        var lease = f.Own();
        f.Clock.NowMs = lease.ExpiresMs - 1;
        var result = f.Router.Renew(lease.SessionId, lease.LeaseId);
        Assert.AreEqual(RouterCode.Accepted, result.Code);
        Assert.AreEqual(10049L, result.Lease!.ExpiresMs);
        f.At(lease.ExpiresMs);
        Assert.AreEqual(RouterState.ExternalOwned, f.Router.State);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void RenewAtOrAfterExpiryCannotResurrect(int delay)
    {
        var f = new Fixture();
        var lease = f.Own();
        f.Clock.NowMs = lease.ExpiresMs + delay;
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Renew(lease.SessionId, lease.LeaseId).Code);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        Assert.IsNull(f.Router.TryDispatch());
        f.At(lease.ExpiresMs + delay + 50);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
    }

    [TestMethod]
    public void LeasesAreBoundToServerSessionsAndEpochs()
    {
        var f = new Fixture();
        var lease = f.Own();
        var foreign = f.Router.OpenSession();
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Renew(foreign, lease.LeaseId).Code);
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Release(lease.SessionId, Guid.NewGuid()).Code);
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Submit(f.External(lease, foreign)));
        Assert.AreEqual(RouterCode.StaleEpoch,
            f.Router.Submit(f.External(lease) with { OwnershipEpoch = lease.OwnershipEpoch - 1 }));
        Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(f.External(lease)));
    }

    [TestMethod]
    public void HolderEofRevokesAndRejectsQueuedWork()
    {
        var f = new Fixture();
        var lease = f.Own();
        f.Router.Submit(f.External(lease));
        f.Router.CloseSession(lease.SessionId);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        Assert.AreEqual(RouterCode.SessionClosed, f.Results.Single().Code);
        Assert.AreEqual(DispatchOutcome.NotDispatched, f.Results.Single().Outcome);
        Assert.AreEqual(0, f.Router.QueuedCount);
        f.At(100);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
    }

    [TestMethod]
    public void OverrideRequiresOperatorAuthorizationAndSuspendsButKeepsLease()
    {
        var f = new Fixture();
        var lease = f.Own();
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.SetOverride(true, false).Code);
        Assert.AreEqual(RouterState.ExternalOwned, f.Router.State);
        f.Router.Submit(f.External(lease));
        f.Router.SetOverride(true, true);
        Assert.AreEqual(RouterCode.OwnershipChanged, f.Results.Single().Code);
        f.At(100);
        Assert.AreEqual(RouterState.OverrideDxLab, f.Router.State);
        Assert.AreEqual(RouterCode.Unauthorized,
            f.Router.Submit(f.External(lease) with { OwnershipEpoch = f.Router.OwnershipEpoch }));
        Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(f.Dx()));
        var renewed = f.Router.Renew(lease.SessionId, lease.LeaseId);
        Assert.AreEqual(RouterCode.Accepted, renewed.Code);
        f.Router.SetOverride(false, true);
        f.At(150);
        Assert.AreEqual(RouterState.ExternalOwned, f.Router.State);
        Assert.AreEqual(0, f.Router.QueuedCount);
        Assert.AreEqual(RouterCode.Accepted,
            f.Router.Submit(f.External(renewed.Lease!) with { OwnershipEpoch = f.Router.OwnershipEpoch }));
    }

    [TestMethod]
    public void LeaseExpiryDuringOverrideDoesNotLoseOverride()
    {
        var f = new Fixture();
        var lease = f.Own();
        f.Router.SetOverride(true, true);
        f.At(100);
        f.At(lease.ExpiresMs);
        Assert.AreEqual(RouterState.OverrideDxLab, f.Router.State);
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Renew(lease.SessionId, lease.LeaseId).Code);
        f.Router.SetOverride(false, true);
        f.At(lease.ExpiresMs + 50);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
    }

    [TestMethod]
    public void SuspendedLeaseReleaseAndDisconnectDoNotEndOverride()
    {
        foreach (var disconnect in new[] { false, true })
        {
            var f = new Fixture();
            var lease = f.Own();
            f.Router.SetOverride(true, true);
            f.At(100);
            if (disconnect) f.Router.CloseSession(lease.SessionId);
            else Assert.AreEqual(RouterCode.Accepted, f.Router.Release(lease.SessionId, lease.LeaseId).Code);
            Assert.AreEqual(RouterState.OverrideDxLab, f.Router.State);
            f.Router.SetOverride(false, true);
            f.At(150);
            Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
        }
    }

    [TestMethod]
    public void PendingOverrideCancelsAcquireWithoutGrant()
    {
        var f = new Fixture();
        f.Router.Acquire(f.Router.OpenSession(), 1);
        f.Router.SetOverride(true, true);
        Assert.AreEqual(RouterCode.OwnershipChanged, f.Acquisitions.Single().Result.Code);
        f.At(50);
        Assert.AreEqual(RouterState.OverrideDxLab, f.Router.State);
        Assert.HasCount(1, f.Acquisitions);
    }

    [TestMethod]
    public void QueueCapacityIncludesOutstandingRequestAndNeverSilentlyDrops()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        f.Adapter.Dispatch();
        for (var i = 0; i < 3; i++) Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(f.Dx()));
        Assert.AreEqual(RouterCode.Busy, f.Router.Submit(f.Dx()));
        Assert.AreEqual(3, f.Router.QueuedCount);
        Assert.AreEqual(DispatchOutcome.NotDispatched, f.Results.Single().Outcome);
        Assert.IsNull(f.Router.TryDispatch());
        Assert.AreEqual(1, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void ProfileQueueCapacityCanBeConfiguredBelowClientLimitCeiling()
    {
        var f = new Fixture(new RoutingLimits { NormalQueueCapacityPerProfile = 2, PendingRequestsPerClient = 2 });
        Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(f.Dx()));
        Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(f.Dx()));
        Assert.AreEqual(RouterCode.Busy, f.Router.Submit(f.Dx()));
        Assert.AreEqual(2, f.Router.QueuedCount);
    }

    [TestMethod]
    public void QueueExpiresAtBoundaryAndIdleTickReleasesBuffers()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        f.At(249);
        Assert.AreEqual(1, f.Router.QueuedCount);
        Assert.IsEmpty(f.Results);
        f.At(250);
        Assert.AreEqual(0, f.Router.QueuedCount);
        Assert.AreEqual(RouterCode.QueueExpired, f.Results.Single().Code);
        Assert.IsNull(f.Router.TryDispatch());
        Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(f.Dx()));
    }

    [TestMethod]
    public void DispatchChecksQueueAgeWithoutPriorTick()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        f.Clock.NowMs = 250;
        Assert.IsNull(f.Router.TryDispatch());
        Assert.AreEqual(RouterCode.QueueExpired, f.Results.Single().Code);
        Assert.AreEqual(0, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void OwnershipBoundaryRejectsOldEpochAndPreservesDispatchedOrigin()
    {
        var f = new Fixture();
        var first = f.Dx();
        f.Router.Submit(first);
        var action = f.Adapter.Dispatch();
        f.Router.Submit(f.Dx());
        f.Router.Acquire(f.Router.OpenSession(), 91);
        Assert.AreEqual(RouterCode.OwnershipChanged, f.Results.Single().Code);
        Assert.IsNull(f.Router.TryDispatch());
        f.Clock.NowMs = 40;
        f.Adapter.Reply(action);
        f.Adapter.Write(action);
        Assert.AreEqual(first.Identity, f.Results.Last().Identity);
        Assert.AreEqual(first.OwnershipEpoch, f.Results.Last().DispatchedEpoch);
        f.At(89);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(90);
        Assert.AreEqual(RouterState.ExternalOwned, f.Router.State);
        Assert.AreEqual(1, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void ReplyBeforeWriteReturnDoesNotDispatchNextOrCompletePrematurely()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        f.Router.Submit(f.Dx());
        var first = f.Adapter.Dispatch();
        f.Adapter.Reply(first);
        Assert.IsEmpty(f.Results);
        Assert.IsTrue(f.Router.HasOutstandingTransaction);
        Assert.IsNull(f.Router.TryDispatch());
        f.Clock.NowMs = 249;
        f.Adapter.Write(first);
        Assert.HasCount(1, f.Results);
        Assert.AreEqual(DispatchOutcome.ReplyReceived, f.Results.Single().Outcome);
        Assert.IsNotNull(f.Router.TryDispatch());
    }

    [TestMethod]
    public void ReplyBufferIsCopiedAndNegativeRadioResultIsNotSuccess()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        byte[] reply = [7, 8];
        f.Router.ReplyReceived(action.TransactionId, reply, radioRejected: true);
        reply[0] = 99;
        f.Adapter.Write(action);
        Assert.AreEqual((byte)7, f.Results.Single().Reply[0]);
        Assert.AreEqual(RouterCode.RadioRejected, f.Results.Single().Code);
        Assert.AreEqual(DispatchOutcome.ReplyReceived, f.Results.Single().Outcome);
    }

    [TestMethod]
    public void ClientCompletionAndAbortSignalsAreAdvisoryOnly()
    {
        var f = new Fixture();
        var request = f.Dx(completionMode: ClientCompletionMode.ReplyExpected);
        Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(request));
        var action = f.Adapter.Dispatch();
        f.Adapter.Write(action);
        var session = f.Router.OpenSession();
        Assert.AreEqual(RouterCode.Pending, f.Router.Acquire(session, 1).Code);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);

        Assert.AreEqual(ClientSignalReceipt.AcceptedAdvisoryOnly, f.Router.ReportClientSignal(
            new(action.TransactionId, action.Identity, ClientTransactionSignalKind.Completed)));
        Assert.AreEqual(ClientSignalReceipt.AcceptedAdvisoryOnly, f.Router.ReportClientSignal(
            new(action.TransactionId, action.Identity, ClientTransactionSignalKind.Aborted)));
        Assert.IsTrue(f.Router.HasOutstandingTransaction);
        Assert.IsEmpty(f.Results);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        Assert.IsNull(f.Router.TryDispatch());

        f.At(500);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, f.Results.Single().Outcome);
    }

    [TestMethod]
    public void NoReplyExpectationIsCarriedToDispatchButCannotBeCompletedByClientOrReply()
    {
        var f = new Fixture();
        Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(
            f.Dx(completionMode: ClientCompletionMode.NoReplyExpected)));
        var action = f.Adapter.Dispatch();
        Assert.AreEqual(ClientCompletionMode.NoReplyExpected, action.CompletionMode);
        f.Adapter.Write(action);

        Assert.AreEqual(ClientSignalReceipt.AcceptedAdvisoryOnly, f.Router.ReportClientSignal(
            new(action.TransactionId, action.Identity, ClientTransactionSignalKind.Completed)));
        Assert.IsTrue(f.Router.HasOutstandingTransaction);

        f.Router.ReplyReceived(action.TransactionId, [1]);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, f.Results.Single().Outcome);
    }

    [TestMethod]
    public void ClientSignalFromAnotherIdentityCannotAffectOutstandingTransaction()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();

        Assert.AreEqual(ClientSignalReceipt.IdentityMismatch, f.Router.ReportClientSignal(
            new(action.TransactionId,
                action.Identity with { RequestId = action.Identity.RequestId + 1 },
                ClientTransactionSignalKind.Completed)));
        Assert.IsTrue(f.Router.HasOutstandingTransaction);
        Assert.IsEmpty(f.Results);
    }

    [TestMethod]
    public void WriteDeadlineEqualityFaultsEvenIfReplyAlreadyArrived()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Adapter.Reply(action);
        f.At(249);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
        f.Clock.NowMs = 250;
        f.Adapter.Write(action);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, f.Results.Single().Outcome);
    }

    [TestMethod]
    [DataRow(0, 249, false)]
    [DataRow(0, 250, true)]
    [DataRow(249, 498, false)]
    [DataRow(249, 499, true)]
    [DataRow(249, 500, true)]
    public void ReplyDeadlineIsCappedAfterWriteAndEqualityExpires(int writeAt, int replyAt, bool expired)
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        Assert.AreEqual(500L, action.TotalDeadlineMs);
        Assert.AreEqual(250L, action.WriteDeadlineMs);
        f.Clock.NowMs = writeAt;
        f.Adapter.Write(action);
        f.Clock.NowMs = replyAt;
        f.Adapter.Reply(action);
        Assert.AreEqual(expired ? RouterState.Faulted : RouterState.DXLabOwned, f.Router.State);
        Assert.AreEqual(expired ? DispatchOutcome.OutcomeUnknown : DispatchOutcome.ReplyReceived,
            f.Results.Single().Outcome);
    }

    [TestMethod]
    public void MissingWriteCompletionAndLongSilenceNeverGrantOwnership()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Router.Acquire(f.Router.OpenSession(), 1);
        f.Adapter.Reply(action);
        f.At(249);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(250);
        f.At(500);
        f.At(5000);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.AreEqual(RouterCode.Faulted, f.Acquisitions.Single().Result.Code);
        Assert.AreEqual(1, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void ReleaseDuringWritePreservesOriginalExternalResponseAndDeadline()
    {
        var f = new Fixture();
        var lease = f.Own();
        var request = f.External(lease);
        f.Router.Submit(request);
        var action = f.Adapter.Dispatch();
        f.Clock.NowMs = 100;
        f.Router.Release(lease.SessionId, lease.LeaseId);
        Assert.IsNull(f.Router.TryDispatch());
        Assert.AreEqual(550L, action.TotalDeadlineMs);
        f.Clock.NowMs = 200;
        f.Adapter.Write(action);
        f.Clock.NowMs = 400;
        f.Adapter.Reply(action);
        Assert.AreEqual(request.Identity, f.Results.Single().Identity);
        Assert.AreEqual(request.OwnershipEpoch, f.Results.Single().DispatchedEpoch);
        f.At(449);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(450);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
    }

    [TestMethod]
    public void LeaseExpiryDuringWriteDoesNotPermitConcurrentOwner()
    {
        var f = new Fixture();
        var lease = f.Own();
        f.Clock.NowMs = lease.ExpiresMs - 100;
        f.Router.Submit(f.External(lease));
        var action = f.Adapter.Dispatch();
        f.Clock.NowMs = lease.ExpiresMs;
        f.Adapter.Write(action);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        Assert.IsNull(f.Router.TryDispatch());
        f.Adapter.Reply(action);
        f.At(lease.ExpiresMs + 49);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(lease.ExpiresMs + 50);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
    }

    [TestMethod]
    [DataRow(AdapterWriteCompletion.Unknown, DispatchOutcome.OutcomeUnknown)]
    [DataRow(AdapterWriteCompletion.ProvenZeroBytes, DispatchOutcome.NotDispatched)]
    public void WriteFailureFaultsWithExplicitOutcome(AdapterWriteCompletion completion, DispatchOutcome outcome)
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Router.WriteCompleted(action.TransactionId, completion);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.AreEqual(outcome, f.Results.Single().Outcome);
        Assert.IsNull(f.Router.TryDispatch());
    }

    [TestMethod]
    public void EarlyReplyContradictsProvenZeroWriteAndRemainsUnknown()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Adapter.Reply(action);
        f.Router.WriteCompleted(action.TransactionId, AdapterWriteCompletion.ProvenZeroBytes);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, f.Results.Single().Outcome);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
    }

    [TestMethod]
    public void TaintRejectsQueuedWorkAndCannotBeUndoneByValidReply()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Router.Submit(f.Dx());
        f.Router.AdapterFault("Malformed addressed candidate.");
        f.Adapter.Reply(action);
        f.At(5000);
        Assert.HasCount(2, f.Results);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, f.Results[0].Outcome);
        Assert.AreEqual(DispatchOutcome.NotDispatched, f.Results[1].Outcome);
        Assert.AreEqual(0, f.Router.QueuedCount);
        Assert.IsNull(f.Router.TryDispatch());
    }

    [TestMethod]
    public void DuplicateReplyAfterSettlementFaultsBeforeNextDispatch()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Adapter.Write(action);
        f.Adapter.Reply(action);
        f.Router.Submit(f.Dx());
        f.Adapter.Reply(action);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.HasCount(2, f.Results);
        Assert.AreEqual(DispatchOutcome.ReplyReceived, f.Results[0].Outcome);
        Assert.AreEqual(DispatchOutcome.NotDispatched, f.Results[1].Outcome);
        Assert.IsNull(f.Router.TryDispatch());
        Assert.AreEqual(1, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void OrphanTerminalAndDuplicateWriteFaultPermanently()
    {
        var orphan = new Fixture();
        orphan.Router.ReplyReceived(Guid.NewGuid(), [1]);
        Assert.AreEqual(RouterState.Faulted, orphan.Router.State);
        var duplicate = new Fixture();
        duplicate.Router.Submit(duplicate.Dx());
        var action = duplicate.Adapter.Dispatch();
        duplicate.Adapter.Write(action);
        duplicate.Adapter.Write(action);
        Assert.AreEqual(RouterState.Faulted, duplicate.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, duplicate.Results.Single().Outcome);
    }

    [TestMethod]
    public void DuplicateIdsAreNeverRedispatchedAndRejectedIdsRemainConsumed()
    {
        var f = new Fixture();
        var request = f.Dx();
        Assert.AreEqual(RouterCode.Accepted, f.Router.Submit(request));
        Assert.AreEqual(RouterCode.DuplicateRequest, f.Router.Submit(request));
        var action = f.Adapter.Dispatch();
        f.Adapter.Write(action);
        f.Adapter.Reply(action);
        Assert.AreEqual(RouterCode.DuplicateRequest, f.Router.Submit(request));
        Assert.IsNull(f.Router.TryDispatch());
        var stale = f.Dx() with { OwnershipEpoch = 0 };
        Assert.AreEqual(RouterCode.StaleEpoch, f.Router.Submit(stale));
        Assert.AreEqual(RouterCode.DuplicateRequest,
            f.Router.Submit(stale with { OwnershipEpoch = f.Router.OwnershipEpoch }));
        Assert.AreEqual(1, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void CleanStopDrainsQueueInvalidatesSessionsAndAllowsExplicitRestart()
    {
        var f = new Fixture();
        var session = f.Router.OpenSession();
        f.Router.Submit(f.Dx());
        var epoch = f.Router.OwnershipEpoch;
        f.Router.Stop();
        Assert.AreEqual(RouterState.Stopped, f.Router.State);
        Assert.AreEqual(RouterCode.Stopped, f.Results.Single().Code);
        Assert.AreEqual(DispatchOutcome.NotDispatched, f.Results.Single().Outcome);
        Assert.AreEqual(0, f.Adapter.WriteCount);
        Assert.AreEqual(RouterCode.Accepted, f.Router.Start(true).Code);
        Assert.IsGreaterThan(epoch, f.Router.OwnershipEpoch);
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Acquire(session, 1).Code);
        Assert.AreNotEqual(session, f.Router.OpenSession());
        Assert.IsNull(f.Router.TryDispatch());
    }

    [TestMethod]
    public void StopWithInflightIsUnknownAndCannotClearAmbiguousFault()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Router.Submit(f.Dx());
        f.Router.Stop();
        Assert.AreEqual(RouterState.Stopped, f.Router.State);
        Assert.IsNotNull(f.Router.Fault);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, f.Results[0].Outcome);
        Assert.AreEqual(DispatchOutcome.NotDispatched, f.Results[1].Outcome);
        Assert.AreEqual(RouterCode.Faulted, f.Router.Start(true).Code);
        f.Adapter.Write(action);
        f.Adapter.Reply(action);
        Assert.AreEqual(RouterCode.Faulted, f.Router.Start(true).Code);
        Assert.IsNull(f.Router.TryDispatch());
        Assert.AreEqual(1, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void StopCancelsPendingAcquireAndNeverWritesCleanupCommands()
    {
        var f = new Fixture();
        f.Router.Acquire(f.Router.OpenSession(), 1);
        f.Router.Stop();
        Assert.AreEqual(RouterCode.Stopped, f.Acquisitions.Single().Result.Code);
        Assert.AreEqual(RouterCode.Accepted, f.Router.Start(true).Code);
        f.At(5000);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
        Assert.AreEqual(0, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void ProfilesAreIsolated()
    {
        var first = new Fixture();
        var second = new Fixture();
        var lease = first.Own();
        Assert.AreEqual(RouterCode.Unauthorized, second.Router.Acquire(lease.SessionId, 1).Code);
        first.Router.AdapterFault("Injected fault.");
        Assert.AreEqual(RouterState.DXLabOwned, second.Router.State);
        second.Router.Submit(second.Dx());
        Assert.IsNotNull(second.Router.TryDispatch());
        Assert.IsEmpty(second.Results);
    }

    [TestMethod]
    public void SessionLimitIsBoundedAndCloseReleasesCapacity()
    {
        var f = new Fixture(new RoutingLimits { ConnectionsPerProfile = 2 });
        var first = f.Router.OpenSession();
        f.Router.OpenSession();
        Assert.ThrowsExactly<InvalidOperationException>(() => f.Router.OpenSession());
        f.Router.CloseSession(first);
        Assert.AreNotEqual(Guid.Empty, f.Router.OpenSession());
    }

    [TestMethod]
    public void SinkOverflowFaultsWithoutRetainedOutputHistoryOrRedispatch()
    {
        var clock = new FakeClock();
        var deliveries = 0;
        var router = new ProfileRouter(clock, new(), _ => ++deliveries <= 1);
        router.Start(true);
        var request = new RouterRequest(new(RequestOrigin.DXLab, Guid.Empty, 1), Payload(),
            router.OwnershipEpoch, ClientCompletionMode.ReplyExpected);
        router.Submit(request);
        var action = router.TryDispatch()!;
        router.WriteCompleted(action.TransactionId, AdapterWriteCompletion.Succeeded);
        router.ReplyReceived(action.TransactionId, [1]);
        Assert.AreEqual(RouterState.Faulted, router.State);
        Assert.AreEqual(2, deliveries);
        Assert.AreEqual(0, router.QueuedCount);
        Assert.IsNull(router.TryDispatch());
        Assert.AreEqual(RouterCode.DeliveryFailed, router.Submit(request with
        {
            Identity = request.Identity with { RequestId = 2 }
        }));
        Assert.AreEqual(2, deliveries);
    }

    [TestMethod]
    public void SinkExceptionIsSurfacedAndLatchesFault()
    {
        var router = new ProfileRouter(new FakeClock(), new(), _ => throw new IOException("Consumer failed."));
        Assert.ThrowsExactly<IOException>(() => router.Start(true));
        Assert.AreEqual(RouterState.Faulted, router.State);
        Assert.AreEqual(RouterCode.Faulted, router.Start(true).Code);
        Assert.IsNull(router.TryDispatch());
    }

    [TestMethod]
    public void ActorRejectsReentrantAndCrossThreadCalls()
    {
        ProfileRouter? router = null;
        router = new ProfileRouter(new FakeClock(), new(), _ =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => router!.Tick());
            return true;
        });
        router.Start(true);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { router.Tick(); }
            catch (InvalidOperationException exception) { failure = exception; }
        });
        thread.Start();
        thread.Join();
        Assert.IsInstanceOfType<InvalidOperationException>(failure);
        Assert.AreEqual(RouterState.DXLabOwned, router.State);
    }

    [TestMethod]
    public void ClockRegressionFaultsExplicitly()
    {
        var f = new Fixture();
        f.At(100);
        f.Clock.NowMs = 99;
        Assert.ThrowsExactly<InvalidOperationException>(() => f.Router.Tick());
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
    }

    [TestMethod]
    public void QueueAt249CanDispatchAndTransactionBudgetStartsAtDispatch()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        f.Clock.NowMs = 249;
        var action = f.Adapter.Dispatch();
        Assert.AreEqual(249L, action.DispatchedMs);
        Assert.AreEqual(499L, action.WriteDeadlineMs);
        Assert.AreEqual(749L, action.TotalDeadlineMs);
        f.Adapter.Write(action);
        f.Clock.NowMs = 498;
        f.Adapter.Reply(action);
        Assert.AreEqual(DispatchOutcome.ReplyReceived, f.Results.Single().Outcome);
    }

    [TestMethod]
    public void HolderDisconnectDuringInflightPreservesOriginalSessionResult()
    {
        var f = new Fixture();
        var lease = f.Own();
        var request = f.External(lease);
        f.Router.Submit(request);
        var action = f.Adapter.Dispatch();
        f.Router.CloseSession(lease.SessionId);
        Assert.IsTrue(f.Router.HasOutstandingTransaction);
        Assert.IsNull(f.Router.TryDispatch());
        f.Adapter.Write(action);
        f.Adapter.Reply(action);
        Assert.AreEqual(request.Identity, f.Results.Single().Identity);
        Assert.AreEqual(DispatchOutcome.ReplyReceived, f.Results.Single().Outcome);
        f.At(100);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
    }

    [TestMethod]
    public void OverrideDuringEarlyReplyWaitsForWriteAndReturnsOriginalResult()
    {
        var f = new Fixture();
        var lease = f.Own();
        var request = f.External(lease);
        f.Router.Submit(request);
        var action = f.Adapter.Dispatch();
        f.Adapter.Reply(action);
        f.Router.SetOverride(true, true);
        f.At(100);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        Assert.IsEmpty(f.Results);
        f.Adapter.Write(action);
        Assert.AreEqual(request.Identity, f.Results.Single().Identity);
        f.At(149);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(150);
        Assert.AreEqual(RouterState.OverrideDxLab, f.Router.State);
        Assert.AreEqual(1, f.Adapter.WriteCount);
    }

    [TestMethod]
    public void ExpiryWhileReturningToSuspendedHolderRetargetsWithoutOrphan()
    {
        var f = new Fixture();
        var lease = f.Own();
        f.Router.SetOverride(true, true);
        f.At(100);
        f.Clock.NowMs = lease.ExpiresMs - 25;
        f.Router.SetOverride(false, true);
        f.At(lease.ExpiresMs);
        Assert.AreEqual(RouterState.Transitioning, f.Router.State);
        f.At(lease.ExpiresMs + 25);
        Assert.AreEqual(RouterState.DXLabOwned, f.Router.State);
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Renew(lease.SessionId, lease.LeaseId).Code);
    }

    [TestMethod]
    public void RxAfterResolvedReplyRestartsHandoverQuiet()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Router.Acquire(f.Router.OpenSession(), 1);
        f.Adapter.Write(action);
        f.Adapter.Reply(action);
        f.Clock.NowMs = 49;
        f.Router.ReceiveActivity(false);
        f.At(98);
        Assert.IsEmpty(f.Acquisitions);
        f.At(99);
        Assert.AreEqual(RouterState.ExternalOwned, f.Router.State);
    }

    [TestMethod]
    public void EchoOrUnrelatedRxDoesNotCompleteAndReplyDeadlineStillExpires()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Adapter.Write(action);
        f.Clock.NowMs = 249;
        f.Router.ReceiveActivity(false);
        Assert.IsEmpty(f.Results);
        f.At(250);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, f.Results.Single().Outcome);
    }

    [TestMethod]
    public void DuplicateEarlyTerminalIsUnknownBeforeWriteReturn()
    {
        var f = new Fixture();
        f.Router.Submit(f.Dx());
        var action = f.Adapter.Dispatch();
        f.Adapter.Reply(action);
        f.Adapter.Reply(action);
        Assert.AreEqual(RouterState.Faulted, f.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, f.Results.Single().Outcome);
        Assert.IsFalse(f.Router.HasOutstandingTransaction);
    }

    [TestMethod]
    public void WrongTransactionCompletionAndInvalidReplySizeFault()
    {
        var wrong = new Fixture();
        wrong.Router.Submit(wrong.Dx());
        wrong.Adapter.Dispatch();
        wrong.Router.WriteCompleted(Guid.NewGuid(), AdapterWriteCompletion.Succeeded);
        Assert.AreEqual(RouterState.Faulted, wrong.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, wrong.Results.Single().Outcome);
        var invalid = new Fixture();
        invalid.Router.Submit(invalid.Dx());
        var action = invalid.Adapter.Dispatch();
        invalid.Router.ReplyReceived(action.TransactionId, []);
        Assert.AreEqual(RouterState.Faulted, invalid.Router.State);
        Assert.AreEqual(DispatchOutcome.OutcomeUnknown, invalid.Results.Single().Outcome);
    }

    [TestMethod]
    public void SinkExceptionDuringFaultCannotRetainActiveOrQueuedBuffers()
    {
        var rejectResults = false;
        var router = new ProfileRouter(new FakeClock(), new(), notification =>
        {
            if (rejectResults && notification is CommandCompleted) throw new IOException("Blocked consumer.");
            return true;
        });
        router.Start(true);
        var request = new RouterRequest(new(RequestOrigin.DXLab, Guid.Empty, 1), Payload(),
            router.OwnershipEpoch, ClientCompletionMode.ReplyExpected);
        router.Submit(request);
        router.TryDispatch();
        router.Submit(request with { Identity = request.Identity with { RequestId = 2 } });
        rejectResults = true;
        Assert.ThrowsExactly<IOException>(() => router.AdapterFault("Taint."));
        Assert.AreEqual(RouterState.Faulted, router.State);
        Assert.AreEqual(0, router.QueuedCount);
        Assert.IsFalse(router.HasOutstandingTransaction);
        Assert.IsNull(router.TryDispatch());
    }

    [TestMethod]
    public void AcquisitionDeliveryFailureRevokesGrantAndFaults()
    {
        var clock = new FakeClock();
        var router = new ProfileRouter(clock, new(), notification => notification is not AcquisitionCompleted);
        router.Start(true);
        var session = router.OpenSession();
        router.Acquire(session, 1);
        clock.NowMs = 50;
        router.Tick();
        Assert.AreEqual(RouterState.Faulted, router.State);
        Assert.AreEqual(RouterCode.Unauthorized, router.Renew(session, Guid.NewGuid()).Code);
        Assert.IsNull(router.TryDispatch());
    }

    [TestMethod]
    public void RenewDoesNotResurrectPreviouslyRejectedSuspendedWork()
    {
        var f = new Fixture();
        var lease = f.Own();
        f.Router.SetOverride(true, true);
        f.At(100);
        var suspended = f.External(lease) with { OwnershipEpoch = f.Router.OwnershipEpoch };
        Assert.AreEqual(RouterCode.Unauthorized, f.Router.Submit(suspended));
        f.Router.Renew(lease.SessionId, lease.LeaseId);
        f.Router.SetOverride(false, true);
        f.At(150);
        Assert.AreEqual(RouterCode.DuplicateRequest,
            f.Router.Submit(suspended with { OwnershipEpoch = f.Router.OwnershipEpoch }));
        Assert.IsNull(f.Router.TryDispatch());
    }

    private static BrokerPayload Payload() => BrokerPayload.Create([1, 2]);

    private sealed class FakeClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }

    private sealed class FakeAdapter(ProfileRouter router)
    {
        public int WriteCount { get; private set; }
        public DispatchAction Dispatch()
        {
            var action = router.TryDispatch();
            Assert.IsNotNull(action);
            Assert.IsTrue(router.HasOutstandingTransaction);
            WriteCount++;
            return action;
        }
        public void Write(DispatchAction action) =>
            router.WriteCompleted(action.TransactionId, AdapterWriteCompletion.Succeeded);
        public void Reply(DispatchAction action) => router.ReplyReceived(action.TransactionId, [3, 4]);
    }

    private sealed class Fixture
    {
        private long nextId;
        private readonly List<RouterNotification> notifications = [];
        public FakeClock Clock { get; } = new();
        public ProfileRouter Router { get; }
        public FakeAdapter Adapter { get; }
        public List<CommandResult> Results => notifications.OfType<CommandCompleted>().Select(item => item.Result).ToList();
        public List<AcquisitionCompleted> Acquisitions => notifications.OfType<AcquisitionCompleted>().ToList();

        public Fixture(RoutingLimits? limits = null)
        {
            Router = new(Clock, limits ?? new(), notification =>
            {
                if (notifications.Count >= 128) return false;
                notifications.Add(notification);
                return true;
            });
            Adapter = new(Router);
            Assert.AreEqual(RouterCode.Accepted, Router.Start(true).Code);
        }

        public void At(long time)
        {
            Clock.NowMs = time;
            Router.Tick();
        }
        public RouterRequest Dx(BrokerPayload? payload = null,
            ClientCompletionMode completionMode = ClientCompletionMode.ReplyExpected) =>
            new(new(RequestOrigin.DXLab, Guid.Empty, ++nextId), payload ?? Payload(),
                Router.OwnershipEpoch, completionMode);
        public RouterRequest External(LeaseCapability lease, Guid? session = null) =>
            new(new(RequestOrigin.External, session ?? lease.SessionId, ++nextId), Payload(),
                lease.OwnershipEpoch, ClientCompletionMode.ReplyExpected, lease.LeaseId);
        public LeaseCapability Own()
        {
            var session = Router.OpenSession();
            Assert.AreEqual(RouterCode.Pending, Router.Acquire(session, ++nextId).Code);
            At(Clock.NowMs + 50);
            return Acquisitions.Last().Result.Lease!;
        }
    }
}
