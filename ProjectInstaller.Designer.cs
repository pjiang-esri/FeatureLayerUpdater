namespace esri.Service
{
    partial class ProjectInstaller
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.featureUpdaterServiceProcessInstaller = new System.ServiceProcess.ServiceProcessInstaller();
            this.featureUpdaterServiceInstaller = new System.ServiceProcess.ServiceInstaller();
            // 
            // featureUpdaterServiceProcessInstaller
            // 
            this.featureUpdaterServiceProcessInstaller.Account = System.ServiceProcess.ServiceAccount.LocalSystem;
            this.featureUpdaterServiceProcessInstaller.Password = null;
            this.featureUpdaterServiceProcessInstaller.Username = null;
            // 
            // featureUpdaterServiceInstaller
            // 
            this.featureUpdaterServiceInstaller.Description = "Update ArcGIS hosted feature layer dynamically with data from a table in a Databa" +
    "se";
            this.featureUpdaterServiceInstaller.DisplayName = "ArcGIS Feature Layer Updater";
            this.featureUpdaterServiceInstaller.ServiceName = "ArcGISFeatureLayerUpdater";
            this.featureUpdaterServiceInstaller.StartType = System.ServiceProcess.ServiceStartMode.Automatic;
            // 
            // ProjectInstaller
            // 
            this.Installers.AddRange(new System.Configuration.Install.Installer[] {
            this.featureUpdaterServiceProcessInstaller,
            this.featureUpdaterServiceInstaller});

        }

        #endregion

        private System.ServiceProcess.ServiceProcessInstaller featureUpdaterServiceProcessInstaller;
        private System.ServiceProcess.ServiceInstaller featureUpdaterServiceInstaller;
    }
}