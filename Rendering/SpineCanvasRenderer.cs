using System;
using System.Collections.Generic;
using SkiaSharp;
using Spine;

namespace NameDayMascot.Rendering;

/// <summary>
/// 把 Spine 骨架绘制到 SkiaSharp 画布。spine-csharp 没有官方 Skia 后端，
/// 这里按其数据结构（RegionAttachment / MeshAttachment / ClippingAttachment）自绘三角网格。
/// </summary>
public sealed class SpineCanvasRenderer
{
    private readonly SkeletonClipping _clipper = new();

    // 世界坐标顶点缓冲（x,y 交错）
    private float[] _verts = new float[1024];

    // 每张贴图一个 shader，避免每帧重建
    private readonly Dictionary<SKImage, SKShader> _shaders = new();

    // 按「精确长度」缓存的顶点缓冲：SKVertices.CreateCopy 只接受数组且要求长度精确，
    // 所以不能复用一个更大的数组；但同一 attachment 的顶点数恒定，按长度建池即可稳定命中。
    private readonly Dictionary<int, SKPoint[]> _posPool = new();
    private readonly Dictionary<int, SKPoint[]> _texPool = new();
    private readonly Dictionary<int, SKColor[]> _colPool = new();
    private readonly Dictionary<int, ushort[]> _idxPool = new();

    private T[] Rent<T>(Dictionary<int, T[]> pool, int n)
    {
        if (!pool.TryGetValue(n, out var a))
        {
            a = new T[n];
            pool[n] = a;
        }
        return a;
    }

    private readonly SKPaint _paint = new()
    {
        IsAntialias = true,
        FilterQuality = SKFilterQuality.High,
    };

    private static readonly ushort[] QuadIndices = { 0, 1, 2, 2, 3, 0 };

    public void Draw(SKCanvas canvas, Skeleton skeleton, SkeletonModel model)
    {
        foreach (var slot in skeleton.DrawOrder)
        {
            var attachment = slot.Attachment;
            if (attachment == null) continue;

            if (attachment is ClippingAttachment clip)
            {
                _clipper.ClipStart(slot, clip);
                continue;
            }

            if (attachment is RegionAttachment region)
            {
                DrawRegion(canvas, slot, region, model);
            }
            else if (attachment is MeshAttachment mesh)
            {
                DrawMesh(canvas, slot, mesh, model);
            }

            // 若该 slot 是某裁剪的 end slot，结束裁剪
            if (_clipper.IsClipping) _clipper.ClipEnd(slot);
        }
    }

    private void DrawRegion(SKCanvas canvas, Slot slot, RegionAttachment region, SkeletonModel model)
    {
        EnsureVerts(8);
        region.ComputeWorldVertices(slot, _verts, 0, 2); // 4 顶点

        var image = model.GetImage(region.Region);
        if (image == null) return;
        float pw = model.PageWidth(region.Region);
        float ph = model.PageHeight(region.Region);

        var pos = Rent(_posPool, 4);
        var tex = Rent(_texPool, 4);
        for (int i = 0; i < 4; i++)
        {
            pos[i] = new SKPoint(_verts[i * 2], _verts[i * 2 + 1]);
            tex[i] = new SKPoint(region.UVs[i * 2] * pw, region.UVs[i * 2 + 1] * ph);
        }

        var color = Tint(slot, region.R, region.G, region.B, region.A);
        DrawQuad(canvas, image, pos, tex, QuadIndices, color, slot.Data.BlendMode);
    }

    private void DrawMesh(SKCanvas canvas, Slot slot, MeshAttachment mesh, SkeletonModel model)
    {
        int count = mesh.WorldVerticesLength; // 浮点个数
        if (count < 6) return;
        EnsureVerts(count);
        mesh.ComputeWorldVertices(slot, 0, count, _verts, 0, 2);

        var image = model.GetImage(mesh.Region);
        if (image == null) return;
        float pw = model.PageWidth(mesh.Region);
        float ph = model.PageHeight(mesh.Region);

        int vcount = count / 2;
        var uvs = mesh.UVs;
        var tris = mesh.Triangles;

        var color = Tint(slot, mesh.R, mesh.G, mesh.B, mesh.A);

        if (_clipper.IsClipping)
        {
            // 裁剪：送进 clipper，得到裁剪后的顶点/三角
            _clipper.ClipTriangles(_verts, tris, tris.Length, uvs);
            var cv = _clipper.ClippedVertices;  // x,y,u,v 交错
            var ct = _clipper.ClippedTriangles; // 索引
            int cvc = cv.Count / 4;
            if (cvc < 3 || ct.Count < 3) return;

            var pos = Rent(_posPool, cvc);
            var tex = Rent(_texPool, cvc);
            var cArr = cv.Items;
            for (int i = 0; i < cvc; i++)
            {
                pos[i] = new SKPoint(cArr[i * 4], cArr[i * 4 + 1]);
                tex[i] = new SKPoint(cArr[i * 4 + 2] * pw, cArr[i * 4 + 3] * ph);
            }
            var idx = Rent(_idxPool, ct.Count);
            var ctArr = ct.Items;
            for (int i = 0; i < ct.Count; i++) idx[i] = (ushort)ctArr[i];
            DrawQuad(canvas, image, pos, tex, idx, color, slot.Data.BlendMode);
            return;
        }

        var pos2 = Rent(_posPool, vcount);
        var tex2 = Rent(_texPool, vcount);
        for (int i = 0; i < vcount; i++)
        {
            pos2[i] = new SKPoint(_verts[i * 2], _verts[i * 2 + 1]);
            tex2[i] = new SKPoint(uvs[i * 2] * pw, uvs[i * 2 + 1] * ph);
        }
        var idx2 = Rent(_idxPool, tris.Length);
        for (int i = 0; i < tris.Length; i++) idx2[i] = (ushort)tris[i];
        DrawQuad(canvas, image, pos2, tex2, idx2, color, slot.Data.BlendMode);
    }

    private void DrawQuad(SKCanvas canvas, SKImage image, SKPoint[] pos, SKPoint[] tex,
        ushort[] idx, SKColor color, BlendMode blend)
    {
        var colors = Rent(_colPool, pos.Length);
        for (int i = 0; i < colors.Length; i++) colors[i] = color;

        var shader = GetShader(image);
        _paint.Shader = shader;
        _paint.Color = SKColors.White;

        // slot 的合成模式要设在 paint 上（plus/multiply 等针对整段绘制）。
        _paint.BlendMode = ToSkia(blend);

        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, colors, idx);

        // 注意：DrawVertices 的 blendMode 参数不是图层合成模式，而是
        // 「顶点色与纹理采样的混合方式」，默认且唯一正确的是 Modulate（相乘）。
        // 这里若传 SrcOver，白色顶点色会把纹理整个盖住 → 每个三角面变成纯白块。
        canvas.DrawVertices(vertices, SKBlendMode.Modulate, _paint);
    }

    private SKShader GetShader(SKImage image)
    {
        if (_shaders.TryGetValue(image, out var s)) return s;
        s = SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        _shaders[image] = s;
        return s;
    }

    private static SKColor Tint(Slot slot, float r, float g, float b, float a)
    {
        // CPU 光栅 surface 的 DrawVertices 按非预乘（straight）处理顶点色，这里给直通色
        float aa = a * slot.A;
        return new SKColor(
            (byte)Math.Clamp((int)(r * slot.R * 255f), 0, 255),
            (byte)Math.Clamp((int)(g * slot.G * 255f), 0, 255),
            (byte)Math.Clamp((int)(b * slot.B * 255f), 0, 255),
            (byte)Math.Clamp((int)(aa * 255f), 0, 255));
    }

    private static SKBlendMode ToSkia(BlendMode mode) => mode switch
    {
        BlendMode.Additive => SKBlendMode.Plus,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        _ => SKBlendMode.SrcOver,
    };

    private void EnsureVerts(int n)
    {
        if (_verts.Length < n) _verts = new float[n * 2];
    }

    public void Dispose()
    {
        foreach (var kv in _shaders) kv.Value?.Dispose();
        _shaders.Clear();
        _paint.Dispose();
    }
}
