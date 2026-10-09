using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Remote;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData(@"C:\Music Spin\.usrdata", "\"C:\\Music Spin\\.usrdata\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData(@"C:\no\spaces\", @"C:\no\spaces\")]
    public void Arguments_are_quoted_for_CommandLineToArgvW(string argument, string expected)
        => Assert.Equal(@"C:\ms\music-spin.exe " + expected, WindowsCommandLine.Build(@"C:\ms\music-spin.exe", [argument]));

    [Fact]
    public void The_executable_path_is_quoted_when_it_contains_spaces()
        => Assert.Equal("\"C:\\Program Files\\ms.exe\" player run", WindowsCommandLine.Build(@"C:\Program Files\ms.exe", ["player", "run"]));
}
