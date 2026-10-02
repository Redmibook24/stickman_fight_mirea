namespace StickmanFight.Reliable.Protocol;

/// <summary>Тип пакета. Занимает 1 байт в заголовке, коды 1-4 совпадают с ПР №2.</summary>
public enum PacketType : byte
{
    Ping     = 1,   // замер RTT, подтверждения не требует
    Pong     = 2,   // ответ на PING, подтверждения не требует
    Movement = 3,   // смещение игрока; надёжность задаётся флагом requiresAck
    Shoot    = 4,   // выстрел; критичное событие, отправляется с requiresAck = 1
    Ack      = 5,   // подтверждение надёжного пакета; сам подтверждения не требует
}
