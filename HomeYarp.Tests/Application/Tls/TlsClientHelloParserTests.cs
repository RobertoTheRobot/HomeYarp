using System.Buffers;
using HomeYarp.Application.Tls;
using HomeYarp.Tests.TestHelpers;

namespace HomeYarp.Tests.Application.Tls;

public class TlsClientHelloParserTests
{
    [Fact]
    public void TryParseSni_WithValidClientHello_ReturnsSniAndRecordLength()
    {
        var bytes = TlsClientHelloFixture.BuildClientHello("ha.home.lan");
        var buffer = new ReadOnlySequence<byte>(bytes);

        var ok = TlsClientHelloParser.TryParseSni(buffer, out var sni, out var recordLength, out var needMore);

        ok.ShouldBeTrue();
        sni.ShouldBe("ha.home.lan");
        recordLength.ShouldBe(bytes.Length);
        needMore.ShouldBeFalse();
    }

    [Fact]
    public void TryParseSni_WithMissingSniExtension_ReturnsFalseAndNoNeedMore()
    {
        var bytes = TlsClientHelloFixture.BuildClientHello(sni: null);
        var buffer = new ReadOnlySequence<byte>(bytes);

        var ok = TlsClientHelloParser.TryParseSni(buffer, out var sni, out _, out var needMore);

        ok.ShouldBeFalse();
        sni.ShouldBeNull();
        needMore.ShouldBeFalse();
    }

    [Fact]
    public void TryParseSni_WithBufferShorterThanFiveBytes_RequestsMoreBytes()
    {
        var buffer = new ReadOnlySequence<byte>(new byte[] { 0x16, 0x03, 0x01 });

        var ok = TlsClientHelloParser.TryParseSni(buffer, out var sni, out _, out var needMore);

        ok.ShouldBeFalse();
        sni.ShouldBeNull();
        needMore.ShouldBeTrue();
    }

    [Fact]
    public void TryParseSni_WithIncompleteRecord_RequestsMoreBytes()
    {
        var bytes = TlsClientHelloFixture.BuildClientHello("partial.example");
        // Trim mid-record to force needMore.
        var truncated = bytes.AsSpan(0, bytes.Length / 2).ToArray();
        var buffer = new ReadOnlySequence<byte>(truncated);

        var ok = TlsClientHelloParser.TryParseSni(buffer, out _, out _, out var needMore);

        ok.ShouldBeFalse();
        needMore.ShouldBeTrue();
    }

    [Fact]
    public void TryParseSni_WithNonHandshakeContentType_ReturnsFalseAndNoNeedMore()
    {
        var bytes = new byte[] { 0x17 /* application data */, 0x03, 0x03, 0x00, 0x10 }
            .Concat(new byte[16]).ToArray();
        var buffer = new ReadOnlySequence<byte>(bytes);

        var ok = TlsClientHelloParser.TryParseSni(buffer, out _, out _, out var needMore);

        ok.ShouldBeFalse();
        needMore.ShouldBeFalse();
    }

    [Fact]
    public void TryParseSni_WithUnknownTlsMajorVersion_ReturnsFalseAndNoNeedMore()
    {
        var bytes = new byte[] { 0x16, 0x02 /* not TLS */, 0x03, 0x00, 0x10 }
            .Concat(new byte[16]).ToArray();
        var buffer = new ReadOnlySequence<byte>(bytes);

        var ok = TlsClientHelloParser.TryParseSni(buffer, out _, out _, out var needMore);

        ok.ShouldBeFalse();
        needMore.ShouldBeFalse();
    }
}
