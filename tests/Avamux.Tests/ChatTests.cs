using Avamux.Core.Ai;
using Avamux.Plugins;

namespace Avamux.Tests;

public class ChatTests
{
    private static async Task WaitFor(Func<bool> condition, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_chat_keeps_its_messages_and_continues_its_conversation_after_a_restart()
    {
        await using var b = new AiTests.Box();
        var chats = b.App.Ai.Chats;
        var chat = chats.Create(new AiChatRequest { ProviderId = "claude", Model = "haiku" });
        await chats.SendAsync(chat.Id, "first question");
        var thread = ChatService.Thread(chat);
        Assert.Equal(["user", "assistant"], thread.Select(m => m.Role));
        Assert.Equal("Echo: first question", thread[1].Text.Trim());
        Assert.Equal("Let me think.", thread[1].Thinking);
        Assert.NotNull(thread[1].ThinkingSeconds);
        Assert.Equal("first question", chat.Title);
        Assert.Equal(1520, chat.ContextTokens);
        Assert.Equal(200000, chat.ContextWindow);
        var session = thread[1].ProviderSession;
        Assert.NotNull(session);

        await chats.SendAsync(chat.Id, "second");
        Assert.Single(b.Calls(), c => c is System.Text.Json.Nodes.JsonArray); // one process for both

        // A new start (avamux restarted): the chat is read back and the next message resumes the conversation.
        await b.App.Ai.Chats.DisposeAsync();
        var again = new ChatService(b.App, b.App.Ai);
        var loaded = again.Get(chat.Id);
        Assert.Equal(4, ChatService.Thread(loaded).Count);
        await again.SendAsync(chat.Id, "third");
        var args = b.Args();
        Assert.Equal(session, args[args.IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--fork-session", args);
        await again.DisposeAsync();
    }

    [Fact]
    public async Task Editing_a_message_branches_and_both_versions_stay()
    {
        await using var b = new AiTests.Box();
        var chats = b.App.Ai.Chats;
        var chat = chats.Create(new AiChatRequest { ProviderId = "claude" });
        await chats.SendAsync(chat.Id, "one");
        await chats.SendAsync(chat.Id, "two");
        var before = ChatService.Thread(chat);
        var firstReply = before[1];
        var second = before[2];
        Assert.Equal(firstReply.ProviderSession, before[3].ProviderSession);
        Assert.StartsWith("assistant-", second.ForkToken);

        await chats.EditAndResendAsync(chat.Id, second.Id, "two, again");
        var after = ChatService.Thread(chat);
        Assert.Equal(["one", "Echo: one", "two, again", "Echo: two, again"], after.Select(m => m.Text.Trim()));
        var versions = ChatService.Versions(chat, after[2]);
        Assert.Equal(["two", "two, again"], versions.Select(v => v.Text));
        var args = b.Args();
        Assert.Equal(firstReply.ProviderSession, args[args.IndexOf("--resume") + 1]);
        Assert.Contains("--fork-session", args);
        Assert.Equal(second.ForkToken, args[args.IndexOf("--resume-session-at") + 1]);
        Assert.NotEqual(firstReply.ProviderSession, after[3].ProviderSession);

        // The old version is still there, with what followed it.
        chats.SelectVersion(chat.Id, second.Id);
        Assert.Equal("Echo: two", ChatService.Thread(chat)[3].Text.Trim());

        // Editing the very first message starts afresh.
        await chats.EditAndResendAsync(chat.Id, before[0].Id, "zero");
        Assert.Equal(["zero", "Echo: zero"], ChatService.Thread(chat).Select(m => m.Text.Trim()));
        Assert.DoesNotContain("--resume", b.Args());
    }

    [Fact]
    public async Task Replies_can_be_edited_by_hand_and_retried()
    {
        await using var b = new AiTests.Box();
        var chats = b.App.Ai.Chats;
        var chat = chats.Create(new AiChatRequest { ProviderId = "claude" });
        await chats.SendAsync(chat.Id, "hi");
        var reply = ChatService.Thread(chat)[1];
        chats.EditReply(chat.Id, reply.Id, "my own words");
        Assert.True(reply.Edited);
        Assert.Equal("my own words", ChatService.Thread(chat)[1].Text);
        await chats.RetryAsync(chat.Id, reply.Id);
        var thread = ChatService.Thread(chat);
        Assert.Equal("Echo: hi", thread[1].Text.Trim());
        Assert.Equal(2, ChatService.Versions(chat, thread[0]).Count);
    }

    [Fact]
    public async Task Questions_and_permissions_wait_in_the_chat_until_answered()
    {
        await using var b = new AiTests.Box();
        var chats = b.App.Ai.Chats;
        var chat = chats.Create(new AiChatRequest { ProviderId = "claude" });
        var send = chats.SendAsync(chat.Id, "please write");
        await WaitFor(() => chats.Requests(chat.Id).Count == 1);
        Assert.True(chats.IsBusy(chat.Id));
        var request = chats.Requests(chat.Id)[0];
        Assert.IsType<AiPermissionRequest>(request.Request);
        await chats.AnswerAsync(chat.Id, request.Id, new AiAnswer { Decision = AiDecision.Allow });
        await send.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(chats.Requests(chat.Id));
        var reply = ChatService.Thread(chat)[1];
        Assert.Equal("Write", reply.Tools.Single().Name);
        Assert.True(reply.Tools.Single().Done);
        Assert.Equal("written", reply.Text.Trim());
    }

    [Fact]
    public async Task Changing_provider_hands_the_conversation_over_as_a_transcript()
    {
        await using var b = new AiTests.Box();
        var chats = b.App.Ai.Chats;
        var chat = chats.Create(new AiChatRequest { ProviderId = "claude" });
        await chats.SendAsync(chat.Id, "remember the word banana");
        await chats.ConfigureAsync(chat.Id, provider: "pi-fake");
        await chats.SendAsync(chat.Id, "what was the word?");
        var args = b.Args();
        Assert.Equal("rpc", args[args.IndexOf("--mode") + 1]);
        Assert.Contains("remember the word banana", args[args.IndexOf("--append-system-prompt") + 1]);
        Assert.Equal("pi-fake", ChatService.Thread(chat)[3].ProviderId);
    }

    [Fact]
    public async Task Attachments_are_copied_into_the_chat_and_pictures_sent_as_pictures()
    {
        await using var b = new AiTests.Box();
        var chats = b.App.Ai.Chats;
        var chat = chats.Create(new AiChatRequest { ProviderId = "claude" });
        var png = chats.AttachBytes(chat.Id, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="), "shot.png");
        var doc = chats.AttachBytes(chat.Id, "notes"u8.ToArray(), "notes.txt");
        Assert.StartsWith(chats.FolderOf(chat.Id), png.Path);
        Assert.True(png.IsImage);
        await chats.SendAsync(chat.Id, "look", [png, doc]);
        Assert.Contains(b.Calls(), c => c is System.Text.Json.Nodes.JsonObject o && o["images"]?.GetValue<int>() == 1);
        Assert.Equal(2, ChatService.Thread(chat)[0].Attachments.Count);
    }

    [Fact]
    public async Task Chats_are_listed_pinned_first_renamed_and_deleted()
    {
        await using var b = new AiTests.Box();
        var chats = b.App.Ai.Chats;
        var a = chats.Create(new AiChatRequest { ProviderId = "claude", Title = "A" });
        var c = chats.Create(new AiChatRequest { ProviderId = "claude", Title = "C" });
        chats.Pin(a.Id, true);
        Assert.Equal(["A", "C"], chats.List().Select(x => x.Title));
        chats.Rename(c.Id, "Renamed");
        Assert.Equal("Renamed", chats.Get(c.Id).Title);
        await chats.DeleteAsync(c.Id);
        Assert.Null(chats.Find(c.Id));
        Assert.False(Directory.Exists(chats.FolderOf(c.Id)));
    }
}
