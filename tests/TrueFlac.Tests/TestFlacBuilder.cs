using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TrueFlac.Tests;

internal static class TestFlacBuilder
{
    public static void WriteFlac16BitMono(string path, int sampleRate, short[] samples, bool corruptMd5 = false, byte[]? prependId3Tag = null)
    {
        var wide = new int[samples.Length];
        for (int i = 0; i < samples.Length; i++) wide[i] = samples[i];
        WriteFlacInternal(path, sampleRate, channels: 1, bitsPerSample: 16, interleavedSamples: wide, corruptMd5, prependId3Tag);
    }

    public static void WriteFlac24BitStereo(string path, int sampleRate, int[] interleavedSamples24, bool corruptMd5 = false, byte[]? prependId3Tag = null)
    {
        WriteFlacInternal(path, sampleRate, channels: 2, bitsPerSample: 24, interleavedSamples: interleavedSamples24, corruptMd5, prependId3Tag);
    }

    public static byte[] CreateId3v2PaddingHeader(int payloadBytes = 128)
    {
        var tag = new byte[10 + payloadBytes];
        tag[0] = (byte)'I';
        tag[1] = (byte)'D';
        tag[2] = (byte)'3';
        tag[3] = 0x03;
        tag[6] = (byte)((payloadBytes >> 21) & 0x7F);
        tag[7] = (byte)((payloadBytes >> 14) & 0x7F);
        tag[8] = (byte)((payloadBytes >> 7) & 0x7F);
        tag[9] = (byte)(payloadBytes & 0x7F);
        return tag;
    }

    private static void WriteFlacInternal(
        string path,
        int sampleRate,
        int channels,
        int bitsPerSample,
        int[] interleavedSamples,
        bool corruptMd5,
        byte[]? prependId3Tag)
    {
        const int blockSize = 4096;
        int bytesPerSample = bitsPerSample / 8;
        int totalFrames = interleavedSamples.Length / channels;
        int paddedFrames = ((totalFrames + blockSize - 1) / blockSize) * blockSize;
        var padded = new int[paddedFrames * channels];
        Array.Copy(interleavedSamples, padded, Math.Min(interleavedSamples.Length, padded.Length));

        byte[] rawPcm = new byte[padded.Length * bytesPerSample];
        for (int i = 0; i < padded.Length; i++)
        {
            int s = padded[i];
            int p = i * bytesPerSample;
            rawPcm[p] = (byte)(s & 0xFF);
            rawPcm[p + 1] = (byte)((s >> 8) & 0xFF);
            if (bytesPerSample == 3)
                rawPcm[p + 2] = (byte)((s >> 16) & 0xFF);
        }

        byte[] md5 = MD5.HashData(rawPcm);
        if (corruptMd5) md5[0] ^= 0xFF;

        using var ms = new MemoryStream();
        if (prependId3Tag is { Length: > 0 })
            ms.Write(prependId3Tag);

        ms.Write(Encoding.ASCII.GetBytes("fLaC"));

        // Metadata block 0: STREAMINFO (last=1, type=0, len=34)
        ms.WriteByte(0x80);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte(34);

        Span<byte> si = stackalloc byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(si[..2], blockSize);
        BinaryPrimitives.WriteUInt16BigEndian(si[2..4], blockSize);
        ulong packed = ((ulong)(uint)sampleRate << 44)
                     | ((ulong)(uint)(channels - 1) << 41)
                     | ((ulong)(uint)(bitsPerSample - 1) << 36)
                     | ((ulong)paddedFrames & 0xFFFFFFFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(si[10..18], packed);
        md5.CopyTo(si[18..34]);
        ms.Write(si);

        int numFrames = paddedFrames / blockSize;
        byte chByte = (byte)(((channels - 1) & 0x0F) << 4);
        for (int frameIdx = 0; frameIdx < numFrames; frameIdx++)
        {
            using var frameBuf = new MemoryStream();
            frameBuf.WriteByte(0xFF);
            frameBuf.WriteByte(0xF8);
            frameBuf.WriteByte(0xC0); // blockSize=4096, sampleRate from STREAMINFO
            frameBuf.WriteByte(chByte); // channels, sampleSize from STREAMINFO
            WriteUtf8Number(frameBuf, (ulong)frameIdx);

            byte crc8 = ComputeCrc8(frameBuf.GetBuffer(), (int)frameBuf.Length);
            frameBuf.WriteByte(crc8);

            int frameBase = frameIdx * blockSize * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                frameBuf.WriteByte(0x02); // VERBATIM subframe
                for (int i = 0; i < blockSize; i++)
                {
                    int s = padded[frameBase + i * channels + ch];
                    if (bytesPerSample == 3)
                    {
                        frameBuf.WriteByte((byte)((s >> 16) & 0xFF));
                        frameBuf.WriteByte((byte)((s >> 8) & 0xFF));
                        frameBuf.WriteByte((byte)(s & 0xFF));
                    }
                    else
                    {
                        frameBuf.WriteByte((byte)((s >> 8) & 0xFF));
                        frameBuf.WriteByte((byte)(s & 0xFF));
                    }
                }
            }

            ushort crc16 = ComputeCrc16(frameBuf.GetBuffer(), (int)frameBuf.Length);
            frameBuf.WriteByte((byte)(crc16 >> 8));
            frameBuf.WriteByte((byte)(crc16 & 0xFF));

            ms.Write(frameBuf.ToArray());
        }

        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteUtf8Number(Stream s, ulong v)
    {
        if (v < 0x80) { s.WriteByte((byte)v); return; }
        if (v < 0x800) { s.WriteByte((byte)(0xC0 | (v >> 6))); s.WriteByte((byte)(0x80 | (v & 0x3F))); return; }
        s.WriteByte((byte)(0xE0 | (v >> 12)));
        s.WriteByte((byte)(0x80 | ((v >> 6) & 0x3F)));
        s.WriteByte((byte)(0x80 | (v & 0x3F)));
    }

    private static byte ComputeCrc8(byte[] data, int len)
    {
        byte crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= data[i];
            for (int b = 0; b < 8; b++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return crc;
    }

    private static ushort ComputeCrc16(byte[] data, int len)
    {
        ushort crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int b = 0; b < 8; b++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x8005) : (ushort)(crc << 1);
        }
        return crc;
    }
}
