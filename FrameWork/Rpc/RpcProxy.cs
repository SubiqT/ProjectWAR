using System;
using System.Reflection;

namespace FrameWork
{
    /// <summary>
    /// Transparent client-side proxy for an RPC contract. Replaces the Remoting
    /// transparent proxy returned by Activator.GetObject.
    /// </summary>
    public class RpcProxy : DispatchProxy
    {
        private RpcConnection _connection;
        private string _service;
        private TimeSpan _timeout;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            return _connection.Invoke(_service, targetMethod, args, _timeout);
        }

        public static T Create<T>(RpcConnection connection, TimeSpan timeout) where T : class
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            object proxy = Create<T, RpcProxy>();
            var rpc = (RpcProxy)proxy;

            rpc._connection = connection;
            rpc._service = typeof(T).Name;
            rpc._timeout = timeout;

            return (T)proxy;
        }
    }
}
