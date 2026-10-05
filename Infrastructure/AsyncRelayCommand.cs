using System.Windows;
using System.Windows.Input;

namespace NameTool.Infrastructure;

/// <summary>
/// 异步命令：执行期间自动禁用自身，避免重复点击；异常统一弹窗提示。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly string _errorTitle;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null, string errorTitle = "操作失败")
    {
        _execute = execute;
        _canExecute = canExecute;
        _errorTitle = errorTitle;
    }

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_isRunning) return;

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"操作失败：{ex.Message}", _errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
