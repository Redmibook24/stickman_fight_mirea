using UnityEngine;
using UnityEngine.InputSystem;

namespace Prac4
{
    /// <summary>
    /// Сбор локального ввода (роль PlayerController в Unreal). Работает только у владельца персонажа.
    /// Ввод превращается в команду InputCmd — намерение, а не готовую позицию.
    /// </summary>
    public class PlayerInputReader : MonoBehaviour
    {
        bool m_DashLatched;

        public float LastDashPressTime { get; private set; } = -1f;

        /// <summary>Вызывается каждый кадр: запоминаем нажатие, чтобы оно не потерялось между тиками.</summary>
        public void Poll()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return;
            if (kb.spaceKey.wasPressedThisFrame || kb.leftShiftKey.wasPressedThisFrame)
            {
                m_DashLatched = true;
                LastDashPressTime = Time.realtimeSinceStartup;
            }
        }

        public InputCmd BuildCmd(uint seq)
        {
            Vector2 move = Vector2.zero;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1f;
            }
            move = Vector2.ClampMagnitude(move, 1f);

            // «Чит» скорости: нечестный клиент шлёт увеличенный вектор — сервер обязан его обрезать.
            if (NetLab.CheatSpeed)
                move *= 1.8f;

            var cmd = new InputCmd { Seq = seq, Move = move, Dash = m_DashLatched };
            m_DashLatched = false;
            return cmd;
        }
    }
}
