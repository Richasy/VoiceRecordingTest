// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VoiceRecording.CaptureEncoder;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace CaptureEncoder
{
    public sealed class Encoder : IDisposable
    {
        public Encoder(IDirect3DDevice device, GraphicsCaptureItem item)
        {
            _device = device;
            _captureItem = item;
            _isRecording = false;

            CreateMediaObjects();
        }

        public IAsyncAction EncodeAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate)
        {
            return EncodeInternalAsync(stream, width, height, bitrateInBps, frameRate).AsAsyncAction();
        }

        public void ChangeMicMute(bool mute)
        {
            if (mute)
            {
                _audioCapture.MuteDeviceInput();
            }
            else
            {
                _audioCapture.UnmuteDeviceInput();
            }
        }

        private async Task EncodeInternalAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate)
        {
            if (!_isRecording)
            {
                _isRecording = true;

                _frameGenerator = new CaptureFrameWait(
                    _device,
                    _captureItem,
                    _captureItem.Size);

                using (_frameGenerator)
                {
                    var encodingProfile = new MediaEncodingProfile();
                    encodingProfile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
                    encodingProfile.Video.Subtype = MediaEncodingSubtypes.H264;
                    encodingProfile.Video.Width = width;
                    encodingProfile.Video.Height = height;
                    encodingProfile.Video.Bitrate = bitrateInBps;
                    encodingProfile.Video.FrameRate.Numerator = frameRate;
                    encodingProfile.Video.FrameRate.Denominator = 1;
                    encodingProfile.Video.PixelAspectRatio.Numerator = 1;
                    encodingProfile.Video.PixelAspectRatio.Denominator = 1;
                    encodingProfile.Audio = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.Auto).Audio;

                    if (_audioCapture == null)
                    {
                        _audioCapture = new AudioCapture();
                        await _audioCapture.InitializeAsync();
                    }

                    _audioDescriptor = new AudioStreamDescriptor(_audioCapture.GetEncodingProeprties());
                    _mediaStreamSource.AddStreamDescriptor(_audioDescriptor);
                    var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, stream, encodingProfile);
                    if (transcode.CanTranscode)
                    {
                        _audioCapture?.Start();
                        await transcode.TranscodeAsync();
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;

            DisposeInternal();
            _isRecording = false;
        }

        private void DisposeInternal()
        {
            _frameGenerator?.Dispose();
            _audioCapture?.Dispose();
        }

        private void CreateMediaObjects()
        {
            // Create our encoding profile based on the size of the item
            int width = _captureItem.Size.Width;
            int height = _captureItem.Size.Height;

            // Describe our input: uncompressed BGRA8 buffers
            var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
            _videoDescriptor = new VideoStreamDescriptor(videoProperties);

            // Create our MediaStreamSource
            _mediaStreamSource = new MediaStreamSource(_videoDescriptor);
            _mediaStreamSource.BufferTime = TimeSpan.Zero;
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;

            // Create our transcoder
            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = true;
        }

        private async void OnMediaStreamSourceSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_isRecording && !_closed)
            {
                try
                {
                    _isVideoStreaming = args.Request.StreamDescriptor is VideoStreamDescriptor;
                    if (!_isVideoStreaming)
                    {
                        var def = args.Request.GetDeferral();

                        var frame = _audioCapture.GetAudioFrame();
                        var count = 0;
                        while (count++ <= 5 && (frame == null || frame.Duration.GetValueOrDefault().TotalSeconds == 0))
                        {
                            await Task.Delay(10);
                            frame = _audioCapture.GetAudioFrame();
                        }
                        if (count >= 5)
                        {
                            OutputDebugString("No audio frame");
                            args.Request.Sample = null;
                            return;
                        }

                        var buffer = _audioCapture.ConvertFrameToBuffer(frame);
                        if (buffer == null)
                        {
                            OutputDebugString("No audio buffer");
                            return;
                        }

                        var timeStamp = frame.RelativeTime.GetValueOrDefault();
                        var sample = MediaStreamSample.CreateFromBuffer(buffer, timeStamp);
                        sample.Duration = frame.Duration.GetValueOrDefault();
                        sample.KeyFrame = true;
                        args.Request.Sample = sample;
                        OutputDebugString($"Audio frame {sample.Timestamp} {sample.Duration}");
                        def.Complete();
                    }
                    else
                    {
                        using (var frame = _frameGenerator.WaitForNewFrame())
                        {
                            if (frame == null)
                            {
                                args.Request.Sample = null;
                                DisposeInternal();
                                return;
                            }

                            var timeStamp = frame.SystemRelativeTime - _timeOffset;

                            var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);
                            args.Request.Sample = sample;

                            OutputDebugString($"Video frame {sample.Timestamp} {sample.Duration}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    Debug.WriteLine(e.StackTrace);
                    Debug.WriteLine(e);
                    args.Request.Sample = null;
                    DisposeInternal();
                }
            }
            else
            {
                args.Request.Sample = null;
                DisposeInternal();
            }
        }

        public static string output = "";
        public static void OutputDebugString(string s)
        {
            output += s;
            //Debug.WriteLine(s);
        }

        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            try
            {
                using (var frame = _frameGenerator.WaitForNewFrame())
                {
                    // args.Request.SetActualStartPosition(frame.SystemRelativeTime);
                    _timeOffset = frame.SystemRelativeTime;
                }

                if (_audioCapture != null)
                {
                    using var frame = _audioCapture.GetAudioFrame();
                    _timeOffset += frame.RelativeTime.GetValueOrDefault();
                }
            }
            catch (Exception)
            {
                DisposeInternal();
            }
        }

        private IDirect3DDevice _device;

        private GraphicsCaptureItem _captureItem;
        private CaptureFrameWait _frameGenerator;
        private AudioCapture _audioCapture;

        private VideoStreamDescriptor _videoDescriptor;
        private AudioStreamDescriptor _audioDescriptor;
        private MediaStreamSource _mediaStreamSource;
        private MediaTranscoder _transcoder;
        private TimeSpan _timeOffset = default;
        private bool _isRecording;
        private bool _isVideoStreaming;
        private bool _closed = false;
    }
}
