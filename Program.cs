using Velopack;

namespace Layts;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Muss als Erstes laufen: Velopack erledigt hier Installation, Updates und Deinstallation
        // (legt z. B. Verknüpfungen an) und beendet sich in diesen Fällen selbst.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
