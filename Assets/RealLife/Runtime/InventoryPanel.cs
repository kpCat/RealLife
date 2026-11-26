using System;
using System.Collections;
using System.Collections.Generic;
using RealLife.Simulation;
using UnityEngine.UIElements;

namespace RealLife.Runtime
{
    internal sealed class InventoryPanel
    {
        internal enum ActionKind
        {
            TransferToOther, TransferToPlayer, MergeToOther, MergeToPlayer, SplitPlayer, SplitOther,
            ChoosePlayerMergeTarget, ChooseOtherMergeTarget, MergePlayer, MergeOther, ReturnToPlacement, Close
        }

        private readonly Action<InventoryMove> move;
        private readonly Action<string, int> returnToPlacement;
        private readonly Func<InventoryMove, InventoryMoveFailure> checkMove;
        private readonly Func<string, int, InventoryMoveFailure> checkPlacement;
        private readonly Func<InventoryMoveFailure, string> reason;
        private readonly Dictionary<ActionKind, Button> buttons = new Dictionary<ActionKind, Button>();
        private readonly Column player;
        private readonly Column other;
        private readonly Label notice;
        private readonly Button place;
        private Column activeColumn;
        private string placementName;

        internal VisualElement Element { get; }
        internal bool IsOpen { get; private set; }

        internal InventoryPanel(Action<InventoryMove> move, Action<string, int> returnToPlacement, Action close,
            Func<InventoryMove, InventoryMoveFailure> checkMove,
            Func<string, int, InventoryMoveFailure> checkPlacement, Func<InventoryMoveFailure, string> reason)
        {
            this.move = move;
            this.returnToPlacement = returnToPlacement;
            this.checkMove = checkMove;
            this.checkPlacement = checkPlacement;
            this.reason = reason;
            Element = Block("inventory-overlay");
            var panel = Block("inventory-panel");
            Element.Add(panel);
            var header = Block("inventory-panel-header");
            panel.Add(header);
            header.Add(Text("Инвентарь", "inventory-panel-title"));
            header.Add(MakeButton(ActionKind.Close, "Закрыть", close));

            var columns = Block("inventory-columns");
            panel.Add(columns);
            player = new Column(this);
            other = new Column(this);
            other.Element.AddToClassList("inventory-column-other");
            columns.Add(player.Element);
            columns.Add(other.Element);
            ConfigureColumn(player, other, ActionKind.TransferToOther, ActionKind.MergeToOther, ActionKind.SplitPlayer,
                ActionKind.ChoosePlayerMergeTarget, ActionKind.MergePlayer, "Переместить →", "В стопку →");
            ConfigureColumn(other, player, ActionKind.TransferToPlayer, ActionKind.MergeToPlayer, ActionKind.SplitOther,
                ActionKind.ChooseOtherMergeTarget, ActionKind.MergeOther, "← Забрать", "← В стопку");
            place = MakeButton(ActionKind.ReturnToPlacement, "Положить", ReturnToPlacement);
            place.AddToClassList("inventory-place");
            player.Element.Add(place);
            notice = Text("", "inventory-notice");
            panel.Add(notice);
            Hide();
        }

        internal void Show(InventoryContainerView playerView, InventoryContainerView otherView = null, string returnPlacementName = null)
        {
            placementName = returnPlacementName;
            player.Show(playerView);
            other.Show(otherView);
            Display(other.Element, otherView != null);
            RefreshActions();
            if (IsOpen) return;
            IsOpen = true;
            Display(Element, true);
            Element.BringToFront();
            player.FocusList();
        }

        internal void Hide()
        {
            IsOpen = false;
            activeColumn = player;
            player.StopChoosingTarget();
            other.StopChoosingTarget();
            Announce(null);
            Display(Element, false);
        }

        internal void Announce(string message) => notice.text = message ?? "";

        internal bool SelectStack(string containerId, string instanceId)
        {
            if (!IsOpen) return false;
            var column = FindColumn(containerId);
            if (column == null || !column.Select(instanceId)) return false;
            activeColumn = column;
            return true;
        }

        internal bool SetQuantity(int quantity, string containerId = null)
        {
            var column = containerId == null ? activeColumn : FindColumn(containerId);
            if (!IsOpen || column?.Source == null) return false;
            column.Quantity.value = quantity;
            return true;
        }

        internal bool Click(ActionKind action)
        {
            if (!IsOpen || !buttons.TryGetValue(action, out var button) || !button.enabledInHierarchy || button.panel == null)
                return false;
            using var submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
            return submit.isPropagationStopped;
        }

        private Column FindColumn(string id)
        {
            if (id == null) return null;
            if (player.View?.ContainerId == id) return player;
            return other.View?.ContainerId == id ? other : null;
        }

        private void ConfigureColumn(Column source, Column destination, ActionKind transfer, ActionKind mergeAcross,
            ActionKind split, ActionKind chooseTarget, ActionKind merge, string transferText, string mergeText)
        {
            source.Transfer = MakeButton(transfer, transferText, () => Move(source, destination));
            source.MergeAcross = MakeButton(mergeAcross, mergeText, () => Move(source, destination, destination.Source));
            source.MergeAcrossName = mergeText;
            source.TransferRow.Add(source.Transfer);
            source.TransferRow.Add(source.MergeAcross);
            source.MergeAcross.tooltip = "Выберите совместимую стопку в другом контейнере.";

            source.Split = MakeButton(split, "Разделить", () => Move(source, source));
            source.ChooseTarget = MakeButton(chooseTarget, "Выбрать цель", () =>
            {
                source.ToggleTargetSelection();
                RefreshActions();
            });
            source.Merge = MakeButton(merge, "Объединить", () => Move(source, source, source.Target));
            source.ChooseTarget.tooltip = "Выберите вторую стопку в этом же списке.";
            source.StackRow.Add(source.Split);
            source.StackRow.Add(source.ChooseTarget);
            source.StackRow.Add(source.Merge);
        }

        private void Move(Column source, Column destination, InventoryStackView? target = null)
        {
            if (source.Source is not InventoryStackView selected || destination.View == null) return;
            move(Command(source, destination, selected, target));
        }

        private void ReturnToPlacement()
        {
            if (player.Source is InventoryStackView selected && placementName != null)
                returnToPlacement(selected.InstanceId, player.Quantity.value);
        }

        private void RefreshActions()
        {
            RefreshActions(player, other);
            RefreshActions(other, player);
            var canPlace = !string.IsNullOrEmpty(placementName);
            Display(place, canPlace);
            place.text = $"Положить · {placementName}";
            SetAvailable(place, canPlace && player.ValidQuantity && player.Source is InventoryStackView selected
                ? checkPlacement(selected.InstanceId, player.Quantity.value) : InventoryMoveFailure.InvalidQuantity);
        }

        private void RefreshActions(Column source, Column destination)
        {
            var selected = source.Source;
            var max = selected?.Quantity ?? 0;
            var splitLimit = max - 1;
            var sameMergeLimit = MergeLimit(selected, source.Target);
            var acrossMergeLimit = MergeLimit(selected, destination.Source);
            var canTransfer = destination.View != null;
            var amount = source.Quantity.value;

            Display(source.TransferRow, canTransfer);
            Display(source.Split, !source.ChoosingTarget);
            Display(source.Merge, source.ChoosingTarget);
            source.Quantity.SetEnabled(max > 0);
            source.Hint.text = source.DescribeSelection();
            Check(source.Transfer, source, destination, canTransfer && source.ValidQuantity);
            source.ChooseTarget.text = source.ChoosingTarget ? "Отменить" : "Выбрать цель";
            source.ChooseTarget.SetEnabled(selected != null && source.Count > 1);
            Check(source.Split, source, source, UpdateLimit(source.Split, "Разделить", amount,
                source.ChoosingTarget ? 0 : splitLimit));
            Check(source.Merge, source, source, UpdateLimit(source.Merge, "Объединить", amount,
                source.ChoosingTarget ? sameMergeLimit : 0), source.Target);
            Check(source.MergeAcross, source, destination, UpdateLimit(source.MergeAcross, source.MergeAcrossName, amount,
                acrossMergeLimit), destination.Source);
        }

        private void Check(Button button, Column source, Column destination, bool valid, InventoryStackView? target = null)
        {
            var result = valid && source.Source is InventoryStackView selected && destination.View != null
                ? checkMove(Command(source, destination, selected, target)) : InventoryMoveFailure.InvalidQuantity;
            SetAvailable(button, result);
        }

        private void SetAvailable(Button button, InventoryMoveFailure result)
        {
            button.SetEnabled(result == InventoryMoveFailure.None);
            button.tooltip = result == InventoryMoveFailure.None ? null : reason(result);
        }

        private static InventoryMove Command(Column source, Column destination, InventoryStackView selected, InventoryStackView? target)
            => new InventoryMove(source.View.ContainerId, selected.InstanceId, source.Quantity.value,
                destination.View.ContainerId, target?.InstanceId);

        private static int MergeLimit(InventoryStackView? selected, InventoryStackView? other)
        {
            if (selected is not InventoryStackView source || other is not InventoryStackView target ||
                source.InstanceId == target.InstanceId ||
                source.DefinitionId != target.DefinitionId || source.VariantId != target.VariantId) return 0;
            return Math.Min(source.Quantity, target.MaxStack - target.Quantity);
        }

        private static bool UpdateLimit(Button button, string text, int quantity, int limit)
        {
            button.text = limit > 1 ? $"{text} (1–{limit})" : text;
            return quantity >= 1 && quantity <= limit;
        }

        private Button MakeButton(ActionKind action, string label, Action onClick)
        {
            var button = new Button(onClick) { text = label };
            button.AddToClassList("inventory-button");
            buttons.Add(action, button);
            return button;
        }

        private static VisualElement Block(string className)
        {
            var element = new VisualElement();
            element.AddToClassList(className);
            return element;
        }

        private static Label Text(string text, string className)
        {
            var element = new Label(text) { pickingMode = PickingMode.Ignore };
            element.AddToClassList(className);
            return element;
        }

        private static void Display(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        private sealed class Column
        {
            private readonly InventoryPanel panel;
            private IReadOnlyList<InventoryStackView> stacks = Array.Empty<InventoryStackView>();
            private readonly Label heading;
            private readonly Label capacity;
            private readonly Label empty;
            private readonly ScrollView scroll;
            private int sourceIndex = -1;
            private int targetIndex = -1;

            public VisualElement Element { get; }
            private ListView List { get; }
            public Label Hint { get; }
            public IntegerField Quantity { get; }
            public VisualElement TransferRow { get; }
            public VisualElement StackRow { get; }
            public Button Transfer { get; set; }
            public Button MergeAcross { get; set; }
            public string MergeAcrossName { get; set; }
            public Button Split { get; set; }
            public Button ChooseTarget { get; set; }
            public Button Merge { get; set; }
            public InventoryContainerView View { get; private set; }
            public bool ChoosingTarget { get; private set; }
            public int Count => stacks.Count;
            public InventoryStackView? Source => At(sourceIndex);
            public InventoryStackView? Target => At(targetIndex);
            public bool ValidQuantity => Source is InventoryStackView source && Quantity.value >= 1 && Quantity.value <= source.Quantity;

            public Column(InventoryPanel panel)
            {
                this.panel = panel;
                Element = Block("inventory-column");
                heading = Text("", "inventory-column-title");
                capacity = Text("", "inventory-capacity");
                Element.Add(heading);
                Element.Add(capacity);

                var listArea = Block("inventory-list-area");
                Element.Add(listArea);
                List = new ListView(Array.Empty<InventoryStackView>(), 34, () => Text("", "inventory-stack"), BindRow)
                {
                    virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                    selectionType = SelectionType.Single
                };
                List.AddToClassList("inventory-list");
                scroll = List.Q<ScrollView>();
                if (scroll != null)
                {
                    scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
                    scroll.mouseWheelScrollSize = 68;
                }
                List.selectionChanged += _ => SelectionChanged();
                listArea.Add(List);
                empty = Text("Пусто", "inventory-empty");
                listArea.Add(empty);

                Hint = Text("", "inventory-selection");
                Element.Add(Hint);
                Quantity = new IntegerField("Количество") { value = 1 };
                Quantity.AddToClassList("inventory-quantity");
                Quantity.RegisterValueChangedCallback(_ =>
                {
                    panel.activeColumn = this;
                    panel.RefreshActions();
                });
                Element.Add(Quantity);
                TransferRow = Block("inventory-actions");
                StackRow = Block("inventory-actions");
                Element.Add(TransferRow);
                Element.Add(StackRow);
            }

            public void Show(InventoryContainerView view)
            {
                if (ReferenceEquals(View, view)) return;
                var sameContainer = View != null && view != null && View.ContainerId == view.ContainerId;
                View = view;
                heading.text = view?.DisplayName ?? "";
                var currentStacks = view?.Stacks ?? Array.Empty<InventoryStackView>();
                if (view != null) ShowCapacity(view);
                if (sameContainer && ReferenceEquals(stacks, currentStacks)) return;

                var sourceId = sameContainer ? Source?.InstanceId : null;
                var targetId = sameContainer ? Target?.InstanceId : null;
                var offset = scroll?.scrollOffset;
                SetListSelection(-1);
                stacks = currentStacks;
                sourceIndex = Find(sourceId);
                targetIndex = Find(targetId);
                ChoosingTarget = ChoosingTarget && sourceIndex >= 0 && stacks.Count > 1;
                if (!ChoosingTarget) targetIndex = -1;
                List.itemsSource = stacks as IList ?? new List<InventoryStackView>(stacks);
                SetListSelection(ChoosingTarget ? targetIndex : sourceIndex);
                if (sameContainer && offset.HasValue) scroll.scrollOffset = offset.Value;
                else if (stacks.Count != 0) List.ScrollToItem(0);
                Quantity.SetValueWithoutNotify(Source is InventoryStackView source
                    ? Math.Max(1, Math.Min(Quantity.value, source.Quantity)) : 1);
                Display(empty, stacks.Count == 0);
            }

            public bool Select(string instanceId)
            {
                var index = Find(instanceId);
                if (index < 0) return false;
                List.SetSelection(index);
                List.ScrollToItem(index);
                return true;
            }

            public void FocusList() => (scroll?.contentContainer ?? (VisualElement)List).Focus();

            public void ToggleTargetSelection()
            {
                if (ChoosingTarget)
                {
                    StopChoosingTarget();
                    return;
                }
                ChoosingTarget = true;
                SetListSelection(-1);
                FocusList();
            }

            public void StopChoosingTarget()
            {
                if (!ChoosingTarget) return;
                ChoosingTarget = false;
                var previousTarget = targetIndex;
                targetIndex = -1;
                SetListSelection(sourceIndex);
                RefreshRow(previousTarget);
            }

            public string DescribeSelection()
            {
                if (Source is not InventoryStackView selected) return "Выберите стопку";
                if (!ChoosingTarget) return $"{selected.DisplayName} · от 1 до {selected.Quantity} шт.";
                if (Target == null) return $"{selected.DisplayName} · выберите вторую стопку";
                return MergeLimit(selected, Target) > 0 ? $"{selected.DisplayName} · выбрана цель"
                    : "Выберите другую совместимую стопку со свободным местом";
            }

            private void ShowCapacity(InventoryContainerView view)
            {
                var limits = view.Capacity;
                var maxSlots = limits.MaxSlots != InventoryCapacity.Unlimited ? limits.MaxSlots.ToString() : "∞";
                var maxWeight = limits.MaxWeightGrams != InventoryCapacity.Unlimited ? $"{limits.MaxWeightGrams / 1000m:0.###} кг" : "∞";
                var slots = view.Stacks.Count;
                var overfull = limits.MaxSlots != InventoryCapacity.Unlimited && slots > limits.MaxSlots ||
                    limits.MaxWeightGrams != InventoryCapacity.Unlimited && view.TotalWeightGrams > limits.MaxWeightGrams;
                capacity.text = $"{slots} / {maxSlots} мест · {view.TotalWeightGrams / 1000m:0.###} кг / {maxWeight}" +
                    (overfull ? " · превышен предел" : "");
                capacity.EnableInClassList("inventory-overfull", overfull);
            }

            private void SelectionChanged()
            {
                if (ChoosingTarget)
                {
                    var previous = targetIndex;
                    targetIndex = List.selectedIndex;
                    RefreshRow(previous);
                    RefreshRow(targetIndex);
                }
                else if (sourceIndex != List.selectedIndex)
                {
                    var previous = sourceIndex;
                    sourceIndex = List.selectedIndex;
                    Quantity.SetValueWithoutNotify(1);
                    RefreshRow(previous);
                    RefreshRow(sourceIndex);
                }
                panel.activeColumn = this;
                panel.RefreshActions();
            }

            private void RefreshRow(int index)
            {
                if (index >= 0) List.RefreshItem(index);
            }

            private void BindRow(VisualElement element, int index)
            {
                var stack = stacks[index];
                var row = (Label)element;
                row.text = $"{stack.DisplayName}   ×{stack.Quantity}";
                row.tooltip = stack.DisplayName;
                row.EnableInClassList("inventory-stack-source", index == sourceIndex);
                row.EnableInClassList("inventory-stack-target", index == targetIndex);
            }

            private int Find(string id)
            {
                if (id != null)
                    for (var i = 0; i < stacks.Count; i++)
                        if (stacks[i].InstanceId == id) return i;
                return -1;
            }

            private InventoryStackView? At(int index) => index >= 0 && index < stacks.Count ? stacks[index] : null;
            private void SetListSelection(int index) =>
                List.SetSelectionWithoutNotify(index < 0 ? Array.Empty<int>() : new[] { index });
        }
    }
}
