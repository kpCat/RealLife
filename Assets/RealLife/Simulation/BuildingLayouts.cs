using System;

namespace RealLife.Simulation
{
    internal static class BuildingLayouts
    {
        internal static void Furnish(BuildingPlan plan)
        {
            if (plan.Kind == BuildingKind.TwoStoreyHouse)
                TwoStoreys(plan);
            else
                OneStorey(plan);
        }

        private static void OneStorey(BuildingPlan plan)
        {
            var shop = plan.Kind == BuildingKind.PublicPremises;
            var edge = plan.Width / 2;
            var division = plan.Depth * .53;
            var main = plan.Room("room.common", shop ? BuildingRoomRole.Shop : BuildingRoomRole.Living,
                0, -edge, edge, 0, division);
            var left = plan.Room("room.left", shop ? BuildingRoomRole.Storeroom : BuildingRoomRole.Kitchen,
                0, -edge, 0, division, plan.Depth);
            var right = plan.Room("room.right", shop ? BuildingRoomRole.Shop : BuildingRoomRole.Bedroom,
                0, 0, edge, division, plan.Depth);
            Entrance(plan, main);
            plan.Partition(-edge / 2, 0, division, edge, true,
                plan.Door("door.left", main, left, -edge / 2, 0, division, true, -1));
            plan.Partition(edge / 2, 0, division, edge, true,
                plan.Door("door.right", main, right, edge / 2, 0, division, true, -1));
            plan.Partition(0, 0, (division + plan.Depth) / 2, plan.Depth - division, false);

            plan.Furnish(main, "surface.common", shop ? BuildingFurnitureKind.Counter : BuildingFurnitureKind.Table,
                -edge + 1.03, 1.43, shop ? 1.55 : 1.3, .66, shop ? .96 : .78);
            plan.Furnish(main, "storage.common", BuildingFurnitureKind.Shelves, edge - .82, 1.30, 1.17, .43, 1.48);
            plan.Furnish(left, shop ? "storage.left" : "surface.left",
                shop ? BuildingFurnitureKind.Shelves : BuildingFurnitureKind.Counter,
                -edge / 2, plan.Depth - .65, 1.45, .52, shop ? 1.46 : .90);
            plan.Furnish(right, shop ? "surface.right" : "storage.right",
                shop ? BuildingFurnitureKind.Counter : BuildingFurnitureKind.Shelves,
                edge / 2, plan.Depth - .62, 1.35, .47, shop ? .92 : 1.47);
            if (!shop && plan.Depth - division >= 4)
                plan.Furnish(right, null, BuildingFurnitureKind.Bed, edge - .64, division + 1.53, .97, 1.90, .52);
            if (!shop && edge > 3.7)
                plan.Furnish(main, null, BuildingFurnitureKind.Chair, -edge + 1.03, 2.29, .45, .43, .49);
        }

        private static void TwoStoreys(BuildingPlan plan)
        {
            const double hallHalf = 1.73;
            var edge = plan.Width / 2;
            var groundHall = plan.Room("room.hall", BuildingRoomRole.Hall, 0, -hallHalf, hallHalf, 0, plan.Depth);
            var upperHall = plan.Room("room.landing", BuildingRoomRole.Hall, 1, -hallHalf, hallHalf, 0, plan.Depth);
            plan.Stairs = new BuildingStairs(groundHall.Id, upperHall.Id);
            Entrance(plan, groundHall);
            var upperDoorZ = plan.Depth - 1.29;
            var groundDoorZ = 1.20;
            var mainX = (hallHalf + edge) / 2;
            for (var floor = 0; floor < 2; floor++)
            {
                var key = floor == 0 ? "ground." : "upper.";
                var hall = floor == 0 ? groundHall : upperHall;
                var left = plan.Room("room." + key + "left", floor == 0 ? BuildingRoomRole.Living : BuildingRoomRole.Bedroom,
                    floor, -edge, -hallHalf, 0, plan.Depth);
                var right = plan.Room("room." + key + "right", floor == 0 ? BuildingRoomRole.Kitchen : BuildingRoomRole.Bedroom,
                    floor, hallHalf, edge, 0, plan.Depth);
                var doorZ = floor == 0 ? groundDoorZ : upperDoorZ;
                plan.Partition(-hallHalf, floor, plan.Depth / 2, plan.Depth, false,
                    plan.Door("door." + key + "left", hall, left, -hallHalf, floor, doorZ, false, -1));
                plan.Partition(hallHalf, floor, plan.Depth / 2, plan.Depth, false,
                    plan.Door("door." + key + "right", hall, right, hallHalf, floor, doorZ, false, 1));
                var displayZ = floor == 0 ? Math.Min(4.1, plan.Depth * .53) : plan.Depth - 3.2;
                plan.Furnish(left, "surface." + key + "left", BuildingFurnitureKind.Table,
                    -mainX, displayZ, 1.19, .64, .78);
                plan.Furnish(right, "surface." + key + "right",
                    floor == 0 ? BuildingFurnitureKind.Counter : BuildingFurnitureKind.Table,
                    mainX, displayZ, 1.20, .63, floor == 0 ? .89 : .78);
                plan.Furnish(left, "storage." + key + "left", BuildingFurnitureKind.Shelves,
                    -mainX, displayZ - 1.22, 1.06, .42, 1.49);
                plan.Furnish(right, "storage." + key + "right", BuildingFurnitureKind.Shelves,
                    mainX, displayZ - 1.22, 1.06, .42, 1.49);
                if (floor == 1)
                {
                    plan.Furnish(left, null, BuildingFurnitureKind.Bed, -mainX, 1.60, .98, 1.98, .52);
                    plan.Furnish(right, null, BuildingFurnitureKind.Bed, mainX, 1.60, .98, 1.98, .52);
                }
                else
                {
                    plan.Furnish(left, null, BuildingFurnitureKind.Chair, -mainX, displayZ + .87, .44, .42, .5);
                }
            }
        }

        private static void Entrance(BuildingPlan plan, BuildingRoom main) =>
            plan.Door("door.entrance", null, main, 0, 0, 0, true, -1);
    }
}
