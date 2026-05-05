using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace HBSort.Services;

/// <summary>
/// Toast-Implementierung: verwaltet eine ObservableCollection von ToastItems,
/// die im Hauptfenster unten rechts gerendert werden. Nach 3 Sek wird der Toast
/// automatisch wieder entfernt.
///
/// Architektur-Entscheidung: Eigene Mini-Implementierung (statt Toolkit-Toast),
/// weil die Toolkit-Variante UWP-Notifications nutzt und damit ueber das System
/// laeuft - wir wollen aber Toasts INNERHALB des App-Fensters, das ist viel
/// dezenter und braucht keine UWP-Registrierung.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly TimeSpan _toastDuration = TimeSpan.FromSeconds(3);

    /// <summary>Aktive Toasts. Wird vom MainWindow per ItemsControl gebunden.</summary>
    public ObservableCollection<ToastItem> ActiveToasts { get; } = new();

    public void ShowInfo(string message)                          => Show(message, ToastKind.Info, null);
    public void ShowSuccess(string message, string? imageUrl = null) => Show(message, ToastKind.Success, imageUrl);
    public void ShowWarning(string message)                       => Show(message, ToastKind.Warning, null);
    public void ShowError(string message)                         => Show(message, ToastKind.Error, null);

    private void Show(string message, ToastKind kind, string? imageUrl)
    {
        var dispatcher = Application.Current?.Dispatcher
                          ?? Dispatcher.CurrentDispatcher;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(message, kind, imageUrl));
            return;
        }

        var toast = new ToastItem { Message = message, Kind = kind, ImageUrl = imageUrl };
        ActiveToasts.Add(toast);

        // Nach 3 Sekunden automatisch entfernen.
        // DispatcherTimer laeuft auf dem UI-Thread, das ist genau was wir wollen.
        var timer = new DispatcherTimer { Interval = _toastDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ActiveToasts.Remove(toast);
        };
        timer.Start();
    }
}
