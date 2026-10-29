using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NameDayMascot.Config;
using NameDayMascot.Services;

namespace NameDayMascot.Views;

/// <summary>设置面板：桌宠 / 按键 / 关于，三个页签。改动实时生效。</summary>
public class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly PetWindow _pet;
    private readonly Action<string> _onSwitchSkin;

    private readonly ComboBox _idleBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _clickBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _keyRows = new() { Spacing = 8 };

    public SettingsWindow(AppConfig config, PetWindow pet, Action<string> onSwitchSkin)
    {
        _config = config;
        _pet = pet;
        _onSwitchSkin = onSwitchSkin;

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
        Background = new SolidColorBrush(Color.Parse("#F7F3ED"));

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
        Closed += (_, __) => ConfigStore.Save(_config);
    }

    private Control BuildTabs()
    {
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "桌宠", Content = BuildPetPage() });
        tabs.Items.Add(new TabItem { Header = "按键", Content = BuildKeyPage() });
        tabs.Items.Add(new TabItem { Header = "关于", Content = BuildAboutPage() });
        return tabs;
    }

    // —— 桌宠页 ——
    private Control BuildPetPage()
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };

        // 皮肤（三套）
        var skinBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var skinItems = new List<string>();
        foreach (var id in AppConfig.AvailableSkins) skinItems.Add(AppConfig.SkinDisplayName(id));
        skinBox.ItemsSource = skinItems;
        int skinIdx = Array.IndexOf(AppConfig.AvailableSkins, _config.Skin);
        skinBox.SelectedIndex = skinIdx >= 0 ? skinIdx : 0;
        skinBox.SelectionChanged += (_, __) =>
        {
            int i = skinBox.SelectedIndex;
            if (i >= 0 && i < AppConfig.AvailableSkins.Length)
                _onSwitchSkin(AppConfig.AvailableSkins[i]);
        };
        panel.Children.Add(Group("皮肤", Row("选择皮肤", skinBox)));

        panel.Children.Add(Group("缩放", MakeSlider(_config.Scale, 0.05, 2.0, v =>
        {
            _config.Scale = v;
            _pet.SetScale(v);
        })));

        var toggles = new StackPanel { Spacing = 8 };
        toggles.Children.Add(MakeCheck("窗口置顶", _config.Topmost, v => _pet.SetTopmost(v)));
        toggles.Children.Add(MakeCheck("白卡片背景（调试/保底）", _config.ShowWhiteCard, v => _pet.SetWhiteCard(v)));
        toggles.Children.Add(MakeCheck("允许点击触发动画", _config.Clickable, v => _config.Clickable = v));
        toggles.Children.Add(MakeCheck("动作锁定（播完再回待机）", _config.LockOnAction, v => _config.LockOnAction = v));
        toggles.Children.Add(MakeCheck("屏幕边缘吸附", _config.SnapToEdge, v => _config.SnapToEdge = v));
        toggles.Children.Add(MakeCheck("开机自启", _config.AutoStart, v =>
        {
            _config.AutoStart = v;
            AutoStartService.SetEnabled(v);
        }));
        panel.Children.Add(Group("行为", toggles));

        var anims = new List<string>(_pet.AnimationNames);
        var animPanel = new StackPanel { Spacing = 10 };
        animPanel.Children.Add(Row("待机动画", MakeAnimBox(_idleBox, anims, _config.IdleAnimation,
            v => _config.IdleAnimation = v)));
        animPanel.Children.Add(Row("点击动作", MakeAnimBox(_clickBox, anims, _config.ClickAnimation,
            v => _config.ClickAnimation = v)));
        panel.Children.Add(Group("动作", animPanel));

        return new ScrollViewer { Content = panel };
    }

    // —— 按键页 ——
    private Control BuildKeyPage()
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "按键触发动作（全局监听，多个动作随机播一个）",
            Opacity = 0.7,
            FontSize = 12,
        });

        foreach (var kb in _config.KeyBindings)
            _keyRows.Children.Add(BuildKeyRow(kb));
        panel.Children.Add(_keyRows);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addBtn = new Button { Content = "＋ 添加" };
        addBtn.Click += (_, __) => AddBinding();
        btnRow.Children.Add(addBtn);
        panel.Children.Add(btnRow);

        return new ScrollViewer { Content = panel };
    }

    private Control BuildKeyRow(KeyBinding kb)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("104,*,30,32") };

        var keyBox = new TextBox { Text = kb.Key, Watermark = "如 Ctrl+Vc1" };
        keyBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) kb.Key = keyBox.Text ?? "";
        };
        g.Children.Add(keyBox);

        var actBox = new TextBox { Text = string.Join(",", kb.Actions), Watermark = "动作名，逗号分隔" };
        actBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                kb.Actions = new List<string>((actBox.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        };
        Grid.SetColumn(actBox, 1);
        g.Children.Add(actBox);

        // 启用开关（之前这个字段在逻辑里生效，但界面上没有入口）
        var enableBox = new CheckBox
        {
            IsChecked = kb.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0),
        };
        enableBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                kb.Enabled = enableBox.IsChecked == true;
                ConfigStore.Save(_config);
            }
        };
        Grid.SetColumn(enableBox, 2);
        g.Children.Add(enableBox);

        var delBtn = new Button { Content = "✕", Padding = new Thickness(4) };
        Grid.SetColumn(delBtn, 3);
        delBtn.Click += (_, __) =>
        {
            _config.KeyBindings.Remove(kb);
            _keyRows.Children.Remove(g);
            ConfigStore.Save(_config);
        };
        g.Children.Add(delBtn);

        return g;
    }

    private void AddBinding()
    {
        var anims = _pet.AnimationNames;
        string act = anims.Count > 0 ? anims[0] : "";
        var kb = new KeyBinding { Key = "", Actions = new List<string> { act }, Enabled = true };
        _config.KeyBindings.Add(kb);
        _keyRows.Children.Add(BuildKeyRow(kb));
        ConfigStore.Save(_config);
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
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13 });
        stack.Children.Add(inner);
        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = stack,
        };
    }

    private static Control Row(string label, Control inner)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*") };
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(inner, 1);
        g.Children.Add(inner);
        return g;
    }

    private static Control MakeSlider(double value, double min, double max, Action<double> onChanged)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) onChanged(slider.Value);
        };
        return slider;
    }

    private static Control MakeCheck(string label, bool value, Action<bool> onChanged)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty) onChanged(cb.IsChecked == true);
        };
        return cb;
    }

    private static Control MakeAnimBox(ComboBox box, List<string> anims, string current, Action<string> onChanged)
    {
        box.ItemsSource = anims;
        int idx = anims.IndexOf(current);
        box.SelectedIndex = idx >= 0 ? idx : (anims.Count > 0 ? 0 : -1);
        box.SelectionChanged += (_, __) =>
        {
            if (box.SelectedItem is string s) onChanged(s);
        };
        return box;
    }
}
