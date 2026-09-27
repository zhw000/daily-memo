using System.Windows;
using DailyMemo.ViewModels;

namespace DailyMemo.Views;

public partial class EditorWindow
{
    private readonly EditorViewModel _vm;

    public EditorWindow(EditorViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += _ => Close();
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.CaretIndex = TitleBox.Text.Length;
        };
    }

    private void ApplyParsed_Click(object sender, RoutedEventArgs e) => _vm.ApplyParsedTitle();
}
