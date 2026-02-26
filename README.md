# Multi-threaded Robocopy Manager

A powerful Windows WPF application that provides an interface for managing multiple Robocopy backup/sync jobs with advanced features like scheduling, versioning, multi-threaded operations, and real-time status tracking.

## Features

### 🚀 Core Functionality

- **Multiple Jobs**: Create and manage unlimited backup/sync jobs with individual configurations
- **Multi-threaded Transfers**: Leverage Robocopy's `/MT` flag with configurable thread counts (1-128)
- **Persistent Storage**: All jobs are automatically saved and restored between sessions
- **Individual Job Control**: Run jobs individually or all at once
- **Real-time Status Indicators**: Visual status dots (green/red/blue) show job state at a glance
- **Detailed Status Tracking**: View start/finish times, duration, and exit codes for each job
- **Collapsible UI**: Collapse job details to keep the interface clean and organized
- **Comprehensive Logging**: All operations logged to daily log files with 12-month retention

### ⏰ Scheduling

- **Daily Scheduling**: Set specific times for each job to run automatically
- **24-hour Format**: Configure jobs to run at any time of day
- **Background Execution**: Jobs run reliably even when RDP is disconnected or screen is locked
- **Automatic Execution**: Jobs run in the background according to their schedule
- **Smart Duplicate Prevention**: Jobs won't run multiple times within their scheduled window
- **Last Run Tracking**: See when each job last ran successfully

### 📦 File Versioning & Archiving

- **Intelligent Archiving**: Only archives files that will actually be changed or deleted
- **Smart File Comparison**: Uses both file size and timestamp to detect real changes (not just time differences)
- **Automatic Archiving**: Old file versions are automatically saved before being overwritten or deleted
- **Dedicated Archive Folder**: All versions stored in `OldVersions` subfolder within destination
- **System File Exclusion**: Automatically skips .DS_Store, Thumbs.db, and desktop.ini files
- **Retention Policies**:
  - Configure how many days to keep old versions (default: 30 days)
  - Limit maximum versions per file (optional)
  - Automatic cleanup of old versions and empty directories
- **Protected Archives**: Archive folder is excluded from mirror operations
- **Directory Structure Preservation**: Archived files maintain their original folder hierarchy
- **Timestamped Versions**: Each version includes the original file's last modified date in the filename

### 📊 Status & Monitoring

- **Visual Status Indicators**: Color-coded dots show job status at a glance
  - 🟢 Green: Last run successful
  - 🔴 Red: Last run failed
  - 🔵 Blue: Currently running
  - ⚫ Gray: Never run
- **Detailed Status Panel**: Each job displays:
  - Last run date and time
  - Start and finish times
  - Duration of last run
  - Robocopy exit code
  - Success/failure status
- **Live Updates**: Status updates in real-time as jobs execute
- **Persistent Status**: Job status saved and restored across application restarts

### ⚙️ Global Settings

- **Mirror Mode**: Full synchronization with source (deletes files not in source)
- **Retry Configuration**: Set retry attempts and wait times for failed operations
- **Subdirectory Options**: Control how subdirectories and empty folders are handled
- **Purge Mode**: Alternative to mirror mode for selective deletions
- **Directory Exclusions**: Automatically excludes system folders like $RECYCLE.BIN and System Volume Information

## Installation

### Prerequisites

- Windows 10/11
- .NET 6.0 or later
- Robocopy (included with Windows)

### Building from Source

1. Clone the repository:

```bash
git clone https://github.com/benjamin-macmichael/RobocopyManager.git
```

2. Open `RobocopyManager.sln` in Visual Studio 2022 or later

3. Build the solution (Ctrl+Shift+B)

4. Run the application (F5)

## Usage

### Creating a Job

1. Click **"Add New Job"** button
2. Enter a descriptive name for the job
3. Browse and select source folder
4. Browse and select destination folder
5. Configure excluded directories (comma-separated, optional)
6. Enable/disable archiving for this specific job
7. Adjust thread count (8-32 recommended for network transfers)
8. Optionally enable scheduling with a specific time

### Running Jobs

- **Run Single Job**: Click the **"▶ Run"** button next to any job
- **Run All Jobs**: Click **"Run All Jobs"** to execute all enabled jobs
- **Stop Execution**: Click **"Stop All"** to terminate running jobs
- **Job Prevention**: Jobs already running cannot be started again until completion

### Managing Job UI

- **Collapse/Expand**: Click the **▶/▼** button to collapse or expand job details
- **Status at a Glance**: View the colored status dot and last run date in the header
- **Delete Jobs**: Click the **🗑 Delete** button to permanently remove a job

### Configuring Settings

1. Click **"Settings"** button
2. Configure global Robocopy options:
   - Mirror mode vs standard copy
   - Retry attempts and wait times
   - Subdirectory and empty directory handling
   - Purge destination option
3. Configure versioning options:
   - Enable/disable file versioning globally
   - Set retention period in days (0 = keep forever)
   - Set maximum versions per file (0 = unlimited)
4. Click **"OK"** to save

### Understanding Status Indicators

**Status Dots:**

- 🟢 **Green**: Last run completed successfully
- 🔴 **Red**: Last run failed or encountered errors
- 🔵 **Blue**: Job is currently running
- ⚫ **Gray**: Job has never been run

**Status Panel Information:**

- Shows complete run history with timing details
- Exit codes indicate what Robocopy did:
  - 0 = No changes, no errors
  - 1 = Files copied successfully
  - 2 = Extra files/directories detected
  - 3 = Files copied and extra files detected
  - 8+ = Errors occurred

### Managing Old Versions

Old file versions are stored in `[Destination]\OldVersions\` with timestamps:

```
OldVersions/
  └── SubFolder/
      └── document_2026-01-15_14-30-45.txt
      └── report_2026-01-14_09-15-22.pdf
```

The system automatically:

- Archives files only when they will actually be changed or deleted
- Compares both file size and timestamp (±2 seconds tolerance) to detect real changes
- Skips system files (.DS_Store, Thumbs.db, desktop.ini)
- Deletes versions older than your configured retention period
- Limits the number of versions kept per file (if configured)
- Maintains the original directory structure
- Cleans up empty directories after version deletion

## Configuration Files

### Jobs and Settings

Automatically saved to:

```
%AppData%\RobocopyManager\config.json
```

This file contains:

- All job configurations
- Global settings
- Job status history
- Last run timestamps

### Log Files

Daily logs saved to:

```
%AppData%\RobocopyManager\Logs\RobocopyManager_YYYY-MM-DD.log
```

Features:

- One log file per day
- Automatic 12-month retention
- Detailed operation tracking
- Timestamps for all events
- Archiving activity details

You can backup the config file to preserve your job configurations.

## Robocopy Command Reference

The application generates Robocopy commands with the following flags:

- `/MIR` - Mirror mode (when enabled in settings)
- `/S` or `/E` - Copy subdirectories (with or without empty dirs)
- `/MT:n` - Multi-threaded with n threads (configurable per job)
- `/R:n` - Number of retries (configured in settings)
- `/W:n` - Wait time between retries in seconds (configured in settings)
- `/NP` - No progress percentage (cleaner output)
- `/A-:SH` - Strip System and Hidden attributes
- `/XD` - Exclude directories (system folders + user-specified + OldVersions)
- `/PURGE` - Purge destination files (when enabled, alternative to mirror)

### Automatically Excluded Directories

- `$RECYCLE.BIN`
- `System Volume Information`
- `$Recycle.Bin`
- `Recycler`
- User-specified exclusions (comma-separated in job config)

## Tips & Best Practices

1. **Thread Count**:

   - Local disk to local disk: 8-16 threads
   - Network transfers: 16-32 threads
   - Very large files: Lower thread count (4-8)
   - Maximum: 128 threads (generally not recommended)

2. **Scheduling**:

   - Schedule intensive jobs during off-hours
   - Stagger multiple jobs to avoid resource conflicts
   - Application must be running for scheduled jobs to execute
   - Scheduler works even when RDP is disconnected

3. **Versioning & Archiving**:

   - Enable archiving per job for granular control
   - Set retention periods based on your backup needs (30 days default)
   - Use "max versions per file" to control disk usage
   - Periodically check the OldVersions folder size
   - Archive folder is automatically excluded from mirroring
   - Only changed files are archived, reducing storage usage

4. **Status Monitoring**:

   - Check status dots for quick health overview
   - Review status panel for detailed run information
   - Monitor log files for troubleshooting
   - Success = exit code 0-7, Failed = exit code 8+

5. **Testing**:

   - Test new jobs with a small subset of data first
   - Verify paths are correct before enabling scheduling
   - Run manually once before enabling scheduling
   - Monitor the execution log for any errors or warnings

6. **Performance**:
   - Collapse unused job details to improve UI responsiveness
   - Higher thread counts don't always mean faster transfers
   - Network bandwidth is often the bottleneck, not thread count

## Troubleshooting

### Jobs Not Running on Schedule

- Ensure the job's main checkbox is enabled (checked)
- Verify "Run on schedule" checkbox is checked for the job
- Check that the application is running at the scheduled time
- Review log files in `%AppData%\RobocopyManager\Logs\` for scheduler messages
- Scheduler runs every minute checking for jobs to execute

### Files Being Archived When They Shouldn't Be

- Check if files actually differ by size or timestamp (>2 seconds)
- Clock skew between source and destination can cause false positives
- Network shares may have timestamp precision differences
- Review log files to see why files were archived (size/timestamp details logged)

### Archive Folder Being Deleted or Not Created

- Ensure "Enable file versioning" is checked in global Settings
- Verify "Enable archiving" is checked for the specific job
- Both must be enabled for archiving to work
- Archive folder (OldVersions) is automatically excluded from operations

### Files Not Being Archived

- Check if source file is actually newer/different than destination
- Verify both global and per-job archiving are enabled
- Review log files for archiving activity
- System files (.DS_Store, etc.) are intentionally skipped

### Status Not Updating

- Status updates when job completes, not during execution
- Check if CMD window is still open (job still running)
- Verify job completed (check log files)
- Status persists across application restarts

### Empty Folders in OldVersions

- Empty folders are cleaned up automatically after version deletion
- If they persist, they may contain hidden system files
- Manually delete if necessary

### High Disk Usage in OldVersions

- Reduce retention period (days to keep versions)
- Set maximum versions per file limit
- Manually clean up OldVersions folder if needed
- Consider disabling archiving for jobs with large/frequent changes

## Advanced Features

### Background Scheduler

The application uses a System.Threading.Timer that:

- Runs independently of the UI
- Works when RDP is disconnected
- Survives screen lock/unlock
- Checks every minute for scheduled jobs
- Prevents duplicate runs of the same job

### Intelligent Archiving

Archiving logic uses multiple checks:

1. Compares file sizes (any difference triggers archive)
2. Compares timestamps with 2-second tolerance
3. Detects files that will be deleted in mirror/purge mode
4. Skips system files automatically
5. Maintains directory structure in archive
6. Cleans up empty directories after deletion

### Thread-Safe Operations

- Dictionary locks prevent race conditions
- Job process tracking ensures single execution
- UI updates marshaled to main thread
- Safe for concurrent job execution

## License

[Your License Here]

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Support

For issues, questions, or suggestions, please open an issue on GitHub.
