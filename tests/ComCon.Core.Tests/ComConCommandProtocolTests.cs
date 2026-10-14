using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ComCon.Core.Ipc;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ComConCommandProtocolTests
{
    private const string RigId = "12345678-abcd-4321-abcd-123456789abc";
    private const string SessionId = "87654321-abcd-4321-abcd-123456789abc";

    public static IEnumerable<object[]> ValidEnvelopes()
    {
        foreach (var type in new[]
        {
            "cat.handshake", "cat.accepted", "cat.ready", "cat.rejected", "control.bind", "control.bound",
            "control.rejected", "record.rejected", "record.outcome", "framing.notification",
            "session.notification", "notification.ack"
        })
            yield return [JsonSerializer.Serialize(Envelope(type))];

        var boundRejection = Envelope("control.rejected");
        boundRejection["profileId"] = 1;
        boundRejection["sessionId"] = SessionId;
        yield return [JsonSerializer.Serialize(boundRejection)];
        foreach (var status in new[] { "NOT_SENT", "UNKNOWN_DELIVERY" })
        {
            var outcome = Envelope("record.outcome");
            outcome["deliveryStatus"] = status;
            outcome["reason"] = "Physical write did not complete.";
            yield return [JsonSerializer.Serialize(outcome)];
        }
        var overflow = Envelope("framing.notification");
        overflow["errorCode"] = "FRAMING_OVERFLOW";
        yield return [JsonSerializer.Serialize(overflow)];
    }

    [TestMethod]
    [DynamicData(nameof(ValidEnvelopes))]
    public void ParsesEveryMessageShapeAndClonesIt(string json)
    {
        var result = ComConCommandProtocol.ParseEnvelope(Encoding.UTF8.GetBytes(json));
        Assert.AreEqual(ComConCommandProtocol.ContractVersion,
            result.GetProperty("contractVersion").GetString());
        Assert.AreEqual(json, result.GetRawText());
    }

    [TestMethod]
    [DynamicData(nameof(ValidEnvelopes))]
    public void RejectsMissingRequiredProperties(string json)
    {
        var envelope = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        foreach (var field in envelope.Keys.ToArray())
        {
            if (envelope["messageType"].GetString() == "control.rejected" &&
                field is "profileId" or "sessionId")
                continue;
            var missing = new Dictionary<string, JsonElement>(envelope);
            missing.Remove(field);
            AssertError("MISSING_FIELD", JsonSerializer.SerializeToUtf8Bytes(missing));
        }
    }

    [TestMethod]
    [DynamicData(nameof(ValidEnvelopes))]
    public void RejectsUnknownAndDuplicateProperties(string json)
    {
        AssertError("UNKNOWN_FIELD", Encoding.UTF8.GetBytes(json[..^1] + ",\"extra\":1}"));
        AssertError("DUPLICATE_FIELD", Encoding.UTF8.GetBytes(
            json[..^1] + ",\"contractVersion\":\"comcon-ipc-v1\"}"));
    }

    [TestMethod]
    [DynamicData(nameof(ValidEnvelopes))]
    public void RejectsNullInsteadOfAnyPresentField(string json)
    {
        var envelope = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        foreach (var field in envelope.Keys)
        {
            var invalid = new Dictionary<string, JsonElement>(envelope)
            {
                [field] = JsonSerializer.SerializeToElement<object?>(null)
            };
            var code = field switch
            {
                "messageType" => "INVALID_MESSAGE_TYPE",
                "rigProfileId" => "INVALID_RIG_PROFILE_ID",
                "profileId" => "INVALID_PROFILE_ID",
                "sessionId" => "INVALID_SESSION_ID",
                _ => "INVALID_FIELD"
            };
            AssertError(code, JsonSerializer.SerializeToUtf8Bytes(invalid));
        }
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("{")]
    [DataRow("{}{}")]
    [DataRow("{/*comment*/}")]
    public void RejectsInvalidJson(string json) =>
        AssertError("INVALID_JSON", Encoding.UTF8.GetBytes(json));

    [TestMethod]
    public void RejectsCommentsTrailingCommasAndNestedPayloads()
    {
        var json = JsonSerializer.Serialize(Envelope("cat.handshake"));
        AssertError("INVALID_JSON", Encoding.UTF8.GetBytes(json[..^1] + ",}"));
        AssertError("INVALID_JSON", Encoding.UTF8.GetBytes(json.Insert(1, "/*comment*/")));
        var nested = Envelope("cat.handshake");
        nested["rigProfileId"] = new { id = RigId };
        AssertError("INVALID_RIG_PROFILE_ID", JsonSerializer.SerializeToUtf8Bytes(nested));
    }

    [TestMethod]
    public void RejectsInvalidUtf8AndBom()
    {
        AssertError("INVALID_UTF8", [0xFF]);
        AssertError("INVALID_UTF8", [0xC0, 0xAF]);
        AssertError("INVALID_JSON", [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);
        AssertError("INVALID_JSON", []);
    }

    [TestMethod]
    [DataRow("""{"messageType":"\uD800","contractVersion":"comcon-ipc-v1"}""")]
    [DataRow("""{"messageType":"cat.rejected","contractVersion":"comcon-ipc-v1","errorCode":"PROFILE_BUSY","reason":"\uDC00"}""")]
    [DataRow("""{"messageType":"cat.handshake","contractVersion":"comcon-ipc-v1","\uD800":"x"}""")]
    public void RejectsUnpairedUnicodeEscapesAsInvalidJson(string json) =>
        AssertError("INVALID_JSON", Encoding.UTF8.GetBytes(json));

    [TestMethod]
    public void AcceptsPairedUnicodeEscapesAndDetectsDecodedDuplicatePropertyNames()
    {
        var json = """{"messageType":"cat.rejected","contractVersion":"comcon-ipc-v1","errorCode":"PROFILE_BUSY","reason":"\uD83D\uDE00"}""";
        Assert.AreEqual("\uD83D\uDE00",
            ComConCommandProtocol.ParseEnvelope(Encoding.UTF8.GetBytes(json)).GetProperty("reason").GetString());
        AssertError("DUPLICATE_FIELD", Encoding.UTF8.GetBytes(
            json[..^1] + ",\"\\u0072eason\":\"second reason\"}"));
    }

    [TestMethod]
    [DataRow("messageType", "CAT.HANDSHAKE", "INVALID_MESSAGE_TYPE")]
    [DataRow("contractVersion", "COMCON-IPC-V1", "UNSUPPORTED_VERSION")]
    [DataRow("rigProfileId", "12345678-ABCD-4321-abcd-123456789abc", "INVALID_RIG_PROFILE_ID")]
    [DataRow("rigProfileId", " 12345678-abcd-4321-abcd-123456789abc", "INVALID_RIG_PROFILE_ID")]
    [DataRow("rigProfileId", "12345678abcd4321abcd123456789abc", "INVALID_RIG_PROFILE_ID")]
    public void RejectsInvalidHandshakeValues(string field, string value, string code)
    {
        var envelope = Envelope("cat.handshake");
        envelope[field] = value;
        AssertError(code, JsonSerializer.SerializeToUtf8Bytes(envelope));
    }

    [TestMethod]
    public void RejectsWrongTypesNullAndPropertyCase()
    {
        foreach (var (field, value, code) in new (string, object?, string)[]
        {
            ("messageType", 1, "INVALID_MESSAGE_TYPE"),
            ("contractVersion", 1, "INVALID_FIELD"),
            ("contractVersion", null, "INVALID_FIELD"),
            ("rigProfileId", 1, "INVALID_RIG_PROFILE_ID"),
            ("profileId", "1", "INVALID_PROFILE_ID"),
            ("profileId", 0, "INVALID_PROFILE_ID"),
            ("profileId", 5, "INVALID_PROFILE_ID"),
            ("profileId", 1.5, "INVALID_PROFILE_ID"),
            ("sessionId", null, "INVALID_SESSION_ID"),
            ("sessionId", SessionId.ToUpperInvariant(), "INVALID_SESSION_ID"),
            ("notificationSequence", 1, "INVALID_FIELD"),
            ("notificationSequence", "", "INVALID_FIELD"),
            ("notificationSequence", "0", "INVALID_FIELD"),
            ("notificationSequence", "01", "INVALID_FIELD"),
            ("notificationSequence", "+1", "INVALID_FIELD"),
            ("notificationSequence", "1 ", "INVALID_FIELD"),
            ("notificationSequence", "\u0661", "INVALID_FIELD"),
            ("recordSequence", "1.0", "INVALID_FIELD"),
            ("deliveryStatus", "SUCCESS", "INVALID_FIELD")
        })
        {
            var envelope = Envelope(field is "rigProfileId" or "contractVersion" or "messageType"
                ? "cat.handshake" : "record.outcome");
            envelope[field] = value;
            AssertError(code, JsonSerializer.SerializeToUtf8Bytes(envelope));
        }
        var wrongCase = Envelope("cat.handshake");
        wrongCase["RigProfileId"] = wrongCase["rigProfileId"];
        wrongCase.Remove("rigProfileId");
        AssertError("UNKNOWN_FIELD", JsonSerializer.SerializeToUtf8Bytes(wrongCase));
    }

    [TestMethod]
    public void EnforcesConditionalFieldsAndCodeFamilies()
    {
        var success = Envelope("record.outcome");
        success["reason"] = "Not allowed.";
        AssertError("INVALID_FIELD", JsonSerializer.SerializeToUtf8Bytes(success));

        var failure = Envelope("record.outcome");
        failure["deliveryStatus"] = "UNKNOWN_DELIVERY";
        AssertError("MISSING_FIELD", JsonSerializer.SerializeToUtf8Bytes(failure));

        var rejected = Envelope("record.rejected");
        rejected["deliveryStatus"] = "UNKNOWN_DELIVERY";
        AssertError("INVALID_FIELD", JsonSerializer.SerializeToUtf8Bytes(rejected));

        var binding = Envelope("control.rejected");
        binding["profileId"] = 1;
        AssertError("MISSING_FIELD", JsonSerializer.SerializeToUtf8Bytes(binding));

        var framing = Envelope("framing.notification");
        framing["recordSequence"] = "1";
        AssertError("UNKNOWN_FIELD", JsonSerializer.SerializeToUtf8Bytes(framing));
        framing.Remove("recordSequence");
        framing["errorCode"] = "PROFILE_BUSY";
        AssertError("INVALID_FIELD", JsonSerializer.SerializeToUtf8Bytes(framing));

        var setup = Envelope("cat.rejected");
        setup["errorCode"] = "FRAMING_ANOMALY";
        AssertError("INVALID_FIELD", JsonSerializer.SerializeToUtf8Bytes(setup));
        setup["errorCode"] = "PROFILE_BUSY";
        setup["sessionId"] = SessionId;
        AssertError("UNKNOWN_FIELD", JsonSerializer.SerializeToUtf8Bytes(setup));

        var ended = Envelope("session.notification");
        ended["event"] = "session.started";
        AssertError("INVALID_FIELD", JsonSerializer.SerializeToUtf8Bytes(ended));
    }

    [TestMethod]
    public void AcceptsExactPayloadLimitAndRejectsOneByteOver()
    {
        var json = JsonSerializer.Serialize(Envelope("cat.handshake"));
        var exact = Encoding.UTF8.GetBytes(json.PadRight(ComConCommandProtocol.MaximumPayloadBytes));
        Assert.AreEqual("cat.handshake",
            ComConCommandProtocol.ParseEnvelope(exact).GetProperty("messageType").GetString());
        AssertError("FRAME_TOO_LARGE", Encoding.UTF8.GetBytes(
            json.PadRight(ComConCommandProtocol.MaximumPayloadBytes + 1)));
    }

    [TestMethod]
    public void LimitsUtf8BytesRatherThanCharacters()
    {
        var envelope = Envelope("cat.rejected");
        var json = JsonSerializer.Serialize(envelope);
        var emptyReasonBytes = Encoding.UTF8.GetByteCount(json.Replace("Diagnostic explanation.", ""));
        var reasonBytes = ComConCommandProtocol.MaximumPayloadBytes - emptyReasonBytes;
        var reason = new string('\u00E9', reasonBytes / 2) + new string('x', reasonBytes % 2);
        var exact = Encoding.UTF8.GetBytes(json.Replace("Diagnostic explanation.", reason));
        Assert.HasCount(ComConCommandProtocol.MaximumPayloadBytes, exact);
        Assert.AreEqual(reason, ComConCommandProtocol.ParseEnvelope(exact).GetProperty("reason").GetString());
        AssertError("FRAME_TOO_LARGE", [.. exact, (byte)' ']);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4096)]
    public async Task ReaderHandlesFragmentedAndCoalescedFramesWithoutConsumingCatTail(int chunkSize)
    {
        var first = Frame(JsonSerializer.SerializeToUtf8Bytes(Envelope("cat.handshake")));
        var second = Frame(JsonSerializer.SerializeToUtf8Bytes(Envelope("control.bind")));
        byte[] catTail = [0xFE, 0xFE, 0x94, 0xE1, 0xFD];
        using var stream = new FragmentedStream([.. first, .. second, .. catTail], chunkSize);
        Assert.AreEqual("cat.handshake", (await ComConCommandProtocol.ReadEnvelopeAsync(stream))
            .GetProperty("messageType").GetString());
        Assert.AreEqual("control.bind", (await ComConCommandProtocol.ReadEnvelopeAsync(stream))
            .GetProperty("messageType").GetString());
        var remaining = new byte[catTail.Length];
        await stream.ReadExactlyAsync(remaining);
        CollectionAssert.AreEqual(catTail, remaining);
    }

    [TestMethod]
    public async Task ReaderPropagatesCancellationWithoutConsumingBytes()
    {
        using var stream = new MemoryStream(Frame(
            JsonSerializer.SerializeToUtf8Bytes(Envelope("cat.handshake"))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ComConCommandProtocol.ReadEnvelopeAsync(stream, cancellation.Token).AsTask());
        Assert.AreEqual(0L, stream.Position);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task ReaderRejectsTruncatedFrames(int count)
    {
        using var stream = new MemoryStream(Frame(
            JsonSerializer.SerializeToUtf8Bytes(Envelope("cat.handshake")))[..count]);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() =>
            ComConCommandProtocol.ReadEnvelopeAsync(stream).AsTask());
    }

    [TestMethod]
    [DataRow(0u, "INVALID_JSON")]
    [DataRow(16385u, "FRAME_TOO_LARGE")]
    [DataRow(uint.MaxValue, "FRAME_TOO_LARGE")]
    public async Task ReaderRejectsInvalidLengthBeforeReadingPayload(uint length, string code)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, length);
        using var stream = new MemoryStream(bytes);
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            ComConCommandProtocol.ReadEnvelopeAsync(stream).AsTask());
        Assert.AreEqual(code, exception.ErrorCode);
        Assert.AreEqual(4L, stream.Position);
    }

    [TestMethod]
    public async Task WriterPreservesPayloadAddsOnlyBigEndianPrefixAndHonorsLimit()
    {
        var json = JsonSerializer.Serialize(Envelope("cat.handshake"));
        var payload = Encoding.UTF8.GetBytes(json.PadRight(ComConCommandProtocol.MaximumPayloadBytes));
        using var stream = new MemoryStream();
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(stream, payload, writer);
        var written = stream.ToArray();
        Assert.HasCount(payload.Length + 4, written);
        Assert.AreEqual((uint)payload.Length, BinaryPrimitives.ReadUInt32BigEndian(written));
        CollectionAssert.AreEqual(payload, written[4..]);
        stream.Position = 0;
        Assert.AreEqual("cat.handshake",
            (await ComConCommandProtocol.ReadEnvelopeAsync(stream)).GetProperty("messageType").GetString());
        await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            ComConCommandProtocol.WriteEnvelopeAsync(
                stream, new byte[ComConCommandProtocol.MaximumPayloadBytes + 1], writer).AsTask());
        await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            ComConCommandProtocol.WriteEnvelopeAsync(stream, "{}"u8.ToArray(), writer).AsTask());
        Assert.AreEqual((long)written.Length, stream.Length);
        Assert.AreEqual(1, writer.CurrentCount);
    }

    [TestMethod]
    public async Task ConcurrentWritersSerializeCompleteFrames()
    {
        using var stream = new YieldingWriteStream();
        using var writer = new SemaphoreSlim(1, 1);
        var handshake = JsonSerializer.SerializeToUtf8Bytes(Envelope("cat.handshake"));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            ComConCommandProtocol.WriteEnvelopeAsync(stream, handshake, writer).AsTask()));
        stream.Position = 0;
        for (var index = 0; index < 16; index++)
            Assert.AreEqual("cat.handshake",
                (await ComConCommandProtocol.ReadEnvelopeAsync(stream)).GetProperty("messageType").GetString());
        Assert.AreEqual(stream.Length, stream.Position);
    }

    [TestMethod]
    public async Task WriterSnapshotsCallerMemoryBeforeWaitingForGate()
    {
        using var stream = new MemoryStream();
        using var writer = new SemaphoreSlim(0, 1);
        var payload = JsonSerializer.SerializeToUtf8Bytes(Envelope("cat.handshake"));
        var original = payload.ToArray();
        var pending = ComConCommandProtocol.WriteEnvelopeAsync(stream, payload, writer).AsTask();
        Array.Fill(payload, (byte)'x');
        writer.Release();
        await pending;
        CollectionAssert.AreEqual(original, stream.ToArray()[4..]);
    }

    [TestMethod]
    public async Task WriterPropagatesPartialIoFailureWithoutRetryAndReleasesGate()
    {
        using var stream = new FailingPayloadStream();
        using var writer = new SemaphoreSlim(1, 1);
        var payload = JsonSerializer.SerializeToUtf8Bytes(Envelope("cat.handshake"));
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            ComConCommandProtocol.WriteEnvelopeAsync(stream, payload, writer).AsTask());
        Assert.AreEqual(4L, stream.Length);
        Assert.AreEqual(2, stream.WriteAttempts);
        Assert.AreEqual(1, writer.CurrentCount);
    }

    [TestMethod]
    public async Task CancellationLeavesUnstartedWriterUntouchedAndReleasesGateAfterIoFailure()
    {
        using var writer = new SemaphoreSlim(1, 1);
        using var stream = new MemoryStream();
        var payload = JsonSerializer.SerializeToUtf8Bytes(Envelope("cat.handshake"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ComConCommandProtocol.WriteEnvelopeAsync(stream, payload, writer, cancellation.Token).AsTask());
        Assert.AreEqual(0L, stream.Length);
        Assert.AreEqual(1, writer.CurrentCount);
        using var readOnly = new MemoryStream([], writable: false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            ComConCommandProtocol.WriteEnvelopeAsync(readOnly, payload, writer).AsTask());
        Assert.AreEqual(1, writer.CurrentCount);
    }

    private static void AssertError(string code, byte[] payload)
    {
        var exception = Assert.ThrowsExactly<ComConCommandProtocolException>(() =>
            ComConCommandProtocol.ParseEnvelope(payload));
        Assert.AreEqual(code, exception.ErrorCode);
    }

    private static byte[] Frame(byte[] payload)
    {
        var result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private static Dictionary<string, object?> Envelope(string type)
    {
        var result = new Dictionary<string, object?>
        {
            ["messageType"] = type,
            ["contractVersion"] = ComConCommandProtocol.ContractVersion
        };
        if (type == "cat.handshake")
            result["rigProfileId"] = RigId;
        else if (type is not ("cat.rejected" or "control.rejected"))
        {
            result["profileId"] = 1;
            result["sessionId"] = SessionId;
        }
        if (type is "record.rejected" or "record.outcome" or "framing.notification" or
            "session.notification" or "notification.ack")
            result["notificationSequence"] = "1";
        if (type is "record.rejected" or "record.outcome")
        {
            result["recordSequence"] = "1";
            result["deliveryStatus"] = type == "record.outcome" ? "WRITE_SUCCEEDED" : "NOT_SENT";
        }
        if (type is "cat.rejected" or "control.rejected")
            result["errorCode"] = "PROFILE_BUSY";
        if (type == "framing.notification")
            result["errorCode"] = "FRAMING_ANOMALY";
        if (type == "session.notification")
            result["event"] = "session.ended";
        if (type is "cat.rejected" or "control.rejected" or "record.rejected" or
            "framing.notification" or "session.notification")
            result["reason"] = "Diagnostic explanation.";
        return result;
    }

    private sealed class FragmentedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunkSize, buffer.Length)], cancellationToken);
    }

    private sealed class YieldingWriteStream : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingPayloadStream : MemoryStream
    {
        public int WriteAttempts { get; private set; }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++WriteAttempts == 2)
                throw new IOException("Simulated payload write failure.");
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
