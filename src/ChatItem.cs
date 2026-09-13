using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TrollWrangler;

/// <summary>聊天消息项。属性变化会通知 UI（含接受学习按钮的显隐）。</summary>
public sealed partial class ChatItem : INotifyPropertyChanged
{
    /// <summary>气泡最大宽度（随窗口大小动态调整，新消息创建时自动套用）。</summary>
    public static double DefaultBubbleMaxWidth = 480;

    private bool _isSelf;
    private bool _isSys;
    private bool _showTime;
    private bool _accepted;
    private bool _canAccept = true;
    private string _text = "";
    private string _meta = "";
    private string _timeStr = "";
    private double _bubbleMaxWidth = DefaultBubbleMaxWidth;
    private ImageSource? _image;
    private string _attachmentName = "";
    private string _attachmentSize = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChatItem() => BubbleMaxWidth = DefaultBubbleMaxWidth;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool IsSelf
    {
        get => _isSelf;
        set
        {
            Set(ref _isSelf, value);
            Notify(nameof(SelfVisibility));
            Notify(nameof(OtherVisibility));
            Notify(nameof(AcceptVisibility));
        }
    }

    public bool IsSys
    {
        get => _isSys;
        set
        {
            Set(ref _isSys, value);
            Notify(nameof(SelfVisibility));
            Notify(nameof(OtherVisibility));
            Notify(nameof(SysVisibility));
            Notify(nameof(AcceptVisibility));
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value))
                DisplayText = MarkdownRenderer.ToPlainText(_text);
        }
    }

    /// <summary>Markdown 清洗后的显示文本（去掉 **、`、# 等标记，保证气泡一定正常显示）。</summary>
    public string DisplayText
    {
        get => _displayText;
        private set => Set(ref _displayText, value);
    }

    private string _displayText = "";

    public string Meta
    {
        get => _meta;
        set => Set(ref _meta, value);
    }

    public string TimeStr
    {
        get => _timeStr;
        set => Set(ref _timeStr, value);
    }

    public bool ShowTime
    {
        get => _showTime;
        set
        {
            Set(ref _showTime, value);
            Notify(nameof(ShowTimeVisibility));
        }
    }

    /// <summary>气泡最大宽度（随窗口尺寸自适应）。</summary>
    public double BubbleMaxWidth
    {
        get => _bubbleMaxWidth;
        set => Set(ref _bubbleMaxWidth, value);
    }

    /// <summary>对方 / AI 头像字符（以理服人=侠，AI 助手=AI）。</summary>
    public string AvatarText { get; set; } = "侠";

    /// <summary>我方头像字符（以理服人=理，AI 助手=你）。</summary>
    public string SelfAvatarText { get; set; } = "理";

    /// <summary>对方 / AI 头像背景色（以理服人=灰，AI 助手=蓝）。</summary>
    public Brush AvatarBrush { get; set; } =
        new SolidColorBrush(Color.FromArgb(255, 0xB0, 0xB0, 0xB0));

    /// <summary>消息附带的本地图片（生成图片工具的结果）。</summary>
    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (Set(ref _image, value))
                Notify(nameof(ImageVisibility));
        }
    }

    public Visibility ImageVisibility => Image != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>上传附件的文件名（非空时显示附件块）。</summary>
    public string AttachmentName
    {
        get => _attachmentName;
        set
        {
            if (Set(ref _attachmentName, value))
                Notify(nameof(AttachmentVisibility));
        }
    }

    public string AttachmentSize
    {
        get => _attachmentSize;
        set => Set(ref _attachmentSize, value);
    }

    public Visibility AttachmentVisibility =>
        string.IsNullOrEmpty(AttachmentName) ? Visibility.Collapsed : Visibility.Visible;

    private bool _isThinking;
    private double _thinkingOpacity = 1.0;

    /// <summary>「思考中…」占位气泡标记：不显示复制/删除等操作按钮。</summary>
    public bool IsThinking
    {
        get => _isThinking;
        set
        {
            Set(ref _isThinking, value);
            Notify(nameof(OperatorVisibility));
            Notify(nameof(ThinkingVisibility));
        }
    }

    /// <summary>「思考中…」占位气泡显示进度环。</summary>
    public Visibility ThinkingVisibility => IsThinking ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「思考中」文字透明度（呼吸闪烁效果，由动画定时器驱动）。</summary>
    public double ThinkingOpacity
    {
        get => _thinkingOpacity;
        set => Set(ref _thinkingOpacity, value);
    }

    /// <summary>非系统消息、非「思考中」占位时显示"复制/删除"操作按钮。</summary>
    public Visibility OperatorVisibility => (IsSys || IsThinking) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>该消息所属会话 id（用于"接受并学习"时定位正确的引擎会话）。</summary>
    public int SessionId { get; set; }

    /// <summary>本轮回复已被用户确认并学习（确认后隐藏"接受并学习"按钮）。</summary>
    public bool Accepted
    {
        get => _accepted;
        set
        {
            Set(ref _accepted, value);
            Notify(nameof(AcceptVisibility));
        }
    }

    /// <summary>该消息是否允许"接受并学习"（仅以理服人标签页的回复开启）。</summary>
    public bool CanAccept
    {
        get => _canAccept;
        set
        {
            Set(ref _canAccept, value);
            Notify(nameof(AcceptVisibility));
        }
    }

    public Visibility SelfVisibility => !IsSys && IsSelf ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OtherVisibility => !IsSys && !IsSelf ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SysVisibility => IsSys ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowTimeVisibility => ShowTime ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>仅我方消息且未确认学习时显示"接受并学习"按钮。</summary>
    public Visibility AcceptVisibility =>
        CanAccept && !IsSys && IsSelf && !Accepted ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>bool 反转转换器（false→Visible, true→Collapsed）用于对方消息。</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool b = value is bool v && v;
        return b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
