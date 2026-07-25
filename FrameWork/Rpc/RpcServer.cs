using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FrameWork
{
    [Serializable]
    public class RpcServerConfig
    {
        public string RpcIp;
        public int RpcPort;
        public int RpcClientStartingPort;

        public RpcServerConfig()
        {
        }

        public RpcServerConfig(string rpcIp, int rpcPort, int rpcClientStartingPort)
        {
            RpcIp = rpcIp;
            RpcPort = rpcPort;
            RpcClientStartingPort = rpcClientStartingPort;
        }
    }

    /// <summary>
    /// Hosts the RPC services other server processes call into, and keeps a duplex
    /// channel open to every connected client so it can call back out.
    /// </summary>
    public class RpcServer
    {
        public static int PING_TIME = 200;

        public ServerMgr Mgr;
        public bool IsRunning = true;

        public int StartingPort;
        public string LocalIp;
        public int LocalPort;
        public int AllowedID;

        private readonly RpcDispatcher _dispatcher = new RpcDispatcher();
        private readonly Dictionary<Type, object> _localServices = new Dictionary<Type, object>();
        private readonly ConcurrentDictionary<RpcConnection, byte> _connections = new ConcurrentDictionary<RpcConnection, byte>();

        private TcpListener _listener;
        private Thread _accepter;
        private Thread _pinger;

        public RpcServer(int startingPort, int allowedId)
        {
            StartingPort = startingPort;
            AllowedID = allowedId;
        }

        public bool Start(string ip, int port)
        {
            try
            {
                if (_listener != null)
                    return false;

                LocalIp = ip;
                LocalPort = port;

                Log.Debug("RpcServer", "Start on : " + ip + ":" + port);

                RegisterLocalServices();

                _listener = new TcpListener(IPAddress.Parse(ip), port);
                _listener.Start();

                _accepter = new Thread(AcceptLoop) { IsBackground = true, Name = "RpcServer.Accept" };
                _accepter.Start();

                _pinger = new Thread(PingLoop) { IsBackground = true, Name = "RpcServer.Ping" };
                _pinger.Start();

                Log.Success("RpcServer", "Listening on : " + ip + ":" + port);
            }
            catch (Exception e)
            {
                Log.Error("RpcServer", e.Message);
                Log.Notice("RpcServer", "Can not start RPC : " + ip + ":" + port);

                return false;
            }

            return true;
        }

        public void Stop()
        {
            IsRunning = false;

            try
            {
                _listener?.Stop();
            }
            catch (Exception e)
            {
                Log.Debug("RpcServer", "Error stopping listener: " + e.Message);
            }

            foreach (RpcConnection connection in _connections.Keys)
                connection.Close();
        }

        private void RegisterLocalServices()
        {
            foreach (RpcServiceDescriptor descriptor in RpcServiceDescriptor.Discover(true, AllowedID))
            {
                object instance = Activator.CreateInstance(descriptor.Implementation);

                _localServices[descriptor.Contract] = instance;
                _dispatcher.Register(descriptor.Contract, instance);

                Log.Debug("RpcServer", "Registering : " + descriptor.Implementation.Name);
            }

            Mgr = GetLocalObject<IServerMgr>() as ServerMgr;

            if (Mgr == null)
                throw new RpcException("ServerMgr was not registered, the RPC server cannot track clients.");

            ServerMgr.Server = this;
            Mgr.StartingPort = StartingPort;
        }

        /// <summary>Returns the locally hosted implementation of a contract.</summary>
        public T GetLocalObject<T>() where T : class
        {
            if (_localServices.TryGetValue(typeof(T), out object instance))
                return (T)instance;

            Log.Error("RpcServer", "No local RPC service registered for " + typeof(T).Name);

            return null;
        }

        /// <summary>Returns a proxy that calls into a connected client by name.</summary>
        public T GetObject<T>(string name) where T : class
        {
            RpcClientInfo info = Mgr.GetClient(name);

            if (info == null)
            {
                Log.Error("RpcServer", "Can not find client : " + name);
                return null;
            }

            return GetObject<T>(info);
        }

        public T GetObject<T>(RpcClientInfo info) where T : class
        {
            RpcConnection connection = FindConnection(info);

            if (connection == null)
            {
                Log.Error("RpcServer", "No open channel for client : " + info.Description());
                return null;
            }

            return RpcProxyCache.Get<T>(connection);
        }

        private RpcConnection FindConnection(RpcClientInfo info)
        {
            foreach (RpcConnection connection in _connections.Keys)
                if (connection.RemoteInfo != null && connection.RemoteInfo.RpcID == info.RpcID)
                    return connection;

            return null;
        }

        internal void NotifyClientConnected(RpcClientInfo info)
        {
            foreach (object service in _dispatcher.Implementations)
                (service as RpcObject)?.OnClientConnected(info);

            foreach (RpcConnection connection in _connections.Keys)
            {
                if (connection.RemoteInfo == null || connection.RemoteInfo.RpcID == info.RpcID || !connection.RemoteInfo.Connected)
                    continue;

                try
                {
                    RpcProxyCache.Get<IClientMgr>(connection).NotifyClientConnected(info);
                    RpcProxyCache.Get<IClientMgr>(FindConnection(info))?.NotifyClientConnected(connection.RemoteInfo);
                }
                catch (Exception e)
                {
                    Log.Error("RpcServer", "Failed to announce " + info.Name + " to " + connection.RemoteInfo.Name + ": " + e.Message);
                }
            }
        }

        private void NotifyClientDisconnected(RpcClientInfo info)
        {
            foreach (object service in _dispatcher.Implementations)
                (service as RpcObject)?.OnClientDisconnected(info);

            foreach (RpcConnection connection in _connections.Keys)
            {
                if (connection.RemoteInfo == null || connection.RemoteInfo.RpcID == info.RpcID)
                    continue;

                try
                {
                    RpcProxyCache.Get<IClientMgr>(connection).NotifyClientDisconnected(info);
                }
                catch (Exception e)
                {
                    Log.Error("RpcServer", "Failed to announce disconnect of " + info.Name + ": " + e.Message);
                }
            }
        }

        private void AcceptLoop()
        {
            while (IsRunning)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    var connection = new RpcConnection(client, _dispatcher);

                    connection.Closed += OnConnectionClosed;
                    _connections[connection] = 0;
                    connection.Start();

                    Log.Debug("RpcServer", "Channel opened from " + client.Client.RemoteEndPoint);
                }
                catch (Exception e)
                {
                    if (IsRunning)
                        Log.Error("RpcServer", "Accept failed: " + e.Message);

                    return;
                }
            }
        }

        private void OnConnectionClosed(RpcConnection connection)
        {
            _connections.TryRemove(connection, out _);

            RpcClientInfo info = connection.RemoteInfo;

            if (info == null)
                return;

            Log.Notice("RpcServer", info.Description() + " | Disconnected");

            Mgr.Remove(info.RpcID);
            NotifyClientDisconnected(info);
        }

        private void PingLoop()
        {
            while (IsRunning)
            {
                int start = Environment.TickCount;

                foreach (RpcConnection connection in _connections.Keys)
                {
                    if (connection.RemoteInfo == null || !connection.RemoteInfo.Connected)
                        continue;

                    try
                    {
                        RpcProxyCache.Get<IClientMgr>(connection).Ping();
                    }
                    catch (Exception e)
                    {
                        Log.Error("RpcServer", connection.RemoteInfo.Description() + " ping failed: " + e.Message);
                        connection.Close();
                    }
                }

                int elapsed = Environment.TickCount - start;

                if (elapsed < PING_TIME)
                    Thread.Sleep(PING_TIME - elapsed);
            }
        }
    }
}
