# Полиморфная система восприятия RimClone

## Контракт

IPerceptionSense — подключаемый модуль одного канала восприятия. У каждого модуля есть стабильный уникальный Id, канал PerceptionSense, флаг Enabled, метод обновления и callbacks жизненного цикла юнита.

PerceptionSystem хранит список модулей и обновляет их в порядке регистрации. Он же продвигает часы общей PerceptionMemoryStore и передаёт жизненный цикл юнитов всем датчикам. Модули публикуют результаты в один sink наблюдений; отдельной памяти контактов на сенсор создавать не нужно.

Общее состояние наблюдения остаётся в PerceptionObservation: предполагаемая позиция, радиус ошибки, интенсивность, уверенность обнаружения и идентификации, временные метки, срок действия и необязательная идентичность источника.

## Добавление чувства

1. Создать класс, наследующий PerceptionSenseBase.
2. Задать уникальный Id и существующий конкретный PerceptionSense-канал.
3. Реализовать UpdateCore(in PerceptionSenseContext context), обнаружение и правила уверенности конкретного чувства.
4. Для каждого наблюдателя публиковать PerceptionObservation через context.Publish(Channel, observerIndex, observation). Указанный канал должен совпадать с каналом датчика.
5. Зарегистрировать экземпляр в composition root: simulation.Perception.Register(new ThermalSense(...)).

Для источника, у которого пока нет специализированного типа события, используйте TacticalStimulusType.SensorReading и указывайте канал явно. Для новых сенсорных модулей не нужно добавлять ветки в UnitSimulation.Update, PerceptionSystem или общий код слияния памяти.

Сенсор не должен напрямую выбирать намерение ИИ или выдавать точные координаты, если его метод обнаружения обеспечивает лишь приблизительную оценку. Он сообщает только наблюдение; актуальность, уверенность и объединение контактов остаются обязанностью общей памяти.

## Подключённые модули

- visual — адаптер существующего VisionSystem; публикует только цели, обнаруженные последним обновлением зрения.
- auditory — TacticalHearingSystem; сохраняет текущую модель выстрелов, шагов, близких пролётов пуль и ударов по укрытиям.

Порядок регистрации имеет значение. В базовой конфигурации слух обновляется первым, затем зрение: это сохраняет прежний порядок обработки звуковых импульсов относительно обновления зрительного кэша. Остальные системы игры продолжают использовать тот же экземпляр VisionSystem.


## Pawn-AI signal semantics

Sensors do not choose a pawn's action. Each observation may additionally publish
semantic affordances: Investigate, ImmediateDanger, PotentialThreat, Resource,
Avoid, and Social. The common contact memory fuses the active flags from all live
evidence, independently of which sensor supplied it.

The Behaviac/Utility layer consumes these semantics rather than querying the
hearing module directly. An investigable contact is selected using its effective
confidence, distance and position-error radius. Immediate-danger evidence is
observer-specific and decays quickly. A thermal, smell, vibration or other future
sensor can therefore influence the same policy by publishing an observation with
the appropriate flags; it does not need a new branch in the AI's main update loop.

Default policy for the existing event bridge:
- gunshots, footsteps and ally contact reports suggest an area to investigate;
- near misses, cover impacts and explosions signal immediate danger;
- other event types receive no tactical affordance unless the producing sensor
  explicitly supplies one.
