using WinFinger.Models;
using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

/// <summary>ChatStore 的纯函数部分（标题 / 截断 / 上下文组装），不需要 Dispatcher。</summary>
public class ChatStoreTests
{
    private static ChatSession Session(params (string Role, string Content)[] messages)
    {
        var session = new ChatSession();
        foreach (var (role, content) in messages)
            session.Messages.Add(new ChatMessage { Role = role, Content = content });
        return session;
    }

    private static ChatMessage User(string text) => new() { Role = ChatMessage.RoleUser, Content = text };
    private static ChatMessage Assistant(string text) => new() { Role = ChatMessage.RoleAssistant, Content = text };

    [Fact]
    public void DeriveTitle_UsesFirstNonEmptyLine()
    {
        Assert.Equal("帮我总结这段日志", ChatStore.DeriveTitle("\n\n帮我总结这段日志\n后面还有很多内容"));
    }

    [Fact]
    public void DeriveTitle_StripsMarkdownNoise()
    {
        Assert.Equal("标题", ChatStore.DeriveTitle("## 标题"));
        Assert.Equal("要点", ChatStore.DeriveTitle("- **要点**"));
    }

    [Fact]
    public void DeriveTitle_TruncatesLongLine()
    {
        string title = ChatStore.DeriveTitle(new string('あ', 50));
        Assert.Equal(21, title.Length); // 20 + 省略号
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void DeriveTitle_EmptyInput_FallsBackToDefault()
    {
        Assert.Equal(ChatSession.DefaultTitle, ChatStore.DeriveTitle("   \n  "));
    }

    [Fact]
    public void TrimForStorage_KeepsShortText()
    {
        string text = ChatStore.TrimForStorage("短文本", out bool truncated);
        Assert.False(truncated);
        Assert.Equal("短文本", text);
    }

    [Fact]
    public void TrimForStorage_CutsOverlongText()
    {
        string text = ChatStore.TrimForStorage(new string('x', ChatStore.MaxMessageChars + 100), out bool truncated);
        Assert.True(truncated);
        Assert.Contains("已截断", text);
        Assert.True(text.Length < ChatStore.MaxMessageChars + 100);
    }

    [Fact]
    public void ClipMiddle_KeepsHeadAndTail()
    {
        string text = new string('a', 100) + new string('b', 100);
        string clipped = ChatStore.ClipMiddle(text, 40);
        Assert.StartsWith("aaaa", clipped);
        Assert.EndsWith("bbbb", clipped);
        Assert.Contains("中间省略", clipped);
    }

    [Fact]
    public void BuildContext_PutsSystemFirstAndKeepsOrder()
    {
        var session = Session(
            (ChatMessage.RoleUser, "一"),
            (ChatMessage.RoleAssistant, "二"),
            (ChatMessage.RoleUser, "三"));

        var turns = ChatStore.BuildContext(session, "系统提示");

        Assert.Equal(4, turns.Count);
        Assert.Equal(ChatMessage.RoleSystem, turns[0].Role);
        Assert.Equal("系统提示", turns[0].Content);
        Assert.Equal(new[] { "一", "二", "三" }, turns.Skip(1).Select(t => t.Content).ToArray());
    }

    [Fact]
    public void BuildContext_WithoutSystemPrompt_OmitsSystemTurn()
    {
        var turns = ChatStore.BuildContext(Session((ChatMessage.RoleUser, "问题")), "  ");
        Assert.Single(turns);
        Assert.Equal(ChatMessage.RoleUser, turns[0].Role);
    }

    [Fact]
    public void BuildContext_DropsOldMessagesOverBudget()
    {
        var session = Session(
            (ChatMessage.RoleUser, new string('a', 500)),
            (ChatMessage.RoleAssistant, new string('b', 500)),
            (ChatMessage.RoleUser, "最新问题"));

        var turns = ChatStore.BuildContext(session, "", charBudget: 600);

        Assert.Single(turns);
        Assert.Equal("最新问题", turns[0].Content);
    }

    [Fact]
    public void BuildContext_AlwaysIncludesNewestUserMessage_EvenWhenOversized()
    {
        var session = Session((ChatMessage.RoleUser, new string('x', 5000)));

        var turns = ChatStore.BuildContext(session, "", charBudget: 100);

        Assert.Single(turns);
        Assert.Contains("中间省略", turns[0].Content);
    }

    [Fact]
    public void BuildContext_SkipsPartialAndFailedReplies()
    {
        var session = new ChatSession();
        session.Messages.Add(User("问题一"));
        session.Messages.Add(new ChatMessage { Role = ChatMessage.RoleAssistant, Content = "半截", IsPartial = true });
        session.Messages.Add(new ChatMessage { Role = ChatMessage.RoleAssistant, Content = "错的", Error = "超时" });
        session.Messages.Add(User("问题二"));

        var turns = ChatStore.BuildContext(session, "");

        Assert.Equal(new[] { "问题一", "问题二" }, turns.Select(t => t.Content).ToArray());
    }

    [Fact]
    public void BuildContext_NeverStartsWithAssistant()
    {
        var session = new ChatSession();
        session.Messages.Add(Assistant("上一轮的回答"));
        session.Messages.Add(User("接着问"));

        var turns = ChatStore.BuildContext(session, "", charBudget: 100000);

        Assert.Equal(ChatMessage.RoleUser, turns[0].Role);
    }

    [Fact]
    public void BuildContext_RespectsTurnLimit()
    {
        var session = new ChatSession();
        for (int i = 0; i < 30; i++) session.Messages.Add(User($"第{i}条"));

        var turns = ChatStore.BuildContext(session, "", charBudget: 100000, maxTurns: 5);

        Assert.Equal(5, turns.Count);
        Assert.Equal("第29条", turns[^1].Content);
    }

    [Fact]
    public void BuildContext_EmptySession_ReturnsSystemOnlyOrNothing()
    {
        Assert.Empty(ChatStore.BuildContext(new ChatSession(), ""));
        var withPrompt = ChatStore.BuildContext(new ChatSession(), "系统");
        Assert.Single(withPrompt);
        Assert.Equal(ChatMessage.RoleSystem, withPrompt[0].Role);
    }
}
