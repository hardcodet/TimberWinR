﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Interop.MSUtil;

using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NLog;
using TimberWinR.Parser;
using LogQuery = Interop.MSUtil.LogQueryClassClass;
using IISW3CLogInputFormat = Interop.MSUtil.COMIISW3CInputContextClassClass;
using LogRecordSet = Interop.MSUtil.ILogRecordset;


namespace TimberWinR.Inputs
{
    public class IISW3CInputListener : InputListener
    {
        private readonly int _pollingIntervalInSeconds;
        private readonly TimberWinR.Parser.IISW3CLog _arguments;
        private long _receivedMessages;

        public IISW3CInputListener(TimberWinR.Parser.IISW3CLog arguments, CancellationToken cancelToken, int pollingIntervalInSeconds = 5)
            : base(cancelToken, "Win32-IISLog")
        {
            _arguments = arguments;
            _receivedMessages = 0;
            _pollingIntervalInSeconds = pollingIntervalInSeconds;
            foreach (string loc in _arguments.Location.Split(','))
            {
                string hive = loc.Trim();
                Task.Factory.StartNew(() => IISW3CWatcher(loc));
            }
        }

        public override void Shutdown()
        {
            base.Shutdown();
        }

        public override JObject ToJson()
        {
            JObject json = new JObject(
                new JProperty("iisw3c",
                    new JObject(
                        new JProperty("messages", _receivedMessages),
                        new JProperty("location", _arguments.Location),
                        new JProperty("codepage", _arguments.CodePage),
                        new JProperty("consolidateLogs", _arguments.ConsolidateLogs),
                        new JProperty("dirTime", _arguments.DirTime),
                        new JProperty("dQuotes", _arguments.DoubleQuotes),
                        new JProperty("recurse", _arguments.Recurse),
                        new JProperty("useDoubleQuotes", _arguments.DoubleQuotes)
                        )));
            return json;
        }


        private void IISW3CWatcher(string location)
        {
            LogManager.GetCurrentClassLogger().Info("IISW3Listener Ready For {0}", location);

            var oLogQuery = new LogQuery();

            var iFmt = new IISW3CLogInputFormat()
            {
                codepage = _arguments.CodePage,
                consolidateLogs = true,
                dirTime = _arguments.DirTime,
                dQuotes = _arguments.DoubleQuotes,               
                recurse = _arguments.Recurse,
                useDoubleQuotes = _arguments.DoubleQuotes
            };

            if (_arguments.MinDateMod.HasValue)
                iFmt.minDateMod = _arguments.MinDateMod.Value.ToString("yyyy-MM-dd hh:mm:ss");

            Dictionary<string, Int64> logFileMaxRecords = new Dictionary<string, Int64>();
         
          
            // Execute the query
            while (!CancelToken.IsCancellationRequested)
            {
                try
                {
                    oLogQuery = new LogQuery();

                    var qfiles = string.Format("SELECT Distinct [LogFilename] FROM {0}", location);
                    var rsfiles = oLogQuery.Execute(qfiles, iFmt);
                    for (; !rsfiles.atEnd(); rsfiles.moveNext())
                    {
                        var record = rsfiles.getRecord();
                        string fileName = record.getValue("LogFilename") as string;
                        if (!logFileMaxRecords.ContainsKey(fileName))
                        {
                            var qcount = string.Format("SELECT max(LogRow) as MaxRecordNumber FROM {0}", fileName);
                            var rcount = oLogQuery.Execute(qcount, iFmt);
                            var qr = rcount.getRecord();
                            var lrn = (Int64)qr.getValueEx("MaxRecordNumber");
                            logFileMaxRecords[fileName] = lrn;
                        }
                    }

                 
                    foreach (string fileName in logFileMaxRecords.Keys.ToList())
                    {
                        var lastRecordNumber = logFileMaxRecords[fileName];
                        var query = string.Format("SELECT * FROM '{0}' Where LogRow > {1}", fileName, lastRecordNumber);

                        var rs = oLogQuery.Execute(query, iFmt);
                        var colMap = new Dictionary<string, int>();
                        for (int col = 0; col < rs.getColumnCount(); col++)
                        {
                            string colName = rs.getColumnName(col);
                            colMap[colName] = col;
                        }

                        // Browse the recordset
                        for (; !rs.atEnd(); rs.moveNext())
                        {
                            var record = rs.getRecord();
                            var json = new JObject();
                            foreach (var field in _arguments.Fields)
                            {
                                if (!colMap.ContainsKey(field.Name))
                                    continue;

                                object v = record.getValue(field.Name);
                                if (field.DataType == typeof (DateTime))
                                {
                                    DateTime dt = DateTime.Parse(v.ToString());
                                    json.Add(new JProperty(field.Name, dt));
                                }
                                else
                                    json.Add(new JProperty(field.Name, v));
                            }
                            ProcessJson(json);
                            _receivedMessages++;
                            var lrn = (Int64)record.getValueEx("LogRow");
                            logFileMaxRecords[fileName] = lrn;
                            record = null;
                            json = null;
                        }
                        // Close the recordset
                        rs.close();
                    }
                }
                catch (Exception ex)
                {
                    LogManager.GetCurrentClassLogger().Error(ex);
                }
               
                System.Threading.Thread.Sleep(_pollingIntervalInSeconds * 1000);
            }

            Finished();
        }
    }
}
