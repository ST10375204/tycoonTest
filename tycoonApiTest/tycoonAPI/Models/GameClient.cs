using System.Collections.Concurrent;

public class GameSession
{
    public ConcurrentDictionary<Guid, SseClient> Clients { get; } = new();
    public CancellationTokenSource SharedCts { get; } = new();
    public bool HasStarted { get; set; } = false;
}