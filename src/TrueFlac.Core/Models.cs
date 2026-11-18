namespace TrueFlac.Core;

public enum AuditStatus
{
    VerifiedTrueFlac,
    SuspectTranscode,
    Rejected
}

public enum RejectionReason
{
    None,
    UnreadableFile,
    NotFlacExtension,
    InvalidMagicBytes,
    LowSampleRate,
    LowBitDepth,
    CorruptedStream,
    Md5Mismatch,
    SuspectedLossyTranscode,
    RemoteNotFlac
}

public sealed record QualityPolicy
{
    public int MinimumSampleRate { get; init; } = 44100;
    public int MinimumBitsPerSample { get; init; } = 16;
    public bool RequireValidMd5 { get; init; } = true;
    public bool RejectSuspectedTranscodes { get; init; } = true;

    public static QualityPolicy CdLosslessStrict => new()
    {
        MinimumSampleRate = 44100,
        MinimumBitsPerSample = 16,
        RequireValidMd5 = true,
        RejectSuspectedTranscodes = true
    };

    public static QualityPolicy HiResStrict => new()
    {
        MinimumSampleRate = 48000,
        MinimumBitsPerSample = 24,
        RequireValidMd5 = true,
        RejectSuspectedTranscodes = true
    };
}

public sealed record FlacStreamInfo
{
    public int MinBlockSize { get; init; }
    public int MaxBlockSize { get; init; }
    public int MinFrameSize { get; init; }
    public int MaxFrameSize { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public long TotalSamples { get; init; }
    public string Md5Hex { get; init; } = string.Empty;
    public bool HasNonZeroMd5 { get; init; }
    public TimeSpan Duration { get; init; }
    public double AverageBitrateKbps { get; init; }
    public double CompressionRatioPercent { get; init; }

    public string FormatBadge => $"{BitsPerSample}-bit / {SampleRate / 1000.0:0.#} kHz";
}

public sealed record SpectralAnalysisResult
{
    public double EstimatedCutoffKhz { get; init; }
    public double MidBandDb { get; init; }
    public double HighBandDb { get; init; }
    public double UltraHighBandDb { get; init; }
    public double MaxDropSlopeDbPerKhz { get; init; }
    public double[] BandSpectrumDb { get; init; } = Array.Empty<double>();
    public double[] BandFrequenciesKhz { get; init; } = Array.Empty<double>();
    public bool IsLikelyTranscode { get; init; }
    public int ActiveWindowsSampled { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class TrackAuditResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public uint TrackNumber { get; set; }
    public uint Year { get; set; }
    public byte[]? CoverArtBytes { get; set; }
    public AuditStatus Status { get; set; }
    public RejectionReason Reason { get; set; }
    public string VerdictHeadline { get; set; } = string.Empty;
    public string TechnicalDetail { get; set; } = string.Empty;
    public long DataOffset { get; set; }
    public FlacStreamInfo? StreamInfo { get; set; }
    public SpectralAnalysisResult? Spectral { get; set; }
    public bool? Md5Verified { get; set; }
    public DateTime AuditedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsPlayable => Status is AuditStatus.VerifiedTrueFlac or AuditStatus.SuspectTranscode;

    public string StatusLabel => Status switch
    {
        AuditStatus.VerifiedTrueFlac => "TRUE FLAC",
        AuditStatus.SuspectTranscode => "SOSPECHOSO",
        _ => "RECHAZADO"
    };

    public string DurationFormatted =>
        StreamInfo is { Duration.TotalSeconds: > 0 }
            ? (StreamInfo.Duration.TotalHours >= 1
                ? StreamInfo.Duration.ToString(@"h\:mm\:ss")
                : StreamInfo.Duration.ToString(@"m\:ss"))
            : "--:--";

    public string QualityDisplay =>
        StreamInfo is not null
            ? $"{StreamInfo.FormatBadge} · {StreamInfo.AverageBitrateKbps:0} kbps"
            : "No FLAC";
}
