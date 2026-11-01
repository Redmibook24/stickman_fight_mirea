using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;

namespace Prac5
{
    /// <summary>
    /// Профилировщик сетевого трафика — аналог «netprofile enable / disable» в Unreal.
    /// Раз в секунду снимает разность счётчиков и, если идёт запись, пишет строку в CSV.
    /// По «netprofile disable» сохраняет итог (средние за время записи) в JSON.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class TrafficProfiler : MonoBehaviour
    {
        public string OutputDirectory;

        public struct Sample
        {
            public float Time;
            public int Clients;
            public double TotalKBps;       // исходящий трафик сервера (вся полезная нагрузка Netcode)
            public double SendsPerSec;
            public double RepKBps;         // из них — сообщения репликации акторов
            public double[] KindKBps;
            public double RecordsPerSec;
            public double OpensPerSec;
            public double ClosesPerSec;
            public double ConsideredPerSec;
            public double DormantSkippedPerSec;
            public double StarvedPerSec;
            public double RelevantPerClient;
            public double RepMsPerTick;
            public double ClientRepKBps;   // клиент: принято сообщений репликации
            public double ClientRecordsPerSec;
            public int ClientVisible;
        }

        public Sample Last { get; private set; }
        public bool Capturing { get; private set; }
        public string CapturePath { get; private set; }

        NetworkManager m_Net;
        float m_NextSample;
        float m_LastSampleTime;
        long m_Bytes, m_Sends, m_Rep, m_Records, m_Opens, m_Closes, m_Considered, m_Dormant, m_Starved, m_Relevant, m_Ticks;
        long m_ClientBytes, m_ClientRecords;
        double m_RepMs;
        readonly long[] m_KindBytes = new long[RepPolicies.KindCount];

        // накопление за время записи
        string m_Label;
        int m_Count;
        Sample m_Sum;
        double[] m_SumKind;
        float m_CaptureStart;
        StreamWriter m_Csv;

        static readonly string[] KindNames = { "tree", "crate", "pickup", "bot", "projectile" };

        void Awake()
        {
            m_Net = GetComponent<NetworkManager>();
            if (string.IsNullOrEmpty(OutputDirectory))
                OutputDirectory = Path.Combine(Application.persistentDataPath, "netprofile");
        }

        void Update()
        {
            if (Time.unscaledTime < m_NextSample)
                return;
            m_NextSample = Time.unscaledTime + 1f;
            TakeSample();
        }

        void TakeSample()
        {
            float now = Time.unscaledTime;
            float dt = Mathf.Max(now - m_LastSampleTime, 0.001f);
            m_LastSampleTime = now;

            long bytes = CountingTransport.BytesSent, sends = CountingTransport.SendCalls;
            long rep = RepStats.TotalBytes(), records = RepStats.TotalRecords();
            long ticks = RepStats.Ticks;
            var s = new Sample
            {
                Time = now,
                Clients = m_Net.IsServer ? m_Net.ConnectedClientsIds.Count - (m_Net.IsHost ? 1 : 0) : 0,
                TotalKBps = (bytes - m_Bytes) / 1024.0 / dt,
                SendsPerSec = (sends - m_Sends) / dt,
                RepKBps = (rep - m_Rep) / 1024.0 / dt,
                KindKBps = new double[RepPolicies.KindCount],
                RecordsPerSec = (records - m_Records) / dt,
                OpensPerSec = (RepStats.OpenRecords - m_Opens) / dt,
                ClosesPerSec = (RepStats.CloseRecords - m_Closes) / dt,
                ConsideredPerSec = (RepStats.ActorsConsidered - m_Considered) / dt,
                DormantSkippedPerSec = (RepStats.DormantSkipped - m_Dormant) / dt,
                StarvedPerSec = (RepStats.StarvedRecords - m_Starved) / dt,
                ClientRepKBps = (RepStats.ClientBytesReceived - m_ClientBytes) / 1024.0 / dt,
                ClientRecordsPerSec = (RepStats.ClientRecordsReceived - m_ClientRecords) / dt,
                ClientVisible = ReplicatedEntity.ClientVisibleCount,
            };
            for (int k = 0; k < RepPolicies.KindCount; k++)
            {
                s.KindKBps[k] = (RepStats.BytesByKind[k] - m_KindBytes[k]) / 1024.0 / dt;
                m_KindBytes[k] = RepStats.BytesByKind[k];
            }
            long dTicks = ticks - m_Ticks;
            int conns = Mathf.Max(s.Clients, 1);
            s.RelevantPerClient = dTicks > 0 ? (RepStats.RelevantPairs - m_Relevant) / (double)dTicks / conns : 0;
            s.RepMsPerTick = dTicks > 0 ? (RepStats.ReplicationMs - m_RepMs) / dTicks : 0;

            m_Bytes = bytes; m_Sends = sends; m_Rep = rep; m_Records = records;
            m_Opens = RepStats.OpenRecords; m_Closes = RepStats.CloseRecords;
            m_Considered = RepStats.ActorsConsidered; m_Dormant = RepStats.DormantSkipped;
            m_Starved = RepStats.StarvedRecords; m_Relevant = RepStats.RelevantPairs; m_Ticks = ticks;
            m_RepMs = RepStats.ReplicationMs;
            m_ClientBytes = RepStats.ClientBytesReceived; m_ClientRecords = RepStats.ClientRecordsReceived;

            Last = s;
            if (Capturing)
                Accumulate(s);
        }

        // ───────────── netprofile enable / disable ─────────────

        public void Enable(string label)
        {
            if (Capturing) Disable();
            Directory.CreateDirectory(OutputDirectory);
            m_Label = label;
            m_Count = 0;
            m_Sum = default;
            m_SumKind = new double[RepPolicies.KindCount];
            m_CaptureStart = Time.unscaledTime;
            string role = m_Net.IsServer ? "server" : "client" + m_Net.LocalClientId;
            CapturePath = Path.Combine(OutputDirectory, $"netprofile_{label}_{role}.csv");
            m_Csv = new StreamWriter(CapturePath, false, new UTF8Encoding(false));
            m_Csv.WriteLine("t,clients,total_kbps,sends_per_s,rep_kbps," +
                            string.Join(",", Array.ConvertAll(KindNames, k => k + "_kbps")) +
                            ",records_per_s,opens_per_s,closes_per_s,considered_per_s,dormant_skipped_per_s," +
                            "starved_per_s,relevant_per_client,rep_ms_per_tick,client_rep_kbps,client_records_per_s,client_visible");
            Capturing = true;
            m_NextSample = Time.unscaledTime + 1f;
            TakeSample(); // обнуляем разности на момент начала записи
            m_Count = 0;
            m_Sum = default;
            m_SumKind = new double[RepPolicies.KindCount];
            Prac4.NetLab.Log($"netprofile enable → {CapturePath}");
        }

        void Accumulate(Sample s)
        {
            m_Count++;
            m_Sum.Clients += s.Clients;
            m_Sum.TotalKBps += s.TotalKBps;
            m_Sum.SendsPerSec += s.SendsPerSec;
            m_Sum.RepKBps += s.RepKBps;
            for (int k = 0; k < RepPolicies.KindCount; k++)
                m_SumKind[k] += s.KindKBps[k];
            m_Sum.RecordsPerSec += s.RecordsPerSec;
            m_Sum.OpensPerSec += s.OpensPerSec;
            m_Sum.ClosesPerSec += s.ClosesPerSec;
            m_Sum.ConsideredPerSec += s.ConsideredPerSec;
            m_Sum.DormantSkippedPerSec += s.DormantSkippedPerSec;
            m_Sum.StarvedPerSec += s.StarvedPerSec;
            m_Sum.RelevantPerClient += s.RelevantPerClient;
            m_Sum.RepMsPerTick += s.RepMsPerTick;
            m_Sum.ClientRepKBps += s.ClientRepKBps;
            m_Sum.ClientRecordsPerSec += s.ClientRecordsPerSec;
            m_Sum.ClientVisible += s.ClientVisible;

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append((s.Time - m_CaptureStart).ToString("F1", inv)).Append(',').Append(s.Clients);
            foreach (double v in new[] { s.TotalKBps, s.SendsPerSec, s.RepKBps })
                sb.Append(',').Append(v.ToString("F3", inv));
            foreach (double v in s.KindKBps)
                sb.Append(',').Append(v.ToString("F3", inv));
            foreach (double v in new[] { s.RecordsPerSec, s.OpensPerSec, s.ClosesPerSec, s.ConsideredPerSec,
                         s.DormantSkippedPerSec, s.StarvedPerSec, s.RelevantPerClient, s.RepMsPerTick,
                         s.ClientRepKBps, s.ClientRecordsPerSec })
                sb.Append(',').Append(v.ToString("F3", inv));
            sb.Append(',').Append(s.ClientVisible);
            m_Csv.WriteLine(sb.ToString());
            m_Csv.Flush();
        }

        public string Disable()
        {
            if (!Capturing)
                return null;
            Capturing = false;
            m_Csv?.Dispose();
            m_Csv = null;

            int n = Mathf.Max(m_Count, 1);
            var inv = CultureInfo.InvariantCulture;
            string F(double v) => (v / n).ToString("F3", inv);
            var server = GetComponent<ReplicationServer>();
            var director = GetComponent<ArenaDirector>();

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"label\": \"{m_Label}\",\n");
            sb.Append($"  \"role\": \"{(m_Net.IsServer ? "server" : "client")}\",\n");
            sb.Append($"  \"optimizations\": \"{(server != null ? server.Optimizations.ToString() : "")}\",\n");
            sb.Append($"  \"seconds\": {m_Count},\n");
            sb.Append($"  \"replicated_actors\": {(director != null ? director.SpawnedCount : ReplicatedEntity.ClientEntities.Count)},\n");
            sb.Append($"  \"clients\": {F(m_Sum.Clients)},\n");
            sb.Append($"  \"total_kbps\": {F(m_Sum.TotalKBps)},\n");
            sb.Append($"  \"sends_per_s\": {F(m_Sum.SendsPerSec)},\n");
            sb.Append($"  \"rep_kbps\": {F(m_Sum.RepKBps)},\n");
            for (int k = 0; k < RepPolicies.KindCount; k++)
                sb.Append($"  \"{KindNames[k]}_kbps\": {F(m_SumKind[k])},\n");
            sb.Append($"  \"records_per_s\": {F(m_Sum.RecordsPerSec)},\n");
            sb.Append($"  \"opens_per_s\": {F(m_Sum.OpensPerSec)},\n");
            sb.Append($"  \"closes_per_s\": {F(m_Sum.ClosesPerSec)},\n");
            sb.Append($"  \"considered_per_s\": {F(m_Sum.ConsideredPerSec)},\n");
            sb.Append($"  \"dormant_skipped_per_s\": {F(m_Sum.DormantSkippedPerSec)},\n");
            sb.Append($"  \"starved_per_s\": {F(m_Sum.StarvedPerSec)},\n");
            sb.Append($"  \"relevant_per_client\": {F(m_Sum.RelevantPerClient)},\n");
            sb.Append($"  \"rep_ms_per_tick\": {F(m_Sum.RepMsPerTick)},\n");
            sb.Append($"  \"client_rep_kbps\": {F(m_Sum.ClientRepKBps)},\n");
            sb.Append($"  \"client_records_per_s\": {F(m_Sum.ClientRecordsPerSec)},\n");
            sb.Append($"  \"client_visible\": {F(m_Sum.ClientVisible)}\n");
            sb.Append("}\n");

            string path = Path.ChangeExtension(CapturePath, ".json");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Prac4.NetLab.Log($"netprofile disable → {path}");
            return path;
        }

        void OnDestroy()
        {
            m_Csv?.Dispose();
        }
    }
}
