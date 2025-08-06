using System.Collections.Concurrent;

public class GameSession
{
    //sse stuff
    public ConcurrentDictionary<Guid, SseClient> Clients { get; set; } = new();
    public CancellationTokenSource SharedCts { get; set; } = new();
    public bool HasStarted { get; set; } = false;

    //round stuff
    public int RoundNumber { get; set; } = 1;
    public List<List<Guid>> RoundResults { get; set; } = new(); // Finishing order per round
    public Guid CurrentTurnPlayerId { get; set; }
    public List<string[]> Pot = new();
    public List<Guid> TurnOrder;
      // Holds each player’s current hand
    public Dictionary<Guid, string[]> PlayerHands { get; set; } = new();

    // Holds exchange submissions until both partners have sent
    public Dictionary<Guid, string[]> ExchangeSubmissions { get; set; } = new();

}
