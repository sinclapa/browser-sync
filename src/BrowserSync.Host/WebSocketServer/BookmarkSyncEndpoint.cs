using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrowserSync.Host.WebSocketServer;

public static class BookmarkSyncEndpoint
{
    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var scopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var registry = context.RequestServices.GetRequiredService<ConnectionRegistry>();
        var logger = context.RequestServices.GetRequiredService<ILogger<ClientConnection>>();

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new ClientConnection(socket, logger);

        try
        {
            await connection.RunAsync(scopeFactory, registry, context.RequestAborted);
        }
        finally
        {
            registry.Remove(connection);
            logger.LogInformation("Client {ClientId} disconnected", connection.ClientId);
        }
    }
}
