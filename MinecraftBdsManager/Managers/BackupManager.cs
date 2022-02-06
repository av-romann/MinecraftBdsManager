﻿using MinecraftBdsManager.Configuration;
using MinecraftBdsManager.Logging;

namespace MinecraftBdsManager.Managers
{
    internal class BackupManager
    {
        private const string DAILY_BACKUP_DIRECTORY_NAME_PREFIX = "Daily_";
        private static System.Timers.Timer _backupTimer = new() { Enabled = false };

        /// <summary>
        /// Event handler for when the backup timer triggers, indicating its time for a backup
        /// </summary>
        /// <param name="sender">Caller of the event handler.</param>
        /// <param name="e">Parameters including the details of the timer when it fired.</param>
        private async static void BackupTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // Check settings to see if the user wanted to do a backup only if players had been online
            if (Settings.CurrentSettings.BackupSettings.OnlyBackupIfUsersWereOnline)
            {
                // User entered backup interval
                var backupTimespan = TimeSpan.FromMinutes(Settings.CurrentSettings.BackupSettings.AutomaticBackupIntervalInMinutes);

                var usersHaveBeenActiveOnTheServer = BdsManager.HaveUsersBeenOnInTheLastAmountOfTime(backupTimespan);

                // If no one is currently active or been on then just return
                if (!usersHaveBeenActiveOnTheServer)
                {
                    LogManager.LogInformation($"Skipping automatic backup since there are no users have been active for over {backupTimespan.TotalMinutes} minutes.");
                    return;
                }
            }

            LogManager.LogInformation("Automatic backup is starting.");

            var backupWasSuccessful = await CreateBackupAsync();
            if (backupWasSuccessful)
            {
                LogManager.LogInformation("Automatic backup completed successfully.");
            }
            else
            {
                LogManager.LogError("Automatic backup failed.");
            }
        }

        /// <summary>
        /// Creates the backup folder with the name of the world and the timestamp when the backup took place.  Should only be called once per backup to avoid getting different targets for the same backup.
        /// </summary>
        /// <returns>The path to the newly created backup folder</returns>
        private static string BuildBackupDirectoryPath()
        {
            // Get the backup root path that was specified by the MinecraftBdsManager settings
            var backupDirectoryPathRoot = Settings.CurrentSettings.BackupSettings.BackupDirectoryPath;

            // Format the log filename to be "<worldName>_2009-06-15T134530Z" using UTC time and...
            var formattedCurrentUtcDateTime = $"{DateTime.UtcNow:O}";
            
            // ... taking out the colons (:) in order to not make Windows file system upset and...
            formattedCurrentUtcDateTime = formattedCurrentUtcDateTime.Replace(":", string.Empty);

            // ... remove the milliseconds/fractional seconds as they are not super useful and the plop the Z back on the end to keep the UTC signifier and...
            formattedCurrentUtcDateTime = string.Concat(formattedCurrentUtcDateTime.Substring(0, formattedCurrentUtcDateTime.IndexOf(".")), "Z");

            // Create the directory name
            var backupDirectoryName = $"{BdsManager.LevelName}_{formattedCurrentUtcDateTime}";

            // Pull out just the date part to allow us to easily check if we have a Daily backup folder yet today
            var shortDateFormCurrentUtcDateTime = formattedCurrentUtcDateTime[0..formattedCurrentUtcDateTime.IndexOf("T")];

            // Check to see if we have existing directories from "today"
            if (!Directory.GetDirectories(backupDirectoryPathRoot, $"*{shortDateFormCurrentUtcDateTime}*").Any())
            {
                // Append the daily prefix
                backupDirectoryName = string.Concat(DAILY_BACKUP_DIRECTORY_NAME_PREFIX, backupDirectoryName);
            }

            // ... finally put it all together with the level name
            var backupDirectoryPath = Path.GetFullPath(Path.Combine(backupDirectoryPathRoot, backupDirectoryName));


            if (!Directory.Exists(backupDirectoryPath))
            {
                Directory.CreateDirectory(backupDirectoryPath);

                // The backup directory will need a db folder as well.
                Directory.CreateDirectory(Path.Combine(backupDirectoryPath, "db"));
            }

            return backupDirectoryPath;
        }

        /// <summary>
        /// Copies everything in the source directory, including subfolders, to the target directory
        /// </summary>
        /// <param name="sourceDirectoryPath">Source location for files to be copied.</param>
        /// <param name="targetDirectoryPath">Target directory where files should be copied to.</param>
        /// <returns>A flag indicating if the copy was successful (true) or not (false).</returns>
        internal static bool CopyDirectoryContents(string sourceDirectoryPath, string targetDirectoryPath)
        {
            // If the source directory was not passed in properly and/or the directory is missing then log and exit.
            if (string.IsNullOrWhiteSpace(sourceDirectoryPath) || !Directory.Exists(sourceDirectoryPath))
            {
                LogManager.LogWarning($"Unable to copy files as source path {sourceDirectoryPath} cannot be found.");
                return false;
            }

            // If the target directory was not passed in properly then log and exit.
            if (string.IsNullOrWhiteSpace(targetDirectoryPath))
            {
                LogManager.LogWarning($"Unable to copy files as target path {targetDirectoryPath} cannot be found.");
                return false;
            }

            // Ensure paths are expanded
            targetDirectoryPath = Path.GetFullPath(targetDirectoryPath);

            // Ensure the target directory exists
            if (!Directory.Exists(targetDirectoryPath))
            {
                Directory.CreateDirectory(targetDirectoryPath);
            }

            // Check if the target directory has any files in it already.  If so, abort so we don't corrupt existing files
            if (Directory.GetFiles(targetDirectoryPath, "*.*", SearchOption.AllDirectories).Length > 0)
            {
                LogManager.LogWarning($"Target directory {targetDirectoryPath} is not empty. Aborting backup.");
                return false;
            }

            // Find all of the source files recursively
            var sourceFilePaths = Directory.GetFiles(sourceDirectoryPath, "*.*", SearchOption.AllDirectories);

            // Copy the files to the target directory
            foreach (var sourceFilePath in sourceFilePaths)
            {
                var sourceFileName = Path.GetFileName(sourceFilePath);
                var targetFileName = GetBackupTargetFileName(sourceFilePath);
                var targetFilePath = Path.GetFullPath(Path.Combine(targetDirectoryPath, targetFileName));

                try
                {
                    File.Copy(sourceFilePath, targetFilePath, overwrite: false);
                }
                catch (Exception ex)
                {
                    LogManager.LogError($"Files were unable to be copied due to {ex.Message}.");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Copies only the files that were part of the specificed backup set.
        /// </summary>
        /// <param name="backupDirectoryPath">The directory path where the backup files should be copied to.</param>
        /// <param name="backupFiles">The set of BackupFile, supplied by BdsManager, that BDS said were the files that needed to be backed up.</param>
        /// <returns>Flag indicating if the offline backup was successful or not.  True means the backup was successful.  False means it failed.</returns>
        private static bool CopyFilesInBackupSet(string backupDirectoryPath, List<BackupFile> backupFiles)
        {
            if (string.IsNullOrWhiteSpace(backupDirectoryPath))
            {
                LogManager.LogWarning($"Unable to copy files as target path {backupDirectoryPath} cannot be found.");
                return false;
            }

            if (!backupFiles.Any())
            {
                LogManager.LogWarning($"No files were specified to be backed up.  Unable to perform backup.");
                return false;
            }

            foreach (var backupFile in backupFiles)
            {
                try
                {
                    // Get the name of the file to backup
                    var backupFileName = GetBackupTargetFileName(backupFile.Path);

                    var targetFilePath = Path.GetFullPath(Path.Combine(backupDirectoryPath, backupFileName));
                    File.Copy(backupFile.Path, targetFilePath, overwrite: false);
                }
                catch (Exception ex)
                {
                    LogManager.LogError($"Files were unable to be copied due to {ex.Message}.");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Creates a backup of the Minecraft world.  Backups can be taken either as Offline (the server is stopped) or Online (the server is running)
        /// </summary>
        /// <returns>Handle to the async promise and a flag indicating if the backup was successful or not.  True means the backup was successful.  False means it failed.</returns>
        internal async static Task<bool> CreateBackupAsync()
        {
            bool backupWasSuccessful;

            try
            {
                // In order to create backups for BDS we need to take some things into consideration if the server is online vs when it is not.
                //
                if (!BdsManager.ServerIsRunning)
                {
                    backupWasSuccessful = CreateOfflineBackup();
                }
                else
                {
                    backupWasSuccessful = await CreateOnlineBackupAsync();
                }

                PerformBackupDirectoryMaintenance();
            }
            catch (Exception ex)
            {
                // Safety catch just in case
                LogManager.LogError($"An error occurred during backup and backup maintenance.  Details are {ex.Message}");

                backupWasSuccessful = false;
            }

            return backupWasSuccessful;
        }

        /// <summary>
        /// Creates a backup when the server is not running.  This is a straight forward file copy as we know the world state is not changing.
        /// </summary>
        /// <returns>Flag indicating if the offline backup was successful or not.  True means the backup was successful.  False means it failed.</returns>
        private static bool CreateOfflineBackup()
        {
            //  1. If the server is offline/stopped we can simply copy out the world files as they are static since the world is not actively being hosted.
            var sourceDirectory = Path.Combine(Settings.CurrentSettings.BedrockDedicateServerDirectoryPath, BdsManager.WorldDirectoryPath!);
            return CopyDirectoryContents(sourceDirectory, BuildBackupDirectoryPath());
        }

        /// <summary>
        /// Creates a backup when the server is running.  This backup is a more involved process as it requires working with BDS to know the correct files and sizes of those files that need to be saved.
        /// </summary>
        /// <returns>Handle to the async promise and a flag indicating if the backup was successful or not.  True means the backup was successful.  False means it failed.</returns>
        private async static Task<bool> CreateOnlineBackupAsync()
        {
            bool backupWasSuccessful = false;

            //  2. If the server is online then a specific process needs to be followed in order to ensure we have uncorrupted backups.
            try
            {
                //      a. First step is to issue a "save hold" command to BDS which will prepare the server for backup.  This call returns immediately and requires follow up polling.
                await BdsManager.SaveHoldAsync();

                // Give BDS a moment to actually respond to the command before spamming the next one
                await Task.Delay(TimeSpan.FromSeconds(2));

                //      b. Second step is to poll for backup completion using "save query".  When success is returned it will include a list of files that comprise the backup and the proper lengths of those files.
                // Poll until the backup files are ready to copy
                while (!BdsManager.BackupFilesAreReadyToCopy && BdsManager.ServerIsRunning)
                {
                    await BdsManager.SaveQueryAsync();
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                // If the server has stopped while waiting for some reason, abort the backup as we will not be getting any more information from the server.
                if (!BdsManager.ServerIsRunning)
                {
                    LogManager.LogWarning("Terminating Online backup due to server shutdown.");
                    return false;
                }

                //      c. Third step is to file copy the whole files from the world directory to the backup instance directory
                var sourceDirectory = Path.Combine(Settings.CurrentSettings.BedrockDedicateServerDirectoryPath, BdsManager.WorldDirectoryPath!);
                var backupDirectoryPath = BuildBackupDirectoryPath();

                List<BackupFile> backupFileSet;

                // Establish a lock to ensure that the collection cannot be modified while we are enumerating it.
                lock (BdsManager.BackupFilesLock)
                {
                    backupFileSet = BdsManager.BackupFiles;
                    backupWasSuccessful = CopyFilesInBackupSet(backupDirectoryPath, backupFileSet);
                }

                if (!backupWasSuccessful)
                {
                    return backupWasSuccessful;
                }

                //      d. Fourth step is to call "save resume" on BDS to let the server know that we're done copying files and it can go about it's normal business
                await BdsManager.SaveResumeAsync();

                //      e. Fifth and final step is to truncate the files that are in the backup instance directory to the sizes specified from the successful "save query" result.
                //          I. This should be done by writing the appropriate byte count of each file to a temp copy of that same file, deleting the backed up version and replacing it with the temp version.
                //              This will avoid us getting in our own way when doing the file truncation and ensure we have a clean set of files at the end.
                backupWasSuccessful = TrimBackupFiles(backupDirectoryPath, backupFileSet);
            }
            catch (Exception ex)
            {
                LogManager.LogError($"Backup was not successful.  The error encountered was {ex}.");
                backupWasSuccessful = false;
            }
            finally
            {
                // Check with BdsManager to see if the server is making changes to the files again.  This is done by checking the BackupFilesAreReadyToCopy field.  
                //  A value of true here means the server is still in a "save hold" state.
                if (BdsManager.BackupFilesAreReadyToCopy)
                {
                    // Since BDS is still in a "save hold" state we need to issue a "save resume" command.
                    await BdsManager.SaveResumeAsync();
                }
            }

            return backupWasSuccessful;
        }

        /// <summary>
        /// Stops the backup timer, disabling the interval based backups
        /// </summary>
        internal static void DisableIntervalBasedBackups()
        {
            _backupTimer.Stop();
        }

        /// <summary>
        /// Creates, if needed, and starts the backup timer, enabling the interval based backups
        /// </summary>
        internal static void EnableIntervalBasedBackups()
        {
            // Interval from the user should be in minutes...
            var backupTimerIntervalMinutes = Settings.CurrentSettings.BackupSettings.AutomaticBackupIntervalInMinutes;
            var backupTimespan = TimeSpan.FromMinutes(backupTimerIntervalMinutes);

            // ... Interval on the timer is in milliseconds, so creating TimeSpan objects of both for easier comparison.
            var timerIntervalTimespan = TimeSpan.FromMilliseconds(_backupTimer.Interval);

            // Check to see if the intervals match (if they match the result will be 0).  If they don't recreate the timer with the new interval.
            if (timerIntervalTimespan.CompareTo(backupTimespan) != 0)
            {
                // The documentation on Timer has a bunch of screwy talk about how updating intervals after they've been set doing strange things like adding the remaining
                //  old interval to the new one and such, so I'm just going to kill the Timer outright and make a new one from scratch each time the interval is change
                //  to minimize the silliness.
                _backupTimer.Stop();
                _backupTimer.Dispose();

                _backupTimer = new(backupTimespan.TotalMilliseconds) { AutoReset = true };
                _backupTimer.Elapsed += BackupTimer_Elapsed;
            }

            if (!_backupTimer.Enabled)
            {
                _backupTimer.Start();
            }
        }

        /// <summary>
        /// Creates a properly formatted backup name for the target backup file from the source file path
        /// </summary>
        /// <param name="backupFilePath">Path to the file that is being backed up.</param>
        /// <returns>The file name for the backup file that is being written.</returns>
        private static string GetBackupTargetFileName(string backupFilePath)
        {
            var backupFileName = Path.GetFileName(backupFilePath);

            // When building the targetFilePath we need to ensure to include the /db folder as appropriate
            if (backupFilePath.EndsWith(Path.Combine("db", backupFileName)))
            {
                backupFileName = Path.Combine("db", backupFileName);
            }

            return backupFileName;
        }

        /// <summary>
        /// Handles the tasks of ensuring that the KeepLastNumberOfBackups and KeepLastNumberOfDailyBackups settings are properly followed.
        /// </summary>
        private static void PerformBackupDirectoryMaintenance()
        {
            // Grab the root of the backup directory path to get information about its subdirectories
            DirectoryInfo backupDirectoryInfo = new(Settings.CurrentSettings.BackupSettings.BackupDirectoryPath);

            // Check if the backup directory exists
            if (!backupDirectoryInfo.Exists)
            {
                // No backups have been performed so we can just exit.
                return;
            }

            // Get information about each of the subdirectories.
            DirectoryInfo[] individualBackupInfos = backupDirectoryInfo.GetDirectories();

            // Snapshot the "now" time to ensure we get the same calculations each time we use it
            DateTime now = DateTime.Now;

            // Establish time borders for when backups should be removed.
            //  If they user has specified 0 keep days, this means keep forever so set the threshold to DateTime.MaxValue, otherwise compute the appropriate threshold
            DateTime normalBackupDeleteDateThreshold =
                Settings.CurrentSettings.BackupSettings.KeepBackupsForNumberOfDays == 0
                ? DateTime.MaxValue
                : now.AddDays(-Settings.CurrentSettings.BackupSettings.KeepBackupsForNumberOfDays);

            DateTime dailyBackupDeleteDateThreshold =
                Settings.CurrentSettings.BackupSettings.KeepDailyBackupsForNumberOfDays == 0
                ? DateTime.MaxValue
                : now.AddDays(-Settings.CurrentSettings.BackupSettings.KeepDailyBackupsForNumberOfDays);

            // Go through each directory and see if it should be kept or not.
            foreach (DirectoryInfo directoryInfo in individualBackupInfos)
            {
                // Check if this is a daily directory
                if (directoryInfo.Name.StartsWith(DAILY_BACKUP_DIRECTORY_NAME_PREFIX))
                {
                    if (directoryInfo.CreationTime < dailyBackupDeleteDateThreshold)
                    {
                        try
                        {
                            directoryInfo.Delete(recursive: true);
                        }
                        catch (Exception ex)
                        {
                            LogManager.LogError($"There was a problem cleaning up regular backups. The error was {ex}.");
                        }
                    }

                    // Since this check only applies to daily, and we just did it, move to the next entry
                    continue;
                }

                // Do a check on the non-daily directories
                if (directoryInfo.CreationTime < normalBackupDeleteDateThreshold)
                {
                    try
                    {
                        directoryInfo.Delete(recursive: true);
                    }
                    catch (Exception ex)
                    {
                        LogManager.LogError($"There was a problem cleaning up daily backups. The error was {ex}.");
                    }
                }
            }
        }

        /// <summary>
        /// Trims each file that was backed up to the proper length told to us by BDS.  If the files are not trimmed properly the world state will be corrupt when trying to load this backup.
        /// </summary>
        /// <param name="backupDirectoryPath">The directory path where the backup files should be copied to.</param>
        /// <param name="backupFiles">The set of BackupFile, supplied by BdsManager, that BDS said were the files that needed to be backed up.</param>
        /// <returns>Flag indicating if the offline backup was successful or not.  True means the backup was successful.  False means it failed.</returns>
        private static bool TrimBackupFiles(string backupDirectoryPath, List<BackupFile> backupFiles)
        {
            if (string.IsNullOrWhiteSpace(backupDirectoryPath))
            {
                LogManager.LogWarning($"Unable to copy files as target path {backupDirectoryPath} cannot be found.");
                return false;
            }

            if (!backupFiles.Any())
            {
                LogManager.LogWarning($"No files were specified to be backed up.  Unable to perform backup.");
                return false;
            }

            foreach (var backupFile in backupFiles)
            {
                // Get the path of the backed up file in the backup directory so we can work with it.
                var backedupFileName = GetBackupTargetFileName(backupFile.Path);
                var backedupFilePath = Path.GetFullPath(Path.Combine(backupDirectoryPath, backedupFileName));

                // If the file doesn't exist for some reason then log and fail.
                if (!File.Exists(backedupFilePath))
                {
                    LogManager.LogError($"The file {backedupFilePath} could not be found in the backup directory.  Backup was not successful.");
                    return false;
                }

                // Check to be sure we have a length that makes sense. No file should be smaller than one byte.
                if (backupFile.Length < 1)
                {
                    LogManager.LogError($"Backup file {backedupFilePath} has an invalid length from BDS of {backupFile.Length}.  Backup was not successful.");
                    return false;
                }

                // Check to be sure that the length specified from BDS is actually shorter, or equal to, the length of the backup file.  If not, we have an issue.
                if (backupFile.Length > new FileInfo(backedupFilePath).Length)
                {
                    LogManager.LogError($"Backup file {backedupFilePath} is smaller than the length BDS says it should be.  Backup was not successful.");
                    return false;
                }

                // Truncate original file in place
                using (var originalFileReader = File.Open(backedupFilePath, FileMode.Open, FileAccess.ReadWrite))
                {
                    // Setting the length of the file to the length specified from BDS will truncate it to the proper size
                    originalFileReader.SetLength(backupFile.Length);
                }
            }

            return true;
        }

        /// <summary>
        /// Contains the backup file path and the size it should be after backup is complete.
        /// </summary>
        public class BackupFile
        {
            public string Path { get; set; } = string.Empty;

            public long Length { get; set; } = 0;
        }
    }
}
