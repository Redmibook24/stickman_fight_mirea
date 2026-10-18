using Unity.Netcode;
using UnityEngine;

namespace Prac4
{
    /// <summary>
    /// Одна команда ввода (аналог SavedMove в Unreal). Клиент отправляет на сервер
    /// не позицию, а намерение: направление движения, флаг рывка и порядковый номер.
    /// </summary>
    public struct InputCmd : INetworkSerializable
    {
        public uint Seq;        // порядковый номер шага симуляции
        public Vector2 Move;    // направление (x — вправо, y — вперёд), |Move| <= 1 у честного клиента
        public bool Dash;       // запрос рывка в этом шаге

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Seq);
            serializer.SerializeValue(ref Move);
            serializer.SerializeValue(ref Dash);
        }
    }

    /// <summary>
    /// Пакет из нескольких последних неподтверждённых команд. Отправляется ненадёжным (unreliable)
    /// каналом; избыточность (каждая команда уходит до N раз) компенсирует потерю пакетов.
    /// </summary>
    public struct InputBatch : INetworkSerializable
    {
        public const int MaxCount = 8;
        public InputCmd[] Cmds;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            int count = Cmds?.Length ?? 0;
            serializer.SerializeValue(ref count);
            if (serializer.IsReader)
            {
                count = Mathf.Clamp(count, 0, MaxCount);
                Cmds = new InputCmd[count];
            }
            for (int i = 0; i < count; i++)
                serializer.SerializeValue(ref Cmds[i]);
        }
    }

    /// <summary>
    /// Полное состояние движения персонажа. Его достаточно, чтобы «перемотать» персонажа
    /// и повторно проиграть неподтверждённые вводы (reconciliation).
    /// </summary>
    public struct MoveState : INetworkSerializable
    {
        public Vector3 Position;
        public float VelY;
        public Vector3 Facing;
        public Vector3 DashDir;
        public int DashTicksLeft;     // сколько шагов ещё длится рывок
        public int CooldownTicksLeft; // сколько шагов до следующего разрешённого рывка

        public bool IsDashing => DashTicksLeft > 0;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref VelY);
            serializer.SerializeValue(ref Facing);
            serializer.SerializeValue(ref DashDir);
            serializer.SerializeValue(ref DashTicksLeft);
            serializer.SerializeValue(ref CooldownTicksLeft);
        }
    }

    public enum DashResult
    {
        None,
        Started,
        RejectedCooldown,
        RejectedNotGrounded,
        RejectedAlreadyDashing,
    }
}
