using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

namespace Prac5
{
    /// <summary>
    /// UnityTransport со счётчиком исходящих данных — источник цифр «исходящий трафик сервера».
    /// Считается полезная нагрузка, которую Netcode передаёт транспорту (пакеты Netcode со всеми
    /// его заголовками). Заголовки UTP/UDP/IP сюда не входят.
    /// </summary>
    public class CountingTransport : UnityTransport
    {
        public static long BytesSent;
        public static long SendCalls;

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
            BytesSent += payload.Count;
            SendCalls++;
            base.Send(clientId, payload, networkDelivery);
        }
    }
}
