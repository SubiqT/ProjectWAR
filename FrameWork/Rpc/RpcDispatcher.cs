using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace FrameWork
{
    /// <summary>
    /// Ambient context describing the connection that issued the call currently
    /// being handled. Set for the duration of a dispatched invocation.
    /// </summary>
    public static class RpcCallContext
    {
        [ThreadStatic]
        private static RpcConnection _connection;

        public static RpcConnection Connection => _connection;

        internal static void Enter(RpcConnection connection) => _connection = connection;

        internal static void Exit() => _connection = null;
    }

    /// <summary>
    /// Routes incoming requests to locally registered service implementations.
    /// </summary>
    public sealed class RpcDispatcher
    {
        private readonly ConcurrentDictionary<string, ServiceEntry> _services =
            new ConcurrentDictionary<string, ServiceEntry>();

        public void Register(Type contract, object implementation)
        {
            if (contract == null)
                throw new ArgumentNullException(nameof(contract));

            if (implementation == null)
                throw new ArgumentNullException(nameof(implementation));

            if (!contract.IsInstanceOfType(implementation))
                throw new ArgumentException($"{implementation.GetType().Name} does not implement {contract.Name}.");

            _services[contract.Name] = new ServiceEntry(contract, implementation);
        }

        public bool TryGetImplementation(Type contract, out object implementation)
        {
            if (_services.TryGetValue(contract.Name, out ServiceEntry entry))
            {
                implementation = entry.Implementation;
                return true;
            }

            implementation = null;
            return false;
        }

        public IEnumerable<object> Implementations
        {
            get
            {
                foreach (ServiceEntry entry in _services.Values)
                    yield return entry.Implementation;
            }
        }

        public RpcResponse Dispatch(RpcRequest request, RpcConnection origin)
        {
            var response = new RpcResponse { Id = request.Id };

            if (!_services.TryGetValue(request.Service, out ServiceEntry entry))
            {
                response.Error = $"No RPC service registered for {request.Service}.";
                return response;
            }

            MethodInfo method = entry.ResolveMethod(request.Method, request.ParameterTypes);

            if (method == null)
            {
                response.Error = $"{request.Service} has no method {request.Method} matching the supplied argument types.";
                return response;
            }

            ParameterInfo[] parameters = method.GetParameters();
            object[] arguments = new object[parameters.Length];

            try
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type declared = ParameterValueType(parameters[i]);
                    arguments[i] = parameters[i].IsOut
                        ? null
                        : RpcSerializer.Deserialize(request.Arguments[i], declared);
                }
            }
            catch (Exception e)
            {
                response.Error = $"Failed to deserialize arguments for {request.Service}.{request.Method}: {e.Message}";
                return response;
            }

            try
            {
                RpcCallContext.Enter(origin);

                object result = method.Invoke(entry.Implementation, arguments);

                if (method.ReturnType != typeof(void))
                    response.Result = RpcSerializer.Serialize(result, method.ReturnType);

                response.ByRefArguments = CollectByRefArguments(parameters, arguments);
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                response.Error = $"{inner.GetType().Name}: {inner.Message}";
                Log.Error("RpcDispatcher", $"{request.Service}.{request.Method} threw: {inner}");
            }
            catch (Exception e)
            {
                response.Error = $"{e.GetType().Name}: {e.Message}";
                Log.Error("RpcDispatcher", $"{request.Service}.{request.Method} failed: {e}");
            }
            finally
            {
                RpcCallContext.Exit();
            }

            return response;
        }

        private static string[] CollectByRefArguments(ParameterInfo[] parameters, object[] arguments)
        {
            string[] byRef = null;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (!parameters[i].ParameterType.IsByRef)
                    continue;

                byRef ??= new string[parameters.Length];
                byRef[i] = RpcSerializer.Serialize(arguments[i], ParameterValueType(parameters[i]));
            }

            return byRef;
        }

        internal static Type ParameterValueType(ParameterInfo parameter)
        {
            Type type = parameter.ParameterType;
            return type.IsByRef ? type.GetElementType() : type;
        }

        private sealed class ServiceEntry
        {
            private readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();
            private readonly MethodInfo[] _methods;

            public object Implementation { get; }

            public ServiceEntry(Type contract, object implementation)
            {
                Implementation = implementation;
                _methods = contract.GetMethods();
            }

            public MethodInfo ResolveMethod(string name, string[] parameterTypes)
            {
                string key = name + "(" + string.Join(",", parameterTypes ?? Array.Empty<string>()) + ")";

                lock (_methodCache)
                {
                    if (_methodCache.TryGetValue(key, out MethodInfo cached))
                        return cached;

                    MethodInfo resolved = Match(name, parameterTypes);
                    _methodCache[key] = resolved;

                    return resolved;
                }
            }

            private MethodInfo Match(string name, string[] parameterTypes)
            {
                int expected = parameterTypes?.Length ?? 0;

                foreach (MethodInfo candidate in _methods)
                {
                    if (candidate.Name != name)
                        continue;

                    ParameterInfo[] parameters = candidate.GetParameters();

                    if (parameters.Length != expected)
                        continue;

                    bool matches = true;

                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (ParameterValueType(parameters[i]).FullName == parameterTypes[i])
                            continue;

                        matches = false;
                        break;
                    }

                    if (matches)
                        return candidate;
                }

                return null;
            }
        }
    }
}
