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
        private Dictionary<int, TextBlock> jobStatusLabels = new Dictionary<int, TextBlock>();
        private Dictionary<int, System.Windows.Shapes.Ellipse> jobStatusIndicators = new Dictionary<int, System.Windows.Shapes.Ellipse>();

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
                var cutoffDate = DateTime.Now.AddMonths(-12);
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
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
                    };

                    var config = JsonSerializer.Deserialize<Config>(json, options);

                    jobs = config?.Jobs ?? new List<RobocopyJob>();
                    settings = config?.Settings ?? new GlobalSettings();

                    // Validate and fix any jobs with missing properties
                    foreach (var job in jobs)
                    {
                        if (job.Name == null) job.Name = "";
                        if (job.SourcePath == null) job.SourcePath = "";
                        if (job.DestinationPath == null) job.DestinationPath = "";
                        if (job.ExcludedDirectories == null) job.ExcludedDirectories = "";
                        if (job.Threads <= 0) job.Threads = 8;
                    }

                    // Validate settings
                    if (settings.VersionFolder == null || string.IsNullOrWhiteSpace(settings.VersionFolder))
                    {
                        settings.VersionFolder = "OldVersions";
                    }

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
                Log($"Error loading configuration: {ex.Message}");
                MessageBox.Show($"Error loading saved configuration: {ex.Message}\n\nStarting with empty configuration.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);

                // Start fresh with defaults
                jobs = new List<RobocopyJob>();
                settings = new GlobalSettings();
                jobIdCounter = 1;
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
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(20),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 38)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1, 1, 1, 1),
                CornerRadius = new CornerRadius(8),
                Tag = job
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header row
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Details row

            // Header section (always visible)
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };

            // Add status indicator dot
            var statusDot = new System.Windows.Shapes.Ellipse
            {
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Fill = System.Windows.Media.Brushes.Gray, // Default gray for no runs
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                StrokeThickness = 1
            };

            // Store reference for updates
            jobStatusIndicators[job.Id] = statusDot;

            // Set initial dot color based on last status
            UpdateStatusIndicator(job, statusDot);

            var chkEnabled = new CheckBox
            {
                IsChecked = job.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = System.Windows.Media.Brushes.White
            };
            chkEnabled.Checked += (s, e) => { job.Enabled = true; SaveConfigAutomatically(); };
            chkEnabled.Unchecked += (s, e) => { job.Enabled = false; SaveConfigAutomatically(); };

            var txtName = new TextBox
            {
                Text = job.Name,
                Width = 200,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1, 1, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13
            };
            txtName.TextChanged += (s, e) => { job.Name = txtName.Text; SaveConfigAutomatically(); };

            // Add last successful run date label
            var lblLastRun = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11,
                FontStyle = FontStyles.Italic
            };

            // Set initial last run text
            if (job.LastStatus == "Success" && job.LastFinishTime.HasValue)
            {
                lblLastRun.Text = $"Last success: {job.LastFinishTime.Value:MMM d, yyyy h:mm tt}";
            }
            else if (job.LastFinishTime.HasValue)
            {
                lblLastRun.Text = $"Last run: {job.LastFinishTime.Value:MMM d, yyyy h:mm tt}";
            }
            else
            {
                lblLastRun.Text = "Never run";
            }

            var btnRunNow = new Button
            {
                Content = "▶ Run",
                Width = 80,
                Height = 32,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212)),
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 10, 0),
                BorderThickness = new Thickness(0, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 13
            };
            btnRunNow.Click += (s, e) => RunSingleJob(job, btnRunNow);

            var btnDelete = new Button
            {
                Content = "🗑 Delete",
                Width = 90,
                Height = 32,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 17, 35)),
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 10, 0),
                BorderThickness = new Thickness(0, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 13
            };
            btnDelete.Click += (s, e) => DeleteJob(border, job);

            var btnToggle = new Button
            {
                Content = job.IsCollapsed ? "▶" : "▼",
                Width = 32,
                Height = 32,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            headerPanel.Children.Add(statusDot);
            headerPanel.Children.Add(chkEnabled);
            headerPanel.Children.Add(txtName);
            headerPanel.Children.Add(lblLastRun);
            headerPanel.Children.Add(btnRunNow);
            headerPanel.Children.Add(btnDelete);

            // Create a grid for header to position toggle button on right
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(headerPanel);
            Grid.SetColumn(headerPanel, 0);
            headerGrid.Children.Add(btnToggle);
            Grid.SetColumn(btnToggle, 1);
            Grid.SetRow(headerGrid, 0);

            // Details section (collapsible)
            var detailsGrid = new Grid();
            detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detailsGrid.Visibility = job.IsCollapsed ? Visibility.Collapsed : Visibility.Visible; // Restore saved state
            Grid.SetRow(detailsGrid, 1);

            var srcPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            srcPanel.Children.Add(new TextBlock { Text = "Source Path:", FontWeight = FontWeights.SemiBold, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)), FontSize = 13 });
            var srcStack = new StackPanel { Orientation = Orientation.Horizontal };
            var txtSource = new TextBox
            {
                Width = 700,
                Margin = new Thickness(0, 6, 8, 0),
                Text = job.SourcePath,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1, 1, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13
            };
            txtSource.TextChanged += (s, e) => { job.SourcePath = txtSource.Text; SaveConfigAutomatically(); };
            var btnBrowseSrc = new Button { Content = "Browse", Width = 80, Height = 32 };
            btnBrowseSrc.Click += (s, e) => BrowseFolder(txtSource);
            srcStack.Children.Add(txtSource);
            srcStack.Children.Add(btnBrowseSrc);
            srcPanel.Children.Add(srcStack);
            Grid.SetRow(srcPanel, 0);

            var dstPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            dstPanel.Children.Add(new TextBlock { Text = "Destination Path:", FontWeight = FontWeights.SemiBold, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)), FontSize = 13 });
            var dstStack = new StackPanel { Orientation = Orientation.Horizontal };
            var txtDest = new TextBox
            {
                Width = 700,
                Margin = new Thickness(0, 6, 8, 0),
                Text = job.DestinationPath,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1, 1, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13
            };
            txtDest.TextChanged += (s, e) => { job.DestinationPath = txtDest.Text; SaveConfigAutomatically(); };
            var btnBrowseDst = new Button { Content = "Browse", Width = 80, Height = 32 };
            btnBrowseDst.Click += (s, e) => BrowseFolder(txtDest);
            dstStack.Children.Add(txtDest);
            dstStack.Children.Add(btnBrowseDst);
            dstPanel.Children.Add(dstStack);
            Grid.SetRow(dstPanel, 1);

            var excludePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            excludePanel.Children.Add(new TextBlock { Text = "Exclude Directories (comma-separated):", FontWeight = FontWeights.SemiBold, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)), FontSize = 13 });
            var txtExclude = new TextBox
            {
                Width = 788,
                Margin = new Thickness(0, 6, 0, 0),
                Text = job.ExcludedDirectories,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1, 1, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13
            };
            txtExclude.TextChanged += (s, e) => { job.ExcludedDirectories = txtExclude.Text; SaveConfigAutomatically(); };
            excludePanel.Children.Add(txtExclude);
            Grid.SetRow(excludePanel, 2);

            var archivePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
            var chkArchive = new CheckBox
            {
                Content = "Enable archiving (save old versions)",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
                FontSize = 13
            };
            chkArchive.IsChecked = job.EnableArchiving;
            chkArchive.Checked += (s, e) => { job.EnableArchiving = true; SaveConfigAutomatically(); };
            chkArchive.Unchecked += (s, e) => { job.EnableArchiving = false; SaveConfigAutomatically(); };
            archivePanel.Children.Add(chkArchive);
            Grid.SetRow(archivePanel, 3);

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

            var statusPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 5) };
            var lblStatusHeader = new TextBlock
            {
                Text = "Last Run Status:",
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 5)
            };
            statusPanel.Children.Add(lblStatusHeader);

            var lblStatus = new TextBlock
            {
                FontSize = 12,
                Padding = new Thickness(10, 8, 10, 8),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)),
                TextWrapping = TextWrapping.Wrap
            };

            // Store reference to status label for updates
            jobStatusLabels[job.Id] = lblStatus;

            // Set initial status text
            UpdateStatusLabel(job, lblStatus);

            statusPanel.Children.Add(lblStatus);
            Grid.SetRow(statusPanel, 6);

            var cmdPanel = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            var txtCommand = new TextBox { IsReadOnly = true, Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.LightGreen, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Padding = new Thickness(5), TextWrapping = TextWrapping.Wrap };
            txtCommand.Text = GenerateRobocopyCommand(job);
            txtSource.TextChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            txtDest.TextChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            txtExclude.TextChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            sliderThreads.ValueChanged += (s, e) => txtCommand.Text = GenerateRobocopyCommand(job);
            cmdPanel.Children.Add(txtCommand);
            Grid.SetRow(cmdPanel, 7);

            detailsGrid.Children.Add(srcPanel);
            detailsGrid.Children.Add(dstPanel);
            detailsGrid.Children.Add(excludePanel);
            detailsGrid.Children.Add(archivePanel);
            detailsGrid.Children.Add(threadPanel);
            detailsGrid.Children.Add(schedulePanel);
            detailsGrid.Children.Add(statusPanel);
            detailsGrid.Children.Add(cmdPanel);

            // Toggle button click handler
            btnToggle.Click += (s, e) =>
            {
                if (detailsGrid.Visibility == Visibility.Visible)
                {
                    detailsGrid.Visibility = Visibility.Collapsed;
                    btnToggle.Content = "▶";
                    job.IsCollapsed = true;
                }
                else
                {
                    detailsGrid.Visibility = Visibility.Visible;
                    btnToggle.Content = "▼";
                    job.IsCollapsed = false;
                }
                SaveConfigAutomatically();
            };

            // Set initial toggle button state based on saved collapse state
            btnToggle.Content = job.IsCollapsed ? "▶" : "▼";

            mainGrid.Children.Add(headerGrid);
            mainGrid.Children.Add(detailsGrid);

            border.Child = mainGrid;
            jobsPanel.Children.Insert(jobsPanel.Children.Count - 1, border);
        }

        private async void RunSingleJob(RobocopyJob job, Button btnRunNow)
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
                MessageBox.Show($"Job '{job.Name}' is already running. Please wait for it to complete or close its CMD window.", "Job Already Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnRunNow.IsEnabled = false;
            btnRunNow.Content = "Running...";

            Log("========================================");
            Log($"Starting single job: {job.Name} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================");

            await Task.Run(() => ExecuteRobocopy(job));

            Log($"[{job.Name}] Robocopy cmd launched successfully at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================");

            btnRunNow.IsEnabled = true;
            btnRunNow.Content = "Run Now";
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

            // Add user-specified excluded directories (including OldVersions if they want)
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


        private void ExecuteRobocopy(RobocopyJob job)
        {
            try
            {
                // Set status to "Running" and update UI
                job.LastStartTime = DateTime.Now;
                job.LastStatus = "Running";
                job.LastFinishTime = null;
                job.LastExitCode = null;

                // Update UI on main thread
                Dispatcher.Invoke(() =>
                {
                    if (jobStatusLabels.TryGetValue(job.Id, out TextBlock statusLabel))
                    {
                        UpdateStatusLabel(job, statusLabel);
                    }

                    if (jobStatusIndicators.TryGetValue(job.Id, out System.Windows.Shapes.Ellipse statusDot))
                    {
                        UpdateStatusIndicator(job, statusDot);
                    }
                });

                SaveConfigAutomatically();

                // Archive old versions if archiving is enabled for this job AND global versioning is enabled
                if (job.EnableArchiving && settings.EnableVersioning)
                {
                    ArchiveOldVersions(job);
                }

                var command = GenerateRobocopyCommand(job);
                Log($"[{job.Name}] Starting at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Log($"[{job.Name}] Command: {command}");

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c title {job.Name} - Robocopy && {command}",
                        UseShellExecute = true,
                        CreateNoWindow = false,
                        WindowStyle = ProcessWindowStyle.Normal
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

                process.Start();

                // Monitor the process in a background thread
                Task.Run(() =>
                {
                    process.WaitForExit();

                    lock (runningProcesses)
                    {
                        runningProcesses.Remove(process);
                    }

                    lock (jobProcesses)
                    {
                        jobProcesses.Remove(job.Id);
                    }

                    // Update job status based on exit code
                    job.LastFinishTime = DateTime.Now;
                    job.LastExitCode = process.ExitCode;

                    // Robocopy exit codes: 0-7 are success/warnings, 8+ are errors
                    if (process.ExitCode < 8)
                    {
                        job.LastStatus = "Success";
                    }
                    else
                    {
                        job.LastStatus = "Failed";
                    }

                    Log($"[{job.Name}] Completed at {DateTime.Now:yyyy-MM-dd HH:mm:ss} with exit code: {process.ExitCode}");

                    // Update UI on main thread
                    Dispatcher.Invoke(() =>
                    {
                        if (jobStatusLabels.TryGetValue(job.Id, out TextBlock statusLabel))
                        {
                            UpdateStatusLabel(job, statusLabel);
                        }

                        if (jobStatusIndicators.TryGetValue(job.Id, out System.Windows.Shapes.Ellipse statusDot))
                        {
                            UpdateStatusIndicator(job, statusDot);
                        }
                    });

                    SaveConfigAutomatically();
                });
            }
            catch (Exception ex)
            {
                Log($"[{job.Name}] ERROR: {ex.Message}");

                job.LastFinishTime = DateTime.Now;
                job.LastStatus = "Failed";
                job.LastExitCode = -1;

                Dispatcher.Invoke(() =>
                {
                    if (jobStatusLabels.TryGetValue(job.Id, out TextBlock statusLabel))
                    {
                        UpdateStatusLabel(job, statusLabel);
                    }

                    if (jobStatusIndicators.TryGetValue(job.Id, out System.Windows.Shapes.Ellipse statusDot))
                    {
                        UpdateStatusIndicator(job, statusDot);
                    }
                });

                SaveConfigAutomatically();
            }
        }

        private void ArchiveOldVersions(RobocopyJob job)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(job.SourcePath) || string.IsNullOrWhiteSpace(job.DestinationPath))
                {
                    return;
                }

                if (!Directory.Exists(job.SourcePath))
                {
                    Log($"[{job.Name}] Archiving skipped - source path does not exist");
                    return;
                }

                if (!Directory.Exists(job.DestinationPath))
                {
                    Log($"[{job.Name}] Archiving skipped - destination path does not exist");
                    return;
                }

                Log($"[{job.Name}] Checking for files to archive...");

                var versionPath = Path.Combine(job.DestinationPath, settings.VersionFolder);

                if (!Directory.Exists(versionPath))
                {
                    Directory.CreateDirectory(versionPath);
                }

                // System folders to skip during archiving
                var systemFolders = new[] {
            "$RECYCLE.BIN",
            "System Volume Information",
            "$Recycle.Bin",
            "Recycler",
            settings.VersionFolder
        };

                // Get all files in source and destination (excluding system folders)
                var sourceFilesDict = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

                if (Directory.Exists(job.SourcePath))
                {
                    var sourceFilesList = GetFilesExcludingSystemFolders(job.SourcePath, systemFolders);
                    foreach (var sourceFile in sourceFilesList)
                    {
                        var relativePath = sourceFile.Substring(job.SourcePath.Length).TrimStart('\\');
                        sourceFilesDict[relativePath] = new FileInfo(sourceFile);
                    }
                }

                var destFiles = GetFilesExcludingSystemFolders(job.DestinationPath, systemFolders)
                    .Where(f => !f.StartsWith(versionPath, StringComparison.OrdinalIgnoreCase));

                int archivedCount = 0;

                foreach (var destFilePath in destFiles)
                {
                    var relativePath = destFilePath.Substring(job.DestinationPath.Length).TrimStart('\\');
                    var destInfo = new FileInfo(destFilePath);

                    // Skip system/hidden files that shouldn't be archived
                    var fileName = destInfo.Name;
                    if (fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Case 1: File exists in BOTH source and destination
                    if (sourceFilesDict.TryGetValue(relativePath, out FileInfo sourceInfo))
                    {
                        // Use robocopy's logic: file differs if size is different OR timestamp differs by more than 2 seconds
                        bool sizeDiffers = sourceInfo.Length != destInfo.Length;
                        bool timestampDiffers = Math.Abs((sourceInfo.LastWriteTime - destInfo.LastWriteTime).TotalSeconds) > 2;

                        // Only archive if the file will actually be overwritten (different size or timestamp)
                        if (sizeDiffers || timestampDiffers)
                        {
                            Log($"[{job.Name}] File will be overwritten: {relativePath} (Size: {sourceInfo.Length} vs {destInfo.Length}, Time diff: {Math.Abs((sourceInfo.LastWriteTime - destInfo.LastWriteTime).TotalSeconds):F1}s)");

                            if (ArchiveFile(destFilePath, relativePath, destInfo, versionPath, job.Name))
                            {
                                archivedCount++;
                            }
                        }
                    }
                    // Case 2: File exists ONLY in destination (will be DELETED by mirror mode or purge)
                    else if (settings.MirrorMode || settings.PurgeDestination)
                    {
                        Log($"[{job.Name}] File will be deleted: {relativePath}");

                        if (ArchiveFile(destFilePath, relativePath, destInfo, versionPath, job.Name))
                        {
                            archivedCount++;
                        }
                    }
                }

                if (archivedCount > 0)
                {
                    Log($"[{job.Name}] Archived {archivedCount} file(s) to {settings.VersionFolder}");
                    CleanupOldVersions(versionPath, job.Name);

                    // Clean up empty directories after archiving
                    DeleteEmptyDirectories(versionPath);
                    Log($"[{job.Name}] Cleaned up empty directories in archive");
                }
                else
                {
                    Log($"[{job.Name}] No files needed archiving");

                    // Still clean up empty directories even if nothing was archived this time
                    DeleteEmptyDirectories(versionPath);
                }
            }
            catch (Exception ex)
            {
                Log($"[{job.Name}] Archiving error: {ex.Message}");
            }
        }

        private IEnumerable<string> GetFilesExcludingSystemFolders(string path, string[] systemFolders)
        {
            var files = new List<string>();

            try
            {
                // Get files in current directory
                try
                {
                    files.AddRange(Directory.GetFiles(path));
                }
                catch
                {
                    // Skip if can't access this directory
                }

                // Get subdirectories and recurse (excluding system folders)
                try
                {
                    var directories = Directory.GetDirectories(path);
                    foreach (var dir in directories)
                    {
                        var dirName = Path.GetFileName(dir);

                        // Skip system folders
                        if (systemFolders.Any(sf => sf.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        // Recursively get files from subdirectory
                        files.AddRange(GetFilesExcludingSystemFolders(dir, systemFolders));
                    }
                }
                catch
                {
                    // Skip if can't access subdirectories
                }
            }
            catch
            {
                // Skip this entire path if we can't access it
            }

            return files;
        }

        private bool ArchiveFile(string sourceFilePath, string relativePath, FileInfo fileInfo, string versionPath, string jobName)
        {
            try
            {
                var timestamp = fileInfo.LastWriteTime.ToString("yyyy-MM-dd_HH-mm-ss");
                var fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
                var extension = Path.GetExtension(sourceFilePath);
                var versionFileName = $"{fileName}_{timestamp}{extension}";

                var relativeDir = Path.GetDirectoryName(relativePath);
                var versionDir = string.IsNullOrEmpty(relativeDir)
                    ? versionPath
                    : Path.Combine(versionPath, relativeDir);

                Directory.CreateDirectory(versionDir);

                var versionFilePath = Path.Combine(versionDir, versionFileName);

                File.Copy(sourceFilePath, versionFilePath, true);

                if (File.Exists(versionFilePath))
                {
                    Log($"[{jobName}] Archived: {relativePath}");
                    return true;
                }
                else
                {
                    Log($"[{jobName}] ERROR: Failed to archive {relativePath}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"[{jobName}] ERROR archiving {relativePath}: {ex.Message}");
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

        private void Log(string message)
        {
            var timestampedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

            // Write to file (no UI, no date checking - that happens in scheduler)
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

        private void BtnAddJob_Click(object sender, RoutedEventArgs e)
        {
            AddNewJob();
        }
        private void UpdateStatusLabel(RobocopyJob job, TextBlock lblStatus)
        {
            if (job.LastStatus == "Running")
            {
                lblStatus.Text = $"🔄 Running... (Started: {job.LastStartTime:yyyy-MM-dd HH:mm:ss})";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 181, 246)); // Blue
            }
            else if (job.LastStatus == "Success")
            {
                var duration = job.LastFinishTime.HasValue && job.LastStartTime.HasValue
                    ? (job.LastFinishTime.Value - job.LastStartTime.Value).TotalMinutes
                    : 0;
                lblStatus.Text = $"✓ Success (Start: {job.LastStartTime:HH:mm:ss} | Finish: {job.LastFinishTime:HH:mm:ss} | Duration: {duration:F1} min | Exit Code: {job.LastExitCode})";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(129, 199, 132)); // Green
            }
            else if (job.LastStatus == "Failed")
            {
                var duration = job.LastFinishTime.HasValue && job.LastStartTime.HasValue
                    ? (job.LastFinishTime.Value - job.LastStartTime.Value).TotalMinutes
                    : 0;
                lblStatus.Text = $"✗ Failed (Start: {job.LastStartTime:HH:mm:ss} | Finish: {job.LastFinishTime:HH:mm:ss} | Duration: {duration:F1} min | Exit Code: {job.LastExitCode})";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 83, 80)); // Red
            }
            else
            {
                lblStatus.Text = "No previous runs";
                lblStatus.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }

        private void UpdateStatusIndicator(RobocopyJob job, System.Windows.Shapes.Ellipse statusDot)
        {
            if (job.LastStatus == "Running")
            {
                statusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 181, 246)); // Blue
            }
            else if (job.LastStatus == "Success")
            {
                statusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
            }
            else if (job.LastStatus == "Failed")
            {
                statusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)); // Red
            }
            else
            {
                statusDot.Fill = System.Windows.Media.Brushes.Gray; // Gray for never run
            }
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
        public bool EnableArchiving { get; set; } = true; // Whether to archive old versions before running
        public bool IsCollapsed { get; set; } = false; // Whether the job UI is collapsed

        // Status tracking for UI display
        public DateTime? LastStartTime { get; set; }
        public DateTime? LastFinishTime { get; set; }
        public string LastStatus { get; set; } = ""; // "Success", "Failed", "Running", or ""
        public int? LastExitCode { get; set; }
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