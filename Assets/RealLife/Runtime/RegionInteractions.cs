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
        private readonly ResidentView residents;
        private readonly GameHud hud;
        private readonly Action<bool> modalChanged;
        private readonly Action synchronize;
        private readonly List<IInteractionOffer> offers = new List<IInteractionOffer>(3);
        private readonly VisualElement conversation;
        private readonly Label speaker;
        private readonly Label answer;
        private readonly Dictionary<ConversationTopic, Button> questions = new Dictionary<ConversationTopic, Button>();
        private string anchorId, residentId;

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

        private readonly struct GreetResident
        {
            public readonly string Id, Name;
            public GreetResident(string id, string name) => (Id, Name) = (id, name);
        }

        public RegionInteractions(RegionSession session, RegionView view, ResidentView residents, GameHud hud,
            VisualElement root, Action<bool> modalChanged, Action synchronize)
        {
            this.session = session;
            this.view = view;
            this.residents = residents;
            this.hud = hud;
            this.modalChanged = modalChanged;
            this.synchronize = synchronize;
            Panel = new InventoryPanel(MoveFromPanel, ReturnToPlacement, Close,
                command => session.CheckMove(command, anchorId), CheckReturnToPlacement, Describe, UseItem);
            root.Add(Panel.Element);

            conversation = new VisualElement();
            conversation.AddToClassList("conversation-overlay");
            var card = new VisualElement();
            card.AddToClassList("conversation-card");
            speaker = new Label();
            speaker.AddToClassList("conversation-speaker");
            answer = new Label();
            answer.AddToClassList("conversation-answer");
            var options = new VisualElement();
            options.AddToClassList("conversation-options");
            AddQuestion(options, ConversationTopic.Name, "Как вас зовут?");
            AddQuestion(options, ConversationTopic.Activity, "Чем занимаетесь?");
            AddQuestion(options, ConversationTopic.Wellbeing, "Как дела?");
            var close = new Button(Close) { text = "Закрыть" };
            close.AddToClassList("inventory-button");
            options.Add(close);
            card.Add(speaker);
            card.Add(answer);
            card.Add(options);
            conversation.Add(card);
            conversation.style.display = DisplayStyle.None;
            root.Add(conversation);
        }

        public InventoryPanel Panel { get; }
        public bool IsOpen => Panel.IsOpen || ConversationOpen;
        public bool ConversationOpen => residentId != null;
        public ConversationResult LastConversation { get; private set; }

        public bool TryFocusResident(Ray ray, out ResidentViewData resident) => residents.TryFocus(ray, Distance, out resident);

        public bool TryFocus(Ray ray, out BuildingFocus focus)
        {
            focus = default;
            return !TryFocusResident(ray, out _) && view.TryFocus(ray, Distance, out focus);
        }

        public string Prompt(Ray ray)
        {
            if (TryFocusResident(ray, out var resident)) return ConversationPrompt(resident);
            if (!view.TryFocus(ray, Distance, out var target)) return null;
            Resolve(target);
            if (offers.Count == 0)
                return target.Kind == BuildingFocusKind.Unsupported ? "В это здание пока нельзя войти" : null;
            return offers.Count == 1 ? offers[0].Prompt : offers[0].Prompt + "     " + offers[1].Prompt;
        }

        public bool Activate(InteractionControl control, Ray ray)
        {
            if (IsOpen) return false;
            if (TryFocusResident(ray, out var resident))
            {
                if (control == InteractionControl.Primary)
                {
                    var offer = new InteractionOffer<GreetResident>(InteractionControl.Primary,
                        ConversationPrompt(resident), new GreetResident(resident.Id, resident.Name), Greet);
                    offer.Execute();
                    return true;
                }
            }
            else if (view.TryFocus(ray, Distance, out var target))
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

        public bool Ask(ConversationTopic topic)
        {
            if (!ConversationOpen || !questions.TryGetValue(topic, out var button) || button.panel == null)
                return false;
            using var submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
            return submit.isPropagationStopped;
        }

        private static string ConversationPrompt(ResidentViewData resident) =>
            $"E  Поговорить · {resident.Name} · {resident.Activity}";

        private void AddQuestion(VisualElement options, ConversationTopic topic, string text)
        {
            var button = new Button(() => Reply(topic)) { text = text };
            button.AddToClassList("inventory-button");
            questions.Add(topic, button);
            options.Add(button);
        }

        private void Greet(GreetResident command)
        {
            LastConversation = session.Talk(command.Id, ConversationTopic.Greeting);
            if (!LastConversation.Applied)
            {
                hud.Announce(LastConversation.Text ?? "Сейчас поговорить не получится.");
                return;
            }
            residentId = command.Id;
            speaker.text = command.Name;
            answer.text = LastConversation.Text;
            conversation.style.display = DisplayStyle.Flex;
            conversation.BringToFront();
            modalChanged(true);
        }

        private void Reply(ConversationTopic topic)
        {
            if (!ConversationOpen) return;
            LastConversation = session.Talk(residentId, topic);
            answer.text = LastConversation.Text ?? "Сейчас поговорить не получится.";
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
            if (result == DoorActionResult.Applied) synchronize();
            else hud.Announce(result == DoorActionResult.Obstructed ? "Отойдите от дверного проёма." : "Дверь недоступна.");
        }

        private void Take(InventoryMove command)
        {
            var result = session.MoveItems(command, command.SourceContainerId);
            if (result.Succeeded) synchronize();
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
            residentId = null;
            conversation.style.display = DisplayStyle.None;
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
                synchronize();
                RefreshPanel();
            }
            Panel.Announce(result.Succeeded ? "Предметы перемещены." : Describe(result.Failure));
        }

        private void UseItem(InventoryStackView item)
        {
            var player = session.PlayerInventoryId;
            var result = session.UseItem(new InventoryUse(player, player, item.InstanceId, session.Tick));
            if (result.Succeeded)
            {
                synchronize();
                RefreshPanel();
            }
            var success = result.Kind == InventoryUseKind.Drink ? "Вы выпили напиток." : "Вы съели порцию.";
            Panel.Announce(result.Succeeded ? success : result.Failure == InventoryUseFailure.DuplicateAction
                ? "Продолжите игру, чтобы использовать ещё одну порцию."
                : "Сейчас этот предмет использовать нельзя.");
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
