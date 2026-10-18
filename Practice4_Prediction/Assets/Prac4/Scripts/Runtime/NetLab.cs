using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Prac4
{
    /// <summary>
    /// Переключатели эксперимента и счётчики, которые выводятся в HUD и в лог-файл.
    /// Значения локальны для каждого процесса (у хоста и у клиента свои).
    /// </summary>
    public static class NetLab
    {
        // режимы эксперимента
        public static bool PredictMovement = true;  // локальное предсказание ходьбы
        public static bool PredictDash = true;      // локальное предсказание рывка (false — ждём ответ сервера)
        public static bool CheatIgnoreCooldown;     // намеренное расхождение: клиент игнорирует кулдаун
        public static bool CheatSpeed;              // намеренное расхождение: клиент бежит в 1.8 раза быстрее

        public static string ProfileName = "T1: без эмуляции";

        // статистика (клиент-владелец)
        public static float LastAckLatencyMs;
        public static float LastDashReactionMs = -1f;
        public static int Corrections;
        public static float LastCorrectionError;
        public static int PendingInputs;
        public static int ServerRejectsSeen;

        // статистика (сервер)
        public static int ServerRejects;
        public static int ServerLostInputs;

        public static readonly List<string> Lines = new List<string>();
        const int MaxLines = 14;

        static StreamWriter s_File;

        public static string LogFilePath { get; private set; }

        public static void ResetStats()
        {
            LastAckLatencyMs = 0f;
            LastDashReactionMs = -1f;
            Corrections = 0;
            LastCorrectionError = 0f;
            ServerRejectsSeen = 0;
            ServerRejects = 0;
            ServerLostInputs = 0;
        }

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Lines.Add(line);
            if (Lines.Count > MaxLines)
                Lines.RemoveAt(0);
            Debug.Log("[Prac4] " + line);

            try
            {
                if (s_File == null)
                {
                    LogFilePath = Path.Combine(Application.persistentDataPath,
                        $"prac4_log_{DateTime.Now:yyyyMMdd_HHmmss}_{System.Diagnostics.Process.GetCurrentProcess().Id}.txt");
                    s_File = new StreamWriter(LogFilePath, true) { AutoFlush = true };
                }
                s_File.WriteLine($"{line}\t[{ProfileName}; predictMove={PredictMovement}; predictDash={PredictDash}]");
            }
            catch (Exception)
            {
                // лог-файл — вспомогательный, ошибки записи игнорируем
            }
        }
    }
}
