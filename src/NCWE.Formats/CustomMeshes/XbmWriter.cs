// SPDX-License-Identifier: GPL-3.0-only
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats.CustomMeshes;

public enum TextureKind
{
    Color,        // sRGB colour without transparency: BC1
    ColorAlpha,   // sRGB colour with transparency: BC7
    Normal,       // tangent-space normal map (X, Y): BC5
    Gray          // one linear channel (roughness, metalness): BC4
}

// Game textures (CBitmapTexture .xbm) written in managed code: block compression by BCnEncoder,
// the full mip chain, and the same header WolvenKit's importer writes (mips in DDS order, the
// DDS header left out, version 2). Like the game's own files (and WolvenKit's import), the image
// is stored upside down: meshes address it with V = 1 - v, which ModTools.ImportMesh writes.
public static class XbmWriter
{
    public static byte[] Write(RgbaImage image, TextureKind kind, CompressionQuality quality = CompressionQuality.Balanced) => CustomCr2w.Write(Build(image, kind, quality));

    public static CBitmapTexture Build(RgbaImage image, TextureKind kind, CompressionQuality quality = CompressionQuality.Balanced)
    {
        if (image.Width % 4 != 0 || image.Height % 4 != 0) throw new ArgumentException("Texture sides must be multiples of 4.");
        var (format, compression, group, gamma, blockBytes) = kind switch
        {
            TextureKind.Color => (CompressionFormat.Bc1, Enums.ETextureCompression.TCM_DXTNoAlpha, Enums.GpuWrapApieTextureGroup.TEXG_Generic_Color, true, 8),
            TextureKind.ColorAlpha => (CompressionFormat.Bc7, Enums.ETextureCompression.TCM_QualityColor, Enums.GpuWrapApieTextureGroup.TEXG_Generic_Color, true, 16),
            TextureKind.Normal => (CompressionFormat.Bc5, Enums.ETextureCompression.TCM_Normalmap, Enums.GpuWrapApieTextureGroup.TEXG_Generic_Normal, false, 16),
            _ => (CompressionFormat.Bc4, Enums.ETextureCompression.TCM_QualityR, Enums.GpuWrapApieTextureGroup.TEXG_Generic_Grayscale, false, 8)
        };
        var encoder = new BcEncoder(format);
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.MaxMipMapLevel = -1;
        encoder.OutputOptions.Quality = quality;
        var mips = encoder.EncodeToRawBytes(TextureFiles.FlipRows(image).Pixels, image.Width, image.Height, PixelFormat.Rgba32);

        var blob = new rendRenderTextureBlobPC();
        var header = blob.Header;
        header.Version = 2; header.Flags = 1;
        header.SizeInfo.Width = (ushort)image.Width; header.SizeInfo.Height = (ushort)image.Height; header.SizeInfo.Depth = 1;
        header.TextureInfo.Type = Enums.GpuWrapApieTextureType.TEXTYPE_2D;
        header.TextureInfo.DataAlignment = 8;
        header.TextureInfo.MipCount = (byte)mips.Length;
        header.TextureInfo.SliceCount = 1;
        uint offset = 0;
        for (int level = 0; level < mips.Length; level++)
        {
            int w = Math.Max(1, image.Width >> level);
            uint rowPitch = (uint)(Math.Max(1, (w + 3) / 4) * blockBytes);
            var info = new rendRenderTextureBlobMipMapInfo();
            info.Layout.RowPitch = rowPitch;
            info.Layout.SlicePitch = (uint)mips[level].Length;
            info.Placement.Offset = offset;
            info.Placement.Size = (uint)mips[level].Length;
            header.MipMapInfo.Add(info);
            offset += (uint)mips[level].Length;
        }
        header.TextureInfo.TextureDataSize = offset;
        header.TextureInfo.SliceSize = offset;
        var data = new byte[offset];
        int at = 0;
        foreach (var mip in mips) { mip.CopyTo(data, at); at += mip.Length; }
        var buffer = new SerializationDeferredDataBuffer(data);
        buffer.Buffer.Flags = 131072;
        blob.TextureData = buffer;

        var texture = new CBitmapTexture
        {
            CookingPlatform = Enums.ECookingPlatform.PLATFORM_PC,
            Width = (uint)image.Width, Height = (uint)image.Height, Depth = 1,
            HistBiasMulCoef = new Vector3 { X = 1, Y = 1, Z = 1 }, HistBiasAddCoef = new Vector3 { X = 0, Y = 0, Z = 0 }
        };
        texture.Setup.Group = group;
        texture.Setup.RawFormat = kind == TextureKind.Gray ? Enums.ETextureRawFormat.TRF_Grayscale : Enums.ETextureRawFormat.TRF_TrueColor;
        texture.Setup.Compression = compression;
        texture.Setup.IsGamma = gamma;
        texture.Setup.IsStreamable = true;
        texture.Setup.HasMipchain = mips.Length > 1;
        texture.Setup.AllowTextureDowngrade = true;
        texture.RenderTextureResource.RenderResourceBlobPC = new CHandle<IRenderResourceBlob>(blob);
        return texture;
    }
}

public static class XbmReader
{
    // RGBA8 of the largest mip no bigger than maximum, decoded like the game's own textures and
    // turned the right way up (top row first, as the image was imported).
    public static RgbaImage? Decode(byte[] xbm, int maximum = 4096)
    {
        using var file = CustomCr2w.Read(xbm);
        return file.RootChunk is CBitmapTexture texture ? Decode(texture, maximum) : null;
    }

    // Block-compressed textures (BC1/3/4/5/7) and RGBA8 / R8 ones, decoded by BCnEncoder.
    public static RgbaImage? Decode(CBitmapTexture texture, int maximum = 4096)
    {
        if (texture.RenderTextureResource.RenderResourceBlobPC.Chunk is not rendRenderTextureBlobPC blob) return null;
        var header = blob.Header;
        int width = header.SizeInfo.Width, height = header.SizeInfo.Height, mips = header.MipMapInfo.Count;
        if (width <= 0 || height <= 0 || mips == 0) return null;
        int level = 0;
        while (level + 1 < mips && Math.Max(width >> level, height >> level) > maximum) level++;
        int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
        var mip = header.MipMapInfo[level];
        var data = blob.TextureData.Buffer.GetBytes().AsMemory((int)(uint)mip.Placement.Offset, (int)(uint)mip.Placement.Size);
        CompressionFormat? format = (Enums.ETextureCompression)texture.Setup.Compression switch
        {
            Enums.ETextureCompression.TCM_DXTNoAlpha => CompressionFormat.Bc1,
            Enums.ETextureCompression.TCM_DXTAlpha or Enums.ETextureCompression.TCM_DXTAlphaLinear => CompressionFormat.Bc3,
            Enums.ETextureCompression.TCM_QualityR => CompressionFormat.Bc4,
            Enums.ETextureCompression.TCM_Normalmap or Enums.ETextureCompression.TCM_QualityRG or Enums.ETextureCompression.TCM_Normals => CompressionFormat.Bc5,
            Enums.ETextureCompression.TCM_QualityColor => CompressionFormat.Bc7,
            Enums.ETextureCompression.TCM_None => null,
            _ => throw new NotSupportedException("Texture compression not supported: " + texture.Setup.Compression)
        };
        var pixels = new byte[w * h * 4];
        if (format is { } bc)
        {
            var decoded = new BCnEncoder.Decoder.BcDecoder().DecodeRaw(new MemoryStream(data.ToArray()), w, h, bc);
            for (int i = 0; i < decoded.Length && i * 4 + 3 < pixels.Length; i++) { pixels[i * 4] = decoded[i].r; pixels[i * 4 + 1] = decoded[i].g; pixels[i * 4 + 2] = decoded[i].b; pixels[i * 4 + 3] = decoded[i].a; }
        }
        else if ((Enums.ETextureRawFormat)texture.Setup.RawFormat == Enums.ETextureRawFormat.TRF_Grayscale)
            for (int i = 0; i < w * h; i++) { pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = data.Span[i]; pixels[i * 4 + 3] = 255; }
        else data.Span[..pixels.Length].CopyTo(pixels);
        return TextureFiles.FlipRows(new RgbaImage(w, h, pixels));
    }
}

internal static class CustomCr2w
{
    public static byte[] Write(RedBaseClass root)
    {
        var file = new CR2WFile { RootChunk = root };
        using var stream = new MemoryStream();
        using (var writer = new CR2WWriter(stream, System.Text.Encoding.UTF8, true)) writer.WriteFile(file);
        return stream.ToArray();
    }

    public static CR2WFile Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new CR2WReader(stream);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException("A generated resource could not be read back.");
        return file;
    }
}

// CR2W reading for probes and tools outside this assembly.
public static class CustomCr2wPublic
{
    public static CR2WFile Read(byte[] bytes) => CustomCr2w.Read(bytes);
}
