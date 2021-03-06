﻿using System;
using System.Collections.Generic;
using Equinox.Utils;
using Equinox.Utils.Pool;
using Equinox.Utils.Session;
using Equinox.Utils.Stream;
using Sandbox.ModAPI;
using VRage.Utils;
using VRage;
using VRage.Game;

namespace Equinox.Utils.Network
{
    public struct EndpointId
    {
        public readonly ulong Value;

        public EndpointId(ulong value)
        {
            Value = value;
        }

        public static readonly ulong BroadcastValue = ulong.MaxValue;
        public static readonly EndpointId Broadcast = BroadcastValue;

        public static implicit operator ulong(EndpointId endpoint)
        {
            return endpoint.Value;
        }
        public static implicit operator EndpointId(ulong endpoint)
        {
            return new EndpointId(endpoint);
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        public override bool Equals(object o)
        {
            return (o as EndpointId?)?.Value == Value;
        }

        public static bool operator ==(EndpointId a, EndpointId b)
        {
            return a.Value == b.Value;
        }

        public static bool operator !=(EndpointId a, EndpointId b)
        {
            return a.Value != b.Value;
        }
    }

    public class NetworkComponent : LoggingSessionComponent
    {
        private readonly ExactBufferPool m_bufferPool = new ExactBufferPool(4 * 1024, 1024 * 1024);
        private readonly TypedObjectPool m_packetPool = new TypedObjectPool(4, 1024 * 2);
        public const ushort MessageChannel = 57654;

        public static readonly Type[] SuppliedDeps = new[] { typeof(NetworkComponent) };
        public override IEnumerable<Type> SuppliedComponents => SuppliedDeps;

        private class PacketInfo
        {
            public ulong PacketID { get; }
            public Func<Packet> Activator { get; }
            public Action<Packet> Handler { get; }
            public Type Type { get; }

            public PacketInfo(ulong id, Type type, Func<Packet> activator, Action<Packet> handler)
            {
                PacketID = id;
                Type = type;
                Activator = activator;
                Handler = handler;
            }
        }
        private readonly FastResourceLock m_packetDbLock = new FastResourceLock();
        private readonly Dictionary<ulong, PacketInfo> m_registeredPacketsByID = new Dictionary<ulong, PacketInfo>();
        private readonly Dictionary<Type, PacketInfo> m_registeredPacketsByType = new Dictionary<Type, PacketInfo>();

        public static EndpointId ServerID => MyAPIGateway.Multiplayer.ServerId;
        // ReSharper disable once InconsistentNaming
        public static EndpointId Id => MyAPIGateway.Multiplayer.MyId;

        protected override void Attach()
        {
            base.Attach();
            if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE) return;
            MyAPIGateway.Multiplayer.RegisterMessageHandler(MessageChannel, MessageHandler);
        }

        protected override void Detach()
        {
            base.Detach();
            if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE) return;
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(MessageChannel, MessageHandler);
        }

        public void RegisterPacketType<T>(Action<T> handler) where T : Packet, new()
        {
            var info = new PacketInfo(typeof(T).FullName.Hash64(), typeof(T), AllocatePacket<T>, (x) => handler(x as T));
            using (m_packetDbLock.AcquireExclusiveUsing())
            {
                PacketInfo oldInfo;
                if (m_registeredPacketsByID.TryGetValue(info.PacketID, out oldInfo))
                {
                    Log(MyLogSeverity.Critical, "Registering packet {0} failed.  The ID collides with {1}", typeof(T), oldInfo.Type);
                    throw new ArgumentException($"Registering packet {typeof(T)} failed.  The ID collides with {oldInfo.Type}");
                }
                m_registeredPacketsByID.Add(info.PacketID, info);
                m_registeredPacketsByType.Add(info.Type, info);
            }
        }

        public void UnregisterPacketType<T>(Action<T> handler) where T : Packet, new()
        {
            using (m_packetDbLock.AcquireExclusiveUsing())
            {
                if (!m_registeredPacketsByID.Remove(typeof(T).FullName.Hash64()))
                    Log(MyLogSeverity.Warning, "Unregistered packet type {0} when it wasn't registered", typeof(T));
                m_registeredPacketsByType.Remove(typeof(T));
            }
        }

        public bool SendMessageToServer(Packet message, bool reliable = true)
        {
            if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE) return false;
            if (MyAPIGateway.Session.IsDecider())
            {
                m_registeredPacketsByType[message.GetType()].Handler.Invoke(message);
                return true;
            }
            var data = SerializePacket(message);
            var result = MyAPIGateway.Multiplayer.SendMessageToServer(MessageChannel, data, reliable);
            m_bufferPool.Return(data);
            return result;
        }

        public bool SendMessage(Packet message, EndpointId recipient, bool reliable = true)
        {
            if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE) return false;
            if (!MyAPIGateway.Session.IsDecider())
            {
                Log(MyLogSeverity.Warning, "Can't send a packet as a server when aren't one.");
                return false;
            }
            if (recipient == Id)
            {
                m_registeredPacketsByType[message.GetType()].Handler.Invoke(message);
                return true;
            }
            message.Source = Id;
            var data = SerializePacket(message);
            bool result;
            // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
            if (recipient == EndpointId.Broadcast)
                result = MyAPIGateway.Multiplayer.SendMessageToOthers(MessageChannel, data, reliable);
            else
                result = MyAPIGateway.Multiplayer.SendMessageTo(MessageChannel, data, recipient, reliable);
            m_bufferPool.Return(data);
            return result;
        }

        public bool SendMessage(Packet message, IEnumerable<EndpointId> recipients, bool reliable = true)
        {
            if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE) return false;
            if (!MyAPIGateway.Session.IsDecider())
            {
                Log(MyLogSeverity.Warning, "Can't send a packet as a server when aren't one.");
                return false;
            }
            message.Source = Id;
            var data = SerializePacket(message);
            var result = true;
            foreach (var recipient in recipients)
            {
                if (recipient == Id)
                    m_registeredPacketsByType[message.GetType()].Handler.Invoke(message);
                else
                    result &= MyAPIGateway.Multiplayer.SendMessageTo(MessageChannel, data, recipient, reliable);
            }
            m_bufferPool.Return(data);
            return result;
        }

        public bool SendMessageGeneric(Packet message, EndpointId recipient, bool reliable = true)
        {
            // ReSharper disable once ConvertIfStatementToReturnStatement
            if (MyAPIGateway.Session.IsDecider())
                return SendMessageToServer(message, reliable);
            return SendMessage(message, recipient, reliable);
        }

        public T AllocatePacket<T>() where T : Packet, new()
        {
            return m_packetPool.GetOrCreate<T>();
        }

        public void ReturnPacket<T>(T packet) where T : Packet
        {
            m_packetPool.Return(packet);
        }

        private byte[] SerializePacket(Packet packet)
        {
            PacketInfo info;
            using (m_packetDbLock.AcquireSharedUsing())
                if (!m_registeredPacketsByType.TryGetValue(packet.GetType(), out info))
                    throw new ArgumentException($"Packet type {packet.GetType()} wasn't registered");
            byte[] serialized;
            using (var stream = MemoryStream.CreateEmptyStream(8192))
            {
                stream.Write(info.PacketID);
                packet.WriteTo(stream);
                serialized = m_bufferPool.GetOrCreate(stream.WriteHead);
                Array.Copy(stream.Backing, 0, serialized, 0, stream.WriteHead);
            }
            return serialized;
        }

        private void MessageHandler(byte[] data)
        {
            if (data.Length < 8) return;
            var stream = MemoryStream.CreateReaderFor(data);
            var packetID = stream.ReadUInt64();
            PacketInfo info;
            using (m_packetDbLock.AcquireSharedUsing())
                if (!m_registeredPacketsByID.TryGetValue(packetID, out info))
                {
                    Log(MyLogSeverity.Warning, "Unknown packet ID {0}", packetID);
                    return;
                }
            try
            {
                var packet = info.Activator.Invoke();
                packet.ReadFrom(stream);
                info.Handler.Invoke(packet);
                ReturnPacket(packet);
            }
            catch (Exception e)
            {
                Log(MyLogSeverity.Critical, "Failed to parse packet of type {0}. Error:\n{1}", info.Type, e);
            }
        }


        public override void LoadConfiguration(Ob_ModSessionComponent config)
        {
            if (config == null) return;
            if (config is Ob_Network) return;
            Log(MyLogSeverity.Critical, "Configuration type {0} doesn't match component type {1}", config.GetType(), GetType());
        }

        public override Ob_ModSessionComponent SaveConfiguration()
        {
            return new Ob_Network();
        }
    }

    public class Ob_Network : Ob_ModSessionComponent
    {

    }
}
