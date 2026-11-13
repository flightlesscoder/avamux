using System.Text.Json.Nodes;
using Avamux.Core;
using Avamux.Core.Nav;

namespace Avamux.Tests;

public class CustomPageTests
{
    [Fact]
    public void Pages_are_validated_kept_in_order_and_survive_a_restart()
    {
        using var box = new Sandbox();
        var nav = new NavService(box.Paths);
        var changes = 0;
        nav.Changed += () => changes++;

        var top = nav.Add(new CustomPage { Name = " Monitor ", Kind = "Terminal", Command = "btm" });
        var mail = nav.Add(new CustomPage { Name = "Mail", Kind = "web", Url = "mail.example.com" });
        var mine = nav.Add(new CustomPage { Name = "Mine", Kind = "plugin", Assembly = "Mine.dll", Enabled = false });
        Assert.Equal(("Monitor", "terminal", "custom:" + top.Id), (top.Name, top.Kind, top.Key));
        Assert.Equal("https://mail.example.com", mail.Url); // a bare host gets a scheme
        Assert.Equal(3, changes);

        Assert.Throws<ArgumentException>(() => nav.Add(new CustomPage { Name = "" }));
        Assert.Throws<ArgumentException>(() => nav.Add(new CustomPage { Name = "monitor" })); // names are unique, whatever the case
        Assert.Throws<ArgumentException>(() => nav.Add(new CustomPage { Name = "X", Kind = "web" }));
        Assert.Throws<ArgumentException>(() => nav.Add(new CustomPage { Name = "X", Kind = "web", Url = "javascript:alert(1)" }));
        Assert.Throws<ArgumentException>(() => nav.Add(new CustomPage { Name = "X", Kind = "plugin" }));
        Assert.Throws<ArgumentException>(() => nav.Add(new CustomPage { Name = "X", Kind = "spreadsheet" }));

        // Found by id, key or name; what comes back is a copy.
        Assert.Equal(mail.Id, nav.Get("mail").Id);
        Assert.Equal(mail.Id, nav.Get(mail.Key).Id);
        nav.Get(mail.Id).Name = "changed";
        Assert.Equal("Mail", nav.Get(mail.Id).Name);

        nav.Move(mine.Id, -5);
        nav.SetEnabled(mine.Id, true);
        Assert.Equal(["Mine", "Monitor", "Mail"], nav.Pages.Select(p => p.Name));

        // The signature tells the window when a page has to be started afresh: not for a new name or icon.
        var before = nav.Get(top.Id).Signature;
        var edited = nav.Get(top.Id);
        edited.Name = "Bottom"; edited.Icon = "pulse";
        nav.Update(edited);
        Assert.Equal(before, nav.Get(top.Id).Signature);
        edited.Command = "htop";
        nav.Update(edited);
        Assert.NotEqual(before, nav.Get(top.Id).Signature);

        nav.Remove("Mail");
        var again = new NavService(box.Paths);
        Assert.Equal(["Mine", "Bottom"], again.Pages.Select(p => p.Name));
        Assert.Equal("htop", again.Get("Bottom").Command);
        Assert.NotEqual(mail.Id, again.Add(new CustomPage { Name = "Later" }).Id); // ids are not reused
    }

    [Fact]
    public void Attention_is_announced_once_and_badges_are_short()
    {
        using var box = new Sandbox();
        var nav = new NavService(box.Paths);
        var page = nav.Add(new CustomPage { Name = "Chat", Kind = "web", Url = "https://chat.example.com" });
        var off = nav.Add(new CustomPage { Name = "Off", Enabled = false });
        var announced = new List<string>();
        var marks = 0;
        nav.AttentionRequested += (p, message) => announced.Add($"{p.Name}: {message}");
        nav.MarksChanged += () => marks++;

        nav.RequestAttention(page.Id, "  someone wrote  ");
        nav.RequestAttention(page.Id, "again"); // already marked: nothing new
        nav.RequestAttention(off.Id, "ignored"); // switched off
        nav.RequestAttention("nope", "ignored");
        Assert.Equal(["Chat: someone wrote"], announced);
        Assert.Equal("someone wrote", nav.Attention(page.Id));
        Assert.Null(nav.Attention(off.Id));

        // Seen, then asking again straight away: marked again, but not announced a second time within seconds.
        nav.ClearAttention(page.Key);
        Assert.Null(nav.Attention(page.Id));
        nav.RequestAttention(page.Id, new string('x', 500));
        Assert.Single(announced);
        Assert.Equal(200, nav.Attention(page.Id)!.Length);

        nav.SetBadge(page.Id, "123456");
        Assert.Equal("1234", nav.Badge(page.Id));
        var soFar = marks;
        nav.SetBadge(page.Id, "1234");
        Assert.Equal(soFar, marks); // unchanged: no redraw
        nav.SetBadge(page.Id, "");
        Assert.Null(nav.Badge(page.Id));
    }

    [Fact]
    public async Task Pages_are_managed_from_the_command_line()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        async Task<JsonNode?> Run(string name, JsonObject args)
        {
            var r = await app.Commands.ExecuteAsync(name, args);
            Assert.True(r["ok"]!.GetValue<bool>(), r["error"]?.ToString());
            return r["result"];
        }

        var added = await Run("page add", new JsonObject { ["name"] = "Git", ["kind"] = "terminal", ["command"] = "lazygit", ["icon"] = "source-branch" });
        Assert.Equal("lazygit", added!["target"]!.GetValue<string>());
        await Run("page add", new JsonObject { ["name"] = "Docs", ["kind"] = "web", ["url"] = "https://docs.example.com", ["inject-api"] = "false", ["disabled"] = true });
        Assert.False(app.Nav.Get("Docs").InjectApi);
        Assert.False(app.Nav.Get("Docs").Enabled);

        await Run("page edit", new JsonObject { ["id"] = "Git", ["name"] = "Lazygit", ["cwd"] = "~/code" });
        Assert.Equal(("lazygit", "~/code"), (app.Nav.Get("Lazygit").Command, app.Nav.Get("Lazygit").Cwd));

        // Going to a page works by name, for custom pages that are switched on.
        await Run("app nav", new JsonObject { ["page"] = "Lazygit" });
        Assert.Equal(app.Nav.Get("Lazygit").Key, app.Host.CurrentPage);
        Assert.False((await app.Commands.ExecuteAsync("page open", new JsonObject { ["id"] = "Docs" }))["ok"]!.GetValue<bool>());
        await Run("page enable", new JsonObject { ["id"] = "Docs" });
        await Run("page open", new JsonObject { ["id"] = "Docs" });
        Assert.Equal(app.Nav.Get("Docs").Key, app.Host.CurrentPage);

        await Run("page badge", new JsonObject { ["id"] = "Docs", ["text"] = "3" });
        await Run("page attention", new JsonObject { ["id"] = "Docs", ["message"] = "look" });
        var list = (JsonArray)(await Run("page list", new JsonObject()))!;
        Assert.Equal(("3", "look"), (list[1]!["badge"]!.GetValue<string>(), list[1]!["attention"]!.GetValue<string>()));
        await Run("page attention", new JsonObject { ["id"] = "Docs", ["clear"] = true });
        Assert.Null(app.Nav.Attention(app.Nav.Get("Docs").Id));

        var reloaded = "";
        app.Nav.ReloadRequested += p => reloaded = p.Name;
        await Run("page reload", new JsonObject { ["id"] = "Docs" });
        Assert.Equal("Docs", reloaded);

        await Run("page remove", new JsonObject { ["id"] = "Lazygit" });
        Assert.Equal(["Docs"], app.Nav.Pages.Select(p => p.Name));
    }
}
