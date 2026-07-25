using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

namespace FrameWork
{
    /// <summary>
    /// A single duplex RPC channel. Both peers may issue requests over the same
    /// socket, which replaces the pair of Remoting channels the previous
    /// implementation opened per endpoint.
    /// </summary>
    public sealed class RpcConnection : IDisposable
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly TcpClient _tcp;
        private readonly Stream _stream;
        private readonly RpcDispatcher _dispatcher;
        private readonly ConcurrentDictionary<long, PendingCall> _pending = new ConcurrentDictionary<long, PendingCall>();
        private readonly object _writeLock = new object();

        private Thread _reader;
        private long _nextRequestId;
        private volatile bool _running;

        /// <summary>Identity of the peer, assigned once it registers with the server.</summary>
        public RpcClientInfo RemoteInfo { get; set; }

        public bool IsConnected => _running && _tcp.Connected;

        /// <summary>Raised once when the channel drops, from the reader thread.</summary>
        public event Action<RpcConnection> Closed;

        public RpcConnection(TcpClient tcp, RpcDispatcher dispatcher)
        {
            _tcp = tcp ?? throw new ArgumentNullException(nameof(tcp));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _tcp.NoDelay = true;
            _stream = tcp.GetStream();
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;

            _reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "RpcConnection"
            };

            _reader.Start();
        }

        public object Invoke(string service, MethodInfo method, object[] args, TimeSpan timeout)
        {
            if (!IsConnected)
                throw new RpcException($"RPC channel is closed, cannot call {service}.{method.Name}.");

            ParameterInfo[] parameters = method.GetParameters();
            var request = new RpcRequest
            {
                Id = Interlocked.Increment(ref _nextRequestId),
                Service = service,
                Method = method.Name,
                ParameterTypes = new string[parameters.Length],
                Arguments = new string[parameters.Length]
            };

            for (int i = 0; i < parameters.Length; i++)
            {
                Type declared = RpcDispatcher.ParameterValueType(parameters[i]);
                request.ParameterTypes[i] = declared.FullName;
                request.Arguments[i] = parameters[i].IsOut ? null : RpcSerializer.Serialize(args[i], declared);
            }

            RpcResponse response = SendAndWait(request, timeout, service, method.Name);

            if (response.Error != null)
                throw new RpcException($"{service}.{method.Name} failed on the remote side: {response.Error}");

            if (response.ByRefArguments != null)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType.IsByRef)
                        args[i] = RpcSerializer.Deserialize(response.ByRefArguments[i], RpcDispatcher.ParameterValueType(parameters[i]));
                }
            }

            if (method.ReturnType == typeof(void))
                return null;

            return RpcSerializer.Deserialize(response.Result, method.ReturnType);
        }

        private RpcResponse SendAndWait(RpcRequest request, TimeSpan timeout, string service, string methodName)
        {
            var pending = new PendingCall();
            _pending[request.Id] = pending;

            try
            {
                Write(RpcSerializer.SerializeFrame(RpcFrameType.Request, request));

                if (!pending.Completed.Wait(timeout))
                    throw new RpcException($"{service}.{methodName} timed out after {timeout.TotalSeconds:0.#}s.");

                return pending.Response;
            }
            finally
            {
                _pending.TryRemove(request.Id, out _);
            }
        }

        private void Write(byte[] frame)
        {
            lock (_writeLock)
            {
                _stream.Write(frame, 0, frame.Length);
                _stream.Flush();
            }
        }

        private void ReadLoop()
        {
            byte[] header = new byte[RpcFraming.HeaderLength];

            try
            {
                while (_running)
                {
                    RpcFraming.ReadExactly(_stream, header, 0, header.Length);

                    int length = BitConverter.ToInt32(header, 0) - 1;

                    if (length < 0 || length > RpcFraming.MaxFrameLength)
                        throw new IOException($"RPC peer sent an invalid frame length of {length} bytes.");

                    byte[] payload = new byte[length];
                    RpcFraming.ReadExactly(_stream, payload, 0, length);

                    HandleFrame((RpcFrameType)header[4], payload);
                }
            }
            catch (Exception e)
            {
                if (_running)
                    Log.Debug("RpcConnection", "Channel closed: " + e.Message);
            }
            finally
            {
                Shutdown();
            }
        }

        private void HandleFrame(RpcFrameType type, byte[] payload)
        {
            switch (type)
            {
                case RpcFrameType.Response:
                    var response = RpcSerializer.DeserializeEnvelope<RpcResponse>(payload, 0, payload.Length);

                    if (_pending.TryGetValue(response.Id, out PendingCall pending))
                        pending.Complete(response);

                    break;

                case RpcFrameType.Request:
                    var request = RpcSerializer.DeserializeEnvelope<RpcRequest>(payload, 0, payload.Length);

                    // Dispatched off the reader thread so that a peer callback issued
                    // while we are waiting on our own request cannot deadlock.
                    ThreadPool.QueueUserWorkItem(_ => ServeRequest(request));
                    break;

                default:
                    Log.Error("RpcConnection", $"Unknown RPC frame type {type}.");
                    break;
            }
        }

        private void ServeRequest(RpcRequest request)
        {
            try
            {
                RpcResponse response = _dispatcher.Dispatch(request, this);
                Write(RpcSerializer.SerializeFrame(RpcFrameType.Response, response));
            }
            catch (Exception e)
            {
                Log.Error("RpcConnection", $"Failed to serve {request.Service}.{request.Method}: {e}");
            }
        }

        private void Shutdown()
        {
            if (!_running)
                return;

            _running = false;

            foreach (PendingCall pending in _pending.Values)
                pending.Complete(new RpcResponse { Error = "RPC channel closed before a response arrived." });

            _pending.Clear();

            Closed?.Invoke(this);
        }

        public void Close()
        {
            _running = false;

            try
            {
                _tcp.Close();
            }
            catch (Exception e)
            {
                Log.Debug("RpcConnection", "Error closing channel: " + e.Message);
            }
        }

        public void Dispose() => Close();

        private sealed class PendingCall
        {
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);

            public RpcResponse Response { get; private set; }

            public void Complete(RpcResponse response)
            {
                Response = response;
                Completed.Set();
            }
        }
    }
}
