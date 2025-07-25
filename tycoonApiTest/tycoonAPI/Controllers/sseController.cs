using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace tycoonAPI.Controllers
{
    [Route("sse")]
    [ApiController]
    public class SseController : ControllerBase
    {
        private static readonly ConcurrentDictionary<int, GameSession> _gameSessions = new();

        [HttpGet("game")]
        public async Task GetEvents([FromQuery] int id, CancellationToken clientToken)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");

            var session = _gameSessions.GetOrAdd(id, _ => new GameSession());

            var clientId = Guid.NewGuid();
            var client = new SseClient(Response, clientToken);
            session.Clients[clientId] = client;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(clientToken, session.SharedCts.Token);
            var linkedToken = linkedCts.Token;

            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, linkedToken); // Heartbeat
                    await Response.WriteAsync(":\n\n");
                    await Response.Body.FlushAsync();
                    if (!session.HasStarted && session.Clients.Count == 4)
                    {
                        session.HasStarted = true;

                        var deckController = new DeckController();
                        var hands = deckController.DealAllHands(); // Deal once!

                        var clientIds = session.Clients.Keys.ToList(); // Maintain join order
                        var players = clientIds.Select((id, idx) => new { Id = id, Hand = hands[idx] }).ToList();
                        
                            // Find the player with 3D
                        var firstPlayer = players.FirstOrDefault(p => p.Hand.Contains("3D"));

                        var otherPlayers = players
                           .Where(p => p.Id != firstPlayer.Id)
                             .OrderBy(_ => Guid.NewGuid())
                              .ToList();

                        var turnOrder = new List<Guid> { firstPlayer.Id };
                        turnOrder.AddRange(otherPlayers.Select(p => p.Id));

                        int i = 0;
                        foreach (var c in session.Clients.Values)
                        {
                            var playerHand = hands[i];
                            await c.Response.WriteAsync("data: Game starting now!\n\n" + string.Join(", ", playerHand) + "\n\n"+ "position: "+
                            "\n\n"+ string.Join(", ",  turnOrder.FindIndex(id => session.Clients[id] == c)) + "\n\n");
                            await c.Response.Body.FlushAsync();
                            i++;
                        }
                    }

                }
            }
            catch (TaskCanceledException) { }
            finally
            {
                session.Clients.TryRemove(clientId, out _);

                // If game started and a client disconnected, cancel all
                if (session.HasStarted)
                {
                    session.SharedCts.Cancel(); // Ends all loops

                    foreach (var c in session.Clients.Values)
                    {
                        try
                        {
                            await c.Response.WriteAsync("data: Game ended due to player disconnect.\n\n");
                            await c.Response.Body.FlushAsync();
                            c.Response.Body.Close();
                        }
                        catch { }
                    }

                    _gameSessions.TryRemove(id, out _);
                }
                else if (session.Clients.IsEmpty)
                {
                    _gameSessions.TryRemove(id, out _); // Clean up idle sessions
                }
            }
        }

        [HttpPost("test")]
        public async Task<IActionResult> PostTest([FromQuery] string data, [FromQuery] int id)
        {
            if (_gameSessions.TryGetValue(id, out var session))
            {
                foreach (var client in session.Clients.Values)
                {
                    try
                    {
                        await client.Response.WriteAsync($"data: {data}, gameId: {id}\n\n");
                        await client.Response.Body.FlushAsync();
                    }
                    catch
                    {
                        // Ignore broken clients
                    }
                }
            }

            return Ok(new { status = "Message broadcasted", id, data });
        }
    }
}
