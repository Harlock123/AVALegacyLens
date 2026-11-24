using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AVALegacyLens.Mcp;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVALegacyLens.Agents.Chat;

/// <summary>The Claude models the chat offers, by their API ids.</summary>
public static class ChatModels
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-4-5-20251001";

    /// <summary>The model a new chat starts with.</summary>
    public const string Default = Opus;

    /// <summary>The choices, most capable first, with a name to show.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Opus, "Claude Opus 5.5"),
        (Sonnet, "Claude Sonnet 5.5"),
        (Haiku, "Claude Haiku 4.5"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>What the model is told: the MCP server's own instructions, and a few lines about working inside the program.</summary>
public static class ChatPrompt
{
    public const string InApp = """
        You are working inside AVA Legacy Lens itself, in its AI Chat panel, with the person watching, on the scan open in their window. Talk to them briefly and plainly: say what you're about to look at, look (describe_screen, trace, who_writes, get_node, get_source, list_rules), then answer in plain words, naming the items and the file:line your answer rests on. Keep it to a few sentences; the tool calls show in the panel on their own. Read before you answer: never guess what code does; the tools show it. Say when a link is only by name or guessed. To point the person at something, use show_in_view (it selects it in their window) or render_view. Mark findings only when the person says what they are. You only ever write new files in the working folder.
        """;

    /// <summary>The system prompt: the same instructions every MCP agent gets, then <see cref="InApp"/>.</summary>
    public static string System => LensServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();

    /// <summary>Added for a model given only <see cref="ChatTools.Core"/>.</summary>
    public const string CoreToolsNote =
        "In this chat you have a core set of the server's tools, not all of them: the instructions above may name tools you don't have, so work with the ones you're given.";

    /// <summary>Added for a model that can't see pictures (its tools' pictures become a line of text).</summary>
    public const string NoPicturesNote =
        "You can't see pictures in this chat: render_view still shows the person the code, but read it yourself with get_source and get_node.";
}

/// <summary>Which of the MCP server's tools the chat offers a model.</summary>
public static class ChatTools
{
    /// <summary>
    /// The core set for small local models, which choose better among fewer tools: the inventory, finding and reading
    /// items, tracing, who writes a table, screens, the findings and the estimate.
    /// </summary>
    public static readonly IReadOnlyList<string> Core =
    [
        "get_inventory", "find", "get_node", "get_source", "trace", "who_writes", "who_reads", "list_screens", "describe_screen",
        "list_rules", "list_dead_code", "list_risks", "get_estimate", "show_in_view",
    ];

    /// <summary>The core tools among <paramref name="tools"/>, in the server's order.</summary>
    public static IReadOnlyList<McpTool> CoreOf(IReadOnlyList<McpTool> tools) => tools.Where(t => Core.Contains(t.Name)).ToList();
}
