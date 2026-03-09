using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
using FireLauncher.Interop;
using FireLauncher.Models;
using FireLauncher.Services;
using Forms = System.Windows.Forms;

namespace FireLauncher
{
    public partial class MainWindow : Window
    {
        private readonly LauncherSettingsService _settingsService;
        private readonly ForkCatalogService _catalogService;
        private readonly ForkProfileService _profileService;
        private readonly DiscordPresenceService _discordPresenceService;

        private LauncherSettings _settings;
        private ForkProfile _currentProfile;
        private bool _suppressUiEvents;
        private bool _operationInProgress;
        private bool _catalogSyncInProgress;
        private bool _installInProgress;
        private bool _initialCatalogSyncStarted;
        private CancellationTokenSource _installCancellationSource;

        private sealed class ForkFamilyToggleItem
        {
            public string FamilyId { get; set; }

            public string FamilyName { get; set; }

            public bool ShowInLauncher { get; set; }

            public string StatusText { get; set; }
        }

        private sealed class LaunchFamilyChoice
        {
            public string FamilyId { get; set; }

            public string FamilyName { get; set; }

            public override string ToString()
            {
                return FamilyName ?? "Fork";
            }
        }

        private sealed class LaunchVersionChoice
        {
            public string ForkId { get; set; }

            public string VersionLabel { get; set; }

            public ForkDefinition Fork { get; set; }

            public override string ToString()
            {
                return VersionLabel ?? "Version";
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            WindowBlur.TryEnable(this);

            _settingsService = new LauncherSettingsService();
            _catalogService = new ForkCatalogService(_settingsService);
            _profileService = new ForkProfileService(_settingsService, _catalogService);
            _discordPresenceService = new DiscordPresenceService();

            LoadApplicationState();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialCatalogSyncStarted)
            {
                return;
            }

            _initialCatalogSyncStarted = true;
            _ = SyncCatalogFromRemoteAsync(false, false);
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveCurrentProfileState();
            _discordPresenceService.Dispose();
            base.OnClosed(e);
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choose a folder for Fire Launcher profiles";
                dialog.SelectedPath = ProfilePathTextBox.Text;
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    ProfilePathTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void BrowseDownloadsFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choose a folder for locally staged fork downloads";
                dialog.SelectedPath = DownloadsPathTextBox.Text;
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    DownloadsPathTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void OpenProfiles_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            Directory.CreateDirectory(_settings.ProfilesRoot);
            Process.Start("explorer.exe", _settings.ProfilesRoot);
        }

        private void OpenDownloads_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            Directory.CreateDirectory(_settings.DownloadedForksRoot);
            Process.Start("explorer.exe", _settings.DownloadedForksRoot);
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_operationInProgress)
            {
                return;
            }

            if (_currentProfile == null)
            {
                MessageBox.Show(
                    "No profile is loaded yet.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SaveCurrentProfileState();

            var selectedFork = GetSelectedLaunchFork();
            if (selectedFork == null)
            {
                MessageBox.Show(
                    "No launchable fork version is selected.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedFork.ExecutablePath) || !File.Exists(selectedFork.ExecutablePath))
            {
                MessageBox.Show(
                    "The selected fork executable was not found.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var onlineMode = OnlineModeCheckBox.IsChecked == true && selectedFork.HasMultiplayer;
            if (onlineMode && selectedFork.SupportsServerIpArgument && string.IsNullOrWhiteSpace(ServerIpTextBox.Text))
            {
                OpenConnectionOverlay(selectedFork);
                return;
            }

            if (onlineMode && (!int.TryParse(PortTextBox.Text, out var parsedPort) || parsedPort < 1 || parsedPort > 65535))
            {
                OpenConnectionOverlay(selectedFork);
                return;
            }

            var port = int.TryParse(PortTextBox.Text, out var portValue) ? portValue : _currentProfile.DefaultServerPort;
            var arguments = BuildLaunchArguments(
                selectedFork,
                UsernameTextBox.Text,
                ServerIpTextBox.Text,
                port,
                onlineMode);

            var startInfo = new ProcessStartInfo
            {
                FileName = selectedFork.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(selectedFork.ExecutablePath),
                Arguments = arguments,
                UseShellExecute = false
            };

            try
            {
                var launchedProcess = Process.Start(startInfo);
                HeroSelectionText.Text = "Launching " + selectedFork.DisplayName + "...";
                WindowState = WindowState.Minimized;

                if (launchedProcess != null)
                {
                    UpdateDiscordPresence(
                        "Launching " + selectedFork.FamilyName,
                        _currentProfile.DisplayName + " | " + selectedFork.VersionLabel);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Fire Launcher could not start the selected executable.\n\n" + ex.Message,
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _operationInProgress)
            {
                return;
            }

            SaveCurrentProfileState();

            _settings.ProfilesRoot = string.IsNullOrWhiteSpace(ProfilePathTextBox.Text)
                ? _settingsService.DefaultProfilesRoot
                : ProfilePathTextBox.Text.Trim();
            _settings.DownloadedForksRoot = string.IsNullOrWhiteSpace(DownloadsPathTextBox.Text)
                ? _settingsService.DefaultTestForksRoot
                : DownloadsPathTextBox.Text.Trim();
            _settings.ForkCatalogRepositoryUrl = _catalogService.NormalizeRepositoryUrl(CatalogRepositoryTextBox.Text);
            _settings.DiscordPresenceEnabled = DiscordPresenceCheckBox.IsChecked == true;

            _settingsService.Save(_settings);
            var synced = await SyncCatalogFromRemoteAsync(false, true);
            ConfigureDiscordPresence();

            MessageBox.Show(
                synced
                    ? "Settings saved and Legacy-DB was refreshed."
                    : "Settings saved. Fire Launcher is using the cached catalog until the remote sync succeeds.",
                "Fire Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async void SyncCatalog_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _operationInProgress)
            {
                return;
            }

            SaveCurrentProfileState();

            _settings.ForkCatalogRepositoryUrl = _catalogService.NormalizeRepositoryUrl(CatalogRepositoryTextBox.Text);
            _settingsService.Save(_settings);
            CatalogRepositoryTextBox.Text = _settings.ForkCatalogRepositoryUrl;
            await SyncCatalogFromRemoteAsync(true, true);
        }

        private async void DownloadSelectedFork_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _currentProfile == null || _operationInProgress)
            {
                return;
            }

            var selectedFork = GetSelectedLaunchFork();
            if (selectedFork == null)
            {
                return;
            }

            SaveCurrentProfileState();
            _installCancellationSource = new CancellationTokenSource();
            BeginInstallOperation("Installing " + selectedFork.DisplayName, "Preparing package download...", null, true);
            var progress = new Progress<ForkInstallProgress>(update =>
            {
                if (update == null)
                {
                    return;
                }

                SetInstallOverlayState(update.Title, update.Message, update.Percent, update.IsIndeterminate);
            });

            try
            {
                var result = await _catalogService.DownloadAndInstallAsync(_settings, selectedFork, progress, _installCancellationSource.Token);
                if (!result.Success)
                {
                    SetInstallOverlayState(
                        "Install failed",
                        string.IsNullOrWhiteSpace(result.Message) ? "Fire Launcher could not install this fork." : result.Message,
                        0,
                        false);
                    MessageBox.Show(
                        result.Message,
                        "Fire Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                _profileService.SaveProfile(_settings.ProfilesRoot, _currentProfile);
                PopulateProfileUi(_currentProfile);
                SetInstallOverlayState(
                    "Install complete",
                    selectedFork.DisplayName + " is downloaded and ready to launch.",
                    100,
                    false);
                HeroSelectionText.Text = selectedFork.DisplayName + " is ready.";
            }
            finally
            {
                _installCancellationSource.Dispose();
                _installCancellationSource = null;
                EndInstallOperation();
                UpdateLaunchSection(GetSelectedLaunchFork());
            }
        }

        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUiEvents)
            {
                return;
            }

            SaveCurrentProfileState();
            LoadSelectedProfile();
        }

        private void ForkFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUiEvents || _currentProfile == null)
            {
                return;
            }

            PopulateVersionOptions(GetSelectedLaunchFamilyId(), _currentProfile.SelectedForkId);
            EnsureValidLaunchSelection();
            UpdateLaunchSection(GetSelectedLaunchFork());
        }

        private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUiEvents || _currentProfile == null)
            {
                return;
            }

            var selectedFork = GetSelectedLaunchFork();
            _currentProfile.SelectedForkId = selectedFork == null ? null : selectedFork.Id;
            SyncProfileFamilySelection(selectedFork == null ? null : selectedFork.FamilyId);
            UpdateLaunchSection(selectedFork);
            UpdateDiscordPresence(
                selectedFork == null ? "Editing launcher" : "Browsing " + selectedFork.FamilyName,
                _currentProfile.DisplayName);
        }

        private void OnlineModeCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents || _currentProfile == null)
            {
                return;
            }

            UpdateLaunchSection(GetSelectedLaunchFork());

            if (OnlineModeCheckBox.IsChecked == true)
            {
                var selectedFork = GetSelectedLaunchFork();
                if (selectedFork != null && selectedFork.HasMultiplayer)
                {
                    OpenConnectionOverlay(selectedFork);
                }
            }
        }

        private void ConnectionSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedFork = GetSelectedLaunchFork();
            if (selectedFork == null || !selectedFork.HasMultiplayer)
            {
                return;
            }

            OpenConnectionOverlay(selectedFork);
        }

        private void SaveConnectionSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortTextBox.Text, out var parsedPort) || parsedPort < 1 || parsedPort > 65535)
            {
                MessageBox.Show(
                    "Enter a valid port between 1 and 65535.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(ServerIpTextBox.Text))
            {
                MessageBox.Show(
                    "Enter a server IP before saving online mode settings.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_currentProfile != null)
            {
                _currentProfile.DefaultServerIp = ServerIpTextBox.Text.Trim();
                _currentProfile.DefaultServerPort = parsedPort;
                _currentProfile.OnlineModeEnabled = true;
                _profileService.SaveProfile(_settings.ProfilesRoot, _currentProfile);
            }

            ConnectionOverlay.Visibility = Visibility.Collapsed;
            OnlineModeCheckBox.IsChecked = true;
            UpdateLaunchSection(GetSelectedLaunchFork());
        }

        private void CancelConnectionSettings_Click(object sender, RoutedEventArgs e)
        {
            ConnectionOverlay.Visibility = Visibility.Collapsed;
        }

        private void CancelInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_installCancellationSource != null && !_installCancellationSource.IsCancellationRequested)
            {
                _installCancellationSource.Cancel();
                SetInstallOverlayState("Cancelling install", "Stopping download and cleaning up partial files...", null, true);
            }
        }

        private void ForkFamilyListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUiEvents || _currentProfile == null)
            {
                return;
            }

            PopulateFamilyDetails(GetSelectedFamilyToggle());
        }

        private void ForkVisibilityCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents || _currentProfile == null)
            {
                return;
            }

            var checkBox = sender as CheckBox;
            var item = checkBox == null ? null : checkBox.DataContext as ForkFamilyToggleItem;
            if (item == null)
            {
                return;
            }

            var visibleFamilies = _currentProfile.Forks
                .Where(fork => fork.ShowInLauncher)
                .Select(fork => GetFamilyId(fork))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var isTurningOff = !item.ShowInLauncher;
            if (isTurningOff && visibleFamilies.Count <= 1 && visibleFamilies.Any(id => string.Equals(id, item.FamilyId, StringComparison.OrdinalIgnoreCase)))
            {
                _suppressUiEvents = true;
                item.ShowInLauncher = true;
                if (checkBox != null)
                {
                    checkBox.IsChecked = true;
                }

                _suppressUiEvents = false;
                MessageBox.Show(
                    "Keep at least one fork family enabled in Profiles so the launcher always has something to select.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            foreach (var fork in _currentProfile.Forks.Where(fork => string.Equals(fork.FamilyId, item.FamilyId, StringComparison.OrdinalIgnoreCase)))
            {
                fork.ShowInLauncher = item.ShowInLauncher;
            }

            _profileService.SaveProfile(_settings.ProfilesRoot, _currentProfile);
            PopulateProfileUi(_currentProfile);
        }

        private void RefreshForks_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProfile == null || _operationInProgress)
            {
                return;
            }

            SaveCurrentProfileState();
            _profileService.RefreshKnownForks(_currentProfile);
            _profileService.SaveProfile(_settings.ProfilesRoot, _currentProfile);
            PopulateProfileUi(_currentProfile);

            MessageBox.Show(
                "Fork records refreshed from local installs.",
                "Fire Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OpenForkFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_operationInProgress)
            {
                return;
            }

            var family = GetSelectedFamilyToggle();
            var fork = ResolveFamilyFolderFork(family == null ? null : family.FamilyId);
            if (fork == null || string.IsNullOrWhiteSpace(fork.InstallDirectory) || !Directory.Exists(fork.InstallDirectory))
            {
                MessageBox.Show(
                    "This fork family does not have a valid local install yet.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Process.Start("explorer.exe", fork.InstallDirectory);
        }

        private void LoadApplicationState()
        {
            _settings = _settingsService.Load();
            _settings.ForkCatalogRepositoryUrl = _catalogService.NormalizeRepositoryUrl(_settings.ForkCatalogRepositoryUrl);
            _settingsService.Save(_settings);
            _profileService.EnsureSeedData(_settings);

            ProfilePathTextBox.Text = _settings.ProfilesRoot;
            DownloadsPathTextBox.Text = _settings.DownloadedForksRoot;
            CatalogRepositoryTextBox.Text = _settings.ForkCatalogRepositoryUrl ?? string.Empty;
            DiscordPresenceCheckBox.IsChecked = _settings.DiscordPresenceEnabled;

            LoadProfilesIntoUi();
            ConfigureDiscordPresence();
            SetSettingsStatus("Using the cached Legacy-DB catalog now. Fire Launcher will sync the latest repo data in the background.");
        }

        private async Task<bool> SyncCatalogFromRemoteAsync(bool showCompletionDialog, bool showErrorDialog)
        {
            if (_settings == null)
            {
                return false;
            }

            BeginCatalogSyncOperation("Syncing Legacy-DB and refreshing package data...");

            try
            {
                var manifest = await _catalogService.SyncCatalogAsync(_settings);
                _profileService.EnsureSeedData(_settings);
                LoadProfilesIntoUi();

                var familyCount = manifest == null || manifest.Forks == null ? 0 : manifest.Forks.Count;
                var versionCount = manifest == null || manifest.Forks == null
                    ? 0
                    : manifest.Forks.Sum(family => family.Versions == null ? 0 : family.Versions.Count);

                SetSettingsStatus(
                    "Legacy-DB synced. "
                    + (familyCount == 1 ? "1 family" : familyCount + " families")
                    + " | "
                    + (versionCount == 1 ? "1 version" : versionCount + " versions")
                    + " available.");

                if (showCompletionDialog)
                {
                    MessageBox.Show(
                        "Catalog synced from Legacy-DB.\n\nFamilies: " + familyCount + "\nVersions: " + versionCount,
                        "Fire Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return true;
            }
            catch (Exception ex)
            {
                SetSettingsStatus("Legacy-DB sync failed. Fire Launcher kept the cached catalog. " + ex.Message);

                if (showErrorDialog)
                {
                    MessageBox.Show(
                        "Fire Launcher could not sync Legacy-DB.\n\n" + ex.Message,
                        "Fire Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return false;
            }
            finally
            {
                EndCatalogSyncOperation();
            }
        }

        private void BeginCatalogSyncOperation(string statusMessage)
        {
            _operationInProgress = true;
            _catalogSyncInProgress = true;
            SetSettingsStatus(statusMessage);
            ApplyOperationState();
        }

        private void EndCatalogSyncOperation()
        {
            _catalogSyncInProgress = false;
            _operationInProgress = _installInProgress;
            ApplyOperationState();
        }

        private void BeginInstallOperation(string title, string message, double? percent, bool isIndeterminate)
        {
            _operationInProgress = true;
            _installInProgress = true;
            SetInstallOverlayState(title, message, percent, isIndeterminate);
            UpdateLaunchSection(GetSelectedLaunchFork());
        }

        private void EndInstallOperation()
        {
            _installInProgress = false;
            InstallOverlay.Visibility = Visibility.Collapsed;
            _operationInProgress = _catalogSyncInProgress;
            ApplyOperationState();
            UpdateLaunchSection(GetSelectedLaunchFork());
        }

        private void SetSettingsStatus(string message)
        {
            SettingsStatusText.Text = string.IsNullOrWhiteSpace(message) ? "Ready." : message;
        }

        private void SetInstallOverlayState(string title, string message, double? percent, bool isIndeterminate)
        {
            InstallOverlay.Visibility = Visibility.Visible;
            InstallOverlayTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Installing fork" : title;
            InstallOverlayMessageText.Text = message ?? string.Empty;
            InstallOverlayProgressBar.IsIndeterminate = isIndeterminate;
            InstallOverlayProgressBar.Value = percent ?? 0;
            InstallOverlayProgressText.Text = percent.HasValue && !isIndeterminate
                ? Math.Round(percent.Value).ToString("0") + "%"
                : isIndeterminate ? "Working..." : string.Empty;
            CancelInstallButton.IsEnabled = _installInProgress;
        }

        private void LoadProfilesIntoUi()
        {
            _suppressUiEvents = true;

            var profiles = _profileService.ListProfileIds(_settings.ProfilesRoot).ToList();
            ProfileComboBox.ItemsSource = profiles;

            var selectedProfileId = _settings.SelectedProfileId;
            if (string.IsNullOrWhiteSpace(selectedProfileId) || !profiles.Contains(selectedProfileId))
            {
                selectedProfileId = profiles.FirstOrDefault();
            }

            ProfileComboBox.SelectedItem = selectedProfileId;
            _suppressUiEvents = false;

            LoadSelectedProfile();
        }

        private void LoadSelectedProfile()
        {
            var selectedProfileId = ProfileComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedProfileId))
            {
                return;
            }

            _currentProfile = _profileService.LoadProfile(_settings.ProfilesRoot, selectedProfileId);
            _settings.SelectedProfileId = _currentProfile.Id;
            _settingsService.Save(_settings);

            PopulateProfileUi(_currentProfile);
            UpdateDiscordPresence("Editing profile", _currentProfile.DisplayName);
        }

        private void PopulateProfileUi(ForkProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            _suppressUiEvents = true;

            UsernameTextBox.Text = profile.DefaultUsername ?? string.Empty;
            ServerIpTextBox.Text = profile.DefaultServerIp ?? string.Empty;
            PortTextBox.Text = profile.DefaultServerPort.ToString();
            OnlineModeCheckBox.IsChecked = profile.OnlineModeEnabled;
            HeroProfileText.Text = "Profile: " + profile.DisplayName;
            ProfileDbSummaryText.Text = "Local profile data is ready for this profile.";

            PopulateLaunchFamilyOptions(profile.Forks ?? new List<ForkDefinition>(), profile.SelectedForkId);
            PopulateProfileFamilyList(profile.Forks ?? new List<ForkDefinition>(), profile.SelectedForkId);
            EnsureValidLaunchSelection();

            _suppressUiEvents = false;

            var selectedFork = GetSelectedLaunchFork();
            UpdateLaunchSection(selectedFork);
            PopulateFamilyDetails(GetSelectedFamilyToggle());
            UpdateDetectedForksSummary(profile.Forks);
        }

        private void PopulateLaunchFamilyOptions(List<ForkDefinition> allForks, string selectedForkId)
        {
            var launchableFamilies = allForks
                .Where(IsVisibleInLauncher)
                .GroupBy(GetFamilyId)
                .OrderBy(group => group.First().FamilyName)
                .Select(group => new LaunchFamilyChoice
                {
                    FamilyId = group.Key,
                    FamilyName = group.First().FamilyName
                })
                .ToList();

            var selectedFork = allForks.FirstOrDefault(fork => string.Equals(fork.Id, selectedForkId, StringComparison.OrdinalIgnoreCase));
            var selectedFamily = launchableFamilies.FirstOrDefault(choice =>
                    string.Equals(choice.FamilyId, selectedFork == null ? null : selectedFork.FamilyId, StringComparison.OrdinalIgnoreCase))
                ?? launchableFamilies.FirstOrDefault();

            ForkFamilyComboBox.ItemsSource = launchableFamilies;
            ForkFamilyComboBox.SelectedItem = selectedFamily;
            if (ForkFamilyComboBox.SelectedItem == null && launchableFamilies.Count > 0)
            {
                ForkFamilyComboBox.SelectedIndex = 0;
                selectedFamily = ForkFamilyComboBox.SelectedItem as LaunchFamilyChoice;
            }

            PopulateVersionOptions(selectedFamily == null ? null : selectedFamily.FamilyId, selectedFork == null ? null : selectedFork.Id);
        }

        private void PopulateVersionOptions(string familyId, string selectedForkId)
        {
            var versions = GetLaunchableVersions(familyId)
                .Select(fork => new LaunchVersionChoice
                {
                    ForkId = fork.Id,
                    VersionLabel = fork.VersionLabel,
                    Fork = fork
                })
                .ToList();
            var selected = versions.FirstOrDefault(choice => string.Equals(choice.ForkId, selectedForkId, StringComparison.OrdinalIgnoreCase))
                ?? versions.FirstOrDefault();

            VersionComboBox.ItemsSource = versions;
            VersionComboBox.SelectedItem = selected;
            if (VersionComboBox.SelectedItem == null && versions.Count > 0)
            {
                VersionComboBox.SelectedIndex = 0;
                selected = VersionComboBox.SelectedItem as LaunchVersionChoice;
            }

            if (_currentProfile != null)
            {
                _currentProfile.SelectedForkId = selected == null ? null : selected.ForkId;
            }
        }

        private void EnsureValidLaunchSelection()
        {
            if (_currentProfile == null)
            {
                return;
            }

            var familyChoices = ForkFamilyComboBox.ItemsSource as IEnumerable<LaunchFamilyChoice>;
            var firstFamily = familyChoices == null ? null : familyChoices.FirstOrDefault();
            if (ForkFamilyComboBox.SelectedItem == null && firstFamily != null)
            {
                ForkFamilyComboBox.SelectedItem = firstFamily;
            }

            var selectedFamilyId = GetSelectedLaunchFamilyId();
            if (string.IsNullOrWhiteSpace(selectedFamilyId))
            {
                return;
            }

            var versionChoices = VersionComboBox.ItemsSource as IEnumerable<LaunchVersionChoice>;
            var hasMatchingVersion = versionChoices != null && versionChoices.Any(choice =>
                string.Equals(choice.ForkId, _currentProfile.SelectedForkId, StringComparison.OrdinalIgnoreCase));

            if (!hasMatchingVersion)
            {
                PopulateVersionOptions(selectedFamilyId, _currentProfile.SelectedForkId);
                versionChoices = VersionComboBox.ItemsSource as IEnumerable<LaunchVersionChoice>;
            }

            if (VersionComboBox.SelectedItem == null && versionChoices != null)
            {
                var firstVersion = versionChoices.FirstOrDefault();
                if (firstVersion != null)
                {
                    VersionComboBox.SelectedItem = firstVersion;
                    _currentProfile.SelectedForkId = firstVersion.ForkId;
                }
            }
        }

        private void PopulateProfileFamilyList(List<ForkDefinition> allForks, string selectedForkId)
        {
            var items = allForks
                .GroupBy(GetFamilyId)
                .OrderBy(group => group.First().FamilyName)
                .Select(group => new ForkFamilyToggleItem
                {
                    FamilyId = group.Key,
                    FamilyName = group.First().FamilyName,
                    ShowInLauncher = group.Any(fork => fork.ShowInLauncher),
                    StatusText = BuildFamilyStatusText(group)
                })
                .ToList();

            var selectedFork = allForks.FirstOrDefault(fork => string.Equals(fork.Id, selectedForkId, StringComparison.OrdinalIgnoreCase));
            var selectedItem = items.FirstOrDefault(item =>
                    string.Equals(item.FamilyId, selectedFork == null ? null : selectedFork.FamilyId, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault();

            ForkFamilyListBox.ItemsSource = items;
            ForkFamilyListBox.SelectedItem = selectedItem;
        }

        private void PopulateFamilyDetails(ForkFamilyToggleItem item)
        {
            if (item == null || _currentProfile == null)
            {
                ProfileForkTitleText.Text = "Fork details";
                ProfileForkInstalledText.Text = "No fork family selected.";
                ProfileForkVersionsText.Text = string.Empty;
                ProfileForkMultiplayerText.Text = string.Empty;
                ProfileForkArgumentsText.Text = string.Empty;
                ProfileForkNotesText.Text = string.Empty;
                return;
            }

            var versions = _currentProfile.Forks
                .Where(fork => string.Equals(fork.FamilyId, item.FamilyId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(fork => fork.Enabled)
                .ThenByDescending(fork => fork.VersionLabel)
                .ToList();
            var installedCount = versions.Count(fork => fork.Enabled);

            ProfileForkTitleText.Text = item.FamilyName;
            ProfileForkInstalledText.Text = installedCount == 0
                ? "Installed: none"
                : "Installed: " + installedCount + " of " + versions.Count + " version(s)";
            ProfileForkVersionsText.Text = string.Join(", ", versions.Select(fork => fork.VersionLabel).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
            ProfileForkMultiplayerText.Text = versions.Any(fork => fork.HasMultiplayer) ? "Available on supported versions" : "Not exposed in Fire Launcher";
            ProfileForkArgumentsText.Text = string.Join(" | ", versions.Select(fork => fork.VersionLabel + ": " + BuildArgumentsDescription(fork)).Distinct());
            ProfileForkNotesText.Text = string.Join(" ", versions.Select(fork => fork.Notes).Where(note => !string.IsNullOrWhiteSpace(note)).Distinct());
        }

        private void SaveCurrentProfileState()
        {
            if (_currentProfile == null || _settings == null)
            {
                return;
            }

            _currentProfile.DefaultUsername = UsernameTextBox.Text == null ? string.Empty : UsernameTextBox.Text.Trim();
            _currentProfile.DefaultServerIp = ServerIpTextBox.Text == null ? string.Empty : ServerIpTextBox.Text.Trim();
            _currentProfile.DefaultServerPort = int.TryParse(PortTextBox.Text, out var port) ? port : 19132;
            _currentProfile.OnlineModeEnabled = OnlineModeCheckBox.IsChecked == true;

            var selectedFork = GetSelectedLaunchFork();
            _currentProfile.SelectedForkId = selectedFork == null ? _currentProfile.SelectedForkId : selectedFork.Id;

            _profileService.SaveProfile(_settings.ProfilesRoot, _currentProfile);
        }

        private void SyncProfileFamilySelection(string familyId)
        {
            if (string.IsNullOrWhiteSpace(familyId) || _currentProfile == null)
            {
                return;
            }

            _suppressUiEvents = true;
            var item = (ForkFamilyListBox.ItemsSource as IEnumerable<ForkFamilyToggleItem>)
                ?.FirstOrDefault(entry => string.Equals(entry.FamilyId, familyId, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                ForkFamilyListBox.SelectedItem = item;
            }

            _suppressUiEvents = false;
            PopulateFamilyDetails(item);
        }

        private void UpdateLaunchSection(ForkDefinition fork)
        {
            if (_currentProfile != null && fork == null)
            {
                EnsureValidLaunchSelection();
                fork = GetSelectedLaunchFork();
            }

            ForkFamilyComboBox.IsEnabled = ForkFamilyComboBox.Items.Count > 0;
            VersionComboBox.IsEnabled = VersionComboBox.Items.Count > 0;

            if (_currentProfile == null || fork == null)
            {
                HeroSelectionText.Text = "Choose a fork family and version to begin.";
                BarProfileText.Text = "Launch";
                UsernameFieldPanel.Visibility = Visibility.Collapsed;
                OnlineModePanel.Visibility = Visibility.Collapsed;
                ConnectionSettingsButton.Visibility = Visibility.Collapsed;
                LaunchButton.IsEnabled = false;
                DownloadButton.IsEnabled = false;
                DownloadButton.Content = "Download";
                ApplyOperationState();
                return;
            }

            var onlineModeVisible = fork.HasMultiplayer;
            var onlineModeEnabled = onlineModeVisible && OnlineModeCheckBox.IsChecked == true;
            var hasDownloadSource = !string.IsNullOrWhiteSpace(_catalogService.BuildPackageUrl(_settings, fork));

            HeroSelectionText.Text = fork.Enabled
                ? fork.DisplayName + " is ready."
                : fork.DisplayName + " is available in Legacy-DB.";
            BarProfileText.Text = fork.FamilyName + " / " + fork.VersionLabel;

            UsernameFieldPanel.Visibility = fork.SupportsUsernameArgument ? Visibility.Visible : Visibility.Collapsed;
            UsernameTextBox.IsEnabled = fork.SupportsUsernameArgument;

            OnlineModePanel.Visibility = onlineModeVisible ? Visibility.Visible : Visibility.Collapsed;
            OnlineModeCheckBox.IsEnabled = onlineModeVisible;
            ConnectionSettingsButton.Visibility = onlineModeVisible ? Visibility.Visible : Visibility.Collapsed;
            ConnectionSettingsButton.IsEnabled = onlineModeVisible;

            ServerIpTextBox.IsEnabled = onlineModeEnabled && fork.SupportsServerIpArgument;
            PortTextBox.IsEnabled = onlineModeEnabled;
            LaunchButton.IsEnabled = fork.Enabled;
            DownloadButton.IsEnabled = hasDownloadSource;
            DownloadButton.Content = fork.Enabled ? "Reinstall" : "Download";

            if (!fork.Enabled)
            {
                ApplyOperationState();
                return;
            }

            if (!fork.HasMultiplayer)
            {
                ApplyOperationState();
                return;
            }

            if (!onlineModeEnabled)
            {
                ApplyOperationState();
                return;
            }

            if (fork.SupportsUsernameArgument && fork.SupportsServerIpArgument && !fork.SupportsPortArgument)
            {
                ApplyOperationState();
                return;
            }

            ApplyOperationState();
        }

        private void ApplyOperationState()
        {
            var controlsEnabled = !_operationInProgress;

            ProfileComboBox.IsEnabled = controlsEnabled && ProfileComboBox.Items.Count > 0;
            ForkFamilyListBox.IsEnabled = controlsEnabled && ForkFamilyListBox.Items.Count > 0;
            SaveSettingsButton.IsEnabled = controlsEnabled;
            BrowseProfilesButton.IsEnabled = controlsEnabled;
            BrowseDownloadsButton.IsEnabled = controlsEnabled;
            OpenProfilesButton.IsEnabled = controlsEnabled;
            OpenDownloadsButton.IsEnabled = controlsEnabled;
            ProfilePathTextBox.IsEnabled = controlsEnabled;
            DownloadsPathTextBox.IsEnabled = controlsEnabled;
            CatalogRepositoryTextBox.IsEnabled = controlsEnabled;
            DiscordPresenceCheckBox.IsEnabled = controlsEnabled;
            SaveSettingsButton.Content = _catalogSyncInProgress ? "Syncing..." : "Save & Sync";

            if (controlsEnabled)
            {
                return;
            }

            ForkFamilyComboBox.IsEnabled = false;
            VersionComboBox.IsEnabled = false;
            UsernameTextBox.IsEnabled = false;
            OnlineModeCheckBox.IsEnabled = false;
            ConnectionSettingsButton.IsEnabled = false;
            ServerIpTextBox.IsEnabled = false;
            PortTextBox.IsEnabled = false;
            DownloadButton.IsEnabled = false;
            LaunchButton.IsEnabled = false;

            if (_installInProgress)
            {
                DownloadButton.Content = "Installing...";
            }
        }

        private void UpdateDetectedForksSummary(IEnumerable<ForkDefinition> forks)
        {
            var catalogFamilies = (forks ?? Enumerable.Empty<ForkDefinition>())
                .GroupBy(GetFamilyId)
                .OrderBy(group => group.First().FamilyName)
                .ToList();
            var installedFamilies = catalogFamilies
                .Where(group => group.Any(fork => fork.Enabled))
                .Select(group => group.First().FamilyName)
                .ToList();

            DetectedForksText.Text = catalogFamilies.Count == 0
                ? "No fork families found yet."
                : "Installed families: " + (installedFamilies.Count == 0 ? "none yet" : string.Join(", ", installedFamilies));
        }

        private ForkDefinition GetSelectedLaunchFork()
        {
            var selected = VersionComboBox.SelectedItem as LaunchVersionChoice;
            if (selected != null)
            {
                return selected.Fork;
            }

            return VersionComboBox.SelectedItem as ForkDefinition;
        }

        private string GetSelectedLaunchFamilyId()
        {
            var selected = ForkFamilyComboBox.SelectedItem as LaunchFamilyChoice;
            if (selected != null)
            {
                return selected.FamilyId;
            }

            var legacyFork = ForkFamilyComboBox.SelectedItem as ForkDefinition;
            return legacyFork == null ? null : GetFamilyId(legacyFork);
        }

        private ForkFamilyToggleItem GetSelectedFamilyToggle()
        {
            return ForkFamilyListBox.SelectedItem as ForkFamilyToggleItem;
        }

        private IEnumerable<ForkDefinition> GetLaunchableVersions(string familyId)
        {
            if (_currentProfile == null || string.IsNullOrWhiteSpace(familyId))
            {
                return Enumerable.Empty<ForkDefinition>();
            }

            return _currentProfile.Forks
                .Where(fork => IsVisibleInLauncher(fork) &&
                    string.Equals(fork.FamilyId, familyId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(fork => fork.Enabled)
                .ThenByDescending(fork => fork.VersionLabel)
                .ThenBy(fork => fork.DisplayName);
        }

        private ForkDefinition ResolveFamilyFolderFork(string familyId)
        {
            if (_currentProfile == null || string.IsNullOrWhiteSpace(familyId))
            {
                return null;
            }

            var selectedLaunchFork = GetSelectedLaunchFork();
            if (selectedLaunchFork != null && string.Equals(selectedLaunchFork.FamilyId, familyId, StringComparison.OrdinalIgnoreCase))
            {
                return selectedLaunchFork;
            }

            return _currentProfile.Forks
                .Where(fork => string.Equals(fork.FamilyId, familyId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(fork => fork.Enabled)
                .ThenByDescending(fork => fork.VersionLabel)
                .FirstOrDefault();
        }

        private static bool IsVisibleInLauncher(ForkDefinition fork)
        {
            return fork != null && fork.ShowInLauncher;
        }

        private static string GetFamilyId(ForkDefinition fork)
        {
            return string.IsNullOrWhiteSpace(fork.FamilyId) ? fork.Id : fork.FamilyId;
        }

        private static string BuildFamilyStatusText(IGrouping<string, ForkDefinition> familyGroup)
        {
            var installedCount = familyGroup.Count(fork => fork.Enabled);
            var shown = familyGroup.Any(fork => fork.ShowInLauncher) ? "shown in launcher" : "hidden from launcher";
            var totalVersions = familyGroup.Count();
            return installedCount == 0
                ? totalVersions + " catalog version(s), none installed | " + shown
                : installedCount + " installed of " + totalVersions + " | " + shown;
        }

        private static string BuildArgumentsDescription(ForkDefinition fork)
        {
            var args = new List<string>();

            if (fork.SupportsUsernameArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentName))
            {
                args.Add(fork.LaunchArgumentName + " <username>");
            }

            if (fork.SupportsServerIpArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentIp))
            {
                args.Add(fork.LaunchArgumentIp + " <server>");
            }

            if (fork.SupportsPortArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentPort))
            {
                args.Add(fork.LaunchArgumentPort + " <port>");
            }

            return args.Count == 0 ? "No launch arguments" : string.Join(", ", args);
        }

        private static string BuildLaunchArguments(ForkDefinition fork, string username, string serverIp, int port, bool onlineModeEnabled)
        {
            var arguments = new List<string>();

            if (fork.SupportsUsernameArgument && !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(fork.LaunchArgumentName))
            {
                arguments.Add(fork.LaunchArgumentName);
                arguments.Add(QuoteArgument(username.Trim()));
            }

            if (onlineModeEnabled &&
                fork.HasMultiplayer &&
                fork.SupportsServerIpArgument &&
                !string.IsNullOrWhiteSpace(serverIp) &&
                !string.IsNullOrWhiteSpace(fork.LaunchArgumentIp))
            {
                arguments.Add(fork.LaunchArgumentIp);
                arguments.Add(QuoteArgument(serverIp.Trim()));
            }

            if (onlineModeEnabled && fork.SupportsPortArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentPort))
            {
                arguments.Add(fork.LaunchArgumentPort);
                arguments.Add(port.ToString());
            }

            return string.Join(" ", arguments);
        }

        private static string QuoteArgument(string value)
        {
            return value.Contains(" ")
                ? "\"" + value.Replace("\"", "\\\"") + "\""
                : value;
        }

        private void OpenConnectionOverlay(ForkDefinition fork)
        {
            ConnectionOverlayHintText.Text = "Enter the server IP and port for " + fork.DisplayName + ".";
            ServerIpTextBox.Text = _currentProfile == null ? string.Empty : (_currentProfile.DefaultServerIp ?? string.Empty);
            PortTextBox.Text = (_currentProfile == null ? 19132 : _currentProfile.DefaultServerPort).ToString();
            ConnectionOverlay.Visibility = Visibility.Visible;
            ServerIpTextBox.Focus();
            ServerIpTextBox.SelectAll();
        }

        private void ConfigureDiscordPresence()
        {
            if (_settings == null)
            {
                return;
            }

            _discordPresenceService.Configure(
                _settings.DiscordPresenceEnabled,
                _settings.DiscordApplicationId);
        }

        private void UpdateDiscordPresence(string details, string state)
        {
            _discordPresenceService.UpdatePresence(details, state);
        }
    }
}
