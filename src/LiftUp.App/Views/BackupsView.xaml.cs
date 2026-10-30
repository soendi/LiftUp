using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiftUp.Core.Backup;
using LiftUp.Core.Backup.Cloud;
using LiftUp.Core.Domain;

namespace LiftUp.App.Views;

public partial class BackupsView : UserControl
{
    private sealed record TargetOption(BackupTargetKind Kind, string Name);

    private static readonly TargetOption[] Targets =
    [
        new(BackupTargetKind.LocalFolder, "Lokaler Ordner"),
        new(BackupTargetKind.GoogleDrive, "Google Drive"),
        new(BackupTargetKind.OneDrive, "OneDrive"),
    ];

    private readonly AppServices _services;
    private CancellationTokenSource? _signIn;

    public BackupsView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        TargetBox.ItemsSource = Targets;
        TargetBox.SelectedIndex = 0;

        LoadOptions();
        RefreshBackups();
        UpdateCloudState();
    }

    private BackupTargetKind SelectedTarget =>
        (TargetBox.SelectedItem as TargetOption)?.Kind ?? BackupTargetKind.LocalFolder;

    private void LoadOptions()
    {
        try
        {
            var options = _services.Backups.LoadOptions();
            TargetBox.SelectedItem = Targets.First(target => target.Kind == options.Target);
            FolderBox.Text = _services.Backups.ResolveFolder(options);
            KeepDaysBox.Text = options.KeepDays.ToString();
            UploadToCloudCheck.IsChecked = options.UploadToCloud;
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void RefreshBackups()
    {
        try
        {
            var files = _services.Backups.List();
            BackupsGrid.ItemsSource = files;
            InfoText.Text = files.Count == 0
                ? "Noch keine Sicherung vorhanden."
                : $"{files.Count} Sicherung(en) im Ordner {_services.Backups.ResolveFolder(_services.Backups.LoadOptions())}";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void UpdateCloudState()
    {
        var target = SelectedTarget;
        var isCloud = target != BackupTargetKind.LocalFolder;

        ClientIdBox.IsEnabled = isCloud;
        ClientSecretPanel.Visibility = target == BackupTargetKind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;
        SignInButton.IsEnabled = isCloud;
        SignOutButton.IsEnabled = isCloud;
        UploadToCloudCheck.IsEnabled = isCloud;

        if (!isCloud)
        {
            CloudStatusText.Text = "Sicherungen liegen nur lokal.";
            return;
        }

        ClientIdBox.Text = _services.CloudBackups.GetClientId(target);
        ClientSecretBox.Password = _services.CloudBackups.GetClientSecret(target);

        var account = _services.CloudBackups.GetAccount(target);

        if (account is not null)
        {
            CloudStatusText.Text = $"Angemeldet als {account.AccountName} (Token gültig bis {CloudBackupService.FormatExpiry(account)}).";
        }
        else if (!_services.CloudBackups.IsConfigured(target))
        {
            CloudStatusText.Text = target == BackupTargetKind.GoogleDrive
                ? "Noch nicht eingerichtet: Client-ID und Client-Schlüssel der Google-Anwendung eintragen."
                : "Noch nicht eingerichtet: Client-ID der Microsoft-Anwendung eintragen.";
        }
        else
        {
            CloudStatusText.Text = "Bereit zur Anmeldung.";
        }
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e) => UpdateCloudState();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Sicherungsordner wählen",
            Multiselect = false,
        };

        var current = FolderBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnSaveOptionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = new BackupOptions
            {
                Target = SelectedTarget,
                LocalFolder = FolderBox.Text?.Trim() ?? string.Empty,
                KeepDays = int.TryParse(KeepDaysBox.Text, out var days) && days > 0 ? days : 30,
                UploadToCloud = UploadToCloudCheck.IsChecked == true,
            };

            _services.Backups.SaveOptions(options);
            InfoText.Text = "Einstellungen gespeichert.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnSaveCloudConfigClick(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget;
        if (target == BackupTargetKind.LocalFolder)
        {
            InfoText.Text = "Für lokale Sicherungen ist keine Anmeldung nötig.";
            return;
        }

        try
        {
            _services.CloudBackups.SaveConfiguration(target, ClientIdBox.Text, ClientSecretBox.Password);
            UpdateCloudState();
            InfoText.Text = "Zugangsdaten der Anwendung gespeichert.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget;
        if (target == BackupTargetKind.LocalFolder)
        {
            return;
        }

        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        SignInButton.IsEnabled = false;

        var progress = new Progress<DeviceCodePrompt>(prompt =>
        {
            CloudStatusText.Text = $"Code {prompt.UserCode} auf {prompt.VerificationUrl} eingeben – warte auf Freigabe …";
            InfoText.Text = prompt.Message;

            try
            {
                Clipboard.SetText(prompt.UserCode);
                InfoText.Text = prompt.Message + " (Code in die Zwischenablage kopiert)";
            }
            catch (Exception)
            {
                // Zwischenablage kann belegt sein; der Code steht im Text.
            }

            Process.Start(new ProcessStartInfo(prompt.VerificationUrl) { UseShellExecute = true });
        });

        try
        {
            var tokens = await _services.CloudBackups.SignInAsync(target, progress, _signIn.Token);
            UpdateCloudState();
            InfoText.Text = $"Angemeldet als {tokens.AccountName}.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (OperationCanceledException)
        {
            InfoText.Text = "Anmeldung abgebrochen.";
        }
        catch (Exception exception)
        {
            InfoText.Text = "Anmeldung fehlgeschlagen: " + exception.Message;
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget;
        if (target == BackupTargetKind.LocalFolder)
        {
            return;
        }

        _signIn?.Cancel();
        _services.CloudBackups.SignOut(target);
        UpdateCloudState();
        InfoText.Text = "Abgemeldet. Die Cloud-Verbindung ist entfernt.";
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            InfoText.Text = "Sicherung wird erstellt …";
            var file = _services.Backups.Create();
            RefreshBackups();

            var target = SelectedTarget;
            if (target == BackupTargetKind.LocalFolder)
            {
                InfoText.Text = $"Sicherung erstellt: {file.FileName} ({file.SizeText})";
                return;
            }

            if (UploadToCloudCheck.IsChecked != true)
            {
                InfoText.Text = $"Sicherung lokal erstellt: {file.FileName}. Upload ist ausgeschaltet.";
                return;
            }

            InfoText.Text = $"Sicherung erstellt, lade zu {CloudBackupService.ProviderName(target)} …";
            await _services.CloudBackups.UploadAsync(target, file.FilePath);

            _services.Backups.SaveOptions(new BackupOptions
            {
                Target = target,
                LocalFolder = FolderBox.Text?.Trim() ?? string.Empty,
                KeepDays = int.TryParse(KeepDaysBox.Text, out var days) && days > 0 ? days : 30,
                UploadToCloud = true,
            });

            InfoText.Text = $"Sicherung {file.FileName} erstellt und zu {CloudBackupService.ProviderName(target)} geladen.";
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            InfoText.Text = "Sicherung fehlgeschlagen: " + exception.Message;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshBackups();

    private BackupFile? Selected => BackupsGrid.SelectedItem as BackupFile;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = Selected is not null;

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Sicherung\n{selected.FileName} ({selected.CreatedText})\n\nwiederherstellen?\n\n" +
            "Der aktuelle Stand wird vorher automatisch gesichert. LiftUp sollte danach neu gestartet werden.",
            "Sicherung wiederherstellen", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var safety = _services.Backups.Restore(selected.FilePath);
            RefreshBackups();
            MessageBox.Show(
                $"Die Sicherung wurde eingespielt.\n\nDer vorherige Stand liegt unter:\n{safety.FileName}\n\nBitte LiftUp neu starten.",
                "Wiederherstellung", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (LiftUpException exception)
        {
            InfoText.Text = exception.Message;
        }
    }

    private void OnShowFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = _services.Backups.ResolveFolder(_services.Backups.LoadOptions());
        if (Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
    }
}
