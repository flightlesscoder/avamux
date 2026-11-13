using System.Text.Json.Nodes;
using Avamux.Core.Nav;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    private void AddPageCommands(AppServices s)
    {
        CommandParam[] edit =
        [
            O("kind", "terminal (a command in its own terminal), web (a web page) or plugin (a .NET DLL)."),
            O("icon", "Icon name as listed in Settings → Custom pages, or SVG path data for a 24x24 box."),
            O("command", "terminal: the command line to run. Empty starts the shell."),
            O("cwd", "terminal: folder to start in."),
            O("url", "web: the address to show."),
            O("script", "web: JavaScript to run in the page after each load."),
            O("inject-api", "web: give the page the limited window.avamux object (true or false)."),
            O("links-in-browser", "web: open links that ask for a new window in the default browser (true or false)."),
            O("assembly", "plugin: path of the DLL (relative paths are looked up in the plugins folder)."),
            O("type", "plugin: class name, when the DLL has more than one page."),
        ];
        static void Apply(CustomPage p, Args a)
        {
            if (a.Str("kind") is { } kind) p.Kind = kind;
            if (a.Str("icon") is { } icon) p.Icon = icon;
            if (a.Str("command") is { } command) p.Command = command;
            if (a.Str("cwd") is { } cwd) p.Cwd = cwd;
            if (a.Str("url") is { } url) p.Url = url;
            if (a.Str("script") is { } script) p.Script = script;
            if (a.Bool("inject-api") is { } api) p.InjectApi = api;
            if (a.Bool("links-in-browser") is { } links) p.LinksInBrowser = links;
            if (a.Str("assembly") is { } assembly) p.Assembly = assembly;
            if (a.Str("type") is { } type) p.Type = type;
        }
        JsonNode? Json(CustomPage p) => new JsonObject
        {
            ["id"] = p.Id, ["name"] = p.Name, ["kind"] = p.Kind, ["enabled"] = p.Enabled, ["icon"] = p.Icon, ["target"] = p.Target,
            ["attention"] = s.Nav.Attention(p.Id), ["badge"] = s.Nav.Badge(p.Id),
        };

        Add("page list", "List the custom pages in the navigation.", [], _ => new JsonArray(s.Nav.Pages.Select(Json).ToArray()));
        Add("page add", "Add a custom page to the navigation.", [P("name", "Name shown in the navigation.", true), .. edit, F("disabled", "Add it switched off.")], a =>
        {
            var page = new CustomPage { Name = a.Require("name"), Enabled = !a.Flag("disabled") };
            Apply(page, a);
            return Json(s.Nav.Add(page));
        });
        Add("page edit", "Change a custom page. A page whose command, address or DLL changes is started afresh.", [P("id", "Page id or name.", true), O("name", "New name."), .. edit], a =>
        {
            var page = s.Nav.Get(a.Require("id"));
            if (a.Str("name") is { } name) page.Name = name;
            Apply(page, a);
            s.Nav.Update(page);
            return Json(page);
        });
        Add("page remove", "Delete a custom page.", [P("id", "Page id or name.", true)], a => { s.Nav.Remove(a.Require("id")); return "removed"; });
        Add("page enable", "Show a custom page in the navigation.", [P("id", "Page id or name.", true)], a => { s.Nav.SetEnabled(a.Require("id"), true); return "enabled"; });
        Add("page disable", "Take a custom page out of the navigation and stop what it runs.", [P("id", "Page id or name.", true)], a => { s.Nav.SetEnabled(a.Require("id"), false); return "disabled"; });
        Add("page move", "Move a custom page up or down the navigation.", [P("id", "Page id or name.", true), P("by", "Places to move: -1 is one up, 1 one down.", true)], a =>
        {
            s.Nav.Move(a.Require("id"), a.Int("by") ?? 0);
            return new JsonArray(s.Nav.Pages.Select(Json).ToArray());
        });
        Add("page open", "Switch the window to a custom page.", [P("id", "Page id or name.", true)], a =>
        {
            var page = s.Nav.Get(a.Require("id"));
            if (!page.Enabled) throw new InvalidOperationException($"'{page.Name}' is switched off.");
            s.Host.Navigate(page.Key);
            return page.Name;
        });
        Add("page reload", "Start a custom page afresh: restart its terminal, reload its web page or load its plugin again.", [P("id", "Page id or name.", true)], a =>
        {
            s.Nav.Reload(a.Require("id"));
            return "reloaded";
        });
        Add("page attention", "Mark a custom page as needing attention (or clear the mark).", [P("id", "Page id or name.", true), Rest("message", "What it needs."), F("clear", "Remove the mark instead.")], a =>
        {
            var page = s.Nav.Get(a.Require("id"));
            if (a.Flag("clear")) s.Nav.ClearAttention(page.Id); else s.Nav.RequestAttention(page.Id, a.Str("message"));
            return Json(page);
        });
        Add("page badge", "Show a few characters on a custom page's navigation entry. No text removes it.", [P("id", "Page id or name.", true), P("text", "Up to four characters.")], a =>
        {
            var page = s.Nav.Get(a.Require("id"));
            s.Nav.SetBadge(page.Id, a.Str("text"));
            return Json(page);
        });
    }
}
