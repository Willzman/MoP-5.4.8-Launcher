using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using MoPLauncher.Config;
using MoPLauncher.Patching;
using MoPLauncher.Update;

namespace MoPLauncher;

public partial class MainWindow : Window
{
    private readonly ConfigStore _store = new();
    private LauncherConfig _config;

    // Set while a newer release is waiting: Play becomes Update.
    private ReleaseInfo? _update;

    // Notes of the release this launcher is, shown when there's nothing to do.
    private string? _currentNotes;

    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        _config = _store.Load();
        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        Refresh();
        SetStatus(IsConfigured ? "Ready" : "Setup needed", IsConfigured ? string.Empty : "choose your game folder to start");

        Loaded += async (_, _) =>
        {
            UpdateService.CleanUpAfterUpdate();
            await CheckForUpdatesAsync(announce: false);
        };
    }

    private bool IsConfigured =>
        _config.FindClientExecutable() is not null && ServerEndpoint.TryParse(_config.ServerAddress, out _, out _);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Dark title bar to match the rest of the window (Windows 10 20H1+).
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---- What the window shows -------------------------------------------

    private void Refresh()
    {
        RealmText.Text = !string.IsNullOrWhiteSpace(ServerDefaults.RealmName)
            ? $"Patch 5.4.8  ·  {ServerDefaults.RealmName}"
            : string.IsNullOrWhiteSpace(_config.ServerAddress)
                ? "Patch 5.4.8"
                : $"Patch 5.4.8  ·  {_config.ServerAddress.Trim()}";

        SetupList.Children.Clear();
        SkipUpdateRow.Visibility = _update is not null && IsConfigured ? Visibility.Visible : Visibility.Collapsed;

        if (_update is not null)
        {
            PanelKicker.Text = "UPDATE AVAILABLE";
            PanelTitle.Text = $"Launcher {_update.Version}";
            PanelBody.Text = NotesToText(_update.Notes, "A new version of the launcher is ready to install.");
            PlayButton.Content = "Update";
        }
        else if (!IsConfigured)
        {
            PanelKicker.Text = "FIRST-TIME SETUP";
            PanelTitle.Text = "Two things before you play";
            PanelBody.Text = "The launcher starts your own Mists of Pandaria 5.4.8 client and points it at the " +
                             "server while it runs. Your game files are never changed.";

            var hasFolder = _config.FindClientExecutable() is not null;
            var hasAddress = ServerEndpoint.TryParse(_config.ServerAddress, out _, out _);
            AddSetupStep("Choose your game folder", hasFolder, current: !hasFolder);
            AddSetupStep("Enter the server address", hasAddress, current: hasFolder && !hasAddress);
            AddSetupStep("Press Play", false, current: false);
            PlayButton.Content = "Set up";
        }
        else
        {
            PanelKicker.Text = "WHAT'S NEW";
            PanelTitle.Text = $"Launcher {UpdateService.CurrentVersion}";
            PanelBody.Text = NotesToText(_currentNotes,
                "You're on the latest version. When a new one comes out, Play turns into Update and the " +
                "launcher replaces itself. There's nothing to reinstall.");
            PlayButton.Content = "Play";
        }

        PlayButton.IsEnabled = !_busy;
    }

    private void AddSetupStep(string text, bool done, bool current)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
        var brush = done || current ? (Brush)FindResource("TextBrush") : (Brush)FindResource("MutedBrush");

        row.Children.Add(new TextBlock
        {
            Text = done ? "✓" : current ? "▸" : "·",
            Width = 22,
            Foreground = done ? (Brush)FindResource("JadeBrush") : brush
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal
        });
        SetupList.Children.Add(row);
    }

    // Release notes are Markdown; show them as plain text with bullets.
    private static string NotesToText(string? notes, string fallback)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return fallback;

        var text = new StringBuilder();
        foreach (var raw in notes.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('#'))
                line = trimmed.TrimStart('#').Trim();
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                line = "•  " + trimmed[2..];

            text.AppendLine(line.Replace("**", string.Empty).Replace("`", string.Empty));
        }

        return text.ToString().Trim();
    }

    private void SetStatus(string title, string detail = "", double progress = 0)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        Progress.Value = progress;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PlayButton.IsEnabled = !busy;
    }

    // ---- Play / Update -----------------------------------------------------

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_update is not null)
            await InstallUpdateAsync(_update);
        else if (!IsConfigured)
            ShowSettings();
        else
            await LaunchAsync();
    }

    private async Task LaunchAsync()
    {
        var exePath = _config.FindClientExecutable();
        if (exePath is null)
        {
            ShowSettings();
            return;
        }

        SetBusy(true);
        PlayButton.Content = "Starting…";
        SetStatus("Starting the game", "finding the server");

        try
        {
            var endpoint = await ServerEndpoint.ResolveAsync(_config.ServerAddress);
            var folder = _config.ClientLocation;
            var clearCache = _config.ClearCacheOnLaunch;
            var redirect = _config.RedirectAllConnections;

            SetStatus("Starting the game", "patching the client in memory", 0.5);

            await Task.Run(() =>
            {
                if (clearCache)
                    ClearCache(folder);

                ConfigWtf.Set(folder, "realmlist", endpoint.Realmlist);
                // Keeps the client off the Battle.net login path.
                ConfigWtf.Remove(folder, "realmListbn");

                ClientLauncher.Launch(exePath, folder, endpoint.Address, endpoint.Port, redirect);
            });

            SetStatus("Game started", $"connected to {endpoint.Realmlist}", 1);
        }
        catch (Exception ex)
        {
            SetStatus("The game couldn't be started", ex.Message);
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    private static void ClearCache(string clientLocation)
    {
        var cachePath = Path.Combine(clientLocation, "Cache");
        if (Directory.Exists(cachePath))
            Directory.Delete(cachePath, recursive: true);
    }

    // ---- Updates -----------------------------------------------------------

    private async Task CheckForUpdatesAsync(bool announce)
    {
        if (_busy)
            return;

        if (announce)
            SetStatus("Checking for updates");

        try
        {
            var latest = await UpdateService.GetLatestReleaseAsync();

            if (latest is not null && latest.Version > UpdateService.CurrentVersion && latest.DownloadUrl is not null)
            {
                _update = latest;
                SetStatus("Update available", $"version {latest.Version} is ready to install");
            }
            else
            {
                _update = null;
                if (latest is not null && latest.Version == UpdateService.CurrentVersion)
                    _currentNotes = latest.Notes;
                if (announce)
                    SetStatus("You're up to date", $"version {UpdateService.CurrentVersion}");
            }
        }
        catch (Exception ex)
        {
            // Offline is fine: the game doesn't need GitHub.
            if (announce)
                SetStatus("Couldn't check for updates", ex.Message);
        }

        Refresh();
    }

    private async Task InstallUpdateAsync(ReleaseInfo release)
    {
        SetBusy(true);
        PlayButton.Content = "Updating…";
        SetStatus("Downloading the update", $"version {release.Version}");

        try
        {
            var progress = new Progress<double>(value =>
                SetStatus("Downloading the update", $"version {release.Version}  ·  {value:P0}", value));

            await UpdateService.DownloadAndApplyAsync(release, progress);

            SetStatus("Restarting", "the new version is starting", 1);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus("The update couldn't be installed", ex.Message);
            SetBusy(false);
            Refresh();
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(announce: true);

    private void SkipUpdate_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        _update = null;
        Refresh();
        SetStatus("Ready", "the update will be offered again next time");
    }

    // ---- Menu and links ------------------------------------------------------

    private void MoreButton_Click(object sender, RoutedEventArgs e) => MorePopup.IsOpen = !MorePopup.IsOpen;

    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        OpenFolder(_config.ClientLocation);
    }

    private void OpenAddOnsFolder_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        if (!Directory.Exists(_config.ClientLocation))
        {
            OpenFolder(_config.ClientLocation);
            return;
        }

        var addons = Path.Combine(_config.ClientLocation, "Interface", "AddOns");
        try
        {
            Directory.CreateDirectory(addons);
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't open the AddOns folder", ex.Message);
            return;
        }

        OpenFolder(addons);
    }

    private void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            SetStatus("No game folder yet", "choose it in Settings");
            return;
        }

        OpenWithShell(path);
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        if (!Directory.Exists(_config.ClientLocation))
        {
            SetStatus("No game folder yet", "choose it in Settings");
            return;
        }

        try
        {
            ClearCache(_config.ClientLocation);
            SetStatus("Game cache cleared");
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't clear the cache", ex.Message);
        }
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        OpenWithShell(UpdateService.RepositoryUrl);
    }

    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't open that", ex.Message);
        }
    }

    // ---- Settings ----------------------------------------------------------

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        ShowSettings();
    }

    private void ShowSettings()
    {
        FolderBox.Text = _config.ClientLocation;
        AddressBox.Text = _config.ServerAddress;
        ArchAuto.IsChecked = _config.Architecture == ClientArchitecture.Auto;
        Arch64.IsChecked = _config.Architecture == ClientArchitecture.X64;
        Arch32.IsChecked = _config.Architecture == ClientArchitecture.X86;
        ClearCacheBox.IsChecked = _config.ClearCacheOnLaunch;
        RedirectBox.IsChecked = _config.RedirectAllConnections;
        SettingsError.Visibility = Visibility.Collapsed;

        SettingsOverlay.Visibility = Visibility.Visible;
        (string.IsNullOrWhiteSpace(FolderBox.Text) ? FolderBox : AddressBox).Focus();
    }

    private void HideSettings() => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose your Mists of Pandaria 5.4.8 folder" };
        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;

        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void SettingsCancel_Click(object sender, RoutedEventArgs e) => HideSettings();

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var candidate = new LauncherConfig
        {
            ClientLocation = FolderBox.Text.Trim().Trim('"'),
            ServerAddress = AddressBox.Text.Trim(),
            Architecture = Arch64.IsChecked == true ? ClientArchitecture.X64
                : Arch32.IsChecked == true ? ClientArchitecture.X86
                : ClientArchitecture.Auto,
            ClearCacheOnLaunch = ClearCacheBox.IsChecked == true,
            RedirectAllConnections = RedirectBox.IsChecked == true
        };

        string? problem = null;
        if (!Directory.Exists(candidate.ClientLocation))
            problem = "That game folder doesn't exist.";
        else if (candidate.FindClientExecutable() is null)
            problem = candidate.Architecture switch
            {
                ClientArchitecture.X64 => "There's no Wow-64.exe in that folder. Pick another folder, or choose Automatic.",
                ClientArchitecture.X86 => "There's no Wow.exe in that folder. Pick another folder, or choose Automatic.",
                _ => "There's no Wow.exe or Wow-64.exe in that folder. Pick the folder the game itself is in."
            };
        else if (!ServerEndpoint.TryParse(candidate.ServerAddress, out _, out _))
            problem = "Enter the server's IP or hostname, like 192.168.1.50.";

        if (problem is null)
        {
            try
            {
                _store.Save(candidate);
            }
            catch (Exception ex)
            {
                problem = $"The settings couldn't be saved: {ex.Message}";
            }
        }

        if (problem is not null)
        {
            SettingsError.Text = problem;
            SettingsError.Visibility = Visibility.Visible;
            return;
        }

        _config = candidate;
        HideSettings();
        Refresh();
        if (_update is null)
            SetStatus("Ready");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsOverlay.Visibility == Visibility.Visible)
        {
            HideSettings();
            e.Handled = true;
        }
    }
}
