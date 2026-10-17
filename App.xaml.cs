using System.Windows;
using BullerStudio.Services;

namespace BullerStudio;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
#if GAME_PLAYER_BUILD
        var window = new PlayerWindow(GameProjectStore.LoadPackaged());
#else
        var window = new HomeWindow();
#endif
        MainWindow = window;
        window.Show();
    }
}
