// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable SuggestVarOrType_SimpleTypes
// ReSharper disable SuggestVarOrType_Elsewhere
#pragma warning disable SA1123
#pragma warning disable SA1124
#pragma warning disable SA1509
#pragma warning disable IDE0007
#pragma warning disable IDE0008

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace NATS.Net.DocsExamples.Advanced;

public class IntroPage
{
    public async Task Run()
    {
        Console.WriteLine("____________________________________________________________");
        Console.WriteLine("NATS.Net.DocsExamples.Advanced.IntroPage");

        {
            #region lowlevel-sub
            await using NatsConnection nc = new NatsConnection();

            // Connections are lazy, so we need to connect explicitly
            // to avoid any races between subscription and publishing.
            await nc.ConnectAsync();

            await using INatsSub<int> sub = await nc.SubscribeCoreAsync<int>("foo");

            for (int i = 0; i < 10; i++)
            {
                Console.WriteLine($" Publishing {i}...");
                await nc.PublishAsync<int>("foo", i);
            }

            // Signal subscription to stop
            await nc.PublishAsync<int>("foo", -1);

            // Messages have been collected in the subscription internal channel
            // now we can drain them
            await foreach (NatsMsg<int> msg in sub.Msgs.ReadAllAsync())
            {
                Console.WriteLine($"Received {msg.Subject}: {msg.Data}\n");
                if (msg.Data == -1)
                    break;
            }

            // We can unsubscribe from the subscription explicitly
            // (otherwise dispose will do it for us)
            await sub.UnsubscribeAsync();
            #endregion
        }

        {
            #region ping
            await using NatsClient nc = new NatsClient();

            TimeSpan rtt = await nc.PingAsync();

            Console.WriteLine($"RTT to server: {rtt}");
            #endregion
        }

        {
            #region logging
            using ILoggerFactory loggerFactory = LoggerFactory.Create(configure: builder => builder.AddConsole());

            NatsOpts opts = new NatsOpts { LoggerFactory = loggerFactory };

            await using NatsClient nc = new NatsClient(opts);
            #endregion
        }

        {
            #region opts

            NatsOpts opts = new NatsOpts
            {
                // You need to set pending in the constructor and not use
                // the option here, as it will be ignored.
                SubPendingChannelFullMode = BoundedChannelFullMode.DropOldest,

                // Your custom options
                SerializerRegistry = new MyProtoBufSerializerRegistry(),

                // ...
            };

            await using NatsClient nc = new NatsClient(opts, pending: BoundedChannelFullMode.DropNewest);
            #endregion
        }

        {
            #region opts2

            NatsOpts opts = new NatsOpts
            {
                // Your custom options
            };

            await using NatsConnection nc = new NatsConnection(opts);
            #endregion
        }
    }
}
