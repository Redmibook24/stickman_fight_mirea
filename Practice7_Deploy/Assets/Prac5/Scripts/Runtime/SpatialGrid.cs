using System.Collections.Generic;
using UnityEngine;

namespace Prac5
{
    /// <summary>
    /// Пространственная сетка (аналог UReplicationGraphNode_GridSpatialization2D): мир разбит на
    /// квадратные ячейки, каждый актор лежит в своей ячейке. Для клиента перебираются только ячейки
    /// вокруг его персонажа, а не все акторы мира.
    /// </summary>
    public class SpatialGrid
    {
        readonly float m_CellSize;
        readonly Dictionary<Vector2Int, List<int>> m_Cells = new Dictionary<Vector2Int, List<int>>();
        readonly List<Vector2Int> m_EntityCell = new List<Vector2Int>();
        readonly List<bool> m_Placed = new List<bool>();

        public SpatialGrid(float cellSize)
        {
            m_CellSize = cellSize;
        }

        public float CellSize => m_CellSize;

        public Vector2Int CellOf(Vector3 p) =>
            new Vector2Int(Mathf.FloorToInt(p.x / m_CellSize), Mathf.FloorToInt(p.z / m_CellSize));

        /// <summary>Обновляет ячейку актора; перемещение между списками только при смене ячейки.</summary>
        public void Update(int index, Vector3 position)
        {
            while (m_EntityCell.Count <= index)
            {
                m_EntityCell.Add(default);
                m_Placed.Add(false);
            }

            Vector2Int cell = CellOf(position);
            if (m_Placed[index])
            {
                if (m_EntityCell[index] == cell)
                    return;
                m_Cells[m_EntityCell[index]].Remove(index);
            }
            if (!m_Cells.TryGetValue(cell, out var list))
                m_Cells[cell] = list = new List<int>();
            list.Add(index);
            m_EntityCell[index] = cell;
            m_Placed[index] = true;
        }

        /// <summary>Добавляет в result индексы акторов из ячеек в радиусе radius от точки.</summary>
        public void Gather(Vector3 center, float radius, List<int> result)
        {
            Vector2Int c = CellOf(center);
            int r = Mathf.CeilToInt(radius / m_CellSize);
            for (int x = c.x - r; x <= c.x + r; x++)
            for (int y = c.y - r; y <= c.y + r; y++)
                if (m_Cells.TryGetValue(new Vector2Int(x, y), out var list))
                    result.AddRange(list);
        }

        public void Clear()
        {
            m_Cells.Clear();
            m_EntityCell.Clear();
            m_Placed.Clear();
        }
    }
}
