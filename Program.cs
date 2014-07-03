﻿using System.Reflection;
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

          { "AppName|ApplicationName=",  v => Param.AppName = v },
          { "AppHome|ApplicationHome=",  v => Param.AppHome = v },
          { "Env|Environment|EnvironmentVariables=",  v => Param.Env = v },

          { "LogFile=",       v => Param.LogFile = v },
          { "LogToConsole=",  v => Param.LogToConsole = v },
          { "ShowCallStack",   v => Param.ShowCallStack = v != null },
          { "InstallStateDir=",  v => Param.InstallStateDir = v },

          { "h|?|help",   v => Param.Help = v != null },
        };

        /// <summary>
        /// アプリケーションのメイン エントリ ポイントです。
        /// </summary>
        private static void Main()
        {
            try {
                ParseArgs();
            } catch (ApplicationException ex) {
                Console.Error.WriteLine(ex.Message);
                Usage();
                return;
            }

            if (Param.Help) {
                Usage();
                return;
            }

            if (Param.Install && Param.Uninstall) {
                Usage();
                return;
            }

            if (!Param.Uninstall && (String.IsNullOrEmpty(Param.AppName) || String.IsNullOrEmpty(Param.AppHome))) {
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
                if (!Directory.Exists(Param.AppHome)) {
                    throw new ApplicationException(String.Format("Directory not exists {0}", Param.AppHome));
                }
                Param.AppHome = Path.GetFullPath(Param.AppHome);
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
                installutilArgs.Add("/LogFile=" + Param.LogFile.QuoteAsNeeded());
            if (Param.LogToConsole != null)
                installutilArgs.Add("/LogToConsole=" + Param.LogToConsole.QuoteAsNeeded());
            if (Param.ShowCallStack)
                installutilArgs.Add("/ShowCallStack");
            if (Param.InstallStateDir != null)
                installutilArgs.Add("/InstallStateDir=" + Param.InstallStateDir.QuoteAsNeeded());

            // サービスインストーラーのオプション
            if (Param.ServiceName != null)
                installutilArgs.Add("/ServiceName=" + Param.ServiceName.QuoteAsNeeded());

            // アセンブリのオプション
            if (Param.AppName != null)
                installutilArgs.Add("/AppName=" + Param.AppName.QuoteAsNeeded());
            if (Param.AppHome != null)
                installutilArgs.Add("/AppHome=" + Param.AppHome.QuoteAsNeeded());
            if (Param.Env != null)
                installutilArgs.Add("/Env=" + Param.Env.Quote());

            // アセンブリ
            Assembly assembly = Assembly.GetEntryAssembly();
            installutilArgs.Add(assembly.Location.QuoteAsNeeded());

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
            var ex = e.ExceptionObject as Exception;
            if (ex != null) {
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
                throw new ApplicationException(String.Format("Invalid Argument: {0}", extra[0]));
        }

        static void Usage()
        {
            Console.Error.WriteLine("Options:");
            OptionSet.WriteOptionDescriptions(Console.Error);
        }
    }
}
