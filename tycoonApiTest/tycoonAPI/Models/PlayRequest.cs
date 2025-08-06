public class PlayRequest
{
    public int SessionId { get; set; }
    public string PlayerId { get; set; }
    public bool PlayType { get; set; }
    public int HandSize { get; set; }
    public string[] HandPlayed { get; set; }
}

