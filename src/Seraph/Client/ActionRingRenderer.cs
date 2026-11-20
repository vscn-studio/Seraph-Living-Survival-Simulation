using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

/// <summary>Custom crosshair ring. A single reusable quad, with no per-frame Cairo drawing or mesh uploads.</summary>
internal sealed class ActionRingRenderer : IDisposable
{
    internal const double Diameter = 36, LineWidth = 5;
    internal static (float R, float G, float B) Color(string action) => action switch {
        "drink" or "well-bucket-raise" => (.15f, .6f, .95f),
        "clean" or "clean-body" or "bathe" or "bathe-wound" => (.2f, .8f, .85f),
        "clean-wound" => (.95f, .8f, .35f),
        "produce-wash" => (.35f, .75f, .35f),
        "defecate" => (.6f, .4f, .2f),
        "pressure" => (.9f, .2f, .2f),
        _ => (1f, 1f, 1f)
    };
    private readonly ICoreClientAPI api;
    private IShaderProgram? shader;
    private MeshRef? quad;
    private LoadedTexture? text;
    private string textKey = "";
    private double nextTextUpdate;
    private bool failed;
    internal ActionRingRenderer(ICoreClientAPI api) { this.api = api; api.Event.ReloadShader += Reload; }

    internal void Render(ActionProgressPresentation view, string label)
    {
        if (!view.Visible || failed) return;
        var width = api.Render.FrameWidth; var height = api.Render.FrameHeight;
        if (width <= 0 || height <= 0) return;
        var scale = Math.Max(.1, RuntimeEnv.GUIScale);
        var previousShader = api.Render.CurrentActiveShader;
        var previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        var depth = GL.IsEnabled(EnableCap.DepthTest); var blend = GL.IsEnabled(EnableCap.Blend);
        var vao = GL.GetInteger(GetPName.VertexArrayBinding);
        var srcRgb = GL.GetInteger(GetPName.BlendSrcRgb); var dstRgb = GL.GetInteger(GetPName.BlendDstRgb);
        var srcAlpha = GL.GetInteger(GetPName.BlendSrcAlpha); var dstAlpha = GL.GetInteger(GetPName.BlendDstAlpha);
        var rgbEquation = GL.GetInteger(GetPName.BlendEquationRgb); var alphaEquation = GL.GetInteger(GetPName.BlendEquationAlpha);
        try
        {
            previousShader?.Stop();
            EnsureResources();
            GL.Disable(EnableCap.DepthTest); GL.Enable(EnableCap.Blend);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            shader!.Use();
            var extent = (Diameter + 2) * scale;
            shader.Uniform("extent", (float)(extent / width), (float)(extent / height));
            shader.Uniform("outerRadius", (float)(Diameter / 2));
            shader.Uniform("lineWidth", (float)LineWidth);
            shader.Uniform("quadRadius", (float)(Diameter / 2 + 1));
            var color = Color(view.Action);
            shader.Uniform("progressColor", color.R, color.G, color.B);
            shader.Uniform("fraction", (float)(view.Activity ? .22 : view.Fraction));
            shader.Uniform("phase", (float)(view.Activity ? view.AnimationTime * .7 % 1 : 0));
            api.Render.RenderMesh(quad!);
        }
        catch (Exception ex)
        {
            failed = true;
            api.Logger.Warning("[vsslssmod] Action ring unavailable: {0}", ex.Message);
            return;
        }
        finally
        {
            shader?.Stop();
            if (depth) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
            if (blend) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);
            GL.BlendEquationSeparate((BlendEquationMode)rgbEquation, (BlendEquationMode)alphaEquation);
            GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb, (BlendingFactorDest)dstRgb, (BlendingFactorSrc)srcAlpha, (BlendingFactorDest)dstAlpha);
            GL.BindVertexArray(vao);
            if (previousShader is { Disposed: false }) previousShader.Use(); else GL.UseProgram(previousProgram);
        }
        var key = label + ":" + scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (key != textKey && (text is null || view.AnimationTime >= nextTextUpdate))
        {
            using var font = CairoFont.WhiteSmallText().WithFontSize(13).WithOrientation(EnumTextOrientation.Center)
                .WithStroke([0, 0, 0, .8], .6);
            text ??= new(api);
            api.Gui.TextTexture.GenOrUpdateTextTexture(label, font, ref text);
            textKey = key; nextTextUpdate = view.AnimationTime + .1;
        }
        if (text is not null)
            api.Render.Render2DTexturePremultipliedAlpha(text.TextureId, (width - text.Width) / 2f,
                (float)(height / 2d + 24 * scale), text.Width, text.Height, 60);
    }
    private void EnsureResources()
    {
        if (shader is null || shader.Disposed)
        {
            shader = api.Shader.NewShaderProgram();
            shader.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader); shader.VertexShader.Code = Vertex;
            shader.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader); shader.FragmentShader.Code = Fragment;
            api.Shader.RegisterMemoryShaderProgram("vsslssmodactionring", shader);
            if (!shader.Compile()) throw new InvalidOperationException("Action ring shader compilation failed.");
        }
        quad ??= api.Render.UploadMesh(new MeshData { xyz = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
            Uv = [0, 0, 1, 0, 1, 1, 0, 1], Indices = [0, 1, 2, 0, 2, 3], VerticesCount = 4, IndicesCount = 6 });
    }
    private bool Reload() { shader?.Dispose(); shader = null; failed = false; return true; }
    internal void ClearText() { text?.Dispose(); text = null; textKey = ""; nextTextUpdate = 0; }
    public void Dispose() { api.Event.ReloadShader -= Reload; shader?.Dispose(); quad?.Dispose(); ClearText(); }
    private const string Vertex = """
        #version 330 core
        layout(location=0) in vec3 vertexPositionIn;
        layout(location=1) in vec2 uvIn;
        uniform vec2 extent;
        uniform float quadRadius;
        out vec2 p;
        void main() { p = vertexPositionIn.xy * quadRadius; gl_Position=vec4(vertexPositionIn.xy*extent, 0.0, 1.0); }
        """;
    private const string Fragment = """
        #version 330 core
        in vec2 p;
        uniform float fraction;
        uniform float phase;
        uniform float outerRadius;
        uniform float lineWidth;
        uniform vec3 progressColor;
        out vec4 outColor;
        void main() {
            float radius=length(p);
            float feather=max(fwidth(radius),0.15);
            float innerRadius=outerRadius-lineWidth;
            float ring=smoothstep(innerRadius-feather,innerRadius+feather,radius)*(1.0-smoothstep(outerRadius-feather,outerRadius+feather,radius));
            float angle=mod(atan(p.x,p.y)/6.28318530718+1.0-phase,1.0);
            bool active=angle<fraction;
            outColor=vec4(active?progressColor:vec3(0.55),ring*(active?0.95:0.18));
        }
        """;
}
