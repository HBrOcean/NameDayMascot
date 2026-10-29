using System;
using System.IO;
using SkiaSharp;
using NameDayMascot.Rendering;
using Spine;

namespace NameDayMascot;

/// <summary>诊断：绕开 Avalonia，直接渲染一帧到 /tmp/dump.png，并打印附件信息。</summary>
public static class Diagnostics
{
    public static void Dump()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "characters", "311801");
        Console.WriteLine("[Dump] atlas 目录: " + dir);
        var model = new SkeletonModel(
            Path.Combine(dir, "311801.atlas"),
            Path.Combine(dir, "311801.skel"));

        Console.WriteLine("[Dump] 动画: " + string.Join(", ", model.AnimationNames));
        var b = model.Bounds;
        Console.WriteLine($"[Dump] Bounds: {b.MinX:F1},{b.MinY:F1} .. {b.MaxX:F1},{b.MaxY:F1}");

        model.Update(0f);

        int regionCount = 0, meshCount = 0, rotCount = 0, seqCount = 0;
        foreach (var slot in model.Skeleton.DrawOrder)
        {
            var att = slot.Attachment;
            if (att == null) continue;
            if (att is RegionAttachment r)
            {
                regionCount++;
                var ar = r.Region as AtlasRegion;
                bool seq = r.Sequence != null;
                if (seq) seqCount++;
                if (ar != null && ar.degrees != 0) rotCount++;
                Console.WriteLine($"[Region] {att.Name,-28} deg={ar?.degrees,-3} seq={seq} " +
                                  $"uvs=[{MinV(r.UVs):F3},{MaxV(r.UVs):F3}] region={ar?.name} page={(ar?.page != null)}");
            }
            else if (att is MeshAttachment m)
            {
                meshCount++;
                var ar = m.Region as AtlasRegion;
                bool seq = m.Sequence != null;
                if (seq) seqCount++;
                if (ar != null && ar.degrees != 0) rotCount++;
                var uvs = m.UVs;
                Console.WriteLine($"[Mesh]   {att.Name,-28} v={m.WorldVerticesLength / 2,-4} deg={ar?.degrees,-3} seq={seq} " +
                                  $"uvs=[{MinV(uvs):F3},{MaxV(uvs):F3}] region={ar?.name}");
            }
        }
        Console.WriteLine($"[Dump] Region={regionCount} Mesh={meshCount} 旋转={rotCount} sequence={seqCount}");

        // 直接用 SkiaSharp 光栅化（不经 Avalonia / WriteableBitmap）
        int W = 700, H = 900;
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(200, 210, 225)); // 浅蓝灰底，便于对比纯白块

        float cx = (b.MinX + b.MaxX) / 2f, cy = (b.MinY + b.MaxY) / 2f;
        float bw = Math.Max(1f, b.MaxX - b.MinX), bh = Math.Max(1f, b.MaxY - b.MinY);
        float scale = Math.Min(W / bw, H / bh) * 0.9f;

        canvas.Save();
        canvas.Translate(W / 2f, H / 2f);
        canvas.Scale(scale, -scale);
        canvas.Translate(-cx, -cy);
        var renderer = new SpineCanvasRenderer();
        renderer.Draw(canvas, model.Skeleton, model);
        canvas.Restore();
        canvas.Flush();

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        // 用系统临时目录（Windows 没有 /tmp）
        string outPath = Path.Combine(Path.GetTempPath(), "NameDayMascot-dump.png");
        File.WriteAllBytes(outPath, data.ToArray());
        Console.WriteLine("[Dump] 已保存 " + outPath);
    }

    private static float MinV(float[] a)
    {
        float m = float.MaxValue;
        foreach (var x in a) if (x < m) m = x;
        return m;
    }

    private static float MaxV(float[] a)
    {
        float m = float.MinValue;
        foreach (var x in a) if (x > m) m = x;
        return m;
    }
}
