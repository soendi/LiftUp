using System.IO;
using System.Windows;
using System.Windows.Input;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace LiftUp.App.Views;

/// <summary>
/// Zeigt die Straßenansicht zur Adresse des Projekts in einem maximierten Fenster. „Foto
/// aufnehmen“ legt den sichtbaren Ausschnitt als PNG ab; danach schließt sich das Fenster,
/// die Anwendung ist wieder im Normalbild und der Browser samt Reiter ist zu.
/// </summary>
public partial class StreetViewWindow : Window
{
    private readonly string _address;
    private readonly string _apiKey;
    private readonly StreetViewService _streetView = new();
    private bool _started;
    private bool _capturing;

    public StreetViewWindow(string address, string? apiKey = null)
    {
        InitializeComponent();

        _address = address;
        _apiKey = apiKey?.Trim() ?? string.Empty;
        AddressText.Text = address;
        AddressText.ToolTip = address;

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
        }
        catch (Exception exception) when (
            exception is WebView2RuntimeNotFoundException
                or InvalidOperationException
                or System.Runtime.InteropServices.COMException
                or DllNotFoundException)
        {
            ShowFailure(exception.Message);
            return;
        }

        Browser.NavigationCompleted += OnNavigationCompleted;

        // Ohne Koordinaten zeigt Google nur die Karte; mit Punkt öffnet es das Straßenbild.
        HintText.Text = "Adresse wird gesucht …";
        var target = await _streetView.LocateAsync(_address, _apiKey, CancellationToken.None);
        if (target is null)
        {
            HintText.Text = "Keine Straßenansicht gefunden – bitte auf der Karte mit dem gelben " +
                            "Männchen selbst hineinziehen, dann „Foto aufnehmen“.";
            Browser.CoreWebView2.Navigate(StreetViewLink.BuildUrl(_address));
            return;
        }

        // Der Punkt liegt auf der Strasse vor dem Haus, die Kamera schaut zum Gebäude.
        HintText.Text = target.RoadName is { Length: > 0 } road
            ? $"Ansicht von der {road}. Bildausschnitt mit der Maus verschieben und zoomen, dann „Foto aufnehmen“."
            : "Strasse nicht gefunden – die Ansicht kann auf dem Gebäude stehen. Bildausschnitt mit " +
              "der Maus verschieben und zoomen, dann „Foto aufnehmen“.";
        Browser.CoreWebView2.Navigate(StreetViewLink.BuildPanoUrl(
            target.Viewpoint.Latitude, target.Viewpoint.Longitude, target.Heading, target.PanoId));
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowFailure("Die Straßenansicht konnte nicht geladen werden. Bitte die Internetverbindung prüfen.");
        }
    }

    /// <summary>Fehler in der Leiste zeigen; aufnehmen lässt sich dann nichts.</summary>
    private void ShowFailure(string message)
    {
        HintText.Text = "Die Straßenansicht ist nicht verfügbar: " + message;
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
            HintText.Text = "Foto wird aufgenommen …";

            // Der Browser zeichnet Bilder asynchron ab; das letzte Kachelende abwarten.
            await Task.Delay(200);

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

        HintText.Text = "Straßenansicht wird geladen …";
        Browser.CoreWebView2.Reload();
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
