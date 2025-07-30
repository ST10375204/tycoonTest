using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace tycoonAPI.Controllers
{ 
   public class RoundController
{
    private DeckController _deck = new DeckController();

    public void StartNewRound(GameSession session)
    {
        var hands = _deck.DealAllHands();
        var players = session.Clients.Keys.ToList();

        session.PlayerHands = players.ToDictionary(id => id, id => hands[players.IndexOf(id)]);

        var turnOrder = GetTurnOrder(session, players, hands);
        BroadcastRoundStart(session, turnOrder);
    }

    private List<Guid> GetTurnOrder(GameSession session, List<Guid> players, List<string[]> hands)
    {
        if (session.RoundNumber == 1)
        {
            // For the first round, order by who has 3D
            var firstPlayer = players.FirstOrDefault(id => session.PlayerHands[id].Contains("3D"));
            var others = players.Where(p => p != firstPlayer).OrderBy(_ => Guid.NewGuid()).ToList();

            return new List<Guid> { firstPlayer }.Concat(others).ToList();
        }
        else
        {
            var lastRound = session.RoundResults.Last();
            var prevFirst = session.RoundResults.First()[0];
            var newOrder = lastRound;

            if (lastRound[0] != prevFirst)
                newOrder = lastRound.Where(id => id != prevFirst).Append(prevFirst).ToList();

            return newOrder;
        }
    }

    private async void BroadcastRoundStart(GameSession session, List<Guid> turnOrder)
    {
        int pos = 0;
        foreach (var id in turnOrder)
        {
            var client = session.Clients[id];
            var hand = session.PlayerHands[id];

            await client.Response.WriteAsync($"data: Round {session.RoundNumber} starting\n\n" +
                                             $"hand: {string.Join(", ", hand)}\n\n" +
                                             $"position: {pos}\n\n");
            await client.Response.Body.FlushAsync();
            pos++;
        }
    }
}




}