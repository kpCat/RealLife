using System;
using System.Collections.Generic;

namespace RealLife.Simulation
{
    public enum ResidentActivity { Idle, Work, Rest, Stroll, Eat, Drink }

    public readonly struct ResidentViewData
    {
        public string Id { get; }
        public string Name { get; }
        public int Age { get; }
        public int AppearanceId { get; }
        public string HouseholdId => HomeId;
        public string HomeId { get; }
        public string WorkId { get; }
        public string Occupation { get; }
        public string Temperament { get; }
        public string Activity { get; }
        public string Reason { get; }
        public ResidentActivity Intent { get; }
        public LocalPoint Position { get; }
        public float Heading { get; }
        public bool Walking { get; }
        public bool Traveling { get; }
        public bool Resting => !Traveling && Intent == ResidentActivity.Rest;
        public string BuildingId { get; }
        public string RoomId { get; }
        public float Hunger { get; }
        public float Thirst { get; }
        public float Fatigue { get; }
        public int CompletedActivities { get; }
        public int InventoryUses { get; }

        internal ResidentViewData(Resident resident, ResidentNavigation navigation)
        {
            Id = resident.Id;
            Name = resident.Name;
            Age = resident.Age;
            AppearanceId = resident.Appearance;
            HomeId = resident.HomeId;
            WorkId = resident.WorkId;
            Occupation = resident.Occupation;
            Temperament = resident.Sociability >= .65f ? "Общительный" :
                resident.Sociability >= .35f ? "Спокойный" : "Сдержанный";
            Intent = resident.Activity;
            Activity = resident.Route == null ? ActivityName(Intent) : TravelName(Intent);
            Reason = resident.Reason;
            Position = resident.Position;
            Heading = resident.Heading;
            Walking = resident.Moved;
            Traveling = resident.Route != null;
            BuildingId = navigation.BuildingAt(resident.Node, resident.NextNode, resident.Progress);
            RoomId = navigation.RoomAt(resident.Node, resident.NextNode, resident.Progress);
            (Hunger, Thirst, Fatigue) = (resident.Hunger, resident.Thirst, resident.Fatigue);
            (CompletedActivities, InventoryUses) = (resident.Completed, resident.Uses);
        }

        private static string ActivityName(ResidentActivity activity) => activity switch
        {
            ResidentActivity.Work => "На работе",
            ResidentActivity.Rest => "Отдыхает",
            ResidentActivity.Stroll => "Гуляет",
            ResidentActivity.Eat => "Перекусывает",
            ResidentActivity.Drink => "Пьёт воду",
            _ => "Занимается своими делами"
        };

        private static string TravelName(ResidentActivity activity) => activity switch
        {
            ResidentActivity.Work => "Идёт на работу",
            ResidentActivity.Rest => "Идёт домой",
            ResidentActivity.Eat => "Ищет еду",
            ResidentActivity.Drink => "Ищет воду",
            _ => "Гуляет"
        };
    }

    [Serializable]
    public sealed class ResidentSnapshot
    {
        public string Id, BuildingId, RoomId, AnchorId, Reason;
        public GlobalPosition Position;
        public ResidentActivity Activity;
        public int Node, RouteIndex, Completed, Uses, BlockedTicks;
        public int[] Route;
        public double Progress;
        public float Hunger, Thirst, Fatigue, Heading;
        public long Until, RetrySupplyAt;
        public bool Returning;
    }

    internal sealed class Resident
    {
        public readonly string Id, Name, HomeId, WorkId, Occupation;
        public readonly int Age, Appearance, StartNode, RestNode, WorkNode, WalkNode, WorkStart, WorkEnd;
        public readonly float Sociability, Speed, InitialHunger, InitialThirst, InitialFatigue;
        public LocalPoint Position;
        public int Node, RouteIndex, Completed, Uses, BlockedTicks;
        public int[] Route;
        public double Progress;
        public float Hunger, Thirst, Fatigue, Heading;
        public long Until, RetrySupplyAt;
        public bool Returning, Moved;
        public ResidentActivity Activity;
        public string AnchorId, Reason;
        public int NextNode => Route == null ? Node : Route[RouteIndex + 1];

        public Resident(string id, string name, string home, string work, string occupation, int age, int appearance,
            int start, int rest, int workNode, int walk, int workStart, int workEnd, float sociability, float speed,
            float hunger, float thirst, float fatigue)
        {
            (Id, Name, HomeId, WorkId, Occupation) = (id, name, home, work, occupation);
            (Age, Appearance, StartNode, RestNode, WorkNode, WalkNode) = (age, appearance, start, rest, workNode, walk);
            (WorkStart, WorkEnd, Sociability, Speed) = (workStart, workEnd, sociability, speed);
            (InitialHunger, InitialThirst, InitialFatigue) = (hunger, thirst, fatigue);
        }

        public ResidentSnapshot Capture(ResidentNavigation navigation, WorldFrame frame) => new ResidentSnapshot
        {
            Id = Id, BuildingId = navigation.BuildingAt(Node, NextNode, Progress),
            RoomId = navigation.RoomAt(Node, NextNode, Progress), AnchorId = AnchorId, Reason = Reason,
            Position = frame.ToGlobal(Position), Activity = Activity, Node = Node, RouteIndex = RouteIndex,
            Completed = Completed, Uses = Uses, BlockedTicks = BlockedTicks,
            Route = Route == null ? Array.Empty<int>() : (int[])Route.Clone(),
            Progress = Progress, Hunger = Hunger, Thirst = Thirst, Fatigue = Fatigue, Heading = Heading,
            Until = Until, RetrySupplyAt = RetrySupplyAt, Returning = Returning
        };

        public void Restore(ResidentSnapshot snapshot, ResidentNavigation navigation)
        {
            (Activity, Node, RouteIndex, Progress) = (snapshot.Activity, snapshot.Node, snapshot.RouteIndex, snapshot.Progress);
            (Until, Completed, Uses, BlockedTicks) = (snapshot.Until, snapshot.Completed, snapshot.Uses, snapshot.BlockedTicks);
            (Hunger, Thirst, Fatigue, Heading) = (snapshot.Hunger, snapshot.Thirst, snapshot.Fatigue, snapshot.Heading);
            (RetrySupplyAt, Returning, Reason) = (snapshot.RetrySupplyAt, snapshot.Returning, snapshot.Reason);
            AnchorId = string.IsNullOrEmpty(snapshot.AnchorId) ? null : snapshot.AnchorId;
            Route = snapshot.Route?.Length > 0 ? (int[])snapshot.Route.Clone() : null;
            Position = navigation.Position(Node, NextNode, Progress);
            Moved = false;
        }
    }

    internal sealed class Actors
    {
        private readonly Resident[] people;
        private readonly ResidentViewData[] views;
        public IReadOnlyList<Resident> People { get; }
        public IReadOnlyList<ResidentViewData> Views { get; }

        public Actors(List<Resident> residents)
        {
            people = residents.ToArray();
            views = new ResidentViewData[people.Length];
            People = Array.AsReadOnly(people);
            Views = Array.AsReadOnly(views);
        }

        public Resident Find(string id)
        {
            foreach (var person in people)
                if (person.Id == id) return person;
            return null;
        }

        public void Project(ResidentNavigation navigation)
        {
            for (var i = 0; i < people.Length; i++)
                views[i] = new ResidentViewData(people[i], navigation);
        }
    }
}
