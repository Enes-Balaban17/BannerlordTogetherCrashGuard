using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BannerlordTogether;
using BannerlordTogether.Network;
using HarmonyLib;

namespace BannerlordTogetherCrashGuard
{
    internal sealed class CompatibilityMessage
    {
        internal byte Kind;
        internal string Session;
        internal long Revision;
        internal Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static class CompatibilityWire
    {
        private static readonly byte[] Magic = { 0, 0x43, 0x47, 0x33, 1 };
        internal const int MaximumBytes = 48000;
        private const int MaximumValues = 512;
        private static readonly ConcurrentQueue<Tuple<CoopNetworkBase, NetworkEnvelope>> Incoming =
            new ConcurrentQueue<Tuple<CoopNetworkBase, NetworkEnvelope>>();

        internal static bool IsOurs(byte[] bytes)
        {
            if (bytes == null || bytes.Length < Magic.Length)
                return false;
            for (int index = 0; index < Magic.Length; index++)
                if (bytes[index] != Magic[index])
                    return false;
            return true;
        }

        internal static byte[] Encode(CompatibilityMessage message)
        {
            if (message.Values.Count > MaximumValues)
                throw new InvalidDataException("Too many compatibility values");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(message.Kind);
                writer.Write(message.Session ?? "");
                writer.Write(message.Revision);
                writer.Write(message.Values.Count);
                foreach (var item in message.Values)
                {
                    if (item.Key.Length > 256 || item.Value.Length > 32000)
                        throw new InvalidDataException("Oversized compatibility value");
                    writer.Write(item.Key);
                    writer.Write(item.Value);
                }
                writer.Flush();
                if (stream.Length > MaximumBytes)
                    throw new InvalidDataException("Compatibility message exceeds transport budget");
                return stream.ToArray();
            }
        }

        internal static CompatibilityMessage Decode(byte[] bytes)
        {
            if (!IsOurs(bytes) || bytes.Length > MaximumBytes)
                throw new InvalidDataException("Invalid compatibility envelope");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                stream.Position = Magic.Length;
                var result = new CompatibilityMessage
                {
                    Kind = reader.ReadByte(), Session = reader.ReadString(), Revision = reader.ReadInt64()
                };
                int count = reader.ReadInt32();
                if (result.Session.Length > 64 || count < 0 || count > MaximumValues)
                    throw new InvalidDataException("Invalid compatibility header");
                for (int index = 0; index < count; index++)
                {
                    string key = reader.ReadString();
                    string value = reader.ReadString();
                    if (key.Length > 256 || value.Length > 32000 || result.Values.ContainsKey(key))
                        throw new InvalidDataException("Invalid compatibility field");
                    result.Values.Add(key, value);
                }
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Trailing compatibility data");
                return result;
            }
        }

        internal static bool TryReceive(out CoopNetworkBase network, out NetworkEnvelope envelope)
        {
            if (Incoming.TryDequeue(out var item))
            {
                network = item.Item1;
                envelope = item.Item2;
                return true;
            }
            network = null;
            envelope = default;
            return false;
        }

        [HarmonyPatch(typeof(CoopNetworkBase), "EnqueueReceivedPacket", new[] { typeof(NetworkEnvelope) })]
        private static class ReceivePatch
        {
            private static bool Prefix(CoopNetworkBase __instance, NetworkEnvelope envelope)
            {
                if (!IsOurs(envelope.Data))
                    return true;
                if (envelope.Data.Length > MaximumBytes)
                    return false;
                // Network callbacks only queue. Campaign objects are touched on the game thread.
                if (IsOurs(envelope.Data) && DismembermentCompatibility.IsVisual(envelope.Data))
                    DismembermentCompatibility.Enqueue(__instance, envelope);
                else if (envelope.Data.Length <= MaximumBytes && Incoming.Count < 256)
                    Incoming.Enqueue(Tuple.Create(__instance, envelope));
                return false;
            }
        }

        [HarmonyPatch(typeof(CoopNetworkBase), "TryDequeueEnvelope")]
        private static class ReassembledReceivePatch
        {
            private static void Postfix(CoopNetworkBase __instance, NetworkEnvelope envelope, ref bool __result)
            {
                if (!__result || !IsOurs(envelope.Data))
                    return;
                if (envelope.Data.Length > MaximumBytes)
                {
                    __result = false;
                    return;
                }
                if (DismembermentCompatibility.IsVisual(envelope.Data))
                    DismembermentCompatibility.Enqueue(__instance, envelope);
                else if (envelope.Data.Length <= MaximumBytes && Incoming.Count < 256)
                    Incoming.Enqueue(Tuple.Create(__instance, envelope));
                __result = false;
            }
        }
    }
}
