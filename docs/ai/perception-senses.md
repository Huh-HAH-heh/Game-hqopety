# Полиморфное восприятие пешек

Каждое чувство реализует `IPerceptionSense` (обычно через `PerceptionSenseBase`). У модуля есть уникальный ID, канал, флаг включения и callbacks жизненного цикла. `PerceptionSystem` регистрирует модули, обновляет их в порядке регистрации и продвигает часы общей `PerceptionMemoryStore` один раз за тик.

Датчик сообщает наблюдения, а не истину о мире: позиция — оценка, погрешность и уверенность обязательны, идентификация отделена от обнаружения. Семантические признаки `Investigate`, `ImmediateDanger`, `PotentialThreat`, `Resource`, `Avoid` и `Social` описывают значение наблюдения для тактики независимо от сенсорного канала.

## Добавление нового чувства

1. Создать класс, наследующий `PerceptionSenseBase`.
2. Задать уникальный `Id` и конкретный канал `PerceptionSense`.
3. Реализовать `UpdateCore(in PerceptionSenseContext context)`.
4. Публиковать наблюдения через `context.Publish(Channel, observerIndex, observation)`.
5. Зарегистрировать модуль: `simulation.Perception.Register(new ThermalSense())`.

```csharp
public sealed class ThermalSense : PerceptionSenseBase
{
    public override string Id => "thermal";
    public override PerceptionSense Channel => PerceptionSense.Thermal;

    protected override void UpdateCore(in PerceptionSenseContext context)
    {
        // Определить приблизительную позицию и уверенность обнаружения.
        // Опубликовать PerceptionObservation с Signals = Investigate | PotentialThreat.
    }
}

simulation.Perception.Register(new ThermalSense());
```

Для неописанного типа события используйте `TacticalStimulusType.SensorReading` и обязательно указывайте конкретный канал. Новый сенсор не должен напрямую выбирать намерение пешки или раскрывать точные координаты, которых не мог определить.

Сейчас подключены `auditory` (существующая модель выстрелов, шагов и опасных импульсов) и `visual` (адаптер действующей системы зрения). Оба пишут в общую память контактов.
