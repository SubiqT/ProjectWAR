using System;

namespace FrameWork
{
    /// <summary>
    /// Lifetime of a registered RPC service. Only a single shared instance per
    /// process is supported, which is what the previous Remoting layer used.
    /// </summary>
    public enum RpcMode
    {
        Singleton
    }

    /// <summary>
    /// Marks a class as an RPC service implementation and declares the interface
    /// remote callers invoke it through.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class RpcAttribute : Attribute
    {
        /// <summary>Interface used as the wire contract for this service.</summary>
        public Type Contract { get; }

        /// <summary>True when the service is hosted by the RPC server, false when hosted by a client.</summary>
        public bool ForServer { get; }

        public RpcMode Mode { get; }

        /// <summary>Endpoint identifiers allowed to host this service. 0 means any.</summary>
        public int[] AllowedId { get; }

        public RpcAttribute(Type contract, bool forServer, RpcMode mode, params int[] allowedId)
        {
            Contract = contract ?? throw new ArgumentNullException(nameof(contract));

            if (!contract.IsInterface)
                throw new ArgumentException($"RPC contract {contract.Name} must be an interface.", nameof(contract));

            ForServer = forServer;
            Mode = mode;
            AllowedId = allowedId ?? Array.Empty<int>();
        }

        public bool IsAllowedFor(int endpointId)
        {
            foreach (int allowed in AllowedId)
                if (allowed == 0 || allowed == endpointId)
                    return true;

            return false;
        }
    }

    /// <summary>
    /// Base class for RPC service implementations. Provides connection lifecycle
    /// callbacks that the transport invokes locally.
    /// </summary>
    public abstract class RpcObject
    {
        /// <summary>Identity of the endpoint this instance belongs to, if it is a client.</summary>
        public RpcClientInfo MyInfo;

        public virtual void OnClientConnected(RpcClientInfo info)
        {
        }

        public virtual void OnClientDisconnected(RpcClientInfo info)
        {
        }

        public virtual void OnServerConnected()
        {
        }

        public virtual void OnServerDisconnected()
        {
        }
    }

    /// <summary>Identity of a process connected to the RPC server.</summary>
    [Serializable]
    public class RpcClientInfo
    {
        public string Name;
        public int RpcID;

        public string Ip;
        public int Port;

        public bool Connected = true;

        public RpcClientInfo()
        {
        }

        public RpcClientInfo(string name, string ip, int port, int rpcId)
        {
            Name = name;
            Ip = ip;
            Port = port;
            RpcID = rpcId;
        }

        public string Description()
        {
            return "[" + RpcID + "]\t| " + Name + "| " + Ip + ":" + Port;
        }
    }

    /// <summary>Raised when a remote call fails or the peer reports an error.</summary>
    public class RpcException : Exception
    {
        public RpcException(string message) : base(message)
        {
        }

        public RpcException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
