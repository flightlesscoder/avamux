using System.Text.Json.Nodes;
using Avamux.Core.Plugins;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    private void AddPluginCommands(AppServices s)
    {
        static JsonObject Json(PluginEntry e) => new()
        {
            ["id"] = e.Id, ["name"] = e.Name, ["version"] = e.Version, ["source"] = e.Source switch { PluginSource.BuiltIn => "built-in", PluginSource.Page => "page", _ => "plugins folder" },
            ["enabled"] = e.Enabled, ["adds"] = new JsonArray(e.Adds.Select(a => (JsonNode)a).ToArray()), ["description"] = e.Description,
            ["location"] = e.Location, ["error"] = e.Error, ["restartToFinish"] = e.RestartToFinish,
        };
        var id = P("plugin", "Plugin id or name (see `plugin list`).", true);
        Add("plugin list", "List every plugin: built in, in the plugins folder, and page plugins.", [], _ => new JsonArray(s.Plugins.Entries.Select(e => (JsonNode)Json(e)).ToArray()));
        Add("plugin show", "Show one plugin.", [id], a => Json(s.Plugins.Get(a.Require("plugin"))));
        Add("plugin enable", "Switch a plugin on.", [id], a => { s.Plugins.SetEnabled(a.Require("plugin"), true); return Json(s.Plugins.Get(a.Require("plugin"))); });
        Add("plugin disable", "Switch a plugin off: what it adds goes at once.", [id], a => { s.Plugins.SetEnabled(a.Require("plugin"), false); return Json(s.Plugins.Get(a.Require("plugin"))); });
        Add("plugin reload", "Load a plugin's DLL again from disk (after rebuilding it) and switch it on again.", [id], a => { s.Plugins.Reload(a.Require("plugin")); return "reloaded"; });
        Add("plugin install", "Copy a plugin (a DLL with what it needs, or a folder) into the plugins folder and switch it on.",
            [P("path", "The plugin's DLL or folder.", true)], a => new JsonArray(s.Plugins.Install(a.Require("path")).Select(e => (JsonNode)Json(e)).ToArray()));
        Add("plugin uninstall", "Switch a plugin off and delete it from the plugins folder (a page plugin: remove the page). Its code stays loaded until avamux restarts.",
            [id], a => { s.Plugins.Uninstall(a.Require("plugin")); return "uninstalled"; });
    }
}
