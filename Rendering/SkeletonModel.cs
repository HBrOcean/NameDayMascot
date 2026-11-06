using System;
using System.Collections.Generic;
using System.IO;
using NameDayMascot.Services;
using SkiaSharp;
using Spine;

namespace NameDayMascot.Rendering;

/// <summary>
/// 加载一个 Spine Q 版角色（atlas 文本 + .skel 二进制 + 图集图），持有贴图、骨骼与动画状态。
/// 贴图用 SkiaSharp 的 SKImage 承载，供 SpineCanvasRenderer 采样。
/// </summary>
public sealed class SkeletonModel : IDisposable
{
    public Atlas Atlas { get; }
    public SkeletonData Data { get; }
    public Skeleton Skeleton { get; }
    public AnimationState State { get; }

    // 图集页 → SKImage
    private readonly Dictionary<AtlasPage, SKImage> _images = new();

    // 动画名缓存：这个属性被 UI 高频访问（设置面板每个按键行都会取一次），
    // 原来每次访问都 new 一个 List，纯浪费。
    private readonly List<string> _animationNames = new();

    private bool _disposed;

    /// <summary>模型自然包围盒（Spine 本地坐标，未缩放），用于确定绘制基准。</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds { get; }

    public SkeletonModel(string atlasPath, string skelPath, float defaultMix = 0.15f)
    {
        var loader = new SkiaTextureLoader(_images);
        string imagesDir = Path.GetDirectoryName(Path.GetFullPath(atlasPath)) ?? ".";

        using (var reader = new StreamReader(atlasPath))
            Atlas = new Atlas(reader, imagesDir, loader);

        using (var fs = File.OpenRead(skelPath))
            Data = new SkeletonBinary(Atlas).ReadSkeletonData(fs);

        Skeleton = new Skeleton(Data);
        var stateData = new AnimationStateData(Data) { DefaultMix = defaultMix };
        State = new AnimationState(stateData);

        foreach (var a in Data.Animations) _animationNames.Add(a.Name);

        Bounds = ComputeBounds();
    }

    /// <summary>取某个区域对应的贴图（RegionAttachment / MeshAttachment 共用）。</summary>
    public SKImage? GetImage(TextureRegion region)
    {
        if (region is AtlasRegion ar && ar.page != null && _images.TryGetValue(ar.page, out var img))
            return img;
        // 兜底：任取一张
        foreach (var kv in _images) return kv.Value;
        return null;
    }

    public float PageWidth(TextureRegion region) =>
        (region is AtlasRegion ar && ar.page != null) ? ar.page.width : region.width;

    public float PageHeight(TextureRegion region) =>
        (region is AtlasRegion ar && ar.page != null) ? ar.page.height : region.height;

    /// <summary>动画名列表（用于设置面板下拉）。构造时算好，之后只读。</summary>
    public IReadOnlyList<string> AnimationNames => _animationNames;

    public bool HasAnimation(string name) => Data.FindAnimation(name) != null;

    /// <summary>track0 是否仍在播放一次性动作（尚未播完、还没回到循环待机）。</summary>
    public bool IsActionPlaying
    {
        get
        {
            var cur = State.GetCurrent(0);
            return cur != null && !cur.Loop && cur.TrackTime < cur.AnimationEnd;
        }
    }

    /// <summary>播放动画；track0 为常规动作轨。</summary>
    public void SetAnimation(int track, string name, bool loop)
    {
        if (string.IsNullOrEmpty(name) || Data.FindAnimation(name) == null) return;
        State.SetAnimation(track, name, loop);
    }

    public void AddAnimation(int track, string name, bool loop, float delay = 0f)
    {
        if (string.IsNullOrEmpty(name) || Data.FindAnimation(name) == null) return;
        State.AddAnimation(track, name, loop, delay);
    }

    /// <summary>推进一帧：时间 → 动画 → 骨骼世界变换。</summary>
    public void Update(float deltaSeconds)
    {
        State.Update(deltaSeconds);
        State.Apply(Skeleton);
        Skeleton.UpdateWorldTransform(Skeleton.Physics.None);
    }

    private (float, float, float, float) ComputeBounds()
    {
        // 逐帧采样，每帧记一个包围盒，最后做统计而不是简单取全局 min/max。
        //
        // 为什么：部分 Spine 模型里 born / posture 这类动画会把骨架整体位移到很远
        // （实测有模型跑到 x≈-999）。直接对所有帧取绝对 min/max，包围盒会被撑成
        // 1200+ 宽，渲染时按框缩放，桌宠就变成一小只还偏在一边。
        var frames = new List<(float X0, float Y0, float X1, float Y1)>();

        void SampleFrame()
        {
            Skeleton.UpdateWorldTransform(Skeleton.Physics.None);

            float fx0 = float.MaxValue, fy0 = float.MaxValue;
            float fx1 = float.MinValue, fy1 = float.MinValue;

            foreach (var slot in Skeleton.DrawOrder)
            {
                if (slot.Attachment is not VertexAttachment va || va.WorldVerticesLength < 2) continue;

                var verts = new float[va.WorldVerticesLength];
                va.ComputeWorldVertices(slot, verts);
                for (int i = 0; i + 1 < verts.Length; i += 2)
                {
                    if (verts[i] < fx0) fx0 = verts[i];
                    if (verts[i] > fx1) fx1 = verts[i];
                    if (verts[i + 1] < fy0) fy0 = verts[i + 1];
                    if (verts[i + 1] > fy1) fy1 = verts[i + 1];
                }
            }

            if (fx0 <= fx1 && fy0 <= fy1) frames.Add((fx0, fy0, fx1, fy1));
        }

        // 遍历「所有」动画采样。
        //
        // 每动画 49 帧（原来只有 13 帧）：采样太稀会让快速摆动的极值落在两个采样点
        // 之间，静态包围盒就会偏小，运行时表现为"渲染超出窗口"。
        foreach (var anim in Data.Animations)
        {
            float dur = anim.Duration <= 0 ? 0.1f : anim.Duration;
            const int steps = 48;
            for (int i = 0; i <= steps; i++)
            {
                anim.Apply(Skeleton, 0f, dur * i / steps, false, null, 1f, MixBlend.Setup, MixDirection.In);
                SampleFrame();
            }
        }

        // 恢复干净状态交给调用方
        Skeleton.SetToSetupPose();
        State.Apply(Skeleton);
        Skeleton.UpdateWorldTransform(Skeleton.Physics.None);

        // 一个动画都没有 → 用 setup pose 自己当包围盒
        if (frames.Count == 0) SampleFrame();
        if (frames.Count == 0) return (-100, -100, 100, 100);

        // 绝对框
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var f in frames)
        {
            if (f.X0 < x0) x0 = f.X0;
            if (f.Y0 < y0) y0 = f.Y0;
            if (f.X1 > x1) x1 = f.X1;
            if (f.Y1 > y1) y1 = f.Y1;
        }

        // 分位框：砍掉最极端的各 10% 帧
        var q = QuantileBox(frames);

        // 只有绝对框被少数极端帧撑大到 2.2 倍以上，才改用它。
        // 阈值定得比较高：宁可包围盒偏大（角色略小），也不要因为误判而裁掉动作。
        float aw = Math.Max(1e-3f, x1 - x0), ah = Math.Max(1e-3f, y1 - y0);
        float qw = Math.Max(1e-3f, q.X1 - q.X0), qh = Math.Max(1e-3f, q.Y1 - q.Y0);

        if (aw > qw * 2.2f || ah > qh * 2.2f)
        {
            Logger.Log($"[Bounds] 检测到极端位移帧，改用分位包围盒 {qw:0}x{qh:0}（绝对框 {aw:0}x{ah:0}）");
            LogBoundsDiagnosis(frames, q.X0, q.Y0, q.X1, q.Y1);
            return q;
        }

        LogBoundsDiagnosis(frames, x0, y0, x1, y1);
        return (x0, y0, x1, y1);
    }

    /// <summary>
    /// 包围盒自检：比较「最终框」和「每一帧的实际尺寸」。
    /// 最大单帧明显大于最终框时，说明有帧注定被裁 —— 排查"渲染超出窗口"的第一手数据。
    /// </summary>
    private static void LogBoundsDiagnosis(
        List<(float X0, float Y0, float X1, float Y1)> frames,
        float x0, float y0, float x1, float y1)
    {
        float finalW = Math.Max(1e-3f, x1 - x0);
        float finalH = Math.Max(1e-3f, y1 - y0);

        float maxFW = 0f, maxFH = 0f;
        foreach (var f in frames)
        {
            float w = f.X1 - f.X0, h = f.Y1 - f.Y0;
            if (w > maxFW) maxFW = w;
            if (h > maxFH) maxFH = h;
        }

        Logger.Log($"[Bounds] 采样 {frames.Count} 帧 → 最终框 {finalW:0}×{finalH:0}"
                 + $"，最大单帧 {maxFW:0}×{maxFH:0}");

        if (maxFW > finalW * 1.02f || maxFH > finalH * 1.02f)
        {
            Logger.Log($"[Bounds] ⚠ 存在比最终框更大的帧"
                     + $"（宽 +{(maxFW / finalW - 1) * 100:0}%，高 +{(maxFH / finalH - 1) * 100:0}%）"
                     + "，这些帧会被裁掉");
        }
    }

    /// <summary>取四边各的 10% / 90% 分位，排除大幅位移的异常帧。</summary>
    private static (float X0, float Y0, float X1, float Y1) QuantileBox(
        List<(float X0, float Y0, float X1, float Y1)> frames)
    {
        static float Pick(List<float> values, float p)
        {
            if (values.Count == 0) return 0f;
            values.Sort();
            int i = (int)Math.Round((values.Count - 1) * p, MidpointRounding.AwayFromZero);
            return values[Math.Clamp(i, 0, values.Count - 1)];
        }

        var left = new List<float>(frames.Count);
        var top = new List<float>(frames.Count);
        var right = new List<float>(frames.Count);
        var bottom = new List<float>(frames.Count);

        foreach (var f in frames)
        {
            left.Add(f.X0);
            top.Add(f.Y0);
            right.Add(f.X1);
            bottom.Add(f.Y1);
        }

        return (Pick(left, 0.10f), Pick(top, 0.10f), Pick(right, 0.90f), Pick(bottom, 0.90f));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _images) kv.Value?.Dispose();
        _images.Clear();
    }

    private sealed class SkiaTextureLoader : TextureLoader
    {
        private readonly Dictionary<AtlasPage, SKImage> _map;
        public SkiaTextureLoader(Dictionary<AtlasPage, SKImage> map) => _map = map;

        public void Load(AtlasPage page, string path)
        {
            if (!File.Exists(path)) return;

            var bytes = File.ReadAllBytes(path);

            // 统一到 RGBA8888 + Premul。不同平台/后端（Windows GPU、Avalonia 内置 libskia）
            // 默认解码格式不一致（实测 Linux 为 BGRA8888/Premul），显式指定可避免
            // alpha 通道丢失或预乘状态错乱导致的色块。
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) throw new InvalidOperationException("贴图解码失败: " + path);

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                SKColorType.Rgba8888, SKAlphaType.Premul);

            using var bmp = SKBitmap.Decode(codec, info);
            if (bmp == null) throw new InvalidOperationException("贴图解码为 RGBA8888 失败: " + path);

            // FromPixelCopy 会复制像素，不依赖 bitmap 的生命周期
            var img = SKImage.FromPixelCopy(info, bmp.GetPixels(), bmp.RowBytes);
            if (img == null) throw new InvalidOperationException("贴图创建失败: " + path);

            // 兜底：真无 alpha 的贴图会在透明区画出白/黑色块，这里明确暴露出来
            if (img.AlphaType == SKAlphaType.Opaque)
                Logger.Log($"[贴图] 警告：{Path.GetFileName(path)} 不含 alpha 通道（Opaque），透明区可能出现色块");

            Logger.Log($"[贴图] {Path.GetFileName(path)} {img.Width}x{img.Height} {img.ColorType}/{img.AlphaType}");

            page.rendererObject = img;
            _map[page] = img;
        }

        public void Unload(object texture) { (texture as SKImage)?.Dispose(); }
    }
}
