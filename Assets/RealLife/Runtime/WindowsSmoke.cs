using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering;

namespace RealLife.Runtime
{
    internal static class WindowsSmoke
    {
        public static IEnumerator Run(DistrictGame game, string directory)
        {
            using var scenario = Scenario(game, directory).GetEnumerator();
            while (true)
            {
                Exception failure = null;
                var hasNext = false;
                try { hasNext = scenario.MoveNext(); }
                catch (Exception error) { failure = error; }
                if (failure != null)
                {
                    Debug.LogException(failure);
                    Application.Quit(1);
                    yield break;
                }
                if (!hasNext) break;
                yield return scenario.Current;
            }
            Debug.Log("RealLife Windows smoke passed.");
            Application.Quit(0);
        }

        private static IEnumerable<object> Scenario(DistrictGame game, string directory)
        {
            Require(Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8, "Windows x64 Player");
            yield return null;
            yield return new WaitForEndOfFrame();
            Require(GraphicsSettings.currentRenderPipeline != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null,
                "active render pipeline and graphics device");
            Require(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf").HasCharacter('Ж'), "Cyrillic player font");
            Directory.CreateDirectory(directory);

            foreach (var next in CheckInput(game)) yield return next;

            var doorLine = game.DoorTarget.z;
            var start = game.PlayerPosition;
            for (var step = 0; step < 260; step++) Move(game, Vector2.up);
            Require(game.PlayerPosition.z - start.z > 8 && game.PlayerPosition.z < doorLine - 0.15f && !game.IsDoorOpen,
                "walking meets the closed door's collider");
            for (var step = 0; step < 16; step++) Move(game, Vector2.down);
            Aim(game, game.DoorTarget);
            Require(game.FocusIsDoor, "the door is reachable through the view ray");
            Frame(game, interact: true);
            Require(game.IsDoorOpen, "opening the door changes the session");

            Aim(game, new Vector3(game.PlayerPosition.x, game.Sight.origin.y, game.PlayerPosition.z + 4));
            for (var step = 0; step < 150 && game.PlayerPosition.z < doorLine + 3.5f; step++) Move(game, Vector2.up);
            Require(game.PlayerPosition.z >= doorLine + 3.5f, "walking crosses the open doorway into the workshop");
            Aim(game, game.ItemTarget);
            Require(game.FocusIsItem, "the lantern is reachable on the workbench");
            Frame(game, interact: true);
            Require(game.IsCarrying, "the lantern moves into the inventory");
            var inside = game.PlayerPosition;
            var insidePitch = game.Pitch;
            Frame(game, slot: DistrictGame.SlotCommand.Save);
            foreach (var next in FinishSave(game)) yield return next;
            Require(File.Exists(game.SavePath), "first snapshot written to disk");

            Frame(game, interact: true);
            Require(!game.IsCarrying, "the lantern returns to the workbench");
            Aim(game, new Vector3(game.PlayerPosition.x, game.Sight.origin.y, game.PlayerPosition.z + 4));
            for (var step = 0; step < 160 && game.PlayerPosition.z > doorLine - 1.5f; step++) Move(game, Vector2.down);
            Require(game.PlayerPosition.z <= doorLine - 1.5f, "walking back outside");
            Aim(game, game.DoorTarget);
            Require(game.FocusIsDoor, "the open door can be targeted");
            Frame(game, interact: true);
            Require(!game.IsDoorOpen, "the door closes after leaving");
            var outside = game.PlayerPosition;
            Frame(game, slot: DistrictGame.SlotCommand.Save);
            foreach (var next in FinishSave(game)) yield return next;
            Require(File.Exists(game.PreviousSavePath), "the previous generation is retained");

            for (var step = 0; step < 12; step++) Move(game, Vector2.down);
            Require(Vector3.Distance(game.PlayerPosition, outside) > 0.4f, "player moved after saving");
            Frame(game, slot: DistrictGame.SlotCommand.Load);
            Require(Vector3.Distance(game.PlayerPosition, outside) < 0.03f && !game.IsDoorOpen && !game.IsCarrying,
                "loading the latest generation restores the outdoor pose and closed door");

            File.WriteAllText(game.SavePath, "{}");
            Frame(game, slot: DistrictGame.SlotCommand.Load);
            Require(Vector3.Distance(game.PlayerPosition, inside) < 0.03f && Mathf.Abs(game.Pitch - insidePitch) < 0.02f &&
                game.IsDoorOpen && game.IsCarrying, "fallback restores indoor pose, view, door and inventory");

            var recovery = File.ReadAllText(game.PreviousSavePath);
            Frame(game, slot: DistrictGame.SlotCommand.Save);
            foreach (var next in FinishSave(game)) yield return next;
            Require(File.ReadAllText(game.PreviousSavePath) == recovery &&
                SaveSlot.Read(game.SavePath).Version == Simulation.GameSession.SnapshotVersion,
                "saving after recovery keeps the previous valid generation");
        }

        private static IEnumerable<object> CheckInput(DistrictGame game)
        {
            var settings = InputSystem.settings;
            var previousBackgroundBehavior = settings.backgroundBehavior;
            // The automated player is hidden. Keep its virtual keyboard active without foreground input.
            settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                yield return null;
                Require(keyboard.enabled && keyboard.escapeKey.wasPressedThisFrame, "virtual keyboard event received");
                game.ReadInput(keyboard, null, 0.05f);
                Require(game.IsPaused, "Escape pauses through the Input System");

                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                yield return null;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                yield return null;
                game.ReadInput(keyboard, null, 0.05f);
                Require(!game.IsPaused, "Escape resumes through the Input System");

                var start = game.PlayerPosition;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
                yield return null;
                game.ReadInput(keyboard, null, 0.10f);
                Require(game.PlayerPosition.z > start.z + 0.20f, "W walks forward through the Input System");
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard);
                settings.backgroundBehavior = previousBackgroundBehavior;
            }
        }

        private static IEnumerable<object> FinishSave(DistrictGame game)
        {
            var deadline = Time.realtimeSinceStartup + 8;
            while (game.IsSaving && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                Frame(game);
            }
            Require(!game.IsSaving, "background save completes");
        }

        private static void Move(DistrictGame game, Vector2 direction) =>
            game.SimulateFrame(direction, Vector2.zero, false, DistrictGame.SlotCommand.None, 1f / 60);

        private static void Frame(DistrictGame game, bool interact = false, DistrictGame.SlotCommand slot = DistrictGame.SlotCommand.None) =>
            game.SimulateFrame(Vector2.zero, Vector2.zero, interact, slot, 0.05f);

        private static void Aim(DistrictGame game, Vector3 point)
        {
            var direction = point - game.Sight.origin;
            var flat = new Vector2(direction.x, direction.z);
            var yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            var pitch = -Mathf.Atan2(direction.y, flat.magnitude) * Mathf.Rad2Deg;
            var delta = new Vector2(Mathf.DeltaAngle(game.Yaw, yaw), game.Pitch - pitch) / FirstPersonMotor.MouseSensitivity;
            game.SimulateFrame(Vector2.zero, delta, false, DistrictGame.SlotCommand.None, 0.05f);
        }

        private static void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException($"Windows smoke failed: {description}.");
            Debug.Log($"Smoke: {description}.");
        }
    }
}
