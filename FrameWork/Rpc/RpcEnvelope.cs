using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;

namespace FrameWork
{
    public enum RpcFrameType : byte
    {
        Request = 1,
        Response = 2
    }

    /// <summary>A single remote method invocation.</summary>
    public sealed class RpcRequest
    {
        public long Id { get; set; }

        /// <summary>Contract interface name the call is routed to.</summary>
        public string Service { get; set; }

        public string Method { get; set; }

        /// <summary>Declared parameter type names, used to resolve overloads.</summary>
        public string[] ParameterTypes { get; set; }

        /// <summary>Arguments, each serialized independently.</summary>
        public string[] Arguments { get; set; }
    }

    /// <summary>The result of a remote method invocation.</summary>
    public sealed class RpcResponse
    {
        public long Id { get; set; }

        public string Result { get; set; }

        /// <summary>Values of out/ref parameters, aligned with the request arguments.</summary>
        public string[] ByRefArguments { get; set; }

        /// <summary>Non-null when the call threw on the remote side.</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// JSON payload serialization for RPC. Arguments and return values are encoded
    /// individually against their declared types, so no polymorphic type metadata
    /// is carried on the wire.
    /// </summary>
    public static class RpcSerializer
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            TypeNameHandling = TypeNameHandling.None,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        public static string Serialize(object value, Type declaredType)
        {
            if (value == null)
                return null;

            return JsonConvert.SerializeObject(value, declaredType, Settings);
        }

        public static object Deserialize(string payload, Type declaredType)
        {
            if (payload == null)
                return declaredType.IsValueType ? Activator.CreateInstance(declaredType) : null;

            return JsonConvert.DeserializeObject(payload, declaredType, Settings);
        }

        public static byte[] SerializeFrame(RpcFrameType type, object envelope)
        {
            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(envelope, Settings));
            byte[] frame = new byte[RpcFraming.HeaderLength + json.Length];

            // Length covers the frame type byte plus the payload.
            BitConverter.GetBytes(json.Length + 1).CopyTo(frame, 0);
            frame[4] = (byte)type;
            json.CopyTo(frame, RpcFraming.HeaderLength);

            return frame;
        }

        public static T DeserializeEnvelope<T>(byte[] payload, int offset, int count)
        {
            return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(payload, offset, count), Settings);
        }
    }

    public static class RpcFraming
    {
        /// <summary>4 byte length prefix plus a single frame type byte.</summary>
        public const int HeaderLength = 5;

        /// <summary>Guards against a malformed peer allocating unbounded memory.</summary>
        public const int MaxFrameLength = 32 * 1024 * 1024;

        /// <summary>Reads exactly <paramref name="count"/> bytes or throws.</summary>
        public static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            int read = 0;

            while (read < count)
            {
                int chunk = stream.Read(buffer, offset + read, count - read);

                if (chunk <= 0)
                    throw new EndOfStreamException("RPC peer closed the connection.");

                read += chunk;
            }
        }
    }
}
