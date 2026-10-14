using System.Text;
using System.Text.Json;
using ComCon.Core.Ipc;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ComConIpcProtocolTests
{
    [TestMethod]
    public void ParseRequestAcceptsCanonicalRequestAndClonesArguments()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"v":1,"kind":"request","id":"8","op":"GET_STATUS","args":{}}""");

        var request = ComConIpcProtocol.ParseRequest(bytes);

        Assert.AreEqual(8L, request.Id);
        Assert.AreEqual("GET_STATUS", request.Operation);
        Assert.IsNull(request.SessionId);
        Assert.AreEqual(JsonValueKind.Object, request.Arguments.ValueKind);
    }

    [TestMethod]
    public void ParseRequestAcceptsCrLfAndOptionalSessionId()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"v":1,"kind":"request","id":"1","op":"GET_STATUS","sessionId":"s","args":{}}""" + "\r");

        var request = ComConIpcProtocol.ParseRequest(bytes);

        Assert.AreEqual("s", request.SessionId);
    }

    [TestMethod]
    [DataRow("""{"v":1,"kind":"request","id":"1","op":"GET_STATUS","args":{},"args":{}}""")]
    [DataRow("""{"v":1,"kind":"request","id":"1","op":"GET_STATUS","args":{},"extra":1}""")]
    [DataRow("""{"v":2,"kind":"request","id":"1","op":"GET_STATUS","args":{}}""")]
    [DataRow("""{"v":1,"kind":"request","id":"01","op":"GET_STATUS","args":{}}""")]
    [DataRow("""{"v":1,"kind":"request","id":"1","op":"GET_STATUS","args":[]}""")]
    public void ParseRequestRejectsInvalidEnvelopes(string json)
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComConIpcProtocol.ParseRequest(Encoding.UTF8.GetBytes(json)));
    }

    [TestMethod]
    public void ParseRequestRejectsDuplicateNestedProperties()
    {
        var json = """{"v":1,"kind":"request","id":"1","op":"CAT_REQUEST","args":{"mode":"live","mode":"cacheOnly"}}""";

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComConIpcProtocol.ParseRequest(Encoding.UTF8.GetBytes(json)));
    }

    [TestMethod]
    public void ParseRequestRejectsMalformedUtf8AndBom()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComConIpcProtocol.ParseRequest([0xFF]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComConIpcProtocol.ParseRequest([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']));
    }

    [TestMethod]
    public async Task ReadRequestRejectsUnterminatedOversizedFrame()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            new string('x', ComConIpcProtocol.MaximumFrameBytes)));
        var reader = new ComConIpcJsonLineReader(stream);

        await AssertThrowsAsync<InvalidDataException>(() => reader.ReadRequestAsync().AsTask());
    }

    [TestMethod]
    public async Task LineReaderPreservesFollowingFrames()
    {
        var line = """{"v":1,"kind":"request","id":"1","op":"GET_STATUS","args":{}}""" + "\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + line));
        var reader = new ComConIpcJsonLineReader(stream);

        var first = await reader.ReadRequestAsync();
        var second = await reader.ReadRequestAsync();

        Assert.AreEqual(1L, first.Id);
        Assert.AreEqual(1L, second.Id);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FrameLimitIncludesLfAndOptionalCr(bool crlf)
    {
        var line = """{"v":1,"kind":"request","id":"1","op":"GET_STATUS","args":{}}""";
        var terminator = crlf ? "\r\n" : "\n";
        var padding = ComConIpcProtocol.MaximumFrameBytes - Encoding.UTF8.GetByteCount(line + terminator);
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes(line + new string(' ', padding) + terminator));
        Assert.AreEqual(1L, (await new ComConIpcJsonLineReader(valid).ReadRequestAsync()).Id);
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes(line + new string(' ', padding + 1) + terminator));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new ComConIpcJsonLineReader(invalid).ReadRequestAsync().AsTask());
    }

    [TestMethod]
    public async Task WriterEnforcesExactFrameLimitIncludingTerminator()
    {
        using var output = new MemoryStream();
        using var writer = new SemaphoreSlim(1, 1);
        await ComConIpcProtocol.WriteJsonLineAsync(
            output, new string('x', ComConIpcProtocol.MaximumFrameBytes - 3), writer);
        Assert.AreEqual((long)ComConIpcProtocol.MaximumFrameBytes, output.Length);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ComConIpcProtocol.WriteJsonLineAsync(
                output, new string('x', ComConIpcProtocol.MaximumFrameBytes - 2), writer).AsTask());
        Assert.AreEqual((long)ComConIpcProtocol.MaximumFrameBytes, output.Length);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name}.");
    }
}
