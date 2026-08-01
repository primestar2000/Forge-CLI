namespace Forge.Runtime;

/// <summary>
/// An expected, actionable condition — an unknown seeder name, an unregistered message type.
///
/// Distinguished from a genuine bug so forge can render the message alone. Everything else
/// crosses the process boundary with its full stack trace, because for an actual bug the trace
/// is the useful part.
/// </summary>
public sealed class ForgeRuntimeException(string message) : Exception(message);
