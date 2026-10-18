using Unity.Netcode;
using UnityEngine;

namespace Prac4
{
    /// <summary>
    /// Правила матча (роль GameMode в Unreal): работает только на сервере, принимает подключения,
    /// решает, создавать ли игрока и где его разместить.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class GameRules : MonoBehaviour
    {
        public int MaxPlayers = 4;
        public Vector3[] SpawnPoints =
        {
            new Vector3(-3f, 1.1f, 0f),
            new Vector3(3f, 1.1f, 0f),
            new Vector3(-3f, 1.1f, 4f),
            new Vector3(3f, 1.1f, 4f),
        };

        NetworkManager m_Net;
        int m_Joined;

        void Awake()
        {
            m_Net = GetComponent<NetworkManager>();
            m_Net.NetworkConfig.ConnectionApproval = true;
            m_Net.ConnectionApprovalCallback = ApproveConnection;
            m_Net.OnClientConnectedCallback += id =>
            {
                if (m_Net.IsServer) NetLab.Log($"[СЕРВЕР] подключился клиент {id}");
            };
            m_Net.OnClientDisconnectCallback += id =>
            {
                if (m_Net.IsServer) NetLab.Log($"[СЕРВЕР] отключился клиент {id}");
            };
        }

        void ApproveConnection(NetworkManager.ConnectionApprovalRequest request,
                               NetworkManager.ConnectionApprovalResponse response)
        {
            if (m_Net.ConnectedClientsIds.Count >= MaxPlayers)
            {
                response.Approved = false;
                response.Reason = "Сервер заполнен";
                return;
            }

            response.Approved = true;
            response.CreatePlayerObject = true;
            response.Position = SpawnPoints[m_Joined % SpawnPoints.Length];
            response.Rotation = Quaternion.identity;
            m_Joined++;
        }
    }
}
