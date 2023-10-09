// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Media.Imaging;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using NAudio.CoreAudioApi;
using static SharpDX.Utilities;
using System.IO;

namespace CaptureEncoder
{
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    };

    public static class Direct3D11Helpers
    {
        internal static Guid IInspectable = new Guid("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");
        internal static Guid ID3D11Resource = new Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d");
        internal static Guid IDXGIAdapter3 = new Guid("645967A4-1392-4310-A798-8053CE3E93FD");
        internal static Guid ID3D11Device = new Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140");
        internal static Guid ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport(
            "d3d11.dll",
            EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
            SetLastError = true,
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall
            )]
        internal static extern UInt32 CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport(
            "d3d11.dll",
            EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface",
            SetLastError = true,
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall
            )]
        internal static extern UInt32 CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

        public static IDirect3DDevice CreateDevice()
        {
            return CreateDevice(false);
        }

        public static IDirect3DDevice CreateDevice(bool useWARP)
        {
            var d3dDevice = new SharpDX.Direct3D11.Device(
                useWARP ? SharpDX.Direct3D.DriverType.Software : SharpDX.Direct3D.DriverType.Hardware,
                SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);
            IDirect3DDevice device = null;

            // Acquire the DXGI interface for the Direct3D device.
            using (var dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device3>())
            {
                // Wrap the native device using a WinRT interop object.
                uint hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pUnknown);

                if (hr == 0)
                {
                    device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pUnknown);
                }
            }

            return device;
        }

        internal static (IDirect3DSurface, Bitmap) CreateDirect3DSurfaceFromSharpDXTexture(SharpDX.Direct3D11.Texture2D texture)
        {
            IDirect3DSurface surface = null;
            Bitmap bitmap = null;

            // Acquire the DXGI interface for the Direct3D surface.
            using (var dxgiSurface = texture.QueryInterface<SharpDX.DXGI.Surface>())
            {
                // Wrap the native device using a WinRT interop object.
                uint hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out IntPtr pUnknown);
                if (hr == 0)
                {
                    surface = MarshalInterface<IDirect3DSurface>.FromAbi(pUnknown);

                    var desc = texture.Description;
                    // 创建一个StagingTexture
                    var stagingTexture = new Texture2D(
                        texture.Device,
                        new Texture2DDescription
                        {
                            Width = desc.Width,
                            Height = desc.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = desc.Format,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            BindFlags = BindFlags.None,
                            CpuAccessFlags = CpuAccessFlags.Read,
                            OptionFlags = ResourceOptionFlags.None
                        });

                    // 将Texture2D复制到StagingTexture
                    texture.Device.ImmediateContext.CopyResource(texture, stagingTexture);

                    bitmap = new Bitmap(stagingTexture.Description.Width, stagingTexture.Description.Height, PixelFormat.Format32bppArgb);
                    var boundsRect = new Rectangle(0, 0, stagingTexture.Description.Width, stagingTexture.Description.Height);
                    var mapSource = stagingTexture.Device.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

                    // Copy pixels from screen capture Texture to GDI bitmap
                    var mapDest = bitmap.LockBits(boundsRect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

                    var sourcePtr = mapSource.DataPointer;
                    var destPtr = mapDest.Scan0;
                    for (int y = 0; y < stagingTexture.Description.Height; y++)
                    {
                        // Copy a single line 
                        Utilities.CopyMemory(destPtr, sourcePtr, stagingTexture.Description.Width * 4);

                        // Advance pointers
                        sourcePtr = IntPtr.Add(sourcePtr, mapSource.RowPitch);
                        destPtr = IntPtr.Add(destPtr, mapDest.Stride);
                    }

                    // Release source and dest locks
                    bitmap.UnlockBits(mapDest);
                    stagingTexture.Device.ImmediateContext.UnmapSubresource(stagingTexture, 0);

                    using var ms = new MemoryStream();
                    bitmap.Save(ms, ImageFormat.Png);

                    Marshal.Release(pUnknown);
                }
            }


            return (surface, bitmap);
        }

        internal static SharpDX.Direct3D11.Device CreateSharpDXDevice(IDirect3DDevice device)
        {
            var access = device.As<IDirect3DDxgiInterfaceAccess>();
            var d3dPointer = access.GetInterface(ID3D11Device);
            var d3dDevice = new SharpDX.Direct3D11.Device(d3dPointer);
            return d3dDevice;
        }

        internal static SharpDX.Direct3D11.Texture2D CreateSharpDXTexture2D(IDirect3DSurface surface)
        {
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            var d3dPointer = access.GetInterface(ID3D11Texture2D);
            var d3dSurface = new SharpDX.Direct3D11.Texture2D(d3dPointer);
            return d3dSurface;
        }
    }
}
