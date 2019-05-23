using System;
using System.ServiceProcess;

namespace esri.Service
{
    public static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        public static void Main()
        {
            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[] 
            { 
                new FeatureLayerUpdater()
            };

            ServiceBase.Run(ServicesToRun);
        }
    }
}
