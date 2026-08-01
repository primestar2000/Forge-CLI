namespace Forge.Runtime;

/// <summary>
/// Builds a test payload for <typeparamref name="TMessage"/>, so <c>forge invoke:run</c> never
/// starts from a blank page.
///
/// Factories are code, fixtures are data: reach for a factory when a plausible-but-fresh instance
/// is enough, and for a saved fixture when replaying an exact edge case.
///
/// <code>
/// public class CreateGigFactory : IForgeMessageFactory&lt;CreateGigCommand&gt;
/// {
///     public CreateGigCommand Create() => new("Sunday set", 100, 500);
///
///     public CreateGigCommand Create(string state) => state switch
///     {
///         "invalid" =&gt; Create() with { BudgetMin = -1 },
///         _ =&gt; Create()
///     };
/// }
/// </code>
/// </summary>
public interface IForgeMessageFactory<out TMessage>
{
    /// <summary>A valid, representative instance.</summary>
    TMessage Create();

    /// <summary>
    /// A named variant — "invalid", "large-order", "expired". Defaults to <see cref="Create()"/>
    /// so implementing it is optional.
    /// </summary>
    TMessage Create(string state) => Create();
}

/// <summary>
/// Non-generic marker so factories can be discovered in DI without knowing the message type at
/// resolve time. Implemented automatically — you implement the generic interface.
/// </summary>
public interface IForgeMessageFactory
{
    Type MessageType { get; }

    object Create(string? state);
}
