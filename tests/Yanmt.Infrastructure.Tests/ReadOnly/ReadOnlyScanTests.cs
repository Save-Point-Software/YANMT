using System.Reflection;
using System.Text.RegularExpressions;
using Yanmt.Infrastructure.Collectors.Snmp;

namespace Yanmt.Infrastructure.Tests.ReadOnly;

/// <summary>
/// Source scans that enforce the read-only design rule (CLAUDE.md §1). The SSH account on the
/// device is plain privilege 15, so these and the allowlist are the enforcement.
/// Never weaken or delete these tests to make a change pass.
/// </summary>
public class ReadOnlyScanTests
{
    /// <summary>The SSH layer: the allowlist and the collector folder.</summary>
    private static readonly string[] SshLayerDirs = ["Yanmt.Core/Ssh", "Yanmt.Infrastructure/Collectors/Ssh"];

    private const string CollectorFile = "Yanmt.Infrastructure/Collectors/Ssh/ReadOnlySshCollector.cs";

    // Words that must never appear in a string literal in the SSH layer, in any case.
    private static readonly Regex SshForbiddenWords = new(
        @"\b(configure|conf(ig)?\s+t|write|wr|copy|reload|erase)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // IOS commands that change state, anywhere in src/ (string literals in .cs files).
    private static readonly Regex StateChangingCommand = new(
        @"^\s*(conf(ig(ure)?)?\s+(t(erm(inal)?)?|replace|mem(ory)?|confirm|network)\b|configure\s*$|write(\s+(mem(ory)?|erase|terminal|network))?\s*$|wr(\s+mem)?\s*$|copy\s+(\S+:\S*|run\S*|start\S*)\s+\S+|reload\b|erase\s+\S+|archive\s+config)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // Any reference to SharpSnmpLib's SET API.
    private static readonly Regex SnmpSetApi = new(
        @"\bSetRequest(Message|Pdu)\b|\bMessenger\s*\.\s*Set(Async)?\b|\bSnmpType\s*\.\s*SetRequestPdu\b|\bSetRequestPdu\b",
        RegexOptions.CultureInvariant);

    public static TheoryData<string> SshLayerFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in SshLayerDirs.SelectMany(d => SourceScanner.Files(d, "*.cs")))
        {
            data.Add(SourceScanner.Relative(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SshLayerFiles))]
    public void Ssh_layer_has_no_config_write_or_copy_strings(string file)
    {
        var (literals, _) = SourceScanner.Split(File.ReadAllText(Path.Combine(SourceScanner.SrcRoot, file)));
        var hits = literals.Where(l => SshForbiddenWords.IsMatch(l)).ToList();
        Assert.True(hits.Count == 0, $"{file} contains forbidden command text in string literal(s): {string.Join(" | ", hits)}");
    }

    [Fact]
    public void Ssh_layer_scan_covers_the_allowlist_and_collector()
    {
        // Guards against the scan silently matching nothing (moved files, a broken path).
        var files = SshLayerDirs.SelectMany(d => SourceScanner.Files(d, "*.cs")).Select(SourceScanner.Relative).ToList();
        Assert.Contains("Yanmt.Core/Ssh/CommandAllowlist.cs", files);
        Assert.Contains(CollectorFile, files);

        var (literals, _) = SourceScanner.Split(File.ReadAllText(Path.Combine(SourceScanner.SrcRoot, "Yanmt.Core/Ssh/CommandAllowlist.cs")));
        Assert.Contains("show running-config", literals);
        Assert.Contains("show version", literals);
    }

    [Fact]
    public void No_state_changing_ios_command_string_anywhere_in_src()
    {
        var hits = SourceScanner.Files(".", "*.cs")
            .SelectMany(f => SourceScanner.Split(File.ReadAllText(f)).Literals
                .Where(l => StateChangingCommand.IsMatch(l))
                .Select(l => $"{SourceScanner.Relative(f)}: \"{l}\""))
            .ToList();
        Assert.True(hits.Count == 0, "State-changing IOS command strings found:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void No_snmp_set_api_anywhere_in_src()
    {
        var hits = SourceScanner.Files(".", "*.cs", "*.razor")
            .Where(f => SnmpSetApi.IsMatch(SourceScanner.Split(File.ReadAllText(f)).Code) || (f.EndsWith(".razor") && SnmpSetApi.IsMatch(File.ReadAllText(f))))
            .Select(SourceScanner.Relative)
            .ToList();
        Assert.True(hits.Count == 0, "SNMP SET API referenced in: " + string.Join(", ", hits));
    }

    [Fact]
    public void SharpSnmpLib_is_used_only_in_the_snmp_collector_folder()
    {
        AssertOnlyIn(@"\bLextm\.SharpSnmpLib\b", "Yanmt.Infrastructure/Collectors/Snmp/");
    }

    [Fact]
    public void SshNet_is_used_only_in_the_ssh_collector_folder()
    {
        AssertOnlyIn(@"\bRenci\.SshNet\b", "Yanmt.Infrastructure/Collectors/Ssh/");
    }

    [Fact]
    public void Only_the_collector_creates_ssh_clients_commands_or_shells()
    {
        // SSH.NET types appear only in the collector file. (CreateCommand is also an ADO.NET method,
        // so it's checked inside the collector by the next test instead.)
        AssertOnlyIn(@"\b(SshClient|ShellStream|CreateShellStream|CreateShellStreamAsync|RunCommand|SshCommand|ScpClient|SftpClient)\b", CollectorFile);
    }

    [Fact]
    public void Collector_sends_through_one_method_that_checks_the_allowlist_first()
    {
        var (_, code) = SourceScanner.Split(File.ReadAllText(Path.Combine(SourceScanner.SrcRoot, CollectorFile)));

        // Exactly one place builds a command, and no shell streams at all (yet).
        Assert.Single(Regex.Matches(code, @"\.CreateCommand\s*\("));
        Assert.DoesNotMatch(@"CreateShellStream|ShellStream|RunCommand", code);

        // That place is SendAsync, and its first statement is the allowlist check.
        var send = Regex.Match(code, @"Task<SshCommandOutput>\s+SendAsync\s*\([^)]*\)\s*\{\s*CommandAllowlist\.EnsureAllowed\s*\(\s*command\s*\)\s*;");
        Assert.True(send.Success, "SendAsync must start with CommandAllowlist.EnsureAllowed(command).");
        var createAt = code.IndexOf(".CreateCommand(", StringComparison.Ordinal);
        Assert.True(createAt > send.Index, "CreateCommand must only be called inside SendAsync, after the allowlist check.");
        var nextMember = Regex.Match(code[(send.Index + send.Length)..], @"\n    (private|public|internal)\s");
        Assert.True(!nextMember.Success || createAt < send.Index + send.Length + nextMember.Index,
            "CreateCommand must be inside SendAsync.");
    }

    [Fact]
    public void Snmp_collector_builds_only_read_request_messages()
    {
        // Any SharpSnmpLib message or PDU constructed for sending must be a read request. Responses and
        // reports appear only as parsed replies (and the reader's synthetic discovery stand-in).
        var built = SourceScanner.Files("Yanmt.Infrastructure/Collectors/Snmp", "*.cs")
            .SelectMany(f => Regex.Matches(SourceScanner.Split(File.ReadAllText(f)).Code, @"\bnew\s+(\w*Request\w*)\s*\(")
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        Assert.NotEmpty(built);
        Assert.All(built, type => Assert.Contains(type, new[] { "GetRequestMessage", "GetNextRequestMessage", "GetBulkRequestMessage" }));
    }

    [Fact]
    public void Snmp_reader_interface_has_no_write_operations()
    {
        var methods = typeof(ISnmpReader).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).ToList();
        Assert.NotEmpty(methods);
        Assert.All(methods, name => Assert.Matches("^(Get|GetNext|GetBulk|Walk)(Async)?$", name));
    }

    [Theory]
    [InlineData("var s = \"conf t\";", true)]
    [InlineData("var s = @\"write memory\";", true)]
    [InlineData("var s = $\"copy {a} start\";", true)]
    [InlineData("var s = \"\"\"\nconfigure terminal\n\"\"\";", true)]
    [InlineData("// configure terminal in a comment is fine\nvar s = \"show version\";", false)]
    [InlineData("/* write memory */ var s = \"show version\";", false)]
    [InlineData("var c = '\"'; var s = \"show version\";", false)]
    public void Scanner_finds_forbidden_literals(string source, bool expected)
    {
        var (literals, _) = SourceScanner.Split(source);
        Assert.Equal(expected, literals.Any(l => SshForbiddenWords.IsMatch(l)));
    }

    [Theory]
    [InlineData("new SetRequestMessage(1, v, c, vars)")]
    [InlineData("await Messenger.SetAsync(v, ep, c, vars)")]
    [InlineData("Messenger . Set(v, ep, c, vars, 1000)")]
    [InlineData("SnmpType.SetRequestPdu")]
    public void Set_api_pattern_catches_set_calls(string code) => Assert.Matches(SnmpSetApi, code);

    [Theory]
    [InlineData("conf t", true)]
    [InlineData("configure terminal", true)]
    [InlineData("configure replace flash:x", true)]
    [InlineData("write memory", true)]
    [InlineData("wr", true)]
    [InlineData("copy run start", true)]
    [InlineData("copy running-config startup-config", true)]
    [InlineData("copy tftp://192.0.2.5/x.cfg running-config", true)]
    [InlineData("copy flash:a.cfg nvram:startup-config", true)]
    [InlineData("reload", true)]
    [InlineData("Copy to clipboard", false)] // UI text, not a 3-token copy command
    [InlineData("Configuration backups", false)]
    [InlineData("show running-config", false)]
    public void State_changing_pattern(string text, bool expected) => Assert.Equal(expected, StateChangingCommand.IsMatch(text));

    private static void AssertOnlyIn(string pattern, string allowedPrefix)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var offenders = SourceScanner.Files(".", "*.cs", "*.razor")
            .Where(f => regex.IsMatch(f.EndsWith(".razor") ? File.ReadAllText(f) : SourceScanner.Split(File.ReadAllText(f)).Code))
            .Select(SourceScanner.Relative)
            .Where(f => !f.StartsWith(allowedPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.True(offenders.Count == 0, $"'{pattern}' must only appear under {allowedPrefix}, but also appears in: {string.Join(", ", offenders)}");
    }
}
