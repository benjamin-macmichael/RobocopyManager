# Multi-threaded Robocopy Manager

A powerful Windows WPF application that provides an interface for managing multiple Robocopy backup/sync jobs with advanced features like scheduling, versioning, and multi-threaded operations.

## Features

### 🚀 Core Functionality
- **Multiple Jobs**: Create and manage unlimited backup/sync jobs with individual configurations
- **Multi-threaded Transfers**: Leverage Robocopy's `/MT` flag with configurable thread counts (1-128)
- **Persistent Storage**: All jobs are automatically saved and restored between sessions
- **Individual Job Control**: Run jobs individually or all at once
- **Real-time Logging**: Monitor all operations in a live execution log

### ⏰ Scheduling
- **Daily Scheduling**: Set specific times for each job to run automatically
- **24-hour Format**: Configure jobs to run at any time of day
- **Automatic Execution**: Jobs run in the background according to their schedule
- **Smart Duplicate Prevention**: Jobs won't run multiple times within their scheduled window

### 📦 File Versioning
- **Automatic Archiving**: Old file versions are automatically saved before being overwritten
- **Dedicated Archive Folder**: All versions stored in `OldVersions` subfolder within destination
- **Retention Policies**: 
  - Configure how many days to keep old versions (or keep forever)
  - Limit maximum versions per file
  - Automatic cleanup of old versions
- **Protected Archives**: Archive folder is excluded from mirror operations

### ⚙️ Global Settings
- **Mirror Mode**: Full synchronization with source (deletes files not in source)
- **Retry Configuration**: Set retry attempts and wait times for failed operations
- **Subdirectory Options**: Control how subdirectories and empty folders are handled
- **Purge Mode**: Alternative to mirror mode for selective deletions

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
5. Adjust thread count (8-32 recommended for network transfers)
6. Optionally enable scheduling with a specific time

### Running Jobs
- **Run Single Job**: Click the **"Run Now"** button next to any job
- **Run All Jobs**: Click **"Run All Jobs"** to execute all enabled jobs
- **Stop Execution**: Click **"Stop All"** to terminate running jobs

### Configuring Settings
1. Click **"Settings"** button
2. Configure global Robocopy options:
   - Retry attempts and wait times
   - Mirror mode vs standard copy
   - Subdirectory handling
3. Configure versioning options:
   - Enable/disable file versioning
   - Set retention period (days)
   - Set maximum versions per file
4. Click **"OK"** to save

### Managing Old Versions
Old file versions are stored in `[Destination]\OldVersions\` with timestamps:
```
OldVersions/
  └── document_2026-01-15_14-30-45.txt
  └── report_2026-01-14_09-15-22.pdf
```

The system automatically:
- Archives files before they're overwritten
- Deletes versions older than your configured retention period
- Limits the number of versions kept per file (if configured)
- Maintains the original directory structure

## Configuration Files

Jobs and settings are automatically saved to:
```
%AppData%\RobocopyManager\config.json
```

This file is automatically created and updated. You can backup this file to preserve your job configurations.

## Robocopy Command Reference

The application generates Robocopy commands with the following flags:
- `/MIR` - Mirror mode (when enabled)
- `/S` or `/E` - Copy subdirectories
- `/MT:n` - Multi-threaded with n threads
- `/R:n` - Number of retries
- `/W:n` - Wait time between retries
- `/NP` - No progress (cleaner output)
- `/V` - Verbose output
- `/TS` - Include timestamps
- `/XD` - Exclude OldVersions directory
- `/PURGE` - Purge destination files (alternative to mirror)

## Tips & Best Practices

1. **Thread Count**: 
   - Local disk to local disk: 8-16 threads
   - Network transfers: 16-32 threads
   - Very large files: Lower thread count (4-8)

2. **Scheduling**: 
   - Schedule intensive jobs during off-hours
   - Stagger multiple jobs to avoid resource conflicts

3. **Versioning**:
   - Set retention periods based on your backup needs
   - Use "max versions per file" to control disk usage
   - Periodically check the OldVersions folder size

4. **Testing**:
   - Test new jobs with a small subset of data first
   - Verify paths are correct before enabling scheduling
   - Monitor the execution log for any errors

## Troubleshooting

### Jobs Not Running on Schedule
- Ensure the job's checkbox is enabled
- Verify "Run on schedule" checkbox is checked
- Check that the application is running at the scheduled time
- Review execution log for scheduler messages

### Archive Folder Being Deleted
- Ensure "Enable file versioning" is checked in Settings
- Verify Mirror Mode is enabled (Archive exclusion only works with Mirror Mode)

### Files Not Being Archived
- Check if source file is actually newer than destination
- Verify "Enable file versioning" is enabled
- Review execution log for archiving details
