using ImGuiNET;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.DebugUI.Client;

/// <summary>Owns a separate CJK atlas without rebuilding VSImGui's shared font atlas.</summary>
internal sealed class DebugFont : IDisposable
{
    private ImFontAtlasPtr atlas;
    private ImFontPtr font;
    private int texture;
    private bool attempted;
    internal bool Available { get; private set; }

    public bool Push(ILogger logger)
    {
        if (Lang.CurrentLocale?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) != true) return false;
        if (!attempted)
        {
            attempted = true;
            try { Load(); }
            catch (Exception ex)
            {
                logger.Warning("[vsslssmod] CJK font unavailable; using the current ImGui font. Set VSSLSSMOD_FONT to a CJK font path. {0}", ex.Message);
                Dispose();
            }
        }
        if (!Available) return false;
        ImGui.PushFont(font);
        return true;
    }

    private unsafe void Load()
    {
        var configured = Environment.GetEnvironmentVariable("VSSLSSMOD_FONT");
        string[] candidates = [
            configured ?? string.Empty,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simhei.ttf"),
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
            "/System/Library/Fonts/PingFang.ttc"
        ];
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No installed CJK font was found.");
        atlas = new ImFontAtlasPtr(ImGuiNative.ImFontAtlas_ImFontAtlas());
        font = atlas.AddFontFromFileTTF(path, 18, new ImFontConfigPtr(), atlas.GetGlyphRangesChineseFull());
        atlas.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        if (font.FindGlyphNoFallback('汉').NativePtr == null)
            throw new InvalidDataException("The selected font does not contain Chinese glyphs.");

        int previousActive = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        int previousAlignment = GL.GetInteger(GetPName.UnpackAlignment);
        int previousRowLength = GL.GetInteger(GetPName.UnpackRowLength);
        int previousUnpackBuffer = GL.GetInteger(GetPName.PixelUnpackBufferBinding);
        try
        {
            if (width > GL.GetInteger(GetPName.MaxTextureSize) || height > GL.GetInteger(GetPName.MaxTextureSize))
                throw new InvalidOperationException("Font atlas exceeds GPU texture size limit.");
            texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            atlas.SetTexID(texture);
            atlas.ClearTexData();
            Available = true;
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, previousAlignment);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, previousRowLength);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, previousUnpackBuffer);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.ActiveTexture((TextureUnit)previousActive);
        }
    }

    public unsafe void Dispose()
    {
        if (texture != 0) { GL.DeleteTexture(texture); texture = 0; }
        if (atlas.NativePtr != null) { atlas.Destroy(); atlas = default; }
        Available = false;
    }
}
