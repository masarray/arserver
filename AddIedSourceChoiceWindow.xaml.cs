using System.Windows;

namespace Ari61850Bridge;

public partial class AddIedSourceChoiceWindow : Window
{
    public string SelectedFlow { get; private set; } = string.Empty;

    public AddIedSourceChoiceWindow()
    {
        InitializeComponent();
    }

    private void IpOnly_Click(object sender, RoutedEventArgs e)
    {
        SelectedFlow = "IP";
        DialogResult = true;
        Close();
    }

    private void OpenScl_Click(object sender, RoutedEventArgs e)
    {
        SelectedFlow = "SCL";
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
