using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace FrameWork
{
    /// <summary>An RPC service implementation discovered through its attribute.</summary>
    public sealed class RpcServiceDescriptor
    {
        public Type Contract { get; }

        public Type Implementation { get; }

        private RpcServiceDescriptor(Type contract, Type implementation)
        {
            Contract = contract;
            Implementation = implementation;
        }

        /// <summary>
        /// Finds every RpcObject in the loaded assemblies that is marked for the
        /// requested side of the connection and permitted for this endpoint.
        /// </summary>
        public static List<RpcServiceDescriptor> Discover(bool forServer, int endpointId)
        {
            var discovered = new List<RpcServiceDescriptor>();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in SafeGetTypes(assembly))
                {
                    if (type == null || !type.IsClass || type.IsAbstract || !typeof(RpcObject).IsAssignableFrom(type))
                        continue;

                    var attribute = type.GetCustomAttribute<RpcAttribute>(true);

                    if (attribute == null || attribute.ForServer != forServer || !attribute.IsAllowedFor(endpointId))
                        continue;

                    discovered.Add(new RpcServiceDescriptor(attribute.Contract, type));
                }
            }

            return discovered;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types;
            }
        }
    }

    /// <summary>
    /// Caches one proxy per contract per channel. Callers fetch proxies on every
    /// call, so creating a fresh DispatchProxy each time would be wasteful.
    /// </summary>
    internal static class RpcProxyCache
    {
        private static readonly ConcurrentDictionary<RpcConnection, ConcurrentDictionary<Type, object>> Proxies =
            new ConcurrentDictionary<RpcConnection, ConcurrentDictionary<Type, object>>();

        public static T Get<T>(RpcConnection connection) where T : class
        {
            if (connection == null)
                return null;

            ConcurrentDictionary<Type, object> perConnection =
                Proxies.GetOrAdd(connection, _ => new ConcurrentDictionary<Type, object>());

            return (T)perConnection.GetOrAdd(typeof(T), _ => RpcProxy.Create<T>(connection, RpcConnection.DefaultTimeout));
        }

        public static void Forget(RpcConnection connection)
        {
            if (connection != null)
                Proxies.TryRemove(connection, out _);
        }
    }
}
