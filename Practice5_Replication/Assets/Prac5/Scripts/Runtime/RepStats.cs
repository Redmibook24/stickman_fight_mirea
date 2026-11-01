namespace Prac5
{
    /// <summary>Накопительные счётчики сервера репликации. TrafficProfiler снимает с них разности раз в секунду.</summary>
    public static class RepStats
    {
        public static readonly long[] BytesByKind = new long[RepPolicies.KindCount];
        public static readonly long[] RecordsByKind = new long[RepPolicies.KindCount];
        public static long HeaderBytes;       // заголовки сообщений репликации
        public static long CloseRecords;      // закрытия «каналов» (актор ушёл из зоны релевантности)
        public static long OpenRecords;       // открытия (актор вошёл в зону релевантности)
        public static long ActorsConsidered;  // акторов, обработанных за кадр репликации (нагрузка на CPU сервера)
        public static long DormantSkipped;    // акторов, пропущенных благодаря dormancy
        public static long StarvedRecords;    // записей, отложенных из-за бюджета (Priority)
        public static long RelevantPairs;     // сумма по тикам и клиентам: сколько акторов релевантно клиенту
        public static long Ticks;
        public static double ReplicationMs;   // время работы ReplicationServer.Replicate

        // клиент
        public static long ClientBytesReceived;
        public static long ClientRecordsReceived;

        public static long TotalRecords()
        {
            long sum = 0;
            foreach (long r in RecordsByKind) sum += r;
            return sum;
        }

        public static long TotalBytes()
        {
            long sum = HeaderBytes;
            foreach (long b in BytesByKind) sum += b;
            return sum;
        }
    }
}
