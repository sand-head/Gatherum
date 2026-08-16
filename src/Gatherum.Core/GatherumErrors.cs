namespace Gatherum.Core;

/// <summary>Raised when a node, revision, or key doesn't exist — or is hidden from the
/// caller, which must look identical to not existing.</summary>
public class NotFoundException(string message) : Exception(message);

public class ForbiddenException(string message) : Exception(message);
