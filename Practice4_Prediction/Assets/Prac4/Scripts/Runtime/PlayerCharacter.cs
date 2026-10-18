using Unity.Netcode;
using UnityEngine;

namespace Prac4
{
    /// <summary>
    /// Персонаж (роль ACharacter): внешний вид, камера владельца, маркер авторитетной позиции.
    /// Само движение и способность «рывок» реализованы в PredictedMovement / MovementSim.
    /// </summary>
    [RequireComponent(typeof(PredictedMovement))]
    public class PlayerCharacter : NetworkBehaviour
    {
        public Renderer BodyRenderer;
        [Tooltip("Маркер последней подтверждённой сервером позиции (виден только владельцу).")]
        public Transform ServerGhost;
        public Vector3 CameraOffset = new Vector3(0f, 11f, -9f);

        static readonly Color OwnerColor = new Color(0.2f, 0.75f, 0.3f);
        static readonly Color OtherColor = new Color(0.85f, 0.3f, 0.25f);
        static readonly Color DashColor = new Color(1f, 0.9f, 0.2f);

        PredictedMovement m_Movement;
        Material m_Material;
        Color m_BaseColor;

        void Awake()
        {
            m_Movement = GetComponent<PredictedMovement>();
        }

        public override void OnNetworkSpawn()
        {
            m_BaseColor = IsOwner ? OwnerColor : OtherColor;
            if (BodyRenderer != null)
            {
                m_Material = BodyRenderer.material; // копия материала для этого экземпляра
                m_Material.color = m_BaseColor;
            }
            if (ServerGhost != null)
            {
                ServerGhost.SetParent(null, true); // маркер живёт в мировых координатах
                ServerGhost.gameObject.SetActive(IsOwner && !IsServer);
            }
            gameObject.name = $"Player_{OwnerClientId}" + (IsOwner ? " (local)" : "");
        }

        public override void OnNetworkDespawn()
        {
            if (ServerGhost != null)
                Destroy(ServerGhost.gameObject);
        }

        void LateUpdate()
        {
            if (!IsSpawned)
                return;

            if (m_Material != null)
                m_Material.color = m_Movement.IsDashingVisual ? DashColor : m_BaseColor;

            if (ServerGhost != null && ServerGhost.gameObject.activeSelf && m_Movement.HasServerState)
                ServerGhost.position = m_Movement.LastServerState.Position + Vector3.down * 0.95f;

            if (IsOwner)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 target = m_Movement.Visual != null ? m_Movement.Visual.position : transform.position;
                    cam.transform.position = Vector3.Lerp(cam.transform.position, target + CameraOffset,
                        1f - Mathf.Exp(-8f * Time.deltaTime));
                    cam.transform.rotation = Quaternion.LookRotation(-CameraOffset);
                }
            }
        }

        void OnGUI()
        {
            if (!IsSpawned)
                return;
            var cam = Camera.main;
            if (cam == null)
                return;
            Vector3 head = (m_Movement.Visual != null ? m_Movement.Visual.position : transform.position) + Vector3.up * 1.4f;
            Vector3 sp = cam.WorldToScreenPoint(head);
            if (sp.z < 0f)
                return;
            string label = $"Игрок {OwnerClientId}\n{m_Movement.RoleName()}";
            var rect = new Rect(sp.x - 110f, Screen.height - sp.y - 34f, 220f, 40f);
            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
            style.normal.textColor = Color.white;
            GUI.Label(rect, label, style);
        }
    }
}
