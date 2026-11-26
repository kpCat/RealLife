using System;
using System.Collections.Generic;
using System.Linq;

namespace RealLife.Simulation
{
    public enum ConversationTopic { Greeting, Name, Activity, Wellbeing }

    public readonly struct ConversationResult
    {
        public bool Applied { get; }
        public string Text { get; }
        public int Encounters { get; }
        public int Regard { get; }

        internal ConversationResult(bool applied, string text, int encounters = 0, int regard = 0) =>
            (Applied, Text, Encounters, Regard) = (applied, text, encounters, regard);
    }

    [Serializable]
    public sealed class AcquaintanceSnapshot
    {
        public string ResidentId;
        public int Encounters, Regard;
        public long LastGreeting;
    }

    internal sealed class ResidentSocial
    {
        private readonly Dictionary<string, AcquaintanceSnapshot> memories =
            new Dictionary<string, AcquaintanceSnapshot>(StringComparer.Ordinal);

        public ConversationResult Talk(Resident person, ResidentViewData view, ConversationTopic topic, long tick)
        {
            memories.TryGetValue(person.Id, out var memory);
            if (person.Activity is ResidentActivity.Eat or ResidentActivity.Drink && person.Route == null ||
                person.Route == null && person.Activity == ResidentActivity.Rest && person.Fatigue > .8f)
                return new ConversationResult(false, $"{person.Name}: Давайте чуть позже, хорошо?",
                    memory?.Encounters ?? 0, memory?.Regard ?? 0);

            if (topic == ConversationTopic.Greeting)
            {
                var first = memory == null;
                if (first)
                    memories.Add(person.Id, memory = new AcquaintanceSnapshot { ResidentId = person.Id });
                if (first || tick - memory.LastGreeting >= SimulationTime.TicksPerSecond * 20)
                {
                    memory.Encounters = Math.Min(memory.Encounters + 1, 10000);
                    memory.Regard = Math.Min(memory.Regard + (person.Sociability > .6f ? 2 : 1), 20);
                    memory.LastGreeting = tick;
                }

                var hour = SimulationTime.MinuteOfDay(tick) / 60;
                var greeting = hour < 11 ? "Доброе утро" : hour < 18 ? "Добрый день" : "Добрый вечер";
                var reply = first ? $"{greeting}! Я {person.Name}." :
                    memory.Regard >= 6 ? "Как приятно снова вас видеть! Как проходит день?" :
                    person.Sociability >= .5f ? "Привет! Хорошо, что снова пересеклись." : "Здравствуйте. Мы уже встречались.";
                return Say(person, reply, memory, first);
            }

            if (memory == null) return new ConversationResult(false, "Сначала поздоровайтесь.");
            var answer = topic switch
            {
                ConversationTopic.Name => $"Меня зовут {person.Name}. Мне {person.Age}.",
                ConversationTopic.Activity => ActivityReply(view),
                ConversationTopic.Wellbeing => WellbeingReply(person),
                _ => null
            };
            return answer == null ? default : Say(person, answer, memory);
        }

        private static string ActivityReply(ResidentViewData view) => view.Intent switch
        {
            ResidentActivity.Work => view.Traveling ? $"Иду на смену. Моя работа — {view.Occupation}." : "Сейчас на смене.",
            ResidentActivity.Rest => view.Traveling ? "Возвращаюсь домой. Хочется немного тишины." : "Отдыхаю после дел.",
            ResidentActivity.Eat => "Ищу, чем бы перекусить.",
            ResidentActivity.Drink => "Хочу воды, давно не пил.",
            ResidentActivity.Stroll => "Вышел пройтись по району.",
            _ => "Пока есть минутка перед следующим делом."
        };

        private static string WellbeingReply(Resident person)
        {
            if (person.Thirst >= .62f) return "Хочется пить. Пора сделать перерыв.";
            if (person.Hunger >= .63f) return "Думаю о еде. Сегодня рано начались дела.";
            if (person.Fatigue >= .65f) return "Сил мало. Скоро отдохну.";
            return person.Sociability > .5f ? "День хороший, приятно встретить знакомого." : "Всё в порядке. Спасибо, что спросили.";
        }

        private static ConversationResult Say(Resident person, string answer, AcquaintanceSnapshot memory, bool first = false) =>
            new ConversationResult(true, first ? answer : $"{person.Name}: {answer}", memory.Encounters, memory.Regard);

        public AcquaintanceSnapshot[] Capture() => memories.Values.OrderBy(value => value.ResidentId, StringComparer.Ordinal)
            .Select(Copy).ToArray();

        public static bool CanRestore(AcquaintanceSnapshot[] snapshots, Actors actors, long tick)
        {
            if (snapshots == null || snapshots.Length > actors.People.Count) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
                if (snapshot == null || actors.Find(snapshot.ResidentId) == null || !ids.Add(snapshot.ResidentId) ||
                    snapshot.Encounters < 1 || snapshot.Encounters > 10000 || snapshot.Regard < 1 || snapshot.Regard > 20 ||
                    snapshot.LastGreeting < 0 || snapshot.LastGreeting > tick) return false;
            return true;
        }

        public void Restore(AcquaintanceSnapshot[] snapshots)
        {
            memories.Clear();
            if (snapshots == null) return;
            foreach (var snapshot in snapshots) memories.Add(snapshot.ResidentId, Copy(snapshot));
        }

        private static AcquaintanceSnapshot Copy(AcquaintanceSnapshot snapshot) => new AcquaintanceSnapshot
        {
            ResidentId = snapshot.ResidentId, Encounters = snapshot.Encounters, Regard = snapshot.Regard,
            LastGreeting = snapshot.LastGreeting
        };
    }
}
