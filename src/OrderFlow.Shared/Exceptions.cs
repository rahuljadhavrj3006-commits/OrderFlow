namespace OrderFlow.Shared;

// Domain-level exceptions. GlobalExceptionMiddleware maps each one to an HTTP status,
// so business code never has to know about HTTP.

/// <summary>Maps to 400 Bad Request.</summary>
public class DomainValidationException(string message) : Exception(message);

/// <summary>Maps to 404 Not Found.</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>Maps to 409 Conflict.</summary>
public class ConflictException(string message) : Exception(message);

/// <summary>Thrown when the circuit breaker is open. Maps to 503 Service Unavailable.</summary>
public class BrokenCircuitException(string message) : Exception(message);
