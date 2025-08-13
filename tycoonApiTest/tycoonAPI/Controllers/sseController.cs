using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace tycoonAPI.Controllers
{
    [Route("sse")]
    [ApiController]
    public class SseController : ControllerBase
    {
        public static readonly ConcurrentDictionary<int, GameSession> _gameSessions = new();

        [HttpGet("gameRoom")]
        public async Task GetEvents([FromQuery] int id, CancellationToken clientToken)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");

            var session = _gameSessions.GetOrAdd(id, _ => new GameSession());

            // Reject connection if full
            if (session.Clients.Count >= 4)
            {
                Response.StatusCode = 429;
                await Response.WriteAsync("data: Game room is full. Connection rejected.\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            // Add new client
            var clientId = Guid.NewGuid();
            var client = new SseClient(Response, clientToken);
            session.Clients[clientId] = client;

            // Notify client of their ID
            await Response.WriteAsync($"data: yourId: {clientId}\n\n");
            await Response.Body.FlushAsync();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(clientToken, session.SharedCts.Token);
            var linkedToken = linkedCts.Token;

            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    await Task.Delay(5000, linkedToken);
                    await Response.WriteAsync(":\n\n");
                    await Response.Body.FlushAsync();

                    // Start the game when ready
                    if (!session.HasStarted && session.Clients.Count == 4)
                    {
                        session.HasStarted = true;

                        var roundController = new RoundController();
                        // Start round safely
                        _ = roundController.StartNewRound(id);
                    }
                }
            }
            catch (TaskCanceledException) { }
            finally
            {
                session.Clients.TryRemove(clientId, out _);

                if (session.HasStarted)
                {
                    session.SharedCts.Cancel();
                    foreach (var c in session.Clients.Values)
                    {
                        try
                        {
                            await c.Response.WriteAsync("data: message: Game ended due to player disconnect.\n\n");
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

        [HttpPost("sendMessage")]
        public async Task<IActionResult> PostTest([FromQuery] string data, [FromQuery] int id)
        {
            if (_gameSessions.TryGetValue(id, out var session))
            {
                foreach (var client in session.Clients.Values)
                {
                    try
                    {
                        await client.Response.WriteAsync($"data: message:{data}\n\n");

                        await client.Response.Body.FlushAsync();
                    }
                    catch
                    {
                        // ignore exceptions for now
                    }
                }
            }

            return Ok(new { message = data });
        }

    }
}
