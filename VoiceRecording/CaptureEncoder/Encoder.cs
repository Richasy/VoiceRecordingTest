// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
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
            _audioCapture = new AudioCapture();

            CreateMediaObjects();
        }

        public IAsyncAction EncodeAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate)
        {
            return EncodeInternalAsync(stream, width, height, bitrateInBps, frameRate).AsAsyncAction();
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
                    encodingProfile.Container.Subtype = "MPEG4";
                    encodingProfile.Video.Subtype = "H264";
                    encodingProfile.Video.Width = width;
                    encodingProfile.Video.Height = height;
                    encodingProfile.Video.Bitrate = bitrateInBps;
                    encodingProfile.Video.FrameRate.Numerator = frameRate;
                    encodingProfile.Video.FrameRate.Denominator = 1;
                    encodingProfile.Video.PixelAspectRatio.Numerator = 1;
                    encodingProfile.Video.PixelAspectRatio.Denominator = 1;
                    encodingProfile.SetAudioTracks(new[] { new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(44100, 2, 16)) });

                    var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, stream, encodingProfile);
                    await _audioCapture.InitializeAsync();

                    await transcode.TranscodeAsync();
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

            if (!_isRecording)
            {
                DisposeInternal();
            }

            _isRecording = false;
        }

        private void DisposeInternal()
        {
            _audioCapture.Stop();
            _frameGenerator.Dispose();
        }

        private void CreateMediaObjects()
        {
            // Create our encoding profile based on the size of the item
            int width = _captureItem.Size.Width;
            int height = _captureItem.Size.Height;

            // Describe our input: uncompressed BGRA8 buffers
            var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
            var audioProperties = AudioEncodingProperties.CreatePcm(44100, 2, 16);
            _videoDescriptor = new VideoStreamDescriptor(videoProperties);
            _audioDescriptor = new AudioStreamDescriptor(audioProperties);

            // Create our MediaStreamSource
            _mediaStreamSource = new MediaStreamSource(_videoDescriptor, _audioDescriptor);
            _mediaStreamSource.BufferTime = TimeSpan.FromSeconds(0);
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SwitchStreamsRequested += OnMediaStreamSourceSwitchStreamsRequested;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;

            // Create our transcoder
            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = true;
        }

        private void OnMediaStreamSourceSwitchStreamsRequested(MediaStreamSource sender, MediaStreamSourceSwitchStreamsRequestedEventArgs args)
        {
            _isVideoStreaming = args.Request.NewStreamDescriptor is VideoStreamDescriptor;
        }

        private void OnMediaStreamSourceSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_isRecording && !_closed)
            {
                try
                {
                    if (_isVideoStreaming)
                    {
                        var frame = _audioCapture.GetAudioFrame();
                        if (frame == null)
                        {
                            args.Request.Sample = null;
                            return;
                        }

                        var timeStamp = frame.SystemRelativeTime.Value;
                        var buffer = _audioCapture.ConvertFrameToBuffer(frame);
                        if (buffer == null)
                        {
                            return;
                        }

                        var sample = MediaStreamSample.CreateFromBuffer(buffer, timeStamp);
                        args.Request.Sample = sample;
                        return;
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

                            var timeStamp = frame.SystemRelativeTime;

                            var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);
                            args.Request.Sample = sample;
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

        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            using (var frame = _frameGenerator.WaitForNewFrame())
            {
                args.Request.SetActualStartPosition(frame.SystemRelativeTime);
                _audioCapture.Start();
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
        private bool _isRecording;
        private bool _isVideoStreaming;
        private bool _closed = false;
    }
}
