// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace NCWE.Studio.Viewport;

// ISwapChainPanelNative::SetSwapChain (microsoft.ui.xaml.media.dxinterop.h), on the panel's UI
// thread. A panel lets go of its swap chain (null) when the view stops (disposal, a preview that
// failed, a lost device): WinUI must never keep composing the swap chain of a released or lost
// Direct3D device.
internal static class SwapChainPanels
{
    private static readonly Guid NativeIid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static unsafe void Set(SwapChainPanel panel, nint swapChain)
    {
        var unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in NativeIid, out var native));
        try
        {
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(*(void***)native + 3));
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally { Marshal.Release(native); }
    }

    // Detaches without ever throwing (teardown and failure paths).
    public static void Detach(SwapChainPanel panel)
    {
        try { Set(panel, 0); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.TraceLine("Detaching a swap chain failed: " + ex.Message); }
    }
}
