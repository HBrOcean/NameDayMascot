using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;   // TransformOperations
using Avalonia.Styling;                // Style / Setter / Selector
using NameDayMascot.Config;
using NameDayMascot.Services;

// Avalonia.Input 里也有一个 KeyBinding（XAML 手势绑定用），
// 而下面用 Avalonia.Input 只是为了 Cursor / StandardCursorType。
// 不指定别名的话，所有 KeyBinding 都会报 CS0104「不明确的引用」。
using KeyBinding = NameDayMascot.Config.KeyBinding;

namespace NameDayMascot.Views;

/// <summary>设置面板：桌宠 / 按键 / 关于，三个页签。改动实时生效。</summary>
public class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly PetWindow _pet;
    private readonly Action<string> _onSwitchSkin;

    /// <summary>绑定表变动后通知宿主刷新按键过滤（否则删/改绑定要等关窗口才生效）。</summary>
    private readonly Action? _onBindingsChanged;

    private readonly ComboBox _idleBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _clickBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _keyRows = new() { Spacing = 8 };

    // 皮肤卡片（用于切换后刷新高亮）与卡片内的图片控件（用于异步回填/重建）
    private readonly Dictionary<string, Border> _skinCards = new();
    private readonly Dictionary<string, Image> _skinImages = new();

    // 皮肤页的动态部分：扫描结果变了要整块重建
    private UniformGrid? _skinGrid;
    private TextBlock? _skinCountText;

    // 刷新动画下拉时临时屏蔽回调，避免"自动选中第一项"被误写成用户配置
    private bool _suppressAnimChange;

    public SettingsWindow(AppConfig config, PetWindow pet, Action<string> onSwitchSkin,
                          Action? onBindingsChanged = null)
    {
        _config = config;
        _pet = pet;
        _onSwitchSkin = onSwitchSkin;
        _onBindingsChanged = onBindingsChanged;

        Title = "NameDayMascot 设置";
        Icon = AppIcons.Load("app.png");

        // 等比缩放：内容按设计尺寸 520×620 排版，外层包 Viewbox，
        // 窗口拉大/拉小时整块内容按比例缩放（文字、控件同步），而不是只拉伸留白。
        const double DesignW = 520;
        const double DesignH = 620;

        Width = DesignW;
        Height = DesignH;
        MinWidth = DesignW * 0.75;
        MinHeight = DesignH * 0.75;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ApplyGlassMode(_config.GlassMode);   // 同时决定 Background 与透明级别

        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            Child = new Border
            {
                Width = DesignW,
                Height = DesignH,
                Child = BuildTabs(),
            },
        };

        BuildStyles();
        Closed += (_, __) => ConfigStore.Save(_config);
    }

    /// <summary>
    /// 样式集中在选择器里定义（借 BongoCatNotice 的做法）：
    /// 控件挂 Classes="card" 即继承整套外观，还能用后代选择器批量处理卡片内控件。
    /// </summary>
    private void BuildStyles()
    {
        // 卡片主体：半透明白 + 轻投影，不画边框，避免"框套框"
        var card = new Style(x => x.OfType<Border>().Class("card"));
        card.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.Parse("#F2FFFFFF"))));
        card.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(10)));
        card.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(12, 9)));
        card.Setters.Add(new Setter(Border.MarginProperty, new Thickness(0, 0, 0, 9)));
        card.Setters.Add(new Setter(Border.BoxShadowProperty,
            BoxShadows.Parse("0 2 7 0 #1A5A4020")));

        // 悬停微动效：整卡缩放到 1.015，带过渡
        var cardHover = new Style(x => x.OfType<Border>().Class("card").Class(":pointerover"));
        cardHover.Setters.Add(new Setter(Border.RenderTransformProperty,
            TransformOperations.Parse("scale(1.015)")));
        Styles.Add(card);

        // 卡片内的输入类控件自动去框（后代选择器，不用逐个设置）
        var innerText = new Style(x => x.OfType<Border>().Class("card")
                                          .Descendant().OfType<TextBox>());
        innerText.Setters.Add(new Setter(TextBox.BackgroundProperty, Brushes.Transparent));
        innerText.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(0)));
        innerText.Setters.Add(new Setter(TextBox.MinHeightProperty, 26.0));

        var innerCombo = new Style(x => x.OfType<Border>().Class("card")
                                           .Descendant().OfType<ComboBox>());
        innerCombo.Setters.Add(new Setter(ComboBox.BackgroundProperty, Brushes.Transparent));
        innerCombo.Setters.Add(new Setter(ComboBox.BorderThicknessProperty, new Thickness(0)));

        Styles.Add(innerText);
        Styles.Add(innerCombo);

        // 按键行卡片：比 Group 卡片更浅、更紧凑，一页能放下更多条
        var keyrow = new Style(x => x.OfType<Border>().Class("keyrow"));
        keyrow.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.Parse("#F7FFFFFF"))));
        keyrow.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));
        keyrow.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(10, 8)));
        Styles.Add(keyrow);

        Styles.Add(cardHover);
    }

    private Control BuildTabs()
    {
        var tabs = new TabControl { Margin = new Thickness(8, 6, 8, 0) };
        tabs.Items.Add(new TabItem { Header = "皮肤", Content = BuildSkinPage() });
        tabs.Items.Add(new TabItem { Header = "桌宠", Content = BuildPetPage() });
        tabs.Items.Add(new TabItem { Header = "按键", Content = BuildKeyPage() });
        tabs.Items.Add(new TabItem { Header = "关于", Content = BuildAboutPage() });
        return tabs;
    }

    // —— 皮肤页：卡片网格 ——
    private Control BuildSkinPage()
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };

        _skinCountText = new TextBlock
        {
            Text = SkinHeaderText(),
            FontSize = 12,
            Opacity = 0.7,
            Foreground = new SolidColorBrush(Color.Parse("#5A4632")),
        };
        panel.Children.Add(_skinCountText);

        // 用 UniformGrid 而不是 WrapPanel：卡片按列等分宽度，
        // 不会因为边框/字体多占一两个像素就把最后一张挤到下一行。
        // Columns 固定 3、Rows 留 0 —— 行数由子元素个数自动算，扫到几套都能正确折行。
        _skinGrid = new UniformGrid { Columns = 3 };
        RebuildSkinGrid();
        panel.Children.Add(_skinGrid);

        // 皮肤资源管理
        var manage = new StackPanel { Spacing = 8 };
        manage.Children.Add(new TextBlock
        {
            Text = "想换自己的模型？在皮肤文件夹里建一个子目录，放入同名的 .atlas / .skel / 图集即可，"
                   + "重启或点「刷新列表」就会被扫描到。目录里放一个 display.txt（单行）可自定义显示名。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#6B5540")),
        });

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var openDirBtn = new Button { Content = "打开皮肤文件夹" };
        openDirBtn.Click += (_, __) => OpenSkinFolder();
        btnRow.Children.Add(openDirBtn);

        var refreshBtn = new Button { Content = "刷新列表" };
        ToolTip.SetTip(refreshBtn, "重新扫描皮肤目录，把新加入或已删除的皮肤同步到列表");
        ToolTip.SetShowDelay(refreshBtn, 300);
        refreshBtn.Click += (_, __) =>
        {
            SkinCatalog.Invalidate();
            RebuildSkinGrid();
            RefreshSkinSelection();
        };
        btnRow.Children.Add(refreshBtn);

        var rebuildBtn = new Button { Content = "重建预览图" };
        rebuildBtn.Click += async (_, __) =>
        {
            rebuildBtn.IsEnabled = false;
            rebuildBtn.Content = "重建中…";
            try
            {
                SkinThumbnails.ClearCache();
                await ReloadSkinThumbnailsAsync();
            }
            catch (Exception ex) { Logger.Error("[Thumb] 重建预览图失败", ex); }
            finally
            {
                rebuildBtn.Content = "重建预览图";
                rebuildBtn.IsEnabled = true;
            }
        };
        btnRow.Children.Add(rebuildBtn);

        manage.Children.Add(btnRow);
        panel.Children.Add(Group("皮肤资源", manage));

        return new ScrollViewer { Content = panel };
    }

    /// <summary>按当前扫描结果整块重建卡片（皮肤增删后调用）。</summary>
    private void RebuildSkinGrid()
    {
        if (_skinGrid == null) return;

        _skinGrid.Children.Clear();
        _skinCards.Clear();
        _skinImages.Clear();

        foreach (var entry in SkinCatalog.All)
            _skinGrid.Children.Add(BuildSkinCard(entry));

        if (_skinCountText != null) _skinCountText.Text = SkinHeaderText();
    }

    private static string SkinHeaderText()
    {
        var all = SkinCatalog.All;
        int usable = all.Count(e => e.IsUsable);
        return usable == all.Count
            ? $"{all.Count} 套装扮 · 点击卡片立即切换"
            : $"{all.Count} 套装扮（{usable} 套可用）· 点击卡片立即切换";
    }

    private Control BuildSkinCard(SkinEntry entry)
    {
        string skinId = entry.Id;
        bool usable = entry.IsUsable;

        // 缩略图还没出来时，这块浅色圆角块就是占位；出图后被图片自然盖住
        var imgHost = new Border
        {
            Height = 148,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#125A4020")),
        };

        if (usable)
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            imgHost.Child = image;
            _skinImages[skinId] = image;

            // 缓存命中就同步出图（读一张小 PNG，几毫秒）；
            // 未命中扔到线程池渲染，绝不阻塞界面构造。
            var cached = SkinThumbnails.TryGetCached(skinId);
            if (cached != null) image.Source = cached;
            else _ = LoadThumbnailAsync(skinId, image);
        }
        else
        {
            imgHost.Child = new TextBlock
            {
                Text = "资源缺失",
                FontSize = 12,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#8A7560")),
            };
        }

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(imgHost);
        stack.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.Parse(usable ? "#5A4632" : "#9A8875")),
        });

        bool selected = string.Equals(skinId, _config.Skin, StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Child = stack,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            Background = SkinCardBg(selected),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = SkinCardBorder(selected),
            Opacity = usable ? 1.0 : 0.55,
        };

        if (usable)
        {
            card.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(card, selected ? "当前正在使用" : "点击切换到这套装扮");
            card.PointerPressed += (_, __) =>
            {
                if (string.Equals(_config.Skin, skinId, StringComparison.OrdinalIgnoreCase)) return;
                _onSwitchSkin(skinId);
                RefreshSkinSelection();
            };
        }
        else
        {
            ToolTip.SetTip(card, $"目录 {entry.Dir ?? "(未找到)"} 里缺少 {skinId}.atlas 或 {skinId}.skel");
            ToolTip.SetShowDelay(card, 200);
        }

        _skinCards[skinId] = card;
        return card;
    }

    /// <summary>
    /// 后台渲染缩略图。图集是 512×1024，解一套要几十毫秒，
    /// 三套连着解会把 UI 线程卡住小半秒，所以放到线程池做。
    /// 渲染尺寸是显示的 2 倍，任何 DPI 缩放下都不糊。
    /// </summary>
    private static async Task LoadThumbnailAsync(string skinId, Image target)
    {
        try
        {
            string? dir = NameDayMascot.App.FindCharacterDir(skinId);
            if (dir == null) return;

            var bmp = await Task.Run(() => SkinThumbnails.Get(skinId, dir));
            if (bmp != null) target.Source = bmp;
        }
        catch (Exception ex) { Logger.Error($"[Thumb] 异步渲染 {skinId} 失败", ex); }
    }

    /// <summary>清缓存后重新载入所有预览图。</summary>
    private async Task ReloadSkinThumbnailsAsync()
    {
        foreach (var kv in _skinImages)
        {
            string? dir = NameDayMascot.App.FindCharacterDir(kv.Key);
            if (dir == null) continue;
            try
            {
                var bmp = await Task.Run(() => SkinThumbnails.Get(kv.Key, dir));
                if (bmp != null) kv.Value.Source = bmp;
            }
            catch (Exception ex) { Logger.Error($"[Thumb] 重建 {kv.Key} 失败", ex); }
        }
    }

    /// <summary>用资源管理器打开皮肤目录（跨平台：Windows 资源管理器 / macOS Finder / Linux 文件管理器）。</summary>
    private static void OpenSkinFolder()
    {
        try
        {
            string target = SkinCatalog.RootCandidates().First();

            if (!Directory.Exists(target))
            {
                // 发布包里可能还没有 characters 子目录，这时开它的上一级（Assets/）
                string? parent = Path.GetDirectoryName(target);
                if (parent != null && Directory.Exists(parent)) target = parent;
                else
                {
                    Logger.Log($"[Skin] 皮肤目录不存在：{target}");
                    return;
                }
            }

            // UseShellExecute 让系统自己挑默认文件管理器，不用分平台判断
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("[Skin] 打开皮肤文件夹失败", ex); }
    }

    private static IBrush SkinCardBg(bool selected)
        => new SolidColorBrush(Color.Parse(selected ? "#FFFFFF" : "#D9FFFFFF"));

    private static IBrush SkinCardBorder(bool selected)
        => new SolidColorBrush(Color.Parse(selected ? "#B86C3A" : "#335A4020"));

    /// <summary>切换皮肤后刷新卡片高亮（只改视觉，不动配置）。</summary>
    private void RefreshSkinSelection()
    {
        foreach (var kv in _skinCards)
        {
            bool sel = string.Equals(kv.Key, _config.Skin, StringComparison.OrdinalIgnoreCase);
            var card = kv.Value;
            card.Background = SkinCardBg(sel);
            card.BorderThickness = new Thickness(sel ? 2 : 1);
            card.BorderBrush = SkinCardBorder(sel);
            if (card.Opacity > 0.9)   // 资源缺失的卡片保持"不可用"的提示，不覆盖
                ToolTip.SetTip(card, sel ? "当前正在使用" : "点击切换到这套装扮");
        }
    }

    // —— 桌宠页 ——
    private Control BuildPetPage()
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };

        // 层级：最上层 / 最下层 / 自由
        var zBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        zBox.ItemsSource = new List<string> { "最上层", "最下层", "自由" };
        zBox.SelectedIndex = _config.ZOrder switch { "bottom" => 1, "free" => 2, _ => 0 };
        zBox.SelectionChanged += (_, __) =>
        {
            string z = zBox.SelectedIndex switch { 1 => "bottom", 2 => "free", _ => "top" };
            _pet.SetZOrder(z);
        };
        panel.Children.Add(Group("层级", Row("窗口位置", zBox)));

        // 位置自救：拔副屏 / 改分辨率后桌宠可能落在屏幕外，这是唯一的恢复入口
        var posPanel = new StackPanel { Spacing = 8 };
        posPanel.Children.Add(new TextBlock
        {
            Text = "桌宠跑到看不见的地方时（比如拔掉副屏、换过分辨率），点这里把它拉回主屏右下角。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#6B5540")),
        });
        var resetPosBtn = new Button
        {
            Content = "重置到右下角",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        resetPosBtn.Click += (_, __) => _pet.ResetPosition();
        posPanel.Children.Add(resetPosBtn);
        panel.Children.Add(Group("找回位置", posPanel));

        // 缩放：滑块 + 右侧实时百分比
        var scalePercent = new TextBlock
        {
            Text = FormatPercent(_config.Scale),
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var scaleGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,52") };
        var scaleSlider = MakeSlider(_config.Scale, 0.05, 2.0, v =>
        {
            _config.Scale = v;
            _pet.SetScale(v);
            scalePercent.Text = FormatPercent(v);
        }, 0.01);   // 吸附到 1% 档，配置里存的就是整数百分比
        scaleGrid.Children.Add(scaleSlider);
        Grid.SetColumn(scalePercent, 1);
        scaleGrid.Children.Add(scalePercent);

        panel.Children.Add(Group("缩放", scaleGrid));

        var toggles = new StackPanel { Spacing = 8 };
        toggles.Children.Add(MakeCheck("在任务栏显示图标", _config.ShowInTaskbar,
            v => _pet.SetShowInTaskbar(v), "在 Windows 任务栏上显示一个图标，方便从一堆窗口里切回来"));
        toggles.Children.Add(MakeCheck("禁止拖动（锁定位置）", _config.LockPosition,
            v => _pet.SetLockPosition(v), "锁住窗口位置，怎么拖都不动；单击仍然会触发动作"));
        toggles.Children.Add(MakeCheck("鼠标穿透（点击落到桌面）", _config.MouseThrough,
            v => _pet.SetMouseThrough(v), "点击直接穿到下层窗口。开着它就没法拖动角色了（仅 Windows）"));
        toggles.Children.Add(MakeCheck("上下浮动", _config.Bobbing,
            v => _pet.SetBobbing(v), "角色会轻微上下漂浮，像在呼吸"));
        toggles.Children.Add(MakeCheck("水平翻转", _config.Flip,
            v => _pet.SetFlip(v), "左右镜像，让角色面朝另一边"));
        toggles.Children.Add(MakeCheck("窗口随动作变化", _config.DynamicSize,
            v => _pet.SetDynamicSize(v),
            "动作幅度变大时窗口跟着长大，动作结束再慢慢缩回，不会把角色裁掉。"
            + "关掉则窗口尺寸恒定（更省 CPU，拖动时也更稳）"));
        toggles.Children.Add(MakeCheck("允许点击触发动画", _config.Clickable,
            v => _config.Clickable = v, "单击角色时随机播放一个动作"));
        toggles.Children.Add(MakeCheck("动作锁定（播完再回待机）", _config.LockOnAction,
            v => _config.LockOnAction = v, "动作必须完整播完才响应下一个请求，避免动画被打断"));
        toggles.Children.Add(MakeCheck("屏幕边缘吸附", _config.SnapToEdge,
            v => _config.SnapToEdge = v,
            "拖动结束后自动贴到最近的屏幕边缘（默认关闭）。关掉后窗口可以随意摆放，也可以拖到屏幕外"));
        toggles.Children.Add(MakeCheck("开机自启", _config.AutoStart, v =>
        {
            _config.AutoStart = v;
            AutoStartService.SetEnabled(v);
        }, "随系统启动自动运行"));
        panel.Children.Add(Group("行为", toggles));

        // —— 定时随机动作 ——
        var autoPanel = new StackPanel { Spacing = 8 };
        autoPanel.Children.Add(MakeCheck("定时随机动作", _config.AutoPlay,
            v => _pet.SetAutoPlay(v),
            "每隔一段时间自动随机播一个动作，让角色看起来是活的。"
            + "点角色、按快捷键、从托盘或右键选动作，都会让它重新计时。"));

        var autoSecText = new TextBlock
        {
            Text = FormatSeconds(_config.AutoPlayIntervalSeconds),
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var autoGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,52") };
        var autoSlider = MakeSlider(_config.AutoPlayIntervalSeconds, 5, 180, v =>
        {
            _pet.SetAutoPlayInterval(v);
            autoSecText.Text = FormatSeconds(v);
        }, 5);   // 吸附到 5 秒档
        autoGrid.Children.Add(autoSlider);
        Grid.SetColumn(autoSecText, 1);
        autoGrid.Children.Add(autoSecText);

        autoPanel.Children.Add(Row("间隔", autoGrid));
        panel.Children.Add(Group("定时动作", autoPanel));

        // 外观：玻璃模式（影响本设置面板的透明度）
        var appearance = new StackPanel { Spacing = 8 };
        appearance.Children.Add(MakeCheck("玻璃模式（半透明面板）", _config.GlassMode, v =>
        {
            _config.GlassMode = v;
            ApplyGlassMode(v);
        }));
        panel.Children.Add(Group("外观", appearance));

        var anims = new List<string>(_pet.AnimationNames);
        var animPanel = new StackPanel { Spacing = 10 };
        animPanel.Children.Add(Row("待机动画", MakeAnimBox(_idleBox, anims, _config.IdleAnimation,
            v => _config.IdleAnimation = v, allowRandom: false)));
        animPanel.Children.Add(Row("点击动作", MakeAnimBox(_clickBox, anims, _config.ClickAnimation,
            v => _config.ClickAnimation = v, allowRandom: true)));
        panel.Children.Add(Group("动作", animPanel));

        return new ScrollViewer { Content = panel };
    }

    // —— 按键页 ——
    private Control BuildKeyPage()
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };

        var hint = new TextBlock
        {
            Text = "按键触发动作（全局监听，多个动作随机播一个）",
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        ToolTip.SetTip(hint,
            "按键格式：修饰键 + Vc键名\n" +
            "例：Ctrl+Vc1、Ctrl+Shift+VcA、VcF1（无修饰键）\n" +
            "动作可以填多个，用逗号分隔，触发时随机挑一个。");
        ToolTip.SetShowDelay(hint, 300);
        panel.Children.Add(hint);

        foreach (var kb in _config.KeyBindings)
            _keyRows.Children.Add(BuildKeyRow(kb));
        panel.Children.Add(_keyRows);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var addBtn = new Button { Content = "＋ 添加" };
        addBtn.Click += (_, __) => AddBinding();
        btnRow.Children.Add(addBtn);

        var resetBtn = new Button { Content = "恢复默认" };
        ToolTip.SetTip(resetBtn, "清空当前所有绑定，换回 10 条默认快捷键（Ctrl+1/2/D/Q/B/G/H/I/P/S）");
        ToolTip.SetShowDelay(resetBtn, 300);
        resetBtn.Click += (_, __) =>
        {
            _config.KeyBindings.Clear();
            _config.KeyBindings.AddRange(AppConfig.CreateDefaultBindings());
            _config.DefaultKeysApplied = true;
            ConfigStore.Save(_config);

            _keyRows.Children.Clear();
            foreach (var kb in _config.KeyBindings)
                _keyRows.Children.Add(BuildKeyRow(kb));

            _onBindingsChanged?.Invoke();
        };
        btnRow.Children.Add(resetBtn);

        panel.Children.Add(btnRow);

        return new ScrollViewer { Content = panel };
    }

    /// <summary>
    /// 一条按键绑定 = 两行卡片。
    /// 第一行：按键 + 启用 + 删除；第二行：动作下拉 + 追加；下面一排是已选动作的可删标签。
    /// 520 设计宽度下一行塞不下"按键 + 两个下拉 + 两个按钮"，所以纵向拆开。
    /// </summary>
    private Control BuildKeyRow(KeyBinding kb)
    {
        var anims = new List<string>(_pet.AnimationNames);

        // ———— 第一行：按键 / 启用 / 删除 ————
        var keyBox = new TextBox
        {
            Text = kb.Key,
            Watermark = "如 Ctrl+Vc1",
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 键名格式校验：修饰键之后必须是 Vc 开头的键名（SharpHook 的 KeyCode 命名）。
        // 写成 "Ctrl+v" 这种"看着对但不生效"的形式时背景标红，否则用户会以为快捷键坏了。
        void ValidateKey()
        {
            string text = (keyBox.Text ?? "").Trim();
            bool ok = text.Length == 0 || KeyFormatOk(text);
            keyBox.Background = ok
                ? Brushes.Transparent
                : new SolidColorBrush(Color.Parse("#22D0503C"));
            ToolTip.SetTip(keyBox, ok ? null : "格式应为 修饰键+Vc键名，例如 Ctrl+Vc1、Ctrl+Shift+VcA");
            ToolTip.SetShowDelay(keyBox, 200);
        }

        keyBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            kb.Key = keyBox.Text ?? "";
            ValidateKey();
            _onBindingsChanged?.Invoke();
        };
        ValidateKey();

        var enableBox = new CheckBox
        {
            IsChecked = kb.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6, 0),
        };
        ToolTip.SetTip(enableBox, "取消勾选即临时停用这条绑定，不用删掉");
        ToolTip.SetShowDelay(enableBox, 300);
        enableBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != ToggleButton.IsCheckedProperty) return;
            kb.Enabled = enableBox.IsChecked == true;
            ConfigStore.Save(_config);
            _onBindingsChanged?.Invoke();
        };

        var delBtn = new Button { Content = "✕", Padding = new Thickness(4) };
        Border? card = null;
        delBtn.Click += (_, __) =>
        {
            _config.KeyBindings.Remove(kb);
            if (card != null) _keyRows.Children.Remove(card);
            ConfigStore.Save(_config);
            _onBindingsChanged?.Invoke();
        };

        var line1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,auto,32") };
        line1.Children.Add(keyBox);
        Grid.SetColumn(enableBox, 1);
        line1.Children.Add(enableBox);
        Grid.SetColumn(delBtn, 2);
        line1.Children.Add(delBtn);

        // ———— 第二行：动作下拉 + 追加 ————
        var pickBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = "选一个动作",
        };

        var addBtn = new Button { Content = "＋", Padding = new Thickness(8, 4) };
        ToolTip.SetTip(addBtn, "把选中的动作加入这条绑定。可以加多个，触发时随机播一个");
        ToolTip.SetShowDelay(addBtn, 300);

        var line2 = new Grid { ColumnDefinitions = new ColumnDefinitions("auto,*,32") };
        line2.Children.Add(new TextBlock
        {
            Text = "动作",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12,
            Opacity = 0.8,
        });
        Grid.SetColumn(pickBox, 1);
        line2.Children.Add(pickBox);
        Grid.SetColumn(addBtn, 2);
        line2.Children.Add(addBtn);

        // ———— 已选动作标签 ————
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        void Persist()
        {
            ConfigStore.Save(_config);
            _onBindingsChanged?.Invoke();
        }

        // 下拉项 = 当前皮肤的动画名 + 这条绑定里残留的旧动作（换过皮肤后可能已不存在）
        void FillPickBox()
        {
            var items = new List<string>(anims);
            foreach (string a in kb.Actions)
                if (!items.Contains(a)) items.Add(a);
            pickBox.ItemsSource = items;
            if (items.Count > 0) pickBox.SelectedIndex = 0;
        }

        void RebuildChips()
        {
            chips.Children.Clear();

            if (kb.Actions.Count == 0)
            {
                chips.Children.Add(new TextBlock
                {
                    Text = "还没选动作 —— 触发时不会播放",
                    FontSize = 11,
                    Opacity = 0.55,
                });
                return;
            }

            // ToList：删除按钮的 Click 会改 kb.Actions，遍历时先取快照
            foreach (string act in kb.Actions.ToList())
            {
                bool known = anims.Contains(act);

                var label = new TextBlock
                {
                    Text = act,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.Parse(known ? "#4A3728" : "#B4553F")),
                };
                if (!known)
                {
                    ToolTip.SetTip(label, "当前皮肤里没有这个动画名（换皮肤后失效了），点 × 移除");
                    ToolTip.SetShowDelay(label, 200);
                }

                var close = new Button
                {
                    Content = "×",
                    FontSize = 11,
                    Padding = new Thickness(3, 0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(close, "从这条绑定里移除这个动作");
                close.Click += (_, __) =>
                {
                    kb.Actions.Remove(act);
                    RebuildChips();
                    Persist();
                };

                var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                inner.Children.Add(label);
                inner.Children.Add(close);

                chips.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse(known ? "#1A5A4020" : "#22B4553F")),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 2, 2),
                    Margin = new Thickness(0, 0, 6, 4),
                    Child = inner,
                });
            }
        }

        addBtn.Click += (_, __) =>
        {
            if (pickBox.SelectedItem is not string act) return;
            if (kb.Actions.Contains(act)) return;
            kb.Actions.Add(act);
            RebuildChips();
            Persist();
        };

        FillPickBox();
        RebuildChips();

        // ———— 组装 ————
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(line1);
        stack.Children.Add(line2);
        stack.Children.Add(chips);

        card = new Border { Child = stack };
        card.Classes.Add("keyrow");
        return card!;
    }

    /// <summary>键名是否形如 [Ctrl+][Alt+][Shift+]VcXxx。</summary>
    private static bool KeyFormatOk(string text)
    {
        string key = text;
        // 依次剥掉修饰键前缀（不区分大小写，允许 Ctrl/Ctrl+ 混写）
        foreach (var mod in new[] { "ctrl+", "alt+", "shift+" })
        {
            while (key.StartsWith(mod, StringComparison.OrdinalIgnoreCase))
                key = key.Substring(mod.Length);
        }
        key = key.Trim();
        return key.Length > 2 && key.StartsWith("Vc", StringComparison.Ordinal);
    }

    private void AddBinding()
    {
        var anims = _pet.AnimationNames;
        string act = anims.Count > 0 ? anims[0] : "";
        var kb = new KeyBinding { Key = "", Actions = new List<string> { act }, Enabled = true };
        _config.KeyBindings.Add(kb);
        _keyRows.Children.Add(BuildKeyRow(kb));
        ConfigStore.Save(_config);
        _onBindingsChanged?.Invoke();
    }

    // —— 关于页 ——
    private Control BuildAboutPage()
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "NameDayMascot",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "C# 版 · Avalonia + spine-csharp + SkiaSharp",
            Opacity = 0.75,
        });

        // 版本号 + 检查更新
        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        updateRow.Children.Add(new TextBlock
        {
            Text = "v" + UpdateChecker.CurrentVersion,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
        });

        var updateBtn = new Button { Content = "检查更新" };
        var updateResult = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 230,
        };
        var openReleaseBtn = new Button
        {
            Content = "打开发布页",
            Padding = new Thickness(8, 2),
            IsVisible = false,
        };

        // 发布页地址：只有检查出更新时才有值。
        // 处理器只注册一次——原来写成"每次检查都 += 一个局部函数，减又减不掉旧的"，
        // 点三次「检查更新」后再点「打开发布页」会连开好几个浏览器标签。
        string releaseUrl = "";

        openReleaseBtn.Click += (_, __) =>
        {
            if (string.IsNullOrWhiteSpace(releaseUrl)) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = releaseUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                updateResult.Text = "无法打开浏览器：" + releaseUrl;
                Logger.Error("[Update] 打开发布页失败", ex);
            }
        };

        updateBtn.Click += async (_, __) =>
        {
            updateBtn.IsEnabled = false;
            openReleaseBtn.IsVisible = false;
            updateResult.Text = "正在检查…";
            try
            {
                var r = await UpdateChecker.CheckAsync();
                if (r.Error != null)
                {
                    updateResult.Text = "检查失败：" + r.Error;
                }
                else if (r.HasUpdate)
                {
                    updateResult.Text = $"发现新版本 {r.Latest}（当前 {r.Current}）";
                    releaseUrl = r.Url;
                    openReleaseBtn.IsVisible = true;
                }
                else
                {
                    updateResult.Text = $"已是最新（{r.Current}）";
                }
            }
            finally
            {
                updateBtn.IsEnabled = true;
            }
        };

        updateRow.Children.Add(updateBtn);
        updateRow.Children.Add(openReleaseBtn);
        panel.Children.Add(updateRow);
        panel.Children.Add(updateResult);
        panel.Children.Add(new TextBlock
        {
            Text = "一只住在桌面上的命名日 Q 版角色。单击它会动，也可以绑定全局按键。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "模型来源：UTTU · Spine 运行时：esotericsoftware/spine-runtimes",
            Opacity = 0.55,
            FontSize = 11,
        });
        return panel;
    }

    // —— 小构件 ——
    private static Border Group(string title, Control inner)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#5A4632")),
        });
        stack.Children.Add(inner);

        var border = new Border { Child = stack };
        border.Classes.Add("card");   // 外观全部来自 BuildStyles()
        return border;
    }

    private static Control Row(string label, Control inner)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*") };
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(inner, 1);
        g.Children.Add(inner);
        return g;
    }

    /// <summary>
    /// 玻璃模式：面板半透明 + 毛玻璃。Windows 上由 DWM 合成 AcrylicBlur，
    /// 其他平台若不支持会自动退化为 Transparent（仍然半透明）。
    /// </summary>
    private void ApplyGlassMode(bool on)
    {
        if (on)
        {
            TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Transparent,
            };
            Background = new SolidColorBrush(Color.Parse("#A6FFFFFF"));   // 65% 白雾
        }
        else
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            // 暖黄渐变（与 BongoCatNotice 同款：上浅下深）
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#FFF8C9"), 0.0),
                    new GradientStop(Color.Parse("#FFD19A"), 1.0),
                },
            };
        }
    }

    /// <summary>缩放值转百分比文本（0.15 → "15%"）。</summary>
    private static string FormatPercent(double scale)
        => Math.Round(scale * 100).ToString("0") + "%";

    /// <summary>秒数文本（20 → "20 秒"）。</summary>
    private static string FormatSeconds(double seconds)
        => Math.Round(seconds).ToString("0") + " 秒";

    /// <summary>
    /// 滑块。<paramref name="step"/> &gt; 0 时把值吸附到档位上：
    /// 拖动本来会得到 0.1533 这种脏值，界面显示"15%"但实际存 15.33%，
    /// 界面上写着 15% 就该真的是 15%。
    /// </summary>
    private static Control MakeSlider(double value, double min, double max, Action<double> onChanged,
                                     double step = 0)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;

            double v = slider.Value;
            if (step > 0)
            {
                // 先吸附到档位，再把浮点残差削掉（0.30000000000000004 → 0.3）
                v = Math.Round(Math.Round(v / step) * step, 4);
            }
            onChanged(v);
        };
        return slider;
    }

    private static Control MakeCheck(string label, bool value, Action<bool> onChanged, string? tip = null)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        if (!string.IsNullOrWhiteSpace(tip))
        {
            ToolTip.SetTip(cb, tip);
            ToolTip.SetShowDelay(cb, 400);
        }
        cb.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty) onChanged(cb.IsChecked == true);
        };
        return cb;
    }

    /// <summary>
    /// 皮肤切换后刷新界面：动画下拉（新旧角色的动画名不通用，旧列表会让用户选到不存在的动画）
    /// 和皮肤卡片的选中高亮。任何入口触发 SwitchSkin 都会走到这里，不会漏。
    /// </summary>
    public void RefreshAnimations()
    {
        var anims = new List<string>(_pet.AnimationNames);
        _suppressAnimChange = true;
        try
        {
            RefreshAnimBox(_idleBox, anims, _config.IdleAnimation, allowRandom: false);
            RefreshAnimBox(_clickBox, anims, _config.ClickAnimation, allowRandom: true);
        }
        finally
        {
            _suppressAnimChange = false;
        }

        RefreshSkinSelection();
        RebuildKeyRows();
    }

    /// <summary>
    /// 重建按键行。动作下拉的候选项来自当前皮肤的动画名，
    /// 换皮肤后旧名字全部失效，必须重建成新的（残留的旧动作会以红字标签保留，提示用户清理）。
    /// </summary>
    private void RebuildKeyRows()
    {
        if (_keyRows.Children.Count == 0) return;   // 按键页还没被打开过
        _keyRows.Children.Clear();
        foreach (var kb in _config.KeyBindings)
            _keyRows.Children.Add(BuildKeyRow(kb));
    }

    private static void RefreshAnimBox(ComboBox box, List<string> anims, string current, bool allowRandom = false)
    {
        var items = new List<string>();
        if (allowRandom) items.Add(RandomOption);
        items.AddRange(anims);

        box.ItemsSource = items;
        int idx = (allowRandom && string.IsNullOrEmpty(current))
            ? 0
            : items.IndexOf(current);
        box.SelectedIndex = idx >= 0 ? idx : (items.Count > 0 ? 0 : -1);
    }

    /// <summary>动画下拉里代表「不指定，交给程序随机挑」的伪选项。</summary>
    private const string RandomOption = "(随机)";

    private Control MakeAnimBox(ComboBox box, List<string> anims, string current, Action<string> onChanged,
                                bool allowRandom = false)
    {
        var items = new List<string>();
        if (allowRandom) items.Add(RandomOption);
        items.AddRange(anims);

        _suppressAnimChange = true;
        try
        {
            box.ItemsSource = items;
            int idx = (allowRandom && string.IsNullOrEmpty(current))
                ? 0
                : items.IndexOf(current);
            box.SelectedIndex = idx >= 0 ? idx : (items.Count > 0 ? 0 : -1);
        }
        finally { _suppressAnimChange = false; }

        box.SelectionChanged += (_, __) =>
        {
            if (_suppressAnimChange) return;

            // 选「(随机)」= 把配置写回空字符串
            if (allowRandom && box.SelectedIndex == 0) { onChanged(""); return; }

            if (box.SelectedItem is string s && s != RandomOption) onChanged(s);
        };
        return box;
    }
}
