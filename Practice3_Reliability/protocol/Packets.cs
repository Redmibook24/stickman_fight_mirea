namespace StickmanFight.Reliable.Protocol;

/// <summary>Заголовок пакета: 8 байт, big-endian.</summary>
/// <param name="Type">packetType, 1 байт.</param>
/// <param name="Sequence">sequenceNumber, 2 байта: номер пакета в потоке отправителя.</param>
/// <param name="PayloadSize">payloadSize, 2 байта.</param>
/// <param name="Version">protocolVersion, 2 байта.</param>
/// <param name="RequiresAck">requiresAck, 1 байт: 1, если отправитель ждёт ACK.</param>
public readonly record struct PacketHeader(
    PacketType Type,
    ushort Sequence,
    ushort PayloadSize,
    ushort Version,
    bool RequiresAck);

/// <summary>Полезная нагрузка пакета. Конкретный тип однозначно задаётся packetType.</summary>
public abstract record Payload;

/// <summary>PING: время отправки по монотонным часам клиента, мкс.</summary>
public sealed record PingPayload(ulong ClientSendTimeUs) : Payload;

/// <summary>PONG: эхо времени клиента и две отметки сервера, мкс.</summary>
public sealed record PongPayload(ulong ClientSendTimeUs, ulong ServerReceiveTimeUs, ulong ServerSendTimeUs) : Payload;

/// <summary>MOVEMENT: желаемое смещение игрока по трём осям.</summary>
public sealed record MovementPayload(float Dx, float Dy, float Dz) : Payload;

/// <summary>SHOOT: оружие и направление выстрела.</summary>
public sealed record ShootPayload(byte WeaponId, float DirX, float DirY, float DirZ) : Payload;

/// <summary>ACK: номер подтверждаемого пакета.</summary>
public sealed record AckPayload(ushort AcknowledgedSequence) : Payload;

/// <summary>Разобранный пакет: заголовок и типизированная нагрузка.</summary>
public sealed record Packet(PacketHeader Header, Payload Payload);
