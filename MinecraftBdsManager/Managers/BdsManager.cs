﻿using MinecraftBdsManager.Configuration;
using MinecraftBdsManager.Logging;

namespace MinecraftBdsManager.Managers
{
    internal static class BdsManager
    {
        /// <summary>
        /// The executable name for BDS. This is the name as it ships and the user should not rename this.
        /// </summary>
        private const string BDS_EXECUTABLE_FILE_NAME = "bedrock_server.exe";
        private static int _onlinePlayerCount = 0;
        private static readonly object _backupFilesLock = new();


        static BdsManager()
        {
            LogMonitor.LineRead += LogMonitor_LineRead;
        }

        /// <summary>
        /// Flag indicating if the backup files are ready to copy or not.
        /// </summary>
        public static bool BackupFilesAreReadyToCopy { get; private set; } = false;

        /// <summary>
        /// The set of files to be backed up.  This field is empty except when BackupFilesAreReadToCopy is true
        /// </summary>
        public static List<BackupManager.BackupFile> BackupFiles { get; private set; } = new();

        /// <summary>
        /// Object that should be used for synchronization for tasks that access the BackupFiles property.
        /// </summary>
        public static object BackupFilesLock => _backupFilesLock;

        /// <summary>
        /// The version of BDS that is running.
        /// </summary>
        public static Version? BedrockServerVersion { get; private set; }

        /// <summary>
        /// The level name that was loaded by BDS.  For example: Bedrock level
        /// </summary>
        public static string LevelName { get; private set; } = string.Empty;

        /// <summary>
        /// Keeps track of the number of players who are online.  Whenever a user logs on this counter ++.  When one logs off this counter --.
        /// </summary>
        public static int OnlinePlayerCount
        {
            get
            {
                return _onlinePlayerCount;
            }
            private set
            {
                _onlinePlayerCount = value;
            }
        }

        /// <summary>
        /// Flag indicating if BDS is running or not
        /// </summary>
        public static bool ServerIsRunning { get; private set; } = false;

        /// <summary>
        /// DateTime of the last time the server was started.  Null if the server has not been started.
        /// </summary>
        public static DateTime? ServerLastStartedOn { get; private set; }

        /// <summary>
        /// DateTime of the last time the server was stopped.  Null if the server has not been stopped.
        /// </summary>
        public static DateTime? ServerLastStoppedOn { get; private set; }

        /// <summary>
        /// DateTime of the last time a user logged in.  Null if no users have logged in.  This will always be the DateTime of the most recent user login.
        /// </summary>
        public static DateTime? UserLastLoggedOnAt { get; private set; }

        /// <summary>
        /// DateTime of the last time a user logged off.  Null if no users have logged off.  This will always be the DateTime of the most recent user log off.
        /// </summary>
        public static DateTime? UserLastLoggedOffAt { get; private set; }

        /// <summary>
        /// Path to the world directory.  This is null if the server has not been started.
        /// </summary>
        public static string? WorldDirectoryPath { get; private set; }

        /// <summary>
        /// Determines if users have active on the server in the last time interval specified
        /// </summary>
        /// <param name="timeIntervalToCheckForActiveUsers">Interval of time, expressed as a TimeSpan, to check to see if users have been active on the server.</param>
        /// <returns></returns>
        internal static bool HaveUsersBeenOnInTheLastAmountOfTime(TimeSpan timeIntervalToCheckForActiveUsers)
        {
            // Get when a user most recently logged off
            var latestUserDisconnectionTime = BdsManager.UserLastLoggedOffAt;
            var latestUserConnectedTime = BdsManager.UserLastLoggedOnAt;

            bool usersHaveBeenActiveOnTheServer = false;

            // If no one has logged on or off we can simply exit.
            if (latestUserConnectedTime == null && latestUserDisconnectionTime == null)
            {
                return usersHaveBeenActiveOnTheServer;
            }

            // Before we bother checking time differences and such, just check to see if anyone is online based on our counter;
            if (OnlinePlayerCount > 0)
            {
                return true;
            }

            // Check the latest log on and off times to see if users are either currently active or have been active in the last backup interval
            if (latestUserConnectedTime > latestUserDisconnectionTime || (latestUserConnectedTime.HasValue && !latestUserDisconnectionTime.HasValue))
            {
                // If a user has connected and not disconnected then we know they're active
                return true;
            }
            else
            {
                // Check if the most recent log off was more that the backup interval ago.  Using local time here because the BDS log times are local.
                //  That does mean DST can bite us here, however I'm not going out of my way for a 2x time a year event right now.
                return (DateTime.Now - latestUserDisconnectionTime!).Value < timeIntervalToCheckForActiveUsers;
            }
        }

        /// <summary>
        /// BdsMonitor will watch LogMonitor updates to see when there are updates that are important to it and record them
        /// </summary>
        /// <param name="sender">The sender of the event.  In this case its the LogMonitor.</param>
        /// <param name="e">Event data from LogMonitor. This includes the details of the line that was just logged.</param>
        private static void LogMonitor_LineRead(object? sender, LogMonitor.LineReadEventArgs e)
        {
            string monitoredLine = e.Line;
            if (string.IsNullOrWhiteSpace(monitoredLine))
            {
                return;
            }

            // MinecraftBdsManager Information: 0 : [2021-12-23 21:10:20:336 INFO] Version 1.18.2.03
            if (monitoredLine.Contains("] Version"))
            {
                int versionNumberIndexStart = monitoredLine.IndexOf("Version") + 8;
                int versionNumberIndexStop = monitoredLine.Length;
                string versionString = monitoredLine[versionNumberIndexStart..versionNumberIndexStop];
                BedrockServerVersion = new Version(versionString);

                return;
            }

            // MinecraftBdsManager Information: 0 : [2021-12-23 21:10:21:932 INFO] opening worlds/Bedrock level/db
            if (monitoredLine.Contains("Level Name:"))
            {
                int levelNameIndexStart = monitoredLine.LastIndexOf(":") + 1;
                int levelNameIndexStop = monitoredLine.Length;
                LevelName = monitoredLine[levelNameIndexStart..levelNameIndexStop].Trim();

                return;
            }

            // MinecraftBdsManager Information: 0 : [2021-12-23 21:10:21:932 INFO] opening worlds/Bedrock level/db
            if (monitoredLine.Contains("] opening worlds"))
            {
                int worldsFolderIndexStart = monitoredLine.IndexOf("worlds");
                int worldsFolderIndexStop = monitoredLine.IndexOf("/db", worldsFolderIndexStart);
                string worldsDirectoryPath = monitoredLine[worldsFolderIndexStart..worldsFolderIndexStop];
                WorldDirectoryPath = Path.GetFullPath(Path.Combine(Settings.CurrentSettings.BedrockDedicateServerDirectoryPath, worldsDirectoryPath));

                return;
            }

            // MinecraftBdsManager Information: 0 : [2021-12-23 21:10:37:788 INFO] Server started.
            if (monitoredLine.Contains("Server started."))
            {
                ServerLastStartedOn = LogMonitor.ReadDateTimeFromLogLine(monitoredLine);

                ServerIsRunning = true;
                return;
            }

            // MinecraftBdsManager Information: 0 : [2021-12-23 21:12:27:589 INFO] Stopping server...
            if (monitoredLine.Contains("Stopping server..."))
            {
                ServerLastStoppedOn = LogMonitor.ReadDateTimeFromLogLine(monitoredLine);
                return;
            }

            // MinecraftBdsManager Information: 0 : Quit correctly
            if (monitoredLine.Contains("Quit correctly"))
            {
                ServerIsRunning = false;
                return;
            }

            // MinecraftBdsManager Information: 0 : Data saved. Files are now ready to be copied.
            if (monitoredLine.Contains("Data saved. Files are now ready to be copied."))
            {
                BackupFilesAreReadyToCopy = true;
                return;
            }

            // MinecraftBdsManager Information: 0 : Bedrock level/db/000096.ldb:308603, Bedrock level/db/000102.ldb:491, Bedrock level/db/000105.ldb:491, Bedrock level/db/000106.log:0, Bedrock level/db/CURRENT:16, Bedrock level/db/MANIFEST-000104:216, Bedrock level/level.dat:2543, Bedrock level/level.dat_old:2543, Bedrock level/levelname.txt:13
            if (monitoredLine.Contains($"{LevelName}/db/CURRENT:"))
            {
                // Establish the lock while we are populating the collection to ensure that it is not accessed until we are done filling the list completely
                lock (_backupFilesLock)
                {
                    var firstColonIndex = monitoredLine.IndexOf(':');
                    var secondColonIndex = monitoredLine.IndexOf(':', firstColonIndex + 1) + 1;
                    string backupFileListCsv = monitoredLine[secondColonIndex..monitoredLine.Length].Trim();

                    foreach (var backupFile in backupFileListCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var backupFileParts = backupFile.Split(':');
                        BackupFiles.Add(new BackupManager.BackupFile()
                        {
                            Path = Path.GetFullPath(Path.Combine(Settings.CurrentSettings.BedrockDedicateServerDirectoryPath, "worlds", backupFileParts[0])),
                            Length = long.Parse(backupFileParts[1])
                        });
                    }
                }

                return;
            }

            // MinecraftBdsManager Information: 0 : Changes to the world are resumed.
            if (monitoredLine.Contains("Changes to the world are resumed."))
            {
                BackupFilesAreReadyToCopy = false;
                BackupFiles.Clear();
                return;
            }

            // MinecraftBdsManager Information: 0 : [2021-12-24 11:50:57:895 INFO] Player connected: IdleOtter7772, xuid: 2535465704948384
            if (monitoredLine.Contains("Player connected"))
            {
                UserLastLoggedOnAt = LogMonitor.ReadDateTimeFromLogLine(monitoredLine);
                Interlocked.Increment(ref _onlinePlayerCount);
                return;
            }

            // MinecraftBdsManager Information: 0 : [2021-12-24 11:51:41:484 INFO] Player disconnected: IdleOtter7772, xuid: 2535465704948384
            if (monitoredLine.Contains("Player disconnected"))
            {
                UserLastLoggedOffAt = LogMonitor.ReadDateTimeFromLogLine(monitoredLine);
                Interlocked.Decrement(ref _onlinePlayerCount);
                return;
            }

        }

        /// <summary>
        /// Sends a command to the BDS instance.
        /// </summary>
        /// <param name="command">The command to send.</param>
        /// <param name="userSentCommand">Flag indicating if this was a command sent based on user imput or not.  A value of true means it was sent by a user. False means it was sent by the system(BdsManager).</param>
        /// <returns>Handle to the async promise</returns>
        internal async static Task SendCommandAsync(string command, bool userSentCommand = false)
        {
            // If a null or blank command was sent then simply return as there is nothing to send
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            var bdsProcess = ProcessManager.TrackedProcesses[ProcessName.BedrockDedicatedServer];
            // Check if the process is null. This should not ever happen, but covering bases.
            if (bdsProcess == null)
            {
                LogManager.LogError("Bedrock Dedicated Server process is not tracked properly.  Unable to continue");
                return;
            }

            if (userSentCommand)
            {
                LogManager.LogVerbose(LoggingLeadIn.BuildLeadIn(LoggingLeadIn.UserSentMessage), command);
            }
            else
            {
                LogManager.LogInformation(command);
            }

            if (!ServerIsRunning)
            {
                LogManager.LogWarning($"Unable to issue command '{command}' since server is not running.");
            }

            await bdsProcess.StandardInput.WriteAsync($"{command}\n");
        }

        /// <summary>
        /// This will ask the server to prepare for a backup. It’s asynchronous and will return immediately.
        /// </summary>
        /// <returns>Handle to the async promise</returns>
        internal async static Task SaveHoldAsync()
        {
            await SendCommandAsync("save hold");
        }

        /// <summary>
        /// After calling save hold you should call this command repeatedly to see if the preparation has finished. When it returns a success it will return a file list (with lengths for each file) 
        /// of the files you need to copy. The server will not pause while this is happening, so some files can be modified while the backup is taking place. As long as you only copy the files in the 
        /// given file list and truncate the copied files to the specified lengths, then the backup should be valid.
        /// </summary>
        /// <returns>Handle to the async promise</returns>
        internal async static Task SaveQueryAsync()
        {
            await SendCommandAsync("save query");
        }

        /// <summary>
        /// When you’re finished with copying the files you should call this to tell the server that it’s okay to remove old files again.
        /// </summary>
        /// <returns>Handle to the async promise</returns>
        internal async static Task SaveResumeAsync()
        {
            await SendCommandAsync("save resume");
        }

        /// <summary>
        /// Starts the Bedrock Server process after validating the necessary configuration values are present.
        /// </summary>
        /// <returns>Handle to the async promise with a flag return value.  True indicates the server started successfully.  False indicates it did not.</returns>
        internal async static Task<bool> StartAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.CurrentSettings.BedrockDedicateServerDirectoryPath))
            {
                // TODO : Consider loading the Settings form if this value is not present.

                LogManager.LogError("Unable to auto start Bedrock Dedicated Server as the path to bedrock_server.exe has not yet been specified.  Please check and update your BDS Manager settings.");
                return false;
            }

            var bdsExecutableFilePath = Path.GetFullPath(Path.Combine(Settings.CurrentSettings.BedrockDedicateServerDirectoryPath, BDS_EXECUTABLE_FILE_NAME));

            if (!File.Exists(bdsExecutableFilePath))
            {
                LogManager.LogError($"Unable to auto start Bedrock Dedicated Server as bedrock_server.exe cannot be found at {bdsExecutableFilePath}.  Please check and update your BDS Manager settings.  Ensure the BDS executable is named {BDS_EXECUTABLE_FILE_NAME}.");
                return false;
            }

            //  If the user has asked for log files, create a new log file per start
            if (Settings.CurrentSettings.LoggingSettings.EnableLoggingToFile)
            {
                LogManager.RegisterFileLogger(Settings.CurrentSettings.LoggingSettings.FileLoggingDirectoryPath, unregisterExistingListener: true);
            }

            // If the user has asked for backups to be taken on start, take one before we start the server.
            if (Settings.CurrentSettings.BackupSettings.BackupOnServerStart)
            {
                LogManager.LogInformation("Performing backup on start per user settings.");
                var backupResult = await BackupManager.CreateBackupAsync();

                if (backupResult.WasSuccessful)
                {
                    LogManager.LogInformation("Backup completed successfully");
                }
                else
                {
                    LogManager.LogError("Backup failed.");
                }
            }

            bool newProcessStarted = ProcessManager.StartProcess(ProcessName.BedrockDedicatedServer, bdsExecutableFilePath, string.Empty);

            if (!newProcessStarted)
            {
                await SendCommandAsync("start");
            }

            // Wait for the server to start
            while (!BdsManager.ServerIsRunning)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            // Check if the user has requested to enable automatic backups.
            if (Settings.CurrentSettings.BackupSettings.EnableAutomaticBackups)
            {
                BackupManager.EnableIntervalBasedBackups();
            }

            // Check if the user has requested to enable automatic map generation.
            if (Settings.CurrentSettings.MapSettings.EnableMapGeneration)
            {
                MapManager.EnableIntervalBasedMapGeneration();
            }

            // Check to see if we should enable auto restart.  These settings should apply even if the server has not started yet.
            if (Settings.CurrentSettings.RestartSettings.EnableRestartOnInterval)
            {
                RestartManager.EnableIntervalBasedRestart();
            }

            if (Settings.CurrentSettings.RestartSettings.EnableRestartOnSchedule)
            {
                RestartManager.EnableScheduleBasedRestart();
            }

            return true;
        }

        /// <summary>
        /// Stops the Bedrock Server instance and checks to see if a backup should be done after server is done stopping.
        /// </summary>
        /// <returns>Handle to the async promise</returns>
        internal async static Task StopAsync()
        {
            // Send the stop command
            await SendCommandAsync("stop");

            // Wait until the server is fully stopped.
            while (BdsManager.ServerIsRunning)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            // Since the server is stopping we want to disable interval based backups, if they are enabled.
            //  Check if the user has requested to enable automatic backups, and if so, disable them while the server is shut down.
            if (Settings.CurrentSettings.BackupSettings.EnableAutomaticBackups)
            {
                BackupManager.DisableIntervalBasedBackups();
            }

            // Check if the user wanted a backup on stop and if so, take one
            if (Settings.CurrentSettings.BackupSettings.BackupOnServerStop)
            {
                LogManager.LogInformation("Performing backup on stop per user settings.");
                var backupResult = await BackupManager.CreateBackupAsync();

                if (backupResult.WasSuccessful)
                {
                    LogManager.LogInformation("Backup completed successfully");
                }
                else
                {
                    LogManager.LogError("Backup failed.");
                }
            }
        }
    }
}
