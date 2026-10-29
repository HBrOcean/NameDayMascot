using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NameDayMascot.Config;
using NameDayMascot.Rendering;
using NameDayMascot.Services;

namespace NameDayMascot.Views;

/// <summary>
/// 桌宠主窗口：透明无边框、置顶、可拖动；单击触发动作，播完自动回待机。
/// </summary>
public class PetWindow : Window
{
    private readonly AppConfig _config;
    private PetRenderControl _pet;
    private SkeletonModel _model;

    // 拖动/点击判定
    private bool _pressed;
    private bool _dragged;
    // 按下点记录为「屏幕绝对坐标」（物理像素）。
    // 由 窗口位置 + 窗口内相对坐标×缩放 合成：窗口移动时两项变化互相抵消，
    // 结果恒等于鼠标真实屏幕位置，因此不会形成
    // 「窗口移动 → 相对坐标反向变化 → 再移动」的负反馈（表现为窗口在两点间闪烁）。
    private double _pressScreenX, _pressScreenY;
    private PixelPoint _pressWindowPos;
    private const double DragThreshold = 4.0; // DIP

    public event Action? SettingsRequested;

    public PetWindow(AppConfig config, SkeletonModel model)
    {
        _config = config;
        _model = model;
        _pet = new PetRenderControl(model) { Scale = config.Scale };

        // 窗口图标（角色大头照）
        Icon = AppIcons.Load("app.png");

        // 透明、无边框、置顶、不占任务栏
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = config.Topmost;
        ShowInTaskbar = false;
        CanResize = false;

        // 白卡片背景（保底/调试）
        if (config.ShowWhiteCard)
        {
            Content = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(16),
                Child = _pet,
            };
        }
        else
        {
            Content = _pet;
        }

        ApplySize();
        if (config.HasSavedPosition)
            Position = new PixelPoint((int)config.WindowX, (int)config.WindowY);
        else
            PlaceAtBottomRight();

        // 事件
        _pet.PointerPressed += OnPointerPressed;
        _pet.PointerMoved += OnPointerMoved;
        _pet.PointerReleased += OnPointerReleased;
        PositionChanged += (_, __) => SavePosition();

        // 启动入场：先播 born，再回默认待机
        PlayIntro();
    }

    private void ApplySize()
    {
        var s = _pet.DesiredPetSize;
        _pet.Width = s.Width;
        _pet.Height = s.Height;
        Width = s.Width;
        Height = s.Height;
    }

    /// <summary>首次启动：贴到主屏右下角（WorkingArea 已排除任务栏，边距 24px）。</summary>
    private void PlaceAtBottomRight(int margin = 24)
    {
        try
        {
            var screen = Screens.Primary
                         ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
            if (screen == null)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            var wa = screen.WorkingArea;                              // 物理像素
            double sc = screen.Scaling > 0 ? screen.Scaling : 1.0;     // DIP → 物理像素
            int w = (int)Math.Ceiling(Width * sc);
            int h = (int)Math.Ceiling(Height * sc);

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(wa.Right - w - margin, wa.Bottom - h - margin);
        }
        catch
        {
            // Screens 在窗口尚未挂到平台时可能不可用，退回居中，不影响启动
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    /// <summary>拖动结束后吸附到最近的屏幕边缘（阈值 24 物理像素）。</summary>
    private void SnapToEdge(int threshold = 24)
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;

            var wa = screen.WorkingArea;
            double sc = RenderScaling > 0 ? RenderScaling : 1.0;
            int w = (int)Math.Ceiling(Width * sc);
            int h = (int)Math.Ceiling(Height * sc);

            int x = Position.X, y = Position.Y;
            if (Math.Abs(x - wa.X) <= threshold) x = wa.X;
            else if (Math.Abs(wa.Right - (x + w)) <= threshold) x = wa.Right - w;

            if (Math.Abs(y - wa.Y) <= threshold) y = wa.Y;
            else if (Math.Abs(wa.Bottom - (y + h)) <= threshold) y = wa.Bottom - h;

            if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
        }
        catch { /* 屏幕信息不可用时忽略 */ }
    }

    /// <summary>把窗口位置约束在屏幕工作区内（缩放变大后防止顶出屏幕）。</summary>
    private void ClampToScreen()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;

            var wa = screen.WorkingArea;
            double sc = RenderScaling > 0 ? RenderScaling : 1.0;
            int w = (int)Math.Ceiling(Width * sc);
            int h = (int)Math.Ceiling(Height * sc);

            int maxX = Math.Max(wa.X, wa.Right - w);
            int maxY = Math.Max(wa.Y, wa.Bottom - h);
            int x = Math.Clamp(Position.X, wa.X, maxX);
            int y = Math.Clamp(Position.Y, wa.Y, maxY);

            if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
        }
        catch { /* 忽略 */ }
    }

    public void SetScale(double scale)
    {
        _config.Scale = scale;
        _pet.Scale = scale;
        ApplySize();
        // 放大后窗口可能向右下顶出屏幕，拉回工作区
        ClampToScreen();
    }

    public void SetTopmost(bool topmost)
    {
        _config.Topmost = topmost;
        Topmost = topmost;
    }

    /// <summary>控制渲染循环开关（窗口隐藏时应关闭，避免空跑占 CPU）。</summary>
    public void SetRenderRunning(bool running) => _pet.Running = running;

    /// <summary>切换到另一套皮肤：重建渲染控件，保留缩放、位置与置顶。</summary>
    public void SetModel(SkeletonModel model)
    {
        var oldPet = _pet;
        oldPet.PointerPressed -= OnPointerPressed;
        oldPet.PointerMoved -= OnPointerMoved;
        oldPet.PointerReleased -= OnPointerReleased;

        _model = model;
        _pet = new PetRenderControl(model) { Scale = _config.Scale };
        _pet.PointerPressed += OnPointerPressed;
        _pet.PointerMoved += OnPointerMoved;
        _pet.PointerReleased += OnPointerReleased;

        if (_config.ShowWhiteCard)
            Content = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Child = _pet };
        else
            Content = _pet;

        oldPet.Dispose();
        _pet.Running = IsVisible;   // 隐藏状态下切换皮肤时不应开始渲染
        ApplySize();
        PlayIntro();
    }

    /// <summary>切换白卡片背景（立即生效，不需重启）。</summary>
    public void SetWhiteCard(bool white)
    {
        _config.ShowWhiteCard = white;
        if (white)
            Content = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Child = _pet };
        else
            Content = _pet;
    }

    public IReadOnlyList<string> AnimationNames => _model.AnimationNames;

    public void PlayIdle()
    {
        string idle = PickIdle();
        _model.SetAnimation(0, idle, true);
    }

    /// <summary>入场：先完整播一遍 born，播完接默认待机；没有 born 就直接待机。</summary>
    public void PlayIntro()
    {
        string idle = PickIdle();
        if (string.IsNullOrEmpty(idle)) return;

        if (_model.HasAnimation("born"))
        {
            _model.SetAnimation(0, "born", false);
            _model.AddAnimation(0, idle, true, 0f);   // born 结束后立刻接待机
        }
        else
        {
            _model.SetAnimation(0, idle, true);
        }
    }

    /// <summary>播放一次性动作，播完自动回到待机。动作锁定时期间忽略新的动作请求。</summary>
    public void PlayAction(string name)
    {
        if (string.IsNullOrEmpty(name) || !_model.HasAnimation(name)) return;

        // 动作锁定：当前一次性动作还没播完 → 拒绝新请求，保证动作完整播完
        if (_config.LockOnAction && _model.IsActionPlaying)
        {
            Logger.Log($"[Action] 动作锁定中，忽略 {name}（可在设置里关闭「动作锁定」）");
            return;
        }

        string idle = PickIdle();
        _model.SetAnimation(0, name, false);
        _model.AddAnimation(0, idle, true, 0f);
    }

    /// <summary>播放指定动作（托盘/快捷键触发，随机挑一个）。</summary>
    public void PlayRandomAction()
    {
        var name = _config.ClickAnimation;
        if (string.IsNullOrEmpty(name))
        {
            var all = _model.AnimationNames;
            if (all.Count > 0) name = all[0];
        }
        PlayAction(name);
    }

    private string PickIdle()
    {
        if (!string.IsNullOrEmpty(_config.IdleAnimation) && _model.HasAnimation(_config.IdleAnimation))
            return _config.IdleAnimation;
        var all = _model.AnimationNames;
        return all.Count > 0 ? all[0] : "";
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_config.Clickable) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _pressed = true;
        _dragged = false;
        _pressWindowPos = Position;

        double sc = RenderScaling > 0 ? RenderScaling : 1.0;
        var rel = e.GetPosition(this);
        _pressScreenX = Position.X + rel.X * sc;
        _pressScreenY = Position.Y + rel.Y * sc;
        e.Pointer.Capture(_pet);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;

        // 合成出鼠标当前屏幕绝对坐标（与按下点同一坐标系）
        double sc = RenderScaling > 0 ? RenderScaling : 1.0;
        var rel = e.GetPosition(this);
        double curScreenX = Position.X + rel.X * sc;
        double curScreenY = Position.Y + rel.Y * sc;

        int dx = (int)Math.Round(curScreenX - _pressScreenX);
        int dy = (int)Math.Round(curScreenY - _pressScreenY);

        if (!_dragged)
        {
            if (Math.Abs(dx) / sc < DragThreshold && Math.Abs(dy) / sc < DragThreshold)
                return;
            _dragged = true;
        }

        Position = new PixelPoint(_pressWindowPos.X + dx, _pressWindowPos.Y + dy);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (!_pressed) return;
        _pressed = false;

        if (_dragged)
        {
            // 拖动结束：靠近屏幕边缘则吸附过去
            if (_config.SnapToEdge) SnapToEdge();
        }
        else if (_config.Clickable)
        {
            // 没拖动 → 视为单击，触发动作
            PlayRandomAction();
        }

        _dragged = false;
    }

    private void SavePosition()
    {
        _config.WindowX = Position.X;
        _config.WindowY = Position.Y;
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePosition();
        ConfigStore.Save(_config);
        _pet.Dispose();
        _model.Dispose();
        base.OnClosed(e);
    }
}
