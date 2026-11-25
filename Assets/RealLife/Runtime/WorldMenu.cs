using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using RealLife.Simulation;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace RealLife.Runtime
{
    public sealed class WorldMenu : MonoBehaviour
    {
        [SerializeField] private UIDocument document;
        [SerializeField] private StyleSheet menuStyle;
        [SerializeField] private StyleSheet hudStyle;
        [SerializeField] private Material globeMaterial;
        [SerializeField] private Material[] regionPalette;

        private const string GeoDirectory = "RealLifeGeo";
        private Texture2D earthTexture;
        private GlobeView globe;
        private RegionMap map;
        private RegionWorld world;
        private RegionGame game;
        private VisualElement sidebar, viewport;
        private Label status;
        private GeoPoint selected;
        private Button start;
        private string saveRoot;
        private bool automation;

        internal RegionWorld Region => world;
        internal GlobeView Globe => globe;
        internal RegionMap Map => map;
        internal RegionGame Game => game;
        internal string Status => status?.text;

        public void Configure(UIDocument ui, StyleSheet menu, StyleSheet hud, Material globe, Material[] palette)
        {
            document = ui;
            menuStyle = menu;
            hudStyle = hud;
            globeMaterial = globe;
            regionPalette = palette;
        }

        private IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "-realLifeSmoke") >= 0)
            {
                OpenYard();
                yield break;
            }

            var smoke = Array.IndexOf(args, "-realLifeGeoSmoke");
            var restart = Array.IndexOf(args, "-realLifeGeoResume");
            automation = smoke >= 0 || restart >= 0;
            saveRoot = automation ? Path.GetFullPath(args[Math.Max(smoke, restart) + 1]) :
                Path.Combine(Application.persistentDataPath, "Saves", "Earth");
            ShowGlobe(loading: true);

            var directory = Application.isEditor
                ? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds", "GeoData")
                : Path.Combine(Application.streamingAssetsPath, GeoDirectory);
            var loadTexture = Task.Run(() => File.ReadAllBytes(Path.Combine(directory, "earth.png")));
            var loadPackage = Task.Run(() => new RegionWorld(JsonUtility.FromJson<RegionDefinition>(
                File.ReadAllText(Path.Combine(directory, "region.json")))));
            while (!loadTexture.IsCompleted || !loadPackage.IsCompleted) yield return null;
            if (loadTexture.Status == TaskStatus.RanToCompletion)
            {
                earthTexture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                if (!earthTexture.LoadImage(loadTexture.Result, markNonReadable: true))
                {
                    Destroy(earthTexture);
                    earthTexture = null;
                }
            }
            else Debug.LogWarning($"Globe data unavailable: {loadTexture.Exception?.GetBaseException().Message}");
            if (loadPackage.Status == TaskStatus.RanToCompletion) world = loadPackage.Result;
            else Debug.LogWarning($"Region unavailable: {loadPackage.Exception?.GetBaseException().Message}");
            ShowGlobe();
            if (automation) yield return RegionSmoke.Run(this, restart >= 0);
        }

        private void Update()
        {
            if (!automation) game?.ReadInput(Mathf.Min(Time.deltaTime, .1f));
        }

        private void OnApplicationFocus(bool focus)
        {
            if (!focus && !automation) game?.SetPaused(true);
        }

        private void OnDestroy()
        {
            game?.Dispose();
            globe?.Dispose();
            if (earthTexture != null) Destroy(earthTexture);
        }

        private void ShowGlobe(bool loading = false)
        {
            game?.Dispose();
            game = null;
            map = null;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Layout();
            sidebar.Add(Text("Выберите место на Земле", "world-title"));
            sidebar.Add(Text("Вращайте глобус мышью, приближайте колёсиком.", "world-copy"));
            sidebar.Add(Text(world?.Title ?? "Реальный район", "world-place"));
            sidebar.Add(Text(world == null ? "Подробная карта и прогулка доступны при установленном пакете."
                : "Молдова · пригород Кишинёва", "world-copy"));
            var open = Button("Открыть карту", () =>
            {
                if (world != null) ChooseGlobe(world.Anchor.Latitude, world.Anchor.Longitude);
            }, true);
            open.SetEnabled(world != null);
            sidebar.Add(open);
            sidebar.Add(status = Text(loading ? "Загружаем географию…" :
                world == null ? "Пакет подробной географии недоступен." : "", "world-status"));
            sidebar.Add(Block("world-spacer"));
            sidebar.Add(Text("Отдельный сценарий", "world-copy"));
            sidebar.Add(Button("Двор на Садовой", OpenYard));
            sidebar.Add(Button("Выйти из игры", () => Application.Quit()));
            if (earthTexture != null)
            {
                globe ??= new GlobeView(earthTexture, globeMaterial, world, ChooseGlobe);
                globe.SetVisible(true);
                viewport.Add(globe.Element);
            }
        }

        internal void ChooseGlobe(double latitude, double longitude)
        {
            if (world == null || !world.Covers(latitude, longitude))
            {
                status.text = "Для этого места нет установленного пакета подробной географии.";
                return;
            }

            globe?.SetVisible(false);
            Layout();
            sidebar.Add(Text(world.Title, "world-title"));
            sidebar.Add(Text("Нажмите на карте, чтобы выбрать старт. Колёсико меняет масштаб, перетаскивание двигает карту.", "world-copy"));
            sidebar.Add(Text("Светлые линии — дороги. Светлые контуры — здания. Золотая линия отмечает край покрытия.", "world-copy"));
            sidebar.Add(start = Button("Начать прогулку", () => Launch(false), true));
            var resume = Button("Продолжить сохранение", () => Launch(true));
            var files = new SaveSlot(PackageSaveDirectory);
            resume.SetEnabled(File.Exists(files.CurrentPath) || File.Exists(files.PreviousPath));
            sidebar.Add(resume);
            sidebar.Add(status = Text("", "world-status"));
            sidebar.Add(Block("world-spacer"));
            sidebar.Add(Text("Фасады и растительность реконструированы. Здания закрыты.", "world-copy"));
            sidebar.Add(Button("К глобусу", () => ShowGlobe()));
            selected = world.Route[world.Route.Count / 2];
            if (!world.IsSafeStart(selected))
                foreach (var point in world.Route)
                    if (world.IsSafeStart(point)) { selected = point; break; }
            map = new RegionMap(world, selected, ChooseStart);
            viewport.Add(map);
            ChooseStart(selected);
        }

        private void ChooseStart(GeoPoint point)
        {
            if (!world.IsSafeStart(point))
            {
                status.text = "Выберите свободное место внутри границы покрытия.";
                return;
            }
            selected = point;
            map.Selection = point;
            map.MarkDirtyRepaint();
            start.SetEnabled(true);
            var meters = new LocalPoint(point.X, 0, point.Z);
            var location = WorldFrame.ToGeodetic(world.Frame.ToGlobal(meters));
            status.text = $"{location.Latitude:F5}° с. ш. · {location.Longitude:F5}° в. д.";
        }

        internal bool Launch(bool resume)
        {
            if (map == null || game != null || !world.IsSafeStart(selected)) return false;
            var yaw = 165f;
            game = new RegionGame(world, selected, yaw, document, hudStyle, regionPalette,
                PackageSaveDirectory, () => ShowGlobe(), automation);
            if (resume && !game.Load())
            {
                game.Dispose();
                game = null;
                ChooseGlobe(world.Anchor.Latitude, world.Anchor.Longitude);
                status.text = "Для сохранения нужен подходящий пакет региона и исправный файл.";
                return false;
            }
            return true;
        }

        private string PackageSaveDirectory => Path.Combine(saveRoot, world.PackageId);

        private void Layout()
        {
            var root = document.rootVisualElement;
            root.Clear();
            root.pickingMode = PickingMode.Position;
            if (!root.styleSheets.Contains(menuStyle)) root.styleSheets.Add(menuStyle);
            root.style.unityFontDefinition = FontDefinition.FromFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            var frame = Block("world-menu");
            root.Add(frame);
            var header = Block("world-header");
            frame.Add(header);
            header.Add(Text("RealLife", "world-brand"));
            header.Add(Text(Application.version, "world-version"));
            var body = Block("world-body");
            frame.Add(body);
            body.Add(viewport = Block("world-view"));
            body.Add(sidebar = Block("world-sidebar"));
            frame.Add(Text(world?.Attribution ?? "Natural Earth", "world-footer"));
        }

        private static void OpenYard() => SceneManager.LoadScene("Sadovaya");

        private static VisualElement Block(string style)
        {
            var element = new VisualElement();
            element.AddToClassList(style);
            return element;
        }

        private static Label Text(string text, string style)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(style);
            return label;
        }

        private static Button Button(string text, Action action, bool primary = false)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("world-button");
            if (primary) button.AddToClassList("world-primary");
            return button;
        }
    }
}
