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
        private Dictionary<int, Process> jobProcesses = new Dictionary<int, Process>(); // Track which process belongs to which job
        private int jobIdCounter = 1;
        private System.Threading.Timer schedulerTimer = null;
        private readonly string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RobocopyManager",
            "Logs"
        );
        private StreamWriter logFileWriter = null;
        private readonly string configFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RobocopyManager",
            "config.json"
        );

        public MainWindow()
        {
            InitializeComponent();
            InitializeLogFile();
            LoadConfigAutomatically();
            InitializeScheduler();
        }

        private void InitializeLogFile()
        {
            try
            {
                // Create logs directory if it doesn't exist
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                // Create log file with today's date
                var logFileName = $"RobocopyManager_{DateTime.Now:yyyy-MM-dd}.log";
                var logFilePath = Path.Combine(logDirectory, logFileName);

                // Open file for appending
                logFileWriter = new StreamWriter(logFilePath, append: true)
                {
                    AutoFlush = true // Ensure logs are written immediately
                };

                Log($"=== Application started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                Log($"Log file: {logFilePath}");

                // Clean up old log files (keep last 30 days)
                CleanupOldLogFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing log file: {ex.Message}", "Logging Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CleanupOldLogFiles()
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-30);
                var logFiles = Directory.GetFiles(logDirectory, "RobocopyManager_*.log");

                foreach (var logFile in logFiles)
                {
                    var fileInfo = new FileInfo(logFile);
                    if (fileInfo.LastWriteTime < cutoffDate)
                    {
                        try
                        {
                            File.Delete(logFile);
                            Log($"Deleted old log file: {Path.GetFileName(logFile)}");
                        }
                        catch
                        {
                            // Skip files we can't delete
                        }
                    }
                }
            }
            catch
            {
                // Fail silently if we can't clean up logs
            }
        }

        private void LoadConfigAutomatically()
        {
            try
            {
                if (File.Exists(configFilePath))
                {
                    var json = File.ReadAllText(configFilePath);
                    var config = JsonSerializer.Deserialize<Config>(json);

                    jobs = config.Jobs ?? new List<RobocopyJob>();
                    settings = config.Settings ?? new GlobalSettings();
                    jobIdCounter = jobs.Any() ? jobs.Max(j => j.Id) + 1 : 1;

                    foreach (var job in jobs)
                    {
                        CreateJobUI(job);
                    }

                    Log($"Loaded {jobs.Count} saved job(s) from previous session");
                }
                else
                {
                    Log("No saved jobs found - starting fresh");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading saved configuration: {ex.Message}\nStarting with empty configuration.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveConfigAutomatically()
        {
            try
            {
                var configDir = Path.GetDirectoryName(configFilePath);
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

                var config = new Config { Jobs = jobs, Settings = settings };
                File.WriteAllText(configFilePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log($"Error auto-saving configuration: {ex.Message}");
            }
        }

        private void InitializeScheduler()
        {
            // Use System.Threading.Timer instead of DispatcherTimer for reliable background operation
            // This works properly even when RDP is disconnected or screen is locked
            schedulerTimer = new System.Threading.Timer(
                SchedulerTimer_Tick,
                null,
                TimeSpan.FromSeconds(10), // Start after 10 seconds
                TimeSpan.FromMinutes(1)   // Check every minute
            );
            Log("Scheduler started - checking every minute for scheduled jobs (background timer)");
        }

        private void SchedulerTimer_Tick(object state)
        {
            var now = DateTime.Now;
            var currentTime = now.TimeOfDay;

            var scheduledJobs = jobs.Where(j => j.Enabled && j.ScheduleEnabled).ToList();

            foreach (var job in scheduledJobs)
            {
                var scheduledTime = job.ScheduledTime;
                var timeDiff = Math.Abs((currentTime - scheduledTime).TotalMinutes);

                // Only run if we're within 1 minute AND haven't run in the last 2 minutes (prevents duplicate runs)
                bool withinScheduleWindow = timeDiff < 1;
                bool notRecentlyRun = !job.LastRun.HasValue || (now - job.LastRun.Value).TotalMinutes >= 2;

                // Only check if within schedule window
                if (withinScheduleWindow)
                {
                    // CRITICAL: Check if job is already running
                    bool isCurrentlyRunning = false;
                    lock (jobProcesses)
                    {
                        isCurrentlyRunning = jobProcesses.ContainsKey(job.Id);
                    }

                    if (isCurrentlyRunning)
                    {
                        // Don't spam the log - skip silently
                        continue;
                    }

                    if (notRecentlyRun)
                    {
                        Log($"[SCHEDULER] Triggering scheduled job: {job.Name} at {now:HH:mm:ss}");
                        job.LastRun = now;
                        SaveConfigAutomatically();

                        // Enable Stop All button when scheduled job starts (must use Dispatcher since we're on background thread)
                        Dispatcher.Invoke(() => btnStopAll.IsEnabled = true);

                        Task.Run(() => ExecuteRobocopy(job));
                    }
                }
            }
        }

        private void AddNewJob()
        {
            var job = new RobocopyJob { Id = jobIdCounter++, Name = $"Job {jobIdCounter - 1}" };
            jobs.Add(job);
            CreateJobUI(job);
            SaveConfigAutomatically();
            Log($"New job added: {job.Name}");
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
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var chkEnabled = new CheckBox { IsChecked = job.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            chkEnabled.Checked += (s, e) => { job.Enabled = true; SaveConfigAutomatically(); };
            chkEnabled.Unchecked += (s, e) => { job.Enabled = false; SaveConfigAutomatically(); };

            var txtName = new TextBox { Text = job.Name, Width = 200, Margin = new Thickness(0, 0, 10, 0) };
            txtName.TextChanged += (s, e) => { job.Name = txtName.Text; SaveConfigAutomatically(); };

            var btnRunNow = new Button { Content = "Run Now", Width = 80, Background = System.Windows.Media.Brushes.DodgerBlue, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 5, 0) };
            var btnCancelJob = new Button { Content = "Cancel", Width = 80, Background = System.Windows.Media.Brushes.Orange, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 10, 0), IsEnabled = false };

            btnRunNow.Click += (s, e) => RunSingleJob(job, btnRunNow, btnCancelJob);
            btnCancelJob.Click += (s, e) => CancelSingleJob(job, btnRunNow, btnCancelJob);

            var btnDelete = new Button { Content = "Delete", Width = 80, Background = System.Windows.Media.Brushes.IndianRed, Foreground = System.Windows.Media.Brushes.White };
            btnDelete.Click += (s, e) => DeleteJob(border, job);

            headerPanel.Children.Add(chkEnabled);
            headerPanel.Children.Add(txtName);
            headerPanel.Children.Add(btnRunNow);
            headerPanel.Children.Add(btnCancelJob);
            headerPanel.Children.Add(btnDelete);
            Grid.SetRow(headerPanel, 0);

            var srcPanel = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            srcPanel.Children.Add(new TextBlock { Text = "Source Path:", FontWeight = FontWeights.Bold });
            var srcStack = new StackPanel { Orientation = Orientation.Horizontal };
            var txtSource = new TextBox { Width = 700, Margin = new Thickness(0, 2, 5, 0), Text = job.SourcePath };
            txtSource.TextChanged += (s, e) => { job.SourcePath = txtSource.Text; SaveConfigAutomatically(); };
            var btnBrowseSrc = new Button { Content = "Browse...", Width = 80 };
            btnBrowseSrc.Click += (s, e) => BrowseFolder(txtSource);
            srcStack.Children.Add(txtSource);
            srcStack.Children.Add(btnBrowseSrc);
            srcPanel.Children.Add(srcStack);
            Grid.SetRow(srcPanel, 1);

            var dstPanel = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            dstPanel.Children.Add(new TextBlock { Text = "Destination Path:", FontWeight = FontWeights.Bold });
            var dstStack = new StackPanel { Orientation = Orientation.Horizontal };
            var txtDest = new TextBox { Width = 700, Margin = new Thickness(0, 2, 5, 0), Text = job.DestinationPath };
            txtDest.TextChanged += (s, e) => { job.DestinationPath = txtDest.Text; SaveConfigAutomatically(); };
            var btnBrowseDst = new Button { Content = "Browse...", Width = 80 };
            btnBrowseDst.Click += (s, e) => BrowseFolder(txtDest);
            dstStack.Children.Add(txtDest);
            dstStack.Children.Add(btnBrowseDst);
            dstPanel.Children.Add(dstStack);
            Grid.SetRow(dstPanel, 2);

            var excludePanel = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            excludePanel.Children.Add(new TextBlock { Text = "Exclude Directories (comma-separated, e.g., temp, .git, node_modules):", FontWeight = FontWeights.Bold });
            var txtExclude = new TextBox { Width = 788, Margin = new Thickness(0, 2, 0, 0), Text = job.ExcludedDirectories };
            txtExclude.TextChanged += (s, e) => { job.ExcludedDirectories = txtExclude.Text; SaveConfigAutomatically(); };
            excludePanel.Children.Add(txtExclude);
            Grid.SetRow(excludePanel, 3);

            var threadPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            threadPanel.Children.Add(new TextBlock { Text = "Thread Count: ", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            var sliderThreads = new Slider { Width = 300, Minimum = 1, Maximum = 128, Value = job.Threads, Margin = new Thickness(5, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var lblThreads = new TextBlock { Text = job.Threads.ToString(), Width = 30, VerticalAlignment = VerticalAlignment.Center };
            sliderThreads.ValueChanged += (s, e) => { job.Threads = (int)sliderThreads.Value; lblThreads.Text = job.Threads.ToString(); SaveConfigAutomatically(); };
            threadPanel.Children.Add(sliderThreads);
            threadPanel.Children.Add(lblThreads);
            threadPanel.Children.Add(new TextBlock { Text = " (Recommended: 8-32 for network)", FontStyle = FontStyles.Italic, Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
            Grid.SetRow(threadPanel, 4);

            var schedulePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            var chkSchedule = new CheckBox { Content = "Run on schedule: ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            chkSchedule.IsChecked = job.ScheduleEnabled;
            chkSchedule.Checked += (s, e) => { job.ScheduleEnabled = true; SaveConfigAutomatically(); };
            chkSchedule.Unchecked += (s, e) => { job.ScheduleEnabled = false; SaveConfigAutomatically(); };

            var txtHour = new TextBox { Width = 40, Text = job.ScheduledTime.Hours.ToString("D2"), Margin = new Thickness(0, 0, 5, 0) };
            var lblColon = new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0), FontWeight = FontWeights.Bold };
            var txtMinute = new TextBox { Width = 40, Text = job.ScheduledTime.Minutes.ToString("D2"), Margin = new Thickness(0, 0, 5, 0) };
            var lblExample = new TextBlock { Text = "(24-hour format, e.g., 18:00 for 6 PM)", FontStyle = FontStyles.Italic, Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };

            txtHour.TextChanged += (s, e) => {
                if (int.TryParse(txtHour.Text, out int h) && h >= 0 && h <= 23)
                {
                    job.ScheduledTime = new TimeSpan(h, job.ScheduledTime.Minutes, 0);
                    SaveConfigAutomatically();
                }
            };
            txtMinute.TextChanged += (s, e) => {
                if (int.TryParse(txtMinute.Text, out int m) && m >= 0 && m <= 59)
                {
                    job.ScheduledTime = new TimeSpan(job.ScheduledTime.Hours, m, 0);
                    SaveConfigAutomatically();
                }
            };

            schedulePanel.Children.Add(chkSchedule);
            schedulePanel.Children.Add(txtHour);
            schedulePanel.Children.Add(lblColon);
            schedulePanel.Children.Add(txtMinute);
            schedulePanel.Children.Add(lblExample);
            Grid.SetRow(schedulePanel, 5);

            var cmdPanel = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            var txtCommand = new TextBox { IsReadOnly = true, Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.LightGreen, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Padding = new Thickness(5), TextWrapping = TextWrapping.Wrap };
            txtCommand.Text = GenerateRobocopyCommand(job);
            txtSource.TextChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            txtDest.TextChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            txtExclude.TextChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            sliderThreads.ValueChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            cmdPanel.Children.Add(txtCommand);
            Grid.SetRow(cmdPanel, 6);

            grid.Children.Add(headerPanel);
            grid.Children.Add(srcPanel);
            grid.Children.Add(dstPanel);
            grid.Children.Add(excludePanel);
            grid.Children.Add(threadPanel);
            grid.Children.Add(schedulePanel);
            grid.Children.Add(cmdPanel);

            border.Child = grid;
            jobsPanel.Children.Insert(jobsPanel.Children.Count - 1, border);
        }

        private async void RunSingleJob(RobocopyJob job, Button btnRunNow, Button btnCancelJob)
        {
            if (string.IsNullOrWhiteSpace(job.SourcePath) || string.IsNullOrWhiteSpace(job.DestinationPath))
            {
                MessageBox.Show("Please configure source and destination paths before running.", "Invalid Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if job is already running
            bool isRunning = false;
            lock (jobProcesses)
            {
                isRunning = jobProcesses.ContainsKey(job.Id);
            }

            if (isRunning)
            {
                MessageBox.Show($"Job '{job.Name}' is already running. Please wait for it to complete or cancel it first.", "Job Already Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnRunNow.IsEnabled = false;
            btnRunNow.Content = "Running...";
            btnCancelJob.IsEnabled = true;

            Log("========================================");
            Log($"Starting single job: {job.Name} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================\n");

            await Task.Run(() => ExecuteRobocopy(job));

            Log($"\n[{job.Name}] Job completed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================\n");

            btnRunNow.IsEnabled = true;
            btnRunNow.Content = "Run Now";
            btnCancelJob.IsEnabled = false;
        }

        private void CancelSingleJob(RobocopyJob job, Button btnRunNow, Button btnCancelJob)
        {
            lock (jobProcesses)
            {
                if (jobProcesses.ContainsKey(job.Id))
                {
                    try
                    {
                        var process = jobProcesses[job.Id];
                        if (!process.HasExited)
                        {
                            process.Kill();
                            Log($"[{job.Name}] Job cancelled by user.");
                        }
                        jobProcesses.Remove(job.Id);
                    }
                    catch (Exception ex)
                    {
                        Log($"[{job.Name}] Error cancelling job: {ex.Message}");
                    }
                }
            }

            btnRunNow.IsEnabled = true;
            btnRunNow.Content = "Run Now";
            btnCancelJob.IsEnabled = false;
        }

        private void DeleteJob(Border border, RobocopyJob job)
        {
            var result = MessageBox.Show($"Are you sure you want to delete '{job.Name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                jobs.Remove(job);
                jobsPanel.Children.Remove(border);
                SaveConfigAutomatically();
                Log($"Deleted job: {job.Name}");
            }
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
            cmd += " /NP"; // No progress percentage
            cmd += " /A-:SH"; // Strip System and Hidden attributes

            if (settings.PurgeDestination && !settings.MirrorMode)
                cmd += " /PURGE";

            // Build list of excluded directories
            var excludedDirs = new List<string>();

            // Always exclude common Windows system folders
            excludedDirs.Add("$RECYCLE.BIN");
            excludedDirs.Add("System Volume Information");
            excludedDirs.Add("$Recycle.Bin");
            excludedDirs.Add("Recycler");

            // Exclude the version folder from robocopy operations
            if (settings.EnableVersioning && !string.IsNullOrWhiteSpace(settings.VersionFolder))
            {
                excludedDirs.Add(settings.VersionFolder);
            }

            // Add user-specified excluded directories
            if (!string.IsNullOrWhiteSpace(job.ExcludedDirectories))
            {
                var userExcluded = job.ExcludedDirectories.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => d.Trim())
                    .Where(d => !string.IsNullOrWhiteSpace(d));

                excludedDirs.AddRange(userExcluded);
            }

            // Add each exclusion with its own /XD flag (safest approach)
            foreach (var dir in excludedDirs)
            {
                cmd += $" /XD \"{dir}\"";
            }

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

                lock (jobProcesses)
                {
                    jobProcesses[job.Id] = process;
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

                lock (jobProcesses)
                {
                    jobProcesses.Remove(job.Id);
                }

                // Disable Stop All button if no more jobs are running
                bool anyRunning = false;
                lock (runningProcesses)
                {
                    anyRunning = runningProcesses.Any();
                }

                if (!anyRunning)
                {
                    Dispatcher.Invoke(() => btnStopAll.IsEnabled = false);
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
                {
                    Log($"[{job.Name}] Archiving skipped - paths not configured");
                    return;
                }

                if (!Directory.Exists(job.SourcePath))
                {
                    Log($"[{job.Name}] Archiving skipped - source path does not exist: {job.SourcePath}");
                    return;
                }

                if (!Directory.Exists(job.DestinationPath))
                {
                    Log($"[{job.Name}] Archiving skipped - destination path does not exist: {job.DestinationPath}");
                    return;
                }

                Log($"[{job.Name}] Checking for files to archive...");
                Log($"[{job.Name}] Source: {job.SourcePath}");
                Log($"[{job.Name}] Destination: {job.DestinationPath}");

                var versionPath = Path.Combine(job.DestinationPath, settings.VersionFolder);

                // CRITICAL: Create the directory FIRST to test if directory creation works
                if (!Directory.Exists(versionPath))
                {
                    Log($"[{job.Name}] Creating archive directory: {versionPath}");
                    Directory.CreateDirectory(versionPath);

                    if (Directory.Exists(versionPath))
                    {
                        Log($"[{job.Name}] ✓ Archive directory created successfully");
                    }
                    else
                    {
                        Log($"[{job.Name}] ✗ ERROR: Failed to create archive directory!");
                        return;
                    }
                }
                else
                {
                    Log($"[{job.Name}] Archive directory already exists: {versionPath}");
                }

                // Get all files in source and destination
                var sourceFiles = Directory.Exists(job.SourcePath)
                    ? Directory.GetFiles(job.SourcePath, "*.*", SearchOption.AllDirectories)
                        .Select(f => f.Substring(job.SourcePath.Length).TrimStart('\\'))
                        .ToHashSet()
                    : new HashSet<string>();

                var destFiles = Directory.GetFiles(job.DestinationPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => !f.StartsWith(versionPath, StringComparison.OrdinalIgnoreCase)); // Exclude OldVersions folder itself

                Log($"[{job.Name}] Found {sourceFiles.Count} file(s) in source");
                Log($"[{job.Name}] Found {destFiles.Count()} file(s) in destination");

                int archivedCount = 0;
                int skippedSameOrNewer = 0;
                int skippedNewFiles = 0;

                foreach (var destFilePath in destFiles)
                {
                    var relativePath = destFilePath.Substring(job.DestinationPath.Length).TrimStart('\\');
                    var sourceFilePath = Path.Combine(job.SourcePath, relativePath);

                    Log($"[{job.Name}] Checking: {relativePath}");

                    // Case 1: File exists in BOTH source and destination
                    if (sourceFiles.Contains(relativePath))
                    {
                        var sourceInfo = new FileInfo(sourceFilePath);
                        var destInfo = new FileInfo(destFilePath);

                        Log($"[{job.Name}]   Source time: {sourceInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss.fff}");
                        Log($"[{job.Name}]   Dest time:   {destInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss.fff}");
                        Log($"[{job.Name}]   Difference:  {(sourceInfo.LastWriteTime - destInfo.LastWriteTime).TotalSeconds:F2} seconds");

                        // Check if source is newer (will be overwritten by robocopy)
                        if ((sourceInfo.LastWriteTime - destInfo.LastWriteTime).TotalSeconds > 2)
                        {
                            Log($"[{job.Name}]   → Source is NEWER - will archive destination");

                            if (ArchiveFile(destFilePath, relativePath, destInfo, versionPath, job.Name))
                            {
                                archivedCount++;
                            }
                        }
                        else
                        {
                            Log($"[{job.Name}]   → Skipped - destination is same age or newer");
                            skippedSameOrNewer++;
                        }
                    }
                    // Case 2: File exists ONLY in destination (will be DELETED by mirror mode)
                    else if (settings.MirrorMode || settings.PurgeDestination)
                    {
                        Log($"[{job.Name}]   → File will be DELETED by mirror/purge - archiving for safety");
                        var destInfo = new FileInfo(destFilePath);

                        if (ArchiveFile(destFilePath, relativePath, destInfo, versionPath, job.Name))
                        {
                            archivedCount++;
                        }
                    }
                    else
                    {
                        Log($"[{job.Name}]   → Skipped - new file (doesn't exist in source, won't be deleted)");
                        skippedNewFiles++;
                    }
                }

                Log($"[{job.Name}] Summary:");
                Log($"[{job.Name}]   - Archived: {archivedCount} file(s)");
                Log($"[{job.Name}]   - Skipped (same/newer): {skippedSameOrNewer} file(s)");
                Log($"[{job.Name}]   - Skipped (new files): {skippedNewFiles} file(s)");
                Log($"[{job.Name}]   - Archive location: {versionPath}");

                if (archivedCount > 0)
                {
                    CleanupOldVersions(versionPath, job.Name);
                }
            }
            catch (Exception ex)
            {
                Log($"[{job.Name}] Archiving error: {ex.Message}");
                Log($"[{job.Name}] Stack trace: {ex.StackTrace}");
            }
        }

        private bool ArchiveFile(string sourceFilePath, string relativePath, FileInfo fileInfo, string versionPath, string jobName)
        {
            try
            {
                // Archive the file
                var timestamp = fileInfo.LastWriteTime.ToString("yyyy-MM-dd_HH-mm-ss");
                var fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
                var extension = Path.GetExtension(sourceFilePath);
                var versionFileName = $"{fileName}_{timestamp}{extension}";

                var relativeDir = Path.GetDirectoryName(relativePath);
                var versionDir = string.IsNullOrEmpty(relativeDir)
                    ? versionPath
                    : Path.Combine(versionPath, relativeDir);

                Log($"[{jobName}]   Creating version directory: {versionDir}");
                Directory.CreateDirectory(versionDir);

                var versionFilePath = Path.Combine(versionDir, versionFileName);
                Log($"[{jobName}]   Copying to: {versionFilePath}");

                File.Copy(sourceFilePath, versionFilePath, true);

                // Verify the file was actually created
                if (File.Exists(versionFilePath))
                {
                    var archivedInfo = new FileInfo(versionFilePath);
                    Log($"[{jobName}]   ✓ VERIFIED: Archived file exists ({archivedInfo.Length} bytes)");
                    return true;
                }
                else
                {
                    Log($"[{jobName}]   ✗ ERROR: Archived file does NOT exist!");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"[{jobName}]   ✗ ERROR archiving file: {ex.Message}");
                return false;
            }
        }

        private void CleanupOldVersions(string versionPath, string jobName)
        {
            try
            {
                Log($"[{jobName}] Starting cleanup in: {versionPath}");

                var allVersionFiles = Directory.GetFiles(versionPath, "*.*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .ToList();

                Log($"[{jobName}] Found {allVersionFiles.Count} total version file(s)");

                int deletedCount = 0;

                // Delete files older than X days
                if (settings.DaysToKeepVersions > 0)
                {
                    var cutoffDate = DateTime.Now.AddDays(-settings.DaysToKeepVersions);
                    Log($"[{jobName}] Deleting files older than: {cutoffDate:yyyy-MM-dd HH:mm:ss} ({settings.DaysToKeepVersions} days ago)");
                    Log($"[{jobName}] Current time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                    var oldFiles = allVersionFiles.Where(f => f.LastWriteTime < cutoffDate).ToList();
                    Log($"[{jobName}] Found {oldFiles.Count} file(s) older than cutoff date");

                    foreach (var oldFile in oldFiles)
                    {
                        try
                        {
                            var age = (DateTime.Now - oldFile.LastWriteTime).TotalDays;
                            Log($"[{jobName}] Deleting: {oldFile.Name} (age: {age:F1} days, modified: {oldFile.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
                            oldFile.Delete();
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            Log($"[{jobName}] Failed to delete {oldFile.Name}: {ex.Message}");
                        }
                    }

                    if (deletedCount > 0)
                    {
                        Log($"[{jobName}] ✓ Cleaned up {deletedCount} version(s) older than {settings.DaysToKeepVersions} days");
                    }
                    else
                    {
                        Log($"[{jobName}] No files older than {settings.DaysToKeepVersions} days to delete");
                    }

                    // Refresh the list after deleting old files
                    allVersionFiles = Directory.GetFiles(versionPath, "*.*", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .ToList();
                }
                else
                {
                    Log($"[{jobName}] Days to keep is 0 - not deleting by age");
                }

                // Limit max versions per file
                if (settings.MaxVersionsPerFile > 0)
                {
                    Log($"[{jobName}] Limiting to max {settings.MaxVersionsPerFile} version(s) per file");

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
                        var groupList = group.OrderByDescending(f => f.LastWriteTime).ToList();
                        var versionsToDelete = groupList.Skip(settings.MaxVersionsPerFile).ToList();

                        if (versionsToDelete.Any())
                        {
                            Log($"[{jobName}] File '{Path.GetFileName(group.Key)}': {groupList.Count} versions, keeping newest {settings.MaxVersionsPerFile}, deleting {versionsToDelete.Count}");
                        }

                        foreach (var oldFile in versionsToDelete)
                        {
                            try
                            {
                                Log($"[{jobName}] Deleting excess version: {oldFile.Name}");
                                oldFile.Delete();
                                versionLimitDeleted++;
                            }
                            catch (Exception ex)
                            {
                                Log($"[{jobName}] Failed to delete {oldFile.Name}: {ex.Message}");
                            }
                        }
                    }

                    if (versionLimitDeleted > 0)
                    {
                        Log($"[{jobName}] ✓ Cleaned up {versionLimitDeleted} version(s) exceeding max {settings.MaxVersionsPerFile} per file");
                    }
                }
                else
                {
                    Log($"[{jobName}] Max versions per file not set (0) - keeping all versions");
                }

                // Clean up empty directories
                DeleteEmptyDirectories(versionPath);

                Log($"[{jobName}] Cleanup complete");
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
            var timestampedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

            // Write to UI (only if dispatcher is available)
            try
            {
                if (Dispatcher.CheckAccess())
                {
                    txtLog.AppendText(timestampedMessage + Environment.NewLine);
                    txtLog.ScrollToEnd();
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        txtLog.AppendText(timestampedMessage + Environment.NewLine);
                        txtLog.ScrollToEnd();
                    });
                }
            }
            catch
            {
                // Fail silently if UI is not ready
            }

            // Write to file
            try
            {
                logFileWriter?.WriteLine(timestampedMessage);
            }
            catch
            {
                // Fail silently if we can't write to log file
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(settings);
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() == true)
            {
                SaveConfigAutomatically();
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            txtLog.Clear();
            Log("Log cleared");
        }

        private void BtnAddJob_Click(object sender, RoutedEventArgs e)
        {
            AddNewJob();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);

            // Stop the scheduler timer
            if (schedulerTimer != null)
            {
                schedulerTimer.Dispose();
                schedulerTimer = null;
            }

            // Check if any jobs are running
            bool anyRunning = false;
            lock (runningProcesses)
            {
                anyRunning = runningProcesses.Any();
            }

            if (anyRunning)
            {
                var result = MessageBox.Show(
                    "Jobs are currently running. Do you want to stop them and exit?",
                    "Jobs Running",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    // Stop all running jobs
                    lock (runningProcesses)
                    {
                        foreach (var proc in runningProcesses.ToList())
                        {
                            try
                            {
                                if (!proc.HasExited)
                                {
                                    proc.Kill();
                                    Log("Process terminated due to application exit.");
                                }
                            }
                            catch { }
                        }
                        runningProcesses.Clear();
                    }

                    lock (jobProcesses)
                    {
                        jobProcesses.Clear();
                    }
                }
                else
                {
                    // Cancel the close
                    e.Cancel = true;
                    return;
                }
            }

            SaveConfigAutomatically();

            // Close log file
            Log("=== Application closing ===");
            try
            {
                logFileWriter?.Close();
                logFileWriter?.Dispose();
            }
            catch { }
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
        public string ExcludedDirectories { get; set; } = ""; // Comma-separated list of folders to exclude
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
        public string VersionFolder { get; set; } = "OldVersions"; // Always "OldVersions", not user-configurable
        public int DaysToKeepVersions { get; set; } = 30;
        public int MaxVersionsPerFile { get; set; } = 0;
    }

    public class Config
    {
        public List<RobocopyJob> Jobs { get; set; }
        public GlobalSettings Settings { get; set; }
    }
}