using System.Windows;

namespace ColdTurkeyClone;

public partial class PasswordDialog : Window
{
    public string EnteredPassword { get; private set; } = "";

    public PasswordDialog(string prompt)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        PasswordInput.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        EnteredPassword = PasswordInput.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
