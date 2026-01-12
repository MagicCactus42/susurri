using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Susurri.Installer;

public partial class MainWindow : Window
{
    private int _currentStep = 1;
    private readonly string _defaultInstallPath;
    private CancellationTokenSource? _installCts;

    // Configuration
    private const string AppName = "Susurri";
    private const string AppVersion = "1.0.0";
    private const string GuiExecutable = "Susurri.GUI.exe";
    private const string CliExecutable = "susurri-cli.exe";
    private const string Publisher = "Susurri";

    public MainWindow()
    {
        InitializeComponent();

        // Set default installation path
        _defaultInstallPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);
        InstallPathTextBox.Text = _defaultInstallPath;

        UpdateSpaceInfo();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Installation Folder",
            InitialDirectory = InstallPathTextBox.Text
        };

        if (dialog.ShowDialog() == true)
        {
            InstallPathTextBox.Text = Path.Combine(dialog.FolderName, AppName);
            UpdateSpaceInfo();
        }
    }

    private void UpdateSpaceInfo()
    {
        try
        {
            var path = InstallPathTextBox.Text;
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                var driveInfo = new DriveInfo(root);
                var availableMb = driveInfo.AvailableFreeSpace / (1024 * 1024);
                SpaceAvailableText.Text = $"Space available: {availableMb:N0} MB";
            }
        }
        catch
        {
            SpaceAvailableText.Text = "Space available: Unknown";
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 1:
                GoToStep(2);
                break;
            case 2:
                StartInstallation();
                break;
            case 3:
                // Installation complete - launch or close
                if (NextButton.Content.ToString() == "Launch Susurri")
                {
                    LaunchApp();
                }
                Close();
                break;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            GoToStep(_currentStep - 1);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installCts != null)
        {
            _installCts.Cancel();
            return;
        }

        var result = MessageBox.Show(
            "Are you sure you want to cancel the installation?",
            "Cancel Installation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Close();
        }
    }

    private void GoToStep(int step)
    {
        _currentStep = step;

        // Update step indicators
        Step1Indicator.Fill = step >= 1 ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("TextSecondaryBrush");
        Step2Indicator.Fill = step >= 2 ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("TextSecondaryBrush");
        Step3Indicator.Fill = step >= 3 ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("TextSecondaryBrush");

        // Update panels
        WelcomePanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PathPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        InstallingPanel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        // Update buttons
        BackButton.Visibility = step > 1 && step < 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = step switch
        {
            1 => "Next",
            2 => "Install",
            3 => "Finish",
            _ => "Next"
        };
    }

    private async void StartInstallation()
    {
        GoToStep(3);
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.IsEnabled = false;
        CancelButton.Content = "Cancel";

        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;

        var installCli = InstallCliCheckbox.IsChecked == true;
        var installedComponents = new List<string>();

        try
        {
            var installPath = InstallPathTextBox.Text;
            var guiPath = Path.Combine(installPath, "gui");
            var cliPath = Path.Combine(installPath, "cli");

            // Step 1: Create directories
            UpdateProgress(5, "Creating installation directories...");
            Directory.CreateDirectory(installPath);
            Directory.CreateDirectory(guiPath);
            if (installCli) Directory.CreateDirectory(cliPath);
            await Task.Delay(300, ct);

            // Step 2: Copy GUI files
            UpdateProgress(10, "Installing GUI application...");
            await CopyGuiFiles(guiPath, ct);
            installedComponents.Add("Desktop Application (GUI)");

            // Step 3: Copy CLI files (if selected)
            if (installCli)
            {
                UpdateProgress(50, "Installing CLI application...");
                await CopyCliFiles(cliPath, ct);
                installedComponents.Add("Command-Line Interface (CLI)");
            }

            // Step 4: Create shortcuts
            if (CreateShortcutCheckbox.IsChecked == true)
            {
                UpdateProgress(75, "Creating desktop shortcut...");
                CreateDesktopShortcut(guiPath);
                await Task.Delay(200, ct);
            }

            // Step 5: Create Start Menu entries
            UpdateProgress(80, "Creating Start Menu entries...");
            CreateStartMenuShortcuts(guiPath, installCli ? cliPath : null);
            await Task.Delay(200, ct);

            // Step 6: Register in Windows
            UpdateProgress(85, "Registering application...");
            RegisterApplication(installPath, guiPath);
            await Task.Delay(200, ct);

            // Step 7: Configure startup (if selected)
            if (LaunchOnStartupCheckbox.IsChecked == true)
            {
                UpdateProgress(90, "Configuring startup...");
                ConfigureStartup(guiPath);
                await Task.Delay(200, ct);
            }

            // Complete
            UpdateProgress(100, "Installation complete!");
            InstallingTitle.Text = "Installation Complete!";
            InstallingStatus.Text = "Susurri has been successfully installed on your computer.";

            // Show summary
            InstallSummaryPanel.Visibility = Visibility.Visible;
            InstalledComponentsList.Text = string.Join("\n", installedComponents.Select(c => $"- {c}"));

            NextButton.Content = "Launch Susurri";
            NextButton.IsEnabled = true;
            CancelButton.Content = "Close";
        }
        catch (OperationCanceledException)
        {
            InstallingTitle.Text = "Installation Cancelled";
            InstallingStatus.Text = "The installation was cancelled.";
            NextButton.Content = "Close";
            NextButton.IsEnabled = true;

            // Cleanup
            try
            {
                var installPath = InstallPathTextBox.Text;
                if (Directory.Exists(installPath))
                {
                    Directory.Delete(installPath, true);
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            InstallingTitle.Text = "Installation Failed";
            InstallingTitle.Foreground = (Brush)FindResource("ErrorBrush");
            InstallingStatus.Text = $"Error: {ex.Message}";
            NextButton.Content = "Close";
            NextButton.IsEnabled = true;
        }
        finally
        {
            _installCts = null;
        }
    }

    private void UpdateProgress(int percentage, string status)
    {
        InstallProgress.Value = percentage;
        ProgressPercentage.Text = $"{percentage}%";
        InstallingStatus.Text = status;
    }

    private async Task CopyGuiFiles(string guiPath, CancellationToken ct)
    {
        var installerDir = AppDomain.CurrentDomain.BaseDirectory;
        var payloadDir = Path.Combine(installerDir, "payload", "gui");

        if (Directory.Exists(payloadDir))
        {
            await CopyDirectoryAsync(payloadDir, guiPath, ct, 10, 45);
        }
        else
        {
            // Development fallback
            var devPath = Path.GetFullPath(Path.Combine(installerDir, "..", "..", "..", "..",
                "src", "Bootstrapper", "Susurri.GUI", "bin", "Release", "net10.0", "publish"));

            if (Directory.Exists(devPath))
            {
                await CopyDirectoryAsync(devPath, guiPath, ct, 10, 45);
            }
            else
            {
                await CreatePlaceholderGuiFiles(guiPath, ct);
            }
        }
    }

    private async Task CopyCliFiles(string cliPath, CancellationToken ct)
    {
        var installerDir = AppDomain.CurrentDomain.BaseDirectory;
        var payloadDir = Path.Combine(installerDir, "payload", "cli");

        if (Directory.Exists(payloadDir))
        {
            await CopyDirectoryAsync(payloadDir, cliPath, ct, 50, 70);
        }
        else
        {
            // Development fallback
            var devPath = Path.GetFullPath(Path.Combine(installerDir, "..", "..", "..", "..",
                "src", "Bootstrapper", "Susurri.CLI", "bin", "Release", "net10.0", "publish"));

            if (Directory.Exists(devPath))
            {
                await CopyDirectoryAsync(devPath, cliPath, ct, 50, 70);
            }
            else
            {
                await CreatePlaceholderCliFiles(cliPath, ct);
            }
        }
    }

    private async Task CopyDirectoryAsync(string sourceDir, string destDir, CancellationToken ct, int startProgress, int endProgress)
    {
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        var totalFiles = files.Length;
        var copiedFiles = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destDir, relativePath);

            var destDirPath = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDirPath))
            {
                Directory.CreateDirectory(destDirPath);
            }

            System.IO.File.Copy(file, destPath, true);

            copiedFiles++;
            var progress = startProgress + (int)(copiedFiles / (float)totalFiles * (endProgress - startProgress));
            UpdateProgress(progress, $"Copying: {Path.GetFileName(file)}");
        }
    }

    private async Task CreatePlaceholderGuiFiles(string guiPath, CancellationToken ct)
    {
        UpdateProgress(25, "Creating GUI placeholder...");

        var batchContent = @"@echo off
echo Susurri GUI - Placeholder
echo.
echo The actual Avalonia GUI application would launch here.
echo.
pause
";
        await System.IO.File.WriteAllTextAsync(
            Path.Combine(guiPath, "Susurri.GUI.bat"),
            batchContent, ct);
    }

    private async Task CreatePlaceholderCliFiles(string cliPath, CancellationToken ct)
    {
        UpdateProgress(60, "Creating CLI placeholder...");

        var batchContent = @"@echo off
echo Susurri CLI - Placeholder
echo.
echo Usage: susurri-cli [command]
echo.
echo Commands:
echo   login       Login to the network
echo   status      Show current status
echo   dht start   Start DHT node
echo   dht stop    Stop DHT node
echo.
pause
";
        await System.IO.File.WriteAllTextAsync(
            Path.Combine(cliPath, "susurri-cli.bat"),
            batchContent, ct);
    }

    private void CreateDesktopShortcut(string guiPath)
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var shortcutPath = Path.Combine(desktopPath, $"{AppName}.lnk");
            var targetPath = Path.Combine(guiPath, GuiExecutable);

            // Fallback to batch file
            if (!System.IO.File.Exists(targetPath))
            {
                targetPath = Path.Combine(guiPath, "Susurri.GUI.bat");
            }

            CreateShortcut(shortcutPath, targetPath, guiPath, "Susurri - Secure P2P Chat");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create desktop shortcut: {ex.Message}");
        }
    }

    private void CreateStartMenuShortcuts(string guiPath, string? cliPath)
    {
        try
        {
            var startMenuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", Publisher);
            Directory.CreateDirectory(startMenuPath);

            // GUI shortcut
            var guiShortcutPath = Path.Combine(startMenuPath, $"{AppName}.lnk");
            var guiTargetPath = Path.Combine(guiPath, GuiExecutable);
            if (!System.IO.File.Exists(guiTargetPath))
            {
                guiTargetPath = Path.Combine(guiPath, "Susurri.GUI.bat");
            }
            CreateShortcut(guiShortcutPath, guiTargetPath, guiPath, "Susurri - Secure P2P Chat");

            // CLI shortcut (if installed)
            if (!string.IsNullOrEmpty(cliPath))
            {
                var cliShortcutPath = Path.Combine(startMenuPath, $"{AppName} (Terminal).lnk");
                var cliTargetPath = Path.Combine(cliPath, CliExecutable);
                if (!System.IO.File.Exists(cliTargetPath))
                {
                    cliTargetPath = Path.Combine(cliPath, "susurri-cli.bat");
                }
                CreateShortcut(cliShortcutPath, cliTargetPath, cliPath, "Susurri CLI - Terminal Interface");
            }

            // Uninstall shortcut
            var uninstallPath = Path.Combine(startMenuPath, "Uninstall Susurri.lnk");
            var installPath = Path.GetDirectoryName(guiPath) ?? guiPath;
            CreateShortcut(uninstallPath, Path.Combine(installPath, "uninstall.bat"), installPath, "Uninstall Susurri");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create Start Menu shortcuts: {ex.Message}");
        }
    }

    private void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        var psScript = $@"
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut('{shortcutPath.Replace("'", "''")}')
$Shortcut.TargetPath = '{targetPath.Replace("'", "''")}'
$Shortcut.WorkingDirectory = '{workingDir.Replace("'", "''")}'
$Shortcut.Description = '{description.Replace("'", "''")}'
$Shortcut.Save()
";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        process?.WaitForExit(5000);
    }

    private void RegisterApplication(string installPath, string guiPath)
    {
        try
        {
            var uninstallKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}");

            if (uninstallKey != null)
            {
                var guiExePath = Path.Combine(guiPath, GuiExecutable);
                if (!System.IO.File.Exists(guiExePath))
                {
                    guiExePath = Path.Combine(guiPath, "Susurri.GUI.bat");
                }

                uninstallKey.SetValue("DisplayName", AppName);
                uninstallKey.SetValue("DisplayVersion", AppVersion);
                uninstallKey.SetValue("Publisher", Publisher);
                uninstallKey.SetValue("InstallLocation", installPath);
                uninstallKey.SetValue("DisplayIcon", guiExePath);
                uninstallKey.SetValue("UninstallString", $"\"{Path.Combine(installPath, "uninstall.bat")}\"");
                uninstallKey.SetValue("NoModify", 1);
                uninstallKey.SetValue("NoRepair", 1);
                uninstallKey.Close();
            }

            // Create uninstall script
            var uninstallScript = $@"@echo off
echo Uninstalling Susurri...
reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}"" /f
reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""{AppName}"" /f 2>nul
rmdir /s /q ""{installPath}""
del ""%USERPROFILE%\Desktop\{AppName}.lnk"" 2>nul
rmdir /s /q ""%APPDATA%\Microsoft\Windows\Start Menu\Programs\{Publisher}"" 2>nul
echo Susurri has been uninstalled.
pause
";
            System.IO.File.WriteAllText(Path.Combine(installPath, "uninstall.bat"), uninstallScript);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to register application: {ex.Message}");
        }
    }

    private void ConfigureStartup(string guiPath)
    {
        try
        {
            var runKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);

            if (runKey != null)
            {
                var targetPath = Path.Combine(guiPath, GuiExecutable);
                if (!System.IO.File.Exists(targetPath))
                {
                    targetPath = Path.Combine(guiPath, "Susurri.GUI.bat");
                }

                runKey.SetValue(AppName, $"\"{targetPath}\"");
                runKey.Close();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to configure startup: {ex.Message}");
        }
    }

    private void LaunchApp()
    {
        try
        {
            var installPath = InstallPathTextBox.Text;
            var guiPath = Path.Combine(installPath, "gui");
            var exePath = Path.Combine(guiPath, GuiExecutable);

            if (!System.IO.File.Exists(exePath))
            {
                exePath = Path.Combine(guiPath, "Susurri.GUI.bat");
            }

            if (System.IO.File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = guiPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch application: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
