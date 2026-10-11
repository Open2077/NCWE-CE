// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;

namespace NCWE.Render;

public static class Gizmo
{
    [DllImport("ncwe-gizmo",EntryPoint="ncwe_gizmo_version",CallingConvention=CallingConvention.Cdecl)] private static extern nint NativeVersion();
    public static string Version => Marshal.PtrToStringUTF8(NativeVersion())!;
    [DllImport("ncwe-gizmo",EntryPoint="ncwe_gizmo_begin",CallingConvention=CallingConvention.Cdecl)] public static extern void Begin(nint context);
    [DllImport("ncwe-gizmo",EntryPoint="ncwe_gizmo_manipulate",CallingConvention=CallingConvention.Cdecl)]
    public static extern int Manipulate(in Matrix4x4 view,in Matrix4x4 projection,ref Matrix4x4 model,float x,float y,float w,float h,int operation);
    // mode: 0 local, 1 world. snap: per-axis steps (rotation in degrees), or null.
    [DllImport("ncwe-gizmo",EntryPoint="ncwe_gizmo_manipulate_ex",CallingConvention=CallingConvention.Cdecl)]
    private static extern unsafe int ManipulateEx(in Matrix4x4 view,in Matrix4x4 projection,ref Matrix4x4 model,float x,float y,float w,float h,int operation,int mode,float* snap,float size);
    public static unsafe int Manipulate(in Matrix4x4 view,in Matrix4x4 projection,ref Matrix4x4 model,float x,float y,float w,float h,int operation,bool world,float? snap,float size=0)
    {
        var steps=stackalloc float[3]; if(snap is { } s){steps[0]=steps[1]=steps[2]=s;}
        return ManipulateEx(view,projection,ref model,x,y,w,h,operation,world?1:0,snap is null?null:steps,size);
    }
    [DllImport("ncwe-gizmo",EntryPoint="ncwe_gizmo_using",CallingConvention=CallingConvention.Cdecl)] public static extern int IsUsing();
    [DllImport("ncwe-gizmo",EntryPoint="ncwe_gizmo_over",CallingConvention=CallingConvention.Cdecl)] public static extern int IsOver();
}
