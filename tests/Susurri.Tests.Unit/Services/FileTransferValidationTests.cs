using Shouldly;
using Susurri.Modules.DHT.Core.Services;
using Xunit;

namespace Susurri.Tests.Unit.Services;

public class FileTransferValidationTests
{
    [Theory]
    [InlineData("hello.txt")]
    [InlineData("photo.jpg")]
    [InlineData("My Document - 2026.pdf")]
    public void IsValidFileName_AcceptsReasonableNames(string name)
    {
        FileTransferService.IsValidFileName(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("foo\0bar")]
    [InlineData("foo\nbar")]
    [InlineData("foo\rbar")]
    [InlineData("foo\tbar")]
    public void IsValidFileName_RejectsDangerousNames(string name)
    {
        FileTransferService.IsValidFileName(name).ShouldBeFalse();
    }

    [Fact]
    public void IsValidFileName_RejectsOverlyLongName()
    {
        var name = new string('a', FileTransferService.MaxFileNameLength + 1);
        FileTransferService.IsValidFileName(name).ShouldBeFalse();
    }

    [Fact]
    public void IsValidFileName_AcceptsExactlyMaxLengthName()
    {
        var name = new string('a', FileTransferService.MaxFileNameLength);
        FileTransferService.IsValidFileName(name).ShouldBeTrue();
    }
}
