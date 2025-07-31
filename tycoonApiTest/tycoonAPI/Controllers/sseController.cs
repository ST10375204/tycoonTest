using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace tycoonAPI.Controllers
{
    [Route("sse")]
    [ApiController]
    public class SseController : ControllerBase
    {
        private static readonly ConcurrentDictionary<int, GameSession> _gameSessions = new();

        [HttpGet("gameRoom")]
        public async Task GetEvents([FromQuery] int id, CancellationToken clientToken)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");

            var session = _gameSessions.GetOrAdd(id, _ => new GameSession());

            // Reject connection if 4 clients already connected
            if (session.Clients.Count >= 4)
            {
                Response.StatusCode = 429; // Too Many Requests
                await Response.WriteAsync("data: Game room is full. Connection rejected.\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            var clientId = Guid.NewGuid();
            var client = new SseClient(Response, clientToken);
            session.Clients[clientId] = client;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(clientToken, session.SharedCts.Token);
            var linkedToken = linkedCts.Token;

            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    await Task.Delay(5000, linkedToken); // Heartbeat
                    await Response.WriteAsync(":\n\n");
                    await Response.Body.FlushAsync();

                    if (!session.HasStarted && session.Clients.Count == 4) // Start game when 4 clients are connected
                    {
                        session.HasStarted = true;

                        var roundCtrl = new RoundController();
                        roundCtrl.StartNewRound(session);
                    }
                }
            }
            catch (TaskCanceledException) { }
            finally
            {
                session.Clients.TryRemove(clientId, out _);

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
                    _gameSessions.TryRemove(id, out _);
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
                        await client.Response.WriteAsync(
                             $"data: {data}\n" +
                             $"data: gameId: {id}\n\n"
                             );
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
