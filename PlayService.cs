﻿using Microsoft.Win32.SafeHandles;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Hocon;

namespace PlayService
{
    public partial class PlayService : ServiceBase
    {
        private const string PidfileName = @"RUNNING_PID";
        private const string ErrorFileName = "PlayServiceError";

        private readonly List<string> _startupInfo = new List<string>();

        private int? _processId;
        private Process _parentProcess;

        internal enum ControlEvent
        {
            CtrlC = 0,
            //CtrlBreak = 1
        }

        private const string Kernel32 = "kernel32.dll";

        [SuppressUnmanagedCodeSecurity]
        internal static class UnsafeNativeMethods
        {
            [DllImport(Kernel32, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GenerateConsoleCtrlEvent(ControlEvent controlEvent, uint processGroupId);

            [DllImport(Kernel32, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool AttachConsole(int processId);

            [DllImport(Kernel32, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool FreeConsole();
        }

        public PlayService()
        {
            InitializeComponent();
            if (!String.IsNullOrEmpty(Program.Param.ServiceName))
                ServiceName = Program.Param.ServiceName;
        }

        protected override void OnStart(string[] args)
        {
            _startupInfo.Clear();
            try {
#if DEBUG
                File.AppendAllText(Program.DebugTextFile, $@"OnStart{'\n'}");
#endif
                var config = LoadConfig();

                var timeoutOnStart = config.GetTimeSpan("service.timeoutOnStart");
                RequestAdditionalTime((int) timeoutOnStart.TotalMilliseconds);
#if DEBUG
                File.AppendAllText(Program.DebugTextFile, $@"startTimeout:{timeoutOnStart.TotalMilliseconds}{'\n'}");
#endif

                var pidfilePath = GetPidfilePath(config);
                if (pidfilePath != null && File.Exists(pidfilePath)) {
                    if (IsValidPidFile(pidfilePath, out int pid))
                        throw new ApplicationException($"This application is already running (ProcessID={pid})");

                    EventLog.WriteEntry(
                        $"Delete last {Path.GetFileName(pidfilePath)}. {(pid >= 0 ? $"(ProcessID={pid})" : "")}",
                        EventLogEntryType.Warning);
                    File.Delete(pidfilePath);
                }

                ExtractDistIfNecessary(config);

                var appHome = FindAppHome(config);
                if (appHome == null)
                    throw new ApplicationException("not found application");

                _startupInfo.Add($@"APP_HOME is {appHome}");

                var launcher = GetLauncher(config, appHome);
                if (!File.Exists(launcher))
                    throw new ApplicationException($"not found {launcher}");

                var outputEncoding = GetOutputEncoding(config);
                var psi = new ProcessStartInfo
                {
                    FileName = launcher,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = appHome,
                    RedirectStandardError = true,
                    StandardErrorEncoding = outputEncoding
                };

                var envs = GetEnv(config);

                var optsKey = $"{GetAppName(config).ToUpper().Replace('-', '_')}_OPTS";
                if (!psi.EnvironmentVariables.ContainsKey(optsKey) && !envs.ContainsKey(optsKey)) {
                    var optionList = config.GetStringList("service.app.option");
                    if (optionList != null && optionList.Count > 0)
                    {
                        var builder = new StringBuilder();
                        foreach (var option in optionList)
                        {
                            builder.Append(option);
                            builder.Append(' ');
                        }

                        builder.Length -= 1;
                        envs.Add(optsKey, builder.ToString());
                    }
                }

                foreach (var env in envs) {
                    if (psi.EnvironmentVariables.ContainsKey((string)env.Key)) {
                        psi.EnvironmentVariables.Remove((string)env.Key);
                    }
                    psi.EnvironmentVariables.Add((string)env.Key, (string)env.Value);
                }

#if DEBUG
                File.AppendAllText(Program.DebugTextFile, $@"env:{'\n'}");
                foreach (DictionaryEntry env in psi.EnvironmentVariables) {
                    File.AppendAllText(Program.DebugTextFile, $@"    {env.Key}={env.Value}{'\n'}");
                }
#endif

                _processId = null;

                if (config.GetBoolean("service.generateTrialLauncher")) {
                    var filename = GenerateTrialLauncher(appHome, launcher, envs, outputEncoding);
                    EventLog.WriteEntry($"Generate Trial Launcher.{'\n'}{filename}{'\n'}Don't Start Service.", EventLogEntryType.Warning);
                    throw new ApplicationException("Generate Trial Launcher.");
                }

                pidfilePath = GetPidfilePath(config) ?? Path.Combine(appHome, PidfileName);
                using (var pidFileWaitHandle = new ManualResetEvent(false))
                using (var pidFileWatcher = new FileSystemWatcher()) {
                    pidFileWatcher.Path = Path.GetDirectoryName(pidfilePath) + @"\";
                    pidFileWatcher.Filter = Path.GetFileName(pidfilePath);
                    pidFileWatcher.NotifyFilter = NotifyFilters.LastWrite;
                    pidFileWatcher.Changed += delegate
                    {
                        // ReSharper disable once AccessToDisposedClosure
                        pidFileWaitHandle.Set();
                    };
                    pidFileWatcher.EnableRaisingEvents = true;

                    _parentProcess?.Dispose();
                    _parentProcess = Process.Start(psi);
                    try {
                        Debug.Assert(_parentProcess != null, nameof(_parentProcess) + " != null");
                        _parentProcess.ErrorDataReceived += delegate(object o, DataReceivedEventArgs eventArgs)
                        {
                            if (!string.IsNullOrEmpty(eventArgs.Data)) {
                                using (var sw = new StreamWriter(GetErrorFilename(_processId.GetValueOrDefault(0)),
                                    true)) {
                                    sw.WriteLine(eventArgs.Data);
                                }
                            }
                        };
                        _parentProcess.BeginErrorReadLine();

                        var waitProcessHandle = new ManualResetEvent(true)
                        {
                            SafeWaitHandle = new SafeWaitHandle(_parentProcess.Handle, false)
                        };
                        var index = WaitHandle.WaitAny(new WaitHandle[] {pidFileWaitHandle, waitProcessHandle},
                            Math.Max((int)timeoutOnStart.Subtract(TimeSpan.FromSeconds(5)).TotalMilliseconds, 1));
                        if (index == WaitHandle.WaitTimeout)
                            throw new ApplicationException("Timeout");

                        if (index == 1) {
                            // pidFileが生成される前にparentProcess(startBatch)が終了 → Application開始失敗
                            throw new ApplicationException("Application Start Failed");
                        }

                        if (!File.Exists(pidfilePath)) {
                            throw new ApplicationException("Pidfile is not created");
                        }

                        pidFileWatcher.EnableRaisingEvents = false;

                        Thread.Sleep(1);

                        for (int i = 0; i < 10; i++) {
                            try {
                                using (var reader = new StreamReader(pidfilePath)) {
                                    var line = reader.ReadLine();
                                    Debug.Assert(line != null, "line != null");
                                    _processId = Int32.Parse(line);
                                }
                            } catch (IOException) {
                                // タイミングによりpidfileがアクセス不可の場合があるので何度か繰り返す
                                Thread.Sleep(100);
                                continue;
                            }

                            break;
                        }

                        _parentProcess.Exited += OnParentProcessExited;
                        _parentProcess.EnableRaisingEvents = true;

                    } catch {
                        _parentProcess?.Dispose();
                        _parentProcess = null;
                        throw;
                    }

                    _startupInfo.Add($@"Play server process ID is {_processId}");
                }


            } finally {
                if (_startupInfo.Count > 0) {
                    var builder = new StringBuilder();
                    foreach (var text in _startupInfo) {
                        builder.AppendLine(text);
                    }
                    EventLog.WriteEntry(builder.ToString(), EventLogEntryType.Information);
                }
            }

            ExitCode = 0;
        }

        protected override void OnStop()
        {
#if DEBUG
            File.AppendAllText(Program.DebugTextFile, $@"OnStop{'\n'}");
#endif

            var config = LoadConfig();

            var timeoutOnStop = config.GetTimeSpan("service.timeoutOnStop");
            RequestAdditionalTime((int)timeoutOnStop.TotalMilliseconds);

            if (!_processId.HasValue) {
                ExitCode = 1;
                return;
            }
            try
            {
                try
                {
                    // Play (java.exe)の終了
                    using (var process = Process.GetProcessById(_processId.Value)) {
                        if (process.HasExited) {
                            ExitCode = 3;
                            return;
                        }
                        _parentProcess.EnableRaisingEvents = false;
                        _parentProcess.Exited -= OnParentProcessExited;

                        // Ctrl+C送信
                        if (!UnsafeNativeMethods.AttachConsole(process.Id)) {
                            throw new ApplicationException($"AttachConsole: {Marshal.GetLastWin32Error()}");
                        }
                        Console.CancelKeyPress += ConsoleOnCancelKeyPress;
                        try {
                            if (!UnsafeNativeMethods.GenerateConsoleCtrlEvent(ControlEvent.CtrlC, (uint)process.SessionId))
                            {
                                throw new ApplicationException($"GenerateConsoleCtrlEvent: {Marshal.GetLastWin32Error()}");
                            }

                            Thread.Sleep(1);
                        } finally {
                            Console.CancelKeyPress -= ConsoleOnCancelKeyPress;
                            if (!UnsafeNativeMethods.FreeConsole()) {
                                throw new ApplicationException($"FreeConsole: {Marshal.GetLastWin32Error()}");
                            }
                        }

                        process.WaitForExit();
                    }
                } catch (ArgumentException) {
                    ExitCode = 2;
                    return;
                }

                Thread.Sleep(1);

                // バッチ呼び出しcmd.exeの終了
                if (_parentProcess == null) {
                    return;
                }
                try {
                    if (_parentProcess.HasExited) {
                        return;
                    }

                    _parentProcess.Kill();

                    File.Delete(GetErrorFilename(_processId.GetValueOrDefault(0)));
                } catch (ArgumentException) {
                    return;
                } finally {
                    _parentProcess.Dispose();
                    _parentProcess = null;
                }

            } finally {
                _processId = null;
            }

            ExitCode = 0;
        }

        private void OnParentProcessExited(object sender, EventArgs e)
        {
            var errorFilename = GetErrorFilename(_processId.GetValueOrDefault(0));
            EventLog.WriteEntry(
                File.Exists(errorFilename)
                    ? $"Play server is aborted. see {errorFilename}"
                    : "Play server is aborted.", EventLogEntryType.Error);
            _parentProcess?.Dispose();
            _parentProcess = null;
            Stop();
        }

        private void ConsoleOnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
        }

        private Config LoadConfig()
        {
            var configFilename = Program.Param.ConfigFile ?? @"service.conf";
            configFilename = Utils.GetFullPath(Program.Param.WorkDir ?? Path.GetFullPath("."), configFilename);
#if DEBUG
            File.AppendAllText(Program.DebugTextFile, $@"configFilename:{configFilename}{'\n'}");
#endif
            if (!File.Exists(configFilename))
                configFilename = null;
            if (configFilename != null)
                _startupInfo.Add($@"ServiceConfigFile is {configFilename}");

            if (Program.Param.WorkDir != null)
            {
                Environment.SetEnvironmentVariable("WorkDir", Program.Param.WorkDir);
                Environment.SetEnvironmentVariable("WorkDirName", Path.GetFileName(Program.Param.WorkDir));
                Environment.SetEnvironmentVariable("WorkDirNameUri", Uri.EscapeUriString(Path.GetFileName(Program.Param.WorkDir)));
            }
            if (Program.Param.AppHome != null)
            {
                Environment.SetEnvironmentVariable("AppHome", Program.Param.AppHome);
            }

            var config =
                (configFilename != null ? HoconConfigurationFactory.FromFile(configFilename) : Config.Empty)
                .WithFallback(HoconConfigurationFactory.FromResource("PlayService.reference.conf", Assembly.GetExecutingAssembly()));

            if (Program.Param.WorkDir != null)
            {
                Environment.SetEnvironmentVariable("WorkDir", null);
                Environment.SetEnvironmentVariable("WorkDirName", null);
                Environment.SetEnvironmentVariable("WorkDirNameUri", null);
            }
            if (Program.Param.AppHome != null)
            {
                Environment.SetEnvironmentVariable("AppHome", null);
            }
            return config;
        }

        private string GetErrorFilename(int processId)
        {
            var filename = Program.Param.WorkDir == null
                ? Path.Combine(Path.GetTempPath(), $@"{ErrorFileName}{processId}.log")
                : Path.Combine(Program.Param.WorkDir, $@"logs\{ErrorFileName}{processId}.log");

            var dir = Path.GetDirectoryName(filename);
            if (dir != null && !Directory.Exists(Path.GetDirectoryName(filename)))
                Directory.CreateDirectory(dir);

            return filename;
        }

        private bool IsValidPidFile(string pidfilePath, out int pid)
        {
            pid = -1;
            using (var r = new StreamReader(pidfilePath, new UTF8Encoding(false))) {
                var line = r.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(line))
                    return false;
                if (!int.TryParse(line, out pid))
                    return false;
                try {
                    using (Process.GetProcessById(pid))
                    {
                    }
                } catch (ArgumentException) {
                    return false;
                }
            }

            return true;
        }

        private void ExtractDistIfNecessary(Config config)
        {
            var distFile = FindNewestDistFile(config);
            if (distFile == null)
                return;

            var targetDir = GetTargetDir();

            if (Directory.Exists(targetDir)) {
                var targetWriteTime = Directory.GetLastWriteTime(targetDir);
                var distWriteTime = File.GetLastWriteTime(distFile);
#if DEBUG
                File.AppendAllText(Program.DebugTextFile, $@"targetWriteTime:{targetWriteTime}{'\n'}");
                File.AppendAllText(Program.DebugTextFile, $@"distWriteTime:{distWriteTime}{'\n'}");
                File.AppendAllText(Program.DebugTextFile, $@"{targetWriteTime > distWriteTime}{'\n'}");
#endif
                if (targetWriteTime > distWriteTime)
                    return;

                var prevTargetDir = targetDir + "Prev";
                if (Directory.Exists(prevTargetDir)) {
                    Directory.Delete(prevTargetDir, true);
                }

#if DEBUG
                File.AppendAllText(Program.DebugTextFile, $@"Move{targetDir} to {prevTargetDir}{'\n'}");
#endif
                Directory.Move(targetDir, prevTargetDir);
            }

            _startupInfo.Add($@"Extract {Path.GetFileName(distFile)}");
            ZipFile.ExtractToDirectory(distFile, targetDir);
        }

        private string FindNewestDistFile(Config config)
        {
            if (Program.Param.WorkDir == null)
                return null;

            var appName = GetAppName(config);

            return Directory.EnumerateFiles(Program.Param.WorkDir, $@"{appName}-*.zip")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }

        private string GetAppName(Config config)
        {
            if (Program.Param.AppName != null)
                return Program.Param.AppName;

            var result = config.GetString("service.app.name");
            if (String.IsNullOrEmpty(result))
                throw new ApplicationException("app.name is not set");
            return result;
        }

        private Encoding GetOutputEncoding(Config config)
        {
            var encoding = config.GetString("service.app.outputEncoding");
            return encoding == "utf-8" ? new UTF8Encoding(false) : Encoding.GetEncoding(encoding);
        }

        /// <summary>
        /// 環境変数
        /// </summary>
        /// <param name="config"></param>
        /// <returns></returns>
        private Dictionary<string,string> GetEnv(Config config)
        {
            var result = new Dictionary<string,string>();
            if (Program.Param.Env != null) {

                Program.Param.Env.Split(',')
                    .Select(x => x.Split(new[] { '=' }, 2))
                    .ToList().ForEach(keyValue => { result.Add(keyValue[0], keyValue.Length > 1 ? keyValue[1] : ""); });
            } else {
                config.GetObject("service.app.environment").Values
                    .ToList().ForEach(field => { result.Add(field.Key, field.Value.GetString()); });
            }

            return result;
        }

        /// <summary>
        /// stageディレクトリの場所
        /// workDirディレクトリ内のstageディレクトリ
        /// </summary>
        /// <returns></returns>
        private string GetStageDir()
        {
            if (Program.Param.WorkDir != null)
                return Path.Combine(Program.Param.WorkDir, "stage");
            return null;
        }

        /// <summary>
        /// targetディレクトリの場所
        /// workDirディレクトリ内のtargetディレクトリ
        /// </summary>
        /// <returns></returns>
        private string GetTargetDir()
        {
            if (Program.Param.WorkDir != null)
                return Path.Combine(Program.Param.WorkDir, "target");
            return null;
        }

        /// <summary>
        /// APP_HOME
        /// launcher script 内の APP_HOME と同じ意味 
        /// </summary>
        /// <param name="config"></param>
        /// <returns></returns>
        private string FindAppHome(Config config)
        {
            if (Program.Param.AppHome != null)
                return Program.Param.AppHome;

            var stageDir = GetStageDir();
            if (Directory.Exists(stageDir)) {
                if (Directory.Exists(Path.Combine(stageDir, "bin")))
                    return stageDir;
            }

            var targetDir = GetTargetDir();
            if (Directory.Exists(targetDir)) {
                var dirList = Directory.EnumerateDirectories(targetDir, $"{GetAppName(config)}-*").ToList();
                if (dirList.Count == 1) {
                    return dirList[0];
                }
            }
            return null;
        }

        private string GetLauncher(Config config, string appHome)
        {
            return Path.Combine(appHome, $@"bin\{GetAppName(config)}.bat");
        }

        private string GetPidfilePath(Config config)
        {
            var result = config.GetString("service.app.pidfilePath");
            if (string.IsNullOrEmpty(result)) {
                var appHome = FindAppHome(config);
                if (appHome != null)
                    result = Path.Combine(appHome, PidfileName);
            } else {
                result = Utils.GetFullPath(Program.Param.WorkDir ?? Program.Param.AppHome, result);
            }

            return result;
        }

        private string GenerateTrialLauncher(string appHome, string launcher, Dictionary<string,string> envs, Encoding outputEncoding)
        {
            var builder = new StringBuilder();
            builder.AppendLine(@"set local");
            builder.AppendLine($@"chcp {outputEncoding.CodePage}");
            builder.AppendLine($@"cd {appHome}");
            builder.AppendLine(@"@echo.");
            foreach (var env in envs) {
                builder.AppendLine($@"set {env.Key}={Utils.BatchEscape((string)env.Value)}");
            }
            builder.AppendLine(@"@echo.");
            builder.AppendLine(@"set");
            builder.AppendLine(@"@echo.");
            builder.AppendLine($@"call {'"'}{launcher}{'"'}");
            builder.AppendLine(@"@pause");
            var result = Path.Combine(Program.Param.WorkDir ?? Program.Param.AppHome, "TrialLauncher.bat");
            File.WriteAllText(result, builder.ToString());
            return result;
        }
    }
}
