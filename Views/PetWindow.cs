using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
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

    // 单击随机动作用
    private readonly Random _rng = new();
    private string _lastRandom = "";

    /// <summary>
    /// 「状态型」动画：点了也不该重复播放的东西。
    /// idle 是等待状态、born 是入场、sleep 睡过去、die 躺下不起来。
    /// </summary>
    private static readonly string[] NotClickable = { "idle", "born", "sleep", "die", "posture" };

    /// <summary>定时随机动作。间隔可配，任何一次动作播放都会让它重新计时。</summary>
    private readonly DispatcherTimer _autoTimer = new();

    /// <summary>窗口尺寸跟随动作幅度的轮询计时器（50ms，比渲染帧率低，减少 layout 次数）。</summary>
    private readonly DispatcherTimer _sizeTimer = new();

    public event Action? SettingsRequested;

    public PetWindow(AppConfig config, SkeletonModel model)
    {
        _config = config;
        _model = model;
        _pet = new PetRenderControl(model)
        {
            Scale = config.Scale,
            Bobbing = config.Bobbing,
            Flip = config.Flip,
            DynamicView = config.DynamicSize,
        };

        // 窗口图标（角色大头照）
        Icon = AppIcons.Load("app.png");

        // 透明、无边框、置顶、不占任务栏
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = config.ZOrder == "top";
        ShowInTaskbar = config.ShowInTaskbar;
        CanResize = false;

        Content = _pet;
        _pet.ContextMenu = BuildContextMenu();

        ApplySize();
        LogSize("启动");
        ApplyAutoPlay();
        ApplyDynamicSize();

        if (config.HasSavedPosition)
        {
            Position = new PixelPoint((int)config.WindowX, (int)config.WindowY);

            // 副屏被拔掉、分辨率变了、缩放改过 —— 保存的坐标可能已经指向屏幕外，
            // 表现就是"进程在跑但看不见"，而且所有入口都点不到。先救回来。
            if (!EnsureOnScreen()) PlaceAtBottomRight();
        }
        else
            PlaceAtBottomRight();

        // 事件
        _pet.PointerPressed += OnPointerPressed;
        _pet.PointerMoved += OnPointerMoved;
        _pet.PointerReleased += OnPointerReleased;
        PositionChanged += (_, __) => SavePosition();

        // 鼠标穿透 / 置底 需要原生窗口句柄，窗口创建完成后才能应用
        Opened += (_, __) =>
        {
            if (_config.MouseThrough) WindowEffects.SetMouseThrough(this, true);
            if (_config.ZOrder == "bottom") WindowEffects.SetBottom(this);
        };

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

    /// <summary>
    /// 把当前尺寸写进日志。只在启动和换皮肤时调 ——
    /// 放进 ApplySize 会在拖缩放滑块时每 1% 刷一条，把日志撑爆。
    /// </summary>
    private void LogSize(string tag)
    {
        var b = _model.Bounds;
        var s = _pet.DesiredPetSize;
        Logger.Log($"[Size] {tag} 包围盒 {b.MaxX - b.MinX:0}×{b.MaxY - b.MinY:0}"
                 + $"（x {b.MinX:0}..{b.MaxX:0}, y {b.MinY:0}..{b.MaxY:0}）"
                 + $" → 窗口 {s.Width:0}×{s.Height:0}（缩放 {_config.Scale:P0}）");
    }

    /// <summary>把配置里的定时动作参数应用到计时器上。</summary>
    private void ApplyAutoPlay()
    {
        // 夹到合理范围：太小像抽搐，太大等于没有
        double sec = Math.Clamp(_config.AutoPlayIntervalSeconds, 5, 180);
        _autoTimer.Interval = TimeSpan.FromSeconds(sec);

        // 先减后加，保证只有一个 handler（设置页反复改间隔时会重复调用）
        _autoTimer.Tick -= OnAutoPlayTick;

        if (_config.AutoPlay)
        {
            _autoTimer.Tick += OnAutoPlayTick;
            ResetAutoPlayClock();
        }
        else
        {
            _autoTimer.Stop();
        }
    }

    /// <summary>重新开始计时。DispatcherTimer 没有 Reset，Stop + Start 效果等价。</summary>
    private void ResetAutoPlayClock()
    {
        if (!_config.AutoPlay) return;
        _autoTimer.Stop();
        _autoTimer.Start();
    }

    private void OnAutoPlayTick(object? sender, EventArgs e)
    {
        // 被托盘「隐藏桌宠」藏起来了 → 不该继续在后台播动作
        if (!IsVisible) return;

        // 正在拖动：别在用户手上抢戏
        if (_pressed) return;

        // 上一个动作还没播完 → 这一轮跳过，等下一个周期
        if (_config.LockOnAction && _model.IsActionPlaying) return;

        // 定时动作走「纯随机」：忽略「点击动作」配置。
        // 它叫"定时随机动作"，如果跟着点击动作走，就等于每 20 秒重播同一个动作。
        PlayRandomAction(useConfigured: false);
        // 成功播放时 PlayAction 内部已经把计时器归零（从这一刻起重新算 20 秒）；
        // 走到这里其实没播（比如被动作锁定拒了）就什么都不做，下一个周期照常到点。
    }

    /// <summary>开/关定时随机动作（设置面板实时调用）。</summary>
    public void SetAutoPlay(bool on)
    {
        _config.AutoPlay = on;
        ApplyAutoPlay();
        Logger.Log($"[AutoPlay] {(on ? "开启" : "关闭")}，间隔 {_config.AutoPlayIntervalSeconds:0} 秒");
    }

    /// <summary>调整定时动作间隔（秒）。</summary>
    public void SetAutoPlayInterval(double seconds)
    {
        _config.AutoPlayIntervalSeconds = Math.Clamp(seconds, 5, 180);
        ApplyAutoPlay();
    }

    /// <summary>应用「窗口随动作自适应」的开关状态。</summary>
    private void ApplyDynamicSize()
    {
        _sizeTimer.Tick -= OnSizeTick;

        if (_config.DynamicSize)
        {
            _pet.DynamicView = true;
            _sizeTimer.Interval = TimeSpan.FromMilliseconds(33);
            _sizeTimer.Tick += OnSizeTick;
            _sizeTimer.Start();
        }
        else
        {
            _pet.DynamicView = false;
            _pet.ResetView();          // 视框立刻回到静态包围盒
            _sizeTimer.Stop();
            ResizeKeepingAnchor(_pet.DesiredPetSize.Width, _pet.DesiredPetSize.Height);
        }
    }

    /// <summary>设置面板调用：实时开关窗口自适应。</summary>
    public void SetDynamicSize(bool on)
    {
        _config.DynamicSize = on;
        ApplyDynamicSize();
        Logger.Log($"[Size] 窗口随动作自适应：{(on ? "开启" : "关闭")}");
    }

    private void OnSizeTick(object? sender, EventArgs e)
    {
        if (_pressed) return;      // 拖动过程中改尺寸会和人抢，手感会抖
        if (!IsVisible) return;

        var s = _pet.DesiredPetSize;

        // 小于 2 DIP 的变化不动窗口，避免一直在 resize
        if (Math.Abs(s.Width - Width) < 2 && Math.Abs(s.Height - Height) < 2) return;

        ResizeKeepingAnchor(s.Width, s.Height);
    }

    /// <summary>
    /// 按新尺寸调整窗口，保持「窗口几何中心」在屏幕上不动。
    ///
    /// 为什么是中心而不是底部：角色是画在窗口几何中心的（canvas 平移到 pw/2, ph/2），
    /// 窗口高度一变，锚在底部的写法会让角色整体上下移动 ΔH/2 —— 那就是"播放动作时偏移"。
    /// 锚在中心，角色在屏幕上纹丝不动，只有看不见的窗口边界在涨缩。
    /// </summary>
    private void ResizeKeepingAnchor(double w, double h)
    {
        if (double.IsNaN(w) || double.IsNaN(h) || w < 1 || h < 1) return;
        if (double.IsNaN(Width) || double.IsNaN(Height)) return;   // 首次布局完成前

        double sc = RenderScaling > 0 ? RenderScaling : 1.0;

        int oldPxW = (int)Math.Ceiling(Width * sc);
        int oldPxH = (int)Math.Ceiling(Height * sc);
        int newPxW = (int)Math.Ceiling(w * sc);
        int newPxH = (int)Math.Ceiling(h * sc);
        if (newPxW < 1 || newPxH < 1) return;

        int anchorX = Position.X + oldPxW / 2;
        int anchorY = Position.Y + oldPxH / 2;

        Width = w;
        Height = h;
        _pet.Width = w;
        _pet.Height = h;

        Position = new PixelPoint(anchorX - newPxW / 2, anchorY - newPxH / 2);

        // 这里刻意**不**调用 ClampToScreen：
        // 视框收缩是渐进的，每 33ms clamp 一次等于把窗口位置一格一格往回推，
        // 表现为"振动/拖影"。窗口边界可能短暂伸出屏幕，但它是透明的，看不见。
    }

    /// <summary>
    /// 确认窗口至少有一部分落在某个屏幕的工作区里。
    /// 拔掉副屏后，保存的坐标可能指向一片不存在的区域，窗口就变成"存在但看不见"。
    /// </summary>
    private bool EnsureOnScreen()
    {
        try
        {
            double sc = RenderScaling > 0 ? RenderScaling : 1.0;
            int w = Math.Max(1, (int)Math.Ceiling(Width * sc));
            int h = Math.Max(1, (int)Math.Ceiling(Height * sc));

            var mine = new PixelRect(Position.X, Position.Y, w, h);
            int mineArea = w * h;

            foreach (var screen in Screens.All)
            {
                var wa = screen.WorkingArea;

                // 手算交集，不依赖 PixelRect.Intersect 的具体行为
                int ix0 = Math.Max(wa.X, mine.X);
                int iy0 = Math.Max(wa.Y, mine.Y);
                int ix1 = Math.Min(wa.Right, mine.Right);
                int iy1 = Math.Min(wa.Bottom, mine.Bottom);
                int iw = ix1 - ix0, ih = iy1 - iy0;
                int area = (iw > 0 && ih > 0) ? iw * ih : 0;

                // 有 40% 以上的面积露在某个工作区里，就算用户还看得见
                if (area >= mineArea * 0.4) return true;
            }

            Logger.Log($"[Window] 记忆位置 ({Position.X},{Position.Y}) 不在任何屏幕内，重新定位");
            return false;
        }
        catch
        {
            // 屏幕信息拿不到就别乱动，保持原样
            return true;
        }
    }

    /// <summary>把窗口重置到主屏右下角（位置出问题时的自救入口）。</summary>
    public void ResetPosition()
    {
        PlaceAtBottomRight();
        SavePosition();
        Logger.Log("[Window] 位置已重置到主屏右下角");
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

        // 只用 ApplySize（只改尺寸，不动 Position）。
        //
        // 之前这里用 ResizeKeepingAnchor 保持窗口中心，紧跟着的 ClampToScreen
        // 又会在放大后把窗口推回工作区 —— 两个动作互相抵消，表现出来就是"缩放时角色偏移"。
        // 参考实现（SemmelweisMascot）用的就是这一句，行为稳定。
        ApplySize();

        // 放大后可能向右下顶出屏幕，拉回工作区
        ClampToScreen();
    }

    /// <summary>设置窗口层级：top = 始终最上 / bottom = 始终最下 / free = 自由跟随普通窗口。</summary>
    public void SetZOrder(string zorder)
    {
        zorder = (zorder ?? "").Trim().ToLowerInvariant();
        if (zorder != "top" && zorder != "bottom" && zorder != "free") zorder = "top";

        _config.ZOrder = zorder;
        _config.Topmost = zorder == "top";   // 同步旧字段，避免配置里两处打架

        switch (zorder)
        {
            case "top":
                Topmost = true;
                break;
            case "bottom":
                Topmost = false;
                WindowEffects.SetBottom(this);
                break;
            default:
                Topmost = false;   // free：跟随普通窗口
                break;
        }

        Logger.Log($"[Window] 层级 = {zorder}");
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
        _pet = new PetRenderControl(model)
        {
            Scale = _config.Scale,
            Bobbing = _config.Bobbing,
            Flip = _config.Flip,
            DynamicView = _config.DynamicSize,
        };
        _pet.PointerPressed += OnPointerPressed;
        _pet.PointerMoved += OnPointerMoved;
        _pet.PointerReleased += OnPointerReleased;

        Content = _pet;
        _pet.ContextMenu = BuildContextMenu();   // 换皮肤后动画名变了，菜单要重建

        oldPet.Dispose();
        _pet.Running = IsVisible;   // 隐藏状态下切换皮肤时不应开始渲染

        // 与 SetScale 同理：只改尺寸，不动 Position（参考实现的做法）
        ApplySize();
        LogSize("换皮肤");

        PlayIntro();
    }

    /// <summary>任务栏图标开关（立即生效）。</summary>
    public void SetShowInTaskbar(bool show)
    {
        _config.ShowInTaskbar = show;
        ShowInTaskbar = show;
    }

    /// <summary>禁止拖动：锁定位置，但单击仍可触发动作。</summary>
    public void SetLockPosition(bool locked) => _config.LockPosition = locked;

    /// <summary>鼠标穿透：点击落到下层窗口（仅 Windows 生效，其他平台记录日志后忽略）。</summary>
    public void SetMouseThrough(bool through)
    {
        _config.MouseThrough = through;
        WindowEffects.SetMouseThrough(this, through);
    }

    /// <summary>上下浮动开关。</summary>
    public void SetBobbing(bool on)
    {
        _config.Bobbing = on;
        _pet.Bobbing = on;
    }

    /// <summary>水平翻转开关。</summary>
    public void SetFlip(bool on)
    {
        _config.Flip = on;
        _pet.Flip = on;
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

        // 任何一次动作（点击/快捷键/托盘/右键/定时）都让定时器重新计时，
        // 否则"刚点完一个，两秒后定时器又催一个"，看起来像抽风
        ResetAutoPlayClock();

        string idle = PickIdle();
        _model.SetAnimation(0, name, false);
        _model.AddAnimation(0, idle, true, 0f);
    }

    /// <summary>
    /// 播放一个动作。
    ///
    /// <paramref name="useConfigured"/> = true：先看「点击动作」配置，没配才随机（点击走这条）。
    /// = false：完全忽略配置，纯随机（定时动作走这条 —— 它叫"定时随机"，就不该被点击动作绑住）。
    /// </summary>
    public void PlayRandomAction(bool useConfigured = true)
    {
        // 1) 用户明确指定了点击动作 → 尊重配置（仅点击场合）
        if (useConfigured)
        {
            string cfg = _config.ClickAnimation;
            if (!string.IsNullOrEmpty(cfg) && _model.HasAnimation(cfg))
            {
                Logger.Log($"[Action] 使用固定的点击动作 {cfg}");
                PlayAction(cfg);
                return;
            }
        }

        // 2) 没指定 → 从可点动作里随机挑（排除待机/入场/睡眠这类状态型动画）
        var all = _model.AnimationNames;
        var pool = new List<string>();
        foreach (string name in all)
        {
            bool excluded = false;
            foreach (string no in NotClickable)
            {
                if (string.Equals(name, no, StringComparison.OrdinalIgnoreCase)) { excluded = true; break; }
            }
            if (!excluded) pool.Add(name);
        }

        // 全都排除了（极端情况）→ 退回全集
        if (pool.Count == 0) pool.AddRange(all);
        if (pool.Count == 0) return;

        int idx = _rng.Next(pool.Count);
        // 只有两个及以上候选时才需要避重
        if (pool.Count > 1 && string.Equals(pool[idx], _lastRandom, StringComparison.Ordinal))
        {
            idx = (idx + 1 + _rng.Next(pool.Count - 1)) % pool.Count;
        }

        _lastRandom = pool[idx];
        Logger.Log($"[Action] {(useConfigured ? "点击" : "定时")}随机选中 {_lastRandom}"
                 + $"（候选 {pool.Count} 个：{string.Join("/", pool)}）");
        PlayAction(_lastRandom);
    }

    private string PickIdle()
    {
        // 1) 配置指定且该动画存在 → 用配置的
        if (!string.IsNullOrEmpty(_config.IdleAnimation) && _model.HasAnimation(_config.IdleAnimation))
            return _config.IdleAnimation;

        // 2) 未指定（老配置里是空字符串）→ 优先 idle
        if (_model.HasAnimation("idle")) return "idle";

        // 3) 实在没有 → 退回第一个动画
        var all = _model.AnimationNames;
        return all.Count > 0 ? all[0] : "";
    }

    /// <summary>
    /// 桌宠右键菜单。之前右键完全没反应，想换皮肤或退出只能去摸托盘图标，
    /// 而托盘图标经常被系统折叠进"隐藏的图标"里，等于没有入口。
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var miSettings = new MenuItem { Header = "设置…" };
        miSettings.Click += (_, __) => SettingsRequested?.Invoke();
        menu.Items.Add(miSettings);

        // 播放动作子菜单
        var anims = _model.AnimationNames;
        if (anims.Count > 0)
        {
            var miPlay = new MenuItem { Header = "播放动作" };
            var items = new List<MenuItem>();
            foreach (string name in anims)
            {
                string captured = name;                  // 闭包捕获，别用循环变量
                var it = new MenuItem { Header = name };
                it.Click += (_, __) => PlayAction(captured);
                items.Add(it);
            }
            miPlay.ItemsSource = items;
            menu.Items.Add(miPlay);
        }

        menu.Items.Add(new Separator());

        var miHide = new MenuItem { Header = "隐藏桌宠" };
        miHide.Click += (_, __) =>
        {
            SetRenderRunning(false);   // 隐藏时停掉 30fps 空跑
            Hide();
        };
        menu.Items.Add(miHide);

        var miQuit = new MenuItem { Header = "退出" };
        miQuit.Click += (_, __) =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        menu.Items.Add(miQuit);

        return menu;
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

        // 禁止拖动：窗口位置不动，但仍保留"单击触发动作"的判定
        if (_config.LockPosition) return;

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
            // 先拉回工作区：拖到屏幕外（比如拖到左侧 -2000px）就再也点不到了，
            // 只能去删配置文件才能恢复。这一步必须在吸附之前。
            ClampToScreen();

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
        _autoTimer.Stop();
        _autoTimer.Tick -= OnAutoPlayTick;
        _sizeTimer.Stop();
        _sizeTimer.Tick -= OnSizeTick;
        SavePosition();
        ConfigStore.Save(_config);
        _pet.Dispose();
        _model.Dispose();
        base.OnClosed(e);
    }
}
