using System.Net.WebSockets
using System.Threading
using Microsoft.AspNetCore.Http

module Echo
  # /ws: a WebSocket that sends every message back until the client closes; plain HTTP gets 400.
  async def self.serve(context : HttpContext)
    if context.WebSockets.IsWebSocketRequest
      socket : WebSocket = await context.WebSockets.AcceptWebSocketAsync()   # typed: see README, "Known gaps"
      buffer = Array(UInt8).new(4096)
      open = true
      while open
        received = await socket.ReceiveAsync(ArraySegment(UInt8).new(buffer), CancellationToken.None)
        if received.MessageType == WebSocketMessageType.Close
          open = false
        else
          await socket.SendAsync(ArraySegment(UInt8).new(buffer, 0, received.Count), received.MessageType, received.EndOfMessage, CancellationToken.None)
        end
      end
      await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
    else
      context.Response.StatusCode = 400
    end
  end
end
