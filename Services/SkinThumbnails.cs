using System;
using System.IO;
using Avalonia.Media.Imaging;
using SkiaSharp;
using NameDayMascot.Config;
using NameDayMascot.Rendering;

namespace NameDayMascot.Services;

/// <summary>
/// 皮肤缩略图：图集本身是"散件拼图"（各部位分开摆放），不能直接当预览图，
/// 所以用同一套渲染器离屏渲染一帧，并缓存到 %AppData%/NameDayMascot/thumbs/。
/// </summary>
public static class SkinThumbnails
{
    private static string CacheDir => Path.Combine(ConfigStore.ConfigDir, "thumbs");

    /// <summary>
    /// 只查缓存，绝不渲染。用于界面构造阶段（此时不能阻塞 UI 线程）。
    /// 命中返回 Bitmap，未命中返回 null，由调用方决定什么时候去渲染。
    /// </summary>
    public static Bitmap? TryGetCached(string skinId, int width = 240, int height = 296)
    {
        string cache = Path.Combine(CacheDir, $"{skinId}_{width}x{height}.png");
        if (!File.Exists(cache)) return null;
        try
        {
            using var fs = File.OpenRead(cache);
            return new Bitmap(fs);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Thumb] 读取缓存失败 {cache}", ex);
            return null;
        }
    }

    /// <summary>删掉全部缩略图缓存（用户换了图集或觉得预览不对时用）。</summary>
    public static void ClearCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (var f in Directory.GetFiles(CacheDir, "*.png")) File.Delete(f);
            Logger.Log("[Thumb] 缩略图缓存已清空");
        }
        catch (Exception ex) { Logger.Error("[Thumb] 清空缓存失败", ex); }
    }

    /// <summary>
    /// 取某套皮肤的缩略图；无缓存时现渲染一张并落盘。
    /// ⚠ 会阻塞调用线程（要加载 skel + 解码图集），只能在后台线程调。
    /// </summary>
    public static Bitmap? Get(string skinId, string characterDir, int width = 240, int height = 296)
    {
        // 缓存命中直接返回（读一张小 PNG，很快）
        var cached = TryGetCached(skinId, width, height);
        if (cached != null) return cached;

        string cache = Path.Combine(CacheDir, $"{skinId}_{width}x{height}.png");

        try
        {
            string atlas = Path.Combine(characterDir, skinId + ".atlas");
            string skel = Path.Combine(characterDir, skinId + ".skel");
            if (!File.Exists(atlas) || !File.Exists(skel)) return null;

            using var model = new SkeletonModel(atlas, skel);

            // setup pose 里大量 slot 没有 attachment，必须先播一个动画推进几帧才有内容
            model.SetAnimation(0, model.HasAnimation("idle") ? "idle" : "", true);
            model.Update(0.5f);

            using var surface = SKSurface.Create(
                new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface == null) return null;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var b = model.Bounds;
            float cx = (b.MinX + b.MaxX) / 2f;
            float cy = (b.MinY + b.MaxY) / 2f;
            float bw = Math.Max(1f, b.MaxX - b.MinX);
            float bh = Math.Max(1f, b.MaxY - b.MinY);
            float s = Math.Min(width / bw, height / bh) * 0.92f;

            canvas.Translate(width / 2f, height / 2f);
            canvas.Scale(s, -s);
            canvas.Translate(-cx, -cy);

            using (var renderer = new SpineCanvasRenderer())
                renderer.Draw(canvas, model.Skeleton, model);

            canvas.Flush();

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 90);
            if (data == null) return null;

            byte[] bytes = data.ToArray();
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllBytes(cache, bytes);
            }
            catch (Exception ex)
            {
                Logger.Error("[Thumb] 写缓存失败（不影响显示）", ex);
            }

            return new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            Logger.Error($"[Thumb] 渲染 {skinId} 缩略图失败", ex);
            return null;
        }
    }
}
