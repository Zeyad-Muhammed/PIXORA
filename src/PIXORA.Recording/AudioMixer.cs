using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PIXORA.Core;

namespace PIXORA.Recording;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> GetMicrophones()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defId = null;
            try { using var d = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); defId = d.ID; } catch (Exception) { }
            var list = new List<AudioDeviceInfo>();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (dev) list.Add(new AudioDeviceInfo(dev.ID, dev.FriendlyName, dev.ID == defId));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list microphones: {ex.Message}");
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    public static string? DefaultSpeakersName()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return d.FriendlyName;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Captures system audio (WASAPI loopback) and/or a microphone and mixes them to a single 48 kHz stereo stream.
/// The mixer is pulled on a clock, so silent sources (loopback produces no packets while nothing plays) never desync the video.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private sealed class PeakMeter : ISampleProvider
    {
        private readonly ISampleProvider _src;
        public float Peak;
        public PeakMeter(ISampleProvider src) => _src = src;
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(Span<float> buffer)
        {
            int n = _src.Read(buffer);
            float p = 0;
            for (int i = 0; i < n; i++) { var a = Math.Abs(buffer[i]); if (a > p) p = a; }
            Peak = p;
            return n;
        }
    }

    private sealed class Source : IDisposable
    {
        public IWaveIn Capture = null!;
        public BufferedWaveProvider Buffer = null!;
        public VolumeSampleProvider Volume = null!;
        public PeakMeter Meter = null!;
        public void Dispose()
        {
            try { Capture.StopRecording(); } catch (Exception) { }
            Capture.Dispose();
        }
    }

    private readonly object _gate = new();
    private Source? _system, _mic;
    private MixingSampleProvider? _mixer;
    private float[] _scratch = new float[SampleRate / 50 * Channels];

    public List<string> Warnings { get; } = new();
    public bool HasSource => _system != null || _mic != null;
    public bool HasSystem => _system != null;
    public bool HasMicrophone => _mic != null;

    public float SystemLevel => _system?.Meter.Peak ?? 0;
    public float MicrophoneLevel => _mic?.Meter.Peak ?? 0;

    public float SystemVolume { set { if (_system != null) _system.Volume.Volume = Math.Clamp(value, 0, 2); } }
    public float MicrophoneVolume { set { if (_mic != null) _mic.Volume.Volume = Math.Clamp(value, 0, 2); } }

    /// <summary>Starts the requested sources. Sources that cannot start are skipped and reported in <see cref="Warnings"/>.</summary>
    public void Start(bool systemAudio, bool microphone, string? micDeviceId, float systemVolume, float micVolume)
    {
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels)) { ReadFully = true };

        if (systemAudio)
        {
            try
            {
                var cap = new WasapiLoopbackCapture();
                _system = Wire(cap, systemVolume);
                cap.StartRecording();
            }
            catch (Exception ex)
            {
                Warnings.Add("System audio could not be captured. Recording continues without it.");
                Log.Warn($"Loopback capture failed: {ex.Message}");
                _system?.Dispose(); _system = null;
            }
        }

        if (microphone)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                MMDevice dev;
                try { dev = !string.IsNullOrEmpty(micDeviceId) ? en.GetDevice(micDeviceId) : en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
                catch (Exception) { dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
                var cap = new WasapiCapture(dev);
                _mic = Wire(cap, micVolume);
                cap.StartRecording();
            }
            catch (Exception ex)
            {
                Warnings.Add("The microphone is unavailable or access is blocked in Windows Privacy settings. Recording continues without it.");
                Log.Warn($"Microphone capture failed: {ex.Message}");
                _mic?.Dispose(); _mic = null;
            }
        }
    }

    private Source Wire(IWaveIn capture, float volume)
    {
        var s = new Source { Capture = capture };
        s.Buffer = new BufferedWaveProvider(capture.WaveFormat, TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        capture.DataAvailable += (_, e) => s.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => { if (e.Exception != null) Log.Warn($"Audio capture stopped: {e.Exception.Message}"); };

        ISampleProvider sp = s.Buffer.ToSampleProvider();
        int ch = sp.WaveFormat.Channels;
        if (ch == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (ch > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { sp }, 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(ch > 1 ? 1 : 0, 1);
            sp = mux;
        }
        if (sp.WaveFormat.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
        s.Volume = new VolumeSampleProvider(sp) { Volume = Math.Clamp(volume, 0, 2) };
        s.Meter = new PeakMeter(s.Volume);
        _mixer!.AddMixerInput(s.Meter);
        return s;
    }

    /// <summary>Reads exactly <paramref name="frames"/> stereo frames as 16-bit little-endian PCM (silence where nothing was captured).</summary>
    public byte[] ReadPcm16(int frames)
    {
        int samples = frames * Channels;
        lock (_gate)
        {
            if (_scratch.Length < samples) _scratch = new float[samples];
            int n = _mixer!.Read(_scratch.AsSpan(0, samples));
            for (int i = n; i < samples; i++) _scratch[i] = 0;
        }
        var bytes = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            float f = Math.Clamp(_scratch[i], -1f, 1f);
            short v = (short)(f * short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>Drops audio buffered while paused so it is not replayed on resume.</summary>
    public void Flush()
    {
        _system?.Buffer.ClearBuffer();
        _mic?.Buffer.ClearBuffer();
    }

    public void Dispose()
    {
        _system?.Dispose(); _system = null;
        _mic?.Dispose(); _mic = null;
    }
}
