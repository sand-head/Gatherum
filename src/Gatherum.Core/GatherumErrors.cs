namespace Gatherum.Core;

/// <summary>Raised when a node, revision, or key doesn't exist — or is hidden from the
/// caller, which must look identical to not existing.</summary>
public class NotFoundException(string message) : Exception(message);

public class ForbiddenException(string message) : Exception(message);

/// <summary>Raised when the caller's input can't name what it is trying to name — a
/// category path with no names in it, or one nested past the limit.</summary>
public class ValidationException(string message) : Exception(message);
