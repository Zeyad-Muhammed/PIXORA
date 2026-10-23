using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using PIXORA.Capture;
using PIXORA.Core;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PIXORA.Recording;

public class RecordingException : Exception
{
    public RecordingException(string message, Exception? inner = null) : base(message, inner) { }
}

public enum RecorderState { Idle, Starting, Recording, Paused, Stopping, Faulted }

public enum RecordingSourceKind { Monitor, Window, Region }

/// <summary>What to record. Region coordinates are physical pixels on the virtual desktop and must lie on one monitor.</summary>
public sealed record RecordingSource(RecordingSourceKind Kind, IntPtr Monitor = default, IntPtr Window = default, RECT Region = default)
{
    public static RecordingSource ForMonitor(MonitorInfo m) => new(RecordingSourceKind.Monitor, Monitor: m.Handle);
    public static RecordingSource ForWindow(IntPtr hwnd) => new(RecordingSourceKind.Window, Window: hwnd);
    public static RecordingSource ForRegion(RECT region) => new(RecordingSourceKind.Region, Region: region);
}

public sealed class RecordingOptions
{
    public required RecordingSource Source { get; init; }
    public required string OutputPath { get; init; }
    public int FrameRate { get; init; } = 30;
    /// <summary>Output height in pixels; 0 keeps the source size. Width follows the aspect ratio.</summary>
    public int OutputHeight { get; init; }
    public VideoQuality Quality { get; init; } = VideoQuality.High;
    public bool CaptureCursor { get; init; } = true;
    public bool SystemAudio { get; init; }
    public bool Microphone { get; init; }
    public string? MicrophoneDeviceId { get; init; }
    public float SystemVolume { get; init; } = 1f;
    public float MicrophoneVolume { get; init; } = 1f;
}

public sealed record RecordingResult(string Path, TimeSpan Duration, int Width, int Height, long FramesWritten, long FramesDropped, IReadOnlyList<string> Warnings);

/// <summary>
/// Records a monitor, window or region with Windows.Graphics.Capture, mixes optional system/microphone audio and
/// encodes H.264/AAC MP4 through the Windows media transcoder. All timing derives from one pausable clock, so audio and video stay in sync.
/// </summary>
public sealed class ScreenRecorder : IAsyncDisposable
{
    private sealed class Clock
    {
        private readonly Stopwatch _sw = new();
        private TimeSpan _accum;
        public void Start() => _sw.Restart();
        public void Pause() { _accum += _sw.Elapsed; _sw.Reset(); }
        public void Resume() => _sw.Restart();
        public TimeSpan Elapsed => _accum + _sw.Elapsed;
    }

    private readonly object _gate = new();
    private readonly Clock _clock = new();
    private RecorderState _state = RecorderState.Idle;

    private RecordingOptions _opt = null!;
    private GraphicsCaptureItem _item = null!;
    private IDirect3DDevice _device = null!;
    private Direct3D11CaptureFramePool _pool = null!;
    private GraphicsCaptureSession _session = null!;
    private SizeInt32 _lastPoolSize;

    private int _srcW, _srcH;           // size of frames delivered by the capture item
    private int _cropX, _cropY, _outW, _outH; // crop inside source frame, size of video content (before scaling)
    private int _encW, _encH;            // encoded size

    private byte[]? _latest;
    private int _latestW, _latestH;
    private bool _hasFrame;
    private readonly object _frameLock = new();

    private BlockingCollection<MediaStreamSample> _videoQ = null!;
    private BlockingCollection<MediaStreamSample> _audioQ = null!;
    private readonly ConcurrentBag<byte[]> _bufferPool = new();
    private AudioMixer? _audio;
    private Thread? _videoThread, _audioThread;
    private volatile bool _stopPumps;
    private long _framesWritten, _framesDropped;
    private Task<string?>? _encodeTask;
    private string _partPath = "";
    private Exception? _fault;
    private readonly List<string> _warnings = new();

    public RecorderState State { get { lock (_gate) return _state; } }
    public TimeSpan Elapsed => _clock.Elapsed;
    public float SystemLevel => _audio?.SystemLevel ?? 0;
    public float MicrophoneLevel => _audio?.MicrophoneLevel ?? 0;
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public IReadOnlyList<string> Warnings => _warnings;
    public Exception? Fault => _fault;

    public event Action<RecorderState>? StateChanged;
    /// <summary>Raised when the recording stops by itself (e.g. the captured window closed or encoding failed).</summary>
    public event Action<string>? Failed;

    public static bool IsSupported => GraphicsInterop.IsCaptureSupported;

    private void SetState(RecorderState s)
    {
        lock (_gate) _state = s;
        StateChanged?.Invoke(s);
    }

    public void SetSystemVolume(float v) { if (_audio != null) _audio.SystemVolume = v; }
    public void SetMicrophoneVolume(float v) { if (_audio != null) _audio.MicrophoneVolume = v; }

    public async Task StartAsync(RecordingOptions options)
    {
        lock (_gate)
        {
            if (_state is not (RecorderState.Idle or RecorderState.Faulted)) throw new InvalidOperationException("A recording is already in progress.");
            _state = RecorderState.Starting;
        }
        StateChanged?.Invoke(RecorderState.Starting);
        _opt = options;
        _fault = null; _warnings.Clear(); _framesWritten = 0; _framesDropped = 0; _hasFrame = false; _stopPumps = false;

        try
        {
            if (!IsSupported) throw new RecordingException("Screen recording is not supported on this version of Windows (requires Windows 10 version 1903 or later).");
            ScreenCapture.EnsureDpiAwareness();
            if (options.FrameRate is < 5 or > 120) throw new RecordingException("Frame rate is out of range.");

            var dir = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!;
            var probe = SettingsValidator.ProbeWritable(dir);
            if (probe != null) throw new RecordingException(probe);
            try
            {
                if (new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace < 200L * 1024 * 1024)
                    throw new RecordingException("There is less than 200 MB of free disk space. Free some space and try again.");
            }
            catch (ArgumentException) { }

            _device = GraphicsInterop.CreateDevice();
            _item = CreateItem(options.Source);
            ComputeGeometry(options);

            _audio = null;
            if (options.SystemAudio || options.Microphone)
            {
                _audio = new AudioMixer();
                _audio.Start(options.SystemAudio, options.Microphone, options.MicrophoneDeviceId, options.SystemVolume, options.MicrophoneVolume);
                _warnings.AddRange(_audio.Warnings);
                if (!_audio.HasSource) { _audio.Dispose(); _audio = null; }
            }

            _videoQ = new BlockingCollection<MediaStreamSample>(new ConcurrentQueue<MediaStreamSample>(), 16);
            _audioQ = new BlockingCollection<MediaStreamSample>(new ConcurrentQueue<MediaStreamSample>(), 600);

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _item.Size);
            _lastPoolSize = _item.Size;
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsCursorCaptureEnabled = options.CaptureCursor; } catch (Exception) { }
            _item.Closed += OnItemClosed;

            _partPath = options.OutputPath + ".part";
            _encodeTask = Task.Run(() => EncodeAsync(options));

            _session.StartCapture();

            // Wait for the first frame so we never claim to be recording an empty source.
            var deadline = Stopwatch.StartNew();
            while (!_hasFrame)
            {
                if (_encodeTask.IsFaulted) throw _encodeTask.Exception!.GetBaseException();
                if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                    throw new RecordingException("No video frames were received from the selected source. It may be protected content or minimised.");
                await Task.Delay(20).ConfigureAwait(false);
            }

            _clock.Start();
            _videoThread = new Thread(VideoPump) { IsBackground = true, Name = "PIXORA video pump", Priority = ThreadPriority.AboveNormal };
            _videoThread.Start();
            if (_audio != null)
            {
                _audioThread = new Thread(AudioPump) { IsBackground = true, Name = "PIXORA audio pump" };
                _audioThread.Start();
            }
            SetState(RecorderState.Recording);
            Log.Info($"Recording started: {_encW}x{_encH}@{options.FrameRate}, audio sys={options.SystemAudio} mic={options.Microphone}");
        }
        catch (Exception ex)
        {
            Log.Error("Recording failed to start", ex);
            await TeardownAsync(deleteOutput: true).ConfigureAwait(false);
            SetState(RecorderState.Faulted);
            _fault = ex;
            if (ex is RecordingException) throw;
            throw new RecordingException(DescribeStartFailure(ex), ex);
        }
    }

    private static string DescribeStartFailure(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access was denied. Check that the output folder is writable and the window is not elevated.",
        IOException => $"The recording file could not be created: {ex.Message}",
        _ => $"Recording could not start: {ex.Message}",
    };

    private static GraphicsCaptureItem CreateItem(RecordingSource src)
    {
        switch (src.Kind)
        {
            case RecordingSourceKind.Window:
                return GraphicsInterop.CreateItemForWindow(src.Window);
            case RecordingSourceKind.Monitor:
                return GraphicsInterop.CreateItemForMonitor(src.Monitor);
            default:
                var m = ScreenCapture.MonitorAt((src.Region.Left + src.Region.Right) / 2, (src.Region.Top + src.Region.Bottom) / 2)
                    ?? throw new RecordingException("The selected region is not on any display.");
                return GraphicsInterop.CreateItemForMonitor(m.Handle);
        }
    }

    private void ComputeGeometry(RecordingOptions o)
    {
        _srcW = _item.Size.Width; _srcH = _item.Size.Height;
        if (_srcW < 2 || _srcH < 2) throw new RecordingException("The selected source has no visible area.");
        _cropX = 0; _cropY = 0; _outW = _srcW; _outH = _srcH;
        if (o.Source.Kind == RecordingSourceKind.Region)
        {
            var mon = ScreenCapture.MonitorAt((o.Source.Region.Left + o.Source.Region.Right) / 2, (o.Source.Region.Top + o.Source.Region.Bottom) / 2)!;
            _cropX = Math.Clamp(o.Source.Region.Left - mon.Bounds.Left, 0, _srcW - 2);
            _cropY = Math.Clamp(o.Source.Region.Top - mon.Bounds.Top, 0, _srcH - 2);
            _outW = Math.Clamp(o.Source.Region.Width, 2, _srcW - _cropX);
            _outH = Math.Clamp(o.Source.Region.Height, 2, _srcH - _cropY);
        }
        _outW &= ~1; _outH &= ~1;
        if (o.OutputHeight > 0 && o.OutputHeight < _outH)
        {
            _encH = o.OutputHeight & ~1;
            _encW = ((int)Math.Round(_outW * (_encH / (double)_outH))) & ~1;
        }
        else { _encW = _outW; _encH = _outH; }
        _encW = Math.Max(2, _encW); _encH = Math.Max(2, _encH);
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        Log.Info("Capture source closed; stopping recording.");
        _ = Task.Run(async () =>
        {
            try { await StopAsync().ConfigureAwait(false); Failed?.Invoke("The recorded window was closed. The recording up to that point was saved."); }
            catch (Exception ex) { Failed?.Invoke(ex.Message); }
        });
    }

    // ---- frame intake ----------------------------------------------------------------------------------------

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var size = frame.ContentSize;
            if (size.Width != _lastPoolSize.Width || size.Height != _lastPoolSize.Height)
            {
                _lastPoolSize = size;
                sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
            }
            using var bmp = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore).AsTask().GetAwaiter().GetResult();
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            lock (_frameLock)
            {
                if (_latest == null || _latest.Length != w * h * 4) _latest = new byte[w * h * 4];
                bmp.CopyToBuffer(_latest.AsBuffer());
                _latestW = w; _latestH = h;
                _hasFrame = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Frame intake error: {ex.Message}");
        }
    }

    private byte[] RentBuffer(int size)
    {
        while (_bufferPool.TryTake(out var b))
            if (b.Length == size) return b;
        return new byte[size];
    }

    private MediaStreamSample BuildVideoSample(long index)
    {
        int rowBytes = _outW * 4;
        var buf = RentBuffer(rowBytes * _outH);
        lock (_frameLock)
        {
            // Source may have been resized mid-recording: copy the overlapping area, leave the rest black.
            int copyW = Math.Max(0, Math.Min(_outW, _latestW - _cropX));
            int copyH = Math.Max(0, Math.Min(_outH, _latestH - _cropY));
            if (copyW < _outW || copyH < _outH) Array.Clear(buf, 0, buf.Length);
            int srcStride = _latestW * 4;
            for (int y = 0; y < copyH; y++)
                Buffer.BlockCopy(_latest!, (y + _cropY) * srcStride + _cropX * 4, buf, y * rowBytes, copyW * 4);
        }
        var sample = MediaStreamSample.CreateFromBuffer(buf.AsBuffer(0, rowBytes * _outH, rowBytes * _outH), TimeSpan.FromTicks(index * TimeSpan.TicksPerSecond / _opt.FrameRate));
        sample.Duration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / _opt.FrameRate);
        sample.Processed += (_, _) => { if (_bufferPool.Count < 8) _bufferPool.Add(buf); };
        return sample;
    }

    private void VideoPump()
    {
        double fps = _opt.FrameRate;
        try
        {
            while (!_stopPumps)
            {
                if (State == RecorderState.Paused) { Thread.Sleep(10); continue; }
                long target = (long)(_clock.Elapsed.TotalSeconds * fps) + 1;
                int burst = 0;
                while (_framesWritten < target && !_stopPumps)
                {
                    long index = _framesWritten;
                    if (burst++ >= 8 || _videoQ.Count >= 15)
                    {
                        // Encoder is behind: skip ahead so the timeline stays truthful instead of ballooning memory.
                        Interlocked.Add(ref _framesDropped, target - _framesWritten);
                        _framesWritten = target;
                        break;
                    }
                    var s = BuildVideoSample(index);
                    if (!_videoQ.TryAdd(s, 50)) Interlocked.Increment(ref _framesDropped);
                    _framesWritten++;
                }
                Thread.Sleep(2);
            }
        }
        catch (Exception ex) when (ex is not ThreadAbortException)
        {
            Log.Error("Video pump failed", ex);
            Fail($"Video capture failed: {ex.Message}");
        }
    }

    private void AudioPump()
    {
        const int chunk = 960; // 20 ms at 48 kHz
        long written = 0;
        try
        {
            while (!_stopPumps)
            {
                if (State == RecorderState.Paused) { Thread.Sleep(10); continue; }
                long target = (long)(_clock.Elapsed.TotalSeconds * AudioMixer.SampleRate);
                while (written + chunk <= target && !_stopPumps)
                {
                    var pcm = _audio!.ReadPcm16(chunk);
                    var sample = MediaStreamSample.CreateFromBuffer(pcm.AsBuffer(), TimeSpan.FromTicks(written * TimeSpan.TicksPerSecond / AudioMixer.SampleRate));
                    sample.Duration = TimeSpan.FromTicks(chunk * TimeSpan.TicksPerSecond / AudioMixer.SampleRate);
                    if (!_audioQ.TryAdd(sample, 100)) Log.Warn("Audio queue full; dropping chunk.");
                    written += chunk;
                }
                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio pump failed", ex);
            Fail($"Audio capture failed: {ex.Message}");
        }
    }

    // ---- encoding --------------------------------------------------------------------------------------------

    private async Task<string?> EncodeAsync(RecordingOptions o)
    {
        var videoProps = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_outW, (uint)_outH);
        videoProps.FrameRate.Numerator = (uint)o.FrameRate;
        videoProps.FrameRate.Denominator = 1;
        videoProps.PixelAspectRatio.Numerator = 1;
        videoProps.PixelAspectRatio.Denominator = 1;
        var videoDesc = new VideoStreamDescriptor(videoProps);

        MediaStreamSource mss;
        if (_audio != null)
        {
            var audioProps = AudioEncodingProperties.CreatePcm(AudioMixer.SampleRate, AudioMixer.Channels, 16);
            mss = new MediaStreamSource(videoDesc, new AudioStreamDescriptor(audioProps));
        }
        else mss = new MediaStreamSource(videoDesc);
        mss.BufferTime = TimeSpan.Zero;
        mss.CanSeek = false;
        mss.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        mss.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video!.Width = (uint)_encW;
        profile.Video.Height = (uint)_encH;
        profile.Video.FrameRate.Numerator = (uint)o.FrameRate;
        profile.Video.FrameRate.Denominator = 1;
        double bpp = o.Quality switch { VideoQuality.Low => 0.045, VideoQuality.Medium => 0.08, _ => 0.13 };
        profile.Video.Bitrate = (uint)Math.Clamp(_encW * _encH * (double)o.FrameRate * bpp, 800_000, 80_000_000);
        if (_audio == null) profile.Audio = null;
        else { profile.Audio!.Bitrate = 160_000; profile.Audio.ChannelCount = 2; profile.Audio.SampleRate = 48000; }

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(_partPath))!);
        var file = await folder.CreateFileAsync(Path.GetFileName(_partPath), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
        if (!prep.CanTranscode)
            throw new RecordingException($"This system cannot encode the recording ({prep.FailureReason}).");
        await prep.TranscodeAsync();
        await stream.FlushAsync();
        return _partPath;
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var req = args.Request;
        var queue = req.StreamDescriptor is VideoStreamDescriptor ? _videoQ : _audioQ;
        var deferral = req.GetDeferral();
        Task.Run(() =>
        {
            try
            {
                req.Sample = queue.TryTake(out var s, Timeout.Infinite) ? s : null;
            }
            catch (Exception) { req.Sample = null; } // collection completed -> end of stream
            finally { deferral.Complete(); }
        });
    }

    // ---- control ---------------------------------------------------------------------------------------------

    public void Pause()
    {
        lock (_gate)
        {
            if (_state != RecorderState.Recording) return;
            _clock.Pause();
            _state = RecorderState.Paused;
        }
        StateChanged?.Invoke(RecorderState.Paused);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != RecorderState.Paused) return;
            _audio?.Flush();
            _clock.Resume();
            _state = RecorderState.Recording;
        }
        StateChanged?.Invoke(RecorderState.Recording);
    }

    private void Fail(string message)
    {
        if (State is RecorderState.Stopping or RecorderState.Idle or RecorderState.Faulted) return;
        _fault ??= new RecordingException(message);
        _ = Task.Run(async () =>
        {
            try { await TeardownAsync(deleteOutput: true).ConfigureAwait(false); } catch (Exception) { }
            SetState(RecorderState.Faulted);
            Failed?.Invoke(message);
        });
    }

    /// <summary>Stops, finalises the MP4 and returns its details. The file only appears at its final path once it is complete.</summary>
    public async Task<RecordingResult> StopAsync()
    {
        lock (_gate)
        {
            if (_state is not (RecorderState.Recording or RecorderState.Paused)) throw new InvalidOperationException("No recording is in progress.");
            if (_state == RecorderState.Recording) { }
            _state = RecorderState.Stopping;
        }
        StateChanged?.Invoke(RecorderState.Stopping);
        _clock.Pause();
        var duration = _clock.Elapsed;

        try
        {
            await StopCaptureAsync().ConfigureAwait(false);
            _videoQ.CompleteAdding();
            _audioQ.CompleteAdding();
            var part = await _encodeTask!.ConfigureAwait(false);
            if (part == null || !File.Exists(part) || new FileInfo(part).Length == 0)
                throw new RecordingException("The recording file is empty.");
            File.Move(part, _opt.OutputPath, true);
            Log.Info($"Recording saved: {Path.GetFileName(_opt.OutputPath)} ({duration:mm\\:ss}), dropped {_framesDropped} frames");
            return new RecordingResult(_opt.OutputPath, duration, _encW, _encH, _framesWritten, _framesDropped, _warnings.ToList());
        }
        catch (Exception ex)
        {
            Log.Error("Recording finalisation failed", ex);
            TryDelete(_partPath);
            _fault = ex;
            SetState(RecorderState.Faulted);
            throw ex is RecordingException ? ex : new RecordingException(
                ex is IOException && ((ex.HResult & 0xFFFF) is 112 or 39) ? "The disk is full. The recording could not be saved." : $"The recording could not be finalised: {ex.Message}", ex);
        }
        finally
        {
            await DisposeResourcesAsync().ConfigureAwait(false);
            if (State == RecorderState.Stopping) SetState(RecorderState.Idle);
        }
    }

    private async Task StopCaptureAsync()
    {
        _stopPumps = true;
        try { _session?.Dispose(); } catch (Exception) { }
        try { if (_pool != null) _pool.FrameArrived -= OnFrameArrived; _pool?.Dispose(); } catch (Exception) { }
        if (_item != null) _item.Closed -= OnItemClosed;
        await Task.Run(() => { _videoThread?.Join(2000); _audioThread?.Join(2000); }).ConfigureAwait(false);
        try { _audio?.Dispose(); } catch (Exception) { }
    }

    private async Task TeardownAsync(bool deleteOutput)
    {
        try { await StopCaptureAsync().ConfigureAwait(false); } catch (Exception) { }
        try { _videoQ?.CompleteAdding(); _audioQ?.CompleteAdding(); } catch (Exception) { }
        if (_encodeTask != null)
        {
            try { await _encodeTask.ConfigureAwait(false); } catch (Exception) { }
        }
        if (deleteOutput) TryDelete(_partPath);
        await DisposeResourcesAsync().ConfigureAwait(false);
    }

    private Task DisposeResourcesAsync()
    {
        try { _audio?.Dispose(); } catch (Exception) { }
        _audio = null;
        try { (_device as IDisposable)?.Dispose(); } catch (Exception) { }
        _latest = null;
        _bufferPool.Clear();
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (State is RecorderState.Recording or RecorderState.Paused)
        {
            try { await StopAsync().ConfigureAwait(false); } catch (Exception) { }
        }
    }
}
