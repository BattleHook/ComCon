using System.Text;
using ComCon.Core.Protocol;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class Ts590SgReadPolicyTests
{
    private static readonly Ts590SgModelIdentity Identity =
        Ts590SgModelIdentity.ValidateResponse(Ascii("ID023;"));

    [TestMethod]
    public void OnlyExactReadOnlyManualRequestsAreAccepted()
    {
        Assert.AreEqual(Ts590SgReadKind.IfStatus, Request("IF;").Kind);
        Assert.AreEqual(Ts590SgReadKind.VfoAFrequency, Request("FA;").Kind);
        Assert.AreEqual(Ts590SgReadKind.VfoBFrequency, Request("FB;").Kind);
        Assert.AreEqual(Ts590SgReadKind.Mode, Request("MD;").Kind);
        foreach (var invalid in new[] { "FA00007000000;", "MD2;", "TX1;", "FA;\r\n", "ID;" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => Request(invalid), invalid);
        }
    }

    [TestMethod]
    public void OnlyVerifiedTs590SgModelIdentityCanCreateReadContracts()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Ts590SgModelIdentity.ValidateResponse(Ascii("ID021;")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Ts590SgModelIdentity.ValidateResponse(Ascii("ID23;")));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            Ts590SgReadContract.ValidateRequest(Ascii("IF;"), null!));
        Assert.AreEqual(Ts590SgReadKind.IfStatus,
            Ts590SgReadContract.ValidateRequest(Ascii("IF;"), Identity).Kind);
    }

    [TestMethod]
    [DataRow("FA;", Ts590SgReadKind.VfoAFrequency)]
    [DataRow("FB;", Ts590SgReadKind.VfoBFrequency)]
    public void FrequencyAnswerRequiresExactlyElevenDigits(string command, Ts590SgReadKind kind)
    {
        var contract = Request(command);
        Assert.AreEqual(kind, contract.Kind);
        var valid = contract.Classify(Ascii(command[..2] + "00007000000;"));
        Assert.AreEqual(Ts590SgReadResultKind.Value, valid.Kind);
        Assert.AreEqual("7000000", valid.Value);
        foreach (var invalid in new[]
        {
            command[..2] + "7000000;",
            command[..2] + "0000700000X;",
            command[..2] + "000070000000;"
        })
        {
            Assert.AreEqual(Ts590SgReadResultKind.Anomaly, contract.Classify(Ascii(invalid)).Kind);
        }
        Assert.AreEqual(Ts590SgReadResultKind.Unrelated, contract.Classify(Ascii("MD2;")).Kind);
    }

    [TestMethod]
    [DataRow("MD1;")]
    [DataRow("MD2;")]
    [DataRow("MD3;")]
    [DataRow("MD4;")]
    [DataRow("MD5;")]
    [DataRow("MD6;")]
    [DataRow("MD7;")]
    [DataRow("MD9;")]
    public void ModeAnswerAcceptsDocumentedModeDigits(string answer)
    {
        var result = Request("MD;").Classify(Ascii(answer));
        Assert.AreEqual(Ts590SgReadResultKind.Value, result.Kind);
        Assert.AreEqual(answer[2].ToString(), result.Value);
    }

    [TestMethod]
    [DataRow("MD0;")]
    [DataRow("MD8;")]
    [DataRow("MD10;")]
    [DataRow("MD2")]
    public void ModeAnswerRejectsUnlistedOrMalformedValues(string answer) =>
        Assert.AreEqual(Ts590SgReadResultKind.Anomaly,
            Request("MD;").Classify(Ascii(answer)).Kind);

    [TestMethod]
    public void IfStatusValidatesDocumentedFixedFieldsAndExposesRxTx()
    {
        var answer = IfAnswer();
        Assert.HasCount(38, answer);
        var result = Request("IF;").Classify(answer);
        Assert.AreEqual(Ts590SgReadResultKind.Value, result.Kind);
        Assert.AreEqual(IfAnswerText(), result.Value);
        Assert.AreEqual(Ts590SgReadResultKind.Unrelated, Request("FA;").Classify(answer).Kind);
    }

    [TestMethod]
    [DataRow(13, (byte)'X')]
    [DataRow(18, (byte)'X')]
    [DataRow(23, (byte)'2')]
    [DataRow(27, (byte)'2')]
    [DataRow(28, (byte)'8')]
    [DataRow(31, (byte)'2')]
    [DataRow(32, (byte)'2')]
    [DataRow(33, (byte)'4')]
    [DataRow(34, (byte)'5')]
    [DataRow(36, (byte)'1')]
    public void IfStatusRejectsInvalidDocumentedField(int index, byte value)
    {
        var answer = IfAnswer();
        answer[index] = value;
        Assert.AreEqual(Ts590SgReadResultKind.Anomaly,
            Request("IF;").Classify(answer).Kind);
    }

    [TestMethod]
    public void IfStatusRequiresExactLengthAndValidOffsetAndToneRange()
    {
        var answer = IfAnswer();
        Assert.AreEqual(Ts590SgReadResultKind.Anomaly,
            Request("IF;").Classify(answer.AsSpan(0, answer.Length - 1)).Kind);
        answer = IfAnswer();
        Encoding.ASCII.GetBytes("9991").CopyTo(answer, 19);
        Assert.AreEqual(Ts590SgReadResultKind.Anomaly, Request("IF;").Classify(answer).Kind);
        answer = IfAnswer();
        Encoding.ASCII.GetBytes("43").CopyTo(answer, 34);
        Assert.AreEqual(Ts590SgReadResultKind.Anomaly, Request("IF;").Classify(answer).Kind);
    }

    [TestMethod]
    [DataRow("?;")]
    [DataRow("E;")]
    [DataRow("O;")]
    public void DocumentedRadioErrorsAreNotReadValues(string answer)
    {
        foreach (var command in new[] { "IF;", "FA;", "FB;", "MD;" })
        {
            Assert.AreEqual(Ts590SgReadResultKind.RadioError,
                Request(command).Classify(Ascii(answer)).Kind);
        }
    }

    private static Ts590SgReadContract Request(string command) =>
        Ts590SgReadContract.ValidateRequest(Ascii(command), Identity);
    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
    private static string IfAnswerText() =>
        string.Concat("IF00007000000     +0000", "00000200000000", ";");
    private static byte[] IfAnswer() => Ascii(IfAnswerText());
}
