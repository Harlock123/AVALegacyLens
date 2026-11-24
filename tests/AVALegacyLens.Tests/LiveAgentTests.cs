using System.IO.Pipelines;
using Avalonia.Input;
using AVALegacyLens.Agents;
using AVALegacyLens.Mcp;
using AVALegacyLens.Tests.Agents;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVALegacyLens.Tests;

/// <summary>The window's doors for outside agents (the pipe for <c>AVALegacyLens --mcp --attach</c>, the network door) and the chat.</summary>
public sealed class LiveAgentTests
{
    [Fact]
    public void An_outside_agent_attaches_through_the_pipe_works_on_the_windows_scan_and_shows_an_item() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow { AgentPipe = "avalegacylens-test-" + Guid.NewGuid().ToString("N") };
        w.Show();
        w.SetMap(Core.Scanning.Scanner.Scan(Path.Combine(root, "ClinicLegacy")));
        HeadlessApp.Settle();
        Assert.StartsWith("AI agents may connect", w.SetAllowAgents(true));
        Assert.True(LiveListener.Answers(w.AgentPipe));
        var bridge = new LiveBridge(w.AgentPipe, root);
        Pipe up = new(), down = new();
        var server = McpServer.Create(new StreamServerTransport(up.Reader.AsStream(), down.Writer.AsStream(), "attach"), bridge.Options());
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => server.RunAsync(stop.Token));
        var client = AgentTests.Wait(McpClient.CreateAsync(new StreamClientTransport(up.Writer.AsStream(), down.Reader.AsStream())));
        string Text(CallToolResult r) => string.Join(" ", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
        try
        {
            Assert.Contains("live app", client.ServerInstructions);
            var tools = AgentTests.Wait(client.ListToolsAsync().AsTask());
            Assert.Equal(AgentTests.ToolNames.Length, tools.Count);
            var inv = AgentTests.Wait(client.CallToolAsync("get_inventory", new Dictionary<string, object?>()).AsTask());
            Assert.False(inv.IsError == true, Text(inv));
            Assert.Contains("\"screens\":4", Text(inv));
            var shown = AgentTests.Wait(client.CallToolAsync("show_in_view", new Dictionary<string, object?> { ["item"] = "frmPatient.btnSave", ["tab"] = "trace" }).AsTask());
            Assert.False(shown.IsError == true, Text(shown));
            HeadlessApp.Settle();
            Assert.Equal("btnSave", w.Current!.Name);
            Assert.Equal(2, w.Tabs.SelectedIndex);
            Assert.Equal("btnSave_Click", w.CurrentTrace!.Next[0].Node.Name);
            var id = w.Map!.Findings.First(f => f.Kind == Core.Findings.FindingKind.Rule).Id;
            var marked = AgentTests.Wait(client.CallToolAsync("mark_finding", new Dictionary<string, object?> { ["finding"] = id, ["state"] = "confirmed" }).AsTask());
            Assert.False(marked.IsError == true, Text(marked));
            HeadlessApp.Settle();
            Assert.Equal(Core.Findings.MarkState.Confirmed, w.Map.Marks[id].State);
            Assert.Equal(3, w.Tabs.SelectedIndex);
        }
        finally
        {
            AgentTests.Wait(client.DisposeAsync().AsTask().ContinueWith(_ => true));
            stop.Cancel();
            AgentTests.Wait(serving.ContinueWith(_ => true));
            AgentTests.Wait(bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            Assert.Equal("AI agents are shut out.", w.SetAllowAgents(false));
            AgentTests.Wait(w.Agents.StopAsync().ContinueWith(_ => true));
            w.Close();
        }
    });

    [Fact]
    public void The_network_door_opens_on_the_settings_and_wants_the_token() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            Assert.Equal(8776, new AgentSettings().NetworkAgentsPort);
            Assert.Equal(8776, McpCommandLine.NetworkPort);
            Assert.Equal("AVALEGACYLENS_MCP_TOKEN", McpCommandLine.TokenVariable);
            w.AgentSettings.AllowNetworkAgents = true;
            w.AgentSettings.NetworkAgentsAddress = "localhost";
            w.AgentSettings.NetworkAgentsPort = 0;
            w.AgentSettings.NetworkAgentsFolder = Samples.Copy();
            Assert.StartsWith("AI agents on other computers may connect", AgentTests.Wait(w.SyncNetworkAsync()));
            using var http = new HttpClient();
            var refused = AgentTests.Wait(http.PostAsync(w.Agents.NetworkUrls.First(), new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
            w.AgentSettings.Save();
            Assert.DoesNotContain(w.NetworkToken, File.ReadAllText(AgentSettings.Path));
            Assert.StartsWith(AgentSettings.FolderOverride!, AgentSettings.Path);
        }
        finally
        {
            w.AgentSettings.AllowNetworkAgents = false;
            AgentTests.Wait(w.SyncNetworkAsync());
            w.Close();
        }
    });

    [Fact]
    public void The_pipe_name_and_command_line_have_this_programs_names() => HeadlessApp.Run(() =>
    {
        Assert.StartsWith("AVALegacyLens-mcp-", LiveProtocol.PipeName);
        Assert.Contains("AVALEGACYLENS_MCP_TOKEN", McpCommandLine.Usage);
        Assert.Contains("--database", McpCommandLine.Usage);
        Assert.DoesNotContain("--deidentify", McpCommandLine.Usage);
        var output = new MemoryStream();
        var log = new StringWriter();
        Assert.Equal(2, AgentTests.Wait(McpCommandLine.RunAsync(["--mcp", "--bogus"], () => throw new InvalidOperationException(), new AVALegacyLens.Tests.Agents.AvaloniaThread(), new MemoryStream(), output, log)));
        Assert.Contains("Unknown option --bogus", log.ToString());
        Assert.Equal(0, output.Length);
    });

    [Fact]
    public void F12_opens_and_closes_the_AI_Chat() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.ChatKeys = (new AVALegacyLens.Agents.Chat.ApiKeyStore(null, null), new AVALegacyLens.Agents.Chat.ApiKeyStore(null, null));
        w.Show();
        try
        {
            Assert.False(w.ChatHolder.IsVisible);
            w.ToggleChat();
            Assert.True(w.ChatHolder.IsVisible);
            Assert.NotNull(w.Chat);
            Assert.Contains("explain the system", w.Chat!.Input.Watermark);
            w.ToggleChat();
            Assert.False(w.ChatHolder.IsVisible);
        }
        finally { w.Close(); }
    });
}
