using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace FrameWork
{
    [Serializable]
    public class RpcClientConfig
    {
        public string RpcLocalIp;
        public string RpcServerIp;
        public int RpcServerPort;

        public RpcClientConfig()
        {
        }

        public RpcClientConfig(string rpcLocalIp, string rpcServerIp, int rpcServerPort)
        {
            RpcLocalIp = rpcLocalIp;
            RpcServerIp = rpcServerIp;
            RpcServerPort = rpcServerPort;
        }
    }

    /// <summary>
    /// Connects to the RPC server over a single duplex channel, hosting the client
    /// side services the server calls back into.
    /// </summary>
    public class RpcClient
    {
        public static int PING_TIME = 200;

        public bool IsRunning = true;
        public RpcClientInfo Info;
        public bool Connecting;

        public string ServerName;
        public string ServerIp;
        public string RpcServerIp;
        public int RpcServerPort;
        public int AllowedID;

        private readonly RpcDispatcher _dispatcher = new RpcDispatcher();
        private readonly Dictionary<Type, object> _localServices = new Dictionary<Type, object>();
        private readonly object _connectLock = new object();

        private RpcConnection _connection;
        private Thread _pinger;

        public RpcClient(string name, string ip, int allowedId)
        {
            ServerName = name;
            ServerIp = ip;
            AllowedID = allowedId;

            RegisterLocalServices();
        }

        public bool Start(string ip, int port)
        {
            RpcServerIp = ip;
            RpcServerPort = port;

            if (!Connect())
                return false;

            if (_pinger == null)
            {
                _pinger = new Thread(PingLoop) { IsBackground = true, Name = "RpcClient.Ping" };
                _pinger.Start();
            }

            return true;
        }

        public void Stop()
        {
            IsRunning = false;
            _connection?.Close();
        }

        private void RegisterLocalServices()
        {
            foreach (RpcServiceDescriptor descriptor in RpcServiceDescriptor.Discover(false, AllowedID))
            {
                object instance = Activator.CreateInstance(descriptor.Implementation);

                _localServices[descriptor.Contract] = instance;
                _dispatcher.Register(descriptor.Contract, instance);

                Log.Debug("RpcClient", "Registering : " + descriptor.Implementation.Name);
            }
        }

        public bool Connect()
        {
            lock (_connectLock)
            {
                if (_connection != null && _connection.IsConnected)
                    return true;

                Connecting = true;

                try
                {
                    Log.Debug("RpcClient", "Connecting to : " + RpcServerIp + ":" + RpcServerPort);

                    var tcp = new TcpClient();
                    tcp.Connect(RpcServerIp, RpcServerPort);

                    RpcConnection connection = new RpcConnection(tcp, _dispatcher);
                    connection.Closed += OnConnectionClosed;
                    connection.Start();

                    IServerMgr serverMgr = RpcProxyCache.Get<IServerMgr>(connection);
                    Info = serverMgr.Connect(ServerName, ServerIp);

                    if (Info == null)
                    {
                        connection.Close();
                        return false;
                    }

                    connection.RemoteInfo = Info;
                    _connection = connection;

                    foreach (object service in _localServices.Values)
                        if (service is RpcObject rpcObject)
                            rpcObject.MyInfo = Info;

                    serverMgr.Connected(Info.RpcID);

                    Log.Success("RpcClient", "Connected to : " + RpcServerIp + ":" + RpcServerPort + " as " + Info.Description());

                    foreach (object service in _localServices.Values)
                        (service as RpcObject)?.OnServerConnected();

                    return true;
                }
                catch (Exception e)
                {
                    Log.Error("RpcClient", e.Message);
                    Log.Notice("RpcClient", "Can not start RPC : " + RpcServerIp + ":" + RpcServerPort);

                    _connection = null;
                    Info = null;

                    return false;
                }
                finally
                {
                    Connecting = false;
                }
            }
        }

        private void OnConnectionClosed(RpcConnection connection)
        {
            RpcProxyCache.Forget(connection);

            if (_connection != connection)
                return;

            _connection = null;

            foreach (object service in _localServices.Values)
                (service as RpcObject)?.OnServerDisconnected();
        }

        /// <summary>Returns a proxy for a contract hosted by the RPC server.</summary>
        public T GetServerObject<T>() where T : class
        {
            RpcConnection connection = _connection;

            if (connection == null || !connection.IsConnected)
                return null;

            return RpcProxyCache.Get<T>(connection);
        }

        /// <summary>Returns the locally hosted implementation of a contract.</summary>
        public T GetLocalObject<T>() where T : class
        {
            return _localServices.TryGetValue(typeof(T), out object instance) ? (T)instance : null;
        }

        private void PingLoop()
        {
            while (IsRunning)
            {
                int start = Environment.TickCount;

                if (!Connecting)
                {
                    RpcConnection connection = _connection;

                    if (connection == null || !connection.IsConnected)
                    {
                        Connect();
                    }
                    else
                    {
                        try
                        {
                            RpcProxyCache.Get<IServerMgr>(connection).Ping();
                        }
                        catch (Exception e)
                        {
                            Log.Error("RpcClient", "Ping failed: " + e.Message);
                            connection.Close();
                        }
                    }
                }

                int elapsed = Environment.TickCount - start;

                if (elapsed < PING_TIME)
                    Thread.Sleep(PING_TIME - elapsed);
            }
        }
    }
}
