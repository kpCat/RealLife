using System;
using System.Collections.Generic;
using RealLife.Simulation;
using UnityEngine;
using UnityEngine.UIElements;

namespace RealLife.Runtime
{
    internal sealed class RegionInteractions
    {
        private const float Distance = 2.65f;
        private readonly RegionSession session;
        private readonly RegionView view;
        private readonly GameHud hud;
        private readonly Action<bool> modalChanged;
        private readonly List<IInteractionOffer> offers = new List<IInteractionOffer>(3);
        private string anchorId;

        private readonly struct DoorCommand
        {
            public readonly string Id;
            public DoorCommand(string id) => Id = id;
        }

        private readonly struct InspectContainer
        {
            public readonly string Id;
            public InspectContainer(string id) => Id = id;
        }

        public RegionInteractions(RegionSession session, RegionView view, GameHud hud, VisualElement root,
            Action<bool> modalChanged)
        {
            this.session = session;
            this.view = view;
            this.hud = hud;
            this.modalChanged = modalChanged;
            Panel = new InventoryPanel(MoveFromPanel, ReturnToPlacement, Close,
                command => session.CheckMove(command, anchorId), CheckReturnToPlacement, Describe);
            root.Add(Panel.Element);
        }

        public InventoryPanel Panel { get; }
        public bool IsOpen => Panel.IsOpen;

        public bool TryFocus(Ray ray, out BuildingFocus focus) => view.TryFocus(ray, Distance, out focus);

        public string Prompt(Ray ray)
        {
            if (!TryFocus(ray, out var target)) return null;
            Resolve(target);
            if (offers.Count == 0)
                return target.Kind == BuildingFocusKind.Unsupported ? "В это здание пока нельзя войти" : null;
            return offers.Count == 1 ? offers[0].Prompt : offers[0].Prompt + "     " + offers[1].Prompt;
        }

        public bool Activate(InteractionControl control, Ray ray)
        {
            if (IsOpen) return false;
            if (TryFocus(ray, out var target))
            {
                Resolve(target);
                foreach (var offer in offers)
                    if (offer.Control == control)
                    {
                        offer.Execute();
                        return true;
                    }
                if (control == InteractionControl.Details && session.CanUseAnchor(target.Id))
                {
                    Open(new InspectContainer(target.Id));
                    return true;
                }
            }
            if (control != InteractionControl.Details) return false;
            Open(new InspectContainer(null));
            return true;
        }

        private void Resolve(BuildingFocus focus)
        {
            offers.Clear();
            OfferDoor(focus.Id);
            OfferPickup(focus);
            OfferInspection(focus);
        }

        private void OfferDoor(string id)
        {
            if (id == null || !session.Buildings.TryGetDoor(id, out _)) return;
            var prompt = session.Buildings.IsDoorOpen(id) ? "E  Закрыть дверь" : "E  Открыть дверь";
            offers.Add(new InteractionOffer<DoorCommand>(InteractionControl.Primary, prompt, new DoorCommand(id), Toggle));
        }

        private void OfferPickup(BuildingFocus focus)
        {
            if (offers.Count != 0 || focus.Kind != BuildingFocusKind.PlacedItem || !session.CanUseAnchor(focus.Id)) return;
            var container = session.GetInventory(focus.Id);
            if (container.Stacks.Count == 0) return;
            var item = container.Stacks[0];
            var command = new InventoryMove(focus.Id, item.InstanceId, item.Quantity, session.PlayerInventoryId);
            offers.Add(new InteractionOffer<InventoryMove>(InteractionControl.Primary,
                $"E  Взять: {item.DisplayName} ×{item.Quantity}", command, Take));
        }

        private void OfferInspection(BuildingFocus focus)
        {
            if (!session.CanUseAnchor(focus.Id)) return;
            var direct = offers.Count == 0;
            offers.Add(new InteractionOffer<InspectContainer>(direct ? InteractionControl.Primary : InteractionControl.Details,
                direct ? "E  Осмотреть содержимое" : "I  Осмотреть предметы", new InspectContainer(focus.Id), Open));
        }

        private void Toggle(DoorCommand command)
        {
            var result = session.ToggleDoor(command.Id);
            if (result == DoorActionResult.Applied) view.RefreshInteractions();
            else hud.Announce(result == DoorActionResult.Obstructed ? "Отойдите от дверного проёма." : "Дверь недоступна.");
        }

        private void Take(InventoryMove command)
        {
            var result = session.MoveItems(command, command.SourceContainerId);
            if (result.Succeeded) view.RefreshInteractions();
            hud.Announce(result.Succeeded ? "Предметы взяты." : Describe(result.Failure));
        }

        private void Open(InspectContainer command)
        {
            if (command.Id != null && !session.CanUseAnchor(command.Id)) return;
            anchorId = command.Id;
            RefreshPanel();
            modalChanged(true);
        }

        public void Close()
        {
            if (!IsOpen) return;
            Panel.Hide();
            anchorId = null;
            modalChanged(false);
        }

        private void RefreshPanel()
        {
            var player = session.GetInventory(session.PlayerInventoryId);
            var other = anchorId == null ? null : session.GetInventory(anchorId);
            var surface = anchorId != null && session.Buildings.TryGetAnchor(anchorId, out var anchor) &&
                anchor.Kind == BuildingAnchorKind.Surface;
            Panel.Show(player, other, surface ? "Поверхность" : null);
        }

        private void MoveFromPanel(InventoryMove command)
        {
            var result = session.MoveItems(command, anchorId);
            if (result.Succeeded)
            {
                view.RefreshInteractions();
                RefreshPanel();
            }
            Panel.Announce(result.Succeeded ? "Предметы перемещены." : Describe(result.Failure));
        }

        private void ReturnToPlacement(string instanceId, int quantity)
        {
            if (anchorId != null) MoveFromPanel(PlacementCommand(instanceId, quantity));
        }

        private InventoryMoveFailure CheckReturnToPlacement(string instanceId, int quantity) => anchorId == null
            ? InventoryMoveFailure.UnknownContainer : session.CheckMove(PlacementCommand(instanceId, quantity), anchorId);

        private InventoryMove PlacementCommand(string instanceId, int quantity)
        {
            var player = session.GetInventory(session.PlayerInventoryId);
            var other = session.GetInventory(anchorId);
            string target = null;
            if (other.Stacks.Count != 0)
                foreach (var stack in player.Stacks)
                    if (stack.InstanceId == instanceId && stack.DefinitionId == other.Stacks[0].DefinitionId &&
                        stack.VariantId == other.Stacks[0].VariantId)
                    {
                        target = other.Stacks[0].InstanceId;
                        break;
                    }
            return new InventoryMove(player.ContainerId, instanceId, quantity, anchorId, target);
        }

        private static string Describe(InventoryMoveFailure failure) => failure switch
        {
            InventoryMoveFailure.Capacity => "Недостаточно места или превышен допустимый вес.",
            InventoryMoveFailure.StackLimit => "Столько предметов не помещается в этот стак.",
            InventoryMoveFailure.IncompatibleStack => "У этих предметов разные свойства.",
            InventoryMoveFailure.InvalidQuantity => "Выберите доступное количество.",
            InventoryMoveFailure.Inaccessible => "Хранилище недоступно.",
            _ => "Не удалось переместить предметы."
        };
    }
}
