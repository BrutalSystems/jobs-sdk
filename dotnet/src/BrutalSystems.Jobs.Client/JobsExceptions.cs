namespace BrutalSystems.Jobs.Client;

/// <summary>The jobs service returned 404 for a named Job.</summary>
public sealed class JobNotFoundException(string message) : Exception(message);

/// <summary>The jobs service returned 404 for a named Run lookup.</summary>
public sealed class RunNotFoundException(string message) : Exception(message);
