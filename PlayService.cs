using Microsoft.Win32.SafeHandles;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace PlayService
{
    public partial class PlayService : ServiceBase
    {
        private const int StartTimeout = 60000;

        private const string ErrorFileName = "PlayServiceError";

        private readonly Encoding _outputEncoding = new UTF8Encoding(false);

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
            if (File.Exists(pidfilePath)) {
                throw new ApplicationException($"This application is already running (Or delete {pidfilePath} file)");
            }

            RequestAdditionalTime(StartTimeout);

            var startBatch = Path.Combine(Program.Param.AppHome, @"bin\" + Program.Param.AppName + ".bat");
            var psi = new ProcessStartInfo
            {
                FileName = startBatch,
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Program.Param.AppHome,
                RedirectStandardError = true,
                StandardErrorEncoding = _outputEncoding
            };
            if (!File.Exists(startBatch))
                throw new ApplicationException($"not found {startBatch}");

            if (Program.Param.Env != null) {
                var envs = Program.Param.Env.Split(new[] { ',' });
                foreach (var env in envs) {
                    var keyValue = env.Split(new[] { '=' }, 2);
                    if (psi.EnvironmentVariables.ContainsKey(keyValue[0])) {
                        psi.EnvironmentVariables.Remove(keyValue[0]);
                    }
                    var value = keyValue.Length > 1 ? keyValue[1] : "";
                    psi.EnvironmentVariables.Add(keyValue[0], value);
                }
            }

            _processId = null;
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
                        if (_processId.HasValue)
                            using (var sw = new StreamWriter(GetErrorFilename(_processId.Value), true)) {
                                sw.Write(eventArgs.Data);
                            }
                        else
                            EventLog.WriteEntry(eventArgs.Data, EventLogEntryType.Error);
                    };
                    _parentProcess.BeginErrorReadLine();

                    var waitProcessHandle = new ManualResetEvent(true)
                    {
                        SafeWaitHandle = new SafeWaitHandle(_parentProcess.Handle, false)
                    };
                    var index = WaitHandle.WaitAny(new WaitHandle[] { pidFileWaitHandle, waitProcessHandle }, StartTimeout - 5000);
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

                }
                catch {
                    _parentProcess?.Dispose();
                    _parentProcess = null;
                    throw;
                }

                EventLog.WriteEntry($"Play server process ID is {_processId}");
            }
            ExitCode = 0;
        }

        private string GetErrorFilename(int processId)
        {
            return Path.Combine(Path.GetTempPath(), $@"{ErrorFileName}{processId}.txt");

        }

        private void OnParentProcessExited(object sender, EventArgs e)
        {
            EventLog.WriteEntry($"Play server is aborted. see {GetErrorFilename(_processId.GetValueOrDefault(0))}", EventLogEntryType.Error);
            _parentProcess?.Dispose();
            _parentProcess = null;
            Stop();
        }

        protected override void OnStop()
        {
            if (!_processId.HasValue) {
                ExitCode = 1;
                return;
            }

            try {
                try {
                    // Play (java.exe)の終了
                    using (var process = Process.GetProcessById(_processId.Value)) {
                        if (process.HasExited) {
                            ExitCode = 3;
                            return;
                        }
                        _parentProcess.EnableRaisingEvents = false;

                        // Ctrl+C送信
                        if (!UnsafeNativeMethods.AttachConsole(process.Id)) {
                            throw new ApplicationException(String.Format("AttachConsole: {0}", Marshal.GetLastWin32Error()));
                        }

                        Console.CancelKeyPress += ConsoleOnCancelKeyPress;
                        if (!UnsafeNativeMethods.GenerateConsoleCtrlEvent(ControlEvent.CtrlC, (uint)process.SessionId)) {
                            throw new ApplicationException(String.Format("GenerateConsoleCtrlEvent: {0}", Marshal.GetLastWin32Error()));
                        }
                        // Win8.1では↓でOKだがWin2008SVRでは↑でないとCtrl+Cが送られない
                        //                    if (!UnsafeNativeMethods.GenerateConsoleCtrlEvent(ControlEvent.CtrlC, (uint)process.Id)) {
                        //                        throw new ApplicationException(String.Format("GenerateConsoleCtrlEvent: {0}", Marshal.GetLastWin32Error()));
                        //                    }

                        if (!UnsafeNativeMethods.FreeConsole()) {
                            throw new ApplicationException(String.Format("FreeConsole: {0}", Marshal.GetLastWin32Error()));
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

                    File.Delete(GetErrorFilename(_processId.Value));
                }
                catch (ArgumentException) {
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

        private void ConsoleOnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
        }

        private string pidfilePath
        {
            get
            {
                return Path.Combine(Program.Param.AppHome, @"RUNNING_PID");
            }
        }
    }
}
