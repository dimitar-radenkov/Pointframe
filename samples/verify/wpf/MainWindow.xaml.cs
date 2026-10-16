using System.Windows;

namespace WpfSample;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (name.Length == 0)
        {
            Status.Text = "Name is required";
            return;
        }

        Status.Text = $"Added {name}";
#if !SAMPLE_BROKEN
        ContactList.Items.Add(name);
        ContactList.SelectedIndex = ContactList.Items.Count - 1;
#endif
        NameInput.Clear();
    }
}
