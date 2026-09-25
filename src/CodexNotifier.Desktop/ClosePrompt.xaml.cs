namespace CodexNotifier.Desktop;

public partial class ClosePrompt : Window
{
    public bool ExitRequested => ExitChoice.IsChecked == true;
    public ClosePrompt()
    {
        InitializeComponent();
        Loaded += (_, _) => ConfirmButton.Focus();
    }
    void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
