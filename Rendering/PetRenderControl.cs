using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using Spine;

namespace NameDayMascot.Rendering;

/// <summary>
/// 桌宠渲染控件：CPU 光栅 SKSurface → 直接拷进 WriteableBitmap，交给 Avalonia 显示。
/// 不做 PNG 编解码（旧实现每帧 PNG 编码约 10~20ms，是最大的 CPU 浪费）。
/// 30fps 更新，复用 surface 与位图，避免每帧分配。
/// </summary>
public sealed class PetRenderControl : Control, IDisposable
{
    private readonly SkeletonModel _model;
    private readonly SpineCanvasRenderer _renderer = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private WriteableBitmap? _wbmp;
    private SKSurface? _surface;
    private int _surfaceW, _surfaceH;
    private double _lastSeconds;
    private double _cachedScaling = 1.0;
    private bool _disposed;

    // 视框：以「静态包围盒中心」为中心的半宽/半高，可随动作幅度扩张。
    //
    // 为什么只存半宽半高、中心固定：
    //   若像原来那样存 minX/minY/maxX/maxY 四边，四边各自独立插值，
    //   视框中心就会漂移，绘制时角色跟着左右上下跑（"偏移"）。
    //   锁定中心后，视框永远对称扩张，角色始终在窗口正中间。
    private float _halfW, _halfH;

    /// <summary>上一次视框扩张的时刻。收缩前要等一小会儿，避免连续动作之间窗口来回缩涨。</summary>
    private float _lastGrowTime;

    // 算当前帧包围盒用的临时缓冲（够长就不重新分配）
    private float[] _boundsBuf = new float[256];

    // 安全边距：绘制框相对「静态包围盒」的放大比例，四周各留这么多。
    // 横向给得多一些 —— 技能动画大多是左右挥动，纵向主要是轻微起伏。
    // ⚠ 这两个值同时决定窗口尺寸和绘制比例，两边必须一致（见 RenderFrame 里的说明）。
    public const double PadX = 0.25;
    public const double PadY = 0.12;

    /// <summary>桌宠缩放（DIP）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>是否允许渲染循环运行（窗口隐藏时应置 false，避免空跑）。</summary>
    public bool Running { get; set; } = true;

    /// <summary>上下浮动（轻微正弦漂浮，像在呼吸）。</summary>
    public bool Bobbing { get; set; }

    /// <summary>水平翻转（左右镜像）。</summary>
    public bool Flip { get; set; }

    /// <summary>
    /// 视框是否跟随动作幅度扩张。关掉则永远用静态包围盒（窗口尺寸恒定）。
    /// </summary>
    public bool DynamicView { get; set; } = true;

    public SkeletonModel Model => _model;

    public PetRenderControl(SkeletonModel model)
    {
        _model = model;
        var b0 = model.Bounds;
        _halfW = (b0.MaxX - b0.MinX) / 2f;
        _halfH = (b0.MaxY - b0.MinY) / 2f;
        // 30fps：桌面挂件足够顺，且比 60fps 省一半 CPU
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnFrame);
        _timer.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!Running || _disposed) return;

        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastSeconds, 0.0, 0.1);
        _lastSeconds = now;

        _model.Update(dt);
        RenderFrame();
        InvalidateVisual();
    }

    /// <summary>
    /// 控件期望的 DIP 尺寸 = 视框 × 缩放 × (1 + 边距)。
    /// 这里的边距和 RenderFrame 里的绘制框用的是同一组常量，两边必须保持同步，
    /// 否则会算出一边贴边、另一边有余量的结果。
    /// </summary>
    public Size DesiredPetSize
    {
        get
        {
            double w = _halfW * 2.0 * Scale;
            double h = _halfH * 2.0 * Scale;
            return new Size(Math.Max(1, w * (1 + PadX)), Math.Max(1, h * (1 + PadY)));
        }
    }

    private void RenderFrame()
    {
        // 缩放系数缓存：不需要每帧去查 TopLevel
        var top = TopLevel.GetTopLevel(this);
        if (top != null) _cachedScaling = top.RenderScaling > 0 ? top.RenderScaling : 1.0;

        int pw = Math.Max(1, (int)(Bounds.Width * _cachedScaling));
        int ph = Math.Max(1, (int)(Bounds.Height * _cachedScaling));
        if (pw < 2 || ph < 2) return; // 尚未完成布局

        if (_surface == null || _surfaceW != pw || _surfaceH != ph)
        {
            _surface?.Dispose();
            _surface = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
            _surfaceW = pw;
            _surfaceH = ph;
        }

        var surface = _surface;
        if (surface == null) return;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // 视框跟随动作幅度（窗口尺寸由 PetWindow 根据 DesiredPetSize 同步调整）
        UpdateView();

        // 变换原点永远用「静态包围盒中心」。视框围绕它对称扩张，
        // 所以角色不会因为某一侧动作摆得更远而偏移。
        var sb = _model.Bounds;
        float cx = (sb.MinX + sb.MaxX) / 2f;
        float cy = (sb.MinY + sb.MaxY) / 2f;

        canvas.Save();

        // 上下浮动：屏幕空间的正弦偏移（物理像素），与模型缩放无关
        float bobPx = Bobbing
            ? (float)Math.Sin(_clock.Elapsed.TotalSeconds * 1.8) * 5f * (float)_cachedScaling
            : 0f;
        canvas.Translate(pw / 2f, ph / 2f + bobPx);

        // 绘制比例只由「用户缩放 × 屏幕 DPI」决定，与窗口尺寸彻底解耦。
        //
        // 这里刻意**不做**「窗口装不下就缩小」的钳制：那个钳制会在窗口追上的那一帧
        // 把角色猛地缩一下再弹回，正是之前看到的"振动"。窗口尺寸最多晚一帧（33ms），
        // 短暂越界远比尺寸抖动更难被察觉。
        float s = (float)(Scale * _cachedScaling);

        // 水平翻转：X 方向取负
        canvas.Scale(Flip ? -s : s, -s);
        canvas.Translate(-cx, -cy);
        _renderer.Draw(canvas, _model.Skeleton, _model);
        canvas.Restore();
        canvas.Flush();

        // 零拷贝：像素直接搬进 WriteableBitmap，不做 PNG 编解码
        using var pixmap = surface.PeekPixels();
        if (pixmap == null) return;

        if (_wbmp == null || _wbmp.PixelSize.Width != pw || _wbmp.PixelSize.Height != ph)
        {
            _wbmp?.Dispose();
            _wbmp = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96),
                                        PixelFormat.Rgba8888, AlphaFormat.Premul);
        }

        unsafe
        {
            using var fb = _wbmp.Lock();
            byte* dst = (byte*)fb.Address;
            byte* src = (byte*)pixmap.GetPixels();
            int srcRow = pixmap.RowBytes;
            int dstRow = fb.RowBytes;
            int copyLen = Math.Min(srcRow, dstRow);
            for (int y = 0; y < ph; y++)
                Buffer.MemoryCopy(src + (long)y * srcRow, dst + (long)y * dstRow, dstRow, copyLen);
        }
    }

    /// <summary>
    /// 更新视框：只算半宽/半高，中心固定为静态包围盒中心。
    ///
    /// 只用「模型顶点」计算，绝不参考窗口尺寸 —— 否则会形成
    /// 「窗口变大 → 比例变大 → 视框变大 → 窗口更大」的反馈环。
    ///
    /// 扩张立即生效、收缩才平滑：动作起手那几帧必须马上让开，
    /// 否则窗口来不及变大，角色当场被裁。
    /// </summary>
    private void UpdateView()
    {
        var sb = _model.Bounds;
        float cx = (sb.MinX + sb.MaxX) / 2f;
        float cy = (sb.MinY + sb.MaxY) / 2f;
        float statHalfW = Math.Max(1e-3f, (sb.MaxX - sb.MinX) / 2f);
        float statHalfH = Math.Max(1e-3f, (sb.MaxY - sb.MinY) / 2f);

        float targetW = statHalfW;
        float targetH = statHalfH;

        if (DynamicView && TryGetFrameBounds(out var fb))
        {
            // 当前帧相对中心需要多大的半宽/半高。
            // 取两侧的较大值 —— 对称扩张才不会让角色偏离窗口中心。
            float needW = Math.Max(cx - fb.MinX, fb.MaxX - cx);
            float needH = Math.Max(cy - fb.MinY, fb.MaxY - cy);

            if (needW > targetW) targetW = needW;
            if (needH > targetH) targetH = needH;

            // 限制最大扩张倍率：个别动画会让骨架整体位移到很远，
            // 不设上限的话窗口会瞬间撑到半个屏幕
            const float MaxExpand = 1.5f;
            targetW = Math.Min(targetW, statHalfW * MaxExpand);
            targetH = Math.Min(targetH, statHalfH * MaxExpand);
        }

        // 不能比静态框更小，否则角色会被整体缩小
        if (targetW < statHalfW) targetW = statHalfW;
        if (targetH < statHalfH) targetH = statHalfH;

        // 扩张立即生效；收缩前留 1 秒宽限 ——
        // 动作之间常常只隔零点几秒，立刻收缩会导致窗口反复缩涨，看着像抽搐。
        float now = (float)_clock.Elapsed.TotalSeconds;
        const float ShrinkDelay = 1.0f;
        const float ShrinkK = 0.12f;   // 开始收之后约 0.6 秒到位

        if (targetW > _halfW) { _halfW = targetW; _lastGrowTime = now; }
        else if (now - _lastGrowTime > ShrinkDelay) _halfW += (targetW - _halfW) * ShrinkK;

        if (targetH > _halfH) { _halfH = targetH; _lastGrowTime = now; }
        else if (now - _lastGrowTime > ShrinkDelay) _halfH += (targetH - _halfH) * ShrinkK;
    }

    /// <summary>把视框立刻复位到静态包围盒（关闭动态尺寸时用）。</summary>
    public void ResetView()
    {
        var b = _model.Bounds;
        _halfW = (b.MaxX - b.MinX) / 2f;
        _halfH = (b.MaxY - b.MinY) / 2f;
    }

    /// <summary>
    /// 取当前帧实际顶点包围盒（Spine 本地坐标）。
    /// 动作幅度大时会比静态 Bounds 大，动态窗口就是靠它。
    /// </summary>
    private bool TryGetFrameBounds(out (float MinX, float MinY, float MaxX, float MaxY) bounds)
    {
        bounds = default;
        if (_disposed) return false;

        float x0 = float.MaxValue, y0 = float.MaxValue;
        float x1 = float.MinValue, y1 = float.MinValue;

        foreach (var slot in _model.Skeleton.DrawOrder)
        {
            if (slot.Attachment is not VertexAttachment va || va.WorldVerticesLength < 2) continue;

            int need = va.WorldVerticesLength;
            if (_boundsBuf.Length < need) _boundsBuf = new float[need];

            va.ComputeWorldVertices(slot, _boundsBuf);
            for (int i = 0; i + 1 < need; i += 2)
            {
                if (_boundsBuf[i] < x0) x0 = _boundsBuf[i];
                if (_boundsBuf[i] > x1) x1 = _boundsBuf[i];
                if (_boundsBuf[i + 1] < y0) y0 = _boundsBuf[i + 1];
                if (_boundsBuf[i + 1] > y1) y1 = _boundsBuf[i + 1];
            }
        }

        if (x0 > x1 || y0 > y1) return false;

        bounds = (x0, y0, x1, y1);
        return true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var img = (IImage?)_wbmp;
        if (img != null)
            context.DrawImage(img, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _renderer.Dispose();
        _surface?.Dispose();
        _wbmp?.Dispose();
    }
}
