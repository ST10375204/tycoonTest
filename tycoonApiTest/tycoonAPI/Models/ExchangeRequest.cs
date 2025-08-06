public class ExchangeRequest
{
    public int SessionId { get; set; }
    public string PlayerId { get; set; } = "";
    public string[] CardsToGive { get; set; } = [];
    public string[] CardsInHand { get; set; } = [];
}
