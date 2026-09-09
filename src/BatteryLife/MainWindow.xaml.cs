using System.Windows;
using BatteryLife.ViewModels;

namespace BatteryLife;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}