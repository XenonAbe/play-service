using Microsoft.Win32.SafeHandles;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using System.Threading;

namespace PlayService
{
    public partial class PlayService : ServiceBase
    {
        private const int StartTimeout = 60000;

        private int? _processId;
        private int? _parentProcessId;
        private ManualResetEvent _waitFileHandle;

        internal enum ControlEvent
        {
            CtrlC = 0,
            //CtrlBreak = 1
        }

        private const string Kernel32 = "kernel32.dll";

        internal static class NativeMethods
        {
            public delegate int ConHndlr(int signalType);
        }

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
                throw new ApplicationException(String.Format("This application is already running (Or delete {0} file)", pidfilePath));
            }

            RequestAdditionalTime(StartTimeout);

            var startBatch = Path.Combine(Program.Param.AppHome, @"bin\" + Program.Param.AppName + ".bat");
            var psi = new ProcessStartInfo
            {
                FileName = startBatch,
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Program.Param.AppHome
            };
            if (!File.Exists(startBatch))
                throw new ApplicationException(String.Format("not found {0}", startBatch));

            if (Program.Param.Env != null) {
                var envs = Program.Param.Env.Split(new[] {','});
                foreach (var env in envs) {
                    var keyValue = env.Split(new[] {'='}, 2);
                    if (psi.EnvironmentVariables.ContainsKey(keyValue[0])) {
                        psi.EnvironmentVariables.Remove(keyValue[0]);
                    }
                    var value = keyValue.Length > 1 ? keyValue[1] : "";
                    psi.EnvironmentVariables.Add(keyValue[0], value);
                }
            }

            using (_waitFileHandle = new ManualResetEvent(false)) {
                var watcher = new FileSystemWatcher();
                watcher.Path = Path.GetDirectoryName(pidfilePath) + @"\";
                watcher.Filter = Path.GetFileName(pidfilePath);
                watcher.NotifyFilter = NotifyFilters.LastWrite;
                watcher.Changed += OnPidfileChanged;
                watcher.EnableRaisingEvents = true;

                using (var process = Process.Start(psi)) {
                    Debug.Assert(process != null, "process != null");
                    var waitProcessHandle = new ManualResetEvent(true)
                    {
                        SafeWaitHandle = new SafeWaitHandle(process.Handle, false)
                    };
                    var index = WaitHandle.WaitAny(new WaitHandle[] {_waitFileHandle, waitProcessHandle}, StartTimeout - 5000);
                    if (index == WaitHandle.WaitTimeout)
                        throw new ApplicationException("Timeout");

                    if (index == 1) {
                        // cmd.exeの終了 → Application開始失敗
                        throw new ApplicationException("Application Start Failed");
                    }

                    if (!File.Exists(pidfilePath)) {
                        throw new ApplicationException("Pidfile is not created");
                    }
                    watcher.EnableRaisingEvents = false;

                    //Thread.Sleep(500);

                    _parentProcessId = process.Id;

                    for (int i = 0; i < 10; i++) {
                        Thread.Sleep(100);

                        try {
                            using (var reader = new StreamReader(pidfilePath)) {
                                var line = reader.ReadLine();
                                Debug.Assert(line != null, "line != null");
                                _processId = Int32.Parse(line);
                            }
                        } catch (IOException) {
                            // タイミングによりpidfileがアクセス不可の場合があるので何度か繰り返す
                            continue;
                        }
                        break;
                    }
                }

                EventLog.WriteEntry(String.Format("Play server process ID is {0}", _processId));
            }
            ExitCode = 0;
        }

        private void OnPidfileChanged(object sender, FileSystemEventArgs e)
        {
            _waitFileHandle.Set();
        }

        protected override void OnStop()
        {
            if (!_processId.HasValue) {
                ExitCode = 1;
                return;
            }

            // Play (java.exe)の終了
            try {
                using (var process = Process.GetProcessById(_processId.Value)) {
                    if (process.HasExited) {
                        ExitCode = 3;
                        return;
                    }

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
            } finally {
                _processId = null;
            }

            EventLog.WriteEntry("Exit");

            // バッチ呼び出しcmd.exeの終了
            if (!_parentProcessId.HasValue) {
                return;
            }
            try {
                using (var parentProcess = Process.GetProcessById(_parentProcessId.Value)) {
                    if (parentProcess.HasExited) {
                        return;
                    }

                    parentProcess.Kill();
                }
            } catch (ArgumentException) {
                return;
            } finally {
                _parentProcessId = null;
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
