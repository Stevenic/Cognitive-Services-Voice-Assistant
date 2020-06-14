using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using Microsoft.CognitiveServices.Speech.Dialog;
using Newtonsoft.Json.Linq;
using Microsoft.Azure.ServiceBus.Management;
using System.Runtime.CompilerServices;
using Microsoft.Azure.ServiceBus;
using System.Threading;
using System.Collections.Concurrent;

namespace VoiceAssistantClient
{
    public class ProactiveActivitiesReceiver : IDisposable
    {
        const string TopicName = "ProactiveActivities";
        static ISubscriptionClient subscriptionClient = null;
        static ConcurrentDictionary<string, ProactiveActivitiesReceiver> receivers = new ConcurrentDictionary<string, ProactiveActivitiesReceiver>();

        private DialogServiceConnector connector = null;
        private string conversationId = null;

        public ProactiveActivitiesReceiver(DialogServiceConnector connector, string conversationId)
        {
            this.connector = connector;
            this.conversationId = conversationId;

            // Register receiver
            receivers.TryAdd(conversationId, this);
        }

        private async Task SendActivityAsync(string activityJSON, CancellationToken token)
        {
            await this.connector.SendActivityAsync(activityJSON).ConfigureAwait(false);
        }

        public void Dispose()
        {
            // Unregister receiver
            ProactiveActivitiesReceiver val;
            receivers.TryRemove(this.conversationId, out val);
        }

        public static async Task SubscribeAsync(string serviceBusConnectionString, string subscriptionName, int maxConcurrentCalls = 1)
        {
            // Create subscription on first call
            var client = new ManagementClient(serviceBusConnectionString);
            if (!await client.SubscriptionExistsAsync(TopicName, subscriptionName).ConfigureAwait(false))
            {
                await client.CreateSubscriptionAsync(new SubscriptionDescription(TopicName, subscriptionName)).ConfigureAwait(false);
            }

            // Create subscription client
            subscriptionClient = new SubscriptionClient(serviceBusConnectionString, TopicName, subscriptionName);

            // Configure the message handler options in terms of exception handling, number of concurrent messages to deliver, etc.
            var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
            {
                // Maximum number of concurrent calls to the callback ProcessMessagesAsync(), set to 1 for simplicity.
                // Set it according to how many messages the application wants to process in parallel.
                MaxConcurrentCalls = maxConcurrentCalls,

                // Indicates whether the message pump should automatically complete the messages after returning from user callback.
                // False below indicates the complete operation is handled by the user callback as in ProcessMessagesAsync().
                AutoComplete = true
            };

            // Register the function that processes messages.
            subscriptionClient.RegisterMessageHandler(ProcessMessagesAsync, messageHandlerOptions);
        }

        public static async Task UnsubscribeAsync()
        {
            if (subscriptionClient != null)
            {
                await subscriptionClient.CloseAsync().ConfigureAwait(false);
                subscriptionClient = null;
            }
        }


        private static async Task ProcessMessagesAsync(Message message, CancellationToken token)
        {
            // Deserialize received activity
            var activityJSON = Encoding.UTF8.GetString(message.Body);
            var activity = JObject.Parse(activityJSON);
            string conversationId = (string)activity["conversation"]["id"];

            // Lookup receiver and forward activity
            ProactiveActivitiesReceiver receiver;
            if (receivers.TryGetValue(conversationId, out receiver))
            {
                await receiver.SendActivityAsync(activityJSON, token).ConfigureAwait(false);
            }
        }

        private static Task ExceptionReceivedHandler(ExceptionReceivedEventArgs exceptionReceivedEventArgs)
        {
            Console.WriteLine($"Message handler encountered an exception {exceptionReceivedEventArgs.Exception}.");
            var context = exceptionReceivedEventArgs.ExceptionReceivedContext;
            Console.WriteLine("Exception context for troubleshooting:");
            Console.WriteLine($"- Endpoint: {context.Endpoint}");
            Console.WriteLine($"- Entity Path: {context.EntityPath}");
            Console.WriteLine($"- Executing Action: {context.Action}");
            return Task.CompletedTask;
        }
    }
}
