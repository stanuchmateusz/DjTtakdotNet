namespace DjTtakdotNet.Music;

public class TrackInfo
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; }
    public string Url { get; set; }
    public string Thumbnail { get; set; }
    public TimeSpan Duration { get; set; }
    public string Uploader { get; set; }
    public string Query { get; set; }
    public string DurationString => Duration.ToString(@"mm\:ss");
}