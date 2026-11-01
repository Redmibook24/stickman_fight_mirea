using Unity.Netcode;
using UnityEngine;

namespace Prac5
{
    /// <summary>
    /// Автопилот игроков для воспроизводимых замеров: каждый игрок бежит по своей окружности вокруг центра
    /// арены (хост — по ближней, клиенты — по всё более дальним). Так все прогоны «до» и «после»
    /// идут по одному и тому же сценарию.
    /// </summary>
    public static class Autopilot
    {
        static readonly float[] RouteRadius = { 10f, 30f, 50f, 68f };

        public static bool Enabled => Prac4.PlayerInputReader.AutoMove != null;

        public static void SetEnabled(bool enabled)
        {
            Prac4.PlayerInputReader.AutoMove = enabled ? Steer : null;
        }

        public static Vector3 PointOnRoute(int route, float angleDeg)
        {
            float r = RouteRadius[route % RouteRadius.Length];
            float a = angleDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
        }

        public static Vector3 SpawnPoint(int route) => PointOnRoute(route, 0f) + Vector3.up * 1.1f;

        static Vector2 Steer(Prac4.PlayerInputReader reader)
        {
            var netObj = reader.GetComponent<NetworkObject>();
            int route = netObj != null ? (int)(netObj.OwnerClientId % (ulong)RouteRadius.Length) : 0;
            float radius = RouteRadius[route];
            float direction = route % 2 == 0 ? 1f : -1f;

            Vector3 pos = reader.transform.position;
            float angle = Mathf.Atan2(pos.z, pos.x);
            float lead = 8f / radius; // цель — точка окружности на 8 м впереди
            float targetAngle = angle + direction * lead;
            var target = new Vector3(Mathf.Cos(targetAngle) * radius, 0f, Mathf.Sin(targetAngle) * radius);
            Vector3 d = target - pos;
            d.y = 0f;
            return d.sqrMagnitude < 0.0001f ? Vector2.zero : new Vector2(d.x, d.z).normalized;
        }
    }
}
