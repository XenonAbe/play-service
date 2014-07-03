﻿using PlayService.Extensions;
using System;
using System.ComponentModel;
using System.Configuration.Install;

namespace PlayService
{
    [RunInstaller(true)]
    public partial class ProjectInstaller : Installer
    {
        public ProjectInstaller()
        {
            InitializeComponent();
            Console.WriteLine(Environment.CommandLine);
        }

        private void ProjectInstaller_BeforeInstall(object sender, InstallEventArgs e)
        {
            SetParameters();
        }

        private void ProjectInstaller_BeforeUninstall(object sender, InstallEventArgs e)
        {
            SetParameters();
        }

        private void SetParameters()
        {
            if (!String.IsNullOrEmpty(Context.Parameters["ServiceName"]))
                serviceInstaller.ServiceName = Context.Parameters["ServiceName"];

            var assemblyPath = Context.Parameters["assemblypath"];
            if (!String.IsNullOrEmpty(Context.Parameters["ServiceName"]))
                assemblyPath += " /ServiceName=" + Context.Parameters["ServiceName"].QuoteAsNeeded();
            if (!String.IsNullOrEmpty(Context.Parameters["AppName"]))
                assemblyPath += " /AppName=" + Context.Parameters["AppName"].QuoteAsNeeded();
            if (!String.IsNullOrEmpty(Context.Parameters["AppHome"]))
                assemblyPath += " /AppHome=" + Context.Parameters["AppHome"].QuoteAsNeeded();
            if (!String.IsNullOrEmpty(Context.Parameters["Env"]))
                assemblyPath += " /Env=" + Context.Parameters["Env"].Quote();
            Context.Parameters["assemblypath"] = assemblyPath;
        }

    }
}