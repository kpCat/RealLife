using System;

namespace RealLife.Simulation
{
    public enum InteractionControl { Primary, Details }

    public interface IInteractionOffer
    {
        InteractionControl Control { get; }
        string Prompt { get; }
        void Execute();
    }

    public sealed class InteractionOffer<TCommand> : IInteractionOffer
    {
        private readonly Action<TCommand> execute;
        private readonly TCommand command;

        public InteractionOffer(InteractionControl control, string prompt, TCommand command, Action<TCommand> execute)
        {
            Control = control;
            Prompt = prompt;
            this.command = command;
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public InteractionControl Control { get; }
        public string Prompt { get; }
        public void Execute() => execute(command);
    }
}
