// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using VoiceRecording;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using WinRT.Interop;

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
            D3D11.D3D11CreateDevice(
                null,
                useWARP ? DriverType.Software : DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_11_1 },
                out var d3dDevice);
            IDirect3DDevice device = null;

            // Acquire the DXGI interface for the Direct3D device.
            using (var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice3>())
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

        internal static IDirect3DSurface CreateDirect3DSurfaceFromSharpDXTexture(ID3D11Texture2D texture)
        {
            IDirect3DSurface surface = null;

            // Acquire the DXGI interface for the Direct3D surface.
            using (var dxgiSurface = texture.QueryInterface<IDXGISurface>())
            {
                // Wrap the native device using a WinRT interop object.
                uint hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out IntPtr pUnknown);

                if (hr == 0)
                {
                    surface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(pUnknown);
                }
            }

            return surface;
        }

        internal static ID3D11Device CreateSharpDXDevice(IDirect3DDevice device)
        {
            var access = device.As<IDirect3DDxgiInterfaceAccess>();
            var d3dPointer = access.GetInterface(ID3D11Device);
            var d3dDevice = new ID3D11Device(d3dPointer);
            return d3dDevice;
        }

        internal static ID3D11Texture2D CreateSharpDXTexture2D(IDirect3DSurface surface)
        {
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            var d3dPointer = access.GetInterface(ID3D11Texture2D);
            var d3dSurface = new ID3D11Texture2D(d3dPointer);
            return d3dSurface;
        }
    }
}
