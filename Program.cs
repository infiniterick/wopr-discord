using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using ServiceStack.Redis;
using System.Text.Json;
using Wopr.Core;
using MassTransit;
using System.Threading;

namespace Wopr.Discord
{
    //https://discord.com/api/oauth2/authorize?client_id=736302317674037288&scope=bot&permissions=8
    public class Program
    {          
        RedisManagerPool redisPool;
        DiscordSocketClient client;
        IBusControl bus;
        CancellationTokenSource cancel;
        string rabbitToken;
        
        //replay is dangerous. extra safety
        bool enableReplay = false;

        [STAThread]
        public static void Main(string[] args)
            => new Program().MainAsync(args).GetAwaiter().GetResult();

        public async Task MainAsync(string[] args)
        {
            var secretsDir = args.Any() ? args[0] : ".";
            var secrets = Secrets.Load(secretsDir);

            if(!string.IsNullOrEmpty(secrets.StackToken)){
                Console.WriteLine("Running service stack in licensed mode");
                ServiceStack.Licensing.RegisterLicense(secrets.StackToken);
            }

            this.rabbitToken = secrets.RabbitToken;
            cancel = new CancellationTokenSource();
            

            redisPool = new RedisManagerPool(secrets.RedisToken);
            client = new DiscordSocketClient(new DiscordSocketConfig(){LogLevel = LogSeverity.Error});
	        client.Log += DiscordLog;
            client.Connected += Connected;
            client.Disconnected += Disconnected;
            client.MessageReceived +=  MessageReceived;
            client.MessageUpdated += MessageUpdated;
            client.MessageDeleted += MessageDeleted;
            client.UserUpdated += UserUpdated;
            client.GuildMemberUpdated += GuildMemberUpdated;
            client.ReactionAdded += ReactionAdded;
            client.ReactionRemoved += ReactionRemoved;

            Console.CancelKeyPress += (s, e) => {
                client.LogoutAsync().Wait();
                bus.Stop();
                cancel.Cancel();
            };

            await StartMassTransit();

        	await client.LoginAsync(TokenType.Bot,  secrets.DiscordToken);
	        await client.StartAsync();
            cancel.Token.WaitHandle.WaitOne();
        }

        private Task StartMassTransit(){
            bus = Bus.Factory.CreateUsingRabbitMq(sbc =>
            {
                var parts = rabbitToken.Split('@');
                sbc.Host(new Uri(parts[2]), cfg => {
                    cfg.Username(parts[0]);
                    cfg.Password(parts[1]);
                });
                rabbitToken = string.Empty;

                sbc.ReceiveEndpoint("wopr:discord", ep =>
                {
                    ep.Consumer<AddContentConsumer>(()=>{return new AddContentConsumer(client);});
                    ep.Consumer<RemoveContentConsumer>(()=>{return new RemoveContentConsumer(client);});
                    ep.Consumer<AddReactionConsumer>(()=>{return new AddReactionConsumer(client);});
                    ep.Consumer<RemoveReactionConsumer>(()=>{return new RemoveReactionConsumer(client);});
                    ep.Consumer<RemoveAllReactionsConsumer>(()=>{return new RemoveAllReactionsConsumer(client);});
                });
            });

            return bus.StartAsync(); // This is important!
        }

        private async Task ReplayBackup(){
            if(!this.enableReplay)
                return;

            using(var client = redisPool.GetClient()){
                foreach(var item in client.GetRangeFromSortedSet(RedisPaths.DiscordBackup, 0, -1)){
                    if(item.StartsWith("{\"MessageType\":\"UserUpdated\"")){
                        var msg = JsonSerializer.Deserialize<UserUpdated>(item);
                        await bus.Publish(msg);
                    }
                    else if(item.StartsWith("{\"MessageType\":\"ContentReceived\"") || item.StartsWith("{\"MessageType\":\"ContentUpdated\"")){
                        var msg = JsonSerializer.Deserialize<ContentReceived>(item);
                        await bus.Publish(msg);
                    }
                    else if(item.StartsWith("{\"MessageType\":\"ReactionAdded\"")){
                        var msg = JsonSerializer.Deserialize<ReactionAdded>(item);
                        await bus.Publish(msg);

                    }
                    else if(item.StartsWith("{\"MessageType\":\"ReactionRemoved\"")){
                        var msg = JsonSerializer.Deserialize<ReactionRemoved>(item);
                        await bus.Publish(msg);

                    }
                    else if(item.StartsWith("{\"MessageType\":\"Connected\"")){
                        var msg = JsonSerializer.Deserialize<Connected>(item);
                        await bus.Publish(msg);

                    }
                    else if(item.StartsWith("{\"MessageType\":\"Disconnected\"")){
                        var msg = JsonSerializer.Deserialize<Disconnected>(item);
                        await bus.Publish(msg);

                    }
                }
            }
        }
        
        private Task Connected(){
            var msg = new Core.Connected(){
                MessageType = "Connected",
                Timestamp = DateTime.UtcNow,
            };
            
            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<Connected>(msg);
        }

        private Task Disconnected(Exception ex){
            var msg = new Core.Disconnected(){
                MessageType = "Disconnected",
                Timestamp = DateTime.UtcNow,
                ExtraInfo = ex.Message
            };

            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<Disconnected>(msg);        
        }
        
        private Task ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction){
            var msg = new Core.ReactionAdded(){
                MessageType = "ReactionAdded",
                Timestamp = DateTime.UtcNow,
                Channel = new NamedEntity(channel.Id, channel.Name),
                MessageId = message.Id.ToString(),
                Emote = reaction.Emote.Name
            };

            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<ReactionAdded>(msg);
        }

        private Task ReactionRemoved(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction){
            var msg = new Core.ReactionRemoved(){
                MessageType = "ReactionRemoved",
                Timestamp = DateTime.UtcNow,
                Channel = new NamedEntity(channel.Id, channel.Name),
                MessageId = message.Id.ToString(),
                Emote = reaction.Emote.Name
            };

            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<ReactionRemoved>(msg);
        }

        private Task MessageReceived(SocketMessage message){
            var msg = new Core.ContentReceived(){
                MessageType = "ContentReceived",
                Timestamp = DateTime.UtcNow,
                MessageId = message.Id.ToString(),
                Channel = new NamedEntity(message.Channel.Id, message.Channel.Name),
                Author = new NamedEntity(message.Author.Id, message.Author.Username),
                Content = message.Content,
                AttatchmentUri = message.Attachments?.FirstOrDefault()?.Url
            };
            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<ContentReceived>(msg);
        }

        private Task MessageUpdated(Cacheable<IMessage, ulong> original, SocketMessage message, ISocketMessageChannel channel){
            var msg = new Core.ContentUpdated(){
                MessageType = "ContentUpdated",
                Timestamp = DateTime.UtcNow,
                MessageId = message.Id.ToString(),
                OriginalId = original.Id.ToString(),
                Channel = new NamedEntity(message.Channel.Id, message.Channel.Name),
                Author = new NamedEntity(message.Author.Id, message.Author.Username),
                Content = message.Content,
                AttatchmentUri = message.Attachments?.FirstOrDefault()?.Url
            };

            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<ContentUpdated>(msg);
        }

        private Task MessageDeleted(Cacheable<IMessage, ulong> original, ISocketMessageChannel channel){
            var msg = new Core.ContentDeleted(){
                MessageType = "ContentDeleted",
                Timestamp = DateTime.UtcNow,
                OriginalId = original.Id.ToString(),
                Channel = new NamedEntity(channel.Id, channel.Name)
            };

            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<ContentDeleted>(msg);
        }

        private Task UserUpdated(SocketUser original, SocketUser changed){
            var msg = new Core.UserUpdated(){
                MessageType = "UserUpdated",
                Timestamp = DateTime.UtcNow,
                Guild = new NamedEntity(null, null),
                User = new NamedEntity(changed.Id, changed.Username),
                Status = changed.Status.ToString(),
                Activity = new Activity(){
                    Name = changed.Activity?.Name,
                    Type = changed.Activity?.Type.ToString(),
                    Details = changed.Activity?.Details
                }
            };

            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<UserUpdated>(msg);
        }

        private Task GuildMemberUpdated(SocketGuildUser original, SocketGuildUser changed){
            var msg = new Core.UserUpdated(){
                MessageType = "UserUpdated",
                Timestamp = DateTime.UtcNow,
                Guild = new NamedEntity(changed.Guild.Id, changed.Guild.Name),
                User = new NamedEntity(changed.Id, changed.Username),
                Status = changed.Status.ToString(),
                Activity = new Activity(){
                    Name = changed?.Activity?.Name,
                    Type = changed?.Activity?.Type.ToString(),
                    Details = changed?.Activity?.Details
                }
            };

            Log(msg.MessageType);
            ArchiveMessage(msg.Timestamp, JsonSerializer.Serialize(msg));
            return bus.Publish<UserUpdated>(msg);
        }

        private void ArchiveMessage(DateTime timestamp, string json){
            using(var client = redisPool.GetClient()){
                client.AddItemToSortedSet(RedisPaths.DiscordBackup, json, timestamp.Ticks);
            }
        }

        private void Log(string message){
            Console.WriteLine($"{DateTime.UtcNow}|{message}");
        }

        private Task DiscordLog(LogMessage msg){
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }
    }
}
