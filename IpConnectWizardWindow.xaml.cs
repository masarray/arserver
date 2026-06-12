using System.Windows;

namespace Ari61850Bridge;

public partial class IpConnectWizardWindow : Window
{
    public static readonly DependencyProperty RelayIpAddressProperty = DependencyProperty.Register(nameof(RelayIpAddress), typeof(string), typeof(IpConnectWizardWindow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty MmsPortProperty = DependencyProperty.Register(nameof(MmsPort), typeof(int), typeof(IpConnectWizardWindow), new PropertyMetadata(102));
    public static readonly DependencyProperty UseRealIecEngineProperty = DependencyProperty.Register(nameof(UseRealIecEngine), typeof(bool), typeof(IpConnectWizardWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty UseNativeCleanRoomEngineProperty = DependencyProperty.Register(nameof(UseNativeCleanRoomEngine), typeof(bool), typeof(IpConnectWizardWindow), new PropertyMetadata(true));

    public string RelayIpAddress { get => (string)GetValue(RelayIpAddressProperty); set => SetValue(RelayIpAddressProperty, value); }
    public int MmsPort { get => (int)GetValue(MmsPortProperty); set => SetValue(MmsPortProperty, value); }
    public bool UseRealIecEngine { get => (bool)GetValue(UseRealIecEngineProperty); set => SetValue(UseRealIecEngineProperty, value); }
    public bool UseNativeCleanRoomEngine { get => (bool)GetValue(UseNativeCleanRoomEngineProperty); set => SetValue(UseNativeCleanRoomEngineProperty, value); }

    public IpConnectWizardWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RelayIpBox.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        RelayIpAddress = (RelayIpAddress ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(RelayIpAddress))
        {
            WizardStatusText.Text = "Enter the IED IP address.";
            RelayIpBox.Focus();
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
