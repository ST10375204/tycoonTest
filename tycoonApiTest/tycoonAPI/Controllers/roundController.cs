using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace tycoonAPI.Controllers
{
    [Route("roundControl")]
    [ApiController]
    public class RoundController : ControllerBase
    {
        private readonly DeckController _deck = new();

        [HttpPost("start")]
        public async Task<IActionResult> StartNewRound([FromQuery] int sessionId)
        {
            if (!SseController._gameSessions.TryGetValue(sessionId, out var session))
                return NotFound();

            // Reset all state
            session.RoundNumber = 1;
            session.Pot.Clear();
            session.RoundResults.Clear();

            // Deal and broadcast start (this sets TurnOrder)
            await DealAndBroadcastRoundStart(session);

            // Seed RoundResults[0] with the turn order (round 1 finish base)
            session.RoundResults = new List<List<Guid>> { new List<Guid>(session.TurnOrder) };

            return Ok();
        }

        [HttpPost("play")]
        public async Task<IActionResult> MakePlay([FromBody] PlayRequest request)
        {
            if (!SseController._gameSessions.TryGetValue(request.SessionId, out var session))
                return NotFound();

            await HandlePlayAsync(session, request);
            return Ok();
        }

        private async Task HandlePlayAsync(GameSession session, PlayRequest request)
        {
            if (!Guid.TryParse(request.PlayerId, out var pid))
                throw new FormatException($"Invalid PlayerId: {request.PlayerId}");

            int idx = session.TurnOrder.IndexOf(pid);
            if (idx < 0) return;

            // Defensive: check player's hand exists
            if (!session.PlayerHands.ContainsKey(pid))
                return;

            var currentHand = session.PlayerHands[pid].ToList();

            // 1. Validate played cards (passes are allowed as empty arrays)
            foreach (var card in request.HandPlayed ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(card) && !currentHand.Contains(card))
                    throw new InvalidOperationException($"Card '{card}' not found in player's hand.");
            }

            // 2. Remove played cards from player's hand (no-op for passes)
            foreach (var card in request.HandPlayed ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(card))
                    currentHand.Remove(card);
            }

            // Update stored hand
            session.PlayerHands[pid] = currentHand.ToArray();

            // 3. Add to pot and clear passes
            session.Pot.Add(request.HandPlayed ?? Array.Empty<string>());
            ClearPotAfterPasses(session);

            // Remember whether this play emptied the player's hand
            bool playerFinished = (request.HandSize == 0);

            // If player finished, handle finish now (this will remove from TurnOrder and set CurrentTurnPlayerId appropriately)
            if (playerFinished)
            {
                HandlePlayerFinished(session, pid, idx);
            }

            // Find previous non-pass play BEFORE the current play (passes are null/empty arrays).
            // Start at pot.Count - 2 (the entry just before the current one) and scan backwards skipping empty plays.
            string[] previousNonPassPlay = Array.Empty<string>();
            for (int search = session.Pot.Count - 2; search >= 0; search--)
            {
                var candidate = session.Pot[search];
                if (candidate != null && candidate.Length > 0)
                {
                    previousNonPassPlay = candidate;
                    break;
                }
            }

            // Only evaluate special "keep turn" rules if the player did NOT finish (a finished player can't keep the turn)
            if (!playerFinished)
            {
                // Helper booleans for special cases
                bool containsEight = (request.HandPlayed ?? Array.Empty<string>()).Any(c => !string.IsNullOrEmpty(c) && IsRankEight(c));

                // Current play is a single 3♠?
                bool currentIsSingleThreeSpades = (request.HandPlayed?.Length ?? 0) == 1
                    && !string.IsNullOrEmpty(request.HandPlayed[0])
                    && IsThreeOfSpades(request.HandPlayed[0]);

                // Previous non-pass play was a single Joker?
                bool previousWasSingleJoker = (previousNonPassPlay?.Length ?? 0) == 1
                    && !string.IsNullOrEmpty(previousNonPassPlay[0])
                    && IsJoker(previousNonPassPlay[0]);

                // Special-case: 8 clears pot OR single 3♠ trumps single Joker -> clear pot and keep turn
                if (containsEight || (currentIsSingleThreeSpades && previousWasSingleJoker))
                {
                    session.Pot.Clear();
                    session.CurrentTurnPlayerId = pid;
                }
                else
                {
                    // Normal rotation — guard against empty TurnOrder
                    if (session.TurnOrder.Count > 0)
                    {
                        // Recompute index in case TurnOrder changed elsewhere; idx should still be valid here because player didn't finish.
                        int curIdx = session.TurnOrder.IndexOf(pid);
                        if (curIdx >= 0)
                            session.CurrentTurnPlayerId = session.TurnOrder[(curIdx + 1) % session.TurnOrder.Count];
                        else
                            session.CurrentTurnPlayerId = Guid.Empty;
                    }
                    else
                    {
                        session.CurrentTurnPlayerId = Guid.Empty;
                    }
                }
            }

            // Build remainingCards map
            var remainingCards = session.PlayerHands
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Length);

            // Broadcast play update (server authoritative for revolution). Include lastNonPassPlay to help clients
            var playPayload = new
            {
                type = "play_update",
                pot = session.Pot,
                playType = request.PlayType,
                revolution = (request.HandPlayed?.Length ?? 0) == 4,
                nextPlayer = session.CurrentTurnPlayerId,
                roundResults = session.RoundResults.Count > 0
                    ? session.RoundResults[session.RoundNumber - 1]
                    : new List<Guid>(),
                remainingCards,
                // helpful do not remove deevee I know you want to
                lastNonPassPlay = previousNonPassPlay,   // may be empty
                lastPlayedBy = pid                        // the player who issued this play
            };
            await BroadcastToAllAsync(session, playPayload);

            // If one or zero left, finalize round results and end round
            if (session.TurnOrder.Count <= 1)
            {
                var roundIdx = session.RoundNumber - 1;

                // Ensure the results list exists
                while (session.RoundResults.Count <= roundIdx)
                    session.RoundResults.Add(new List<Guid>());

                var resultsThisRound = session.RoundResults[roundIdx];

                if (session.TurnOrder.Count == 1)
                {
                    var lastOne = session.TurnOrder[0];
                    // Add the last remaining player into the results if not already present
                    if (!resultsThisRound.Contains(lastOne))
                        resultsThisRound.Add(lastOne);

                    // Clear TurnOrder since round is effectively over
                    session.TurnOrder.Clear();
                }

                // If TurnOrder.Count == 0 we assume the finish handler already added the last player.
                await EndRoundAsync(session);
            }
        }

        private void HandlePlayerFinished(GameSession session, Guid finisher, int removedIndex)
        {
            var roundIdx = session.RoundNumber - 1;

            // Ensure the results list for this round exists
            while (session.RoundResults.Count <= roundIdx)
                session.RoundResults.Add(new List<Guid>());

            var resultsThisRound = session.RoundResults[roundIdx];

            // Determine round1 winner (assumed present)
            var round1Winner = session.RoundResults[0][0];

            // Is this the first finisher in this round?
            bool isFirstOut = resultsThisRound.Count == 0;

            // Remove finisher from turn order
            if (removedIndex >= 0 && removedIndex < session.TurnOrder.Count)
                session.TurnOrder.RemoveAt(removedIndex);
            else
                session.TurnOrder.Remove(finisher);

            // ROUND 2 SPECIAL: first-out and not round1 winner
            if (session.RoundNumber == 2 && isFirstOut && finisher != round1Winner)
            {
                // Create reserved result slots: [finisher, EMPTY, EMPTY, round1Winner]
                resultsThisRound.Clear();
                resultsThisRound.Add(finisher);          // index 0
                resultsThisRound.Add(Guid.Empty);       // index 1 (to be filled by next finisher)
                resultsThisRound.Add(Guid.Empty);       // index 2 (to be filled by following finisher)
                resultsThisRound.Add(round1Winner);     // index 3

                // Remove round1Winner from turn order so they don't take turns this round
                session.TurnOrder.Remove(round1Winner);

                // Now only two players remain to fight for 2nd/3rd
                session.CurrentTurnPlayerId = session.TurnOrder.Count > 0 ? session.TurnOrder[0] : Guid.Empty;
                return;
            }

            // Normal finish handling: fill first empty slot if present, otherwise append
            int emptyIdx = resultsThisRound.IndexOf(Guid.Empty);
            if (emptyIdx >= 0)
            {
                // If finisher is already present somewhere (defensive), remove it first
                if (resultsThisRound.Contains(finisher))
                    resultsThisRound.Remove(finisher);

                resultsThisRound[emptyIdx] = finisher;
            }
            else
            {
                // Append if not already present
                if (!resultsThisRound.Contains(finisher))
                    resultsThisRound.Add(finisher);
            }

            // If only one player left in turn order, they're last — fill any empty slot or append
            if (session.TurnOrder.Count == 1)
            {
                var lastOne = session.TurnOrder[0];

                int empty = resultsThisRound.IndexOf(Guid.Empty);
                if (empty >= 0)
                    resultsThisRound[empty] = lastOne;
                else if (!resultsThisRound.Contains(lastOne))
                    resultsThisRound.Add(lastOne);

                session.TurnOrder.Clear();
                session.CurrentTurnPlayerId = Guid.Empty;
                return;
            }

            // Otherwise, set next player based on reduced TurnOrder.
            if (session.TurnOrder.Count > 0)
                session.CurrentTurnPlayerId = session.TurnOrder[removedIndex % session.TurnOrder.Count];
            else
                session.CurrentTurnPlayerId = Guid.Empty;
        }

        private async Task EndRoundAsync(GameSession session)
        {
            // Broadcast round_end (typed)
            var payload = new
            {
                type = "round_end",
                results = session.RoundResults[session.RoundNumber - 1]
            };
            await BroadcastToAllAsync(session, payload);

            // Clear pot and prepare next round
            session.Pot.Clear();
            session.RoundNumber++;
            session.RoundResults.Add(new List<Guid>());

            await DealAndBroadcastRoundStart(session);
        }

        private async Task DealAndBroadcastRoundStart(GameSession session)
        {
            // Deal
            var hands = _deck.DealAllHands();

            var players = session.Clients.Keys.ToList();
            session.PlayerHands = players
                .Zip(hands, (id, hand) => (id, hand))
                .ToDictionary(x => x.id, x => x.hand);

            var remainingCards = session.PlayerHands
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Length);

            // Determine order based on round rules
            session.TurnOrder = DetermineInitialOrder(session, players, hands);

            // Ensure we have a results list for this round
            var roundIdx = session.RoundNumber - 1;
            while (session.RoundResults.Count <= roundIdx)
                session.RoundResults.Add(new List<Guid>());

            // Broadcast round_start to all players with their hand and the turn order (private per-player)
            session.CurrentTurnPlayerId = session.TurnOrder.Count > 0 ? session.TurnOrder[0] : Guid.Empty;
            for (int pos = 0; pos < session.TurnOrder.Count; pos++)
            {
                var id = session.TurnOrder[pos];
                var client = session.Clients[id];
                var payload = new
                {
                    type = "round_start",
                    round = session.RoundNumber,
                    pot = session.Pot,
                    hand = session.PlayerHands[id],
                    turnOrder = session.TurnOrder,
                    nextPlayer = session.CurrentTurnPlayerId,
                    position = pos,
                    remainingCards
                };
                await SendPrivateSseAsync(session, id, payload);
            }
        }

        private List<Guid> DetermineInitialOrder(
            GameSession session,
            List<Guid> players,
            List<string[]> hands)
        {
            if (session.RoundNumber == 1)
            {
                // Round 1: find player with 3♦ and rotate the original players list
                var starter = players
                    .Select((id, idx) => new { id, hand = hands[idx] })
                    .First(x => x.hand.Contains("3D")).id;

                int startIdx = players.IndexOf(starter);
                return players.Skip(startIdx).Concat(players.Take(startIdx)).ToList();
            }

            // baseOrder = exact round1 finish order (1st..4th)
            var baseOrder = new List<Guid>(session.RoundResults[0]);

            // prevRound finishes (exact)
            var prevFinishes = session.RoundResults[session.RoundNumber - 2];
            var lastFinisher = prevFinishes.Last();

            // rotate baseOrder so last finisher of previous round starts
            int pivot = baseOrder.IndexOf(lastFinisher);
            if (pivot < 0) pivot = 0;
            var rotated = baseOrder.Skip(pivot).Concat(baseOrder.Take(pivot)).ToList();

            // Round 2 & 3: same cyclic behavior per your rules (Round 2 special is handled when someone finishes)
            return rotated;
        }

        private static void ClearPotAfterPasses(GameSession session)
        {
            var requiredPasses = Math.Max(1, session.TurnOrder.Count - 1);
            var pot = session.Pot;

            for (int i = 0; i + requiredPasses - 1 < pot.Count; i++)
            {
                if (pot.Skip(i).Take(requiredPasses).All(p => p == null || p.Length == 0))
                {
                    pot.Clear();
                    return;
                }
            }
        }

        private async Task BroadcastToAllAsync(GameSession session, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            foreach (var client in session.Clients.Values)
            {
                try
                {
                    await client.Response.WriteAsync($"data: {json}\n\n");
                    await client.Response.Body.FlushAsync();
                }
                catch { /* ignore broken clients */ }
            }
        }

        [HttpPost("exchange")]
        public async Task<IActionResult> ExchangeCards([FromBody] ExchangeRequest request)
        {
            if (!SseController._gameSessions.TryGetValue(request.SessionId, out var session))
                return NotFound();

            if (!Guid.TryParse(request.PlayerId, out var playerGuid))
                return BadRequest("Invalid PlayerId");

            // Only allow exchanges at start of rounds 2 or 3
            if (session.RoundNumber != 2 && session.RoundNumber != 3)
                return BadRequest("Exchanges are only allowed at the start of rounds 2 and 3.");

            // Validate we have round1 finish order
            if (session.RoundResults == null || session.RoundResults.Count == 0 || session.RoundResults[0].Count < 4)
                return BadRequest("Round 1 finish order not available or incomplete.");

            var baseOrder = new List<Guid>(session.RoundResults[0]); // 0..3 => 1st..4th
            int idxInBase = baseOrder.IndexOf(playerGuid);
            if (idxInBase < 0)
                return BadRequest("Player is not in the base (round 1) order.");

            // Determine partner: 1st <-> 4th, 2nd <-> 3rd
            int partnerIndex = 3 - idxInBase;
            var partnerGuid = baseOrder[partnerIndex];

            // Validate players have hands
            if (!session.PlayerHands.ContainsKey(playerGuid) || !session.PlayerHands.ContainsKey(partnerGuid))
                return BadRequest("Player or partner hand not found.");

            // Defensive: ensure ExchangeSubmissions map exists
            if (session.ExchangeSubmissions == null)
                session.ExchangeSubmissions = new Dictionary<Guid, string[]>();

            // Basic validation of the cards the player claims to give
            var currentHand = session.PlayerHands[playerGuid];
            var toGive = request.CardsToGive ?? Array.Empty<string>();

            // Validate that each card in toGive exists in the current hand
            foreach (var c in toGive)
            {
                if (string.IsNullOrEmpty(c) || !currentHand.Contains(c))
                    return BadRequest($"Card '{c}' not found in player's hand.");
            }

            // Save submission and check if partner already submitted
            bool partnerAlreadySubmitted = false;
            lock (session.ExchangeSubmissions)
            {
                // overwrite existing submission for this player (if any)
                session.ExchangeSubmissions[playerGuid] = toGive;

                partnerAlreadySubmitted = session.ExchangeSubmissions.ContainsKey(partnerGuid);
            }

            if (!partnerAlreadySubmitted)
            {
                // Wait for partner — notify just the submitter that we're waiting
                var notifyPayload = new
                {
                    type = "exchange_submitted",
                    waitingForPartner = true,
                    partnerId = partnerGuid
                };
                await SendPrivateSseAsync(session, playerGuid, notifyPayload);
                return Ok(new { status = "waiting_for_partner" });
            }

            // Both sides have submitted: perform swap
            string[] partnerToGive;
            lock (session.ExchangeSubmissions)
            {
                partnerToGive = session.ExchangeSubmissions[partnerGuid];
                // remove both submissions now that we'll process
                session.ExchangeSubmissions.Remove(playerGuid);
                session.ExchangeSubmissions.Remove(partnerGuid);
            }

            // Defensive null-checks
            partnerToGive ??= Array.Empty<string>();

            // Apply swap: remove given cards from each hand, add received cards.
            // Use helper methods below for clarity.
            try
            {
                session.PlayerHands[playerGuid] = RemoveCardsFromHandAndAdd(session.PlayerHands[playerGuid], toGive, partnerToGive);
                session.PlayerHands[partnerGuid] = RemoveCardsFromHandAndAdd(session.PlayerHands[partnerGuid], partnerToGive, toGive);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }

            // Notify the two players privately with their new hands
            var payloadForPlayer = new
            {
                type = "exchange_result",
                hand = session.PlayerHands[playerGuid],
                partnerId = partnerGuid
            };
            var payloadForPartner = new
            {
                type = "exchange_result",
                hand = session.PlayerHands[partnerGuid],
                partnerId = playerGuid
            };

            await SendPrivateSseAsync(session, playerGuid, payloadForPlayer);
            await SendPrivateSseAsync(session, partnerGuid, payloadForPartner);

            // Broadcast a global minimal notification (no card values) so UIs can reflect exchange state
            await BroadcastToAllAsync(session, new
            {
                type = "exchange_complete",
                round = session.RoundNumber,
                pair = new[] { playerGuid, partnerGuid }
            });

            return Ok(new { status = "exchanged" });
        }

        // Helper: remove 'toRemove' from hand (all occurrences) and add 'toAdd' to the end.
        // Throws InvalidOperationException if a card to remove is not present.
        private string[] RemoveCardsFromHandAndAdd(string[] hand, string[] toRemove, string[] toAdd)
        {
            var list = hand.ToList();

            // verify removals exist
            foreach (var c in toRemove)
            {
                if (!list.Remove(c))
                    throw new InvalidOperationException($"Card to remove '{c}' not present in hand.");
            }

            // add incoming cards (append)
            foreach (var c in toAdd)
                list.Add(c);

            return list.ToArray();
        }

        // Helper to send a private SSE message to a single player (if connected)
        private async Task SendPrivateSseAsync(GameSession session, Guid playerId, object payload)
        {
            if (!session.Clients.TryGetValue(playerId, out var client)) return;

            try
            {
                var json = JsonSerializer.Serialize(payload);
                await client.Response.WriteAsync($"data: {json}\n\n");
                await client.Response.Body.FlushAsync();
            }
            catch
            {
                // ignore broken client
            }
        }

        // ----- Card string helpers (tolerant parsing) -----
        private static string NormalizeAlnum(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return Regex.Replace(s, @"[^A-Za-z0-9]", string.Empty).ToUpperInvariant();
        }

        private static bool IsJoker(string? card)
        {
            if (string.IsNullOrEmpty(card)) return false;
            var an = NormalizeAlnum(card);
            return an.Contains("JOKER") || an.Contains("RJ") || an.Contains("BJ") || an.Contains("JOKERRED") || an.Contains("JOKERBLACK");
        }

        private static bool IsThreeOfSpades(string? card)
        {
            if (string.IsNullOrEmpty(card)) return false;
            var an = NormalizeAlnum(card);
            if (an.Length == 0) return false;
            // Typical forms: "3S", "3SPADES", "3_S", etc.
            if (an.EndsWith("S")) // last char indicates suit
            {
                var rankPart = an.Substring(0, an.Length - 1);
                return rankPart == "3" || rankPart == "03";
            }
            // fallback: exact match
            return an == "3S";
        }

        private static bool IsRankEight(string? card)
        {
            if (string.IsNullOrEmpty(card)) return false;
            var an = NormalizeAlnum(card);
            // Accept "8", "08", "8H", "8S", etc.
            if (an.Length == 0) return false;
            if (an.StartsWith("8")) return true;
            // look for patterns containing "8" as rank
            return Regex.IsMatch(an, @"(^|[^0-9])8($|[^0-9])");
        }
    }
}
