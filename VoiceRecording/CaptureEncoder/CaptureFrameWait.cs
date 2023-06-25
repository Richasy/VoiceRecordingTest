// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace CaptureEncoder
{
    public sealed class SurfaceWithInfo : IDisposable
    {
        public IDirect3DSurface Surface { get; internal set; }
        public TimeSpan SystemRelativeTime { get; internal set; }

        public void Dispose()
        {
            Surface?.Dispose();
            Surface = null;
        }
    }

    class MultithreadLock : IDisposable
    {
        public MultithreadLock(ID3D11Multithread multithread)
        {
            _multithread = multithread;
            _multithread?.Enter();
        }

        public void Dispose()
        {
            _multithread?.Leave();
            _multithread = null;
        }

        private ID3D11Multithread _multithread;
    }

    public sealed class CaptureFrameWait : IDisposable
    {
        public CaptureFrameWait(
            IDirect3DDevice device,
            GraphicsCaptureItem item,
            SizeInt32 size)
        {
            _device = device;
            _d3dDevice = Direct3D11Helpers.CreateSharpDXDevice(device);
            _multithread = _d3dDevice.QueryInterface<ID3D11Multithread>();
            _multithread.SetMultithreadProtected(true);
            _item = item;
            _frameEvent = new ManualResetEvent(false);
            _closedEvent = new ManualResetEvent(false);
            _events = new[] { _closedEvent, _frameEvent };

            InitializeBlankTexture(size);
            InitializeCapture(size);
        }

        private void InitializeCapture(SizeInt32 size)
        {
            _item.Closed += OnClosed;
            _framePool = Direct3D11CaptureFramePool.Create(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                size);
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(_item);
            _session.StartCapture();
        }

        private void InitializeBlankTexture(SizeInt32 size)
        {
            var description = new Texture2DDescription
            {
                Width = size.Width,
                Height = size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription()
                {
                    Count = 1,
                    Quality = 0
                },
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            _blankTexture = _d3dDevice.CreateTexture2D(description);

            using (var renderTargetView = _d3dDevice.CreateRenderTargetView(_blankTexture))
            {
                _d3dDevice.ImmediateContext.ClearRenderTargetView(renderTargetView, new Vortice.Mathematics.Color4(0, 0, 0, 1));
            }
        }

        private void SetResult(Direct3D11CaptureFrame frame)
        {
            _currentFrame = frame;
            _frameEvent.Set();
        }

        private void Stop()
        {
            _closedEvent.Set();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            SetResult(sender.TryGetNextFrame());
        }

        private void OnClosed(GraphicsCaptureItem sender, object args)
        {
            Stop();
        }

        private void Cleanup()
        {
            _framePool?.Dispose();
            _session?.Dispose();
            if (_item != null)
            {
                _item.Closed -= OnClosed;
            }
            _item = null;
            _device = null;
            _d3dDevice = null;
            _blankTexture?.Dispose();
            _blankTexture = null;
            _currentFrame?.Dispose();
        }

        public SurfaceWithInfo WaitForNewFrame()
        {
            // Let's get a fresh one.
            _currentFrame?.Dispose();
            _frameEvent.Reset();

            var signaledEvent = _events[WaitHandle.WaitAny(_events)];
            if (signaledEvent == _closedEvent)
            {
                Cleanup();
                return null;
            }

            var result = new SurfaceWithInfo();
            result.SystemRelativeTime = _currentFrame.SystemRelativeTime;
            using (var multithreadLock = new MultithreadLock(_multithread))
            using (var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(_currentFrame.Surface))
            {
                var description = sourceTexture.Description;
                description.Usage = ResourceUsage.Default;
                description.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
                description.CPUAccessFlags = CpuAccessFlags.None;
                description.MiscFlags = ResourceOptionFlags.None;

                using (var copyTexture = _d3dDevice.CreateTexture2D(description))
                {
                    var width = Math.Clamp(_currentFrame.ContentSize.Width, 0, _currentFrame.Surface.Description.Width);
                    var height = Math.Clamp(_currentFrame.ContentSize.Height, 0, _currentFrame.Surface.Description.Height);

                    var region = new Vortice.Mathematics.Box(0, 0, 0, width, height, 1);

                    _d3dDevice.ImmediateContext.CopyResource(_blankTexture, copyTexture);
                    _d3dDevice.ImmediateContext.CopySubresourceRegion(copyTexture, 0, 0, 0, 0, sourceTexture, 0, region);
                    result.Surface = Direct3D11Helpers.CreateDirect3DSurfaceFromSharpDXTexture(copyTexture);
                }
            }

            return result;
        }

        public void Dispose()
        {
            Stop();
            Cleanup();
        }

        private IDirect3DDevice _device;
        private ID3D11Device _d3dDevice;
        private ID3D11Multithread _multithread;
        private ID3D11Texture2D _blankTexture;

        private ManualResetEvent[] _events;
        private ManualResetEvent _frameEvent;
        private ManualResetEvent _closedEvent;
        private Direct3D11CaptureFrame _currentFrame;

        private GraphicsCaptureItem _item;
        private GraphicsCaptureSession _session;
        private Direct3D11CaptureFramePool _framePool;
    }
}
