using System.Buffers.Binary;
using System.Text;

namespace TrueFlac.Core;

public static class FlacBinaryInspector
{
    private static readonly byte[] FlacMagic = Encoding.ASCII.GetBytes("fLaC");

    public static (bool IsValid, string Error, FlacStreamInfo? StreamInfo, long DataOffset) Inspect(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Inspect(fs, fs.Length);
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo leer el archivo: {ex.Message}", null, 0);
        }
    }

    public static Stream OpenFlacStream(string filePath, long dataOffset = 0)
    {
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (dataOffset <= 0)
            return fs;

        try
        {
            fs.Position = dataOffset;
            return new OffsetStream(fs, dataOffset);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static (bool IsValid, string Error, FlacStreamInfo? StreamInfo, long DataOffset) Inspect(Stream stream, long totalFileBytes)
    {
        if (stream.Length < 42)
            return (false, "Archivo demasiado pequeño para contener cabecera fLaC y STREAMINFO.", null, 0);

        Span<byte> header = stackalloc byte[10];
        if (stream.Read(header[..4]) < 4)
            return (false, "No se pudo leer la firma inicial del archivo.", null, 0);

        long flacStart = 0;

        if (header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
        {
            if (stream.Read(header[4..10]) < 6)
                return (false, "Cabecera ID3 truncada; no es un contenedor FLAC válido.", null, 0);

            int id3Size = ((header[6] & 0x7F) << 21)
                        | ((header[7] & 0x7F) << 14)
                        | ((header[8] & 0x7F) << 7)
                        | (header[9] & 0x7F);

            flacStart = 10L + id3Size;
            if (flacStart + 42 > stream.Length)
                return (false, "Archivo con etiqueta ID3 sin flujo FLAC válido después.", null, 0);

            stream.Position = flacStart;
            Span<byte> magicAfterId3 = stackalloc byte[4];
            if (stream.Read(magicAfterId3) < 4 || !magicAfterId3.SequenceEqual(FlacMagic))
                return (false, "Firma 'fLaC' ausente tras cabecera ID3 (probable MP3 o formato incompatible).", null, 0);
        }
        else if (!header[..4].SequenceEqual(FlacMagic))
        {
            string hint = DetectCommonFakeFormat(header[..4]);
            return (false, $"Firma binaria inválida (no empieza con 'fLaC'{hint}).", null, 0);
        }

        Span<byte> blockHeader = stackalloc byte[4];
        if (stream.Read(blockHeader) < 4)
            return (false, "Bloque de metadatos FLAC truncado.", null, 0);

        int blockType = blockHeader[0] & 0x7F;
        int blockLength = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
        if (blockType != 0 || blockLength != 34)
            return (false, $"Primer bloque inválido (tipo={blockType}, longitud={blockLength}; se esperaba STREAMINFO).", null, 0);

        Span<byte> infoBytes = stackalloc byte[34];
        if (stream.Read(infoBytes) < 34)
            return (false, "Bloque STREAMINFO incompleto.", null, 0);

        int minBlockSize = BinaryPrimitives.ReadUInt16BigEndian(infoBytes[..2]);
        int maxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(infoBytes[2..4]);
        int minFrameSize = (infoBytes[4] << 16) | (infoBytes[5] << 8) | infoBytes[6];
        int maxFrameSize = (infoBytes[7] << 16) | (infoBytes[8] << 8) | infoBytes[9];

        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(infoBytes[10..18]);
        int sampleRate = (int)((packed >> 44) & 0xFFFFF);
        int channels = (int)(((packed >> 41) & 0x7) + 1);
        int bitsPerSample = (int)(((packed >> 36) & 0x1F) + 1);
        long totalSamples = (long)(packed & 0xFFFFFFFFFUL);

        if (sampleRate <= 0 || channels <= 0 || bitsPerSample < 4)
            return (false, $"Parámetros STREAMINFO inválidos ({sampleRate} Hz, {channels} ch, {bitsPerSample} bit).", null, 0);

        ReadOnlySpan<byte> md5Span = infoBytes[18..34];
        bool hasNonZeroMd5 = false;
        foreach (byte b in md5Span)
        {
            if (b != 0) { hasNonZeroMd5 = true; break; }
        }

        string md5Hex = Convert.ToHexString(md5Span).ToLowerInvariant();
        TimeSpan duration = totalSamples > 0 ? TimeSpan.FromSeconds((double)totalSamples / sampleRate) : TimeSpan.Zero;
        double avgBitrateKbps = duration.TotalSeconds > 0 ? (totalFileBytes * 8.0) / duration.TotalSeconds / 1000.0 : 0;
        double rawBytes = (double)totalSamples * channels * (bitsPerSample / 8.0);
        double compressionRatio = rawBytes > 0 ? (totalFileBytes / rawBytes) * 100.0 : 0;

        var info = new FlacStreamInfo
        {
            MinBlockSize = minBlockSize,
            MaxBlockSize = maxBlockSize,
            MinFrameSize = minFrameSize,
            MaxFrameSize = maxFrameSize,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            TotalSamples = totalSamples,
            Md5Hex = md5Hex,
            HasNonZeroMd5 = hasNonZeroMd5,
            Duration = duration,
            AverageBitrateKbps = avgBitrateKbps,
            CompressionRatioPercent = compressionRatio
        };

        return (true, string.Empty, info, flacStart);
    }

    private static string DetectCommonFakeFormat(ReadOnlySpan<byte> first4)
    {
        if (first4.Length >= 2 && first4[0] == 0xFF && (first4[1] & 0xE0) == 0xE0)
            return " — parece MP3/ADTS";
        if (first4.Length >= 4 && first4[0] == (byte)'O' && first4[1] == (byte)'g' && first4[2] == (byte)'g' && first4[3] == (byte)'S')
            return " — parece Ogg/Opus/Vorbis";
        if (first4.Length >= 4 && first4[0] == (byte)'R' && first4[1] == (byte)'I' && first4[2] == (byte)'F' && first4[3] == (byte)'F')
            return " — parece WAV/RIFF";
        return string.Empty;
    }

    private sealed class OffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _startOffset;

        public OffsetStream(Stream inner, long startOffset)
        {
            _inner = inner;
            _startOffset = startOffset;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => Math.Max(0L, _inner.Length - _startOffset);

        public override long Position
        {
            get => Math.Max(0L, _inner.Position - _startOffset);
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_inner.Position < _startOffset) _inner.Position = _startOffset;
            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (_inner.Position < _startOffset) _inner.Position = _startOffset;
            return _inner.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => _startOffset + offset,
                SeekOrigin.Current => _inner.Position + offset,
                SeekOrigin.End => _inner.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < _startOffset)
                throw new IOException("No se puede posicionar antes del inicio del flujo FLAC.");
            return _inner.Seek(target, SeekOrigin.Begin) - _startOffset;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
