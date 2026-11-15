using System.Net;
using System.Net.Sockets;
using System.Text;
using AiContent.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration["RUN_WORKER_IN_API"] = "true";
builder.Services.AddStudio(builder.Configuration, builder.Environment);
var host = builder.Build();
var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
var probe = new TcpListener(IPAddress.Any, 8081);
probe.Start();
_ = Task.Run(async () =>
{
    var body = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
    try
    {
        while (!stopping.IsCancellationRequested)
        {
            var client = await probe.AcceptTcpClientAsync(stopping);
            await using var stream = client.GetStream();
            await stream.WriteAsync(body);
            client.Close();
        }
    }
    catch (OperationCanceledException) { }
    catch (ObjectDisposedException) { }
});
host.Run();
probe.Stop();
