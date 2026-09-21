using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameBuddy.ViewModels;

namespace GameBuddy.Views;

public partial class MainWindow : Window
{
    public MainWindow(ViewModels.MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnGameDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: GameItemViewModel item } &&
            DataContext is MainViewModel vm &&
            vm.LaunchCommand.CanExecute(item))
        {
            vm.LaunchCommand.Execute(item);
        }
    }
}
