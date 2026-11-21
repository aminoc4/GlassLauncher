using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace GlassLauncher.Models;

/// <summary>
/// 启动器条目。字段严格按项目规格实现，另加 SourcePath 保存原始拖入路径（重新解析图标/失效检测要用），
/// 规格要求的是“至少包含”，因此不冲突。
///
/// 可变字段都带 PropertyChanged：重命名、失效检测、重新指定目标都会在运行期改这些值，
/// 绑不上就等于界面上看不到变化。
/// </summary>
public sealed class AppEntry : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private string _targetPath = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private bool _isBroken;
    private bool _isDragging;
    private ImageSource? _icon;

    /// <summary>唯一标识。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>显示名，默认取快捷方式文件名或目标文件名。</summary>
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value)
            {
                return;
            }

            _displayName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Initial));
        }
    }

    /// <summary>解析后的真实目标路径（不是 .lnk 路径）；.url 则为网址。</summary>
    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (_targetPath == value)
            {
                return;
            }

            _targetPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUrl));
        }
    }

    /// <summary>启动参数。</summary>
    public string Arguments
    {
        get => _arguments;
        set
        {
            if (_arguments == value)
            {
                return;
            }

            _arguments = value;
            OnPropertyChanged();
        }
    }

    /// <summary>工作目录。</summary>
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (_workingDirectory == value)
            {
                return;
            }

            _workingDirectory = value;
            OnPropertyChanged();
        }
    }

    /// <summary>本地缓存的图标文件路径。</summary>
    public string IconCachePath { get; set; } = string.Empty;

    /// <summary>排序序号，用于持久化后还原顺序。</summary>
    public int SortOrder { get; set; }

    /// <summary>目标不存在或启动失败时为 true。</summary>
    public bool IsBroken
    {
        get => _isBroken;
        set
        {
            if (_isBroken == value)
            {
                return;
            }

            _isBroken = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 正在被拖拽：原位的图标要隐藏，让出格子给邻居补位。
    /// 挂在数据上而不是容器上，是因为 Move 之后容器会重新绑定，
    /// 按索引去找容器会找错人；跟着条目走才永远对。
    /// 纯运行期状态，不落盘。
    /// </summary>
    [JsonIgnore]
    public bool IsDragging
    {
        get => _isDragging;
        set
        {
            if (_isDragging == value)
            {
                return;
            }

            _isDragging = value;
            OnPropertyChanged();
        }
    }

    /// <summary>拖入时的原始路径（.lnk / .url / .exe / 目录本身）。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>原始图标位置，形如 "C:\x\app.exe,3" 或 "%SystemRoot%\system32\shell32.dll,17"。</summary>
    public string IconLocation { get; set; } = string.Empty;

    /// <summary>是否来自 .url 网络快捷方式。</summary>
    [JsonIgnore]
    public bool IsUrl =>
        TargetPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || TargetPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>条目下方显示名首字，取不到图标时的占位。</summary>
    [JsonIgnore]
    public string Initial
    {
        get
        {
            string name = DisplayName.Trim();
            return name.Length == 0 ? "?" : name[..1].ToUpperInvariant();
        }
    }

    /// <summary>已加载的图标位图。不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (!ReferenceEquals(_icon, value))
            {
                _icon = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
