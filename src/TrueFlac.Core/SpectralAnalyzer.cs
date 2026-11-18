using System.Numerics;

namespace TrueFlac.Core;

public static class SpectralAnalyzer
{
    public const int FftSize = 4096;
    private const int SpectrumDisplayBins = 32;

    public static SpectralAnalysisResult Analyze(IReadOnlyList<float[]> activeWindows, int sampleRate)
    {
        if (activeWindows.Count == 0 || sampleRate < 16000)
        {
            return new SpectralAnalysisResult
            {
                EstimatedCutoffKhz = 0,
                IsLikelyTranscode = false,
                ActiveWindowsSampled = activeWindows.Count,
                Summary = "Pista demasiado silenciosa o corta para análisis espectral fiable."
            };
        }

        int halfBins = FftSize / 2;
        double[] avgPower = new double[halfBins];
        double[] peakPower = new double[halfBins];
        double[] hann = CreateHannWindow(FftSize);

        var buffer = new Complex[FftSize];
        foreach (float[] window in activeWindows)
        {
            int copyLen = Math.Min(window.Length, FftSize);
            for (int i = 0; i < FftSize; i++)
            {
                double s = i < copyLen ? window[i] * hann[i] : 0.0;
                buffer[i] = new Complex(s, 0.0);
            }

            FftInPlace(buffer);

            for (int k = 0; k < halfBins; k++)
            {
                double mag = buffer[k].Magnitude / (FftSize * 0.5);
                double p = mag * mag;
                avgPower[k] += p;
                if (p > peakPower[k]) peakPower[k] = p;
            }
        }

        double[] blendedPower = new double[halfBins];
        for (int k = 0; k < halfBins; k++)
        {
            double avg = avgPower[k] / activeWindows.Count;
            blendedPower[k] = Math.Max((avg * 0.4) + (peakPower[k] * 0.6), 1e-14);
        }

        double nyquistKhz = sampleRate / 2000.0;
        double binHz = (double)sampleRate / FftSize;
        double midBandDb = BandPowerDb(blendedPower, binHz, 1000, 8000);
        double highBandDb = BandPowerDb(blendedPower, binHz, 16000, Math.Min(20000, nyquistKhz * 1000.0 - 200));
        double ultraHighDb = BandPowerDb(blendedPower, binHz, 19500, Math.Min(21800, nyquistKhz * 1000.0 - 150));

        double maxGraphKhz = Math.Min(22.05, nyquistKhz);
        var graphFreqs = new double[SpectrumDisplayBins];
        var graphDb = new double[SpectrumDisplayBins];
        double stepKhz = (maxGraphKhz - 0.5) / SpectrumDisplayBins;
        for (int i = 0; i < SpectrumDisplayBins; i++)
        {
            double fStart = 0.5 + i * stepKhz;
            double fEnd = fStart + stepKhz;
            graphFreqs[i] = Math.Round((fStart + fEnd) * 0.5, 2);
            graphDb[i] = Math.Round(BandPowerDb(blendedPower, binHz, fStart * 1000.0, fEnd * 1000.0) - midBandDb, 1);
        }

        return EvaluateSpectrum(blendedPower, binHz, nyquistKhz, maxGraphKhz, midBandDb, highBandDb, ultraHighDb, graphFreqs, graphDb, activeWindows.Count);
    }

    private static SpectralAnalysisResult EvaluateSpectrum(
        double[] blendedPower,
        double binHz,
        double nyquistKhz,
        double maxGraphKhz,
        double midBandDb,
        double highBandDb,
        double ultraHighDb,
        double[] graphFreqs,
        double[] graphDb,
        int activeWindowsCount)
    {
        var sliceFreqs = new List<double>();
        var sliceRelDb = new List<double>();
        double scanTopKhz = Math.Min(21.5, nyquistKhz - 0.25);
        for (double fKhz = 11.0; fKhz <= scanTopKhz; fKhz += 0.5)
        {
            double rel = BandPowerDb(blendedPower, binHz, fKhz * 1000.0, (fKhz + 0.5) * 1000.0) - midBandDb;
            sliceFreqs.Add(fKhz);
            sliceRelDb.Add(rel);
        }

        double maxSlope = 0;
        double steepestDropKhz = maxGraphKhz;
        double? cliffKhz = null;
        for (int i = 0; i < sliceRelDb.Count - 2; i++)
        {
            double drop1Khz = sliceRelDb[i] - sliceRelDb[i + 2];
            if (drop1Khz > maxSlope)
            {
                maxSlope = drop1Khz;
                steepestDropKhz = sliceFreqs[i + 1];
            }

            if (cliffKhz is null && sliceRelDb[i] > -48.0)
            {
                double drop15Khz = i + 3 < sliceRelDb.Count ? sliceRelDb[i] - sliceRelDb[i + 3] : drop1Khz;
                int postIdx = (i + 3 < sliceRelDb.Count && drop15Khz > drop1Khz) ? i + 3 : i + 2;
                double maxDrop = Math.Max(drop1Khz, drop15Khz);
                if (sliceRelDb[postIdx] <= -58.0 || maxDrop >= 20.0)
                {
                    bool staysSuppressed = true;
                    for (int k = postIdx; k < sliceRelDb.Count; k++)
                    {
                        if (sliceRelDb[k] > sliceRelDb[i] - 18.0 || sliceRelDb[k] > -50.0)
                        {
                            staysSuppressed = false;
                            break;
                        }
                    }
                    if (staysSuppressed)
                        cliffKhz = sliceFreqs[i + 1];
                }
            }
        }

        double cutoffKhz = maxGraphKhz;
        if (cliffKhz is { } detectedCliff)
        {
            cutoffKhz = detectedCliff;
            for (int i = 0; i < sliceRelDb.Count; i++)
            {
                if (sliceRelDb[i] < -56.0 && sliceFreqs[i] >= detectedCliff - 0.5)
                {
                    bool staysDead = true;
                    for (int j = i; j < sliceRelDb.Count; j++)
                    {
                        if (sliceRelDb[j] > -50.0) { staysDead = false; break; }
                    }
                    if (staysDead) { cutoffKhz = sliceFreqs[i]; break; }
                }
            }
        }
        else if (sliceRelDb.Count > 0)
        {
            int lastActive = -1;
            for (int i = sliceRelDb.Count - 1; i >= 0; i--)
            {
                if (sliceRelDb[i] >= -58.0)
                {
                    lastActive = i;
                    break;
                }
            }
            if (lastActive >= 0 && lastActive < sliceRelDb.Count - 1)
                cutoffKhz = Math.Min(maxGraphKhz, sliceFreqs[lastActive] + 0.5);
            else if (lastActive < 0)
                cutoffKhz = sliceFreqs[0];
        }

        double band12to15Rel = BandPowerDb(blendedPower, binHz, 12000, 15000) - midBandDb;
        double highRel = highBandDb - midBandDb;
        double ultraRel = ultraHighDb - midBandDb;

        bool lowBrickwall = cutoffKhz <= 17.5 && band12to15Rel > -40.0 && (cliffKhz is not null || maxSlope >= 12.0 || (ultraRel < -68.0 && band12to15Rel > -28.0));
        bool midHighBrickwall = cutoffKhz <= 20.2 && maxSlope >= 18.0 && ultraRel < -55.0;
        bool deadShelfTranscode = nyquistKhz >= 21.5 && band12to15Rel > -32.0 && ultraRel < -66.0 && maxSlope >= 15.0;
        bool likelyTranscode = lowBrickwall || midHighBrickwall || deadShelfTranscode;

        string summary;
        if (likelyTranscode)
        {
            double reportKhz = cutoffKhz < maxGraphKhz - 0.5 ? cutoffKhz : steepestDropKhz;
            summary = $"Posible transcode lossy: corte abrupto cerca de {reportKhz:0.0} kHz (pendiente {maxSlope:0.0} dB/kHz, ultra-agudos {ultraRel:0.0} dB).";
        }
        else
        {
            summary = $"Espectro lossless continuo hasta ~{cutoffKhz:0.0} kHz (agudos {highRel:0.0} dB, ultra-agudos {ultraRel:0.0} dB).";
        }

        return new SpectralAnalysisResult
        {
            EstimatedCutoffKhz = Math.Round(cutoffKhz, 1),
            MidBandDb = Math.Round(midBandDb, 1),
            HighBandDb = Math.Round(highRel, 1),
            UltraHighBandDb = Math.Round(ultraRel, 1),
            MaxDropSlopeDbPerKhz = Math.Round(maxSlope, 1),
            BandSpectrumDb = graphDb,
            BandFrequenciesKhz = graphFreqs,
            IsLikelyTranscode = likelyTranscode,
            ActiveWindowsSampled = activeWindowsCount,
            Summary = summary
        };
    }

    private static double BandPowerDb(double[] spectrumPower, double binHz, double lowHz, double highHz)
    {
        if (highHz <= lowHz) return -140.0;
        int start = Math.Clamp((int)Math.Floor(lowHz / binHz), 1, spectrumPower.Length - 1);
        int end = Math.Clamp((int)Math.Ceiling(highHz / binHz), start, spectrumPower.Length - 1);
        double sum = 0;
        int count = 0;
        for (int k = start; k <= end; k++) { sum += spectrumPower[k]; count++; }
        double meanPower = count > 0 ? sum / count : 1e-14;
        return 10.0 * Math.Log10(Math.Max(meanPower, 1e-14));
    }

    private static double[] CreateHannWindow(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++)
            w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }

    private static void FftInPlace(Complex[] data)
    {
        int n = data.Length;
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
            int m = n >> 1;
            while (m >= 1 && j >= m) { j -= m; m >>= 1; }
            j += m;
        }

        for (int s = 1; (1 << s) <= n; s++)
        {
            int m = 1 << s;
            int half = m >> 1;
            double angle = -2.0 * Math.PI / m;
            Complex wm = new(Math.Cos(angle), Math.Sin(angle));
            for (int k = 0; k < n; k += m)
            {
                Complex w = Complex.One;
                for (int jj = 0; jj < half; jj++)
                {
                    Complex t = w * data[k + jj + half];
                    Complex u = data[k + jj];
                    data[k + jj] = u + t;
                    data[k + jj + half] = u - t;
                    w *= wm;
                }
            }
        }
    }
}
