using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Structs;

using System.Runtime.InteropServices;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MicroCell
    {
        // 2 байта: Что за объект/стена/мебель находится в этой МИКРО-точке.
        // 0 = пусто. 1 = стена из гранита, 2 = ножка стола, 3 = кусок скалы...
        public ushort EdificeId;

        // 2 байта: Какой пол в этой МИКРО-точке.
        // Позволяет делать плавные биомы (песок переходит в траву попиксельно!)
        public ushort FloorId;

        // 1 байт: Высота (для рендеринга и логики, как у вас и было)
        public byte Height;

        // 1 байт: Тип крыши или спец-зоны (0 = небо, 1 = тонкая крыша, 2 = скала)
        public byte RoofType;

        // 2 байта: Флаги состояния (Битовая маска)
        // Бит 0: Горит? Бит 1: Грязь? Бит 2: Проходимо для муравьев?
        public ushort Flags;

    }

