using System.Windows;
using System.Windows.Input;

namespace GlassLauncher.Dialogs;

/// <summary>单行文本输入对话框（重命名用）。返回 null 表示取消。</summary>
public partial class PromptDialog : Chrome.GlassWindow
{
    public PromptDialog()
    {
        InitializeComponent();
    }

    public PromptDialog(string title, string initialValue)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        Input.Text = initialValue;

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    /// <summary>弹出并返回输入值；取消返回 null。</summary>
    public static string? Ask(Window owner, string title, string initialValue)
    {
        var dialog = new PromptDialog(title, initialValue) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value.Trim() : null;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
