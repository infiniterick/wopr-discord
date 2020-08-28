
using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using MassTransit;
using Wopr.Core;

namespace Wopr.Discord {

    public class DiscordHelper{
         public static string FixupEmotes(string emote){
            //something in the chain is adding an extra escape to the unicode emojis breaking discord
            return emote.Contains(@"\\u") ? emote.Replace(@"\\u",@"\u") : emote;
        }
    }

    public class AddContentConsumer : IConsumer<AddContent> {

        DiscordSocketClient client;

        public AddContentConsumer(DiscordSocketClient client){
            this.client = client;
        }

        public async Task Consume(ConsumeContext<AddContent> context){
            var channel = client.GetChannel(Convert.ToUInt64(context.Message.ChannelId)) as ISocketMessageChannel;
            if(channel != null){
                await channel.SendMessageAsync(context.Message.Content);
            }
        }
    }

    public class RemoveContentConsumer : IConsumer<RemoveContent> {

        DiscordSocketClient client;
        
        public RemoveContentConsumer(DiscordSocketClient client){
            this.client = client;
        }

        public async Task Consume(ConsumeContext<RemoveContent> context){
            var channel = client.GetChannel(Convert.ToUInt64(context.Message.ChannelId)) as ISocketMessageChannel;
            if(channel != null){
                await channel.DeleteMessageAsync(Convert.ToUInt64(context.Message.MessageId));
            }
        }
    }

    public class AddReactionConsumer : IConsumer<AddReaction> {
        
        DiscordSocketClient client;
        
        public AddReactionConsumer(DiscordSocketClient client){
            this.client = client;
        }

        public async Task Consume(ConsumeContext<AddReaction> context){
            var channel = client.GetChannel(Convert.ToUInt64(context.Message.ChannelId)) as ISocketMessageChannel;
            if(channel != null){
                var message = await channel.GetMessageAsync(Convert.ToUInt64(context.Message.MessageId));
                await message.AddReactionAsync(new Emoji(DiscordHelper.FixupEmotes(context.Message.Emote)));
            }
        }
    }

    public class RemoveReactionConsumer : IConsumer<RemoveReaction> {
        
        DiscordSocketClient client;
        
        public RemoveReactionConsumer(DiscordSocketClient client){
            this.client = client;
        }

        public async Task Consume(ConsumeContext<RemoveReaction> context){
            var channel = client.GetChannel(Convert.ToUInt64(context.Message.ChannelId)) as ISocketMessageChannel;
            if(channel != null){
                var message = await channel.GetMessageAsync(Convert.ToUInt64(context.Message.MessageId));
                await message.RemoveReactionAsync(new Emoji(DiscordHelper.FixupEmotes(context.Message.Emote)), client.CurrentUser);
            }
        }
    }

    public class RemoveAllReactionsConsumer : IConsumer<RemoveAllReactions> {
        
        DiscordSocketClient client;

        public RemoveAllReactionsConsumer(DiscordSocketClient client){
            this.client = client;
        }

        public async Task Consume(ConsumeContext<RemoveAllReactions> context){
            var channel = client.GetChannel(Convert.ToUInt64(context.Message.ChannelId)) as ISocketMessageChannel;
            if(channel != null){
                var message = await channel.GetMessageAsync(Convert.ToUInt64(context.Message.MessageId));
                await message.RemoveAllReactionsAsync();
            }
        }
    }



}