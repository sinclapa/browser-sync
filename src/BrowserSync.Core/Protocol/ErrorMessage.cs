namespace BrowserSync.Core.Protocol;

public class ErrorMessage : BsMessage
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
