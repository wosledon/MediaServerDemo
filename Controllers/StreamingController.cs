using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Buffers;

namespace MediaServerDemo.Controllers
{
    

    [Route("api/[controller]")]
    [ApiController]
    public class StreamingController : ControllerBase
    {
        private static readonly ConcurrentDictionary<string, Channel<byte[]>> Channels = new ConcurrentDictionary<string, Channel<byte[]>>();


        private  static readonly VideoStreamManager Manager = new VideoStreamManager();

        [HttpPost("{id}")]
        [RequestSizeLimit(100_000_000)] // Limit the request size to 10MB
        public async Task<IActionResult> Post(string id)
        {
            if (!Channels.TryGetValue(id, out var channel) || !channel.Writer.TryWrite([]))
            {
                channel = Channel.CreateUnbounded<byte[]>();
                Channels[id] = channel;
            }
            var data = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int bytesRead;
                while ((bytesRead = await Request.Body.ReadAsync(data.AsMemory(0, data.Length))) > 0)
                {
                    var chunk = data.AsSpan(0, bytesRead).ToArray();
                    await channel.Writer.WriteAsync(chunk);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
            channel.Writer.Complete();
            return Ok();

            //var channel = Manager.GetVideoStream(id);
            //if ( channel is null || !channel.Writer.TryWrite([]))
            //{
            //    channel = Channel.CreateUnbounded<byte[]>();
            //    Manager.AddVideoStream(id, channel);
            //}
            //var data = ArrayPool<byte>.Shared.Rent(8192);
            //try
            //{
            //    int bytesRead;
            //    while ((bytesRead = await Request.Body.ReadAsync(data.AsMemory(0, data.Length))) > 0)
            //    {
            //        var chunk = data.AsSpan(0, bytesRead).ToArray();
            //        await channel.Writer.WriteAsync(chunk);
            //    }
            //}
            //finally
            //{
            //    ArrayPool<byte>.Shared.Return(data);
            //}
            //channel.Writer.Complete();
            //return Ok();
        }

        [HttpGet("{id}")]
        public IActionResult Get(string id)
        {
            if (Channels.TryGetValue(id, out var channel))
            {
                return new PushStreamResult(async outputStream =>
                {
                    var cts = new CancellationTokenSource();
                    try
                    {
                        await foreach (var buffer in channel.Reader.ReadAllAsync(cts.Token))
                        {
                            await outputStream.WriteAsync(buffer.AsMemory(0, buffer.Length));
                            await outputStream.FlushAsync();
                            cts.CancelAfter(TimeSpan.FromSeconds(10)); // Reset the cancellation token to cancel after 10 seconds
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        //Channels.TryRemove(id, out _);
                    }
                    finally
                    {
                        Channels.TryRemove(id, out _);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }, "video/x-flv");
            }
            else
            {
                return NotFound();
            }
            //var channel = Manager.GetVideoStream(id);
            //if (channel is not null)
            //{
            //    return new PushStreamResult(async outputStream =>
            //    {
            //        var cts = new CancellationTokenSource();
            //        try
            //        {
            //            await foreach (var buffer in channel.Reader.ReadAllAsync(cts.Token))
            //            {
            //                await outputStream.WriteAsync(buffer.AsMemory(0, buffer.Length));
            //                await outputStream.FlushAsync();
            //                cts.CancelAfter(TimeSpan.FromSeconds(10)); // Reset the cancellation token to cancel after 10 seconds
            //            }
            //        }
            //        catch (OperationCanceledException)
            //        {
            //            Manager.CancelVideoStream(id);
            //        }
            //        finally
            //        {
            //            GC.Collect();
            //            GC.WaitForPendingFinalizers();
            //        }
            //    }, "video/x-flv");
            //}
            //else
            //{
            //    return NotFound();
            //}
        }

        public class PushStreamResult : IActionResult
        {
            private readonly Func<Stream, Task> _onStreamAvailable;
            private readonly string _contentType;

            public PushStreamResult(Func<Stream, Task> onStreamAvailable, string contentType)
            {
                _onStreamAvailable = onStreamAvailable;
                _contentType = contentType;
            }

            public async Task ExecuteResultAsync(ActionContext context)
            {
                var response = context.HttpContext.Response;
                response.ContentType = _contentType;
                await _onStreamAvailable(response.Body);
            }
        }

        public class VideoStreamManager
        {
            private ConcurrentBag<(string id, Channel<byte[]> channel)> channels = new ConcurrentBag<(string id, Channel<byte[]> channel)>();
            private ConcurrentDictionary<string, CancellationTokenSource> cancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();

            public void AddVideoStream(string id, Channel<byte[]> channel)
            {
                var cts = new CancellationTokenSource();
                if (cancellationTokens.TryAdd(id, cts))
                {
                    channels.Add((id, channel));
                    _ = RemoveVideoStreamAsync(id, cts.Token);
                }
            }

            private async Task RemoveVideoStreamAsync(string id, CancellationToken cancellationToken)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    cancellationTokens.TryRemove(id, out _);
                    channels = new ConcurrentBag<(string id, Channel<byte[]> channel)>(channels.Where(x => x.id != id));
                }
            }

            public void CancelVideoStream(string id)
            {
                if (cancellationTokens.TryRemove(id, out var cts))
                {
                    cts.Cancel();
                    channels = new ConcurrentBag<(string id, Channel<byte[]> channel)>(channels.Where(x => x.id != id));
                }
            }

            public Channel<byte[]> GetVideoStream(string id)
            {
                return channels.FirstOrDefault(x => x.id == id).channel;
            }
        }
    }
}
