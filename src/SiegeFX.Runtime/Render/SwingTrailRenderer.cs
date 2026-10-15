using System.Numerics;
using Silk.NET.OpenGL;

namespace SiegeFX.Runtime.Render;

/// <summary>SC-SWING-TRAIL — the pale arc a melee weapon leaves through the
/// air during a swing. Each swinging weapon contributes a short history of
/// blade segments (inner point, tip, time sampled); consecutive segments are
/// joined into quads whose alpha falls off with age, so the arc trails the
/// blade and fades within <see cref="Life"/>. Streamed world-space geometry,
/// alpha blended, depth-tested with depth writes off, no culling (the strip
/// is seen from both sides), the same recipe as
/// <see cref="SelectionFxRenderer"/>.</summary>
public sealed class SwingTrailRenderer : IDisposable
{
    /// <summary>One blade sample: the inner point, the tip, and when it was
    /// taken (game seconds).</summary>
    public readonly record struct Sample(Vector3 Inner, Vector3 Tip, double Time);

    /// <summary>How long a sample stays visible, in game seconds.</summary>
    public const float Life = 0.18f;

    /// <summary>Alpha at the newest sample (fading to zero at <see cref="Life"/>).</summary>
    private const float HeadAlpha = 0.55f;

    private const string VertexSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec4 aColor;
uniform mat4 uViewProj;
out vec4 vColor;
void main() { vColor = aColor; gl_Position = uViewProj * vec4(aPos, 1.0); }";

    private const string FragmentSrc = @"#version 330 core
in vec4 vColor;
out vec4 frag;
void main() { if (vColor.a <= 0.004) discard; frag = vColor; }";

    private const int Stride = 7; // pos3 + rgba4

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo;
    private readonly List<float> _verts = new(Stride * 6 * 32);
    private float[] _uploadBuf = Array.Empty<float>();
    private bool _disposed;

    public SwingTrailRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSrc, FragmentSrc);
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
        unsafe
        {
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, GLEnum.Float, false, Stride * sizeof(float), (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 4, GLEnum.Float, false, Stride * sizeof(float), (void*)(3 * sizeof(float)));
        }
        _gl.BindVertexArray(0);
    }

    /// <summary>Drops samples older than <see cref="Life"/> from a trail.</summary>
    public static void Prune(List<Sample> trail, double now)
    {
        int drop = 0;
        while (drop < trail.Count && now - trail[drop].Time > Life) drop++;
        if (drop > 0) trail.RemoveRange(0, drop);
    }

    public void Begin() => _verts.Clear();

    /// <summary>Adds one trail's strip (oldest sample first).</summary>
    public void Add(List<Sample> trail, double now)
    {
        for (int i = 1; i < trail.Count; i++)
        {
            var a = trail[i - 1];
            var b = trail[i];
            float aa = Alpha(a, now), ab = Alpha(b, now);
            if (aa <= 0f && ab <= 0f) continue;
            // Two triangles: a.Inner a.Tip b.Tip / a.Inner b.Tip b.Inner. The
            // inner edge is fainter, so the arc reads brightest at the tip.
            Emit(a.Inner, aa * 0.35f); Emit(a.Tip, aa); Emit(b.Tip, ab);
            Emit(a.Inner, aa * 0.35f); Emit(b.Tip, ab); Emit(b.Inner, ab * 0.35f);
        }
    }

    private static float Alpha(Sample s, double now) =>
        HeadAlpha * Math.Clamp(1f - (float)(now - s.Time) / Life, 0f, 1f);

    private void Emit(Vector3 p, float alpha)
    {
        _verts.Add(p.X); _verts.Add(p.Y); _verts.Add(p.Z);
        _verts.Add(0.92f); _verts.Add(0.94f); _verts.Add(0.96f); _verts.Add(alpha);
    }

    public void Draw(Matrix4x4 viewProj)
    {
        if (_disposed || _verts.Count == 0) return;
        bool blendWas = _gl.IsEnabled(GLEnum.Blend);
        bool cullWas = _gl.IsEnabled(GLEnum.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
        if (_uploadBuf.Length < _verts.Count)
            _uploadBuf = new float[Math.Max(_verts.Count, _uploadBuf.Length * 2)];
        _verts.CopyTo(_uploadBuf);
        unsafe
        {
            fixed (float* p = _uploadBuf)
                _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(_verts.Count * sizeof(float)), p, GLEnum.DynamicDraw);
        }
        _gl.DrawArrays(GLEnum.Triangles, 0, (uint)(_verts.Count / Stride));

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        if (cullWas) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
        if (!blendWas) _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
