using Cairo;
using System.Globalization;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Seraph.Network;

namespace Seraph.Client;

/// <summary>Dimming/eyelids use an alpha overlay; defocus uses two half-resolution Gaussian passes.</summary>
internal sealed class SymptomPostProcess : IDisposable
{
    private readonly ICoreClientAPI api;
    private IShaderProgram? shader;
    private MeshRef? quad;
    private int scene, width, height, sceneWidth, sceneHeight;
    private readonly int[] viewport = new int[4];
    private readonly int[] previousBox = new int[4];
    private readonly bool[] colorWrite = new bool[4];
    private readonly double[] depthRange = new double[2];
    private readonly int[] blurTextures = new int[2], blurBuffers = new int[2];
    private int blurWidth, blurHeight;
    private bool failed;
    private string noticeText = "";
    private double noticeScale;
    private readonly List<LoadedTexture> noticeGlyphs = [];
    private NativeProgressDisplay? restDisplay;
    internal SymptomPostProcess(ICoreClientAPI api) { this.api = api; api.Event.ReloadShader += Reload; }

    internal bool Render(double time, double blur, double darkness, double closure, double severity, double red, double green, double blue, double night, ScreenRegion[] protectedMenus, double dozing = 0, ServerSnapshot? rest = null, double centerDarkness = 0, double upperLid = 0, double ghost = 0)
    {
        // Full closure and pure dimming never copy the scene, including with F4.
        if (rest is null || protectedMenus.Length > 0) ClearRest();
        if (failed) { CoverEyes(closure, protectedMenus, upperLid); return false; }
        var copyScene = NeedsSceneCopy(blur, closure, severity, night);
        var previousShader = api.Render.CurrentActiveShader;
        var depth = GL.IsEnabled(EnableCap.DepthTest);
        var blend = GL.IsEnabled(EnableCap.Blend);
        var scissor = GL.IsEnabled(EnableCap.ScissorTest);
        GL.GetInteger(GetPName.ScissorBox, previousBox);
        var cull = GL.IsEnabled(EnableCap.CullFace);
        var stencil = GL.IsEnabled(EnableCap.StencilTest);
        var depthWrite = GL.GetBoolean(GetPName.DepthWritemask);
        GL.GetBoolean(GetPName.ColorWritemask, colorWrite);
        GL.GetDouble(GetPName.DepthRange, depthRange);
        var previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        var blendSrcRgb = GL.GetInteger(GetPName.BlendSrcRgb); var blendDstRgb = GL.GetInteger(GetPName.BlendDstRgb);
        var blendSrcAlpha = GL.GetInteger(GetPName.BlendSrcAlpha); var blendDstAlpha = GL.GetInteger(GetPName.BlendDstAlpha);
        var blendEquationRgb = GL.GetInteger(GetPName.BlendEquationRgb); var blendEquationAlpha = GL.GetInteger(GetPName.BlendEquationAlpha);
        var previousVao = GL.GetInteger(GetPName.VertexArrayBinding);
        var activeTexture = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture1);
        var previousBlurTexture = GL.GetInteger(GetPName.TextureBinding2D);
        GL.ActiveTexture(TextureUnit.Texture0);
        var previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        var readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        var readBuffer = GL.GetInteger(GetPName.ReadBuffer);
        var drawFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
        try
        {
            GL.GetInteger(GetPName.Viewport, viewport);
            if (viewport[2] <= 0 || viewport[3] <= 0) return false;
            width = viewport[2]; height = viewport[3];
            if (copyScene)
            {
                // Copy from the framebuffer actually being rendered, including the launcher's offscreen target.
                // Never sample the render target itself: the private copy avoids framebuffer feedback.
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, drawFramebuffer);
                GL.ReadBuffer(drawFramebuffer == 0 ? ReadBufferMode.Back : ReadBufferMode.ColorAttachment0);
                if (scene == 0) scene = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, scene);
                if (sceneWidth != width || sceneHeight != height)
                {
                    sceneWidth = width; sceneHeight = height;
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
                        PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                }
                GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, viewport[0], viewport[1], width, height);
            }
            if (previousShader is { Disposed: false }) previousShader.Stop();
            EnsureResources();
            if (dozing > 0) PrepareNotice();
            GL.Disable(EnableCap.DepthTest);
            if (copyScene) GL.Disable(EnableCap.Blend);
            else
            {
                GL.Enable(EnableCap.Blend); GL.BlendEquation(BlendEquationMode.FuncAdd);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }
            GL.Disable(EnableCap.ScissorTest); GL.Disable(EnableCap.CullFace); GL.Disable(EnableCap.StencilTest);
            GL.DepthMask(false); GL.ColorMask(true, true, true, true);
            shader!.Use();
            shader.Uniform("screenRect", -1f, -1f, 2f, 2f);
            shader.Uniform("noticeMode", 0);
            var defocus = copyScene && blur > 0;
            if (defocus)
            {
                DrawDefocus(time, blur);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFramebuffer);
                GL.Viewport(viewport[0], viewport[1], width, height);
            }
            shader.Uniform("blurPass", 0);
            shader.Uniform("hasBlur", defocus ? 1 : 0);
            shader.BindTexture2D("blurTexture", blurTextures[1], 1);
            shader.Uniform("overlayOnly", copyScene ? 0 : 1);
            shader.Uniform("centerDarkness", (float)Math.Clamp(centerDarkness, 0, .04));
            shader.BindTexture2D("sceneTexture", scene, 0);
            shader.Uniform("pixelSize", 1f / width, 1f / height);
            shader.Uniform("blurStrength", (float)Math.Clamp(blur, 0, 1));
            shader.Uniform("darkness", (float)Math.Clamp(darkness, 0, .4));
            shader.Uniform("closure", (float)closure);
            shader.Uniform("upperLid", (float)Math.Clamp(upperLid, 0, .4));
            shader.Uniform("ghost", (float)Math.Clamp(ghost, 0, .12));
            shader.Uniform("time", (float)(time % 1000));
            shader.Uniform("edgeTint", (float)red, (float)green, (float)blue, (float)severity);
            shader.Uniform("night", (float)night);
            GL.Enable(EnableCap.ScissorTest);
            foreach (var region in SymptomRegions.Outside(width, height, protectedMenus))
            {
                GL.Scissor(viewport[0] + region.X, viewport[1] + height - region.Y - region.Height, region.Width, region.Height);
                api.Render.RenderMesh(quad!);
            }
            if (dozing > 0 && protectedMenus.Length == 0) DrawNotice(time, dozing);
            if (rest is not null && protectedMenus.Length == 0) DrawRest(rest);
            return true;
        }
        catch (Exception ex)
        {
            failed = true;
            api.Logger.Warning("[vsslssmod] Symptom blur unavailable; eyelid fallback retained: {0}", ex.Message);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFramebuffer);
            GL.Viewport(viewport[0], viewport[1], width, height);
            CoverEyes(closure, protectedMenus, upperLid);
            return false;
        }
        finally
        {
            if (shader is { Disposed: false }) shader.Stop();
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFramebuffer);
            GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
            GL.ReadBuffer((ReadBufferMode)readBuffer);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, previousBlurTexture);
            GL.ActiveTexture((TextureUnit)activeTexture);
            if (depth) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
            if (blend) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);
            GL.BlendFuncSeparate((BlendingFactorSrc)blendSrcRgb, (BlendingFactorDest)blendDstRgb,
                (BlendingFactorSrc)blendSrcAlpha, (BlendingFactorDest)blendDstAlpha);
            GL.BlendEquationSeparate((BlendEquationMode)blendEquationRgb, (BlendEquationMode)blendEquationAlpha);
            GL.Scissor(previousBox[0], previousBox[1], previousBox[2], previousBox[3]);
            SetEnabled(EnableCap.ScissorTest, scissor); SetEnabled(EnableCap.CullFace, cull);
            SetEnabled(EnableCap.StencilTest, stencil);
            GL.DepthMask(depthWrite); GL.ColorMask(colorWrite[0], colorWrite[1], colorWrite[2], colorWrite[3]);
            GL.DepthRange(depthRange[0], depthRange[1]);
            GL.BindVertexArray(previousVao);
            if (previousShader is { Disposed: false }) previousShader.Use();
            else if (previousShader is null) GL.UseProgram(previousProgram);
        }
    }

    private static void SetEnabled(EnableCap cap, bool enabled)
    { if (enabled) GL.Enable(cap); else GL.Disable(cap); }

    internal static bool NeedsSceneCopy(double blur, double closure, double severity, double night)
        => closure < 1 && (blur > 0 || severity > 0 || night > 0);

    private void DrawDefocus(double time, double blur)
    {
        var w = Math.Max(1, (width + 1) / 2); var h = Math.Max(1, (height + 1) / 2);
        if (blurTextures[0] == 0)
        { GL.GenTextures(2, blurTextures); GL.GenFramebuffers(2, blurBuffers); }
        if (blurWidth != w || blurHeight != h)
        {
            for (var i = 0; i < 2; i++)
            {
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, blurTextures[i]);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, blurBuffers[i]);
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, blurTextures[i], 0);
                if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete)
                    throw new InvalidOperationException("Defocus framebuffer incomplete.");
            }
            blurWidth = w; blurHeight = h;
        }
        // Slight changes in focal distance, never a warped UV grid or camera movement.
        var radius = (.35 + 3.3 * Math.Clamp(blur, 0, .5)) * height / 1080d
            * (1 + .10 * Math.Sin(time * .8) + .04 * Math.Sin(time * 1.7));
        GL.Disable(EnableCap.Blend); shader!.Uniform("blurPass", 1);
        GL.Viewport(0, 0, w, h);
        for (var i = 0; i < 2; i++)
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, blurBuffers[i]);
            shader.BindTexture2D("sceneTexture", i == 0 ? scene : blurTextures[0], 0);
            shader.Uniform("blurAxis", i == 0 ? (float)(radius / width) : 0, i == 0 ? 0 : (float)(radius / height));
            api.Render.RenderMesh(quad!);
        }
    }

    private void DrawRest(ServerSnapshot state)
    {
        restDisplay ??= ClosedEyeRestDisplay.Create(api);
        var scale = Math.Min(Math.Max(.1, RuntimeEnv.GUIScale), Math.Min(width / 360d, height / 190d));
        var values = ClosedEyeRestDisplay.Values(state);
        var labels = new[] { "stamina", "fatigue", "sleeppressure" }.Select(k => LocalizedText.Get("vsslssmod:metric-" + k)).ToArray();
        var text = values.Select(v => v?.ToString("0.0", CultureInfo.InvariantCulture) + (v.HasValue ? "%" : "—")).ToArray();
        restDisplay.Update(values, labels, text, scale, (width - ClosedEyeRestDisplay.Width * scale) / 2,
            (height - ClosedEyeRestDisplay.Height * scale) / 2, api.World.ElapsedMilliseconds / 1000d);
        shader!.Stop();
        GL.Disable(EnableCap.ScissorTest); GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // Native statbar rendering needs the GUI shader and matrices even when F4 skipped Ortho.
        var gui = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        api.Render.OrthoMode(width, height);
        try { gui.Use(); restDisplay.Render(); }
        finally { gui.Stop(); api.Render.PerspectiveMode(); }
    }

    internal void ClearRest() { restDisplay?.Dispose(); restDisplay = null; }

    private void PrepareNotice()
    {
        var text = Lang.Get("vsslssmod:symptom-dozing-active").ToUpperInvariant();
        var scale = Math.Clamp(RuntimeEnv.GUIScale, .65, 2);
        if (text == noticeText && scale == noticeScale && noticeGlyphs.Count > 0) return;
        ClearNotice();
        using var font = CairoFont.WhiteDetailText().WithFontSize(36).WithWeight(FontWeight.Bold)
            .WithStroke([.08, .08, .1, .9], 1.5);
        var elements = StringInfo.GetTextElementEnumerator(text);
        try
        {
            while (elements.MoveNext())
                noticeGlyphs.Add(api.Gui.TextTexture.GenTextTexture(elements.GetTextElement(), font));
            noticeText = text; noticeScale = scale;
        }
        catch { ClearNotice(); throw; }
    }

    // Pre-closure warning in final composition, independent of F4; it ends when eyelids start closing.
    // Cairo textures are premultiplied and cached; animation changes only quad positions and alpha.
    private void DrawNotice(double time, double strength)
    {
        if (noticeGlyphs.Count == 0) return;
        var total = noticeGlyphs.Sum(g => (double)g.Width);
        var fit = Math.Min(1, width * .8 / Math.Max(1, total));
        var left = (width - total * fit) / 2;
        GL.Disable(EnableCap.ScissorTest); GL.Enable(EnableCap.Blend);
        GL.BlendEquationSeparate(BlendEquationMode.FuncAdd, BlendEquationMode.FuncAdd);
        GL.BlendFuncSeparate(BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha,
            BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        shader!.Uniform("noticeMode", 1);
        shader.Uniform("noticeAlpha", (float)Math.Clamp(strength * 8, 0, 1));
        for (var i = 0; i < noticeGlyphs.Count; i++)
        {
            var glyph = noticeGlyphs[i];
            var w = glyph.Width * fit; var h = glyph.Height * fit;
            var top = height * .33 - h / 2 + Math.Sin(time * 5 - i * .7) * 5 * noticeScale * fit;
            shader.Uniform("screenRect", (float)(left / width * 2 - 1), (float)(1 - (top + h) / height * 2),
                (float)(w / width * 2), (float)(h / height * 2));
            shader.BindTexture2D("sceneTexture", glyph.TextureId, 0);
            api.Render.RenderMesh(quad!);
            left += w;
        }
    }

    private void ClearNotice()
    {
        foreach (var glyph in noticeGlyphs) glyph.Dispose();
        noticeGlyphs.Clear(); noticeText = ""; noticeScale = 0;
    }

    // Opaque eyelid fallback without GUI matrices, textures or any shader. Preserve caller state.
    private void CoverEyes(double closure, ScreenRegion[] protectedMenus, double upperLid = 0)
    {
        if (closure <= 0 && upperLid <= 0) return;
        var scissor = GL.IsEnabled(EnableCap.ScissorTest);
        var box = new int[4]; GL.GetInteger(GetPName.ScissorBox, box);
        var clear = new float[4]; GL.GetFloat(GetPName.ColorClearValue, clear);
        var mask = new bool[4]; GL.GetBoolean(GetPName.ColorWritemask, mask);
        GL.GetInteger(GetPName.Viewport, viewport);
        try
        {
            GL.Enable(EnableCap.ScissorTest); GL.ColorMask(true, true, true, true); GL.ClearColor(0f, 0f, 0f, 1f);
            var c = Math.Clamp(closure, 0, 1);
            var topCoverage = (int)Math.Ceiling(viewport[3] * (Math.Clamp(upperLid, 0, .4) * (1 - c) + .85 * c));
            var bottomCoverage = (int)Math.Ceiling(viewport[3] * .15 * c);
            foreach (var r in SymptomRegions.Outside(viewport[2], viewport[3], protectedMenus))
            foreach (var lid in new[] { new ScreenRegion(0, 0, viewport[2], topCoverage),
                new ScreenRegion(0, viewport[3] - bottomCoverage, viewport[2], bottomCoverage) })
            {
                var region = r.Intersect(lid);
                if (region.Empty) continue;
                GL.Scissor(viewport[0] + region.X, viewport[1] + viewport[3] - region.Y - region.Height, region.Width, region.Height);
                GL.Clear(ClearBufferMask.ColorBufferBit);
            }
        }
        finally
        {
            GL.Scissor(box[0], box[1], box[2], box[3]); SetEnabled(EnableCap.ScissorTest, scissor);
            GL.ClearColor(clear[0], clear[1], clear[2], clear[3]); GL.ColorMask(mask[0], mask[1], mask[2], mask[3]);
        }
    }

    private void EnsureResources()
    {
        if (shader is null || shader.Disposed)
        {
            shader = api.Shader.NewShaderProgram();
            shader.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
            shader.VertexShader.Code = Vertex;
            shader.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
            shader.FragmentShader.Code = Fragment;
            api.Shader.RegisterMemoryShaderProgram("vsslssmodsymptoms", shader);
            if (!shader.Compile()) throw new InvalidOperationException("Symptom shader compilation failed.");
        }
        quad ??= api.Render.UploadMesh(new MeshData {
            xyz = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
            Uv = [0, 0, 1, 0, 1, 1, 0, 1], Indices = [0, 1, 2, 0, 2, 3],
            VerticesCount = 4, IndicesCount = 6
        });
    }
    private bool Reload() { shader?.Dispose(); shader = null; failed = false; return true; }
    public void Dispose()
    {
        api.Event.ReloadShader -= Reload;
        ClearNotice();
        ClearRest();
        shader?.Dispose(); shader = null; quad?.Dispose(); quad = null;
        if (scene != 0) GL.DeleteTexture(scene);
        foreach (var texture in blurTextures) if (texture != 0) GL.DeleteTexture(texture);
        foreach (var buffer in blurBuffers) if (buffer != 0) GL.DeleteFramebuffer(buffer);
        Array.Clear(blurTextures); Array.Clear(blurBuffers); blurWidth = blurHeight = 0;
        scene = width = height = sceneWidth = sceneHeight = 0;
    }

    private const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 vertexPositionIn;
        layout(location = 1) in vec2 uvIn;
        uniform vec4 screenRect;
        out vec2 uv;
        void main() {
            uv = uvIn;
            gl_Position = vec4(screenRect.xy + (vertexPositionIn.xy * 0.5 + 0.5) * screenRect.zw, 0.0, 1.0);
        }
        """;
    private const string Fragment = """
        #version 330 core
        uniform sampler2D sceneTexture;
        uniform sampler2D blurTexture;
        uniform int blurPass;
        uniform int hasBlur;
        uniform vec2 blurAxis;
        uniform vec2 pixelSize;
        uniform float blurStrength;
        uniform float darkness;
        uniform float centerDarkness;
        uniform int overlayOnly;
        uniform float closure;
        uniform float upperLid;
        uniform float ghost;
        uniform float time;
        uniform vec4 edgeTint;
        uniform float night;
        uniform int noticeMode;
        uniform float noticeAlpha;
        in vec2 uv;
        out vec4 outColor;
        void main() {
            if (blurPass == 1) {
                // Bilinear taps implement a smooth nine-tap Gaussian in each axis.
                vec3 c = texture(sceneTexture, uv).rgb * .2270270270;
                c += texture(sceneTexture, uv + blurAxis * 1.3846153846).rgb * .3162162162;
                c += texture(sceneTexture, uv - blurAxis * 1.3846153846).rgb * .3162162162;
                c += texture(sceneTexture, uv + blurAxis * 3.2307692308).rgb * .0702702703;
                c += texture(sceneTexture, uv - blurAxis * 3.2307692308).rgb * .0702702703;
                outColor = vec4(c, 1.0); return;
            }
            if (noticeMode == 1) {
                outColor = texture(sceneTexture, vec2(uv.x, 1.0 - uv.y)) * noticeAlpha;
                return;
            }
            if (closure >= 1.0) { outColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
            float radial = smoothstep(0.2, 1.0, length((uv - 0.5) * 2.0));
            float dim = mix(centerDarkness, darkness, radial);
            vec3 color = vec3(0.0);
            if (overlayOnly == 0) {
                color = texture(sceneTexture, uv).rgb;
                if (hasBlur == 1) {
                    float focus = smoothstep(0.0, .12, blurStrength);
                    color = mix(color, texture(blurTexture, uv).rgb, focus);
                    // A faint displaced image emerges during sustained fixation; no refraction or UV warp.
                    float pulse = pow(.5 + .5 * sin(time * 1.15), 3.0);
                    vec2 shift = vec2(2.0 + sin(time * .7), .35 * cos(time * .6)) * pixelSize
                        * (1.0 / (1080.0 * pixelSize.y));
                    color = mix(color, texture(blurTexture, uv + shift).rgb, ghost * pulse);
                }
            }
            float feather = 0.012 + closure * 0.018;
            float curve = .88 + .12 * pow(uv.x * 2.0 - 1.0, 2.0);
            float topEdge = mix(upperLid * curve, .85, closure);
            float bottomEdge = .15 * closure;
            float top = topEdge <= 0.0 ? 0.0 : smoothstep(1.0 - topEdge - feather, 1.0 - topEdge, uv.y);
            float bottom = bottomEdge <= 0.0 ? 0.0 : 1.0 - smoothstep(bottomEdge, bottomEdge + feather, uv.y);
            float mask = max(top, bottom);
            if (overlayOnly == 1) { outColor = vec4(0.0, 0.0, 0.0, 1.0 - (1.0 - mask) * (1.0 - dim)); return; }
            float edgeDistance = min(min(uv.x, 1.0 - uv.x) / 0.108, min(uv.y, 1.0 - uv.y) / 0.144);
            float vignette = (1.0 - smoothstep(0.0, 1.0, edgeDistance)) * edgeTint.a * 0.14;
            color = mix(color, vec3(.55), clamp(night * .35, 0.0, .35));
            color = mix(color, edgeTint.rgb, vignette) * (1.0 - dim);
            outColor = vec4(color * (1.0 - mask), 1.0);
        }
        """;
}
