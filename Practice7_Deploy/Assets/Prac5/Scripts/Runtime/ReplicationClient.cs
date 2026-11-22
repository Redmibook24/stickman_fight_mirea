using Unity.Netcode;
using UnityEngine;

namespace Prac5
{
    /// <summary>Приём сообщений репликации на клиенте и раздача записей акторам.</summary>
    [RequireComponent(typeof(NetworkManager))]
    public class ReplicationClient : MonoBehaviour
    {
        NetworkManager m_Net;
        bool m_Registered;

        void Awake()
        {
            m_Net = GetComponent<NetworkManager>();
        }

        void OnEnable()
        {
            m_Net.OnClientStarted += OnClientStarted;
            m_Net.OnClientStopped += OnClientStopped;
        }

        void OnDisable()
        {
            m_Net.OnClientStarted -= OnClientStarted;
            m_Net.OnClientStopped -= OnClientStopped;
        }

        void OnClientStarted()
        {
            if (m_Net.IsServer || m_Registered)
                return;
            m_Net.CustomMessagingManager.RegisterNamedMessageHandler(RepWire.MessageName, OnMessage);
            m_Registered = true;
        }

        void OnClientStopped(bool wasHost)
        {
            if (m_Registered && m_Net.CustomMessagingManager != null)
                m_Net.CustomMessagingManager.UnregisterNamedMessageHandler(RepWire.MessageName);
            m_Registered = false;
            ReplicatedEntity.ClientEntities.Clear();
            ReplicatedEntity.ClientVisibleCount = 0;
        }

        void OnMessage(ulong sender, FastBufferReader reader)
        {
            RepStats.ClientBytesReceived += reader.Length;
            reader.ReadValueSafe(out byte header);
            bool quantize = (header & RepWire.HeaderQuantized) != 0;
            float now = Time.time;

            while (reader.Position < reader.Length)
            {
                ulong id = 0;
                RepField mask;
                ReplicatedEntity.ClientEntities.TryGetValue(PeekId(ref reader), out var entity);
                var state = entity != null ? entity.Known : default;
                RepWire.ReadRecord(ref reader, quantize, out id, out mask, ref state);
                RepStats.ClientRecordsReceived++;
                if (entity != null)
                    entity.ApplyRecord(mask, state, now);
            }
        }

        static ulong PeekId(ref FastBufferReader reader)
        {
            int pos = reader.Position;
            ByteUnpacker.ReadValueBitPacked(reader, out ulong id);
            reader.Seek(pos);
            return id;
        }
    }
}
