using OpenTK.Graphics.OpenGL4;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.Client;

/// <summary>World-only low-light vision from visible surface depth. Persistent GPU resources;
/// no CPU pixel readback, world scans, UI capture or additional network traffic.</summary>
internal sealed class DarkAdaptationRenderer : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly Func<ServerSnapshot?> snapshot;
    private readonly DarkAdaptationState eyes = new();
    private readonly BlockPos eyePos = new(0);
    private readonly long listener;
    private readonly int[] viewport = new int[4];
    private readonly bool[] colorMask = new bool[4];
    private IShaderProgram? shader;
    private MeshRef? quad;
    private int scene, width, height;
    private bool failed, warnedDepth;
    private string? epoch;
    private long life;
    private double strength;
    private double noiseTime;
    internal int IconStage => strength > 0 ? eyes.IconStage : 0;
    private readonly PanelMetric metric = new() { Key = "darkadaptation", Unit = "%", Minimum = 0, Maximum = 100, Available = true };
    internal PanelMetric Metric => metric;
    public double RenderOrder => .99;
    public int RenderRange => 0;

    internal DarkAdaptationRenderer(ICoreClientAPI api, Func<ServerSnapshot?> snapshot)
    {
        this.api = api; this.snapshot = snapshot;
        listener = api.Event.RegisterGameTickListener(Sample, 250);
        api.Event.RegisterRenderer(this, EnumRenderStage.AfterBlit, "vsslssmod-darkadaptation");
        api.Event.ReloadShader += Reload;
    }

    private void Sample(float dt)
    {
        var state = snapshot();
        if (state is null || api.World.Player?.Entity is not { Alive: true } player) { Reset(); return; }
        if (epoch != state.SessionEpoch || life != state.LifeGeneration)
        { Reset(); epoch = state.SessionEpoch; life = state.LifeGeneration; }
        if (state.IsSleeping || state.IsClosedEyeResting) { strength = 0; return; }
        var p = player.Pos;
        eyePos.Set((int)Math.Floor(p.X + player.LocalEyePos.X), (int)Math.Floor(p.Y + player.LocalEyePos.Y),
            (int)Math.Floor(p.Z + player.LocalEyePos.Z));
        eyePos.dimension = p.Dimension;
        var blocks = api.World.BlockAccessor;
        var light = blocks.GetLightLevel(eyePos, EnumLightLevelType.MaxTimeOfDayLight);
        // Held light is dynamic and is not necessarily present in the block light grid.
        light = Math.Max(light, Brightness(player.LightHsv));
        if (player.RightHandItemSlot?.Itemstack is { } right)
            light = Math.Max(light, Brightness(right.Collectible.GetLightHsv(blocks, eyePos, right)));
        if (player.LeftHandItemSlot?.Itemstack is { } left)
            light = Math.Max(light, Brightness(left.Collectible.GetLightHsv(blocks, eyePos, left)));
        eyes.Advance(light, dt, api.IsGamePaused);
        var penalty = 0d;
        foreach (var effect in state.ClinicalEffects)
            if (effect.Key == "night-vision" && double.IsFinite(effect.Severity)) penalty = Math.Max(penalty, effect.Severity);
        strength = eyes.Strength(penalty);
        metric.Value = Math.Round(eyes.Level * 100);
    }

    private static int Brightness(byte[]? hsv) => hsv is { Length: >= 3 } ? hsv[2] : 0;
    internal void Reset() { eyes.Reset(); metric.Value = strength = noiseTime = 0; epoch = null; life = 0; }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (failed || strength <= .0001 || snapshot() is not { IsSleeping: false, IsClosedEyeResting: false }
            || api.World.Player?.Entity.Alive != true) return;
        var buffers = api.Render.FrameBuffers;
        var primary = buffers.Count > (int)EnumFrameBuffer.Primary ? buffers[(int)EnumFrameBuffer.Primary] : null;
        if (primary is null || primary.Disposed || primary.DepthTextureId == 0)
        {
            if (!warnedDepth) { warnedDepth = true; api.Logger.Warning("[vsslssmod] Dark adaptation requires the primary depth texture; enhancement skipped."); }
            return;
        }
        var projection = api.Render.PerspectiveProjectionMat;
        if (projection is not { Length: >= 16 } || Math.Abs(projection[14]) < 1e-9) return;
        if (!api.IsGamePaused && float.IsFinite(dt)) noiseTime = (noiseTime + Math.Clamp(dt, 0, .1f)) % 1000;
        Draw(primary.DepthTextureId, projection);
    }

    private void Draw(int depthTexture, double[] projection)
    {
        var previousShader = api.Render.CurrentActiveShader;
        var program = GL.GetInteger(GetPName.CurrentProgram);
        var depth = GL.IsEnabled(EnableCap.DepthTest); var blend = GL.IsEnabled(EnableCap.Blend);
        var cull = GL.IsEnabled(EnableCap.CullFace); var scissor = GL.IsEnabled(EnableCap.ScissorTest);
        var stencil = GL.IsEnabled(EnableCap.StencilTest); var depthWrite = GL.GetBoolean(GetPName.DepthWritemask);
        GL.GetBoolean(GetPName.ColorWritemask, colorMask);
        var vao = GL.GetInteger(GetPName.VertexArrayBinding);
        var activeTexture = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0); var texture0 = GL.GetInteger(GetPName.TextureBinding2D);
        GL.ActiveTexture(TextureUnit.Texture1); var texture1 = GL.GetInteger(GetPName.TextureBinding2D);
        var readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding); var readBuffer = GL.GetInteger(GetPName.ReadBuffer);
        try
        {
            GL.GetInteger(GetPName.Viewport, viewport);
            if (viewport[2] <= 0 || viewport[3] <= 0) return;
            GL.ActiveTexture(TextureUnit.Texture0);
            if (scene == 0) scene = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, scene);
            if (width != viewport[2] || height != viewport[3])
            {
                width = viewport[2]; height = viewport[3];
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            }
            var target = GL.GetInteger(GetPName.DrawFramebufferBinding);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target);
            GL.ReadBuffer(target == 0 ? ReadBufferMode.Back : ReadBufferMode.ColorAttachment0);
            GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, viewport[0], viewport[1], width, height);
            previousShader?.Stop(); EnsureResources();
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.CullFace); GL.Disable(EnableCap.StencilTest);
            GL.DepthMask(false); GL.ColorMask(true, true, true, true);
            shader!.Use();
            shader.BindTexture2D("sceneTexture", scene, 0);
            shader.BindTexture2D("depthTexture", depthTexture, 1);
            shader.Uniform("projection", (float)projection[0], (float)projection[5], (float)projection[10], (float)projection[14]);
            shader.Uniform("strength", (float)strength);
            shader.Uniform("noiseFrame", (float)Math.Floor(noiseTime * 12));
            api.Render.RenderMesh(quad!);
        }
        catch (Exception ex)
        { failed = true; api.Logger.Warning("[vsslssmod] Dark adaptation renderer disabled: {0}", ex.Message); }
        finally
        {
            shader?.Stop();
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer); GL.ReadBuffer((ReadBufferMode)readBuffer);
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, texture0);
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, texture1);
            GL.ActiveTexture((TextureUnit)activeTexture);
            Restore(EnableCap.DepthTest, depth); Restore(EnableCap.Blend, blend); Restore(EnableCap.CullFace, cull);
            Restore(EnableCap.ScissorTest, scissor); Restore(EnableCap.StencilTest, stencil);
            GL.DepthMask(depthWrite); GL.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]); GL.BindVertexArray(vao);
            if (previousShader is { Disposed: false }) previousShader.Use(); else GL.UseProgram(program);
        }
    }

    private static void Restore(EnableCap capability, bool enabled) { if (enabled) GL.Enable(capability); else GL.Disable(capability); }
    private void EnsureResources()
    {
        if (shader is null || shader.Disposed)
        {
            shader = api.Shader.NewShaderProgram();
            shader.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader); shader.VertexShader.Code = Vertex;
            shader.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader); shader.FragmentShader.Code = Fragment;
            api.Shader.RegisterMemoryShaderProgram("vsslssmoddarkadaptation", shader);
            if (!shader.Compile()) throw new InvalidOperationException("Dark adaptation shader compilation failed.");
        }
        quad ??= api.Render.UploadMesh(new MeshData {
            xyz = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], Uv = [0, 0, 1, 0, 1, 1, 0, 1],
            Indices = [0, 1, 2, 0, 2, 3], VerticesCount = 4, IndicesCount = 6 });
    }
    private bool Reload() { shader?.Dispose(); shader = null; failed = false; return true; }
    public void Dispose()
    {
        api.Event.UnregisterRenderer(this, EnumRenderStage.AfterBlit); api.Event.UnregisterGameTickListener(listener);
        api.Event.ReloadShader -= Reload; shader?.Dispose(); quad?.Dispose(); if (scene != 0) GL.DeleteTexture(scene);
    }

    private const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 vertexPositionIn;
        layout(location = 1) in vec2 uvIn;
        out vec2 uv;
        void main() { uv = uvIn; gl_Position = vec4(vertexPositionIn, 1.0); }
        """;
    private const string Fragment = """
        #version 330 core
        uniform sampler2D sceneTexture;
        uniform sampler2D depthTexture;
        uniform vec4 projection;
        uniform float strength;
        uniform float noiseFrame;
        in vec2 uv;
        out vec4 outColor;
        float luminance(vec3 c) { return dot(c, vec3(.2126, .7152, .0722)); }
        void main() {
            vec3 color = texture(sceneTexture, uv).rgb;
            float depth = texture(depthTexture, uv).r;
            float z = projection.w / (depth * 2.0 - 1.0 + projection.z);
            vec3 position = vec3((uv * 2.0 - 1.0) / projection.xy, -1.0) * z;
            float distanceToEye = length(position);
            vec3 dx = dFdx(position), dy = dFdy(position);
            vec3 normal = normalize(cross(dx, dy));
            float contour = clamp(length(vec2(dFdx(z), dFdy(z))) / max(.05, z) * 6.0, 0.0, 1.0);
            // Only the frontmost visible geometry contributes; sky and surfaces beyond 6 m never brighten.
            float nearMask = (1.0 - smoothstep(4.0, 6.0, distanceToEye)) * (1.0 - step(.999999, depth));
            float luma = luminance(color);
            float dark = 1.0 - smoothstep(.025, .22, luma);
            float amount = nearMask * dark * strength;
            if (amount <= .00001) { outColor = vec4(color, 1.0); return; }
            vec2 size = vec2(textureSize(sceneTexture, 0));
            vec2 texel = 1.0 / size;
            vec2 eccentricity = (uv - .5) * vec2(size.x / size.y, 1.0);
            float peripheral = smoothstep(.12, .55, length(eccentricity));
            // Keep texture detail: apply a shadow exposure curve, never a screen-wide gray floor.
            // A small depth cue only supplements surfaces with virtually no remaining light.
            float gain = mix(1.7, 2.5, peripheral);
            float geometry = (.006 + .018 * abs(normal.z) + .012 * contour)
                * (1.0 - smoothstep(.005, .04, luma)) * mix(.8, 1.15, peripheral);
            float lifted = min(.115, luma * (1.0 + gain) + geometry);
            float localMean = (luminance(texture(sceneTexture, uv + vec2(texel.x, 0)).rgb)
                + luminance(texture(sceneTexture, uv - vec2(texel.x, 0)).rgb)
                + luminance(texture(sceneTexture, uv + vec2(0, texel.y)).rgb)
                + luminance(texture(sceneTexture, uv - vec2(0, texel.y)).rgb)) * .25;
            // Sharpen existing central contrast without blurring the centre or spreading light sources.
            float detail = clamp(luma - localMean, -.008, .008) * mix(.65, .15, peripheral);
            float target = max(luma, min(.115, lifted + detail));
            vec3 exposed = color * (target / max(luma, .0001));
            vec3 coolGray = target * vec3(.94, .985, 1.04);
            vec3 night = mix(exposed, coolGray, mix(.94, .985, peripheral));
            float grain = fract(sin(dot(floor(gl_FragCoord.xy), vec2(12.9898, 78.233))
                + noiseFrame * 37.719) * 43758.5453) * 2.0 - 1.0;
            grain *= .002 * (1.0 - smoothstep(.008, .04, luma)) * mix(.35, 1.0, peripheral);
            outColor = vec4(max(vec3(0), mix(color, night, amount) + grain * amount), 1.0);
        }
        """;
}
