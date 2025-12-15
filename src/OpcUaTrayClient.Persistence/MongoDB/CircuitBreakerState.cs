using Microsoft.Extensions.Logging;

namespace OpcUaTrayClient.Persistence.MongoDB;

/// <summary>
/// Circuit breaker pattern implementation for MongoDB operations.
///
/// States:
/// - Closed: Normal operation, requests pass through
/// - Open: Failure threshold exceeded, requests rejected immediately (fast-fail)
/// - HalfOpen: Testing if service recovered, allows one request through
///
/// This prevents cascading failures and reduces load on an unhealthy MongoDB.
/// </summary>
public sealed class CircuitBreakerState
{
    private readonly ILogger<CircuitBreakerState> _logger;
    private readonly object _lock = new();

    private State _state = State.Closed;
    private DateTime _openedAt;
    private int _failureCount;

    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;

    public enum State
    {
        /// <summary>
        /// Normal operation. Requests pass through.
        /// </summary>
        Closed,

        /// <summary>
        /// Failure threshold exceeded. Requests rejected immediately.
        /// </summary>
        Open,

        /// <summary>
        /// Testing recovery. One request allowed through.
        /// </summary>
        HalfOpen
    }

    /// <summary>
    /// Current circuit breaker state.
    /// </summary>
    public State CurrentState
    {
        get
        {
            lock (_lock)
            {
                UpdateState();
                return _state;
            }
        }
    }

    /// <summary>
    /// Number of consecutive failures.
    /// </summary>
    public int FailureCount => _failureCount;

    /// <summary>
    /// Event raised when the circuit breaker state changes.
    /// </summary>
    public event EventHandler<State>? StateChanged;

    public CircuitBreakerState(
        ILogger<CircuitBreakerState> logger,
        int failureThreshold = 5,
        int openDurationSeconds = 30)
    {
        _logger = logger;
        _failureThreshold = failureThreshold;
        _openDuration = TimeSpan.FromSeconds(openDurationSeconds);
    }

    /// <summary>
    /// Check if a request should be allowed through.
    /// </summary>
    /// <returns>True if request allowed, false if circuit is open.</returns>
    public bool AllowRequest()
    {
        lock (_lock)
        {
            UpdateState();

            switch (_state)
            {
                case State.Closed:
                    return true;

                case State.Open:
                    return false;

                case State.HalfOpen:
                    // Allow one test request in half-open state
                    // The result of this request will determine if we close or re-open
                    return true;

                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// Record a successful operation.
    /// Resets failure count and closes the circuit.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            var previousState = _state;
            _failureCount = 0;
            _state = State.Closed;

            if (previousState != State.Closed)
            {
                _logger.LogInformation("Circuit breaker closed. MongoDB connection restored.");
                StateChanged?.Invoke(this, State.Closed);
            }
        }
    }

    /// <summary>
    /// Record a failed operation.
    /// Increments failure count and may open the circuit.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;

            if (_state == State.HalfOpen)
            {
                // Test request failed, re-open the circuit
                _state = State.Open;
                _openedAt = DateTime.UtcNow;
                _logger.LogWarning("Circuit breaker re-opened. HalfOpen test failed.");
                StateChanged?.Invoke(this, State.Open);
            }
            else if (_failureCount >= _failureThreshold && _state == State.Closed)
            {
                // Threshold exceeded, open the circuit
                _state = State.Open;
                _openedAt = DateTime.UtcNow;
                _logger.LogWarning("Circuit breaker opened. Failure threshold ({Threshold}) exceeded.",
                    _failureThreshold);
                StateChanged?.Invoke(this, State.Open);
            }
        }
    }

    /// <summary>
    /// Force the circuit breaker to a specific state.
    /// Use for testing or manual intervention.
    /// </summary>
    public void ForceState(State newState)
    {
        lock (_lock)
        {
            var previousState = _state;
            _state = newState;

            if (newState == State.Closed)
            {
                _failureCount = 0;
            }
            else if (newState == State.Open)
            {
                _openedAt = DateTime.UtcNow;
            }

            if (previousState != newState)
            {
                _logger.LogInformation("Circuit breaker forced to {State}", newState);
                StateChanged?.Invoke(this, newState);
            }
        }
    }

    /// <summary>
    /// Reset the circuit breaker to closed state with zero failures.
    /// </summary>
    public void Reset()
    {
        ForceState(State.Closed);
    }

    private void UpdateState()
    {
        // Check if open circuit should transition to half-open
        if (_state == State.Open && DateTime.UtcNow - _openedAt > _openDuration)
        {
            _state = State.HalfOpen;
            _logger.LogInformation("Circuit breaker transitioning to HalfOpen. Allowing test request.");
            StateChanged?.Invoke(this, State.HalfOpen);
        }
    }
}
