using System.Reflection;
using NDesk.Options;
using PlayService.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace PlayService
{
    static class Program
    {
        public static readonly ParamType Param = new ParamType();

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        public class ParamType
        {
            public bool Install { get; set; }
            public bool Uninstall { get; set; }

            public String ServiceName { get; set; }

            public String WorkDir { get; set; }
            public String ConfigFile { get; set; }

            public String AppName { get; set; }
            public String AppHome { get; set; }
            public String Env { get; set; }

            public String LogFile { get; set; }
            public String LogToConsole { get; set; }
            public bool ShowCallStack { get; set; }
            public String InstallStateDir { get; set; }

            public bool Help { get; set; }

            public override string ToString()
            {
                return this.ReflectionToString();
            }
        }

        private static readonly OptionSet OptionSet = new OptionSet
        {
          { "i|install",  v => Param.Install = v != null },
          { "u|uninstall",v => Param.Uninstall = v != null },

          { "ServiceName=",  v => Param.ServiceName = v },

          { "WorkDir=",  v => Param.WorkDir = v },
          { "ConfigFile|ConfigurationFile=",  v => Param.ConfigFile = v },
          { "AppName|ApplicationName=",  v => Param.AppName = v },
          { "AppHome|ApplicationHome=",  v => Param.AppHome = v },
          { "Env|Environment|EnvironmentVariables=",  v => Param.Env = v },

          { "LogFile=",       v => Param.LogFile = v },
          { "LogToConsole=",  v => Param.LogToConsole = v },
          { "ShowCallStack",   v => Param.ShowCallStack = v != null },
          { "InstallStateDir=",  v => Param.InstallStateDir = v },

          { "h|?|help",   v => Param.Help = v != null },
        };

#if DEBUG
        public static string DebugTextFile
        {
            get
            {
                var filename = "ServiceDebug.txt";
                if (Param.WorkDir != null)
                    return Path.Combine(Param.WorkDir, filename);
                return filename;
            }
        }
#endif

        /// <summary>
        /// アプリケーションのメイン エントリ ポイントです。
        /// </summary>
        private static void Main()
        {
            try
            {
                ParseArgs();
            } catch (ApplicationException ex) {
                Console.Error.WriteLine(ex.Message);
                Usage();
                return;
            }

#if DEBUG
            File.WriteAllText(Program.DebugTextFile, $@"");
#endif

            if (Param.Help) {
                Usage();
                return;
            }

            if (Param.Install && Param.Uninstall) {
                Usage();
                return;
            }

#if DEBUG
            Console.WriteLine(Param.ToString());
#endif

            if (Param.Install || Param.Uninstall) {
                //ThreadExceptionイベントハンドラを追加
                Thread.GetDomain().UnhandledException += Application_UnhandledException;
                InstallOrUninstall();
                return;
            }

            if (Param.WorkDir != null && !Directory.Exists(Param.WorkDir))
                throw new ApplicationException($"Directory not exists {Param.WorkDir}");
            if (Param.AppHome != null && !Directory.Exists(Param.AppHome))
                throw new ApplicationException($"Directory not exists {Param.AppHome}");

            FreeConsole();

            var servicesToRun = new ServiceBase[] 
            { 
                new PlayService() 
            };
            ServiceBase.Run(servicesToRun);
        }

        private static void InstallOrUninstall()
        {
            if (Param.Install) {
                // パラメーターチェック
                if (!string.IsNullOrEmpty(Param.WorkDir)) {
                    if (!Directory.Exists(Param.WorkDir))
                        throw new ApplicationException($"Directory not exists {Param.WorkDir}");
                    Param.WorkDir = Path.GetFullPath(Param.WorkDir);
                }

                if (!string.IsNullOrEmpty(Param.AppHome)) {
                    if (!Directory.Exists(Param.AppHome))
                        throw new ApplicationException($"Directory not exists {Param.AppHome}");
                    Param.AppHome = Path.GetFullPath(Param.AppHome);
                }

                if (string.IsNullOrEmpty(Param.WorkDir) && string.IsNullOrEmpty(Param.AppHome))
                    Param.WorkDir = Path.GetFullPath(@".\");
            }

            //installutil.exeのフルパスを取得
            string installutilPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "installutil.exe");
            if (!File.Exists(installutilPath)) {
                throw new ApplicationException("installutil.exeが見つかりませんでした。");
            }

            var installutilArgs = new List<string>();
            // installutilのオプション
            if (Param.Uninstall)
                installutilArgs.Add("/u");
            if (Param.LogFile != null)
                installutilArgs.Add("/LogFile=" + Param.LogFile.QuoteIfNecessary());
            if (Param.LogToConsole != null)
                installutilArgs.Add("/LogToConsole=" + Param.LogToConsole.QuoteIfNecessary());
            if (Param.ShowCallStack)
                installutilArgs.Add("/ShowCallStack");
            if (Param.InstallStateDir != null)
                installutilArgs.Add("/InstallStateDir=" + Param.InstallStateDir.QuoteIfNecessary());

            // サービスインストーラーのオプション
            if (Param.ServiceName != null)
                installutilArgs.Add("/ServiceName=" + Param.ServiceName.QuoteIfNecessary());

            // アセンブリのオプション
            if (Param.WorkDir != null)
                installutilArgs.Add("/WorkDir=" + Param.WorkDir.QuoteIfNecessary());
            if (Param.ConfigFile != null)
                installutilArgs.Add("/ConfigFile=" + Param.ConfigFile.QuoteIfNecessary());

            if (Param.AppHome != null)
                installutilArgs.Add("/AppHome=" + Param.AppHome.QuoteIfNecessary());
            if (Param.AppName != null)
                installutilArgs.Add("/AppName=" + Param.AppName.QuoteIfNecessary());
            if (Param.Env != null)
                installutilArgs.Add("/Env=" + Param.Env.Quote());

            // アセンブリ
            Assembly assembly = Assembly.GetEntryAssembly();
            Debug.Assert(assembly != null, nameof(assembly) + " != null");
            installutilArgs.Add(assembly.Location.QuoteIfNecessary());

            var installutilArg = String.Join(" ", installutilArgs);

            //installutil.exeを起動
            var psi = new ProcessStartInfo
            {
                FileName = installutilPath,
                Arguments = installutilArg,
                UseShellExecute = false
            };
            var p = Process.Start(psi);
            Debug.Assert(p != null, "p != null");
            p.WaitForExit();
            Environment.ExitCode = p.ExitCode;
        }

        private static void Application_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex) {
                Console.Error.WriteLine(ex.Message);
                //Console.Error.WriteLine("???");
                Environment.Exit(1);
            }
        }

        private static void ParseArgs()
        {
            var args = Environment.GetCommandLineArgs().Skip(1);

            var extra = OptionSet.Parse(args);
            if (extra.Count > 0)
                throw new ApplicationException($"Invalid Argument: {extra[0]}");
        }

        static void Usage()
        {
            Console.Error.WriteLine("Options:");
            OptionSet.WriteOptionDescriptions(Console.Error);
        }
    }
}
