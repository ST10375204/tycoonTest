using System.Collections.Concurrent;

public class GameSession
{
    public ConcurrentDictionary<Guid, SseClient> Clients { get; set; } = new();
    public CancellationTokenSource SharedCts { get; set; } = new();
    public bool HasStarted { get; set; } = false;
    
    public int RoundNumber { get; set; } = 1;
    public List<List<Guid>> RoundResults { get; set; } = new(); // Finishing order per round
    public Dictionary<Guid, string[]> PlayerHands { get; set; } = new(); // To preserve hands if needed
}
