﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using MaxMind.Db;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace TimberWinR.Inputs
{
    public class TcpInputListener : InputListener
    {
        private readonly System.Net.Sockets.TcpListener _tcpListenerV4;
        private readonly System.Net.Sockets.TcpListener _tcpListenerV6;
        private Thread _listenThreadV4;
        private Thread _listenThreadV6;
        private readonly int _port;
        private long _receivedMessages;

        public override JObject ToJson()
        {
            JObject json = new JObject(
                new JProperty("tcp",
                    new JObject(
                        new JProperty("port", _port),
                        new JProperty("messages", _receivedMessages)
                        )));

            return json;
        }

        public TcpInputListener(CancellationToken cancelToken, int port = 5140)
            : base(cancelToken, "Win32-Tcp")
        {
            _port = port;
          
            LogManager.GetCurrentClassLogger().Info("Tcp Input(v4/v6) on Port {0} Ready", _port);


            _tcpListenerV6 = new System.Net.Sockets.TcpListener(IPAddress.IPv6Any, port);
            _tcpListenerV4 = new System.Net.Sockets.TcpListener(IPAddress.Any, port);

            _listenThreadV4 = new Thread(new ParameterizedThreadStart(ListenForClients));
            _listenThreadV4.Start(_tcpListenerV4);

            _listenThreadV6 = new Thread(new ParameterizedThreadStart(ListenForClients));
            _listenThreadV6.Start(_tcpListenerV6);
        }


        public override void Shutdown()
        {
            this._tcpListenerV4.Stop();
            this._tcpListenerV6.Stop();

            Finished();
            base.Shutdown();
        }


        private void ListenForClients(object olistener)
        {
            System.Net.Sockets.TcpListener listener = olistener as System.Net.Sockets.TcpListener;

            listener.Start();

          
            while (!CancelToken.IsCancellationRequested)
            {
                try
                {
                    //blocks until a client has connected to the server
                    TcpClient client = listener.AcceptTcpClient();

                    // Wait for a client, spin up a thread.
                    var clientThread = new Thread(new ParameterizedThreadStart(HandleNewClient));
                    clientThread.Start(client);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.Interrupted)
                        break;
                    else
                        LogManager.GetCurrentClassLogger().Error(ex);
                }
            }
        }

        private void HandleNewClient(object client)
        {
            var tcpClient = (TcpClient)client;

            try
            {
                if (CancelToken.IsCancellationRequested) return;

                NetworkStream clientStream = tcpClient.GetStream();
                using(var stream = new StreamReader(clientStream))
                {
                    using (var reader = new JsonTextReader(stream))
                    {
                        //assume the stream provides a single JSON object
                        JObject json = JObject.Load(reader);
                        ProcessJson(json);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error(ex);
            }

            tcpClient.Close();
            Finished();
        }
    }
}
