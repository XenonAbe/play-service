using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace PlayService
{
    public partial class PlayService : ServiceBase
    {
        public PlayService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            var imagePathArgs = Environment.GetCommandLineArgs();



        }

        protected override void OnStop()
        {
        }
    }
}
