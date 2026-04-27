namespace HomeYarp.Tests.TestHelpers;

/// <summary>
/// Builds minimal TLS 1.2 ClientHello byte buffers (with optional SNI) for parser tests.
/// Layout follows RFC 5246 § 7.4.1.2 and RFC 6066 § 3.
/// </summary>
public static class TlsClientHelloFixture
{
    public static byte[] BuildClientHello(string? sni)
    {
        var ms = new MemoryStream();

        // ClientHello body
        var bodyMs = new MemoryStream();
        bodyMs.Write(new byte[] { 0x03, 0x03 }); // legacy_version TLS 1.2
        bodyMs.Write(new byte[32]); // random (32 bytes)
        bodyMs.WriteByte(0); // session_id length
        bodyMs.Write(new byte[] { 0x00, 0x02, 0x00, 0x35 }); // cipher_suites_length=2 + TLS_RSA_WITH_AES_256_CBC_SHA
        bodyMs.WriteByte(1); // compression_methods length
        bodyMs.WriteByte(0); // null compression

        var extensionsMs = new MemoryStream();
        if (sni is not null)
        {
            // server_name extension (type=0x0000)
            var sniBytes = System.Text.Encoding.ASCII.GetBytes(sni);
            var serverNameListMs = new MemoryStream();
            serverNameListMs.WriteByte(0); // name_type = host_name
            WriteUInt16BE(serverNameListMs, (ushort)sniBytes.Length);
            serverNameListMs.Write(sniBytes);

            var serverNameList = serverNameListMs.ToArray();
            var extensionDataMs = new MemoryStream();
            WriteUInt16BE(extensionDataMs, (ushort)serverNameList.Length); // ServerNameList length
            extensionDataMs.Write(serverNameList);
            var extensionData = extensionDataMs.ToArray();

            WriteUInt16BE(extensionsMs, 0x0000); // extension type
            WriteUInt16BE(extensionsMs, (ushort)extensionData.Length);
            extensionsMs.Write(extensionData);
        }

        var extensions = extensionsMs.ToArray();
        WriteUInt16BE(bodyMs, (ushort)extensions.Length);
        bodyMs.Write(extensions);

        var body = bodyMs.ToArray();

        // Handshake header: type(1) + length(3) + body
        ms.WriteByte(0x01); // ClientHello
        WriteUInt24BE(ms, (uint)body.Length);
        ms.Write(body);

        var handshake = ms.ToArray();

        // TLS record header: content_type(1)=0x16 + version(2)=0x0301 + length(2)
        var record = new MemoryStream();
        record.WriteByte(0x16); // handshake
        record.Write(new byte[] { 0x03, 0x01 }); // record version (often TLS 1.0 even for 1.2 hellos)
        WriteUInt16BE(record, (ushort)handshake.Length);
        record.Write(handshake);

        return record.ToArray();
    }

    private static void WriteUInt16BE(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteUInt24BE(Stream s, uint value)
    {
        s.WriteByte((byte)((value >> 16) & 0xFF));
        s.WriteByte((byte)((value >> 8) & 0xFF));
        s.WriteByte((byte)(value & 0xFF));
    }
}
