using System.Windows;

namespace DemoUI.Views
{
    /// <summary>
    /// Interaction logic for PlayerSearch.xaml
    /// </summary>
    public partial class PlayerSearch : Window
    {
        public PlayerSearch()
        {
            InitializeComponent();
            Anon.IsChecked = null;
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
