using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RobocopyManager
{
    public partial class MainWindow : Window
    {
        private List<RobocopyJob> jobs = new List<RobocopyJob>();
        private GlobalSettings settings = new GlobalSettings();
        private List<Process> runningProcesses = new List<Process>();
        private int jobIdCounter = 1;
        private System.Windows.Threading.DispatcherTimer schedulerTimer;

        public MainWindow()
        {
            InitializeComponent();
            AddNewJob();
            InitializeScheduler();
        }

        private void InitializeScheduler()
        {
            schedulerTimer = new System.Windows.Threading.DispatcherTimer();
            schedulerTimer.Interval = TimeSpan.FromMinutes(1);
            schedulerTimer.Tick += SchedulerTimer_Tick;
            schedulerTimer.Start();
            Log("Scheduler started - checking every minute for scheduled jobs");
        }

        private void SchedulerTimer_Tick(object sender, EventArgs e)
        {
            var now = DateTime.Now;
            var currentTime = now.TimeOfDay;

            foreach (var job in jobs.Where(j => j.Enabled && j.ScheduleEnabled))
            {
                var scheduledTime = job.ScheduledTime;
                var timeDiff = Math.Abs((currentTime - scheduledTime).TotalMinutes);

                bool shouldRun = timeDiff < 1;

                if (job.LastRun.HasValue)
                {
                    var hoursSinceLastRun = (now - job.LastRun.Value).TotalHours;
                    if (hoursSinceLastRun < 23)
                    {
                        shouldRun = false;
                    }
                }

                if (shouldRun)
                {
                    Log($"[SCHEDULER] Triggering scheduled job: {job.Name} at {now:HH:mm:ss}");
                    job.LastRun = now;
                    Task.Run(() => ExecuteRobocopy(job));
                }
            }
        }

        private void AddNewJob()
        {
            var job = new RobocopyJob { Id = jobIdCounter++, Name = $"Job {jobIdCounter - 1}" };
            jobs.Add(job);
            CreateJobUI(job);
        }

        private void CreateJobUI(RobocopyJob job)
        {
            var border = new Border
            {
                Margin = new Thickness(5),
                Padding = new Thickness(10),
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Tag = job
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var chkEnabled = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            chkEnabled.Checked += (s, e) => job.Enabled = true;
            chkEnabled.Unchecked += (s, e) => job.Enabled = false;

            var txtName = new TextBox { Text = job.Name, Width = 200, Margin = new Thickness(0, 0, 10, 0) };
            txtName.TextChanged += (s, e) => job.Name = txtName.Text;

            var btnDelete = new Button { Content = "Delete", Width = 80, Background = System.Windows.Media.Brushes.IndianRed, Foreground = System.Windows.Media.Brushes.White };
            btnDelete.Click += (s, e) => DeleteJob(border, job);

            headerPanel.Children.Add(chkEnabled);
            headerPanel.Children.Add(txtName);
            headerPanel.Children.Add(btnDelete);
            Grid.SetRow(headerPanel, 0);

            var srcPanel = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            srcPanel.Children.Add(new TextBlock { Text = "Source Path:", FontWeight = FontWeights.Bold });
            var srcStack = new StackPanel { Orientation = Orientation.Horizontal };
            var txtSource = new TextBox { Width = 700, Margin = new Thickness(0, 2, 5, 0) };
            txtSource.TextChanged += (s, e) => job.SourcePath = txtSource.Text;
            var btnBrowseSrc = new Button { Content = "Browse...", Width = 80 };
            btnBrowseSrc.Click += (s, e) => BrowseFolder(txtSource);
            srcStack.Children.Add(txtSource);
            srcStack.Children.Add(btnBrowseSrc);
            srcPanel.Children.Add(srcStack);
            Grid.SetRow(srcPanel, 1);

            var dstPanel = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            dstPanel.Children.Add(new TextBlock { Text = "Destination Path:", FontWeight = FontWeights.Bold });
            var dstStack = new StackPanel { Orientation = Orientation.Horizontal };
            var txtDest = new TextBox { Width = 700, Margin = new Thickness(0, 2, 5, 0) };
            txtDest.TextChanged += (s, e) => job.DestinationPath = txtDest.Text;
            var btnBrowseDst = new Button { Content = "Browse...", Width = 80 };
            btnBrowseDst.Click += (s, e) => BrowseFolder(txtDest);
            dstStack.Children.Add(txtDest);
            dstStack.Children.Add(btnBrowseDst);
            dstPanel.Children.Add(dstStack);
            Grid.SetRow(dstPanel, 2);

            var threadPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            threadPanel.Children.Add(new TextBlock { Text = "Thread Count: ", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            var sliderThreads = new Slider { Width = 300, Minimum = 1, Maximum = 128, Value = 8, Margin = new Thickness(5, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var lblThreads = new TextBlock { Text = "8", Width = 30, VerticalAlignment = VerticalAlignment.Center };
            sliderThreads.ValueChanged += (s, e) => { job.Threads = (int)sliderThreads.Value; lblThreads.Text = job.Threads.ToString(); };
            threadPanel.Children.Add(sliderThreads);
            threadPanel.Children.Add(lblThreads);
            threadPanel.Children.Add(new TextBlock { Text = " (Recommended: 8-32 for network)", FontStyle = FontStyles.Italic, Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
            Grid.SetRow(threadPanel, 3);

            var cmdPanel = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            var txtCommand = new TextBox { IsReadOnly = true, Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.LightGreen, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Padding = new Thickness(5), TextWrapping = TextWrapping.Wrap };
            txtCommand.Text = GenerateRobocopyCommand(job);
            txtSource.TextChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            txtDest.TextChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            sliderThreads.ValueChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            cmdPanel.Children.Add(txtCommand);
            Grid.SetRow(cmdPanel, 4);

            grid.Children.Add(headerPanel);
            grid.Children.Add(srcPanel);
            grid.Children.Add(dstPanel);
            grid.Children.Add(threadPanel);
            grid.Children.Add(schedulePanel);
            grid.Children.Add(cmdPanel);

            border.Child = grid;
            jobsPanel.Children.Insert(jobsPanel.Children.Count - 1, border);
        }

        private void DeleteJob(Border border, RobocopyJob job)
        {
            jobs.Remove(job);
            jobsPanel.Children.Remove(border);
        }

        private void BrowseFolder(TextBox textBox)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select a folder",
                Filter = "Folders|*.none",
                FileName = "Select Folder",
                CheckFileExists = false,
                CheckPathExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                textBox.Text = System.IO.Path.GetDirectoryName(dialog.FileName);
            }
        }

        private string GenerateRobocopyCommand(RobocopyJob job)
        {
            var cmd = $"robocopy \"{job.SourcePath}\" \"{job.DestinationPath}\"";

            if (settings.MirrorMode)
                cmd += " /MIR";
            else if (settings.CopySubdirs)
                cmd += settings.CopyEmptyDirs ? " /E" : " /S";

            cmd += $" /MT:{job.Threads}";
            cmd += $" /R:{settings.Retries}";
            cmd += $" /W:{settings.WaitTime}";
            cmd += " /NP /V /TS";

            if (settings.PurgeDestination && !settings.MirrorMode)
                cmd += " /PURGE";

            return cmd;
        }

        private async void BtnRunAll_Click(object sender, RoutedEventArgs e)
        {
            var enabledJobs = jobs.Where(j => j.Enabled && !string.IsNullOrWhiteSpace(j.SourcePath) && !string.IsNullOrWhiteSpace(j.DestinationPath)).ToList();

            if (!enabledJobs.Any())
            {
                MessageBox.Show("No valid jobs to run. Please configure source and destination paths.", "No Jobs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnRunAll.IsEnabled = false;
            btnStopAll.IsEnabled = true;
            txtLog.Clear();
            runningProcesses.Clear();

            Log("========================================");
            Log($"Starting {enabledJobs.Count} job(s) at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================\n");

            var tasks = enabledJobs.Select(job => Task.Run(() => ExecuteRobocopy(job))).ToArray();
            await Task.WhenAll(tasks);

            Log("\n========================================");
            Log($"All jobs completed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================");

            btnRunAll.IsEnabled = true;
            btnStopAll.IsEnabled = false;
        }

        private void ExecuteRobocopy(RobocopyJob job)
        {
            try
            {
                // Archive old versions if versioning is enabled
                if (settings.EnableVersioning)
                {
                    ArchiveOldVersions(job);
                }

                var command = GenerateRobocopyCommand(job);
                Log($"[{job.Name}] Starting...");
                Log($"[{job.Name}] Command: {command}\n");

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {command}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                lock (runningProcesses)
                {
                    runningProcesses.Add(process);
                }

                process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log($"[{job.Name}] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log($"[{job.Name}] ERROR: {e.Data}"); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                lock (runningProcesses)
                {
                    runningProcesses.Remove(process);
                }

                Log($"[{job.Name}] Completed with exit code: {process.ExitCode}\n");
            }
            catch (Exception ex)
            {
                Log($"[{job.Name}] ERROR: {ex.Message}");
            }
        }

        private void ArchiveOldVersions(RobocopyJob job)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(job.SourcePath) || string.IsNullOrWhiteSpace(job.DestinationPath))
                    return;

                if (!Directory.Exists(job.SourcePath) || !Directory.Exists(job.DestinationPath))
                    return;

                Log($"[{job.Name}] Checking for files to archive...");

                var versionPath = Path.Combine(job.DestinationPath, settings.VersionFolder);
                Directory.CreateDirectory(versionPath);

                // Get all files in source
                var sourceFiles = Directory.GetFiles(job.SourcePath, "*.*", SearchOption.AllDirectories);

                int archivedCount = 0;
                foreach (var sourceFile in sourceFiles)
                {
                    var relativePath = sourceFile.Substring(job.SourcePath.Length).TrimStart('\\');
                    var destFile = Path.Combine(job.DestinationPath, relativePath);

                    // If file exists in destination and will be overwritten
                    if (File.Exists(destFile))
                    {
                        var sourceInfo = new FileInfo(sourceFile);
                        var destInfo = new FileInfo(destFile);

                        // Check if source is newer (will be overwritten by robocopy)
                        if (sourceInfo.LastWriteTime > destInfo.LastWriteTime)
                        {
                            // Archive the old version
                            var timestamp = destInfo.LastWriteTime.ToString("yyyy-MM-dd_HH-mm-ss");
                            var fileName = Path.GetFileNameWithoutExtension(destFile);
                            var extension = Path.GetExtension(destFile);
                            var versionFileName = $"{fileName}_{timestamp}{extension}";

                            var relativeDir = Path.GetDirectoryName(relativePath);
                            var versionDir = string.IsNullOrEmpty(relativeDir)
                                ? versionPath
                                : Path.Combine(versionPath, relativeDir);

                            Directory.CreateDirectory(versionDir);
                            var versionFilePath = Path.Combine(versionDir, versionFileName);

                            File.Copy(destFile, versionFilePath, true);
                            archivedCount++;
                            Log($"[{job.Name}] Archived: {relativePath} -> {settings.VersionFolder}\\{Path.Combine(relativeDir ?? "", versionFileName)}");
                        }
                    }
                }

                if (archivedCount > 0)
                {
                    Log($"[{job.Name}] Archived {archivedCount} file(s) to {settings.VersionFolder}");
                    CleanupOldVersions(versionPath, job.Name);
                }
                else
                {
                    Log($"[{job.Name}] No files needed archiving");
                }
            }
            catch (Exception ex)
            {
                Log($"[{job.Name}] Archiving error: {ex.Message}");
            }
        }

        private void CleanupOldVersions(string versionPath, string jobName)
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-settings.DaysToKeepVersions);
                var allVersionFiles = Directory.GetFiles(versionPath, "*.*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .ToList();

                int deletedCount = 0;

                // Delete files older than X days
                if (settings.DaysToKeepVersions > 0)
                {
                    var oldFiles = allVersionFiles.Where(f => f.LastWriteTime < cutoffDate).ToList();
                    foreach (var oldFile in oldFiles)
                    {
                        try
                        {
                            oldFile.Delete();
                            deletedCount++;
                        }
                        catch { }
                    }

                    if (deletedCount > 0)
                    {
                        Log($"[{jobName}] Cleaned up {deletedCount} version(s) older than {settings.DaysToKeepVersions} days");
                    }

                    // Refresh the list after deleting old files
                    allVersionFiles = Directory.GetFiles(versionPath, "*.*", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .ToList();
                }

                // Limit max versions per file
                if (settings.MaxVersionsPerFile > 0)
                {
                    var fileGroups = allVersionFiles.GroupBy(f =>
                    {
                        var name = Path.GetFileNameWithoutExtension(f.Name);
                        var lastUnderscore = name.LastIndexOf('_');
                        var dir = Path.GetDirectoryName(f.FullName);
                        var baseName = lastUnderscore > 0 ? name.Substring(0, lastUnderscore) : name;
                        return Path.Combine(dir, baseName);
                    });

                    int versionLimitDeleted = 0;
                    foreach (var group in fileGroups)
                    {
                        var versionsToDelete = group
                            .OrderByDescending(f => f.LastWriteTime)
                            .Skip(settings.MaxVersionsPerFile)
                            .ToList();

                        foreach (var oldFile in versionsToDelete)
                        {
                            try
                            {
                                oldFile.Delete();
                                versionLimitDeleted++;
                            }
                            catch { }
                        }
                    }

                    if (versionLimitDeleted > 0)
                    {
                        Log($"[{jobName}] Cleaned up {versionLimitDeleted} version(s) exceeding max {settings.MaxVersionsPerFile} per file");
                    }
                }

                // Clean up empty directories
                DeleteEmptyDirectories(versionPath);
            }
            catch (Exception ex)
            {
                Log($"[{jobName}] Cleanup error: {ex.Message}");
            }
        }

        private void DeleteEmptyDirectories(string path)
        {
            try
            {
                foreach (var directory in Directory.GetDirectories(path))
                {
                    DeleteEmptyDirectories(directory);
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory, false);
                    }
                }
            }
            catch
            {
                // Skip directories we can't access
            }
        }

        private void BtnStopAll_Click(object sender, RoutedEventArgs e)
        {
            lock (runningProcesses)
            {
                foreach (var proc in runningProcesses)
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill();
                            Log("Process terminated by user.");
                        }
                    }
                    catch { }
                }
                runningProcesses.Clear();
            }
            btnRunAll.IsEnabled = true;
            btnStopAll.IsEnabled = false;
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText(message + Environment.NewLine);
                txtLog.ScrollToEnd();
            });
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(settings);
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
        }

        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = "JSON files (*.json)|*.json", DefaultExt = "json" };
            if (dialog.ShowDialog() == true)
            {
                var config = new Config { Jobs = jobs, Settings = settings };
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show("Configuration saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnLoadConfig_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "JSON files (*.json)|*.json" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var config = JsonSerializer.Deserialize<Config>(json);

                    jobsPanel.Children.Clear();
                    jobsPanel.Children.Add(new Button { Content = "Add New Job", Height = 40, Margin = new Thickness(5), Background = System.Windows.Media.Brushes.DodgerBlue, Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold });
                    ((Button)jobsPanel.Children[0]).Click += BtnAddJob_Click;

                    jobs = config.Jobs;
                    settings = config.Settings;
                    jobIdCounter = jobs.Any() ? jobs.Max(j => j.Id) + 1 : 1;

                    foreach (var job in jobs)
                    {
                        CreateJobUI(job);
                    }

                    if (schedulerTimer != null)
                    {
                        schedulerTimer.Stop();
                        InitializeScheduler();
                    }

                    MessageBox.Show("Configuration loaded successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnAddJob_Click(object sender, RoutedEventArgs e)
        {
            AddNewJob();
        }
    }

    public class RobocopyJob
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public int Threads { get; set; } = 8;
        public bool Enabled { get; set; } = true;
        public bool ScheduleEnabled { get; set; } = false;
        public TimeSpan ScheduledTime { get; set; } = new TimeSpan(18, 0, 0);
        public DateTime? LastRun { get; set; }
    }

    public class GlobalSettings
    {
        public int Retries { get; set; } = 3;
        public int WaitTime { get; set; } = 30;
        public bool CopySubdirs { get; set; } = true;
        public bool CopyEmptyDirs { get; set; } = false;
        public bool PurgeDestination { get; set; } = false;
        public bool MirrorMode { get; set; } = true;
        public bool EnableVersioning { get; set; } = true;
        public string VersionFolder { get; set; } = "OldVersions";
        public int DaysToKeepVersions { get; set; } = 30;
        public int MaxVersionsPerFile { get; set; } = 0;
    }

    public class Config
    {
        public List<RobocopyJob> Jobs { get; set; }
        public GlobalSettings Settings { get; set; }
    }
}