using Yanmt.Core.Ssh;

namespace Yanmt.Core.Tests.Ssh;

public class CommandAllowlistTests
{
    [Theory]
    [InlineData("show running-config")]
    [InlineData("show version")]
    [InlineData("terminal length 0")]
    [InlineData("terminal width 0")]
    [InlineData("terminal width 80")]
    [InlineData("terminal width 132")]
    [InlineData("terminal width 512")]
    public void Allows_exactly_the_read_only_commands(string command)
    {
        Assert.True(CommandAllowlist.IsAllowed(command));
        CommandAllowlist.EnsureAllowed(command); // does not throw
    }

    [Theory]
    // Config mode and writes, every spelling we can think of
    [InlineData("configure terminal")]
    [InlineData("configure")]
    [InlineData("conf t")]
    [InlineData("config t")]
    [InlineData("configure replace flash:base.cfg")]
    [InlineData("configure confirm")]
    [InlineData("write")]
    [InlineData("write memory")]
    [InlineData("write erase")]
    [InlineData("wr")]
    [InlineData("wr mem")]
    [InlineData("copy running-config startup-config")]
    [InlineData("copy run start")]
    [InlineData("copy tftp: running-config")]
    [InlineData("reload")]
    [InlineData("erase startup-config")]
    [InlineData("delete flash:x")]
    [InlineData("enable")]
    [InlineData("exit")]
    [InlineData("debug all")]
    [InlineData("clear counters")]
    [InlineData("terminal monitor")]
    [InlineData("terminal length 24")]
    // Abbreviations and other show commands are not on the list either
    [InlineData("sh run")]
    [InlineData("show run")]
    [InlineData("sh ver")]
    [InlineData("show running")]
    [InlineData("show running-config all")]
    [InlineData("show startup-config")]
    [InlineData("show tech-support")]
    // Pipes and redirects: "| redirect"/"| append"/"| tee" write files on the device
    [InlineData("show running-config | redirect flash:run.txt")]
    [InlineData("show running-config | append flash:run.txt")]
    [InlineData("show running-config | tee flash:run.txt")]
    [InlineData("show version | include Version")]
    [InlineData("show running-config > flash:x")]
    // Chaining and injection
    [InlineData("show version; configure terminal")]
    [InlineData("show version;conf t")]
    [InlineData("show version\nconfigure terminal")]
    [InlineData("show version\r\nwrite memory")]
    [InlineData("show version\n")]
    [InlineData("show version\r")]
    [InlineData("show version\0")]
    [InlineData("show version\u001aconf t")]
    [InlineData("terminal width 80\nconf t")]
    // Whitespace and case variants
    [InlineData(" show version")]
    [InlineData("show version ")]
    [InlineData("show  version")]
    [InlineData("show\tversion")]
    [InlineData("show version")]
    [InlineData("SHOW VERSION")]
    [InlineData("Show Version")]
    [InlineData("show Running-Config")]
    // Look-alike characters
    [InlineData("show versiоn")] // Cyrillic o
    [InlineData("show running‑config")] // non-breaking hyphen
    [InlineData("show version​")] // zero-width space
    // terminal width edge cases
    [InlineData("terminal width 513")]
    [InlineData("terminal width 1000")]
    [InlineData("terminal width -1")]
    [InlineData("terminal width 080")]
    [InlineData("terminal width 00")]
    [InlineData("terminal width +80")]
    [InlineData("terminal width 8 0")]
    [InlineData("terminal width 0x50")]
    [InlineData("terminal width ٨٠")] // Arabic-Indic digits 80
    [InlineData("terminal width ８０")] // full-width digits 80
    [InlineData("terminal width")]
    [InlineData("terminal width ")]
    [InlineData("terminal width 80 ")]
    [InlineData("")]
    [InlineData(" ")]
    public void Refuses_everything_else(string command)
    {
        Assert.False(CommandAllowlist.IsAllowed(command));
        var ex = Assert.Throws<CommandNotAllowedException>(() => CommandAllowlist.EnsureAllowed(command));
        Assert.Equal(command, ex.Command);
    }

    [Fact]
    public void Refuses_null()
    {
        Assert.False(CommandAllowlist.IsAllowed(null));
        Assert.Throws<CommandNotAllowedException>(() => CommandAllowlist.EnsureAllowed(null));
    }

    [Fact]
    public void Exact_list_is_exactly_the_three_commands()
    {
        Assert.Equal(
            new[] { "show running-config", "show version", "terminal length 0" }.Order(),
            CommandAllowlist.ExactCommands.Order());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(512)]
    public void TerminalWidth_builds_an_allowed_command(int width)
    {
        Assert.True(CommandAllowlist.IsAllowed(CommandAllowlist.TerminalWidth(width)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(513)]
    public void TerminalWidth_rejects_out_of_range(int width)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandAllowlist.TerminalWidth(width));
    }

    [Fact]
    public void Exception_message_makes_control_characters_visible()
    {
        var ex = Assert.Throws<CommandNotAllowedException>(() => CommandAllowlist.EnsureAllowed("show version\nconf t"));
        Assert.Contains(@"show version\x0Aconf t", ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
    }
}
