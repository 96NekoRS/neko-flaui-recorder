using System;
using System.Windows;
using FlaUI_Test_Recorder.ViewModels;

namespace FlaUI_Test_Recorder
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainWindowViewModel();
            DataContext = _viewModel;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _viewModel.OnWindowClosed();
        }
    }
}