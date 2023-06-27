using NAudio.Wave;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media;
using System.Diagnostics;
using WinRT;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.Storage;
using System.Linq;

namespace VoiceRecording.CaptureEncoder;

internal class AudioCapture : IDisposable
{
    private WasapiLoopbackCapture _wasapiLoopbackCapture;
    private AudioGraph _audioGraph;
    private AudioFrameInputNode _loopbackInputNode;
    private AudioFileInputNode _audioFileInputNode;
    private AudioDeviceInputNode _deviceInputNode;
    private AudioFrameOutputNode _frameOutputNode;
    private AudioSubmixNode _submixNode;
    private Stream _loopingAudioStream;
    private object _lockObject = new object();
    private double _readPosition = 0;
    private Stopwatch _stopwatch = new Stopwatch();
    private int _frameCount = 0;
    private bool disposedValue;
    private bool _isStarted;

    public async Task InitializeAsync()
    {
        InitializeAudioRecording();
        ShowMessage("NAudio initialized");
        await InitializeAudioGraphAsync();
        ShowMessage("AudioGraph initialized");
    }

    public void Start()
    {
        // 开始录制.
        ShowMessage("开始录制");
        _audioGraph.Start();
        _audioFileInputNode.Start();
        _loopbackInputNode?.Start();
        _frameOutputNode.Start();
        _stopwatch.Start();
        _wasapiLoopbackCapture?.StartRecording();
        _isStarted = true;
    }

    public void Stop()
    {
        // 结束录制.
        ShowMessage("录制结束");
        var duration = _stopwatch.Elapsed.TotalSeconds;
        ShowMessage($"总计音频帧数：{_frameCount}\n用时：{duration:0.0}s\n频率：{_frameCount / duration}");
        _audioFileInputNode?.Stop();
        _loopbackInputNode?.Stop();
        _frameOutputNode?.Stop();
        _audioGraph?.Stop();
        _isStarted = false;
        // ShowMessage($"当前指针：{_readPosition}\n流的长度：{_loopingAudioStream.Length}");
    }

    public AudioEncodingProperties GetEncodingProeprties()
        => _audioGraph.EncodingProperties;

    public AudioFrame GetAudioFrame()
    {
        try
        {
            var frame = _frameOutputNode.GetFrame();
            var ts = Stopwatch.GetTimestamp();
            ts = ts * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
            frame.SystemRelativeTime = new TimeSpan(ts);
            return frame;
        }
        catch (Exception)
        {
            return default;
        }
    }

    public void MuteDeviceInput()
    {
        if (!_isStarted)
        {
            return;
        }


        _deviceInputNode.OutgoingGain = 0;
    }

    public void UnmuteDeviceInput()
    {
        if(!_isStarted)
        {
            return;
        }

        _deviceInputNode.OutgoingGain = 1;
    }

    public IBuffer ConvertFrameToBuffer(AudioFrame frame)
    {
        try
        {
            return ProcessFrameOutput(frame);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private async Task InitializeAudioGraphAsync()
    {
        AudioGraphSettings settings = new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Media);
        settings.QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired;
        CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);
        if (result.Status != AudioGraphCreationStatus.Success)
        {
            ShowMessage("AudioGraph creation error: " + result.Status.ToString());
        }

        _audioGraph = result.Graph;
        CreateFrameOutputNode();
        await CreateDeviceInputNodeAsync();
        await CreateFileInputNodeAsync();
        CreateFrameInputNode();

        if (_frameOutputNode == null || _deviceInputNode == null)
        {
            return;
        }


        var subNode = _audioGraph.CreateSubmixNode();
        _deviceInputNode.AddOutgoingConnection(subNode);
        _loopbackInputNode.AddOutgoingConnection(subNode);
        _audioFileInputNode.AddOutgoingConnection(subNode);
        _submixNode = subNode;
        subNode.AddOutgoingConnection(_frameOutputNode);
    }

    private void InitializeAudioRecording()
    {
        _wasapiLoopbackCapture = new WasapiLoopbackCapture();

        // 设置音频输入设备的格式
        var waveFormat = _wasapiLoopbackCapture.WaveFormat;

        _loopingAudioStream = new MemoryStream();

        // 录制音频数据
        _wasapiLoopbackCapture.DataAvailable += (sender, e) =>
        {
            lock (_lockObject)
            {
                _loopingAudioStream.Seek(0, SeekOrigin.End);
                _loopingAudioStream.WriteAsync(e.Buffer, 0, e.BytesRecorded);
            }
        };
    }

    private void ShowMessage(string msg)
    {
        // ResultBox.Text += msg + "\n";
        Debug.WriteLine(msg);
    }

    private void CreateFrameInputNode()
    {
        _loopbackInputNode = _audioGraph.CreateFrameInputNode();
        _loopbackInputNode.Stop();
        _loopbackInputNode.QuantumStarted += OnLoopbackInputNodeQuantumStarted;
        _readPosition = 0;
    }

    private async Task CreateFileInputNodeAsync()
    {
        var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/emptyAudio.mp3"));
        var result = await _audioGraph.CreateFileInputNodeAsync(file);
        if(result.Status != AudioFileNodeCreationStatus.Success)
        {
            ShowMessage(result.Status.ToString());
        }

        _audioFileInputNode = result.FileInputNode;
        _audioFileInputNode.LoopCount = null;
    }

    private async Task CreateDeviceInputNodeAsync()
    {
        var result = await _audioGraph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Other).AsTask();
        if (result.Status != AudioDeviceNodeCreationStatus.Success)
        {
            // Cannot create device output node
            ShowMessage(result.Status.ToString());
            return;
        }

        _deviceInputNode = result.DeviceInputNode;
    }

    private void CreateFrameOutputNode()
    {
        _frameOutputNode = _audioGraph.CreateFrameOutputNode();
    }

    unsafe private AudioFrame GenerateAudioData(uint samples)
    {
        uint bufferSize = _audioGraph.EncodingProperties.SampleRate;
        // Buffer size is (number of samples) * (size of each sample)
        // We choose to generate single channel (mono) audio. For multi-channel, multiply by number of channels
        AudioFrame frame = new AudioFrame(bufferSize);
        if (_loopingAudioStream == null || !_loopingAudioStream.CanSeek)
        {
            return default;
        }

        using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (IMemoryBufferReference reference = buffer.CreateReference())
        {
            byte* dataInBytes;
            uint capacityInBytes;

            // Get the buffer from the AudioFrame
            (reference.As<IMemoryBufferByteAccess>()).GetBuffer(out dataInBytes, out capacityInBytes);

            if (_loopingAudioStream.Length < _readPosition + bufferSize)
            {
                return default;
            }


            var bytes = new byte[bufferSize];
            lock (_lockObject)
            {
                _loopingAudioStream.Seek(Convert.ToInt64(_readPosition), SeekOrigin.Begin);
                _loopingAudioStream.Read(bytes, 0, (int)bufferSize);
                for (int i = 0; i < bufferSize; i++)
                {
                    dataInBytes[i] = bytes[i];
                }

                _readPosition += bufferSize;
            }
        }


        return frame;
    }

    private void OnLoopbackInputNodeQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
    {
        if (args.RequiredSamples == 0)
        {
            return;
        }

        var frame = GenerateAudioData((uint)args.RequiredSamples);
        if (frame == null)
        {
            return;
        }

        sender.AddFrame(frame);
        _frameCount++;
    }

    unsafe private IBuffer ProcessFrameOutput(AudioFrame frame)
    {
        using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (IMemoryBufferReference reference = buffer.CreateReference())
        {
            byte* dataInBytes;
            uint capacityInBytes;

            // Get the buffer from the AudioFrame
            (reference.As<IMemoryBufferByteAccess>()).GetBuffer(out dataInBytes, out capacityInBytes);
            var bytes = new byte[capacityInBytes];
            Marshal.Copy((IntPtr)dataInBytes, bytes, 0, (int)capacityInBytes);
            return bytes.AsBuffer();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            Stop();

            if (disposing)
            {
                try
                {
                    _stopwatch?.Stop();
                    _audioGraph?.Dispose();
                    _loopingAudioStream?.Dispose();
                    _wasapiLoopbackCapture?.Dispose();
                }
                catch (Exception)
                {
                }
            }

            _audioGraph = null;
            _loopingAudioStream = null;
            _deviceInputNode = null;
            _loopbackInputNode = null;
            _frameOutputNode = null;
            _audioFileInputNode = null;
            _wasapiLoopbackCapture = null;
            _stopwatch = null;
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
