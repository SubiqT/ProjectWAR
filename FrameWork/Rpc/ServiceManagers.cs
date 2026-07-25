using System.Collections.Generic;

namespace FrameWork
{
    /// <summary>Registration and liveness contract exposed by the RPC server.</summary>
    public interface IServerMgr
    {
        RpcClientInfo Connect(string name, string ip);

        bool Connected(int rpcId);

        void Ping();
    }

    /// <summary>Liveness and notification contract exposed by every RPC client.</summary>
    public interface IClientMgr
    {
        void Ping();

        void NotifyClientConnected(RpcClientInfo info);

        void NotifyClientDisconnected(RpcClientInfo info);
    }

    /// <summary>
    /// Server side endpoint registry. Clients call Connect to obtain an identity,
    /// then Connected once they are ready to receive callbacks.
    /// </summary>
    [Rpc(typeof(IServerMgr), true, RpcMode.Singleton, 0)]
    public class ServerMgr : RpcObject, IServerMgr
    {
        public static RpcServer Server;

        public int StartingPort;

        private readonly List<RpcClientInfo> _clients = new List<RpcClientInfo>();

        public RpcClientInfo GetClient(string name)
        {
            lock (_clients)
                return _clients.Find(info => info.Name == name);
        }

        public RpcClientInfo GetClient(int rpcId)
        {
            lock (_clients)
                return _clients.Find(info => info.RpcID == rpcId);
        }

        public RpcClientInfo[] GetClients()
        {
            lock (_clients)
                return _clients.ToArray();
        }

        public void Remove(int rpcId)
        {
            lock (_clients)
                _clients.RemoveAll(info => info.RpcID == rpcId);
        }

        public RpcClientInfo Connect(string name, string ip)
        {
            RpcClientInfo info = GetClient(name);

            if (info == null)
            {
                info = new RpcClientInfo(name, ip, ++StartingPort, System.Guid.NewGuid().GetHashCode())
                {
                    Connected = false
                };

                lock (_clients)
                    _clients.Add(info);
            }

            // Bind the identity to the channel the call arrived on so the server can
            // route callbacks and detect which endpoint dropped.
            RpcConnection origin = RpcCallContext.Connection;

            if (origin != null)
                origin.RemoteInfo = info;

            Log.Debug("ServerMgr", info.Description() + " | Connecting");

            return info;
        }

        public bool Connected(int rpcId)
        {
            RpcClientInfo info = GetClient(rpcId);

            if (info == null)
                return false;

            info.Connected = true;

            Log.Success("ServerMgr", info.Description() + " | Connected");

            Server?.NotifyClientConnected(info);

            return true;
        }

        public void Ping()
        {
        }
    }

    /// <summary>Client side endpoint that receives server callbacks.</summary>
    [Rpc(typeof(IClientMgr), false, RpcMode.Singleton, 0)]
    public class ClientMgr : RpcObject, IClientMgr
    {
        public void Ping()
        {
        }

        public void NotifyClientConnected(RpcClientInfo info)
        {
            OnClientConnected(info);
        }

        public void NotifyClientDisconnected(RpcClientInfo info)
        {
            OnClientDisconnected(info);
        }

        public override void OnClientConnected(RpcClientInfo info)
        {
            Log.Notice("ClientMgr", info.Description() + " | Connected");
        }

        public override void OnClientDisconnected(RpcClientInfo info)
        {
            Log.Notice("ClientMgr", info.Description() + " | Disconnected");
        }

        public override void OnServerConnected()
        {
            Log.Notice("ClientMgr", "Server connected !");
        }

        public override void OnServerDisconnected()
        {
            Log.Notice("ClientMgr", "Server disconnected !");
        }
    }
}
