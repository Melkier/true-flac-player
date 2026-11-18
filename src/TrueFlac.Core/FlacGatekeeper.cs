using System.Security.Cryptography;
using NAudio.Flac;

namespace TrueFlac.Core;

public static class FlacGatekeeper
{
    public static TrackAuditResult AuditFile(string filePath, QualityPolicy? policy = null)
    {
        policy ??= QualityPolicy.CdLosslessStrict;
        var result = new TrackAuditResult
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Title = Path.GetFileNameWithoutExtension(filePath),
            Artist = "Desconocido",
            Album = "Sin álbum",
            AuditedAtUtc = DateTime.UtcNow
        };

        if (!File.Exists(filePath))
        {
            result.Status = AuditStatus.Rejected;
            result.Reason = RejectionReason.UnreadableFile;
            result.VerdictHeadline = "Archivo no encontrado";
            result.TechnicalDetail = $"No existe la ruta: {filePath}";
            return result;
        }

        var fi = new FileInfo(filePath);
        result.FileSizeBytes = fi.Length;

        string ext = fi.Extension.ToLowerInvariant();
        if (ext != ".flac")
        {
            result.Status = AuditStatus.Rejected;
            result.Reason = RejectionReason.NotFlacExtension;
            result.VerdictHeadline = $"Formato {ext.ToUpperInvariant()} rechazado";
            result.TechnicalDetail = $"Solo se admite True FLAC (.flac). Se rechazó '{ext}' por no ser audio sin pérdida FLAC.";
            return result;
        }

        var (validHeader, headerErr, info, dataOffset) = FlacBinaryInspector.Inspect(filePath);
        if (!validHeader || info is null)
        {
            result.Status = AuditStatus.Rejected;
            result.Reason = RejectionReason.InvalidMagicBytes;
            result.VerdictHeadline = "Falso FLAC / cabecera inválida";
            result.TechnicalDetail = headerErr;
            return result;
        }

        result.DataOffset = dataOffset;
        result.StreamInfo = info;
        PopulateTags(filePath, result);

        if (!TryApplyResolutionGate(result, info, policy))
            return result;

        return DecodeAndAnalyzeStream(filePath, info, policy, result);
    }

    public static TrackAuditResult ReevaluatePolicy(TrackAuditResult existing, QualityPolicy? policy = null)
    {
        policy ??= QualityPolicy.CdLosslessStrict;

        if (existing.StreamInfo is null)
        {
            return !string.IsNullOrWhiteSpace(existing.FilePath) && File.Exists(existing.FilePath)
                ? AuditFile(existing.FilePath, policy)
                : existing;
        }

        FlacStreamInfo info = existing.StreamInfo;
        if (!TryApplyResolutionGate(existing, info, policy))
        {
            existing.AuditedAtUtc = DateTime.UtcNow;
            return existing;
        }

        if (existing.Spectral is null)
        {
            return !string.IsNullOrWhiteSpace(existing.FilePath) && File.Exists(existing.FilePath)
                ? AuditFile(existing.FilePath, policy)
                : existing;
        }

        if (policy.RequireValidMd5 && existing.Md5Verified == false)
        {
            existing.Status = AuditStatus.Rejected;
            existing.Reason = RejectionReason.Md5Mismatch;
            existing.VerdictHeadline = "MD5 corrupto o flujo alterado";
            existing.TechnicalDetail = $"MD5 STREAMINFO ({info.Md5Hex[..Math.Min(8, info.Md5Hex.Length)]}...) no coincide con el PCM decodificado.";
            existing.AuditedAtUtc = DateTime.UtcNow;
            return existing;
        }

        ApplySpectralAndFinalVerdict(existing, info, existing.Spectral, policy);
        existing.AuditedAtUtc = DateTime.UtcNow;
        return existing;
    }

    private static bool TryApplyResolutionGate(TrackAuditResult result, FlacStreamInfo info, QualityPolicy policy)
    {
        if (info.SampleRate < policy.MinimumSampleRate)
        {
            result.Status = AuditStatus.Rejected;
            result.Reason = RejectionReason.LowSampleRate;
            result.VerdictHeadline = $"Frecuencia insuficiente ({info.SampleRate} Hz)";
            result.TechnicalDetail = $"Requiere al menos {policy.MinimumSampleRate} Hz; el archivo tiene {info.SampleRate} Hz.";
            return false;
        }

        if (info.BitsPerSample < policy.MinimumBitsPerSample)
        {
            result.Status = AuditStatus.Rejected;
            result.Reason = RejectionReason.LowBitDepth;
            result.VerdictHeadline = $"Profundidad insuficiente ({info.BitsPerSample}-bit)";
            result.TechnicalDetail = $"Requiere al menos {policy.MinimumBitsPerSample}-bit; el archivo tiene {info.BitsPerSample}-bit.";
            return false;
        }

        return true;
    }

    private static TrackAuditResult DecodeAndAnalyzeStream(
        string filePath,
        FlacStreamInfo info,
        QualityPolicy policy,
        TrackAuditResult result)
    {
        try
        {
            using var rawStream = FlacBinaryInspector.OpenFlacStream(filePath, result.DataOffset);
            using var reader = new FlacReader(rawStream);
            int bytesPerSample = Math.Max(1, reader.WaveFormat.BitsPerSample / 8);
            int channels = Math.Max(1, reader.WaveFormat.Channels);
            int frameStride = bytesPerSample * channels;
            long totalFrames = info.TotalSamples > 0
                ? info.TotalSamples
                : (frameStride > 0 ? reader.Length / frameStride : 0);

            var collector = new FrameWindowCollector(totalFrames, bytesPerSample, channels);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            byte[] buffer = new byte[frameStride * 8192];
            byte[] carry = new byte[frameStride];
            int carryLen = 0;
            int read;

            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                md5.AppendData(buffer, 0, read);
                int bufOffset = 0;

                if (carryLen > 0)
                {
                    int take = Math.Min(frameStride - carryLen, read);
                    Buffer.BlockCopy(buffer, 0, carry, carryLen, take);
                    carryLen += take;
                    bufOffset += take;
                    if (carryLen == frameStride)
                    {
                        collector.PushFrame(carry);
                        carryLen = 0;
                    }
                }

                while (bufOffset + frameStride <= read)
                {
                    collector.PushFrame(buffer.AsSpan(bufOffset, frameStride));
                    bufOffset += frameStride;
                }

                int rem = read - bufOffset;
                if (rem > 0)
                {
                    Buffer.BlockCopy(buffer, bufOffset, carry, 0, rem);
                    carryLen = rem;
                }
            }

            collector.FlushPartialWindow();

            return FinalizeAudit(info, reader.WaveFormat.BitsPerSample, md5.GetHashAndReset(), collector.Windows, policy, result);
        }
        catch (Exception ex)
        {
            result.Status = AuditStatus.Rejected;
            result.Reason = RejectionReason.CorruptedStream;
            result.VerdictHeadline = "Flujo FLAC corrupto";
            result.TechnicalDetail = $"Error decodificando tramas FLAC: {ex.Message}";
            return result;
        }
    }

    private static TrackAuditResult FinalizeAudit(
        FlacStreamInfo info,
        int decodedBitsPerSample,
        byte[] md5Bytes,
        List<float[]> windows,
        QualityPolicy policy,
        TrackAuditResult result)
    {
        string actualMd5 = Convert.ToHexString(md5Bytes).ToLowerInvariant();
        bool md5CanCompare = info.HasNonZeroMd5 && decodedBitsPerSample == info.BitsPerSample;
        bool md5Ok = !md5CanCompare || string.Equals(actualMd5, info.Md5Hex, StringComparison.OrdinalIgnoreCase);
        result.Md5Verified = md5CanCompare ? md5Ok : null;

        if (policy.RequireValidMd5 && md5CanCompare && !md5Ok)
        {
            result.Status = AuditStatus.Rejected;
            result.Reason = RejectionReason.Md5Mismatch;
            result.VerdictHeadline = "MD5 corrupto o flujo alterado";
            result.TechnicalDetail = $"MD5 STREAMINFO ({info.Md5Hex[..8]}...) no coincide con PCM decodificado ({actualMd5[..8]}...).";
            return result;
        }

        var spectral = SpectralAnalyzer.Analyze(windows, info.SampleRate);
        result.Spectral = spectral;

        ApplySpectralAndFinalVerdict(result, info, spectral, policy);
        return result;
    }

    private static void ApplySpectralAndFinalVerdict(
        TrackAuditResult result,
        FlacStreamInfo info,
        SpectralAnalysisResult spectral,
        QualityPolicy policy)
    {
        if (spectral.IsLikelyTranscode)
        {
            if (policy.RejectSuspectedTranscodes)
            {
                result.Status = AuditStatus.Rejected;
                result.Reason = RejectionReason.SuspectedLossyTranscode;
                result.VerdictHeadline = $"Fake FLAC rechazado (corte {spectral.EstimatedCutoffKhz:0.0} kHz)";
                result.TechnicalDetail = spectral.Summary;
                return;
            }

            result.Status = AuditStatus.SuspectTranscode;
            result.Reason = RejectionReason.SuspectedLossyTranscode;
            result.VerdictHeadline = $"Sospechoso (corte {spectral.EstimatedCutoffKhz:0.0} kHz)";
            result.TechnicalDetail = spectral.Summary;
            return;
        }

        string md5State = result.Md5Verified switch
        {
            true => "íntegro",
            false => "no coincide (ignorado por política)",
            null => "no declarado"
        };

        result.Status = AuditStatus.VerifiedTrueFlac;
        result.Reason = RejectionReason.None;
        result.VerdictHeadline = $"True FLAC verificado ({info.FormatBadge})";
        result.TechnicalDetail = $"{spectral.Summary} MD5: {md5State}.";
    }

    private sealed class FrameWindowCollector
    {
        private readonly long[] _targetFrameStarts;
        private readonly int _bytesPerSample;
        private readonly int _channels;
        private int _nextTargetIdx;
        private float[]? _activeWindow;
        private int _windowPos;
        private long _processedFrames;

        public List<float[]> Windows { get; }

        public FrameWindowCollector(long totalFrames, int bytesPerSample, int channels)
        {
            _bytesPerSample = bytesPerSample;
            _channels = channels;
            _targetFrameStarts = totalFrames > SpectralAnalyzer.FftSize * 2
                ? new[]
                {
                    (long)(totalFrames * 0.18),
                    (long)(totalFrames * 0.34),
                    (long)(totalFrames * 0.50),
                    (long)(totalFrames * 0.68),
                    (long)(totalFrames * 0.82)
                }
                : new[] { 0L };
            Windows = new List<float[]>(_targetFrameStarts.Length);
        }

        public void PushFrame(ReadOnlySpan<byte> frame)
        {
            if (_activeWindow is null && _nextTargetIdx < _targetFrameStarts.Length && _processedFrames >= _targetFrameStarts[_nextTargetIdx])
            {
                _activeWindow = new float[SpectralAnalyzer.FftSize];
                _windowPos = 0;
                _nextTargetIdx++;
            }

            if (_activeWindow is not null)
            {
                _activeWindow[_windowPos++] = ReadMonoNormalizedSample(frame, _bytesPerSample, _channels);
                if (_windowPos >= SpectralAnalyzer.FftSize)
                {
                    if (ComputeRmsDb(_activeWindow) > -45.0) Windows.Add(_activeWindow);
                    _activeWindow = null;
                }
            }

            _processedFrames++;
        }

        public void FlushPartialWindow()
        {
            if (_activeWindow is not null && _windowPos >= 1024 && ComputeRmsDb(_activeWindow) > -45.0)
                Windows.Add(_activeWindow);
            _activeWindow = null;
        }
    }

    private static float ReadMonoNormalizedSample(ReadOnlySpan<byte> frame, int bytesPerSample, int channels)
    {
        double sum = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int idx = ch * bytesPerSample;
            if (bytesPerSample == 1)
            {
                sum += ((sbyte)frame[idx]) / 128.0;
            }
            else if (bytesPerSample == 2)
            {
                short v = (short)(frame[idx] | (frame[idx + 1] << 8));
                sum += v / 32768.0;
            }
            else if (bytesPerSample == 3)
            {
                int v = frame[idx] | (frame[idx + 1] << 8) | ((sbyte)frame[idx + 2] << 16);
                sum += v / 8388608.0;
            }
            else if (bytesPerSample == 4)
            {
                int v = frame[idx] | (frame[idx + 1] << 8) | (frame[idx + 2] << 16) | ((sbyte)frame[idx + 3] << 24);
                sum += v / 2147483648.0;
            }
        }
        return (float)(sum / channels);
    }

    private static double ComputeRmsDb(float[] samples)
    {
        double sq = 0;
        for (int i = 0; i < samples.Length; i++) sq += samples[i] * samples[i];
        return 20.0 * Math.Log10(Math.Max(Math.Sqrt(sq / samples.Length), 1e-9));
    }

    private static void PopulateTags(string filePath, TrackAuditResult result)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title)) result.Title = tag.Title.Trim();
            if (tag.Performers.Length > 0 && !string.IsNullOrWhiteSpace(tag.Performers[0]))
                result.Artist = string.Join(", ", tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)));
            else if (tag.AlbumArtists.Length > 0 && !string.IsNullOrWhiteSpace(tag.AlbumArtists[0]))
                result.Artist = string.Join(", ", tag.AlbumArtists.Where(p => !string.IsNullOrWhiteSpace(p)));
            if (!string.IsNullOrWhiteSpace(tag.Album)) result.Album = tag.Album.Trim();
            result.TrackNumber = tag.Track;
            result.Year = tag.Year;
            if (tag.Pictures is { Length: > 0 })
                result.CoverArtBytes = tag.Pictures[0].Data.Data;
        }
        catch { }
    }
}
