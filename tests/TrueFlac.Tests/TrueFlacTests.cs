using TrueFlac.Core;
using Xunit;

namespace TrueFlac.Tests;

public sealed class TrueFlacTests : IDisposable
{
    private readonly string _tempDir;

    public TrueFlacTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TrueFlacTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void RejectsNonFlacExtension()
    {
        string path = Path.Combine(_tempDir, "song.mp3");
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFB, 0x90, 0x64, 0x00 });
        var res = FlacGatekeeper.AuditFile(path);
        Assert.Equal(AuditStatus.Rejected, res.Status);
        Assert.Equal(RejectionReason.NotFlacExtension, res.Reason);
    }

    [Fact]
    public void RejectsFakeFlacWithMp3MagicBytes()
    {
        string path = Path.Combine(_tempDir, "fake.flac");
        var bytes = new byte[128];
        bytes[0] = 0xFF; bytes[1] = 0xFB;
        File.WriteAllBytes(path, bytes);
        var res = FlacGatekeeper.AuditFile(path);
        Assert.Equal(AuditStatus.Rejected, res.Status);
        Assert.Equal(RejectionReason.InvalidMagicBytes, res.Reason);
    }

    [Fact]
    public void RejectsLowSampleRateAndHiResPolicy()
    {
        string low = Path.Combine(_tempDir, "low.flac");
        TestFlacBuilder.WriteFlac16BitMono(low, 22050, SynthesizeMultiTone(22050, 22050, 10000));
        var r1 = FlacGatekeeper.AuditFile(low, QualityPolicy.CdLosslessStrict);
        Assert.Equal(RejectionReason.LowSampleRate, r1.Reason);

        string cd = Path.Combine(_tempDir, "cd.flac");
        TestFlacBuilder.WriteFlac16BitMono(cd, 44100, SynthesizeMultiTone(44100, 44100 * 2, 21000));
        var r2 = FlacGatekeeper.AuditFile(cd, QualityPolicy.HiResStrict);
        Assert.Equal(RejectionReason.LowSampleRate, r2.Reason);
    }

    [Fact]
    public void RejectsCorruptedMd5Flac()
    {
        string path = Path.Combine(_tempDir, "bad_md5.flac");
        TestFlacBuilder.WriteFlac16BitMono(path, 44100, SynthesizeMultiTone(44100, 44100 * 2, 21000), corruptMd5: true);
        var res = FlacGatekeeper.AuditFile(path, QualityPolicy.CdLosslessStrict);
        Assert.Equal(AuditStatus.Rejected, res.Status);
        Assert.Equal(RejectionReason.Md5Mismatch, res.Reason);
        Assert.False(res.Md5Verified);
    }

    [Fact]
    public void AcceptsTrueLosslessAndRejectsLossyTranscode()
    {
        string trueFlac = Path.Combine(_tempDir, "true.flac");
        TestFlacBuilder.WriteFlac16BitMono(trueFlac, 44100, SynthesizeMultiTone(44100, 44100 * 2, 21200));
        var ok = FlacGatekeeper.AuditFile(trueFlac, QualityPolicy.CdLosslessStrict);
        Assert.True(ok.Status == AuditStatus.VerifiedTrueFlac, $"{ok.Reason}: {ok.VerdictHeadline} | {ok.TechnicalDetail}");
        Assert.True(ok.Md5Verified);
        Assert.False(ok.Spectral!.IsLikelyTranscode);
        Assert.True(ok.Spectral.EstimatedCutoffKhz >= 20.5);

        string fakeFlac = Path.Combine(_tempDir, "transcode.flac");
        TestFlacBuilder.WriteFlac16BitMono(fakeFlac, 44100, SynthesizeMultiTone(44100, 44100 * 2, 15500));
        var bad = FlacGatekeeper.AuditFile(fakeFlac, QualityPolicy.CdLosslessStrict);
        Assert.Equal(AuditStatus.Rejected, bad.Status);
        Assert.Equal(RejectionReason.SuspectedLossyTranscode, bad.Reason);
        Assert.True(bad.Spectral!.IsLikelyTranscode);
    }

    [Fact]
    public void PlaybackEngineAndLinkResolversEnforceRules()
    {
        using var engine = new AudioPlaybackEngine();
        Assert.Throws<InvalidOperationException>(() => engine.Play(new TrackAuditResult
        {
            FileName = "bad.mp3",
            FilePath = Path.Combine(_tempDir, "bad.mp3"),
            Status = AuditStatus.Rejected
        }));

        string sub = Path.Combine(_tempDir, "album");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "01.flac"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(sub, "02.mp3"), Array.Empty<byte>());
        string m3u = Path.Combine(_tempDir, "list.m3u");
        File.WriteAllLines(m3u, new[] { "#EXTM3U", "album\\01.flac", "album\\02.mp3" });

        Assert.Equal(2, LinkAndSourceResolver.ExpandLocalInputs(new[] { m3u }).Count);
        Assert.Equal("malice-mizer-merveilles", ArchiveOrgClient.ExtractIdentifier("https://archive.org/details/malice-mizer-merveilles"));
    }

    [Fact]
    public void Accepts24Bit96kStereoAcrossMultipleChunks()
    {
        string path = Path.Combine(_tempDir, "hires_stereo.flac");
        const int sampleRate = 96000;
        const int frames = 20000; // 20,000 * 6 bytes = 120,000 bytes (> 65,536)
        short[] mono = SynthesizeMultiTone(sampleRate, frames, 22000);
        var interleaved24 = new int[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            int s24 = mono[i] << 8;
            interleaved24[i * 2] = s24;
            interleaved24[i * 2 + 1] = s24;
        }

        TestFlacBuilder.WriteFlac24BitStereo(path, sampleRate, interleaved24);
        var res = FlacGatekeeper.AuditFile(path, QualityPolicy.HiResStrict);
        Assert.True(res.Status == AuditStatus.VerifiedTrueFlac, $"{res.Reason}: {res.VerdictHeadline} | {res.TechnicalDetail}");
        Assert.True(res.Md5Verified);
        Assert.Equal(24, res.StreamInfo!.BitsPerSample);
        Assert.Equal(96000, res.StreamInfo.SampleRate);
        Assert.Equal(2, res.StreamInfo.Channels);
        Assert.False(res.Spectral!.IsLikelyTranscode);
    }

    [Fact]
    public void AcceptsId3v2PrefixedFlacAndVerifiesMd5()
    {
        string path = Path.Combine(_tempDir, "with_id3.flac");
        byte[] id3 = TestFlacBuilder.CreateId3v2PaddingHeader(128);
        TestFlacBuilder.WriteFlac16BitMono(path, 44100, SynthesizeMultiTone(44100, 44100 * 2, 21200), prependId3Tag: id3);

        var res = FlacGatekeeper.AuditFile(path, QualityPolicy.CdLosslessStrict);
        Assert.True(res.Status == AuditStatus.VerifiedTrueFlac, $"{res.Reason}: {res.VerdictHeadline} | {res.TechnicalDetail}");
        Assert.Equal(138, res.DataOffset);
        Assert.True(res.Md5Verified);
    }

    [Fact]
    public void AllowsSuspectTranscodeWhenStrictRejectionDisabledAndReevaluatesFast()
    {
        string path = Path.Combine(_tempDir, "suspect.flac");
        TestFlacBuilder.WriteFlac16BitMono(path, 44100, SynthesizeMultiTone(44100, 44100 * 2, 15500));

        var permissive = new QualityPolicy
        {
            MinimumSampleRate = 44100,
            MinimumBitsPerSample = 16,
            RequireValidMd5 = true,
            RejectSuspectedTranscodes = false
        };

        var res = FlacGatekeeper.AuditFile(path, permissive);
        Assert.Equal(AuditStatus.SuspectTranscode, res.Status);
        Assert.True(res.IsPlayable);

        FlacGatekeeper.ReevaluatePolicy(res, QualityPolicy.CdLosslessStrict);
        Assert.Equal(AuditStatus.Rejected, res.Status);
        Assert.False(res.IsPlayable);

        FlacGatekeeper.ReevaluatePolicy(res, permissive);
        Assert.Equal(AuditStatus.SuspectTranscode, res.Status);
        Assert.True(res.IsPlayable);
    }

    [Fact]
    public void AcceptsNaturalMusicalRolloffWithoutFalsePositive()
    {
        string path = Path.Combine(_tempDir, "acoustic_rolloff.flac");
        const int sampleRate = 44100;
        const int count = sampleRate * 2;
        var pcm = new short[count];
        var freqs = new List<(double Freq, double Gain)>();
        for (double f = 440; f <= 21200; f += 430)
        {
            // Natural ~ -4.5 dB/octave acoustic decay above 2 kHz
            double gain = f <= 2000 ? 1.0 : Math.Pow(2000.0 / f, 0.75);
            freqs.Add((f, gain));
        }

        double norm = freqs.Sum(x => x.Gain);
        var rng = new Random(42);
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / sampleRate;
            double v = 0;
            foreach (var (f, gain) in freqs)
                v += Math.Sin(2.0 * Math.PI * f * t) * (0.72 * gain / norm);
            // Gentle TPDF dither at ~ -92 dBFS
            double dither = (rng.NextDouble() - rng.NextDouble()) * 0.00004;
            pcm[i] = (short)Math.Clamp((v + dither) * 32767.0, short.MinValue, short.MaxValue);
        }

        TestFlacBuilder.WriteFlac16BitMono(path, sampleRate, pcm);
        var res = FlacGatekeeper.AuditFile(path, QualityPolicy.CdLosslessStrict);
        Assert.True(res.Status == AuditStatus.VerifiedTrueFlac, $"{res.Reason}: {res.VerdictHeadline} | {res.TechnicalDetail}");
        Assert.False(res.Spectral!.IsLikelyTranscode);
    }

    [Fact]
    public void ArchiveUrlClassifierHandlesSearchItemsAndDirectFlac()
    {
        var search = ArchiveOrgClient.ClassifyUrl("https://archive.org/search?tab=all&query=malice+mizer+format%3AFLAC");
        Assert.Equal(ArchiveLinkKind.SearchQuery, search.Kind);
        Assert.Equal("malice mizer format:FLAC", search.Value);

        var details = ArchiveOrgClient.ClassifyUrl("https://archive.org/details/malice-mizer-merveilles");
        Assert.Equal(ArchiveLinkKind.ItemIdentifier, details.Kind);
        Assert.Equal("malice-mizer-merveilles", details.Value);

        var itemDl = ArchiveOrgClient.ClassifyUrl("https://archive.org/download/malice-mizer-merveilles");
        Assert.Equal(ArchiveLinkKind.ItemIdentifier, itemDl.Kind);
        Assert.Equal("malice-mizer-merveilles", itemDl.Value);

        var flacDl = ArchiveOrgClient.ClassifyUrl("https://archive.org/download/malice-mizer-merveilles/01.flac");
        Assert.Equal(ArchiveLinkKind.DirectFlacUrl, flacDl.Kind);

        var mp3Dl = ArchiveOrgClient.ClassifyUrl("https://archive.org/download/malice-mizer-merveilles/01.mp3");
        Assert.Equal(ArchiveLinkKind.NonFlacArchiveUrl, mp3Dl.Kind);
    }

    [Fact]
    public void AuditsRealDownloadsMaliceMizerFlacReadOnlyWhenPresent()
    {
        string realPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "01 ma chérie~Itoshii Kimi he~.flac");
        if (!File.Exists(realPath))
            return;

        var res = FlacGatekeeper.AuditFile(realPath, QualityPolicy.CdLosslessStrict);
        Assert.Equal(AuditStatus.VerifiedTrueFlac, res.Status);
        Assert.True(res.Md5Verified);
        Assert.NotNull(res.StreamInfo);
        Assert.Equal(16, res.StreamInfo!.BitsPerSample);
        Assert.Equal(44100, res.StreamInfo.SampleRate);
        Assert.Equal(2, res.StreamInfo.Channels);
        Assert.NotNull(res.Spectral);
        Assert.False(res.Spectral!.IsLikelyTranscode);
    }

    private static short[] SynthesizeMultiTone(int sampleRate, int count, int maxFreqHz)
    {
        var pcm = new short[count];
        var freqs = new List<double>();
        for (double f = 440; f <= maxFreqHz; f += 430) freqs.Add(f);
        double amp = 0.7 / freqs.Count;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / sampleRate;
            double v = 0;
            foreach (double f in freqs) v += Math.Sin(2.0 * Math.PI * f * t) * amp;
            pcm[i] = (short)Math.Clamp(v * 32767.0, short.MinValue, short.MaxValue);
        }
        return pcm;
    }
}
