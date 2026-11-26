using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RealLife.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace RealLife.Runtime
{
    internal sealed class RegionGame : IDisposable
    {
        private readonly RegionWorld world;
        private readonly RegionSession session;
        private readonly FirstPersonMotor motor;
        private readonly RegionView view;
        private readonly ResidentView residentView;
        private readonly GameHud hud;
        private readonly SaveSlot slot;
        private readonly SaveSlot[] readSlots;
        private readonly RegionInteractions interactions;
        private readonly GameObject root = new GameObject("Regional first person");
        private readonly GameObject sunlight;
        private readonly bool automation;
        private readonly Func<string, LocalPoint, LocalPoint, bool> canResidentStep;
        private Task write;
        private float remainder;
        private bool paused;
        private int presentationRevision;

        public RegionGame(RegionWorld world, GeoPoint spawn, float yaw, UIDocument ui, StyleSheet styles, Material[] colors,
            NeighborhoodCatalog catalog, string saveDirectory, Action openWorlds, bool automation, bool enableResidents = true)
        {
            this.world = world;
            this.automation = automation;
            session = new RegionSession(world, spawn, yaw, catalog, enableResidents: enableResidents);
            readSlots = ReadSlots(saveDirectory);
            slot = readSlots[0];
            var controller = root.AddComponent<CharacterController>();
            controller.height = 1.82f;
            controller.radius = 0.27f;
            controller.center = Vector3.up * (controller.height / 2);
            controller.skinWidth = 0.035f;
            controller.stepOffset = 0.25f;
            controller.minMoveDistance = 0;
            var camera = new GameObject("Regional view").AddComponent<Camera>();
            RegionalCamera = camera;
            camera.transform.SetParent(root.transform, false);
            camera.transform.localPosition = Vector3.up * 1.63f;
            camera.fieldOfView = 65;
            camera.nearClipPlane = 0.04f;
            camera.farClipPlane = 500;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(160, 187, 198, 255);
            var sun = new GameObject("Regional daylight").AddComponent<Light>();
            sun.transform.rotation = Quaternion.Euler(48, -36, 0);
            sun.type = LightType.Directional;
            sun.intensity = 1.55f;
            sun.shadows = LightShadows.Soft;
            sun.color = new Color32(255, 231, 195, 255);
            sunlight = sun.gameObject;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color32(116, 130, 142, 255);
            RenderSettings.fog = true;
            RenderSettings.fogColor = camera.backgroundColor;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 90;
            RenderSettings.fogEndDistance = 330;

            motor = new FirstPersonMotor(controller, camera);
            view = new RegionView(world, colors, session.Buildings, VisibleItem);
            residentView = new ResidentView(world, view, controller, colors[(int)RegionView.Tone.Plaster]);
            canResidentStep = CanResidentStep;
            hud = new GameHud(ui, styles, world.Title, () => SetPaused(false), Save, () => Load(),
                openWorlds, hasItems: false, regionalInventory: true);
            interactions = new RegionInteractions(session, view, residentView, hud, ui.rootVisualElement,
                SetModalMode, SyncWorld);
            PreparePresentation(session.PlayerLocal);
            RestoreMotor();
            PresentResidents(0);
            SetPaused(false);
            RefreshHud();
        }

        public Vector3 Position => motor.Position;
        internal Camera RegionalCamera { get; }
        public float Yaw => motor.Yaw;
        public float Pitch => motor.Pitch;
        public long Tick => session.Tick;
        public bool IsSaving => write != null;
        public CellKey CurrentCell => world.CellAt(Flat(Position));
        public RegionView View => view;
        public Buildings Buildings => session.Buildings;
        public InventoryPanel InventoryPanel => interactions.Panel;
        public IReadOnlyList<ResidentViewData> Residents => session.Residents;
        public int MaterializedResidentCount => residentView.Count;
        public bool ConversationOpen => interactions.ConversationOpen;
        public ConversationResult LastConversation => interactions.LastConversation;
        public string PlayerInventoryId => session.PlayerInventoryId;
        public string SavePath => slot.CurrentPath;
        public string PreviousSavePath => slot.PreviousPath;

        public InventoryContainerView GetInventory(string id) => session.GetInventory(id);
        public RegionSnapshot CaptureState() => session.Capture();
        public bool TryFocus(out BuildingFocus focus) => interactions.TryFocus(motor.Sight, out focus);
        public bool TryFocusResident(out ResidentViewData resident) => interactions.TryFocusResident(motor.Sight, out resident);
        public bool IsResidentMaterialized(string id) => residentView.Contains(id);
        public bool AskResident(ConversationTopic topic) => interactions.Ask(topic);
        public void CloseInteraction() => interactions.Close();
        public bool Interact(InteractionControl control) => !paused && interactions.Activate(control, motor.Sight);

        internal static bool HasSave(string directory) => Array.Exists(ReadSlots(directory), candidate => candidate.Exists);

        private static SaveSlot[] ReadSlots(string directory) => new[]
        {
            new SaveSlot(directory, "slot-01-v4"),
            new SaveSlot(directory, "slot-01-v3"),
            new SaveSlot(directory)
        };

        public void ReadInput(float seconds)
        {
            var keyboard = Keyboard.current;
            if (interactions.IsOpen)
            {
                if (Pressed(keyboard, Key.Escape) || Pressed(keyboard, Key.I)) interactions.Close();
                PollSave();
                return;
            }
            if (Pressed(keyboard, Key.Escape)) SetPaused(!paused);
            if (!paused)
            {
                var movement = new Vector2(Axis(keyboard, Key.D, Key.RightArrow, Key.A, Key.LeftArrow),
                    Axis(keyboard, Key.W, Key.UpArrow, Key.S, Key.DownArrow));
                TickFrame(movement, Mouse.current?.delta.ReadValue() ?? Vector2.zero, seconds);
                if (Pressed(keyboard, Key.E)) Interact(InteractionControl.Primary);
                else if (Pressed(keyboard, Key.I)) Interact(InteractionControl.Details);
                if (interactions.IsOpen) return;
            }
            else
            {
                PollSave();
                RefreshHud();
            }
            if (Pressed(keyboard, Key.F5)) Save();
            if (Pressed(keyboard, Key.F9)) Load();
        }

        public void TickFrame(Vector2 movement, Vector2 look, float seconds)
        {
            PollSave();
            if (paused || interactions.IsOpen) return;
            view.Step(CurrentCell, FirstPersonMotor.ToLocal(Position));
            var predicted = Flat(motor.PredictHorizontal(movement, look, seconds));
            var target = world.CellAt(predicted);
            if (!view.Contains(target) || world.DistanceToOutline(predicted) < 0.55) movement = Vector2.zero;
            motor.Drive(movement, look, seconds);
            if (!session.SetPlayerPose(FirstPersonMotor.ToLocal(Position), motor.Yaw, motor.Pitch))
                motor.Restore(session.PlayerLocal, session.Yaw, session.Pitch);
            remainder += seconds;
            const float step = 1f / GameSession.TicksPerSecond;
            while (remainder >= step)
            {
                remainder -= step;
                session.AdvanceTick(canResidentStep);
            }
            SyncWorld();
            PresentResidents(seconds);
            RefreshHud();
        }

        public void Save()
        {
            if (Busy()) return;
            var current = Read(slot.CurrentPath);
            var previous = Read(slot.PreviousPath);
            if (Incompatible(current) || Incompatible(previous))
            {
                hud.Announce("Слот относится к другой версии пакета. Сохранение осталось на диске.");
                return;
            }
            var snapshot = session.Capture();
            if (!session.CanRestore(snapshot))
            {
                hud.Announce("Для сохранения встаньте на свободный участок пола.");
                return;
            }
            write = slot.WriteAsync(snapshot, !session.CanRestore(current) && session.CanRestore(previous));
            hud.Announce("Сохранение…");
        }

        public bool Load()
        {
            if (Busy()) return false;
            var incompatible = false;
            foreach (var source in readSlots)
            {
                var current = Read(source.CurrentPath);
                var previous = Read(source.PreviousPath);
                var valid = session.TryGetRestorePosition(current, out var position) ? current :
                    session.TryGetRestorePosition(previous, out position) ? previous : null;
                if (valid == null)
                {
                    incompatible |= Incompatible(current) || Incompatible(previous);
                    continue;
                }

                residentView.Reset();
                PreparePresentation(position);
                if (!session.Restore(valid))
                    throw new InvalidOperationException("Validated region snapshot could not be restored.");
                view.RefreshInteractions();
                presentationRevision = session.PresentationRevision;
                RestoreMotor();
                PresentResidents(0);
                interactions.Close();
                hud.Announce("Игра загружена.");
                return true;
            }
            var message = incompatible
                ? "Для сохранения требуется другая версия пакета региона."
                : "Подходящего сохранения нет.";
            hud.Announce(message);
            return false;
        }

        private bool Incompatible(RegionSnapshot snapshot) => snapshot?.PackageId != null && !session.Matches(snapshot);

        private bool Busy()
        {
            PollSave();
            if (!IsSaving) return false;
            hud.Announce("Идёт сохранение. Подождите немного.");
            return true;
        }

        private static RegionSnapshot Read(string path)
        {
            if (!File.Exists(path)) return null;
            try { return SaveSlot.Read<RegionSnapshot>(path); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            {
                Debug.LogWarning($"Unable to read the region slot: {error.Message}");
                return null;
            }
        }

        private void PollSave()
        {
            if (write == null || !write.IsCompleted) return;
            if (write.IsFaulted)
            {
                Debug.LogWarning($"Unable to write the region slot: {write.Exception.GetBaseException().Message}");
                hud.Announce("Не удалось сохранить игру.");
            }
            else hud.Announce("Игра сохранена.");
            write = null;
        }

        private void PreparePresentation(LocalPoint position) => view.Prepare(position);

        private bool CanResidentStep(string id, LocalPoint from, LocalPoint to)
        {
            if (!residentView.NeedsCollision(from, to)) return true;
            SyncWorld();
            return residentView.CanStep(id, from, to, session.Residents);
        }

        private void SyncWorld()
        {
            if (presentationRevision == session.PresentationRevision) return;
            view.RefreshInteractions();
            presentationRevision = session.PresentationRevision;
        }

        private void PresentResidents(float seconds) => residentView.Present(session.Residents, session.PlayerLocal, seconds);

        private void RestoreMotor()
        {
            motor.Restore(session.PlayerLocal, session.Yaw, session.Pitch);
            remainder = 0;
        }

        private string VisibleItem(string anchorId)
        {
            var container = session.GetInventory(anchorId);
            return container == null || container.Stacks.Count == 0 ? null : container.Stacks[0].Appearance ?? "";
        }

        public void SetPaused(bool value)
        {
            paused = value;
            if (value) interactions.Close();
            hud.SetPaused(value);
            SetModalMode(interactions.IsOpen);
        }

        private void SetModalMode(bool open)
        {
            hud.SetModalOpen(open);
            Cursor.lockState = paused || open || automation ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = paused || open || automation;
        }

        private void RefreshHud()
        {
            var prompt = paused ? null : interactions.Prompt(motor.Sight);
            if (prompt == null && world.DistanceToOutline(Flat(Position)) < 13) prompt = "Граница установленного района";
            hud.Refresh(session.MinuteOfDay, null, prompt);
        }

        public void Dispose()
        {
            try { write?.GetAwaiter().GetResult(); }
            catch (Exception error) { Debug.LogException(error); }
            residentView.Dispose();
            view.Dispose();
            if (root != null)
            {
                root.SetActive(false);
                UnityEngine.Object.Destroy(root);
            }
            if (sunlight != null)
            {
                sunlight.SetActive(false);
                UnityEngine.Object.Destroy(sunlight);
            }
            RenderSettings.fog = false;
        }

        private static GeoPoint Flat(Vector3 p) => new GeoPoint(p.x, p.z);
        private static bool Pressed(Keyboard k, Key key) => k != null && k[key].wasPressedThisFrame;
        private static bool Held(Keyboard k, Key key) => k != null && k[key].isPressed;
        private static int Axis(Keyboard k, Key a, Key b, Key c, Key d) =>
            (Held(k, a) || Held(k, b) ? 1 : 0) - (Held(k, c) || Held(k, d) ? 1 : 0);
    }
}
