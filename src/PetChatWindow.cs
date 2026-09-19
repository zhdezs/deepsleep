using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace TrollWrangler;

/// <summary>
/// 桌宠的独立交互窗口：点一下鲸鱼就冒出来的小聊天框 —— 不用切主界面也能跟 AI 说话。
/// 消息跟主界面共用同一条会话（同一个 Items 集合），所以两边内容连贯、流式同步。
/// 无系统标题栏的浮窗：贴着桌宠显示、跟着桌宠移动，Esc 或 ✕ 关掉。
/// </summary>
public sealed class PetChatWindow
{
    private const int WinW = 404, WinH = 366;
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr h, int i, int v);

    /// <summary>一条消息在浮窗里的呈现（气泡 + 文本 + 图片）。</summary>
    private sealed class Bubble
    {
        public FrameworkElement Root = null!;
        public TextBlock Text = null!;
        public Image? Image;
    }

    private readonly Window _win = new();
    private readonly StackPanel _list = new() { Spacing = 8, Padding = new Thickness(12, 6, 12, 10) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _empty;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly TextBlock _state;
    private readonly TextBlock _notice;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ChatItem, Bubble> _bubbles = new();

    private readonly Func<ObservableCollection<ChatItem>> _getItems;
    private readonly Func<bool> _isRunning;
    private readonly Func<string> _statusText;
    private readonly Action<string> _onSend;
    private readonly Action _onStop;
    private readonly Action _onOpenMain;
    private readonly Action _onClosed;

    private ObservableCollection<ChatItem>? _items;
    private bool _visible;
    private int _anchorX, _anchorY, _anchorW, _anchorH;
    private bool _hasAnchor;

    /// <param name="getItems">取当前会话的消息集合（主界面切会话时这里跟着换）。</param>
    /// <param name="isRunning">当前会话是否正在跑（跑的时候按钮变「停止」）。</param>
    /// <param name="statusText">状态文案（思考中 / 后端 / 模式）。</param>
    /// <param name="onSend">发消息（复用主界面的 Agent 流程）。</param>
    /// <param name="onStop">停止当前这一轮。</param>
    /// <param name="onOpenMain">打开主界面。</param>
    /// <param name="onClosed">窗口被关掉时回调（主界面清引用）。</param>
    public PetChatWindow(Func<ObservableCollection<ChatItem>> getItems, Func<bool> isRunning,
                         Func<string> statusText, Action<string> onSend, Action onStop,
                         Action onOpenMain, Action onClosed)
    {
        _getItems = getItems;
        _isRunning = isRunning;
        _statusText = statusText;
        _onSend = onSend;
        _onStop = onStop;
        _onOpenMain = onOpenMain;
        _onClosed = onClosed;

        _win.Title = "deepsleep 桌宠";

        var root = new Grid { Background = Res("PanelBrush", Color.FromArgb(255, 30, 32, 36)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 标题栏
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 消息
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 提示
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 输入

        // ── 标题栏：🐳 桌宠 + 状态 + 主界面 + 关闭 ──
        var header = new Grid { Padding = new Thickness(12, 9, 8, 6), ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = "🐳 桌宠", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Res("TextBrush", Color.FromArgb(255, 232, 232, 232)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        _state = new TextBlock
        {
            FontSize = 11, Foreground = Res("MutedBrush", Color.FromArgb(255, 138, 144, 153)),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(_state, 1);
        header.Children.Add(_state);

        var mainBtn = new Button { Content = "主界面", FontSize = 11, Padding = new Thickness(9, 2, 9, 2), VerticalAlignment = VerticalAlignment.Center };
        mainBtn.Click += (_, _) => { try { _onOpenMain(); } catch { } };
        Grid.SetColumn(mainBtn, 2);
        header.Children.Add(mainBtn);

        var closeBtn = new Button { Content = "✕", FontSize = 11, Padding = new Thickness(9, 2, 9, 2), VerticalAlignment = VerticalAlignment.Center };
        closeBtn.Click += (_, _) => Hide();
        Grid.SetColumn(closeBtn, 3);
        header.Children.Add(closeBtn);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ── 消息区 ──
        _empty = new TextBlock
        {
            Text = "还什么都没聊。直接跟鲸鱼说句话吧 —— 这里的消息和主界面是同一条会话。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 30, 14, 0),
            Foreground = Res("MutedBrush", Color.FromArgb(255, 138, 144, 153)),
        };
        var listHost = new Grid();
        listHost.Children.Add(_empty);
        _scroll.Content = _list;
        listHost.Children.Add(_scroll);
        Grid.SetRow(listHost, 1);
        root.Children.Add(listHost);

        // ── 临时提示（比如"有命令要确认"） ──
        _notice = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 4),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 230, 160, 60)), Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_notice, 2);
        root.Children.Add(_notice);

        // ── 输入区 ──
        var inputRow = new Grid { Padding = new Thickness(10, 0, 10, 10), ColumnSpacing = 8 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input = new TextBox { PlaceholderText = "跟桌宠说点什么…（回车发送）", FontSize = 13 };
        _input.KeyDown += OnInputKeyDown;
        inputRow.Children.Add(_input);
        _send = new Button { Content = "发送", FontSize = 13, MinWidth = 72 };
        _send.Click += (_, _) => { if (_isRunning()) _onStop(); else DoSend(); };
        Grid.SetColumn(_send, 1);
        inputRow.Children.Add(_send);
        Grid.SetRow(inputRow, 3);
        root.Children.Add(inputRow);

        _win.Content = root;

        // 无系统标题栏 + 置顶 + 不进任务栏：桌宠旁边的小浮窗
        try
        {
            if (_win.AppWindow.Presenter is OverlappedPresenter p)
            {
                p.IsAlwaysOnTop = true;
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                p.SetBorderAndTitleBar(true, false);
            }
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
            SetWindowLongW(hwnd, GWL_EXSTYLE, GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        }
        catch { /* 拿不到 AppWindow 也照常用 */ }

        // Esc 关窗（放在捕获阶段，焦点在输入框里也生效）
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { Hide(); e.Handled = true; }
        }), true);

        // 每 0.4 秒对一下状态：会话被切走就跟着换，运行状态决定按钮是「发送」还是「停止」
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Sync();
        _timer.Start();

        _win.Closed += (_, _) =>
        {
            _timer.Stop();
            Unhook();
            _onClosed();
        };

        SetWindowSize();
    }

    private static Brush Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources?.TryGetValue(key, out object? v) == true && v is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }

    private void SetWindowSize()
    {
        try
        {
            var wa = DisplayArea.GetFromWindowId(_win.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            int x = _hasAnchor ? _anchorX + _anchorW - WinW : wa.X + wa.Width - WinW - 40;
            int y = _hasAnchor ? _anchorY - WinH - 8 : wa.Y + wa.Height - WinH - 120;
            if (_hasAnchor && y < wa.Y) y = _anchorY + _anchorH + 8;   // 桌宠太靠屏幕顶就改放它下面
            x = Math.Max(wa.X, Math.Min(x, wa.X + wa.Width - WinW));
            y = Math.Max(wa.Y, Math.Min(y, wa.Y + wa.Height - WinH));
            _win.AppWindow.MoveAndResize(new RectInt32(x, y, WinW, WinH));
        }
        catch { }
    }

    /// <summary>贴着桌宠的矩形显示（桌宠被拖走时同步跟着挪）。</summary>
    public void SetAnchor(int x, int y, int w, int h)
    {
        _anchorX = x; _anchorY = y; _anchorW = w; _anchorH = h;
        _hasAnchor = true;
        if (_visible) SetWindowSize();
    }

    public bool IsVisible => _visible;

    public void Toggle()
    {
        if (_visible) Hide();
        else Show();
    }

    public void Show()
    {
        try
        {
            Sync();
            SetWindowSize();
            _win.Activate();
            _visible = true;
            _input.Focus(FocusState.Programmatic);
        }
        catch { }
    }

    public void Hide()
    {
        try
        {
            _win.AppWindow.Hide();
            _visible = false;
        }
        catch { }
    }

    /// <summary>临时提示一行（比如「有命令要确认」），几秒后自动消失。</summary>
    public void Notify(string text)
    {
        try
        {
            _win.DispatcherQueue.TryEnqueue(() =>
            {
                _notice.Text = text;
                _notice.Visibility = Visibility.Visible;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                t.Tick += (_, _) => { t.Stop(); _notice.Visibility = Visibility.Collapsed; };
                t.Start();
            });
        }
        catch { }
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (shift) return;                        // Shift+回车：留给输入框，不发送
        e.Handled = true;
        if (!_isRunning()) DoSend();
    }

    private void DoSend()
    {
        string text = _input.Text?.Trim() ?? "";
        if (text.Length == 0 || _isRunning()) return;   // 正在跑就别插队，这时按钮是「停止」
        _input.Text = "";
        try { _onSend(text); } catch { }
    }

    /// <summary>对状态：会话换了就重挂，按钮文案跟着运行状态变。</summary>
    private void Sync()
    {
        try
        {
            var items = _getItems();
            if (!ReferenceEquals(items, _items)) Hook(items);
            bool run = _isRunning();
            _send.Content = run ? "停止" : "发送";
            string st = _statusText();
            _state.Text = string.IsNullOrEmpty(st) ? "消息跟主界面同一条会话" : st;
            _empty.Visibility = _list.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private void Hook(ObservableCollection<ChatItem>? items)
    {
        Unhook();
        _items = items;
        if (items == null) return;
        items.CollectionChanged += OnCollectionChanged;
        foreach (var it in items) AddBubble(it);
        ScrollEnd();
    }

    private void Unhook()
    {
        if (_items != null) _items.CollectionChanged -= OnCollectionChanged;
        foreach (var item in _bubbles.Keys) item.PropertyChanged -= OnItemChanged;
        _bubbles.Clear();
        _list.Children.Clear();
        _items = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { Hook(_items); return; }
        if (e.OldItems != null)
            foreach (var o in e.OldItems) if (o is ChatItem old) RemoveBubble(old);
        if (e.NewItems != null)
            foreach (var o in e.NewItems) if (o is ChatItem fresh) AddBubble(fresh);
        Sync();
        ScrollEnd();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ChatItem item) return;
        if (e.PropertyName is nameof(ChatItem.Text) or nameof(ChatItem.DisplayText) or nameof(ChatItem.Image))
            UpdateBubble(item);
    }

    private void AddBubble(ChatItem item)
    {
        if (_bubbles.ContainsKey(item)) return;
        var text = new TextBlock
        {
            Text = item.DisplayText, TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Foreground = Res("TextBrush", Color.FromArgb(255, 232, 232, 232)),
            IsTextSelectionEnabled = true,
        };
        var bubble = new Bubble { Text = text };

        if (item.IsSys)                          // 系统提示：居中小灰字
        {
            text.FontSize = 11.5;
            text.Foreground = Res("MutedBrush", Color.FromArgb(255, 138, 144, 153));
            text.TextAlignment = TextAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            bubble.Root = text;
        }
        else                                     // 普通气泡：图片位 + 文本
        {
            var stack = new StackPanel { Spacing = 6 };
            var img = new Image { MaxWidth = 240, MaxHeight = 240, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
            if (item.Image != null) { img.Source = item.Image; img.Visibility = Visibility.Visible; }
            bubble.Image = img;
            stack.Children.Add(img);
            stack.Children.Add(text);
            bubble.Root = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                MaxWidth = 300,
                Background = item.IsSelf
                    ? new SolidColorBrush(Color.FromArgb(255, 46, 125, 69))
                    : Res("CardBrush", Color.FromArgb(255, 42, 45, 51)),
                HorizontalAlignment = item.IsSelf ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
        }

        item.PropertyChanged += OnItemChanged;
        _bubbles[item] = bubble;
        _list.Children.Add(bubble.Root);
        ScrollEnd();
    }

    private void UpdateBubble(ChatItem item)
    {
        if (!_bubbles.TryGetValue(item, out var bubble)) return;
        bubble.Text.Text = item.DisplayText;
        if (bubble.Image != null)
        {
            if (item.Image != null)
            {
                bubble.Image.Source = item.Image;
                bubble.Image.Visibility = Visibility.Visible;
            }
            else
            {
                bubble.Image.Visibility = Visibility.Collapsed;
            }
        }
        ScrollEnd();
    }

    private void RemoveBubble(ChatItem item)
    {
        if (!_bubbles.TryGetValue(item, out var bubble)) return;
        item.PropertyChanged -= OnItemChanged;
        _bubbles.Remove(item);
        _list.Children.Remove(bubble.Root);
    }

    private void ScrollEnd()
    {
        try { _scroll.ChangeView(null, Math.Max(0, _scroll.ScrollableHeight), null, true); } catch { }
    }
}