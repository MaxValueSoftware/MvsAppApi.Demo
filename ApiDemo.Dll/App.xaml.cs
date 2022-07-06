using System.Windows;
using DemoUI.Views;
using MvsAppApi.DllAdapter;

namespace ApiDemo.Dll
{
    public partial class App : Application
    {
        private void OnStartup(object sender, StartupEventArgs e)
        {
            var mvsApi = new DllAdapter();
            var win = new MainWindow(mvsApi);
            win.Show();
        }
    }
}
