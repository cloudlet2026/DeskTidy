using System.Windows;

namespace DeskTidy.Views;

public partial class InputDialog : Window
{
    public string ResultText => InputBox.Text.Trim();

    public InputDialog()
    {
        InitializeComponent();
    }

    public static string? Show(string title, string label, string defaultValue = "")
    {
        var dlg = new InputDialog
        {
            Title = title,
            LabelText = { Text = label },
            InputBox = { Text = defaultValue }
        };
        dlg.InputBox.Focus();
        dlg.InputBox.SelectAll();
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
