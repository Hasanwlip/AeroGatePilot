using System.Windows;
using System.Windows.Input;
using AeroGatePilot.App.Localization;

namespace AeroGatePilot.App.Mvvm;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
        }
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public static class Dialogs
{
    public static void Error(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, Loc.T("Msg_Error"), MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Options);

    public static void Info(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, Loc.T("App_Title"), MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, Options);

    public static bool Confirm(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, Loc.T("Msg_Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, Options) == MessageBoxResult.Yes;

    private static MessageBoxOptions Options =>
        Loc.Instance.IsPersian ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
}
