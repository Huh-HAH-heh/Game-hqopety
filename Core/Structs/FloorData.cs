using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Structs
{
    public struct FloorData
    {
        /// <summary>
        /// Тип пола.
        /// 0 = нет пола.
        /// </summary>
        public ushort Id;

        /// <summary>
        /// Высота пола внутри клетки.
        /// </summary>
        public byte Height;
    }
    public struct RoofData
    {
        /// <summary>
        /// Тип крыши.
        /// 0 = отсутствует.
        /// </summary>
        public byte Type;
    }
    //public struct WallData
    //{
    //    /// <summary>
    //    /// Северная стена.
    //    /// </summary>
    //    public ushort North;

    //    /// <summary>
    //    /// Южная стена.
    //    /// </summary>
    //    public ushort South;

    //    /// <summary>
    //    /// Западная стена.
    //    /// </summary>
    //    public ushort West;

    //    /// <summary>
    //    /// Восточная стена.
    //    /// </summary>
    //    public ushort East;
    //}
    public struct SolidData
    {
        /// <summary>
        /// Заполнение объема.
        /// 0 = пусто.
        /// </summary>
        public ushort Id;
    }
    public struct EdificeData
    {
        /// <summary>
        /// ID здания/мебели.
        /// 0 = отсутствует.
        /// </summary>
        public int Id;
    }
}
