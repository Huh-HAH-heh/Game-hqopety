using Core.Unit;

namespace Core.World.Components
{
    /// <summary>
    /// Структура уличной грязи (Кровь, рвота, инсектная слизь). Хранится в плоском ECS массиве карты.
    /// </summary>
    public struct FilthInstance
    {
        public byte Type;         // 1 = Человеческая кровь, 2 = Кислотная слизь жука, 3 = Мусор
        public byte Thickness;    // Толщина слоя (от 1 до 5). Увеличивается при повторных ранениях в этой точке.
        public float Age;         // Сколько секунд грязь лежит на полу (кровь со временем запекается и темнеет)
    }

    /// <summary>
    /// Физический труп пешки на манер RimWorld. Занимает отдельное место в памяти предметов!
    /// </summary>
    public struct CorpseInstance
    {
        public int OriginalUnitId; // ID существа, которым этот труп был при жизни
        public UnitType SourceType;// Кем он был (Человек / Муравей)
        public float Freshness;    // Свежесть от 1.0f до 0.0f (При 0.0f труп превращается в Скелет)
        public uint MissingZones;  // Биты оторванных конечностей (если пуля СВД снесла ногу/голову)
        public int MicroX;         // Координата X приземления тела
        public int MicroY;         // Координата Y приземления тела
        public int ZLevel;
    }
}
