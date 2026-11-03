# In-process tests of the whole API: WebApplicationFactory starts Api's real startup code (DI, EF Core, routes,
# WebSockets) on an in-memory TestServer, so the HTTP checks open no port. `Health` is any type from the Api assembly:
# the factory uses it to find Api's entry point.
using System.Diagnostics
using System.IO
using System.Net
using System.Net.Http
using System.Net.WebSockets
using System.Text
using System.Threading
using Microsoft.AspNetCore.Mvc.Testing

class ApiChecks
  getter passed : Int32 = 0
  getter failed : Int32 = 0

  def initialize
    # The factory looks for Api's content root next to a solution file; the build output is enough here.
    Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_API", AppContext.BaseDirectory)
    Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning")   # quiet request logs
    # the "malformed json" check makes the Development error page log the parse error; expected, so not shown
    Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware", "None")
    @factory = WebApplicationFactory(Health).new
    @client = @factory.CreateClient()
  end

  def check(name : String, expected : String, actual : String)
    if expected == actual
      @passed += 1
      puts "PASS #{name}"
    else
      @failed += 1
      puts "FAIL #{name}\n  expected: #{expected}\n  actual:   #{actual}"
    end
  end

  def get(path : String) : String
    response = @client.GetAsync(path).Result
    "#{response.StatusCode.as(Int32)} #{response.Content.ReadAsStringAsync().Result}"
  end

  def post(path : String, json : String) : String
    response = @client.PostAsync(path, StringContent.new(json, Encoding.UTF8, "application/json")).Result
    location = response.Headers.Location.nil? ? "" : " at #{response.Headers.Location}"
    "#{response.StatusCode.as(Int32)}#{location} #{response.Content.ReadAsStringAsync().Result}"
  end

  # Sends text messages over /ws and returns what came back, joined by "|". This check starts Api.dll as a real
  # Kestrel process on a free loopback port and connects a standard ClientWebSocket. (WebApplicationFactory's
  # TestServer would avoid the process, but the compiler cannot bind `factory.Server` yet: see the README.)
  def self.echo(messages : Array(String)) : String
    psi = ProcessStartInfo.new("dotnet")
    psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Api.dll"))
    psi.ArgumentList.Add("--urls")
    psi.ArgumentList.Add("http://127.0.0.1:0")
    psi.RedirectStandardOutput = true
    psi.Environment["Logging__LogLevel__Default"] = "Information"   # prints "Now listening on: ..."
    api = Process.Start(psi).not_nil!
    address = ""
    while address == ""
      line = api.StandardOutput.ReadLine()
      raise "Api.dll exited before listening" if line.nil?
      at = line.not_nil!.IndexOf("Now listening on: http://")
      address = line.not_nil!.Substring(at + 25).strip if at >= 0
    end
    socket = ClientWebSocket.new
    socket.ConnectAsync(Uri.new("ws://#{address}/ws"), CancellationToken.None).Wait()
    replies = List(String).new
    buffer = Array(UInt8).new(1024)
    for message in messages
      bytes = Encoding.UTF8.GetBytes(message)
      socket.SendAsync(ArraySegment(UInt8).new(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait()
      received = socket.ReceiveAsync(ArraySegment(UInt8).new(buffer), CancellationToken.None).Result
      replies.Add(Encoding.UTF8.GetString(buffer, 0, received.Count))
    end
    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).Wait()
    closed = socket.State == WebSocketState.Closed
    api.Kill(true)
    api.WaitForExit()
    "#{replies.join("|")} closed=#{closed}"
  end

  def run_all : Int32
    check "health", "200 {\"status\":\"ok\"}", get("/health")
    check "seeded products", "200 [{\"id\":1,\"name\":\"Assam tea\",\"priceCents\":450},{\"id\":2,\"name\":\"Sencha tea\",\"priceCents\":1250}]", get("/products")
    check "one product", "200 {\"id\":2,\"name\":\"Sencha tea\",\"priceCents\":1250}", get("/products/2")
    check "missing product", "404 {\"error\":\"no product 99\"}", get("/products/99")
    check "create product", "201 at /products/3 {\"id\":3,\"name\":\"Oolong\",\"priceCents\":900}", post("/products", "{\"name\":\" Oolong \",\"priceCents\":900}")
    check "created product is stored", "200 {\"id\":3,\"name\":\"Oolong\",\"priceCents\":900}", get("/products/3")
    check "invalid product", "400 {\"error\":\"priceCents must be 0..1000000\"}", post("/products", "{\"name\":\"Gold\",\"priceCents\":-1}")
    check "malformed json", "400", post("/products", "{not json").split(" ")[0]   # body: the Development error page
    check "order quote", "200 {\"subtotalCents\":12500,\"discountCents\":1250,\"totalCents\":11250}", post("/orders/quote", "{\"productId\":2,\"quantity\":10}")
    check "order quote, unknown product", "404 {\"error\":\"no product 7\"}", post("/orders/quote", "{\"productId\":7,\"quantity\":1}")
    check "order quote, bad quantity", "400 {\"error\":\"quantity must be 1..1000\"}", post("/orders/quote", "{\"productId\":1,\"quantity\":0}")
    check "query quote", "200 {\"subtotalCents\":5000,\"discountCents\":0,\"totalCents\":5000}", get("/quote?unitPriceCents=1250&quantity=4")
    check "websocket path over plain http", "400 ", get("/ws")
    @factory.Dispose()
    check "websocket echo", "hello|wonderland closed=true", ApiChecks.echo(["hello", "wonderland"])
    @failed
  end
end
