using System.Windows;
using Cineclub.App.Desktop;

namespace Cineclub.App;

public partial class App : Application
{
    /// <summary>
    /// Composition hook used by the networking layer. Keeping it here lets XAML
    /// retain StartupUri while the desktop shell stays testable with a fake transport.
    /// </summary>
    public static Func<IRoomTransport>? RoomTransportFactory { get; set; } =
        static () => new SignalRRoomTransport();
}
