using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace RealLife.Runtime
{
    internal sealed class GameHud
    {
        private readonly Label time;
        private readonly Label inventory;
        private readonly Label prompt;
        private readonly Label notice;
        private readonly VisualElement pause;
        private readonly VisualElement crosshair;
        private readonly VisualElement context;
        private bool paused;
        private bool modalOpen;
        private string currentNotice;
        private float noticeUntil;

        public GameHud(UIDocument document, StyleSheet styles, string location, Action resume, Action save, Action load,
            Action openWorlds = null, bool hasItems = true, bool regionalInventory = false)
        {
            var root = document.rootVisualElement;
            root.Clear();
            if (!root.styleSheets.Contains(styles)) root.styleSheets.Add(styles);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            root.style.unityFontDefinition = FontDefinition.FromFont(font);
            root.pickingMode = PickingMode.Ignore;
            root.Add(Label(location, "location"));
            time = Label("", "clock");
            root.Add(time);

            crosshair = new VisualElement { pickingMode = PickingMode.Ignore };
            crosshair.AddToClassList("crosshair");
            root.Add(crosshair);

            context = new VisualElement { pickingMode = PickingMode.Ignore };
            context.AddToClassList("context");
            prompt = Label("", "prompt");
            notice = Label("", "notice");
            context.Add(prompt);
            context.Add(notice);
            root.Add(context);

            inventory = Label("", "inventory");
            root.Add(inventory);
            if (!hasItems) inventory.style.display = DisplayStyle.None;
            var controls = regionalInventory ? "WASD  Движение     E  Действие     I  Инвентарь     F5 / F9  Сохранить / загрузить"
                : hasItems ? "WASD  Движение     E  Действие     F5 / F9  Сохранить / загрузить"
                : "WASD  Движение     F5 / F9  Сохранить / загрузить     Esc  Меню";
            root.Add(Label(controls, "controls"));

            pause = new VisualElement();
            pause.AddToClassList("pause");
            var menu = new VisualElement();
            menu.AddToClassList("pause-menu");
            menu.Add(Label("Пауза", "pause-title"));
            menu.Add(new Button(resume) { text = "Продолжить" });
            menu.Add(new Button(save) { text = "Сохранить" });
            menu.Add(new Button(load) { text = "Загрузить" });
            if (openWorlds != null) menu.Add(new Button(openWorlds) { text = "Выбор мира" });
            menu.Add(new Button(() => Application.Quit()) { text = "Выйти из игры" });
            pause.Add(menu);
            root.Add(pause);
            context.BringToFront();
            SetPaused(false);
        }

        public void SetPaused(bool paused)
        {
            this.paused = paused;
            pause.style.display = paused ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshCrosshair();
        }

        public void SetModalOpen(bool open)
        {
            modalOpen = open;
            context.style.display = open ? DisplayStyle.None : DisplayStyle.Flex;
            RefreshCrosshair();
        }

        private void RefreshCrosshair() => crosshair.style.display = paused || modalOpen
            ? DisplayStyle.None : DisplayStyle.Flex;

        public void Announce(string message)
        {
            currentNotice = message;
            noticeUntil = Time.unscaledTime + 4;
        }

        public void Refresh(int minute, string carried, string interaction)
        {
            SetText(time, $"{minute / 60:00}:{minute % 60:00}");
            SetText(inventory, carried == null ? "Инвентарь · пусто" : $"Инвентарь · {carried}");
            SetOptionalText(prompt, interaction);
            SetOptionalText(notice, Time.unscaledTime < noticeUntil ? currentNotice : null);
        }

        private static void SetOptionalText(Label label, string value)
        {
            SetText(label, value ?? "");
            label.EnableInClassList("hidden", string.IsNullOrEmpty(value));
        }

        private static void SetText(Label label, string value)
        {
            if (label.text != value) label.text = value;
        }

        private static Label Label(string text, string className)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            return label;
        }
    }
}
