using NAudio.CoreAudioApi;
using NAudio.Flac;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TrueFlac.Core;

public sealed record AudioDeviceOption(string Id, string Name, bool IsDefault);

public sealed record PlaybackOutputInfo(
    string DeviceName,
    bool IsExclusive,
    bool IsBitPerfect,
    int SampleRate,
    int BitsPerSample,
    int Channels)
{
    public string ModeBadge => (IsExclusive, IsBitPerfect) switch
    {
        (true, true) => $"WASAPI EXCLUSIVE · BIT-PERFECT · {BitsPerSample}-bit / {SampleRate / 1000.0:0.#} kHz",
        (true, false) => $"WASAPI EXCLUSIVE · VOLUMEN DSP · {BitsPerSample}-bit / {SampleRate / 1000.0:0.#} kHz",
        _ => $"WASAPI SHARED (Fallback) · {BitsPerSample}-bit / {SampleRate / 1000.0:0.#} kHz"
    };
}

public sealed class AudioPlaybackEngine : IDisposable
{
    private readonly object _sync = new();
    private IWavePlayer? _output;
    private Stream? _sourceStream;
    private FlacReader? _reader;
    private VolumeSampleProvider? _volumeProvider;
    private float _volume = 1.0f;
    private bool _preferExclusive = true;
    private string? _selectedDeviceId;
    private bool _suppressAutoAdvance;

    public event Action? PlaybackEnded;
    public event Action<PlaybackOutputInfo?>? OutputChanged;

    public TrackAuditResult? CurrentTrack { get; private set; }
    public PlaybackOutputInfo? CurrentOutput { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;

    public TimeSpan CurrentTime
    {
        get { lock (_sync) return _reader?.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan TotalTime
    {
        get { lock (_sync) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    public bool PreferExclusive
    {
        get => _preferExclusive;
        set => _preferExclusive = value;
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_sync)
            {
                if (_volumeProvider is not null)
                    _volumeProvider.Volume = _volume;
            }
        }
    }

    public static IReadOnlyList<AudioDeviceOption> GetOutputDevices()
    {
        var list = new List<AudioDeviceOption>();
        using var enumerator = new MMDeviceEnumerator();
        string defaultId = string.Empty;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }

        foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            list.Add(new AudioDeviceOption(dev.ID, dev.FriendlyName, dev.ID == defaultId));
        }
        return list;
    }

    public void SetOutputDevice(string? deviceId) => _selectedDeviceId = deviceId;

    public PlaybackOutputInfo Play(TrackAuditResult track)
    {
        if (!track.IsPlayable)
            throw new InvalidOperationException($"Bloqueado: '{track.FileName}' no pasó la validación True FLAC ({track.VerdictHeadline}).");

        if (!File.Exists(track.FilePath))
            throw new FileNotFoundException("El archivo FLAC ya no existe en disco.", track.FilePath);

        StopInternal();

        lock (_sync)
        {
            _suppressAutoAdvance = false;
            Stream rawStream = FlacBinaryInspector.OpenFlacStream(track.FilePath, track.DataOffset);
            FlacReader? reader = null;
            try
            {
                reader = new FlacReader(rawStream);
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device = ResolveDevice(enumerator);
                string deviceName = device.FriendlyName;

                bool wantBitPerfect = _preferExclusive && _volume >= 0.995f;
                bool isExclusive = false;
                IWavePlayer player;
                VolumeSampleProvider? volProvider;

                if (_preferExclusive)
                {
                    try
                    {
                        (player, volProvider) = InitWasapi(device, reader, AudioClientShareMode.Exclusive, useDspVolume: !wantBitPerfect);
                        isExclusive = true;
                    }
                    catch
                    {
                        reader.Position = 0;
                        (player, volProvider) = InitWasapi(device, reader, AudioClientShareMode.Shared, useDspVolume: true);
                    }
                }
                else
                {
                    (player, volProvider) = InitWasapi(device, reader, AudioClientShareMode.Shared, useDspVolume: true);
                }

                _sourceStream = rawStream;
                _reader = reader;
                _volumeProvider = volProvider;
                _output = player;
                CurrentTrack = track;

                int bits = track.StreamInfo?.BitsPerSample ?? reader.WaveFormat.BitsPerSample;
                int rate = track.StreamInfo?.SampleRate ?? reader.WaveFormat.SampleRate;
                int ch = track.StreamInfo?.Channels ?? reader.WaveFormat.Channels;
                bool isBitPerfect = isExclusive && _volumeProvider is null;
                CurrentOutput = new PlaybackOutputInfo(deviceName, isExclusive, isBitPerfect, rate, bits, ch);

                player.PlaybackStopped += OnPlaybackStopped;
                player.Play();
                OutputChanged?.Invoke(CurrentOutput);
                return CurrentOutput;
            }
            catch
            {
                reader?.Dispose();
                rawStream.Dispose();
                throw;
            }
        }
    }

    public void CommitVolumeChange()
    {
        TrackAuditResult? track;
        TimeSpan pos;
        bool wasPaused;

        lock (_sync)
        {
            if (_output is null || CurrentTrack is null || CurrentOutput is null) return;
            bool shouldBeBitPerfect = _preferExclusive && _volume >= 0.995f;
            bool currentlyBitPerfect = _volumeProvider is null;
            if (!CurrentOutput.IsExclusive || shouldBeBitPerfect == currentlyBitPerfect)
                return;

            track = CurrentTrack;
            pos = CurrentTime;
            wasPaused = IsPaused;
        }

        Play(track);
        if (pos > TimeSpan.Zero) Seek(pos);
        if (wasPaused) TogglePauseResume();
    }

    public void TogglePauseResume()
    {
        lock (_sync)
        {
            if (_output is null) return;
            if (_output.PlaybackState == PlaybackState.Playing) _output.Pause();
            else if (_output.PlaybackState == PlaybackState.Paused) _output.Play();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (_reader is null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > _reader.TotalTime) position = _reader.TotalTime;
            _reader.CurrentTime = position;
        }
    }

    public void Stop()
    {
        StopInternal();
        CurrentTrack = null;
        OutputChanged?.Invoke(null);
    }

    private (IWavePlayer Player, VolumeSampleProvider? VolumeProvider) InitWasapi(
        MMDevice device,
        FlacReader reader,
        AudioClientShareMode shareMode,
        bool useDspVolume)
    {
        bool eventSync = shareMode == AudioClientShareMode.Shared;
        var player = new WasapiOut(device, shareMode, eventSync, 100);
        try
        {
            if (useDspVolume)
            {
                var vol = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = _volume };
                player.Init(vol);
                return (player, vol);
            }

            player.Init(reader);
            return (player, null);
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    private void StopInternal()
    {
        lock (_sync)
        {
            _suppressAutoAdvance = true;
            if (_output is not null)
            {
                _output.PlaybackStopped -= OnPlaybackStopped;
                try { _output.Stop(); } catch { }
                _output.Dispose();
                _output = null;
            }
            if (_reader is not null)
            {
                _reader.Dispose();
                _reader = null;
            }
            if (_sourceStream is not null)
            {
                _sourceStream.Dispose();
                _sourceStream = null;
            }
            _volumeProvider = null;
            CurrentOutput = null;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool invokeErrorReset = false;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _output) || _suppressAutoAdvance)
                return;

            if (e.Exception is not null)
            {
                invokeErrorReset = true;
            }
        }

        if (invokeErrorReset)
        {
            StopInternal();
            CurrentTrack = null;
            OutputChanged?.Invoke(null);
            return;
        }

        PlaybackEnded?.Invoke();
    }

    private MMDevice ResolveDevice(MMDeviceEnumerator enumerator)
    {
        if (!string.IsNullOrWhiteSpace(_selectedDeviceId))
        {
            try { return enumerator.GetDevice(_selectedDeviceId); } catch { }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public void Dispose() => StopInternal();
}
