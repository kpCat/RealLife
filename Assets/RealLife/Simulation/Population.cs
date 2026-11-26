using System;
using System.Collections.Generic;
using System.Linq;

namespace RealLife.Simulation
{
    internal sealed class Population
    {
        public const int GeneratorVersion = 1;
        private const int WakeInterval = SimulationTime.TicksPerSecond;
        private const int BlockedLimit = SimulationTime.TicksPerSecond * 3;
        private const int SupplyRetry = SimulationTime.TicksPerMinute * 9;

        private static readonly string[] FirstNames =
        {
            "Анна", "Михаил", "Ирина", "Павел", "София", "Виктор", "Нина", "Андрей", "Елена", "Роман",
            "Дарья", "Илья", "Лидия", "Алексей", "Ольга", "Сергей", "Марина", "Артур", "Вера", "Антон"
        };
        private static readonly string[] Surnames =
        {
            "Лазар", "Данилюк", "Мирон", "Горя", "Еремия", "Урсу", "Мунтян", "Тома", "Капра",
            "Санду", "Кирияк", "Негру", "Топал", "Стратан", "Балан", "Бондарь", "Ботнарь"
        };
        private static readonly string[] Jobs =
        {
            "приём поставок", "выкладка товара", "учёт запасов", "уборка помещения", "подготовка витрины"
        };

        private readonly ResidentNavigation navigation;
        private readonly WorldFrame frame;
        private readonly Dictionary<string, SupplyStop> supplies = new Dictionary<string, SupplyStop>(StringComparer.Ordinal);
        private readonly SupplyStop[] publicSupplies;
        public Actors Actors { get; }
        public string[] InventoryIds { get; }

        private readonly struct SupplyStop
        {
            public readonly BuildingAnchor Anchor;
            public readonly int Node;

            public SupplyStop(BuildingAnchor anchor, int node) => (Anchor, Node) = (anchor, node);
        }

        public Population(RegionWorld world, Buildings buildings, bool enabled, int residentsPerHome)
        {
            frame = world.Frame;
            var people = new List<Resident>();
            var shared = new List<SupplyStop>();
            var workAssignments = new Dictionary<string, int>(StringComparer.Ordinal);
            if (enabled && buildings.Plans.Count != 0)
            {
                navigation = new ResidentNavigation(world, buildings);
                var plans = buildings.Plans.OrderBy(plan => plan.Id, StringComparer.Ordinal).ToArray();
                foreach (var plan in plans)
                {
                    var anchor = RegionInventory.ProvisionSource(plan);
                    if (anchor == null || !navigation.TryAnchor(anchor.Id, out var node)) continue;
                    var stop = new SupplyStop(anchor, node);
                    supplies.Add(plan.Id, stop);
                    if (plan.Kind == BuildingKind.PublicPremises) shared.Add(stop);
                }
                foreach (var plan in plans)
                {
                    if (plan.Kind == BuildingKind.PublicPremises) continue;
                    for (var slot = 0; slot < residentsPerHome; slot++)
                    {
                        var person = Generate(plan, slot, shared, workAssignments);
                        if (person != null) people.Add(person);
                    }
                }
            }
            Actors = new Actors(people);
            publicSupplies = shared.ToArray();
            InventoryIds = people.Select(person => person.Id).ToArray();
            Reset(0);
        }

        private Resident Generate(BuildingPlan home, int slot, List<SupplyStop> stops, Dictionary<string, int> assignments)
        {
            if (!navigation.TryRest(home.Id, slot, out var rest) ||
                !navigation.TryOutside(home.Id, slot, out var outside) || !navigation.IsReachable(rest, outside)) return null;

            var id = home.Id + "/resident." + (slot + 1);
            var seed = StableHash(id);
            var firstName = (int)(seed % (uint)FirstNames.Length);
            var lastName = (int)((seed >> 8) % (uint)Surnames.Length);
            var appearance = (int)((seed >> 13) % 6) * 2 + firstName % 2;
            var atHome = slot == 0;
            var walk = navigation.TryOutside(home.Id, slot + 1, out var stroll) ? stroll : outside;
            var candidates = stops.Where(stop => navigation.IsReachable(outside, stop.Node))
                .OrderBy(stop => DistanceSquared(navigation.GetNode(outside).Position, navigation.GetNode(stop.Node).Position))
                .Take(3).ToArray();
            var workId = home.Id;
            var workNode = supplies.TryGetValue(home.Id, out var homeSupply) ? homeSupply.Node : rest;
            if (candidates.Length > 0 && slot != 0)
            {
                var job = candidates[(seed >> 3) % (uint)candidates.Length];
                workId = job.Anchor.BuildingId;
                workNode = job.Node;
                assignments.TryGetValue(workId, out var assignment);
                if (navigation.TryWork(workId, assignment, out var station) && navigation.IsReachable(outside, station))
                {
                    workNode = station;
                    assignments[workId] = assignment + 1;
                }
            }
            var startHour = 7 * 60 + (int)((seed >> 5) % 125);
            return new Resident(id, FirstNames[firstName] + " " + Surnames[lastName], home.Id, workId,
                workId == home.Id ? "дела по дому" : Jobs[(seed >> 17) % (uint)Jobs.Length],
                22 + (int)((seed >> 11) % 49), appearance, atHome ? rest : outside, rest, workNode, walk,
                startHour, startHour + 7 * 60 + (int)((seed >> 21) % 105),
                .2f + (seed >> 20) % 70 / 100f, 1.12f + (seed >> 16) % 38 / 100f,
                .38f + (seed >> 22) % 22 / 100f, .39f + (seed >> 18) % 23 / 100f, .17f + (seed >> 9) % 20 / 100f);
        }

        public void Reset(long tick)
        {
            foreach (var person in Actors.People)
                person.Restore(new ResidentSnapshot
                {
                    Node = person.StartNode, Hunger = person.InitialHunger, Thirst = person.InitialThirst,
                    Fatigue = person.InitialFatigue, Heading = StableHash(person.Id) % 360,
                    Until = SimulationTime.After(tick, 4 + (int)(StableHash(person.Id) % (WakeInterval * 2))),
                    Reason = "Начинается день"
                }, navigation);
            Actors.Project(navigation);
        }

        public void Step(RegionSession session, Func<string, LocalPoint, LocalPoint, bool> stepAllowed)
        {
            var tick = session.Tick;
            foreach (var person in Actors.People)
            {
                person.Moved = false;
                if ((tick % WakeInterval + StableHash(person.Id) % WakeInterval) % WakeInterval == 0)
                    UpdateNeeds(person, session.MinuteOfDay);

                if (person.Route != null)
                    Walk(session, person, stepAllowed);
                else if (tick >= person.Until)
                    Decide(session, person);
            }
            Actors.Project(navigation);
        }

        private static void UpdateNeeds(Resident person, int minute)
        {
            var resting = person.Route == null && person.Activity == ResidentActivity.Rest;
            var walking = person.Route != null;
            person.Hunger = Math.Clamp(person.Hunger + (walking ? .00096f : resting ? .00021f : .00045f), 0, 1);
            person.Thirst = Math.Clamp(person.Thirst + (walking ? .00112f : resting ? .00027f : .00056f), 0, 1);
            person.Fatigue = Math.Clamp(person.Fatigue + (resting ? -.0038f :
                walking || minute < 360 || minute >= 22 * 60 ? .00094f : .00064f), 0, 1);
        }

        private void Decide(RegionSession session, Resident person)
        {
            var tick = session.Tick;
            if (person.Activity != ResidentActivity.Idle && person.Completed < int.MaxValue)
                person.Completed++;
            var minute = session.MinuteOfDay;
            var night = minute < 355 || minute >= 22 * 60 + StableHash(person.Id) % 65;
            var hunger = (person.Hunger - .53f) * 2.1f;
            var thirst = (person.Thirst - .55f) * 2.4f;
            if (tick >= person.RetrySupplyAt && Math.Max(hunger, thirst) > 0)
            {
                var foodFirst = hunger >= thirst;
                if (TryProvision(session, person, foodFirst ? InventoryUseKind.Food : InventoryUseKind.Drink) ||
                    Math.Min(hunger, thirst) > 0 &&
                    TryProvision(session, person, foodFirst ? InventoryUseKind.Drink : InventoryUseKind.Food)) return;
                person.RetrySupplyAt = SimulationTime.After(tick, SupplyRetry);
            }

            if (night || person.Fatigue >= .71f || minute >= person.WorkEnd)
            {
                if (Begin(session, person, person.RestNode, ResidentActivity.Rest, null,
                    night ? "Пора спать" : person.Fatigue >= .71f ? "Нужно восстановить силы" : "Дела на сегодня закончены"))
                    return;
            }
            else if (minute >= person.WorkStart)
            {
                if (Begin(session, person, person.WorkNode, ResidentActivity.Work, null, "Идёт рабочая смена")) return;
            }
            else
            {
                if (Begin(session, person, person.WalkNode, ResidentActivity.Stroll, null, "Свободное время")) return;
            }

            Wait(person, tick, "Подходящий маршрут пока недоступен");
        }

        private bool TryProvision(RegionSession session, Resident person, InventoryUseKind kind)
        {
            var activity = kind == InventoryUseKind.Food ? ResidentActivity.Eat : ResidentActivity.Drink;
            var reason = kind == InventoryUseKind.Food ? "Проголодался" : "Хочется пить";
            if (FindUse(session, person.Id, kind).Length != 0)
                return Begin(session, person, person.Node, activity, null, reason);

            if (supplies.TryGetValue(person.HomeId, out var home) &&
                DistanceSquared(person.Position, navigation.GetNode(home.Node).Position) <= 75 * 75 &&
                TrySource(session, person, home, kind, activity, reason)) return true;

            var nearest = publicSupplies
                .Where(stop => navigation.IsReachable(person.Node, stop.Node) && FindUse(session, stop.Anchor.Id, kind).Length != 0)
                .OrderBy(stop => DistanceSquared(person.Position, navigation.GetNode(stop.Node).Position));
            foreach (var stop in nearest)
                if (Begin(session, person, stop.Node, activity, stop.Anchor.Id, reason)) return true;
            return home.Anchor != null && TrySource(session, person, home, kind, activity, reason);
        }

        private bool TrySource(RegionSession session, Resident person, SupplyStop stop, InventoryUseKind kind,
            ResidentActivity activity, string reason) => FindUse(session, stop.Anchor.Id, kind).Length != 0 &&
            Begin(session, person, stop.Node, activity, stop.Anchor.Id, reason);

        private bool Begin(RegionSession session, Resident person, int destination, ResidentActivity activity,
            string anchorId, string reason)
        {
            if (!navigation.TryRoute(person.Node, destination, out var route)) return false;
            person.Activity = activity;
            person.AnchorId = anchorId;
            person.Reason = reason;
            if (route.Length < 2)
                Arrive(session, person);
            else
            {
                person.Route = route;
                person.RouteIndex = 0;
                person.Progress = 0;
                person.BlockedTicks = 0;
            }
            return true;
        }

        private void Walk(RegionSession session, Resident person, Func<string, LocalPoint, LocalPoint, bool> stepAllowed)
        {
            var next = person.NextNode;
            if (!navigation.TryStep(person.Node, next, out var step))
                throw new InvalidOperationException("Resident route contains an unknown step.");
            if (step.DoorId != null && !session.Buildings.IsDoorOpen(step.DoorId))
            {
                var result = session.SetDoor(person.Id, step.DoorId, true);
                if (result != DoorActionResult.Applied && result != DoorActionResult.AlreadyInState)
                {
                    Blocked(session, person, "Проход у двери занят");
                    return;
                }
            }

            var sign = person.Returning ? -1 : 1;
            var distance = person.Speed / SimulationTime.TicksPerSecond;
            var progress = Math.Clamp(person.Progress + sign * distance / step.Length, 0, 1);
            var proposed = navigation.Position(person.Node, next, progress);
            if (stepAllowed != null && !stepAllowed(person.Id, person.Position, proposed))
            {
                Blocked(session, person, "Кто-то или что-то перегородило путь");
                return;
            }
            var difference = new LocalPoint(proposed.X - person.Position.X, proposed.Y - person.Position.Y,
                proposed.Z - person.Position.Z);
            if (Math.Abs(difference.X) + Math.Abs(difference.Z) > 1e-6)
            {
                person.Heading = (float)((Math.Atan2(difference.X, difference.Z) * 180 / Math.PI + 360) % 360);
                if (person.Heading >= 360) person.Heading = 0;
            }
            (person.Position, person.Progress, person.Moved, person.BlockedTicks) = (proposed, progress, true, 0);
            if (person.Returning && progress == 0)
                Wait(person, session.Tick, "Уступает дорогу и ждёт");
            else if (progress == 1)
            {
                person.Node = next;
                person.Progress = 0;
                person.RouteIndex++;
                if (person.RouteIndex == person.Route.Length - 1)
                    Arrive(session, person);
            }
        }

        private static void Blocked(RegionSession session, Resident person, string reason)
        {
            person.Reason = reason;
            person.BlockedTicks = Math.Min(person.BlockedTicks + 1, BlockedLimit);
            if (person.BlockedTicks < BlockedLimit) return;
            if (person.Progress == 0) Wait(person, session.Tick, reason);
            else person.Returning = true;
        }

        private void Arrive(RegionSession session, Resident person)
        {
            ClearRoute(person);
            var spread = (int)(StableHash(person.Id) % 21);
            switch (person.Activity)
            {
                case ResidentActivity.Eat:
                case ResidentActivity.Drink:
                    var kind = person.Activity == ResidentActivity.Eat ? InventoryUseKind.Food : InventoryUseKind.Drink;
                    if (!UseProvision(session, person, kind))
                    {
                        person.RetrySupplyAt = SimulationTime.After(session.Tick, SupplyRetry);
                        Wait(person, session.Tick, "Запас закончился или занят");
                        return;
                    }
                    person.Until = SimulationTime.After(session.Tick, WakeInterval * 2);
                    break;
                case ResidentActivity.Work:
                    person.Until = SimulationTime.After(session.Tick, (16 + spread) * SimulationTime.TicksPerMinute);
                    break;
                case ResidentActivity.Rest:
                    person.Until = SimulationTime.After(session.Tick, (18 + spread) * SimulationTime.TicksPerMinute);
                    break;
                default:
                    person.Until = SimulationTime.After(session.Tick, (3 + spread) * WakeInterval);
                    break;
            }
        }

        private static bool UseProvision(RegionSession session, Resident person, InventoryUseKind kind)
        {
            var instanceId = FindUse(session, person.Id, kind);
            if (instanceId.Length == 0 && person.AnchorId != null)
            {
                var source = FindUse(session, person.AnchorId, kind);
                if (source.Length == 0) return false;
                var moved = session.MoveItems(person.Id, new InventoryMove(person.AnchorId, source, 1, person.Id),
                    person.AnchorId);
                if (!moved.Succeeded) return false;
                instanceId = moved.InstanceId;
            }
            return instanceId.Length != 0 &&
                session.UseItem(new InventoryUse(person.Id, person.Id, instanceId, session.Tick)).Succeeded;
        }

        private static string FindUse(RegionSession session, string containerId, InventoryUseKind kind)
        {
            var container = session.GetInventory(containerId);
            if (container != null)
                foreach (var stack in container.Stacks)
                    if (stack.UseKind == kind) return stack.InstanceId;
            return "";
        }

        private static void Wait(Resident person, long tick, string why)
        {
            ClearRoute(person);
            person.Activity = ResidentActivity.Idle;
            person.AnchorId = null;
            person.Reason = why;
            person.Until = SimulationTime.After(tick, (3 + (int)(StableHash(person.Id) % 5)) * WakeInterval);
        }

        private static void ClearRoute(Resident person)
        {
            person.Route = null;
            person.RouteIndex = 0;
            person.Progress = 0;
            person.Returning = false;
            person.BlockedTicks = 0;
        }

        public ResidentSnapshot[] Capture() => Actors.People.Select(person => person.Capture(navigation, frame)).ToArray();

        public string RoomOf(Resident person) => navigation.RoomAt(person.Node, person.NextNode, person.Progress);

        public bool CanRestore(ResidentSnapshot[] snapshots, Buildings buildings, string[] openDoors, long tick)
        {
            if (snapshots == null || snapshots.Length != Actors.People.Count) return false;
            for (var i = 0; i < snapshots.Length; i++)
                if (!Valid(snapshots[i], Actors.People[i], buildings, openDoors, tick)) return false;
            return true;
        }

        private bool Valid(ResidentSnapshot save, Resident person, Buildings buildings, string[] openDoors, long tick)
        {
            if (save == null || save.Id != person.Id || !save.Position.IsFinite || !navigation.IsNode(save.Node) ||
                !Unit(save.Hunger) || !Unit(save.Thirst) || !Unit(save.Fatigue) ||
                !WorldFrame.IsFinite(save.Heading) || save.Heading < 0 || save.Heading >= 360 ||
                !WorldFrame.IsFinite(save.Progress) || save.Progress < 0 || save.Progress >= 1 ||
                save.Activity < ResidentActivity.Idle || save.Activity > ResidentActivity.Drink ||
                save.Until < 0 || save.Until > SimulationTime.After(tick, 40 * SimulationTime.TicksPerMinute) ||
                save.RetrySupplyAt < 0 || save.RetrySupplyAt > SimulationTime.After(tick, SupplyRetry) ||
                save.Completed < 0 || save.Uses < 0 ||
                save.BlockedTicks < 0 || save.BlockedTicks > BlockedLimit || save.Reason?.Length > 160) return false;
            var next = save.Node;
            var hasRoute = save.Route?.Length > 0;
            if (!hasRoute)
            {
                if (save.RouteIndex != 0 || save.Progress != 0 || save.Returning || save.BlockedTicks != 0) return false;
            }
            else
            {
                if (save.Route.Length < 2 || save.Route.Length > 4096 || save.RouteIndex < 0 ||
                    save.RouteIndex >= save.Route.Length - 1 || save.Route[save.RouteIndex] != save.Node ||
                    save.Activity == ResidentActivity.Idle || save.Returning && save.Progress == 0) return false;
                next = save.Route[save.RouteIndex + 1];
                for (var i = 1; i < save.Route.Length; i++)
                    if (!navigation.TryStep(save.Route[i - 1], save.Route[i], out _)) return false;
            }
            var destination = hasRoute ? save.Route[save.Route.Length - 1] : save.Node;
            if (save.Activity == ResidentActivity.Work && destination != person.WorkNode ||
                save.Activity == ResidentActivity.Rest && destination != person.RestNode ||
                save.Activity == ResidentActivity.Stroll && destination != person.WalkNode) return false;
            if (!string.IsNullOrEmpty(save.AnchorId) && (save.Activity != ResidentActivity.Eat && save.Activity != ResidentActivity.Drink ||
                !buildings.TryGetAnchor(save.AnchorId, out var anchor) ||
                !supplies.TryGetValue(anchor.BuildingId, out var stop) ||
                stop.Anchor.Id != save.AnchorId || stop.Node != destination || anchor.BuildingId != person.HomeId &&
                    (!buildings.TryGet(anchor.BuildingId, out var plan) || plan.Kind != BuildingKind.PublicPremises)))
                return false;
            var expected = navigation.Position(save.Node, next, save.Progress);
            return DistanceSquared(frame.ToLocal(save.Position), expected) < .0004 &&
                (save.BuildingId ?? "") == (navigation.BuildingAt(save.Node, next, save.Progress) ?? "") &&
                (save.RoomId ?? "") == (navigation.RoomAt(save.Node, next, save.Progress) ?? "") &&
                buildings.IsPoseClearOfDoors(expected, openDoors);
        }

        public void Restore(ResidentSnapshot[] snapshots)
        {
            for (var i = 0; i < snapshots.Length; i++) Actors.People[i].Restore(snapshots[i], navigation);
            Actors.Project(navigation);
        }

        public static double DistanceSquared(LocalPoint from, LocalPoint to) =>
            (from.X - to.X) * (from.X - to.X) + (from.Y - to.Y) * (from.Y - to.Y) + (from.Z - to.Z) * (from.Z - to.Z);

        private static bool Unit(float value) => WorldFrame.IsFinite(value) && value >= 0 && value <= 1;

        private static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in value) hash = (hash ^ character) * 16777619;
                return hash;
            }
        }
    }
}
