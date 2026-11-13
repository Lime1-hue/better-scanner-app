// Small helpers that keep one codebase building for both .NET 10 (Windows 10/11)
// and .NET Framework 4.8 (Windows 7). Everything under #if NETFRAMEWORK fills in
// APIs that only exist in modern .NET.

using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace BetterScanner
{
    internal static class Num
    {
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    }

    /// <summary>Monotonic milliseconds (Environment.TickCount64 is .NET Core only).</summary>
    internal static class MonoClock
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        public static long Ms => Watch.ElapsedMilliseconds;
    }

    internal static class StreamExt
    {
        public static void WriteAll(this Stream s, byte[] data) => s.Write(data, 0, data.Length);
    }

    /// <summary>JSON: System.Text.Json on .NET 10, the built-in JavaScriptSerializer on .NET Framework.</summary>
    internal static class Json
    {
#if NETFRAMEWORK
        public static string Serialize(object o, bool indented = false) =>
            new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(o);

        public static T? Deserialize<T>(string s) where T : class =>
            new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<T>(s);
#else
        private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

        public static string Serialize(object o, bool indented = false) =>
            System.Text.Json.JsonSerializer.Serialize(o, o.GetType(), indented ? Indented : null);

        public static T? Deserialize<T>(string s) where T : class =>
            System.Text.Json.JsonSerializer.Deserialize<T>(s);
#endif
    }

#if NETFRAMEWORK
    internal static class Polyfills
    {
        public static bool Contains(this string s, string value, StringComparison comparison) =>
            s.IndexOf(value, comparison) >= 0;

        public static Task<string> ReadAsStringAsync(this HttpContent c, CancellationToken _) => c.ReadAsStringAsync();

        public static Task<byte[]> ReadAsByteArrayAsync(this HttpContent c, CancellationToken _) => c.ReadAsByteArrayAsync();

        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
        {
            key = kv.Key;
            value = kv.Value;
        }

        public static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int size)
        {
            var buf = new List<T>(size);
            foreach (var item in source)
            {
                buf.Add(item);
                if (buf.Count == size)
                {
                    yield return buf.ToArray();
                    buf.Clear();
                }
            }
            if (buf.Count > 0) yield return buf.ToArray();
        }

        /// <summary>UdpClient.ReceiveAsync with cancellation (the socket is closed by the caller afterwards).</summary>
        public static async Task<UdpReceiveResult> ReceiveAsync(this UdpClient u, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var receive = u.ReceiveAsync();
            var cancelled = new TaskCompletionSource<bool>();
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(receive, cancelled.Task).ConfigureAwait(false) != receive)
                {
                    // Observe the abandoned receive so its eventual error isn't reported as unobserved.
                    _ = receive.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw new OperationCanceledException(ct);
                }
            }
            return await receive.ConfigureAwait(false);
        }
    }
#endif
}

#if NETFRAMEWORK
// Compiler support types for records / init / required on .NET Framework.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;
        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
#endif
