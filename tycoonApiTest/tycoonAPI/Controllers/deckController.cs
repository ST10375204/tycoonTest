using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace tycoonAPI.Controllers
{
    public class DeckController : ControllerBase
    {
        private string[] deck = new string[]{
    "2H", "3H", "4H", "5H", "6H", "7H", "8H", "9H", "10H", "JH", "QH", "KH", "AH",
    "2D", "3D", "4D", "5D", "6D", "7D", "8D", "9D", "10D", "JD", "QD", "KD", "AD",
    "2C", "3C", "4C", "5C", "6C", "7C", "8C", "9C", "10C", "JC", "QC", "KC", "AC",
    "2S", "3S", "4S", "5S", "6S", "7S", "8S", "9S", "10S", "JS", "QS", "KS", "AS",
    "Joker", "Joker"};
        private Random random = new Random();

        public List<string[]> DealAllHands()
        {
            List<string> workingDeck = new List<string>(deck);
            List<string[]> hands = new List<string[]>();

            int[] handSizes = { 14, 13, 14, 13 };

            foreach (int size in handSizes)
            {
                List<string> hand = new List<string>();

                for (int i = 0; i < size; i++)
                {
                    int index = random.Next(workingDeck.Count);
                    hand.Add(workingDeck[index]);
                    workingDeck.RemoveAt(index);
                }

                hands.Add(hand.ToArray());
            }

            return hands;
        }
    }

}