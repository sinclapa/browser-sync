namespace BrowserSync.Core.Protocol;

/// <summary>Sent by the extension immediately after the socket opens.</summary>
public class HelloMessage : BsMessage
{
    public string Browser { get; set; } = string.Empty; // "chrome" | "edge"
    public int ProtocolVersion { get; set; } = 1;
}

/// <summary>Host's reply to `hello`. v1 always requests a fresh snapshot on connect.</summary>
public class HelloAckMessage : BsMessage
{
    public DateTime ServerTimeUtc { get; set; }
    public bool RequestSnapshot { get; set; } = true;
}
