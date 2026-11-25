using System;
using System.IO;
using System.Threading.Tasks;
using RealLife.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace RealLife.Runtime
{
    public sealed class DistrictGame : MonoBehaviour
    {
        [SerializeField] private TextAsset districtMap;
        [SerializeField] private CharacterController character;
        [SerializeField] private Camera playerCamera;
        [SerializeField] private Transform doorLeaf;
        [SerializeField] private Collider doorCollider;
        [SerializeField] private GameObject itemModel;
        [SerializeField] private Collider itemTarget;
        [SerializeField] private UIDocument ui;
        [SerializeField] private StyleSheet hudStyle;

        private FirstPersonMotor motor;
        private GameHud hud;
        private DistrictDefinition district;
        private GameSession session;
        private SaveSlot slot;
        private Task saveTask;
        private bool paused;
        private bool smoke;
        private float tickRemainder;
        private SlotCommand pendingSlot;
        private Collider pendingInteraction;

        internal enum SlotCommand { None, Save, Load }
        internal Vector3 PlayerPosition => motor.Position;
        internal Ray Sight => motor.Sight;
        internal Vector3 DoorTarget => doorCollider.bounds.center;
        internal Vector3 ItemTarget => itemTarget.bounds.center;
        internal float Yaw => motor.Yaw;
        internal float Pitch => motor.Pitch;
        internal bool IsPaused => paused;
        internal bool IsDoorOpen => session.IsDoorOpen;
        internal bool IsCarrying => session.IsCarryingItem;
        internal bool IsSaving => saveTask != null;
        internal bool FocusIsDoor => FocusedCollider() == doorCollider;
        internal bool FocusIsItem => FocusedCollider() == itemTarget;
        internal string SavePath => slot.CurrentPath;
        internal string PreviousSavePath => slot.PreviousPath;

        public void Configure(TextAsset map, CharacterController controller, Camera camera, Transform hinge,
            Collider leaf, GameObject model, Collider target, UIDocument document, StyleSheet style)
        {
            districtMap = map;
            character = controller;
            playerCamera = camera;
            doorLeaf = hinge;
            doorCollider = leaf;
            itemModel = model;
            itemTarget = target;
            ui = document;
            hudStyle = style;
        }

        private void Start()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var smokeIndex = Array.IndexOf(args, "-realLifeSmoke");
                smoke = smokeIndex >= 0;
                var saveRoot = smoke
                    ? Path.GetFullPath(args[smokeIndex + 1])
                    : Path.Combine(Application.persistentDataPath, "Saves");
                district = JsonUtility.FromJson<DistrictDefinition>(districtMap.text);
                session = new GameSession(district);
                motor = new FirstPersonMotor(character, playerCamera);
                slot = new SaveSlot(saveRoot);
                Action worlds = Application.CanStreamedLevelBeLoaded("Earth") ? OpenWorlds : null;
                hud = new GameHud(ui, hudStyle, district.Title, () => SetPaused(false),
                    () => pendingSlot = SlotCommand.Save, () => pendingSlot = SlotCommand.Load, worlds);
                RestorePresentation();
                SetPaused(false);
                RefreshHud();
                if (smoke)
                    StartCoroutine(WindowsSmoke.Run(this, saveRoot));
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                enabled = false;
                if (smoke) Application.Quit(1);
            }
        }

        private void Update()
        {
            if (!smoke) ReadInput(Keyboard.current, Mouse.current, Mathf.Min(Time.deltaTime, 0.1f));
        }

        internal void ReadInput(Keyboard keyboard, Mouse mouse, float seconds)
        {
            if (Pressed(keyboard, Key.Escape)) SetPaused(!paused);
            var saveCommand = Pressed(keyboard, Key.F5) ? SlotCommand.Save :
                Pressed(keyboard, Key.F9) ? SlotCommand.Load : SlotCommand.None;
            if (paused)
            {
                PollSave();
                if (saveCommand != SlotCommand.None) pendingSlot = saveCommand;
                ProcessSlot();
                RefreshHud();
                return;
            }

            var movement = new Vector2(
                Axis(keyboard, Key.D, Key.RightArrow, Key.A, Key.LeftArrow),
                Axis(keyboard, Key.W, Key.UpArrow, Key.S, Key.DownArrow));
            var look = mouse == null ? Vector2.zero : mouse.delta.ReadValue();
            SimulateFrame(movement, look, Pressed(keyboard, Key.E), saveCommand, seconds);
        }

        internal void SimulateFrame(Vector2 movement, Vector2 look, bool interact, SlotCommand saveCommand, float seconds)
        {
            PollSave();
            motor.Drive(movement, look, seconds);
            if (!session.SetPlayerPose(FirstPersonMotor.ToLocal(motor.Position), motor.Yaw, motor.Pitch))
                motor.Restore(session.PlayerLocal, session.Yaw, session.Pitch);

            if (interact) pendingInteraction = FocusedCollider();
            if (saveCommand != SlotCommand.None) pendingSlot = saveCommand;
            tickRemainder += seconds;
            const float step = 1f / GameSession.TicksPerSecond;
            while (tickRemainder >= step)
            {
                tickRemainder -= step;
                session.AdvanceTick();
                ProcessInteraction();
                ProcessSlot();
            }

            RefreshHud();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && hud != null && !smoke) SetPaused(true);
        }

        private void OnApplicationQuit()
        {
            try { saveTask?.GetAwaiter().GetResult(); }
            catch (Exception error) { Debug.LogException(error); }
        }

        private void OpenWorlds()
        {
            OnApplicationQuit();
            SceneManager.LoadScene("Earth");
        }

        private void SetPaused(bool value)
        {
            paused = value;
            pendingInteraction = null;
            hud.SetPaused(value);
            Cursor.lockState = value || smoke ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = value || smoke;
        }

        private Collider FocusedCollider()
        {
            return Physics.Raycast(motor.Sight, out var hit, GameSession.Reach, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide) ? hit.collider : null;
        }

        private void ProcessInteraction()
        {
            var target = pendingInteraction;
            pendingInteraction = null;
            if (target == null || target != FocusedCollider()) return;

            ActionResult result;
            if (target == doorCollider)
                result = session.Apply(GameAction.ToggleDoor, session.DoorId);
            else if (target == itemTarget)
                result = session.Apply(session.IsCarryingItem ? GameAction.ReturnItem : GameAction.TakeItem, session.ItemId);
            else return;

            if (result == ActionResult.Applied) ProjectObjects();
            if (result == ActionResult.DoorObstructed) hud.Announce("Отойдите от дверного проёма.");
            if (result == ActionResult.OutOfReach) hud.Announce("Подойдите ближе.");
        }

        private void ProcessSlot()
        {
            var command = pendingSlot;
            pendingSlot = SlotCommand.None;
            if (command == SlotCommand.None) return;
            if (saveTask != null)
            {
                hud.Announce("Идёт сохранение. Подождите немного.");
                return;
            }

            if (command == SlotCommand.Save)
            {
                var preservePrevious = !session.CanRestore(ReadSnapshot(slot.CurrentPath)) &&
                    session.CanRestore(ReadSnapshot(slot.PreviousPath));
                saveTask = slot.WriteAsync(session.Capture(), preservePrevious);
                hud.Announce("Сохранение…");
                return;
            }

            if (TryRestore(slot.CurrentPath)) hud.Announce("Игра загружена.");
            else if (TryRestore(slot.PreviousPath)) hud.Announce("Загружено предыдущее сохранение.");
            else hud.Announce("Подходящего сохранения нет.");
        }

        private bool TryRestore(string path)
        {
            if (!session.Restore(ReadSnapshot(path))) return false;
            tickRemainder = 0;
            RestorePresentation();
            return true;
        }

        private static SessionSnapshot ReadSnapshot(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return SaveSlot.Read(path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            {
                Debug.LogWarning($"Unable to read the save slot: {error.Message}");
                return null;
            }
        }

        private void PollSave()
        {
            if (saveTask == null || !saveTask.IsCompleted) return;
            if (saveTask.IsFaulted)
            {
                Debug.LogWarning($"Unable to write the save slot: {saveTask.Exception.GetBaseException().Message}");
                hud.Announce("Не удалось сохранить игру.");
            }
            else hud.Announce("Игра сохранена.");
            saveTask = null;
        }

        private void RestorePresentation()
        {
            motor.Restore(session.PlayerLocal, session.Yaw, session.Pitch);
            ProjectObjects();
        }

        private void ProjectObjects()
        {
            doorLeaf.localRotation = Quaternion.Euler(0, session.IsDoorOpen ? -96 : 0, 0);
            itemModel.SetActive(!session.IsCarryingItem);
            Physics.SyncTransforms();
        }

        private void RefreshHud()
        {
            string prompt = null;
            if (!paused)
            {
                var target = FocusedCollider();
                if (target == doorCollider) prompt = session.IsDoorOpen ? "E  Закрыть дверь" : "E  Открыть дверь";
                if (target == itemTarget) prompt = session.IsCarryingItem ? "E  Положить фонарь" : "E  Взять фонарь";
            }
            hud.Refresh(session.MinuteOfDay, session.IsCarryingItem ? district.Item.Name : null, prompt);
        }

        private static bool Pressed(Keyboard keyboard, Key key) => keyboard != null && keyboard[key].wasPressedThisFrame;
        private static bool Held(Keyboard keyboard, Key key) => keyboard != null && keyboard[key].isPressed;

        private static int Axis(Keyboard keyboard, Key positive, Key positiveArrow, Key negative, Key negativeArrow) =>
            (Held(keyboard, positive) || Held(keyboard, positiveArrow) ? 1 : 0) -
            (Held(keyboard, negative) || Held(keyboard, negativeArrow) ? 1 : 0);
    }
}
