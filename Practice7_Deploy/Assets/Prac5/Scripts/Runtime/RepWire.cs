using Unity.Netcode;
using UnityEngine;

namespace Prac5
{
    /// <summary>
    /// Формат сообщения репликации. Одно сообщение = заголовок (1 байт) + последовательность записей:
    ///   id (varint, NetworkObjectId) | mask (1 байт) | поля из маски.
    ///
    /// Без квантования поля пишутся «как есть» (FVector: 3×float = 12 байт, float = 4 байта).
    /// С квантованием (аналог FVector_NetQuantize100 / NetQuantize10 и сжатого FRotator):
    ///   позиция — 3×int16 с шагом 1 см (±327 м), скорость — 3×int16 с шагом 0,1 м/с,
    ///   поворот — 1 байт (шаг 1,4°), HP — 1 байт.
    /// Без push-based репликации маска всегда = All: каждый раз пересылается всё состояние.
    /// </summary>
    public static class RepWire
    {
        public const string MessageName = "Prac5.Replication";

        public const byte HeaderQuantized = 1 << 0;
        public const byte HeaderDelta = 1 << 1;

        /// <summary>Максимальный размер одной записи (без квантования, все поля).</summary>
        public const int MaxRecordSize = 10 + 1 + 12 + 4 + 12 + 4 + 2;

        const float PosScale = 100f; // 1 см
        const float VelScale = 10f;  // 0,1 м/с

        public static void WriteRecord(ref FastBufferWriter w, ulong id, RepField mask, in EntityState s, bool quantize)
        {
            BytePacker.WriteValueBitPacked(w, id);
            w.WriteValueSafe((byte)mask);
            if ((mask & RepField.Close) != 0)
                return;

            if ((mask & RepField.Position) != 0)
            {
                if (quantize) WriteQuantized(ref w, s.Position, PosScale);
                else w.WriteValueSafe(s.Position);
            }
            if ((mask & RepField.Yaw) != 0)
            {
                if (quantize) w.WriteValueSafe((byte)Mathf.RoundToInt(Mathf.Repeat(s.Yaw, 360f) / 360f * 256f));
                else w.WriteValueSafe(s.Yaw);
            }
            if ((mask & RepField.Velocity) != 0)
            {
                if (quantize) WriteQuantized(ref w, s.Velocity, VelScale);
                else w.WriteValueSafe(s.Velocity);
            }
            if ((mask & RepField.Hp) != 0)
            {
                if (quantize) w.WriteValueSafe((byte)Mathf.Clamp(Mathf.RoundToInt(s.Hp), 0, 255));
                else w.WriteValueSafe(s.Hp);
            }
            if ((mask & RepField.Flags) != 0)
            {
                w.WriteValueSafe((byte)(s.Active ? 1 : 0));
                w.WriteValueSafe(s.SpawnSeq);
            }
        }

        /// <summary>Читает запись; поля, отсутствующие в маске, остаются в state без изменений.</summary>
        public static void ReadRecord(ref FastBufferReader r, bool quantize, out ulong id, out RepField mask, ref EntityState s)
        {
            ByteUnpacker.ReadValueBitPacked(r, out id);
            r.ReadValueSafe(out byte m);
            mask = (RepField)m;
            if ((mask & RepField.Close) != 0)
                return;

            if ((mask & RepField.Position) != 0)
            {
                if (quantize) s.Position = ReadQuantized(ref r, PosScale);
                else r.ReadValueSafe(out s.Position);
            }
            if ((mask & RepField.Yaw) != 0)
            {
                if (quantize) { r.ReadValueSafe(out byte b); s.Yaw = b * 360f / 256f; }
                else r.ReadValueSafe(out s.Yaw);
            }
            if ((mask & RepField.Velocity) != 0)
            {
                if (quantize) s.Velocity = ReadQuantized(ref r, VelScale);
                else r.ReadValueSafe(out s.Velocity);
            }
            if ((mask & RepField.Hp) != 0)
            {
                if (quantize) { r.ReadValueSafe(out byte b); s.Hp = b; }
                else r.ReadValueSafe(out s.Hp);
            }
            if ((mask & RepField.Flags) != 0)
            {
                r.ReadValueSafe(out byte active);
                s.Active = active != 0;
                r.ReadValueSafe(out s.SpawnSeq);
            }
        }

        static void WriteQuantized(ref FastBufferWriter w, Vector3 v, float scale)
        {
            w.WriteValueSafe(ToShort(v.x * scale));
            w.WriteValueSafe(ToShort(v.y * scale));
            w.WriteValueSafe(ToShort(v.z * scale));
        }

        static Vector3 ReadQuantized(ref FastBufferReader r, float scale)
        {
            r.ReadValueSafe(out short x);
            r.ReadValueSafe(out short y);
            r.ReadValueSafe(out short z);
            return new Vector3(x / scale, y / scale, z / scale);
        }

        static short ToShort(float v) => (short)Mathf.Clamp(Mathf.RoundToInt(v), short.MinValue, short.MaxValue);
    }
}
