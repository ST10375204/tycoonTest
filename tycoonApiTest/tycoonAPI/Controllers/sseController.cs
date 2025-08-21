using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text.Json;

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
                var rej = JsonSerializer.Serialize(new { type = "connection_rejected", reason = "Game room is full. Connection rejected." });
                await Response.WriteAsync($"data: {rej}\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            // Add new client
            var clientId = Guid.NewGuid();
            var client = new SseClient(Response, clientToken);
            session.Clients[clientId] = client;

            // Notify client of their ID (typed JSON)
            var idPayload = JsonSerializer.Serialize(new { type = "yourId", id = clientId });
            await Response.WriteAsync($"data: {idPayload}\n\n");
            await Response.Body.FlushAsync();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(clientToken, session.SharedCts.Token);
            var linkedToken = linkedCts.Token;

            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    // send a minimal comment ping to keep connection alive (SSE comment)
                    await Task.Delay(5000, linkedToken);
                    await Response.WriteAsync(":\n\n");
                    await Response.Body.FlushAsync();

                    // Start the game when ready (once 4 clients)
                    if (!session.HasStarted && session.Clients.Count == 4)
                    {
                        session.HasStarted = true;
                        // Start the round in background, don't block the SSE loop
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var roundController = new RoundController();
                                await roundController.StartNewRound(id);
                            }
                            catch
                            {
                                // ignore — starting the round should not crash the SSE loop
                            }
                        });
                    }
                }
            }
            catch (TaskCanceledException) { }
            finally
            {
                // remove client
                session.Clients.TryRemove(clientId, out _);

                // if the session had started, cancel everything and inform remaining clients
                if (session.HasStarted)
                {
                    session.SharedCts.Cancel();

                    var payload = JsonSerializer.Serialize(new
                    {
                        type = "game_ended",
                        reason = "Game ended due to player disconnect."
                    });

                    foreach (var c in session.Clients.Values)
                    {
                        try
                        {
                            await c.Response.WriteAsync($"data: {payload}\n\n");
                            await c.Response.Body.FlushAsync();
                            // we intentionally don't call Response.Body.Close here to avoid exceptions;
                            // client connections will observe cancellation.
                        }
                        catch { /* ignore broken clients */ }
                    }

                    _gameSessions.TryRemove(id, out _);
                }
                else if (session.Clients.IsEmpty)
                {
                    // no players left, remove session
                    _gameSessions.TryRemove(id, out _);
                }
            }
        }

        [HttpPost("sendMessage")]
        public async Task<IActionResult> PostTest([FromQuery] string data, [FromQuery] int id)
        {
            if (_gameSessions.TryGetValue(id, out var session))
            {
                var msgPayload = JsonSerializer.Serialize(new
                {
                    type = "message",
                    from = "server",
                    text = data
                });

                foreach (var client in session.Clients.Values)
                {
                    try
                    {
                        await client.Response.WriteAsync($"data: {msgPayload}\n\n");
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
