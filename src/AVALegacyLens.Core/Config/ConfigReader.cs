using System.Data.Common;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using Microsoft.CodeAnalysis;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace AVALegacyLens.Core.Config;

/// <summary>
/// Reads .config files (app.config, web.config): each connection string becomes a <see cref="NodeKind.Connection"/>
/// with its server, database and user, and its password never kept; each app setting a <see cref="NodeKind.Setting"/>,
/// masked if its name says it's a secret. Values that point outside the system (a file share, a web address) become
/// <see cref="NodeKind.External"/> nodes. In code, <c>ConfigurationManager.ConnectionStrings("x")</c>,
/// <c>AppSettings["x"]</c> and <c>My.Settings.x</c> use them, and hard-coded shares and addresses are outside links too.
/// </summary>
public sealed partial class ConfigReader(SystemMap map)
{
    public const string Masked = "(masked)";

    private readonly Dictionary<string, List<string>> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _settings = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"password|passwd|pwd|secret|token|apikey|api_key|credential|private.?key", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"^(\\\\[^\\/\s]+\\[^\\/\s]+|(https?|ftp|sftp|ldap)://[^/\s:]+)", RegexOptions.IgnoreCase)]
    private static partial Regex OutsideStart();

    public static bool IsSecret(string name) => SecretName().IsMatch(name);

    public void ReadFile(string file, string text)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException e)
        {
            map.Notes.Add(new ScanNote(file, e.LineNumber, "Config that isn't valid XML: " + e.Message));
            return;
        }
        var fileId = "file:" + file;
        static int LineOf(XElement e) => ((System.Xml.IXmlLineInfo)e).LineNumber;

        foreach (var add in doc.Descendants().Where(e => e.Name.LocalName == "add" && e.Parent?.Name.LocalName == "connectionStrings"))
        {
            var name = (string?)add.Attribute("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var node = map.Add(new Node { Id = $"connection:{file}#{name}", Kind = NodeKind.Connection, Name = name, Parent = fileId, File = file, Line = LineOf(add), EndLine = LineOf(add), Language = "Config" });
            if ((string?)add.Attribute("providerName") is { } provider) node.Props["provider"] = provider;
            foreach (var (k, v) in Describe((string?)add.Attribute("connectionString") ?? "")) node.Props[k] = v;
            Remember(_connections, name, node.Id);
        }

        foreach (var add in doc.Descendants().Where(e => e.Name.LocalName == "add" && e.Parent?.Name.LocalName == "appSettings"))
        {
            var key = (string?)add.Attribute("key");
            if (string.IsNullOrWhiteSpace(key)) continue;
            AddSetting(file, fileId, key, (string?)add.Attribute("value") ?? "", LineOf(add), "appSettings");
        }

        // applicationSettings / userSettings: <setting name="X"><value>…</value></setting> (My.Settings, Settings.Default).
        foreach (var setting in doc.Descendants().Where(e => e.Name.LocalName == "setting" && e.Attribute("name") != null))
            AddSetting(file, fileId, (string)setting.Attribute("name")!, setting.Elements().FirstOrDefault(e => e.Name.LocalName == "value")?.Value ?? "", LineOf(setting), "settings");

        // WCF clients and the like: <endpoint address="https://…">.
        foreach (var endpoint in doc.Descendants().Where(e => e.Name.LocalName == "endpoint" && e.Attribute("address") != null))
            if (Outside((string)endpoint.Attribute("address")!) is { } ext)
                map.Link(fileId, ext.Id, EdgeKind.Uses, file, LineOf(endpoint), Certainty.Resolved, "endpoint " + (string)endpoint.Attribute("address")!);
    }

    private void AddSetting(string file, string fileId, string key, string value, int line, string section)
    {
        var node = map.Add(new Node { Id = $"setting:{file}#{key}", Kind = NodeKind.Setting, Name = key, Parent = fileId, File = file, Line = line, EndLine = line, Language = "Config" });
        node.Props["section"] = section;
        node.Props["value"] = IsSecret(key) ? Masked : value;
        if (IsSecret(key)) node.Props["secret"] = "true";
        Remember(_settings, key, node.Id);
        if (!IsSecret(key) && Outside(value) is { } ext)
            map.Link(node.Id, ext.Id, EdgeKind.Uses, file, line, Certainty.Resolved, value);
    }

    private static void Remember(Dictionary<string, List<string>> index, string key, string id)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(id);
    }

    /// <summary>A connection string's parts worth showing (server, database, user, how it signs in), its password never.</summary>
    public static List<(string Key, string Value)> Describe(string connectionString)
    {
        var parts = new List<(string, string)>();
        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            parts.Add(("connectionString", "(not readable)"));
            return parts;
        }
        string? Get(params string[] keys) => keys.Select(k => builder.TryGetValue(k, out var v) ? v?.ToString() : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (Get("Server", "Data Source", "Address", "Addr", "Network Address", "Host") is { } server) parts.Add(("server", server));
        if (Get("Database", "Initial Catalog") is { } db) parts.Add(("database", db));
        if (Get("User Id", "User ID", "UID", "User", "Username") is { } user) parts.Add(("user", user));
        var integrated = Get("Integrated Security", "Trusted_Connection");
        if (integrated != null && (integrated.Equals("true", StringComparison.OrdinalIgnoreCase) || integrated.Equals("sspi", StringComparison.OrdinalIgnoreCase) || integrated.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            parts.Add(("signIn", "Windows (integrated)"));
        else if (Get("Password", "PWD") != null)
        {
            parts.Add(("signIn", "user and password"));
            parts.Add(("password", Masked));
        }
        if (Get("Provider") is { } provider) parts.Add(("oledbProvider", provider));
        return parts;
    }

    /// <summary>The outside system a value points at (\\server\share, https://host), as a node, or null.</summary>
    public Node? Outside(string value)
    {
        var m = OutsideStart().Match(value.Trim());
        if (!m.Success) return null;
        var root = m.Value.ToLowerInvariant();
        var id = "external:" + root;
        return map[id] ?? map.Add(new Node
        {
            Id = id,
            Kind = NodeKind.External,
            Name = m.Value,
            Parent = map.Outside(),
            Props = { ["subkind"] = root.StartsWith(@"\\", StringComparison.Ordinal) ? "file share" : "web address" },
        });
    }

    public void Attach(DotNetReader reader) => reader.MemberRead += OnMember;

    private void OnMember(MemberContext m)
    {
        foreach (var node in m.Syntax.DescendantNodes())
        {
            // ConfigurationManager.ConnectionStrings("x") / AppSettings["x"] / WebConfigurationManager…
            if (Lookup(node) is var (section, key))
            {
                var index = section == "ConnectionStrings" ? _connections : _settings;
                if (index.TryGetValue(key, out var ids))
                    foreach (var id in ids)
                        map.Link(m.Node.Id, id, EdgeKind.Uses, m.File, DotNetReader.Line(node), ids.Count == 1 ? Certainty.Resolved : Certainty.ByName, DotNetReader.Snippet(node));
                continue;
            }
            // My.Settings.X / Properties.Settings.Default.X / Settings.Default.X
            if (SettingsProperty(node) is { } name && _settings.TryGetValue(name, out var settingIds))
            {
                foreach (var id in settingIds)
                    map.Link(m.Node.Id, id, EdgeKind.Uses, m.File, DotNetReader.Line(node), settingIds.Count == 1 ? Certainty.Resolved : Certainty.ByName, DotNetReader.Snippet(node));
                continue;
            }
            // A share or address written into the code.
            var text = node switch
            {
                VBS.LiteralExpressionSyntax l when l.Token.Value is string s => s,
                CSS.LiteralExpressionSyntax l when l.Token.Value is string s => s,
                _ => null,
            };
            if (text != null && Outside(text) is { } ext)
            {
                map.Link(m.Node.Id, ext.Id, EdgeKind.Uses, m.File, DotNetReader.Line(node), Certainty.Resolved, "hard-coded: " + text);
                m.Node.Props["hardCodedOutside"] = "true";
            }
        }
    }

    private static (string Section, string Key)? Lookup(SyntaxNode node)
    {
        (SyntaxNode? target, SyntaxNode? arg) = node switch
        {
            VBS.InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } i => ((SyntaxNode?)i.Expression, (SyntaxNode?)i.ArgumentList.Arguments[0].GetExpression()),
            CSS.ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } e => (e.Expression, e.ArgumentList.Arguments[0].Expression),
            CSS.InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } i => (i.Expression, i.ArgumentList.Arguments[0].Expression),
            _ => (null, null),
        };
        if (target == null) return null;
        var section = target switch
        {
            VBS.MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            CSS.MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            VBS.IdentifierNameSyntax id => id.Identifier.ValueText,
            CSS.IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };
        if (section is not ("ConnectionStrings" or "AppSettings")) return null;
        var key = arg switch
        {
            VBS.LiteralExpressionSyntax l when l.Token.Value is string s => s,
            CSS.LiteralExpressionSyntax l when l.Token.Value is string s => s,
            _ => null,
        };
        return key == null ? null : (section, key);
    }

    private static string? SettingsProperty(SyntaxNode node)
    {
        switch (node)
        {
            case VBS.MemberAccessExpressionSyntax { Expression: VBS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Settings", Expression: VBS.IdentifierNameSyntax { Identifier.ValueText: "My" } } } vb:
                return vb.Name.Identifier.ValueText;
            case CSS.MemberAccessExpressionSyntax { Expression: CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Default", Expression: var settings } } cs
                when settings.ToString().EndsWith("Settings", StringComparison.Ordinal):
                return cs.Name.Identifier.ValueText;
            default:
                return null;
        }
    }
}
