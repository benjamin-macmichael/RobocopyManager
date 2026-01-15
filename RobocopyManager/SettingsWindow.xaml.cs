using System.Windows;
namespace RobocopyManager
{
    public partial class SettingsWindow : Window
    {
        private GlobalSettings settings;
        public SettingsWindow(GlobalSettings settings)
        {
            InitializeComponent();
            this.settings = settings;
            LoadSettings();
        }
        private void LoadSettings()
        {
            txtRetries.Text = settings.Retries.ToString();
            txtWaitTime.Text = settings.WaitTime.ToString();
            chkMirrorMode.IsChecked = settings.MirrorMode;
            chkCopySubdirs.IsChecked = settings.CopySubdirs;
            chkCopyEmptyDirs.IsChecked = settings.CopyEmptyDirs;
            chkPurge.IsChecked = settings.PurgeDestination;
            chkEnableVersioning.IsChecked = settings.EnableVersioning;
            txtDaysToKeep.Text = settings.DaysToKeepVersions.ToString();
            txtMaxVersionsPerFile.Text = settings.MaxVersionsPerFile.ToString();
        }
        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtRetries.Text, out int retries))
                settings.Retries = retries;
            if (int.TryParse(txtWaitTime.Text, out int waitTime))
                settings.WaitTime = waitTime;
            if (int.TryParse(txtDaysToKeep.Text, out int daysToKeep))
                settings.DaysToKeepVersions = daysToKeep;
            if (int.TryParse(txtMaxVersionsPerFile.Text, out int maxVersions))
                settings.MaxVersionsPerFile = maxVersions;
            settings.MirrorMode = chkMirrorMode.IsChecked == true;
            settings.CopySubdirs = chkCopySubdirs.IsChecked == true;
            settings.CopyEmptyDirs = chkCopyEmptyDirs.IsChecked == true;
            settings.PurgeDestination = chkPurge.IsChecked == true;
            settings.EnableVersioning = chkEnableVersioning.IsChecked == true;
            // VersionFolder is now hardcoded to "OldVersions" and not user-configurable
            DialogResult = true;
            Close();
        }
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}