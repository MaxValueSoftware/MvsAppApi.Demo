using System.Windows;
using DemoUI.Views;
using MvsAppApi.JsonAdapter;

namespace ApiDemo
{
    public partial class App : Application
    {
        private void OnStartup(object sender, StartupEventArgs e)
        {
            var mvsApi = new JsonAdapter();
            var win = new MainWindow(mvsApi);
            win.Show();
        }
    }
}
