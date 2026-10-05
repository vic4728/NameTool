using System.ComponentModel;
using System.Windows;

namespace NameTool;

public partial class UpdateDownloadWindow : Window
{
    private bool _allowClose;
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken CancellationToken => _cts.Token;

    public int Progress
    {
        get
        {
            if (!int.TryParse(PercentText.Text.Replace("%", string.Empty), out var value))
            {
                return 0;
            }

            return value;
        }
        set
        {
            var normalized = Math.Max(0, Math.Min(100, value));
            PercentText.Text = $"{normalized}%";
            UpdateProgressFill(normalized);
        }
    }

    public string FileName
    {
        set => FileNameText.Text = "正在下载...";
    }

    public UpdateDownloadWindow()
    {
        InitializeComponent();
        ProgressTrack.SizeChanged += (_, _) => UpdateProgressFill(Progress);
    }

    public void MarkCompleted()
    {
        _allowClose = true;
        CancelButton.Visibility = Visibility.Collapsed;
        PercentText.Text = "100%";
        UpdateProgressFill(100);
        FileNameText.Text = "下载完成，正在启动安装程序...";
    }

    private void UpdateProgressFill(int progress)
    {
        var width = ProgressTrack.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        ProgressFill.Width = width * progress / 100.0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        _allowClose = true;
        Close();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            _cts.Cancel();
            _allowClose = true;
        }
    }
}
