using System.Buffers;
using System.Buffers.Binary;

namespace HomeYarp.Application.Tls;

internal static class TlsClientHelloParser
{
    public const byte HandshakeContentType = 0x16;
    public const byte ClientHelloHandshakeType = 0x01;
    public const ushort ServerNameExtensionType = 0x0000;

    public static bool TryParseSni(ReadOnlySequence<byte> buffer, out string? sni, out int recordLength, out bool needMore)
    {
        sni = null;
        recordLength = 0;
        needMore = false;

        if (buffer.Length < 5)
        {
            needMore = true;
            return false;
        }

        Span<byte> header = stackalloc byte[5];
        buffer.Slice(0, 5).CopyTo(header);

        if (header[0] != HandshakeContentType)
        {
            return false;
        }
        if (header[1] != 0x03)
        {
            return false;
        }

        var fragmentLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(3, 2));
        recordLength = 5 + fragmentLength;

        if (buffer.Length < recordLength)
        {
            needMore = true;
            return false;
        }

        var record = buffer.Slice(5, fragmentLength);
        return TryParseHandshake(record, out sni);
    }

    private static bool TryParseHandshake(ReadOnlySequence<byte> handshake, out string? sni)
    {
        sni = null;
        var bytes = handshake.IsSingleSegment ? handshake.FirstSpan : handshake.ToArray();

        if (bytes.Length < 4)
        {
            return false;
        }

        if (bytes[0] != ClientHelloHandshakeType)
        {
            return false;
        }

        var bodyLength = (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        if (bytes.Length < 4 + bodyLength)
        {
            return false;
        }

        var body = bytes.Slice(4, bodyLength);

        if (body.Length < 2 + 32)
        {
            return false;
        }

        var pos = 2 + 32;

        if (pos + 1 > body.Length) return false;
        var sessionIdLength = body[pos];
        pos += 1 + sessionIdLength;

        if (pos + 2 > body.Length) return false;
        var cipherSuitesLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(pos, 2));
        pos += 2 + cipherSuitesLength;

        if (pos + 1 > body.Length) return false;
        var compressionMethodsLength = body[pos];
        pos += 1 + compressionMethodsLength;

        if (pos + 2 > body.Length) return false;
        var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(pos, 2));
        pos += 2;

        if (pos + extensionsLength > body.Length) return false;
        var extensions = body.Slice(pos, extensionsLength);

        var ext = 0;
        while (ext + 4 <= extensions.Length)
        {
            var extType = BinaryPrimitives.ReadUInt16BigEndian(extensions.Slice(ext, 2));
            var extLength = BinaryPrimitives.ReadUInt16BigEndian(extensions.Slice(ext + 2, 2));
            if (ext + 4 + extLength > extensions.Length) return false;

            if (extType == ServerNameExtensionType)
            {
                var data = extensions.Slice(ext + 4, extLength);
                if (data.Length < 2) return false;
                var listLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0, 2));
                if (data.Length < 2 + listLength) return false;
                var list = data.Slice(2, listLength);

                var entry = 0;
                while (entry + 3 <= list.Length)
                {
                    var nameType = list[entry];
                    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(list.Slice(entry + 1, 2));
                    if (entry + 3 + nameLength > list.Length) return false;
                    if (nameType == 0)
                    {
                        sni = System.Text.Encoding.ASCII.GetString(list.Slice(entry + 3, nameLength));
                        return true;
                    }
                    entry += 3 + nameLength;
                }
                return false;
            }

            ext += 4 + extLength;
        }

        return false;
    }
}
