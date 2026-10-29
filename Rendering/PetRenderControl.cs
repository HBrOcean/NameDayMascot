using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

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

    // 安全边距：动作摆动、发丝/衣摆外扩常超出静态包围盒，横向多留一些
    private const double PadX = 0.20;
    private const double PadY = 0.08;

    /// <summary>桌宠缩放（DIP）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>是否允许渲染循环运行（窗口隐藏时应置 false，避免空跑）。</summary>
    public bool Running { get; set; } = true;

    public SkeletonModel Model => _model;

    public PetRenderControl(SkeletonModel model)
    {
        _model = model;
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

    /// <summary>控件期望的 DIP 尺寸（模型包围盒 × 缩放 + 安全边距）。</summary>
    public Size DesiredPetSize
    {
        get
        {
            var b = _model.Bounds;
            double w = (b.MaxX - b.MinX) * Scale;
            double h = (b.MaxY - b.MinY) * Scale;
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

        var b = _model.Bounds;
        float cx = (b.MinX + b.MaxX) / 2f;
        float cy = (b.MinY + b.MaxY) / 2f;

        canvas.Save();
        canvas.Translate(pw / 2f, ph / 2f);

        // 按「控件能容纳的最大比例」绘制：即使某帧摆幅超出静态包围盒，
        // 结果只是整体略缩，而不会被窗口边缘裁掉。
        float bw = Math.Max(1e-3f, b.MaxX - b.MinX);
        float bh = Math.Max(1e-3f, b.MaxY - b.MinY);
        float s = Math.Min(pw / bw, ph / bh);

        canvas.Scale(s, -s);
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
