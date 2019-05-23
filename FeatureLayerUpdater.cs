using System;
using System.IO;
using System.Web;
using System.Net;
using System.Text;
using System.Linq;
using System.Data;
using System.Data.Odbc;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.ServiceProcess;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;

namespace esri.Service
{
    public delegate void EventLogCallback(string message, EventLogEntryType msgType);

    public partial class FeatureLayerUpdater : ServiceBase
    {
        List<Thread> updatingThreads = new List<Thread>();
  
        public FeatureLayerUpdater()
        {
            InitializeComponent();

            if (!System.Diagnostics.EventLog.SourceExists("ESRI Feature Layer Updater"))
            {
                System.Diagnostics.EventLog.CreateEventSource("ESRI Feature Layer Updater", "ArcGIS Feature Layer Updater");
            }

            esriFLU_EventLog.Source = "ESRI Feature Layer Updater";
            esriFLU_EventLog.Log = "ArcGIS Feature Layer Updater";
        }

        public void LogMessageCallback(string message, EventLogEntryType msgType)
        {
            esriFLU_EventLog.WriteEntry(string.Format("Feature Layer Updater: {0}", message), msgType);
        }

        protected override void OnStart(string[] args)
        {
            ConfigurationManager.RefreshSection("agolFeatureLayerConfig");
            string connectionString = ConfigurationManager.ConnectionStrings["ODBC_ConnectionString"].ConnectionString;
            string tokenUsername = ConfigurationManager.AppSettings.Get("Token_Username");
            string tokenPassword = ConfigurationManager.AppSettings.Get("Token_Password");

            if (string.IsNullOrWhiteSpace(tokenUsername)) { esriFLU_EventLog.WriteEntry("Username of the user permitted to edit the feature layer is not configured"); return; }
            if (string.IsNullOrWhiteSpace(tokenPassword)) { esriFLU_EventLog.WriteEntry("Password of the user permitted to edit the feature layer is not configured"); return; }

            try
            {
                AgolFeatureLayerConfiguration layerConfiguration = ConfigurationManager.GetSection("agolFeatureLayerConfig") as AgolFeatureLayerConfiguration;
                if (layerConfiguration == null || layerConfiguration.FeatureLayers == null) { esriFLU_EventLog.WriteEntry("ArcGIS hosted feature layers section is not configured properly"); return; }
                AgolFeatureLayer[] featureLayers = new AgolFeatureLayer[layerConfiguration.FeatureLayers.Count];
                layerConfiguration.FeatureLayers.CopyTo(featureLayers, 0);
                EditorStateManager.layerEditors.Clear();
                updatingThreads.Clear();

                //Thread.Sleep(30000);
                for (int i = 0; i < featureLayers.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(featureLayers[i].AgolAdminUrl)) { esriFLU_EventLog.WriteEntry("ArcGIS Online Hosted Feature Service Admin Url is not configured"); break; }
                    if (string.IsNullOrWhiteSpace(featureLayers[i].AgolLayerUrl)) { esriFLU_EventLog.WriteEntry("ArcGIS Online Hosted Feature Service Url is not configured"); break; }

                    LayerUpdatingRunner updatingRunner = new LayerUpdatingRunner(tokenUsername, tokenPassword, featureLayers[i], connectionString, LogMessageCallback);
                    updatingRunner.IsSharingService = featureLayers.LongCount(layer => (layer.AgolAdminUrl.Equals(featureLayers[i].AgolAdminUrl, StringComparison.CurrentCultureIgnoreCase))) > 1;
                    EditorStateManager.layerEditors.Add(updatingRunner);

                    Thread updatingThread = new Thread(new ThreadStart(updatingRunner.StartUpdating));
                    updatingThread.Name = featureLayers[i].Name;
                    updatingThread.IsBackground = true;
                    updatingThreads.Add(updatingThread);
                }

                updatingThreads.ForEach(thread => { thread.Start(); });
            }
            catch (Exception ex)
            {
                esriFLU_EventLog.WriteEntry("ArcGIS Hosted Feature Layer Configuration Error: " + ex.Message, EventLogEntryType.Error);
            }
        }

        protected override void OnStop()
        {
            EditorStateManager.layerEditors.Clear();

            if (updatingThreads.Count > 0) updatingThreads.ForEach(thread =>
            {
                if (thread != null && thread.IsAlive)
                {
                    thread.Abort();
                    thread.Join();
                }
            });

            updatingThreads.Clear();
            esriFLU_EventLog.WriteEntry("Service Stopped - Updating Threads Aborted", EventLogEntryType.Warning);
        }
    }

    #region Class - Actual doer of running updating tasks
    public class LayerUpdatingRunner
    {
        [DefaultValue(false)]
        public bool IsEditing { get; set; }

        [DefaultValue(false)]
        public bool IsSharingService { get; set; }

        public string TokenUsername { get; set; }
        public string TokenPassword { get; set; }
        public string ConnectionString { get; set; }
        public AgolFeatureLayer FeatureLayer { get; set; }
        public FeatureLayerUpdateMode UpdateMode { get; private set; }

        private static bool isTokenReady = false;
        private static AgolToken agolToken; // All threads share a token
        private static object realLocker = new object();

        private int numTries = 0;
        private bool isLoaded = false;
        private bool isErrorLogged = false;
        private EventLogCallback logCallback;
        private List<SpatialObject> loadedRows = null;
        private object fakeLocker = new object();

        private const int maxTries = 4;

        public LayerUpdatingRunner(string username, string password, AgolFeatureLayer featureLayer, string connectionString, EventLogCallback callback)
        {
            this.TokenUsername = username;
            this.TokenPassword = password;
            this.FeatureLayer = featureLayer;
            this.ConnectionString = connectionString;
            this.logCallback = callback;
            this.UpdateMode = (FeatureLayerUpdateMode)Enum.Parse(typeof(FeatureLayerUpdateMode), featureLayer.UpdateMode);
        }

        public void StartUpdating()
        {
            while (true)
            {
                if (isErrorLogged) break;

                if (logCallback != null)
                {
                    logCallback("Start Updating " + this.FeatureLayer.Name, EventLogEntryType.Information);
                }

                if (GenerateToken())
                {
                    RunEnableEditingTask();
                }

                Thread.Sleep(this.FeatureLayer.UpdateRate * 1000);
            }

            if (isErrorLogged) OnEditingError();
        }

        private void OnEditingError()
        {
            this.IsEditing = false;
            EditorStateManager.RemoveEditor(this);
            if (logCallback != null) logCallback(string.Format("{0} updating thread is aborting. Left living threads: {1}", Thread.CurrentThread.Name, EditorStateManager.layerEditors.Count), EventLogEntryType.Warning);
            Thread.CurrentThread.Join();
        }

        private void RunEnableEditingTask()
        {
            Task<bool> enableEditingTask = new Task<bool>(() => { return ChangeServiceCapability(true); });
            enableEditingTask.ContinueWith(antecedent =>
            {
                if (antecedent.Result) RunLayerEditingTask(this.isLoaded);
            });

            enableEditingTask.Start();
        }

        private void RunLayerEditingTask(bool isCleared)
        {
            if (isCleared)
            {
                Task<bool> updatingTask = new Task<bool>(() => { return LoadTableRows(); });
                updatingTask.ContinueWith(antecedent =>
                {
                    ChangeServiceCapability(false);
                });

                updatingTask.Start();
            }
            else
            {
                Task<bool> deletingTask = new Task<bool>(() => { return ClearHostedFeatureLayer(); });
                deletingTask.ContinueWith(antecedent =>
                {
                    if (antecedent.Result) RunLayerEditingTask(true);
                    else ChangeServiceCapability(false);
                });

                deletingTask.Start();
            }
        }

        private HttpWebRequest CreateHttpRequest(string url, string jsonParams)
        {
            byte[] jsonBytes = UTF8Encoding.UTF8.GetBytes(jsonParams);

            HttpWebRequest agolRequest = (HttpWebRequest)HttpWebRequest.Create(new Uri(url));
            agolRequest.ContentType = "application/x-www-form-urlencoded";
            agolRequest.ContentLength = jsonBytes.Length;
            agolRequest.Method = "POST";

            using (Stream stream = agolRequest.GetRequestStream())
            {
                stream.Write(jsonBytes, 0, jsonBytes.Length);
            }

            return agolRequest;
        }

        private bool GenerateToken()
        {
            lock (realLocker)
            {
                long msRun = (DateTime.UtcNow.Ticks - new DateTime(1970, 1, 1).Ticks) / 10000;
                isTokenReady = (AgolToken.staticToken != null) && (AgolToken.staticExpires - msRun > 300000); // expiring in 5 minutes;
                if (isTokenReady) return true;

                string jsonData = string.Format("f=json&username={0}&password={1}&expiration=1440&referer=http://127.0.0.1", this.TokenUsername, this.TokenPassword);
                HttpWebRequest agolRequest = CreateHttpRequest("https://www.arcgis.com/sharing/rest/generateToken", jsonData);

                using (HttpWebResponse response = (HttpWebResponse)agolRequest.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AgolToken));
                        agolToken = (AgolToken)serializer.ReadObject(responseStream);

                        if (agolToken.token != null)
                        {
                            isTokenReady = true;
                            if (logCallback != null) logCallback("Token Created: " + agolToken.token, EventLogEntryType.Information);
                        }
                        else
                        {
                            isTokenReady = false;
                            isErrorLogged = true;
                            if (logCallback != null) logCallback(string.Format("Create Token Error - {0}. Thread aborted.", (agolToken.error != null) ? agolToken.error.message : "Unknown error"), EventLogEntryType.Error);
                        }
                    }
                }

                return isTokenReady;
            }
        }

        private bool ChangeServiceCapability(bool enableEditing)
        {
            lock ((this.IsSharingService) ? realLocker : fakeLocker)
            {
                bool isChanged = false;
                this.IsEditing = enableEditing;
                if (this.IsSharingService && EditorStateManager.IsAnyEditing(this)) return true;

                string capabilities = (enableEditing) ? "Create,Delete,Query,Update,Editing" : "Query";
                string jsonData = string.Format("f=json&updateDefinition={{\"capabilities\":\"{0}\"}}&async=false&token={1}", capabilities, AgolToken.staticToken);
                string url = this.FeatureLayer.AgolAdminUrl + (this.FeatureLayer.AgolAdminUrl.EndsWith("/") ? "updateDefinition" : "/updateDefinition");
                HttpWebRequest agolRequest = CreateHttpRequest(url, jsonData);

                using (HttpWebResponse response = (HttpWebResponse)agolRequest.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AgolAdminResponse));
                        AgolAdminResponse adminResponse = (AgolAdminResponse)serializer.ReadObject(responseStream);

                        if (adminResponse.success)
                        {
                            isChanged = true;
                            if (logCallback != null) logCallback(string.Format("Update {0} Feature Layer Definition to \"{1}\"", this.FeatureLayer.Name, capabilities), EventLogEntryType.Information);
                        }
                        else
                        {
                            isChanged = false;
                            this.IsEditing = false;
                            this.isErrorLogged = true;
                            if (logCallback != null) logCallback(string.Format("Update {0} Feature Layer Definition Error - {1}", this.FeatureLayer.Name, (adminResponse.error != null) ? (adminResponse.error.message + "; " + string.Join(", ", adminResponse.error.details)) : "Unknown error"), EventLogEntryType.Error);
                        }
                    }
                }

                return isChanged;
            }
        }

        protected bool ClearHostedFeatureLayer()
        {
            bool isDone = false;

            string jsonData = "f=json&where=1=1&rollbackOnFailure=false";
            string url = this.FeatureLayer.AgolLayerUrl + (this.FeatureLayer.AgolLayerUrl.EndsWith("/") ? "deleteFeatures" : "/deleteFeatures");
            HttpWebRequest agolRequest = CreateHttpRequest(url, jsonData);

            using (HttpWebResponse response = (HttpWebResponse)agolRequest.GetResponse())
            {
                using (Stream responseStream = response.GetResponseStream())
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FeatureLayerEditResults));
                    FeatureLayerEditResults editResults = (FeatureLayerEditResults)serializer.ReadObject(responseStream);
                    if (editResults.error != null)
                    {
                        if (numTries < maxTries && editResults.error.details[0].IndexOf("operation is not supported") > -1)
                        {
                            numTries++;
                            RunEnableEditingTask();
                        }
                        else
                        {
                            numTries = 0;
                            isErrorLogged = true;
                            if (logCallback != null) logCallback(string.Format("Delete {0} Features Error - {1}, {2}", this.FeatureLayer.Name, editResults.error.message, string.Join(",", editResults.error.details)), EventLogEntryType.Error);
                        }
                    }
                    else
                    {
                        PostEditProcess(editResults.deleteResults, null, "Delete");
                    }

                    isDone = true;
                }
            }

            return isDone;
        }

        protected bool LoadTableRows()
        {
            bool isDone = false;

            List<SpatialObject> addedRows = new List<SpatialObject>();
            List<SpatialObject> updatedRows = new List<SpatialObject>();
            List<SpatialObject> deletedRows = new List<SpatialObject>();
            List<SpatialObject> currentRows = new List<SpatialObject>();

            using (OdbcConnection connection = new OdbcConnection(this.ConnectionString))
            {
                using (OdbcCommand command = new OdbcCommand(this.FeatureLayer.SelectSQL, connection))
                {
                    try
                    {
                        connection.Open();
                        using (OdbcDataReader dr = command.ExecuteReader())
                        {
                            if (dr.HasRows)
                            {
                                int count = 0;
                                while (dr.Read())
                                {
                                    count++;
                                    SpatialObject obj = new SpatialObject();
                                    obj.uniqueId = ((int)this.UpdateMode == 4) ? Convert.ToString(dr.GetValue(0)) : count.ToString();
                                    obj.x = ((int)this.UpdateMode < 3) ? Convert.ToDecimal(dr.GetValue(1)) : -1;
                                    obj.y = ((int)this.UpdateMode < 3) ? Convert.ToDecimal(dr.GetValue(2)) : -1;
                                    obj.fieldValues = new FieldValueList();
          
                                    for (int i = 0; i < dr.FieldCount; i++)
                                    {
                                        obj.fieldValues.Add(dr.GetName(i), dr.GetFieldType(i), dr.GetValue(i));
                                    }

                                    currentRows.Add(obj);
                                }
                            }

                            if (isLoaded && loadedRows != null)
                            {
                                currentRows.ForEach(current =>
                                {
                                    SpatialObject loaded = loadedRows.FirstOrDefault(existed => existed.uniqueId == current.uniqueId);
                                    if (loaded == null)
                                    {
                                        addedRows.Add(current);
                                        loadedRows.Add(current);
                                    }
                                    else if (!loaded.Equals(current, this.UpdateMode, true))
                                    {
                                        updatedRows.Add(loaded);
                                    }
                                });

                                deletedRows = loadedRows.Where(existed => !currentRows.Any(current => existed.uniqueId == current.uniqueId)).ToList();
                                deletedRows.ForEach(item => loadedRows.Remove(item));
                                isDone = ApplyEdits(addedRows, updatedRows, deletedRows);
                            }
                            else
                            {
                                this.isLoaded = true;
                                loadedRows = currentRows.ToList();
                                isDone = ApplyEdits(currentRows, null, null);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        isDone = true;
                        isErrorLogged = true;
                        if (logCallback != null) logCallback(string.Format("Query {0} Table Error - {1}", this.FeatureLayer.Name, ex.Message), EventLogEntryType.Error);
                    }

                    if (connection.State == System.Data.ConnectionState.Open) connection.Close();
                }
            }

            return isDone;
        }

        protected bool ApplyEdits(List<SpatialObject> adds, List<SpatialObject> updates, List<SpatialObject> deletes)
        {
            bool isDone = false;

            try
            {
                string jsonAdds = BuildFeatuesJson(adds, this.FeatureLayer.SrWKID, "add");
                string jsonUpdates = BuildFeatuesJson(updates, this.FeatureLayer.SrWKID, "update");
                string jsonDeletes = BuildFeatuesJson(deletes, this.FeatureLayer.SrWKID, "delete");

                string jsonData = string.Format("f=json&adds={0}&updates={1}&deletes={2}&rollbackOnFailure=true", jsonAdds, jsonUpdates, jsonDeletes);
                string url = this.FeatureLayer.AgolLayerUrl + (this.FeatureLayer.AgolLayerUrl.EndsWith("/") ? "applyEdits" : "/applyEdits");
                HttpWebRequest agolRequest = CreateHttpRequest(url, jsonData);

                using (HttpWebResponse response = (HttpWebResponse)agolRequest.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FeatureLayerEditResults));
                        FeatureLayerEditResults editResults = (FeatureLayerEditResults)serializer.ReadObject(responseStream);

                        if (editResults != null)
                        {
                            if (editResults.error != null)
                            {
                                if (numTries < maxTries && editResults.error.details[0].IndexOf("operation is not supported") > -1)
                                {
                                    numTries++;
                                    RunEnableEditingTask();
                                }
                                else
                                {
                                    numTries = 0;
                                    isErrorLogged = true;
                                    if (logCallback != null) logCallback(string.Format("Editing {0} Feature Layer Error - {1}, {2}", this.FeatureLayer.Name, editResults.error.message, string.Join(",", editResults.error.details)), EventLogEntryType.Error);
                                }
                            }
                            else
                            {
                                PostEditProcess(editResults.addResults, adds, "Add");
                                PostEditProcess(editResults.updateResults, null, "Update");
                                PostEditProcess(editResults.deleteResults, null, "Delete");
                            }
                        }

                        isDone = true;
                    }
                }
            }
            catch (Exception ex)
            {
                isDone = true;
                isErrorLogged = true;
                if (logCallback != null) logCallback(string.Format("JSON Formatting {0} Features Error: {1}", this.FeatureLayer.Name, ex.Message), EventLogEntryType.Error);
            }

            return isDone;
        }

        private void PostEditProcess(FeatureLayerEditResult[] editResults, List<SpatialObject> adds, string action)
        {
            if (editResults != null && editResults.Length > 0)
            {
                int countFailed = 0;
                int countSuccess = 0;
                string errFailed = "";

                for (int i = 0; i < editResults.Length; i++)
                {
                    if (Boolean.Parse(editResults[i].success))
                    {
                        countSuccess++;

                        if (action.Equals("Add"))
                        {
                            adds[i].objectId = editResults[i].objectId;
                            adds[i].globalId = editResults[i].globalId;
                            SpatialObject feature = loadedRows.SingleOrDefault(row => row.uniqueId == adds[i].uniqueId);

                            if (feature != null)
                            {
                                feature.objectId = adds[i].objectId;
                                feature.globalId = adds[i].globalId;
                            }
                        }
                    }
                    else if (editResults[i].error != null)
                    {
                        countFailed++;
                        errFailed = editResults[i].error.description;
                    }
                }

                if (logCallback != null) logCallback(string.Format("Succeeded to {0} {1} {2} feature(s)", action, countSuccess, this.FeatureLayer.Name), EventLogEntryType.Information);
                if (logCallback != null && countFailed > 0) logCallback(string.Format("Failed to {0} {1} {2} feature(s) - {3}", action, countFailed, this.FeatureLayer.Name, errFailed), EventLogEntryType.Error);
            }
        }

        private string BuildFeatuesJson(List<SpatialObject> rows, int srWkid, string action)
        {
            StringBuilder jsonBuilder = new StringBuilder("");

            if (rows != null)
            {
                if (action.Equals("delete"))
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (i > 0) jsonBuilder.Append(",");
                        jsonBuilder.Append(rows[i].objectId);
                    }
                }
                else
                {
                    bool isUpdating = action.Equals("update");
                    bool hasGeometry = ((int)this.UpdateMode < 2) || (action.Equals("add") && (int)this.UpdateMode == 2);

                    if (hasGeometry)
                    {
                        jsonBuilder.Append("[");
                        for (int i = 0; i < rows.Count; i++)
                        {
                            if (i > 0) jsonBuilder.Append(",");
                            jsonBuilder.Append("{"); // object begin
                            jsonBuilder.Append(string.Format("geometry:{{x:{0},y:{1},spatialReference:{{wkid:{2}}}}},", rows[i].x, rows[i].y, srWkid));
                            BuildAttributesJson(jsonBuilder, rows[i].fieldValues, rows[i].objectId, isUpdating);
                            jsonBuilder.Append("}"); // object end
                        }
                        jsonBuilder.Append("]");
                    }
                    else
                    {
                        jsonBuilder.Append("[");
                        for (int i = 0; i < rows.Count; i++)
                        {
                            if (i > 0) jsonBuilder.Append(",");
                            jsonBuilder.Append("{"); // object begin
                            BuildAttributesJson(jsonBuilder, rows[i].fieldValues, rows[i].objectId, isUpdating);
                            jsonBuilder.Append("}"); // object end
                        }
                        jsonBuilder.Append("]");
                    }
                }
            }

            return jsonBuilder.ToString();
        }

        private void BuildAttributesJson(StringBuilder jsonBuilder, FieldValueList fieldValues, long objectId, bool isUpdating)
        {
            FieldValue fv = null;
            jsonBuilder.Append("attributes:{");

            if (isUpdating)
            {
                jsonBuilder.Append(string.Format("OBJECTID:{0},", objectId));
                if (this.UpdateMode == FeatureLayerUpdateMode.GeometryOnly)
                {
                    jsonBuilder.Append(string.Format("LAST_UPDATED:\"{0:G}\"}}", DateTime.Now));
                    return;
                }
            }

            for (int j = 0; j < fieldValues.Count; j++)
            {
                fv = fieldValues[j];
                jsonBuilder.Append(fv.name);
                jsonBuilder.Append(":");
                jsonBuilder.Append((fv.type == typeof(string) || fv.type == typeof(DateTime)) ? ("\"" + fv.value + "\"") : fv.value);
                jsonBuilder.Append(",");
            }

            jsonBuilder.Append(string.Format("LAST_UPDATED:\"{0:G}\"}}", DateTime.Now));
        }
    }
    #endregion

    #region AgolFeatureLayer Configuration Section Classes
    public class AgolFeatureLayerConfiguration : ConfigurationSection
    {
        [ConfigurationProperty("featureLayers", IsRequired = true, IsKey = false, IsDefaultCollection = true)]
        public AgolFeatureLayerCollection FeatureLayers
        {
            get { return ((AgolFeatureLayerCollection)(this["featureLayers"])); }
            set { this["featureLayers"] = value; }
        }
    }

    [ConfigurationCollection(typeof(AgolFeatureLayer), AddItemName = "add", RemoveItemName = "remove", CollectionType = ConfigurationElementCollectionType.AddRemoveClearMap)]
    public class AgolFeatureLayerCollection : ConfigurationElementCollection
    {
        public override ConfigurationElementCollectionType CollectionType
        {
            get { return ConfigurationElementCollectionType.AddRemoveClearMap; }
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return ((AgolFeatureLayer)element).Name;
        }

        protected override ConfigurationElement CreateNewElement()
        {
            return new AgolFeatureLayer();
        }

        public override bool IsReadOnly()
        {
            return false;
        }
    }

    public class AgolFeatureLayer : ConfigurationElement
    {
        [ConfigurationProperty("name", IsRequired = true, IsKey = true)]
        public string Name { get { return (string)this["name"]; } set { this["name"] = value; } }

        [ConfigurationProperty("srWKID", IsRequired = false, DefaultValue = 4326)]
        public int SrWKID { get { return (int)this["srWKID"]; } set { this["srWKID"] = value; } }

        [ConfigurationProperty("updateRate", IsRequired = true, DefaultValue = 300)]
        public int UpdateRate { get { return (int)this["updateRate"]; } set { this["updateRate"] = value; } }

        [ConfigurationProperty("updateMode", IsRequired = false, DefaultValue = "GeometryOnly")]
        public string UpdateMode { get { return (string)this["updateMode"]; } set { this["updateMode"] = value; } }

        [ConfigurationProperty("agolAdminUrl", IsRequired = true)]
        public string AgolAdminUrl { get { return (string)this["agolAdminUrl"]; } set { this["agolAdminUrl"] = value; } }

        [ConfigurationProperty("agolLayerUrl", IsRequired = true)]
        public string AgolLayerUrl { get { return (string)this["agolLayerUrl"]; } set { this["agolLayerUrl"] = value; } }

        [ConfigurationProperty("selectSQL", IsRequired = true)]
        public string SelectSQL { get { return (string)this["selectSQL"]; } set { this["selectSQL"] = value; } }
    }

    public enum FeatureLayerUpdateMode
    {
        GeometryOnly = 0,
        GeometryAndAttributes = 1,
        AttributesOnly = 2,
        NoGeometry = 3,
        NoGeometryAndID = 4
    }
    #endregion

    #region JSON Contract Classes for ArcGIS Portal REST Responses
    [DataContract]
    public class AgolToken
    {
        private string _token = null;
        private long _expires = 0;

        public static string staticToken { get; set; }
        public static long staticExpires { get; set; }

        [DataMember]
        public string token
        {
            get { return _token; }
            set { _token = value; AgolToken.staticToken = value; }
        }

        [DataMember]
        public long expires
        {
            get { return _expires; }
            set { _expires = value; AgolToken.staticExpires = value; }
        }

        [DataMember]
        public bool ssl { get; set; }

        [DataMember]
        public AgolRequestError error { get; set; }
    }

    [DataContract]
    public class FeatureLayerEditResults
    {
        [DataMember]
        public FeatureLayerEditResult[] addResults { get; set; }

        [DataMember]
        public FeatureLayerEditResult[] updateResults { get; set; }

        [DataMember]
        public FeatureLayerEditResult[] deleteResults { get; set; }

        [DataMember]
        public AgolRequestError error { get; set; }
    }

    [DataContract]
    public class FeatureLayerEditResult
    {
        [DataMember]
        public int objectId { get; set; }

        [DataMember]
        public string globalId { get; set; }

        [DataMember]
        public string success { get; set; }

        [DataMember]
        public FeatureEditError error { get; set; }
    }

    [DataContract]
    public class FeatureEditError
    {
        [DataMember]
        public int code { get; set; }

        [DataMember]
        public string description { get; set; }
    }

    [DataContract]
    public class AgolAdminResponse
    {
        [DataMember]
        public bool success { get; set; }

        [DataMember]
        public AgolRequestError error { get; set; }
    }

    [DataContract]
    public class AgolRequestError
    {
        [DataMember]
        public int code { get; set; }

        [DataMember]
        public string message { get; set; }

        [DataMember]
        public string[] details { get; set; }
    }
    #endregion

    #region Utility Classes to Store Spatial Data from Database
    public class SpatialObject : IEquatable<SpatialObject>
    {
        public int objectId { get; set; }
        public string uniqueId { get; set; }
        public string globalId { get; set; }
        public decimal x { get; set; }
        public decimal y { get; set; }
        public FieldValueList fieldValues { get; set; }

        public bool Equals(SpatialObject obj)
        {
            //ObjectID is not compared
            return this.x == obj.x && this.y == obj.y && this.fieldValues.Equals(obj.fieldValues);
        }

        public bool Equals(SpatialObject obj, FeatureLayerUpdateMode mode, bool updateWith)
        {
            bool isEqual = true;

            if (mode == FeatureLayerUpdateMode.GeometryOnly)
            {
                isEqual = this.x == obj.x && this.y == obj.y;
            }
            else if (mode == FeatureLayerUpdateMode.GeometryAndAttributes)
            {
                isEqual = this.x == obj.x && this.y == obj.y && this.fieldValues.Equals(obj.fieldValues);
            }
            else if ((int)mode > 1) // AttributesOnly || NoGeometry || NoGeometryAndID
            {
                isEqual = this.fieldValues.Equals(obj.fieldValues);
            }

            if (!isEqual && updateWith)
            {
                this.x = obj.x;
                this.y = obj.y;
                this.fieldValues.UpdateWith(obj.fieldValues);
            }

            return isEqual;
        }
    }

    public class FieldValue
    {
        public FieldValue(string name, Type type, object value)
        {
            this.name = name;
            this.type = type;
            this.value = value;
        }

        public string name { get; set; }
        public Type type { get; set; }
        public object value { get; set; }
    }

    public class FieldValueList : IList<FieldValue>
    {
        private List<FieldValue> internalList = new List<FieldValue>();
  
        public bool IsReadOnly { get { return false; } }
        public bool IsFixedSize { get { return false; } }
        public int Count { get { return internalList.Count; } }

        public void Add(FieldValue value)
        {
            internalList.Add(value);
        }

        public void Add(string name, Type type, object value)
        {
            FieldValue newItem = new FieldValue(name, type, value);
            internalList.Add(newItem);
        }

        public void Insert(int index, FieldValue item)
        {
            internalList.Insert(index, item);
        }

        public bool Contains(FieldValue item)
        {
            return internalList.Contains(item);
        }

        public int IndexOf(FieldValue item)
        {
            return internalList.IndexOf(item);
        }

        public bool Remove(FieldValue item)
        {
            return internalList.Remove(item);
        }

        public void RemoveAt(int index)
        {
            internalList.RemoveAt(index);
        }

        public void Clear()
        {
            internalList.Clear();
        }

        public FieldValue GetItem(string field)
        {
            FieldValue fieldValue = internalList.SingleOrDefault(item => item.name.Equals(field, StringComparison.CurrentCultureIgnoreCase));
            return fieldValue;
        }

        public Type GetType(string field)
        {
            FieldValue fieldValue = internalList.SingleOrDefault(item => item.name.Equals(field, StringComparison.CurrentCultureIgnoreCase));
            return (fieldValue == null) ? null : fieldValue.type;
        }

        public FieldValue this[int index]
        {
            get { return internalList[index]; }
            set { internalList[index] = value; }
        }

        public object this[string field]
        {
            get
            {
                FieldValue fieldValue = internalList.SingleOrDefault(item => item.name.Equals(field, StringComparison.CurrentCultureIgnoreCase));
                return (fieldValue == null) ? null : fieldValue.value;
            }
        }

        public bool Equals(FieldValueList compareList)
        {
            if (compareList == null) return false;

            for (int j = 0; j < internalList.Count; j++)
            {
                if (!internalList[j].value.Equals(compareList[j].value)) return false;
            }

            return true;
        }

        public void UpdateWith(FieldValueList updateList)
        {
            for (int j = 0; j < internalList.Count; j++)
            {
                internalList[j].value = updateList[j].value;
            }
        }

        public void CopyTo(FieldValue[] array, int index)
        {
            internalList.CopyTo(array, index);
        }

        public System.Collections.Generic.IEnumerator<FieldValue> GetEnumerator()
        {
            return internalList.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return internalList.GetEnumerator();
        }
    }
    #endregion

    #region State Management Classes for Threaded Editors
    public static class EditorStateManager
    {
        public static List<LayerUpdatingRunner> layerEditors = new List<LayerUpdatingRunner>();

        public static bool IsAnyEditing(LayerUpdatingRunner currentEditor)
        {
            return layerEditors.Any(editor => (editor != currentEditor && editor.IsSharingService && editor.IsEditing));
        }

        public static void RemoveEditor(LayerUpdatingRunner item)
        {
            if (layerEditors.Contains(item)) layerEditors.Remove(item);
        }
    }
    #endregion
}
