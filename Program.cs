using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using ServiceStack.Redis;
using System.Text.Json;
using Wopr.Core;

namespace Wopr.Discord
{
    //https://discord.com/api/oauth2/authorize?client_id=736302317674037288&scope=bot&permissions=8
    public class Program
    {   
        RedisManagerPool redisPool;
        DiscordSocketClient client;
        IDiscordEventClient woprClient;
        RedisPubSubServer redisPubSub;

        [STAThread]
        public static void Main(string[] args)
            => new Program().MainAsync(args).GetAwaiter().GetResult();

        public async Task MainAsync(string[] args)
        {
            var secretsDir = args.Any() ? args[0] : ".";
            var secrets = Secrets.Load(secretsDir);

            

            redisPool = new RedisManagerPool(secrets.RedisToken);
            woprClient = new DiscordEventClient(redisPool);
            client = new DiscordSocketClient(new DiscordSocketConfig(){LogLevel = LogSeverity.Error});
	        client.Log += Log;
            client.Connected += Connected;
            client.Disconnected += Disconnected;
            client.MessageReceived +=  MessageReceived;
            client.MessageUpdated += MessageUpdated;
            client.MessageDeleted += MessageDeleted;
            client.UserUpdated += UserUpdated;
            client.GuildMemberUpdated += GuildMemberUpdated;
            client.ReactionAdded += ReactionAdded;
            client.ReactionRemoved += ReactionRemoved;

            redisPubSub = new RedisPubSubServer(redisPool, RedisPaths.ControlReady); 
            redisPubSub.OnMessage += NewControlReady;

        	await client.LoginAsync(TokenType.Bot,  secrets.DiscordToken);
	        await client.StartAsync();
            await Task.Delay(-1);            
            
            
            
        }


        private void PrimeControlPlane(){
            using(var client = redisPool.GetClient()){
                client.PublishMessage(RedisPaths.ControlReady, "init");
            }
        }
        
        private Task Connected(){
            redisPubSub.Start();
            PrimeControlPlane();
            return woprClient.OnConnected();
        }

        private Task Disconnected(Exception ex){
            redisPubSub.Stop();
            return woprClient.OnDisconnected(ex);
        }
        
        private Task ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction){
            return woprClient.OnReactionAdded(
                new NamedEntity(channel.Id, channel.Name),
                message.Id.ToString(),
                reaction.Emote.Name
            );
        }

        private Task ReactionRemoved(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction){
            return woprClient.OnReactionRemoved(
                new NamedEntity(channel.Id, channel.Name),
                message.Id.ToString(),
                reaction.Emote.Name
            );
        }

        private Task MessageReceived(SocketMessage message){
            return woprClient.OnContentReceived(
                message.Id.ToString(),
                new NamedEntity(message.Channel.Id, message.Channel.Name),
                new NamedEntity(message.Author.Id, message.Author.Username),
                message.Content,
                message.Attachments?.FirstOrDefault()?.Url
            );
        }

        private Task MessageUpdated(Cacheable<IMessage, ulong> original, SocketMessage message, ISocketMessageChannel channel){
            return woprClient.OnContentUpdated(
                message.Id.ToString(),
                original.Id.ToString(),
                new NamedEntity(message.Channel.Id, message.Channel.Name),
                new NamedEntity(message.Author.Id, message.Author.Username),
                message.Content,
                message.Attachments?.FirstOrDefault()?.Url
            );
        }

        private Task MessageDeleted(Cacheable<IMessage, ulong> original, ISocketMessageChannel channel){
            return woprClient.OnContentDeleted(
                original.Id.ToString(),
                new NamedEntity(channel.Id, channel.Name)
            );
        }

        private Task UserUpdated(SocketUser original, SocketUser changed){
            return woprClient.OnUserUpdated(
                new NamedEntity(null, null),
                new NamedEntity(changed.Id, changed.Username),
                changed.Status.ToString(),
                new Activity(){
                    Name = changed.Activity.Name,
                    Type = changed.Activity.Type.ToString(),
                    Details = changed.Activity.Details
                }
            );
        }

        private Task GuildMemberUpdated(SocketGuildUser original, SocketGuildUser changed){
            return woprClient.OnUserUpdated(
                new NamedEntity(changed.Guild.Id, changed.Guild.Name),
                new NamedEntity(changed.Id, changed.Username),
                changed.Status.ToString(),
                new Activity(){
                    Name = changed?.Activity?.Name,
                    Type = changed?.Activity?.Type.ToString(),
                    Details = changed?.Activity?.Details
                }
            );
        }

        ///Redis pubsub will call this when new control messages are available in the fresh list
        private void NewControlReady(string channel, string message){
            var raw = string.Empty;
            using(var client = redisPool.GetClient()){
                do{
                    raw = client.PopAndPushItemBetweenLists(RedisPaths.ControlFresh, RedisPaths.ControlProcessed);
                    if(!string.IsNullOrEmpty(raw)){
                        ProcessRawControlMessage(raw);
                    }
                }
                while(!string.IsNullOrEmpty(raw));
            }
        }

        private void ProcessRawControlMessage(string raw){
            try{
                if(raw.StartsWith("{\"MessageType\":\"AddContent\"")){
                    var msg = JsonSerializer.Deserialize<AddContent>(raw);
                    var channel = client.GetChannel(Convert.ToUInt64(msg.ChannelId)) as ISocketMessageChannel;
                    if(channel != null){
                        channel.SendMessageAsync(msg.Content);
                    }
                }else if (raw.StartsWith("{\"MessageType\":\"AddReaction\"")){
                    var msg = JsonSerializer.Deserialize<AddReaction>(raw);
                    var channel = client.GetChannel(Convert.ToUInt64(msg.ChannelId)) as ISocketMessageChannel;
                    if(channel != null){
                        var message = channel.GetMessageAsync(Convert.ToUInt64(msg.MessageId)).Result;
                        message.AddReactionAsync(new Emoji(msg.Emote)).Wait();
                    }
                }else if(raw.StartsWith("{\"MessageType\":\"RemoveContent\"")){
                    var msg = JsonSerializer.Deserialize<RemoveContent>(raw);
                    var channel = client.GetChannel(Convert.ToUInt64(msg.ChannelId)) as ISocketMessageChannel;
                    if(channel != null){
                        channel.DeleteMessageAsync(Convert.ToUInt64(msg.MessageId)).Wait();
                    }
                }else if (raw.StartsWith("{\"MessageType\":\"RemoveReaction\"")){
                    var msg = JsonSerializer.Deserialize<AddReaction>(raw);
                    var channel = client.GetChannel(Convert.ToUInt64(msg.ChannelId)) as ISocketMessageChannel;
                    if(channel != null){
                        var message = channel.GetMessageAsync(Convert.ToUInt64(msg.MessageId)).Result;
                        message.RemoveReactionAsync(new Emoji(msg.Emote), client.CurrentUser);
                    }
                } else if (raw.StartsWith("{\"MessageType\":\"RemoveAllReactions\"")){
                    var msg = JsonSerializer.Deserialize<RemoveAllReactions>(raw);
                    var channel = client.GetChannel(Convert.ToUInt64(msg.ChannelId)) as ISocketMessageChannel;
                    if(channel != null){
                        var message = channel.GetMessageAsync(Convert.ToUInt64(msg.MessageId)).Result;
                        message.RemoveAllReactionsAsync().Wait();
                    }
                }

            }
            catch(Exception ex){
               Console.WriteLine("Exception handling control message: " + ex.Message);
                throw;
            }
        }

        private Task Log(LogMessage msg){
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }
    }
}
