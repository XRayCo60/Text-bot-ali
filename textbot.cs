using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;

// Set these two values before starting the bot. They intentionally are the first variables in the program.
const long OwnerId = 123456789; // Telegram numeric ID of the owner
const string BotToken = "PUT_YOUR_BOT_TOKEN_HERE";

if (OwnerId == 123456789 || BotToken == "PUT_YOUR_BOT_TOKEN_HERE")
    Console.WriteLine("Warning: replace OwnerId and BotToken at the top of textbot.cs before using the bot.");

var state = BotState.Load("bot-state.json");
var bot = new TelegramBotClient(BotToken);
var me = await bot.GetMeAsync();
Console.WriteLine($"@{me.Username} is running");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = Array.Empty<UpdateType>()
};

await bot.ReceiveAsync(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cts.Token);

Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken cancellationToken)
{
    var message = exception is ApiRequestException apiException
        ? $"Telegram API error ({apiException.ErrorCode}): {apiException.Message}"
        : exception.ToString();
    Console.Error.WriteLine(message);
    return Task.CompletedTask;
}

async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
{
    try
    {
        // A bot only receives channel posts from channels in which it has sufficient access.
        // Telegram has no API for enumerating every channel an account has ever joined, so these
        // updates are used to discover channels for the "تنظیم کانال" picker.
        if (update.Type == UpdateType.ChannelPost && update.ChannelPost is { } channelPost)
        {
            state.RememberChannel(channelPost.Chat);
            state.Save("bot-state.json");
            return;
        }

        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(client, callback, cancellationToken);
            return;
        }

        if (update.Message is not { } message || message.From is null)
            return;

        if (message.Chat.Type == ChatType.Private && message.Text == "/start")
        {
            await client.SendTextMessageAsync(message.Chat.Id, "گپ خود را از ادمین بگیرید", cancellationToken: cancellationToken);
            return;
        }

        // The owner can say عکس and then send the image in the next message.
        if (message.From.Id == OwnerId && message.Chat.Type == ChatType.Private && message.Text?.Trim() == "عکس")
        {
            state.WaitingForPhoto = true;
            state.Save("bot-state.json");
            await client.SendTextMessageAsync(message.Chat.Id, "حالا عکس پوستر را ارسال کنید.", cancellationToken: cancellationToken);
            return;
        }

        if (message.From.Id == OwnerId && state.WaitingForPhoto && message.Chat.Type == ChatType.Private && message.Photo?.Length > 0)
        {
            state.PosterFileId = message.Photo[^1].FileId;
            state.WaitingForPhoto = false;
            state.Save("bot-state.json");
            await client.SendTextMessageAsync(message.Chat.Id, "عکس پوستر ذخیره شد.", cancellationToken: cancellationToken);
            await RefreshPosterAsync(client, cancellationToken);
            return;
        }

        if (message.From.Id != OwnerId)
            return;

        if (message.Chat.Type == ChatType.Private && message.Text?.Trim() == "تنظیم کانال")
        {
            await SendChannelPickerAsync(client, message.Chat.Id, cancellationToken);
            return;
        }

        if (message.Text is { } text && TryReadCountry(text, out var countryName, out var vip))
        {
            if (state.MainChannelId is null)
            {
                await client.SendTextMessageAsync(message.Chat.Id, "ابتدا کانال اصلی را با «تنظیم کانال» انتخاب کنید.", cancellationToken: cancellationToken);
                return;
            }
            if (string.IsNullOrWhiteSpace(state.PosterFileId))
            {
                await client.SendTextMessageAsync(message.Chat.Id, "ابتدا با نوشتن «عکس» عکس پوستر را تعیین کنید.", cancellationToken: cancellationToken);
                return;
            }

            state.Countries[message.Chat.Id.ToString()] = new CountryAssignment
            {
                Country = countryName,
                Vip = vip
            };
            state.Save("bot-state.json");
            await client.SendTextMessageAsync(message.Chat.Id, $"کشور «{countryName}» تنظیم شد. برای تعیین صاحب، به پیام فرد موردنظر پاسخ دهید و بنویسید «تنظیم صاحب».", cancellationToken: cancellationToken);
            await RefreshPosterAsync(client, cancellationToken);
            return;
        }

        if (message.Text?.Trim() == "تنظیم صاحب" && message.ReplyToMessage?.From is { } target)
        {
            if (!state.Countries.TryGetValue(message.Chat.Id.ToString(), out var assignment))
            {
                await client.SendTextMessageAsync(message.Chat.Id, "ابتدا کشور این چت را تنظیم کنید.", cancellationToken: cancellationToken);
                return;
            }

            assignment.OwnerId = target.Id;
            assignment.OwnerName = DisplayName(target);
            state.Save("bot-state.json");
            await client.SendTextMessageAsync(message.Chat.Id, $"{assignment.OwnerName} صاحب کشور {assignment.Country} شد.", cancellationToken: cancellationToken);
            await RefreshPosterAsync(client, cancellationToken);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
    }
}

async Task HandleCallbackAsync(ITelegramBotClient client, CallbackQuery callback, CancellationToken cancellationToken)
{
    if (callback.From.Id != OwnerId)
    {
        await client.AnswerCallbackQueryAsync(callback.Id, "دسترسی ندارید.", cancellationToken: cancellationToken);
        return;
    }

    if (callback.Data?.StartsWith("channel:", StringComparison.Ordinal) == true &&
        long.TryParse(callback.Data["channel:".Length..], out var channelId))
    {
        var channel = state.Channels.FirstOrDefault(x => x.Id == channelId);
        if (channel is null)
        {
            await client.AnswerCallbackQueryAsync(callback.Id, "این کانال دیگر شناخته‌شده نیست.", cancellationToken: cancellationToken);
            return;
        }

        state.MainChannelId = channel.Id;
        state.MainChannelTitle = channel.Title;
        state.Save("bot-state.json");
        await client.AnswerCallbackQueryAsync(callback.Id, "کانال اصلی تنظیم شد.", cancellationToken: cancellationToken);
        if (callback.Message is { } callbackMessage)
            await client.SendTextMessageAsync(callbackMessage.Chat.Id, $"کانال اصلی «{channel.Title}» انتخاب شد.", cancellationToken: cancellationToken);
    }
}

async Task SendChannelPickerAsync(ITelegramBotClient client, long ownerChatId, CancellationToken cancellationToken)
{
    if (state.Channels.Count == 0)
    {
        await client.SendTextMessageAsync(ownerChatId, "هنوز کانالی برای انتخاب پیدا نشد. ربات را در کانال ادمین کنید و یک پست در آن منتشر کنید، سپس دوباره «تنظیم کانال» را بفرستید.", cancellationToken: cancellationToken);
        return;
    }

    // Re-check membership so a channel that was left later is not offered anymore.
    var adminChannels = new List<KnownChannel>();
    foreach (var channel in state.Channels)
    {
        try
        {
            var membership = await client.GetChatMemberAsync(channel.Id, me.Id, cancellationToken);
            if (membership.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator)
                adminChannels.Add(channel);
        }
        catch (ApiRequestException)
        {
            // The channel may have been deleted, or the bot may no longer be an administrator.
        }
    }

    if (adminChannels.Count == 0)
    {
        await client.SendTextMessageAsync(ownerChatId, "هیچ کانالی که ربات در آن ادمین باشد پیدا نشد. ربات را در کانال ادمین کنید و یک پست در آن منتشر کنید.", cancellationToken: cancellationToken);
        return;
    }

    var buttons = adminChannels
        .Select(channel => new[] { InlineKeyboardButton.WithCallbackData(channel.Title, $"channel:{channel.Id}") })
        .ToArray();
    await client.SendTextMessageAsync(ownerChatId, "کانال اصلی را انتخاب کنید:", replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: cancellationToken);
}

async Task RefreshPosterAsync(ITelegramBotClient client, CancellationToken cancellationToken)
{
    if (state.MainChannelId is not long channelId || string.IsNullOrWhiteSpace(state.PosterFileId) || state.Countries.Count == 0)
        return;

    var caption = BuildCaption();
    try
    {
        if (state.PosterMessageId is int messageId)
        {
            await client.EditMessageCaptionAsync(channelId, messageId, caption: caption, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
        }
        else
        {
            var sent = await client.SendPhotoAsync(channelId, new InputOnlineFile(state.PosterFileId), caption: caption, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            state.PosterMessageId = sent.MessageId;
            state.Save("bot-state.json");
        }
    }
    catch (ApiRequestException ex)
    {
        Console.Error.WriteLine($"Could not update the poster in the main channel: {ex.Message}");
    }
}

string BuildCaption()
{
    var result = new StringBuilder("<b>کشورهای موجود</b>\n\n");
    foreach (var assignment in state.Countries.Values.OrderBy(x => x.Country, StringComparer.Ordinal))
    {
        var country = System.Net.WebUtility.HtmlEncode(assignment.Country);
        if (assignment.Vip)
            country = $"<b>✦ {country} ✦</b>";

        result.Append(country);
        if (assignment.OwnerId is long ownerId)
        {
            var owner = System.Net.WebUtility.HtmlEncode(assignment.OwnerName ?? "صاحب کشور");
            result.Append($" — <a href=\"tg://user?id={ownerId}\">{owner}</a>");
        }
        result.Append('\n');
    }
    return result.ToString();
}

static bool TryReadCountry(string text, out string country, out bool vip)
{
    country = string.Empty;
    vip = false;
    var value = text.Trim();
    const string prefix = "تنظیم کشور";
    if (!value.StartsWith(prefix, StringComparison.Ordinal))
        return false;

    var remainder = value[prefix.Length..].Trim();
    if (remainder.StartsWith("ویپ", StringComparison.Ordinal))
    {
        vip = true;
        remainder = remainder[3..].Trim();
    }
    if (remainder.Length == 0)
        return false;
    country = remainder;
    return true;
}

static string DisplayName(User user) => string.IsNullOrWhiteSpace(user.LastName)
    ? user.FirstName
    : $"{user.FirstName} {user.LastName}";

sealed class BotState
{
    public long? MainChannelId { get; set; }
    public string? MainChannelTitle { get; set; }
    public string? PosterFileId { get; set; }
    public int? PosterMessageId { get; set; }
    public bool WaitingForPhoto { get; set; }
    public Dictionary<string, CountryAssignment> Countries { get; set; } = new();
    public List<KnownChannel> Channels { get; set; } = new();

    public void RememberChannel(Chat chat)
    {
        var existing = Channels.FirstOrDefault(x => x.Id == chat.Id);
        if (existing is null)
            Channels.Add(new KnownChannel { Id = chat.Id, Title = chat.Title ?? chat.Username ?? chat.Id.ToString() });
        else
            existing.Title = chat.Title ?? chat.Username ?? existing.Title;
    }

    public static BotState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<BotState>(File.ReadAllText(path)) ?? new BotState();
        }
        catch (Exception ex) { Console.Error.WriteLine($"Could not load state: {ex.Message}"); }
        return new BotState();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

sealed class CountryAssignment
{
    public string Country { get; set; } = string.Empty;
    public bool Vip { get; set; }
    public long? OwnerId { get; set; }
    public string? OwnerName { get; set; }
}

sealed class KnownChannel
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
}
