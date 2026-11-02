using System.IO;
using System.Windows;
using System.Windows.Input;
using LiftUp.Core.Domain;
using Microsoft.Web.WebView2.Core;

namespace LiftUp.App.Views;

/// <summary>
/// Zeigt die Straßenansicht zur Adresse des Projekts im Vollbild. „Foto aufnehmen“ legt den
/// sichtbaren Ausschnitt als PNG ab; danach schließt sich das Fenster, die Anwendung ist also
/// wieder im Normalbild und der Browser samt Reiter ist zu.
/// </summary>
public partial class StreetViewWindow : Window
{
    private readonly string _url;
    private bool _started;
    private bool _capturing;

    public StreetViewWindow(string address)
    {
        InitializeComponent();

        AddressText.Text = address;
        AddressText.ToolTip = address;
        _url = StreetViewLink.BuildUrl(address);

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;

        // Das Browserfenster wirklich beenden, damit kein Reiter offen bleibt.
        Closed += (_, _) => Browser.Dispose();
    }

    /// <summary>Aufgenommenes Foto (PNG im temporären Ordner); sonst <c>null</c>.</summary>
    public string? ImagePath { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.Navigate(_url);
        }
        catch (Exception exception) when (
            exception is WebView2RuntimeNotFoundException
                or InvalidOperationException
                or System.Runtime.InteropServices.COMException
                or DllNotFoundException)
        {
            ShowFailure(exception.Message);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            WaitPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowFailure("Die Straßenansicht konnte nicht geladen werden. Bitte die Internetverbindung prüfen.");
    }

    /// <summary>Fehler an der Stelle des Ladehinweises zeigen; aufnehmen lässt sich dann nichts.</summary>
    private void ShowFailure(string message)
    {
        WaitPanel.Visibility = Visibility.Visible;
        WaitText.Text = "Die Straßenansicht ist nicht verfügbar.\r\n\r\n" + message;
        CaptureButton.IsEnabled = false;
    }

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_capturing || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        _capturing = true;
        CaptureButton.IsEnabled = false;
        try
        {
            // Leiste ausblenden und das Zeichnen abwarten, damit sie nicht mit auf dem Foto steht.
            Bar.Visibility = Visibility.Collapsed;
            WaitPanel.Visibility = Visibility.Visible;
            WaitText.Text = "Foto wird aufgenommen …";
            await Task.Delay(250);

            var path = Path.Combine(Path.GetTempPath(), $"StreetView-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using (var stream = File.Create(path))
            {
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }

            ImagePath = path;
            DialogResult = true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.Runtime.InteropServices.COMException
                or IOException
                or UnauthorizedAccessException)
        {
            Bar.Visibility = Visibility.Visible;
            WaitPanel.Visibility = Visibility.Collapsed;
            HintText.Text = "Das Foto konnte nicht aufgenommen werden: " + exception.Message;
        }
        finally
        {
            _capturing = false;
            CaptureButton.IsEnabled = true;
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        WaitPanel.Visibility = Visibility.Visible;
        WaitText.Text = "Straßenansicht wird geladen …";
        Browser.CoreWebView2.Navigate(_url);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
