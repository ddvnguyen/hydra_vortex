namespace Hydra.StateMachine;

/// <summary>
/// Thrown when <see cref="StateMachine{TState,TEvent}.FireAsync"/> is asked to
/// process an event that has no fireable transition from the current state, or
/// when the destination state's entry guard rejects the transition. The state
/// machine is left unchanged in both cases.
/// </summary>
public sealed class StateMachineException : Exception
{
    public StateMachineException(string message) : base(message) { }

    public StateMachineException(string message, Exception inner) : base(message, inner) { }
}
